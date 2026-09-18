using System.Diagnostics;
using System.Numerics;

namespace CpuRayTracer;

/// <summary>形の探し方(B キー)。同じ絵が出るはずで、変わるのは速さだけ(要点8。自己チェック18)。</summary>
internal enum AccelerationMode
{
    /// <summary>総当たり。Day 59 と同じ。光線1本につき、全部の形に聞く。</summary>
    BruteForce,

    /// <summary>BVH。<b>中央分割</b>——いちばん長い軸で、形の重心の中央値で半分ずつに割る。作るのは速いが質は SAH に劣る。</summary>
    Median,

    /// <summary>BVH。<b>SAH</b>(表面積ヒューリスティック)。割る位置を費用の見積もりで選ぶ。既定。</summary>
    Sah,
}

/// <summary>
/// BVH(Bounding Volume Hierarchy。境界ボリューム階層)。<b>今日の後半の主役</b>(要点7・8)。
///
/// <para>
/// 形を箱で囲み、その箱をさらに大きな箱で囲む、を繰り返した木。光線は根の箱から辿り、
/// <b>通らない箱に出会ったら、その中身を丸ごと飛ばす</b>。形が N 個でも、辿る節点は平均して log N 程度で済む。
/// </para>
/// <para>
/// 「空間を区切る」方法(BSP・kd 木・八分木)と違い、<b>形のほうを木に入れる</b>のが BVH。
/// </para>
/// <list type="bullet">
/// <item>形は必ずどれか1つの葉に入る(空間を区切る方法だと、境目にまたがる形が複数の区画に重複して入る)</item>
/// <item>兄弟の箱は<b>重なってよい</b>。そのぶん「両方の子を見る」ことが起きるが、形の重複が無い</item>
/// <item>節点の数が形の数から決まる(葉が最大 N 個、内側が N−1 個)ので、<b>配列であらかじめ確保できる</b></item>
/// </list>
/// <para>
/// 木は<b>配列1本</b>(<see cref="Node"/> の並び)に平らに詰める。ポインタで繋いだ木より、
/// 隣の節点がキャッシュに載りやすい。<b>左の子は必ず自分の次</b>に置く決まりにして、
/// 節点が覚えるのは右の子の番号だけにしてある。
/// </para>
/// </summary>
internal sealed class Bvh
{
    /// <summary>
    /// 葉に入れる形の数の上限。
    ///
    /// <para>
    /// 1個まで刻むと木が深くなり、箱の判定ばかり増える。数個まとめて総当たりするほうが速い
    /// (形の判定は箱の判定の2〜3倍の手間だが、節点を1つ辿る手間も同じくらいかかる)。
    /// 4 は経験的によく使われる値。<b>改造課題1で 1・2・8・16 と変えて比べる</b>。
    /// </para>
    /// </summary>
    private const int MaxShapesPerLeaf = 4;

    /// <summary>
    /// SAH の費用の式で使う「箱を1つ辿る手間」。形1個の判定を 1 としたときの比。
    /// 小さくすると木が深くなり、大きくすると浅くなる。0.125 は PBRT が使っている値。
    /// </summary>
    private const float TraversalCost = 0.125f;

    /// <summary>
    /// 木の節点。<b>葉か内側かを <see cref="Count"/> で見分ける</b>。
    /// </summary>
    private struct Node
    {
        /// <summary>この節点の下にある形を全部囲む箱。</summary>
        public Aabb Bounds;

        /// <summary>葉なら <see cref="_shapes"/> の中の開始位置、内側なら<b>右の子</b>の節点番号(左の子は自分の次)。</summary>
        public int Index;

        /// <summary>葉なら形の数(1 以上)、内側なら 0。</summary>
        public int Count;

        /// <summary>内側の節点が、どの軸で割ったか(0/1/2)。葉では使わない。</summary>
        public int Axis;
    }

    private readonly Node[] _nodes;

    /// <summary>葉ごとにまとまるよう並べ替えた形。元の <see cref="Scene.Shapes"/> は並べ替えない。</summary>
    private readonly Shape[] _shapes;

    private Bvh(Node[] nodes, Shape[] shapes, int nodeCount, int leafCount, int maxDepth, double buildMilliseconds)
    {
        _nodes = nodes;
        _shapes = shapes;
        NodeCount = nodeCount;
        LeafCount = leafCount;
        MaxDepth = maxDepth;
        BuildMilliseconds = buildMilliseconds;
    }

    public int NodeCount { get; }

    public int LeafCount { get; }

    public int MaxDepth { get; }

