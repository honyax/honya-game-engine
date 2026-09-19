using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace HardwareRayTracer;

/// <summary>
/// Vulkan の入り口。<b>画面に一切関わらずに GPU を1台用意する</b>(今日の要点1)。
///
/// <para>
/// Day 61 の <c>GpuDevice</c> は、GPGPU しかしないのに<b>見えない窓を1つ作った</b>。
/// OpenGL のコンテキストが窓(描画先)の上にしか作れないからで、あれは OpenGL の歴史的な都合だった。
/// Vulkan には <c>VkSurfaceKHR</c>(窓との接点)という別の型があり、
/// <b>画面に出さないなら作らなくてよい</b>。このクラスに窓の話が1つも出てこないのがその証拠。
/// </para>
/// <para>
/// 代わりに Vulkan は、OpenGL が黙ってやっていたことを全部こちらに書かせる。順に:
/// </para>
/// <list type="number">
/// <item><b>インスタンス</b> … Vulkan のローダとの接点。どの拡張・どのレイヤを使うかをここで決める</item>
/// <item><b>物理デバイス</b> … 刺さっている GPU の一覧から1つ選ぶ。何ができるかは<b>聞かないと分からない</b></item>
/// <item><b>キューファミリ</b> … GPU の中の「仕事の受け口」の種類。計算・描画・転送で口が別なことがある</item>
/// <item><b>論理デバイス</b> … 選んだ GPU に対する自分専用の窓口。<b>使う機能をここで明示的に有効化する</b></item>
/// <item><b>コマンドプール</b> … コマンドバッファを切り出す元</item>
/// </list>
/// <para>
/// この5段は Vulkan を使う限りどの用途でも同じで、<b>Day 62c もここは変わらない</b>。
/// </para>
/// <para>
/// <b>Day 62b の差分はこのファイルの2か所だけ</b>。
/// </para>
/// <list type="number">
/// <item>論理デバイスを作るときに<b>拡張を3つ</b>宣言する(要点1)</item>
/// <item>同じく<b>機能を3つ</b>有効化する(<c>bufferDeviceAddress</c> / <c>accelerationStructure</c> / <c>rayQuery</c>)</item>
/// </list>
/// <para>
/// 「聞いてから使う」の作法もそのまま効く——対応していない GPU では
/// <see cref="RayQuerySupported"/> が false になり、総当たりだけで動き続ける。
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

    /// <summary>
    /// ハードウェアレイトレーシングに要る拡張3つ(Day 62b で追加)。
    ///
    /// <para>
    /// <b>VK_KHR_acceleration_structure</b> … BLAS/TLAS を作る・建てる・壊す API 一式。本体。<br/>
    /// <b>VK_KHR_ray_query</b> … <b>どのシェーダからでも</b> <c>rayQueryEXT</c> で加速構造をたどれるようにする。
    /// 今日はコンピュートシェーダから使う(Day 62c の <c>ray_tracing_pipeline</c> とは別の入口)。<br/>
    /// <b>VK_KHR_deferred_host_operations</b> … それ自体は使わないが、
    /// <c>acceleration_structure</c> が<b>依存として要求する</b>ので宣言が要る
    /// (CPU 側で加速構造を建てるときにスレッドを分けるための仕組み)。
    /// </para>
    /// <para>
    /// <c>VK_KHR_buffer_device_address</c> と <c>VK_KHR_spirv_1_4</c> も必要だが、
    /// <b>どちらも Vulkan 1.2 のコアに入った</b>ので、ApiVersion を 1.2 にしてある今日は宣言が要らない。
    /// </para>
    /// </summary>
    private static readonly string[] RayTracingExtensions =
    [
        KhrAccelerationStructure.ExtensionName,

        // VK_KHR_ray_query は**関数を1つも増やさない**拡張(増えるのは GLSL 側の rayQueryEXT だけ)なので、
        // Silk.NET が包みのクラスを生成していない。名前を直に書く。
        "VK_KHR_ray_query",

        KhrDeferredHostOperations.ExtensionName,
    ];

    private readonly ExtDebugUtils? _debugUtils;
    private readonly DebugUtilsMessengerEXT _messenger;

    /// <summary>検証レイヤからの通知を受ける関数。GC に回収されると呼ばれた瞬間に落ちるので、参照を保持する。</summary>
    private readonly DebugUtilsMessengerCallbackFunctionEXT? _callbackKeepAlive;

    private readonly CommandPool _commandPool;

    /// <summary>加速構造の API(Day 62b で追加)。対応していない GPU では null。</summary>
    private readonly KhrAccelerationStructure? _accelerationStructureApi;

    /// <summary>一時的なコマンド(転送や、Day 62b の加速構造の構築)を積むための使い回しのコマンドバッファ。</summary>
    private readonly CommandBuffer _oneShot;

    private readonly Fence _oneShotFence;

    private VulkanDevice(
        Vk vk, Instance instance, PhysicalDevice physicalDevice, Device device,
        uint queueFamily, Queue queue, CommandPool commandPool,
        CommandBuffer oneShot, Fence oneShotFence,
        ExtDebugUtils? debugUtils, DebugUtilsMessengerEXT messenger,
        DebugUtilsMessengerCallbackFunctionEXT? callbackKeepAlive,
        KhrAccelerationStructure? accelerationStructureApi,
        string name, string driver, bool validation)
    {
        _accelerationStructureApi = accelerationStructureApi;
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

        if (accelerationStructureApi is not null)
        {
            // 加速構造まわりの上限や境界は、ふつうの Properties ではなく**拡張の Properties**にある。
            // PNext につないで Properties2 で聞くのが Vulkan の一般形(要点1の「聞いてから使う」)。
            var asProps = new PhysicalDeviceAccelerationStructurePropertiesKHR
            {
                SType = StructureType.PhysicalDeviceAccelerationStructurePropertiesKhr,
            };
            var props2 = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &asProps,
            };
            vk.GetPhysicalDeviceProperties2(physicalDevice, &props2);

            ScratchOffsetAlignment = asProps.MinAccelerationStructureScratchOffsetAlignment;
            MaxPrimitiveCount = asProps.MaxPrimitiveCount;
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
    /// タイムスタンプ1目盛りのナノ秒(今日の要点6)。
    ///
    /// <para>
    /// GPU が刻むクロックの粒度は機種ごとに違う。<c>vkCmdWriteTimestamp</c> が書き込むのは
    /// <b>その GPU 固有の目盛りの数</b>なので、ナノ秒に直すにはこの係数を掛ける
    /// (NVIDIA はふつう 1.0、AMD は 40 前後)。
    /// </para>
    /// </summary>
    public float TimestampPeriod { get; }

    /// <summary>
    /// ray query が使えるか(Day 62b で追加)。false なら総当たりだけで動く。
    ///
    /// <para>
    /// ハードウェア RT は<b>GPU の世代で決まる</b>(NVIDIA RTX 2000 系以降 / AMD RX 6000 系以降 /
    /// Intel Arc)。使えない GPU では拡張が一覧に出てこないので、宣言した瞬間に
    /// <c>vkCreateDevice</c> が <c>ErrorExtensionNotPresent</c> を返す。
    /// だから<b>宣言する前に聞く</b>(<see cref="HasDeviceExtensions"/>)。
    /// </para>
    /// </summary>
    [MemberNotNullWhen(true, nameof(AccelerationStructureApi))]
    public bool RayQuerySupported => _accelerationStructureApi is not null;

    /// <summary>加速構造の API。<see cref="RayQuerySupported"/> が true のときだけ使える。</summary>
    public KhrAccelerationStructure? AccelerationStructureApi => _accelerationStructureApi;

    /// <summary>
    /// 加速構造を建てるときの作業用バッファ(スクラッチ)に要求される境界(バイト)。
    /// NVIDIA は 128、AMD は 128 前後。<b>守らないと構築がそのまま壊れる</b>。
    /// </summary>
    public uint ScratchOffsetAlignment { get; }

    /// <summary>1つの加速構造に入れられる図形の数の上限。参考までに HUD に出す。</summary>
    public ulong MaxPrimitiveCount { get; }

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
        // 1.2 にしておくと bufferDeviceAddress などが**コア機能として**使えて、
        // Day 62b のレイトレーシング拡張もそのまま乗る。
        byte* appName = (byte*)Marshal.StringToHGlobalAnsi("Day62 HardwareRayTracer");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            PEngineName = appName,
            ApplicationVersion = new Version32(1, 0, 0),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version12,
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
            message = "計算キューを持つ GPU が見つかりませんでした。";
            return null;
        }

        // ---- 4. キューファミリを選ぶ --------------------------------------------------
        // GPU の中には「受け口」が何種類かあり、描画・計算・転送のどれができるかが口ごとに違う。
        // 今日要るのは計算と転送だけ。**描画ができない計算専用の口**を持つ GPU もあるので、
        // Compute が立っていることだけを条件にする(Compute の口は転送も必ずできる、と仕様で決まっている)。
        uint queueFamily = FindComputeQueueFamily(vk, physical);

        // ---- 5. 論理デバイス ----------------------------------------------------------
        float priority = 1.0f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        // ---- 5-1. レイトレーシングが使えるか聞く(Day 62b で追加)--------------------
        // **宣言する前に聞く**。持っていない拡張を宣言すると vkCreateDevice が
        // ErrorExtensionNotPresent を返して、そこで終わってしまう。
        bool rayTracing = HasDeviceExtensions(vk, physical, RayTracingExtensions);

        // ---- 5-2. 機能の鎖を組む ------------------------------------------------------
        // 使う機能は**明示的に有効化する**のが Vulkan の流儀。既定では全部 off で、
        // 有効にしていない機能を使うシェーダを渡すと未定義動作になる(検証レイヤが捕まえてくれる)。
        //
        // 機能は版と拡張ごとに別の構造体に分かれていて、**PNext で数珠つなぎにして渡す**。
        // ここでは 3 つつなぐ(rayQuery -> accelerationStructure -> Vulkan12 -> DeviceCreateInfo)。
        var rayQueryFeatures = new PhysicalDeviceRayQueryFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceRayQueryFeaturesKhr,
            RayQuery = true,
        };

        var asFeatures = new PhysicalDeviceAccelerationStructureFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceAccelerationStructureFeaturesKhr,
            PNext = &rayQueryFeatures,
            AccelerationStructure = true,
        };

        var features12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,

            // **加速構造を建てるのに要る**。BLAS も TLAS も、中身のバッファを
            // ハンドルではなく「GPU のアドレス」で指す作りになっているため(要点3)。
            BufferDeviceAddress = rayTracing,

            PNext = rayTracing ? &asFeatures : null,
        };

        nint deviceExtPtr = SilkMarshal.StringArrayToPtr(rayTracing ? RayTracingExtensions : []);
        Device device;
        try
        {
            var deviceInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features12,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = rayTracing ? (uint)RayTracingExtensions.Length : 0u,
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
        KhrAccelerationStructure? asApi = null;
        if (rayTracing && vk.TryGetDeviceExtension(instance, device, out KhrAccelerationStructure loaded))
        {
            asApi = loaded;
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
            + $" / ハードウェアRT {(asApi is not null ? "あり" : "なし")}";
        return new VulkanDevice(
            vk, instance, physical, device, queueFamily, queue, pool, oneShot, fence,
            debugUtils, messenger, callback, asApi, name, driver, useValidation);
    }

    /// <summary>
    /// コマンドを積んで、<b>投げて、終わるまで待つ</b>(今日の要点4)。
    ///
    /// <para>
    /// 毎回待つのは効率がよくないが、今日の使い道(起動時の転送、1フレーム1回のディスパッチ)では
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
    /// 欲しい性質を満たすメモリの種類を探す(今日の要点3)。
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
        _accelerationStructureApi?.Dispose();
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
    /// GPU が拡張を<b>全部</b>持っているか聞く(Day 62b で追加)。
    /// 1つでも欠けたら false——レイトレーシングの3つは揃って初めて意味がある。
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

            // 計算キューが1つも無い GPU は今日の用には立たない(まず無いが、仕様上はありうる)。
            if (!HasComputeQueue(vk, device))
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

    private static bool HasComputeQueue(Vk vk, PhysicalDevice device)
    {
        foreach (QueueFamilyProperties family in GetQueueFamilies(vk, device))
        {
            if ((family.QueueFlags & QueueFlags.ComputeBit) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static uint FindComputeQueueFamily(Vk vk, PhysicalDevice device)
    {
        QueueFamilyProperties[] families = GetQueueFamilies(vk, device);
        for (uint i = 0; i < families.Length; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.ComputeBit) != 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("計算キューが見つかりません。");
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
