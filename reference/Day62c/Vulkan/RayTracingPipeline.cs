using System.Runtime.InteropServices;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace HardwareRayTracer;

/// <summary>
/// レイトレーシングパイプライン(Day 62c の主役)。
///
/// <para>
/// コンピュートパイプラインは<b>シェーダが1本</b>だった。こちらは6本を1つに束ねる。
/// </para>
/// <list type="table">
/// <item><term>raygen</term><description>光線を作る。1画素に1回、<c>vkCmdTraceRaysKHR</c> が呼ぶ</description></item>
/// <item><term>miss(2本)</term><description>何にも当たらなかったとき。一次光線用と影の光線用</description></item>
/// <item><term>closest-hit(2本)</term><description>いちばん近い交点が決まったとき。拡散用と金属用</description></item>
/// <item><term>intersection(1本)</term><description>箱の中で球の式を解く。2つの hit グループで共有</description></item>
/// </list>
/// <para>
/// <b>「段(stage)」と「グループ(group)」は別物</b>で、ここがいちばん混乱しやすい。
/// </para>
/// <list type="bullet">
/// <item><b>段</b> = シェーダのモジュール1本。上の6本がそれ</item>
/// <item><b>グループ</b> = <b>SBT に並ぶ単位</b>。general(1本だけ)か hit グループ
/// (closest-hit + any-hit + intersection の組)のどちらか</item>
/// </list>
/// <para>
/// 今日は 6 段 → 5 グループになる。intersection は単独では呼べず、
/// <b>hit グループの一部として2つのグループに同じものが入る</b>。
/// </para>
/// <code>
///   グループ 0: general       raygen           -> SBT の raygen 区画
///   グループ 1: general       sky.rmiss        -> SBT の miss 区画 [0]
///   グループ 2: general       shadow.rmiss     -> SBT の miss 区画 [1]
///   グループ 3: procedural    diffuse + rint   -> SBT の hit 区画 [0]   (ジオメトリ 0 = 拡散)
///   グループ 4: procedural    metal   + rint   -> SBT の hit 区画 [1]   (ジオメトリ 1 = 金属)
/// </code>
/// <para>
/// <b>グループの並びが SBT の並びと1対1になる</b>のが約束で、
/// <c>vkGetRayTracingShaderGroupHandlesKHR</c> はこの順に取っ手を返してくる。
/// </para>
/// </summary>
internal sealed unsafe class RayTracingPipeline : IDisposable
{
    /// <summary>
    /// シェーダから光線を撃てる深さの上限。
    ///
    /// <para>
    /// 一次光線(1)→ 反射 x3(2〜4)→ その先からの影の光線(5)で 5 段。
    /// <b>宣言より深く潜ると未定義動作</b>で、シェーダ側でも <c>payload.depth</c> を数えて止めている
    /// (<c>metal.rchit</c>)。宣言を小さくするほどドライバが確保するスタックが減って速くなるので、
    /// 「足りるだけの最小」を書くのが定石。
    /// </para>
    /// </summary>
    private const uint RecursionDepth = 5;

    private readonly VulkanDevice _device;
    private readonly KhrRayTracingPipeline _api;
    private readonly ShaderModule[] _modules;

    private RayTracingPipeline(
        VulkanDevice device, KhrRayTracingPipeline api,
        Pipeline pipeline, ShaderModule[] modules, ShaderBindingTable table)
    {
        _device = device;
        _api = api;
        _modules = modules;
        Handle = pipeline;
        Table = table;
    }

    public Pipeline Handle { get; }

    public ShaderBindingTable Table { get; }

