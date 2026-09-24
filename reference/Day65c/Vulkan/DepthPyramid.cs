using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

// System.Windows.Forms.ImageLayout と衝突する。VulkanImage.cs と同じ事情。
using ImageLayout = Silk.NET.Vulkan.ImageLayout;

namespace MeshletRenderer;

/// <summary>
/// Hi-Z(深度のピラミッド)。<b>Day 65b で追加</b>(要点2)。
///
/// <para>
/// 描き終わった深度を、縦横半分ずつに畳みながら「2x2 のうちいちばん奥の深度」を残した画像の山。
/// 960 x 540 の深度から、480 x 270 → 240 x 135 → ... → 1 x 1 の <b>9 段</b>ができる。
/// Day 65c のカリングのシェーダは、物が画面で覆う範囲の大きさに合った段を選び、<b>2x2 画素だけ読んで</b>
/// 「その範囲でいちばん奥の深度」を知る(何千画素もある範囲でも、読むのは 4 画素)。
/// Day 65b では作って、1段を CPU へ引き取って画面に出すところまで。
/// </para>
/// <para>
/// 段ごとに1回ずつ、合わせて 9 回 <c>depth_reduce.comp</c> を走らせる。段 L は段 L − 1 を読むので、
/// <b>段と段の間にバリアが要る</b>(書き終わってから次の段が読む)。
/// </para>
/// <para>
/// 画像のレイアウトは作った直後に <see cref="ImageLayout.General"/> へ移し、そのまま置きっぱなしにする。
/// 書く(ストレージ画像)のも読む(サンプルする画像)のもシェーダだけで、どちらも General で済む
/// (Day 62 の画像と同じ扱い)。CPU へ引き取るコピーも General のまま読める。
/// </para>
/// </summary>
internal sealed unsafe class DepthPyramid : IDisposable
{
    /// <summary><c>depth_reduce.comp</c> の <c>local_size_x / y</c>。</summary>
    private const int WorkGroupSize = 8;

    private readonly VulkanDevice _device;
    private readonly VulkanImage _image;
    private readonly ImageView[] _levelViews;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _sets;
    private readonly PipelineLayout _pipelineLayout;
    private readonly ComputePipeline _pipeline;

    /// <param name="depth">読む深度。<see cref="ImageLayout.ShaderReadOnlyOptimal"/> に移してから <see cref="Record"/> を呼ぶこと。</param>
    public DepthPyramid(VulkanDevice device, VulkanImage depth, ShaderCompiler compiler, string shaderDirectory)
    {
        _device = device;
        Vk vk = device.Api;

        // 段 0 は深度の半分。そこから 1 x 1 になるまで半分にしていく。
        // **半分は切り捨て**(Vulkan のミップの大きさの決まり。max(1, 大きさ >> 段))。
        // 480 x 270 → 240 x 135 → 120 x 67 → 60 x 33 → 30 x 16 → 15 x 8 → 7 x 4 → 3 x 2 → 1 x 1 の 9 段。
        // 切り上げで数えると 10 段になるが、画像は 9 段までしか作れない(作ろうとすると仕様違反。検証レイヤが無いと黙って壊れる)。
        Width = depth.Width / 2;
        Height = depth.Height / 2;
        int levels = 1;
        for (int w = Width, h = Height; w > 1 || h > 1; levels++)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        _image = VulkanImage.CreatePyramid(device, Width, Height, levels);
        _levelViews = new ImageView[levels];
        for (int i = 0; i < levels; i++)
        {
            _levelViews[i] = _image.CreateMipView(i);
        }

        // 作った直後に1回だけ General へ移す。以降は移し替えない。
        device.SubmitAndWait(cmd => _image.RecordBarrier(
            cmd, ImageLayout.Undefined, ImageLayout.General,
            PipelineStageFlags.TopOfPipeBit, 0,
            PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit));

        // ---- ディスクリプタ: 0 = 深度 / 1 = Hi-Z 全体 / 2 = 書く段 ----------------------
        // **セットを段の数だけ作る**。違うのは 2 番(書く段のビュー)だけだが、
        // ディスクリプタセットは「挿したものの組」なので、組が変わるならセットも変わる。
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (int i = 0; i < 3; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = i == 2 ? DescriptorType.StorageImage : DescriptorType.SampledImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.ComputeBit,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        VulkanDevice.Check(vk.CreateDescriptorSetLayout(device.Handle, &layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout");

        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.SampledImage, DescriptorCount = (uint)(2 * levels) };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = (uint)levels };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)levels,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        VulkanDevice.Check(vk.CreateDescriptorPool(device.Handle, &poolInfo, null, out _pool), "vkCreateDescriptorPool");

        _sets = new DescriptorSet[levels];
        DescriptorSetLayout setLayout = _setLayout;
        var images = stackalloc DescriptorImageInfo[3];
        var writes = stackalloc WriteDescriptorSet[3];
        for (int level = 0; level < levels; level++)
        {
            var setAlloc = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            VulkanDevice.Check(vk.AllocateDescriptorSets(device.Handle, &setAlloc, out _sets[level]), "vkAllocateDescriptorSets");

            // 画像を挿すときは「どのレイアウトで置いてあるか」も一緒に言う(読む側がそれを前提に読む)。
            images[0] = new DescriptorImageInfo { ImageView = depth.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            images[1] = new DescriptorImageInfo { ImageView = _image.View, ImageLayout = ImageLayout.General };
            images[2] = new DescriptorImageInfo { ImageView = _levelViews[level], ImageLayout = ImageLayout.General };

            for (int i = 0; i < 3; i++)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[level],
                    DstBinding = (uint)i,
                    DescriptorCount = 1,
                    DescriptorType = bindings[i].DescriptorType,
                    PImageInfo = &images[i],
                };
            }

            vk.UpdateDescriptorSets(device.Handle, 3, writes, 0, null);
        }

