using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// ブロードフェーズが出す「調べる価値のある組」。**体の番号を2つ持つだけ**。
///
/// 形も位置も持たない。ここに形を持たせると
/// 「ブロードフェーズが形を知っている」ことになり、
/// 形を増やすたびにブロードフェーズを直すはめになる。
/// <b>番号だけを返して、実際の判定は呼び出し側に任せる</b>のが分離の肝。
/// </summary>
internal readonly struct BroadPair
{
    public readonly int A;
    public readonly int B;

    public BroadPair(int a, int b)
    {
        A = a;
        B = b;
    }
}

/// <summary>
/// 均一グリッド(uniform grid)によるブロードフェーズ。
/// **総当たり O(n^2) から抜けるための、いちばん単純な空間分割**。
///
/// 考え方は一言で済む——<b>世界を格子に切り、同じマス(あるいは隣のマス)に
/// いるものだけを調べる</b>。遠くにいる組は最初から候補に入らない。
///
/// 実装で押さえるところは4つ。
///
/// 1. <b>物体は複数のマスにまたがる</b>。中心のマスだけに入れると、
///    マスの境界をまたいでいる相手を見落とす。**見落としは絶対に許されない**
///    (ブロードフェーズは「余計な候補を出す」のは許されるが「取りこぼす」のは許されない)ので、
///    外接 AABB が触れるマス全部に登録する
/// 2. またがるぶん、<b>同じ組が複数のマスで見つかる</b>。
///    重複したまま返すと同じ組を何度も判定することになるので、
///    <see cref="_mark"/> の「印」で1回に潰す
/// 3. <b>List のマス配列は使わない</b>。数える → 接頭辞和 → 詰める、の3パスで
///    1本の配列に並べ替える(カウンティングソート)。
///    毎フレーム作り直しても割り当てがゼロで、走査も連続したメモリになる
/// 4. <b>マスの大きさが性能を決める</b>。小さすぎると1個が大量のマスにまたがり、
///    大きすぎると1マスに大量に入って結局そのマスの中で総当たりになる。
///    目安は「物体の平均的な直径くらい」だが、**最後は測って決める**(F12 の掃引)
///
/// **使い方は2通りある**。
///
/// <code>
///   CollectPairs … 全部の組を列挙する。総当たりの置き換え
///   Query        … 1つの箱の近くにいるものを集める。単発の問い合わせ
/// </code>
///
/// 前者は「敵どうしの押し合い」のように<b>全員対全員</b>を見る場面、
/// 後者は「この弾に当たっている敵は?」「いちばん近い敵は?」のように
/// <b>特定の1つから探す</b>場面で使う。
/// 格子は1回組めば両方に使い回せる——Day 29 の卒業制作では、
/// 1ステップに1回組んだ格子を4通りに使っている。
///
/// この構造は 3D でもそのまま通る。Day 46 で 3D 版(セルが立方体になるだけ)へ広げる。
/// </summary>
internal sealed class SpatialGrid
{
    /// <summary>1軸あたりのマス数の上限。セルを小さくしすぎたときの暴走止め。</summary>
    private const int MaxCellsPerAxis = 2048;

    private Vector2 _origin;
    private float _cellSize = 32.0f;
    private float _inverseCellSize = 1.0f / 32.0f;
    private int _columns = 1;
    private int _rows = 1;

    /// <summary>
    /// マスごとの開始位置。長さは「マス数 + 1」。
    /// <c>_cellStart[c]</c> から <c>_cellStart[c + 1]</c> の手前までが、そのマスの中身。
    /// **末尾に1個余分を持つ**ことで「最後のマス」を特別扱いせずに済む。
    /// </summary>
    private int[] _cellStart = [];

    /// <summary>詰めるときの書き込み位置(<see cref="_cellStart"/> の作業用コピー)。</summary>
    private int[] _cursor = [];

    /// <summary>マス順に並べ替えた「体の番号」。長さは登録の総数。</summary>
    private int[] _entries = [];

    /// <summary>
    /// 重複した組を潰すための印。<c>_mark[j]</c> に「最後に j を見つけたときの通し番号」を入れる。
    ///
    /// 毎回クリアすると O(n) が余分にかかるので、**通し番号を増やしていくことでクリアの代わり**にする。
    /// 集合の代わりに整数1個で済むこの手口は、グラフ探索の visited などでも定番。
    /// </summary>
    private int[] _mark = [];
    private int _stamp;

    private BroadPair[] _pairs = [];
    private int _pairCount;

    public float CellSize => _cellSize;

    public int Columns => _columns;

