using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 3D で当たった結果。**<see cref="Contact2D"/> に接触点が1つ増えた形**。
///
/// 2D では「法線と深さ」で足りた。3D で接触点が要るのは、
/// <b>回転を扱い始めたから</b>。
///
/// <list type="bullet">
/// <item>力やインパルスを**どこに**掛けるかでトルクが変わる(Day 43 の要点2)</item>
/// <item>だから「押し戻す向き」だけでなく「押し戻す場所」が要る</item>
/// </list>
///
/// Day 26 までの2Dは並進しか無かったので、場所を持たずに済んでいた。
///
/// <para>
/// <see cref="Normal"/> は <see cref="Contact2D"/> と同じ約束で、
/// <b>A を B から引き離す向き</b>で長さ1。
/// A を <c>Normal * Depth</c> だけ動かせばちょうど接する。
/// **どちらから見た法線か**を取り違えると物がめり込む方向へ飛ぶので、
/// 2D と同じ約束で通してある。
/// </para>
///
/// <para>
/// <see cref="Point"/> は**めり込んだ領域の真ん中**を採る。
/// 「A の表面上の点」と「B の表面上の点」は、めり込んでいる間ずれているので、
/// どちらか一方を選ぶと押し戻しの向きに偏りが出る。中点なら偏らない。
/// </para>
/// </summary>
internal readonly struct Contact3D
{
    public readonly bool Hit;
    public readonly Vector3 Normal;
    public readonly float Depth;
    public readonly Vector3 Point;

    private Contact3D(bool hit, Vector3 normal, float depth, Vector3 point)
    {
        Hit = hit;
        Normal = normal;
        Depth = depth;
        Point = point;
    }

    public static Contact3D None => default;

    public static Contact3D Touching(Vector3 normal, float depth, Vector3 point) =>
        new(true, normal, depth, point);

    /// <summary>
    /// A と B を入れ替えた版。**法線を裏返すだけ**。
    ///
    /// 判定関数は「球と平面」の順でしか書いていないので、
    /// 呼ぶ側が逆順で持っているときにこれで揃える。
    /// 深さと接触点は入れ替えても同じ。
    /// </summary>
    public Contact3D Flipped() => new(Hit, -Normal, Depth, Point);
}

/// <summary>
/// 3D の衝突判定。**今日、カプセルが入って9通りになった**。
///
/// <see cref="Collision2D"/> の説明に書いた表を、3D で埋めていく。
/// Phase 7 で扱う形は 球 / 箱(OBB) / カプセル / 平面 / 地形 の5種類なので、
/// 組み合わせは 15 通り。今日終わって<b>9通り</b>。
///
/// <list type="table">
/// <item><term>球 × 球</term><description>Day 43。中心距離と半径の和を比べるだけ</description></item>
/// <item><term>球 × 平面</term><description>Day 43。符号付き距離と半径を比べるだけ</description></item>
/// <item><term>球 × 箱</term><description>Day 44。箱の上のいちばん近い点との距離</description></item>
/// <item><term>箱 × 平面</term><description>Day 44。8つの頂点を平面に落とすだけ</description></item>
/// <item><term>箱 × 箱</term><description>Day 44。分離軸定理(<see cref="Sat"/>)</description></item>
/// <item><term>カプセル × 球</term><description><b>今日</b>。線分上へ球を滑らせて、球と球に落とす</description></item>
/// <item><term>カプセル × 平面</term><description><b>今日</b>。両端の球を平面に当てる。**最大2点**</description></item>
/// <item><term>カプセル × 箱</term><description><b>今日</b>。線分と箱の最近接点(交互射影)</description></item>
/// <item><term>カプセル × カプセル</term><description><b>今日</b>。線分どうしの最近接点</description></item>
/// <item><term>地形 × 各種</term><description>Day 46(高さマップ)</description></item>
/// </list>
///
/// <para>
/// <b>Day 44 とは山場の位置がまた違う</b>。箱の日は判定と接触点の生成で 700 行だった。
/// 今日は<b>判定が驚くほど短い</b>——4通り足して 200 行に届かない。
/// カプセルが「線分から一定距離」でしかなく、
/// <b>球の判定に落とせる</b>からで、
/// <see cref="Sphere3D"/> の説明に「カプセルは球を引き伸ばしたもの」と
/// Day 44 に書いておいたのが、そのまま実装になっている。
/// </para>
///
/// <para>
/// <b>そのぶん今日の重心は <see cref="CharacterController"/> にある</b>。
/// 判定が答えるのは「当たったか」までで、
/// 「坂は登れるか」「段差は越えられるか」「今は接地しているか」は
/// 判定の外側で決めることになる——**そこが今日の 350 行**。
/// </para>
///
/// <para>
/// 戻り値は Day 44 のまま<b>最大4点の束</b>(<see cref="ContactManifold"/>)。
/// カプセルは球と同じく1点しか出ないことが多いが、
/// <b>寝かせると2点になる</b>——床に転がしたカプセルが震えないのはそのおかげで、
/// Day 44 の「面で触れているものは面で支える」がそのまま効いている。
/// </para>
/// </summary>
internal static class Collision3D
{
    /// <summary>
    /// 球と球。**中心距離と半径の和を比べるだけ**。
    ///
    /// 平方根を1回だけ使う。判定「だけ」なら距離の2乗の比較で済ませて
    /// <c>MathF.Sqrt</c> を避けられるが、
    /// **当たっているときは法線と深さが要る**ので結局は根が要る。
    /// 「まず2乗で足切りして、当たったものだけ根を取る」の形にしてあるのはそのため——
    /// 数千組を回すとき、ほとんどの組は最初の <c>if</c> で返る。
    ///
    /// <para>
    /// <b>中心が完全に一致したとき</b>は法線が決められない(0 ベクトルを正規化すると NaN)。
    /// 同じ場所に2つ湧かせると本当に起きるので、
    /// 適当な向き(上)を返して**とにかく引き離す**。
    /// NaN を1つ流すと、その後のフレームで座標も速度も全部 NaN になり、
    /// **画面から物が消える**——原因を追いにくい種類の事故なので、ここで止める。
    /// </para>
    /// </summary>
    public static Contact3D SphereSphere(in Sphere3D a, in Sphere3D b)
    {
        Vector3 delta = a.Center - b.Center;
        float radiusSum = a.Radius + b.Radius;
        float squared = delta.LengthSquared();

        if (squared >= radiusSum * radiusSum)
        {
            return Contact3D.None;
        }

        float distance = MathF.Sqrt(squared);

        // 中心が一致していると向きが決まらない。**上へ逃がす**。
        Vector3 normal = distance > 1e-6f ? delta / distance : Vector3.UnitY;
        float depth = radiusSum - distance;

        // 接触点は**めり込んだ領域の真ん中**。
        // A の表面(中心から -normal 方向へ半径ぶん)から、めり込みの半分だけ戻る。
        Vector3 point = a.Center - (normal * (a.Radius - (depth * 0.5f)));

        return Contact3D.Touching(normal, depth, point);
    }

