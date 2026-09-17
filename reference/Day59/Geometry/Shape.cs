using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 光線を当てられる形の基底。RTIOW の <c>hittable</c> にあたる。
///
/// <para>
/// 聞き方を<b>2段に分けている</b>のがこの型のいちばんの工夫。
/// </para>
/// <list type="number">
/// <item><see cref="Intersect"/> … <b>距離 t だけ</b>を答える。光線1本につき、場面の<b>全部の形</b>に聞く</item>
/// <item><see cref="OutwardNormal"/> … 法線を答える。<b>いちばん近かった1つにだけ</b>聞く</item>
/// </list>
/// <para>
/// 形が N 個あっても、法線の計算(割り算や正規化)は1本の光線につき1回で済む。
/// RTIOW は当たるたびに法線まで入れた記録を作り直すが、近いものが後から見つかるとその計算は捨てられる。
/// Day 60 で形が数千個になると、この差が効いてくる(そこでは BVH で「聞く形」自体を減らす)。
/// </para>
/// </summary>
internal abstract class Shape
{
    protected Shape(string name, Material material)
    {
        Name = name;
        Material = material;
    }

    /// <summary>1画素を追ったとき(右クリック)に木の中へ出す名前。</summary>
    public string Name { get; }

    public Material Material { get; }

    /// <summary>
    /// 光線が <c>tMin &lt; t &lt; tMax</c> の範囲でこの形に当たるなら、<b>いちばん近い t</b> を返す。
    ///
    /// <para>
    /// tMax を渡すのは、<b>もっと近くで当たったものが既にあるなら、その先は聞く必要が無い</b>から
    /// (<see cref="Scene.Intersect"/> は見つけるたびに tMax を縮めていく)。
    /// 影の光線では tMax を光源までの距離にして、<b>光源より向こうの物体を影にしない</b>ためにも使う。
    /// </para>
    /// </summary>
    public abstract bool Intersect(in Ray ray, float tMin, float tMax, out float t);

    /// <summary>
    /// 面の上の点での<b>外向きの</b>法線(長さ 1)。
    ///
    /// 「光線に向いた側」ではなく「形の外側」を返す。光線が内側から当たったかどうかは
    /// 呼ぶ側(<see cref="WhittedTracer"/>)が光線の向きと比べて決める。
    /// ガラスではこの区別が「空気からガラスへ入るのか、ガラスから空気へ出るのか」になり、屈折率の比が逆になる。
    /// </summary>
    public abstract Vector3 OutwardNormal(Vector3 point);
}
