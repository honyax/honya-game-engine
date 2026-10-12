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
/// 3D の衝突判定。**今日、箱が入って5通りになった**。
///
/// <see cref="Collision2D"/> の説明に書いた表を、3D で埋めていく。
/// Phase 7 で扱う形は 球 / 箱(OBB) / カプセル / 平面 / 地形 の5種類なので、
/// 組み合わせは 15 通り。今日終わって<b>5通り</b>。
///
/// <list type="table">
/// <item><term>球 × 球</term><description>Day 43。中心距離と半径の和を比べるだけ</description></item>
/// <item><term>球 × 平面</term><description>Day 43。符号付き距離と半径を比べるだけ</description></item>
/// <item><term>球 × 箱</term><description><b>今日</b>。箱の上のいちばん近い点との距離</description></item>
/// <item><term>箱 × 平面</term><description><b>今日</b>。8つの頂点を平面に落とすだけ</description></item>
/// <item><term>箱 × 箱</term><description><b>今日</b>。分離軸定理(<see cref="Sat"/>)</description></item>
/// <item><term>カプセル × 各種</term><description>Day 45(線分間距離)</description></item>
/// <item><term>地形 × 各種</term><description>Day 46(高さマップ)</description></item>
/// </list>
///
/// <para>
/// <b>Day 43 とは山場の位置が違う</b>。あの日は判定が 30 行で、
/// 残り 300 行が「当たったあとどうするか」だった。
/// 今日は解決の側がほとんど変わらず(点が増えただけ)、
/// <b>判定の中身が一気に膨らむ</b>——箱どうしの判定と接触点の生成で
/// <see cref="Sat"/> が 300 行を超える。
/// </para>
///
/// <para>
/// 戻り値も変わった。Day 43 は1点(<see cref="Contact3D"/>)を返していたが、
/// 今日からは<b>最大4点の束</b>(<see cref="ContactManifold"/>)を返す。
/// 球が絡む判定は点が1つしか出ないので <see cref="Contact3D"/> のまま書いて、
/// <see cref="Collide"/> のところで束に包み直している。
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
    /// 形が増えるほど効いてくる(15 通りのうち実際に書くのは 5 通りで済む)。
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

            default:
                return default;
        }
    }

    /// <summary>1点の判定結果を、マニフォールドの形に包み直す。</summary>
    private static ContactManifold Wrap(in Contact3D contact) =>
        contact.Hit ? ContactManifold.Single(contact, ManifoldSource.Point) : default;
}
