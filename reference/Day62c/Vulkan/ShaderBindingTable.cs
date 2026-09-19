using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace HardwareRayTracer;

/// <summary>
/// シェーダバインディングテーブル(SBT)。<b>Day 62c のいちばん分かりにくいところ</b>。
///
/// <para>
/// コンピュートシェーダは「このパイプラインを bind して dispatch」で終わりだった。
/// レイトレーシングパイプラインには<b>入口が1つではない</b>——
/// raygen が1本、miss が2本、hit グループが2つあり、
/// <b>どれを呼ぶかは光線がどこに当たったかで決まる</b>。
/// その対応表を<b>GPU のメモリの上に自分で並べる</b>のが SBT。
/// </para>
/// <para>
/// 表に並ぶのは<b>取っ手</b>(handle)。<c>vkGetRayTracingShaderGroupHandlesKHR</c> が返す
/// 32 バイト(GPU による)の不透明なバイト列で、<b>中身に意味を求めてはいけない</b>。
/// アプリがやるのは「取っ手を、決められた境界に沿って、決められた順に並べる」ことだけ。
/// </para>
/// <para>
/// 表は4つの区画に分かれる。
/// </para>
/// <code>
///   +--------------------+ 0                     raygen  (必ず1つだけ)
///   | raygen の取っ手     |
///   +--------------------+ ShaderGroupBaseAlignment
///   | miss[0] sky        |                       miss    (traceRayEXT の missIndex で選ぶ)
///   | miss[1] shadow     |
///   +--------------------+ (次の境界)
///   | hit[0]  diffuse    |                       hit     (当たった図形から番号が決まる)
///   | hit[1]  metal      |
///   +--------------------+
/// </code>
/// <para>
/// <b>hit グループの番号の決まり方</b>がこの仕組みの核心。
/// </para>
/// <code>
///   番号 = instanceShaderBindingTableRecordOffset   ← TLAS の実体が持つ(今日は 0)
///        + geometryIndex * sbtRecordStride          ← BLAS の中のジオメトリ番号(0 か 1)
///        + sbtRecordOffset                          ← traceRayEXT の引数(今日は 0)
/// </code>
/// <para>
/// 今日は <c>sbtRecordStride = 1</c> にしてあるので、<b>ジオメトリ番号がそのまま
/// hit グループの番号</b>になる。BLAS のジオメトリ 0 に拡散の球、1 に金属の球を入れてあるので、
/// 拡散に当たれば <c>diffuse.rchit</c>、金属なら <c>metal.rchit</c> が呼ばれる。
/// </para>
/// <para>
/// <b>ここが Day 62b の <c>if (kind == 1)</c> の行き先</b>。分岐がコードから消えて、
/// 代わりに「表の並べ方」になった。面倒に見えるが、材質が 50 種類ある場面では
/// 巨大な分岐を1本のシェーダに抱えるより、はるかに素直に速くなる。
/// </para>
/// </summary>
internal sealed unsafe class ShaderBindingTable : IDisposable
{
    private readonly VulkanBuffer _buffer;

    private ShaderBindingTable(
        VulkanBuffer buffer,
        StridedDeviceAddressRegionKHR raygen,
        StridedDeviceAddressRegionKHR miss,
        StridedDeviceAddressRegionKHR hit)
    {
        _buffer = buffer;
        Raygen = raygen;
        Miss = miss;
        Hit = hit;
    }

    /// <summary>raygen の区画。<b>Size と Stride が等しくなければならない</b>(仕様)。</summary>
    public StridedDeviceAddressRegionKHR Raygen { get; }

    public StridedDeviceAddressRegionKHR Miss { get; }

    public StridedDeviceAddressRegionKHR Hit { get; }

    /// <summary>
    /// 使わない区画(callable)。<c>vkCmdTraceRaysKHR</c> は4つとも要求するので、
    /// 空の値を渡す。<b>null を渡すことはできない</b>。
    /// </summary>
    public StridedDeviceAddressRegionKHR Callable => default;

    /// <summary>
    /// SBT が実際に使うバイト数。<b>確保したバッファの大きさとは違う</b>——
    /// <c>vkAllocateMemory</c> は境界に切り上げるので、256 バイト確保されて 192 バイト使う、
    /// ということが起きる。
    /// </summary>
    public ulong SizeBytes { get; private init; }

