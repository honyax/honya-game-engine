using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 球。レイトレーサの「最初の形」になるのは、<b>光線との交差が2次方程式1本で閉じる</b>から。
/// </summary>
internal sealed class Sphere : Shape
{
    public Sphere(string name, Vector3 center, float radius, Material material)
        : base(name, material)
    {
        Center = center;
        Radius = radius;
    }

    public Vector3 Center { get; }

    public float Radius { get; }

    /// <summary>
    /// 光線と球の交差(要点2)。
    ///
    /// <para>
    /// 球の面は <c>|P − C|² = r²</c>。ここに光線 <c>P = O + tD</c> を入れて、<c>oc = O − C</c> と置くと
    /// </para>
    /// <code>
    ///   (D·D) t² + 2 (oc·D) t + (oc·oc − r²) = 0
    /// </code>
    /// <para>
    /// D は長さ 1 なので D·D = 1。さらに1次の係数を <c>2b</c> と置く(b = oc·D)と、解の公式の 2 と 4 が消えて
    /// <c>t = −b ± √(b² − c)</c> になる(RTIOW の「half-b」の形)。
    /// </para>
    /// <list type="bullet">
    /// <item>判別式 <c>b² − c &lt; 0</c> … 光線の直線が球をかすりもしない</item>
    /// <item>小さいほうの解 <c>−b − √</c> … 球に<b>入る</b>点。普通はこちらが答え</item>
    /// <item>大きいほうの解 <c>−b + √</c> … 球から<b>出る</b>点。始点が球の中にあるとき(ガラスの中を進む屈折の光線)はこちら</item>
    /// </list>
    /// </summary>
    public override bool Intersect(in Ray ray, float tMin, float tMax, out float t)
    {
        Vector3 oc = ray.Origin - Center;
        float b = Vector3.Dot(oc, ray.Direction);
        float c = Vector3.Dot(oc, oc) - Radius * Radius;

        float discriminant = b * b - c;
        if (discriminant < 0.0f)
        {
            t = 0.0f;
            return false;
        }

        float root = MathF.Sqrt(discriminant);

        // 近いほうの解から試す。それが始点より後ろ(tMin 以下)なら、始点は球の中にあるか、
        // 球が丸ごと後ろにある。どちらの場合も遠いほうの解を試せば正しく振り分けられる
        // (丸ごと後ろなら遠いほうも tMin 以下で、下の判定で外れになる)。
        t = -b - root;
        if (t <= tMin)
        {
            t = -b + root;
        }

        return t > tMin && t < tMax;
    }

    /// <summary>
    /// 中心から面の点へ向かう向きが、そのまま外向きの法線になる。
    /// 長さはちょうど半径なので、正規化(平方根)の代わりに半径で割るだけで済む。
    /// </summary>
    public override Vector3 OutwardNormal(Vector3 point) => (point - Center) / Radius;

    /// <summary>
    /// 球へ向かう向きを引く(要点4)。<b>球が空を覆っている円錐の中に一様</b>に引く。
    ///
    /// <para>
    /// 距離 d のところから半径 r の球を見ると、球は半頂角 θmax の円錐を覆っていて、
    /// <c>sinθmax = r/d</c>、つまり <c>cosθmax = √(1 − r²/d²)</c>。
    /// この円錐の中から一様に引けば、<b>引いた向きは必ず球に当たる</b>(手前側の面のどこかに)。
    /// pdf は円錐の立体角 <c>2π(1 − cosθmax)</c> の逆数。
    /// </para>
    /// <para>
    /// 球の<b>面の上</b>に一様に点を打つ方法もあり、そちらはどんな形にも一般化できるが、
    /// 半分は裏側の点になって球自身に遮られ、寄与 0 の光線を撃つことになる(分散が2倍)。
    /// 球と分かっているなら円錐のほうがよい。
    /// </para>
    /// <para>
    /// <paramref name="from"/> が球の中(または面の上)にあるときは円錐が作れないので false を返す。
    /// 今日の場面では、光源の球の中に拡散面は無い。
    /// </para>
    /// </summary>
    public override bool TrySampleDirection(Vector3 from, ref Rng rng, out Vector3 direction, out float distance, out float pdf)
    {
        direction = Vector3.Zero;
        distance = 0.0f;
        pdf = 0.0f;

        Vector3 toCenter = Center - from;
        float distanceToCenter = toCenter.Length();
        if (distanceToCenter <= Radius * 1.0001f)
        {
            return false;
        }

        float sin2 = Radius * Radius / (distanceToCenter * distanceToCenter);
        float cosThetaMax = MathF.Sqrt(MathF.Max(0.0f, 1.0f - sin2));

        Vector3 axis = toCenter / distanceToCenter;
        direction = Sampler.UniformCone(axis, cosThetaMax, ref rng);
        pdf = Sampler.UniformConePdf(cosThetaMax);

        // 引いた向きは円錐の中にあるので必ず当たる……はずだが、cosθmax の丸め誤差で
        // ぎりぎり外れることがある。外れたら諦める(引き直すと確率が変わって偏る)。
        if (!Intersect(new Ray(from, direction), 0.0f, float.PositiveInfinity, out distance))
        {
            return false;
        }

        return true;
    }
}
