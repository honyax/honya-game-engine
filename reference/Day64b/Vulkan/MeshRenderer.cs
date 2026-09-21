using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

// System.Windows.Forms.ImageLayout と衝突する。VulkanImage.cs と同じ事情。
using ImageLayout = Silk.NET.Vulkan.ImageLayout;

// System.Buffer と衝突する。VulkanBuffer.cs と同じ事情。
using Buffer = Silk.NET.Vulkan.Buffer;

namespace MeshletRenderer;

/// <summary>
/// 1フレームぶんの値。<b>uniform buffer</b> で渡す(GLSL の <c>Frame</c> ブロックと1バイトも違ってはいけない)。
/// Day 64b で 96 バイトから 208 バイトになった(カリングの値が増えた)。
///
/// <para>
/// Day 62 はプッシュ定数(128 バイトまで保証)で渡していた。今日は行列 64 バイトに収まるが、
/// Day 64b で<b>カリング用の値(視錐台の面6枚と目の位置)が増えて 128 バイトを超える</b>ので、
/// 最初から uniform buffer にしておく。GPU が1回読めば全員で使い回せる、小さな読み取り専用の置き場。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct FrameUniforms
{
    /// <summary>世界 → クリップ座標。</summary>
    public Matrix4x4 ViewProjection;

    /// <summary>xyz = 目の位置(ハイライトの向きに使う)。w は未使用。</summary>
    public Vector4 CameraPosition;

    /// <summary>表示の種類。0 = 陰影、1 = メッシュレットごとの色、2 = 三角形ごとの色。</summary>
    public uint Mode;

    /// <summary>1体ぶんのメッシュレットの数(Day 64b で追加)。タスクシェーダが範囲の外を捨てるのに使う。</summary>
    public uint MeshletCount;

    /// <summary>どのカリングをするか(Day 64b で追加)。1 = 視錐台、2 = 背面。</summary>
    public uint CullFlags;

    public uint Padding;

    /// <summary>xyz = カリングの目の位置(Day 64b で追加)。描く目と別に持つので、止めておける。</summary>
    public Vector4 CullPosition;

    /// <summary>カリングの目の視錐台の6面(Day 64b で追加)。<see cref="Camera.FrustumPlanes"/>。</summary>
    public FrustumPlaneArray Frustum;
}

/// <summary>
/// <c>Vector4</c> 6つの固定長の並び(Day 64b で追加)。GLSL の <c>vec4 frustum[6]</c> に当たる。
///
/// <para>
/// 構造体の中に配列(<c>Vector4[]</c>)を置くと、中身ではなく<b>参照(ポインタ)</b>が入ってしまい、
/// GPU へそのまま送れない。<c>[InlineArray]</c>(C# 12)を付けると、同じ型の値を
/// <b>構造体の中にじかに6つ並べた</b>ものになり、<c>frustum[i]</c> と添字で触れる。
/// </para>
/// </summary>
[InlineArray(6)]
internal struct FrustumPlaneArray
{
    private Vector4 _element;
}

/// <summary>
/// 描き方(Day 64a の要点1)。Day 64b でタスクシェーダの道が増えて3つになった。
/// <b>カリングを切れば3つとも同じ絵を出す</b>(カリングしても、正しければ同じ絵になる。検証1)。
/// </summary>
internal enum DrawPath
{
    /// <summary>
    /// タスクシェーダ + メッシュシェーダ(Day 64b で追加)。タスクシェーダが 32 個ずつメッシュレットを調べ、
    /// 生き残ったものだけメッシュシェーダを立ち上げる。
    /// </summary>
    TaskMesh,

    /// <summary>メッシュシェーダ。1ワークグループが1メッシュレットを受け持つ。<c>vkCmdDrawMeshTasksEXT</c>。</summary>
    MeshShader,

    /// <summary>頂点シェーダ(従来の道)。索引を読む回路が頂点を1つずつ配る。<c>vkCmdDrawIndexed</c>。</summary>
    VertexShader,
}

/// <summary>
/// 1回の描画で、各段が何回走ったか(要点6)。GPU が自分で数えた値。
/// </summary>
/// <param name="VertexShaderInvocations">頂点シェーダが走った回数。メッシュの道では 0。</param>
/// <param name="Triangles">
/// ラスタライザへ渡った三角形の数。<b>どちらの道でも同じになるはず</b>。
/// 頂点の道は「クリップの段に届いた数」、メッシュの道は「メッシュシェーダが出した数」で、
/// <b>数える仕組みが道ごとに別</b>になっている(要点6)。
/// </param>
/// <param name="FragmentShaderInvocations">画素シェーダが走った回数。</param>
internal readonly record struct DrawStatistics(
    ulong VertexShaderInvocations,
    ulong Triangles,
    ulong FragmentShaderInvocations);