        // 何段目かはプッシュ定数で渡す(uint 1つ)。
        var pushRange = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Offset = 0, Size = sizeof(uint) };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        VulkanDevice.Check(vk.CreatePipelineLayout(device.Handle, &pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        uint[] spirv = compiler.CompileFile(Path.Combine(shaderDirectory, "depth_reduce.comp"), ShaderKind.ComputeShader);
        _pipeline = ComputePipeline.Create(device, _pipelineLayout, spirv);
    }

    /// <summary>段 0 の幅(深度の半分、切り捨て)。</summary>
    public int Width { get; }

    /// <summary>段 0 の高さ。</summary>
    public int Height { get; }

    /// <summary>段の数。960 x 540 の深度なら 9。</summary>
    public int Levels => _levelViews.Length;

    /// <summary>全部の段が見えるビュー(<see cref="ImageLayout.General"/> で置いてある)。Day 65c でカリングのシェーダに挿す。</summary>
    public ImageView View => _image.View;

    /// <summary>段 <paramref name="level"/> の幅。Vulkan のミップの決まりどおり max(1, 段 0 の幅 >> 段)。</summary>
    public int LevelWidth(int level) => Math.Max(1, Width >> level);

    /// <summary>段 <paramref name="level"/> の高さ。</summary>
    public int LevelHeight(int level) => Math.Max(1, Height >> level);

    /// <summary>
    /// 段 <paramref name="level"/> をバッファへ写すコマンドを積む(Day 65b。画面に出すため)。
    /// Hi-Z を書き終えてから(コンピュートの書き込み → 転送の読み込みのバリアの後で)呼ぶこと。
    /// </summary>
    public void RecordCopyLevel(CommandBuffer cmd, VulkanBuffer destination, int level)
        => _image.RecordCopyToBuffer(cmd, destination, level, ImageLayout.General);

    /// <summary>
    /// Hi-Z を下の段から順に作るコマンドを積む。
    /// 深度は <see cref="ImageLayout.ShaderReadOnlyOptimal"/> に移し終えていること。
    /// 最後の段を書き終えたあとのバリア(書いた → カリングが読む)は、読む側を知っている呼び出し側が積む。
    /// </summary>
    public void Record(CommandBuffer cmd)
    {
        Vk vk = _device.Api;
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline.Handle);

        int width = Width;
        int height = Height;
        for (int level = 0; level < Levels; level++)
        {
            DescriptorSet set = _sets[level];
            vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
            uint pushLevel = (uint)level;
            vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, sizeof(uint), &pushLevel);
            vk.CmdDispatch(cmd, (uint)((width + WorkGroupSize - 1) / WorkGroupSize), (uint)((height + WorkGroupSize - 1) / WorkGroupSize), 1);

            // 次の段はいま書いた段を読む。「書き終わってから読め」。最後の段の後ろは呼び出し側。
            if (level < Levels - 1)
            {
                var written = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                };
                vk.CmdPipelineBarrier(cmd, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.ComputeShaderBit, 0, 1, &written, 0, null, 0, null);
            }

            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
    }

    public void Dispose()
    {
        Vk vk = _device.Api;
        _pipeline.Dispose();
        vk.DestroyPipelineLayout(_device.Handle, _pipelineLayout, null);
        vk.DestroyDescriptorPool(_device.Handle, _pool, null);
        vk.DestroyDescriptorSetLayout(_device.Handle, _setLayout, null);
        foreach (ImageView view in _levelViews)
        {
            vk.DestroyImageView(_device.Handle, view, null);
        }

        _image.Dispose();
    }
}
