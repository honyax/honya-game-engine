using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace MeshletRenderer;

/// <summary>
/// Vulkan の入り口。<b>画面に一切関わらずに GPU を1台用意する</b>(Day 62a の要点1)。
///
/// <para>
/// Day 62c の同名のクラスから持ってきた。5段(インスタンス → 物理デバイス → キューファミリ →
/// 論理デバイス → コマンドプール)の骨組みは同じで、<b>差し替えたのは3か所だけ</b>。
/// </para>
/// <list type="number">
/// <item><b>版を 1.2 から 1.3 に上げた</b>。dynamic rendering(描画パスを作らずに描き始める仕組み)が
/// 1.3 のコアに入ったので、拡張として引かずに使える</item>
/// <item><b>キューを「計算」から「描画」に替えた</b>。今日はラスタライズするので、
/// Graphics の立った受け口が要る</item>
/// <item><b>拡張をレイトレーシングの4つから <c>VK_EXT_mesh_shader</c> 1つに替えた</b>。
/// 機能の鎖の先頭も入れ替わる</item>
/// </list>
/// <para>
/// 「聞いてから使う」の作法も Day 62b のまま。メッシュシェーダを持たない GPU
/// (NVIDIA なら GTX 1000 系以前)では <see cref="MeshShaderSupported"/> が false になり、
/// 頂点シェーダの道だけで動き続ける。
/// </para>
/// <para>
/// <b>Day 64b の差分</b>は機能を1つ(<c>taskShader</c>)立てることと、タスクシェーダの中で
/// <b>subgroup の投票(ballot)</b>が使えるかを聞くこと(<see cref="TaskShaderSupported"/>)。
/// </para>
/// </summary>
internal sealed unsafe class VulkanDevice : IDisposable
{
    /// <summary>
    /// 検証レイヤの名前。<b>Vulkan SDK を入れると使えるようになる</b>(入っていなければ黙って諦める)。
    ///
    /// <para>
    /// Vulkan の API は引数の正しさをほとんど確かめない。構造体の <c>SType</c> を1つ書き忘れると、
    /// エラーではなく<b>未定義動作</b>(たいてい画面が真っ黒か、ドライバごと落ちる)。
    /// 検証レイヤは API 呼び出しの間に割り込んで「その使い方は仕様違反」と教えてくれる層で、
    /// <b>Vulkan を書くときの実質的なコンパイラ</b>にあたる。
    /// </para>
    /// <para>
    /// ここでは「あれば使う」にしてある。無くても今日のコードは動くが、自分で書き換えて詰まったら
    /// LunarG の Vulkan SDK(https://vulkan.lunarg.com/sdk/home)を入れると何が悪いか1行で出る。
    /// 入っているかどうかは起動時の HUD に出る。
    /// </para>
    /// </summary>
    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";

    private readonly ExtDebugUtils? _debugUtils;
    private readonly DebugUtilsMessengerEXT _messenger;

    /// <summary>検証レイヤからの通知を受ける関数。GC に回収されると呼ばれた瞬間に落ちるので、参照を保持する。</summary>
    private readonly DebugUtilsMessengerCallbackFunctionEXT? _callbackKeepAlive;

    private readonly CommandPool _commandPool;

    /// <summary>メッシュシェーダの API(今日の主役)。対応していない GPU では null。</summary>
    private readonly ExtMeshShader? _meshShaderApi;

    /// <summary>一時的なコマンド(起動時の転送やレイアウトの移し替え)を積むための使い回しのコマンドバッファ。</summary>
    private readonly CommandBuffer _oneShot;

    private readonly Fence _oneShotFence;

