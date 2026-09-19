using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

// System.Buffer と衝突する(VulkanBuffer.cs と同じ事情)。
using Buffer = Silk.NET.Vulkan.Buffer;

namespace HardwareRayTracer;

/// <summary>
/// 1個の図形を囲む箱(今日の要点2)。<c>VkAabbPositionsKHR</c> と同じ 24 バイト。
///
/// <para>
/// <b>三角形以外を加速構造に入れる唯一の方法がこれ</b>。RT コアは
/// 「三角形」と「箱(procedural)」の2種類しか知らない。球を入れたければ、
/// <b>球を囲む箱</b>を登録して、<b>球との交差判定は自分で書く</b>ことになる。
/// </para>
/// <para>
/// Silk.NET は <c>VkAabbPositionsKHR</c> の中身を公開していないので、自分で並びを定義する。
/// 順番は仕様のとおり minX, minY, minZ, maxX, maxY, maxZ。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuAabb
{
    public float MinX;
    public float MinY;
    public float MinZ;
    public float MaxX;
    public float MaxY;
    public float MaxZ;

    public static GpuAabb FromSphere(Vector4 centerRadius)
    {
        float r = centerRadius.W;
        return new GpuAabb
        {
            MinX = centerRadius.X - r,
            MinY = centerRadius.Y - r,
            MinZ = centerRadius.Z - r,
            MaxX = centerRadius.X + r,
            MaxY = centerRadius.Y + r,
            MaxZ = centerRadius.Z + r,
        };
    }
}

/// <summary>
/// TLAS に入れる「実体」1個ぶん(今日の要点3)。<c>VkAccelerationStructureInstanceKHR</c> と同じ 64 バイト。
///
/// <para>
/// Silk.NET の <c>AccelerationStructureInstanceKHR</c> はビットフィールドを公開していないので、
/// <b>並びを自分で定義する</b>。C の側では
/// </para>
/// <code>
/// VkTransformMatrixKHR transform;                             // 48 バイト(3x4、行優先)
/// uint32_t instanceCustomIndex : 24;  uint32_t mask : 8;      // 4 バイトに詰めて入っている
/// uint32_t sbtRecordOffset    : 24;  uint32_t flags : 8;      // 同上
/// uint64_t accelerationStructureReference;                    // BLAS の**アドレス**
/// </code>
/// <para>
/// ビットフィールドを手で詰めるのは気持ちが悪いが、<b>この 64 バイトの並びは仕様で固定されていて、
/// ドライバが直接読む</b>(ディスクリプタ越しではなく、GPU のアドレスで渡す)。
/// 1バイトずれると加速構造が丸ごと無意味になる。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuInstance
{
    /// <summary>3x4 の行優先の行列。48 バイト。</summary>
    public Matrix3x4 Transform;

    /// <summary>下位 24 ビット = シェーダから読める自前の番号、上位 8 ビット = マスク。</summary>
    public uint CustomIndexAndMask;

    /// <summary>下位 24 ビット = SBT の record オフセット(Day 62c で効く)、上位 8 ビット = フラグ。</summary>
    public uint SbtOffsetAndFlags;

    /// <summary>この実体が指す BLAS の<b>GPU アドレス</b>。</summary>
    public ulong AccelerationStructureReference;
}

/// <summary>3x4 の行優先の行列。<c>VkTransformMatrixKHR</c> と同じ 48 バイト。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct Matrix3x4
{
    public float M00, M01, M02, M03;
    public float M10, M11, M12, M13;
    public float M20, M21, M22, M23;

    /// <summary>
    /// 単位行列(= 動かさない)。
    ///
    /// <para>
    /// TLAS の実体は<b>BLAS を好きな位置・向き・大きさに置ける</b>のが持ち味で、
    /// 同じ BLAS を 1000 個の実体で使い回せば、木が 1000 本並んだ森が
    /// <b>木1本ぶんのメモリ</b>で建つ。今日は球をそれぞれ別の図形として BLAS に入れているので、
    /// 実体は1つだけ、変換も単位行列。<b>階層を使わない使い方</b>。
    /// </para>
    /// </summary>
    public static Matrix3x4 Identity => new()
    {
        M00 = 1.0f,
        M11 = 1.0f,
        M22 = 1.0f,
    };
}

