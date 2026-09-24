using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MeshletRenderer;

/// <summary>メッシュレットの切り方(要点2)。</summary>
internal enum MeshletStrategy
{
    /// <summary>
    /// <b>育てる</b>。1枚の三角形を種にして、<b>新しい頂点をいちばん増やさない</b>隣の三角形を足していく。
    /// ひとつながりの小片になる。
    /// </summary>
    Grow,

    /// <summary>
    /// <b>並び順のまま切る</b>。索引の先頭から三角形を詰めて、入らなくなったら次へ。
    /// 比べるための「何も考えない」切り方。
    /// </summary>
    Scan,

    /// <summary>
    /// <b>丸く育てる</b>(Day 64b で追加)。<see cref="Grow"/> と同じだが、新しい頂点の数が同点の候補のうち
    /// <b>メッシュレットの中心にいちばん近いもの</b>を選ぶ。規則は1つ増えるだけで、形が細長い帯から丸い小片に変わる。
    /// 法線の円錐が狭くなり、背面カリングが効くようになる(要点4)。
    /// </summary>
    Round,
}

/// <summary>
/// メッシュレット1つ。<b>48 バイト</b>(Day 64b で 16 バイトから増えた)。
/// GLSL の <c>Meshlet</c> 構造体と1バイトも違ってはいけない。
///
/// <para>
/// 前半は「どこから何個」が2組。頂点そのものも三角形そのものも持たず、
/// 大きな配列(<see cref="MeshletSet.VertexIndices"/> と <see cref="MeshletSet.Triangles"/>)の
/// <b>区間</b>を指す。メッシュシェーダの1ワークグループが、この1つを受け持つ。
/// </para>
/// <para>
/// 後半の2つ(Day 64b で追加)は<b>カリングのための要約</b>。タスクシェーダは三角形を1枚も読まずに、
/// この 32 バイトだけで「このメッシュレットは描かなくてよい」を決める(要点2・3)。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuMeshlet
{
    /// <summary><see cref="MeshletSet.VertexIndices"/> の中での先頭。</summary>
    public uint VertexOffset;

    /// <summary>
    /// <see cref="MeshletSet.Triangles"/> の中での先頭。<b>描く順での三角形の通し番号</b>でもある
    /// (画素シェーダが <c>gl_PrimitiveID</c> として受け取る)。
    /// </summary>
    public uint TriangleOffset;

    /// <summary>使う頂点の数(<see cref="MeshletBuilder.MaxVertices"/> 以下)。</summary>
    public uint VertexCount;

    /// <summary>三角形の数(<see cref="MeshletBuilder.MaxTriangles"/> 以下)。</summary>
    public uint TriangleCount;

    /// <summary>
    /// <b>境界の球</b>(Day 64b で追加)。xyz = 中心、w = 半径。模型の座標で持つ。
    /// 三角形がすべてこの球の中にあるので、球が視錐台の外なら丸ごと捨ててよい。
    /// </summary>
    public Vector4 BoundingSphere;

    /// <summary>
    /// <b>法線の円錐</b>(Day 64b で追加)。xyz = 軸(三角形の法線の平均の向き)、
    /// w = 円錐の半角 α の sin。三角形の法線がすべて「軸から α 以内」に収まる。
    /// 円錐が開きすぎて(α ≥ 90°)背面の判定に使えないときは w = 1(タスクシェーダの式で決して捨てられない値)。
    /// </summary>
    public Vector4 NormalCone;
}

/// <summary>
/// メッシュを小分けにした結果。GPU に送る配列4本と、頂点シェーダの道のための索引1本。
/// </summary>
internal sealed class MeshletSet
{
    public required GpuMeshlet[] Meshlets { get; init; }

    /// <summary>
    /// メッシュレットの<b>局所番号 → 元の頂点番号</b>。メッシュレットごとに区間を持つ。
    /// <b>境目の頂点は、隣り合うメッシュレットの両方に入る</b>(だから元の頂点数より長い)。
    /// </summary>
    public required uint[] VertexIndices { get; init; }

