using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **イメージベースドライティング(IBL)の一式**(Day 36)。今日の主役。
///
/// Day 35 で PBR を入れたが、光源は平行光源1本だけだった。
/// その結果、金属はハイライト以外が真っ黒になった——
/// <b>金属は拡散反射をせず、映り込む景色が無かった</b>から。
///
/// IBL はそこを埋める。「まわりの景色」を1枚の環境マップとして持ち、
/// <b>あらゆる方向から来る光</b>としてライティングに入れる。
/// 費用対効果がとても大きく、HDRI を1枚差し替えるだけで
/// シーンの説得力が一段変わる。
///
/// <para>
/// <b>素直にやると重すぎる</b>。1画素ごとに半球ぶんの積分が要り、
/// それを毎フレーム全画素でやることはできない。
/// そこで<b>事前に焼いておく</b>。焼くものは3つ。
/// </para>
///
/// <list type="table">
/// <item>
/// <term>放射照度マップ(<see cref="Irradiance"/>)</term>
/// <description>
/// 拡散ぶん。法線だけで決まるので、小さなキューブマップ1枚に収まる(32x32)。
/// </description>
/// </item>
/// <item>
/// <term>事前フィルタ環境マップ(<see cref="Prefiltered"/>)</term>
/// <description>
/// 鏡面ぶんの「環境側」。粗さごとにぼかした環境を、ミップの段に1つずつ入れる。
/// </description>
/// </item>
/// <item>
/// <term>BRDF の表(<see cref="BrdfLut"/>)</term>
/// <description>
/// 鏡面ぶんの「材質側」。<c>N・V</c> と粗さの2次元表で、**環境にもモデルにも依存しない**。
/// つまり一度焼けばどのシーンでも使い回せる。
/// </description>
/// </item>
/// </list>
///
/// <para>
/// 2番目と3番目に分けられるのが<b>分割和近似</b>(Karis 2013)で、
/// これが無いと IBL の鏡面は実時間に乗らなかった。
/// 理屈は <see cref="Pbr.IntegrateBrdf"/> のコメントに書いてある。
/// </para>
///
/// <para>
/// <b>シーンを知らない</b>のは <see cref="PostProcess"/> / <see cref="ShadowMap"/> と同じ方針。
/// このクラスが持つのは環境の絵と、そこから焼いた3枚だけ。
/// 「何を映すか」は外から渡す太陽の向きだけで決まる。
/// </para>
/// </summary>
internal sealed class EnvironmentMap : IDisposable
{
    /// <summary>環境マップ本体の1辺。**空を見るだけなら 256 で足りる**(細部が無いので)。</summary>
    public const int EnvironmentSize = 256;

    /// <summary>放射照度マップの1辺。**32 で十分**——中身がとても滑らかだから。</summary>
    public const int IrradianceSize = 32;

    /// <summary>事前フィルタの1辺(ミップ 0)。</summary>
    public const int PrefilterSize = 128;

    /// <summary>事前フィルタの段数。段ごとに粗さ 0, 0.25, 0.5, 0.75, 1.0 を割り当てる。</summary>
    public const int PrefilterMipCount = 5;

    /// <summary>BRDF の表の1辺。**横が N・V、縦が粗さ**。</summary>
    public const int BrdfLutSize = 128;

    private readonly GL _gl;
    private readonly RenderResources _resources;

    /// <summary>焼き先を差し替えるためのフレームバッファ。**面とミップを直接挿す**。</summary>
    private uint _fbo;

    /// <summary>焼くときに描く立方体。中から見る。</summary>
    private readonly Mesh<Vertex> _cube;

    private readonly Handle<Shader> _equirectShader;
    private readonly Handle<Shader> _irradianceShader;
    private readonly Handle<Shader> _prefilterShader;
    private readonly Handle<Shader> _skyboxShader;

    /// <summary>
    /// 元になる正距円筒の HDR 画像。<see cref="SkyImage"/> が作るか、
    /// <see cref="HdrImage"/> が <c>.hdr</c> から読む(Day 39)。
    /// </summary>
    private Texture? _source;

