using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>数え上げのやり方(Day 57 の要点6)。</summary>
internal enum ReductionMode
{
    /// <summary>共有メモリで畳んでから、グループにつき1回だけ <c>atomicAdd</c>。</summary>
    Shared,

    /// <summary>呼び出しごとに <c>atomicAdd</c>。**答えは同じ**。</summary>
    Naive,

    /// <summary>**同じフレームで両方**走らせて時間を比べる(既定)。答えは同じなので後勝ちでよい。</summary>
    Both,
}

/// <summary>筋書き。シェーダ側の <c>uMode</c> と同じ番号。</summary>
internal enum GpuParticleMode
{
    /// <summary>噴水。重力で落ちて床で跳ねる。**CPU 版(Day 49)と直に比べられる唯一の筋書き**。</summary>
    Fountain,

    /// <summary>引力。中心へ引かれて軌道を描く。**逆二乗**。</summary>
    Attractor,

    /// <summary>渦。位置から速度を作る(速度場)。粒が流れの筋になる。</summary>
    Vortex,
}

/// <summary>
/// 粒1つぶん。**32 バイト**。<c>particle-sim.comp</c> の <c>struct Particle</c> と1バイトずつ同じ。
///
/// <para>
/// <b>並びを合わせるのがこの型の全部の仕事</b>。std430 では <c>vec3</c> が 16 バイトに整列するので、
/// 「vec3 のあとに float」を2組にすると穴が空かない。
///   <c>Position</c>(0-11) <c>Age</c>(12-15) <c>Velocity</c>(16-27) <c>Lifetime</c>(28-31)。
/// </para>
///
/// <para>
/// <b>ずれても絵は出る</b>のがいちばん厄介なところ。ずれたぶんだけ「速度を位置として読む」ことになり、
/// 粒が明後日の方向へ飛ぶだけで、エラーは1つも出ない。
/// <see cref="GpuParticles.StrideBytes"/> を <c>Marshal.SizeOf</c> ではなく
/// <see cref="Unsafe.SizeOf{T}"/> で取って自己チェックで突き合わせてあるのはそのため。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuParticle
{
    public Vector3 Position;

    public float Age;

    public Vector3 Velocity;

    public float Lifetime;
}

/// <summary>
/// **コンピュートシェーダで動く 100 万粒**(Day 57)。今日の主役。
///
/// <para>
/// Day 49 の <see cref="ParticleEmitter"/> との違いは、粒の配列の置き場所だけ。
/// あちらは C# の <c>Particle[]</c> で、毎フレーム CPU が全部を舐め、
/// 四隅を作って 960KB を GPU へ送っていた。こちらは<b>粒が最初から最後まで VRAM に居る</b>。
/// </para>
///
/// <list type="table">
/// <item><term>撒く</term><description>死んだ粒が<b>自分で</b>生まれ直す(番号は変わらない)。CPU は関与しない</description></item>
/// <item><term>進める</term><description><c>glDispatchCompute</c> 1回。CPU の仕事は uniform を数本送ることだけ</description></item>
/// <item><term>描く</term><description>同じバッファを頂点バッファとして挿し、<c>GL_POINTS</c> で1回のドローコール</description></item>
/// <item><term>数える</term><description>共有メモリで畳んでからアトミック(<c>particle-reduce.comp</c>)</description></item>
/// </list>
///
/// <para>
/// <b>置き場所は <c>Sandbox/</c></b>。教養編のうちシェーダー中心の題材はここに置く決まりで、
/// <c>Render/</c> の中の誰もこのクラスを知らない——
/// エンジン本体の描画パス(Day 52〜56)に1本も線が伸びていない、ということ。
/// </para>
/// </summary>
internal sealed class GpuParticles : IDisposable
{
    /// <summary>粒1つのバイト数。**シェーダ側の std430 と合っていなければならない**。</summary>
    public static readonly int StrideBytes = Unsafe.SizeOf<GpuParticle>();

    /// <summary>選べる粒の数。**メモリは常に最大数ぶん確保する**(切り替えで作り直さない)。</summary>
    public static readonly int[] CountSteps = [65_536, 262_144, 1_048_576];

    /// <summary>選べるワークグループの大きさ。1024 は仕様の保証値(<c>GL_MAX_COMPUTE_WORK_GROUP_INVOCATIONS</c>)の上限。</summary>
    public static readonly int[] LocalSizeSteps = [64, 128, 256, 1024];

