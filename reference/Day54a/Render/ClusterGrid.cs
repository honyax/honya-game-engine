using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **クラスターの升目と、そこへの光の振り分け**(Day 53)。今日の主役。GL を1行も知らない。
///
/// <para>
/// Day 52 のディファードが速かったのは「光が届く画素だけを塗る」からだった。
/// Forward+ はその儲けどころを<b>フォワードのまま</b>取りに行く。
/// 画面を升目に切り、升目ごとに「ここに届きうる光」の一覧を先に作っておけば、
/// 画素シェーダは自分の升目の一覧だけを回せばよい。
/// </para>
///
/// <code>
///   Day 52 フォワード   画素ごとに 全部の光 を回す        … 1024 個なら 1024 周(実際は 64 個で打ち切り)
///   Day 53 Forward+     画素ごとに 升目の一覧 だけを回す  … 1024 個でも数個
/// </code>
///
/// <para>
/// <b>升目は3次元</b>(クラスター)。画面のタイル(既定 64 画素角)に加えて、奥行きも切る。
/// タイルだけ(2次元)だと、タイルを覗き込む視線に沿って並ぶ光が全部入ってしまう——
/// 手前の床の画素でも、20m 先の光まで回すことになる。奥行きを切ると、
/// 「その画素の深さの近くにある光」だけに絞れる(<see cref="SliceCount"/> を 1 にすると比べられる)。
/// </para>
///
/// <para>
/// <b>奥行きは等比に切る</b>(<see cref="SliceDepth"/>)。タイルが世界で覆う幅は深さに比例して広がるので、
/// 厚みも深さに比例させると、どの深さのクラスターも<b>だいたい同じ形</b>(立方体に近い)になる。
/// 等間隔に切ると、手前は細長い針、奥は平たい板になる——手前の針は奥行き方向に何メートルも伸びるので、
/// そこに入る光がタイルだけのときとほとんど変わらない。
/// </para>
///
/// <para>
/// <b>深度バッファを使わない</b>のが、振り分けを CPU でできる理由。
/// 升目の形はカメラの射影だけで決まるので、何が描かれるかを知らなくても作れる。
/// 元祖の Forward+(Harada ら 2012)は2次元のタイルを使い、深度を先に描いて(Z プリパス)
/// タイルごとの深さの範囲を測ってから絞る——その「測る」は GPU でしかできない(Day 57 のコンピュート)。
/// 奥行きを先に切ってしまうクラスタード(Olsson ら 2012)なら、測らずに済む。
/// Avalanche Studios の Persson(SIGGRAPH 2013)が振り分けを CPU でやっていたのも同じ理由。
/// </para>
///
/// <para>
/// <b>どこにも依存しない</b>のは <see cref="PointLight"/> や <see cref="LightSwarm"/> と同じ判断。
/// 入力はカメラと光の並び、出力は整数の配列2本だけなので、窓を開かずに検算できる
/// (自己チェックの半分)。GPU へ送るのは <see cref="ClusteredLighting"/> の仕事。
/// </para>
/// </summary>
internal sealed class ClusterGrid
{
    /// <summary>
    /// タイル1枚の既定の大きさ [画素]。960x640 で 15 x 10 = 150 枚。
    /// 細かくすると1升の光は減るが、升目の数と CPU の振り分けが増える(計画書の要点8)。
    /// </summary>
    public const int DefaultTileSize = 64;

    /// <summary>奥行きの既定の切り身の数。0.1m〜100m を 16 枚に切ると、1枚ごとに 1.54 倍。</summary>
    public const int DefaultSliceCount = 16;

    /// <summary>
    /// 升目の決め方の控え。**これが変わったときだけ箱を作り直す**。
    /// カメラの位置と向きは入っていない——箱はビュー空間で持つので、カメラが動いても形は変わらない。
    /// 変わるのは画面の大きさ・射影(画角や縦横比)・つまみ(タイルの大きさ・切り身の数)のときだけ。
    /// </summary>
    private (int Width, int Height, int Tile, int Slices, float Near, float Far, float ScaleX, float ScaleY) _key;

