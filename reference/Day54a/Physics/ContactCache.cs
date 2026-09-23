using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 1つの接触点について、**前のステップで掛けたインパルスを覚えておく**もの(Day 47 の要点2)。
///
/// 覚えるのは大きさ3つと、それがどこの点だったかを言うための印2つ。
/// 印は<b>物体座標</b>で持つ——体が動いても印は体に付いて回るので、
/// 「同じ角の同じ接触」を次のステップでも見つけられる。
/// </summary>
internal struct CachedPoint
{
    /// <summary>接触点を A の物体座標で表したもの。**照合の鍵**。</summary>
    public Vector3 LocalA;

    /// <summary>接触点を B の物体座標で表したもの。</summary>
    public Vector3 LocalB;

    /// <summary>法線方向に溜まっていたインパルス [N·s]。</summary>
    public float NormalImpulse;

    /// <summary>接線1方向に溜まっていたインパルス。</summary>
    public float TangentImpulse1;

    /// <summary>接線2方向に溜まっていたインパルス。</summary>
    public float TangentImpulse2;
}

/// <summary>
/// 1組ぶんのキャッシュ。**点は最大4つ**(<see cref="ContactManifold.MaxPoints"/> と同じ)。
/// </summary>
internal struct CachedManifold
{
    public int Count;
    public CachedPoint Point0;
    public CachedPoint Point1;
    public CachedPoint Point2;
    public CachedPoint Point3;

    /// <summary>最後に書き込まれたステップ番号。**古くなった組を捨てるため**。</summary>
    public int Stamp;

    /// <summary>添字で読む。<c>[InlineArray]</c> を使わないのは、辞書の値に入れるから。</summary>
    public readonly CachedPoint this[int index] => index switch
    {
        0 => Point0,
        1 => Point1,
        2 => Point2,
        _ => Point3,
    };

    /// <summary>添字で書く。</summary>
    public void Set(int index, in CachedPoint point)
    {
        switch (index)
        {
            case 0: Point0 = point; break;
            case 1: Point1 = point; break;
            case 2: Point2 = point; break;
            default: Point3 = point; break;
        }
    }
}

/// <summary>
/// 接触インパルスの持ち越し(Day 47 の要点2)。**ウォームスタートの記憶装置**。
///
/// <para>
/// Sequential Impulses は反復法なので、<b>初期値の良し悪しがそのまま反復回数になる</b>。
/// 床に置かれた箱は、毎ステップ「重力ぶんを打ち消すインパルス」を
/// 0 から探し直している——けれど答えは前のステップとほとんど同じはずで、
/// <b>前の答えから始めれば1〜2周でほぼ正解に着く</b>。
/// これがウォームスタート(温存)で、Box2D のデモで
/// 「反復回数を減らしても柱が崩れなくなる」のはこの1点による。
/// </para>
///
/// <para>
/// <b>難しいのは「同じ接触点」をどう見分けるか</b>。
/// 接触点は毎ステップ作り直されるので、番号も並び順も当てにならない。
/// 2通りの答えがある。
/// </para>
///
/// <list type="number">
/// <item>
/// <b>特徴 ID</b>(Box2D)。「基準面3番 × 入射面の頂点2番」のような
/// <b>どの部品どうしが触れたか</b>を接触点に持たせて、ID が一致したら同じ点とみなす。
/// 正確だが、クリップの途中で生まれる交点にも ID を振る必要があり、
/// 面の切り出しのコードが ID を運ぶために全部書き換わる。
/// </item>
/// <item>
/// <b>位置で照合</b>(Bullet の <c>btPersistentManifold</c>)。
/// <b>物体座標での距離</b>がしきい値より近ければ同じ点とみなす。
/// 判定側のコードに一切触らずに済むのが値打ちで、
/// 形が5種類ある今日の世界では<b>こちらのほうが安い</b>——
/// 球も箱もカプセルも地形も、同じ1本の照合で通る。
/// </item>
/// </list>
///
/// <para>
/// ここでは 2 を採った。取り違えても<b>初期値が少しずれるだけ</b>で、
/// 反復が正しい答えへ引き戻す——<b>間違えても壊れない</b>のがこの手の強み。
/// (特徴 ID を通すのは改造課題3)
/// </para>
/// </summary>
internal sealed class ContactCache
{
    /// <summary>
    /// 同じ点とみなす距離 [m]。**2cm**。
    ///
    /// 広すぎると隣の角のインパルスを引き継いで一瞬跳ね、
    /// 狭すぎると毎ステップ照合に失敗して<b>温存が効かなくなる</b>。
    /// 1ステップで接触点が動く距離(速度 × dt)より広く、
    /// 接触点どうしの間隔(箱の一辺)より狭ければよい。
    /// </summary>
    public const float MatchDistance = 0.02f;

    private const float MatchDistanceSquared = MatchDistance * MatchDistance;

    /// <summary>
    /// 組(体の番号2つ)からキャッシュを引く辞書。
    ///
    /// **鍵は 64bit に詰めた2つの番号**。<c>(long, long)</c> のタプルでも書けるが、
    /// 単純な整数のほうがハッシュが速く、割り当ても起きない。
    /// </summary>
    private readonly Dictionary<long, CachedManifold> _entries = [];

