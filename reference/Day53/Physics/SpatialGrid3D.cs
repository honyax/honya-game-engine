using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 均一グリッドによるブロードフェーズの 3D 版。
/// **総当たり O(n²) から抜けるための、いちばん単純な空間分割**(要点5〜7)。
///
/// Day 26 の <see cref="SpatialGrid"/> をそのまま3次元へ広げたもので、
/// 考え方は一言のまま——<b>世界を格子に切り、同じマス(あるいは隣のマス)に
/// いるものだけを調べる</b>。3パスのカウンティングソートも、
/// 重複を潰す通し番号の印も、Day 26 のものをそのまま使っている。
///
/// <para>
/// <b>2D から素直に増えるところ</b>
/// </para>
/// <list type="bullet">
/// <item>マスが立方体になり、番号が <c>(z * rows + y) * columns + x</c> の3重になる</item>
/// <item>1つの体がまたぐマスが<b>面ではなく体積</b>になる——
/// セルを半分にすると登録数が 4 倍ではなく <b>8 倍</b>になる。
/// マスの大きさの選び方が 2D よりシビアなのはこのため</item>
/// <item>マスの総数も3乗で増える。100m 四方を 1m マスで切ると 2D なら1万だが、
/// 3D では高さ 20m でも 20 万——<b>空のマスの走査が効いてくる</b></item>
/// </list>
///
/// <para>
/// <b>3D で新しく出てくる問題が1つある</b>。それが「格子に入らないもの」(要点6)。
/// 2D の Day 26 では、体は全部同じくらいの大きさの円と箱だった。
/// 今日の世界には<b>無限に広い平面</b>と<b>世界じゅうを覆う地形</b>がいる。
/// これを素直に登録すると、平面は inf のマス番号を作って壊れ、
/// 地形は全部のマスに入って格子の意味を消す。
/// </para>
///
/// <para>
/// 答えは「入れない」。<b>格子に入らないものは別の列(<see cref="Oversized"/>)に置き、
/// 全部の相手と組にする</b>。数は数個なので、
/// n 個の体に対して数個 × n の候補が増えるだけで済む。
/// <b>ブロードフェーズは余計な候補を出すのは許されるが、取りこぼすのは許されない</b>——
/// 迷ったら候補に入れる、が鉄則になる。
/// </para>
///
/// <para>
/// <b>使い方は Day 26 と同じ2通り</b>。
/// <c>CollectPairs</c> が全部の組(総当たりの置き換え)、
/// <c>Query</c> が1つの箱の近く(単発の問い合わせ)。
/// 前者は <see cref="PhysicsWorld.Step"/>、
/// 後者は <see cref="PhysicsWorld.QueryCapsule"/>——
/// つまり<b>キャラクターの足元の判定</b>がこれで速くなる。
/// </para>
/// </summary>
internal sealed class SpatialGrid3D
{
    /// <summary>1軸あたりのマス数の上限。セルを小さくしすぎたときの暴走止め。</summary>
    private const int MaxCellsPerAxis = 256;

    /// <summary>
    /// 1つの体が占めてよいマスの数の上限。**これを超えたら格子に入れない**(要点6)。
    ///
    /// 地形のように広いものを登録すると、全部のマスにその番号が入る。
    /// そうなると「同じマスにいる相手」を探すたびに地形が必ず出てくるので、
    /// <b>格子を引いた意味がそのまま消える</b>うえに、
    /// 登録の3パスがマスの数ぶん走って構築だけで重くなる。
    ///
    /// <para>
    /// 64 は「4×4×4 マス」ぶん。世界を 30 マス角で切ってあるなら、
    /// 1辺の 13% までの大きさなら格子に入る、という目安になる。
    /// </para>
    /// </summary>
    private const int MaxCellsPerEntry = 64;

    private Vector3 _origin;
    private float _cellSize = 2.0f;
    private float _inverseCellSize = 0.5f;
    private int _columns = 1;
    private int _rows = 1;
    private int _layers = 1;

    /// <summary>
    /// マスごとの開始位置。長さは「マス数 + 1」。
    /// <c>_cellStart[c]</c> から <c>_cellStart[c + 1]</c> の手前までが、そのマスの中身。
    /// **末尾に1個余分を持つ**ことで「最後のマス」を特別扱いせずに済む(Day 26 と同じ)。
    /// </summary>
    private int[] _cellStart = [];

    /// <summary>詰めるときの書き込み位置(<see cref="_cellStart"/> の作業用コピー)。</summary>
    private int[] _cursor = [];

    /// <summary>マス順に並べ替えた「体の番号」。長さは登録の総数。</summary>
    private int[] _entries = [];

