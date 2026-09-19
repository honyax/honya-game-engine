using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

// System.Windows.Forms.ImageLayout と衝突する。VulkanImage.cs と同じ事情。
using ImageLayout = Silk.NET.Vulkan.ImageLayout;

namespace HardwareRayTracer;

/// <summary>
/// シェーダに渡す小さな値のかたまり(今日の要点4)。
///
/// <para>
/// 毎フレーム変わる少量の値(カメラ、画面の大きさ、表示の種類)は、バッファを作って転送するより
/// <b>プッシュ定数</b>のほうが速い。コマンドバッファに値を直接埋め込む仕組みで、
/// 転送も同期も要らない。ただし<b>大きさの保証はたった 128 バイト</b>しかない
/// (これは Vulkan が保証する最低値で、実際の GPU はもっと持っていることが多い)。
/// </para>
/// <para>
/// OpenGL の <c>glUniform*</c> に近いが、あちらはプログラムオブジェクトが状態として値を覚えていた。
/// プッシュ定数は<b>コマンドバッファが覚える</b>——同じコマンドバッファの中で値を変えながら
/// 何度もディスパッチできる。
/// </para>
/// <para>
/// <b>並びは GLSL 側と1バイトも違ってはいけない</b>。<c>vec3</c> を避けて <c>vec4</c> で並べ、
/// 端数は w に詰める(<see cref="GpuSphere"/> と同じ作法)。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PushConstants
{
    /// <summary>xyz = カメラ位置、w = 画面の半分の幅。</summary>
    public Vector4 PositionHalfWidth;

    /// <summary>xyz = 右方向、w = 画面の半分の高さ。</summary>
    public Vector4 RightHalfHeight;

    /// <summary>xyz = 上方向、w = 未使用。</summary>
    public Vector4 Up;

    /// <summary>xyz = 前方向、w = 未使用。</summary>
    public Vector4 Forward;

    public int Width;

    public int Height;

    public int SphereCount;

    /// <summary>表示の種類。0 = 陰影、1 = 法線、2 = 交差判定の回数、3 = 同(細かい目盛り)。</summary>
    public int Mode;

    /// <summary>
    /// 拡散の球の数 = 金属の球が始まる位置(Day 62c で追加)。
    /// BLAS のジオメトリを材質で割ったので、シェーダが球の番号を引き直すのに要る。
    /// </summary>
    public int DiffuseCount;
}

/// <summary>
/// 探し方(Day 62c で3つ目が増えた)。
/// <b>3つとも同じ絵を出す</b>ので、速さと仕組みだけを比べられる。
/// </summary>
internal enum TraceMode
{
    /// <summary>総当たり。球を先頭から全部試す(Day 62a)。</summary>
    BruteForce,

    /// <summary>コンピュートシェーダから <c>rayQueryEXT</c> で TLAS をたどる(Day 62b)。</summary>
    RayQuery,

    /// <summary>レイトレーシングパイプライン + SBT(Day 62c)。</summary>
    Pipeline,
}

