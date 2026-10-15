using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 軸に平行な直方体(AABB)。**3D でいちばん安い形**であり、今日の主役の片方(要点1)。
///
/// <see cref="Aabb2D"/>(Day 25)の 3D 版。回らない前提を置くだけで、
/// 判定が「区間が重なっているか」を x・y・z で1回ずつ見るだけになる——
/// <b>比較6つで終わる</b>。
///
/// <para>
/// 当たり判定の形としては使いにくい(斜めを向いた剣を表せない)のに
/// AABB がどこにでも出てくるのは、<b>ほかの形の「外接箱」として使える</b>から。
/// 今日のブロードフェーズ(<see cref="SpatialGrid3D"/>)は、
/// <b>形を1つも知らない</b>まま外接箱だけで候補を絞る。
/// 球でも箱でもカプセルでも地形でも、いったんここへ落としてしまえば同じに扱える。
/// </para>
///
/// <para>
/// <b>「無限に広い」を表せる</b>ようにしてある(<see cref="Infinite"/>)。
/// 平面(<see cref="Plane3D"/>)には外接箱が無いので、
/// 素直に書くと「平面だけ別扱い」の分岐がブロードフェーズの中に生える。
/// 無限を1つの値として持てば、<b>格子の側は「入らないもの」として一律に扱える</b>——
/// <see cref="IsFinite"/> がその札になる。
/// </para>
/// </summary>
internal readonly struct Aabb3D
{
    public readonly Vector3 Min;
    public readonly Vector3 Max;

    public Aabb3D(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>中心と半分の大きさから作る。</summary>
    public static Aabb3D FromCenter(Vector3 center, Vector3 halfSize) =>
        new(center - halfSize, center + halfSize);

    /// <summary>中心と半径(等方)から作る。**球の外接箱**。</summary>
    public static Aabb3D FromCenter(Vector3 center, float radius) =>
        FromCenter(center, new Vector3(radius));

    /// <summary>
    /// 世界を丸ごと覆う箱。**平面のための値**。
    ///
    /// <see cref="float.PositiveInfinity"/> をそのまま入れてある。
    /// <c>float.MaxValue</c> でも実害は無さそうに見えるが、
    /// <b>引き算した途端にあふれる</b>(Max - Min が inf になる)ので、
    /// 最初から inf にしておくほうが挙動が読める。
    /// </summary>
    public static Aabb3D Infinite => new(
        new Vector3(float.NegativeInfinity), new Vector3(float.PositiveInfinity));

    public Vector3 Center => (Min + Max) * 0.5f;

    public Vector3 Size => Max - Min;

    public Vector3 HalfSize => (Max - Min) * 0.5f;

    /// <summary>
    /// 有限の大きさを持つか。**格子に入れられるかの札**(要点2)。
    ///
    /// 平面は無限なので false。地形(<see cref="Terrain3D"/>)は有限だが広いので、
    /// 「有限だが大きすぎる」ほうは <see cref="SpatialGrid3D"/> が
    /// マスの数で別に足切りする——<b>無限と巨大は別の問題</b>。
    /// </summary>
    public bool IsFinite =>
        float.IsFinite(Min.X) && float.IsFinite(Min.Y) && float.IsFinite(Min.Z)
        && float.IsFinite(Max.X) && float.IsFinite(Max.Y) && float.IsFinite(Max.Z);

    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.X <= Max.X
        && point.Y >= Min.Y && point.Y <= Max.Y
        && point.Z >= Min.Z && point.Z <= Max.Z;

    /// <summary>六方に広げた箱。**判定に余裕を持たせる**ときに使う。</summary>
    public Aabb3D Expanded(float amount) =>
        new(Min - new Vector3(amount), Max + new Vector3(amount));

    /// <summary>2つを包む最小の箱。木構造(BVH)を組むときの基本操作。</summary>
    public static Aabb3D Union(in Aabb3D a, in Aabb3D b) =>
        new(Vector3.Min(a.Min, b.Min), Vector3.Max(a.Max, b.Max));

    /// <summary>
    /// 重なっているか。**軸ごとに区間の重なりを見るだけ**。
    ///
    /// 「重なっていない」条件のほうが短いので、そちらを否定して書く。
    /// <b>1軸でも離れていれば重なっていない</b>ので、たいていの組は
    /// 最初の比較で返る——これが AABB が足切りに向いている理由。
    /// </summary>
    public static bool Overlap(in Aabb3D a, in Aabb3D b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
        && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
        && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
}

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
/// たいてい球で足りる。キャラクター本体はカプセル(<see cref="Capsule3D"/>)になるが、
/// カプセルは「線分から一定距離」——つまり<b>球を引き伸ばしたもの</b>なので、
/// Day 43 で書いた球の判定がそのまま下敷きになる。
/// <b>今日のカプセルの判定は、全部いったん球に落として解いている</b>。
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

    /// <summary>外接する AABB。**ブロードフェーズが見るのはこれだけ**(Day 46)。</summary>
    public Aabb3D Bounds => Aabb3D.FromCenter(Center, Radius);
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
/// <see cref="Capsule3D"/> は「線分から一定距離」なので軸1本ぶんだけ向きが効く——
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
    /// 線分にいちばん近い、箱の上の点。**距離が凸なので三分探索で詰める**(Day 45a の要点3)。
    ///
    /// 線分と箱の最近接点対は、解析的に解こうとすると
    /// 「線分の端 × 箱の 6 面 + 12 辺 + 8 頂点」の場合分けになって手に負えない。
    /// 代わりに、線分のパラメータ t を1つの変数と見て**最小化する**。
    ///
    /// <code>
    ///   f(t) = |seg(t) から箱までの距離|²      t ∈ [0, 1]
    /// </code>
    ///
    /// <para>
    /// <b>f は t について凸</b>。凸集合までの距離は点について凸な関数で、
    /// <c>t ↦ seg(t)</c> は一次(アフィン)なので、合成しても凸のまま。
    /// 凸なら谷は1つしかない——だから<b>三分探索</b>が使える。
    /// 区間を3等分して、内側の2点のうち値の大きいほうを捨てる、を繰り返すだけ。
    /// 1回で区間が 2/3 になるので、32 回で 10⁻⁶ 以下まで詰まる。
    /// </para>
    ///
    /// <para>
    /// <b>「交互に投げ合う」方式(交互射影)は使えない</b>。
    /// 片方の上の点をもう片方へ投げ返すのを繰り返す素朴な手で、
    /// 凸どうしなら理屈のうえでは最近接点対に収束する。
    /// ところが<b>収束が絶望的に遅い</b>——箱と線分が浅い角度で向き合うと、
    /// 64 回まわしても 5cm ずれる配置が 2,000 通り中 61 件も残った
    /// (この日、実際にそう書いて自己チェックで落ちた)。
    /// 三分探索なら回数ぶんだけ確実に区間が縮むので、
    /// <b>「何回まわせば十分か」が事前に言える</b>。
    /// </para>
    ///
    /// <para>
    /// <b>物体座標で解く</b>のが速さの肝。物体座標へ持ち込めば箱は軸に沿った箱になり、
    /// 距離の計算が <c>Clamp</c> 1回で済む。
    /// 回転(クォータニオンの変換)は最初と最後の3回だけで、
    /// 探索の内側には入らない。
    /// </para>
    ///
    /// <para>
    /// <b>線分が箱を貫いているときは距離 0 に落ちる</b>。
    /// そのときは最近接点が意味を持たないので、
    /// 呼ぶ側(<see cref="Collision3D.CapsuleBox"/>)が
    /// 「球の中心が箱の中」の枝——<see cref="Collision3D.SphereBox"/> の
    /// いちばん近い面へ逃がす処理——に落ちる。
    /// </para>
    /// </summary>
    public Vector3 ClosestPoint(in Segment3D segment, out Vector3 onSegment)
    {
        // **物体座標へ**。ここから先、箱は原点中心の軸に沿った箱になる。
        Vector3 localStart = ToLocal(segment.Start);
        Vector3 localDelta = ToLocal(segment.End) - localStart;

        float low = 0.0f;
        float high = 1.0f;

        // 32 回で (2/3)^32 ≒ 5×10⁻⁶。線分の長さが 10m でも 50µm の精度。
        for (int i = 0; i < 32; i++)
        {
            float third = (high - low) / 3.0f;
            float a = low + third;
            float b = high - third;

            if (LocalDistanceSquared(localStart + (localDelta * a))
                < LocalDistanceSquared(localStart + (localDelta * b)))
            {
                high = b;
            }
            else
            {
                low = a;
            }
        }

        float t = (low + high) * 0.5f;

        onSegment = segment.PointAt(t);
        return ToWorld(Vector3.Clamp(localStart + (localDelta * t), -HalfExtents, HalfExtents));
    }

    /// <summary>物体座標の点から箱までの距離の2乗。**中にあれば 0**。</summary>
    private float LocalDistanceSquared(Vector3 local) =>
        (local - Vector3.Clamp(local, -HalfExtents, HalfExtents)).LengthSquared();

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
    /// 外接する AABB(Day 46)。**<see cref="ProjectedRadius"/> を世界の3軸で使う**。
    ///
    /// 傾いた箱を包む箱の半径は、各世界軸への投影の絶対値の和になる——
    /// つまり「その軸に落としたときの半径」そのもので、
    /// <b>SAT のために書いた関数がそのまま使える</b>。
    /// <see cref="Obb2D.Bounds"/>(Day 25)で <c>|cos|*hx + |sin|*hy</c> と
    /// 書いていたのと同じ式が、3D では軸3本ぶんの和になっている。
    ///
    /// <para>
    /// <b>回すと外接箱は太る</b>。45 度回した立方体の外接箱は
    /// 一辺が √2 倍になり、体積は 2.8 倍。
    /// ブロードフェーズの候補が「当たっていないのに出てくる」原因の多くはこれで、
    /// 箱を回しながら落とす筋書き(Day 44)では候補の数が目に見えて増える。
    /// </para>
    /// </summary>
    public Aabb3D Bounds => Aabb3D.FromCenter(
        Center,
        new Vector3(
            ProjectedRadius(Vector3.UnitX),
            ProjectedRadius(Vector3.UnitY),
            ProjectedRadius(Vector3.UnitZ)));

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

/// <summary>
/// 三角形。**地形を構成する最小の面**であり、今日の判定の入口(要点2)。
///
/// 3点しか持たない。法線は毎回作ると <c>Normalize</c> が判定のたびに走るので、
/// **構築のときに1回だけ計算して持つ**——
/// 地形は1回作れば動かないので、これで完全に元が取れる。
///
/// <para>
/// <b>裏表がある</b>。<see cref="Normal"/> の向きが「表」で、
/// 地形の三角形は必ず上を向くように組んである(<see cref="Terrain3D.Triangle"/>)。
/// 表裏を決めておかないと、地面に潜った物体をどちらへ押し戻せばよいか決まらない。
/// </para>
///
/// <para>
/// <b>凸であること</b>が今日いちばん効く性質。3点しか無いので当然凸で、
/// だから <see cref="ClosestPoint(Vector3)"/> は1点に決まり、
/// 線分との最近接点は Day 45 の箱とまったく同じ**三分探索**で詰まる
/// (Day 45a の要点3)。<b>地形そのものは凹んでいる</b>が、
/// 面ごとに分ければ全部凸——これが「凹んだ形を三角形に割って扱う」ことの意味になる。
/// </para>
/// </summary>
internal readonly struct Triangle3D
{
    public readonly Vector3 A;
    public readonly Vector3 B;
    public readonly Vector3 C;

    /// <summary>面の向き(長さ1)。**構築時に1回だけ計算する**。</summary>
    public readonly Vector3 Normal;

    public Triangle3D(Vector3 a, Vector3 b, Vector3 c)
    {
        A = a;
        B = b;
        C = c;

        // 潰れた三角形(3点が一直線)では外積が 0 になる。
        // **そのまま正規化すると NaN が絵にも物理にも流れる**ので、上向きに逃がす。
        Vector3 cross = Vector3.Cross(b - a, c - a);
        float length = cross.Length();
        Normal = length > 1e-12f ? cross / length : Vector3.UnitY;
    }

    /// <summary>外接する AABB。**ブロードフェーズと地形のマス探しに使う**。</summary>
    public Aabb3D Bounds =>
        new(Vector3.Min(A, Vector3.Min(B, C)), Vector3.Max(A, Vector3.Max(B, C)));

    /// <summary>点から面(無限に広げた平面)までの符号付き距離。**表側が正**。</summary>
    public float SignedDistance(Vector3 point) => Vector3.Dot(point - A, Normal);

    /// <summary>
    /// 三角形の上でいちばん近い点。**7つの領域に分けて解く**(要点2)。
    ///
    /// 三角形のまわりの空間は、最近接点がどこになるかで7つに分かれる——
    /// 頂点3つ・辺3つ・面1つ。素直に書くと
    /// 「3頂点と3辺と面に落として、いちばん近いものを選ぶ」で 7 回の計算になるが、
    /// <b>領域を順に潰していけば1回で済む</b>。
    ///
    /// <code>
    ///   1. A の外側の頂点領域か? → A
    ///   2. B の外側の頂点領域か? → B
    ///   3. AB の辺の領域か?      → AB 上の点
    ///   4. C …(以下同様)
    ///   7. どれでもない           → 面の上へ落とす
    /// </code>
    ///
    /// <para>
    /// 判定に使っているのは全部<b>内積</b>で、割り算は最後の1〜2回だけ。
    /// 出典は Christer Ericson, <i>Real-Time Collision Detection</i> 5.1.5
    /// (<c>ClosestPtPointTriangle</c>)。
    /// Day 45 の <see cref="Segment3D.ClosestPoint(Vector3, out float)"/> が
    /// 「直線で解いて範囲に切り詰める」だったのの、次元が1つ上がった版になる。
    /// </para>
    ///
    /// <para>
    /// <b><paramref name="onFace"/> が今日の要</b>。
    /// 最近接点が面の内側(7番目の領域)だったかどうかを返す。
    /// これが false ということは「三角形の縁か角が最近接」——
    /// つまり<b>隣の三角形の受け持ちかもしれない</b>ということで、
    /// 内部エッジ問題(要点3)の分かれ道がここになる。
    /// </para>
    /// </summary>
    public Vector3 ClosestPoint(Vector3 point, out bool onFace)
    {
        onFace = false;

        Vector3 ab = B - A;
        Vector3 ac = C - A;
        Vector3 ap = point - A;

        // --- 1. A の頂点領域 ---
        float d1 = Vector3.Dot(ab, ap);
        float d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0.0f && d2 <= 0.0f)
        {
            return A;
        }

        // --- 2. B の頂点領域 ---
        Vector3 bp = point - B;
        float d3 = Vector3.Dot(ab, bp);
        float d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0.0f && d4 <= d3)
        {
            return B;
        }

        // --- 3. AB の辺領域 ---
        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f)
        {
            return A + (ab * (d1 / (d1 - d3)));
        }

        // --- 4. C の頂点領域 ---
        Vector3 cp = point - C;
        float d5 = Vector3.Dot(ab, cp);
        float d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0.0f && d5 <= d6)
        {
            return C;
        }

        // --- 5. AC の辺領域 ---
        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f)
        {
            return A + (ac * (d2 / (d2 - d6)));
        }

        // --- 6. BC の辺領域 ---
        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f)
        {
            return B + ((C - B) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
        }

        // --- 7. 面の内側 ---
        //
        // ここまで落ちてきたら、点を面へ垂直に落とした先が三角形の中にある。
        // **重心座標で書く**と、上で計算した va・vb・vc がそのまま面積比になっている。
        onFace = true;

        float denominator = 1.0f / (va + vb + vc);
        return A + (ab * (vb * denominator)) + (ac * (vc * denominator));
    }

    /// <summary>面の上でいちばん近い点(面の内側かどうかが要らないとき)。</summary>
    public Vector3 ClosestPoint(Vector3 point) => ClosestPoint(point, out _);

    /// <summary>
    /// 線分にいちばん近い、三角形の上の点。**Day 45 の箱とまったく同じ三分探索**。
    ///
    /// 三角形は凸なので、「線分上の点から三角形までの距離」は
    /// パラメータ t について凸な関数になる(Day 45a の要点3)。
    /// 凸なら谷は1つしかないので、区間を3等分して
    /// 内側の2点のうち値の大きいほうを捨てる、を繰り返せば確実に詰まる。
    ///
    /// <para>
    /// <b>箱のときと違って物体座標へ移す必要が無い</b>。
    /// 箱は「物体座標なら軸に沿った箱になる」ことに寄りかかっていたが、
    /// 三角形は最初から <see cref="ClosestPoint(Vector3, out bool)"/> が
    /// どの向きでも解けるので、そのまま世界座標で回せる。
    /// </para>
    ///
    /// <para>
    /// 32 回で区間が (2/3)^32 ≒ 5×10⁻⁶ になる。
    /// 1マス 1m の地形なら 5µm の精度で、絵にも判定にも十分。
    /// </para>
    /// </summary>
    public Vector3 ClosestPoint(in Segment3D segment, out Vector3 onSegment)
    {
        float low = 0.0f;
        float high = 1.0f;

        for (int i = 0; i < 32; i++)
        {
            float third = (high - low) / 3.0f;
            float a = low + third;
            float b = high - third;

            if (DistanceSquared(segment.PointAt(a)) < DistanceSquared(segment.PointAt(b)))
            {
                high = b;
            }
            else
            {
                low = a;
            }
        }

        float t = (low + high) * 0.5f;

        onSegment = segment.PointAt(t);
        return ClosestPoint(onSegment);
    }

    /// <summary>
    /// 真上から見たとき、この点が三角形の中に入っているか(Day 46a の要点3)。
    ///
    /// <b>高さの格子だからこそ意味がある問い</b>。地形は「1つの (x, z) に高さが1つ」なので、
    /// x と z だけ見れば「その点の真上(または真下)にこの面があるか」が決まる。
    /// 一般の三角形メッシュでは、真上に何枚も面が重なりうるので使えない。
    ///
    /// <para>
    /// 使い道は<b>地面に深く潜った物体を拾うこと</b>。
    /// 潜ってしまうと最近接点は三角形の縁や角になり、
    /// <see cref="ClosestPoint(Vector3, out bool)"/> の「面の内側か」だけでは
    /// <b>どの三角形も受け持たない</b>状態が起きる。
    /// 真上から見た内外で決めれば、隣り合う面のどれかが必ず受け持つ。
    /// </para>
    ///
    /// <para>
    /// 中身は 2D の点と三角形の内外判定。3辺それぞれについて
    /// 「点が辺の左右どちら側か」を外積の符号で見て、<b>符号が揃っていれば中</b>。
    /// 縁のちょうど上では符号が 0 になるので、
    /// <b>両側の三角形が受け持つ</b>ように 0 も中に含めてある——
    /// 重複した接触点は、マニフォールドを組むときにまとめられる。
    /// </para>
    /// </summary>
    public bool ContainsColumn(Vector3 point)
    {
        float d1 = Edge2D(point, A, B);
        float d2 = Edge2D(point, B, C);
        float d3 = Edge2D(point, C, A);

        bool anyNegative = d1 < 0.0f || d2 < 0.0f || d3 < 0.0f;
        bool anyPositive = d1 > 0.0f || d2 > 0.0f || d3 > 0.0f;

        return !(anyNegative && anyPositive);
    }

    /// <summary>xz 平面で「点が辺のどちら側か」。**符号だけを使う**。</summary>
    private static float Edge2D(Vector3 point, Vector3 from, Vector3 to) =>
        ((point.X - to.X) * (from.Z - to.Z)) - ((from.X - to.X) * (point.Z - to.Z));

    /// <summary>点から三角形までの距離の2乗。**三分探索の評価関数**。</summary>
    private float DistanceSquared(Vector3 point) =>
        (point - ClosestPoint(point)).LengthSquared();
}

