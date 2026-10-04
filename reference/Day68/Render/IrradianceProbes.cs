using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **焼いたイラディアンスプローブの格子**(Day 66a)。今日の主役。
///
/// <para>
/// Day 36 の IBL は「その点は空だけを見ている」と仮定していた。放射照度マップは世界に1枚しか無く、
/// どこに立っていても同じ空を見る——壁の陰でも、灯りに照らされた路面の真上でも。
/// 夜の裏通りでは空がほとんど暗いので、この仮定のもとでは<b>灯りが照らした路面や壁の照り返しが
/// どこにも回り込まない</b>(灯りの当たらない面は、月明かりと 7% の空だけになる)。
/// </para>
///
/// <para>
/// プローブは「空だけ」の仮定を外す。空間に点を格子状に並べ、<b>各点でシーンそのものを6方向に撮り</b>、
/// そこに届いている光を球面調和の9係数に縮めて持つ。描くときは、画素の位置を囲む8個の係数を
/// 混ぜて、その向きの放射照度を引く。
/// </para>
///
/// <list type="table">
/// <item><term>焼く(起動後に1回・キーで)</term><description>
/// 点ごとに6面を撮る → 読み返す → SH へ射影 → 余弦のこぶを掛ける → テクスチャへ
/// </description></item>
/// <item><term>引く(毎フレーム・画素ごと)</term><description>
/// 位置 → 囲む8点と重み → 各点の9係数を法線で評価 → 重みで混ぜる
/// </description></item>
/// </list>
///
/// <para>
/// <b>シーンを知らない</b>のは <see cref="EnvironmentMap"/> と同じ方針。何を描くかは
/// <see cref="Bake"/> に渡される関数が決め、このクラスが決めるのは「どこから・どの向きへ撮るか」だけ。
/// だから灯りの点け消しも、何回跳ね返らせるかも、呼ぶ側(Program)の都合で決められる。
/// </para>
/// </summary>
internal sealed class IrradianceProbes : IDisposable
{
    /// <summary>
    /// 1面の1辺。**32 で足りる**——9係数に縮めるので、細部は最初から捨てる前提。
    ///
    /// <para>
    /// 16 まで落とすと、街灯のガラスのような小さく明るいものが1〜2画素になり、
    /// どの画素に乗るかで係数が揺れる。64 にすると焼く時間の半分以上を占める読み返しが4倍になる。
    /// </para>
    /// </summary>
    public const int CaptureSize = 32;

    /// <summary>
    /// 1回に撮るプローブの数。<b>撮るたびに読み返すと遅い</b>ので、1枚の大きな画像に並べて撮り、
    /// まとめて1回で読む。横に6面、縦に 16 点で 192x512。
    /// </summary>
    public const int ProbesPerBatch = 16;

    /// <summary>
    /// 係数のテクスチャを刺すユニット。**14 番**。
    /// <code>
    ///   0〜4 マテリアル / 5 影 / 6 高さ / 7〜9 IBL / 10 SSAO / 11〜13 Forward+ の升目
    ///   14 プローブの係数   ← 今日
    /// </code>
    /// </summary>
    public const int ProbeUnit = 14;

    /// <summary>
    /// 6面を撮る向き。<b><see cref="CubeMap.FaceViews"/> と同じ表</b>(面の順も上向きも)。
    ///
    /// <para>
    /// キューブの面の上向きが「下(-Y)」なのは GL のキューブマップの約束(Day 36 の要点)。
    /// ここで同じ向きに撮っておけば、読み返した画素を <see cref="SphericalHarmonics.CubeTexelDirection"/>
    /// でそのまま向きに直せる。<b>表を1行でも取り違えると、その面の光が別の向きから来たことになる</b>
    /// ——自己チェックが「空だけを撮ったプローブ」と Day 36 の空の係数を突き合わせる。
    /// </para>
    /// </summary>
    private static readonly (Vector3 Forward, Vector3 Up)[] Faces =
    [
        (Vector3.UnitX, -Vector3.UnitY),
        (-Vector3.UnitX, -Vector3.UnitY),
        (Vector3.UnitY, Vector3.UnitZ),
        (-Vector3.UnitY, -Vector3.UnitZ),
        (Vector3.UnitZ, -Vector3.UnitY),
        (-Vector3.UnitZ, -Vector3.UnitY),
    ];