    /// <summary>
    /// 球と平面。**符号付き距離と半径を比べるだけ**。
    ///
    /// 法線は<b>常に平面の法線</b>になる。
    /// 球を平面から引き離す向き = 平面の表向き、なので約束(A を B から離す向き)と一致する。
    /// 平面の裏側へ抜けた球は符号付き距離が負になり、深さが半径より大きくなる——
    /// **抜けたぶんも含めて表側へ押し返す**式になっているので、
    /// 1フレームで貫通した球も次のステップで戻ってくる。
    ///
    /// <para>
    /// ただしこれは<b>薄い床には効かない</b>。平面は無限に厚いので裏へ抜けても戻せるが、
    /// 実際の床(箱)は速い弾が1ステップで通り抜ける。
    /// 通り抜けを本当に止めるには連続衝突判定(CCD)が要る——今日は扱わない。
    /// </para>
    /// </summary>
    public static Contact3D SpherePlane(in Sphere3D sphere, in Plane3D plane)
    {
        float distance = plane.SignedDistance(sphere.Center);
        if (distance >= sphere.Radius)
        {
            return Contact3D.None;
        }

        float depth = sphere.Radius - distance;

        // 接触点は面の上の点。**球の真下**(法線に沿って距離ぶん降りた点)。
        Vector3 point = sphere.Center - (plane.Normal * distance);

        return Contact3D.Touching(plane.Normal, depth, point);
    }

    /// <summary>
    /// 球と箱。**箱の上でいちばん近い点との距離を測るだけ**。
    ///
    /// <see cref="Box3D.ClosestPoint"/> で最近接点を取り、
    /// そこまでの距離が半径より短ければ当たっている。
    /// 傾いた箱でも物体座標へ持ち込めば軸に沿った箱になるので、
    /// **Day 25 の 2D の円と箱(<c>Collision2D.CircleBox</c>)とほとんど同じ形**になる。
    ///
    /// <para>
    /// <b>球の中心が箱の中に入ったときだけ別扱い</b>が要る。
    /// 最近接点が中心そのものになり距離が 0 なので、法線が決められない。
    /// このときは<b>いちばん近い面へ押し出す</b>——
    /// 3つの軸それぞれについて「面までどれだけ余裕があるか」を測り、
    /// いちばん余裕の少ない面を抜け道に選ぶ。
    /// 深くめり込ませると突然別の面から出てくることがあるが、
    /// そもそも中心まで潜られている時点で正解は無い。
    /// </para>
    /// </summary>
    public static Contact3D SphereBox(in Sphere3D sphere, in Box3D box)
    {
        Vector3 local = box.ToLocal(sphere.Center);
        Vector3 clamped = Vector3.Clamp(local, -box.HalfExtents, box.HalfExtents);

        Vector3 delta = local - clamped;
        float squared = delta.LengthSquared();

        if (squared > sphere.Radius * sphere.Radius)
        {
            return Contact3D.None;
        }

        Vector3 localNormal;
        float depth;

        if (squared > 1e-12f)
        {
            float distance = MathF.Sqrt(squared);
            localNormal = delta / distance;
            depth = sphere.Radius - distance;
        }
        else
        {
            // 中心が箱の中。**いちばん近い面へ逃がす**。
            Vector3 slack = box.HalfExtents - Vector3.Abs(local);

            if (slack.X <= slack.Y && slack.X <= slack.Z)
            {
                localNormal = new Vector3(local.X >= 0.0f ? 1.0f : -1.0f, 0.0f, 0.0f);
                depth = sphere.Radius + slack.X;
            }
            else if (slack.Y <= slack.Z)
            {
                localNormal = new Vector3(0.0f, local.Y >= 0.0f ? 1.0f : -1.0f, 0.0f);
                depth = sphere.Radius + slack.Y;
            }
            else
            {
                localNormal = new Vector3(0.0f, 0.0f, local.Z >= 0.0f ? 1.0f : -1.0f);
                depth = sphere.Radius + slack.Z;
            }
        }

        // 法線は**球(A)を箱(B)から引き離す向き**。物体座標で作ったので世界へ戻す。
        Vector3 normal = Vector3.Transform(localNormal, box.Orientation);
        Vector3 surface = box.ToWorld(clamped);

        // 接触点はめり込んだ領域の真ん中。箱の表面から、めり込みの半分だけ内側。
        return Contact3D.Touching(normal, depth, surface - (normal * (depth * 0.5f)));
    }