    public int Rows => _rows;

    public int CellCount => _columns * _rows;

    /// <summary>登録の総数。**体数より多くなる**(またがったぶんだけ重複して入る)。</summary>
    public int EntryCount { get; private set; }

    /// <summary>中身が1個以上あるマスの数。</summary>
    public int OccupiedCells { get; private set; }

    /// <summary>いちばん混んでいるマスの中身の数。**ここが大きいと総当たりに戻っていく**。</summary>
    public int MaxPerCell { get; private set; }

    /// <summary>同じマスに同居していた組の数。AABB で足切りする**前**の数。</summary>
    public long CoLocatedPairs { get; private set; }

    /// <summary>足切りを通った候補の数。ナローフェーズを呼ぶ回数がこれになる。</summary>
    public int PairCount => _pairCount;

    public ReadOnlySpan<BroadPair> Pairs => _pairs.AsSpan(0, _pairCount);

    /// <summary>
    /// 格子の位置と大きさ、マスの大きさを決める。
    ///
    /// **世界の外に出た物体は端のマスに丸められる**(<see cref="CellRange"/> のクランプ)。
    /// 落ちも見落としも起きない代わりに、端のマスが混みやすくなる。
    /// 世界の広さが決まらない(無限に広がる)場合は、
    /// マス番号をハッシュして固定長の表に落とす「空間ハッシュ」にする。
    /// 実装はほぼ同じで、<c>cell = hash(cx, cy) % 表の大きさ</c> に変わるだけ。
    /// </summary>
    public void Configure(Vector2 origin, Vector2 size, float cellSize)
    {
        _origin = origin;
        _cellSize = MathF.Max(cellSize, 1.0f);
        _inverseCellSize = 1.0f / _cellSize;

        _columns = Math.Clamp((int)MathF.Ceiling(size.X * _inverseCellSize), 1, MaxCellsPerAxis);
        _rows = Math.Clamp((int)MathF.Ceiling(size.Y * _inverseCellSize), 1, MaxCellsPerAxis);
    }

    /// <summary>
    /// マスの大きさの目安を出す。**平均的な直径**を返す。
    ///
    /// 小さいほうへ外すと1個が大量のマスにまたがり(コストは辺の比の2乗で効く)、
    /// 大きいほうへ外すと1マスの中で総当たりになる。
    /// どちらへ外しても損なので、この目安は「測り始める場所」でしかない。
    /// F12 の掃引で、実際の最適値がこの前後にあることを確かめられる。
    /// </summary>
    public static float SuggestCellSize(ReadOnlySpan<Aabb2D> bounds)
    {
        if (bounds.Length == 0)
        {
            return 32.0f;
        }

        double total = 0.0;
        for (int i = 0; i < bounds.Length; i++)
        {
            Vector2 size = bounds[i].Size;
            total += (size.X + size.Y) * 0.5;
        }

        return MathF.Max((float)(total / bounds.Length), 1.0f);
    }

    /// <summary>
    /// 格子を組み直す。**毎フレーム丸ごと作り直す**。
    ///
    /// 差分更新(動いた物体だけマスを移す)もできるが、
    ///   - 全員が毎フレーム動くなら差分にする意味がない
    ///   - 「今どのマスに入っているか」を物体側に持たせることになり、状態が増える
    /// ので、まずは作り直しでよい。3パスとも O(n) なので、
    /// 実測でも構築は候補列挙よりずっと安い。
    ///
    /// 3パスの構成はカウンティングソートそのもの。
    ///   1. マスごとの個数を数える
    ///   2. 接頭辞和を取って、各マスの開始位置を出す
    ///   3. もう一度なめて、番号を所定の位置へ書き込む
    /// **List&lt;int&gt;[] を使わない**のはこのため。マスの数だけ List を持つと、
    /// 割り当てとポインタ追跡が毎フレーム発生する(計画書の改造課題1で測る)。
    /// </summary>
    public void Build(ReadOnlySpan<Aabb2D> bounds)
    {
        int cells = CellCount;

        // **印の確保はここでやる**。組んだあとなら <see cref="CollectPairs"/> も
        // <see cref="Query"/> も同じ印を使えるので、置き場所はここが自然。
        EnsureMark(bounds.Length);

        if (_cellStart.Length < cells + 1)
        {
            _cellStart = new int[cells + 1];
            _cursor = new int[cells + 1];
        }
        else
        {
            Array.Clear(_cellStart, 0, cells + 1);
        }

        // --- パス1: 数える ---
        //
        // **1つずらした位置に数を入れる**(_cellStart[cell + 1]++)。
        // こうしておくと、次の接頭辞和がそのまま「開始位置」になる。
        for (int i = 0; i < bounds.Length; i++)
        {
            CellRange(bounds[i], out int x0, out int y0, out int x1, out int y1);

            for (int cy = y0; cy <= y1; cy++)
            {
                int rowBase = cy * _columns;
                for (int cx = x0; cx <= x1; cx++)
                {
                    _cellStart[rowBase + cx + 1]++;
                }
            }
        }

        // --- パス2: 接頭辞和 ---
        for (int c = 1; c <= cells; c++)
        {
            _cellStart[c] += _cellStart[c - 1];
        }

        EntryCount = _cellStart[cells];

        if (_entries.Length < EntryCount)
        {
            // 増えるたびに確保し直さないよう、少し多めに取る。
            _entries = new int[Math.Max(EntryCount * 2, 64)];
        }

        // --- パス3: 詰める ---
        Array.Copy(_cellStart, _cursor, cells);

        for (int i = 0; i < bounds.Length; i++)
        {
            CellRange(bounds[i], out int x0, out int y0, out int x1, out int y1);

            for (int cy = y0; cy <= y1; cy++)
            {
                int rowBase = cy * _columns;
                for (int cx = x0; cx <= x1; cx++)
                {
                    _entries[_cursor[rowBase + cx]++] = i;
                }
            }
        }

        UpdateOccupancyStats(cells);
    }

