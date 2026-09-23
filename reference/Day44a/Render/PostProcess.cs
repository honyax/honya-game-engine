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
/// FXAA の効き(Shift+F4)。**しきい値と歩幅の組に名前を付けたもの**。
///
/// 数字そのものは <see cref="PostProcess.SetFxaaQuality"/> が入れる。
/// 元の FXAA 3.11 にも同じ趣旨のプリセットが 10 段階あり、
/// コンソールでは低め、PC では高めを使うのが定番だった。
/// </summary>
internal enum FxaaQuality
{
    /// <summary>粗い。縁を拾いにくく、混ぜる幅も狭い。**残るジャギーが見える**。</summary>
    Low,

    /// <summary>元の FXAA の既定値。ほとんどの場面でこれで足りる。</summary>
    Medium,

    /// <summary>細かい段差まで拾う。</summary>
    High,

    /// <summary>拾えるだけ拾う。**細い線が滲み始める**のが見える。</summary>
    Extreme,
}

/// <summary>FXAA の中間の量を直接見る(Shift+F2)。</summary>
internal enum FxaaDebugView
{
    /// <summary>普通に出す。</summary>
    None,

    /// <summary>輝度。**FXAA が見ている世界そのもの**。</summary>
    Luma,

    /// <summary>縁と判定された画素を赤く。しきい値の効き目が分かる。</summary>
    Edge,

    /// <summary>元の色からどれだけ動いたか。**輪郭の線だけが光るのが正解**。</summary>
    Blend,
}

/// <summary>左右に並べて比べる窓(Shift+F11)。**画面の左半分を加工前にする**。</summary>
internal enum PostSplit
{
    /// <summary>比較しない。</summary>
    None,

    /// <summary>左半分をグレーディング前にする(<c>composite.frag</c>)。</summary>
    Grade,

    /// <summary>左半分を FXAA 前にする(<c>fxaa.frag</c>)。</summary>
    Fxaa,
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
///                   露出 → 合成 → グレーディング → トーンマップ → ガンマ
///                                       │
///                                       ▼
///                              [LDR バッファ RGBA8] ──▶ FXAA ──▶ 画面
/// </code>
///
/// <para>
/// <b>Day 38 で出口が2段になった</b>。トーンマップとガンマまで済ませた 8bit の絵を
/// いったん <see cref="_ldr"/> に置き、そこから FXAA が画面へ出す。
/// FXAA は「もう出来上がった絵を見てジャギーを均す」手法なので、
/// <b>ガンマまで通したあとの値でなければ正しく動かない</b>——
/// 詳しくは <c>fxaa.frag</c> の <c>Luma</c> を参照。
/// FXAA を切っているときは <see cref="_ldr"/> を素通りして直接画面へ書く。
/// </para>
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

    /// <summary>
    /// 合成まで済ませた 8bit の絵(Day 38)。**FXAA の入力**。
    ///
    /// <b>なぜ RGBA8 でよいのか</b>。ここに来る値はトーンマップとガンマを通ったあとで、
    /// すでに 0〜1 に畳まれている。畳んだあとの値に 16bit の幅は要らない——
    /// 画面に出す値そのものなので、画面と同じ精度で足りる。
    /// 1920x1080 で 8.3MB。RGBA16F にすると倍の 16.6MB を、
    /// **何も足さずに**使うことになる。
    ///
    /// <para>
    /// FXAA を切っていても確保したままにしてある。作り直す手間と、
    /// 「切っている間だけ VRAM が減る」という分かりにくさを避けるため——
    /// HUD に出る MB は常に同じ値になる。
    /// </para>
    /// </summary>
    private readonly Framebuffer _ldr;

    private readonly Handle<Shader> _brightShader;
    private readonly Handle<Shader> _blurShader;
    private readonly Handle<Shader> _compositeShader;
    private readonly Handle<Shader> _fxaaShader;

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

        // **画面と同じ大きさ**でなければならない(Day 38)。
        // FXAA は「1テクセルの隣」を見る手法なので、
        // 縮めた絵の上で走らせると、画面に拡大した時点で階段が戻ってくる。
        // ブルームと違い、**ここは1画素の精度そのものが仕事**。
        _ldr = new Framebuffer(gl, width, height, RenderTargetFormat.Rgba8, depth: false);