    /// <summary>
    /// 6本のシェーダを翻訳して、パイプラインと SBT を作る。
    /// </summary>
    public static RayTracingPipeline Create(
        VulkanDevice device, ShaderCompiler compiler, PipelineLayout layout, string shaderDirectory)
    {
        KhrRayTracingPipeline api = device.RayTracingPipelineApi
            ?? throw new InvalidOperationException("この GPU はレイトレーシングパイプラインに対応していません。");

        Vk vk = device.Api;

        // ---- 1. 段(シェーダ6本)-----------------------------------------------------
        // **並び順がそのままグループの参照番号になる**ので、ここを動かすと下の GeneralShader /
        // ClosestHitShader / IntersectionShader の番号も全部ずれる。
        (string File, ShaderKind Kind, ShaderStageFlags Stage)[] sources =
        [
            ("raytrace.rgen", ShaderKind.RaygenShader, ShaderStageFlags.RaygenBitKhr),        // 0
            ("sky.rmiss", ShaderKind.MissShader, ShaderStageFlags.MissBitKhr),                // 1
            ("shadow.rmiss", ShaderKind.MissShader, ShaderStageFlags.MissBitKhr),             // 2
            ("diffuse.rchit", ShaderKind.ClosesthitShader, ShaderStageFlags.ClosestHitBitKhr),// 3
            ("metal.rchit", ShaderKind.ClosesthitShader, ShaderStageFlags.ClosestHitBitKhr),  // 4
            ("sphere.rint", ShaderKind.IntersectionShader, ShaderStageFlags.IntersectionBitKhr), // 5
        ];

        var modules = new ShaderModule[sources.Length];
        var stages = new PipelineShaderStageCreateInfo[sources.Length];
        byte* entryPoint = (byte*)Marshal.StringToHGlobalAnsi("main");

        try
        {
            for (int i = 0; i < sources.Length; i++)
            {
                uint[] spirv = compiler.CompileFile(Path.Combine(shaderDirectory, sources[i].File), sources[i].Kind);
                modules[i] = CreateModule(device, spirv);
                stages[i] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = sources[i].Stage,
                    Module = modules[i],
                    PName = entryPoint,
                };
            }

            // ---- 2. グループ(SBT に並ぶ単位)------------------------------------------
            // 使わない欄には Vk.ShaderUnusedKhr を入れる。**0 を入れてはいけない**——
            // 0 は「0 番の段」という意味になってしまう。
            var groups = new RayTracingShaderGroupCreateInfoKHR[5];
            groups[0] = General(0);   // raygen
            groups[1] = General(1);   // sky.rmiss
            groups[2] = General(2);   // shadow.rmiss
            groups[3] = Procedural(closestHit: 3, intersection: 5);   // 拡散
            groups[4] = Procedural(closestHit: 4, intersection: 5);   // 金属

            // ---- 3. パイプライン --------------------------------------------------------
            Pipeline pipeline;
            fixed (PipelineShaderStageCreateInfo* pStages = stages)
            fixed (RayTracingShaderGroupCreateInfoKHR* pGroups = groups)
            {
                var info = new RayTracingPipelineCreateInfoKHR
                {
                    SType = StructureType.RayTracingPipelineCreateInfoKhr,
                    StageCount = (uint)stages.Length,
                    PStages = pStages,
                    GroupCount = (uint)groups.Length,
                    PGroups = pGroups,
                    MaxPipelineRayRecursionDepth = Math.Min(RecursionDepth, device.MaxRayRecursionDepth),
                    Layout = layout,
                };

                // 第2引数は「遅延実行」。CPU で長い時間をかけて最適化したいときに
                // VK_KHR_deferred_host_operations を渡すが、今日は同期で作る(default = null)。
                VulkanDevice.Check(
                    api.CreateRayTracingPipelines(device.Handle, default, default, 1, &info, null, &pipeline),
                    "vkCreateRayTracingPipelinesKHR");
            }

            // ---- 4. SBT ------------------------------------------------------------------
            ShaderBindingTable table = ShaderBindingTable.Create(device, api, pipeline, missCount: 2, hitCount: 2);
            return new RayTracingPipeline(device, api, pipeline, modules, table);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entryPoint);
        }
    }

    /// <summary>
    /// 光線を撃つコマンドを積む。<c>CmdDispatch</c> の代わり。
    ///
    /// <para>
    /// <b>ワークグループの大きさを渡さない</b>のが目に付く違い。
    /// 何人ずつ束ねて走らせるかは<b>ドライバが決める</b>ので、
    /// <c>local_size_x</c> を書く場所がシェーダにも無い。
    /// レイトレーシングは隣の画素と協調しない(共有メモリを使わない)ので、
    /// 束ね方をアプリが決める意味が無い、という判断。
    /// </para>
    /// </summary>
    public void RecordTraceRays(CommandBuffer cmd, uint width, uint height)
    {
        StridedDeviceAddressRegionKHR raygen = Table.Raygen;
        StridedDeviceAddressRegionKHR miss = Table.Miss;
        StridedDeviceAddressRegionKHR hit = Table.Hit;
        StridedDeviceAddressRegionKHR callable = Table.Callable;

        _api.CmdTraceRays(cmd, &raygen, &miss, &hit, &callable, width, height, 1);
    }

    public void Dispose()
    {
        Table.Dispose();
        _device.Api.DestroyPipeline(_device.Handle, Handle, null);
        foreach (ShaderModule module in _modules)
        {
            _device.Api.DestroyShaderModule(_device.Handle, module, null);
        }
    }

    private static ShaderModule CreateModule(VulkanDevice device, uint[] spirv)
    {
        fixed (uint* pCode = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(spirv.Length * sizeof(uint)),
                PCode = pCode,
            };
            VulkanDevice.Check(
                device.Api.CreateShaderModule(device.Handle, &info, null, out ShaderModule module),
                "vkCreateShaderModule");
            return module;
        }
    }

    /// <summary>raygen と miss は「1本だけのグループ」。</summary>
    private static RayTracingShaderGroupCreateInfoKHR General(uint stage) => new()
    {
        SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
        Type = RayTracingShaderGroupTypeKHR.GeneralKhr,
        GeneralShader = stage,
        ClosestHitShader = Vk.ShaderUnusedKhr,
        AnyHitShader = Vk.ShaderUnusedKhr,
        IntersectionShader = Vk.ShaderUnusedKhr,
    };

    /// <summary>
    /// 箱(procedural)の hit グループ。<b>intersection が必須</b>で、
    /// これが無いと箱に当たっても誰も球の式を解かない。
    /// 三角形なら <c>TrianglesHitGroupKhr</c> にして intersection を
    /// <c>Vk.ShaderUnusedKhr</c> にする(RT コアが三角形の式を持っているので)。
    /// </summary>
    private static RayTracingShaderGroupCreateInfoKHR Procedural(uint closestHit, uint intersection) => new()
    {
        SType = StructureType.RayTracingShaderGroupCreateInfoKhr,
        Type = RayTracingShaderGroupTypeKHR.ProceduralHitGroupKhr,
        GeneralShader = Vk.ShaderUnusedKhr,
        ClosestHitShader = closestHit,

        // any-hit は「当たるたびに呼ばれる」シェーダ(葉っぱのアルファ抜きなどに使う)。
        // ジオメトリに Opaque を立ててあるので今日は呼ばれない。
        AnyHitShader = Vk.ShaderUnusedKhr,
        IntersectionShader = intersection,
    };
}
