using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace CpuRayTracer;

/// <summary>
/// 材質1つぶん。<b>48 バイト</b>。<c>pathtrace.comp</c> の <c>struct Material</c> と1バイトずつ同じ。
///
/// <para>
/// CPU 側の <see cref="Material"/> は <c>MaterialKind</c> の enum と6つのプロパティを持つクラスだが、
/// GPU へ送れるのは<b>数の並び</b>だけ。種類も float に詰める(<c>Kind</c> = 0/1/2)。
/// </para>
/// <para>
/// 詰め方は <b>vec4 を3本</b>。std430 では <c>vec3</c> が 16 バイトに整列するので、
/// 「vec3 のあとに float」を3組にすると穴が空かない(Day 57 の <c>GpuParticle</c> と同じ手)。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuMaterial
{
    /// <summary>rgb = 拡散の反射率(金属は F0)、w = 市松の1マスの大きさ(0 なら無地)。</summary>
    public Vector4 Albedo;

    /// <summary>rgb = 市松のもう一方の色、w = 屈折率。</summary>
    public Vector4 Checker;

    /// <summary>rgb = 発光(放射輝度)、w = 種類(0 拡散 / 1 金属 / 2 ガラス)。</summary>
    public Vector4 Emission;
}

/// <summary>球1つぶん。<b>32 バイト</b>。xyz = 中心、w = 半径。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuSphere
{
    public Vector4 CenterRadius;

    /// <summary>材質の<b>番号</b>。参照ではなく番号なのが、GPU へ送るときの一番の違い(要点4)。</summary>
    public int Material;

    private int _pad0;

    private int _pad1;

    private int _pad2;
}

/// <summary>無限の平面1枚ぶん。<b>32 バイト</b>。xyz = 法線、w = 原点からの符号付き距離。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuPlane
{
    public Vector4 NormalDistance;

    public int Material;

    private int _pad0;

    private int _pad1;

    private int _pad2;
}

/// <summary>
/// BVH の節点1つぶん。<b>48 バイト</b>。
/// <see cref="Count"/> が 0 なら内側の節点で <see cref="Index"/> は右の子、
/// 1 以上なら葉で <see cref="Index"/> は球の配列の開始位置(Day 60 の要点8)。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuNode
{
    public Vector4 BoundsMin;

    public Vector4 BoundsMax;

    public int Index;

    public int Count;

    public int Axis;

    private int _pad;
}

/// <summary>点光源1つぶん。<b>32 バイト</b>。xyz = 位置 / rgb = 色、w = 強さ。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuPointLight
{
    public Vector4 Position;

    public Vector4 ColorIntensity;
}

/// <summary>
/// 場面を GPU の配列に平らにしたもの(<b>今日の要点4</b>)。
///
/// <para>
/// Day 60 の設計書で「今日残した歪み1」として挙げたのがここ——
/// <c>Shape</c> が <c>Material</c> を<b>参照で</b>持っていた。GPU には参照が無いので、
/// <b>材質の配列を1本作り、形には「材質の番号」を持たせる</b>形に組み直す。
/// これはグラフィックスでは避けて通れない作り替えで、<b>bindless / インデックス参照</b>と呼ばれる形の入り口。
/// </para>
/// <para>
/// 送るのは6本の SSBO。
/// </para>
/// <list type="number">
/// <item><b>材質</b>(0) … 同じ <see cref="Material"/> の参照は1つにまとめる</item>
/// <item><b>球</b>(1) … <b>BVH が並べ替えた順</b>で送る。葉が「何番目から何個」で指すので、順が命</item>
/// <item><b>平面</b>(2) … 箱で囲めないので BVH に入らない。毎回総当たり(Day 60 の要点7)</item>
/// <item><b>BVH の節点</b>(3) … 総当たりのときは 0 個</item>
/// <item><b>点光源</b>(4)</item>
/// <item><b>面光源</b>(5) … 球の配列の中の<b>番号</b>。<see cref="Scene.AreaLights"/> と同じ順に並べる
/// (順が変わると NEE の光源選び <c>rng.NextInt(total)</c> が CPU と別の光源を指す)</item>
/// </list>
///
/// <para>
/// <b>並びがずれても絵は出る</b>のがいちばん厄介なところ。ずれたぶんだけ「半径を材質の番号として読む」
/// ことになり、真っ黒な玉が並ぶだけで、エラーは1つも出ない。
/// だから <see cref="GpuScene"/> の各構造体の大きさを自己チェック19 が GLSL 側と突き合わせている。
/// </para>
/// </summary>
internal sealed class GpuScene : IDisposable
{
    /// <summary>SSBO を挿す口の番号。<b>GLSL の <c>layout(std430, binding = n)</c> と揃っていること</b>。</summary>
    public const int MaterialBinding = 0;