/// <summary>どのカリングをするか(Day 64b で追加)。<see cref="FrameUniforms.CullFlags"/> のビットに対応する。</summary>
[Flags]
internal enum CullingMode
{
    None = 0,

    /// <summary>視錐台の外のメッシュレットを捨てる。</summary>
    Frustum = 1,

    /// <summary>三角形が全部裏を向いているメッシュレットを捨てる。</summary>
    Backface = 2,

    Both = Frustum | Backface,
}

/// <summary>
/// タスクシェーダが数えた、メッシュレットの行き先(Day 64b で追加)。単位は「メッシュレット x 体」。
/// 3つの和は <see cref="Tested"/> に一致する(一致しなければ数え方が壊れている)。
/// </summary>
internal readonly record struct CullingStatistics(long Tested, long FrustumCulled, long BackfaceCulled, long Visible);

/// <summary>
/// トーラスノットを何体も描いて、絵を <c>int[]</c> で受け取る(今日の要点1・5・6)。
///
/// <para>
/// Day 62 の <c>ComputeRenderer</c> と同じ役回りで、骨組み(ディスクリプタ → レイアウト → パイプライン →
/// 毎フレームのコマンド → フェンスで待つ → 引き取る)も同じ。違いは3つ。
/// </para>
/// <list type="number">
/// <item><b>パイプラインがグラフィックス</b>になった。2本作る(頂点の道とメッシュの道)</item>
/// <item>描く前後に<b>画像のレイアウトを移し替える</b>(的にする → 引き取れるようにする)</item>
/// <item><b>パイプライン統計</b>で「どの段が何回走ったか」を GPU に数えさせる</item>
/// </list>
/// <para>
/// 2本のパイプラインは<b>同じディスクリプタセットを使う</b>。頂点の道は使わないバッファ(メッシュレットの3本)も
/// 挿さったままになるが、使わない binding は何が挿さっていてもよい(Day 62b と同じ)。
/// </para>
/// <para>
/// <b>Day 64b の差分は4つ</b>。
/// </para>
/// <list type="number">
/// <item>3本目のパイプライン(タスク + メッシュ)。<c>meshlet.mesh</c> を <c>USE_TASK</c> 付きでもう1回翻訳する</item>
/// <item>uniform に<b>カリングの目</b>(位置と視錐台の6面)を足す。描く目と別に持つ</item>
/// <item>数え上げのバッファ(binding 7)。描く前に 0 に戻し、描いたあと CPU で読む。<b>その前後にバリア</b></item>
/// <item>描く命令の意味が変わる。同じ <c>CmdDrawMeshTask</c> で、立ち上げるのがタスクシェーダの組になる</item>
/// </list>
/// </summary>
internal sealed unsafe class MeshRenderer : IDisposable
{
    /// <summary>
    /// タスクシェーダの1ワークグループの人数 = 1ワークグループが調べるメッシュレットの数(Day 64b で追加)。
    /// <b>meshlet.task の <c>local_size_x</c> と、荷物の配列の大きさ(32)と一致させる</b>。
    /// </summary>
    public const int TaskWorkGroupSize = 32;

    /// <summary>空の色。暗い青灰色。</summary>
    private static readonly ClearColorValue Background = new(0.10f, 0.12f, 0.16f, 1.0f);

    private readonly VulkanDevice _device;
    private readonly VulkanImage _color;
    private readonly VulkanImage _depth;

    /// <summary>画像を引き取る先。CPU から map できる置き場所。</summary>
    private readonly VulkanBuffer _readback;

    private readonly VulkanBuffer _frame;
    private readonly VulkanBuffer _vertices;
    private readonly VulkanBuffer _orderedIndices;
    private readonly VulkanBuffer _instances;
    private readonly VulkanBuffer _meshlets;
    private readonly VulkanBuffer _meshletVertices;
    private readonly VulkanBuffer _meshletTriangles;
    private readonly VulkanBuffer _triangleMeshlet;

    /// <summary>タスクシェーダが数えるカリングの内訳(Day 64b で追加)。CPU から読むので CPU から見える置き場所。</summary>
    private readonly VulkanBuffer _cullCounters;

    private readonly uint _indexCount;
    private readonly uint _instanceCount;
    private readonly uint _meshletCount;

    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;

    private readonly GraphicsPipeline _vertexPipeline;

    /// <summary>メッシュの道。メッシュシェーダを持たない GPU では null。</summary>
    private readonly GraphicsPipeline? _meshPipeline;