    /// <summary>
    /// クラスターごとの箱(ビュー空間。x・y と、**z の代わりに深さ**)。
    /// 深さ(= -z)で持つのは、比べる相手の光も深さで持つから。符号を反転しても距離は変わらない。
    /// </summary>
    private Vector3[] _boundsMin = [];

    private Vector3[] _boundsMax = [];

    /// <summary>クラスターごとの (始まり, 数)。GPU へそのまま送る形。</summary>
    private uint[] _grid = [];

    /// <summary>クラスターごとの数(振り分けの途中で使う)。</summary>
    private uint[] _counts = [];

    /// <summary>並べ直すときの書き込み位置(振り分けの途中で使う)。</summary>
    private uint[] _cursor = [];

    /// <summary>光の番号の並び。クラスターの (始まり, 数) がここを指す。</summary>
    private uint[] _indices = new uint[4096];

    /// <summary>(クラスター, 光) の組を見つけた順に溜めておく場所。</summary>
    private int[] _pairClusters = new int[4096];

    private int[] _pairLights = new int[4096];

    /// <summary>タイル1枚の大きさ [画素]。</summary>
    public int TileSize => _key.Tile;

    /// <summary>奥行きの切り身の数。**1 ならタイルだけ(2次元)**。</summary>
    public int SliceCount => _key.Slices;

    public int Width => _key.Width;

    public int Height => _key.Height;

    /// <summary>奥行きを切り始める深さ。カメラの近クリップ面。</summary>
    public float Near => _key.Near;

    /// <summary>奥行きを切り終える深さ。カメラの遠クリップ面。</summary>
    public float Far => _key.Far;

    public int TilesX { get; private set; }

    public int TilesY { get; private set; }

    public int ClusterCount { get; private set; }

    /// <summary>
    /// 深さから切り身の番号を出すための係数。<c>slice = log(深さ / near) × これ</c>。
    /// シェーダの <c>uClusterSliceScale</c> と同じもの。
    /// </summary>
    public float SliceScale => SliceCount / MathF.Log(Far / Near);

    /// <summary>
    /// 番号の並びに入れてよい数の上限(テクスチャバッファの上限。<see cref="BufferTexture.MaxTexels"/>)。
    /// 超えたぶんは捨てて <see cref="Overflowed"/> を立てる——光が欠けるが、落ちはしない。
    /// </summary>
    public int IndexLimit { get; set; } = int.MaxValue;

    /// <summary>最後に振り分けた光の数。</summary>
    public int LightCount { get; private set; }

    /// <summary>番号の並びの長さ(= 振り分けた (クラスター, 光) の組の数)。</summary>
    public int PairCount { get; private set; }

    /// <summary>粗い箱(1段目)が候補に挙げた組の数。2段目でここから <see cref="PairCount"/> まで減る。</summary>
    public int CandidateCount { get; private set; }

    /// <summary>1クラスターにいちばん多く入った光の数。**画素シェーダがいちばん長く回るところ**。</summary>
    public int MaxPerCluster { get; private set; }

    /// <summary>光が1つ以上入ったクラスターの数。</summary>
    public int NonEmptyCount { get; private set; }

    /// <summary>光の入ったクラスター1つあたりの光の数(平均)。</summary>
    public float AveragePerCluster => NonEmptyCount == 0 ? 0.0f : (float)PairCount / NonEmptyCount;

    /// <summary>上限(<see cref="IndexLimit"/>)に当たって組を捨てたか。</summary>
    public bool Overflowed { get; private set; }

    /// <summary>クラスターごとの (始まり, 数) を並べたもの。長さはクラスターの数 × 2。</summary>
    public ReadOnlySpan<uint> Grid => _grid.AsSpan(0, ClusterCount * 2);