    /// <summary>
    /// 候補の組を集める。**ここがブロードフェーズの本体**。
    ///
    /// 体 i について、i が触れているマスを順に見て、そこに入っている j を拾う。
    /// 拾うときの条件が2つあり、どちらも重複を消すためのもの。
    ///
    ///   - <b><c>j &gt; i</c> のときだけ</b> — 同じ組を (i,j) と (j,i) の2回作らない。
    ///     番号の小さいほうから見たときにだけ組にする、と決めておけば1回で済む
    ///   - <b>印(<see cref="_mark"/>)が今回のものでないときだけ</b> —
    ///     i と j が2つ以上のマスを共有していると、マスごとに同じ組が見つかる。
    ///     上の <c>j &gt; i</c> だけでは消えない
    ///
    /// 最後に外接 AABB で足切りする。同じマスにいても離れていることは普通にあるので、
    /// **7.4ns の判定を1回挟むほうが、120ns の SAT を呼ぶより安い**(Day 25 の要点1)。
    /// </summary>
    /// <returns>候補の組の数。</returns>
    public int CollectPairs(ReadOnlySpan<Aabb2D> bounds)
    {
        _pairCount = 0;
        CoLocatedPairs = 0;

        if (_pairs.Length < 64)
        {
            _pairs = new BroadPair[1024];
        }

        for (int i = 0; i < bounds.Length; i++)
        {
            int stamp = ++_stamp;
            Aabb2D box = bounds[i];
            CellRange(box, out int x0, out int y0, out int x1, out int y1);

            for (int cy = y0; cy <= y1; cy++)
            {
                int rowBase = cy * _columns;

                for (int cx = x0; cx <= x1; cx++)
                {
                    int cell = rowBase + cx;
                    int end = _cellStart[cell + 1];

                    for (int e = _cellStart[cell]; e < end; e++)
                    {
                        int j = _entries[e];

                        if (j <= i)
                        {
                            continue;
                        }

                        if (_mark[j] == stamp)
                        {
                            continue;
                        }

                        _mark[j] = stamp;
                        CoLocatedPairs++;

                        if (!Collision2D.Overlap(box, bounds[j]))
                        {
                            continue;
                        }

                        if (_pairCount == _pairs.Length)
                        {
                            Array.Resize(ref _pairs, _pairs.Length * 2);
                        }

                        _pairs[_pairCount++] = new BroadPair(i, j);
                    }
                }
            }
        }

        return _pairCount;
    }

