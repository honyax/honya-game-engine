using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>G-Buffer のどれを直接見るか(「ディファード」の F3)。</summary>
internal enum GBufferView
{
    /// <summary>普通に光を当てた絵。</summary>
    None,

    /// <summary>ベースカラー。**陰影が1つも無い**のが正しい姿。</summary>
    Albedo,

    /// <summary>ビュー空間の法線。右向きが赤、上向きが緑、こちら向きが青。</summary>
    Normal,

    /// <summary>カメラからの距離。近いほど白い。</summary>
    Depth,

    /// <summary>金属度。ゴミ箱と街灯だけが白くなる。</summary>
    Metallic,

    /// <summary>粗さ。</summary>
    Roughness,

    /// <summary>モデルが焼いてきた AO(SSAO ではない)。</summary>
    Occlusion,

    /// <summary>
    /// 発光。**裏通りには光る材質が1つも無い**ので真っ黒が正しい。
    /// プレイアブルデモで当たり判定(「プレイアブルデモ」の F6)を出すと、カプセルと接地の印だけが光る。
    /// </summary>
    Emissive,
}

/// <summary>
/// **G-Buffer**(Geometry Buffer)。Day 52 の主役の半分。
///
/// <para>
/// Day 51 までの本描画は、1つの画素シェーダ(<c>textured.frag</c>)の中で
/// 「表面を決める」と「光を当てる」を続けてやっていた。
/// ディファードレンダリングはこれを<b>真ん中で切る</b>。
/// </para>
///
/// <code>
///   フォワード   三角形 → [表面を決める → 光を当てる] → 色
///   ディファード 三角形 → [表面を決める] → G-Buffer → [光を当てる] → 色
///                         ↑ 幾何パス            ↑ ライティングパス(三角形を見ない)
/// </code>
///
/// <para>
/// 切り口に置く中間データが G-Buffer で、中身は<b>「光を当てるのに要るもの」の全部</b>。
/// 光を当てる側は三角形もマテリアルも知らず、画素ごとにこの4枚を読むだけになる。
/// </para>
///
/// <list type="table">
/// <item><term>0 (RGBA8)</term><description>ベースカラー(<b>平方根で詰める</b>)。A は空き</description></item>
/// <item><term>1 (RGBA8)</term><description>R = AO / G = 粗さ / B = 金属度。<b>glTF の ORM と同じ並び</b></description></item>
/// <item><term>2 (RGBA16F)</term><description>ビュー空間の法線 + 距離。<b>Day 37 の幾何バッファとまったく同じ形</b></description></item>
/// <item><term>3 (RGBA16F)</term><description>発光。1.0 を超えるので 16F</description></item>
/// </list>
///
/// <para>
/// <b>位置を持たない</b>のが最初の設計判断。位置は3成分の float で、
/// そのまま持つと RGBA16F をもう1枚食う。ところが「どの画素か」は
/// <c>gl_FragCoord</c> が知っているので、<b>距離1つあれば位置は戻せる</b>
/// (<c>ssao.frag</c> の <c>ViewPosition</c> と同じ式)。
/// G-Buffer の重さは帯域としてそのまま速度に効くので、戻せるものは持たない。
/// </para>
///
/// <para>
/// <b>2枚目を Day 37 の形に合わせた</b>のが2つ目の判断。
/// SSAO はこれまで自分で幾何パスを1回描いて法線と距離を作っていたが、
/// 同じものが G-Buffer にあるなら借りればよい——
/// <b>1フレームにジオメトリを描く回数が 3 回(影・幾何・本描画)から 2 回(影・G-Buffer)に減る</b>。
/// Day 37 の設計書に書いた歪みが、ここで片付く。
/// </para>
///
/// <para>
/// <b>シーンを知らない</b>のは <see cref="Ssao"/> たちと同じ(5つ目の「自分でバッファを持ち、
/// シェーダは借りる」クラス)。<see cref="Begin"/> と <see cref="End"/> の間で、
/// 何を描くかは外が決める。
/// </para>
/// </summary>
internal sealed class GBuffer : IDisposable
{
    /// <summary>
    /// アタッチメントの番号。**<c>textured.frag</c> の <c>layout(location = N)</c> と two-way の約束**。
    /// </summary>
    public const int AlbedoIndex = 0;