/// <summary>
/// 加速構造(BLAS + TLAS)。<b>Day 62b の主役</b>。
///
/// <para>
/// Day 60 で CPU に BVH を書いたときは、節点を自分で分割して自分でたどった。
/// ハードウェア RT では<b>どちらも GPU に渡す</b>。
/// </para>
/// <list type="bullet">
/// <item><b>建てるのは GPU</b>(<c>vkCmdBuildAccelerationStructures</c>)。
/// 分割の方針(SAH かどうか、どこで切るか)は<b>ドライバの中で決まる</b>——
/// Day 60 の <c>Bvh.cs</c> にあたるものが、ベンダの実装として GPU の中にある</item>
/// <item><b>たどるのも GPU</b>(RT コア。GLSL からは <c>rayQueryEXT</c> の数行)。
/// 節点をたどるループも、スタックも、書かない</item>
/// </list>
/// <para>
/// <b>2階建てになっているのが BVH との大きな違い</b>。
/// </para>
/// <code>
///   TLAS (top level)      … 「実体」の集まり。実体 = BLAS + 置き方(行列)
///     └ BLAS (bottom level) … 図形そのものの集まり(三角形 or 箱)
/// </code>
/// <para>
/// 分けてあるのは<b>動くものに強くするため</b>。物が動いても BLAS は建て直さず、
/// TLAS の行列を書き換えて TLAS だけ建て直せばよい(TLAS は実体が数千でも 1ms 程度で建つ)。
/// 今日の場面は動かないので、両方1回だけ建てる。
/// </para>
/// </summary>
internal sealed unsafe class AccelerationStructure : IDisposable
{
    private readonly VulkanDevice _device;
    private readonly KhrAccelerationStructure _api;

    private readonly AccelerationStructureKHR _blas;
    private readonly VulkanBuffer _blasBuffer;
    private readonly VulkanBuffer _aabbBuffer;

    private readonly AccelerationStructureKHR _tlas;
    private readonly VulkanBuffer _tlasBuffer;
    private readonly VulkanBuffer _instanceBuffer;

    private AccelerationStructure(
        VulkanDevice device, KhrAccelerationStructure api,
        AccelerationStructureKHR blas, VulkanBuffer blasBuffer, VulkanBuffer aabbBuffer,
        AccelerationStructureKHR tlas, VulkanBuffer tlasBuffer, VulkanBuffer instanceBuffer,
        ulong blasBytes, ulong tlasBytes, ulong scratchBytes, double buildMilliseconds, int primitiveCount)
    {
        _device = device;
        _api = api;
        _blas = blas;
        _blasBuffer = blasBuffer;
        _aabbBuffer = aabbBuffer;
        _tlas = tlas;
        _tlasBuffer = tlasBuffer;
        _instanceBuffer = instanceBuffer;

        BlasBytes = blasBytes;
        TlasBytes = tlasBytes;
        ScratchBytes = scratchBytes;
        BuildMilliseconds = buildMilliseconds;
        PrimitiveCount = primitiveCount;
    }

    /// <summary>ディスクリプタに挿すのは<b>TLAS のほう</b>。BLAS は TLAS からアドレスで参照されている。</summary>
    public AccelerationStructureKHR Handle => _tlas;

    /// <summary>BLAS の大きさ(バイト)。HUD に出す——<b>自前 BVH と比べるため</b>。</summary>
    public ulong BlasBytes { get; }

    public ulong TlasBytes { get; }

    /// <summary>構築に使った作業用バッファの大きさ。<b>構築が終われば捨ててよい</b>。</summary>
    public ulong ScratchBytes { get; }

    /// <summary>建てるのにかかった時間(ms)。GPU 側の構築コマンドを待った時間。</summary>
    public double BuildMilliseconds { get; }

    public int PrimitiveCount { get; }