    public const int SphereBinding = 1;

    public const int PlaneBinding = 2;

    public const int NodeBinding = 3;

    public const int PointLightBinding = 4;

    public const int AreaLightBinding = 5;

    /// <summary>画面に出す画素(<see cref="GpuRenderer"/> が持つ)。</summary>
    public const int PixelBinding = 6;

    /// <summary>光線の数の集計(<see cref="GpuRenderer"/> が持つ)。</summary>
    public const int CounterBinding = 7;

    private readonly GL _gl;

    private readonly uint[] _buffers;

    private GpuScene(GL gl, uint[] buffers)
    {
        _gl = gl;
        _buffers = buffers;
    }

    public int MaterialCount { get; private init; }

    public int SphereCount { get; private init; }

    public int PlaneCount { get; private init; }

    /// <summary>BVH の節点の数。<b>0 なら総当たり</b>(GLSL 側もそれで分岐する)。</summary>
    public int NodeCount { get; private init; }

    public int PointLightCount { get; private init; }

    public int AreaLightCount { get; private init; }

    /// <summary>
    /// 場面を GPU へ送る。<b><see cref="Scene.Prepare"/> を呼んだ後</b>に使うこと
    /// (BVH ができていないと、球の並びが決まらない)。
    /// </summary>
    public static GpuScene Build(GL gl, Scene scene)
    {
        // --- 材質: 参照を番号に変える ---
        // Material は Equals を書いていないので、既定の比較がそのまま「同じ参照か」になる。
        var materialIndex = new Dictionary<Material, int>();
        var materials = new List<GpuMaterial>();

        int IndexOf(Material material)
        {
            if (materialIndex.TryGetValue(material, out int index))
            {
                return index;
            }

            index = materials.Count;
            materialIndex[material] = index;
            materials.Add(new GpuMaterial
            {
                Albedo = new Vector4(material.Albedo, material.CheckerSize),
                Checker = new Vector4(material.CheckerAlbedo, material.Ior),
                Emission = new Vector4(material.Emission, (float)(int)material.Kind),
            });
            return index;
        }

        // --- 球: BVH が並べ替えた順で。総当たりのときは場面に書いた順 ---
        IReadOnlyList<Shape> orderedShapes = scene.Bvh is Bvh bvh
            ? bvh.OrderedShapes
            : scene.Shapes.Where(s => s is Sphere).ToArray();

        var spheres = new List<GpuSphere>();
        var sphereIndex = new Dictionary<Shape, int>();
        foreach (Shape shape in orderedShapes)
        {
            if (shape is not Sphere sphere)
            {
                continue;
            }

            sphereIndex[sphere] = spheres.Count;
            spheres.Add(new GpuSphere
            {
                CenterRadius = new Vector4(sphere.Center, sphere.Radius),
                Material = IndexOf(sphere.Material),
            });
        }

        // --- 平面: 場面に書いた順(CPU 側の総当たりの順と揃える) ---
        var planes = new List<GpuPlane>();
        foreach (Shape shape in scene.Shapes)
        {
            if (shape is not Plane plane)
            {
                continue;
            }

            planes.Add(new GpuPlane
            {
                NormalDistance = new Vector4(plane.Normal, plane.Distance),
                Material = IndexOf(plane.Material),
            });
        }

        // --- BVH の節点 ---
        var nodes = new List<GpuNode>();
        if (scene.Bvh is Bvh tree)
        {
            for (int i = 0; i < tree.NodeCount; i++)
            {
                Bvh.NodeView node = tree.GetNode(i);
                nodes.Add(new GpuNode
                {
                    BoundsMin = new Vector4(node.Bounds.Min, 0.0f),
                    BoundsMax = new Vector4(node.Bounds.Max, 0.0f),
                    Index = node.Index,
                    Count = node.Count,
                    Axis = node.Axis,
                });
            }
        }

        // --- 光源 ---
        var pointLights = scene.Lights
            .Select(light => new GpuPointLight
            {
                Position = new Vector4(light.Position, 0.0f),
                ColorIntensity = new Vector4(light.Color, light.Intensity),
            })
            .ToList();

        // 面光源は「球の配列の中の番号」。**Scene.AreaLights と同じ順**であることが要る。
        var areaLights = new List<int>();
        foreach (Shape shape in scene.AreaLights)
        {
            if (sphereIndex.TryGetValue(shape, out int index))
            {
                areaLights.Add(index);
            }
        }

        var buffers = new uint[6];
        gl.GenBuffers((uint)buffers.Length, buffers.AsSpan());
        Upload(gl, buffers[0], materials);
        Upload(gl, buffers[1], spheres);
        Upload(gl, buffers[2], planes);
        Upload(gl, buffers[3], nodes);
        Upload(gl, buffers[4], pointLights);
        Upload(gl, buffers[5], areaLights);

        return new GpuScene(gl, buffers)
        {
            MaterialCount = materials.Count,
            SphereCount = spheres.Count,
            PlaneCount = planes.Count,
            NodeCount = nodes.Count,
            PointLightCount = pointLights.Count,
            AreaLightCount = areaLights.Count,
        };
    }

