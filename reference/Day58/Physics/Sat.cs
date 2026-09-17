using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 分離軸定理(SAT)による箱どうしの判定。**今日いちばん歯応えのあるファイル**。
///
/// <para>
/// 定理そのものは1行で言える——
/// <b>2つの凸形状が離れているなら、その2つを分けてしまう平面が必ず1枚ある</b>。
/// 裏を返すと、どの向きに投影しても重なっているなら当たっている。
/// </para>
///
/// <para>
/// 実用にするには「どの向きを試せばよいか」が要る。
/// 向きは無限にあるが、<b>候補は有限個で足りる</b>のが定理の値打ちで、
/// 凸多面体どうしなら
/// <list type="bullet">
/// <item>それぞれの<b>面の法線</b>(箱なら向かい合う面が同じ軸なので、6面で3本ずつ)</item>
/// <item>それぞれの<b>辺の向きの外積</b>(3 × 3 = 9 本)</item>
/// </list>
/// の合わせて<b>15 本</b>を調べれば済む。
/// </para>
///
/// <para>
/// <b>辺×辺の 9 本が要る理由</b>は絵で考えると早い。
/// 2つの箱を「角どうしが斜めに擦れ違う」ように置くと、
/// どちらの面の法線に投影しても重なって見えるのに、実際には離れている——
/// この場合を捕まえる軸が、2つの辺の両方に直交する向き(= 外積)になる。
/// 面の法線だけで済ませると、**斜めに置いた箱がすり抜ける**。
/// </para>
///
/// <para>
/// <b>判定だけなら 15 回の投影で終わる</b>。今日の仕事の大半は、
/// そこから<b>接触点をどう作るか</b>(<see cref="ContactManifold"/>)に費やされる——
/// Day 43 で「判定は 10 行、残り 300 行は解決」と書いたのと同じ構図が、
/// 今日は判定の内側でもう一度起きる。
/// </para>
/// </summary>
internal static class Sat
{
    /// <summary>
    /// 辺×辺の軸に履かせる下駄。**1 より大きいほど面が選ばれやすい**。
    ///
    /// ほぼ平行な2つの辺の外積は長さがほとんど 0 で、
    /// 正規化すると<b>向きが数値誤差でぐらぐら揺れる</b>。
    /// その軸がわずかな差で最小になると、面で触れているのに
    /// 「辺どうし」と判定されて接触点が1つに落ち、箱が震え出す。
    /// 5% の下駄を履かせておけば、面と辺が拮抗する場面では必ず面が勝つ。
    ///
    /// <para>
    /// Bullet も Box2D も同じ性格の下駄を持っている。値に理論的な根拠は無く、
    /// 「迷ったら面のほうが正しい」という経験則を数にしたもの。
    /// </para>
    /// </summary>
    private const float EdgeBias = 1.05f;

    /// <summary>
    /// 外積がこれより短ければ「2つの辺は平行」として軸から外す。
    ///
    /// 平行な辺から作った軸は向きが決まらないので使えない。
    /// **外しても取りこぼさない**のが大事なところで、
    /// 2つの辺が平行なら、その辺を含む面の法線がすでに候補に入っている。
    /// </summary>
    private const float ParallelEpsilon = 1e-8f;

