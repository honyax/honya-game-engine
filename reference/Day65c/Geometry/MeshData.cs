using System.Numerics;
using System.Runtime.InteropServices;

namespace MeshletRenderer;

/// <summary>
/// GPU に渡す頂点1つ。<b>32 バイト</b>。
///
/// <para>
/// 位置と法線はどちらも <c>vec3</c> だが、<b>それぞれ 16 バイトに揃えて</b>並べる。
/// メッシュシェーダはこの配列を<b>ストレージバッファとして自分で読む</b>ので、
/// GLSL の std430 の並べ方(<c>vec3</c> は 16 バイト境界に乗る)に合わせておく必要がある。
/// 頂点シェーダの道は同じバッファを<b>頂点バッファとして</b>読む(こちらは並びを自由に宣言できる)。
/// 1本のバッファを両方の道で読むために、厳しいほうの決まりに合わせた。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuVertex
{
    public Vector3 Position;

    /// <summary>std430 の境界合わせ。使わない。</summary>
    public float Padding0;

    public Vector3 Normal;

    public float Padding1;

    public GpuVertex(Vector3 position, Vector3 normal)
    {
        Position = position;
        Normal = normal;
    }

    /// <summary>法線が構造体の先頭から何バイト目にあるか。頂点シェーダの道の属性の宣言に使う。</summary>
    public const uint NormalOffset = 16;
}

/// <summary>
/// 頂点と索引の組。<b>三角形の並び(索引)がそのまま「元のメッシュ」</b>で、
/// メッシュレットはこれを小分けにしたもの(<see cref="MeshletBuilder"/>)。
/// </summary>
/// <param name="Vertices">頂点。</param>
/// <param name="Indices">3つずつで三角形1枚。表から見て反時計回り。</param>
internal sealed record MeshData(GpuVertex[] Vertices, uint[] Indices)
{
    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// 形全体の境界の球(Day 65a で追加)。xyz = 中心、w = 半径(模型の座標)。
    ///
    /// <para>
    /// 作り方はメッシュレットの球(<c>MeshletBuilder</c> の <c>ComputeSphere</c>)と同じで、
    /// 頂点を囲む箱の中心から、いちばん遠い頂点までを半径にする。
    /// <b>体ごとのカリング</b>は、この球を体の行列で運んで視錐台と比べるだけ。
    /// 形が1つしか無いので、球も1つで足りる(形が何種類もあれば、形ごとに1つ持つ)。
    /// </para>
    /// </summary>
    public Vector4 ComputeBoundingSphere()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (GpuVertex v in Vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        Vector3 center = (min + max) * 0.5f;
        float radius = 0.0f;
        foreach (GpuVertex v in Vertices)
        {
            radius = MathF.Max(radius, Vector3.Distance(center, v.Position));
        }

        return new Vector4(center, radius);
    }
}