    public double BuildMilliseconds { get; }

    public int ShapeCount => _shapes.Length;

    /// <summary>
    /// 葉ごとにまとまるよう並べ替えた形(<b>Day 61 で公開した</b>)。
    ///
    /// <para>
    /// 葉が覚えているのは「この並びの何番目から何個」なので、GPU へ送るときも<b>この順で</b>送らないと
    /// 節点の番号が指す先がずれる。<see cref="GpuScene"/> がここから球の配列を作る。
    /// </para>
    /// </summary>
    public IReadOnlyList<Shape> OrderedShapes => _shapes;

    /// <summary>節点1つの中身(<see cref="GpuScene"/> が GPU の配列へ写すために読む。Day 61 で追加)。</summary>
    /// <param name="Bounds">この節点の下にある形を全部囲む箱。</param>
    /// <param name="Index">葉なら <see cref="OrderedShapes"/> の中の開始位置、内側なら右の子の節点番号。</param>
    /// <param name="Count">葉なら形の数(1 以上)、内側なら 0。</param>
    /// <param name="Axis">内側の節点が割った軸(0/1/2)。</param>
    public readonly record struct NodeView(Aabb Bounds, int Index, int Count, int Axis);

    /// <summary>
    /// <paramref name="index"/> 番の節点を読む(Day 61 で追加)。
    ///
    /// <para>
    /// <see cref="Node"/> を private のままにして読み出し口だけ開けたのは、
    /// <b>木の形は作ったあと変えない</b>という約束を型で守るため。GPU 側は写すだけで、書き換えない。
    /// </para>
    /// </summary>
    public NodeView GetNode(int index)
    {
        ref Node node = ref _nodes[index];
        return new NodeView(node.Bounds, node.Index, node.Count, node.Axis);
    }

