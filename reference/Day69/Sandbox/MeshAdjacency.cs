using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **隣接付きインデックスを組む**(Day 63a)。今日の C# 側の主役。
///
/// <para>
/// <c>GL_TRIANGLES_ADJACENCY</c> は、インデックスを<b>6つずつ</b>束ねて1枚の三角形として読む。
/// 偶数番(0・2・4)が三角形そのもの、奇数番(1・3・5)が<b>各辺の向こう側の頂点</b>。
/// この並びは GL が作ってくれるものではなく、<b>CPU 側が組んで渡すもの</b>。
/// ジオメトリシェーダで輪郭やシャドウボリュームを出す話は、
/// 実のところ半分以上がこの前処理の話になる。
/// </para>
///
/// <para>
/// <b>素直に組むと、たいてい隣が見つからない</b>。理由は頂点の重複で、
///   - 立方体は <b>8 隅を 24 頂点</b>で持つ(面ごとに法線が違うので分けざるを得ない)
///   - UV 球は<b>継ぎ目の1列が2重</b>(U が 0 と 1 で違う)、<b>極は slices 個</b>が同じ場所
/// という形をしている。インデックスだけを突き合わせると、隣り合う面が
/// 「別の頂点」を使っているので<b>辺が共有されていないことになる</b>。
/// だから先に<b>位置で溶接</b>して、その上で辺をたどる(<see cref="Weld"/>)。
/// </para>
///
/// <para>
/// 溶接を切ると何が起きるかは絵で見られる(「ジオメトリシェーダ」の F7)。
/// 立方体は<b>辺が1本残らず輪郭になり</b>、球は継ぎ目と極に縦線が出る。
/// </para>
/// </summary>
internal static class MeshAdjacency
{
    /// <summary>
    /// 溶接の升目(m)。**位置をこの幅で丸めてから突き合わせる**。
    ///
    /// <para>
    /// ぴったり一致を見ないのは、位置が計算で作られるため。UV 球の継ぎ目は
    /// φ = 0 と φ = 2π で作るので、<c>sin(0) = 0</c> に対して <c>sin(2π)</c> は
    /// -2.4e-7 になり、**同じ点のつもりでも同じ float にならない**。
    /// </para>
    ///
    /// <para>
    /// <b>丸めは万能ではない</b>。升目の境目をまたぐ2点(0.09mm しか離れていないのに別の升)は
    /// 溶接されない。本気でやるなら格子の 27 近傍を見るか、空間分割で近傍を探す。
    /// ここは「自分で作ったメッシュを閉じる」用途なので、いちばん短い形にしてある。
    /// </para>
    /// </summary>
    public const float WeldGrid = 1.0e-4f;

    /// <summary>組んだ結果の内訳。**絵に出ないことを数字で押さえる**ためのもの。</summary>
    /// <param name="VertexCount">元の頂点の数。</param>
    /// <param name="WeldedVertexCount">位置で溶接したあとの頂点の数。</param>
    /// <param name="TriangleCount">元の三角形の数。</param>
    /// <param name="DegenerateTriangles">溶接したら潰れた三角形の数(UV 球の極に出る)。</param>
    /// <param name="EdgeCount">潰れていない三角形が使っている辺の数(重複を除く)。</param>
    /// <param name="BoundaryEdges">**隣が見つからなかった辺**の数。閉じた形なら 0 になるはず。</param>
    /// <param name="NonManifoldEdges">3枚以上の面が共有している辺の数。1つでもあれば形が怪しい。</param>
    public readonly record struct Report(
        int VertexCount,
        int WeldedVertexCount,
        int TriangleCount,
        int DegenerateTriangles,
        int EdgeCount,
        int BoundaryEdges,
        int NonManifoldEdges)
    {
        /// <summary>実際に描かれる三角形の数(潰れたものを除く)。</summary>
        public int LiveTriangles => TriangleCount - DegenerateTriangles;

        /// <summary>隣接付きインデックスの数。三角形1枚につき6つ。</summary>
        public int AdjacencyIndexCount => LiveTriangles * 6;

        /// <summary>
        /// **オイラーの多面体定理の左辺** V - E + F。
        /// 穴の無い閉じた面なら 2 になる(球も立方体も、細かさによらず 2)。
        /// 溶接に失敗していると頂点が余るので、ここが 2 から離れる——
        /// <b>絵を見ずに「ちゃんと閉じたか」を1つの数で確かめられる</b>。
        /// </summary>
        public int EulerCharacteristic => WeldedVertexCount - EdgeCount + LiveTriangles;
    }