    /// <summary>集計の置き場(<c>aliveCount</c> と <c>heightFixed</c> の uint 2つ)。</summary>
    private const int StatsBytes = 2 * sizeof(uint);

    /// <summary>高さを整数にするときの倍率。シェーダ側の 100.0 と揃える。</summary>
    private const float HeightScale = 100.0f;

    private readonly GL _gl;
    private readonly RenderResources _resources;

    private readonly Handle<Shader> _simShader;
    private readonly Handle<Shader> _reduceShader;
    private readonly Handle<Shader> _drawShader;

    /// <summary>粒の本体。**SSBO でもあり、頂点バッファでもある1本**。</summary>
    private readonly uint _particleBuffer;

    /// <summary>集計の置き場。</summary>
    private readonly uint _statsBuffer;

    /// <summary>粒を頂点として読むための口。中身は <see cref="_particleBuffer"/> を指しているだけ。</summary>
    private readonly uint _vertexArray;

    private readonly GpuTimer _simTimer;

    /// <summary>共有メモリで畳んだときの時間。</summary>
    private readonly GpuTimer _sharedTimer;

    /// <summary>呼び出しごとに atomicAdd したときの時間。</summary>
    private readonly GpuTimer _naiveTimer;

    private readonly GpuTimer _drawTimer;

    private int _count = CountSteps[1];
    private int _localSize = 256;
    private uint _salt;
    private bool _resetPending = true;
    private bool _disposed;

