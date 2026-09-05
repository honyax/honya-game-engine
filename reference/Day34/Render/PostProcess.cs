using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>トーンマッピングの流儀。<c>shaders/composite.frag</c> の <c>uToneMap</c> と対応する。</summary>
internal enum ToneMapOperator
{
    /// <summary>畳まない。1.0 を超えたぶんは切り捨て。**Day 30 までと同じ絵**。</summary>
    None,

    /// <summary>x / (1 + x)。素朴で軽いが眠い。</summary>
    Reinhard,

    /// <summary>ACES のカーブフィット。暗部が締まり、明部に粘りが出る。</summary>
    Aces,
}

/// <summary>後処理のどの段を画面に出すか。**目で追えるようにするための窓**。</summary>
internal enum PostDebugView
{
    /// <summary>最終結果。</summary>
    Final,

    /// <summary>ブルームを足さないもの。滲みがどれだけ効いているかの比較用。</summary>
    SceneOnly,

    /// <summary>明部の抽出結果(ぼかす前)。しきい値の効き目を見る。</summary>
    Bright,

    /// <summary>ぼかしたあと。これが最終結果に足される。</summary>
    Bloom,
}

/// <summary>
/// **HDR パイプライン**。Day 31 の主役。
///
/// Day 30 までの描画は「画面へ直接描いて終わり」だった。今日からはこうなる。
///
/// <code>
///   シーンを描く ──▶ [シーンバッファ RGBA16F]
///                        │
///                        ├──▶ 明部を抜く ──▶ 横ぼかし ⇄ 縦ぼかし(×4)
///                        │                             │
///                        └──────────────┬──────────────┘
///                                       ▼
///                             露出 → 合成 → トーンマップ → ガンマ ──▶ 画面
/// </code>
///
/// 段が増えているように見えるが、増えているのは**フルスクリーンのパス**だけで、
/// シーンの描き方は1行も変わっていない。
/// これが Render To Texture の効き目で、
/// 「描いた結果をもう一度読める」ようにするだけで、後ろにいくらでも処理を継ぎ足せる。
///
/// <para>
/// <b>なぜ HDR が要るのか</b>。
/// 現実の明るさは、月明かりから太陽まで 10 桁以上の幅がある。
/// 画面が出せるのはそのうちの狭い一区間だけなので、
/// 「どの区間を切り出すか(露出)」と「区間の外をどう畳むか(トーンマップ)」を
/// 決める必要がある。**この2つを決めるには、畳む前の値が残っていなければならない**——
/// 8bit のバッファに描いた時点で 1.0 で切られていたら、もう手が無い。
/// </para>
///
/// <para>
/// <b>代償</b>。1920x1080 で、シーンバッファが 21.7MB
/// (カラー RGBA16F 15.8MB + 深度 5.9MB。カラーは 8bit の倍)。
/// ブルーム用の半分の大きさのバッファが3枚で 11.9MB。合計 33.6MB の VRAM と、
/// 毎フレーム 10 回のフルスクリーンパス。実測は計画書の完成条件に載せてある。
/// </para>
/// </summary>
internal sealed class PostProcess : IDisposable
{
    /// <summary>
    /// ブルーム用のバッファを画面の何分の1にするか。
    ///
    /// **ぼかしたものを縮めても分からない**、というのがブルームの美味しいところ。
    /// 半分にすればピクセル数は 1/4 で、ぼかしのコストもそのまま 1/4 になる。
    /// おまけに「半分に縮めて拡大する」こと自体が弱いぼかしとして働くので、
    /// 同じタップ数でより広く滲む。実際のエンジンは 1/2 → 1/4 → 1/8 …と
    /// 何段も縮めたものを重ねる(Day 39 で見直す余地として残す)。
    /// </summary>
    private const int BloomDownscale = 2;

    /// <summary>
    /// 横 → 縦 のぼかしを何往復するか。
    ///
    /// ガウスぼかしは重ねるほど広がる(分散が足し算になる)ので、
    /// 片側4タップのぼかしでも、4往復すれば十分に広い滲みになる。
    /// **半径の大きいフィルタを1回**より**小さいフィルタを何回**のほうが、
    /// 同じ広がりを安く作れる。
    /// </summary>
    private const int BlurIterations = 4;