    private VulkanDevice(
        Vk vk, Instance instance, PhysicalDevice physicalDevice, Device device,
        uint queueFamily, Queue queue, CommandPool commandPool,
        CommandBuffer oneShot, Fence oneShotFence,
        ExtDebugUtils? debugUtils, DebugUtilsMessengerEXT messenger,
        DebugUtilsMessengerCallbackFunctionEXT? callbackKeepAlive,
        ExtMeshShader? meshShaderApi, bool taskShader,
        string name, string driver, bool validation)
    {
        TaskShaderSupported = meshShaderApi is not null && taskShader;
        _meshShaderApi = meshShaderApi;
        Api = vk;
        Instance = instance;
        PhysicalDevice = physicalDevice;
        Handle = device;
        QueueFamilyIndex = queueFamily;
        Queue = queue;
        _commandPool = commandPool;
        _oneShot = oneShot;
        _oneShotFence = oneShotFence;
        _debugUtils = debugUtils;
        _messenger = messenger;
        _callbackKeepAlive = callbackKeepAlive;
        Name = name;
        Driver = driver;
        ValidationEnabled = validation;

        MemoryProperties = vk.GetPhysicalDeviceMemoryProperties(physicalDevice);

        PhysicalDeviceProperties props = vk.GetPhysicalDeviceProperties(physicalDevice);
        TimestampPeriod = props.Limits.TimestampPeriod;

        if (meshShaderApi is not null)
        {
            // メッシュシェーダの上限は、ふつうの Properties ではなく**拡張の Properties**にある。
            // PNext につないで Properties2 で聞くのが Vulkan の一般形(Day 62b と同じ)。
            var meshProps = new PhysicalDeviceMeshShaderPropertiesEXT
            {
                SType = StructureType.PhysicalDeviceMeshShaderPropertiesExt,
            };
            var props2 = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &meshProps,
            };
            vk.GetPhysicalDeviceProperties2(physicalDevice, &props2);

            MaxMeshOutputVertices = meshProps.MaxMeshOutputVertices;
            MaxMeshOutputPrimitives = meshProps.MaxMeshOutputPrimitives;
            MaxPreferredMeshWorkGroupInvocations = meshProps.MaxPreferredMeshWorkGroupInvocations;
            MaxPreferredTaskWorkGroupInvocations = meshProps.MaxPreferredTaskWorkGroupInvocations;
        }
    }

    public Vk Api { get; }

    public Instance Instance { get; }

    public PhysicalDevice PhysicalDevice { get; }

    /// <summary>論理デバイス。以降ほとんどの呼び出しの第1引数になる。</summary>
    public Device Handle { get; }

    public uint QueueFamilyIndex { get; }

    /// <summary>仕事を投げる口。<b>1本しか作っていない</b>ので、submit は1スレッドからだけ行う。</summary>
    public Queue Queue { get; }

    public PhysicalDeviceMemoryProperties MemoryProperties { get; }

    public string Name { get; }

    public string Driver { get; }

    public bool ValidationEnabled { get; }

    /// <summary>
    /// タイムスタンプ1目盛りのナノ秒(Day 62a の要点6)。
    ///
    /// <para>
    /// GPU が刻むクロックの粒度は機種ごとに違う。<c>vkCmdWriteTimestamp</c> が書き込むのは
    /// <b>その GPU 固有の目盛りの数</b>なので、ナノ秒に直すにはこの係数を掛ける
    /// (NVIDIA はふつう 1.0、AMD は 40 前後)。
    /// </para>
    /// </summary>
    public float TimestampPeriod { get; }

    /// <summary>
    /// メッシュシェーダが使えるか。false なら頂点シェーダの道だけで動く。
    ///
    /// <para>
    /// メッシュシェーダも<b>GPU の世代で決まる</b>(NVIDIA RTX 2000 系以降 / AMD RX 6000 系以降 /
    /// Intel Arc)。ハードウェア RT とほぼ同じ線引きなのは偶然ではなく、どちらも同じ世代で入った。
    /// 使えない GPU では拡張が一覧に出てこないので、<b>宣言する前に聞く</b>(Day 62b と同じ)。
    /// </para>
    /// </summary>
    [MemberNotNullWhen(true, nameof(MeshShaderApi))]
    public bool MeshShaderSupported => _meshShaderApi is not null;

    /// <summary>メッシュシェーダの API。<see cref="MeshShaderSupported"/> が true のときだけ使える。</summary>
    public ExtMeshShader? MeshShaderApi => _meshShaderApi;

    /// <summary>
    /// メッシュシェーダ1回(1ワークグループ)が出せる頂点の数の上限(手元の RTX 3070 は 256)。
    /// シェーダ側の <c>max_vertices</c> はこれを超えてはいけない。
    /// </summary>
    public uint MaxMeshOutputVertices { get; }

    /// <summary>同じく三角形の数の上限(手元の RTX 3070 は 256)。</summary>
    public uint MaxMeshOutputPrimitives { get; }

    /// <summary>
    /// <b>「このくらいの人数で組むと速い」という GPU からの推奨</b>(手元の RTX 3070 は 32)。
    ///
    /// <para>
    /// 上限(<c>maxMeshWorkGroupInvocations</c> = 128)とは別に、推奨値が用意されているのが
    /// メッシュシェーダらしいところ。NVIDIA は 32(ワープ1つ)を返す。
    /// 今日の <c>local_size_x = 32</c> はこの数に合わせてある(要点4)。
    /// </para>
    /// </summary>
    public uint MaxPreferredMeshWorkGroupInvocations { get; }

    /// <summary>タスクシェーダの推奨の人数(Day 64b で追加。手元の RTX 3070 は 32)。</summary>
    public uint MaxPreferredTaskWorkGroupInvocations { get; }

    /// <summary>
    /// タスクシェーダの道が使えるか(Day 64b で追加)。
    ///
    /// <para>
    /// メッシュシェーダがあるだけでは足りず、<b>タスクシェーダの中で subgroup の投票(ballot)が使えて、
    /// subgroup が 32 人以上</b>であることを求める。今日のタスクシェーダは「32 人のうち誰が生き残ったか」を
    /// 投票で数えて席を詰める(要点4)ので、32 人が1つの subgroup に収まっている必要がある。
    /// NVIDIA は 32、AMD は 32 か 64 なので満たす。subgroup が 8 や 16 の GPU では false になり、タスクの道だけ使えない。
    /// </para>
    /// </summary>
    public bool TaskShaderSupported { get; }

    /// <summary>
    /// GPU を用意する。使えない環境では <c>null</c> を返し、理由を <paramref name="message"/> に入れる。
    /// <b>例外で落とさない</b>——Vulkan が無い環境(古いドライバ、一部のリモートデスクトップ)でも
    /// 「なぜ駄目か」を画面に出せるようにしておきたい。
    /// </summary>
    public static VulkanDevice? TryCreate(out string message)
    {
        Vk vk;
        try
        {
            vk = Vk.GetApi();
        }
        catch (Exception e)
        {
            message = $"Vulkan のローダが見つかりません: {e.Message}";
            return null;
        }

        try
        {
            return Create(vk, out message);
        }
        catch (Exception e)
        {
            message = $"Vulkan の初期化に失敗しました: {e.Message}";
            return null;
        }
    }

    private static VulkanDevice? Create(Vk vk, out string message)
    {
        // ---- 1. インスタンス ----------------------------------------------------------
        // ApplicationInfo の ApiVersion は「自分がどの版の規則で書いたか」の宣言。
        // **1.3 にする**。dynamic rendering(要点5)が 1.3 のコアに入ったので、
        // 描画パスとフレームバッファのオブジェクトを作らずに描き始められる。
        byte* appName = (byte*)Marshal.StringToHGlobalAnsi("Day64 MeshletRenderer");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            PEngineName = appName,
            ApplicationVersion = new Version32(1, 0, 0),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version13,
        };

        bool useValidation = HasInstanceLayer(vk, ValidationLayerName);

        // 検証レイヤが無いなら debug_utils を有効にしても何も飛んでこないので、まとめて off にする。
        var instanceExtensions = new List<string>();
        if (useValidation)
        {
            instanceExtensions.Add(ExtDebugUtils.ExtensionName);
        }

        nint extPtr = SilkMarshal.StringArrayToPtr(instanceExtensions);
        nint layerPtr = SilkMarshal.StringArrayToPtr(useValidation ? new[] { ValidationLayerName } : Array.Empty<string>());
        Instance instance;
        try
        {
            var instanceInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = (uint)instanceExtensions.Count,
                PpEnabledExtensionNames = (byte**)extPtr,
                EnabledLayerCount = useValidation ? 1u : 0u,
                PpEnabledLayerNames = (byte**)layerPtr,
            };

            Check(vk.CreateInstance(&instanceInfo, null, out instance), "vkCreateInstance");
        }
        finally
        {
            SilkMarshal.Free(extPtr);
            SilkMarshal.Free(layerPtr);
            Marshal.FreeHGlobal((nint)appName);
        }

        // Silk.NET に「以降の拡張はこのインスタンスから引く」と教える。
        vk.CurrentInstance = instance;

        // ---- 2. 検証レイヤの通知先 ----------------------------------------------------
        ExtDebugUtils? debugUtils = null;
        DebugUtilsMessengerEXT messenger = default;
        DebugUtilsMessengerCallbackFunctionEXT? callback = null;
        if (useValidation && vk.TryGetInstanceExtension(instance, out ExtDebugUtils utils))
        {
            debugUtils = utils;
            callback = DebugCallback;
            var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                                | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                            | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                            | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(callback),
            };
            utils.CreateDebugUtilsMessenger(instance, &messengerInfo, null, out messenger);
        }

        // ---- 3. 物理デバイスを選ぶ ----------------------------------------------------
        PhysicalDevice physical = PickPhysicalDevice(vk, instance, out string name, out string driver);
        if (physical.Handle == 0)
        {
            vk.DestroyInstance(instance, null);
            message = "描画キューを持つ GPU が見つかりませんでした。";
            return null;
        }

        // ---- 4. キューファミリを選ぶ --------------------------------------------------
        // **今日は描画(Graphics)の口が要る**。Day 62 は計算しかしなかったので Compute の口で足りたが、
        // ラスタライザを動かせるのは Graphics の立った口だけ。
        // (Graphics の口は計算も転送も必ずできる、と仕様で決まっている。)
        uint queueFamily = FindGraphicsQueueFamily(vk, physical);

        // ---- 5. 論理デバイス ----------------------------------------------------------
        float priority = 1.0f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        // ---- 5-1. メッシュシェーダが使えるか聞く --------------------------------------
        // **宣言する前に聞く**(Day 62b と同じ)。持っていない拡張を宣言すると
        // vkCreateDevice が ErrorExtensionNotPresent を返して、そこで終わってしまう。
        bool meshShader = HasDeviceExtensions(vk, physical, [ExtMeshShader.ExtensionName]);

        // ---- 5-1b. タスクシェーダの中で subgroup の投票が使えるか聞く(Day 64b で追加)----
        // subgroup の性質は 1.1 の Properties にまとまっている。段(どこで使えるか)と
        // 操作(何が使えるか)と人数を見る。
        var properties11 = new PhysicalDeviceVulkan11Properties
        {
            SType = StructureType.PhysicalDeviceVulkan11Properties,
        };
        var properties2 = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &properties11,
        };
        vk.GetPhysicalDeviceProperties2(physical, &properties2);
        bool taskShader = meshShader
            && (properties11.SubgroupSupportedStages & ShaderStageFlags.TaskBitExt) != 0
            && (properties11.SubgroupSupportedOperations & SubgroupFeatureFlags.BallotBit) != 0
            && properties11.SubgroupSize >= 32;

        // ---- 5-2. 機能の鎖を組む ------------------------------------------------------
        // 使う機能は**明示的に有効化する**のが Vulkan の流儀(Day 62b の要点)。
        // 鎖は meshShader -> Vulkan13 -> Features2 -> DeviceCreateInfo の順につなぐ。
        var meshFeatures = new PhysicalDeviceMeshShaderFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
            MeshShader = true,

            // Day 64b で追加。メッシュシェーダの手前にタスクシェーダを置けるようにする。
            TaskShader = true,

            // 「メッシュシェーダが出した三角形の数」を数えるクエリに要る(Day 64a の要点6)。
            // これを立てずに MeshPrimitivesGeneratedExt のクエリを作ると仕様違反。
            MeshShaderQueries = true,
        };

        var features13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            PNext = meshShader ? &meshFeatures : null,

            // 描画パス(VkRenderPass)とフレームバッファを作らずに描き始める(要点5)。
            DynamicRendering = true,
        };

        // Vulkan 1.0 からある機能は、PhysicalDeviceFeatures2 の中の Features に書く。
        // 鎖を使うときは DeviceCreateInfo.PEnabledFeatures を null にして、こちらで渡すのが決まり。
        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features13,
            Features = new PhysicalDeviceFeatures
            {
                // パイプライン統計(何回・何枚)を数える(要点6)。
                PipelineStatisticsQuery = true,

                // **画素シェーダで gl_PrimitiveID を読むのに要る**。SPIR-V では PrimitiveId が
                // Geometry の能力に属していて、ジオメトリシェーダを使わなくてもこの機能の宣言が要る
                // (Day 63a の GS が「三角形の番号」を持っていたことの名残り)。
                GeometryShader = true,
            },
        };

        string[] deviceExtensions = meshShader ? [ExtMeshShader.ExtensionName] : [];
        nint deviceExtPtr = SilkMarshal.StringArrayToPtr(deviceExtensions);
        Device device;
        try
        {
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features2,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = (uint)deviceExtensions.Length,
                PpEnabledExtensionNames = (byte**)deviceExtPtr,
            };

            Check(vk.CreateDevice(physical, &deviceInfo, null, out device), "vkCreateDevice");
        }
        finally
        {
            SilkMarshal.Free(deviceExtPtr);
        }

        vk.CurrentDevice = device;

        // 拡張の関数ポインタを引く。デバイスを作った後でないと引けない。
        ExtMeshShader? meshApi = null;
        if (meshShader && vk.TryGetDeviceExtension(instance, device, out ExtMeshShader loaded))
        {
            meshApi = loaded;
        }

        vk.GetDeviceQueue(device, queueFamily, 0, out Queue queue);

        // ---- 6. コマンドプールと使い回しのコマンドバッファ ----------------------------
        // ResetCommandBuffer を立てておくと、1本のコマンドバッファを毎フレーム録り直せる。
        // 立てないと、Begin のたびにプールごと reset しなければならない。
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        Check(vk.CreateCommandPool(device, &poolInfo, null, out CommandPool pool), "vkCreateCommandPool");

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Check(vk.AllocateCommandBuffers(device, &allocInfo, out CommandBuffer oneShot), "vkAllocateCommandBuffers");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(vk.CreateFence(device, &fenceInfo, null, out Fence fence), "vkCreateFence");

        message = $"{name} / driver {driver} / 検証レイヤ {(useValidation ? "あり" : "なし")}"
            + $" / メッシュシェーダ {(meshApi is not null ? "あり" : "なし")}"
            + $" / タスクシェーダ {(meshApi is not null && taskShader ? "あり" : "なし")}";
        return new VulkanDevice(
            vk, instance, physical, device, queueFamily, queue, pool, oneShot, fence,
            debugUtils, messenger, callback, meshApi, taskShader, name, driver, useValidation);
    }

    /// <summary>
    /// コマンドを積んで、<b>投げて、終わるまで待つ</b>(Day 62a の要点4)。
    ///
    /// <para>
    /// 毎回待つのは効率がよくないが、今日の使い道(起動時の転送、レイアウトの移し替え)では
    /// 分かりやすさのほうが大事。<b>Vulkan では「投げた仕事がいつ終わったか」を自分で見る義務がある</b>
    /// ——OpenGL の <c>glFinish</c> が暗黙にやっていたことを、フェンスという形で明示する。
    /// </para>
    /// </summary>
    public void SubmitAndWait(Action<CommandBuffer> record)
    {
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };

        Check(Api.ResetCommandBuffer(_oneShot, 0), "vkResetCommandBuffer");
        Check(Api.BeginCommandBuffer(_oneShot, &begin), "vkBeginCommandBuffer");
        record(_oneShot);
        Check(Api.EndCommandBuffer(_oneShot), "vkEndCommandBuffer");

        CommandBuffer buffer = _oneShot;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &buffer,
        };

        Fence fence = _oneShotFence;
        Check(Api.ResetFences(Handle, 1, &fence), "vkResetFences");
        Check(Api.QueueSubmit(Queue, 1, &submit, fence), "vkQueueSubmit");
        Check(Api.WaitForFences(Handle, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
    }

    /// <summary>
    /// 欲しい性質を満たすメモリの種類を探す(Day 62a の要点3)。
    ///
    /// <para>
    /// OpenGL は <c>GL_STATIC_DRAW</c> のような<b>ヒント</b>を渡すだけで、置き場所はドライバが決めた。
    /// Vulkan は GPU が持つメモリの種類を全部見せてくるので、<b>こちらが選ぶ</b>。
    /// </para>
    /// <list type="bullet">
    /// <item><b>DeviceLocal</b> … GPU の直近(VRAM)。GPU から読むのは速いが、CPU からは触れないことが多い</item>
    /// <item><b>HostVisible</b> … CPU から map して書ける。GPU からは PCIe 越しになるので遅い</item>
    /// </list>
    /// <para>
    /// 両方立っている種類(ReBAR / Smart Access Memory)があればそれが理想だが、
    /// 大きさが限られるので、ふつうは「HostVisible に書いて DeviceLocal へ転送」の2段を踏む。
    /// </para>
    /// </summary>
    /// <param name="typeBits">
    /// <c>vkGetBufferMemoryRequirements</c> が返すビット列。<b>「この資源はこの種類のメモリになら置ける」</b>
    /// という GPU 側の制約で、ここに入っていない種類を選ぶと確保に失敗する。
    /// </param>
    /// <param name="required">欲しい性質。全部立っている種類だけを通す。</param>
    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        for (uint i = 0; i < MemoryProperties.MemoryTypeCount; i++)
        {
            bool allowed = (typeBits & (1u << (int)i)) != 0;
            if (allowed && (MemoryProperties.MemoryTypes[(int)i].PropertyFlags & required) == required)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"条件に合うメモリの種類がありません(typeBits=0x{typeBits:X}, required={required})。");
    }

    /// <summary>Vulkan の戻り値を確かめる。<b>ほとんどの関数が Result を返すので、黙って捨てない</b>。</summary>
    public static void Check(Result result, string what)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{what} が {result} を返しました。");
        }
    }

    public void Dispose()
    {
        // **まだ走っている仕事があるうちに資源を壊すと落ちる**。Vulkan は参照を数えてくれないので、
        // 片付けの前に必ず「GPU が空になるまで待つ」。OpenGL には要らなかった作法。
        Api.DeviceWaitIdle(Handle);
        _meshShaderApi?.Dispose();
        Api.DestroyFence(Handle, _oneShotFence, null);
        Api.DestroyCommandPool(Handle, _commandPool, null);
        Api.DestroyDevice(Handle, null);

        if (_debugUtils is not null)
        {
            _debugUtils.DestroyDebugUtilsMessenger(Instance, _messenger, null);
            _debugUtils.Dispose();
        }

        Api.DestroyInstance(Instance, null);
        Api.Dispose();

        GC.KeepAlive(_callbackKeepAlive);
    }

    /// <summary>
    /// GPU が拡張を<b>全部</b>持っているか聞く(Day 62b で書いたもの)。
    /// 1つでも欠けたら false。
    /// </summary>
    private static bool HasDeviceExtensions(Vk vk, PhysicalDevice device, string[] names)
    {
        uint count = 0;
        vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, null);
        var extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = extensions)
        {
            vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref count, p);
        }

        var available = new HashSet<string>();
        foreach (ExtensionProperties extension in extensions)
        {
            ExtensionProperties copy = extension;
            string? name = SilkMarshal.PtrToString((nint)copy.ExtensionName);
            if (name is not null)
            {
                available.Add(name);
            }
        }

        return names.All(available.Contains);
    }

    private static bool HasInstanceLayer(Vk vk, string name)
    {
        uint count = 0;
        vk.EnumerateInstanceLayerProperties(ref count, null);
        var layers = new LayerProperties[count];
        fixed (LayerProperties* p = layers)
        {
            vk.EnumerateInstanceLayerProperties(ref count, p);
        }

        foreach (LayerProperties layer in layers)
        {
            LayerProperties copy = layer;
            if (SilkMarshal.PtrToString((nint)copy.LayerName) == name)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// GPU を1台選ぶ。<b>「刺さっている順」ではなく性質で選ぶ</b>——ノート PC では内蔵 GPU が
    /// 先に並ぶことが多く、素直に先頭を取ると 10 倍遅い側を掴む。
    /// </summary>
    private static PhysicalDevice PickPhysicalDevice(Vk vk, Instance instance, out string name, out string driver)
    {
        uint count = 0;
        vk.EnumeratePhysicalDevices(instance, ref count, null);
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
        {
            vk.EnumeratePhysicalDevices(instance, ref count, p);
        }

        PhysicalDevice best = default;
        int bestScore = -1;
        name = string.Empty;
        driver = string.Empty;

        foreach (PhysicalDevice device in devices)
        {
            PhysicalDeviceProperties props = vk.GetPhysicalDeviceProperties(device);

            // 描画キューが1つも無い GPU(計算専用のアクセラレータ)は今日の用には立たない。
            if (!HasGraphicsQueue(vk, device))
            {
                continue;
            }

            int score = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = device;
                name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "?";

                // ドライバの版の詰め方はベンダごとに違う(これは Vulkan の一般形)。
                // 正確に読みたければ VK_KHR_driver_properties を使う。ここは目安。
                uint v = props.DriverVersion;
                driver = $"{v >> 22}.{(v >> 14) & 0xFF}.{(v >> 6) & 0xFF}";
            }
        }

        return best;
    }

    private static bool HasGraphicsQueue(Vk vk, PhysicalDevice device)
    {
        foreach (QueueFamilyProperties family in GetQueueFamilies(vk, device))
        {
            if ((family.QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static uint FindGraphicsQueueFamily(Vk vk, PhysicalDevice device)
    {
        QueueFamilyProperties[] families = GetQueueFamilies(vk, device);
        for (uint i = 0; i < families.Length; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("描画キューが見つかりません。");
    }

    /// <summary>
    /// キューファミリの一覧。<b>「個数を聞いてから、その大きさの配列でもう一度聞く」</b>という
    /// Vulkan の定型(この形の関数が何十個もある)。
    /// </summary>
    private static QueueFamilyProperties[] GetQueueFamilies(Vk vk, PhysicalDevice device)
    {
        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, p);
        }

        return families;
    }

    /// <summary>
    /// 検証レイヤからの通知。<b>どのスレッドから呼ばれるか分からない</b>ので、
    /// ここで UI を触らない(標準エラーへ出すだけにする)。
    /// </summary>
    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        string text = SilkMarshal.PtrToString((nint)data->PMessage) ?? string.Empty;
        Console.Error.WriteLine($"[vulkan/{severity}] {text}");

        // false を返すのが決まり。true は「この API 呼び出しを中断せよ」の意味で、
        // 検証レイヤ自身を試すためのもの(ふつうのアプリが使うものではない)。
        return Vk.False;
    }
}
