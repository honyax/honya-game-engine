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

    /// <summary>
    /// カプセルを体1つに当てる。**形の札を見て振り分けるだけ**の窓口(Day 45)。
    ///
    /// <see cref="Collide"/> のカプセルの枝は、全部ここへ落ちてくる。
    /// 引数を体ではなく<b>形(<see cref="Capsule3D"/>)で受けている</b>のは、
    /// 体になっていないカプセルも相手にできるようにするため——
    /// Day 45b のキャラクターは剛体ではない(質量も速度も物理には預けない)ので、
    /// <c>RigidBody</c> を1つでっち上げて <see cref="Collide"/> を呼ぶ、という形にはしたくない。
    ///
    /// <para>
    /// 法線は<b>カプセル(A)を体(B)から引き離す向き</b>。
    /// <see cref="Collide"/> でカプセルが B 側に来る組は、これを裏返して使う。
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

            // --- カプセル(Day 45)---
            //
            // **相手が何であっても1行**。ここまで落ちてきた時点で
            // 相手はカプセルか、球・箱・平面のどれか。
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
