using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;

namespace GaussianSplatting;

/// <summary>表示のしかた(<c>V</c> キー)。番号はシェーダの <c>uMode</c> と揃えてある。</summary>
internal enum DisplayMode
{
    /// <summary>ふつうの絵。ガウス関数で薄くしながら、半透明で重ねる。</summary>
    Splats = 0,

    /// <summary>中心だけを小さな点で描く。<b>点群</b>として見る。</summary>
    Centers = 1,

    /// <summary>2σ の楕円の内側を、ぼかさずに一様に塗る(濃さはその楕円体の不透明度)。楕円体の形と大きさがそのまま見える。</summary>
    Solid = 2,

    /// <summary>2σ の楕円の輪郭だけを描く。</summary>
    Outline = 3,

    /// <summary>画素シェーダがその画素で走った回数を、色で数える(要点8)。</summary>
    Overdraw = 4,
}

/// <summary>描き方の設定。キーで変える。</summary>
internal sealed record RenderOptions
{
    public DisplayMode Mode { get; init; } = DisplayMode.Splats;

    /// <summary>使う球面調和の次数(<c>M</c> キー)。楕円体が持っている次数より大きければ、持っている次数まで。</summary>
    public int ShDegree { get; init; } = SphericalHarmonics.MaxDegree;

    /// <summary>0.3 の広げ(<c>F</c> キー。要点4)。</summary>
    public bool Dilate { get; init; } = true;

    /// <summary>楕円体の大きさの倍率(<c>[</c> <c>]</c>)。共分散には2乗で掛かる。</summary>
    public float ScaleMultiplier { get; init; } = 1.0f;

    public SortMode Sort { get; init; } = SortMode.EveryFrame;
}

/// <summary>1フレームを描いた結果の数字。HUD に出す。</summary>
internal readonly record struct RenderStats(double GpuMilliseconds, double ReadbackMilliseconds, double AverageOverdraw, int MaxOverdraw);

/// <summary>
/// 楕円体を GPU で描く。<b>楕円体1個 = 四角形1枚</b>を、インスタンス描画でまとめて出す(要点3)。
///
/// <para>
/// 頂点バッファは使わない。四角形の4隅は <c>gl_VertexID</c>(0〜3)から、どの楕円体かは <c>gl_InstanceID</c> から決め、
/// 楕円体のデータは SSBO から読む。1回の <c>glDrawArraysInstanced(4 頂点 × 楕円体の数)</c> で全部が出る。
/// </para>
/// <para>
/// <b>深度バッファは作らない</b>。並べ替えた順(<see cref="DepthSorter"/>)に描き、
/// 「奥にある今までの色の上に、新しい色を α だけ重ねる」をブレンドで GPU にやらせる。
/// 色は RGBA の 32 ビット浮動小数で持つ。8 ビットだと、薄い楕円体を数百枚重ねたときに丸めの誤差が積もる。
/// また「重なりの数」の表示(1 を足していく)で、数を正確に数えられる。
/// </para>
/// </summary>
internal sealed class SplatRenderer : IDisposable
{
    // SSBO の口の番号。splat.vert の binding と揃える。
    private const uint SplatBinding = 0;
    private const uint ShBinding = 1;
    private const uint OrderBinding = 2;

    /// <summary>楕円体1個が SSBO で使う float の数(vec4 が3つ)。</summary>
    private const int FloatsPerSplat = 12;

    private readonly GL _gl;

    private readonly ShaderProgram _program;

    private readonly uint _vertexArray;

    private readonly uint _framebuffer;

    private readonly uint _colorTexture;

    private readonly uint _splatBuffer;

    private readonly uint _shBuffer;

    private readonly uint _orderBuffer;

    private readonly uint _timerQuery;

    /// <summary>読み戻した画素(RGBA の float。<b>下の行から</b>並んでいる。GL の約束)。</summary>
    private readonly float[] _readback;

    private SplatCloud? _cloud;