    /// <summary>タスク + メッシュの道(Day 64b で追加)。タスクシェーダを使えない GPU では null。</summary>
    private readonly GraphicsPipeline? _taskPipeline;

    private readonly CommandPool _ownPool;
    private readonly CommandBuffer _commands;
    private readonly Fence _fence;

    /// <summary>GPU の時計(Day 62a の要点6)。描く命令の前と後の2目盛り。</summary>
    private readonly QueryPool _timestamps;

    /// <summary>
    /// パイプライン統計の入れ物。<b>道ごとに1つ</b>(要点6)。
    ///
    /// <para>
    /// 仕様は、メッシュの描画の間に<b>頂点シェーダ・入力の組み立て・クリップ</b>を数える統計を
    /// 開いておくことを禁じている(VUID-vkCmdDrawMeshTasksEXT-pipelineStatistics-07076)。
    /// メッシュの道にはそれらの段が無いからで、だから数える項目の違う入れ物を2つ用意して、描き方に合わせて使い分ける。
    /// </para>
    /// </summary>
    private readonly QueryPool _vertexStatistics;

    private readonly QueryPool _meshStatistics;

    /// <summary>
    /// メッシュシェーダが出した三角形の数を数える入れ物(<c>VK_QUERY_TYPE_MESH_PRIMITIVES_GENERATED_EXT</c>)。
    /// 頂点の道の「クリップへ届いた数」の代わり。統計とは別の種類のクエリなので、同時に開いておける。
    /// </summary>
    private readonly QueryPool _meshPrimitives;