    public GpuParticles(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        // **同じソースを2つの大きさで焼き分けられる**ようにしてある(Shader.SetDefines)。
        _simShader = resources.LoadComputeShader(
            Path.Combine(shaderDirectory, "particle-sim.comp"), $"LOCAL_SIZE {_localSize}");

        _reduceShader = resources.LoadComputeShader(
            Path.Combine(shaderDirectory, "particle-reduce.comp"), $"LOCAL_SIZE {_localSize}");

        _drawShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "gpu-particle.vert"),
            Path.Combine(shaderDirectory, "gpu-particle.frag"));

        _particleBuffer = CreateParticleBuffer();
        _statsBuffer = CreateStatsBuffer();
        _vertexArray = CreateVertexArray();

        _simTimer = new GpuTimer(gl);
        _sharedTimer = new GpuTimer(gl);
        _naiveTimer = new GpuTimer(gl);
        _drawTimer = new GpuTimer(gl);
    }

    /// <summary>確保してあるバイト数。**常に最大数ぶん**。</summary>
    public static long CapacityBytes => (long)CountSteps[^1] * StrideBytes;

    /// <summary>いま動かしている粒の数。</summary>
    public int Count
    {
        get => _count;
        set
        {
            int clamped = Math.Clamp(value, 1, CountSteps[^1]);
            if (clamped == _count)
            {
                return;
            }

            _count = clamped;

            // **増やしたぶんの粒は中身がゴミ**(確保しただけで一度も書いていない)。
            // 撒き直さずに進めると、NaN の位置から始まった粒が画面を横切る。
            _resetPending = true;
        }
    }

    public GpuParticleMode Mode { get; private set; } = GpuParticleMode.Fountain;

    /// <summary>ワークグループの大きさ。**変えるとシェーダを焼き直す**。</summary>
    public int LocalSize => _localSize;

    /// <summary>
    /// 数え上げのやり方(要点6)。**既定は「両方」**。
    ///
    /// <para>
    /// 片方ずつ切り替えて時間を比べると、<b>GPU のクロックの上がり下がりが差に化ける</b>——
    /// VSync で休んでいる間にクロックが落ちるので、同じ処理でも走行ごとに倍ぶれる。
    /// <b>同じフレームの中で続けて両方走らせる</b>と、その揺れが両方に等しく乗るので比べられる。
    /// (Day 57 の検証で、切り替え方式では「素朴なほうが速い」と読める数字が出た。
    ///  同じフレームで測り直したら、そちらは向きが逆だった)
    /// </para>
    /// </summary>
    public ReductionMode Reduction { get; set; } = ReductionMode.Both;

    /// <summary>毎フレーム集計を CPU へ読み戻すか。**読み戻した瞬間に同期が起きる**(要点7)。</summary>
    public bool ReadBackEveryFrame { get; set; }

    /// <summary>粒の大きさ(m)。点の画素数は距離で割って決まる。</summary>
    public float Size { get; set; } = 0.06f;

    /// <summary>
    /// 加算合成の明るさ。**1粒ぶんではなく「群れ全体」の明るさ**(要点8)。
    ///
    /// <para>
    /// 加算合成では、重なった粒の明るさがそのまま足し算になる。
    /// 1粒の明るさを固定したまま個数を 16 倍にすると<b>絵が 16 倍明るくなって真っ白に飽和する</b>——
    /// 「粒を増やしたら形が見えなくなった」の正体がこれ。
    /// </para>
    ///
    /// <para>
    /// そこで<b>決まった量の光を粒の数で割って配る</b>(<see cref="ParticleIntensity"/>)。
    /// 本物のエンジンも同じことをしていて、エフェクトの明るさは
    /// 「1粒いくら」ではなく「この炎ぜんぶでいくら」で決めるのが普通。
    /// </para>
    /// </summary>
    public float Intensity { get; set; } = 1.2f;

    /// <summary>光を配るときの基準の数。この数のときに <see cref="Intensity"/> がそのまま出る。</summary>
    public const int ReferenceCount = 65_536;

    /// <summary>実際にシェーダへ送る1粒あたりの明るさ。**個数に反比例する**。</summary>
    public float ParticleIntensity => Intensity * ReferenceCount / _count;

    /// <summary>撒く場所(噴水と渦の中心)。</summary>
    public Vector3 Origin { get; set; } = new(0.0f, 0.02f, 0.0f);

    /// <summary>引力の中心。</summary>
    public Vector3 Attractor { get; set; } = new(0.0f, 3.0f, 0.0f);

    public float AttractorStrength { get; set; } = 60.0f;

    /// <summary>1回のディスパッチで走らせるワークグループの数。**切り上げる**。</summary>
    public int GroupCount => (_count + _localSize - 1) / _localSize;

    /// <summary>ワークグループの都合で余分に走る呼び出しの数。シェーダの先頭の番人が弾く。</summary>
    public int WastedInvocations => (GroupCount * _localSize) - _count;

    /// <summary>最後に読み戻した「生きている粒の数」。読み戻していなければ -1。</summary>
    public int AliveCount { get; private set; } = -1;

    /// <summary>最後に読み戻した平均の高さ(m)。</summary>
    public float AverageHeight { get; private set; }

    /// <summary>集計を読み戻すのに掛かった時間(ms)。**ここが同期の代償そのもの**。</summary>
    public double ReadBackMilliseconds { get; private set; }

    public double SimGpuMilliseconds => _simTimer.Milliseconds;

    /// <summary>共有メモリで畳んだときの GPU 時間(ms)。</summary>
    public double SharedGpuMilliseconds => _sharedTimer.Milliseconds;

    /// <summary>全部 <c>atomicAdd</c> したときの GPU 時間(ms)。</summary>
    public double NaiveGpuMilliseconds => _naiveTimer.Milliseconds;

    public double DrawGpuMilliseconds => _drawTimer.Milliseconds;

    /// <summary>次のフレームで全部撒き直す。</summary>
    public void Reset() => _resetPending = true;

    public void SetMode(GpuParticleMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;

        // 筋書きが変わると撒き方も寿命も変わる。古い粒を残すと混ざる。
        _resetPending = true;
    }

    /// <summary>
    /// ワークグループの大きさを変える。**シェーダを焼き直す**ので、失敗しうる。
    /// </summary>
    public bool SetLocalSize(int localSize)
    {
        if (localSize == _localSize)
        {
            return true;
        }

        string define = $"LOCAL_SIZE {localSize}";

        if (!_resources.GetShader(_simShader).SetDefines(define))
        {
            return false;
        }

        if (!_resources.GetShader(_reduceShader).SetDefines(define))
        {
            // 片方だけ新しい大きさになる状態を残さない。
            _resources.GetShader(_simShader).SetDefines($"LOCAL_SIZE {_localSize}");
            return false;
        }

        _localSize = localSize;
        return true;
    }

    /// <summary>
    /// **1フレームぶん進める**(要点4)。中身は uniform を送ってディスパッチを1回。
    ///
    /// <para>
    /// <b>ここで CPU がやっているのは、100 万個ぶんの仕事ではない</b>。
    /// 何個であっても送る uniform の数は同じで、変わるのはワークグループの数だけ。
    /// Day 49 の <c>ParticleEmitter.Update</c> が粒の数に比例して重くなるのと、
    /// そこがいちばん違う。
    /// </para>
    /// </summary>
    public void Simulate(float deltaSeconds, float time)
    {
        Shader shader = _resources.GetShader(_simShader);
        shader.Use();

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _particleBuffer);

        shader.SetUInt("uCount", (uint)_count);

        // **刻みに上限を付ける**。ウィンドウを掴んで止めたあとの1フレームは
        // 数百 ms になることがあり、そのまま積分すると粒が一瞬で画面の外へ飛ぶ。
        shader.SetFloat("uDeltaTime", _resetPending ? 0.0f : MathF.Min(deltaSeconds, 1.0f / 30.0f));
        shader.SetFloat("uTime", time);
        shader.SetInt("uMode", (int)Mode);
        shader.SetInt("uReset", _resetPending ? 1 : 0);
        shader.SetUInt("uSalt", _salt);
        shader.SetVector3("uGravity", new Vector3(0.0f, -9.81f, 0.0f));
        shader.SetVector3("uOrigin", Origin);
        shader.SetVector3("uAttractor", Attractor);
        shader.SetFloat("uAttractorStrength", AttractorStrength);

        _simTimer.Begin();
        shader.Dispatch((uint)GroupCount);
        _simTimer.End();

        // **書いたものを頂点として読む前に、見えるようにする**(要点5)。
        // ShaderStorageBarrierBit … このあと別のコンピュート(数え上げ)が SSBO として読む
        // VertexAttribArrayBarrierBit … このあと描画が頂点属性として読む
        shader.Barrier(
            MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.VertexAttribArrayBarrierBit);

        _resetPending = false;

        // 次に生まれる粒の向きを変える。**撒き直した直後だけ回す**のでは足りない——
        // 粒は寿命が来るたびに生まれ直すので、毎フレーム変えておく。
        _salt++;
    }

    /// <summary>
    /// **生きている粒の数と平均の高さを GPU に数えさせる**(要点6)。
    ///
    /// <para>
    /// 数え上げそのものは速い(100 万個で 0.1ms 前後)。
    /// 高いのは<b>結果を CPU へ持って帰ること</b>で、そちらは <see cref="ReadStats"/> のほう。
    /// </para>
    /// </summary>
    public void Reduce()
    {
        if (Reduction != ReductionMode.Naive)
        {
            RunReduce(naive: false, _sharedTimer);
        }

        if (Reduction != ReductionMode.Shared)
        {
            RunReduce(naive: true, _naiveTimer);
        }

        if (ReadBackEveryFrame)
        {
            ReadStats();
        }
    }

    /// <summary>数え上げを1回走らせる。**毎回 0 に戻してから**。</summary>
    private unsafe void RunReduce(bool naive, GpuTimer timer)
    {
        // **毎回 0 に戻す**。atomicAdd は足すだけなので、戻し忘れると合計が積み上がり続ける。
        Span<uint> zero = stackalloc uint[2];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _statsBuffer);

        fixed (uint* pointer = zero)
        {
            _gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, StatsBytes, pointer);
        }

        Shader shader = _resources.GetShader(_reduceShader);
        shader.Use();

        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, _particleBuffer);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, _statsBuffer);

        shader.SetUInt("uCount", (uint)_count);
        shader.SetInt("uNaive", naive ? 1 : 0);

        timer.Begin();
        shader.Dispatch((uint)GroupCount);
        timer.End();

        // **書いた集計を glGetBufferSubData で読む前に**見えるようにする。
        // BufferUpdateBarrierBit が「バッファの読み書き API から見えるようにする」ぶん。
        shader.Barrier(MemoryBarrierMask.BufferUpdateBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    /// <summary>
    /// **集計を CPU へ読み戻す**。8 バイトしか読まないのに、ここで数 ms 止まる。
    ///
    /// <para>
    /// <c>glGetBufferSubData</c> は「その時点の中身」を返す約束なので、
    /// <b>GPU が積まれた命令を全部こなすまで CPU が待つ</b>。
    /// バイト数ではなく<b>並走が切れること</b>が代償、というのが今日の要点7。
    /// 本物のエンジンはピクセルバッファやフェンスを使って「数フレーム前の結果」を拾う——
    /// Day 54 の <see cref="GpuTimer"/> が輪を作っているのと、まったく同じ形の解決になる。
    /// </para>
    /// </summary>
    public unsafe void ReadStats()
    {
        Span<uint> stats = stackalloc uint[2];

        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _statsBuffer);

        fixed (uint* pointer = stats)
        {
            _gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, StatsBytes, pointer);
        }

        ReadBackMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        AliveCount = (int)stats[0];
        AverageHeight = stats[0] == 0 ? 0.0f : stats[1] / HeightScale / stats[0];
    }

    /// <summary>
    /// **粒を1回のドローコールで描く**。
    ///
    /// <para>
    /// 頂点バッファの中身を作る処理がどこにも無いのが、Day 49 との決定的な違い。
    /// <see cref="CreateVertexArray"/> で<b>コンピュートが書く SSBO をそのまま挿してある</b>ので、
    /// ここは「何番目から何個」を言うだけで済む。
    /// </para>
    /// </summary>
    /// <param name="viewportHeight">画面の高さ(画素)。点の大きさを画素数に直すのに要る。</param>
    public void Draw(Camera camera, int viewportHeight)
    {
        Shader shader = _resources.GetShader(_drawShader);
        shader.Use();

        shader.SetMatrix4("uView", camera.ViewMatrix);
        shader.SetMatrix4("uProjection", camera.ProjectionMatrix);

        // 透視投影で「1m のものが何画素になるか」は 画面の高さ / (2 tan(fov/2)) / 距離。
        // 分子までをここで作って、距離での割り算だけ頂点シェーダに任せる。
        shader.SetFloat(
            "uPointScale", viewportHeight / (2.0f * MathF.Tan(camera.FieldOfView * 0.5f)));

        shader.SetFloat("uSize", Size);
        shader.SetFloat("uIntensity", ParticleIntensity);
        shader.SetInt("uMode", (int)Mode);

        // **点の大きさを頂点シェーダに決めさせる**ための許可。
        // これを出し忘れると gl_PointSize が無視されて、全部の粒が1画素になる。
        _gl.Enable(EnableCap.ProgramPointSize);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);

        // 深度は読むが書かない(Day 49 の要点4と同じ)。
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _drawTimer.Begin();
        _gl.BindVertexArray(_vertexArray);
        _gl.DrawArrays(PrimitiveType.Points, 0, (uint)_count);
        _gl.BindVertexArray(0);
        _drawTimer.End();

        // **借りた状態は返す**(Day 18 からの作法)。
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ProgramPointSize);
    }

    /// <summary>
    /// 粒を CPU へ読み戻す(自己チェック用)。**100 万個なら 32MB 動く**。
    /// </summary>
    public unsafe void ReadParticles(Span<GpuParticle> destination)
    {
        int count = Math.Min(destination.Length, _count);

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _particleBuffer);

        fixed (GpuParticle* pointer = destination)
        {
            _gl.GetBufferSubData(
                BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(count * StrideBytes), pointer);
        }
    }

    /// <summary>
    /// **同じ計算を C# で1ステップ回す**(噴水のみ)。GPU と見比べるための鏡。
    ///
    /// <para>
    /// <c>particle-sim.comp</c> の <c>uMode == 0</c> の分岐と<b>同じ式</b>を書いてある。
    /// 撒き直し(寿命が尽きた粒)はここでは行わない——
    /// ハッシュまで写すと長くなるうえ、比べたいのは積分のほうだから。
    /// そのぶん<b>CPU 側にわずかに有利な計測</b>になっている。
    /// </para>
    /// </summary>
    /// <returns>寿命が尽きて、このステップでは進めなかった粒の数。</returns>
    public static int StepFountainOnCpu(Span<GpuParticle> particles, float deltaSeconds, Vector3 origin)
    {
        var gravity = new Vector3(0.0f, -9.81f, 0.0f);
        int expired = 0;

        for (int i = 0; i < particles.Length; i++)
        {
            ref GpuParticle p = ref particles[i];

            p.Age += deltaSeconds;

            if (p.Age >= p.Lifetime)
            {
                expired++;
                continue;
            }

            p.Velocity += gravity * deltaSeconds;
            p.Position += p.Velocity * deltaSeconds;

            if (p.Position.Y < origin.Y)
            {
                p.Position.Y = origin.Y;
                p.Velocity.Y = MathF.Abs(p.Velocity.Y) * 0.35f;
                p.Velocity.X *= 0.75f;
                p.Velocity.Z *= 0.75f;
            }
        }

        return expired;
    }

    // ================================================================
    //  GPU 側の作り
    // ================================================================

    private unsafe uint CreateParticleBuffer()
    {
        uint buffer = _gl.GenBuffer();

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);

        // **中身は渡さない**(null)。場所だけ取って、最初の撒き直しで GPU が全部書く。
        //
        // DynamicCopy は「GPU が書いて GPU が読む」という使い方の申告。
        // Day 18 の StreamDraw(CPU が書いて GPU が読む)と対になっている——
        // **この2つの違いが、今日やっていることの全部**と言ってもよい。
        _gl.BufferData(
            BufferTargetARB.ShaderStorageBuffer,
            (nuint)CapacityBytes,
            null,
            BufferUsageARB.DynamicCopy);

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return buffer;
    }

    private unsafe uint CreateStatsBuffer()
    {
        uint buffer = _gl.GenBuffer();

        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, StatsBytes, null, BufferUsageARB.DynamicRead);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        return buffer;
    }

    /// <summary>
    /// **同じバッファを頂点バッファとして挿す**(要点4)。今日いちばん効く1手。
    ///
    /// <para>
    /// OpenGL のバッファオブジェクトは<b>型を持たないバイト列</b>で、
    /// 「何であるか」は挿した口(ターゲット)が決める。だから
    /// <c>GL_SHADER_STORAGE_BUFFER</c> に挿せば SSBO、
    /// <c>GL_ARRAY_BUFFER</c> に挿せば頂点バッファになる——**同じ1本が両方になれる**。
    /// </para>
    ///
    /// <para>
    /// これが無いと、コンピュートの結果をいったん CPU へ戻して頂点バッファへ詰め直すことになる。
    /// 100 万粒で毎フレーム 32MB の往復——GPU で計算した意味がそこで消える。
    /// </para>
    /// </summary>
    private unsafe uint CreateVertexArray()
    {
        uint array = _gl.GenVertexArray();

        _gl.BindVertexArray(array);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _particleBuffer);

        uint stride = (uint)StrideBytes;

        // 0: position(vec3)  12 バイト目まで
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);

        // 1: age(float)
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)12);

        // 2: velocity(vec3)
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)16);

        // 3: lifetime(float)
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)28);

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        return array;
    }

    // ================================================================
    //  ドライバに聞く(実行モデルの上限)
    // ================================================================

    /// <summary>
    /// このドライバが許すワークグループの上限。**仕様の保証値より大きいのが普通**。
    ///
    /// <para>
    /// 保証されているのは「1グループ 1024 呼び出し」「各次元 1024/1024/64」
    /// 「グループの数は各次元 65535 以上」「共有メモリ 32KB 以上」。
    /// <b>これを超える値を前提にしたコードは、別の GPU で動かない</b>。
    /// </para>
    /// </summary>
    public static ComputeLimits QueryLimits(GL gl)
    {
        gl.GetInteger(GLEnum.MaxComputeWorkGroupInvocations, out int invocations);
        gl.GetInteger(GLEnum.MaxComputeSharedMemorySize, out int shared);

        // **数と大きさの上限は次元ごと**なので、添字付きで聞く(glGetIntegeri_v)。
        gl.GetInteger(GLEnum.MaxComputeWorkGroupCount, 0u, out int countX);
        gl.GetInteger(GLEnum.MaxComputeWorkGroupSize, 0u, out int sizeX);

        return new ComputeLimits(invocations, shared, countX, sizeX);
    }

    /// <summary>ドライバに聞いたコンピュートの上限。</summary>
    internal readonly record struct ComputeLimits(
        int MaxInvocations, int MaxSharedBytes, int MaxGroupCountX, int MaxGroupSizeX);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _gl.DeleteBuffer(_particleBuffer);
        _gl.DeleteBuffer(_statsBuffer);
        _gl.DeleteVertexArray(_vertexArray);

        _simTimer.Dispose();
        _sharedTimer.Dispose();
        _naiveTimer.Dispose();
        _drawTimer.Dispose();

        // シェーダは RenderResources が持っているので、ここでは返さない
        // (Day 31 の「窓口が寿命を持つ」に合わせる)。
    }
}