    private readonly GL _gl;
    private readonly RenderResources _resources;

    /// <summary>シーンを描き込む先。深度つき。**ここだけ画面と同じ大きさ**。</summary>
    private readonly Framebuffer _scene;

    /// <summary>明部を抜いた結果。ぼかす前の姿を残しておくと、しきい値の効き目が見られる。</summary>
    private readonly Framebuffer _bright;

    /// <summary>ぼかしの往復用。**2枚を交互に使う**(ping-pong)。</summary>
    private readonly Framebuffer _blurA;
    private readonly Framebuffer _blurB;

    private readonly Handle<Shader> _brightShader;
    private readonly Handle<Shader> _blurShader;
    private readonly Handle<Shader> _compositeShader;

    /// <summary>
    /// 中身が空の頂点配列オブジェクト。
    ///
    /// <c>fullscreen.vert</c> は頂点属性をひとつも使わないが、
    /// **コアプロファイルでは VAO が 0 のまま描画すると GL_INVALID_OPERATION** になる。
    /// 「何も入っていない VAO」を1個だけ作って、それをバインドしてから描く。
    /// </summary>
    private uint _emptyVao;

    private bool _disposed;

    public PostProcess(GL gl, RenderResources resources, string shaderDirectory, int width, int height)
    {
        _gl = gl;
        _resources = resources;

        _scene = new Framebuffer(gl, width, height, RenderTargetFormat.Rgba16F, depth: true);

        int bloomWidth = Math.Max(1, width / BloomDownscale);
        int bloomHeight = Math.Max(1, height / BloomDownscale);

        // ぼかし用は**深度を持たない**。フルスクリーンの板を1枚描くだけなので、
        // 手前も奥も無い。付けても使われないまま VRAM を食う。
        _bright = new Framebuffer(gl, bloomWidth, bloomHeight, RenderTargetFormat.Rgba16F, depth: false);
        _blurA = new Framebuffer(gl, bloomWidth, bloomHeight, RenderTargetFormat.Rgba16F, depth: false);
        _blurB = new Framebuffer(gl, bloomWidth, bloomHeight, RenderTargetFormat.Rgba16F, depth: false);

        string fullscreen = Path.Combine(shaderDirectory, "fullscreen.vert");
        _brightShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "bright.frag"));
        _blurShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "blur.frag"));
        _compositeShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "composite.frag"));

        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>シーンバッファのテクセルの持ち方(Shift+1)。**今日の見せ場**。</summary>
    public RenderTargetFormat SceneFormat
    {
        get => _scene.Format;
        set => _scene.SetFormat(value);
    }

    /// <summary>ブルームを足すか(Shift+2)。</summary>
    public bool BloomEnabled { get; set; } = true;

    /// <summary>トーンマッピングの流儀(Shift+3)。</summary>
    public ToneMapOperator ToneMap { get; set; } = ToneMapOperator.Aces;

    /// <summary>どの段を画面に出すか(Shift+4)。</summary>
    public PostDebugView DebugView { get; set; } = PostDebugView.Final;

    /// <summary>露出(Shift+5 / Shift+6)。**何を 1.0 とみなすか**。</summary>
    public float Exposure { get; set; } = 1.0f;

    /// <summary>この明るさを超えたところがブルームの元になる(Shift+7)。</summary>
    public float BloomThreshold { get; set; } = 1.0f;

    /// <summary>ぼかした結果をどれだけ足すか。</summary>
    public float BloomIntensity { get; set; } = 0.55f;

    /// <summary>シーンバッファの内容。自己チェックから読み戻すために公開する。</summary>
    public Framebuffer Scene => _scene;

    /// <summary>パイプラインが抱えている VRAM の推定バイト数。</summary>
    public long ByteSize => _scene.ByteSize + _bright.ByteSize + _blurA.ByteSize + _blurB.ByteSize;

    /// <summary>
    /// **直前のフレーム**で走らせたフルスクリーンパスの数。代償を数えるための値。
    ///
    /// 「今のフレーム」ではなく1つ前なのは、HUD を描いているのが
    /// <see cref="Begin"/> と <see cref="End"/> の**間**だから。
    /// その時点ではまだ後処理が1パスも走っていないので、
    /// 今フレームの値を出そうとすると常に 0 になる。
    /// </summary>
    public int PassCount { get; private set; }

    /// <summary>今のフレームで走った数。<see cref="End"/> の最後に <see cref="PassCount"/> へ移す。</summary>
    private int _passes;

    /// <summary>
    /// シーンの描画を始める。**以降の描画は画面ではなくテクスチャへ行く**。
    /// </summary>
    /// <param name="clearColor">
    /// 背景色。**リニアな明るさで渡すこと**。
    /// 出口でガンマをかけるので、ここに sRGB の数字(0.08 など)をそのまま入れると
    /// 画面では明るい灰色になってしまう。
    /// </param>
    public void Begin(Vector4 clearColor)
    {
        _passes = 0;

        _scene.Bind();
        _gl.ClearColor(clearColor.X, clearColor.Y, clearColor.Z, clearColor.W);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    /// <summary>
    /// シーンの描画を締めて、**後処理を通してから画面へ出す**。
    /// </summary>
    /// <param name="screenWidth">ウィンドウ側のフレームバッファの幅。</param>
    /// <param name="screenHeight">同じく高さ。</param>
    public void End(int screenWidth, int screenHeight)
    {
        // **シーンが残していった GL の状態を畳む**。
        //
        // OpenGL の状態はグローバルなので、直前に何が描かれたかで挙動が変わる。
        // フルスクリーンの板にとって、深度・ブレンド・カリング・ポリゴンモードは
        // どれも「効いていたら困る」もので、たとえば
        //   - 深度テストが有効 … シーンの深度が残っているので、板が奥に判定されて消える
        //   - ブレンドが有効   … 半透明として画面に混ざる
        //   - ワイヤーフレーム … 板の輪郭線しか出ず、画面がほぼ真っ黒になる(W キー)
        // という壊れ方をする。**後処理が真っ黒**のときは、まずこの4つを疑う。
        //
        // 元の値を覚えて最後に戻すのは、シーン側の設定(Z/C/W キー)を壊さないため。
        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        bool cull = _gl.IsEnabled(EnableCap.CullFace);

        // **GL_POLYGON_MODE は int を2個返す**(表面用と裏面用)。
        // out int の版を使うと GL が 2 個目を書き込む先が無く、その場のメモリを踏む。
        // 「返る個数」は glGet の項目ごとに決まっているので、必ず仕様を見て器を用意する。
        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        if (BloomEnabled || DebugView is PostDebugView.Bright or PostDebugView.Bloom)
        {
            ExtractBright();
            Blur();
        }

        Composite(screenWidth, screenHeight);

        SetCap(EnableCap.DepthTest, depth);
        SetCap(EnableCap.Blend, blend);
        SetCap(EnableCap.CullFace, cull);
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);

        PassCount = _passes;
    }

    /// <summary>ウィンドウの大きさが変わったら、全部のバッファを作り直す。</summary>
    public void Resize(int width, int height)
    {
        _scene.Resize(width, height);

        int bloomWidth = Math.Max(1, width / BloomDownscale);
        int bloomHeight = Math.Max(1, height / BloomDownscale);
        _bright.Resize(bloomWidth, bloomHeight);
        _blurA.Resize(bloomWidth, bloomHeight);
        _blurB.Resize(bloomWidth, bloomHeight);
    }

    /// <summary>後処理のシェーダを読み直す(F5)。**絵を見ながら曲線をいじる**ために要る。</summary>
    public void ReloadShaders()
    {
        _resources.GetShader(_brightShader).TryReload();
        _resources.GetShader(_blurShader).TryReload();
        _resources.GetShader(_compositeShader).TryReload();
    }

    /// <summary>明部を抜いて <see cref="_bright"/> に貯める。</summary>
    private void ExtractBright()
    {
        _bright.Bind();

        Shader shader = _resources.GetShader(_brightShader);
        shader.Use();
        shader.SetFloat("uThreshold", BloomThreshold);
        BindTexture(0, _scene.Color, shader, "uScene");

        DrawFullscreen();
    }

    /// <summary>
    /// 横 → 縦 を <see cref="BlurIterations"/> 回繰り返す。結果は <see cref="_blurA"/> に入る。
    ///
    /// **同じテクスチャを読みながら同じテクスチャに書くことはできない**
    /// (結果が未定義。GPU は読み書きの順序を保証しない)ので、2枚を交互に使う。
    /// これが ping-pong と呼ばれる形で、後処理ではどこでも出てくる。
    /// </summary>
    private void Blur()
    {
        Shader shader = _resources.GetShader(_blurShader);
        shader.Use();

        // 1テクセルぶんの移動量。**バッファの大きさで決まる**ので、
        // 画面をリサイズすると滲みの広さも自動で追従する。
        var texelX = new Vector2(1.0f / _blurA.Width, 0.0f);
        var texelY = new Vector2(0.0f, 1.0f / _blurA.Height);

        Framebuffer source = _bright;

        for (int i = 0; i < BlurIterations; i++)
        {
            // 横パス: source → _blurB
            _blurB.Bind();
            shader.SetVector2("uDirection", texelX);
            BindTexture(0, source.Color, shader, "uSource");
            DrawFullscreen();

            // 縦パス: _blurB → _blurA
            _blurA.Bind();
            shader.SetVector2("uDirection", texelY);
            BindTexture(0, _blurB.Color, shader, "uSource");
            DrawFullscreen();

            // 2周目以降は、前の往復の結果を入力にする。
            source = _blurA;
        }
    }

    /// <summary>露出・合成・トーンマップ・ガンマ。**唯一、画面へ書くパス**。</summary>
    private void Composite(int screenWidth, int screenHeight)
    {
        Framebuffer.BindDefault(_gl, screenWidth, screenHeight);

        Shader shader = _resources.GetShader(_compositeShader);
        shader.Use();
        shader.SetFloat("uExposure", Exposure);
        shader.SetFloat("uBloomIntensity", BloomIntensity);
        shader.SetInt("uToneMap", (int)ToneMap);

        // ブルームを切っているときは「シーンのみ」と同じ扱いにする。
        // シェーダ側に「ブルームを足すか」の分岐をもう1つ増やすより、
        // **呼ぶ側で意味を1本にまとめる**ほうが分岐が減る。
        int debug = (int)DebugView;
        if (!BloomEnabled && debug == 0)
        {
            debug = 1;
        }

        shader.SetInt("uDebug", debug);

        BindTexture(0, _scene.Color, shader, "uScene");

        // 1番のユニットに刺すものは表示モードで変わる。
        // **中間バッファを見るモードでは、そのバッファ自身を刺して全画面に映す**。
        Texture bloom = DebugView switch
        {
            PostDebugView.Bright => _bright.Color,
            _ => _blurA.Color,
        };

        BindTexture(1, bloom, shader, "uBloom");

        DrawFullscreen();
    }

    private void BindTexture(int unit, Texture texture, Shader shader, string name)
    {
        texture.Bind(TextureUnit.Texture0 + unit);
        shader.SetInt(name, unit);
    }

    /// <summary>
    /// 画面を覆う三角形を1枚描く。**頂点バッファは無い**(<c>fullscreen.vert</c> 参照)。
    /// </summary>
    private void DrawFullscreen()
    {
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _passes++;
    }

    private void SetCap(EnableCap cap, bool enabled)
    {
        if (enabled)
        {
            _gl.Enable(cap);
        }
        else
        {
            _gl.Disable(cap);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _scene.Dispose();
        _bright.Dispose();
        _blurA.Dispose();
        _blurB.Dispose();

        _gl.DeleteVertexArray(_emptyVao);
        _emptyVao = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
