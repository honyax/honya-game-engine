using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 無限に広がる平面。<c>Normal · P = Distance</c> を満たす点の集まり。
///
/// <para>
/// ラスタライズでは「無限の床」は描けなかった(三角形は有限なので、大きな板で代用した)。
/// レイトレースでは<b>式1本で本当に無限</b>にできる。地平線まで続く市松模様は、
/// 今日の要点6(アンチエイリアス)をいちばん分かりやすく見せてくれる。
/// </para>
/// <para>
/// 表裏の区別は持たない(両面)。<see cref="OutwardNormal"/> は常に <see cref="Normal"/> を返し、
/// 裏から当たったかどうかは <see cref="WhittedTracer"/> が光線の向きで判断する。
/// 合わせ鏡(場面3)の壁は、この性質のおかげで片側から見ても反対側から見ても鏡になる。
/// </para>
/// </summary>
internal sealed class Plane : Shape
{
    /// <param name="normal">面の向き。長さ 1 にしてから渡すこと。</param>
    /// <param name="distance">原点から面までの符号付き距離。床(y = 0、上向き)なら 0。</param>
    public Plane(string name, Vector3 normal, float distance, Material material)
        : base(name, material)
    {
        Normal = normal;
        Distance = distance;
    }

    public Vector3 Normal { get; }

    public float Distance { get; }

    /// <summary>
    /// 光線と平面の交差(要点2)。<c>N · (O + tD) = d</c> を t について解くと
    /// <c>t = (d − N·O) / (N·D)</c>。
    ///
    /// <para>
    /// 分母 <c>N·D</c> は「光線が面に向かう速さ」。0 なら光線は面と平行で、永遠に当たらない。
    /// 0 に近いだけ(かすめる光線)なら、t はとても大きくなるが正しい——地平線の近くの床がそれ。
    /// </para>
    /// </summary>
    public override bool Intersect(in Ray ray, float tMin, float tMax, out float t)
    {
        float denominator = Vector3.Dot(Normal, ray.Direction);

        // 平行(またはほぼ平行)。ちょうど 0 との比較にすると、1e-30 のような値で割って
        // 無限大や NaN を作ることがあるので、小さな幅を持たせて外れにしておく。
        if (MathF.Abs(denominator) < 1e-8f)
        {
            t = 0.0f;
            return false;
        }

        t = (Distance - Vector3.Dot(Normal, ray.Origin)) / denominator;
        return t > tMin && t < tMax;
    }

    public override Vector3 OutwardNormal(Vector3 point) => Normal;
}