    /// <summary>今のステップ番号。**書かれなかった組を捨てるのに使う**。</summary>
    private int _stamp;

    /// <summary>直前のステップで温存が効いた点の数。**HUD 用**。</summary>
    public int MatchedPoints { get; private set; }

    /// <summary>直前のステップで新しく生まれた点の数。</summary>
    public int NewPoints { get; private set; }

    /// <summary>覚えている組の数。</summary>
    public int PairCount => _entries.Count;

    /// <summary>ステップの頭で1回呼ぶ。**印を1つ進めて、数え直す**。</summary>
    public void BeginStep()
    {
        _stamp++;
        MatchedPoints = 0;
        NewPoints = 0;
    }

    /// <summary>全部忘れる。**体を入れ替えたら必ず呼ぶ**(番号の意味が変わるので)。</summary>
    public void Clear()
    {
        _entries.Clear();
        _stamp = 0;
        MatchedPoints = 0;
        NewPoints = 0;
    }

    /// <summary>
    /// 前のステップの同じ接触点を探す。**見つからなければ 0 から始める**。
    ///
    /// <b>いちばん近い1点</b>を選ぶ。しきい値の中に2点あることは
    /// 実際にはまず無いが、「最初に見つかったもの」にすると
    /// キャッシュの並び順に結果が依存してしまう。
    /// </summary>
    public bool TryMatch(
        int bodyA,
        int bodyB,
        Vector3 localA,
        Vector3 localB,
        out CachedPoint result)
    {
        result = default;

        if (!_entries.TryGetValue(Key(bodyA, bodyB), out CachedManifold entry))
        {
            NewPoints++;
            return false;
        }

        int best = -1;
        float bestDistance = MatchDistanceSquared;

        for (int i = 0; i < entry.Count; i++)
        {
            CachedPoint candidate = entry[i];

            // **両方の体で近いことを求める**。片方だけで見ると、
            // 薄い板の表と裏のように「A では同じ場所、B では別の場所」を
            // 取り違える。距離は大きいほうを採る(どちらも近いことの確認)。
            float distance = MathF.Max(
                (candidate.LocalA - localA).LengthSquared(),
                (candidate.LocalB - localB).LengthSquared());

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        if (best < 0)
        {
            NewPoints++;
            return false;
        }

        result = entry[best];
        MatchedPoints++;
        return true;
    }

    /// <summary>
    /// 1組ぶんの結果を書き込む。**ステップの最後に呼ぶ**。
    ///
    /// 上書きなので、点が4つから2つに減れば古い2つは消える。
    /// 「消える」ことに意味があり、離れた接触のインパルスを残しておくと
    /// <b>次に触れた瞬間に有りもしない力で弾かれる</b>。
    /// </summary>
    public void Store(int bodyA, int bodyB, ReadOnlySpan<CachedPoint> points)
    {
        var entry = new CachedManifold
        {
            Count = Math.Min(points.Length, ContactManifold.MaxPoints),
            Stamp = _stamp,
        };

        for (int i = 0; i < entry.Count; i++)
        {
            entry.Set(i, points[i]);
        }

        _entries[Key(bodyA, bodyB)] = entry;
    }

    /// <summary>
    /// このステップで書かれなかった組を捨てる。**離れた組を溜め込まないため**。
    ///
    /// 捨てないと、一度でも触れた組の記憶が永久に残る。
    /// 地形の上に 200 個降らせる筋書きでは、
    /// <b>組の数は毎ステップ入れ替わる</b>ので、放っておくと辞書が際限なく太る。
    ///
    /// <para>
    /// <b>1ステップ遅れで捨てている</b>(印が1つ以上古いもの)。
    /// 「今このステップで触れていない = すぐ捨てる」でよいのは、
    /// 接触が消えたらインパルスも 0 から始め直すべきだから。
    /// </para>
    /// </summary>
    public void Prune()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        // **辞書を回しながら消せない**ので、消す鍵を先に集める。
        // 毎ステップ割り当てが起きるのが気になるなら、
        // 鍵の配列を使い回す形に変えられる(組の数は数百なので今は素朴に)。
        List<long>? doomed = null;

        foreach (KeyValuePair<long, CachedManifold> pair in _entries)
        {
            if (pair.Value.Stamp != _stamp)
            {
                doomed ??= [];
                doomed.Add(pair.Key);
            }
        }

        if (doomed is null)
        {
            return;
        }

        foreach (long key in doomed)
        {
            _entries.Remove(key);
        }
    }

    /// <summary>
    /// 体の番号2つを1つの整数にする。**小さいほうを上位に置く**。
    ///
    /// 組は順序を持たないので、<c>(3, 7)</c> と <c>(7, 3)</c> が
    /// 同じ鍵にならなければならない。
    /// </summary>
    private static long Key(int a, int b)
    {
        int low = Math.Min(a, b);
        int high = Math.Max(a, b);
        return ((long)low << 32) | (uint)high;
    }
}
