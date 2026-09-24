using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MeshletRenderer;

/// <summary>
/// コンピュートパイプライン1本(Day 65b で追加)。
///
/// <para>
/// Day 65a では <c>MeshRenderer</c> の中の関数(<c>CreateComputePipeline</c>)で作り、ハンドルを2つ持っていた。
/// Day 65b で Hi-Z を作るパイプライン(<see cref="DepthPyramid"/>)が2つ目の使い手になったので、
/// <see cref="GraphicsPipeline"/> と同じ形のクラスに切り出した。中身は Day 62c の <c>ComputeRenderer</c> から持ってきたものと同じ。
/// </para>
/// <para>
/// グラフィックスパイプラインと違って、固定機能の設定は1つも無い。シェーダ1本とレイアウトだけ。
/// </para>
/// </summary>
internal sealed unsafe class ComputePipeline : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly ShaderModule _module;

    private ComputePipeline(VulkanDevice device, Pipeline handle, ShaderModule module)
    {
        _device = device;
        Handle = handle;
        _module = module;
    }

    public Pipeline Handle { get; }

    /// <summary>
    /// SPIR-V からコンピュートパイプラインを1本作る。
    ///
    /// <para>
    /// シェーダモジュールは SPIR-V をそのまま包んだだけの物で、<b>この時点ではまだ機械語ではない</b>。
    /// 実際の翻訳は <c>vkCreateComputePipelines</c> で起きる。
    /// </para>
    /// </summary>
    public static ComputePipeline Create(VulkanDevice device, PipelineLayout layout, uint[] spirv)
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
            var pipelineInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = module,
                    PName = entryPoint,
                },
                Layout = layout,
            };
            VulkanDevice.Check(
                vk.CreateComputePipelines(device.Handle, default, 1, &pipelineInfo, null, out Pipeline pipeline),
                "vkCreateComputePipelines");
            return new ComputePipeline(device, pipeline, module);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entryPoint);
        }
    }

    public void Dispose()
    {
        _device.Api.DestroyPipeline(_device.Handle, Handle, null);
        _device.Api.DestroyShaderModule(_device.Handle, _module, null);
    }
}