    private readonly GL _gl;
    private readonly RenderResources _resources;

    /// <summary>撮る先。横 6 面 x 縦 <see cref="ProbesPerBatch"/> 点。RGBA16F と深度。</summary>
    private readonly Framebuffer _capture;

    /// <summary>
    /// 撮るためのカメラ。**画角 90 度・縦横 1:1** なら、6枚で球をちょうど隙間なく覆う。
    ///
    /// <para>
    /// <b>手前の面を近くに置く</b>(0.05m)。プローブが壁から 50cm の所にあるとき、
    /// 本描画と同じ 0.1m のままでも写るが、小物の間にあるプローブは物に近いことがある。
    /// 近すぎる面が切られると、<b>その向きに穴が開いて空(か、壁の向こう)が見える</b>。
    /// </para>
    /// </summary>
    private readonly Camera _camera = new()
    {
        FieldOfView = MathF.PI / 2.0f,
        AspectRatio = 1.0f,
        NearPlane = 0.05f,
        FarPlane = 80.0f,
    };

    private readonly Handle<Shader> _viewShader;

    /// <summary>プローブを見るための球。本描画の球(三角形 3,072 枚)より粗くてよい。</summary>
    private readonly Mesh<Vertex> _sphere;

    /// <summary>係数のテクスチャ。<b>横 9 列が係数、縦 1 行が1プローブ</b>。RGBA16F。</summary>
    private Texture? _texture;

    /// <summary>係数の CPU 側の写し(プローブ数 x 9)。放射照度 / π。自己チェックと HUD が読む。</summary>
    private Vector3[] _coefficients = [];

    private bool _disposed;