    /// <summary>
    /// 三角形1枚 = 局所番号3つを <b>8 ビットずつ詰めた uint</b>。
    /// 局所番号は 0〜63 なので 8 ビットで足りる。元の索引(32 ビット x 3)の 1/3 の大きさ。
    /// </summary>
    public required uint[] Triangles { get; init; }

    /// <summary>三角形(描く順)→ それが属するメッシュレットの番号。色分けにだけ使う。</summary>
    public required uint[] TriangleMeshlet { get; init; }

    /// <summary>
    /// <b>頂点シェーダの道のための索引</b>。元の索引を、メッシュレットの順に並べ直したもの。
    /// 2つの道で「k 番目の三角形」が同じ三角形を指すようにして、同じ絵を出させる。
    /// </summary>
    public required uint[] OrderedIndices { get; init; }

    public required int SourceVertexCount { get; init; }

    public required MeshletStrategy Strategy { get; init; }

    /// <summary>切るのにかかった時間(CPU、ms)。</summary>
    public required double BuildMilliseconds { get; init; }

    public int TriangleCount => Triangles.Length;

    /// <summary>1メッシュレットあたりの三角形の数の平均。<b>上限(124)にどれだけ近いか</b>が詰め方の良さ。</summary>
    public double TrianglesPerMeshlet => (double)Triangles.Length / Meshlets.Length;

    /// <summary>1メッシュレットあたりの頂点の数の平均。</summary>
    public double VerticesPerMeshlet => (double)VertexIndices.Length / Meshlets.Length;

    /// <summary>
    /// <b>頂点の重複率</b>。メッシュシェーダが変換する頂点の総数 ÷ 元の頂点数。
    /// 境目の頂点は隣のメッシュレットでも変換し直すので、1 より大きくなる。
    /// 小さいほど無駄が少ない(要点3)。
    /// </summary>
    public double VertexDuplication => (double)VertexIndices.Length / SourceVertexCount;

    /// <summary>
    /// <b>背面の判定に使えるメッシュレットの割合</b>(Day 64b で追加)。
    /// 法線の円錐が半球より狭い(α &lt; 90°)ものの割合で、これが 0 なら、どこから見ても裏向きとは言えない。
    /// 並び順のまま切った輪切りのメッシュレットは、管を1周しているので 0 になる(要点4)。
    /// </summary>
    public double ConeUsableFraction => Meshlets.Count(m => m.NormalCone.W < 1.0f) / (double)Meshlets.Length;

    /// <summary>
    /// 判定に使える円錐の、半角の平均(度)。狭いほど、裏向きと言える見る向きが広い。
    /// <b>三角形の数で重みを付ける</b>——三角形1〜2枚だけの小さな欠片は円錐がほぼ 0 度になるので、
    /// 単純に平均すると実態より狭く見えてしまう。
    /// </summary>
    public double AverageConeAngleDegrees
    {
        get
        {
            GpuMeshlet[] usable = Meshlets.Where(m => m.NormalCone.W < 1.0f).ToArray();
            double triangles = usable.Sum(m => (double)m.TriangleCount);
            return triangles == 0.0
                ? 0.0
                : usable.Sum(m => MathF.Asin(m.NormalCone.W) * 180.0 / Math.PI * m.TriangleCount) / triangles;
        }
    }
}

/// <summary>
/// メッシュをメッシュレットに切る(今日の要点2)。
///
/// <para>
/// メッシュシェーダは<b>索引を1本ずつ読む入力の回路を持たない</b>。代わりに、
/// ワークグループ1つが「頂点 64 個・三角形 124 枚まで」の小さなメッシュを丸ごと受け持つ。
/// その小さなメッシュを<b>起動前に CPU で作っておく</b>のがこのクラスの仕事。
/// 実際のエンジンでもアセットを取り込むときに1回だけやる(meshoptimizer の <c>meshopt_buildMeshlets</c> がこれ)。
/// </para>
/// <para>
/// 切り方の良し悪しは2つの数で測る。
/// </para>
/// <list type="bullet">
/// <item><b>三角形 / メッシュレット</b> … 上限まで詰まっているほど、ワークグループの数が減る</item>
/// <item><b>頂点の重複率</b> … 境目が短いほど(丸く固まっているほど)、変換し直す頂点が減る</item>
/// </list>
/// </summary>
internal static class MeshletBuilder
{
    /// <summary>
    /// 1メッシュレットの頂点の上限。<b>シェーダの <c>max_vertices</c> と一致させる</b>。
    /// 64 は NVIDIA の推奨。ハードウェアの上限(手元の RTX 3070 は 256)よりずっと小さいのは、
    /// 出力を置く場所が小さいほど1つの SM に多くのワークグループを同時に載せられるから。
    /// </summary>
    public const int MaxVertices = 64;