    /// <summary>
    /// 木を作る。<paramref name="shapes"/> は<b>箱で囲める形だけ</b>(平面は入らない。<see cref="Scene"/> を参照)。
    /// </summary>
    public static Bvh Build(IReadOnlyList<Shape> shapes, AccelerationMode mode)
    {
        var clock = Stopwatch.StartNew();

        var ordered = new Shape[shapes.Count];
        var bounds = new Aabb[shapes.Count];
        var centroids = new Vector3[shapes.Count];
        for (int i = 0; i < shapes.Count; i++)
        {
            ordered[i] = shapes[i];
            shapes[i].TryGetBounds(out bounds[i]);
            centroids[i] = bounds[i].Centroid;
        }

        // 節点の数の上限は 2N−1(葉が N 個で、内側がその1つ少ない)。あらかじめ取っておけば伸ばさずに済む。
        var nodes = new Node[Math.Max(1, 2 * shapes.Count - 1)];
        int nodeCount = 0;
        int leafCount = 0;
        int maxDepth = 0;

        // 並べ替えは「形の並び」そのものを動かす。深さ優先で作るので、
        // 1つの節点が受け持つ範囲は常に連続した区間になり、葉は「開始位置と個数」だけで表せる。
        int BuildRange(int first, int count, int depth)
        {
            maxDepth = Math.Max(maxDepth, depth);

            int self = nodeCount++;
            Aabb box = Aabb.Empty;
            for (int i = first; i < first + count; i++)
            {
                box = box.Union(bounds[i]);
            }

            nodes[self].Bounds = box;

            int splitAxis = 0;
            int split = -1;
            if (count > MaxShapesPerLeaf)
            {
                split = mode == AccelerationMode.Sah
                    ? FindSahSplit(ordered, bounds, centroids, first, count, box, out splitAxis)
                    : FindMedianSplit(ordered, bounds, centroids, first, count, out splitAxis);
            }

            if (split < 0)
            {
                // 葉。
                nodes[self].Index = first;
                nodes[self].Count = count;
                leafCount++;
                return self;
            }

            nodes[self].Count = 0;
            nodes[self].Axis = splitAxis;

            // 左の子を先に作る。深さ優先なので、左の子は必ず自分の次の番号になる。
            BuildRange(first, split - first, depth + 1);
            nodes[self].Index = BuildRange(split, first + count - split, depth + 1);
            return self;
        }

        if (shapes.Count > 0)
        {
            BuildRange(0, shapes.Count, 1);
        }
        else
        {
            nodes[0].Bounds = Aabb.Empty;
            nodes[0].Count = 0;
            nodes[0].Index = 0;
            nodeCount = 1;
        }

        return new Bvh(nodes, ordered, nodeCount, leafCount, maxDepth, clock.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// <b>中央分割</b>。いちばん長い軸で重心の中央値を境にして、半分ずつに割る。
    ///
    /// <para>
    /// 作るのは速い(並べ替えるだけ)が、<b>形の疎密を見ていない</b>。
    /// 密集した一角と、遠くにぽつんと1個、という並びでも同じ数ずつ割るので、
    /// 密集した側の箱が遠くの1個まで届く大きさに膨らみ、光線がその箱に何度も入ることになる。
    /// </para>
    /// </summary>
    private static int FindMedianSplit(Shape[] shapes, Aabb[] bounds, Vector3[] centroids, int first, int count, out int axis)
    {
        Aabb centroidBox = Aabb.Empty;
        for (int i = first; i < first + count; i++)
        {
            centroidBox = centroidBox.Union(centroids[i]);
        }

        axis = centroidBox.LongestAxis;

        // 重心が全部同じ点に重なっているときは割れない(割ろうとすると無限に再帰する)。
        if (Aabb.Component(centroidBox.Size, axis) <= 0.0f)
        {
            return -1;
        }

        SortRange(shapes, bounds, centroids, first, count, axis);
        return first + count / 2;
    }

    /// <summary>
    /// <b>SAH</b>(表面積ヒューリスティック。要点8)。<b>割った後の費用を見積もって、いちばん安い割り方を選ぶ</b>。
    ///
    /// <code>
    ///   費用(左右に割る) = 箱を辿る手間 + (左の箱の表面積 × 左の形の数 + 右の箱の表面積 × 右の形の数) / 親の箱の表面積
    ///   費用(葉にする)   = 形の数
    /// </code>
    /// <para>
    /// 「でたらめな光線が子の箱に入る確率は、表面積の比」という見積もりに基づく。
    /// <b>葉にするほうが安ければ割らない</b>ので、木の深さも自動で決まる。
    /// </para>
    /// <para>
    /// ここでは3軸それぞれで重心の順に並べ替え、<b>全部の切れ目</b>を試す(掃引法)。
    /// 左からの表面積を溜めながら1回、右からの表面積を配列に貯めて1回、計 2N 回で全部の切れ目の費用が出る。
    /// 形が数千個までならこれで十分速い(場面5の 499 個で 2〜4ms)。
    /// もっと多いときは、重心を 12〜16 個の区間にまとめてから試す(ビン化)方法に替える。
    /// </para>
    /// </summary>
    private static int FindSahSplit(Shape[] shapes, Aabb[] bounds, Vector3[] centroids, int first, int count, in Aabb parent, out int axis)
    {
        float parentArea = parent.SurfaceArea;
        float bestCost = count;              // 葉にする費用
        int bestAxis = -1;
        int bestSplit = -1;

        // 右側からの表面積を貯めておく作業場。毎回確保しないよう、必要な長さだけ借りる。
        var rightAreas = new float[count];

        for (int a = 0; a < 3; a++)
        {
            SortRange(shapes, bounds, centroids, first, count, a);

            // 右から: i 番目より右にある形を全部囲む箱の表面積。
            Aabb right = Aabb.Empty;
            for (int i = count - 1; i > 0; i--)
            {
                right = right.Union(bounds[first + i]);
                rightAreas[i] = right.SurfaceArea;
            }

            // 左から掃引しながら費用を出す。i は「左に入る形の数」。
            Aabb left = Aabb.Empty;
            for (int i = 1; i < count; i++)
            {
                left = left.Union(bounds[first + i - 1]);
                float cost = TraversalCost + (left.SurfaceArea * i + rightAreas[i] * (count - i)) / parentArea;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestAxis = a;
                    bestSplit = first + i;
                }
            }
        }

        axis = bestAxis < 0 ? 0 : bestAxis;
        if (bestAxis < 0)
        {
            // どう割っても葉より高い。重心が1点に重なっている場合もここに来る。
            return -1;
        }

        // 最後に試した軸(Z)の並びのままなので、選んだ軸で並べ直す。
        if (bestAxis != 2)
        {
            SortRange(shapes, bounds, centroids, first, count, bestAxis);
        }

        return bestSplit;
    }

    /// <summary>
    /// 区間を重心の座標で並べ替える。形・箱・重心の3つの配列を<b>同じ順で</b>動かす。
    /// <see cref="Array.Sort{TKey, TValue}(TKey[], TValue[], int, int)"/> は配列2本までなので、
    /// 鍵を取り出して番号を並べ替え、その順に書き戻す。
    /// </summary>
    private static void SortRange(Shape[] shapes, Aabb[] bounds, Vector3[] centroids, int first, int count, int axis)
    {
        var keys = new float[count];
        var order = new int[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = Aabb.Component(centroids[first + i], axis);
            order[i] = first + i;
        }

        Array.Sort(keys, order);

        var s = new Shape[count];
        var b = new Aabb[count];
        var c = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            s[i] = shapes[order[i]];
            b[i] = bounds[order[i]];
            c[i] = centroids[order[i]];
        }

        Array.Copy(s, 0, shapes, first, count);
        Array.Copy(b, 0, bounds, first, count);
        Array.Copy(c, 0, centroids, first, count);
    }