    /// <summary>光の番号の並び。</summary>
    public ReadOnlySpan<uint> Indices => _indices.AsSpan(0, PairCount);

    /// <summary>
    /// **升目を決める**。画面の大きさ・射影・つまみが前と同じなら何もしない(false を返す)。
    ///
    /// <para>
    /// 平行投影には対応していない(タイルの側面が原点を通らず、箱の式が変わる)。
    /// 呼ぶ側(<c>Program.ActivePath</c>)が平行投影のときはフォワードに戻す。
    /// </para>
    /// </summary>
    /// <param name="tileSize">タイル1枚の大きさ [画素]。</param>
    /// <param name="sliceCount">奥行きの切り身の数。1 ならタイルだけ。</param>
    public bool Configure(int width, int height, int tileSize, int sliceCount, Camera camera)
    {
        Matrix4x4 projection = camera.ProjectionMatrix;
        var key = (
            Math.Max(1, width),
            Math.Max(1, height),
            Math.Max(1, tileSize),
            Math.Max(1, sliceCount),
            camera.NearPlane,
            camera.FarPlane,
            projection.M11,
            projection.M22);

        if (key == _key && _boundsMin.Length > 0)
        {
            return false;
        }

        _key = key;

        // **端の半端なタイルも1枚に数える**(切り上げ)。1000 画素を 64 で切ると 15.6 → 16 枚。
        // 切り捨てると右端の 40 画素がどのタイルにも属さず、そこだけ光が消える。
        TilesX = (Width + _key.Tile - 1) / _key.Tile;
        TilesY = (Height + _key.Tile - 1) / _key.Tile;
        ClusterCount = TilesX * TilesY * _key.Slices;

        Array.Resize(ref _boundsMin, ClusterCount);
        Array.Resize(ref _boundsMax, ClusterCount);
        Array.Resize(ref _grid, ClusterCount * 2);
        Array.Resize(ref _counts, ClusterCount);
        Array.Resize(ref _cursor, ClusterCount);
        Array.Clear(_grid);

        for (int slice = 0; slice < _key.Slices; slice++)
        {
            for (int y = 0; y < TilesY; y++)
            {
                for (int x = 0; x < TilesX; x++)
                {
                    int cluster = IndexOf(x, y, slice);
                    (_boundsMin[cluster], _boundsMax[cluster]) = ComputeBounds(x, y, slice);
                }
            }
        }

        PairCount = 0;
        LightCount = 0;
        return true;
    }

    /// <summary>
    /// **k 枚目の切れ目の深さ**。<c>near × (far / near)^(k / 枚数)</c>。k = 0 が near、k = 枚数 が far。
    /// 隣どうしの比がいつも同じ(等比)になる。
    /// </summary>
    public float SliceDepth(int k) => Near * MathF.Pow(Far / Near, (float)k / _key.Slices);

    /// <summary>
    /// **深さから切り身の番号**。<see cref="SliceDepth"/> の逆関数を切り捨てたもの。
    /// <c>textured.frag</c> の <c>ClusterCoord</c> と同じ式(自己チェックが文字列を突き合わせる)。
    /// near より手前は 0、far より奥は最後の1枚に収める。
    /// </summary>
    public int SliceOf(float depth) =>
        Math.Clamp((int)(MathF.Log(depth / Near) * SliceScale), 0, _key.Slices - 1);

    /// <summary>(タイル x, タイル y, 切り身) → クラスターの通し番号。x がいちばん速く回る。</summary>
    public int IndexOf(int x, int y, int slice) => x + (TilesX * (y + (TilesY * slice)));

    /// <summary>
    /// **画素と深さから、どのクラスターか**。<c>textured.frag</c> の <c>ClusterCoord</c> を CPU で書いたもの。
    /// <paramref name="fragCoord"/> は <c>gl_FragCoord.xy</c> と同じく<b>画素の中心</b>(+0.5)を渡すこと。
    /// </summary>
    public (int X, int Y, int Slice) CoordOf(Vector2 fragCoord, float depth) => (
        Math.Clamp((int)(fragCoord.X / _key.Tile), 0, TilesX - 1),
        Math.Clamp((int)(fragCoord.Y / _key.Tile), 0, TilesY - 1),
        SliceOf(depth));