/// <summary>
/// 線分。**カプセルの背骨**であり、今日の判定が全部ここから始まる。
///
/// 点と点を結んだだけの、いちばん素朴な形。
/// それでも独立した型にしてあるのは、
/// <b>今日の判定がほぼ「線分に対する最近接点」だけでできている</b>から。
///
/// <list type="bullet">
/// <item>球とカプセル … 球の中心にいちばん近い線分上の点を取る</item>
/// <item>カプセルとカプセル … 2本の線分の最近接点を取る(<see cref="ClosestPoints"/>)</item>
/// <item>カプセルと箱 … 線分と箱の最近接点を取る(<see cref="Box3D.ClosestPoint(in Segment3D, out Vector3)"/>)</item>
/// </list>
///
/// <para>
/// <b>正規化した向きを持たない</b>のがこの型の性格。
/// 「始点 + 向き × 長さ」で持つと、長さ 0 の線分(2点が一致)を作った瞬間に
/// 向きが NaN になる。始点と終点だけで持てば退化しても壊れず、
/// 向きが要る場面(<see cref="Axis"/> を持つのはカプセルの側)だけで
/// 正規化を考えればよい。
/// </para>
/// </summary>
internal readonly struct Segment3D
{
    public readonly Vector3 Start;
    public readonly Vector3 End;

    public Segment3D(Vector3 start, Vector3 end)
    {
        Start = start;
        End = end;
    }

    /// <summary>始点から終点へのベクトル。**正規化していない**(長さが情報)。</summary>
    public Vector3 Delta => End - Start;

    public float Length => Delta.Length();

    /// <summary>中点。カプセルの重心はここ。</summary>
    public Vector3 Center => (Start + End) * 0.5f;

    /// <summary>パラメータ t(0 で始点、1 で終点)の位置。</summary>
    public Vector3 PointAt(float t) => Start + (Delta * t);

    /// <summary>
    /// 点にいちばん近い線分上の点。**直線に落としてから 0〜1 に切り詰めるだけ**。
    ///
    /// <code>
    ///   t = (p - Start)·d / (d·d)      d = End - Start
    /// </code>
    /// これは「点を直線へ垂直に落としたときのパラメータ」。
    /// <b>線分は直線の一部でしかない</b>ので、t が範囲を出たら端が答えになる——
    /// だから <c>Clamp</c> 1回で終わる。
    ///
    /// <para>
    /// この「直線で解いてから範囲に切り詰める」形は今日じゅう何度も出てくる。
    /// 箱の最近接点(<see cref="Box3D.ClosestPoint(Vector3)"/>)が
    /// 物体座標で各成分を clamp するだけだったのと、まったく同じ考え方になっている。
    /// </para>
    /// </summary>
    public Vector3 ClosestPoint(Vector3 point, out float t)
    {
        Vector3 delta = Delta;
        float lengthSquared = delta.LengthSquared();

        // **長さ 0 の線分**(2点が一致)。割れないので始点を返す。
        // カプセルの半分の高さを 0 にすると本当にここへ来る——そのときカプセルは球になる。
        if (lengthSquared < 1e-12f)
        {
            t = 0.0f;
            return Start;
        }

        t = Math.Clamp(Vector3.Dot(point - Start, delta) / lengthSquared, 0.0f, 1.0f);
        return Start + (delta * t);
    }

    /// <summary>点にいちばん近い線分上の点(パラメータが要らないとき)。</summary>
    public Vector3 ClosestPoint(Vector3 point) => ClosestPoint(point, out _);

    /// <summary>
    /// 2つの線分の最近接点。**今日いちばん大事な 40 行**(要点2)。
    ///
    /// 直線どうしなら、Day 44 の <c>Sat.ClosestPointsOnLines</c> と同じく
    /// 連立方程式を1回解けば終わる。**線分だと端で場合分けが要る**——
    /// 解いた (s, t) が 0〜1 を出たら、そこは線分の外なので答えにならない。
    ///
    /// <code>
    ///   d1 = P1 - P0、d2 = Q1 - Q0、r = P0 - Q0
    ///   a = d1·d1、b = d1·d2、c = d1·r、e = d2·d2、f = d2·r
    ///   s = (b f - c e) / (a e - b²)、 t = (b s + f) / e
    /// </code>
    ///
    /// <para>
    /// <b>手順は「まず s を切り詰め、その s に対する t を求め、t も切り詰めたら s を求め直す」</b>。
    /// t を切り詰めた時点で「t を固定したときの最適な s」は変わるので、
    /// 求め直さないと答えがずれる。ここを省くと、
    /// <b>すれ違う2本のカプセルで接触点が端に寄りすぎる</b>という形で出る。
    /// </para>
    ///
    /// <para>
    /// <b>平行なとき</b>(分母 <c>a e - b²</c> が 0)は最近接点が1つに決まらない。
    /// そのときは <c>s = 0</c> を選んで、そこから t を決める——
    /// 距離としては正しく、床に寝かせた2本のカプセルでは
    /// このあと端の球を足す(<see cref="Collision3D.CapsuleCapsule"/>)ので実害が無い。
    /// </para>
    ///
    /// <para>
    /// 出典は Christer Ericson, <i>Real-Time Collision Detection</i> 5.1.9
    /// (<c>ClosestPtSegmentSegment</c>)。
    /// 物理エンジンでカプセルを扱うなら必ず1本は書くことになる関数で、
    /// <b>Bullet も PhysX も中身はこれ</b>。
    /// </para>
    /// </summary>
    public static void ClosestPoints(
        in Segment3D p,
        in Segment3D q,
        out Vector3 closestOnP,
        out Vector3 closestOnQ)
    {
        Vector3 d1 = p.Delta;
        Vector3 d2 = q.Delta;
        Vector3 r = p.Start - q.Start;

        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);

        const float epsilon = 1e-12f;

        float s;
        float t;

        if (a <= epsilon && e <= epsilon)
        {
            // 両方とも点。**カプセル2つが球2つに退化した場合**。
            closestOnP = p.Start;
            closestOnQ = q.Start;
            return;
        }

        if (a <= epsilon)
        {
            // P が点。**球とカプセルの判定に落ちる**。
            s = 0.0f;
            t = Math.Clamp(f / e, 0.0f, 1.0f);
        }
        else
        {
            float c = Vector3.Dot(d1, r);

            if (e <= epsilon)
            {
                // Q が点。
                t = 0.0f;
                s = Math.Clamp(-c / a, 0.0f, 1.0f);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denominator = (a * e) - (b * b);

                // **平行なら分母が 0**。s は 0 に決め打ちする。
                s = denominator > epsilon
                    ? Math.Clamp(((b * f) - (c * e)) / denominator, 0.0f, 1.0f)
                    : 0.0f;

                t = ((b * s) + f) / e;

                // **t が外へ出たら、t を端に留めたうえで s を求め直す**。
                // ここを省くと答えが端に寄りすぎる。
                if (t < 0.0f)
                {
                    t = 0.0f;
                    s = Math.Clamp(-c / a, 0.0f, 1.0f);
                }
                else if (t > 1.0f)
                {
                    t = 1.0f;
                    s = Math.Clamp((b - c) / a, 0.0f, 1.0f);
                }
            }
        }

        closestOnP = p.PointAt(s);
        closestOnQ = q.PointAt(t);
    }
}