    /// <summary>
    /// パイプラインから取っ手を取り出して、SBT を組む。
    /// </summary>
    /// <param name="missCount">miss シェーダの本数(今日は 2: sky と shadow)。</param>
    /// <param name="hitCount">hit グループの数(今日は 2: diffuse と metal)。</param>
    public static ShaderBindingTable Create(
        VulkanDevice device, KhrRayTracingPipeline api, Pipeline pipeline, uint missCount, uint hitCount)
    {
        uint handleSize = device.ShaderGroupHandleSize;

        // **レコードの刻み幅**は「取っ手の大きさを ShaderGroupHandleAlignment に切り上げたもの」。
        // 取っ手のすぐ後ろに自前のデータ(テクスチャの番号など)を詰めたければ、
        // ここをもっと広げる——それが「SBT レコードにデータを埋める」という言い方の中身。
        uint stride = Align(handleSize, device.ShaderGroupHandleAlignment);

        // **区画の先頭**は ShaderGroupBaseAlignment(手元では 64)に乗せる。
        // 刻み幅(32)より大きいので、区画の間には必ず隙間ができる。
        uint baseAlign = device.ShaderGroupBaseAlignment;

        uint raygenSize = Align(stride, baseAlign);
        uint missSize = Align(stride * missCount, baseAlign);
        uint hitSize = Align(stride * hitCount, baseAlign);

        uint groupCount = 1 + missCount + hitCount;
        var handles = new byte[groupCount * handleSize];
        fixed (byte* pHandles = handles)
        {
            // **全部のグループの取っ手をまとめて1回で受け取る**。順番はパイプラインを作ったときの
            // シェーダグループの並びと同じ(RayTracingPipeline.cs の GroupIndex を参照)。
            VulkanDevice.Check(
                api.GetRayTracingShaderGroupHandles(
                    device.Handle, pipeline, 0, groupCount, (nuint)handles.Length, pHandles),
                "vkGetRayTracingShaderGroupHandlesKHR");
        }

        // SBT のバッファ。**CPU から書けるところに置く**——小さい(今日は 192 バイト)ので、
        // VRAM へ転送する値打ちが無い。ShaderBindingTableBitKhr を忘れると検証レイヤに怒られる。
        ulong total = raygenSize + missSize + hitSize;
        VulkanBuffer buffer = VulkanBuffer.Create(
            device, total,
            BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        // 取っ手を区画の決まった位置へ写す。
        var bytes = new byte[total];
        CopyHandle(handles, bytes, 0, 0, handleSize);
        for (uint i = 0; i < missCount; i++)
        {
            CopyHandle(handles, bytes, (1 + i) * handleSize, raygenSize + i * stride, handleSize);
        }

        for (uint i = 0; i < hitCount; i++)
        {
            CopyHandle(handles, bytes, (1 + missCount + i) * handleSize, raygenSize + missSize + i * stride, handleSize);
        }

        buffer.Write<byte>(bytes);

        ulong address = buffer.DeviceAddress;
        var raygen = new StridedDeviceAddressRegionKHR
        {
            DeviceAddress = address,

            // **raygen だけは Stride == Size** でなければならない、と仕様に書いてある。
            // raygen は1本しか呼ばれないので「並び」に意味が無く、区画そのものが1レコード。
            Stride = raygenSize,
            Size = raygenSize,
        };
        var miss = new StridedDeviceAddressRegionKHR
        {
            DeviceAddress = address + raygenSize,
            Stride = stride,
            Size = missSize,
        };
        var hit = new StridedDeviceAddressRegionKHR
        {
            DeviceAddress = address + raygenSize + missSize,
            Stride = stride,
            Size = hitSize,
        };

        return new ShaderBindingTable(buffer, raygen, miss, hit) { SizeBytes = total };
    }

    public void Dispose() => _buffer.Dispose();

    private static void CopyHandle(byte[] handles, byte[] destination, uint source, ulong offset, uint size)
        => Array.Copy(handles, (int)source, destination, (int)offset, (int)size);

    /// <summary><paramref name="value"/> を <paramref name="alignment"/> の倍数へ切り上げる。</summary>
    private static uint Align(uint value, uint alignment)
        => alignment == 0 ? value : (value + alignment - 1) / alignment * alignment;
}