    private bool _disposed;

    public EnvironmentMap(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        string cubeVert = Path.Combine(shaderDirectory, "cube.vert");
        _equirectShader = resources.LoadShader(cubeVert, Path.Combine(shaderDirectory, "equirect.frag"));
        _irradianceShader = resources.LoadShader(cubeVert, Path.Combine(shaderDirectory, "irradiance.frag"));
        _prefilterShader = resources.LoadShader(cubeVert, Path.Combine(shaderDirectory, "prefilter.frag"));

        _skyboxShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "skybox.vert"),
            Path.Combine(shaderDirectory, "skybox.frag"));

        // **焼くのも空を描くのも、同じ立方体1個で足りる**。
        // Primitives の立方体は面ごとに色と UV を持っているが、
        // cube.vert が使うのは位置(location 0)だけなので何も邪魔にならない。
        _cube = Primitives.CreateCube(gl);

        _fbo = gl.GenFramebuffer();

        // 面をまたいだ補間。**コンテキスト全体の設定**なので、ここで1回入れておく。
        CubeMap.EnableSeamless(gl);
    }

    /// <summary>環境そのもの。空を描くのに使う。ミップ付き(事前フィルタの入力になる)。</summary>
    public CubeMap? Environment { get; private set; }

    /// <summary>拡散ぶんの放射照度。</summary>
    public CubeMap? Irradiance { get; private set; }

    /// <summary>鏡面ぶん。ミップの段が粗さに対応する。</summary>
    public CubeMap? Prefiltered { get; private set; }

    /// <summary>BRDF の事前積分表。**環境が変わっても焼き直す必要が無い**。</summary>
    public Texture? BrdfLut { get; private set; }

    /// <summary>IBL を使うか(Ctrl+Alt+1)。OFF で Day 35 の「環境光は定数」に戻る。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>空を描くか(Ctrl+Alt+2)。</summary>
    public bool SkyboxVisible { get; set; } = true;

    /// <summary>環境光の強さ(Ctrl+Alt+3)。</summary>
    public float Intensity { get; set; } = 1.0f;

    /// <summary>空として出す事前フィルタの段(Ctrl+Alt+4)。-1 なら環境そのもの。</summary>
    public int SkyboxMip { get; set; } = -1;

    /// <summary>
    /// 鏡面で事前フィルタの段を使うか(Ctrl+Alt+8)。
    /// OFF にすると**粗さを無視して原寸を引く**——粗い金属が鏡になり、
    /// 太陽の反射が点として散ってちらつく。事前フィルタが何をしているかの裏返し。
    /// </summary>
    public bool UsePrefilter { get; set; } = true;

    /// <summary>空を 8bit(1.0 で頭打ち)にして焼くか(Ctrl+Alt+6)。**HDR が要る理由**。</summary>
    public bool ClampSkyToLdr { get; set; }

    /// <summary>
    /// 空を Y 軸まわりに回す角度(ラジアン。Day 39)。
    /// **HDRI の太陽をシーンの都合のよい方位へ持ってくる**ためのつまみ。
    /// 焼き込みパスでだけ効くので、ここを変えたら <see cref="BakeFromPixels"/> を呼び直すこと。
    /// 理屈は <c>shaders/equirect.frag</c> のコメント。
    /// </summary>
    public float SkyYaw { get; set; }

    /// <summary>焼くのにかかった時間(ミリ秒)。**代償を数字で見る**。</summary>
    public double BakeMilliseconds { get; private set; }

    /// <summary>CPU で空を作るのにかかった時間。</summary>
    public double SkyMilliseconds { get; private set; }

    /// <summary>BRDF の表を焼くのにかかった時間。</summary>
    public double LutMilliseconds { get; private set; }

    /// <summary>
    /// いま焼いてある空がどこから来たか(Day 39)。HUD と自己チェックの表示用。
    /// 手で焼いたものなら <c>"手焼き"</c>、HDRI なら**ファイル名**が入る。
    /// </summary>
    public string SourceLabel { get; private set; } = "(未焼き)";

    /// <summary>元画像の大きさ(Day 39)。手焼きは 1024x512、HDRI は 2048x1024。</summary>
    public int SourceWidth { get; private set; }

    public int SourceHeight { get; private set; }

    /// <summary>3枚が抱えている VRAM の推定バイト数。</summary>
    public long ByteSize =>
        (Environment?.ByteSize ?? 0)
        + (Irradiance?.ByteSize ?? 0)
        + (Prefiltered?.ByteSize ?? 0)
        + (BrdfLutSize * BrdfLutSize * 2 * 2);

    /// <summary>
    /// **全部焼き直す**。起動時に1回と、太陽の向きを変えたときに呼ぶ。
    ///
    /// 順番に意味がある。前の結果が次の入力になるので、入れ替えられない。
    /// <code>
    ///   空の画像(CPU) → 環境キューブ → ミップ生成 → 放射照度 → 事前フィルタ
    ///                                     ~~~~~~~~
    ///                          事前フィルタのちらつき対策がこれを要る
    /// </code>
    /// BRDF の表だけは独立していて、**環境が変わっても焼き直さなくてよい**
    /// (材質の性質しか入っていないため)。初回だけ焼く。
    /// </summary>
    public void Bake(Vector3 sunDirection, int screenWidth, int screenHeight)
    {
        // --- 1. 空を CPU で作る ---
        var skyWatch = Stopwatch.StartNew();
        float[] sky = SkyImage.Create(sunDirection);
        double skyMilliseconds = skyWatch.Elapsed.TotalMilliseconds;

        BakeFromPixels(sky, SkyImage.Width, SkyImage.Height, screenWidth, screenHeight, "手焼き");
        SkyMilliseconds = skyMilliseconds;
    }

    /// <summary>
    /// **すでにある正距円筒の HDR 画像から焼く**(Day 39)。
    ///
    /// <see cref="Bake"/> との違いは「空をどこから持ってくるか」だけで、
    /// 2 段目から先(キューブ → 放射照度 → 事前フィルタ)はまったく同じ道を通る。
    /// Day 36 で「Day 39 で本物の HDRI を差し込むときは**この層を差し替えるだけ**で済む」
    /// と書いた設計が、そのとおりに効いた形。
    ///
    /// <para>
    /// <b>ここで <see cref="ClampSkyToLdr"/> も効く</b>。HDRI に対して掛けると、
    /// 手焼きのときよりずっと落差が大きい——本物の太陽は 10 万を超えるので、
    /// 1.0 で切ると環境光の 8 割が消える。
    /// </para>
    /// </summary>
    /// <param name="pixels">RGB の float が横並び。長さ <c>width * height * 3</c>。</param>
    /// <param name="label">どこから来た空か。HUD に出す。</param>
    public void BakeFromPixels(
        float[] pixels, int width, int height, int screenWidth, int screenHeight, string label)
    {
        var total = Stopwatch.StartNew();

        SkyMilliseconds = 0.0;
        SourceLabel = label;
        SourceWidth = width;
        SourceHeight = height;

        float[] source = pixels;

        if (ClampSkyToLdr)
        {
            // **切るのはここ**。手焼きの側は SkyImage.Create が自分で切っていたが、
            // 読み込んだ画像に対しては呼び出し側でやる場所が無い。
            source = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                source[i] = MathF.Min(pixels[i], 1.0f);
            }
        }

        _source?.Dispose();
        _source = Texture.FromFloatPixels(_gl, source, width, height, components: 3);

        // **横は繰り返す**。方位角は一周してつながっているので、
        // ClampToEdge のままだと u = 0 と u = 1 の継ぎ目に細い線が出る。
        _source.SetWrap(TextureWrap.Repeat);

        // --- 2. 正距円筒 → キューブ ---
        Environment?.Dispose();
        Environment = CubeMap.Create(_gl, EnvironmentSize, CubeMap.FullMipCount(EnvironmentSize));

        Shader equirect = _resources.GetShader(_equirectShader);
        equirect.Use();
        _source.Bind(TextureUnit.Texture0);
        equirect.SetInt("uEquirect", 0);
        equirect.SetFloat("uSkyYaw", SkyYaw);

        RenderToCube(Environment, equirect, mip: 0);

        // **ミップを作る**。事前フィルタが「粗いほど小さいミップから引く」ため
        // (prefilter.frag のちらつき対策)。
        Environment.GenerateMipmaps();

        // --- 3. 放射照度(拡散ぶん)---
        Irradiance?.Dispose();
        Irradiance = CubeMap.Create(_gl, IrradianceSize);

        Shader irradiance = _resources.GetShader(_irradianceShader);
        irradiance.Use();
        Environment.Bind(TextureUnit.Texture0);
        irradiance.SetInt("uEnvironment", 0);

        RenderToCube(Irradiance, irradiance, mip: 0);

        // --- 4. 事前フィルタ(鏡面ぶんの環境側)---
        Prefiltered?.Dispose();
        Prefiltered = CubeMap.Create(_gl, PrefilterSize, PrefilterMipCount);

        Shader prefilter = _resources.GetShader(_prefilterShader);
        prefilter.Use();
        Environment.Bind(TextureUnit.Texture0);
        prefilter.SetInt("uEnvironment", 0);
        prefilter.SetFloat("uSourceSize", EnvironmentSize);

        for (int mip = 0; mip < PrefilterMipCount; mip++)
        {
            // 段を粗さ 0〜1 に等間隔で割り当てる。
            // **本番のシェーダはこの対応を逆に使う**(粗さから段を求める)ので、
            // 両側で同じ式でなければならない(textured.frag の uPrefilterMaxLod)。
            float roughness = (float)mip / (PrefilterMipCount - 1);
            prefilter.SetFloat("uRoughness", roughness);

            RenderToCube(Prefiltered, prefilter, mip);
        }

        // --- 5. BRDF の表(初回だけ)---
        if (BrdfLut is null)
        {
            var lutWatch = Stopwatch.StartNew();
            BrdfLut = BakeBrdfLut();
            LutMilliseconds = lutWatch.Elapsed.TotalMilliseconds;
        }

        Framebuffer.BindDefault(_gl, screenWidth, screenHeight);

        BakeMilliseconds = total.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// 本描画のシェーダに IBL を渡す。**フレームに1回**でよい。
    ///
    /// ユニットの割り当ては two-way の約束(<see cref="Material.Apply"/> の表の続き)。
    /// <code>
    ///   0〜4 マテリアル / 5 シャドウマップ / 6 高さ
    ///   7 放射照度 / 8 事前フィルタ / 9 BRDF の表   ← 今日
    /// </code>
    /// </summary>
    public void Apply(Shader shader)
    {
        bool ready = Enabled && Irradiance is not null && Prefiltered is not null && BrdfLut is not null;

        shader.SetInt("uIblEnabled", ready ? 1 : 0);
        shader.SetFloat("uIblIntensity", Intensity);
        shader.SetInt("uIblPrefilter", UsePrefilter ? 1 : 0);

        // **いちばん粗い段の番号**。シェーダは roughness にこれを掛けて段を選ぶ。
        shader.SetFloat("uPrefilterMaxLod", PrefilterMipCount - 1);

        // **使わないときも必ず刺す**。サンプラが指すユニットが空だと、
        // 環境によっては未定義の値が返る(ShadowMap.Apply と同じ理由)。
        // キューブマップのサンプラに 2D テクスチャを刺すのは型違いなので、
        // 環境が未完成のときは放射照度に環境そのものを流用しておく。
        (Irradiance ?? Environment)?.Bind(TextureUnit.Texture7);
        shader.SetInt("uIrradianceMap", 7);

        (Prefiltered ?? Environment)?.Bind(TextureUnit.Texture8);
        shader.SetInt("uPrefilterMap", 8);

        (BrdfLut ?? _resources.Placeholder).Bind(TextureUnit.Texture9);
        shader.SetInt("uBrdfLut", 9);
    }

    /// <summary>
    /// **空を描く**。立方体を内側から見ているだけ。
    ///
    /// 呼ぶのは他の 3D を描くより前。深度を書かないので、あとから描いたものが必ず手前に来る。
    /// 順番を逆(いちばん最後)にすると、深度テストで隠れた画素を省けるぶん速いが、
    /// <b>描画の分岐がいくつもある</b>(モデル / デモ / 材質グリッド / 材質テスト)ので、
    /// 全部の末尾に足すより前に1回置くほうが確実だと判断した。
    /// フルスクリーン1枚ぶんの塗りつぶしなので、実測でも差が出ない。
    ///
    /// <para>
    /// <b>カリングを切っている</b>のは、立方体の内側に居るから。
    /// 中心から見れば1つの方向が当たる面はちょうど1つなので、
    /// 表裏どちらを残しても結果は同じになる——**深度も要らない**。
    /// </para>
    ///
    /// <para>
    /// <b><paramref name="environmentMip"/> は Day 66a で足した</b>。プローブを撮るときは 32x32 の面に描くので、
    /// 256x256 の空を1画素おきどころか 8 画素おきに読むことになる。太陽を抜いた後にも残る明るい点
    /// (輝度 3,000 ほど)に当たるか外れるかで、空全体の明るさが倍以上ぶれる。
    /// <b>撮る画素と同じ大きさまで縮めた段</b>(256 / 32 = 8 なので 3 段目)を引けば、8x8 の平均を読むことになり、
    /// 明るい点は面積に見合った重さで入る。画面に描くときは 0 段目(既定)のまま。
    /// </para>
    /// </summary>
    public void DrawSkybox(Camera camera, bool restoreDepthTest, bool restoreCulling, float environmentMip = 0.0f)
    {
        if (!SkyboxVisible || Environment is null || Prefiltered is null)
        {
            return;
        }

        // **平行移動を落とす**。空は無限遠にあるので、カメラが動いても見え方が変わらない。
        // 落とさないと、歩いただけで空の模様が流れる(近くの箱の中に居るように見える)。
        Matrix4x4 view = camera.ViewMatrix;
        view.M41 = 0.0f;
        view.M42 = 0.0f;
        view.M43 = 0.0f;

        Shader shader = _resources.GetShader(_skyboxShader);
        shader.Use();
        shader.SetMatrix4("uViewProjection", view * camera.ProjectionMatrix);
        shader.SetFloat("uIntensity", Intensity);

        // 事前フィルタの段を見たいとき(Ctrl+Alt+4)は、そちらを引く。
        // **ぼけ具合が粗さに対応していること**を目で確かめるための窓。
        if (SkyboxMip >= 0)
        {
            Prefiltered.Bind(TextureUnit.Texture0);
            shader.SetFloat("uMip", Math.Min(SkyboxMip, PrefilterMipCount - 1));
        }
        else
        {
            Environment.Bind(TextureUnit.Texture0);
            shader.SetFloat("uMip", environmentMip);
        }

        shader.SetInt("uEnvironment", 0);

        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _cube.Draw();

        // **借りた状態は返す**(ShadowMap.End と同じ作法)。
        // 呼び出し側が今どの設定でいるかは知らないので、引数で受け取っている。
        _gl.DepthMask(true);
        SetCap(EnableCap.DepthTest, restoreDepthTest);
        SetCap(EnableCap.CullFace, restoreCulling);
    }

    /// <summary>
    /// **キューブマップの6面に描く**。焼くパスの共通部分。
    ///
    /// フレームバッファのカラーアタッチメントに<b>面とミップを直接挿す</b>のがここの肝。
    /// <see cref="Framebuffer"/> を使わないのは、あちらが
    /// 「自分でテクスチャを作って持つ」形になっているため——
    /// 外から用意したキューブマップの1面を挿す口が無い。
    ///
    /// <para>
    /// <b>これは設計の歪み</b>で、本来は <c>Framebuffer</c> に
    /// 「外のテクスチャを挿す」モードを足すのが筋。
    /// 今日は生の FBO を1つ持つほうが差分が小さく済むのでこうしたが、
    /// Day 52(ディファードレンダリング)で複数のカラーアタッチメントが要るときに、
    /// まとめて整理するのがよさそう。
    /// </para>
    /// </summary>
    private void RenderToCube(CubeMap target, Shader shader, int mip)
    {
        int side = Math.Max(1, target.Size >> mip);

        Matrix4x4 projection = CubeMap.FaceProjection();
        Matrix4x4[] views = CubeMap.FaceViews(Vector3.Zero);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)side, (uint)side);

        // 焼く間は深度もカリングも要らない(DrawSkybox のコメントと同じ理由)。
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);

        for (int face = 0; face < 6; face++)
        {
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.TextureCubeMapPositiveX + face,
                target.Handle,
                mip);

            // **毎回確かめる**。面やミップの指定を間違えると不完全になるが、
            // 不完全なフレームバッファに描いても**エラーは出ずに何も起きない**
            // (Framebuffer.Create のコメント)。焼くのは起動時の数十回だけなので、
            // 確認の代償はゼロに等しい。
            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                throw new InvalidOperationException(
                    $"キューブマップの焼き先が不完全です: {status}(面 {face} / ミップ {mip} / {side}x{side})");
            }

            shader.SetMatrix4("uViewProjection", views[face] * projection);

            _gl.Clear((uint)ClearBufferMask.ColorBufferBit);
            _cube.Draw();
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// **BRDF の表を CPU で焼く**。<see cref="Pbr.IntegrateBrdf"/> を格子状に呼ぶだけ。
    ///
    /// GPU でやるのが普通だが、ここは CPU にした。理由は2つ。
    ///   - Day 35 で書いた重点サンプリングが**そのまま使える**(シェーダを1本増やさずに済む)
    ///   - **自己チェックで表の中身を直接確かめられる**(GPU だと読み返しが要る)
    ///
    /// 代償は時間。128x128 の各点で 1024 サンプルなので 1600 万回の評価になる。
    /// <see cref="Parallel.For"/> に載せて実測 100ms 前後——起動時に1回なら払える。
    ///
    /// <para>
    /// 表の並びは <b>横が N・V(0〜1)、縦が粗さ(0〜1)</b>。
    /// 本番のシェーダは <c>texture(uBrdfLut, vec2(NdotV, roughness)).rg</c> で引く。
    /// **横縦を取り違えても絵は出る**(それらしく光る)ので、
    /// 自己チェックで「粗さ 0・正面」の値が (1, 0) に近いことを見ている。
    /// </para>
    /// </summary>
    private Texture BakeBrdfLut()
    {
        var data = new float[BrdfLutSize * BrdfLutSize * 2];

        Parallel.For(0, BrdfLutSize, y =>
        {
            // **テクセルの中心**で評価する。端(0 と 1)で取ると、
            // 線形補間したときに表全体が半テクセルずれる。
            float roughness = (y + 0.5f) / BrdfLutSize;

            for (int x = 0; x < BrdfLutSize; x++)
            {
                float nDotV = (x + 0.5f) / BrdfLutSize;

                Vector2 value = Pbr.IntegrateBrdf(nDotV, roughness);

                int index = ((y * BrdfLutSize) + x) * 2;
                data[index + 0] = value.X;
                data[index + 1] = value.Y;
            }
        });

        return Texture.FromFloatPixels(_gl, data, BrdfLutSize, BrdfLutSize, components: 2);
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

        _source?.Dispose();
        Environment?.Dispose();
        Irradiance?.Dispose();
        Prefiltered?.Dispose();
        BrdfLut?.Dispose();

        _cube.Dispose();

        _gl.DeleteFramebuffer(_fbo);
        _fbo = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
