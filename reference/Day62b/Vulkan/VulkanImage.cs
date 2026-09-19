using Silk.NET.Vulkan;

// System.Drawing.Image / System.Windows.Forms.ImageLayout と衝突するので別名で固定する。
// VulkanBuffer.cs と同じ事情(Vulkan の型名は短いので、WinForms とよくぶつかる)。
using Image = Silk.NET.Vulkan.Image;
using ImageLayout = Silk.NET.Vulkan.ImageLayout;

namespace HardwareRayTracer;

/// <summary>
/// シェーダが書き込む画像1枚と、それを CPU 側へ引き取るための置き場所(今日の要点5)。
///
/// <para>
/// コンピュートシェーダの出力先には、バッファ(<c>uint</c> の並び)でも画像でもなれる。
/// ここで画像を選ぶのは<b>Day 62c でレイトレーシングパイプラインが書き込む先がこれになる</b>から
/// ——ハードウェア RT の raygen シェーダは、慣例として storage image に <c>imageStore</c> する。
/// 今から同じ形にしておくと、62c で差し替えるのがシェーダ1本で済む。
/// </para>
/// <para>
/// 画像がバッファと決定的に違うのは<b>レイアウト</b>という状態を持つこと。GPU は画像を
/// 用途ごとに違う並べ方(タイル状、圧縮あり/なし)で持っていて、
/// 「いまどの並べ方か」を<b>アプリが追跡して宣言する</b>のが Vulkan の決まり。
/// OpenGL ではドライバが裏で切り替えていた(そしてその切り替えが予期しない待ちを生んでいた)。
/// </para>
/// </summary>
internal sealed unsafe class VulkanImage : IDisposable
{
    /// <summary>
    /// 画素の形式。<b>RGBA の順であって BGRA ではない</b>ことに注意(今日の要点5)。
    ///
    /// <para>
    /// ストレージ画像として使える形式は仕様で決まっていて、<c>R8G8B8A8_UNORM</c> は
    /// <b>どの Vulkan 実装でも必ず使える</b>(必須形式)。<c>B8G8R8A8_UNORM</c> は必須ではない。
    /// 一方 WinForms の <c>Format32bppRgb</c> が欲しいバイトの並びは B, G, R, X。
    /// 差を埋める場所は2つあって、
    /// </para>
    /// <list type="bullet">
    /// <item>CPU 側で 100 万画素ぶん入れ替える … 毎フレーム数 ms。もったいない</item>
    /// <item><b>シェーダが書くときに入れ替える</b> … <c>imageStore(img, p, vec4(color.bgr, 1))</c> の1行</item>
    /// </list>
    /// <para>
    /// 後者を採った。GPU 側では並べ替えの費用がほぼ 0 で、しかも「どこで入れ替えたか」が
    /// シェーダの1行に閉じる。shaders/trace.comp の最後の行がそれ。
    /// </para>
    /// </summary>
    public const Format PixelFormat = Format.R8G8B8A8Unorm;

    private readonly VulkanDevice _device;
    private readonly DeviceMemory _memory;

    private VulkanImage(VulkanDevice device, Image handle, ImageView view, DeviceMemory memory, int width, int height)
    {
        _device = device;
        _memory = memory;
        Handle = handle;
        View = view;
        Width = width;
        Height = height;
    }

    public Image Handle { get; }

    /// <summary>
    /// ディスクリプタに挿すのは画像そのものではなく<b>ビュー</b>。
    /// 「この画像のこのミップ・この配列層を、この形式として見る」という指定で、
    /// 1枚の画像に何通りもの見え方を付けられる(今日は1通りしか使わない)。
    /// </summary>
    public ImageView View { get; }

    public int Width { get; }

    public int Height { get; }

