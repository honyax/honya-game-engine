using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 当たった結果。**「当たったか」だけでは足りない**。
///
/// 判定だけなら <c>bool</c> で済むが、それだと当たったあと何もできない。
///   - 押し戻す(めり込みを解消する)には**どちらへ、どれだけ**が要る
///   - 跳ね返すには法線が要る
///   - ダメージ表示を出す位置にも接触点が要る
/// なので、判定関数は最初からこの3点を返す形にしておく。
///
/// <see cref="Normal"/> は**A を B から引き離す向き**で長さ1。
/// <see cref="Depth"/> だけ A を <c>-Normal</c> 方向へ動かせば、ちょうど接する。
/// 「どちらから見た法線か」は実装ごとに違うので、
/// **必ずコメントに書いておく**べきところ(ここを取り違えると物がめり込む方向に飛ぶ)。
/// </summary>
internal readonly struct Contact2D
{
    public readonly bool Hit;
    public readonly Vector2 Normal;
    public readonly float Depth;

    private Contact2D(bool hit, Vector2 normal, float depth)
    {
        Hit = hit;
        Normal = normal;
        Depth = depth;
    }

    public static Contact2D None => default;

    public static Contact2D Touching(Vector2 normal, float depth) => new(true, normal, depth);
}

/// <summary>
/// 2D の衝突判定。**安い順に並べてある**。
///
/// 形の組み合わせは、種類が N 個あれば N(N+1)/2 通りある。
/// 今日は円・AABB・OBB の3種類なので6通り。
/// 3D で球・箱・カプセル・平面・地形と増やすと 15 通りになり、
/// **この表を全部埋めるのが物理エンジンを書くということ**になる(Phase 7)。
///
/// 全部に共通する考え方が**分離軸定理(SAT)**で、
///
///   凸な形が2つあるとき、当たっていないなら
///   「その軸に投影すると2つが重ならない」軸が必ず存在する
///
/// と言っている。逆に言えば、**候補の軸を全部試して1本も分離できなければ当たっている**。
/// AABB 同士は「候補の軸が X と Y の2本しかない」特殊ケースにすぎず、
/// 円は「相手の最近点へ向かう1本だけ」を試せばよい形、と読める。
/// 別々の公式を覚えるのではなく、同じ定理の適用先が違うだけと見ると整理しやすい。
/// </summary>
internal static class Collision2D
{
    // ===== AABB 同士 =====

