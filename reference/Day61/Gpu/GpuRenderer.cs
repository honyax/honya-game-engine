using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;

namespace CpuRayTracer;

/// <summary>
/// <b>GPU で積み重ねて描く</b>(今日の主役の C# 側)。<see cref="ProgressiveRenderer"/> の GPU 版。
///
/// <para>
/// 持ち物は3つだけ。
/// </para>
/// <list type="number">
/// <item><b>積算バッファ</b>(<c>rgba32f</c> の image)… サンプルの色の合計。CPU 版の <c>Vector3[]</c> にあたる</item>
/// <item><b>画素バッファ</b>(<c>uint</c> の SSBO)… 画面に出す 0xAARRGGBB。**これだけを読み戻す**</item>
/// <item><b>数え上げバッファ</b>(<c>uint</c> 7個)… 光線と交差判定の数</item>
/// </list>
///
/// <para>
/// <b>CPU 版といちばん違うのは、スレッドを1本も作らないこと</b>。
/// CPU 版は「描画スレッドが黙々と積み、UI スレッドは出来たところを見せる」形だったが、
/// GPU では<b>投げた仕事は GPU が勝手に進める</b>ので、UI スレッドから投げて、
/// 次のフレームで取りに行けばよい(要点6)。
/// </para>
/// <para>
/// 積算バッファを<b>読み戻さない</b>のも要点。1パスごとに 960x540x4 の float(8MB)を持ち帰ると、
/// 計算より転送のほうが重くなる。<b>平均して sRGB に直して 8 ビットに詰めるところまで GPU にやらせて</b>、
/// 持ち帰るのは 2MB の画素だけにする(自己チェックのときだけ、生の float を読む)。
/// </para>
/// </summary>
internal sealed class GpuRenderer : IDisposable
{
    /// <summary>GLSL 側の <c>local_size_x/y</c>。ここを変えたらシェーダも変える。</summary>
    private const int GroupSize = 8;

    /// <summary>数え上げの数(<see cref="RayCounters"/> の7つ)。</summary>
    private const int CounterSlots = 7;

    /// <summary>1フレームに投げるパスの上限。多すぎると窓の反応が鈍る。</summary>
    private const int MaxPassesPerFrame = 64;

    private readonly GL _gl;

    private readonly ComputeProgram _program;

    private readonly uint _accumTexture;

    private readonly uint _pixelBuffer;

    private readonly uint _counterBuffer;

    private readonly int[] _pixels;

    private readonly uint[] _counterZero = new uint[CounterSlots];

    private GpuScene? _scene;

    private Scene? _sourceScene;

    private Camera? _camera;

    private RenderSettings _settings = new();

    private int _samples;

    private int _displayVersion;

    private double _totalSeconds;

    /// <summary>直前に測った1パスの秒数。次のフレームに何パス投げるかの見積もりに使う。</summary>
    private double _passSeconds = 0.002;

    private RayCounters _passRays;