    public static VulkanImage Create(VulkanDevice device, int width, int height)
    {
        Vk vk = device.Api;

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = PixelFormat,
            Extent = new Extent3D((uint)width, (uint)height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,

            // Optimal = GPU の都合のよい並べ方(タイル状)。中身を CPU から直接読むことはできないが、
            // シェーダからの読み書きは速い。Linear にすると map して読めるが、
            // ストレージ画像として使える保証が無くなる。
            Tiling = ImageTiling.Optimal,

            // StorageBit … シェーダから imageStore できる
            // TransferSrcBit … ここから転送(引き取り)できる
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,

            // 作りたてのレイアウトは Undefined か Preinitialized のどちらかしか選べない。
            // Undefined は「中身は保証しない」の意味で、下で General へ移す。
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
            Format = PixelFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        VulkanDevice.Check(vk.CreateImageView(device.Handle, &viewInfo, null, out ImageView view), "vkCreateImageView");

        var image = new VulkanImage(device, handle, view, memory, width, height);
        image.TransitionToGeneral();
        return image;
    }

    /// <summary>
    /// レイアウトを <see cref="ImageLayout.General"/> にして、以降<b>ずっとそのままにする</b>。
    ///
    /// <para>
    /// General は「何にでも使える代わりに、どれにも最適ではない」レイアウト。
    /// 描画パスに組み込むなら用途ごとに移し替えて性能を取るが、
    /// <b>ストレージ画像は General でしか読み書きできない</b>ので、今日のように
    /// 「シェーダが書く → 転送で引き取る」だけなら移し替える意味が無い。
    /// 実際のハードウェア RT のコードでも、出力画像は General に置きっぱなしにするのがふつう。
    /// </para>
    /// <para>
    /// バリアが必要な理由は<b>レイアウトの宣言だけではない</b>。GPU は投げられたコマンドを
    /// 勝手に並べ替えて走らせるので、「書き終わってから読む」も自分で書く義務がある。
    /// <see cref="RecordCopyToBuffer"/> のバリアがそれ。
    /// </para>
    /// </summary>
    private void TransitionToGeneral()
    {
        _device.SubmitAndWait(cmd =>
        {
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.General,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = Handle,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.ShaderWriteBit,
            };

            _device.Api.CmdPipelineBarrier(
                cmd,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.ComputeShaderBit,
                0, 0, null, 0, null, 1, &barrier);
        });
    }

    /// <summary>
    /// 画像の中身をバッファへ引き取るコマンドを積む。<b>バリアを先に積むのが肝</b>。
    ///
    /// <para>
    /// バリアは2つのことを同時に言っている。
    /// </para>
    /// <list type="bullet">
    /// <item><b>実行の順序</b>: 計算シェーダの段が終わってから、転送の段を始めよ
    /// (<c>ComputeShaderBit</c> → <c>TransferBit</c>)</item>
    /// <item><b>見え方</b>: シェーダが書いた値をキャッシュから追い出して、転送から読めるようにせよ
    /// (<c>ShaderWriteBit</c> → <c>TransferReadBit</c>)</item>
    /// </list>
    /// <para>
    /// 片方でも欠けると、<b>たいていの場合は正しく動いてしまう</b>のが厄介なところ。
    /// GPU が空いていれば順番どおりに走るので、負荷が上がったときだけ絵が1フレーム古くなる、
    /// といった形で出る。検証レイヤの同期チェック(synchronization2 validation)が拾ってくれる。
    /// </para>
    /// </summary>
    public void RecordCopyToBuffer(CommandBuffer cmd, VulkanBuffer destination)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.General,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };

        _device.Api.CmdPipelineBarrier(
            cmd,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &barrier);

        var region = new BufferImageCopy
        {
            BufferOffset = 0,

            // 0 は「画像の幅・高さに合わせて詰める」の意味。隙間なく並ぶので、
            // 受け取った側は行ごとの stride を考えずに丸ごとコピーできる。
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)Width, (uint)Height, 1),
        };

        _device.Api.CmdCopyImageToBuffer(cmd, Handle, ImageLayout.General, destination.Handle, 1, &region);
    }

    public void Dispose()
    {
        _device.Api.DestroyImageView(_device.Handle, View, null);
        _device.Api.DestroyImage(_device.Handle, Handle, null);
        _device.Api.FreeMemory(_device.Handle, _memory, null);
    }
}
