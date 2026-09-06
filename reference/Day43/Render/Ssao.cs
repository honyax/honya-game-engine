using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>1画面ぶんの中間結果のうち、どれを直接見るか(Ctrl+F2)。</summary>
internal enum SsaoDebugView
{
    /// <summary>普通に合成した絵。</summary>
    None,

    /// <summary>遮蔽率そのもの。**今日いちばん使うデバッグ表示**。</summary>
    Occlusion,

    /// <summary>ビュー空間の法線。1パス目が正しく焼けているかの確認。</summary>
    Normal,

    /// <summary>カメラからの距離。同じく1パス目の確認。</summary>
    Depth,
}

/// <summary>
/// **スクリーンスペース環境遮蔽(SSAO)**。Day 37 の主役。
///
/// Day 36 で環境光が「まわりの景色ぜんぶ」になった。
/// ところが <see cref="EnvironmentMap"/> が返す放射照度は
/// <b>その点が空だけを見ている前提</b>の値で、
/// 隣に壁があろうと箱の下だろうと、法線が同じなら同じ明るさを返す。
///
/// <para>
/// 結果として、Day 36 までの絵には<b>物と物が接している場所の暗がりが無い</b>。
/// 立方体は床から浮いて見え、隅は妙に明るく、へこみは平らに見える。
/// 影(Day 33)は太陽が遮られたところを暗くするが、
/// <b>太陽と関係ない暗がり</b>——曇りの日でも隅が暗いこと——は誰も作っていなかった。
/// </para>
///
/// <para>
/// 本来これは <see cref="Pbr"/> のレンダリング方程式に入っている
/// <b>可視項</b> V(L) そのもので、正しく解くなら1画素ごとに
/// 半球じゅうへレイを飛ばすことになる。実時間ではとても無理なので、
/// <b>すでに描いてある深度バッファを「世界の模型」として流用する</b>——
/// これが Crytek(2007)の SSAO で、以後どのエンジンにも入っている。
/// </para>
///
/// <list type="number">
/// <item>
/// <b>1パス目(幾何パス)</b> … カメラから見て、ビュー空間の法線と距離を
/// 1枚のテクスチャに描く。色も材質も要らない。<see cref="BeginGeometry"/>
/// </item>
/// <item>
/// <b>2パス目(遮蔽の計算)</b> … 各画素で、法線側の半球にばらまいた点が
/// 「すでに描かれている面より奥にあるか」を数える。<c>ssao.frag</c>
/// </item>
/// <item>
/// <b>3パス目(ぼかし)</b> … 少ない標本で出たノイズを 4x4 でならす。<c>ssao-blur.frag</c>
/// </item>
/// <item>
/// <b>本描画</b> … 出来上がった遮蔽率を<b>環境光にだけ</b>掛ける。<see cref="Apply"/>
/// </item>
/// </list>
///
/// <para>
/// <b>この形の限界を最初に押さえておく</b>。スクリーンスペースは
/// 「画面に写っているものしか知らない」ので、
///   - 画面の外にある物は遮蔽しない(カメラを振ると隅の暗がりが揺れる)
///   - 手前の物の裏側は存在しないことになる(縁に沿って不自然な明暗が出る)
///   - 深度の1枚だけでは厚みが分からない(薄い板と分厚い柱の区別が付かない)
/// という3つが必ず付いてくる。**それでも使われる**のは、
/// 三角形の数にもシーンの構成にも一切依存せず、
/// 画面の画素数だけで代償が決まるから。
/// </para>
///
/// <para>
/// <b>シーンを知らない</b>のは <see cref="PostProcess"/> / <see cref="ShadowMap"/> /
/// <see cref="EnvironmentMap"/> と同じ方針。持っているのはバッファと設定だけで、
/// 「何を描くか」は <see cref="BeginGeometry"/> と <see cref="EndGeometry"/> の間で
/// 外から描いてもらう。
/// </para>
/// </summary>
internal sealed class Ssao : IDisposable
{
    /// <summary>標本数の候補(Ctrl+F4)。**そのままコストに比例する**。</summary>
    public static readonly int[] SampleCounts = [8, 16, 32, 64];