        string fullscreen = Path.Combine(shaderDirectory, "fullscreen.vert");
        _brightShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "bright.frag"));
        _blurShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "blur.frag"));
        _compositeShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "composite.frag"));
        _fxaaShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "fxaa.frag"));

        _emptyVao = gl.GenVertexArray();

        SetFxaaQuality(FxaaQuality.Medium);
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

    /// <summary>カラーグレーディングのつまみ(Day 38)。**数字だけを持つ相棒**。</summary>
    public ColorGrade Grade { get; } = new();

    /// <summary>FXAA を掛けるか(Shift+F1)。OFF なら合成が直接画面へ書く。</summary>
    public bool FxaaEnabled { get; set; } = true;

    /// <summary>FXAA の中間の量を直接見る(Shift+F2)。</summary>
    public FxaaDebugView FxaaDebugView { get; set; } = FxaaDebugView.None;

    /// <summary>いま選ばれている効き(Shift+F4)。HUD の表示用。</summary>
    public FxaaQuality Quality { get; private set; } = FxaaQuality.Medium;

    /// <summary>
    /// 局所コントラストがこの割合を超えたら縁とみなす(相対しきい値)。
    ///
    /// **1.0 にすると何も縁にならない**——自己チェックはこれを使って
    /// 「早期打ち切りの経路が本当に効いているか」を確かめる。
    /// </summary>
    public float FxaaEdgeThreshold { get; set; }

    /// <summary>暗いところで誤検出しないための下限(絶対しきい値)。</summary>
    public float FxaaEdgeThresholdMin { get; set; }

    /// <summary>何テクセルまで離れて混ぜてよいか。**大きいほど滑らかで、大きいほど滲む**。</summary>
    public float FxaaSpanMax { get; set; }

    /// <summary>左右に並べて比べる窓(Shift+F11)。</summary>
    public PostSplit Split { get; set; } = PostSplit.None;

    /// <summary>比較の境目(0〜1)。既定は画面の真ん中。</summary>
    public float SplitPosition { get; set; } = 0.5f;

    /// <summary>シーンバッファの内容。自己チェックから読み戻すために公開する。</summary>
    public Framebuffer Scene => _scene;

    /// <summary>合成まで済ませた 8bit の絵。自己チェックが読み戻すために公開する。</summary>
    public Framebuffer Ldr => _ldr;

    /// <summary>パイプラインが抱えている VRAM の推定バイト数。</summary>
    public long ByteSize =>
        _scene.ByteSize + _bright.ByteSize + _blurA.ByteSize + _blurB.ByteSize + _ldr.ByteSize;

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
    public void End(int screenWidth, int screenHeight) =>
        EndCore(null, screenWidth, screenHeight);

    /// <summary>
    /// **自己チェック用の出口**(Day 38)。画面ではなく <paramref name="target"/> へ出す。
    ///
    /// 合成 → FXAA を本番とまったく同じ経路で走らせるので、
    /// <b>読み戻して数字で確かめられる</b>。
    /// 画面(既定のフレームバッファ)を読み戻すこともできなくはないが、
    /// そちらは「今どちらのバッファが表か」に結果が左右されるので、
    /// 確かめる側の道具としては使えない。
    /// </summary>
    public void EndToTarget(Framebuffer target) =>
        EndCore(target, target.Width, target.Height);

    private void EndCore(Framebuffer? target, int screenWidth, int screenHeight)
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

        // **FXAA を掛けるかどうかで、合成の行き先が変わる**(Day 38)。
        //
        // 掛けないときに LDR バッファを経由して素通しのコピーを1パス挟む、
        // という書き方もできる(コードは1本になる)が、
        // **1920x1080 の読み書きが丸ごと1回増える**のでやらない。
        // 後処理では「パスを1つ増やす/減らす」が、そのまま帯域の話になる。
        if (FxaaEnabled)
        {
            Composite(_ldr, _ldr.Width, _ldr.Height);
            ApplyFxaa(target, screenWidth, screenHeight);
        }
        else
        {
            Composite(target, screenWidth, screenHeight);
        }

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

        // **LDR バッファは画面と同じ大きさのまま**(Day 38)。
        // ここを縮めると FXAA が拡大後の階段を見られなくなる。
        _ldr.Resize(width, height);
    }

    /// <summary>後処理のシェーダを読み直す(F5)。**絵を見ながら曲線をいじる**ために要る。</summary>
    public void ReloadShaders()
    {
        _resources.GetShader(_brightShader).TryReload();
        _resources.GetShader(_blurShader).TryReload();
        _resources.GetShader(_compositeShader).TryReload();

        // **FXAA こそリロードが効く**(Day 38)。しきい値も歩幅も、
        // 数字を1つ変えて縁を見る、を繰り返して決めるもの。
        _resources.GetShader(_fxaaShader).TryReload();
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

    /// <summary>
    /// 露出・合成・グレーディング・トーンマップ・ガンマ。
    ///
    /// **Day 38 で行き先が引数になった**。FXAA が有効なら
    /// <see cref="_ldr"/> へ、無効なら画面(あるいは自己チェックの的)へ。
    /// </summary>
    private void Composite(Framebuffer? target, int screenWidth, int screenHeight)
    {
        BindTarget(target, screenWidth, screenHeight);

        Shader shader = _resources.GetShader(_compositeShader);
        shader.Use();
        shader.SetFloat("uExposure", Exposure);
        shader.SetFloat("uBloomIntensity", BloomIntensity);
        shader.SetInt("uToneMap", (int)ToneMap);

        // グレーディングのつまみ(Day 38)。**送るだけで1パスも増えない**。
        Grade.Apply(shader);
        shader.SetFloat("uGradeSplit", Split == PostSplit.Grade ? SplitPosition : 0.0f);

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

    /// <summary>
    /// **FXAA**(Day 38)。<see cref="_ldr"/> を読んで、縁だけを均して出す。
    ///
    /// <para>
    /// 入力が「ガンマまで済ませた 8bit の絵」であることが効いている。
    /// FXAA はジオメトリも深度も見ず、<b>1枚の画像の輝度だけ</b>で段差を探すので、
    /// ポリゴンの縁だけでなくテクスチャの模様や高輝度部の縁にも効く——
    /// MSAA が三角形の縁しか直せないのと、ここが決定的に違う。
    /// </para>
    ///
    /// <para>
    /// 代償はフルスクリーン1パス。読むのは 8bit の画像1枚で、
    /// 1画素あたり最大9回のテクスチャ取得。
    /// <b>ほとんどの画素は最初の5回で抜ける</b>(平らなら早期打ち切り)ので、
    /// 実測は 1920x1080 で 0.2ms 前後に収まる。
    /// </para>
    /// </summary>
    private void ApplyFxaa(Framebuffer? target, int screenWidth, int screenHeight)
    {
        BindTarget(target, screenWidth, screenHeight);

        Shader shader = _resources.GetShader(_fxaaShader);
        shader.Use();

        // **入力の大きさで割る**。行き先ではない。
        // FXAA が知りたいのは「読む側の1テクセルはどれだけか」なので、
        // ここを画面の大きさで計算すると、
        // 自己チェックが別の大きさの的へ出したときだけ結果がずれる。
        shader.SetVector2("uTexelSize", new Vector2(1.0f / _ldr.Width, 1.0f / _ldr.Height));

        shader.SetFloat("uEdgeThreshold", FxaaEdgeThreshold);
        shader.SetFloat("uEdgeThresholdMin", FxaaEdgeThresholdMin);
        shader.SetFloat("uSpanMax", FxaaSpanMax);

        // この2つはつまみにしていない。元の FXAA でも固定値で、
        // 動かしても絵がほとんど変わらないため。
        shader.SetFloat("uReduceMul", 1.0f / 8.0f);
        shader.SetFloat("uReduceMin", 1.0f / 128.0f);

        shader.SetInt("uDebug", (int)FxaaDebugView);
        shader.SetFloat("uSplit", Split == PostSplit.Fxaa ? SplitPosition : 0.0f);

        BindTexture(0, _ldr.Color, shader, "uSource");

        DrawFullscreen();
    }

    /// <summary>
    /// 効きの段を選ぶ(Shift+F4)。**数字はここに1箇所だけ**置く。
    ///
    /// 相対しきい値は「明るいところほど大きな差でないと縁とみなさない」割合、
    /// 絶対しきい値は「真っ暗なところでノイズを縁と誤認しない」ための床。
    /// 元の FXAA 3.11 のコメントに載っている値をそのまま使っている。
    /// </summary>
    public void SetFxaaQuality(FxaaQuality quality)
    {
        Quality = quality;

        (FxaaEdgeThreshold, FxaaEdgeThresholdMin, FxaaSpanMax) = quality switch
        {
            FxaaQuality.Low => (0.250f, 0.0833f, 4.0f),
            FxaaQuality.High => (0.125f, 0.0312f, 8.0f),
            FxaaQuality.Extreme => (0.063f, 0.0156f, 16.0f),
            _ => (0.166f, 0.0625f, 8.0f),
        };
    }

    /// <summary>
    /// 描き込み先を決める。<c>null</c> なら画面(既定のフレームバッファ)。
    ///
    /// **ビューポートも一緒に変わる**ので、
    /// どちらへ書くかを1箇所にまとめておかないと必ず取りこぼす(Day 31 の轍)。
    /// </summary>
    private void BindTarget(Framebuffer? target, int width, int height)
    {
        if (target is null)
        {
            Framebuffer.BindDefault(_gl, width, height);
        }
        else
        {
            target.Bind();
        }
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
        _ldr.Dispose();

        _gl.DeleteVertexArray(_emptyVao);
        _emptyVao = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
