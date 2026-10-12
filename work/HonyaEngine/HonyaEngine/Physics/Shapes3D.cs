using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 球。**3D でいちばん安い形**。
///
/// <see cref="Circle2D"/> の3D版で、性格もそのまま——
/// どちらを向いていても同じなので、判定は中心距離と半径の和を比べるだけ。
/// 回転を一切考えなくてよいので、
/// **物理エンジンを書くときは必ずここから始める**ことになる。
///
/// <para>
/// 球が特別なのは判定の安さだけではない。<b>慣性テンソルが等方</b>——
/// つまり「どの軸まわりにも回りにくさが同じ」なので、
/// <see cref="RigidBody"/> 側の計算も1段簡単になる(Day 43 の要点3)。
/// **今日足す箱(<see cref="Box3D"/>)では、この2つがどちらも崩れる**——
/// 判定は 15 本の軸を調べることになり、慣性テンソルは軸ごとに違う値になる。
/// </para>
///
/// <para>
/// 実際のゲームでも球は現役で、弾・投擲物・爆風の範囲・簡易な足元判定は
/// たいてい球で足りる。キャラクター本体はカプセル(Day 45)に化けるが、
/// カプセルは「線分から一定距離」——つまり<b>球を引き伸ばしたもの</b>なので、
/// 今日書く球の判定がそのまま下敷きになる。
/// </para>
/// </summary>
internal readonly struct Sphere3D
{
    public readonly Vector3 Center;
    public readonly float Radius;

    public Sphere3D(Vector3 center, float radius)
    {
        Center = center;
        Radius = radius;
    }
}

/// <summary>
/// 無限に広がる平面。**厚みも端も無い**。
///
/// <c>Normal · x = Distance</c> を満たす点 x の集合。
/// <see cref="Normal"/> は長さ1で、<see cref="Distance"/> は
/// **原点から平面までの符号付き距離**になる
/// (法線の向きに進めば距離が増える、という約束)。
///
/// <para>
/// 4つの数(法線3つ + 距離1つ)しか持たないので、
/// <b>判定が内積1回で終わる</b>——これが平面を最初の静的形状に選ぶ理由。
/// 床・壁・天井・見えない結界は全部これで書ける。
/// </para>
///
/// <para>
/// <b>端が無い</b>ことは、忘れると事故になる。床として使った平面は
/// 世界の果てまで続いているので、遠くへ飛んでいった球も落ちない。
/// 「有限の床」が要るときは、平面ではなく箱(<see cref="Box3D"/>。今日足した)か
/// 地形(Day 46)を使う。
/// 今日のデモが四方に壁を立てているのは、
/// <b>端が無い床の上で球が延々と滑っていかないように</b>するためでもある
/// (摩擦を入れるのは Day 47 なので、今日の球は転がらずに滑る)。
/// </para>
/// </summary>
internal readonly struct Plane3D
{
    /// <summary>面の向き。**長さ1**。この向きが「表」。</summary>
    public readonly Vector3 Normal;

    /// <summary>原点から面までの符号付き距離。<c>Normal · x = Distance</c>。</summary>
    public readonly float Distance;

    public Plane3D(Vector3 normal, float distance)
    {
        Normal = normal;
        Distance = distance;
    }

    /// <summary>
    /// 「この点を通り、この向きを表とする面」。**こちらのほうが書きやすい**。
    ///
    /// 床を <c>y = -0.5</c> に置きたいとき、
    /// <c>new Plane3D(Vector3.UnitY, -0.5f)</c> と書けなくはないが、
    /// 法線が斜めになった途端に距離の意味が分からなくなる。
    /// 点と向きで書けば、傾いた壁でも間違えない。
    /// </summary>
    public static Plane3D FromPointNormal(Vector3 point, Vector3 normal)
    {
        Vector3 unit = Vector3.Normalize(normal);
        return new Plane3D(unit, Vector3.Dot(unit, point));
    }

    /// <summary>
    /// 点から面までの**符号付き**距離。表側が正、裏側が負。
    ///
    /// 内積1回と引き算1回。**3D の衝突判定でいちばん多く呼ばれる式**で、
    /// 球と平面(<see cref="Collision3D.SpherePlane"/>)も、
    /// 視錐台カリングも、**今日の SAT の投影**も、全部これでできている。
    /// </summary>
    public float SignedDistance(Vector3 point) => Vector3.Dot(Normal, point) - Distance;

    /// <summary>面の上でいちばん近い点。**法線方向へ距離ぶん戻すだけ**。</summary>
    public Vector3 ClosestPoint(Vector3 point) => point - (Normal * SignedDistance(point));
}