    /// <summary>カーネルに確保する本数。<c>ssao.frag</c> の配列の長さと一致させること。</summary>
    public const int MaxSamples = 64;

    /// <summary>ランダムな向きのタイルの一辺。**ぼかしの幅と同じ数**にする。</summary>
    public const int NoiseSize = 4;

    private readonly GL _gl;
    private readonly RenderResources _resources;

    /// <summary>1パス目の行き先。RGB = ビュー法線、A = 距離。**深度も要る**(手前優先)。</summary>
    private readonly Framebuffer _geometry;

    /// <summary>遮蔽率。**1成分 8bit で足りる**(<see cref="RenderTargetFormat.R8"/>)。</summary>
    private readonly Framebuffer _occlusion;

    /// <summary>ぼかした遮蔽率。本描画が読むのはこちら。</summary>
    private readonly Framebuffer _blurred;

    private readonly Handle<Shader> _geometryShader;
    private readonly Handle<Shader> _occlusionShader;
    private readonly Handle<Shader> _blurShader;
    private readonly Handle<Shader> _viewShader;

    /// <summary>半球状の標本。**接空間で持つ**ので、法線が変わっても作り直さなくてよい。</summary>
    private readonly Vector3[] _kernel = new Vector3[MaxSamples];

    /// <summary>4x4 のランダムな向き。画面にタイル状に敷く。</summary>
    private readonly Texture _noise;

    /// <summary><see cref="BeginGeometry"/> で受け取ったビュー行列。<see cref="Draw"/> が使う。</summary>
    private Matrix4x4 _view = Matrix4x4.Identity;

    /// <summary>フルスクリーンの三角形用の空 VAO(<see cref="PostProcess"/> と同じ手口)。</summary>
    private uint _emptyVao;

    /// <summary>幾何パスに入る前の GL の状態。<see cref="EndGeometry"/> で戻す。</summary>
    private bool _savedDepthTest;
    private bool _savedCullFace;
    private int _savedPolygonMode;

    /// <summary>画面の大きさ。AO バッファの大きさとは<b>別</b>(半解像度があるので)。</summary>
    private int _screenWidth;
    private int _screenHeight;

    private bool _disposed;

