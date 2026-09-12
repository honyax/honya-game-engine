namespace HonyaEngine;

/// <summary>
/// 接触で繋がった体の塊(島)を作る(Day 47 の要点5)。**Union-Find**。
///
/// <para>
/// <b>眠りは1体では決められない</b>。積み上げた6段の箱は、
/// 上から順に止まっていく——1体ずつ判定すると、
/// 上の箱だけが眠って下の箱がまだ起きている状態が生まれる。
/// そこへ7段目を落とすと、下は起きているのに上は眠ったままで、
/// <b>塊の途中で力が伝わらなくなる</b>。
/// </para>
///
/// <para>
/// 答えは<b>「触れ合っているものはまとめて眠り、まとめて起きる」</b>。
/// 接触で繋がった体を1つの島とみなして、
/// <b>島の中でいちばん眠りが浅い体</b>に合わせて島全体の眠りを決める。
/// Box2D の <c>b2Island</c> がやっているのがこれで、
/// あちらは解くのも島ごとに分けている(並列化のため)。
/// 今日は<b>眠りの判定だけ</b>に使う。
/// </para>
///
/// <para>
/// <b>静的な体は繋がない</b>。床は世界じゅうの物と触れているので、
/// 繋いだ瞬間に<b>全部が1つの島になって、どこかで誰かが動いている限り誰も眠れない</b>。
/// 静的な体は眠りも起きもしないので、島に入れる意味も無い。
/// </para>
///
/// <para>
/// Union-Find(素集合データ構造)は「2つが同じ組か」を高速に扱う古典的な仕掛け。
/// <b>木を作って根で組を表す</b>だけの素朴なもので、
/// 経路圧縮(<see cref="Find"/>)と重さによる併合(<see cref="Union"/>)を
/// 入れておけば、実質定数時間で動く。
/// </para>
/// </summary>
internal sealed class IslandBuilder
{
    /// <summary>親の番号。**自分自身なら根**。</summary>
    private int[] _parent = [];

    /// <summary>木の高さの上限(ランク)。深い木の下に浅い木を付けるために持つ。</summary>
    private byte[] _rank = [];

    /// <summary>今扱っている体の数。</summary>
    private int _count;

    /// <summary>直前に組んだ島の数(1体だけの島も数える)。**HUD 用**。</summary>
    public int IslandCount { get; private set; }

    /// <summary>
    /// 体の数を決めて、全部を「自分ひとりの島」に戻す。**ステップごとに呼ぶ**。
    /// </summary>
    public void Begin(int count)
    {
        _count = count;

        if (_parent.Length < count)
        {
            // **2倍に取る**。体は増える一方なので、毎回きっちり取ると
            // 1個足すたびに配列が作り直される。
            int capacity = Math.Max(count * 2, 64);
            _parent = new int[capacity];
            _rank = new byte[capacity];
        }

        for (int i = 0; i < count; i++)
        {
            _parent[i] = i;
            _rank[i] = 0;
        }

        IslandCount = count;
    }

    /// <summary>
    /// 2つを同じ島にする。**既に同じ島なら何もしない**。
    /// </summary>
    public void Union(int a, int b)
    {
        int rootA = Find(a);
        int rootB = Find(b);

        if (rootA == rootB)
        {
            return;
        }

        // **浅いほうを深いほうにぶら下げる**。逆にすると木がどんどん伸びて、
        // <see cref="Find"/> が線形時間になる。
        if (_rank[rootA] < _rank[rootB])
        {
            (rootA, rootB) = (rootB, rootA);
        }

        _parent[rootB] = rootA;

        if (_rank[rootA] == _rank[rootB])
        {
            _rank[rootA]++;
        }

        // 島が1つ減った。
        IslandCount--;
    }

    /// <summary>
    /// この体が属する島の代表(根)。**経路圧縮つき**。
    ///
    /// 根まで辿るついでに、通った節を全部根に直付けする。
    /// 次からは1歩で根に着くので、同じ島を何度も引く今日の使い方では効きが大きい。
    /// </summary>
    public int Find(int index)
    {
        int root = index;
        while (_parent[root] != root)
        {
            root = _parent[root];
        }

        // 経路圧縮。**辿った道を根に付け替える**。
        while (_parent[index] != root)
        {
            int next = _parent[index];
            _parent[index] = root;
            index = next;
        }

        return root;
    }

    /// <summary>体の数(<see cref="Begin"/> で渡したもの)。</summary>
    public int Count => _count;
}