    public IrradianceProbes(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        _capture = new Framebuffer(
            gl, CaptureSize * 6, CaptureSize * ProbesPerBatch, RenderTargetFormat.Rgba16F, depth: true);

        _viewShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "probe.vert"),
            Path.Combine(shaderDirectory, "probe.frag"));

        _sphere = Primitives.CreateSphere(gl, slices: 16, stacks: 12);
    }

    /// <summary>格子のいちばん小さい角(プローブ (0,0,0) の位置)。</summary>
    public Vector3 GridMin { get; private set; }

    /// <summary>隣のプローブまでの距離。3軸とも同じ値にしてある(<see cref="Configure"/>)。</summary>
    public float Spacing { get; private set; } = 1.0f;

    public int CountX { get; private set; } = 2;

    public int CountY { get; private set; } = 2;

    public int CountZ { get; private set; } = 2;

    public int ProbeCount => CountX * CountY * CountZ;

    /// <summary>焼いてあるか。<b>焼く前は引かない</b>(<see cref="Apply"/> が 0 を送る)。</summary>
    public bool Baked => _texture is not null;

    /// <summary>いまの係数が何回ぶんの跳ね返りを含んでいるか(<see cref="Bake"/> を呼んだ回数)。</summary>
    public int Bounces { get; private set; }

    /// <summary>直前の <see cref="Bake"/> で、撮るのに掛かった時間(CPU の時計。描画命令を積んで待つまで)。</summary>
    public double CaptureMilliseconds { get; private set; }

    /// <summary>直前の <see cref="Bake"/> で、読み返しに掛かった時間(GPU が撮り終えるのを待つぶんを含む)。</summary>
    public double ReadbackMilliseconds { get; private set; }

    /// <summary>直前の <see cref="Bake"/> で、SH へ射影するのに掛かった時間。</summary>
    public double ProjectMilliseconds { get; private set; }

    /// <summary>焼いた回数ぶんの合計。跳ね返り3回なら3回ぶん。</summary>
    public double TotalBakeMilliseconds { get; private set; }

    /// <summary>直前の <see cref="Bake"/> で描いた面の数(プローブ数 x 6)。</summary>
    public int FacesRendered { get; private set; }

    /// <summary>係数のテクスチャの大きさ。**プローブ1個 72 バイト**(9 係数 x RGBA16F の 8 バイト。A は使っていない)。</summary>
    public long ByteSize => (long)ProbeCount * SphericalHarmonics.CoefficientCount * 4 * 2;

    /// <summary>
    /// **格子を決める**。<paramref name="min"/> から <paramref name="spacing"/> おきに、
    /// <paramref name="max"/> を覆うまで並べる。焼いてあったものは捨てる(並びが変わるので使えない)。
    ///
    /// <para>
    /// <b>間隔は3軸で同じにする</b>。軸ごとに変えると、同じ距離でも向きによって補間の効き方が変わり、
    /// 光漏れの幅が向きで違ってくる。<b>max は覆うまで延ばす</b>(割り切れないときは少しはみ出す)——
    /// 縮めて合わせると、端のプローブの外側を「端の値で塗る」範囲が広がる。
    /// </para>
    /// </summary>
    public void Configure(Vector3 min, Vector3 max, float spacing)
    {
        GridMin = min;
        Spacing = spacing;

        // **どの軸も 2 個以上**。補間は「囲む2点」を読むので、1個しか無い軸では相手が居ない。
        CountX = Math.Max(2, (int)MathF.Ceiling(((max.X - min.X) / spacing) - 1e-3f) + 1);
        CountY = Math.Max(2, (int)MathF.Ceiling(((max.Y - min.Y) / spacing) - 1e-3f) + 1);
        CountZ = Math.Max(2, (int)MathF.Ceiling(((max.Z - min.Z) / spacing) - 1e-3f) + 1);

        Clear();
    }

    /// <summary>焼いたものを捨てる。<see cref="Apply"/> は 0 を送るようになり、描き方は IBL に戻る。</summary>
    public void Clear()
    {
        _texture?.Dispose();
        _texture = null;
        _coefficients = [];
        Bounces = 0;
        TotalBakeMilliseconds = 0.0;
    }

    /// <summary>
    /// プローブの通し番号。<b>x がいちばん速く回る</b>(x → y → z)。シェーダの <c>ProbeIrradiance</c> と同じ並び。
    /// </summary>
    public int Index(int x, int y, int z) => x + (CountX * (y + (CountY * z)));

    public Vector3 ProbePosition(int x, int y, int z) => GridMin + (new Vector3(x, y, z) * Spacing);

    /// <summary>通し番号からプローブの位置。</summary>
    public Vector3 ProbePosition(int index)
    {
        int x = index % CountX;
        int y = (index / CountX) % CountY;
        int z = index / (CountX * CountY);
        return ProbePosition(x, y, z);
    }

    /// <summary>1プローブぶんの係数(放射照度 / π)。焼く前は空。</summary>
    public ReadOnlySpan<Vector3> Coefficients(int index) =>
        _coefficients.Length == 0
            ? ReadOnlySpan<Vector3>.Empty
            : _coefficients.AsSpan(index * SphericalHarmonics.CoefficientCount, SphericalHarmonics.CoefficientCount);

    /// <summary>
    /// **1回ぶん焼く**。全部のプローブで6面を撮り、SH に縮めて、テクスチャを差し替える。
    ///
    /// <para>
    /// <b>跳ね返りを重ねるには、これを繰り返し呼ぶ</b>。撮っている間はテクスチャを差し替えないので、
    /// 2回目に撮るシーンは「1回目の係数で照らされた」絵になる——その照り返しを撮れば2回跳ねた光になる。
    /// 差し替えるのは全部撮り終えてから。<b>途中で差し替えると、先に焼いたプローブの光を
    /// 後のプローブが拾い、並べた順で明るさが変わる</b>(格子の片側だけ1回多く跳ねる)。
    /// </para>
    /// </summary>
    /// <param name="drawScene">
    /// 渡されたカメラでシーンを描く関数。<b>今バインドされているフレームバッファとビューポートに描くこと</b>
    /// (こちらが1面ずつビューポートを切り替えて呼ぶ)。空も描くのは呼ぶ側の仕事。
    /// </param>
    public unsafe void Bake(Action<Camera> drawScene)
    {
        int probeCount = ProbeCount;
        var next = new Vector3[probeCount * SphericalHarmonics.CoefficientCount];

        int width = _capture.Width;
        int height = _capture.Height;
        var pixels = new float[width * height * 4];

        var captureWatch = new Stopwatch();
        var readbackWatch = new Stopwatch();
        var projectWatch = new Stopwatch();

        for (int first = 0; first < probeCount; first += ProbesPerBatch)
        {
            int batch = Math.Min(ProbesPerBatch, probeCount - first);

            // --- 撮る ---
            captureWatch.Start();

            _capture.Bind();
            _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            for (int row = 0; row < batch; row++)
            {
                Vector3 position = ProbePosition(first + row);

                for (int face = 0; face < 6; face++)
                {
                    // **1面ぶんの枠だけに描く**。ビューポートの外は切られるので、隣の面にはみ出さない
                    // (空は深度テストなしで立方体を描くが、それも枠の中で止まる)。
                    _gl.Viewport(face * CaptureSize, row * CaptureSize, (uint)CaptureSize, (uint)CaptureSize);

                    _camera.Position = position;
                    _camera.Target = position + Faces[face].Forward;
                    _camera.Up = Faces[face].Up;

                    drawScene(_camera);
                }
            }

            captureWatch.Stop();

            // --- 読み返す ---
            //
            // **ここで GPU を待つ**。積んだ 96 面ぶんの描画が終わるまで glReadPixels は返らない。
            // 1面ずつ読むと待ちが 6 倍(1プローブごとに6回)になり、焼く時間の大半が待ちになる。
            readbackWatch.Start();

            _capture.Bind();
            fixed (float* data = pixels)
            {
                _gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.Float, data);
            }

            readbackWatch.Stop();

            // --- 縮める ---
            projectWatch.Start();

            Parallel.For(0, batch, row =>
            {
                Span<Vector3> coefficients = next.AsSpan(
                    (first + row) * SphericalHarmonics.CoefficientCount, SphericalHarmonics.CoefficientCount);

                var tile = new float[CaptureSize * CaptureSize * 4];

                for (int face = 0; face < 6; face++)
                {
                    CopyTile(pixels, width, face * CaptureSize, row * CaptureSize, tile);
                    SphericalHarmonics.ProjectFace(tile, 4, CaptureSize, face, coefficients);
                }

                // **放射輝度 → 放射照度 / π**。次数ごとに 1, 2/3, 1/4 を掛けるだけ。
                SphericalHarmonics.ToIrradiance(coefficients);
            });

            projectWatch.Stop();
        }

        // ビューポートは戻さない。次に描く側が自分の枠を Bind で決め直す(Framebuffer.Bind のコメント)。
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // **全部撮り終えてから差し替える**(上のコメント)。
        Upload(next);
        _coefficients = next;
        Bounces++;

        FacesRendered = probeCount * 6;
        CaptureMilliseconds = captureWatch.Elapsed.TotalMilliseconds;
        ReadbackMilliseconds = readbackWatch.Elapsed.TotalMilliseconds;
        ProjectMilliseconds = projectWatch.Elapsed.TotalMilliseconds;
        TotalBakeMilliseconds += CaptureMilliseconds + ReadbackMilliseconds + ProjectMilliseconds;
    }

    /// <summary>
    /// **1点だけ撮って係数を返す**(自己チェック用)。格子もテクスチャも触らない。
    /// </summary>
    public unsafe Vector3[] CaptureOne(Vector3 position, Action<Camera> drawScene)
    {
        var coefficients = new Vector3[SphericalHarmonics.CoefficientCount];
        var tile = new float[CaptureSize * CaptureSize * 4];

        _capture.Bind();
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        for (int face = 0; face < 6; face++)
        {
            _gl.Viewport(face * CaptureSize, 0, (uint)CaptureSize, (uint)CaptureSize);

            _camera.Position = position;
            _camera.Target = position + Faces[face].Forward;
            _camera.Up = Faces[face].Up;

            drawScene(_camera);
        }

        _capture.Bind();

        var pixels = new float[_capture.Width * CaptureSize * 4];
        fixed (float* data = pixels)
        {
            _gl.ReadPixels(0, 0, (uint)_capture.Width, (uint)CaptureSize, PixelFormat.Rgba, PixelType.Float, data);
        }

        for (int face = 0; face < 6; face++)
        {
            CopyTile(pixels, _capture.Width, face * CaptureSize, 0, tile);
            SphericalHarmonics.ProjectFace(tile, 4, CaptureSize, face, coefficients);
        }

        SphericalHarmonics.ToIrradiance(coefficients);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        return coefficients;
    }

    /// <summary>大きな画像から1面ぶん(<see cref="CaptureSize"/> 四方)を切り出す。</summary>
    private static void CopyTile(float[] pixels, int width, int left, int bottom, float[] tile)
    {
        int rowFloats = CaptureSize * 4;

        for (int y = 0; y < CaptureSize; y++)
        {
            int source = ((((bottom + y) * width) + left) * 4);
            Array.Copy(pixels, source, tile, y * rowFloats, rowFloats);
        }
    }

    /// <summary>
    /// 係数をテクスチャへ。<b>横 9 列、縦がプローブの数</b>。
    ///
    /// <para>
    /// 3D テクスチャにして GPU の三線形補間に任せる手もある。そうしないのは2つの理由から。
    /// 1つは<b>サンプラの数</b>——9 係数 x RGB = 27 個の数を RGBA の3D テクスチャに入れると7枚要り、
    /// textured.frag はすでに 14 枚使っている。もう1つは<b>重みを自分で持ちたい</b>から。
    /// 8点の重みがシェーダの中で見えていれば、「この点は壁の向こうだから重みを 0 にする」を
    /// 後から足せる(Day 66b で読む DDGI がやっているのがまさにそれ)。
    /// </para>
    /// </summary>
    private void Upload(Vector3[] coefficients)
    {
        var data = new float[coefficients.Length * 4];

        for (int i = 0; i < coefficients.Length; i++)
        {
            data[(i * 4) + 0] = coefficients[i].X;
            data[(i * 4) + 1] = coefficients[i].Y;
            data[(i * 4) + 2] = coefficients[i].Z;
            data[(i * 4) + 3] = 1.0f;
        }

        _texture?.Dispose();

        // **補間させない**。シェーダは texelFetch で1テクセルずつ読むので、フィルタは効かないが、
        // Nearest にしておけば texture() で読み間違えたときも隣の係数と混ざらない。
        _texture = Texture.FromFloatPixels(
            _gl, data, SphericalHarmonics.CoefficientCount, ProbeCount, components: 4);
        _texture.SetFilter(TextureFilter.Nearest);
    }

    /// <summary>
    /// 本描画のシェーダにプローブを渡す。**フレームに1回**(<see cref="EnvironmentMap.Apply"/> と同じ置き場所)。
    ///
    /// <para>
    /// <b>使わないときも必ず刺す</b>。サンプラが空のユニットを指すと、環境によっては未定義の値が返る。
    /// 焼く前はプレースホルダ(2D テクスチャ)を刺しておく——型さえ合っていれば中身は読まれない。
    /// </para>
    /// </summary>
    public void Apply(Shader shader, bool enabled)
    {
        shader.SetInt("uProbeEnabled", enabled && Baked ? 1 : 0);
        shader.SetVector3("uProbeGridMin", GridMin);
        shader.SetFloat("uProbeSpacing", Spacing);
        shader.SetInt3("uProbeCount", CountX, CountY, CountZ);

        (_texture ?? _resources.Placeholder).Bind(TextureUnit.Texture0 + ProbeUnit);
        shader.SetInt("uProbeSh", ProbeUnit);
    }

    /// <summary>
    /// **シェーダの <c>ProbeIrradiance</c> の C# の鏡**。位置と法線から、放射照度 / π を返す。
    ///
    /// <para>
    /// 手順はシェーダと1行ずつ同じ。<b>格子の外は端のプローブで塗る</b>(位置を格子の中へ寄せる)。
    /// 8点の重みは3軸の一次補間の積で、足すと必ず 1 になる(自己チェック)。
    /// <b>各点で評価してから混ぜても、係数を混ぜてから評価しても同じ</b>——どちらも一次式だから。
    /// </para>
    /// </summary>
    public Vector3 Irradiance(Vector3 position, Vector3 normal)
    {
        if (!Baked)
        {
            return Vector3.Zero;
        }

        Vector3 sum = Vector3.Zero;

        foreach ((int index, float weight) in Corners(position))
        {
            sum += SphericalHarmonics.Evaluate(Coefficients(index), normal) * weight;
        }

        return Vector3.Max(sum, Vector3.Zero);
    }

    /// <summary>
    /// 位置を囲む8点と、それぞれの重み(三線形補間)。**シェーダと同じ決め方**。
    /// </summary>
    public (int Index, float Weight)[] Corners(Vector3 position)
    {
        Vector3 cell = (position - GridMin) / Spacing;
        cell = Vector3.Clamp(cell, Vector3.Zero, new Vector3(CountX - 1, CountY - 1, CountZ - 1));

        // **いちばん上の点に乗ったときは、1つ手前の升に入れる**(重み 1 が上の点に付く)。
        // そうしないと、囲む2点の片方が格子の外になる。
        int baseX = Math.Min((int)cell.X, CountX - 2);
        int baseY = Math.Min((int)cell.Y, CountY - 2);
        int baseZ = Math.Min((int)cell.Z, CountZ - 2);

        var t = new Vector3(cell.X - baseX, cell.Y - baseY, cell.Z - baseZ);
        var corners = new (int, float)[8];

        for (int i = 0; i < 8; i++)
        {
            int cx = i & 1;
            int cy = (i >> 1) & 1;
            int cz = (i >> 2) & 1;

            float wx = cx == 1 ? t.X : 1.0f - t.X;
            float wy = cy == 1 ? t.Y : 1.0f - t.Y;
            float wz = cz == 1 ? t.Z : 1.0f - t.Z;

            corners[i] = (Index(baseX + cx, baseY + cy, baseZ + cz), wx * wy * wz);
        }

        return corners;
    }

    /// <summary>
    /// **テクスチャの中身を読み返す**(自己チェック用)。CPU の写しと突き合わせ、
    /// 並びと RGBA16F への丸めを確かめる。
    /// </summary>
    public unsafe float[] ReadTexture()
    {
        if (_texture is null)
        {
            return [];
        }

        var data = new float[_texture.Width * _texture.Height * 4];
        _texture.Bind();

        fixed (float* pointer = data)
        {
            _gl.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, pointer);
        }

        return data;
    }

    /// <summary>
    /// **プローブを球で見る**(「プローブGI」の F7)。1個ずつ、その点の係数で塗った球を描く。
    ///
    /// <para>
    /// 球の各点の色は「その向きを向いた白い面が、この点で受ける照り返し」。
    /// <b>灯りの直接の光は入っていない</b>(プローブが運ぶのは、ほかの面や空から来る光だけ)。
    /// 灯りのそばの球は、灯りのある側ではなく<b>灯りに照らされた路面や壁の側</b>が明るくなる。
    /// </para>
    /// </summary>
    public void DrawProbes(Camera camera, float radius)
    {
        if (_texture is null)
        {
            return;
        }

        Shader shader = _resources.GetShader(_viewShader);
        shader.Use();
        shader.SetMatrix4("uViewProjection", camera.ViewProjection);
        shader.SetFloat("uRadius", radius);

        _texture.Bind(TextureUnit.Texture0 + ProbeUnit);
        shader.SetInt("uProbeSh", ProbeUnit);

        for (int i = 0; i < ProbeCount; i++)
        {
            shader.SetInt("uProbeIndex", i);
            shader.SetVector3("uCenter", ProbePosition(i));
            _sphere.Draw();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _texture?.Dispose();
        _capture.Dispose();
        _sphere.Dispose();

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