    public MeshRenderer(
        VulkanDevice device, int width, int height,
        MeshData mesh, MeshletSet meshlets, Matrix4x4[] instances,
        ShaderCompiler compiler, string shaderDirectory)
    {
        _device = device;
        Width = width;
        Height = height;
        Meshlets = meshlets;
        _indexCount = (uint)meshlets.OrderedIndices.Length;
        _instanceCount = (uint)instances.Length;
        _meshletCount = (uint)meshlets.Meshlets.Length;

        Vk vk = device.Api;

        _color = VulkanImage.CreateColor(device, width, height);
        _depth = VulkanImage.CreateDepth(device, width, height);
        _readback = VulkanBuffer.CreateStaging(device, (ulong)(width * height * sizeof(int)), BufferUsageFlags.TransferDstBit);

        // 毎フレーム CPU から書くので、VRAM ではなく CPU から見える置き場所に置く(小さいので遅くない)。
        _frame = VulkanBuffer.CreateStaging(device, (ulong)Unsafe.SizeOf<FrameUniforms>(), BufferUsageFlags.UniformBufferBit);

        // **頂点は1本のバッファを2つの道で読む**。頂点の道は「頂点バッファ」として、
        // メッシュの道は「ストレージバッファ」として。だから使い道を両方宣言しておく。
        _vertices = VulkanBuffer.CreateDeviceLocal<GpuVertex>(
            device, mesh.Vertices, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit);
        _orderedIndices = VulkanBuffer.CreateDeviceLocal<uint>(device, meshlets.OrderedIndices, BufferUsageFlags.IndexBufferBit);
        _instances = VulkanBuffer.CreateDeviceLocal<Matrix4x4>(device, instances, BufferUsageFlags.StorageBufferBit);
        _meshlets = VulkanBuffer.CreateDeviceLocal<GpuMeshlet>(device, meshlets.Meshlets, BufferUsageFlags.StorageBufferBit);
        _meshletVertices = VulkanBuffer.CreateDeviceLocal<uint>(device, meshlets.VertexIndices, BufferUsageFlags.StorageBufferBit);
        _meshletTriangles = VulkanBuffer.CreateDeviceLocal<uint>(device, meshlets.Triangles, BufferUsageFlags.StorageBufferBit);
        _triangleMeshlet = VulkanBuffer.CreateDeviceLocal<uint>(device, meshlets.TriangleMeshlet, BufferUsageFlags.StorageBufferBit);

        // uint 4つ。毎フレーム GPU の命令(CmdFillBuffer)で 0 に戻すので、転送先にもなれるようにしておく。
        _cullCounters = VulkanBuffer.CreateStaging(device, sizeof(uint) * 4, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit);

        // ---- 1. レイアウト(型の宣言)------------------------------------------------
        // どの段から見えるか。**メッシュシェーダを持たない GPU で MeshBitExt を立てると仕様違反**なので、
        // 使えるときだけ足す(Day 62b の「聞いてから使う」)。
        ShaderStageFlags stagesUsed = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
        if (device.MeshShaderSupported)
        {
            stagesUsed |= ShaderStageFlags.MeshBitExt;
        }

        if (device.TaskShaderSupported)
        {
            stagesUsed |= ShaderStageFlags.TaskBitExt;
        }

        // 0 番が uniform buffer、1〜7 番がストレージバッファ。番号は common.glsl の binding と一致させる。
        // 7 番(カリングの数え上げ)は Day 64b で追加。
        const int BindingCount = 8;
        var bindings = stackalloc DescriptorSetLayoutBinding[BindingCount];
        for (int i = 0; i < BindingCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = i == 0 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = stagesUsed,
            };
        }

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = BindingCount,
            PBindings = bindings,
        };
        VulkanDevice.Check(vk.CreateDescriptorSetLayout(device.Handle, &layoutInfo, null, out _setLayout), "vkCreateDescriptorSetLayout");

        // ---- 2. プール(切り出す元)----------------------------------------------------
        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = BindingCount - 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        VulkanDevice.Check(vk.CreateDescriptorPool(device.Handle, &poolInfo, null, out _pool), "vkCreateDescriptorPool");

        // ---- 3. セット(実体)+ 資源を挿す --------------------------------------------
        DescriptorSetLayout setLayout = _setLayout;
        var setAlloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        VulkanDevice.Check(vk.AllocateDescriptorSets(device.Handle, &setAlloc, out _set), "vkAllocateDescriptorSets");

        // 並びは binding の番号順。common.glsl の一覧と見比べながら読む。
        Buffer[] buffers =
        [
            _frame.Handle,             // 0: Frame(uniform)
            _vertices.Handle,          // 1: 頂点
            _instances.Handle,         // 2: 体ごとの行列
            _meshlets.Handle,          // 3: メッシュレット
            _meshletVertices.Handle,   // 4: メッシュレットの局所番号 → 元の頂点番号
            _meshletTriangles.Handle,  // 5: 局所番号3つを詰めた三角形
            _triangleMeshlet.Handle,   // 6: 三角形 → メッシュレット(色分け用)
            _cullCounters.Handle,      // 7: カリングの数え上げ(Day 64b。タスクシェーダが書く)
        ];

        var bufferInfos = stackalloc DescriptorBufferInfo[BindingCount];
        var writes = stackalloc WriteDescriptorSet[BindingCount];
        for (int i = 0; i < BindingCount; i++)
        {
            bufferInfos[i] = new DescriptorBufferInfo { Buffer = buffers[i], Offset = 0, Range = Vk.WholeSize };
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = bindings[i].DescriptorType,
                PBufferInfo = &bufferInfos[i],
            };
        }

        vk.UpdateDescriptorSets(device.Handle, BindingCount, writes, 0, null);

        // ---- 4. パイプラインレイアウト --------------------------------------------------
        // プッシュ定数は今日は使わない(値は全部 uniform buffer に入れた)。
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
        };
        VulkanDevice.Check(vk.CreatePipelineLayout(device.Handle, &pipelineLayoutInfo, null, out _pipelineLayout), "vkCreatePipelineLayout");

        // ---- 5. パイプライン(2本)------------------------------------------------------
        // **画素シェーダは2本で同じもの**。その手前が「頂点シェーダ」か「メッシュシェーダ」かだけが違う。
        uint[] fragment = compiler.CompileFile(Path.Combine(shaderDirectory, "shade.frag"), ShaderKind.FragmentShader);
        uint[] vertex = compiler.CompileFile(Path.Combine(shaderDirectory, "scene.vert"), ShaderKind.VertexShader);
        _vertexPipeline = GraphicsPipeline.Create(
            device, _pipelineLayout, width, height,
            [(ShaderStageFlags.VertexBit, vertex), (ShaderStageFlags.FragmentBit, fragment)],
            vertexInput: true);

        if (device.MeshShaderSupported)
        {
            uint[] meshShader = compiler.CompileFile(Path.Combine(shaderDirectory, "meshlet.mesh"), ShaderKind.MeshShader);
            _meshPipeline = GraphicsPipeline.Create(
                device, _pipelineLayout, width, height,
                [(ShaderStageFlags.MeshBitExt, meshShader), (ShaderStageFlags.FragmentBit, fragment)],
                vertexInput: false);
        }

        // Day 64b: タスク + メッシュの道。**同じ meshlet.mesh を USE_TASK 付きでもう1回翻訳する**
        // (Day 62b の trace.comp と同じ手)。段が1つ増えるだけで、固定機能の設定は同じ。
        if (device.TaskShaderSupported)
        {
            uint[] taskShader = compiler.CompileFile(Path.Combine(shaderDirectory, "meshlet.task"), ShaderKind.TaskShader);
            uint[] meshAfterTask = compiler.CompileFile(Path.Combine(shaderDirectory, "meshlet.mesh"), ShaderKind.MeshShader, "USE_TASK");
            _taskPipeline = GraphicsPipeline.Create(
                device, _pipelineLayout, width, height,
                [(ShaderStageFlags.TaskBitExt, taskShader), (ShaderStageFlags.MeshBitExt, meshAfterTask), (ShaderStageFlags.FragmentBit, fragment)],
                vertexInput: false);
        }

        // ---- 6. 毎フレーム使い回すコマンドバッファとフェンス --------------------------
        _ownPool = CreatePool(device);
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _ownPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        VulkanDevice.Check(vk.AllocateCommandBuffers(device.Handle, &allocInfo, out _commands), "vkAllocateCommandBuffers");

        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        VulkanDevice.Check(vk.CreateFence(device.Handle, &fenceInfo, null, out _fence), "vkCreateFence");

        // ---- 7. 計測の入れ物 ------------------------------------------------------------
        _timestamps = CreateQueryPool(device, QueryType.Timestamp, 2, 0);

        // 頂点の道で数えるもの: 頂点シェーダ / クリップ / 画素シェーダ。
        _vertexStatistics = CreateQueryPool(
            device, QueryType.PipelineStatistics, 1,
            QueryPipelineStatisticFlags.VertexShaderInvocationsBit
            | QueryPipelineStatisticFlags.ClippingInvocationsBit
            | QueryPipelineStatisticFlags.FragmentShaderInvocationsBit);

        // メッシュの道で数えるもの: 画素シェーダ + 出した三角形の数(別のクエリ)。
        //
        // **メッシュシェーダの回数(MeshShaderInvocationsBitExt)は数えない**。
        // 数えられるし仕様にもあるが、手元の RTX 3070(ドライバ 596.49)では
        // これを立てた統計を開いているだけで、メッシュの道が 0.07ms → 2.4ms と 34 倍遅くなった
        // (中身が空のメッシュシェーダでも同じ。Day64a.md の「検証の途中で分かったこと」)。
        // 回数は「メッシュレットの数 × 体の数 × 32 人」と決まっているので、数えなくても分かる。
        if (device.MeshShaderSupported)
        {
            _meshStatistics = CreateQueryPool(
                device, QueryType.PipelineStatistics, 1,
                QueryPipelineStatisticFlags.FragmentShaderInvocationsBit);
            _meshPrimitives = CreateQueryPool(device, QueryType.MeshPrimitivesGeneratedExt, 1, 0);
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>描いているメッシュレット(HUD に数を出すため)。</summary>
    public MeshletSet Meshlets { get; }

    /// <summary>
    /// メッシュの道で1回に立ち上げるワークグループの数(= メッシュレットの数 × 体の数)。
    /// 1つが 32 人なので、メッシュシェーダが走る回数はこの 32 倍になる。
    /// </summary>
    public long MeshWorkGroups => (long)_meshletCount * _instanceCount;

    /// <summary>メッシュの道を持っているか。</summary>
    public bool MeshShaderAvailable => _meshPipeline is not null;

    /// <summary>タスク + メッシュの道を持っているか(Day 64b で追加)。</summary>
    public bool TaskShaderAvailable => _taskPipeline is not null;

    /// <summary>
    /// タスクの道で1回に立ち上げるタスクのワークグループの数(Day 64b で追加)。
    /// 1体あたり ceil(メッシュレットの数 / 32)。343 個なら 11。
    /// </summary>
    public long TaskWorkGroups => (long)TaskGroupsPerInstance * _instanceCount;

    private uint TaskGroupsPerInstance => (_meshletCount + TaskWorkGroupSize - 1) / TaskWorkGroupSize;

    /// <summary>
    /// 直前の1フレームを CPU 側から見た時間(ms)。<b>引き取りの転送と提出の往復を含む</b>。
    /// </summary>
    public double LastFrameMilliseconds { get; private set; }

    /// <summary>
    /// 直前の1フレームで<b>描く命令だけ</b>にかかった時間(ms)。GPU の時計で測ったもの。
    /// 2つの道を比べるのはこちら。
    /// </summary>
    public double LastDrawMilliseconds { get; private set; }

    /// <summary>直前の1フレームのパイプライン統計。</summary>
    public DrawStatistics LastStatistics { get; private set; }

    /// <summary>
    /// 直前の1フレームでタスクシェーダが数えた、メッシュレットの行き先(Day 64b で追加)。
    /// タスクの道でないときは、全部が「生き残り」になる(捨てる段が無いので)。
    /// </summary>
    public CullingStatistics LastCulling { get; private set; }

    /// <summary>
    /// いま実際に使われる描き方。持っていない道を頼まれたら、1段ずつ簡単な道に落ちる
    /// (タスク → メッシュ → 頂点)。HUD も同じ判断をするので、ここに1つだけ書く(Day 64b で追加)。
    /// </summary>
    public DrawPath Effective(DrawPath path) => path switch
    {
        DrawPath.TaskMesh when _taskPipeline is null => Effective(DrawPath.MeshShader),
        DrawPath.MeshShader when _meshPipeline is null => DrawPath.VertexShader,
        _ => path,
    };

    /// <summary>
    /// 1枚描いて <paramref name="destination"/> に受け取る。
    /// </summary>
    /// <param name="camera">描く目。</param>
    /// <param name="cullCamera">
    /// カリングの目(Day 64b で追加)。ふだんは <paramref name="camera"/> と同じものを渡す。
    /// 別のものを渡すと、「その目から見て要らないもの」が捨てられた世界を、描く目から眺められる。
    /// </param>
    /// <param name="mode">表示の種類(<see cref="FrameUniforms.Mode"/>)。</param>
    /// <param name="path">描き方。持っていない道なら <see cref="Effective"/> のとおり落ちる。</param>
    /// <param name="culling">どのカリングをするか。タスクの道のときだけ効く。</param>
    public void Render(Camera camera, Camera cullCamera, int mode, DrawPath path, CullingMode culling, int[] destination)
    {
        var clock = Stopwatch.StartNew();
        Vk vk = _device.Api;
        DrawPath actual = Effective(path);
        bool useTask = actual == DrawPath.TaskMesh;
        bool useMesh = actual != DrawPath.VertexShader;

        // 1フレームぶんの値を書く。**前のフレームの描画が終わっている**(下でフェンスを待っている)ので、
        // GPU が読んでいる最中に書き換える心配は無い。
        var uniforms = new FrameUniforms
        {
            ViewProjection = camera.ViewProjection,
            CameraPosition = new Vector4(camera.Position, 1.0f),
            Mode = (uint)mode,
            MeshletCount = _meshletCount,
            CullFlags = (uint)culling,
            CullPosition = new Vector4(cullCamera.Position, 1.0f),
        };
        Vector4[] planes = cullCamera.FrustumPlanes();
        for (int i = 0; i < planes.Length; i++)
        {
            uniforms.Frustum[i] = planes[i];
        }

        _frame.Write(new ReadOnlySpan<FrameUniforms>(ref uniforms));

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VulkanDevice.Check(vk.ResetCommandBuffer(_commands, 0), "vkResetCommandBuffer");
        VulkanDevice.Check(vk.BeginCommandBuffer(_commands, &begin), "vkBeginCommandBuffer");

        // 計測の入れ物の reset は**描画の外で**しかできない(CmdBeginRendering より前)。
        QueryPool statistics = useMesh ? _meshStatistics : _vertexStatistics;
        vk.CmdResetQueryPool(_commands, _timestamps, 0, 2);
        vk.CmdResetQueryPool(_commands, statistics, 0, 1);
        if (useMesh)
        {
            vk.CmdResetQueryPool(_commands, _meshPrimitives, 0, 1);
        }

        // カリングの数え上げを 0 に戻す(Day 64b)。これも**描画の外で**しかできない転送の命令。
        // 転送で書いてからタスクシェーダが足し込むので、間に「転送の書き込み → タスクシェーダの読み書き」のバリアを置く。
        if (useTask)
        {
            vk.CmdFillBuffer(_commands, _cullCounters.Handle, 0, _cullCounters.Size, 0);
            var clear = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            };
            vk.CmdPipelineBarrier(_commands, PipelineStageFlags.TransferBit, PipelineStageFlags.TaskShaderBitExt, 0, 1, &clear, 0, null, 0, null);
        }

        // ---- 的を用意する(要点5)------------------------------------------------------
        // 色: 前のフレームの「引き取り(転送で読む)」が終わってから、的として書き始める。
        //     Undefined から移すので中身は捨てる(どうせ全部塗り直す)。
        _color.RecordBarrier(
            _commands, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags.TransferBit, 0,
            PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.ColorAttachmentWriteBit);

        // 深度: 前のフレームの深度の書き込みが終わってから。深度を触る段は2つある
        //       (画素シェーダの前の早期テストと、後の遅延テスト)ので、両方を挙げる。
        _depth.RecordBarrier(
            _commands, ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal,
            PipelineStageFlags.LateFragmentTestsBit, AccessFlags.DepthStencilAttachmentWriteBit,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
            AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);

        // ---- 描き始める(dynamic rendering)--------------------------------------------
        // 描画パスもフレームバッファも作らず、**その場で的を渡す**。
        // Clear = 描き始めに塗りつぶす / Store = 描き終わったら中身を残す(引き取るので要る)。
        // 深度は引き取らないので DontCare(捨ててよい。タイル型の GPU ではメモリへの書き戻しが省ける)。
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _color.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(color: Background),
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _depth.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1.0f, 0)),
        };
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)Width, (uint)Height)),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };
        vk.CmdBeginRendering(_commands, &renderingInfo);

        vk.CmdWriteTimestamp(_commands, PipelineStageFlags.TopOfPipeBit, _timestamps, 0);
        vk.CmdBeginQuery(_commands, statistics, 0, 0);
        if (useMesh)
        {
            vk.CmdBeginQuery(_commands, _meshPrimitives, 0, 0);
        }

        GraphicsPipeline pipeline = actual switch
        {
            DrawPath.TaskMesh => _taskPipeline!,
            DrawPath.MeshShader => _meshPipeline!,
            _ => _vertexPipeline,
        };
        vk.CmdBindPipeline(_commands, PipelineBindPoint.Graphics, pipeline.Handle);
        DescriptorSet set = _set;
        vk.CmdBindDescriptorSets(_commands, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);

        if (useTask)
        {
            // Day 64b: 立ち上げるのは**タスクシェーダ**のワークグループ。x = 32 個ずつの組の数、y = 体の数。
            // メッシュシェーダのワークグループを何組立ち上げるかは、タスクシェーダが GPU の上で決める。
            // 同じ関数(vkCmdDrawMeshTasksEXT)だが、パイプラインにタスクの段があるかどうかで意味が変わる。
            _device.MeshShaderApi!.CmdDrawMeshTask(_commands, TaskGroupsPerInstance, _instanceCount, 1);
        }
        else if (useMesh)
        {
            // **頂点バッファも索引バッファも挿さない**。渡すのはワークグループの数だけ。
            // x = メッシュレットの数、y = 体の数。シェーダは gl_WorkGroupID.xy で「どの体のどのメッシュレットか」を知る。
            // (C の vkCmdDrawMeshTasksEXT。Silk.NET では末尾の s が落ちて CmdDrawMeshTask という名前になっている)
            _device.MeshShaderApi!.CmdDrawMeshTask(_commands, _meshletCount, _instanceCount, 1);
        }
        else
        {
            // 従来の道。頂点バッファと索引バッファを挿して、索引の数と体の数を渡す。
            // 入力の回路が索引を3つずつ読み、頂点を1つずつ頂点シェーダに配る。
            Buffer vertexBuffer = _vertices.Handle;
            ulong offset = 0;
            vk.CmdBindVertexBuffers(_commands, 0, 1, &vertexBuffer, &offset);
            vk.CmdBindIndexBuffer(_commands, _orderedIndices.Handle, 0, IndexType.Uint32);
            vk.CmdDrawIndexed(_commands, _indexCount, _instanceCount, 0, 0, 0);
        }

        if (useMesh)
        {
            vk.CmdEndQuery(_commands, _meshPrimitives, 0);
        }

        vk.CmdEndQuery(_commands, statistics, 0);
        vk.CmdWriteTimestamp(_commands, PipelineStageFlags.BottomOfPipeBit, _timestamps, 1);
        vk.CmdEndRendering(_commands);

        // ---- 引き取る --------------------------------------------------------------------
        // 的として書き終わってから、転送で読めるレイアウトへ移す。
        _color.RecordBarrier(
            _commands, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.ColorAttachmentWriteBit,
            PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit);
        _color.RecordCopyToBuffer(_commands, _readback);

        // CPU から読む前に「転送で書いたものを、CPU(Host)から見えるようにせよ」と言っておく。
        // **フェンスを待つだけでは足りない**。フェンスが面倒を見るのは GPU の中の書き込みまでだ、と
        // 仕様の注記にはっきり書いてある。Day 62 はこれを省いていた(NVIDIA では省いても困らないが、仕様どおりに書く)。
        // Day 64b: タスクシェーダが書いた数え上げも CPU で読むので、書いた段と種類を足す。
        var toHost = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
        };
        PipelineStageFlags written = useTask
            ? PipelineStageFlags.TransferBit | PipelineStageFlags.TaskShaderBitExt
            : PipelineStageFlags.TransferBit;
        vk.CmdPipelineBarrier(_commands, written, PipelineStageFlags.HostBit, 0, 1, &toHost, 0, null, 0, null);

        VulkanDevice.Check(vk.EndCommandBuffer(_commands), "vkEndCommandBuffer");

        CommandBuffer commands = _commands;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commands,
        };

        Fence fence = _fence;
        VulkanDevice.Check(vk.ResetFences(_device.Handle, 1, &fence), "vkResetFences");
        VulkanDevice.Check(vk.QueueSubmit(_device.Queue, 1, &submit, fence), "vkQueueSubmit");
        VulkanDevice.Check(vk.WaitForFences(_device.Handle, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");

        ulong* timestamps = stackalloc ulong[2];
        VulkanDevice.Check(
            vk.GetQueryPoolResults(_device.Handle, _timestamps, 0, 2, sizeof(ulong) * 2, timestamps, sizeof(ulong), QueryResultFlags.Result64Bit),
            "vkGetQueryPoolResults");
        LastDrawMilliseconds = (timestamps[1] - timestamps[0]) * _device.TimestampPeriod / 1_000_000.0;

        // パイプライン統計は**立てたビットの小さい順**に並んで返ってくる(仕様)。
        // 頂点の道: [頂点シェーダ, クリップ, 画素]  メッシュの道: [画素]
        ulong* counts = stackalloc ulong[3];
        uint countLength = useMesh ? 1u : 3u;
        VulkanDevice.Check(
            vk.GetQueryPoolResults(_device.Handle, statistics, 0, 1, sizeof(ulong) * countLength, counts, sizeof(ulong) * countLength, QueryResultFlags.Result64Bit),
            "vkGetQueryPoolResults");

        if (useMesh)
        {
            ulong primitives = 0;
            VulkanDevice.Check(
                vk.GetQueryPoolResults(_device.Handle, _meshPrimitives, 0, 1, sizeof(ulong), &primitives, sizeof(ulong), QueryResultFlags.Result64Bit),
                "vkGetQueryPoolResults");
            LastStatistics = new DrawStatistics(0, primitives, counts[0]);
        }
        else
        {
            LastStatistics = new DrawStatistics(counts[0], counts[1], counts[2]);
        }

        // カリングの内訳(Day 64b)。タスクの道でないときは、捨てる段が無いので全部が生き残り。
        long tested = (long)_meshletCount * _instanceCount;
        if (useTask)
        {
            var culled = new int[4];
            _cullCounters.Read(culled);
            LastCulling = new CullingStatistics(tested, culled[0], culled[1], culled[2]);
        }
        else
        {
            LastCulling = new CullingStatistics(tested, 0, 0, tested);
        }

        _readback.Read(destination);
        LastFrameMilliseconds = clock.Elapsed.TotalMilliseconds;
    }

    public void Dispose()
    {
        Vk vk = _device.Api;
        vk.DeviceWaitIdle(_device.Handle);

        vk.DestroyQueryPool(_device.Handle, _timestamps, null);
        vk.DestroyQueryPool(_device.Handle, _vertexStatistics, null);
        if (_meshStatistics.Handle != 0)
        {
            vk.DestroyQueryPool(_device.Handle, _meshStatistics, null);
            vk.DestroyQueryPool(_device.Handle, _meshPrimitives, null);
        }

        vk.DestroyFence(_device.Handle, _fence, null);
        vk.DestroyCommandPool(_device.Handle, _ownPool, null);

        _taskPipeline?.Dispose();
        _meshPipeline?.Dispose();
        _vertexPipeline.Dispose();
        vk.DestroyPipelineLayout(_device.Handle, _pipelineLayout, null);

        // セットはプールごと壊れるので、個別に解放しなくてよい。
        vk.DestroyDescriptorPool(_device.Handle, _pool, null);
        vk.DestroyDescriptorSetLayout(_device.Handle, _setLayout, null);

        _cullCounters.Dispose();
        _triangleMeshlet.Dispose();
        _meshletTriangles.Dispose();
        _meshletVertices.Dispose();
        _meshlets.Dispose();
        _instances.Dispose();
        _orderedIndices.Dispose();
        _vertices.Dispose();
        _frame.Dispose();
        _readback.Dispose();
        _depth.Dispose();
        _color.Dispose();
    }

    private static CommandPool CreatePool(VulkanDevice device)
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = device.QueueFamilyIndex,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        VulkanDevice.Check(device.Api.CreateCommandPool(device.Handle, &poolInfo, null, out CommandPool pool), "vkCreateCommandPool");
        return pool;
    }

    private static QueryPool CreateQueryPool(VulkanDevice device, QueryType type, uint count, QueryPipelineStatisticFlags statistics)
    {
        var queryInfo = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = type,
            QueryCount = count,
            PipelineStatistics = statistics,
        };
        VulkanDevice.Check(device.Api.CreateQueryPool(device.Handle, &queryInfo, null, out QueryPool pool), "vkCreateQueryPool");
        return pool;
    }
}
