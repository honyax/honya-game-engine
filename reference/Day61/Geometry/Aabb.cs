using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 軸に平行な直方体(AABB: axis-aligned bounding box)。BVH の「箱」(要点7)。
///
/// <para>
/// 箱を軸に平行に限るのは、<b>光線との交差が掛け算と比較だけで終わる</b>から。
/// 傾いた箱や球で囲むほうが隙間は少なくなるが、当たり判定に平方根や行列が要る。
/// 光線1本につき何十回も聞くものなので、<b>ぴったり囲むことより1回の安さ</b>が効く。
/// </para>
/// </summary>
internal readonly struct Aabb
{
    public Aabb(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public Vector3 Min { get; }

    public Vector3 Max { get; }

    /// <summary>
    /// 何も入っていない箱。<b>Min に +∞、Max に −∞</b> を入れておくと、
    /// <see cref="Union(Aabb)"/> で点を足していくだけで囲む箱ができる(場合分けが要らない)。
    /// </summary>
    public static Aabb Empty => new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    /// <summary>中身が無い(まだ何も足していない)。</summary>
    public bool IsEmpty => Min.X > Max.X;

    public Vector3 Size => Max - Min;

    public Vector3 Centroid => (Min + Max) * 0.5f;

    /// <summary>
    /// 表面積。SAH(表面積ヒューリスティック。要点8)で使う。
    ///
    /// <para>
    /// 「でたらめな向きの光線が、この箱を通る確率」は<b>表面積に比例する</b>
    /// (凸な立体に対する Cauchy の公式)。だから SAH は体積ではなく表面積で費用を見積もる。
    /// </para>
    /// </summary>
    public float SurfaceArea
    {
        get
        {
            if (IsEmpty)
            {
                return 0.0f;
            }

            Vector3 d = Size;
            return 2.0f * (d.X * d.Y + d.Y * d.Z + d.Z * d.X);
        }
    }

    public Aabb Union(in Aabb other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    public Aabb Union(Vector3 point) => new(Vector3.Min(Min, point), Vector3.Max(Max, point));

    /// <summary>いちばん長い辺の軸(0 = X / 1 = Y / 2 = Z)。分割する軸の既定の選び方。</summary>
    public int LongestAxis
    {
        get
        {
            Vector3 d = Size;
            if (d.X >= d.Y && d.X >= d.Z)
            {
                return 0;
            }

            return d.Y >= d.Z ? 1 : 2;
        }
    }

    public static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>
    /// 光線が <c>tMin &lt; t &lt; tMax</c> の範囲でこの箱を通るか(<b>スラブ法</b>。要点7)。
    ///
    /// <para>
    /// 箱を「3対の平行な板(スラブ)の重なり」と見る。軸ごとに<b>板に入る t と出る t</b> を求め、
    /// 3軸ぶんの「入る」のいちばん遅いものと「出る」のいちばん早いものを比べる。
    /// <b>全部の板の中にいる時間帯が残っていれば</b>箱を通っている。
    /// </para>
    /// <para>
    /// 当たった距離は返さない。BVH では「この箱の中を見る価値があるか」だけが要るので、
    /// 距離を持ち帰っても使い道が無い。
    /// </para>
    /// </summary>
    /// <param name="invDirection">
    /// 向きの逆数(<c>1/dx, 1/dy, 1/dz</c>)。光線1本につき1回だけ作って使い回す。
    /// <b>割り算は掛け算の 10 倍以上かかる</b>ので、箱を何十個も見るこの場面では効く。
    /// 向きの成分が 0 なら ±∞ になるが、下の書き方なら正しく扱える。
    /// </param>
    public bool Intersect(Vector3 origin, Vector3 invDirection, float tMin, float tMax)
    {
        float enter = tMin;
        float exit = tMax;

        Slab(Min.X, Max.X, origin.X, invDirection.X, ref enter, ref exit);
        Slab(Min.Y, Max.Y, origin.Y, invDirection.Y, ref enter, ref exit);
        Slab(Min.Z, Max.Z, origin.Z, invDirection.Z, ref enter, ref exit);

        return enter <= exit;
    }

    /// <summary>
    /// 1組の板との出入りを求め、範囲を狭める。
    ///
    /// <para>
    /// <b><c>MathF.Min</c> / <c>MathF.Max</c> ではなく、比較で書いてある</b>のがここの肝。
    /// 光線が板と平行(<c>invDirection</c> が ±∞)で、しかも始点がちょうど板の上にあると
    /// <c>0 × ∞ = NaN</c> が出る。<c>MathF.Max(NaN, x)</c> は NaN を返すので、
    /// そのまま使うと <c>enter</c> が NaN になり、最後の <c>enter &lt;= exit</c> が false——
    /// <b>箱の中にいるのに「外れ」</b>になる。
    /// </para>
    /// <para>
    /// <c>if (t0 &gt; enter)</c> という書き方なら、NaN との比較は全部 false なので
    /// <b>その軸からの制約を無視する</b>ことになり、答えが正しくなる(PBRT と同じ書き方。自己チェック17)。
    /// </para>
    /// </summary>
    private static void Slab(float low, float high, float origin, float invDirection, ref float enter, ref float exit)
    {
        float t0 = (low - origin) * invDirection;
        float t1 = (high - origin) * invDirection;

        // 向きが負なら、先に当たるのは Max 側の板。
        if (t0 > t1)
        {
            (t0, t1) = (t1, t0);
        }

        if (t0 > enter)
        {
            enter = t0;
        }

        if (t1 < exit)
        {
            exit = t1;
        }
    }

    /// <summary>光線の向きから <c>invDirection</c> を作る。0 で割って ±∞ になるのは意図どおり。</summary>
    public static Vector3 Reciprocal(Vector3 direction)
        => new(1.0f / direction.X, 1.0f / direction.Y, 1.0f / direction.Z);
}
