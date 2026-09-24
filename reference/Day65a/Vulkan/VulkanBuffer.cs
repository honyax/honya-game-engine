using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

// ImplicitUsings で System が入っているので、素の Buffer は System.Buffer と衝突する。
// **Vulkan の型名は C の API をそのまま写した短い名前が多く**、この種の衝突が頻繁に起きる
// (Image / Buffer / Semaphore / Event など)。名前空間ごと別名にすると読みにくいので、
// ぶつかった型だけ別名で固定する。
using Buffer = Silk.NET.Vulkan.Buffer;

namespace MeshletRenderer;

/// <summary>
/// バッファ1本。<b>「入れ物」と「メモリ」が別々の概念</b>なのが Vulkan らしいところ(Day 62a の要点3)。
///
/// <para>
/// OpenGL の <c>glBufferData</c> は、入れ物を作ることとメモリを確保することと中身を書くことを
/// 1回の呼び出しでやっていた。Vulkan はこれを3つに割る。
/// </para>
/// <list type="number">
/// <item><c>vkCreateBuffer</c> … <b>大きさと使い道だけ</b>を宣言する。メモリはまだ1バイトも無い</item>
/// <item><c>vkAllocateMemory</c> … メモリを確保する。<b>どの種類のメモリか</b>はこちらが選ぶ</item>
/// <item><c>vkBindBufferMemory</c> … 入れ物とメモリを貼り合わせる</item>
/// </list>
/// <para>
/// 面倒に見えるが、これは<b>1つの大きなメモリを何本ものバッファで分け合う</b>ためにこの形になっている。
/// 実際のエンジンは <c>vkAllocateMemory</c> の回数に上限がある(数千回)ので、
/// 64MB 単位で確保して自前で切り分ける(VulkanMemoryAllocator がやっているのはこれ)。
/// ここでは本数が少ないので、<b>1本につき1回確保する</b>という素直だが実戦的でない形にしてある。
/// </para>
/// </summary>
internal sealed unsafe class VulkanBuffer : IDisposable
{
    private readonly VulkanDevice _device;

    private VulkanBuffer(VulkanDevice device, Buffer handle, DeviceMemory memory, ulong size)
    {
        _device = device;
        Handle = handle;
        Memory = memory;
        Size = size;
    }

    public Buffer Handle { get; }

    public DeviceMemory Memory { get; }

    /// <summary>確保したバイト数。</summary>
    public ulong Size { get; }

    /// <summary>
    /// 入れ物を作って、メモリを確保して、貼り合わせる。
    /// </summary>
    /// <param name="usage">
    /// <b>使い道を全部宣言する</b>。後から「やっぱり転送先にも使いたい」は効かない
    /// (検証レイヤが止める。レイヤが無ければ静かに壊れる)。
    /// </param>
    /// <param name="properties">
    /// <see cref="MemoryPropertyFlags.HostVisibleBit"/> を含めると CPU から map できる。
    /// 含めないなら <see cref="MemoryPropertyFlags.DeviceLocalBit"/> で VRAM に置き、
    /// 中身は転送で入れる(<see cref="CreateDeviceLocal{T}"/>)。
    /// </param>
    public static VulkanBuffer Create(VulkanDevice device, ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
    {
        Vk vk = device.Api;

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,

            // キューを1本しか作っていないので、他の口と共有する必要が無い。
            // Concurrent にすると GPU 側で最適化が一段落ちる。
            SharingMode = SharingMode.Exclusive,
        };
        VulkanDevice.Check(vk.CreateBuffer(device.Handle, &bufferInfo, null, out Buffer handle), "vkCreateBuffer");

        // **要求されるのは「大きさ」だけではない**。境界(Alignment)と、
        // 置ける種類のビット列(MemoryTypeBits)も一緒に返ってくる。
        vk.GetBufferMemoryRequirements(device.Handle, handle, out MemoryRequirements requirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = device.FindMemoryType(requirements.MemoryTypeBits, properties),
        };
        VulkanDevice.Check(vk.AllocateMemory(device.Handle, &allocInfo, null, out DeviceMemory memory), "vkAllocateMemory");

        // 最後の 0 はメモリの先頭からのオフセット。1つのメモリを複数のバッファで分け合うなら、
        // ここをずらして貼る(そのとき requirements.Alignment を守る義務がある)。
        VulkanDevice.Check(vk.BindBufferMemory(device.Handle, handle, memory, 0), "vkBindBufferMemory");

        return new VulkanBuffer(device, handle, memory, requirements.Size);
    }