    /// <summary>
    /// 当たっているかだけ。**区間の重なりを X と Y で見るだけ**。
    ///
    /// 「4条件すべてが成り立てば重なっている」と肯定で書く。
    /// 「重なっていない」を <c>||</c> で並べて否定する形も等価だが、
    /// 座標に NaN が混ざったとき否定形は**「当たっている」を返してしまう**。
    /// 肯定形なら false になるので、壊れた物体が周囲を吹き飛ばさない。
    /// 分岐が短絡するので、外れている組ほど速く抜ける。
    /// </summary>
    public static bool Overlap(in Aabb2D a, in Aabb2D b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
        && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y;

    /// <summary>
    /// 法線とめり込み量まで求める。
    ///
    /// **重なりが小さいほうの軸で押し戻す**のが要点。
    /// 深くめり込んだ軸へ押すと、箱が反対側へ突き抜ける。
    /// 「いちばん浅い方向へ逃がす」のが、見た目にも自然になる。
    /// </summary>
    public static Contact2D Test(in Aabb2D a, in Aabb2D b)
    {
        Vector2 delta = b.Center - a.Center;
        Vector2 overlap = a.HalfSize + b.HalfSize - Vector2.Abs(delta);

        if (overlap.X <= 0.0f || overlap.Y <= 0.0f)
        {
            return Contact2D.None;
        }

        if (overlap.X < overlap.Y)
        {
            return Contact2D.Touching(new Vector2(MathF.CopySign(1.0f, delta.X), 0.0f), overlap.X);
        }

        return Contact2D.Touching(new Vector2(0.0f, MathF.CopySign(1.0f, delta.Y)), overlap.Y);
    }

    // ===== 円同士 =====

    /// <summary>
    /// **平方根を取らない**。二乗のまま比べる。
    ///
    /// 距離を求めるには <c>sqrt</c> が要るが、
    /// 「距離が半径の和より小さいか」を知りたいだけなら二乗同士で比べれば足りる。
    /// <c>sqrt</c> は加減乗算の 10 倍以上かかるので、
    /// **総当たりで何十万回も呼ぶ場面では効く**。
    /// </summary>
    public static bool Overlap(in Circle2D a, in Circle2D b)
    {
        float radii = a.Radius + b.Radius;
        return (b.Center - a.Center).LengthSquared() <= radii * radii;
    }

    public static Contact2D Test(in Circle2D a, in Circle2D b)
    {
        Vector2 delta = b.Center - a.Center;
        float radii = a.Radius + b.Radius;
        float distanceSquared = delta.LengthSquared();

        if (distanceSquared > radii * radii)
        {
            return Contact2D.None;
        }

        // ここでは押し戻す向きが要るので sqrt を取る。
        // **弾いたあとにだけ取る**のが順番として正しい。
        float distance = MathF.Sqrt(distanceSquared);

        if (distance < 1e-6f)
        {
            // 中心が完全に重なっていると向きが決まらない。
            // 放っておくと NaN が出て、以降その物体は永遠に消える。
            // **決め打ちでよいので、必ず何か返す**。
            return Contact2D.Touching(Vector2.UnitX, radii);
        }

        return Contact2D.Touching(delta / distance, radii - distance);
    }

    // ===== 円と AABB =====

    /// <summary>
    /// 箱の中で、点にいちばん近いところ。**各軸を範囲に押し込むだけ**。
    /// これが分かれば、円と箱の判定は「その点と円の中心の距離」に化ける。
    /// </summary>
    public static Vector2 ClosestPoint(in Aabb2D box, Vector2 point) =>
        Vector2.Clamp(point, box.Min, box.Max);

    public static Contact2D Test(in Circle2D circle, in Aabb2D box)
    {
        Vector2 closest = ClosestPoint(box, circle.Center);
        Vector2 delta = circle.Center - closest;
        float distanceSquared = delta.LengthSquared();

        if (distanceSquared > circle.Radius * circle.Radius)
        {
            return Contact2D.None;
        }

        // **中心が箱の中にあるとき**は最近点が中心そのものになり、距離が 0 になる。
        // 上の式では向きが決まらないので、AABB 同士と同じ
        // 「いちばん浅い面へ逃がす」に切り替える。
        // 円が箱にすっぽり入る場面(高速で飛び込んだ弾など)は普通に起きるので、
        // ここを書き忘れると**たまに弾が壁の中で止まる**。
        if (distanceSquared < 1e-12f)
        {
            Vector2 toCenter = circle.Center - box.Center;
            Vector2 overlap = box.HalfSize + new Vector2(circle.Radius) - Vector2.Abs(toCenter);

            return overlap.X < overlap.Y
                ? Contact2D.Touching(new Vector2(-MathF.CopySign(1.0f, toCenter.X), 0.0f), overlap.X)
                : Contact2D.Touching(new Vector2(0.0f, -MathF.CopySign(1.0f, toCenter.Y)), overlap.Y);
        }

        float distance = MathF.Sqrt(distanceSquared);

        // Normal は「円(A)を箱(B)から引き離す向き」の逆、
        // すなわち A から B へ向かう向きに揃える(Contact2D の約束)。
        return Contact2D.Touching(-delta / distance, circle.Radius - distance);
    }

    // ===== 円と OBB =====

    /// <summary>
    /// **箱の座標系に持ち込めば AABB と同じ**。
    ///
    /// 円は回しても円のままなので、
    /// 「箱を戻す回転」を円の中心にかけてしまえば、話は円 vs AABB に落ちる。
    /// 最後に法線だけワールドへ戻す。
    /// **難しい形を、知っている形に変換する**のは衝突判定の常套手段で、
    /// カプセル(Day 45)も「線分と点の距離」に落として解く。
    /// </summary>
    public static Contact2D Test(in Circle2D circle, in Obb2D box)
    {
        Vector2 localCenter = box.ToLocal(circle.Center);
        var localBox = new Aabb2D(-box.HalfSize, box.HalfSize);

        Contact2D local = Test(new Circle2D(localCenter, circle.Radius), localBox);
        if (!local.Hit)
        {
            return Contact2D.None;
        }

        // 法線をワールドへ。位置は要らないので回転だけ戻す。
        Vector2 worldNormal = (box.AxisX * local.Normal.X) + (box.AxisY * local.Normal.Y);
        return Contact2D.Touching(worldNormal, local.Depth);
    }

    // ===== OBB 同士(分離軸定理) =====

    /// <summary>
    /// 回転する矩形どうし。**候補の軸は4本**(それぞれの X 軸と Y 軸)。
    ///
    /// 手順は軸ごとに同じことの繰り返し。
    ///   1. 2つの箱をその軸に投影して、それぞれの「半径」を出す
    ///   2. 中心間の距離を同じ軸に投影する
    ///   3. 半径の和より離れていたら、**その時点で当たっていない**
    ///   4. 全部の軸で重なっていたら当たっている。
    ///      重なりが最小の軸が押し戻す向きになる
    ///
    /// 3 で即座に抜けられるのが SAT の効率のよいところで、
    /// **離れている組ほど速い**。当たっている組だけが4軸全部を回る。
    ///
    /// 投影半径の式が要点。軸 n に対して
    ///
    ///   半径 = |dot(n, 箱のX軸)| * halfX + |dot(n, 箱のY軸)| * halfY
    ///
    /// 「箱の辺を n に射影した長さの合計」で、
    /// 絶対値を取るのは向きに関係なく広がりを見たいから。
    /// </summary>
    public static Contact2D Test(in Obb2D a, in Obb2D b)
    {
        Vector2 delta = b.Center - a.Center;

        Vector2 bestNormal = Vector2.UnitX;
        float bestDepth = float.MaxValue;

        // 4本の軸を順に試す。配列を作ると割り当てが出るので、そのまま並べる。
        if (!TestAxis(a.AxisX, a, b, delta, ref bestNormal, ref bestDepth)
            || !TestAxis(a.AxisY, a, b, delta, ref bestNormal, ref bestDepth)
            || !TestAxis(b.AxisX, a, b, delta, ref bestNormal, ref bestDepth)
            || !TestAxis(b.AxisY, a, b, delta, ref bestNormal, ref bestDepth))
        {
            return Contact2D.None;
        }

        return Contact2D.Touching(bestNormal, bestDepth);
    }

    /// <summary>
    /// 1本の軸で分離できるか調べ、できなければ最小の重なりを更新する。
    /// 分離できたら false を返して打ち切る。
    /// </summary>
    private static bool TestAxis(
        Vector2 axis,
        in Obb2D a,
        in Obb2D b,
        Vector2 delta,
        ref Vector2 bestNormal,
        ref float bestDepth)
    {
        float radiusA = ProjectedRadius(a, axis);
        float radiusB = ProjectedRadius(b, axis);
        float separation = Vector2.Dot(delta, axis);
        float overlap = radiusA + radiusB - MathF.Abs(separation);

        if (overlap <= 0.0f)
        {
            return false;
        }

        if (overlap < bestDepth)
        {
            bestDepth = overlap;

            // **A から B へ向く側に揃える**。
            // 軸の向きは箱の作り方次第で反転するので、ここで符号をそろえないと
            // 押し戻しが逆向きになって、物体が相手にめり込んでいく。
            bestNormal = separation < 0.0f ? -axis : axis;
        }

        return true;
    }

    private static float ProjectedRadius(in Obb2D box, Vector2 axis) =>
        (MathF.Abs(Vector2.Dot(axis, box.AxisX)) * box.HalfSize.X)
        + (MathF.Abs(Vector2.Dot(axis, box.AxisY)) * box.HalfSize.Y);
}