    /// <summary>
    /// 箱の近くにいるものを集める。**単発の問い合わせ**。
    ///
    /// <see cref="CollectPairs"/> が「全部の組」を返すのに対して、
    /// こちらは「この箱の近くにいるもの」だけを返す。
    /// 弾が当たった敵を探す、いちばん近い敵を探す、
    /// 爆風の範囲に入っている敵を探す——**1対多**はこちらになる。
    ///
    /// <b>返すのは候補まで</b>。同じマスにいるだけで実際には離れている相手も混ざる。
    /// <see cref="CollectPairs"/> は外接 AABB で足切りしていたが、こちらはしない——
    /// 呼び出し側が持っている形(円なのか箱なのか)で判定したほうが
    /// 正確だし速いことが多いため。**ブロードフェーズは絞るところまで**。
    ///
    /// <paramref name="results"/> があふれたらそこで打ち切る。
    /// **足りなくても落とさない**代わりに、取りこぼしが起きる。
    /// 「爆風に巻き込まれる敵は最大 64 体まで」のような割り切りは、
    /// ゲームでは普通に受け入れられる(戻り値と長さを比べれば起きたことは分かる)。
    /// </summary>
    /// <returns>見つかった数。<paramref name="results"/> の長さで打ち切られる。</returns>
    public int Query(in Aabb2D box, Span<int> results)
    {
        if (results.Length == 0 || _cellStart.Length == 0)
        {
            return 0;
        }

        int stamp = ++_stamp;
        int found = 0;

        CellRange(box, out int x0, out int y0, out int x1, out int y1);

        for (int cy = y0; cy <= y1; cy++)
        {
            int rowBase = cy * _columns;

            for (int cx = x0; cx <= x1; cx++)
            {
                int cell = rowBase + cx;
                int end = _cellStart[cell + 1];

                for (int e = _cellStart[cell]; e < end; e++)
                {
                    int index = _entries[e];

                    // **またがっているものは複数のマスで見つかる**。
                    // 組を作るときと同じ印で潰す(<see cref="CollectPairs"/> の要点2)。
                    if (_mark[index] == stamp)
                    {
                        continue;
                    }

                    _mark[index] = stamp;
                    results[found++] = index;

                    if (found == results.Length)
                    {
                        return found;
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 印の配列を用意し、通し番号があふれそうなら 0 に戻す。
    ///
    /// 毎回クリアする代わりに番号を進めていく方式なので、
    /// **番号が一周すると古い印が「今回の印」に見えてしまう**。
    /// 1万体 60fps でも1時間近くかかる話だが、
    /// 起きたら組を丸ごと取りこぼす種類のバグなので潰しておく。
    /// </summary>
    private void EnsureMark(int count)
    {
        if (_mark.Length < count)
        {
            _mark = new int[Math.Max(count * 2, 64)];
        }

        if (_stamp > int.MaxValue - count - 2)
        {
            Array.Clear(_mark);
            _stamp = 0;
        }
    }

    /// <summary>
    /// 箱が触れているマスの範囲。**世界の外は端に丸める**。
    ///
    /// <c>(int)</c> の切り捨ては 0 方向へ働くので、-0.5 は -0 になって
    /// 「本来 -1 のマス」が 0 に見える。ここでは端に丸めるのが正解なので
    /// <see cref="Math.Clamp(int, int, int)"/> がそのまま効くが、
    /// **負の座標をマス番号に使う場面では <c>MathF.Floor</c> が要る**
    /// (空間ハッシュにするときはそちら)。
    /// </summary>
    private void CellRange(in Aabb2D box, out int x0, out int y0, out int x1, out int y1)
    {
        Vector2 min = (box.Min - _origin) * _inverseCellSize;
        Vector2 max = (box.Max - _origin) * _inverseCellSize;

        x0 = Math.Clamp((int)min.X, 0, _columns - 1);
        y0 = Math.Clamp((int)min.Y, 0, _rows - 1);
        x1 = Math.Clamp((int)max.X, 0, _columns - 1);
        y1 = Math.Clamp((int)max.Y, 0, _rows - 1);
    }

    /// <summary>
    /// 混み具合を数える。**表示と診断のためだけ**にあるので、
    /// 本番のエンジンなら <c>#if DEBUG</c> で囲うところ。
    /// マスの数ぶんの走査なので、体数が増えても増えない。
    /// </summary>
    private void UpdateOccupancyStats(int cells)
    {
        int occupied = 0;
        int max = 0;

        for (int c = 0; c < cells; c++)
        {
            int count = _cellStart[c + 1] - _cellStart[c];
            if (count > 0)
            {
                occupied++;
                if (count > max)
                {
                    max = count;
                }
            }
        }

        OccupiedCells = occupied;
        MaxPerCell = max;
    }

    /// <summary>
    /// マスの中身を覗く。**可視化と自己チェック用**。
    /// 返すのは内部配列そのままの窓なので、書き換えてはいけない。
    /// </summary>
    public ReadOnlySpan<int> CellContents(int column, int row)
    {
        int cell = (row * _columns) + column;
        int start = _cellStart[cell];
        return _entries.AsSpan(start, _cellStart[cell + 1] - start);
    }
}