    /// <summary>クラスターの箱(ビュー空間の x・y と深さ)。</summary>
    public (Vector3 Min, Vector3 Max) Bounds(int cluster) => (_boundsMin[cluster], _boundsMax[cluster]);

    /// <summary>そのクラスターに振り分けた光の番号。**番号の小さい順**に並んでいる。</summary>
    public ReadOnlySpan<uint> LightsIn(int cluster) =>
        _indices.AsSpan((int)_grid[cluster * 2], (int)_grid[(cluster * 2) + 1]);

    /// <summary>
    /// **切り身の形**。その切り身のクラスターの「厚み ÷ 幅」(幅は手前の面で測る)。
    ///
    /// <para>
    /// タイルの幅は NDC で一定なので、世界での幅は深さに比例する。
    /// 等比に切れば厚みも深さに比例するので、<b>どの切り身でも同じ値になる</b>——
    /// 等比に切る理由がそのまま数字に出る(自己チェックと内訳が使う)。
    /// </para>
    /// </summary>
    public float SliceAspect(int slice)
    {
        float near = SliceDepth(slice);
        float thickness = SliceDepth(slice + 1) - near;
        float width = 2.0f * _key.Tile / Width / _key.ScaleX * near;

        return thickness / width;
    }

    /// <summary>
    /// **光を升目に振り分ける**。毎フレーム呼ぶ(光もカメラも動くので)。
    ///
    /// <para>
    /// 光1つずつ「どのクラスターにかかるか」を2段で決める。
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>粗い箱</b>: 光の球を切り身ごとに箱で包み、画面へ写してタイルの範囲を出す。
    /// 1024 個の光 × 2400 クラスターを総当たりすると 250 万回になるが、
    /// ここで<b>候補を光の近くのクラスターだけ</b>に絞る
    /// </item>
    /// <item>
    /// <b>クラスターの箱</b>: 候補のクラスター1つずつ、球と箱が交わるかを確かめる。
    /// 1段目の箱は球を四角く包むので、角のクラスターは実際には球にかかっていないことがある
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <b>どちらの段も「かかっていないのに、かかっている」と答えることはあっても、逆は無い</b>
    /// (DeferredLighting.IsVisible と同じ性質)。余分に入れた光は画素シェーダで 0 を足すだけで済むが、
    /// 入れ忘れた光は<b>タイルの形に四角く欠ける</b>。自己チェックはこちら側(見落とし 0)を見る。
    /// </para>
    /// </summary>
    /// <param name="view">ビュー行列。光をビュー空間へ運ぶのに使う。</param>
    public void Assign(in Matrix4x4 view, ReadOnlySpan<PointLight> lights)
    {
        LightCount = lights.Length;
        CandidateCount = 0;
        Overflowed = false;

        int pairs = 0;
        Array.Clear(_counts, 0, ClusterCount);

        for (int i = 0; i < lights.Length; i++)
        {
            // ビュー空間へ。System.Numerics は行ベクトル(v * M)なので Transform がそのまま使える。
            Vector3 v = Vector3.Transform(lights[i].Position, view);
            AssignLight(i, new Vector3(v.X, v.Y, -v.Z), lights[i].Radius, ref pairs);
        }

        // --- 数え上げてから並べる(計数ソート)---
        //
        // 見つけた組は「光の順」に並んでいるが、GPU は「クラスターの順」に引きたい。
        // 各クラスターの数が分かれば始まりの位置は足し算で決まるので、並べ替えは1往復で済む。
        // 光の順に書き込んでいくので、**クラスターの中は光の番号の小さい順**になる——
        // フォワードのループと同じ順に足すことになり、足し算の順番まで Day 52 と揃う。
        uint running = 0;
        MaxPerCluster = 0;
        NonEmptyCount = 0;

        for (int c = 0; c < ClusterCount; c++)
        {
            uint count = _counts[c];
            _grid[c * 2] = running;
            _grid[(c * 2) + 1] = count;
            _cursor[c] = running;
            running += count;

            MaxPerCluster = Math.Max(MaxPerCluster, (int)count);
            NonEmptyCount += count > 0 ? 1 : 0;
        }

        if (_indices.Length < pairs)
        {
            Array.Resize(ref _indices, Math.Max(pairs, _indices.Length * 2));
        }

        for (int p = 0; p < pairs; p++)
        {
            _indices[_cursor[_pairClusters[p]]++] = (uint)_pairLights[p];
        }

        PairCount = pairs;
    }