    /// <summary>
    /// 球の配列から BLAS と TLAS を建てる。
    ///
    /// <para>
    /// 手順は BLAS も TLAS も同じ4段で、<b>違うのは「何を入れるか」だけ</b>。
    /// </para>
    /// <list type="number">
    /// <item>入力(AABB / 実体)を <b>GPU のアドレスで指せるバッファ</b>に置く</item>
    /// <item><c>vkGetAccelerationStructureBuildSizes</c> で<b>必要な大きさを聞く</b>
    /// (加速構造本体と、構築中の作業用バッファ)</item>
    /// <item>その大きさのバッファを確保して <c>vkCreateAccelerationStructure</c></item>
    /// <item><c>vkCmdBuildAccelerationStructures</c> を投げる</item>
    /// </list>
    /// </summary>
    public static AccelerationStructure Build(VulkanDevice device, SceneSpheres scene)
    {
        if (!device.RayQuerySupported)
        {
            throw new InvalidOperationException("この GPU はハードウェアレイトレーシングに対応していません。");
        }

        KhrAccelerationStructure api = device.AccelerationStructureApi;
        var clock = Stopwatch.StartNew();
        GpuSphere[] spheres = scene.Spheres;

        // ---- BLAS: 球を囲む箱を並べる ------------------------------------------------
        var aabbs = new GpuAabb[spheres.Length];
        for (int i = 0; i < spheres.Length; i++)
        {
            aabbs[i] = GpuAabb.FromSphere(spheres[i].CenterRadius);
        }

        // AccelerationStructureBuildInputReadOnlyBit … 加速構造の入力として読まれる
        // ShaderDeviceAddressBit                     … GPU のアドレスで指せる
        VulkanBuffer aabbBuffer = VulkanBuffer.CreateDeviceLocal<GpuAabb>(
            device, aabbs,
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr
            | BufferUsageFlags.ShaderDeviceAddressBit);

        // **ジオメトリを2つに割る**(Day 62c)。0 番が拡散の球、1 番が金属の球。
        //
        // 割る理由は SBT で、**どの hit シェーダを呼ぶかはジオメトリ単位でしか切り替えられない**
        // (要点3)。箱の配列そのものは1本のままで、先頭からのずれ(PrimitiveOffset)と
        // 個数(PrimitiveCount)で「この範囲がこのジオメトリ」と言う。
        //
        // Day 62b では1つだったので、ray query 側は PrimitiveIndex をそのまま球の番号に使えた。
        // 今日は GeometryIndex も見て足し戻す必要がある(shaders/common.glsl の sphereIndex)。
        ulong stride = (ulong)Unsafe.SizeOf<GpuAabb>();
        var geometries = new AccelerationStructureGeometryKHR[2];
        for (int i = 0; i < 2; i++)
        {
            geometries[i] = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.AabbsKhr,

                // **Opaque を立てると any-hit を呼ばなくなる**ので速い。今日は透ける物が無い。
                Flags = GeometryFlagsKHR.OpaqueBitKhr,
                Geometry = new AccelerationStructureGeometryDataKHR
                {
                    Aabbs = new AccelerationStructureGeometryAabbsDataKHR
                    {
                        SType = StructureType.AccelerationStructureGeometryAabbsDataKhr,

                        // **同じバッファの先頭を渡して、範囲は下の PrimitiveOffset でずらす**。
                        // ここでアドレスをずらしてもよいが、offset は範囲側で言うのが仕様の流儀。
                        Data = new DeviceOrHostAddressConstKHR { DeviceAddress = aabbBuffer.DeviceAddress },
                        Stride = stride,
                    },
                },
            };
        }

        var ranges = new AccelerationStructureBuildRangeInfoKHR[2];
        ranges[0] = new AccelerationStructureBuildRangeInfoKHR
        {
            PrimitiveCount = (uint)scene.DiffuseCount,
            PrimitiveOffset = 0,
        };
        ranges[1] = new AccelerationStructureBuildRangeInfoKHR
        {
            PrimitiveCount = (uint)scene.MetalCount,

            // **バイト単位のずれ**(図形の個数ではない)。ここを間違えると金属の球が消える。
            PrimitiveOffset = (uint)(scene.DiffuseCount * (int)stride),
        };

        (AccelerationStructureKHR blas, VulkanBuffer blasBuffer, ulong blasBytes, ulong blasScratch) =
            BuildOne(device, api, AccelerationStructureTypeKHR.BottomLevelKhr, geometries, ranges);

        // ---- TLAS: BLAS を1個だけ置く ------------------------------------------------
        // 今日は階層を使わない(要点3)。実体1つ、変換は単位行列。
        var instance = new GpuInstance
        {
            Transform = Matrix3x4.Identity,

            // 下位 24 ビットが自前の番号(今日は使わない)、上位 8 ビットがマスク。
            // **マスクは光線側のマスクと AND を取って、0 なら当たらない**ことにする仕組み。
            // 0xFF にしておけば必ず当たる(影だけ無視する物、などを作るときに効く)。
            CustomIndexAndMask = 0xFFu << 24,

            // 下位 24 ビットは SBT の record オフセット。ray query では使われないが、
            // **Day 62c ではここが「どの hit シェーダを呼ぶか」を決める**。
            SbtOffsetAndFlags = (uint)GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr << 24,

            AccelerationStructureReference = GetDeviceAddress(device, api, blas),
        };