    /// <summary>
    /// 重複した組を潰すための印。<c>_mark[j]</c> に「最後に j を見つけたときの通し番号」を入れる。
    /// 毎回クリアする代わりに通し番号を増やしていく(Day 26 の要点)。
    /// </summary>
    private int[] _mark = [];
    private int _stamp;

    /// <summary>格子に入らなかった体の番号(平面・地形など)。**全部の相手と組にする**。</summary>
    private int[] _oversized = [];
    private int _oversizedCount;

    private BroadPair[] _pairs = [];
    private int _pairCount;

    public float CellSize => _cellSize;

    public int Columns => _columns;

    public int Rows => _rows;

    public int Layers => _layers;

    public int CellCount => _columns * _rows * _layers;

    /// <summary>格子の (0,0,0) 隅の世界座標。</summary>
    public Vector3 Origin => _origin;

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

    /// <summary>格子に入らなかった体(平面・地形)。**必ず候補に入る組**。</summary>
    public ReadOnlySpan<int> Oversized => _oversized.AsSpan(0, _oversizedCount);

    /// <summary>格子に入らなかった体の数。**HUD に出す**——増えていたら設計を疑う。</summary>
    public int OversizedCount => _oversizedCount;

    /// <summary>
    /// 一度でも組んだか。**中身を覗く前に必ず確かめる**。
    ///
    /// <see cref="Configure"/> はマスの数だけを決めるので、
    /// <b><see cref="Build"/> より先にマスの数が確定してしまう</b>。
    /// その隙間で <see cref="CellContents"/> を呼ぶと、
    /// まだ長さ 0 の配列を「マスの数ぶんある」つもりで引いて落ちる——
    /// 実際、可視化(Alt+G)を最初のフレームで有効にすると必ずそうなった。
    ///
    /// <para>
    /// <b>「設定した」と「組んだ」は別</b>という、
    /// 遅延して用意する種類のオブジェクトにありがちな段差で、
    /// 内部の配列の長さを外から見えるようにして塞いである。
    /// </para>
    /// </summary>
    public bool IsBuilt => _cellStart.Length > CellCount;

    /// <summary>
    /// 格子の位置と大きさ、マスの大きさを決める。
    ///
    /// **世界の外に出た物体は端のマスに丸められる**(<see cref="CellRange"/>)。
    /// 落ちも見落としも起きない代わりに、端のマスが混みやすくなる。
    /// 世界の広さが決まらない場合は、マス番号をハッシュして
    /// 固定長の表に落とす「空間ハッシュ」にする——実装はほぼ同じ(Day 26 と同じ話)。
    /// </summary>
    public void Configure(Vector3 origin, Vector3 size, float cellSize)
    {
        _origin = origin;
        _cellSize = MathF.Max(cellSize, 0.05f);
        _inverseCellSize = 1.0f / _cellSize;

        _columns = Math.Clamp((int)MathF.Ceiling(size.X * _inverseCellSize), 1, MaxCellsPerAxis);
        _rows = Math.Clamp((int)MathF.Ceiling(size.Y * _inverseCellSize), 1, MaxCellsPerAxis);
        _layers = Math.Clamp((int)MathF.Ceiling(size.Z * _inverseCellSize), 1, MaxCellsPerAxis);
    }

    /// <summary>
    /// マスの大きさの目安。**格子に入るものだけの平均的な差し渡し**を返す。
    ///
    /// 無限のもの(平面)を混ぜると平均が壊れるので外す。
    /// 2D(Day 26)では「小さすぎると1個が大量のマスにまたがり、
    /// 大きすぎると1マスの中で総当たり」と書いた。
    /// **3D ではまたがるほうの罰が重い**——辺の比の2乗ではなく3乗で効く。
    /// 迷ったら大きめに外すほうが安い、というのが 2D との違い。
    /// </summary>
    public static float SuggestCellSize(ReadOnlySpan<Aabb3D> bounds)
    {
        double total = 0.0;
        int count = 0;

        for (int i = 0; i < bounds.Length; i++)
        {
            if (!bounds[i].IsFinite)
            {
                continue;
            }

            Vector3 size = bounds[i].Size;
            total += (size.X + size.Y + size.Z) / 3.0;
            count++;
        }

        return count == 0 ? 2.0f : MathF.Max((float)(total / count), 0.1f);
    }