    /// <summary>
    /// 箱と平面。**8つの頂点を平面に落とすだけ**(要点3)。
    ///
    /// 平面より下にある頂点が、そのまま接触点になる。
    /// 分離軸定理を持ち出すまでもない——平面は片側しか無いので、
    /// **試すべき軸は平面の法線1本だけ**だと分かっている。
    ///
    /// <para>
    /// <b>ここが今日いちばん分かりやすいマニフォールド</b>になる。
    /// 箱を平らに置けば4つの頂点が同時に沈むので接触点が4つ、
    /// 傾けて辺で立てれば2つ、角で立てれば1つ。
    /// <b>接触点の数が、そのまま支え方の数</b>になっているのが目で見える。
    /// </para>
    ///
    /// <para>
    /// 法線は平面の法線そのもの(箱 = A を平面 = B から引き離す向き)。
    /// 種類の札が <see cref="ManifoldSource.FaceB"/> なのは、
    /// <b>基準面が平面(B)の側</b>だから——箱の面ではなく平面が支えている。
    /// </para>
    /// </summary>
    public static ContactManifold BoxPlane(in Box3D box, in Plane3D plane)
    {
        var manifold = new ContactManifold
        {
            Normal = plane.Normal,
            Source = ManifoldSource.FaceB,
        };

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = box.Corner(i);
            float distance = plane.SignedDistance(corner);

            if (distance >= 0.0f)
            {
                continue;
            }

            // 接触点はめり込んだ領域の真ん中(頂点と、その真上の面上の点の中点)。
            manifold.Add(corner - (plane.Normal * (distance * 0.5f)), -distance);
        }

