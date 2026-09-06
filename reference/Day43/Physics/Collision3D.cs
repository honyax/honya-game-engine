using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 3D で当たった結果。**<see cref="Contact2D"/> に接触点が1つ増えた形**。
///
/// 2D では「法線と深さ」で足りた。3D で接触点が要るのは、
/// <b>回転を扱い始めたから</b>。
///
/// <list type="bullet">
/// <item>力やインパルスを**どこに**掛けるかでトルクが変わる(要点2)</item>
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
/// 3D の衝突判定。**今日は球と平面だけ**。
///
/// <see cref="Collision2D"/> の説明に書いた表を、3D で埋め始める日になる。
/// Phase 7 で扱う形は 球 / 箱(OBB) / カプセル / 平面 / 地形 の5種類なので、
/// 組み合わせは 15 通り。今日はそのうち<b>2通り</b>。
///
/// <list type="table">
/// <item><term>球 × 球</term><description>今日。中心距離と半径の和を比べるだけ</description></item>
/// <item><term>球 × 平面</term><description>今日。符号付き距離と半径を比べるだけ</description></item>
/// <item><term>箱 × 箱</term><description>Day 44(分離軸定理と接触マニフォールド)</description></item>
/// <item><term>カプセル × 各種</term><description>Day 45(線分間距離)</description></item>
/// <item><term>地形 × 各種</term><description>Day 46(高さマップ)</description></item>
/// </list>
///
/// <para>
/// <b>2通りしか無いのに、今日の山場はここではない</b>のが大事なところ。
/// 判定は 10 行で終わり、残りの 300 行は「当たったあとどうするか」
/// (<see cref="PhysicsWorld"/> のインパルス解決)に費やされる。
/// 物理エンジンの難しさは判定ではなく<b>解決</b>にある、というのが
/// 今日いちばん体で分かってほしいこと。
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
}
