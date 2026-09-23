using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 光線を当てられる形の基底。RTIOW の <c>hittable</c> にあたる。
///
/// <para>
/// 聞き方を<b>2段に分けている</b>のがこの型のいちばんの工夫。
/// </para>
/// <list type="number">
/// <item><see cref="Intersect"/> … <b>距離 t だけ</b>を答える。光線1本につき、当たりそうな形に何回も聞く</item>
/// <item><see cref="OutwardNormal"/> … 法線を答える。<b>いちばん近かった1つにだけ</b>聞く</item>
/// </list>
/// <para>
/// 形が N 個あっても、法線の計算(割り算や正規化)は1本の光線につき1回で済む。
/// RTIOW は当たるたびに法線まで入れた記録を作り直すが、近いものが後から見つかるとその計算は捨てられる。
/// </para>
///
/// <para>
/// <b>Day 60 で2つの役目が増えた。</b>
/// </para>
/// <list type="bullet">
/// <item><see cref="TryGetBounds"/> … BVH に入れるための箱(要点7)。<b>平面は無限なので囲めない</b></item>
/// <item><see cref="TrySampleDirection"/> … 面光源として、そこへ向かう向きを引く(要点4)。今日は球だけ</item>
/// </list>
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
    /// 呼ぶ側(<see cref="Tracer"/>)が光線の向きと比べて決める。
    /// ガラスではこの区別が「空気からガラスへ入るのか、ガラスから空気へ出るのか」になり、屈折率の比が逆になる。
    /// </summary>
    public abstract Vector3 OutwardNormal(Vector3 point);

    /// <summary>
    /// この形を囲む AABB。<b>囲めない形は false</b> を返す(要点7)。
    ///
    /// <para>
    /// 無限の平面は、どんな有限の箱にも入らない。「巨大な箱で囲む」ことはできるが、
    /// その箱は場面の全部と重なるので、BVH のどの枝を辿っても必ず葉まで降りることになり、木の意味が無くなる。
    /// <b>囲めない形は BVH に入れず、毎回総当たりで聞く</b>——今日の場面ではせいぜい6枚なので、これで足りる
    /// (<see cref="Scene.Prepare"/>)。
    /// </para>
    /// </summary>
    public abstract bool TryGetBounds(out Aabb bounds);

    /// <summary>
    /// 点 <paramref name="from"/> から見て、この形の上のどこかへ向かう向きを引く(面光源のサンプリング。要点4)。
    ///
    /// <para>
    /// 返す <paramref name="pdf"/> は<b>立体角あたりの確率密度</b>(1/sr)。
    /// 面積あたりで引いた場合は、呼ぶ側が単位を揃えなくてよいよう、ここで立体角へ直してから返す決まりにする。
    /// </para>
    /// <para>
    /// 既定では false(サンプリングできない)。平面は無限に広いので、
    /// その上に一様な点を引くこと自体ができない——無限に広い面光源が要るなら、
    /// 空(<see cref="Scene.Sky"/>)のように「当たらなかったときの色」で表すほうが素直になる。
    /// </para>
    /// </summary>
    /// <param name="distance">
    /// <paramref name="from"/> から引いた点までの距離。影の光線の tMax に使う
    /// (この距離のわずか手前で打ち切ると、光源自身に当たって「遮られた」と判定されずに済む)。
    /// </param>
    public virtual bool TrySampleDirection(Vector3 from, ref Rng rng, out Vector3 direction, out float distance, out float pdf)
    {
        direction = Vector3.Zero;
        distance = 0.0f;
        pdf = 0.0f;
        return false;
    }
}