    /// <summary>
    /// 格子を組み直す。**毎フレーム丸ごと作り直す**(Day 26 と同じ)。
    ///
    /// 3パスのカウンティングソートで、<c>List&lt;int&gt;[]</c> を1本も作らない。
    ///   1. マスごとの個数を数える
    ///   2. 接頭辞和を取って、各マスの開始位置を出す
    ///   3. もう一度なめて、番号を所定の位置へ書き込む
    ///
    /// <para>
    /// <b>3D で1つ増えた仕事</b>が、格子に入らないものの仕分け(要点6)。
    /// 数える前に <see cref="Fits"/> を通し、
    /// 落ちたものは <see cref="_oversized"/> へ回す。
    /// 「入れられないものをどうするか」を最初に決めておかないと、
    /// <b>平面が inf のマス番号を作って落ちる</b>。
    /// </para>
    /// </summary>
    public void Build(ReadOnlySpan<Aabb3D> bounds)
    {
        int cells = CellCount;

        EnsureCapacity(bounds.Length);

        if (_cellStart.Length < cells + 1)
        {
            _cellStart = new int[cells + 1];
            _cursor = new int[cells + 1];
        }
        else
        {
            Array.Clear(_cellStart, 0, cells + 1);
        }

        _oversizedCount = 0;

        // --- パス1: 数える(と同時に、入らないものを仕分ける)---
        //
        // **1つずらした位置に数を入れる**(_cellStart[cell + 1]++)。
        // こうしておくと、次の接頭辞和がそのまま「開始位置」になる。
        for (int i = 0; i < bounds.Length; i++)
        {
            if (!Fits(bounds[i], out int x0, out int y0, out int z0, out int x1, out int y1, out int z1))
            {
                _oversized[_oversizedCount++] = i;
                continue;
            }

            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cy = y0; cy <= y1; cy++)
                {
                    int rowBase = ((cz * _rows) + cy) * _columns;
                    for (int cx = x0; cx <= x1; cx++)
                    {
                        _cellStart[rowBase + cx + 1]++;
                    }
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
            _entries = new int[Math.Max(EntryCount * 2, 64)];
        }

        // --- パス3: 詰める ---
        Array.Copy(_cellStart, _cursor, cells);

        for (int i = 0; i < bounds.Length; i++)
        {
            if (!Fits(bounds[i], out int x0, out int y0, out int z0, out int x1, out int y1, out int z1))
            {
                continue;
            }

            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cy = y0; cy <= y1; cy++)
                {
                    int rowBase = ((cz * _rows) + cy) * _columns;
                    for (int cx = x0; cx <= x1; cx++)
                    {
                        _entries[_cursor[rowBase + cx]++] = i;
                    }
                }
            }
        }

        UpdateOccupancyStats(cells);
    }

    /// <summary>
    /// 候補の組を集める。**ここがブロードフェーズの本体**(要点7)。
    ///
    /// Day 26 の 2D 版と重複を消す仕掛けは同じ2つ。
    ///   - <b><c>j &gt; i</c> のときだけ</b> — 同じ組を2回作らない
    ///   - <b>印が今回のものでないときだけ</b> — 2つ以上のマスを共有していると
    ///     マスごとに同じ組が見つかる
    ///
    /// <para>
    /// <b>3D で足したのが最後のループ</b>。格子に入らなかったもの(平面・地形)は
    /// どのマスにも居ないので、ここで<b>全部の相手と手作業で組にする</b>。
    /// 数が数個なら n × 数個で済み、n² には戻らない。
    /// </para>
    ///
    /// <para>
    /// 最後に外接 AABB で足切りするのも Day 26 と同じ。
    /// 同じマスにいても離れていることは普通にあるので、
    /// <b>比較6回の判定を挟むほうが、SAT を呼ぶよりずっと安い</b>。
    /// </para>
    /// </summary>
    /// <returns>候補の組の数。</returns>
    public int CollectPairs(ReadOnlySpan<Aabb3D> bounds)
    {
        _pairCount = 0;
        CoLocatedPairs = 0;

        if (_pairs.Length < 64)
        {
            _pairs = new BroadPair[1024];
        }

        for (int i = 0; i < bounds.Length; i++)
        {
            if (!Fits(bounds[i], out int x0, out int y0, out int z0, out int x1, out int y1, out int z1))
            {
                continue;
            }

            int stamp = ++_stamp;
            Aabb3D box = bounds[i];

            for (int cz = z0; cz <= z1; cz++)
            {
                for (int cy = y0; cy <= y1; cy++)
                {
                    int rowBase = ((cz * _rows) + cy) * _columns;

                    for (int cx = x0; cx <= x1; cx++)
                    {
                        int cell = rowBase + cx;
                        int end = _cellStart[cell + 1];

                        for (int e = _cellStart[cell]; e < end; e++)
                        {
                            int j = _entries[e];

                            if (j <= i || _mark[j] == stamp)
                            {
                                continue;
                            }

                            _mark[j] = stamp;
                            CoLocatedPairs++;

                            if (Aabb3D.Overlap(box, bounds[j]))
                            {
                                AddPair(i, j);
                            }
                        }
                    }
                }
            }
        }

        // --- 格子に入らなかったもの(要点6)---
        //
        // **どのマスにも居ないので、上のループでは1度も出てこない**。
        // 取りこぼすと床をすり抜けるので、ここで全員と組にする。
        // 無限の AABB(平面)は <see cref="Aabb3D.Overlap"/> が必ず true を返すので、
        // 足切りの式を場合分けせずに書ける——
        // <b>「無限」を値として持っておいた配当がここに出る</b>。
        for (int k = 0; k < _oversizedCount; k++)
        {
            int i = _oversized[k];

            for (int j = 0; j < bounds.Length; j++)
            {
                if (j == i)
                {
                    continue;
                }

                // **両方とも「入らない組」なら、番号の小さいほうの番でだけ作る**。
                // そうしないと (平面, 地形) の組が2回できる。
                if (i > j && IsOversized(j))
                {
                    continue;
                }

                CoLocatedPairs++;

                if (!Aabb3D.Overlap(bounds[i], bounds[j]))
                {
                    continue;
                }

                // **番号の小さいほうを A にそろえる**。
                // 上のループが (i, j) を i &lt; j で作っているので、
                // ここだけ順番が違うと呼ぶ側から見て一貫しない。
                AddPair(Math.Min(i, j), Math.Max(i, j));
            }
        }

        return _pairCount;
    }

    /// <summary>
    /// 箱の近くにいるものを集める。**単発の問い合わせ**(要点7)。
    ///
    /// <see cref="CollectPairs"/> が「全部の組」を返すのに対して、
    /// こちらは「この箱の近くにいるもの」だけを返す。
    /// <b>キャラクターの足元の判定がこれ</b>——
    /// Day 45 の <see cref="PhysicsWorld.QueryCapsule"/> は
    /// 毎ステップ 10 回以上、世界じゅうの体をなめていた。
    ///
    /// <para>
    /// <b>格子に入らなかったものは必ず入れる</b>。
    /// 床(平面)と地形がここに入っているので、
    /// 落とすと<b>キャラクターが世界をすり抜けて落ちる</b>。
    /// しかも「たまに落ちる」ではなく「必ず落ちる」ので、
    /// 幸いすぐ気づける種類の間違いではある。
    /// </para>
    ///
    /// <para>
    /// <b>返すのは候補まで</b>。同じマスにいるだけで実際には離れている相手も混ざる。
    /// <paramref name="results"/> があふれたらそこで打ち切る——
    /// 取りこぼしは起きるが、戻り値と長さを比べれば起きたことは分かる。
    /// </para>
    /// </summary>
    /// <returns>見つかった数。<paramref name="results"/> の長さで打ち切られる。</returns>
    public int Query(in Aabb3D box, Span<int> results)
    {
        if (results.Length == 0 || _cellStart.Length == 0)
        {
            return 0;
        }

        int stamp = ++_stamp;
        int found = 0;

        // **入らない組を先に入れる**。あふれたときに落ちるのが
        // 床や地形になると致命的なので、いちばん大事なものから詰める。
        for (int k = 0; k < _oversizedCount && found < results.Length; k++)
        {
            int index = _oversized[k];
            _mark[index] = stamp;
            results[found++] = index;
        }

        CellRange(box, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);

        for (int cz = z0; cz <= z1; cz++)
        {
            for (int cy = y0; cy <= y1; cy++)
            {
                int rowBase = ((cz * _rows) + cy) * _columns;

                for (int cx = x0; cx <= x1; cx++)
                {
                    int cell = rowBase + cx;
                    int end = _cellStart[cell + 1];

                    for (int e = _cellStart[cell]; e < end; e++)
                    {
                        int index = _entries[e];

                        // **またがっているものは複数のマスで見つかる**。印で潰す。
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
        }

        return found;
    }

    /// <summary>マスの中身を覗く。**可視化と自己チェック用**(書き換えてはいけない)。</summary>
    public ReadOnlySpan<int> CellContents(int column, int row, int layer)
    {
        int cell = (((layer * _rows) + row) * _columns) + column;
        int start = _cellStart[cell];
        return _entries.AsSpan(start, _cellStart[cell + 1] - start);
    }

    /// <summary>マス1つの世界での箱。**可視化用**。</summary>
    public Aabb3D CellBounds(int column, int row, int layer)
    {
        Vector3 min = _origin + (new Vector3(column, row, layer) * _cellSize);
        return new Aabb3D(min, min + new Vector3(_cellSize));
    }

    /// <summary>組を1つ足す。**あふれたら倍に伸ばす**。</summary>
    private void AddPair(int a, int b)
    {
        if (_pairCount == _pairs.Length)
        {
            Array.Resize(ref _pairs, _pairs.Length * 2);
        }

        _pairs[_pairCount++] = new BroadPair(a, b);
    }

    /// <summary>この番号が「格子に入らなかった組」か。**線形に探す**(数個しかない)。</summary>
    private bool IsOversized(int index)
    {
        for (int k = 0; k < _oversizedCount; k++)
        {
            if (_oversized[k] == index)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 格子に入れられるか。**無限か、大きすぎるかを見る**(要点6)。
    ///
    /// 落ちる条件は2つだけ。
    /// <list type="number">
    /// <item><b>無限</b>(平面)。マス番号が作れない</item>
    /// <item><b>大きすぎる</b>(地形)。
    /// <see cref="MaxCellsPerEntry"/> を超えるマスにまたがる</item>
    /// </list>
    ///
    /// <para>
    /// 2つ目は<b>掛け算があふれないように書く</b>のが地味な要点。
    /// マスの数を素直に <c>(x1-x0+1) * (y1-y0+1) * (z1-z0+1)</c> で出すと、
    /// 256³ の格子いっぱいに広がったものが 1,600 万になり、
    /// int でも通るが桁が気持ち悪い。上限で打ち切りながら掛ける。
    /// </para>
    /// </summary>
    private bool Fits(
        in Aabb3D box,
        out int x0, out int y0, out int z0,
        out int x1, out int y1, out int z1)
    {
        if (!box.IsFinite)
        {
            x0 = y0 = z0 = x1 = y1 = z1 = 0;
            return false;
        }

        CellRange(box, out x0, out y0, out z0, out x1, out y1, out z1);

        long span = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
        return span <= MaxCellsPerEntry;
    }

    /// <summary>
    /// 箱が触れているマスの範囲。**世界の外は端に丸める**。
    ///
    /// <c>(int)</c> の切り捨ては 0 方向へ働くので、
    /// 負の座標では1つずれる。ここでは <see cref="Math.Clamp(int, int, int)"/> が
    /// そのまま端へ丸めてくれるので実害が無いが、
    /// **空間ハッシュにするときは <c>MathF.Floor</c> が要る**(Day 26 と同じ注意)。
    /// </summary>
    private void CellRange(
        in Aabb3D box,
        out int x0, out int y0, out int z0,
        out int x1, out int y1, out int z1)
    {
        Vector3 min = (box.Min - _origin) * _inverseCellSize;
        Vector3 max = (box.Max - _origin) * _inverseCellSize;

        x0 = Math.Clamp((int)min.X, 0, _columns - 1);
        y0 = Math.Clamp((int)min.Y, 0, _rows - 1);
        z0 = Math.Clamp((int)min.Z, 0, _layers - 1);
        x1 = Math.Clamp((int)max.X, 0, _columns - 1);
        y1 = Math.Clamp((int)max.Y, 0, _rows - 1);
        z1 = Math.Clamp((int)max.Z, 0, _layers - 1);
    }

    /// <summary>印と「入らない組」の配列を用意し、通し番号があふれそうなら 0 に戻す。</summary>
    private void EnsureCapacity(int count)
    {
        if (_mark.Length < count)
        {
            _mark = new int[Math.Max(count * 2, 64)];
        }

        if (_oversized.Length < count)
        {
            _oversized = new int[Math.Max(count * 2, 64)];
        }

        // 毎回クリアする代わりに番号を進めていく方式なので、
        // **番号が一周すると古い印が「今回の印」に見えてしまう**(Day 26 の話)。
        if (_stamp > int.MaxValue - count - 2)
        {
            Array.Clear(_mark);
            _stamp = 0;
        }
    }

    /// <summary>
    /// 混み具合を数える。**表示と診断のためだけ**なので、
    /// 本番のエンジンなら <c>#if DEBUG</c> で囲うところ。
    ///
    /// <b>3D ではここが効いてくる</b>。マスの数は3乗で増えるので、
    /// 体が数十個しか無くても数万マスをなめることになる。
    /// マスを細かくしていくと、ある点から先は<b>この走査だけで遅くなる</b>——
    /// 掃引(Ctrl+Shift+G)でセルの大きさを振ると、
    /// 小さすぎる側で時間が増えるのが見える。
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
}