        return manifold;
    }

    /// <summary>
    /// 球とカプセル。**線分の上へ球を滑らせて、球と球に落とすだけ**(要点1)。
    ///
    /// カプセルは「線分から半径いくつ以内」という形なので、
    /// 相手の球にいちばん近い位置へ球を1つ置けば、そこから先は Day 43 の
    /// <see cref="SphereSphere"/> がそのまま使える。
    ///
    /// <para>
    /// <b>4行で終わる</b>のがカプセルの値打ち。箱では 15 本の軸を試したうえに
    /// 面と面をクリップしたのに、カプセルは最近接点を1回取るだけで済む。
    /// 角も辺も無い形なので、場合分けするものが最初から無い。
    /// </para>
    /// </summary>
    public static Contact3D SphereCapsule(in Sphere3D sphere, in Capsule3D capsule) =>
        SphereSphere(sphere, capsule.SphereNear(sphere.Center));

    /// <summary>
    /// カプセルと平面。**両端の球を平面に当てるだけ**。ここで初めて2点が出る(要点4)。
    ///
    /// カプセルの中でいちばん深く沈むのは、必ず<b>線分の端のどちらか</b>になる。
    /// 線分の中ほどが端より沈むことはない——平面は平らなので、
    /// 線分上の深さは端から端へ一次に変わるだけ。
    /// だから端2つを見れば漏れがない。
    ///
    /// <para>
    /// <b>寝かせたカプセルは2点、立てたカプセルは1点</b>になる。
    /// Day 44 の箱で「接触点の数 = 支え方の数」と書いたのがそのまま当てはまり、
    /// <b>床に転がしたカプセルが震えないのはこの2点目のおかげ</b>。
    /// 端を片方しか見ない実装にすると、寝たカプセルが1点で支えられて
    /// Day 44 の「上限 1」と同じ揺れ方を始める(自己チェックで確かめてある)。
    /// </para>
    ///
    /// <para>
    /// 種類の札が <see cref="ManifoldSource.FaceB"/> なのは、
    /// <b>基準面が平面(B)の側</b>だから——箱と平面(<see cref="BoxPlane"/>)と同じ扱い。
    /// </para>
    /// </summary>
    public static ContactManifold CapsulePlane(in Capsule3D capsule, in Plane3D plane)
    {
        var manifold = new ContactManifold
        {
            Normal = plane.Normal,
            Source = ManifoldSource.FaceB,
        };

        AddPlanePoint(ref manifold, capsule.Segment.Start, capsule.Radius, plane);
        AddPlanePoint(ref manifold, capsule.Segment.End, capsule.Radius, plane);

        return manifold;
    }

    /// <summary>端の球1つを平面に当てて、当たっていれば点を足す。</summary>
    private static void AddPlanePoint(
        ref ContactManifold manifold, Vector3 center, float radius, in Plane3D plane)
    {
        float distance = plane.SignedDistance(center);
        float depth = radius - distance;

        if (depth <= 0.0f)
        {
            return;
        }

        // 接触点はめり込んだ領域の真ん中。**球と平面(Day 43)と同じ置き方**。
        manifold.Add(center - (plane.Normal * distance), depth);
    }

    /// <summary>
    /// カプセルと箱。**線分と箱の最近接点を取って、球と箱に落とす**(要点3)。
    ///
    /// 最近接点対は <see cref="Box3D.ClosestPoint(in Segment3D, out Vector3)"/> が
    /// 交互射影で詰めてくれるので、ここは
    /// 「返ってきた線分上の点に球を置いて <see cref="SphereBox"/> を呼ぶ」だけ。
    ///
    /// <para>
    /// <b>それだと1点しか出ない</b>のが問題になる。箱の上に寝かせたカプセルは
    /// 面で触れているのに、最近接点は1つしか返らないので、
    /// Day 44 の「上限 1」と同じ揺れ方をする。
    /// そこで<b>寝ているときだけ端の球を足す</b>——
    /// <see cref="AddCapsuleEnds"/> がその役をしている。
    /// </para>
    /// </summary>
    public static ContactManifold CapsuleBox(in Capsule3D capsule, in Box3D box)
    {
        box.ClosestPoint(capsule.Segment, out Vector3 onSegment);

        Contact3D primary = SphereBox(new Sphere3D(onSegment, capsule.Radius), box);
        if (!primary.Hit)
        {
            return default;
        }

        var manifold = ContactManifold.Single(primary, ManifoldSource.FaceB);
        AddCapsuleEnds(ref manifold, capsule, primary, box, default, useBox: true);

        return manifold;
    }

    /// <summary>
    /// カプセルとカプセル。**線分どうしの最近接点を取って、球と球に落とす**(要点2)。
    ///
    /// <see cref="Segment3D.ClosestPoints"/> が2本の線分の最近接点を返すので、
    /// そこに球を1つずつ置けば <see cref="SphereSphere"/> がそのまま使える。
    /// <b>2本の線分の距離が、そのまま2本のカプセルの「中心線どうしの距離」</b>で、
    /// 半径の和と比べるだけで当たり判定が終わる。
    ///
    /// <para>
    /// <b>平行に寝かせた2本では2点が要る</b>。並べて置いた丸太のような配置で、
    /// 最近接点は1つに決まらない(どこを取っても同じ距離)。
    /// 1点で押し合うと接触点の位置が毎ステップ揺れて、
    /// 押されるたびに向きが変わる。<see cref="AddCapsuleEnds"/> で端を足しておく。
    /// </para>
    /// </summary>
    public static ContactManifold CapsuleCapsule(in Capsule3D a, in Capsule3D b)
    {
        Segment3D.ClosestPoints(a.Segment, b.Segment, out Vector3 onA, out Vector3 onB);

        Contact3D primary = SphereSphere(
            new Sphere3D(onA, a.Radius), new Sphere3D(onB, b.Radius));

        if (!primary.Hit)
        {
            return default;
        }

        var manifold = ContactManifold.Single(primary, ManifoldSource.Point);
        AddCapsuleEnds(ref manifold, a, primary, default, b, useBox: false);

        return manifold;
    }

    /// <summary>
    /// 寝ているカプセルに2点目・3点目を足す。**端の球を、基準の法線で測り直す**(要点4)。
    ///
    /// やっていることは3つだけ。
    /// <list type="number">
    /// <item>カプセルの端に球を置いて、<b>実際に相手へ当てる</b>(当たっていなければ足さない)</item>
    /// <item>深さは端の球が返した値ではなく、<b>基準の法線に沿って測り直す</b></item>
    /// <item>すでにある点に近すぎるものは捨てる</item>
    /// </list>
    ///
    /// <para>
    /// <b>2 が肝</b>。マニフォールドの法線は1本しか持てない(Day 44)ので、
    /// 端の球が別の向きの法線を返しても、それは使えない。
    /// 基準面(相手の表面。<c>基準点 = 接触点 + 法線 × 深さ/2</c>)からの距離で
    /// 測り直せば、4点が同じ向きの押し戻しになる。
    /// </para>
    ///
    /// <para>
    /// <b>1 を省くと、棚の縁に寝かせたカプセルで宙に浮いた接触点が立つ</b>。
    /// 基準面をどこまでも延長して測ってしまうので、
    /// 箱の外側にはみ出した端にも「めり込み」が出てしまう。
    /// 実際に球を当てて確かめるのはそのため。
    /// </para>
    ///
    /// <para>
    /// <b>3 は立っているカプセルのため</b>。立ったカプセルでは
    /// 最近接点と下の端の球がほぼ同じ場所になるので、
    /// そのまま足すと同じ点を2つ持つことになる。
    /// 同じ場所に2点あると、まとめて解くときに点の数で割る(Day 44a の要点5)ぶん
    /// <b>片方の押し戻しが半分になって沈む</b>。
    /// </para>
    ///
    /// <para>
    /// 相手が箱かカプセルかで当てる関数が違うだけなので、
    /// <paramref name="useBox"/> で切り替えている。
    /// 呼ぶ側が2つしかないのに委譲(デリゲート)を挟むと
    /// <b>N² の内側のループで割り当てが起きる</b>ので、素朴な bool にしてある。
    /// </para>
    /// </summary>
    private static void AddCapsuleEnds(
        ref ContactManifold manifold,
        in Capsule3D capsule,
        in Contact3D primary,
        in Box3D box,
        in Capsule3D other,
        bool useBox)
    {
        Vector3 normal = primary.Normal;

        // **相手の表面上の点**。接触点はめり込んだ領域の真ん中なので、
        // 法線の向きへ深さの半分だけ戻せば相手の面に乗る。
        Vector3 surface = primary.Point + (normal * (primary.Depth * 0.5f));

        // 同じ点を2つ持たないための距離のしきい値(半径の 1/4)。
        float minimumSpacing = capsule.Radius * 0.25f;

        for (int end = 0; end < 2; end++)
        {
            Vector3 center = end == 0 ? capsule.Segment.Start : capsule.Segment.End;
            var sphere = new Sphere3D(center, capsule.Radius);

            // 1. **本当に当たっているか**を相手の形で確かめる。
            bool hit = useBox
                ? SphereBox(sphere, box).Hit
                : SphereCapsule(sphere, other).Hit;

            if (!hit)
            {
                continue;
            }

            // 2. 深さは**基準の法線で測り直す**。
            float depth = capsule.Radius - Vector3.Dot(center - surface, normal);
            if (depth <= 0.0f)
            {
                continue;
            }

            Vector3 point = center - (normal * (capsule.Radius - (depth * 0.5f)));

            // 3. すでにある点に近すぎるなら捨てる。
            bool tooClose = false;
            for (int i = 0; i < manifold.Count; i++)
            {
                if ((manifold.Points[i].Point - point).LengthSquared()
                    < minimumSpacing * minimumSpacing)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                manifold.Add(point, depth);
            }
        }
    }

    // ================================================================
    //  Day 46: 地形(ハイトマップ)の判定
    // ================================================================

    /// <summary>
    /// 1回の地形の判定で見る三角形の上限。**はみ出したら諦める**。
    ///
    /// 1マス 1m の地形に半径 0.35m のカプセルを立てれば、
    /// 触れうるマスは 2×2 = 4、三角形は 8 枚。
    /// 32 枚あれば「4×4 マスにまたがる大きな箱」まで届く。
    /// <b>足りなくなるのは地形のマスを細かくしすぎたとき</b>で、
    /// そのときはマスを粗くするのが正しい直し方になる。
    /// </summary>
    private const int MaxTerrainTriangles = 32;

    /// <summary>
    /// 球と三角形。**最近接点まで測って、面の法線で押し戻す**(要点2・要点3)。
    ///
    /// 前半は <see cref="SphereBox"/> とまったく同じ形をしている——
    /// 「相手の上でいちばん近い点を取って、距離を半径と比べる」。
    /// 違うのは<b>押し戻す向きの決め方</b>で、ここが今日いちばん細かい判断になる。
    ///
    /// <para>
    /// <b>法線は必ず三角形の面の法線にする</b>(内部エッジ対策。要点3)。
    /// 素直に「中心 - 最近接点」を正規化すると、
    /// 最近接点が三角形の<b>辺</b>に乗ったときに横向きの法線が出る。
    /// 平らな地面を2枚の三角形で作って球を転がすと、
    /// 対角線をまたぐ瞬間だけ<b>横へ蹴られる</b>——
    /// これが有名な「内部エッジ問題」で、
    /// 三角形メッシュの地形を書いた人が必ず1度は踏む。
    /// </para>
    ///
    /// <para>
    /// 面の法線に寄せると、代わりに<b>本当の縁</b>(地形の端や崖の上端)でも
    /// 上向きに押してしまう。地形の中では縁のほとんどが内部エッジなので、
    /// この取り替えは<b>ほぼ必ず得</b>になる。
    /// Bullet の <c>btInternalEdgeUtility</c> は隣の面の向きを覚えておいて
    /// 「本当の縁か内部の縁か」を見分けるが、
    /// <b>高さの格子ならそもそも内部の縁しか無い</b>(端以外)ので、
    /// ここまで割り切ってよい。
    /// </para>
    ///
    /// <para>
    /// <b>裏側へ潜った球</b>は別扱い。深くめり込むと最近接点までの距離が
    /// 半径を超えてしまい、素直に書くと「当たっていない」と答えて
    /// <b>地面をすり抜けて落ちていく</b>。
    /// 面の裏側(<c>signed &lt; 0</c>)で、かつ最近接点が面の内側なら、
    /// 上へ押し戻す量を「半径 - 符号付き距離」にする——
    /// <see cref="SpherePlane"/> が裏へ抜けた球を戻すのと同じ式になる。
    /// </para>
    /// </summary>
    public static Contact3D SphereTriangle(in Sphere3D sphere, in Triangle3D triangle)
    {
        Vector3 closest = triangle.ClosestPoint(sphere.Center);
        float signed = triangle.SignedDistance(sphere.Center);

        if (signed < 0.0f)
        {
            // **面の裏側**(要点3)。地面に潜っている状態で、
            // 素直に距離を測ると半径を超えて「当たっていない」と答えてしまい、
            // <b>そのまま落ちていく</b>。
            //
            // 拾うかどうかは<b>真上から見て、この三角形の中にいるか</b>で決める。
            // 「最近接点が面の内側か」で決めると、深く潜った球では
            // 最近接点が縁や角に寄ってしまい、
            // <b>周りのどの三角形も受け持たない穴</b>ができる。
            // 高さの格子は「1つの (x, z) に高さが1つ」なので、
            // 真上から見た内外で決めれば必ずどれかが受け持つ。
            if (!triangle.ContainsColumn(sphere.Center))
            {
                return Contact3D.None;
            }

            // 押し戻す量は「半径 + 潜ったぶん」。<see cref="SpherePlane"/> が
            // 裏へ抜けた球を戻すのとまったく同じ式になる。
            return Contact3D.Touching(
                triangle.Normal,
                sphere.Radius - signed,
                sphere.Center - (triangle.Normal * signed));
        }

        float squared = (sphere.Center - closest).LengthSquared();
        if (squared >= sphere.Radius * sphere.Radius)
        {
            return Contact3D.None;
        }

        // **法線は面の法線**(内部エッジ対策)。深さは実際の距離から測る。
        return Contact3D.Touching(
            triangle.Normal, sphere.Radius - MathF.Sqrt(squared), closest);
    }

    /// <summary>
    /// カプセルと三角形。**線分と三角形の最近接点を取って、球と三角形に落とす**。
    ///
    /// Day 45 の <see cref="CapsuleBox"/> とまったく同じ形。
    /// 「どこへ球を滑らせるか」を決めれば、あとは球の判定がそのまま使える——
    /// カプセルが「線分から一定の距離」であることの配当が、
    /// 相手が三角形になっても変わらずに効いている。
    ///
    /// <para>
    /// <b>端の球を足す処理(<c>AddCapsuleEnds</c>)が要らない</b>のが、
    /// 箱のときとの違い。地形の上に寝かせたカプセルは
    /// <b>複数の三角形にまたがる</b>ので、
    /// 三角形ごとに1点ずつ出せば自然に2点以上のマニフォールドになる
    /// (<see cref="CapsuleTerrain"/>)。
    /// 面が1枚しか無い箱では、そうはいかなかった。
    /// </para>
    /// </summary>
    public static Contact3D CapsuleTriangle(in Capsule3D capsule, in Triangle3D triangle)
    {
        triangle.ClosestPoint(capsule.Segment, out Vector3 onSegment);

        return SphereTriangle(new Sphere3D(onSegment, capsule.Radius), triangle);
    }

    /// <summary>
    /// 球と地形。**触れうるマスの三角形を全部当てて、深い順に4点まで採る**(要点4)。
    ///
    /// 手順は3つだけ。
    /// <list type="number">
    /// <item>外接箱からマスの範囲を出す(<see cref="Terrain3D.CellRange"/>。割り算2回)</item>
    /// <item>そのマスの三角形を1枚ずつ当てて、当たったものを控える</item>
    /// <item>いちばん深いものの法線をマニフォールドの法線にして、点を並べる</item>
    /// </list>
    ///
    /// <para>
    /// <b>3 が今日の新しいところ</b>。マニフォールドは法線を1本しか持てない
    /// (Day 44a の要点3)のに、地形の三角形はそれぞれ違う向きを向いている。
    /// 谷の底にはまった球なら、左の斜面と右の斜面から別の向きに押される——
    /// そのままでは2本の法線が要ることになる。
    /// </para>
    ///
    /// <para>
    /// <b>いちばん深いものを基準にして、向きの近いものだけを足す</b>のが答え。
    /// 向きが 60 度以上違う面は捨てる(<c>Dot &lt; 0.5</c>)。
    /// 捨てられた面は<b>次のステップで拾われる</b>——
    /// 押し戻されて浅くなれば、今度は別の面がいちばん深くなるので、
    /// 数ステップで谷の底に落ち着く。
    /// <b>1ステップで完全に解こうとしない</b>のは Day 43 から一貫している構えで、
    /// 速度の反復も位置の補正も同じ考え方でできている。
    /// </para>
    /// </summary>
    public static ContactManifold SphereTerrain(in Sphere3D sphere, in Terrain3D terrain)
    {
        Span<Contact3D> hits = stackalloc Contact3D[MaxTerrainTriangles];
        int count = GatherTerrain(sphere.Bounds, terrain, hits, sphere, default, useCapsule: false);

        return BuildTerrainManifold(hits[..count], sphere.Radius);
    }

    /// <summary>
    /// カプセルと地形。**球と地形と同じ手順で、当てる関数だけが違う**。
    ///
    /// <b>地形の上を歩けるようになるのはこの関数</b>。
    /// <see cref="CharacterController"/> は
    /// <see cref="PhysicsWorld.QueryCapsule"/> 越しにこれを呼び、
    /// 返ってきた法線の Y から「登れる坂か」を決める(Day 45b の要点2)。
    /// つまり<b>坂の上限は三角形1枚ごとに効く</b>ことになる。
    /// </summary>
    public static ContactManifold CapsuleTerrain(in Capsule3D capsule, in Terrain3D terrain)
    {
        Span<Contact3D> hits = stackalloc Contact3D[MaxTerrainTriangles];
        int count = GatherTerrain(
            capsule.Bounds, terrain, hits, default, capsule, useCapsule: true);

        return BuildTerrainManifold(hits[..count], capsule.Radius);
    }

    /// <summary>
    /// 箱と地形。**8つの角の下に地面があるかを見るだけ**(要点4)。
    ///
    /// 球やカプセルとは<b>まったく違う解き方</b>をしている。
    /// 箱と三角形をまともに当てると分離軸が 13 本(箱の3面 + 三角形の1面 + 外積9本)、
    /// そこから接触点を作るのに面のクリップが要る——Day 44 の SAT がもう1本要ることになる。
    ///
    /// <para>
    /// <b>地形が「高さの関数」であることを使えば、それが要らない</b>。
    /// 箱の角の (x, z) における地面の高さは
    /// <see cref="Terrain3D.TryHeightAt"/> が補間1回で答える。
    /// 角がそれより下にあれば、そのぶん押し上げればよい。
    /// 床に置いた箱は<b>4つの角が同時に沈む</b>ので、
    /// Day 44 で欲しかった「面で支える4点」が自然に出る。
    /// </para>
    ///
    /// <para>
    /// <b>代償ははっきりしている</b>。角が1つも沈んでいないのに
    /// 面の途中が地形の尖った峰に刺さっている、という配置を見逃す。
    /// 1マスより小さい箱では起きないので、
    /// <b>「地形のマスより小さい箱しか置かない」</b>という約束で回避している。
    /// まともに直すなら箱と三角形の SAT を書くことになる(改造課題3)。
    /// </para>
    ///
    /// <para>
    /// 深さを<b>法線に沿って測り直している</b>のも要点。
    /// 高さの差(縦の距離)をそのまま深さにすると、
    /// 急な斜面では実際のめり込みより大きくなって箱が跳ねる——
    /// 45 度の斜面なら 1.41 倍。法線の Y を掛けるだけで正しくなる。
    /// </para>
    /// </summary>
    public static ContactManifold BoxTerrain(in Box3D box, in Terrain3D terrain)
    {
        Span<Contact3D> hits = stackalloc Contact3D[8];
        int count = 0;

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = box.Corner(i);

            if (!terrain.TryHeightAt(corner.X, corner.Z, out float ground)
                || corner.Y >= ground)
            {
                continue;
            }

            terrain.TryNormalAt(corner.X, corner.Z, out Vector3 normal);

            // **縦の差 × 法線の Y** が、法線に沿った本当のめり込み。
            float depth = (ground - corner.Y) * normal.Y;
            if (depth <= 0.0f)
            {
                continue;
            }

            hits[count++] = Contact3D.Touching(
                normal, depth, new Vector3(corner.X, ground, corner.Z));
        }

        // 角の間隔は箱の大きさで決まるので、まとめる距離もそれに合わせる。
        return BuildTerrainManifold(hits[..count], box.HalfExtents.Length() * 0.5f);
    }

    /// <summary>
    /// 触れうるマスの三角形を1枚ずつ当てて、当たったものを控える。
    ///
    /// 球とカプセルで当てる関数が違うだけなので、
    /// <paramref name="useCapsule"/> で切り替えている——
    /// Day 45 の <c>AddCapsuleEnds</c> と同じ理由で、
    /// <b>委譲(デリゲート)を挟むと内側のループで割り当てが起きる</b>。
    /// </summary>
    private static int GatherTerrain(
        in Aabb3D bounds,
        in Terrain3D terrain,
        Span<Contact3D> hits,
        in Sphere3D sphere,
        in Capsule3D capsule,
        bool useCapsule)
    {
        if (!terrain.CellRange(bounds, out int x0, out int z0, out int x1, out int z1))
        {
            return 0;
        }

        int count = 0;

        for (int cz = z0; cz <= z1; cz++)
        {
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int which = 0; which < 2; which++)
                {
                    if (count >= hits.Length)
                    {
                        return count;
                    }

                    Triangle3D triangle = terrain.Triangle(cx, cz, which);

                    Contact3D contact = useCapsule
                        ? CapsuleTriangle(capsule, triangle)
                        : SphereTriangle(sphere, triangle);

                    if (contact.Hit)
                    {
                        hits[count++] = contact;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>
    /// 三角形ごとの接触を、1本の法線を持つマニフォールドにまとめる(要点4)。
    ///
    /// <list type="number">
    /// <item>いちばん深いものを選ぶ。**その法線がマニフォールドの法線**</item>
    /// <item>向きが 60 度以内の接触だけを足す。それ以外は次のステップに任せる</item>
    /// <item>深さは法線に沿って測り直す(<c>depth × cosθ</c>)</item>
    /// <item>近すぎる点は捨てる。**同じ点を2つ持つと押し戻しが半分になる**</item>
    /// </list>
    ///
    /// <para>
    /// <b>4 は Day 45 で踏んだ落とし穴と同じ</b>。
    /// 隣り合う三角形は辺を共有しているので、
    /// 球がその辺の上に乗っていると<b>2枚とも同じ点を返す</b>。
    /// まとめて解くとき点の数で割る(Day 44a の要点5)ので、
    /// 同じ点が2つあると押し戻しがちょうど半分になって沈む。
    /// </para>
    /// </summary>
    private static ContactManifold BuildTerrainManifold(
        ReadOnlySpan<Contact3D> hits, float mergeScale)
    {
        if (hits.Length == 0)
        {
            return default;
        }

        // --- 1. いちばん深いものを選ぶ ---
        int deepest = 0;
        for (int i = 1; i < hits.Length; i++)
        {
            if (hits[i].Depth > hits[deepest].Depth)
            {
                deepest = i;
            }
        }

        var manifold = new ContactManifold
        {
            Normal = hits[deepest].Normal,

            // 基準面は地形(B)の側。箱と平面(Day 44)・カプセルと平面(Day 45)と同じ扱い。
            Source = ManifoldSource.FaceB,
        };

        manifold.Add(hits[deepest].Point, hits[deepest].Depth);

        // 近すぎる点をまとめる距離。**相手の大きさに比例させる**——
        // 半径 0.1m の小石と半径 2m の岩で同じ絶対値を使うと、
        // 片方では何もまとまらず、片方では全部1点になる。
        float mergeSquared = MathF.Max(mergeScale * 0.35f, 1e-3f);
        mergeSquared *= mergeSquared;

        // --- 2〜4. 残りを足す ---
        for (int i = 0; i < hits.Length && manifold.Count < ContactManifold.MaxPoints; i++)
        {
            if (i == deepest)
            {
                continue;
            }

            float alignment = Vector3.Dot(hits[i].Normal, manifold.Normal);
            if (alignment < 0.5f)
            {
                continue;
            }

            bool duplicate = false;
            for (int k = 0; k < manifold.Count; k++)
            {
                if ((manifold.Points[k].Point - hits[i].Point).LengthSquared() < mergeSquared)
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
            {
                manifold.Add(hits[i].Point, hits[i].Depth * alignment);
            }
        }

        return manifold;
    }

    /// <summary>
    /// カプセルを体1つに当てる。**形の札を見て振り分けるだけ**の窓口(Day 45)。
    ///
    /// <see cref="Collide"/> と役目が重なるようだが、こちらは
    /// <b>体になっていないカプセル</b>——つまり <see cref="CharacterController"/> の
    /// 当たり判定用の形——を相手にできる。
    /// キャラクターは剛体ではない(質量も速度も物理には預けていない)ので、
    /// <c>RigidBody</c> を1つでっち上げて <see cref="Collide"/> を呼ぶ、という形にはしたくない。
    ///
    /// <para>
    /// 法線は<b>カプセル(A)を体(B)から引き離す向き</b>。
    /// <see cref="PhysicsWorld.QueryCapsule"/> と
    /// <see cref="CharacterController"/> はこの約束に寄りかかっている。
    /// </para>
    /// </summary>
    public static ContactManifold CapsuleAgainst(in Capsule3D capsule, RigidBody body) =>
        body.Shape.Kind switch
        {
            ColliderKind.Sphere =>
                Wrap(SphereCapsule(body.ToSphere(), capsule)).Flipped(),

            ColliderKind.Plane => CapsulePlane(capsule, body.ToPlane()),

            ColliderKind.Box => CapsuleBox(capsule, body.ToBox()),

            ColliderKind.Capsule => CapsuleCapsule(capsule, body.ToCapsule()),

            // **地形(Day 46)**。ここが繋がると地形の上を歩けるようになる。
            ColliderKind.HeightField => CapsuleTerrain(capsule, body.ToTerrain()),

            _ => default,
        };

    /// <summary>
    /// 2つの剛体を当てる。**形の組み合わせで判定関数を選ぶだけ**の窓口。
    ///
    /// Day 43 では <see cref="PhysicsWorld"/> が
    /// 「体どうしなら <see cref="SphereSphere"/>、平面なら <see cref="SpherePlane"/>」と
    /// 直に呼び分けていた。形が2つしか無いうちはそれで足りたが、
    /// 5種類になると 15 通りになるので、**分岐は1箇所にまとめる**。
    ///
    /// <para>
    /// 判定関数は片方の順番でしか書いていないので、
    /// 逆順で来たときは <see cref="ContactManifold.Flipped"/> で法線を裏返す。
    /// <b>「箱と球」を書かずに「球と箱」だけ書けばよい</b>ようにするための仕掛けで、
    /// 形が増えるほど効いてくる(15 通りのうち実際に書くのは 9 通りで済む)。
    /// </para>
    ///
    /// <para>
    /// <b>カプセルの4組は <c>_</c>(何でもよい)で受けている</b>(Day 45)。
    /// 上の枝に <see cref="ColliderKind.Capsule"/> は1つも出てこないので、
    /// ここまで落ちてきた時点で相手は球・箱・平面のどれかに決まっている——
    /// あとは <see cref="CapsuleAgainst"/> が形の札を見て振り分ける。
    /// <b>この2行が下にあることに寄りかかっている</b>ので、
    /// 順番を入れ替えると球と箱の組み合わせがカプセル扱いになって静かに壊れる。
    /// Day 39 から書いている「具体的なものほど上」が、
    /// キーの <c>switch</c> ではないところにも出てきた。
    /// </para>
    ///
    /// <para>
    /// <b>平面どうしは当たらない</b>ことにしてある。どちらも静的なので、
    /// 当たっていても何も起きない。
    /// </para>
    /// </summary>
    public static ContactManifold Collide(RigidBody a, RigidBody b)
    {
        switch (a.Shape.Kind, b.Shape.Kind)
        {
            case (ColliderKind.Sphere, ColliderKind.Sphere):
                return Wrap(SphereSphere(a.ToSphere(), b.ToSphere()));

            case (ColliderKind.Sphere, ColliderKind.Plane):
                return Wrap(SpherePlane(a.ToSphere(), b.ToPlane()));

            case (ColliderKind.Plane, ColliderKind.Sphere):
                return Wrap(SpherePlane(b.ToSphere(), a.ToPlane())).Flipped();

            case (ColliderKind.Sphere, ColliderKind.Box):
                return Wrap(SphereBox(a.ToSphere(), b.ToBox()));

            case (ColliderKind.Box, ColliderKind.Sphere):
                return Wrap(SphereBox(b.ToSphere(), a.ToBox())).Flipped();

            case (ColliderKind.Box, ColliderKind.Plane):
                return BoxPlane(a.ToBox(), b.ToPlane());

            case (ColliderKind.Plane, ColliderKind.Box):
                return BoxPlane(b.ToBox(), a.ToPlane()).Flipped();

            case (ColliderKind.Box, ColliderKind.Box):
                return Sat.BoxBox(a.ToBox(), b.ToBox());

            // --- 地形(Day 46)---
            //
            // **カプセルの2行より上に置く**。下に置くと、
            // (カプセル, 地形) が上の `(Capsule, _)` に吸われて
            // <see cref="CapsuleAgainst"/> 経由になる——
            // それでも同じ関数に着くので<b>たまたま動く</b>が、
            // 「具体的なものほど上」の並びが崩れる。
            // 地形どうしは当たらない(どちらも静的)。
            case (ColliderKind.Sphere, ColliderKind.HeightField):
                return SphereTerrain(a.ToSphere(), b.ToTerrain());

            case (ColliderKind.HeightField, ColliderKind.Sphere):
                return SphereTerrain(b.ToSphere(), a.ToTerrain()).Flipped();

            case (ColliderKind.Box, ColliderKind.HeightField):
                return BoxTerrain(a.ToBox(), b.ToTerrain());

            case (ColliderKind.HeightField, ColliderKind.Box):
                return BoxTerrain(b.ToBox(), a.ToTerrain()).Flipped();

            // --- カプセル(Day 45)---
            //
            // **相手が何であっても1行**。ここまで落ちてきた時点で
            // 相手はカプセルか、球・箱・平面・地形のどれか。
            // 振り分けは <see cref="CapsuleAgainst"/> に任せる。
            case (ColliderKind.Capsule, _):
                return CapsuleAgainst(a.ToCapsule(), b);

            case (_, ColliderKind.Capsule):
                return CapsuleAgainst(b.ToCapsule(), a).Flipped();

            default:
                return default;
        }
    }

    /// <summary>1点の判定結果を、マニフォールドの形に包み直す。</summary>
    private static ContactManifold Wrap(in Contact3D contact) =>
        contact.Hit ? ContactManifold.Single(contact, ManifoldSource.Point) : default;
}