    /// <summary>
    /// 箱と箱。**15 本の軸を試して、いちばん重なりが浅い1本を採る**。
    ///
    /// 返る法線は Day 43 からの約束どおり<b>A を B から引き離す向き</b>。
    ///
    /// <para>
    /// 「いちばん浅い」を選ぶのは、そこが<b>いちばん抜けやすい向き</b>だから。
    /// めり込んだ箱を最短距離で引き離す向きが、物理的にいちばんもっともらしい。
    /// 深い軸で押し戻すと、床にわずかに沈んだ箱が真横へ吹っ飛ぶ。
    /// </para>
    /// </summary>
    public static ContactManifold BoxBox(in Box3D a, in Box3D b)
    {
        Vector3 toCenter = b.Center - a.Center;

        // --- 軸 0〜5: 面の法線(A から3本、B から3本)---
        float bestFaceOverlap = float.MaxValue;
        int bestFaceIndex = -1;
        Vector3 bestFaceAxis = Vector3.Zero;

        for (int i = 0; i < 3; i++)
        {
            if (!TryAxis(a, b, a.Axis(i), toCenter, out float overlap))
            {
                // **1本でも隙間があれば、その時点で離れている**。
                // 残りの 14 本を調べる必要は無い——これが SAT が実用になる理由で、
                // 離れている組(大多数)は数本で抜ける。
                return default;
            }

            if (overlap < bestFaceOverlap)
            {
                bestFaceOverlap = overlap;
                bestFaceIndex = i;
                bestFaceAxis = a.Axis(i);
            }
        }

        for (int i = 0; i < 3; i++)
        {
            if (!TryAxis(a, b, b.Axis(i), toCenter, out float overlap))
            {
                return default;
            }

            if (overlap < bestFaceOverlap)
            {
                bestFaceOverlap = overlap;
                bestFaceIndex = 3 + i;
                bestFaceAxis = b.Axis(i);
            }
        }

        // --- 軸 6〜14: 辺の向きの外積(3 × 3)---
        float bestEdgeOverlap = float.MaxValue;
        Vector3 bestEdgeAxis = Vector3.Zero;
        int bestEdgeA = -1;
        int bestEdgeB = -1;

        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                Vector3 axis = Vector3.Cross(a.Axis(i), b.Axis(j));
                float lengthSquared = axis.LengthSquared();

                if (lengthSquared < ParallelEpsilon)
                {
                    continue;
                }

                axis /= MathF.Sqrt(lengthSquared);

                if (!TryAxis(a, b, axis, toCenter, out float overlap))
                {
                    return default;
                }

                if (overlap < bestEdgeOverlap)
                {
                    bestEdgeOverlap = overlap;
                    bestEdgeAxis = axis;
                    bestEdgeA = i;
                    bestEdgeB = j;
                }
            }
        }

        // 15 本すべてで重なっていた = 当たっている。
        // **どの軸を採るかで、この先の接触点の作り方が変わる**。
        bool useEdge = bestEdgeA >= 0 && (bestEdgeOverlap * EdgeBias) < bestFaceOverlap;

        Vector3 normal = useEdge ? bestEdgeAxis : bestFaceAxis;

        // **向きを揃える**。軸そのものは「どちら向きか」を持っていないので、
        // A を B から引き離す向き(= B から A へ向かう向き)に倒す。
        if (Vector3.Dot(normal, toCenter) > 0.0f)
        {
            normal = -normal;
        }

        if (useEdge)
        {
            return BuildEdgeContact(a, b, normal, bestEdgeA, bestEdgeB, bestEdgeOverlap);
        }

        // 基準面は「重なりがいちばん浅かった軸を持っていたほうの箱」。
        // 相手(入射側)の面を、基準面の枠でクリップする。
        //
        // 基準面の外向き法線は、基準の箱から相手へ向く向きになる。
        // A が基準なら normal の逆(normal は A を B から離す向きなので)、
        // B が基準なら normal そのもの。
        return bestFaceIndex < 3
            ? BuildFaceContact(a, b, normal, -normal, ManifoldSource.FaceA)
            : BuildFaceContact(b, a, normal, normal, ManifoldSource.FaceB);
    }

    /// <summary>
    /// 1本の軸を試す。**投影して重なりを測るだけ**。
    ///
    /// <code>
    ///   重なり = r_A(n) + r_B(n) - |(中心間ベクトル)·n|
    /// </code>
    ///
    /// <c>r(n)</c> は箱を軸 n に落としたときの長さの半分
    /// (<see cref="Box3D.ProjectedRadius"/>)。
    /// つまり<b>2つの線分が重なっているか</b>を見ているだけで、
    /// 中身は 1D の当たり判定そのもの。
    /// 戻り値が false なら<b>その軸で分離している</b>——当たっていない。
    ///
    /// <para>
    /// <paramref name="axis"/> は<b>長さ1でなければならない</b>。
    /// 正規化を忘れると重なりの大きさが軸ごとにばらつき、
    /// 「いちばん浅い軸」の選択が意味を失う。
    /// </para>
    /// </summary>
    private static bool TryAxis(
        in Box3D a, in Box3D b, Vector3 axis, Vector3 toCenter, out float overlap)
    {
        float distance = MathF.Abs(Vector3.Dot(toCenter, axis));
        overlap = a.ProjectedRadius(axis) + b.ProjectedRadius(axis) - distance;
        return overlap > 0.0f;
    }

    /// <summary>
    /// 面が基準のときの接触点を作る(要点5)。**多角形クリップ**。
    ///
    /// 手順は4段。
    /// <list type="number">
    /// <item><b>基準面</b>を決める … <paramref name="reference"/> の面のうち、
    ///   外向き法線が <paramref name="referenceOutward"/> に最も近いもの</item>
    /// <item><b>入射面</b>を決める … <paramref name="incident"/> の面のうち、
    ///   外向き法線が基準面の法線と最も逆を向いているもの
    ///   (= <b>いちばん正面から当たっている面</b>)</item>
    /// <item><b>クリップ</b> … 入射面の4頂点を、基準面の側面4枚で切り落とす
    ///   (Sutherland-Hodgman。2D の多角形クリップと同じ手続き)</item>
    /// <item><b>ふるい</b> … 残った点のうち、基準面より内側(めり込んでいる)ものだけを採る</item>
    /// </list>
    ///
    /// <para>
    /// <b>なぜ「入射面を基準面の枠で切る」のか</b>。
    /// 触れているのは2つの面の<b>重なっている部分</b>で、凸どうしなのでそれは凸多角形になる。
    /// 入射面(4角形)を基準面の枠(側面4枚)で切ると、まさにその重なりが出てくる。
    /// 床の上に箱を置けば4点、床から半分はみ出せば、
    /// はみ出した側の2点が切り落とされて<b>床の縁の上に新しい2点が生まれる</b>。
    /// </para>
    ///
    /// <para>
    /// <b>クリップで点の数は減るとは限らない</b>。辺と側面の交点が新しく増えるので、
    /// 4角形を4枚で切ると最大8つになる。そこから4つに絞るのが
    /// <see cref="ContactManifold.Add"/> の役目。
    /// </para>
    /// </summary>
    private static ContactManifold BuildFaceContact(
        in Box3D reference,
        in Box3D incident,
        Vector3 normal,
        Vector3 referenceOutward,
        ManifoldSource source)
    {
        // --- 1. 基準面 ---
        int referenceAxis = MostAlignedAxis(reference, referenceOutward, out float referenceSign);

        Vector3 referenceNormal = reference.Axis(referenceAxis) * referenceSign;
        Vector3 referenceCenter =
            reference.Center + (referenceNormal * reference.Extent(referenceAxis));

        // --- 2. 入射面 ---
        //
        // 基準面の法線と最も「逆を向いている」面を選ぶ。
        // 内積の絶対値がいちばん大きい軸を採り、符号を逆に倒せばよい。
        int incidentAxis = MostAlignedAxis(incident, referenceNormal, out float incidentSign);
        incidentSign = -incidentSign;

        Vector3 incidentNormal = incident.Axis(incidentAxis) * incidentSign;
        Vector3 incidentCenter =
            incident.Center + (incidentNormal * incident.Extent(incidentAxis));

        int u = (incidentAxis + 1) % 3;
        int v = (incidentAxis + 2) % 3;
        Vector3 edgeU = incident.Axis(u) * incident.Extent(u);
        Vector3 edgeV = incident.Axis(v) * incident.Extent(v);

        // --- 3. クリップ ---
        //
        // 入射面の4頂点から始めて、基準面の側面4枚で順に切る。
        // 交点が増えるぶん、入れ物は多めに取っておく(4角形を4枚で切って最大8つ)。
        Span<Vector3> bufferA = stackalloc Vector3[16];
        Span<Vector3> bufferB = stackalloc Vector3[16];

        bufferA[0] = incidentCenter - edgeU - edgeV;
        bufferA[1] = incidentCenter + edgeU - edgeV;
        bufferA[2] = incidentCenter + edgeU + edgeV;
        bufferA[3] = incidentCenter - edgeU + edgeV;

        Span<Vector3> polygon = bufferA;
        Span<Vector3> scratch = bufferB;
        int count = 4;

        // 基準面に平行な2軸それぞれについて、+側と -側を切る。合わせて4枚。
        for (int k = 1; k <= 2 && count > 0; k++)
        {
            int sideAxis = (referenceAxis + k) % 3;
            Vector3 axis = reference.Axis(sideAxis);
            float extent = reference.Extent(sideAxis);
            float center = Vector3.Dot(reference.Center, axis);

            count = Clip(polygon, count, scratch, axis, center + extent);
            Swap(ref polygon, ref scratch);

            if (count == 0)
            {
                break;
            }

            // -側は「法線を裏返して同じ式」。不等号を反転させる代わりに向きを反転させる。
            count = Clip(polygon, count, scratch, -axis, -(center - extent));
            Swap(ref polygon, ref scratch);
        }

        // --- 4. ふるい ---
        var manifold = new ContactManifold { Normal = normal, Source = source };
        float referenceOffset = Vector3.Dot(referenceCenter, referenceNormal);

        for (int i = 0; i < count; i++)
        {
            Vector3 point = polygon[i];
            float separation = Vector3.Dot(point, referenceNormal) - referenceOffset;

            if (separation > 0.0f)
            {
                // 基準面より外側。枠の中には入っているが、まだ触れていない点。
                continue;
            }

            // 接触点は**めり込んだ領域の真ん中**(Day 43 の <see cref="Contact3D"/> と同じ約束)。
            manifold.Add(point - (referenceNormal * (separation * 0.5f)), -separation);
        }

        if (manifold.Count == 0)
        {
            // SAT は当たっていると言ったのに、クリップで1点も残らなかった。
            // ごく浅い接触や数値誤差で起きる。**接触を落とすと物が沈む**ので、
            // 相手のいちばん突き出た点1つで代用しておく。
            Vector3 deepest = incident.Support(-referenceNormal);
            float separation = Vector3.Dot(deepest, referenceNormal) - referenceOffset;

            manifold.Add(
                deepest - (referenceNormal * (separation * 0.5f)),
                MathF.Max(-separation, 0.0f));
        }

        return manifold;
    }

    /// <summary>
    /// 辺と辺が最小だったときの接触点。**1点だけ**。
    ///
    /// 角と角が擦れ違うように当たっている場面なので、触れているのは1点しかない。
    /// マニフォールドが4点要るのは「面で触れている」ときの話で、
    /// ここで無理に点を増やすと、有りもしない支えを作ることになる。
    ///
    /// <para>
    /// 支えている辺は、<b>相手のほうへいちばん突き出た辺</b>。
    /// 頂点の支持点(<see cref="Box3D.Support"/>)と同じ要領で、
    /// 辺の向きの成分だけを 0 にすれば辺の中点が出る。
    /// あとは<b>2直線の最近接点</b>を求めれば、そこが接触点になる。
    /// </para>
    /// </summary>
    private static ContactManifold BuildEdgeContact(
        in Box3D a, in Box3D b, Vector3 normal, int axisA, int axisB, float depth)
    {
        // 法線は「A を B から引き離す向き」なので、
        // A 側の辺は -normal(B のほう)、B 側の辺は +normal(A のほう)へ突き出ている。
        Vector3 pointA = EdgeMidpoint(a, axisA, -normal);
        Vector3 pointB = EdgeMidpoint(b, axisB, normal);

        ClosestPointsOnLines(
            pointA, a.Axis(axisA), pointB, b.Axis(axisB),
            out Vector3 closestA, out Vector3 closestB);

        var manifold = new ContactManifold { Normal = normal, Source = ManifoldSource.EdgeEdge };
        manifold.Add((closestA + closestB) * 0.5f, depth);
        return manifold;
    }

    /// <summary>
    /// この向きにいちばん近い(または逆を向いた)箱の軸を返す。
    ///
    /// 箱は向かい合う面が同じ軸を共有しているので、
    /// <b>軸の添字</b>と<b>どちら向きか</b>の2つで面が1枚決まる。
    /// </summary>
    private static int MostAlignedAxis(in Box3D box, Vector3 direction, out float sign)
    {
        int best = 0;
        float bestDot = MathF.Abs(Vector3.Dot(box.AxisX, direction));

        float dotY = MathF.Abs(Vector3.Dot(box.AxisY, direction));
        if (dotY > bestDot)
        {
            best = 1;
            bestDot = dotY;
        }

        if (MathF.Abs(Vector3.Dot(box.AxisZ, direction)) > bestDot)
        {
            best = 2;
        }

        sign = Vector3.Dot(box.Axis(best), direction) >= 0.0f ? 1.0f : -1.0f;
        return best;
    }

    /// <summary>
    /// 箱の辺の中点。<paramref name="skip"/> の軸方向だけ 0 にして、
    /// 残り2軸を <paramref name="direction"/> の側へ倒す。
    /// </summary>
    private static Vector3 EdgeMidpoint(in Box3D box, int skip, Vector3 direction)
    {
        var local = new Vector3(
            skip == 0 ? 0.0f : SignedExtent(box, 0, direction),
            skip == 1 ? 0.0f : SignedExtent(box, 1, direction),
            skip == 2 ? 0.0f : SignedExtent(box, 2, direction));

        return box.ToWorld(local);
    }

    private static float SignedExtent(in Box3D box, int index, Vector3 direction) =>
        Vector3.Dot(box.Axis(index), direction) >= 0.0f ? box.Extent(index) : -box.Extent(index);

    /// <summary>
    /// 2直線の最近接点。**向きは長さ1である前提**。
    ///
    /// 直線 P + s·d1 と Q + t·d2 の距離が最小になる (s, t) を解く。
    /// 「最近接点を結ぶ線分は、どちらの直線にも直交する」という条件を
    /// 内積2本の連立方程式に書き下すと、
    /// <code>
    ///   s = (b·f - c) / (1 - b²)、 t = (f - b·c) / (1 - b²)
    ///   ただし b = d1·d2、c = d1·(P-Q)、f = d2·(P-Q)
    /// </code>
    /// になる(d1·d1 = d2·d2 = 1 を使って約分済み)。
    ///
    /// <para>
    /// <b>平行なときは分母が 0 になる</b>。そのときは最近接点が1つに決まらないので、
    /// 片方の点をそのまま使う。SAT 側で平行な辺の組は軸から外してあるので、
    /// ここに平行な組が来るのは数値誤差の場合だけ。
    /// </para>
    /// </summary>
    private static void ClosestPointsOnLines(
        Vector3 pointA, Vector3 directionA,
        Vector3 pointB, Vector3 directionB,
        out Vector3 closestA, out Vector3 closestB)
    {
        Vector3 offset = pointA - pointB;

        float b = Vector3.Dot(directionA, directionB);
        float c = Vector3.Dot(directionA, offset);
        float f = Vector3.Dot(directionB, offset);

        float denominator = 1.0f - (b * b);

        if (MathF.Abs(denominator) < 1e-6f)
        {
            closestA = pointA;
            closestB = pointB;
            return;
        }

        float s = ((b * f) - c) / denominator;
        float t = (f - (b * c)) / denominator;

        closestA = pointA + (directionA * s);
        closestB = pointB + (directionB * t);
    }

    /// <summary>
    /// 多角形を半空間で切る(Sutherland-Hodgman)。
    /// <c>n·p ≤ offset</c> を満たす側だけを残す。
    ///
    /// 頂点を順にたどり、
    /// <list type="bullet">
    /// <item>今の点が内側なら、その点を残す</item>
    /// <item>今の点と次の点で内外が入れ替わるなら、<b>境界との交点</b>を足す</item>
    /// </list>
    /// の2つを繰り返すだけ。凸多角形を凸な半空間で切るので、
    /// 結果も必ず凸多角形になり、順番も保たれる。
    ///
    /// <para>
    /// <b>この手続きは 2D でも 3D でも同じ</b>。
    /// 視錐台に三角形をクリップするラスタライザの処理(Day 8)と、
    /// やっていることは完全に同じ形をしている。
    /// </para>
    /// </summary>
    /// <summary>
    /// 2つの入れ物を入れ替える。**タプルでは書けない**。
    ///
    /// <c>Span&lt;T&gt;</c> は ref 構造体なので、
    /// <c>(a, b) = (b, a)</c> のようにタプルへ詰めることができない
    /// (タプルはヒープに載りうる型なので、スタックにしか置けない値は入れられない)。
    /// 昔ながらの一時変数で書くしかない箇所。
    /// </summary>
    private static void Swap(ref Span<Vector3> a, ref Span<Vector3> b)
    {
        Span<Vector3> temporary = a;
        a = b;
        b = temporary;
    }

    private static int Clip(
        ReadOnlySpan<Vector3> input, int count, Span<Vector3> output,
        Vector3 planeNormal, float planeOffset)
    {
        int result = 0;

        for (int i = 0; i < count; i++)
        {
            Vector3 current = input[i];
            Vector3 next = input[(i + 1) % count];

            float distanceCurrent = Vector3.Dot(planeNormal, current) - planeOffset;
            float distanceNext = Vector3.Dot(planeNormal, next) - planeOffset;

            if (distanceCurrent <= 0.0f && result < output.Length)
            {
                output[result++] = current;
            }

            // 符号が入れ替わったところに交点がある。
            if (distanceCurrent * distanceNext < 0.0f && result < output.Length)
            {
                float t = distanceCurrent / (distanceCurrent - distanceNext);
                output[result++] = current + ((next - current) * t);
            }
        }

        return result;
    }
}