        VulkanBuffer instanceBuffer = VulkanBuffer.CreateDeviceLocal<GpuInstance>(
            device, new[] { instance },
            BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr
            | BufferUsageFlags.ShaderDeviceAddressBit);

        var instanceGeometry = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = GeometryTypeKHR.InstancesKhr,
            Flags = GeometryFlagsKHR.OpaqueBitKhr,
            Geometry = new AccelerationStructureGeometryDataKHR
            {
                Instances = new AccelerationStructureGeometryInstancesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,

                    // false = 配列そのものが並んでいる(true にするとポインタの配列になる)。
                    ArrayOfPointers = false,
                    Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instanceBuffer.DeviceAddress },
                },
            },
        };

        (AccelerationStructureKHR tlas, VulkanBuffer tlasBuffer, ulong tlasBytes, ulong tlasScratch) =
            BuildOne(device, api, AccelerationStructureTypeKHR.TopLevelKhr, [instanceGeometry],
                [new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = 1 }]);

        return new AccelerationStructure(
            device, api, blas, blasBuffer, aabbBuffer, tlas, tlasBuffer, instanceBuffer,
            blasBytes, tlasBytes, Math.Max(blasScratch, tlasScratch),
            clock.Elapsed.TotalMilliseconds, spheres.Length);
    }

    /// <summary>
    /// 加速構造を1つ建てる。BLAS と TLAS で共通の4段。
    /// </summary>
    private static (AccelerationStructureKHR Handle, VulkanBuffer Buffer, ulong Size, ulong ScratchSize) BuildOne(
        VulkanDevice device, KhrAccelerationStructure api,
        AccelerationStructureTypeKHR type,
        AccelerationStructureGeometryKHR[] geometries,
        AccelerationStructureBuildRangeInfoKHR[] ranges)
    {
        fixed (AccelerationStructureGeometryKHR* pGeometries = geometries)
        fixed (AccelerationStructureBuildRangeInfoKHR* pRanges = ranges)
        {
            var buildInfo = new AccelerationStructureBuildGeometryInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = type,

                // **どちらを優先するかをここで伝える**。今日は1回建てて何万フレームも使うので、
                // たどるのが速いほうを選ぶ。毎フレーム建て直すもの(壊れる建物など)は
                // PreferFastBuildBitKhr にする。ドライバの分割の仕方が変わる。
                Flags = BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr,
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = (uint)geometries.Length,
                PGeometries = pGeometries,
            };

            // ---- 2段目: 大きさを聞く --------------------------------------------------
            // **自分で計算しない**。何バイト要るかはドライバの分割方針で決まるので、聞くしかない。
            // 渡すのは「ジオメトリごとの図形の数」の配列(Day 62c で2つになった)。
            var sizeInfo = new AccelerationStructureBuildSizesInfoKHR
            {
                SType = StructureType.AccelerationStructureBuildSizesInfoKhr,
            };
            var counts = new uint[geometries.Length];
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] = ranges[i].PrimitiveCount;
            }

            fixed (uint* pCounts = counts)
            {
                api.GetAccelerationStructureBuildSizes(
                    device.Handle, AccelerationStructureBuildTypeKHR.DeviceKhr, &buildInfo, pCounts, &sizeInfo);
            }

            return Finish(device, api, type, buildInfo, pRanges, sizeInfo);
        }
    }

    /// <summary>
    /// 場所を作って建てる(<see cref="BuildOne"/> の3段目と4段目)。
    /// <c>fixed</c> の入れ子が深くなるので分けてある。
    /// </summary>
    private static (AccelerationStructureKHR Handle, VulkanBuffer Buffer, ulong Size, ulong ScratchSize) Finish(
        VulkanDevice device, KhrAccelerationStructure api,
        AccelerationStructureTypeKHR type,
        AccelerationStructureBuildGeometryInfoKHR buildInfo,
        AccelerationStructureBuildRangeInfoKHR* ranges,
        AccelerationStructureBuildSizesInfoKHR sizeInfo)
    {
        Vk vk = device.Api;

        // ---- 3段目: 場所を作る --------------------------------------------------------
        VulkanBuffer buffer = VulkanBuffer.Create(
            device, sizeInfo.AccelerationStructureSize,
            BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit);

        var createInfo = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = buffer.Handle,
            Offset = 0,
            Size = sizeInfo.AccelerationStructureSize,
            Type = type,
        };
        VulkanDevice.Check(
            api.CreateAccelerationStructure(device.Handle, &createInfo, null, out AccelerationStructureKHR handle),
            "vkCreateAccelerationStructureKHR");

        // ---- 4段目: 建てる ------------------------------------------------------------
        // 作業用のバッファ(スクラッチ)は**構築中だけ要る**ので、建て終わったら捨てる。
        // 境界の要求(ScratchOffsetAlignment)は GPU ごとに違うので、余分に取って先頭をずらす……
        // のが正式だが、ここは1本を丸ごと使うので、確保の先頭が十分に揃っていることに頼る
        // (vkAllocateMemory が返すアドレスは 256 バイト境界以上に揃っている)。
        using VulkanBuffer scratch = VulkanBuffer.Create(
            device, sizeInfo.BuildScratchSize + device.ScratchOffsetAlignment,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryPropertyFlags.DeviceLocalBit);

        ulong scratchAddress = scratch.DeviceAddress;
        ulong alignment = Math.Max(device.ScratchOffsetAlignment, 1u);
        scratchAddress = (scratchAddress + alignment - 1) / alignment * alignment;

        buildInfo.DstAccelerationStructure = handle;
        buildInfo.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = scratchAddress };

        // **ラムダの中では局所変数のアドレスを取れない**(C# の制約。CS1686)ので、
        // 構造体を値で渡す別メソッドに逃がす。Vulkan を C# で書くとよく踏む。
        AccelerationStructureBuildGeometryInfoKHR info = buildInfo;
        nint rangePointer = (nint)ranges;
        device.SubmitAndWait(cmd => RecordBuild(device, api, cmd, info, (AccelerationStructureBuildRangeInfoKHR*)rangePointer));

        return (handle, buffer, sizeInfo.AccelerationStructureSize, sizeInfo.BuildScratchSize);
    }

    /// <summary>
    /// 構築のコマンドを積む。引数は<b>値で受け取る</b>ので、ここでならアドレスを取れる。
    /// </summary>
    private static void RecordBuild(
        VulkanDevice device, KhrAccelerationStructure api, CommandBuffer cmd,
        AccelerationStructureBuildGeometryInfoKHR buildInfo, AccelerationStructureBuildRangeInfoKHR* ranges)
    {
        // 第4引数が**ポインタのポインタ**なのは、加速構造ごとに「範囲の配列」を渡す形だから
        // (複数の加速構造を1回のコマンドで建てられる)。加速構造は1つずつ建てるので外側は1つ、
        // 内側はジオメトリの数だけ並んでいる(Day 62c で BLAS は2つになった)。
        AccelerationStructureBuildRangeInfoKHR* rangePtr = ranges;
        api.CmdBuildAccelerationStructures(cmd, 1, &buildInfo, &rangePtr);

        // **建て終わってから読む**。TLAS の構築は BLAS の結果を読むので、
        // BLAS → TLAS の間にこのバリアが要る(今日は SubmitAndWait が毎回待っているので
        // 実際には効いていないが、1本のコマンドバッファにまとめるなら必須になる)。
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.AccelerationStructureWriteBitKhr,
            DstAccessMask = AccessFlags.AccelerationStructureReadBitKhr,
        };
        device.Api.CmdPipelineBarrier(
            cmd,
            PipelineStageFlags.AccelerationStructureBuildBitKhr,
            PipelineStageFlags.AccelerationStructureBuildBitKhr,
            0, 1, &barrier, 0, null, 0, null);
    }

    private static ulong GetDeviceAddress(VulkanDevice device, KhrAccelerationStructure api, AccelerationStructureKHR handle)
    {
        var info = new AccelerationStructureDeviceAddressInfoKHR
        {
            SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
            AccelerationStructure = handle,
        };
        return api.GetAccelerationStructureDeviceAddress(device.Handle, &info);
    }

    public void Dispose()
    {
        _api.DestroyAccelerationStructure(_device.Handle, _tlas, null);
        _api.DestroyAccelerationStructure(_device.Handle, _blas, null);

        _instanceBuffer.Dispose();
        _tlasBuffer.Dispose();
        _aabbBuffer.Dispose();
        _blasBuffer.Dispose();
    }
}
