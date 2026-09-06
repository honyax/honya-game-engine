using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// シャドウマップ。**光の目から見た深度を1枚のテクスチャに焼き、あとで引く**。
///
/// 影の正体は、突き詰めると1つの問いに尽きる。
///
/// > この点は、光から**まっすぐ見えている**か?
///
/// 見えていれば光が当たり、途中に何かが挟まっていれば影になる。
/// 「見えているか」を毎ピクセル調べる方法として、レイトレーシングなら
/// 光へ向かってレイを飛ばす。ラスタライザにはレイが無いので、代わりに
/// **カメラを光の位置に置いて1回描き、いちばん手前の深度だけを覚えておく**。
/// これがシャドウマップで、Day 31 の Render To Texture がそのまま土台になる。
///
/// <para>
/// 手順は2パス。
/// <list type="number">
/// <item><b>深度パス</b> … 光から見て、色を書かずに深度だけを <see cref="Framebuffer"/> へ描く</item>
/// <item><b>本描画</b> … 各ピクセルの世界座標を光の座標系へ写し、
///   焼いておいた深度と**比べる**。自分のほうが奥なら、間に何かある = 影</item>
/// </list>
/// </para>
///
/// <para>
/// <b>この形の限界も最初に押さえておく</b>。シャドウマップは
/// 「光から見た絵」を有限の解像度で持つので、
///   - 解像度が足りなければ影の輪郭がギザギザになる(エイリアス)
///   - 深度の比較は等号ぎりぎりなので、自分自身を影と判定しやすい(シャドウアクネ)
///   - 平行光源は無限遠なので、**どこを写すか**を自分で決めないといけない
/// という3つが必ず付いてくる。今日はこの3つと正面から付き合う。
/// </para>
///
/// <para>
/// <b>シーンを知らない</b>のは <see cref="PostProcess"/> と同じ方針。
/// このクラスが持つのは深度バッファと光源行列だけで、
/// 「何を影として落とすか」は <c>Begin</c> と <c>End</c> の間で外から描いてもらう。
/// おかげでデモでもモデルでも、同じ道具がそのまま使える。
/// </para>
/// </summary>
internal sealed class ShadowMap : IDisposable
{
    /// <summary>解像度の候補。Ctrl+2 で巡回する。**2の冪にそろえる**(GPU が扱いやすい)。</summary>
    public static readonly int[] Resolutions = [512, 1024, 2048, 4096];

    private readonly GL _gl;
    private readonly RenderResources _resources;

    /// <summary>深度専用のフレームバッファ。カラーアタッチメントは無い。</summary>
    private Framebuffer _target;

    /// <summary>深度だけを書くシェーダ。**色を1つも出力しない**。</summary>
    private readonly Handle<Shader> _depthShader;

    /// <summary>シャドウマップを画面の隅に出すためのシェーダ。</summary>
    private readonly Handle<Shader> _viewShader;

    /// <summary>フルスクリーンの三角形用の空 VAO(<see cref="PostProcess"/> と同じ手口)。</summary>
    private uint _emptyVao;

    /// <summary>デバッグ表示のときに使う深度の見える範囲。<see cref="Begin"/> で決まる。</summary>
    private float _viewDepthMin;
    private float _viewDepthMax;

    /// <summary>
    /// 深度パスに入る前の GL の状態。**End で元に戻すため**に覚えておく。
    ///
    /// <see cref="PostProcess.End"/> と同じ用心で、OpenGL の状態がグローバルだから要る。
    /// ここで戻さないと、Z キー(深度テスト)・C キー(カリング)・W キー(ワイヤー)の
    /// 設定が影パスの都合で書き換わったまま本描画へ流れていく。
    /// </summary>
    private bool _savedDepthTest;
    private bool _savedCullFace;
    private int _savedPolygonMode;

    private bool _disposed;