    /// <summary>
    /// **位置で頂点をまとめる**。返すのは「その頂点の代表は元の何番か」の表。
    ///
    /// <para>
    /// 代表を<b>元の番号のまま</b>返すのがここの肝。番号を振り直すと
    /// 頂点バッファも作り直すことになるが、<b>隣接インデックスが指したいのは
    /// 「位置が同じ頂点のうちどれか1つ」</b>でしかない
    /// (ジオメトリシェーダが奇数番の頂点から読むのは位置だけ)。
    /// 頂点バッファをそのまま使い回せるので、溶接の ON / OFF が
    /// <b>インデックスの差し替えだけ</b>で済む。
    /// </para>
    /// </summary>
    /// <param name="weldedCount">まとめたあとに残る位置の数。</param>
    /// <returns><c>remap[i]</c> = i 番の頂点の代表(元の番号)。</returns>
    public static int[] Weld(ReadOnlySpan<Vertex> vertices, out int weldedCount)
    {
        var representatives = new Dictionary<(int X, int Y, int Z), int>(vertices.Length);
        var remap = new int[vertices.Length];

        for (int i = 0; i < vertices.Length; i++)
        {
            (int X, int Y, int Z) key = Quantize(vertices[i].Position);

            if (!representatives.TryGetValue(key, out int representative))
            {
                // **最初に出てきたものを代表にする**。誰でもよいので、順番で決める。
                representative = i;
                representatives[key] = representative;
            }

            remap[i] = representative;
        }

        weldedCount = representatives.Count;
        return remap;
    }

    /// <summary>
    /// **隣接付きインデックスを組む**。返す配列は三角形1枚につき6つ。
    ///
    /// <para>
    /// 手順は3段。
    /// <list type="number">
    /// <item>溶接する(<paramref name="weld"/> が false なら何もしない = 元の番号のまま)</item>
    /// <item><b>向きのある辺</b>から「その辺の向かい側の頂点」を引ける表を作る。
    ///       三角形 (a, b, c) は 辺 a→b・b→c・c→a を持ち、それぞれの向かいが c・a・b</item>
    /// <item>辺 a→b の隣は、<b>逆向きの辺 b→a を持つ三角形</b>。表を引けば1回で出る</item>
    /// </list>
    /// 向きを付けて持つのが要点で、これだと「隣り合う2枚は辺を逆向きに共有する」
    /// (= 巻き順が揃っている)という約束がそのまま検査になる。
    /// 向きを無視して持つと、裏返った面が混じっていても気付けない。
    /// </para>
    ///
    /// <para>
    /// <b>隣が無い辺</b>(端の辺、あるいは溶接していないせいで見つからない辺)は、
    /// <b>その辺の始点をもう一度書く</b>。すると隣の三角形が潰れて面積 0 になり、
    /// シェーダ側の外積が 0 ベクトルになる——
    /// つまり<b>必ず「裏を向いている」と判定される</b>ので、
    /// 表の面から見た端の辺は必ず輪郭として出る(<c>gs-silhouette.geom</c>)。
    /// 「隣が無い」を 0xFFFFFFFF のような番兵で表さないのは、
    /// <b>シェーダ側に分岐を足したくない</b>から。
    /// </para>
    /// </summary>
    public static uint[] Build(
        ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, bool weld, out Report report)
    {
        int triangleCount = indices.Length / 3;

        int[] remap;
        int weldedCount;

        if (weld)
        {
            remap = Weld(vertices, out weldedCount);
        }
        else
        {
            remap = new int[vertices.Length];
            for (int i = 0; i < remap.Length; i++)
            {
                remap[i] = i;
            }

            weldedCount = vertices.Length;
        }

        // --- 1周目: 向きのある辺の表を作る ---
        var opposite = new Dictionary<(int From, int To), int>(triangleCount * 3);

        // 辺が何枚の面に使われているか(向きを無視して数える)。多様体かどうかの検査用。
        var edgeUse = new Dictionary<(int Low, int High), int>(triangleCount * 3);

        int degenerate = 0;

        for (int t = 0; t < triangleCount; t++)
        {
            int a = remap[(int)indices[(t * 3) + 0]];
            int b = remap[(int)indices[(t * 3) + 1]];
            int c = remap[(int)indices[(t * 3) + 2]];

            // **溶接すると潰れる三角形がある**。UV 球の極がそれで、
            // 極に接する四角形の2枚のうち1枚は、3頂点のうち2つが極そのものになる。
            // 面積が 0 なので絵には出ないが、辺として数えると帳尻が合わなくなる(オイラーの式)。
            if (a == b || b == c || c == a)
            {
                degenerate++;
                continue;
            }

            AddEdge(opposite, edgeUse, a, b, c);
            AddEdge(opposite, edgeUse, b, c, a);
            AddEdge(opposite, edgeUse, c, a, b);
        }

        // --- 2周目: 6つ組を書き出す ---
        var adjacency = new uint[(triangleCount - degenerate) * 6];
        int boundary = 0;
        int write = 0;

        for (int t = 0; t < triangleCount; t++)
        {
            uint i0 = indices[(t * 3) + 0];
            uint i1 = indices[(t * 3) + 1];
            uint i2 = indices[(t * 3) + 2];

            int a = remap[(int)i0];
            int b = remap[(int)i1];
            int c = remap[(int)i2];

            if (a == b || b == c || c == a)
            {
                continue;
            }

            // **三角形そのものは元の番号のまま置く**(偶数番)。
            // ここを代表に置き換えると、法線も UV も継ぎ目の反対側のものになってしまう。
            adjacency[write + 0] = i0;
            adjacency[write + 2] = i1;
            adjacency[write + 4] = i2;

            // 奇数番は隣の三角形の向かい側の頂点。**位置しか読まれない**ので代表でよい。
            adjacency[write + 1] = Neighbor(opposite, b, a, i0, ref boundary);
            adjacency[write + 3] = Neighbor(opposite, c, b, i1, ref boundary);
            adjacency[write + 5] = Neighbor(opposite, a, c, i2, ref boundary);

            write += 6;
        }

        int nonManifold = 0;
        foreach (int uses in edgeUse.Values)
        {
            if (uses > 2)
            {
                nonManifold++;
            }
        }

        report = new Report(
            vertices.Length,
            weldedCount,
            triangleCount,
            degenerate,
            edgeUse.Count,
            boundary,
            nonManifold);

        return adjacency;
    }

