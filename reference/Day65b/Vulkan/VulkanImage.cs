using Silk.NET.Vulkan;

// System.Drawing.Image / System.Windows.Forms.ImageLayout と衝突するので別名で固定する。
// VulkanBuffer.cs と同じ事情(Vulkan の型名は短いので、WinForms とよくぶつかる)。
using Image = Silk.NET.Vulkan.Image;
using ImageLayout = Silk.NET.Vulkan.ImageLayout;

namespace MeshletRenderer;

/// <summary>
/// 画像1枚。Day 62c の同名のクラスから持ってきて、<b>ラスタライザの的(アタッチメント)</b>になれるようにした。
///
/// <para>
/// Day 62 ではシェーダが <c>imageStore</c> で書くストレージ画像だったので、
/// レイアウトを General に置きっぱなしにできた。今日は<b>ラスタライザが書く</b>。
/// ラスタライザが書ける画像は、<b>その用途専用のレイアウト</b>に置いておく必要がある。
/// </para>
/// <list type="bullet">
/// <item>色 … <see cref="ImageLayout.ColorAttachmentOptimal"/>(描く間)→ <see cref="ImageLayout.TransferSrcOptimal"/>(引き取る間)</item>
/// <item>深度 … <see cref="ImageLayout.DepthAttachmentOptimal"/>(描く間だけ。引き取らない)</item>
/// </list>
/// <para>
/// だから毎フレーム<b>移し替え(バリア)が入る</b>(要点5)。Day 62 では作ったときに1回だけだった。
/// 移し替えは「並べ方を変えよ」と「書き終わってから次へ進め」を同時に言う命令で、
/// OpenGL ではドライバが裏で差し込んでいた。
/// </para>
/// <para>
/// <b>Day 65b の差分</b>: 深度を<b>シェーダから読める</b>ようにした(Hi-Z を作るため)。
/// そして<b>ミップを何段も持つ画像</b>(Hi-Z そのもの)を作れるようにした。
/// </para>
/// </summary>
internal sealed unsafe class VulkanImage : IDisposable
{
    /// <summary>
    /// 色の形式。<b>今日は B, G, R, A の順にできる</b>。
    ///
    /// <para>
    /// Day 62 は <c>R8G8B8A8_UNORM</c> にして、シェーダの最後で <c>color.bgr</c> と並べ替えていた。
    /// ストレージ画像として必ず使える形式が RGBA のほうだけだったから。
    /// ラスタライザの的(カラーアタッチメント)なら <c>B8G8R8A8_UNORM</c> も<b>必須形式</b>なので、
    /// WinForms の <c>Format32bppRgb</c> が欲しい並び(B, G, R, X)でそのまま描ける。
    /// 画素シェーダはふつうに RGB を書けばよく、並べ替えはハードウェアが書き込むときにやってくれる。
    /// </para>
    /// </summary>
    public const Format ColorFormat = Format.B8G8R8A8Unorm;

    /// <summary>
    /// 深度の形式。32 ビット浮動小数。
    /// 深度だけの形式は <c>D32_SFLOAT</c> か <c>X8_D24</c> の<b>どちらか</b>が使えることしか保証されていないが、
    /// デスクトップの GPU はどれも D32 を持っている。
    /// </summary>
    public const Format DepthFormat = Format.D32Sfloat;

    private readonly VulkanDevice _device;
    private readonly DeviceMemory _memory;

    private VulkanImage(
        VulkanDevice device, Image handle, ImageView view, DeviceMemory memory,
        int width, int height, ImageAspectFlags aspect, int mipLevels)
    {
        _device = device;
        _memory = memory;
        Handle = handle;
        View = view;
        Width = width;
        Height = height;
        Aspect = aspect;
        MipLevels = mipLevels;
    }

    public Image Handle { get; }