    /// <summary>6本の SSBO を口に挿す。ディスパッチの前に1回呼べばよい。</summary>
    public void Bind()
    {
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, MaterialBinding, _buffers[0]);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, SphereBinding, _buffers[1]);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, PlaneBinding, _buffers[2]);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, NodeBinding, _buffers[3]);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, PointLightBinding, _buffers[4]);
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, AreaLightBinding, _buffers[5]);
    }

    /// <summary>
    /// 配列を1本の SSBO にする。
    ///
    /// <para>
    /// <b>空でも 1 要素ぶん確保する</b>。中身が 0 バイトのバッファを挿すと、
    /// シェーダ側の <c>buffer { Sphere items[]; }</c> の長さが 0 になり、
    /// ドライバによっては「読まないのに」警告やエラーを出す。数は uniform で別に渡すので、
    /// 1 要素の無駄は誰も読まない。
    /// </para>
    /// </summary>
    private static void Upload<T>(GL gl, uint buffer, IReadOnlyList<T> items)
        where T : unmanaged
    {
        // 空でも 1 要素ぶん確保するが、**渡された一覧は書き換えない**。
        // ここで items に詰め物を足すと、呼ぶ側が後から数える Count がその 1 個ぶん増える——
        // それが「点光源が 0 個の場面に、真っ黒な点光源が1つ生える」という形で絵に出た(検証2)。
        var data = new T[Math.Max(1, items.Count)];
        for (int i = 0; i < items.Count; i++)
        {
            data[i] = items[i];
        }

        int stride = Unsafe.SizeOf<T>();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        gl.BufferData<T>(BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * stride), data, BufferUsageARB.StaticDraw);
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    public void Dispose() => _gl.DeleteBuffers((uint)_buffers.Length, _buffers.AsSpan());
}