    /// <summary>
    /// **C# の鏡**。<c>gs-silhouette.geom</c> とまったく同じ判定で輪郭の辺を数える。
    ///
    /// <para>
    /// GPU の中で何本の線が出たかは、絵を見ても数えられない。
    /// <see cref="PrimitiveCounter"/> が GL に数えさせるが、それは「何本出たか」であって
    /// 「正しい辺を選んだか」ではない。同じ式を C# で走らせて本数を突き合わせれば、
    /// <b>両方が同じ辺を選んでいる</b>ことまで言える。Day 58 の <c>SdfScene</c> と同じ作法。
    /// </para>
    /// </summary>
    /// <param name="frontFacingTriangles">そのうち表を向いていた三角形の数。</param>
    public static int CountSilhouetteEdges(
        ReadOnlySpan<Vertex> vertices,
        ReadOnlySpan<uint> adjacency,
        in Matrix4x4 model,
        Vector3 eye,
        out int frontFacingTriangles)
    {
        int edges = 0;
        frontFacingTriangles = 0;

        Matrix4x4 matrix = model;

        for (int i = 0; i + 5 < adjacency.Length; i += 6)
        {
            Vector3 v0 = Vector3.Transform(vertices[(int)adjacency[i + 0]].Position, matrix);
            Vector3 v1 = Vector3.Transform(vertices[(int)adjacency[i + 1]].Position, matrix);
            Vector3 v2 = Vector3.Transform(vertices[(int)adjacency[i + 2]].Position, matrix);
            Vector3 v3 = Vector3.Transform(vertices[(int)adjacency[i + 3]].Position, matrix);
            Vector3 v4 = Vector3.Transform(vertices[(int)adjacency[i + 4]].Position, matrix);
            Vector3 v5 = Vector3.Transform(vertices[(int)adjacency[i + 5]].Position, matrix);

            if (!IsFrontFacing(v0, v2, v4, eye))
            {
                continue;
            }

            frontFacingTriangles++;

            if (!IsFrontFacing(v0, v1, v2, eye))
            {
                edges++;
            }

            if (!IsFrontFacing(v2, v3, v4, eye))
            {
                edges++;
            }

            if (!IsFrontFacing(v4, v5, v0, eye))
            {
                edges++;
            }
        }

        return edges;
    }

    /// <summary>シェーダの <c>isFrontFacing</c> と同じ式。</summary>
    public static bool IsFrontFacing(Vector3 a, Vector3 b, Vector3 c, Vector3 eye) =>
        Vector3.Dot(Vector3.Cross(b - a, c - a), eye - a) > 0.0f;

    private static void AddEdge(
        Dictionary<(int From, int To), int> opposite,
        Dictionary<(int Low, int High), int> edgeUse,
        int from,
        int to,
        int third)
    {
        // 同じ向きの辺が2回出てきたら、面が裏返っているか、同じ三角形が2枚重なっている。
        // ここでは後から来たほうを無視する(**先に見つかったほうを隣にする**)。
        opposite.TryAdd((from, to), third);

        (int Low, int High) key = from < to ? (from, to) : (to, from);
        edgeUse[key] = edgeUse.TryGetValue(key, out int uses) ? uses + 1 : 1;
    }

    /// <summary>辺 <paramref name="from"/> → <paramref name="to"/> を持つ三角形の、向かい側の頂点。</summary>
    /// <param name="fallback">隣が無いときに書く番号(その辺の始点)。</param>
    private static uint Neighbor(
        Dictionary<(int From, int To), int> opposite,
        int from,
        int to,
        uint fallback,
        ref int boundary)
    {
        if (opposite.TryGetValue((from, to), out int third))
        {
            // 代表は「元の番号のうちどれか」なので、そのまま頂点バッファを指せる。
            return (uint)third;
        }

        boundary++;
        return fallback;
    }

    /// <summary>位置を升目で丸めて、突き合わせに使う鍵にする。</summary>
    private static (int X, int Y, int Z) Quantize(Vector3 position) => (
        (int)MathF.Round(position.X / WeldGrid),
        (int)MathF.Round(position.Y / WeldGrid),
        (int)MathF.Round(position.Z / WeldGrid));
}