    public unsafe GpuRenderer(GpuDevice device, int width, int height)
    {
        _gl = device.Gl;
        Device = device;
        Width = width;
        Height = height;
        _pixels = new int[width * height];

        _program = ComputeProgram.Load(_gl, Path.Combine("shaders", "pathtrace.comp"));

        // 積算バッファ。**32 ビット float の4成分**。16 ビット(rgba16f)だと、
        // 何千サンプルも足し込むうちに「足しても増えない」(丸めで消える)点が出る。
        _accumTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _accumTexture);
        _gl.TexStorage2D(TextureTarget.Texture2D, 1, SizedInternalFormat.Rgba32f, (uint)width, (uint)height);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        _pixelBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _pixelBuffer);

        // DynamicRead =「GPU が書いて CPU が読む」。ドライバへの申告で、置き場所の選び方が変わる。
        _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(width * height * sizeof(uint)), null, BufferUsageARB.DynamicRead);

        _counterBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterBuffer);
        _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(CounterSlots * sizeof(uint)), null, BufferUsageARB.DynamicRead);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>この描き手が乗っている GPU。自己チェックが別の大きさの描き手を作るのに使う。</summary>
    public GpuDevice Device { get; }

    /// <summary><see cref="ComputeProgram.MissingUniforms"/> の中身(自己チェック19 が見る)。</summary>
    public IReadOnlyList<string> MissingUniforms => _program.MissingUniforms;

    /// <summary>SSBO のブロック1要素のバイト数を GPU に聞く(自己チェック19)。</summary>
    public int StorageBlockStride(string blockName) => _program.StorageBlockStride(blockName);

    public RenderStatus Status => new(_samples, _settings.MaxSamples, _passSeconds, _totalSeconds, _passRays);

    /// <summary>
    /// 描き直しを始める。<b>場面を GPU へ送り直し、積み上げを 0 に戻す</b>。
    /// <see cref="ProgressiveRenderer.Start"/> と同じ役目だが、待つスレッドが無いぶん短い。
    /// </summary>
    public void Start(Scene scene, Camera camera, RenderSettings settings)
    {
        // 場面は毎回送り直す。球 466 個 + 節点 301 個で 30KB ほどなので、
        // 「いつ作り直すべきか」を判定する仕掛けを持つより、毎回送るほうが安くて間違いが無い。
        _scene?.Dispose();
        _scene = GpuScene.Build(_gl, scene);
        _sourceScene = scene;

        _camera = camera;
        _settings = settings;
        _samples = 0;
        _totalSeconds = 0.0;
        _passRays = default;

        // 積算バッファは消さない。シェーダが uSample == 0 のときに読まないので、
        // 「消す」という 8MB ぶんの書き込みが丸ごと要らなくなる。
    }

    /// <summary>
    /// <paramref name="budgetSeconds"/> ぶんだけパスを投げて、画素を読み戻す。
    /// 何か進んだら true(画面を描き直す価値がある)。
    ///
    /// <para>
    /// <b>「時間になるまで投げ続ける」ができない</b>のが GPU の面白いところ。
    /// <c>glDispatchCompute</c> は<b>仕事を積むだけで、すぐ返ってくる</b>ので、
    /// 経過時間を見ながら回すと、ほぼ0秒のまま何百パスも積んでしまう。
    /// だから<b>前のフレームで測った1パスの時間から、投げる本数を見積もる</b>。
    /// </para>
    /// </summary>
    public bool RunPasses(double budgetSeconds)
    {
        if (_scene is null || _camera is null || _samples >= _settings.MaxSamples)
        {
            return false;
        }

        int passes = (int)Math.Clamp(budgetSeconds / Math.Max(_passSeconds, 1e-5), 1, MaxPassesPerFrame);
        passes = Math.Min(passes, _settings.MaxSamples - _samples);

        BindAll();
        SetSceneUniforms(_scene, _sourceScene!, _settings);
        SetCameraUniforms(_camera);

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < passes; i++)
        {
            // **最後の1パスだけ数える**。数え上げは uint なので、何パスも足し込むと 42 億で一周する
            // (960x540 の総当たりだと、たった 6 パスで溢れた。検証3)。
            // どのみち HUD に出すのは「1パスの光線」なので、直前で 0 に戻しておけばそれがそのまま答えになる。
            if (i == passes - 1)
            {
                _program.Barrier(MemoryBarrierMask.BufferUpdateBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);
                ClearCounters();
            }

            DispatchPass(_samples + i, _settings);
        }

        // 積んだ仕事が終わるまで待つ。普段は使わない大鉈(パイプラインが止まる)だが、
        // **1パスの時間を測る**にはこれしかない。ここを外すと HUD の「1パス」が転送待ちの時間になる。
        _gl.Finish();
        _passSeconds = clock.Elapsed.TotalSeconds / passes;
        _totalSeconds += clock.Elapsed.TotalSeconds;
        _samples += passes;

        ReadPixels();
        ReadCounters();
        return true;
    }

    /// <summary>1パス(全画素に1サンプル)を投げる。</summary>
    private void DispatchPass(int sampleIndex, RenderSettings settings)
    {
        Vector2 offset = settings.Jitter ? ProgressiveRenderer.SampleOffset(sampleIndex) : new Vector2(0.5f);
        _program.Set("uSample", sampleIndex);
        _program.Set("uJitter", offset);
        _program.Dispatch(
            (uint)((Width + GroupSize - 1) / GroupSize),
            (uint)((Height + GroupSize - 1) / GroupSize));

        // **パスとパスの間にも要る**。次のパスは前のパスが書いた積算バッファを読むので、
        // ここを抜くと「前のパスぶんが乗っていない絵」がたまに混ざる。
        _program.Barrier(MemoryBarrierMask.ShaderImageAccessBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    /// <summary>画面用の値が <paramref name="version"/> から更新されていれば写す(CPU 版と同じ口)。</summary>
    public bool CopyDisplay(int[] destination, ref int version)
    {
        if (_displayVersion == version)
        {
            return false;
        }

        Array.Copy(_pixels, destination, _pixels.Length);
        version = _displayVersion;
        return true;
    }

    /// <summary>
    /// 自己チェック用。<paramref name="samples"/> サンプル積んで、<b>積算バッファの生の値</b>(線形、平均済み)を返す。
    /// 並びは rgba の順で <c>Width * Height * 4</c> 個。
    ///
    /// <para>
    /// 画面に出す 8 ビットではなく float を読むのは、<b>1e-6 の桁で CPU と突き合わせたい</b>から
    /// (8 ビットに落とすと 1/255 = 0.004 より細かい違いが全部消える)。
    /// </para>
    /// </summary>
    public float[] RenderLinear(Scene scene, Camera camera, RenderSettings settings, int samples)
    {
        Start(scene, camera, settings);
        BindAll();
        SetSceneUniforms(_scene!, scene, settings);
        SetCameraUniforms(camera);

        for (int i = 0; i < samples; i++)
        {
            if (i == samples - 1)
            {
                _program.Barrier(MemoryBarrierMask.BufferUpdateBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);
                ClearCounters();
            }

            DispatchPass(i, settings);
        }

        _samples = samples;
        _program.Barrier(MemoryBarrierMask.TextureUpdateBarrierBit);
        ReadCounters();

        var data = new float[Width * Height * 4];
        _gl.BindTexture(TextureTarget.Texture2D, _accumTexture);
        _gl.GetTexImage<float>(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.Float, data);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        float scale = 1.0f / samples;
        for (int i = 0; i < data.Length; i++)
        {
            data[i] *= scale;
        }

        return data;
    }

    private void BindAll()
    {
        _program.Use();
        _scene!.Bind();
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GpuScene.PixelBinding, _pixelBuffer);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, GpuScene.CounterBinding, _counterBuffer);

        // image は SSBO とは別の口。ReadWrite なのは「読んで足して書く」ため。
        _gl.BindImageTexture(0, _accumTexture, 0, false, 0, GLEnum.ReadWrite, GLEnum.Rgba32f);
    }

    private void SetSceneUniforms(GpuScene gpuScene, Scene scene, RenderSettings settings)
    {
        _program.Set("uResolutionX", Width);
        _program.Set("uResolutionY", Height);
        _program.Set("uPlaneCount", gpuScene.PlaneCount);
        _program.Set("uSphereCount", gpuScene.SphereCount);
        _program.Set("uNodeCount", gpuScene.NodeCount);
        _program.Set("uPointLightCount", gpuScene.PointLightCount);
        _program.Set("uAreaLightCount", gpuScene.AreaLightCount);
        _program.Set("uSkyHorizon", scene.SkyHorizon);
        _program.Set("uSkyZenith", scene.SkyZenith);

        _program.Set("uMaxDepth", settings.MaxDepth);
        _program.Set("uNee", settings.NextEventEstimation);
        _program.Set("uRoulette", settings.RussianRoulette);
        _program.Set("uDecorrelate", settings.DecorrelatePixels);
        _program.Set("uOffsetOrigin", settings.OffsetOrigin);
        _program.Set("uFresnel", (int)settings.Fresnel);
        _program.Set("uViewMode", (int)settings.View);
    }

    private void SetCameraUniforms(Camera camera)
    {
        _program.Set("uCameraPosition", camera.Position);
        _program.Set("uCameraForward", camera.Forward);
        _program.Set("uCameraRight", camera.Right);
        _program.Set("uCameraUp", camera.Up);
        _program.Set("uHalfWidth", camera.HalfWidth);
        _program.Set("uHalfHeight", camera.HalfHeight);
    }

    private void ClearCounters()
    {
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterBuffer);
        _gl.BufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(CounterSlots * sizeof(uint)), _counterZero);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private void ReadPixels()
    {
        // 「これから CPU が読む」と宣言してから読む。宣言を忘れると、たまに1パス古い絵が返る。
        _program.Barrier(MemoryBarrierMask.BufferUpdateBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _pixelBuffer);
        _gl.GetBufferSubData<int>(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(_pixels.Length * sizeof(uint)), _pixels);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        _displayVersion++;
    }

    /// <summary>数え上げ(<b>直前の1パスぶん</b>)を読み戻す。CPU 版の HUD と同じ意味になる。</summary>
    private void ReadCounters()
    {
        _program.Barrier(MemoryBarrierMask.BufferUpdateBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);

        var raw = new uint[CounterSlots];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _counterBuffer);
        _gl.GetBufferSubData<uint>(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(CounterSlots * sizeof(uint)), raw);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);

        _passRays = new RayCounters
        {
            Camera = raw[0],
            Shadow = raw[1],
            Reflection = raw[2],
            Refraction = raw[3],
            Scatter = raw[4],
            BoxTests = raw[5],
            ShapeTests = raw[6],
        };
    }

    public void Dispose()
    {
        _scene?.Dispose();
        _program.Dispose();
        _gl.DeleteTexture(_accumTexture);
        _gl.DeleteBuffer(_pixelBuffer);
        _gl.DeleteBuffer(_counterBuffer);
    }
}