/// <summary>
/// コンピュートシェーダを1回走らせて、絵を <c>int[]</c> で受け取る(今日の要点4・5)。
///
/// <para>
/// OpenGL なら「シェーダを使う」「SSBO を挿す」「glDispatchCompute」の3行だった。
/// Vulkan は同じことに<b>5つの物</b>を要求する。
/// </para>
/// <list type="number">
/// <item><b>ディスクリプタセットレイアウト</b> … 「0 番に画像、1 番にバッファが来る」という<b>型の宣言</b></item>
/// <item><b>ディスクリプタプール</b> … セットを切り出す元。何個・何種類切り出すかを先に申告する</item>
/// <item><b>ディスクリプタセット</b> … レイアウトに沿って実際の資源を挿した<b>実体</b></item>
/// <item><b>パイプラインレイアウト</b> … セットの並び + プッシュ定数の大きさ。シェーダとの契約</item>
/// <item><b>パイプライン</b> … シェーダとレイアウトを束ねた、GPU が実行できる形</item>
/// </list>
/// <para>
/// 面倒だが、<b>これが Vulkan が速い理由でもある</b>。OpenGL はドローのたびに
/// 「今バインドされているものの組み合わせ」を検証して、必要ならシェーダを再コンパイルしていた
/// (いわゆるシェーダコンパイルによるカクつきの正体)。Vulkan は組み合わせを事前に固めさせるので、
/// 実行時には検証も再コンパイルも起きない。
/// </para>
/// <para>
/// <b>Day 62b の差分は3つ</b>。
/// </para>
/// <list type="number">
/// <item>ディスクリプタに<b>3番目(TLAS)</b>を足す。型は <c>AccelerationStructureKhr</c> で、
/// 書き込み方だけが他と違う(<c>WriteDescriptorSetAccelerationStructureKHR</c> を PNext につなぐ)</item>
/// <item><b>パイプラインを2つ</b>作る。同じ GLSL を <c>USE_RAY_QUERY</c> あり/なしで2回翻訳したもの</item>
/// <item><see cref="Render"/> が<b>どちらのパイプラインを使うか</b>を受け取る</item>
/// </list>
/// <para>
/// <b>起動時に作っておく</b>のが Vulkan らしいところ。切り替えは
/// <c>CmdBindPipeline</c> に渡すハンドルが変わるだけで、作り直しは起きない。
/// </para>
/// <para>
/// <b>Day 62c の差分は3つ</b>。
/// </para>
/// <list type="number">
/// <item>ディスクリプタの <c>StageFlags</c> に<b>レイトレーシングの段</b>を足す
/// (raygen / miss / closest-hit / intersection から同じ資源を触るので)</item>
/// <item><see cref="RayTracingPipeline"/> を1つ持つ(パイプライン + SBT)</item>
/// <item><see cref="Render"/> が <see cref="TraceMode"/> を受け取り、
/// <c>CmdDispatch</c> か <c>CmdTraceRaysKHR</c> かを選ぶ</item>
/// </list>
/// <para>
/// <b>クラス名が実態と合わなくなった</b>(もう「コンピュート」だけではない)。
/// ただ名前を変えると git の差分が 500 行の削除 + 追加になって、
/// その日の変更が読めなくなるので今日は据え置く(Day62c.md の「今日残した歪み」)。
/// </para>
/// </summary>
internal sealed unsafe class ComputeRenderer : IDisposable
{
    /// <summary>
    /// ワークグループの大きさ。<b>GLSL 側の <c>local_size_x/y</c> と必ず一致させる</b>。
    /// 8x8 = 64 呼び出しは NVIDIA のワープ(32)の倍数で、AMD の wave32/64 にも都合がよい。
    /// </summary>
    private const int WorkGroupSize = 8;

    /// <summary>
    /// プッシュ定数を流す段。<b>パイプラインレイアウトで宣言したものと一致させる</b>。
    /// </summary>
    private const ShaderStageFlags AllPushStages =
        ShaderStageFlags.ComputeBit
        | ShaderStageFlags.RaygenBitKhr
        | ShaderStageFlags.MissBitKhr
        | ShaderStageFlags.ClosestHitBitKhr
        | ShaderStageFlags.IntersectionBitKhr;

    private readonly VulkanDevice _device;
    private readonly VulkanImage _image;

    /// <summary>画像を引き取る先。CPU から map できる置き場所(要点5)。</summary>
    private readonly VulkanBuffer _readback;

    private readonly VulkanBuffer _spheres;
    private readonly int _sphereCount;

    /// <summary>加速構造(Day 62b で追加)。対応していない GPU では null。</summary>
    private readonly AccelerationStructure? _acceleration;

    /// <summary>レイトレーシングパイプラインと SBT(Day 62c で追加)。</summary>
    private readonly RayTracingPipeline? _rayTracingPipeline;