    public Ssao(GL gl, RenderResources resources, string shaderDirectory, int width, int height)
    {
        _gl = gl;
        _resources = resources;

        _screenWidth = Math.Max(1, width);
        _screenHeight = Math.Max(1, height);

        // **幾何バッファは必ず画面と同じ大きさ**にする。
        // ここを半分にすると、細い柱や葉の輪郭がそもそも記録されず、
        // AO を原寸で計算しても情報が戻らない。
        // 縮めてよいのは「計算した結果」であって「入力」ではない。
        _geometry = new Framebuffer(
            gl, _screenWidth, _screenHeight, RenderTargetFormat.Rgba16F, depth: true);

        _occlusion = CreateOcclusionBuffer();
        _blurred = CreateOcclusionBuffer();

        string fullscreen = Path.Combine(shaderDirectory, "fullscreen.vert");

        _geometryShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "geometry.vert"),
            Path.Combine(shaderDirectory, "geometry.frag"));

        _occlusionShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "ssao.frag"));
        _blurShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "ssao-blur.frag"));
        _viewShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "ssao-view.frag"));

        _emptyVao = gl.GenVertexArray();

        // **種を固定する**。自己チェックが同じ結果を再現できるようにするためで、
        // Day 19 の決定性チェックと同じ考え方。
        // 見た目の都合でも、起動のたびにノイズの模様が変わると
        // 「さっきと違って見えるのは設定を変えたからか、乱数か」が分からなくなる。
        var random = new Random(20370101);

        BuildKernel(random);
        _noise = CreateNoiseTexture(random);
    }

    /// <summary>SSAO を使うか(Ctrl+F1)。OFF にすると幾何パスごと飛ばす。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 遮蔽を探る球の半径(メートル、Ctrl+F3)。**絵の印象を最も強く決める**。
    ///
    /// <b>シーンの大きさに合わせて決めるもの</b>で、万能の値は無い。
    /// このデモは1辺 1m の立方体が並ぶ縮尺なので 1.0m にした。
    /// LearnOpenGL などの作例が 0.5 なのは、そちらの箱が小さいから——
    /// **数字をそのまま持ってくると効かない**のがこのつまみの厄介なところ。
    /// </summary>
    public float Radius { get; set; } = 1.0f;

    /// <summary>自己遮蔽よけの下駄(Ctrl+F8)。</summary>
    public float Bias { get; set; } = 0.025f;

    /// <summary>遮蔽の効き(Ctrl+F7)。</summary>
    public float Strength { get; set; } = 1.0f;

    /// <summary>遮蔽率にかける指数。1 より大きいと暗がりが締まる。</summary>
    public float Power { get; set; } = 1.6f;

    /// <summary>1画素あたりの標本数(Ctrl+F4)。</summary>
    public int SampleCount { get; set; } = 32;

    /// <summary>ぼかすか(Ctrl+F5)。OFF にすると**ノイズのタイルが直接見える**。</summary>
    public bool BlurEnabled { get; set; } = true;

    /// <summary>AO バッファを画面の半分にするか(Ctrl+F6)。**代償が 1/4 になる**。</summary>
    public bool HalfResolution { get; set; }

    /// <summary>直接光にも遮蔽率を掛けるか(Ctrl+F11)。**わざと間違えるための窓**。</summary>
    public bool ApplyToDirectLight { get; set; }

    /// <summary>中間結果を直接見る(Ctrl+F2)。</summary>
    public SsaoDebugView DebugView { get; set; } = SsaoDebugView.None;

    /// <summary>この幾何パスで描いた回数。HUD 用。</summary>
    public int DrawCalls { get; private set; }

    /// <summary>フルスクリーンパスを走らせた回数(遮蔽 + ぼかし)。</summary>
    public int PassCount { get; private set; }

    /// <summary>AO バッファの幅。半解像度のときは画面の半分。</summary>
    public int Width => _occlusion.Width;

    /// <summary>AO バッファの高さ。</summary>
    public int Height => _occlusion.Height;

    /// <summary>3枚が占める VRAM の推定バイト数。</summary>
    public long ByteSize => _geometry.ByteSize + _occlusion.ByteSize + _blurred.ByteSize;

    /// <summary>1パス目の中身。自己チェックが読み返すために開けてある。</summary>
    public Framebuffer Geometry => _geometry;

    /// <summary>本描画が読む遮蔽率。ぼかしを切っているときは生のほう。</summary>
    public Framebuffer Result => BlurEnabled ? _blurred : _occlusion;

    /// <summary>半球状の標本。自己チェックが性質を確かめるために開けてある。</summary>
    public ReadOnlySpan<Vector3> Kernel => _kernel;

    /// <summary>
    /// **1パス目を始める**。以降の描画はビュー法線と距離のバッファへ行く。
    ///
    /// <para>
    /// <see cref="ShadowMap.Begin"/> と形はそっくりだが、目的が違う。
    /// あちらは<b>光の目</b>から、こちらは<b>カメラの目</b>から描く。
    /// 同じジオメトリを1フレームに3回描くことになる
    /// (影 → 幾何 → 本描画)——**これがディファードレンダリングの動機**で、
    /// Day 51 でこの重複を1回にまとめる。
    /// </para>
    ///
    /// <para>
    /// <b>クリア色は (0,0,0,0)</b>。距離 0 は「カメラの位置」を意味するので
    /// 現実には起こらず、<b>何も描かれていない印</b>として使える。
    /// <c>ssao.frag</c> がこれを見て空を素通りさせている。
    /// </para>
    /// </summary>
    public void BeginGeometry(Camera camera)
    {
        DrawCalls = 0;
        PassCount = 0;

        _savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _savedCullFace = _gl.IsEnabled(EnableCap.CullFace);

        // **GL_POLYGON_MODE は int を2個返す**(表面用と裏面用)。ShadowMap.Begin と同じ用心。
        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);
        _savedPolygonMode = polygonModes[0];

        _geometry.Bind();

        _gl.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // 深度テストは必ず有効に。Z キーで切っていても、
        // ここが切れていると**最後に描いたものの法線**がバッファに残る。
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);

        // ワイヤーフレーム(W キー)のままだと輪郭線しか焼かれず、
        // AO が線の周りにだけ出るという分かりにくい壊れ方をする。
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        // **裏面は捨てる**。本描画と同じものが写っていないと、
        // AO と絵がずれる(裏面の法線は逆を向いている)。
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);

        Shader shader = _resources.GetShader(_geometryShader);
        shader.Use();
        shader.SetMatrix4("uProjection", camera.ProjectionMatrix);

        // ビュー行列は Draw のたびにモデル行列と掛けるので、ここでは覚えておくだけ。
        _view = camera.ViewMatrix;
    }

    /// <summary>幾何パスの1体ぶん。**モデル行列だけ渡す**(マテリアルは要らない)。</summary>
    /// <param name="joints">
    /// スキニングの関節行列(Day 41)。**空ならスキニングしない**。
    ///
    /// 深度パスと SSAO の幾何パスは本描画とは別のシェーダを使うので、
    /// **同じ関節行列を3回送ることになる**。冗長に見えるが、
    /// 3つのパスが同じ頂点位置を見ることのほうがずっと大事——
    /// 1つでも送り忘れると、影だけ・遮蔽だけがバインドポーズに取り残される。
    /// </param>
    public void Draw(Mesh<Vertex> mesh, Matrix4x4 model, ReadOnlySpan<Matrix4x4> joints = default)
    {
        Shader shader = _resources.GetShader(_geometryShader);

        Matrix4x4 modelView = model * _view;
        shader.SetMatrix4("uModelView", modelView);
        shader.SetMatrix3("uNormalMatrix", NormalMatrix(modelView));

        // ShadowMap.Draw と同じ理由で毎回設定する。
        shader.SetInt("uSkinned", joints.IsEmpty ? 0 : 1);

        if (!joints.IsEmpty)
        {
            shader.SetMatrix4Array("uJoints", joints);
        }

        mesh.Draw();
        DrawCalls++;
    }

    /// <summary>幾何パスを閉じる。**遮蔽の計算はまだ**(<see cref="Compute"/>)。</summary>
    public void EndGeometry()
    {
        // 借りた状態を返す(ShadowMap.End と同じ作法)。
        _gl.CullFace(TriangleFace.Back);
        SetCap(EnableCap.DepthTest, _savedDepthTest);
        SetCap(EnableCap.CullFace, _savedCullFace);
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)_savedPolygonMode);
    }

    /// <summary>
    /// **遮蔽率を計算してぼかす**。2パス目と3パス目。
    ///
    /// フルスクリーンの板を1〜2枚描くだけなので、
    /// 幾何パスと違ってシーンの複雑さに影響されない。
    /// **画素数と標本数の積**だけがコスト——ここが SSAO の性格そのもの。
    /// </summary>
    public void Compute(Camera camera, int screenWidth, int screenHeight)
    {
        // シーンが残していった GL の状態を畳む(PostProcess.End と同じ4つ)。
        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        bool cull = _gl.IsEnabled(EnableCap.CullFace);

        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        ComputeOcclusion(camera);

        if (BlurEnabled)
        {
            BlurOcclusion();
        }

        Framebuffer.BindDefault(_gl, screenWidth, screenHeight);

        SetCap(EnableCap.DepthTest, depth);
        SetCap(EnableCap.Blend, blend);
        SetCap(EnableCap.CullFace, cull);
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);
    }

    /// <summary>
    /// 本描画で読めるように遮蔽率を割り当て、uniform をまとめて送る。
    ///
    /// **10 番のユニット**に刺す。0〜4 がマテリアル、5 が影(Day 33)、
    /// 7〜9 が IBL(Day 36)なので、その後ろ。
    /// 6 は高さマップ(Day 34)なので飛ばしてある。
    /// </summary>
    public void Apply(Shader shader, int unit = 10)
    {
        shader.SetInt("uSsaoEnabled", Enabled ? 1 : 0);
        shader.SetInt("uSsaoOnDirect", ApplyToDirectLight ? 1 : 0);

        // **画面の大きさ**を送るのは、シェーダが gl_FragCoord を UV に直すため。
        // AO バッファの大きさではないことに注意——半解像度でも
        // gl_FragCoord は画面の座標なので、割る数は画面の大きさになる
        // (0〜1 に直してから引けば、拡大はサンプラのバイリニアが面倒を見る)。
        shader.SetVector2("uScreenSize", new Vector2(_screenWidth, _screenHeight));

        (Enabled ? Result.Color : _resources.Placeholder).Bind(TextureUnit.Texture0 + unit);
        shader.SetInt("uAoMap", unit);
    }

    /// <summary>
    /// 中間結果を画面全体に出す(Ctrl+F2)。**後処理の外**で呼ぶこと。
    ///
    /// <see cref="ShadowMap.DrawDebug"/> が隅に小さく出すのと違って、
    /// こちらは画面いっぱいに出す。AO の善し悪しは
    /// **1画素単位のノイズと、暗がりの広がり方**で決まるので、
    /// 縮めて見ても何も分からない。
    /// </summary>
    public void DrawDebug(int screenWidth, int screenHeight, float depthRange)
    {
        if (DebugView == SsaoDebugView.None || !Enabled)
        {
            return;
        }

        Framebuffer.BindDefault(_gl, screenWidth, screenHeight);

        Shader shader = _resources.GetShader(_viewShader);
        shader.Use();
        shader.SetInt("uMode", (int)DebugView);
        shader.SetFloat("uDepthRange", MathF.Max(depthRange, 0.001f));

        Result.Color.Bind(TextureUnit.Texture0);
        shader.SetInt("uAo", 0);

        _geometry.Color.Bind(TextureUnit.Texture1);
        shader.SetInt("uGeometry", 1);

        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);

        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        DrawFullscreen();

        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);
        SetCap(EnableCap.DepthTest, depth);
        SetCap(EnableCap.Blend, blend);
    }

    /// <summary>ウィンドウの大きさが変わったら、3枚とも作り直す。</summary>
    public void Resize(int width, int height)
    {
        _screenWidth = Math.Max(1, width);
        _screenHeight = Math.Max(1, height);

        _geometry.Resize(_screenWidth, _screenHeight);
        ResizeOcclusionBuffers();
    }

    /// <summary>解像度の切り替え(Ctrl+F6)。**バッファを作り直す**。</summary>
    public void SetHalfResolution(bool half)
    {
        if (half == HalfResolution)
        {
            return;
        }

        HalfResolution = half;
        ResizeOcclusionBuffers();
    }

    /// <summary>SSAO のシェーダを読み直す(F5)。**絵を見ながら式をいじる**ために要る。</summary>
    public void ReloadShaders()
    {
        _resources.GetShader(_geometryShader).TryReload();
        _resources.GetShader(_occlusionShader).TryReload();
        _resources.GetShader(_blurShader).TryReload();
        _resources.GetShader(_viewShader).TryReload();
    }

    /// <summary>
    /// **半球状の標本を作る**。今日の理屈がいちばん濃く出ているところ。
    ///
    /// <para>
    /// <b>なぜ半球か</b>。遮蔽を調べたいのは<b>面の表側</b>だけで、
    /// 面の裏へ潜った標本は必ず「遮られている」と答える。
    /// 球全体に散らすと、平らな面でも半分が遮蔽と数えられ、
    /// <b>どこもかしこも AO が 0.5 になる</b>——初期の SSAO(Crytek 版)は
    /// 実際に球で散らしていて、これが「全体が均一に薄暗い」原因だった。
    /// </para>
    ///
    /// <para>
    /// <b>なぜ中心寄りに密なのか</b>。遮蔽への寄与は近くの物のほうが大きいので、
    /// 標本も近くに多く置きたい。<c>scale²</c> で内側へ寄せるのが定番で、
    /// これは重点サンプリング(Day 36 の <c>ImportanceSampleGgx</c>)と同じ発想——
    /// <b>効く場所に標本を集める</b>。
    /// </para>
    ///
    /// <para>
    /// <b>接空間で持つ</b>ので、法線の向きに関係なく使い回せる。
    /// 使うときに TBN 行列で回す(<c>ssao.frag</c>)。
    /// 画素ごとに半球を作り直す必要が無いのがこの持ち方の値打ち。
    /// </para>
    /// </summary>
    private void BuildKernel(Random random)
    {
        for (int i = 0; i < MaxSamples; i++)
        {
            // xy は -1〜1、**z は 0〜1**。これで法線側の半球に収まる。
            var sample = new Vector3(
                (random.NextSingle() * 2.0f) - 1.0f,
                (random.NextSingle() * 2.0f) - 1.0f,
                random.NextSingle());

            // 長さを 1 にしてから 0〜1 の乱数を掛ける。
            // **表面だけでなく中身にも散らす**ためで、
            // 球面に貼り付けたままだと「半径ちょうどの殻」しか調べられない。
            sample = Vector3.Normalize(sample) * random.NextSingle();

            // 中心寄りに寄せる。0.1〜1.0 を二次曲線で。
            float scale = (float)i / MaxSamples;
            sample *= 0.1f + (0.9f * scale * scale);

            _kernel[i] = sample;
        }
    }

    /// <summary>
    /// **4x4 のランダムな向き**を焼く。z = 0 なのは、接平面の中で回すため。
    ///
    /// ここで作るのは「回転」であって「標本」ではない。
    /// <c>ssao.frag</c> がこのベクトルをグラム・シュミットで接平面に落とし、
    /// <b>画素ごとに違う向きの接空間</b>を作る。
    ///
    /// <para>
    /// <b>Nearest + Repeat</b>にするのが必須。
    ///   - Linear だと隣のテクセルと混ざって、乱数がなまる(=模様が戻る)
    ///   - ClampToEdge だと画面の大半が同じ値になり、そもそも敷き詰められない
    /// </para>
    /// </summary>
    private Texture CreateNoiseTexture(Random random)
    {
        var pixels = new float[NoiseSize * NoiseSize * 3];

        for (int i = 0; i < NoiseSize * NoiseSize; i++)
        {
            pixels[(i * 3) + 0] = (random.NextSingle() * 2.0f) - 1.0f;
            pixels[(i * 3) + 1] = (random.NextSingle() * 2.0f) - 1.0f;
            pixels[(i * 3) + 2] = 0.0f;
        }

        Texture noise = Texture.FromFloatPixels(_gl, pixels, NoiseSize, NoiseSize, components: 3);
        noise.SetFilter(TextureFilter.Nearest);
        noise.SetWrap(TextureWrap.Repeat);

        return noise;
    }

    /// <summary>
    /// 法線を<b>ビュー空間へ</b>運ぶ行列。左上 3x3 の逆転置(Day 32 の <c>NormalMatrix</c> と同じ)。
    ///
    /// 本描画は<b>モデル行列だけ</b>から作って世界空間の法線を出すが、
    /// SSAO の計算はまるごとビュー空間で行うので、ビュー行列も掛けたものから作る。
    /// **同じ式で、入れるものが1つ多い**だけ。
    /// </summary>
    private static Matrix4x4 NormalMatrix(Matrix4x4 modelView)
    {
        // 平行移動は法線に効かないので落とす(逆行列の計算を安定させるため)。
        modelView.M41 = 0.0f;
        modelView.M42 = 0.0f;
        modelView.M43 = 0.0f;

        return Matrix4x4.Invert(modelView, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse)
            : modelView;
    }

    private Framebuffer CreateOcclusionBuffer()
    {
        int divisor = HalfResolution ? 2 : 1;

        // 深度は持たない。**フルスクリーンの板を1枚描くだけ**なので手前も奥も無い
        // (PostProcess のぼかし用バッファと同じ判断)。
        return new Framebuffer(
            _gl,
            Math.Max(1, _screenWidth / divisor),
            Math.Max(1, _screenHeight / divisor),
            RenderTargetFormat.R8,
            depth: false);
    }

    private void ResizeOcclusionBuffers()
    {
        int divisor = HalfResolution ? 2 : 1;
        int width = Math.Max(1, _screenWidth / divisor);
        int height = Math.Max(1, _screenHeight / divisor);

        _occlusion.Resize(width, height);
        _blurred.Resize(width, height);
    }

    /// <summary>2パス目。半球にばらまいて数える。</summary>
    private void ComputeOcclusion(Camera camera)
    {
        _occlusion.Bind();

        Shader shader = _resources.GetShader(_occlusionShader);
        shader.Use();

        Matrix4x4 projection = camera.ProjectionMatrix;
        shader.SetMatrix4("uProjection", projection);

        // **射影行列の対角成分の逆数**。深度からビュー空間の位置を戻すのに使う
        // (ssao.frag の ViewPosition)。透視でも平行でも同じ2つの数で足りる、
        // というのが気持ちのよいところ。
        shader.SetVector2(
            "uProjScale",
            new Vector2(1.0f / projection.M11, 1.0f / projection.M22));

        shader.SetInt("uOrthographic", camera.Mode == ProjectionMode.Orthographic ? 1 : 0);

        shader.SetFloat("uRadius", Radius);
        shader.SetFloat("uBias", Bias);
        shader.SetFloat("uStrength", Strength);
        shader.SetFloat("uPower", Power);
        int samples = Math.Clamp(SampleCount, 1, MaxSamples);
        shader.SetInt("uSampleCount", samples);

        // **間引いて読ませる**。カーネルは短いものから順に並んでいるので、
        // 先頭から取ると手元しか探らない(ssao.frag の uSampleStride)。
        shader.SetInt("uSampleStride", Math.Max(1, MaxSamples / samples));

        // **ノイズを敷き詰める倍率**。AO バッファの大きさを 4 で割ったもの。
        // ここを画面の大きさで計算すると、半解像度のときにタイルが2倍に伸び、
        // 4x4 のぼかしでは消えなくなる(格子が薄く残る)。
        shader.SetVector2(
            "uNoiseScale",
            new Vector2((float)_occlusion.Width / NoiseSize, (float)_occlusion.Height / NoiseSize));

        // 64 本まとめて1回で送る(Shader.SetVector3Array)。
        shader.SetVector3Array("uKernel", _kernel);

        _geometry.Color.Bind(TextureUnit.Texture0);
        shader.SetInt("uGeometry", 0);

        _noise.Bind(TextureUnit.Texture1);
        shader.SetInt("uNoise", 1);

        DrawFullscreen();
    }

    /// <summary>3パス目。ノイズのタイルをならす。</summary>
    private void BlurOcclusion()
    {
        _blurred.Bind();

        Shader shader = _resources.GetShader(_blurShader);
        shader.Use();
        shader.SetInt("uRadius", NoiseSize);
        shader.SetVector2(
            "uTexelSize",
            new Vector2(1.0f / _occlusion.Width, 1.0f / _occlusion.Height));

        _occlusion.Color.Bind(TextureUnit.Texture0);
        shader.SetInt("uSource", 0);

        DrawFullscreen();
    }

    /// <summary>画面を覆う三角形を1枚描く(<c>fullscreen.vert</c>)。</summary>
    private void DrawFullscreen()
    {
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        PassCount++;
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

        _geometry.Dispose();
        _occlusion.Dispose();
        _blurred.Dispose();
        _noise.Dispose();

        if (_emptyVao != 0)
        {
            _gl.DeleteVertexArray(_emptyVao);
            _emptyVao = 0;
        }

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