    public ShadowMap(GL gl, RenderResources resources, string shaderDirectory, int resolution)
    {
        _gl = gl;
        _resources = resources;

        _target = Framebuffer.CreateDepthOnly(gl, resolution, resolution);

        _depthShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "depth.vert"),
            Path.Combine(shaderDirectory, "depth.frag"));

        _viewShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "fullscreen.vert"),
            Path.Combine(shaderDirectory, "shadow-view.frag"));

        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>影を落とすか。OFF にすると深度パスごと飛ばす(HUD の <c>影パス</c> が 0 になる)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>1辺のテクセル数。</summary>
    public int Resolution => _target.Width;

    /// <summary>VRAM の推定バイト数。解像度を上げた代償を HUD に出すため。</summary>
    public long ByteSize => _target.ByteSize;

    /// <summary>
    /// 焼き込み先。**自己チェックが深度を読み返す**ために開けてある
    /// (<see cref="PostProcess.Scene"/> と同じ理由)。
    /// 描画の本筋では触らない。
    /// </summary>
    public Framebuffer Target => _target;

    /// <summary>
    /// 光の座標系へ写す行列。**ビュー行列 × 正射影行列**を掛けたもの。
    ///
    /// 深度パスではこれを <c>uLightSpaceMatrix</c> として使い、
    /// 本描画では**まったく同じ行列**を使って世界座標を光の座標系へ写す。
    /// 2つがずれると影が丸ごとずれるので、**1か所で作って両方に配る**。
    /// </summary>
    public Matrix4x4 LightSpaceMatrix { get; private set; } = Matrix4x4.Identity;

    /// <summary>
    /// PCF の半径(テクセル)。0 なら1タップ、1 なら 3x3、2 なら 5x5、3 なら 7x7。
    /// <see cref="TapCount"/> がそのまま**1ピクセルあたりのテクスチャ読み込み回数**になる。
    /// </summary>
    public int PcfRadius { get; set; } = 1;

    /// <summary>PCF のタップ数。</summary>
    public int TapCount => ((2 * PcfRadius) + 1) * ((2 * PcfRadius) + 1);

    /// <summary>
    /// 深度バイアス。**比較の前に、自分の深度をこれだけ手前へずらす**。
    /// 単位は光の座標系の深度(0〜1)。<c>0</c> にするとシャドウアクネが出る。
    /// </summary>
    public float DepthBias { get; set; } = 0.0015f;

    /// <summary>
    /// 傾きに比例したバイアスを足すか。
    /// 光に対して寝ている面ほど1テクセルが覆う奥行きが広くなるので、必要なバイアスも大きくなる。
    /// </summary>
    public bool SlopeBias { get; set; } = true;

    /// <summary>
    /// 深度パスで**表を捨てて裏だけ描く**か。アクネのもう1つの対処法。
    /// 閉じた立体にしか使えない(<see cref="Begin"/> のコメント)。
    /// </summary>
    public bool CullFrontFaces { get; set; }

    /// <summary>光が照らす範囲の半径(ワールド単位)。**小さいほど影が細かくなる**。</summary>
    public float Radius { get; set; } = 6.0f;

    /// <summary>1テクセルが覆うワールドの長さ。影のギザギザの大きさそのもの。</summary>
    public float WorldPerTexel => Radius * 2.0f / Resolution;

    /// <summary>デバッグ表示を出すか(Ctrl+7)。</summary>
    public bool ShowMap { get; set; }

    /// <summary>この <c>Begin</c>〜<c>End</c> で描いたドローコール数。HUD 用。</summary>
    public int DrawCalls { get; set; }

    /// <summary>
    /// **光の目から見た行列を作り、深度パスを始める**。以降の描画はシャドウマップへ行く。
    ///
    /// <para>
    /// <b>平行光源には位置が無い</b>のが、ここでいちばん考えることになる点。
    /// 太陽は無限遠にあるので「カメラを光の位置に置く」ができない。
    /// 代わりに**向きだけ**が決まっているので、
    ///   - 投影は正射影(平行光線なので遠近感が付いてはいけない)
    ///   - 視点は「注目したい球の中心から、光の来る方へ引いた場所」
    /// と決める。引く距離は絵に影響しない(正射影なので)が、
    /// ニアクリップより手前のものが消えるので、余裕を持って 2 倍の半径ぶん引く。
    /// </para>
    ///
    /// <para>
    /// <b>どこを写すかは自分で決める</b>。カメラの投影行列は視野角と縦横比から一意に決まるが、
    /// 光の正射影の箱は**何も決めてくれない**。世界全体を入れれば影は粗くなり、
    /// 狭くすれば細かくなるが範囲外に影が出なくなる。
    /// この綱引きが「シャドウマップの調整」と呼ばれるものの大半で、
    /// 実際のエンジンは視錐台を距離で分割して箱を何段も作る(カスケードシャドウマップ)。
    /// 今日は1段だけにして、<see cref="Radius"/> を手で動かして綱引きを体感する。
    /// </para>
    /// </summary>
    /// <param name="lightDirection">光の**進む**向き(正規化済み)。太陽から地面へ向かうベクトル。</param>
    /// <param name="center">照らしたい球の中心。ふつうはカメラの注視点。</param>
    public void Begin(Vector3 lightDirection, Vector3 center)
    {
        float radius = MathF.Max(Radius, 0.1f);
        Vector3 direction = Vector3.Normalize(lightDirection);

        // **真上からの光で破綻しないように**。LookAt は視線と up が平行だと軸を作れない
        // (Camera.Up のコメントと同じ話)。真上に近ければ up を Z 軸に逃がす。
        Vector3 up = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        Vector3 eye = center - (direction * radius * 2.0f);

        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, center, up);

        // 近・遠は「視点から球の手前/奥まで」に少し余白を足したところに置く。
        // 近を 0 に近づけるほど、光と球の間にある物体まで拾えるが、
        // **深度の刻みがそのぶん粗くなる**ので必要なだけにする。
        float near = radius * 0.5f;
        float far = radius * 3.5f;

        Matrix4x4 projection = Camera.CreateOrthographic(radius * 2.0f, radius * 2.0f, near, far);

        LightSpaceMatrix = view * projection;

        // デバッグ表示で使う「実際に絵が入っている深度の範囲」。
        // 球の手前が radius、奥が 3*radius なので、near〜far のうち中央 2/3 しか使っていない。
        _viewDepthMin = (radius - near) / (far - near);
        _viewDepthMax = ((radius * 3.0f) - near) / (far - near);

        DrawCalls = 0;

        // シーン側の設定を覚えてから上書きする(フィールドのコメント)。
        _savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _savedCullFace = _gl.IsEnabled(EnableCap.CullFace);

        // **GL_POLYGON_MODE は int を2個返す**(表面用と裏面用)。
        // out int の版を使うと GL が 2 個目を書き込む先が無く、その場のメモリを踏む。
        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);
        _savedPolygonMode = polygonModes[0];

        _target.Bind();

        // **色は書かないので深度だけ消す**。カラーバッファが存在しないので、
        // ColorBufferBit を混ぜても効果は無い(GL は黙って無視する)。
        _gl.Clear(ClearBufferMask.DepthBufferBit);

        // 深度パスの間だけ深度テストを必ず有効にする。
        // Z キーで切っていても、ここは切られていると**シャドウマップが最後に描いたものになる**。
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);

        // ワイヤーフレーム(W キー)のままだと**輪郭線しか焼かれない**。
        // 影が線状に抜ける、という分かりにくい壊れ方をするので、ここで塗りに固定する。
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        // **表を捨てて裏だけ描く**とアクネが消える理由。
        // 影の判定でぶつかるのは「自分自身」で、自分の表面の深度と自分の深度がほぼ等しいから。
        // 裏面だけを焼けば、記録される深度は物体の**向こう側**になり、
        // 表面との間に物体の厚みぶんの隙間ができる。バイアス無しでもアクネが出ない。
        //
        // 代償は2つ。**閉じた立体でないと使えない**(床のような1枚板は裏が無いので消える)のと、
        // 薄い物体だと厚みが足りず、**影が本体から離れて浮く**(ピーターパン)こと。
        // 板を含むシーンでは既定を OFF にしてバイアスで対処するほうが素直で、今日もそうしている。
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(CullFrontFaces ? TriangleFace.Front : TriangleFace.Back);

        Shader shader = _resources.GetShader(_depthShader);
        shader.Use();
        shader.SetMatrix4("uLightSpaceMatrix", LightSpaceMatrix);
    }

    /// <summary>深度パスの1体ぶん。**モデル行列だけ送って描く**。マテリアルは要らない。</summary>
    public void Draw(Mesh<Vertex> mesh, Matrix4x4 model)
    {
        Shader shader = _resources.GetShader(_depthShader);
        shader.SetMatrix4("uModel", model);
        mesh.Draw();
        DrawCalls++;
    }

    /// <summary>
    /// 深度パスを閉じて、画面へ戻す。
    ///
    /// **ビューポートを戻すのを忘れない**。<see cref="Framebuffer.Bind"/> が
    /// シャドウマップの大きさ(2048 など)に変えているので、
    /// ここで戻さないと本描画が画面の一部にしか出ない(あるいははみ出す)。
    /// </summary>
    public void End(int screenWidth, int screenHeight)
    {
        Framebuffer.BindDefault(_gl, screenWidth, screenHeight);

        // **借りた状態は返す**。カリングの向きは既定(裏を捨てる)に戻し、
        // 有効/無効とポリゴンモードは Begin の時点の値へ。
        _gl.CullFace(TriangleFace.Back);
        SetCap(EnableCap.DepthTest, _savedDepthTest);
        SetCap(EnableCap.CullFace, _savedCullFace);
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)_savedPolygonMode);
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

    /// <summary>
    /// 本描画で読めるようにシャドウマップを割り当て、必要な uniform をまとめて送る。
    ///
    /// **フレームに1回でよい**(Day 15 の要点: uniform の3階層)。
    /// テクスチャユニットの割り当ては GL のコンテキストの状態、
    /// サンプラの番号はプログラムの状態なので、どちらも次に上書きするまで残る。
    /// マテリアルが使う 0〜4 の後ろ、**5 番**に固定で刺す。
    /// </summary>
    public void Apply(Shader shader, int unit = 5)
    {
        shader.SetMatrix4("uLightSpaceMatrix", LightSpaceMatrix);
        shader.SetInt("uShadowEnabled", Enabled ? 1 : 0);
        shader.SetInt("uPcfRadius", PcfRadius);
        shader.SetFloat("uShadowBias", DepthBias);
        shader.SetFloat("uShadowSlopeBias", SlopeBias ? DepthBias * 6.0f : 0.0f);
        shader.SetVector2("uShadowTexelSize", new Vector2(1.0f / Resolution, 1.0f / Resolution));

        // 深度テクスチャは必ず刺す。**影 OFF のときも刺しておく**——
        // サンプラが指すユニットに何も無い状態は、環境によっては未定義の値を返す。
        (_target.Depth ?? _resources.Placeholder).Bind(TextureUnit.Texture0 + unit);
        shader.SetInt("uShadowMap", unit);
    }

    /// <summary>
    /// 焼いたシャドウマップを画面の隅に出す(Ctrl+7)。**影がおかしいときの最初の1歩**。
    ///
    /// 影の不具合は「シャドウマップが空っぽ」「行列がずれている」「比較の向きが逆」の
    /// どれでも同じ絵(影が出ない/全部影)になるので、
    /// **焼けているものを直接見る**のが切り分けとしていちばん速い。
    ///   - 真っ白 … 何も描かれていない。深度パスに物体が来ていないか、行列が外れている
    ///   - モデルの形が見える … 深度パスは正しい。疑うのは本描画側の座標変換か比較
    ///
    /// 全画面三角形をそのまま使い、**ビューポートだけ隅に寄せて**描いている
    /// (板を用意して行列を組むより短い)。
    /// </summary>
    public void DrawDebug(int screenWidth, int screenHeight)
    {
        if (!ShowMap)
        {
            return;
        }

        // 画面の短辺の 30%。正方形にしないと、正方形のシャドウマップが歪んで見える。
        int size = Math.Max(96, (int)(Math.Min(screenWidth, screenHeight) * 0.3f));
        int margin = 12;

        Shader shader = _resources.GetShader(_viewShader);
        shader.Use();

        (_target.Depth ?? _resources.Placeholder).Bind(TextureUnit.Texture0);
        shader.SetInt("uDepthMap", 0);
        shader.SetFloat("uDepthMin", _viewDepthMin);
        shader.SetFloat("uDepthMax", _viewDepthMax);

        // 後処理のあとに呼ぶので、深度テストは切れている前提だが念のため。
        // ワイヤーフレーム(W キー)のときに線しか出ないのも防いでおく。
        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        _gl.Disable(EnableCap.DepthTest);

        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        _gl.Viewport(screenWidth - size - margin, margin, (uint)size, (uint)size);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.Viewport(0, 0, (uint)Math.Max(1, screenWidth), (uint)Math.Max(1, screenHeight));
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);

        if (depth)
        {
            _gl.Enable(EnableCap.DepthTest);
        }
    }

    /// <summary>解像度を変える。**テクスチャは作り直しになる**(大きさは確保時に決まる)。</summary>
    public void SetResolution(int resolution)
    {
        resolution = Math.Clamp(resolution, 128, 8192);
        if (resolution == Resolution)
        {
            return;
        }

        _target.Dispose();
        _target = Framebuffer.CreateDepthOnly(_gl, resolution, resolution);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _target.Dispose();

        if (_emptyVao != 0)
        {
            _gl.DeleteVertexArray(_emptyVao);
            _emptyVao = 0;
        }
    }
}