/// <summary>
/// カプセル。**線分から一定の距離にある点の集合**(要点1)。
///
/// 別の言い方をすると、<b>球を線分に沿って滑らせた跡</b>。
/// この一言が今日の判定を全部説明してしまう——
/// 相手にいちばん近い位置へ球を滑らせれば、あとは Day 43 の球の判定に落ちる。
///
/// <para>
/// <b>球 → 箱 → カプセルで、向きの効き方が 0 → 3 → 1 と並ぶ</b>
/// (Day 44 の <see cref="Box3D"/> に書いたとおり)。
/// カプセルは軸1本ぶんだけ向きを持つので、
/// </para>
/// <list type="bullet">
/// <item>判定は<b>球とほとんど同じ安さ</b>(線分の最近接点を1回取るだけ)</item>
/// <item>慣性テンソルは<b>軸方向と横方向の2種類</b>(箱の3種類より1つ少ない)</item>
/// <item>角も辺も無いので、<b>SAT のような場合分けが要らない</b></item>
/// </list>
///
/// <para>
/// <b>だからキャラクターはカプセルで表す</b>。人型を箱で囲うと角が地形に引っかかり、
/// 球で囲うと背が足りない。カプセルなら「立った人の形」に近いうえに、
/// <b>どの向きから当たっても丸い</b>ので、階段でも坂でも段差でも
/// 引っかからずに滑る。Unity の <c>CharacterController</c> も
/// Unreal の <c>UCapsuleComponent</c> も、中身はこの形。
/// </para>
///
/// <para>
/// <b>円柱ではなくカプセルを選ぶ</b>のはロードマップに書いたとおりで、
/// 円柱はフチ(側面と底面の境の円)の場合分けが多く、工数対効果が悪い。
/// 実際のゲームエンジンでもカプセル近似が一般的になっている。
/// </para>
/// </summary>
internal readonly struct Capsule3D
{
    /// <summary>背骨の線分。**両端は「球の中心」であって、カプセルの先端ではない**。</summary>
    public readonly Segment3D Segment;

    /// <summary>半径 [m]。線分からこの距離までがカプセルの中。</summary>
    public readonly float Radius;

    public Capsule3D(in Segment3D segment, float radius)
    {
        Segment = segment;
        Radius = radius;
    }

    public Capsule3D(Vector3 start, Vector3 end, float radius)
        : this(new Segment3D(start, end), radius)
    {
    }

    /// <summary>重心(線分の中点)。**密度が一様なら、ここが重心になる**。</summary>
    public Vector3 Center => Segment.Center;

    /// <summary>線分の半分の長さ [m]。**全高は <c>2 * (HalfHeight + Radius)</c>**。</summary>
    public float HalfHeight => Segment.Length * 0.5f;

    /// <summary>
    /// 軸の向き(長さ1)。線分が退化していれば上向きを返す。
    ///
    /// **カプセルが球に退化しても NaN を出さない**ための保険。
    /// 半分の高さ 0 のカプセルは、正しく球として振る舞ってほしい。
    /// </summary>
    public Vector3 Axis
    {
        get
        {
            Vector3 delta = Segment.Delta;
            float length = delta.Length();
            return length > 1e-6f ? delta / length : Vector3.UnitY;
        }
    }

    /// <summary>
    /// 中心・向き・寸法から作る。**軸は物体座標の Y**。
    ///
    /// 剛体の向き(<see cref="RigidBody.Orientation"/>)がそのままカプセルの向きになる。
    /// <b>Y 軸に決め打ちする</b>のは、キャラクターが立っている姿勢と揃えるため——
    /// 慣性テンソル(<see cref="Collider.InertiaLocal"/>)も同じ約束で書いてある。
    /// </summary>
    public static Capsule3D FromCenter(
        Vector3 center, Quaternion orientation, float radius, float halfHeight)
    {
        Vector3 axis = Vector3.Transform(Vector3.UnitY, orientation) * halfHeight;
        return new Capsule3D(center - axis, center + axis, radius);
    }

    /// <summary>
    /// **相手にいちばん近い位置へ滑らせた球**。今日の判定の共通の入口。
    ///
    /// カプセルが「球を線分に沿って滑らせたもの」なら、
    /// 相手に対する判定は「いちばん近い位置に球を置いたときの、球の判定」で足りる。
    /// <b>球と球・球と平面・球と箱は Day 43〜44 で全部書いてある</b>ので、
    /// 今日足す判定は実質「どこへ滑らせるか」を決めるだけになる。
    ///
    /// <para>
    /// <b>これが厳密に正しいのは相手が凸のとき</b>。凸形状なら
    /// 「最近接点の対」が距離を決めるので、そこに置いた球で判定して過不足がない。
    /// 凹んだ形(Day 46 の地形)では面ごとに分けて当てることになる。
    /// </para>
    /// </summary>
    public Sphere3D SphereNear(Vector3 point) => new(Segment.ClosestPoint(point), Radius);

    /// <summary>下側の端の球(**足元**)。<see cref="CharacterController"/> が接地判定に使う。</summary>
    public Sphere3D LowerSphere => new(Segment.Start, Radius);

    /// <summary>上側の端の球(**頭**)。</summary>
    public Sphere3D UpperSphere => new(Segment.End, Radius);

    /// <summary>
    /// 外接する AABB(Day 46)。**端の球2つを包むだけ**。
    ///
    /// カプセルは「線分から半径ぶんの距離」なので、
    /// 線分の両端に半径を足した箱がそのまま外接箱になる。
    /// <b>間の胴を考えなくてよい</b>のは、線分が両端の間にしか無いから——
    /// カプセルが箱や球より「外接箱に無駄が出にくい」形なのはこの素直さによる。
    /// </summary>
    public Aabb3D Bounds => Aabb3D.Union(
        Aabb3D.FromCenter(Segment.Start, Radius),
        Aabb3D.FromCenter(Segment.End, Radius));
}