    /// <summary>
    /// CPU から書ける置き場所(ステージングバッファ)を作る。
    ///
    /// <para>
    /// <see cref="MemoryPropertyFlags.HostCoherentBit"/> を付けておくと、
    /// 書いた内容が GPU から見えるようになるまでの<b>明示的な flush が要らなくなる</b>。
    /// 付けないと <c>vkFlushMappedMemoryRanges</c> を自分で呼ぶ必要があり、忘れると
    /// 「たまに古い値が読まれる」という最悪の種類のバグになる。
    /// </para>
    /// </summary>
    public static VulkanBuffer CreateStaging(VulkanDevice device, ulong size, BufferUsageFlags usage)
        => Create(device, size, usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

    /// <summary>
    /// 配列の中身を VRAM(DeviceLocal)に置いたバッファを作る。<b>ステージング経由の2段</b>。
    ///
    /// <para>
    /// なぜ2段かというと、DeviceLocal なメモリはふつう CPU から map できないから。
    /// 「CPU から書ける置き場所へ書く」→「GPU に転送コマンドを投げる」の順になる。
    /// シェーダから何度も読まれるデータ(今日の頂点・索引・メッシュレット)は、この手間をかけて VRAM に置く値打ちがある。
    /// </para>
    /// </summary>
    public static VulkanBuffer CreateDeviceLocal<T>(VulkanDevice device, ReadOnlySpan<T> data, BufferUsageFlags usage)
        where T : unmanaged
    {
        ulong size = (ulong)(data.Length * Unsafe.SizeOf<T>());

        using VulkanBuffer staging = CreateStaging(device, size, BufferUsageFlags.TransferSrcBit);
        staging.Write(data);

        VulkanBuffer target = Create(
            device, size,
            usage | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit);

        device.SubmitAndWait(cmd =>
        {
            var region = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = size };
            device.Api.CmdCopyBuffer(cmd, staging.Handle, target.Handle, 1, &region);
        });

        return target;
    }

    /// <summary>CPU から中身を書く。HostVisible なメモリでしか呼べない。</summary>
    public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        ulong size = (ulong)(data.Length * Unsafe.SizeOf<T>());
        void* mapped = null;
        VulkanDevice.Check(_device.Api.MapMemory(_device.Handle, Memory, 0, size, 0, &mapped), "vkMapMemory");
        try
        {
            data.CopyTo(new Span<T>(mapped, data.Length));
        }
        finally
        {
            _device.Api.UnmapMemory(_device.Handle, Memory);
        }
    }

    /// <summary>
    /// CPU から中身を読んで <c>int[]</c> に写す。画面へ出す画素を受け取るのに使う。
    ///
    /// <para>
    /// map しっぱなしにして毎フレーム <c>Marshal.Copy</c> だけする手もある(そのほうが速い)。
    /// ここでは「map して、写して、unmap する」の対応が目で追えるほうを採った。
    /// </para>
    /// </summary>
    public void Read(int[] destination)
    {
        ulong size = (ulong)destination.Length * sizeof(int);
        void* mapped = null;
        VulkanDevice.Check(_device.Api.MapMemory(_device.Handle, Memory, 0, size, 0, &mapped), "vkMapMemory");
        try
        {
            Marshal.Copy((nint)mapped, destination, 0, destination.Length);
        }
        finally
        {
            _device.Api.UnmapMemory(_device.Handle, Memory);
        }
    }

    /// <summary>
    /// 入れ物とメモリを<b>別々に</b>壊す。作るときが2段だったので、壊すときも2段。
    /// 順番は逆(入れ物が先)——メモリを先に解放すると、入れ物が宙に浮いた状態になる。
    /// </summary>
    public void Dispose()
    {
        _device.Api.DestroyBuffer(_device.Handle, Handle, null);
        _device.Api.FreeMemory(_device.Handle, Memory, null);
    }
}