    public SplatRenderer(GpuDevice device, int width, int height)
    {
        _gl = device.Gl;
        Width = width;
        Height = height;
        _readback = new float[width * height * 4];

        _program = ShaderProgram.Load(_gl, "shaders/splat.vert", "shaders/splat.frag");

        // コアプロファイルでは、頂点属性を1つも使わなくても VAO を結んでおかないと描けない。
        _vertexArray = _gl.GenVertexArray();

        _colorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        _gl.TexStorage2D(TextureTarget.Texture2D, 1, SizedInternalFormat.Rgba32f, (uint)width, (uint)height);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _framebuffer = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colorTexture, 0);
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"描き込み先を作れなかった: {status}");
        }

        _splatBuffer = _gl.GenBuffer();
        _shBuffer = _gl.GenBuffer();
        _orderBuffer = _gl.GenBuffer();
        _timerQuery = _gl.GenQuery();
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>シェーダに見つからなかった uniform の名前(自己チェック10)。</summary>
    public IReadOnlyList<string> MissingUniforms => _program.MissingUniforms;

    /// <summary>
    /// 楕円体を GPU へ送る。場面を替えたときに1回だけ。
    ///
    /// <para>
    /// 送るのは位置・不透明度と、<b>大きさと回転から作った3D の共分散</b>(6つの数)。
    /// 大きさと回転のまま送ってシェーダで共分散を作ってもよいが、毎フレーム同じ計算をすることになるので、先に作っておく。
    /// 球面調和の係数は別のバッファにする(次数 3 なら1個 48 個の float で、位置の4倍ある)。
    /// </para>
    /// </summary>
    public unsafe void Upload(SplatCloud cloud)
    {
        _cloud = cloud;

        var splats = new float[cloud.Count * FloatsPerSplat];
        for (int i = 0; i < cloud.Count; i++)
        {
            Vector3 p = cloud.Positions[i];
            Covariance3 c = cloud.Covariance(i);
            int o = i * FloatsPerSplat;
            splats[o + 0] = p.X;
            splats[o + 1] = p.Y;
            splats[o + 2] = p.Z;
            splats[o + 3] = cloud.Opacities[i];
            splats[o + 4] = c.Xx;
            splats[o + 5] = c.Xy;
            splats[o + 6] = c.Xz;
            splats[o + 7] = c.Yy;
            splats[o + 8] = c.Yz;
            splats[o + 9] = c.Zz;
        }

        UploadBuffer<float>(_splatBuffer, splats);
        UploadBuffer<float>(_shBuffer, cloud.Sh);

        // 並べた順は毎フレーム送るので、大きさだけ確保しておく。
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _orderBuffer);
        _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(Math.Max(1, cloud.Count) * sizeof(uint)), null, BufferUsageARB.StreamDraw);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void UploadBuffer<T>(uint buffer, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);

        // 空の場面でも 0 バイトのバッファを結ばないように、最低 16 バイトは取る。
        nuint bytes = (nuint)Math.Max(16, data.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>());
        _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, bytes, ReadOnlySpan<byte>.Empty, BufferUsageARB.StaticDraw);
        if (data.Length > 0)
        {
            _gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, data);
        }

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    /// <summary>
    /// 1フレーム描いて、<paramref name="pixels"/>(窓に出す 32 ビットの色、上の行から)に書く。
    /// </summary>
    public RenderStats Render(in CameraFrame camera, ReadOnlySpan<uint> order, RenderOptions options, int[] pixels)
    {
        if (_cloud is not SplatCloud cloud)
        {
            throw new InvalidOperationException("Upload より先に Render が呼ばれた");
        }

        // 並べた順を送る。74 万個で 3MB。毎フレーム送っても PCIe には軽い。
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _orderBuffer);
        _gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, order);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        // 深度テストは使わない。並べた順に、ブレンドで重ねるだけ。
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        if (options.Mode == DisplayMode.Overdraw)
        {
            // 走った回数を数える: 足すだけ。
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        }
        else
        {
            // 奥から重ねる「over」。シェーダは色に α を掛けて出す(乗算済みアルファ)ので、
            //     新しい値 = 出した色 + 今までの値 × (1 − α)
            // α の列にも同じ式が掛かり、最後に残るのは「1 − 残りの透明度」になる。
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        }

        _program.Use();
        _program.Set("uCameraPosition", camera.Position);
        _program.Set("uRight", camera.Right);
        _program.Set("uUp", camera.Up);
        _program.Set("uForward", camera.Forward);
        _program.Set("uFocal", new Vector2(camera.Fx, camera.Fy));
        _program.Set("uViewport", new Vector2(Width, Height));
        _program.Set("uTanHalf", new Vector2(camera.TanHalfX, camera.TanHalfY));
        _program.Set("uShDegree", Math.Min(options.ShDegree, cloud.ShDegree));
        _program.Set("uShStride", cloud.ShFloatsPerSplat);
        _program.Set("uDilation", options.Dilate ? SplatProjection.Dilation : 0.0f);
        _program.Set("uCovarianceScale", options.ScaleMultiplier * options.ScaleMultiplier);
        _program.Set("uMode", (int)options.Mode);

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SplatBinding, _splatBuffer);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, ShBinding, _shBuffer);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, OrderBinding, _orderBuffer);
        _gl.BindVertexArray(_vertexArray);

        // 描いた時間を GPU に測らせる(タイマークエリ)。CPU の時計だと、命令を積んだ時間しか分からない。
        _gl.BeginQuery(QueryTarget.TimeElapsed, _timerQuery);
        _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)order.Length);
        _gl.EndQuery(QueryTarget.TimeElapsed);

        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.Blend);

        // GPU が描き終わるまで待ってから、読み戻しの時間を測り始める
        // (待たずに測ると、描いている時間まで「読み戻し」に入ってしまう)。
        _gl.Finish();
        var clock = Stopwatch.StartNew();
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.ReadPixels<float>(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.Float, _readback);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.GetQueryObject(_timerQuery, QueryObjectParameterName.Result, out ulong nanoseconds);

        double averageOverdraw = 0.0;
        int maxOverdraw = 0;
        if (options.Mode == DisplayMode.Overdraw)
        {
            ResolveOverdraw(pixels, out averageOverdraw, out maxOverdraw);
        }
        else
        {
            ResolveColor(pixels, cloud.Background);
        }

        return new RenderStats(nanoseconds / 1e6, clock.Elapsed.TotalMilliseconds, averageOverdraw, maxOverdraw);
    }

    /// <summary>
    /// 窓の画素 (x, y)(左上が原点)の色。<b>空を重ねたあとの、0〜1 に切る前の値</b>。自己チェック9で CPU の重ね直しと比べる。
    /// </summary>
    public Vector3 PixelColor(int x, int y, Vector3 background)
    {
        int o = ((Height - 1 - y) * Width + x) * 4;
        var rgb = new Vector3(_readback[o], _readback[o + 1], _readback[o + 2]);
        return rgb + background * (1.0f - _readback[o + 3]);
    }

    /// <summary>
    /// 描いた色に、残りの透明度のぶんだけ空を重ね、窓の 32 ビットの色にする。
    /// 学習済みの 3DGS の色は<b>写真の画素の値そのもの</b>(sRGB)なので、ガンマもトーンマッピングも掛けない。
    /// </summary>
    private void ResolveColor(int[] pixels, Vector3 background)
    {
        for (int y = 0; y < Height; y++)
        {
            int src = (Height - 1 - y) * Width * 4;
            int dst = y * Width;
            for (int x = 0; x < Width; x++, src += 4)
            {
                float t = 1.0f - _readback[src + 3];
                int r = ToByte(_readback[src] + background.X * t);
                int g = ToByte(_readback[src + 1] + background.Y * t);
                int b = ToByte(_readback[src + 2] + background.Z * t);
                pixels[dst + x] = (r << 16) | (g << 8) | b;
            }
        }
    }

    /// <summary>
    /// 回数を色にする(Day 60 の「光線の数」と同じ段々)。1 紺 / 4 青 / 16 水色 / 64 緑 / 256 黄 / 1024 赤 / 4096 以上 白。
    /// </summary>
    private void ResolveOverdraw(int[] pixels, out double average, out int max)
    {
        long total = 0;
        max = 0;
        for (int y = 0; y < Height; y++)
        {
            int src = (Height - 1 - y) * Width * 4;
            int dst = y * Width;
            for (int x = 0; x < Width; x++, src += 4)
            {
                int count = (int)_readback[src];
                total += count;
                max = Math.Max(max, count);
                pixels[dst + x] = HeatColor(count);
            }
        }

        average = (double)total / (Width * Height);
    }

    private static readonly int[] HeatSteps = [0x000000, 0x1a1a6e, 0x2255dd, 0x33bbee, 0x33cc55, 0xeedd33, 0xee3322, 0xffffff];

    /// <summary>回数 → 色の表。画素ごとに log を取ると、それだけで数 ms かかるので、先に作っておく。</summary>
    private static readonly int[] HeatTable = Enumerable.Range(0, 4097).Select(ComputeHeatColor).ToArray();

    public static int HeatColor(int count) => HeatTable[Math.Clamp(count, 0, HeatTable.Length - 1)];

    /// <summary>0 は黒。1 以上は 4 倍ごとに次の色(対数の目盛り)。段の間は線形に混ぜる。</summary>
    private static int ComputeHeatColor(int count)
    {
        if (count <= 0)
        {
            return 0;
        }

        float level = 1.0f + MathF.Log(count, 4.0f);
        int low = Math.Min((int)level, HeatSteps.Length - 1);
        int high = Math.Min(low + 1, HeatSteps.Length - 1);
        float f = level - (int)level;
        int Mix(int shift) => (int)(((HeatSteps[low] >> shift) & 0xff) * (1.0f - f) + ((HeatSteps[high] >> shift) & 0xff) * f);
        return (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
    }

    private static int ToByte(float v) => (int)(Math.Clamp(v, 0.0f, 1.0f) * 255.0f + 0.5f);

    public void Dispose()
    {
        _program.Dispose();
        _gl.DeleteVertexArray(_vertexArray);
        _gl.DeleteFramebuffer(_framebuffer);
        _gl.DeleteTexture(_colorTexture);
        _gl.DeleteBuffer(_splatBuffer);
        _gl.DeleteBuffer(_shBuffer);
        _gl.DeleteBuffer(_orderBuffer);
        _gl.DeleteQuery(_timerQuery);
    }
}