    public const int MaterialIndex = 1;

    public const int NormalDepthIndex = 2;

    public const int EmissiveIndex = 3;

    /// <summary>
    /// 4枚の形式。**中身ごとに必要な精度が違う**ので、全部を 16F にはしない。
    ///
    /// <para>
    /// 色と材質は 0〜1 に収まり、8bit(256 段)で目に見える差が出ない。
    /// 距離と発光は 1.0 を超えるので 16F が要る。
    /// 全部 16F にすると 1画素 32 バイトになり、いまの 24 バイトより 3 割重い。
    /// </para>
    /// </summary>
    public static readonly RenderTargetFormat[] Layout =
    [
        RenderTargetFormat.Rgba8,
        RenderTargetFormat.Rgba8,
        RenderTargetFormat.Rgba16F,
        RenderTargetFormat.Rgba16F,
    ];

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Framebuffer _target;
    private readonly Handle<Shader> _viewShader;

    /// <summary>全画面の三角形用の空 VAO(<see cref="Ssao"/> と同じ手口)。</summary>
    private uint _emptyVao;

    private bool _savedDepthTest;
    private bool _savedBlend;

    private bool _disposed;

    public GBuffer(GL gl, RenderResources resources, string shaderDirectory, int width, int height)
    {
        _gl = gl;
        _resources = resources;

        // **深度はレンダーバッファのまま**でよい。位置は 2 枚目の距離から戻すので、
        // 深度をテクスチャとして読む場面が無い。要るのは「手前のものが残る」ための深度テストと、
        // あとでシーンのバッファへ写す(<see cref="Framebuffer.BlitDepthTo"/>)ことだけ。
        _target = new Framebuffer(gl, width, height, Layout, depth: true);

        _viewShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "fullscreen.vert"),
            Path.Combine(shaderDirectory, "gbuffer-view.frag"));

        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>中間結果を直接見る(「ディファード」の F3)。</summary>
    public GBufferView DebugView { get; set; } = GBufferView.None;

    /// <summary>4枚 + 深度の入れ物。**深度を写すときに相手として要る**。</summary>
    public Framebuffer Target => _target;

    /// <summary>
    /// 2枚目(ビュー法線 + 距離)。**SSAO が自前の幾何パスの代わりに読む**。
    /// </summary>
    public Texture NormalDepth => _target.Colors[NormalDepthIndex];

    public int Width => _target.Width;

    public int Height => _target.Height;

    /// <summary>4枚 + 深度が占める VRAM の推定バイト数。</summary>
    public long ByteSize => _target.ByteSize;

    /// <summary>
    /// **幾何パスを始める**。以降の描画は4枚へ同時に行く。
    ///
    /// <para>
    /// <b>クリア色は (0,0,0,0)</b>。2枚目の距離 0 が「何も描かれていない」印になるのは
    /// Day 37 と同じ約束で、ライティングパスはこれを見て空を素通りさせる。
    /// glClear は挿してある全部の枚数を同じ色で塗る。
    /// </para>
    ///
    /// <para>
    /// <b>ブレンドは必ず切る</b>。G-Buffer の中身は色ではなく「値」なので、
    /// 前に書いた法線と混ぜると、どこも向いていない法線ができる。
    /// <b>深度テストは必ず入れる</b>(Z キーで切っていても)——
    /// 切れていると最後に描いたものが残り、深度も書かれないので、
    /// シーンのバッファへ写した深度が使いものにならなくなる。
    /// </para>
    /// </summary>
    public void Begin()
    {
        _savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _savedBlend = _gl.IsEnabled(EnableCap.Blend);

        _target.Bind();

        _gl.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>幾何パスを閉じる。借りた状態を返し、画面へ戻す。</summary>
    public void End(int screenWidth, int screenHeight)
    {
        SetCap(EnableCap.DepthTest, _savedDepthTest);
        SetCap(EnableCap.Blend, _savedBlend);

        Framebuffer.BindDefault(_gl, screenWidth, screenHeight);
    }

    /// <summary>
    /// **ライティングパスが読めるように4枚を刺す**。位置を戻すための行列も送る。
    ///
    /// <para>
    /// <b>番号は 0・1・2・4</b>。<see cref="Material.Apply"/> の表をそのまま引き継いでいる——
    /// 0 がベースカラー、1 がメタリック・ラフネス、2 が法線、4 が発光。
    /// ライティングパスにはマテリアルが無いので 0〜4 は空いていて、
    /// <b>「その番号に何の意味の値が居るか」まで同じ</b>にしておくと表を1枚で済ませられる。
    /// 3 番(AO)は 1 枚目の R に畳んだので空く。5〜10 は影・IBL・SSAO が今までどおり使う。
    /// </para>
    /// </summary>
    public void Apply(Shader shader, Camera camera)
    {
        BindAttachment(shader, "uGAlbedo", AlbedoIndex, unit: 0);
        BindAttachment(shader, "uGMaterial", MaterialIndex, unit: 1);
        BindAttachment(shader, "uGNormalDepth", NormalDepthIndex, unit: 2);
        BindAttachment(shader, "uGEmissive", EmissiveIndex, unit: 4);

        // **ビュー空間 → 世界**。G-Buffer の法線と位置はビュー空間(Day 37 の形)で、
        // 影も IBL も点光源も世界で考えているので、戻す行列が要る。
        // ビュー行列は回転と平行移動だけなので逆行列は必ずある。
        Matrix4x4.Invert(camera.ViewMatrix, out Matrix4x4 inverseView);
        shader.SetMatrix4("uInverseView", inverseView);

        // **距離から位置を戻す2つの数**(ssao.frag の uProjScale と同じもの)。
        Matrix4x4 projection = camera.ProjectionMatrix;
        shader.SetVector2("uProjScale", new Vector2(1.0f / projection.M11, 1.0f / projection.M22));
        shader.SetInt("uOrthographic", camera.Mode == ProjectionMode.Orthographic ? 1 : 0);
    }

    /// <summary>
    /// 1枚を画面いっぱいに出す(「ディファード」の F3)。**後処理の外**で呼ぶこと。
    ///
    /// <para>
    /// SSAO の表示(<see cref="Ssao.DrawDebug"/>)と同じ理由で全画面にする。
    /// G-Buffer の不具合は「法線だけが黒い」「粗さと金属度が入れ替わっている」の形で出て、
    /// <b>光を当てた絵ではそれらしく見えてしまう</b>。1枚ずつ見るのがいちばん速い。
    /// </para>
    /// </summary>
    public void DrawDebug(int screenWidth, int screenHeight, float depthRange)
    {
        if (DebugView == GBufferView.None)
        {
            return;
        }

        Framebuffer.BindDefault(_gl, screenWidth, screenHeight);

        Shader shader = _resources.GetShader(_viewShader);
        shader.Use();
        shader.SetInt("uMode", (int)DebugView);
        shader.SetFloat("uDepthRange", MathF.Max(depthRange, 0.001f));

        BindAttachment(shader, "uGAlbedo", AlbedoIndex, unit: 0);
        BindAttachment(shader, "uGMaterial", MaterialIndex, unit: 1);
        BindAttachment(shader, "uGNormalDepth", NormalDepthIndex, unit: 2);
        BindAttachment(shader, "uGEmissive", EmissiveIndex, unit: 4);

        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);

        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);
        SetCap(EnableCap.DepthTest, depth);
        SetCap(EnableCap.Blend, blend);
    }

    /// <summary>ウィンドウの大きさが変わったら、4枚と深度をまとめて作り直す。</summary>
    public void Resize(int width, int height) => _target.Resize(width, height);

    public void ReloadShaders() => _resources.GetShader(_viewShader).TryReload();

    private void BindAttachment(Shader shader, string name, int index, int unit)
    {
        _target.Colors[index].Bind(TextureUnit.Texture0 + unit);
        shader.SetInt(name, unit);
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
        _target.Dispose();

        if (_emptyVao != 0)
        {
            _gl.DeleteVertexArray(_emptyVao);
            _emptyVao = 0;
        }

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