    /// <summary>
    /// <b>いちばん近く</b>で当たる形を探す。見つかったら <paramref name="closest"/> を縮めて true を返す。
    ///
    /// <para>
    /// 再帰ではなく<b>自前のスタック</b>で辿る。再帰にすると、形1個ごとに呼び出しの出入りが乗るうえ、
    /// 「もう <paramref name="closest"/> より遠いと分かった枝」をスタックに積んだまま持ち歩くことになる。
    /// </para>
    /// <para>
    /// <b>近い子から辿る</b>のが効く。光線の向きの符号と、その節点を割った軸を見れば、
    /// どちらの子が手前かが分かる。手前から見れば <paramref name="closest"/> が早く縮み、
    /// 奥の箱は入る前に捨てられる(<c>enter &gt; closest</c> で外れになる)。
    /// </para>
    /// </summary>
    public bool Intersect(in Ray ray, ref RayCounters counters, ref float closest, ref Shape? shape)
    {
        if (NodeCount == 0 || _shapes.Length == 0)
        {
            return false;
        }

        Vector3 invDirection = Aabb.Reciprocal(ray.Direction);
        bool negative0 = invDirection.X < 0.0f;
        bool negative1 = invDirection.Y < 0.0f;
        bool negative2 = invDirection.Z < 0.0f;

        // 深さは log(N) 程度にしかならないので、64 あれば形が 2⁶⁴ 個まで足りる。
        Span<int> stack = stackalloc int[64];
        int top = 0;
        int current = 0;
        bool hit = false;

        while (true)
        {
            counters.BoxTests++;
            if (_nodes[current].Bounds.Intersect(ray.Origin, invDirection, 0.0f, closest))
            {
                int count = _nodes[current].Count;
                if (count > 0)
                {
                    int start = _nodes[current].Index;
                    counters.ShapeTests += count;
                    for (int i = start; i < start + count; i++)
                    {
                        if (_shapes[i].Intersect(ray, 0.0f, closest, out float t))
                        {
                            closest = t;
                            shape = _shapes[i];
                            hit = true;
                        }
                    }
                }
                else
                {
                    int left = current + 1;
                    int right = _nodes[current].Index;
                    bool rightFirst = _nodes[current].Axis switch
                    {
                        0 => negative0,
                        1 => negative1,
                        _ => negative2,
                    };

                    if (rightFirst)
                    {
                        (left, right) = (right, left);
                    }

                    stack[top++] = right;
                    current = left;
                    continue;
                }
            }

            if (top == 0)
            {
                break;
            }

            current = stack[--top];
        }

        return hit;
    }

    /// <summary>
    /// 距離 <paramref name="maxDistance"/> より手前に<b>何か1つでも</b>あれば返す(影の光線用)。
    /// 見つけ次第すぐ抜ける。どれがいちばん近いかは要らないので、近い子から辿る工夫もしない。
    /// </summary>
    public Shape? FindOccluder(in Ray ray, float maxDistance, ref RayCounters counters)
    {
        if (NodeCount == 0 || _shapes.Length == 0)
        {
            return null;
        }

        Vector3 invDirection = Aabb.Reciprocal(ray.Direction);
        Span<int> stack = stackalloc int[64];
        int top = 0;
        int current = 0;

        while (true)
        {
            counters.BoxTests++;
            if (_nodes[current].Bounds.Intersect(ray.Origin, invDirection, 0.0f, maxDistance))
            {
                int count = _nodes[current].Count;
                if (count > 0)
                {
                    int start = _nodes[current].Index;
                    counters.ShapeTests += count;
                    for (int i = start; i < start + count; i++)
                    {
                        if (_shapes[i].Intersect(ray, 0.0f, maxDistance, out _))
                        {
                            return _shapes[i];
                        }
                    }
                }
                else
                {
                    stack[top++] = _nodes[current].Index;
                    current++;
                    continue;
                }
            }

            if (top == 0)
            {
                return null;
            }

            current = stack[--top];
        }
    }
}
