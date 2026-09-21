using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MeshletRenderer;

/// <summary>
/// グラフィックスパイプライン1本(今日の要点1・5)。<b>頂点の道とメッシュの道で同じクラスを使う</b>。
///
/// <para>
/// Day 62 のコンピュートパイプラインは「シェーダ1本 + レイアウト」で済んだ。
/// グラフィックスパイプラインは、<b>固定機能の段の設定を全部</b>一緒に固める。
/// </para>
/// <list type="table">
/// <item><term>頂点入力</term><description>頂点バッファのどこに何があるか(<b>メッシュの道には無い</b>)</description></item>
/// <item><term>入力の組み立て</term><description>索引を3つずつ読んで三角形にする(<b>メッシュの道には無い</b>)</description></item>
/// <item><term>ビューポート</term><description>クリップ座標 → 画素の写し方</description></item>
/// <item><term>ラスタライズ</term><description>塗りつぶすか線か、裏を捨てるか、表はどちらの回り方か</description></item>
/// <item><term>深度</term><description>手前だけを残すか</description></item>
/// <item><term>色の合成</term><description>書き込む色の混ぜ方(今日は上書き)</description></item>
/// </list>
/// <para>
/// OpenGL ではこれらは全部「今の状態」で、<c>glEnable(GL_DEPTH_TEST)</c> のように描く直前に変えられた。
/// Vulkan は<b>パイプラインを作る時点で固める</b>(Day 62a の要点4 と同じ「準備を前に寄せる」)。
/// </para>
/// <para>
/// 2つの道の差は、上の表の<b>最初の2行が有るか無いか</b>だけ——これが今日の要点1 の
/// 「メッシュシェーダは頂点を配る回路を飛ばす」を、コードの形で見せている場所。
/// </para>
/// </summary>
internal sealed unsafe class GraphicsPipeline : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly ShaderModule[] _modules;

    private GraphicsPipeline(VulkanDevice device, Pipeline handle, ShaderModule[] modules)
    {
        _device = device;
        Handle = handle;
        _modules = modules;
    }

    public Pipeline Handle { get; }

    /// <summary>
    /// パイプラインを作る。
    /// </summary>
    /// <param name="stages">段と SPIR-V の組。頂点の道は (頂点, 画素)、メッシュの道は (メッシュ, 画素)。</param>
    /// <param name="vertexInput">
    /// 頂点入力と入力の組み立てを使うか。<b>頂点の道だけ true</b>。
    /// メッシュシェーダを含むパイプラインでは、この2つは<b>渡しても無視される</b>(仕様)。
    /// ここでは無視されるものを渡さず null にして、無いことを見えるようにしてある。
    /// </param>
    public static GraphicsPipeline Create(
        VulkanDevice device, PipelineLayout layout, int width, int height,
        (ShaderStageFlags Stage, uint[] Spirv)[] stages, bool vertexInput)
    {
        Vk vk = device.Api;

        var modules = new ShaderModule[stages.Length];
        var stageInfos = stackalloc PipelineShaderStageCreateInfo[stages.Length];
        byte* entryPoint = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            for (int i = 0; i < stages.Length; i++)
            {
                fixed (uint* pCode = stages[i].Spirv)
                {
                    var moduleInfo = new ShaderModuleCreateInfo
                    {
                        SType = StructureType.ShaderModuleCreateInfo,
                        CodeSize = (nuint)(stages[i].Spirv.Length * sizeof(uint)),
                        PCode = pCode,
                    };
                    VulkanDevice.Check(vk.CreateShaderModule(device.Handle, &moduleInfo, null, out modules[i]), "vkCreateShaderModule");
                }

                stageInfos[i] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = stages[i].Stage,
                    Module = modules[i],
                    PName = entryPoint,
                };
            }

            // ---- 頂点入力(頂点の道だけ)------------------------------------------------
            // 「0 番の頂点バッファを 32 バイトずつ読み、先頭 12 バイトを location 0、
            //   16 バイト目から 12 バイトを location 1 に渡せ」という宣言。
            // これを読んで頂点シェーダに配るのが**入力の回路**(Input Assembler)で、
            // メッシュシェーダはこの回路を使わない——自分で storage buffer から読む。
            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = (uint)Unsafe.SizeOf<GpuVertex>(),
                InputRate = VertexInputRate.Vertex,
            };
            var attributes = stackalloc VertexInputAttributeDescription[2];
            attributes[0] = new VertexInputAttributeDescription
            {
                Location = 0,
                Binding = 0,
                Format = Format.R32G32B32Sfloat,
                Offset = 0,
            };
            attributes[1] = new VertexInputAttributeDescription
            {
                Location = 1,
                Binding = 0,
                Format = Format.R32G32B32Sfloat,
                Offset = GpuVertex.NormalOffset,
            };
            var vertexInputState = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 2,
                PVertexAttributeDescriptions = attributes,
            };

            // 索引を3つずつ読んで三角形1枚にする。これも頂点の道だけ。
            // メッシュシェーダは gl_PrimitiveTriangleIndicesEXT に**自分で**3つ組を書く。
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };

            // ---- ここから下は2つの道で共通 ----------------------------------------------
            // 画面の大きさは固定なので、ビューポートも固めてしまう(動的にすると CmdSetViewport が要る)。
            var viewport = new Viewport(0.0f, 0.0f, width, height, 0.0f, 1.0f);
            var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height));
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor,
            };

            var rasterization = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,

                // 裏を向いた三角形はラスタライザが捨てる。**ここで捨てられるのは三角形1枚ずつ**で、
                // それより前の段(頂点シェーダ・メッシュシェーダ)はもう走り終わっている。
                // Day 64b でこれを<b>メッシュレットごとに、もっと前で</b>やるのがタスクシェーダ。
                CullMode = CullModeFlags.BackBit,

                // 投影の y を裏返したので(Camera.cs)、画面上では表が反時計回りのまま見える。
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1.0f,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Less,
            };

            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                               | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            var colorBlend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment,
            };

            // ---- dynamic rendering(要点5)----------------------------------------------
            // 描画パス(VkRenderPass)を作らない代わりに、「どの形式の的に描くか」だけをここで言う。
            // 実物の画像は描くときに CmdBeginRendering で渡す。
            Format colorFormat = VulkanImage.ColorFormat;
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = VulkanImage.DepthFormat,
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = (uint)stages.Length,
                PStages = stageInfos,

                // **ここが2つの道の差のすべて**。
                PVertexInputState = vertexInput ? &vertexInputState : null,
                PInputAssemblyState = vertexInput ? &inputAssembly : null,

                PViewportState = &viewportState,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlend,
                Layout = layout,

                // dynamic rendering なので描画パスは渡さない。
                RenderPass = default,
            };

            VulkanDevice.Check(
                vk.CreateGraphicsPipelines(device.Handle, default, 1, &pipelineInfo, null, out Pipeline pipeline),
                "vkCreateGraphicsPipelines");
            return new GraphicsPipeline(device, pipeline, modules);
        }
        catch
        {
            foreach (ShaderModule module in modules)
            {
                if (module.Handle != 0)
                {
                    vk.DestroyShaderModule(device.Handle, module, null);
                }
            }

            throw;
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entryPoint);
        }
    }

    public void Dispose()
    {
        _device.Api.DestroyPipeline(_device.Handle, Handle, null);
        foreach (ShaderModule module in _modules)
        {
            _device.Api.DestroyShaderModule(_device.Handle, module, null);
        }
    }
}