/// <summary>
/// 箱(OBB: Oriented Bounding Box)。**向きを持った直方体**。
///
/// <see cref="Box2D"/>(Day 25)の3D版だが、性格はだいぶ違う。
/// 2D の箱は軸に沿った AABB で、判定は各軸の重なりを見るだけだった。
/// こちらは**向きを持つ**ので、比べるべき軸が最初から決まっていない——
/// そこで出てくるのが分離軸定理(要点1)。
///
/// <para>
/// 持っているのは中心・半分の長さ・向きの3つだけ。
/// 8つの頂点も 6 枚の面も、この3つから作れる(<see cref="Corner"/>)ので持たない。
/// **頂点を配列で持つと、回すたびに 8 点を書き換える**ことになり、
/// 剛体が回り続ける今日のような使い方では毎ステップの更新が要る。
/// </para>
///
/// <para>
/// <b>「半分の長さ」で持つ</b>のは、判定の式に出てくるのが常に半分の側だから。
/// 中心から面までの距離、中心から頂点までの距離——どちらも半分の長さでできている。
/// </para>
///
/// <para>
/// 箱は<b>3D で最初に出会う「回転が効く形」</b>になる。
/// 球は等方なので、慣性テンソルも判定も向きを無視できた。
/// 箱では慣性テンソルが軸ごとに違い、判定も向きを見る。
/// Day 45 のカプセルは「線分から一定距離」なので軸1本ぶんだけ向きが効く——
/// **球 → 箱 → カプセルは、向きの効き方が 0 → 3 → 1 の順**に並んでいる。
/// </para>
/// </summary>
internal readonly struct Box3D
{
    /// <summary>中心(世界座標)。**重心と一致する**(密度が一様なので)。</summary>
    public readonly Vector3 Center;

    /// <summary>各軸の半分の長さ [m]。**物体座標での寸法**なので、回しても変わらない。</summary>
    public readonly Vector3 HalfExtents;

    /// <summary>向き。**単位クォータニオン**。</summary>
    public readonly Quaternion Orientation;

    public Box3D(Vector3 center, Vector3 halfExtents, Quaternion orientation)
    {
        Center = center;
        HalfExtents = halfExtents;
        Orientation = orientation;
    }

    /// <summary>箱の x 軸(世界座標)。**長さ1**。</summary>
    public Vector3 AxisX => Vector3.Transform(Vector3.UnitX, Orientation);

    /// <summary>箱の y 軸(世界座標)。</summary>
    public Vector3 AxisY => Vector3.Transform(Vector3.UnitY, Orientation);

    /// <summary>箱の z 軸(世界座標)。</summary>
    public Vector3 AxisZ => Vector3.Transform(Vector3.UnitZ, Orientation);

    /// <summary>
    /// 添字で軸を取る。**SAT のループが添字で回る**ので要る。
    ///
    /// 0 = x、1 = y、2 = z。範囲外は z を返す(呼ぶ側が 0〜2 で回すので起きない)。
    /// </summary>
    public Vector3 Axis(int index) => index switch
    {
        0 => AxisX,
        1 => AxisY,
        _ => AxisZ,
    };

    /// <summary>半分の長さを添字で取る。<see cref="Axis"/> と対で使う。</summary>
    public float Extent(int index) => index switch
    {
        0 => HalfExtents.X,
        1 => HalfExtents.Y,
        _ => HalfExtents.Z,
    };

    /// <summary>物体座標の点を世界へ。</summary>
    public Vector3 ToWorld(Vector3 local) => Center + Vector3.Transform(local, Orientation);

    /// <summary>
    /// 世界の点を物体座標へ。**箱の判定はほとんどここから始まる**。
    ///
    /// 物体座標へ持ち込めば、箱は原点中心の軸に沿った箱になる。
    /// 「傾いた箱と点」の問題が「軸に沿った箱と点」に化けるので、
    /// Day 25 の 2D で書いた <c>Collision2D.CircleBox</c> と同じ手が使える。
    /// </summary>
    public Vector3 ToLocal(Vector3 world) =>
        Vector3.Transform(world - Center, Quaternion.Conjugate(Orientation));

    /// <summary>
    /// 箱の上(内側を含む)でいちばん近い点。**物体座標で各成分を clamp するだけ**。
    ///
    /// 傾いた箱でも、物体座標へ持ち込めば軸に沿った箱になるので、
    /// x・y・z それぞれを ±半分の長さに切り詰めれば終わる。
    /// **点が箱の中にあるときは、その点自身が返る**——
    /// 距離が 0 になるので、球と箱の判定(<see cref="Collision3D.SphereBox"/>)では
    /// そこだけ別に扱うことになる。
    /// </summary>
    public Vector3 ClosestPoint(Vector3 world)
    {
        Vector3 local = ToLocal(world);
        Vector3 clamped = Vector3.Clamp(local, -HalfExtents, HalfExtents);
        return ToWorld(clamped);
    }

    /// <summary>
    /// この向きに投影したときの「半径」。**SAT の主役**(要点1)。
    ///
    /// <code>
    ///   r(n) = |e_x (a_x · n)| + |e_y (a_y · n)| + |e_z (a_z · n)|
    /// </code>
    ///
    /// 箱を軸 n に落とすと線分になる。その線分の中心は箱の中心を落とした点で、
    /// 長さの半分がこの値。**絶対値の和になる**のは、
    /// どの角がいちばん n の向きに突き出るかを場合分けせずに済ませるため——
    /// 3つの軸それぞれについて「n に沿った成分を、突き出る側へ」足している。
    ///
    /// <para>
    /// n が箱の軸の1つと揃っているときは、他の2項が 0 になって
    /// その軸の半分の長さがそのまま出る。当たり前の答えが出るのが確認になる。
    /// </para>
    /// </summary>
    public float ProjectedRadius(Vector3 axis) =>
        MathF.Abs(HalfExtents.X * Vector3.Dot(AxisX, axis))
        + MathF.Abs(HalfExtents.Y * Vector3.Dot(AxisY, axis))
        + MathF.Abs(HalfExtents.Z * Vector3.Dot(AxisZ, axis));

    /// <summary>
    /// 8つの頂点のうち1つ。添字の**下位3ビットが各軸の符号**。
    ///
    /// <c>0b000</c> が (-x, -y, -z)、<c>0b111</c> が (+x, +y, +z)。
    /// ビットで書けるので、8点まわすループが <c>for (int i = 0; i &lt; 8; i++)</c> で済む。
    /// </summary>
    public Vector3 Corner(int index)
    {
        var local = new Vector3(
            (index & 1) != 0 ? HalfExtents.X : -HalfExtents.X,
            (index & 2) != 0 ? HalfExtents.Y : -HalfExtents.Y,
            (index & 4) != 0 ? HalfExtents.Z : -HalfExtents.Z);

        return ToWorld(local);
    }

    /// <summary>
    /// この向きにいちばん突き出た頂点(**支持点**)。
    ///
    /// 物体座標で「各軸の成分の符号を、n の側へ倒す」だけ。
    /// 凸形状の判定で繰り返し出てくる関数で、GJK も EPA もこれ1本でできている
    /// (発展課題として Day 44b の改造課題3に置いた)。
    /// </summary>
    public Vector3 Support(Vector3 direction)
    {
        var local = new Vector3(
            Vector3.Dot(AxisX, direction) >= 0.0f ? HalfExtents.X : -HalfExtents.X,
            Vector3.Dot(AxisY, direction) >= 0.0f ? HalfExtents.Y : -HalfExtents.Y,
            Vector3.Dot(AxisZ, direction) >= 0.0f ? HalfExtents.Z : -HalfExtents.Z);

        return ToWorld(local);
    }
}