    private readonly int _diffuseCount;

    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;

    /// <summary>総当たり版。</summary>
    private readonly Pipeline _brutePipeline;

    private readonly ShaderModule _bruteModule;

    /// <summary>ray query 版(Day 62b で追加)。対応していない GPU では既定値のまま。</summary>
    private readonly Pipeline _rayQueryPipeline;

    private readonly ShaderModule _rayQueryModule;

    /// <summary>毎フレームのコマンドを積むプール。<see cref="VulkanDevice"/> の使い回し用とは分けてある。</summary>
    private readonly CommandPool _ownPool;

    private readonly CommandBuffer _commands;
    private readonly Fence _fence;

    /// <summary>
    /// GPU の時計を読むための入れ物(今日の要点6)。2目盛りぶん使う。
    ///
    /// <para>
    /// CPU 側の <see cref="Stopwatch"/> で測れるのは「投げてから待ち終わるまで」で、
    /// そこには<b>引き取りの転送も、提出の往復の待ち時間も混ざっている</b>。
    /// 今日の設定ではそちらが 8ms ほどあり、光線を追う時間(1〜4ms)を覆い隠してしまう。
    /// 「球を増やすと重くなる」を見たいなら、<b>GPU に測らせるしかない</b>。
    /// </para>
    /// </summary>
    private readonly QueryPool _queryPool;

    /// <param name="bruteSpirv">総当たり版の SPIR-V。</param>
    /// <param name="rayQuerySpirv">
    /// ray query 版の SPIR-V(Day 62b で追加)。null なら総当たりだけで動く。
    /// </param>
    /// <param name="compiler">
    /// レイトレーシングパイプラインの6本を翻訳するのに使う(Day 62c で追加)。
    /// </param>
    /// <param name="shaderDirectory">シェーダの置き場所。</param>
    public ComputeRenderer(
        VulkanDevice device, int width, int height, SceneSpheres scene,
        uint[] bruteSpirv, uint[]? rayQuerySpirv,
        ShaderCompiler compiler, string shaderDirectory)
    {
        _device = device;
        Width = width;
        Height = height;
        _sphereCount = scene.Count;
        _diffuseCount = scene.DiffuseCount;

        Vk vk = device.Api;

        _image = VulkanImage.Create(device, width, height);
        _readback = VulkanBuffer.CreateStaging(device, (ulong)(width * height * sizeof(int)), BufferUsageFlags.TransferDstBit);
        _spheres = VulkanBuffer.CreateDeviceLocal<GpuSphere>(device, scene.Spheres, BufferUsageFlags.StorageBufferBit);

        // 加速構造を建てる(Day 62b)。ray query を使わないなら建てる意味が無いので、
        // SPIR-V が渡されたときだけ。**起動時に1回だけ**建てて、以降は読むだけ。
        if (rayQuerySpirv is not null && device.RayQuerySupported)
        {
            _acceleration = AccelerationStructure.Build(device, scene);
        }

        bool useAcceleration = _acceleration is not null;

        // ---- 1. レイアウト(型の宣言)------------------------------------------------
        // binding の番号は GLSL の layout(binding = N) と一致させる。
        // StageFlags で「どの段から見えるか」を絞る。今日は計算しかないので ComputeBit だけ。
        //
        // **StageFlags にレイトレーシングの段を足す**(Day 62c)。
        // 同じ binding を、コンピュートからもレイトレーシングパイプラインの4段からも触るので、
        // 全部を or で並べる。挙げ忘れた段から触ると未定義動作(検証レイヤは捕まえてくれる)。
        const ShaderStageFlags AllStages =
            ShaderStageFlags.ComputeBit
            | ShaderStageFlags.RaygenBitKhr
            | ShaderStageFlags.MissBitKhr
            | ShaderStageFlags.ClosestHitBitKhr
            | ShaderStageFlags.IntersectionBitKhr;

        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = AllStages,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = AllStages,
        };