    /// <summary>
    /// 描画の的に挿すのは画像そのものではなく<b>ビュー</b>。
    /// 「この画像のこのミップ・この配列層を、この形式として見る」という指定で、
    /// 1枚の画像に何通りもの見え方を付けられる(今日は1通りしか使わない)。
    /// </summary>
    public ImageView View { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>色か深度か。バリアもビューも、どちらの面を触るかを毎回言う必要がある。</summary>
    public ImageAspectFlags Aspect { get; }

    /// <summary>ミップの段数(Day 65b で追加)。Hi-Z 以外は 1。<see cref="View"/> は全部の段を見るビュー。</summary>
    public int MipLevels { get; }

    /// <summary>
    /// 色の的。描いたあと CPU へ引き取るので、転送元(TransferSrc)にもなれるようにしておく。
    /// </summary>
    public static VulkanImage CreateColor(VulkanDevice device, int width, int height)
        => Create(device, width, height, ColorFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit);

    /// <summary>
    /// 深度の的。<b>引き取らない</b>ので使い道は1つだけ。
    /// 使い道を絞るほど、GPU は圧縮などの都合のよい並べ方を選べる。
    ///
    /// <para>
    /// Day 65b: <b>シェーダから読む(Sampled)</b>使い道を足した。Hi-Z を作るコンピュートシェーダが、
    /// 描き終わった深度を1画素ずつ読む。使い道が増えたぶん圧縮が効きにくくなるかもしれないが、
    /// 読めなければ Hi-Z は作れない。
    /// </para>
    /// </summary>
    public static VulkanImage CreateDepth(VulkanDevice device, int width, int height)
        => Create(device, width, height, DepthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.DepthBit);

    /// <summary>
    /// Hi-Z の画像(Day 65b で追加)。1画素 = 深度1つ(32 ビット浮動小数)で、ミップを <paramref name="mipLevels"/> 段持つ。
    ///
    /// <para>
    /// コンピュートシェーダが1段ずつ書き(Storage)、次の段を作るときに読む(Sampled)。
    /// Day 65b では1段を CPU へ引き取って画面に出す(TransferSrc)。Day 65c からはカリングのシェーダも読む。
    /// <c>R32_SFLOAT</c> はストレージ画像として使えることが仕様で保証されている形式。
    /// </para>
    /// </summary>
    public static VulkanImage CreatePyramid(VulkanDevice device, int width, int height, int mipLevels)
        => Create(device, width, height, Format.R32Sfloat,
            ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit,
            ImageAspectFlags.ColorBit, mipLevels);

    /// <summary>
    /// ミップの1段だけを見るビューを作る(Day 65b で追加)。Hi-Z を1段ずつ書くときに使う
    /// (ストレージ画像としてシェーダに挿せるのは、ミップ1段ぶんのビューだけ)。
    /// <b>壊すのは呼んだ側</b>(<c>vkDestroyImageView</c>)。
    /// </summary>
    public ImageView CreateMipView(int level)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Handle,
            ViewType = ImageViewType.Type2D,
            Format = Format.R32Sfloat,
            SubresourceRange = new ImageSubresourceRange(Aspect, (uint)level, 1, 0, 1),
        };
        VulkanDevice.Check(_device.Api.CreateImageView(_device.Handle, &viewInfo, null, out ImageView view), "vkCreateImageView");
        return view;
    }

    private static VulkanImage Create(
        VulkanDevice device, int width, int height, Format format, ImageUsageFlags usage, ImageAspectFlags aspect,
        int mipLevels = 1)
    {
        Vk vk = device.Api;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = (uint)mipLevels,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,

            // Optimal = GPU の都合のよい並べ方(タイル状)。中身を CPU から直接読むことはできないが、
            // 描くのは速い。Linear にすると map して読めるが、描画の的として使える保証が無くなる。
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,

            // 作りたてのレイアウトは Undefined。Day 62 は作った直後に General へ移したが、
            // 今日は**毎フレーム描く前に**移すので、ここでは何もしない。
            InitialLayout = ImageLayout.Undefined,
        };
        VulkanDevice.Check(vk.CreateImage(device.Handle, &imageInfo, null, out Image handle), "vkCreateImage");

        vk.GetImageMemoryRequirements(device.Handle, handle, out MemoryRequirements requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = device.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        VulkanDevice.Check(vk.AllocateMemory(device.Handle, &allocInfo, null, out DeviceMemory memory), "vkAllocateMemory");
        VulkanDevice.Check(vk.BindImageMemory(device.Handle, handle, memory, 0), "vkBindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = handle,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = (uint)mipLevels,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        VulkanDevice.Check(vk.CreateImageView(device.Handle, &viewInfo, null, out ImageView view), "vkCreateImageView");

        return new VulkanImage(device, handle, view, memory, width, height, aspect, mipLevels);
    }

    /// <summary>
    /// レイアウトを移し替えるバリアを積む(要点5)。
    ///
    /// <para>
    /// バリアは2つのことを同時に言っている(Day 62a の要点5 と同じ)。
    /// </para>
    /// <list type="bullet">
    /// <item><b>実行の順序</b>: <paramref name="sourceStage"/> の段が終わってから、<paramref name="destinationStage"/> の段を始めよ</item>
    /// <item><b>見え方</b>: <paramref name="sourceAccess"/> で書いた値をキャッシュから追い出して、
    /// <paramref name="destinationAccess"/> から見えるようにせよ</item>
    /// </list>
    /// <para>
    /// 引数が6つもあるのは、この2つを<b>両側ぶん</b>言うから。
    /// 片方でも間違えると<b>たいていの場合は正しく動いてしまう</b>のが怖いところで
    /// (GPU が空いていれば順番どおりに走る)、負荷が上がったときだけ絵が崩れる。
    /// </para>
    /// </summary>
    /// <param name="oldLayout">
    /// いまのレイアウト。<see cref="ImageLayout.Undefined"/> を渡すと「中身は捨ててよい」の意味になる。
    /// 毎フレーム塗り直す的にはそれでよく、GPU は古い中身を読みに行かなくて済む。
    /// </param>
    public void RecordBarrier(
        CommandBuffer cmd,
        ImageLayout oldLayout, ImageLayout newLayout,
        PipelineStageFlags sourceStage, AccessFlags sourceAccess,
        PipelineStageFlags destinationStage, AccessFlags destinationAccess)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Handle,
            // Day 65b: ミップを持つ画像(Hi-Z)は全部の段をまとめて移す。
            SubresourceRange = new ImageSubresourceRange(Aspect, 0, (uint)MipLevels, 0, 1),
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess,
        };

        _device.Api.CmdPipelineBarrier(cmd, sourceStage, destinationStage, 0, 0, null, 0, null, 1, &barrier);
    }

    /// <summary>
    /// 画像の中身をバッファへ引き取るコマンドを積む。
    /// <b>呼ぶ前に <see cref="ImageLayout.TransferSrcOptimal"/> へ移しておく</b>こと(<see cref="RecordBarrier"/>)。
    /// Day 62 はこの中でバリアも積んでいたが、今日は「何の段で書いたか」が描画側の事情なので外に出した。
    /// </summary>
    /// <param name="level">どのミップの段を写すか(Day 65b で追加。Hi-Z の1段を引き取る)。大きさは段に合わせて半分ずつになる。</param>
    /// <param name="layout">
    /// 画像がいま置かれているレイアウト(Day 65b で追加)。色の的は <see cref="ImageLayout.TransferSrcOptimal"/> に移してから、
    /// Hi-Z は <see cref="ImageLayout.General"/> に置いたまま写す。
    /// </param>
    public void RecordCopyToBuffer(
        CommandBuffer cmd, VulkanBuffer destination, int level = 0, ImageLayout layout = ImageLayout.TransferSrcOptimal)
    {
        var region = new BufferImageCopy
        {
            BufferOffset = 0,

            // 0 は「画像の幅・高さに合わせて詰める」の意味。隙間なく並ぶので、
            // 受け取った側は行ごとの stride を考えずに丸ごとコピーできる。
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(Aspect, (uint)level, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)Math.Max(1, Width >> level), (uint)Math.Max(1, Height >> level), 1),
        };

        _device.Api.CmdCopyImageToBuffer(cmd, Handle, layout, destination.Handle, 1, &region);
    }

    public void Dispose()
    {
        _device.Api.DestroyImageView(_device.Handle, View, null);
        _device.Api.DestroyImage(_device.Handle, Handle, null);
        _device.Api.FreeMemory(_device.Handle, _memory, null);
    }
}
