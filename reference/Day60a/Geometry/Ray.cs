using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 光線。<b>始点から、向きへまっすぐ伸びる半直線</b> <c>P(t) = Origin + t × Direction</c>(t ≥ 0)。
///
/// <para>
/// <b>Direction は必ず長さ 1 にしておく</b>。そうすると t がそのまま「始点からの距離(m)」になり、
/// 次の3つがどれも単位を気にせず書ける。
/// </para>
/// <list type="bullet">
/// <item>影の光線を「光源までの距離の手前まで」で打ち切る(<see cref="Scene.IsOccluded"/>)</item>
/// <item>自己交差を避けるずらし幅を m で決める(<see cref="WhittedTracer.SurfaceOffset"/>)</item>
/// <item>球との交差で2次方程式の a が 1 になり、式が1項減る(<see cref="Sphere.Intersect"/>)</item>
/// </list>
///
/// <para>
/// RTIOW は長さ 1 を仮定しない書き方をしているが、Day 61 で GLSL に移すときも
/// 「向きは正規化済み」を約束にしておいたほうが式が短くなる。
/// </para>
/// </summary>
/// <param name="Origin">始点(ワールド座標、m)。</param>
/// <param name="Direction">向き。<b>長さ 1</b>。</param>
internal readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    /// <summary>始点から距離 t だけ進んだ点。</summary>
    public Vector3 At(float t) => Origin + Direction * t;
}
