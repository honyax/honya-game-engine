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

    /// <summary>表示の種類。0 = 陰影、1 = 法線、2 = 交差判定の回数。</summary>
    public int Mode;
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
/// </summary>
internal sealed unsafe class ComputeRenderer : IDisposable
{
    /// <summary>
    /// ワークグループの大きさ。<b>GLSL 側の <c>local_size_x/y</c> と必ず一致させる</b>。
    /// 8x8 = 64 呼び出しは NVIDIA のワープ(32)の倍数で、AMD の wave32/64 にも都合がよい。
    /// </summary>
    private const int WorkGroupSize = 8;

    private readonly VulkanDevice _device;
    private readonly VulkanImage _image;

    /// <summary>画像を引き取る先。CPU から map できる置き場所(要点5)。</summary>
    private readonly VulkanBuffer _readback;

    private readonly VulkanBuffer _spheres;
    private readonly int _sphereCount;

    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;
    private readonly ShaderModule _module;

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

    public ComputeRenderer(VulkanDevice device, int width, int height, GpuSphere[] spheres, uint[] spirv)
    {
        _device = device;
        Width = width;
        Height = height;
        _sphereCount = spheres.Length;

        Vk vk = device.Api;

        _image = VulkanImage.Create(device, width, height);
        _readback = VulkanBuffer.CreateStaging(device, (ulong)(width * height * sizeof(int)), BufferUsageFlags.TransferDstBit);
        _spheres = VulkanBuffer.CreateDeviceLocal<GpuSphere>(device, spheres, BufferUsageFlags.StorageBufferBit);

        // ---- 1. レイアウト(型の宣言)------------------------------------------------
        // binding の番号は GLSL の layout(binding = N) と一致させる。
        // StageFlags で「どの段から見えるか」を絞る。今日は計算しかないので ComputeBit だけ。
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.ComputeBit,
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        VulkanDevice.Check(vk.CreateDescriptorSetLayout(device.Handle, &layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout");

        // ---- 2. プール(切り出す元)----------------------------------------------------
        // 「画像1つ、バッファ1つ、セットは1個まで」と先に申告する。
        // 足りなくなっても伸びない(ErrorOutOfPoolMemory が返る)。
        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = 1 };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 1 };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
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

        var writes = stackalloc WriteDescriptorSet[2];
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

        // ここで初めて「0 番はこの画像、1 番はこのバッファ」が確定する。
        // 一度書いたら、毎フレーム挿し直す必要は無い(中身が変わっても挿し直しは不要)。
        vk.UpdateDescriptorSets(device.Handle, 2, writes, 0, null);

        // ---- 4. パイプラインレイアウト(シェーダとの契約)------------------------------
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.ComputeBit,
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

        // ---- 5. パイプライン ------------------------------------------------------------
        // シェーダモジュールは SPIR-V をそのまま包んだだけの物。**この時点ではまだ機械語ではない**。
        // 実際の翻訳は vkCreateComputePipelines で起きる(だからここが起動時いちばん重い)。
        fixed (uint* pCode = spirv)
        {
            var moduleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(spirv.Length * sizeof(uint)),
                PCode = pCode,
            };
            VulkanDevice.Check(vk.CreateShaderModule(device.Handle, &moduleInfo, null, out _module), "vkCreateShaderModule");
        }

        byte* entryPoint = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = _module,

                // SPIR-V は1つのモジュールに複数の入口を持てる。どれを使うか名前で指す。
                PName = entryPoint,
            };

            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _pipelineLayout,
            };
            VulkanDevice.Check(
                vk.CreateComputePipelines(device.Handle, default, 1, &pipelineInfo, null, out _pipeline),
                "vkCreateComputePipelines");
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entryPoint);
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
    public void Render(Camera camera, int mode, int[] destination)
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

        vk.CmdBindPipeline(_commands, PipelineBindPoint.Compute, _pipeline);

        DescriptorSet set = _set;
        vk.CmdBindDescriptorSets(_commands, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);

        vk.CmdPushConstants(_commands, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)Unsafe.SizeOf<PushConstants>(), &push);

        // 渡すのは**ワークグループの個数**であって呼び出しの個数ではない(OpenGL と同じ)。
        // 割り切れないぶんは切り上げて、はみ出した呼び出しはシェーダ側で捨てる。
        vk.CmdDispatch(
            _commands,
            (uint)((Width + WorkGroupSize - 1) / WorkGroupSize),
            (uint)((Height + WorkGroupSize - 1) / WorkGroupSize),
            1);

        // ディスパッチが**終わってから**の時刻。BottomOfPipe は「この時点までのコマンドが全部済んだら」の意味。
        vk.CmdWriteTimestamp(_commands, PipelineStageFlags.BottomOfPipeBit, _queryPool, 1);

        _image.RecordCopyToBuffer(_commands, _readback);

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
        vk.DestroyPipeline(_device.Handle, _pipeline, null);
        vk.DestroyShaderModule(_device.Handle, _module, null);
        vk.DestroyPipelineLayout(_device.Handle, _pipelineLayout, null);

        // セットはプールごと壊れるので、個別に解放しなくてよい。
        vk.DestroyDescriptorPool(_device.Handle, _pool, null);
        vk.DestroyDescriptorSetLayout(_device.Handle, _setLayout, null);

        _spheres.Dispose();
        _readback.Dispose();
        _image.Dispose();
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