    /// <summary>
    /// **光1つを振り分ける**。<paramref name="center"/> はビュー空間の x・y と深さ(= -z)。
    /// </summary>
    private void AssignLight(int light, Vector3 center, float radius, ref int pairs)
    {
        float depth = center.Z;

        // 近クリップ面より丸ごと手前、または遠クリップ面より丸ごと奥なら、どのクラスターにもかからない。
        // **カメラの背後の光もここで落ちる**(深さが負)。
        if (depth + radius < Near || depth - radius > Far)
        {
            return;
        }

        float nearEdge = MathF.Max(depth - radius, Near);
        float farEdge = MathF.Min(depth + radius, Far);
        float radiusSquared = radius * radius;

        int firstSlice = SliceOf(nearEdge);
        int lastSlice = SliceOf(farEdge);

        // 切れ目ちょうどの深さは、log の丸めで隣の切り身に転ぶことがある。
        // **1枚広めに見ておけば取りこぼさない**(余分な1枚は2段目で落ちる)。
        if (firstSlice > 0 && SliceDepth(firstSlice) > nearEdge)
        {
            firstSlice--;
        }

        if (lastSlice < _key.Slices - 1 && SliceDepth(lastSlice + 1) < farEdge)
        {
            lastSlice++;
        }

        for (int slice = firstSlice; slice <= lastSlice; slice++)
        {
            // --- 1段目: この切り身の中で、球を箱で包む ---
            //
            // 切り身は深さ [lo, hi] の厚い板。球をこの板で切った断面の半径は、
            // 球の中心の深さが板の中にあれば球の半径そのもの、外にあれば近い側の面での断面になる。
            float lo = MathF.Max(SliceDepth(slice), nearEdge);
            float hi = MathF.Min(SliceDepth(slice + 1), farEdge);

            float gap = depth < lo ? lo - depth : depth > hi ? depth - hi : 0.0f;
            float sectionRadius = MathF.Sqrt(MathF.Max(radiusSquared - (gap * gap), 0.0f));

            // 断面の円を x・y の箱にして画面へ写す。**深さが板の中で変わる**ので、
            // 画面の左端は「x が負なら手前(lo)で割る、正なら奥(hi)で割る」ほうが外側になる。
            // lo は near 以上なので、ここで 0 や負で割ることは無い——
            // 近クリップ面をまたぐ球でも破綻しないのが、切り身ごとに包む形の取り柄。
            float left = ProjectMin(center.X - sectionRadius, lo, hi) * _key.ScaleX;
            float right = ProjectMax(center.X + sectionRadius, lo, hi) * _key.ScaleX;
            float bottom = ProjectMin(center.Y - sectionRadius, lo, hi) * _key.ScaleY;
            float top = ProjectMax(center.Y + sectionRadius, lo, hi) * _key.ScaleY;

            // この切り身の中では画面の外。
            if (right < -1.0f || left > 1.0f || top < -1.0f || bottom > 1.0f)
            {
                continue;
            }

            int x0 = TileOf(left, Width, TilesX);
            int x1 = TileOf(right, Width, TilesX);
            int y0 = TileOf(bottom, Height, TilesY);
            int y1 = TileOf(top, Height, TilesY);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    CandidateCount++;
                    int cluster = IndexOf(x, y, slice);

                    // --- 2段目: クラスターの箱と球が交わるか ---
                    if (DistanceSquaredToBox(center, _boundsMin[cluster], _boundsMax[cluster]) > radiusSquared)
                    {
                        continue;
                    }

                    if (pairs >= IndexLimit)
                    {
                        Overflowed = true;
                        return;
                    }

                    if (pairs == _pairClusters.Length)
                    {
                        Array.Resize(ref _pairClusters, pairs * 2);
                        Array.Resize(ref _pairLights, pairs * 2);
                    }

                    _pairClusters[pairs] = cluster;
                    _pairLights[pairs] = light;
                    pairs++;
                    _counts[cluster]++;
                }
            }
        }
    }

    /// <summary>
    /// **クラスターの箱**を作る。タイルの4つの側面は目(原点)から放射状に広がるので、
    /// 切り身の手前と奥の面で幅が違う。両方の角を入れた箱にする。
    ///
    /// <para>
    /// 箱はクラスター(角錐台)より少し大きい。<b>大きいぶんには困らない</b>(余分に入った光は 0 を足すだけ)。
    /// 小さいと光が欠けるので、角錐台を必ず含む形にしてある。
    /// </para>
    /// </summary>
    private (Vector3 Min, Vector3 Max) ComputeBounds(int x, int y, int slice)
    {
        // タイルの縁を NDC(-1〜1)で。右端の半端なタイルは画面の外まで伸びるが、そのままでよい。
        float left = ((x * _key.Tile / (float)Width) * 2.0f) - 1.0f;
        float right = (((x + 1) * _key.Tile / (float)Width) * 2.0f) - 1.0f;
        float bottom = ((y * _key.Tile / (float)Height) * 2.0f) - 1.0f;
        float top = (((y + 1) * _key.Tile / (float)Height) * 2.0f) - 1.0f;

        float near = SliceDepth(slice);
        float far = SliceDepth(slice + 1);

        // ビュー空間では x = NDC の x / M11 × 深さ(透視除算の逆)。
        float minX = MathF.Min(left / _key.ScaleX * near, left / _key.ScaleX * far);
        float maxX = MathF.Max(right / _key.ScaleX * near, right / _key.ScaleX * far);
        float minY = MathF.Min(bottom / _key.ScaleY * near, bottom / _key.ScaleY * far);
        float maxY = MathF.Max(top / _key.ScaleY * near, top / _key.ScaleY * far);

        return (new Vector3(minX, minY, near), new Vector3(maxX, maxY, far));
    }

    /// <summary>深さが [lo, hi] で変わるとき、<c>value / 深さ</c> のいちばん小さい値。</summary>
    private static float ProjectMin(float value, float lo, float hi) => value >= 0.0f ? value / hi : value / lo;

    /// <summary>深さが [lo, hi] で変わるとき、<c>value / 深さ</c> のいちばん大きい値。</summary>
    private static float ProjectMax(float value, float lo, float hi) => value >= 0.0f ? value / lo : value / hi;

    /// <summary>NDC の座標が入るタイルの番号(画面の外は端のタイルに収める)。</summary>
    private int TileOf(float ndc, int pixels, int tiles)
    {
        float window = ((ndc * 0.5f) + 0.5f) * pixels;
        return Math.Clamp((int)MathF.Floor(window / _key.Tile), 0, tiles - 1);
    }

    /// <summary>点から箱までの距離の2乗(箱の中なら 0)。軸ごとに、はみ出したぶんだけを足す。</summary>
    private static float DistanceSquaredToBox(Vector3 point, Vector3 min, Vector3 max)
    {
        Vector3 outside = Vector3.Max(min - point, Vector3.Zero) + Vector3.Max(point - max, Vector3.Zero);
        return outside.LengthSquared();
    }
}