    /// <summary>
    /// 1メッシュレットの三角形の上限。<b>シェーダの <c>max_primitives</c> と一致させる</b>。
    /// NVIDIA の推奨は 126 で、半端なのは初代(Turing)の回路が三角形の索引を 128 バイト単位で確保し、
    /// そこに数の 4 バイトも入るから(3 x 126 + 4 ≤ 384)。meshoptimizer は三角形の数を
    /// 4 の倍数に揃える作りなので、それを切り下げた 124 がよく使われる。
    /// </summary>
    public const int MaxTriangles = 124;

    public static MeshletSet Build(MeshData mesh, MeshletStrategy strategy)
    {
        var clock = Stopwatch.StartNew();
        var writer = new MeshletWriter(mesh);

        if (strategy == MeshletStrategy.Scan)
        {
            BuildScan(mesh, writer);
        }
        else
        {
            BuildGrow(mesh, writer, round: strategy == MeshletStrategy.Round);
        }

        return writer.Finish(strategy, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// 並び順のまま切る。<b>10 行で済む</b>が、形は索引の並び次第になる。
    ///
    /// <para>
    /// トーラスノットは三角形が「断面を1周する帯」ごとに並んでいるので、
    /// 1つのメッシュレットは<b>管を輪切りにした帯</b>になる(頂点 64・三角形 64)。
    /// 帯の両側の円は隣のメッシュレットでも使うので、<b>頂点の重複率がちょうど 2</b> になる。
    /// そして帯は管を1周しているので、<b>法線があらゆる向きを向いている</b>——
    /// Day 64b で「このメッシュレットは丸ごと裏を向いている」と言えなくなる。
    /// </para>
    /// </summary>
    private static void BuildScan(MeshData mesh, MeshletWriter writer)
    {
        for (int triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            if (!writer.CanAdd(triangle))
            {
                writer.Flush();
            }

            writer.Add(triangle);
        }

        writer.Flush();
    }

    /// <summary>
    /// 育てる。<b>種の三角形から、隣へ隣へと広げていく</b>。
    ///
    /// <para>
    /// 次に足す三角形は「いまのメッシュレットと頂点を共有している三角形」(候補)の中から、
    /// <b>新しく増える頂点がいちばん少ないもの</b>を選ぶ。
    /// </para>
    /// <list type="bullet">
    /// <item>増える頂点が 0 … 3頂点とも既に入っている。<b>隙間を埋める</b>三角形で、これを最優先する</item>
    /// <item>増える頂点が 1 … 縁に1枚貼り足す。ふつうはこれで広がっていく</item>
    /// <item>増える頂点が 2 … 頂点1つでしか触れていない。広がり方が歪むので最後に回す</item>
    /// </list>
    /// <para>
    /// 同じ点数なら<b>候補に入った順</b>を選ぶ。
    /// meshoptimizer はここに「中心からの距離」や「法線の揃い具合」も混ぜて点数を付けているが、
    /// 今日は頂点の数だけで見る——それでも重複率は並び順のままの 2.0 から 1.33 まで下がる。
    /// </para>
    /// <para>
    /// <b>Day 64b で分かったこと</b>: 候補に入った順で選ぶと、管の周りを<b>回り込むように</b>育ちやすい
    /// (1つのメッシュレットが管の周り 32 升のうち平均 12.7 升ぶんを覆う)。法線があちこちを向くので、
    /// 法線の円錐が平均 64 度まで開き、背面カリングにほとんど使えなかった。
    /// <paramref name="round"/> を立てると、同点のときに<b>中心にいちばん近い候補</b>を選び、周り 5.9 升・円錐 24 度まで締まる。
    /// </para>
    /// </summary>
    /// <param name="round">同点を「中心に近い順」で決めるか(<see cref="MeshletStrategy.Round"/>)。</param>
    private static void BuildGrow(MeshData mesh, MeshletWriter writer, bool round)
    {
        int triangleCount = mesh.TriangleCount;
        uint[] indices = mesh.Indices;

        // ---- 頂点 → その頂点を使う三角形 の表 ------------------------------------------
        // 「隣の三角形」を引くのに要る。頂点ごとの可変長の一覧を、2本の配列に詰めて持つ
        // (first[v] から first[v + 1] の手前までが頂点 v の一覧。CSR と呼ばれる詰め方)。
        var first = new int[mesh.Vertices.Length + 1];
        foreach (uint index in indices)
        {
            first[index + 1]++;
        }

        for (int v = 0; v < mesh.Vertices.Length; v++)
        {
            first[v + 1] += first[v];
        }

        var cursor = (int[])first.Clone();
        var trianglesOfVertex = new int[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            trianglesOfVertex[cursor[indices[i]]++] = i / 3;
        }

        // ---- 三角形の重心(Day 64b で追加)------------------------------------------------
        // 「メッシュレットの中心」は、いま入っている三角形の重心の平均で持つ(足し算だけで更新できる)。
        var centroids = new Vector3[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            centroids[t] = (mesh.Vertices[indices[t * 3]].Position
                + mesh.Vertices[indices[t * 3 + 1]].Position
                + mesh.Vertices[indices[t * 3 + 2]].Position) / 3.0f;
        }

        Vector3 centroidSum = Vector3.Zero;
        int takenCount = 0;

        // ---- 育てる ----------------------------------------------------------------------
        var used = new bool[triangleCount];
        var queued = new bool[triangleCount];
        var candidates = new List<int>();
        int seed = 0;

        void Take(int triangle)
        {
            used[triangle] = true;
            writer.Add(triangle);
            centroidSum += centroids[triangle];
            takenCount++;

            // この三角形の3頂点を共有する三角形を、まだなら候補に入れる。
            for (int corner = 0; corner < 3; corner++)
            {
                uint v = indices[triangle * 3 + corner];
                for (int k = first[v]; k < first[v + 1]; k++)
                {
                    int neighbor = trianglesOfVertex[k];
                    if (!used[neighbor] && !queued[neighbor])
                    {
                        queued[neighbor] = true;
                        candidates.Add(neighbor);
                    }
                }
            }
        }

        while (true)
        {
            // 種は「まだ使っていない三角形のうち、索引でいちばん前のもの」。
            while (seed < triangleCount && used[seed])
            {
                seed++;
            }

            if (seed == triangleCount)
            {
                break;
            }

            Take(seed);

            while (true)
            {
                int best = -1;
                int bestNew = int.MaxValue;
                float bestDistance = float.MaxValue;
                Vector3 center = centroidSum / takenCount;
                int write = 0;

                // 候補を1周見る。ついでに、もう使った三角形を一覧から詰めて消す。
                for (int read = 0; read < candidates.Count; read++)
                {
                    int triangle = candidates[read];
                    if (used[triangle])
                    {
                        continue;
                    }

                    candidates[write++] = triangle;
                    if (!round && best >= 0 && bestNew == 0)
                    {
                        // 0 より良い点数は無いので、残りは詰めるだけ。
                        // (丸く育てるときは、0 点どうしの中でも近いものを探すので、最後まで見る)
                        continue;
                    }

                    int added = writer.NewVertexCount(triangle);
                    if (added > bestNew || !writer.CanAdd(triangle))
                    {
                        continue;
                    }

                    // 同点のとき: Grow は先に見つけたほう(候補に入った順)、Round は中心に近いほう。
                    float distance = round ? Vector3.DistanceSquared(centroids[triangle], center) : 0.0f;
                    if (added < bestNew || distance < bestDistance)
                    {
                        best = triangle;
                        bestNew = added;
                        bestDistance = distance;
                    }
                }

                candidates.RemoveRange(write, candidates.Count - write);

                // どれも入らない(頂点か三角形が上限に達した)か、候補が尽きた(島の端まで来た)。
                if (best < 0)
                {
                    break;
                }

                Take(best);
            }

            writer.Flush();
            centroidSum = Vector3.Zero;
            takenCount = 0;

            // 使われなかった候補は、次のメッシュレットでまた候補になれるように印を消す。
            foreach (int triangle in candidates)
            {
                queued[triangle] = false;
            }

            candidates.Clear();
        }
    }

    /// <summary>
    /// 作りかけのメッシュレットを1つ持ち、閉じるたびに大きな配列へ書き出す。
    /// 2つの切り方は<b>どの三角形をどの順に足すか</b>だけが違うので、足す・閉じるはここに1つだけ書く。
    /// </summary>
    private sealed class MeshletWriter
    {
        private readonly MeshData _mesh;

        /// <summary>元の頂点番号 → いまのメッシュレットでの局所番号。入っていなければ -1。</summary>
        private readonly int[] _localIndex;

        private readonly List<uint> _vertices = [];
        private readonly List<uint> _triangles = [];

        private readonly List<GpuMeshlet> _meshlets = [];
        private readonly List<uint> _allVertices = [];
        private readonly List<uint> _allTriangles = [];
        private readonly List<uint> _triangleMeshlet = [];
        private readonly List<uint> _orderedIndices = [];

        public MeshletWriter(MeshData mesh)
        {
            _mesh = mesh;
            _localIndex = new int[mesh.Vertices.Length];
            Array.Fill(_localIndex, -1);
        }

        /// <summary>この三角形を足すと、頂点がいくつ増えるか(0〜3)。</summary>
        public int NewVertexCount(int triangle)
        {
            int count = 0;
            for (int corner = 0; corner < 3; corner++)
            {
                if (_localIndex[_mesh.Indices[triangle * 3 + corner]] < 0)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>足しても上限を超えないか。<b>頂点と三角形の両方</b>を見る。</summary>
        public bool CanAdd(int triangle)
            => _triangles.Count < MaxTriangles
            && _vertices.Count + NewVertexCount(triangle) <= MaxVertices;

        public void Add(int triangle)
        {
            uint packed = 0;
            for (int corner = 0; corner < 3; corner++)
            {
                uint v = _mesh.Indices[triangle * 3 + corner];
                if (_localIndex[v] < 0)
                {
                    _localIndex[v] = _vertices.Count;
                    _vertices.Add(v);
                }

                // 局所番号を 8 ビットずつ詰める(i0 | i1 << 8 | i2 << 16)。
                // 回り方(頂点の順)は元の三角形のまま。変えると表と裏が入れ替わる。
                packed |= (uint)_localIndex[v] << (corner * 8);
                _orderedIndices.Add(v);
            }

            _triangles.Add(packed);
        }

        /// <summary>作りかけを閉じて書き出す。空なら何もしない。</summary>
        public void Flush()
        {
            if (_triangles.Count == 0)
            {
                return;
            }

            uint meshletIndex = (uint)_meshlets.Count;
            _meshlets.Add(new GpuMeshlet
            {
                VertexOffset = (uint)_allVertices.Count,
                TriangleOffset = (uint)_allTriangles.Count,
                VertexCount = (uint)_vertices.Count,
                TriangleCount = (uint)_triangles.Count,
                BoundingSphere = ComputeSphere(),
                NormalCone = ComputeCone(),
            });

            _allVertices.AddRange(_vertices);
            _allTriangles.AddRange(_triangles);
            for (int i = 0; i < _triangles.Count; i++)
            {
                _triangleMeshlet.Add(meshletIndex);
            }

            // 局所番号の表を元に戻す。配列ごと作り直すと頂点数ぶんの手間になるので、使った所だけ消す。
            foreach (uint v in _vertices)
            {
                _localIndex[v] = -1;
            }

            _vertices.Clear();
            _triangles.Clear();
        }

        /// <summary>
        /// 境界の球(Day 64b で追加)。<b>頂点を囲む箱の中心</b>を球の中心にして、いちばん遠い頂点までを半径にする。
        /// 最小の球ではない(最小にするには Welzl のアルゴリズムが要る)が、少し大きいのは安全側なので構わない。
        /// </summary>
        private Vector4 ComputeSphere()
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (uint v in _vertices)
            {
                min = Vector3.Min(min, _mesh.Vertices[v].Position);
                max = Vector3.Max(max, _mesh.Vertices[v].Position);
            }

            Vector3 center = (min + max) * 0.5f;
            float radius = 0.0f;
            foreach (uint v in _vertices)
            {
                radius = MathF.Max(radius, Vector3.Distance(center, _mesh.Vertices[v].Position));
            }

            return new Vector4(center, radius);
        }

        /// <summary>
        /// 法線の円錐(Day 64b で追加。要点3)。
        ///
        /// <para>
        /// 使う法線は<b>頂点の法線ではなく三角形の面の法線</b>。ラスタライザが裏を捨てるときに見るのは
        /// 画面上の回り方で、それは面の法線と一致する(頂点の法線は光の計算のために滑らかにしたもの)。
        /// </para>
        /// <list type="number">
        /// <item>軸 = 面の法線の平均の向き</item>
        /// <item>α = 軸といちばん離れた法線との角度。cos α = min(法線・軸)</item>
        /// <item>α ≥ 90°(法線が半球より広く散っている)なら、判定に使えない印として w = 1</item>
        /// </list>
        /// <para>
        /// meshoptimizer はもっと狭い円錐(全部の法線を含む最小の円錐)を探しているが、
        /// 平均の向きを軸にするだけでも、丸く育てたメッシュレットには十分に効く。
        /// </para>
        /// </summary>
        private Vector4 ComputeCone()
        {
            var normals = new Vector3[_triangles.Count];
            Vector3 sum = Vector3.Zero;
            for (int t = 0; t < _triangles.Count; t++)
            {
                uint packed = _triangles[t];
                Vector3 p0 = _mesh.Vertices[_vertices[(int)(packed & 0xFF)]].Position;
                Vector3 p1 = _mesh.Vertices[_vertices[(int)((packed >> 8) & 0xFF)]].Position;
                Vector3 p2 = _mesh.Vertices[_vertices[(int)((packed >> 16) & 0xFF)]].Position;

                // 反時計回りが表なので、(p1 - p0) x (p2 - p0) が表の側を向く。
                normals[t] = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                sum += normals[t];
            }

            // 法線が打ち消し合う(管を1周している、など)と平均がほぼ 0 になり、向きが決まらない。
            if (sum.Length() < 1e-3f)
            {
                return new Vector4(0.0f, 0.0f, 1.0f, 1.0f);
            }

            Vector3 axis = Vector3.Normalize(sum);
            float minCos = 1.0f;
            foreach (Vector3 n in normals)
            {
                minCos = MathF.Min(minCos, Vector3.Dot(n, axis));
            }

            // cos α ≤ 0 は α ≥ 90°。背面の判定に使えない。
            if (minCos <= 0.0f)
            {
                return new Vector4(axis, 1.0f);
            }

            // sin α = sqrt(1 - cos²α)。タスクシェーダの式が sin を欲しがるので、ここで直しておく。
            return new Vector4(axis, MathF.Sqrt(1.0f - minCos * minCos));
        }

        public MeshletSet Finish(MeshletStrategy strategy, double milliseconds) => new()
        {
            Meshlets = [.. _meshlets],
            VertexIndices = [.. _allVertices],
            Triangles = [.. _allTriangles],
            TriangleMeshlet = [.. _triangleMeshlet],
            OrderedIndices = [.. _orderedIndices],
            SourceVertexCount = _mesh.Vertices.Length,
            Strategy = strategy,
            BuildMilliseconds = milliseconds,
        };
    }
}