        // TLAS(Day 62b で追加)。**総当たり版のシェーダはこの binding を使わない**が、
        // レイアウトに入れておいても構わない——シェーダが静的に使っていない binding は、
        // 挿していなくても、中身が何でもよい(仕様で明記されている)。
        bindings[2] = new DescriptorSetLayoutBinding
        {
            Binding = 2,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
            DescriptorCount = 1,
            StageFlags = AllStages,
        };

        uint bindingCount = useAcceleration ? 3u : 2u;
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = bindingCount,
            PBindings = bindings,
        };
        VulkanDevice.Check(vk.CreateDescriptorSetLayout(device.Handle, &layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout");

        // ---- 2. プール(切り出す元)----------------------------------------------------
        // 「画像1つ、バッファ1つ、セットは1個まで」と先に申告する。
        // 足りなくなっても伸びない(ErrorOutOfPoolMemory が返る)。
        var poolSizes = stackalloc DescriptorPoolSize[3];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 1 };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 1 };
        poolSizes[2] = new DescriptorPoolSize { Type = DescriptorType.AccelerationStructureKhr, DescriptorCount = 1 };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = bindingCount,
            PPoolSizes = poolSizes,
        };
        VulkanDevice.Check(vk.CreateDescriptorPool(device.Handle, &poolInfo, null, out _pool), "vkCreateDescriptorPool");

        // ---- 3. セット(実体)+ 資源を挿す --------------------------------------------
        DescriptorSetLayout setLayout = _setLayout;
        var setAlloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        VulkanDevice.Check(vk.AllocateDescriptorSets(device.Handle, &setAlloc, out _set), "vkAllocateDescriptorSets");

        var imageInfo = new DescriptorImageInfo
        {
            ImageView = _image.View,

            // ストレージ画像は General でしか読み書きできない(VulkanImage.cs の説明)。
            ImageLayout = ImageLayout.General,
        };
        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = _spheres.Handle,
            Offset = 0,
            Range = Vk.WholeSize,
        };

        var writes = stackalloc WriteDescriptorSet[3];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageImage,
            PImageInfo = &imageInfo,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &bufferInfo,
        };

        // TLAS だけ書き方が違う(Day 62b)。**WriteDescriptorSet に加速構造を入れる場所が無い**ので、
        // 専用の構造体を PNext につないで渡す。DescriptorCount は WriteDescriptorSet 側にも要る。
        AccelerationStructureKHR tlas = _acceleration?.Handle ?? default;
        var tlasWrite = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &tlas,
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            PNext = &tlasWrite,
            DstSet = _set,
            DstBinding = 2,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.AccelerationStructureKhr,
        };

        // ここで初めて「0 番はこの画像、1 番はこのバッファ、2 番はこの TLAS」が確定する。
        // 一度書いたら、毎フレーム挿し直す必要は無い(中身が変わっても挿し直しは不要)。
        vk.UpdateDescriptorSets(device.Handle, bindingCount, writes, 0, null);

        // ---- 4. パイプラインレイアウト(シェーダとの契約)------------------------------
        var pushRange = new PushConstantRange
        {
            // プッシュ定数も同じで、**触る段を全部挙げる**(Day 62c)。
            StageFlags = AllStages,
            Offset = 0,
            Size = (uint)Unsafe.SizeOf<PushConstants>(),
        };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        VulkanDevice.Check(vk.CreatePipelineLayout(device.Handle, &pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        // ---- 5. パイプライン(Day 62b で2本になった)------------------------------------
        // **両方を起動時に作っておく**。切り替えは CmdBindPipeline に渡すハンドルが変わるだけで、
        // 作り直しは起きない——これが「準備を前に寄せる」設計の利き方。
        (_brutePipeline, _bruteModule) = CreatePipeline(device, _pipelineLayout, bruteSpirv);
        if (rayQuerySpirv is not null && useAcceleration)
        {
            (_rayQueryPipeline, _rayQueryModule) = CreatePipeline(device, _pipelineLayout, rayQuerySpirv);

            // ---- 5-2. レイトレーシングパイプライン(Day 62c)-----------------------------
            // **同じパイプラインレイアウトを使い回す**のが効く。ディスクリプタもプッシュ定数も
            // コンピュート版とまったく同じ並びなので、束ね直すだけで両方から使える。
            if (device.RayTracingPipelineSupported)
            {
                _rayTracingPipeline = RayTracingPipeline.Create(device, compiler, _pipelineLayout, shaderDirectory);
            }
        }

        // ---- 6. 毎フレーム使い回すコマンドバッファとフェンス --------------------------
        // VulkanDevice の使い回し用(SubmitAndWait)とは別のプールにしてある。
        // 同じプールから切り出したコマンドバッファを同時に触るのは禁止なので、
        // 「準備用」と「毎フレーム用」を混ぜないほうが事故が少ない。
        _ownPool = CreatePool(device);
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _ownPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        VulkanDevice.Check(vk.AllocateCommandBuffers(device.Handle, &allocInfo, out _commands), "vkAllocateCommandBuffers");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        VulkanDevice.Check(vk.CreateFence(device.Handle, &fenceInfo, null, out _fence), "vkCreateFence");

        var queryInfo = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = 2,
        };
        VulkanDevice.Check(vk.CreateQueryPool(device.Handle, &queryInfo, null, out _queryPool), "vkCreateQueryPool");
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>ray query 版のパイプラインを持っているか。</summary>
    public bool RayQueryAvailable => _rayQueryPipeline.Handle != 0;

    /// <summary>レイトレーシングパイプラインを持っているか(Day 62c)。</summary>
    public bool PipelineAvailable => _rayTracingPipeline is not null;

    /// <summary>SBT のバイト数。HUD に出す——<b>驚くほど小さい</b>ことを見るため。</summary>
    public ulong ShaderBindingTableBytes => _rayTracingPipeline?.Table.SizeBytes ?? 0;

    /// <summary>加速構造(HUD に大きさと構築時間を出すため)。持っていなければ null。</summary>
    public AccelerationStructure? Acceleration => _acceleration;

    /// <summary>
    /// 直前の1フレームを CPU 側から見た時間(ms)。<b>引き取りの転送と提出の往復を含む</b>ので、
    /// 光線を追う時間そのものではない。
    /// </summary>
    public double LastFrameMilliseconds { get; private set; }

    /// <summary>
    /// 直前の1フレームで<b>ディスパッチだけ</b>にかかった時間(ms)。GPU の時計で測ったもの。
    /// 球を増やしたときに伸びるのはこちら(<see cref="LastFrameMilliseconds"/> はほとんど動かない)。
    /// </summary>
    public double LastTraceMilliseconds { get; private set; }

    /// <summary>
    /// 1枚描いて <paramref name="destination"/> に受け取る。
    ///
    /// <para>
    /// 積むコマンドは4つだけ。<b>バインド → プッシュ定数 → ディスパッチ → 引き取り</b>。
    /// Vulkan の長さはここまでの準備にあって、<b>毎フレームの仕事は短い</b>——
    /// これが「準備を前に寄せる」という設計思想の見える形。
    /// </para>
    /// </summary>
    /// <param name="trace">
    /// どの探し方を使うか(Day 62c で3つになった)。
    /// 持っていないものを指定したときは黙って総当たりに落ちる。
    /// </param>
    public void Render(Camera camera, int mode, TraceMode trace, int[] destination)
    {
        var clock = Stopwatch.StartNew();
        Vk vk = _device.Api;

        var push = new PushConstants
        {
            PositionHalfWidth = new Vector4(camera.Position, camera.HalfWidth),
            RightHalfHeight = new Vector4(camera.Right, camera.HalfHeight),
            Up = new Vector4(camera.Up, 0.0f),
            Forward = new Vector4(camera.Forward, 0.0f),
            Width = Width,
            Height = Height,
            SphereCount = _sphereCount,
            Mode = mode,
            DiffuseCount = _diffuseCount,
        };

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanDevice.Check(vk.ResetCommandBuffer(_commands, 0), "vkResetCommandBuffer");
        VulkanDevice.Check(vk.BeginCommandBuffer(_commands, &begin), "vkBeginCommandBuffer");

        // 計測の目盛りは**使う前に必ず reset する**。前のフレームの値が残ったまま読むと、
        // 「まだ書かれていない」と「前の値」の区別が付かない。
        vk.CmdResetQueryPool(_commands, _queryPool, 0, 2);
        vk.CmdWriteTimestamp(_commands, PipelineStageFlags.TopOfPipeBit, _queryPool, 0);

        // 持っていないものを頼まれたら総当たりへ落とす。
        TraceMode actual = trace switch
        {
            TraceMode.RayQuery when !RayQueryAvailable => TraceMode.BruteForce,
            TraceMode.Pipeline when !PipelineAvailable => TraceMode.BruteForce,
            _ => trace,
        };

        // **バインドする先(bind point)が違う**のが Day 62c の目に見える差。
        // レイトレーシングパイプラインは Compute とは別の口に刺さるので、
        // ディスクリプタセットもプッシュ定数もその口へ流し直す必要がある。
        PipelineBindPoint bindPoint = actual == TraceMode.Pipeline
            ? PipelineBindPoint.RayTracingKhr
            : PipelineBindPoint.Compute;

        if (actual == TraceMode.Pipeline)
        {
            vk.CmdBindPipeline(_commands, bindPoint, _rayTracingPipeline!.Handle);
        }
        else
        {
            vk.CmdBindPipeline(_commands, bindPoint, actual == TraceMode.RayQuery ? _rayQueryPipeline : _brutePipeline);
        }

        DescriptorSet set = _set;
        vk.CmdBindDescriptorSets(_commands, bindPoint, _pipelineLayout, 0, 1, &set, 0, null);

        vk.CmdPushConstants(_commands, _pipelineLayout, AllPushStages, 0, (uint)Unsafe.SizeOf<PushConstants>(), &push);

        if (actual == TraceMode.Pipeline)
        {
            // **ワークグループの大きさを渡さない**。束ね方はドライバが決める(要点5)。
            _rayTracingPipeline!.RecordTraceRays(_commands, (uint)Width, (uint)Height);
        }
        else
        {
            // 渡すのは**ワークグループの個数**であって呼び出しの個数ではない(OpenGL と同じ)。
            // 割り切れないぶんは切り上げて、はみ出した呼び出しはシェーダ側で捨てる。
            vk.CmdDispatch(
                _commands,
                (uint)((Width + WorkGroupSize - 1) / WorkGroupSize),
                (uint)((Height + WorkGroupSize - 1) / WorkGroupSize),
                1);
        }

        // ディスパッチが**終わってから**の時刻。BottomOfPipe は「この時点までのコマンドが全部済んだら」の意味。
        vk.CmdWriteTimestamp(_commands, PipelineStageFlags.BottomOfPipeBit, _queryPool, 1);

        // 書いた段を正しく伝える(Day 62c)。レイトレーシングパイプラインで書いた画像を
        // ComputeShaderBit として待つと、**待ちが成立しない**。
        _image.RecordCopyToBuffer(
            _commands, _readback,
            actual == TraceMode.Pipeline
                ? PipelineStageFlags.RayTracingShaderBitKhr
                : PipelineStageFlags.ComputeShaderBit);

        VulkanDevice.Check(vk.EndCommandBuffer(_commands), "vkEndCommandBuffer");

        CommandBuffer commands = _commands;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commands,
        };

        Fence fence = _fence;
        VulkanDevice.Check(vk.ResetFences(_device.Handle, 1, &fence), "vkResetFences");
        VulkanDevice.Check(vk.QueueSubmit(_device.Queue, 1, &submit, fence), "vkQueueSubmit");

        // ここで待つ。**待たずに読むと前のフレームの絵が混ざる**(そして検証レイヤも黙っている
        // ——メモリの読み書きとしては合法なので)。Day 61 の glFinish と同じ役目だが、
        // フェンスは「どの提出を待つか」を選べるぶん細かい。
        VulkanDevice.Check(vk.WaitForFences(_device.Handle, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");

        // 目盛りを読む。フェンスを待った後なので、必ず両方書かれている。
        ulong* timestamps = stackalloc ulong[2];
        VulkanDevice.Check(
            vk.GetQueryPoolResults(_device.Handle, _queryPool, 0, 2, sizeof(ulong) * 2, timestamps, sizeof(ulong), QueryResultFlags.Result64Bit),
            "vkGetQueryPoolResults");
        LastTraceMilliseconds = (timestamps[1] - timestamps[0]) * _device.TimestampPeriod / 1_000_000.0;

        _readback.Read(destination);
        LastFrameMilliseconds = clock.Elapsed.TotalMilliseconds;
    }

    public void Dispose()
    {
        Vk vk = _device.Api;
        vk.DeviceWaitIdle(_device.Handle);

        vk.DestroyQueryPool(_device.Handle, _queryPool, null);
        vk.DestroyFence(_device.Handle, _fence, null);
        vk.DestroyCommandPool(_device.Handle, _ownPool, null);

        _rayTracingPipeline?.Dispose();

        if (RayQueryAvailable)
        {
            vk.DestroyPipeline(_device.Handle, _rayQueryPipeline, null);
            vk.DestroyShaderModule(_device.Handle, _rayQueryModule, null);
        }

        vk.DestroyPipeline(_device.Handle, _brutePipeline, null);
        vk.DestroyShaderModule(_device.Handle, _bruteModule, null);
        vk.DestroyPipelineLayout(_device.Handle, _pipelineLayout, null);

        // セットはプールごと壊れるので、個別に解放しなくてよい。
        vk.DestroyDescriptorPool(_device.Handle, _pool, null);
        vk.DestroyDescriptorSetLayout(_device.Handle, _setLayout, null);

        _acceleration?.Dispose();
        _spheres.Dispose();
        _readback.Dispose();
        _image.Dispose();
    }

    /// <summary>
    /// SPIR-V からコンピュートパイプラインを1本作る。
    ///
    /// <para>
    /// シェーダモジュールは SPIR-V をそのまま包んだだけの物で、<b>この時点ではまだ機械語ではない</b>。
    /// 実際の翻訳は <c>vkCreateComputePipelines</c> で起きる(だからここが起動時いちばん重い)。
    /// </para>
    /// </summary>
    private static (Pipeline Pipeline, ShaderModule Module) CreatePipeline(
        VulkanDevice device, PipelineLayout layout, uint[] spirv)
    {
        Vk vk = device.Api;

        ShaderModule module;
        fixed (uint* pCode = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(spirv.Length * sizeof(uint)),
                PCode = pCode,
            };
            VulkanDevice.Check(vk.CreateShaderModule(device.Handle, &moduleInfo, null, out module), "vkCreateShaderModule");
        }

        byte* entryPoint = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,

                // SPIR-V は1つのモジュールに複数の入口を持てる。どれを使うか名前で指す。
                PName = entryPoint,
            };

            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = layout,
            };
            VulkanDevice.Check(
                vk.CreateComputePipelines(device.Handle, default, 1, &pipelineInfo, null, out Pipeline pipeline),
                "vkCreateComputePipelines");
            return (pipeline, module);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entryPoint);
        }
    }

    private static CommandPool CreatePool(VulkanDevice device)
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = device.QueueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        VulkanDevice.Check(device.Api.CreateCommandPool(device.Handle, &poolInfo, null, out CommandPool pool), "vkCreateCommandPool");
        return pool;
    }
}
