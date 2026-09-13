using System.Numerics;
using System.Runtime.InteropServices;

namespace HonyaEngine;

/// <summary>
/// 積分のやり方。**順番が違うだけ**なのに、結果はまるで違う(Day 43 の要点1)。
/// </summary>
internal enum IntegratorMode
{
    /// <summary>速度を先に進めてから位置を進める。**ゲームの標準**。</summary>
    SemiImplicit,

    /// <summary>位置を先に進めてから速度を進める。**素朴だが発散する**。</summary>
    Explicit,
}

/// <summary>
/// 候補の組をどう作るか(Day 46 の要点5)。**答えは同じで、速さだけが違う**。
///
/// Day 43 から今日まで、判定は全部の組を試す総当たりだった。
/// 体が数十個のうちは平気でも、n² は 100 個で 4,950 組、
/// 1,000 個で 50 万組——<b>体を増やした瞬間に壁に当たる</b>。
///
/// <para>
/// つまみとして残してあるのは、Day 43 の <see cref="IntegratorMode"/> と同じ理由。
/// <b>「正しいが遅いもの」を隣に置いておく</b>と、
/// 速いほうが本当に同じ答えを出しているかを実際に突き合わせられる。
/// 今日の自己チェックは、まさにそれを 200 体でやっている。
/// </para>
/// </summary>
internal enum BroadphaseMode
{
    /// <summary>全部の組を試す。**Day 43〜45 のやり方**。n(n-1)/2 組。</summary>
    BruteForce,

    /// <summary>均一グリッドで絞る(<see cref="SpatialGrid3D"/>)。**Day 26 の 3D 版**。</summary>
    UniformGrid,
}

/// <summary>
/// 1つの接触点。**判定の結果 + 解決に必要な下ごしらえ**。
///
/// <para>
/// <b>Day 43 から2つ変わった</b>。
/// </para>
///
/// <list type="number">
/// <item>
/// <b><c>Plane</c> と <c>B = -1</c> が消えた</b>。
/// 平面も剛体(<see cref="RigidBody.CreatePlane"/>)になったので、
/// 相手は必ず体の番号で指せる。Day 43 の設計書で
/// 「Day 44 で箱を足すときに片付く」と書いた歪みがこれ。
/// </item>
/// <item>
/// <b><see cref="LocalA"/> / <see cref="LocalB"/> / <see cref="Separation"/> が増えた</b>。
/// 位置の補正でめり込みを測り直すのに使う(要点6)。
/// Day 43 は判定関数をもう一度呼んで測り直していたが、
/// あれは「形が球しか無い」ことに寄りかかった書き方だった。
/// </item>
/// </list>
/// </summary>
internal struct ContactPoint
{
    public readonly int A;
    public readonly int B;

    /// <summary>押し戻す向き。**A を B から引き離す向き**で長さ1。</summary>
    public readonly Vector3 Normal;

    /// <summary>
    /// 接線その1(Day 47 の要点3)。**法線と直交する2本のうち片方**。
    ///
    /// 摩擦は「接触面に沿った向き」に掛かる。面は2次元なので、
    /// 向きを表すには直交する2本が要る——法線1本だけでは足りない。
    /// この2本の取り方は<b>面の中で好きに回してよい</b>(要点3)。
    /// </summary>
    public readonly Vector3 Tangent1;

    /// <summary>接線その2。<c>Normal x Tangent1</c>。</summary>
    public readonly Vector3 Tangent2;

    /// <summary>接触点(世界座標)。速度の解決はこの点で相対速度を測る。</summary>
    public readonly Vector3 Point;

    /// <summary>
    /// 接触点を **A の物体座標で**表したもの。**位置の補正で測り直すのに使う**(Day 44 の要点6)。
    ///
    /// 体に画鋲で留めた印だと思えばよい。体が動けば印も一緒に動くので、
    /// 「印どうしがどれだけ離れたか」を見れば、判定をやり直さずに
    /// 今のめり込みが分かる。
    ///
    /// <para>
    /// <b>Day 47 で2つ目の役目が付いた</b>。前のステップの接触点と
    /// <b>同じ点かどうかを照合する鍵</b>にもなる(<see cref="ContactCache"/>)。
    /// 「体に貼り付けた印」という性格が、そのまま持ち越しの鍵として使える。
    /// </para>
    /// </summary>
    public readonly Vector3 LocalA;

    /// <summary>接触点を B の物体座標で表したもの。</summary>
    public readonly Vector3 LocalB;

    /// <summary>
    /// 接触を作った時点の隙間 [m]。**めり込んでいれば負**。
    ///
    /// 深さ(正の値)ではなく隙間(負の値)で持つのは、
    /// 位置の補正の式が「今の隙間 = 最初の隙間 + 印どうしのずれ」という
    /// 足し算1本になるから。
    /// </summary>
    public readonly float Separation;

    /// <summary>
    /// 目標とする離れる速度 [m/s]。**接触を作った時点の相対速度から1回だけ決める**。
    ///
    /// 反復のたびに <c>-e·vn</c> を計算し直すと、
    /// 跳ね返りが反復回数ぶん重ね掛けされて<b>球が天井まで飛ぶ</b>。
    /// 目標値は最初に決めて固定するのが定石(Day 43 の要点7)。
    /// </summary>
    public readonly float Bounce;

    /// <summary>
    /// この接触の摩擦係数 μ(Day 47)。**2つの体の相乗平均**。
    ///
    /// 接触ごとに持つのは、同じ体でも相手によって滑りやすさが違うから——
    /// 氷の上のゴムと、紙やすりの上のゴムは別の μ になる。
    /// </summary>
    public readonly float Friction;

    // ===== ここから下は解いている間に書き換わる(Day 47)=====

    /// <summary>
    /// 法線方向の実効質量の逆数(Day 47 の要点1)。**下ごしらえで1回だけ計算する**。
    ///
    /// Day 46 までは反復のたびに <c>AngularTerm</c> を2回計算していた。
    /// 反復が8周なら8回、点が4つなら32回——
    /// <b>体の姿勢は反復の間に変わらない</b>ので、全部同じ値が出ていた。
    /// 先に1回だけ計算して持っておくのが Sequential Impulses の書き方。
    /// </summary>
    public float NormalMass;

    /// <summary>接線1方向の実効質量の逆数。</summary>
    public float TangentMass1;

    /// <summary>接線2方向の実効質量の逆数。</summary>
    public float TangentMass2;

    /// <summary>
    /// 法線方向に**溜まった**インパルス [N·s](Day 47 の要点1)。
    ///
    /// <b>これが今日いちばん大事な1つのフィールド</b>。
    /// Day 46 までは1周ごとに「今掛けるぶん」を計算して、
    /// それが負なら捨てていた。今日は<b>合計を持ち、合計が負にならないように</b>する——
    /// 途中の周では負の補正(掛けすぎたぶんを戻す)が許される。
    /// これだけで反復が正しい答えへ収束するようになる。
    /// </summary>
    public float NormalImpulse;

    /// <summary>接線1方向に溜まったインパルス。**符号は両向きに振れる**。</summary>
    public float TangentImpulse1;

    /// <summary>接線2方向に溜まったインパルス。</summary>
    public float TangentImpulse2;

    /// <summary>前のステップから持ち越したか(Day 47)。**HUD と自己チェック用**。</summary>
    public bool WarmStarted;

    public ContactPoint(
        int a,
        int b,
        Vector3 normal,
        Vector3 point,
        Vector3 localA,
        Vector3 localB,
        float separation,
        float bounce,
        float friction)
    {
        A = a;
        B = b;
        Normal = normal;
        Point = point;
        LocalA = localA;
        LocalB = localB;
        Separation = separation;
        Bounce = bounce;
        Friction = friction;

        BuildTangents(normal, out Tangent1, out Tangent2);
    }

    /// <summary>
    /// 法線と直交する2本を作る(Day 47 の要点3)。**どの向きでもよい**。
    ///
    /// 摩擦の上限は接線ベクトルの<b>合成した長さ</b>で決めるので(摩擦円錐)、
    /// 2本をどう回して取っても結果は変わらない。
    /// だから「とにかく法線と直交する1本」を安く作れればよい。
    ///
    /// <para>
    /// 安く作るときの罠は<b>選んだ軸が法線と平行になる場合</b>で、
    /// 外積が 0 ベクトルになって正規化が NaN を返す。
    /// <b>法線の成分がいちばん小さい軸</b>を選べば、
    /// 平行になりようがない——法線は長さ1なので、
    /// 3成分のうち最小のものは必ず 1/√3 より小さい。
    /// </para>
    /// </summary>
    public static void BuildTangents(Vector3 normal, out Vector3 tangent1, out Vector3 tangent2)
    {
        Vector3 axis = MathF.Abs(normal.X) < 0.57735f
            ? Vector3.UnitX
            : MathF.Abs(normal.Y) < 0.57735f ? Vector3.UnitY : Vector3.UnitZ;

        tangent1 = Vector3.Normalize(Vector3.Cross(normal, axis));
        tangent2 = Vector3.Cross(normal, tangent1);
    }
}

/// <summary>
/// 剛体の世界。**1ステップを5段で進める**。
///
/// <list type="number">
/// <item>速度の積分 … 力と重力から速度を進める(<see cref="IntegratorMode"/>)</item>
/// <item>判定 … 候補を作って当てる(<see cref="BroadphaseMode"/>)</item>
/// <item>下ごしらえ … 実効質量を先に出し、前のステップのインパルスを掛け直す(Day 47)</item>
/// <item>速度の解決 … 摩擦 → 法線の順に、インパルスを溜めながら何周もする</item>
/// <item>位置の積分 … <b>解いたあとの</b>速度で位置を進める(Day 47 で順番が変わった)</item>
/// <item>位置の補正 … 残っためり込みを押し戻す</item>
/// <item>持ち越し … 溜まったインパルスをキャッシュへ書き戻す(Day 47)</item>
/// <item>眠り … 島ごとに、止まっている塊を計算から外す(Day 47)</item>
/// <item>後始末 … 力の溜め場を空にする</item>
/// </list>
///
/// <para>
/// <b>Day 44 で構造が2つ片付いた</b>。
/// 平面が剛体になったので<b>体のリスト1本</b>になり、
/// 解決から <c>b?.</c> という書き方が全部消えた。
/// そして判定が1点ではなく<b>マニフォールド</b>(最大4点)を返すようになった——
/// 5段の順番はそのままで、2段目が返すものの形だけが変わった格好になる。
/// </para>
///
/// <para>
/// <b>3 と 4 が分かれている</b>のが引っかかりどころ。
/// 速度を直したのに位置がめり込んだままなのは、
/// インパルスは「これ以上めり込まない」ようにするだけで、
/// <b>既にめり込んだぶんは戻さない</b>から。
/// 位置を直接動かすのは物理的にはインチキだが、
/// これをやらないと重力で少しずつ沈み続ける(Day 43 の要点8)。
/// </para>
///
/// <para>
/// <b>Day 46 で候補の作り方が2通りになった</b>(<see cref="BroadphaseMode"/>)。
/// 総当たりなら体が N 個で N(N-1)/2 組。均一グリッドなら、
/// 近くにあるものだけが組になる。<b>答えは同じで、速さだけが違う</b>。
/// </para>
///
/// <para>
/// <b>Day 45 で「進める」以外の入口が1つ増えた</b>——
/// <see cref="QueryCapsule"/> と <see cref="OverlapCapsule"/>。
/// シミュレーションを1歩進めるのではなく、
/// <b>今この形を置いたら何に当たるか</b>を問うだけのもので、
/// <see cref="CharacterController"/> がこれだけを使って動く。
/// 世界の側は「キャラクター」という言葉を知らないままでいられる。
/// </para>
///
/// <para>
/// <b>Day 47 で段が5つから8つに増えた</b>。判定と解決の間に<b>下ごしらえ</b>が入り、
/// 最後に<b>インパルスの持ち越し</b>と<b>眠りの更新</b>が付いた。
/// 増えた3段はどれも「解く」ことそのものではなく、
/// <b>反復を良い初期値から始めるため</b>と<b>止まったものを計算から外すため</b>にある——
/// Sequential Impulses が反復法だという性格が、そのまま段の形に出ている。
/// </para>
///
/// <para>
/// <b>これでミニ物理エンジンが一通り揃う</b>。
/// 積分(Day 43)・形と判定(Day 44〜46)・ブロードフェーズ(Day 46)・
/// 摩擦と収束と眠り(Day 47)。
/// この先は Day 48 が入力の整理、Day 49〜50 がエフェクトなので、物理の本体に手を入れるのは今日が最後になる。
/// </para>
/// </summary>
internal sealed class PhysicsWorld
{
    /// <summary>
    /// 1回の問い合わせで格子から受け取る候補の上限(Day 46)。
    ///
    /// **足りないと取りこぼす**ので、キャラクターの足元に来うる数より
    /// 十分多く取る。64 なら、1マスに 60 個ひしめいていても届く。
    /// <c>stackalloc</c> なので割り当ては起きない。
    /// </summary>
    private const int MaxQueryCandidates = 64;

    /// <summary>
    /// 問い合わせの箱を広げる余裕 [m](Day 46)。
    ///
    /// 格子を組んだのは <see cref="Step"/> の判定の段で、
    /// そのあと位置の補正が体を数ミリ動かしている。
    /// **ブロードフェーズは取りこぼしが許されない**ので、
    /// ずれうるぶんだけ広げてから問い合わせる。
    /// </summary>
    private const float QueryMargin = 0.05f;

    private readonly List<RigidBody> _bodies = [];
    private readonly List<ContactPoint> _contacts = [];

    /// <summary>
    /// マニフォールドごとの範囲(<see cref="_contacts"/> の開始位置と点の数)。
    ///
    /// **点をまとめて解くために要る**(要点7)。
    /// 接触点は組ごとに続けて並んでいるので、区切りさえ覚えておけば
    /// 「この4点は同じ組」と分かる。
    /// </summary>
    private readonly List<(int Start, int Count)> _manifolds = [];

    /// <summary>接触の出どころ別の数(<see cref="ManifoldSource"/> の添字)。HUD 用。</summary>
    private readonly int[] _sourceCounts = new int[5];

    /// <summary>
    /// ブロードフェーズの格子(Day 46)。**世界が1つ持っていればよい**。
    ///
    /// 毎ステップ組み直すが、配列は使い回すので割り当てが起きない。
    /// <see cref="Step"/> と <see cref="QueryCapsule"/> が<b>同じ格子を使い回す</b>——
    /// 1回組んだものを2通りに使うのは Day 26 の卒業制作と同じ形。
    /// </summary>
    private readonly SpatialGrid3D _grid = new();

    /// <summary>体ごとの外接 AABB(Day 46)。**ステップの頭で1回だけ作る**。</summary>
    private Aabb3D[] _bounds = [];

    /// <summary>
    /// 今のステップで格子を組んだか(Day 46)。
    ///
    /// <see cref="QueryCapsule"/> は <see cref="Step"/> とは別の入口なので、
    /// **格子がまだ無い状態で呼ばれうる**(総当たりに切り替えている、
    /// 一時停止している、シーンを組み直した直後)。
    /// そのときは黙って総当たりに落ちる——
    /// <b>速さのための仕掛けが、正しさの前提になってはいけない</b>。
    /// </summary>
    private bool _gridReady;

    /// <summary>
    /// 前のステップのインパルスを覚えておく辞書(Day 47 の要点2)。
    ///
    /// **世界が1つ持つ**。組ごとに分けて持たせる案もあるが、
    /// 組は毎ステップ生まれては消えるので、置き場所が無い。
    /// </summary>
    private readonly ContactCache _cache = new();

    /// <summary>接触で繋がった塊(島)を作るもの(Day 47 の要点5)。**眠りの判定にだけ使う**。</summary>
    private readonly IslandBuilder _islands = new();

    /// <summary>
    /// 島ごとの「いちばん眠りが浅い体の時計」[s]。**添字は島の代表の体番号**。
    ///
    /// 体の数ぶん取ってあるが、実際に使うのは代表の席だけ。
    /// 島の数は毎ステップ変わるので、**代表の番号で引ける形**にしておくのがいちばん安い。
    /// </summary>
    private float[] _islandSleepTimers = [];

    /// <summary>キャッシュへ書き戻すときの一時置き場。**割り当てを起こさないため**。</summary>
    private readonly CachedPoint[] _cacheScratch = new CachedPoint[ContactManifold.MaxPoints];

    /// <summary>重力加速度 [m/s²]。地球はおよそ 9.81。</summary>
    public Vector3 Gravity { get; set; } = new(0.0f, -9.81f, 0.0f);

    /// <summary>積分のやり方。**既定はセミインプリシット**。</summary>
    public IntegratorMode Integrator { get; set; } = IntegratorMode.SemiImplicit;

    /// <summary>
    /// 速度の解決を何周するか。**1 だと積み上げが持たない**(Day 43 の要点8)。
    ///
    /// 接触は互いに影響し合う——下の箱を押し戻すと上の箱が浮く。
    /// 1周では下から上へ1段ぶんしか情報が伝わらないので、
    /// 段数ぶん回さないと柱が沈む。
    /// **これが Sequential Impulses の素朴版**で、
    /// 蓄積インパルスと温存(warm starting)を足したものが Day 47。
    /// </summary>
    public int VelocityIterations { get; set; } = 8;

    /// <summary>
    /// 1組の接触から採る点の数の上限。**1 にすると箱が落ち着かない**(要点4)。
    ///
    /// 実装の都合ではなく<b>実験のための道具</b>。
    /// 4 が本来の値で、1 にすると Day 43 の「1点だけの接触」に戻る——
    /// 床に置いた箱が1点で支えられ、その点まわりに傾き、
    /// 傾いた先の角が次の最深点になって、いつまでもガタガタと揺れ続ける。
    /// </summary>
    public int MaxContactsPerPair { get; set; } = ContactManifold.MaxPoints;

    /// <summary>
    /// 1組の接触点を**同時に**解くか(要点7)。**切ると柱が崩れる**。
    ///
    /// true(既定)なら、4点ぶんのインパルスを同じ状態から計算してからまとめて掛ける。
    /// false なら Day 43 と同じで、1点ずつ順に解く——
    /// <b>1点目が全部を背負い、その偏りがトルクになって積んだ箱が滑り出す</b>。
    ///
    /// <para>
    /// Day 43 の <see cref="IntegratorMode"/> と同じ性格のつまみで、
    /// <b>「正しい解き方」と「素朴な解き方」を並べて比べるため</b>だけにある。
    /// </para>
    /// </summary>
    public bool SolveContactsTogether { get; set; } = true;

    /// <summary>位置の補正をするか。**切ると沈む**。</summary>
    public bool PositionCorrection { get; set; } = true;

    /// <summary>
    /// 位置の補正を何周するか。**速度と同じで、1周では足りない**。
    ///
    /// 柱の下を押し上げると上が浮き、上を押し下げると下が沈む——
    /// 接触どうしが打ち消し合うので、1周だと平衡が出ない。
    /// 周ごとに<b>今の位置からめり込みを測り直す</b>のが肝で、
    /// 判定した時点の深さを使い回すと、押し戻しすぎて弾け飛ぶ。
    /// </summary>
    public int PositionIterations { get; set; } = 4;

    /// <summary>
    /// 1周で戻すめり込みの割合。1 にすると一気に戻して跳ねる。
    /// 0.2〜0.5 あたりが普通で、数フレームかけてじわりと戻す。
    /// </summary>
    public float CorrectionRate { get; set; } = 0.6f;

    /// <summary>
    /// 許すめり込み [m]。**0 にしてはいけない**。
    ///
    /// ぴったり 0 を目指すと、接触が付いたり消えたりを毎フレーム繰り返して震える。
    /// 5mm 程度の重なりを許しておけば、接触が安定して残る——
    /// <b>箱ではこれがいっそう効く</b>。4点の接触が「3点だけ残る」状態を行き来すると、
    /// 支える点が入れ替わるたびに箱が傾こうとする。
    /// </summary>
    public float Slop { get; set; } = 0.005f;

    /// <summary>
    /// この速さ [m/s] より遅い衝突では跳ね返らせない。
    ///
    /// **これが無いと、置いてある箱が永久に細かく震える**。
    /// 重力は毎ステップ <c>9.81 × dt ≈ 0.16 m/s</c> を足すので、
    /// 床に乗っているだけの物にも常に小さな接近速度がある。
    /// それに反発係数を掛けると、いつまでも跳ね続けてしまう。
    /// </summary>
    public float RestitutionThreshold { get; set; } = 1.0f;

    /// <summary>
    /// 候補の組の作り方(Day 46)。**既定は均一グリッド**。
    ///
    /// <see cref="BroadphaseMode.BruteForce"/> にすると Day 45 までの総当たりに戻る。
    /// <b>絵は1ピクセルも変わらない</b>のが正しい姿で、
    /// 変わるのは <see cref="PairTests"/> と1ステップの時間だけ。
    /// </summary>
    public BroadphaseMode Broadphase { get; set; } = BroadphaseMode.UniformGrid;

    /// <summary>
    /// 格子1マスの一辺 [m](Day 46)。**性能はここでほぼ決まる**(要点7)。
    ///
    /// 小さすぎると1個が大量のマスにまたがり(3D では<b>辺の比の3乗</b>で効く)、
    /// 大きすぎると1マスに大量に入って結局そのマスの中で総当たりになる。
    /// 目安は「物体の平均的な差し渡しの2〜3倍」だが、**最後は測って決める**。
    /// </summary>
    public float CellSize { get; set; } = 2.0f;

    /// <summary>格子が覆う範囲の隅(Day 46)。**外に出たものは端のマスに丸められる**。</summary>
    public Vector3 GridOrigin { get; set; } = new(-32.0f, -8.0f, -32.0f);

    /// <summary>格子が覆う範囲の大きさ [m](Day 46)。</summary>
    public Vector3 GridSize { get; set; } = new(64.0f, 32.0f, 64.0f);

    // ===== Day 47: Sequential Impulses・摩擦・眠り =====

    /// <summary>
    /// インパルスを溜めながら解くか(Day 47 の要点1)。**切ると Day 46 の解き方に戻る**。
    ///
    /// true(既定)なら<b>合計が負にならないように</b>クランプするので、
    /// 途中の周で「掛けすぎたぶんを戻す」ことができる。
    /// false なら1周ごとに計算した値を <c>max(0, ...)</c> で切って掛ける——
    /// <b>戻せないので、掛けすぎたまま次の周へ進む</b>。
    ///
    /// <para>
    /// Day 43 の <see cref="IntegratorMode"/> と同じ性格のつまみ。
    /// 6段の柱で切ると、同じ反復回数でも<b>目に見えて沈む</b>。
    /// </para>
    /// </summary>
    public bool AccumulateImpulses { get; set; } = true;

    /// <summary>
    /// 前のステップのインパルスを初期値に使うか(Day 47 の要点2)。**切ると柱が沈む**。
    ///
    /// 効き目がいちばん分かりやすいのは<b>反復回数を落としたとき</b>。
    /// 反復2周 + 温存ありは、反復8周 + 温存なしとほぼ同じに見える——
    /// <b>良い初期値は反復回数を買える</b>。
    /// </summary>
    public bool WarmStarting { get; set; } = true;

    /// <summary>
    /// 実際に温存が働いているか(Day 47)。**蓄積を切ると温存も止まる**。
    ///
    /// <b>この2つは切り離せない</b>。素朴なクランプ(1周ごとに <c>max(0, λ)</c>)では
    /// 合計を<b>減らせない</b>ので、持ち越した値が大きすぎても直せない。
    /// しかも1ステップで溜まる量は必ず 0 以上なので、
    /// <b>持ち越しを続けると単調に増え続ける</b>——
    /// 置いてあるだけの箱が、数秒後に突然跳ね上がる。
    ///
    /// <para>
    /// Erin Catto の資料で蓄積クランプと温存がいつも一組で説明されるのは、
    /// <b>片方だけでは成り立たないから</b>。
    /// つまみは2つ用意してあるが、実際に効く組み合わせは3通りしかない。
    /// </para>
    /// </summary>
    public bool WarmStartActive => WarmStarting && AccumulateImpulses;

    /// <summary>
    /// 摩擦を掛けるか(Day 47 の要点3)。**切ると Day 46 までの氷の世界**。
    ///
    /// 切った瞬間に、坂に乗っていた箱が全部滑り出す。
    /// Day 43 から「見えない壁で囲っていた」のは、これが無かったからだった。
    /// </summary>
    public bool FrictionEnabled { get; set; } = true;

    /// <summary>
    /// 摩擦係数を上書きする値。**負なら体ごとの値を使う**(既定)。
    ///
    /// 0 以上を入れると全部の接触がその μ になる——
    /// 「今の場面で μ を 0.1 にしたらどうなるか」を1キーで試すための実験用。
    /// </summary>
    public float FrictionOverride { get; set; } = -1.0f;

    /// <summary>
    /// 眠りを使うか(Day 47 の要点5)。**切ると全部起きる**。
    ///
    /// 切った瞬間に眠っていた体が全部起きるので、
    /// <b>眠っていた体がどれだけあったか</b>が1ステップの時間の差で分かる。
    /// </summary>
    public bool SleepEnabled { get; set; } = true;

    /// <summary>
    /// 眠ってよい速さの上限 [m/s]。**これより遅い状態が続いたら眠る**。
    ///
    /// 小さすぎると永久に眠れない(数値誤差でこの程度は常に動く)。
    /// 大きすぎると<b>ゆっくり滑っている物が止まる</b>——
    /// 傾きの緩い坂を滑り降りている箱が、途中で凍り付いたように止まって見える。
    /// </summary>
    public float SleepLinearThreshold { get; set; } = 0.05f;

    /// <summary>眠ってよい角速度の上限 [rad/s]。**転がっている球を止めないように**。</summary>
    public float SleepAngularThreshold { get; set; } = 0.10f;

    /// <summary>
    /// 眠るまでに要る時間 [s]。**短いと動いている物が止まる**。
    ///
    /// 跳ね返りの頂点では速度が一瞬 0 になる。
    /// そこで眠らせてしまうと<b>ボールが空中で止まる</b>ので、
    /// 「しばらく遅いままだったこと」を条件にする。
    /// </summary>
    public float SleepTime { get; set; } = 0.5f;

    /// <summary>ブロードフェーズの格子(**読むだけ**。可視化と自己チェック用)。</summary>
    public SpatialGrid3D Grid => _grid;

    public IReadOnlyList<RigidBody> Bodies => _bodies;

    public IReadOnlyList<ContactPoint> Contacts => _contacts;

    /// <summary>
    /// 直前のステップでナローフェーズを呼んだ組の数。
    ///
    /// Day 45 までは「総当たりの組の数」とほぼ同じ意味だった。
    /// **今日から、これはブロードフェーズを通り抜けた候補の数**になる——
    /// <see cref="BruteForcePairs"/> と並べて見ると、絞れた割合が分かる。
    /// </summary>
    public long PairTests { get; private set; }

    /// <summary>
    /// 総当たりなら試すことになる組の数(Day 46)。**比べるための数字**。
    ///
    /// 動かないものどうしを除いた n(n-1)/2 相当。
    /// HUD に <see cref="PairTests"/> と並べて出すと、
    /// <b>「200 体で 19,900 組 → 480 組」</b>のような減り方が数字で見える。
    /// </summary>
    public long BruteForcePairs { get; private set; }

    /// <summary>直前のステップで当たっていた組の数。**接触点の数とは別**。</summary>
    public int ContactPairs => _manifolds.Count;

    /// <summary>直前のステップで残っていた最大のめり込み [m]。**沈んでいるかの目安**。</summary>
    public float MaxPenetration { get; private set; }

    /// <summary>前のステップからインパルスを引き継げた点の数(Day 47)。**温存が効いている証拠**。</summary>
    public int WarmStartedPoints => _cache.MatchedPoints;

    /// <summary>今のステップで新しく生まれた接触点の数(Day 47)。</summary>
    public int NewContactPoints => _cache.NewPoints;

    /// <summary>キャッシュが覚えている組の数(Day 47)。**増え続けていたら <c>Prune</c> の疑い**。</summary>
    public int CachedPairs => _cache.PairCount;

    /// <summary>接触で繋がった塊の数(Day 47 の要点5)。**静的な体は数に入らない**。</summary>
    public int IslandCount { get; private set; }

    /// <summary>
    /// 眠っている体の数(Day 47)。**動く体のうち何個が計算から外れているか**。
    ///
    /// <see cref="DynamicCount"/> と並べて見ると、
    /// 「60 個のうち 57 個が眠っている」のような状態が数字で分かる。
    /// </summary>
    public int SleepingCount
    {
        get
        {
            int count = 0;
            foreach (RigidBody body in _bodies)
            {
                if (body.IsSleeping)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>動く体の数(逆質量が 0 でないもの)。</summary>
    public int DynamicCount
    {
        get
        {
            int count = 0;
            foreach (RigidBody body in _bodies)
            {
                if (!body.IsStatic)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>平面の形を持った体の数。**Day 43 の <c>Planes</c> の後身**。</summary>
    public int PlaneCount
    {
        get
        {
            int count = 0;
            foreach (RigidBody body in _bodies)
            {
                if (body.Shape.Kind == ColliderKind.Plane)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>地形の形を持った体の数(Day 46)。**普通は 0 か 1**。</summary>
    public int TerrainCount
    {
        get
        {
            int count = 0;
            foreach (RigidBody body in _bodies)
            {
                if (body.Shape.Kind == ColliderKind.HeightField)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>全部の体の運動エネルギーの合計 [J]。</summary>
    public float TotalKineticEnergy
    {
        get
        {
            float total = 0.0f;
            foreach (RigidBody body in _bodies)
            {
                total += body.KineticEnergy;
            }

            return total;
        }
    }

    /// <summary>全部の体の運動量の合計 [kg·m/s]。**外力が無ければ保存する**。</summary>
    public Vector3 TotalMomentum
    {
        get
        {
            Vector3 total = Vector3.Zero;
            foreach (RigidBody body in _bodies)
            {
                total += body.Momentum;
            }

            return total;
        }
    }

    /// <summary>
    /// 直前のステップで、この出どころから出た接触が何組あったか。
    ///
    /// **絵からは読めない数字**なので HUD に出す。
    /// 面で触れているはずなのに <see cref="ManifoldSource.EdgeEdge"/> が並ぶときは、
    /// たいてい <see cref="Sat"/> の下駄が効いていない(要点2)。
    /// </summary>
    public int SourceCount(ManifoldSource source) => _sourceCounts[(int)source];

    public int AddBody(RigidBody body)
    {
        _bodies.Add(body);
        return _bodies.Count - 1;
    }

    /// <summary>
    /// 無限に広い平面を足す。**中身は「平面の形を持った静的な体」**(Day 44)。
    ///
    /// Day 43 では平面だけ別のリストに入れていたが、
    /// 形が <see cref="Collider"/> になったので体で表せるようになった。
    /// 呼ぶ側の書き味を変えないために、この名前だけ残してある。
    /// </summary>
    public int AddPlane(Plane3D plane) =>
        AddBody(RigidBody.CreatePlane(
            plane.Normal * plane.Distance, plane.Normal));

    /// <summary>
    /// 地形を1枚足す(Day 46)。**中身は「地形の形を持った静的な体」**。
    ///
    /// <paramref name="corner"/> は格子の (0, 0) 隅の世界座標。
    /// <see cref="AddPlane"/> と同じで、呼ぶ側の書き味のためだけにある薄い包み。
    /// </summary>
    public int AddTerrain(HeightField field, Vector3 corner) =>
        AddBody(RigidBody.CreateTerrain(field, corner));

    /// <summary>
    /// カプセルと当たっている体を集める。**キャラクターのための問い合わせ**(Day 45 の要点6)。
    ///
    /// <see cref="Step"/> が回している総当たりとは<b>別の入口</b>にしてある。
    /// キャラクターは剛体ではない——質量も速度も物理に預けていないので、
    /// <c>_bodies</c> に並ぶ資格が無い。
    /// それでも「世界の形と当たっているか」は知りたいので、
    /// <b>読むだけの問い合わせ</b>をここに置く。
    ///
    /// <para>
    /// <b>これが「クエリ」と呼ばれる種類の API</b>。
    /// Unity の <c>Physics.OverlapCapsule</c>、Unreal の <c>SweepMulti</c>、
    /// Bullet の <c>contactTest</c> が同じ役をしている。
    /// シミュレーションを進めるのとは別に、
    /// <b>今この形を置いたら何に当たるか</b>を問える窓口が要る——
    /// キャラクター・カメラの遮蔽・銃弾・AI の視線、どれもこれを使う。
    /// </para>
    ///
    /// <para>
    /// <b>結果は呼ぶ側が用意した入れ物に書く</b>(<c>Span</c>)。
    /// キャラクターは毎ステップ 10 回以上これを呼ぶので、
    /// <c>List</c> を毎回作るとそのままゴミになる。
    /// 入りきらないぶんは捨てる——キャラクターが同時に触る面は
    /// せいぜい数枚なので、8 も見れば足りる。
    /// </para>
    ///
    /// <para>
    /// <b>今日も総当たり</b>。体が数十個なら平気だが、
    /// キャラクターが1歩動くたびに世界じゅうの体を見るのは筋が悪い。
    /// <b>ここが Day 46 のブロードフェーズのいちばん分かりやすい客</b>になる。
    /// </para>
    /// </summary>
    /// <returns>書き込んだ数。</returns>
    public int QueryCapsule(in Capsule3D capsule, Span<ContactManifold> results)
    {
        int count = 0;
        CapsuleQueries++;

        // --- ブロードフェーズが使えるなら使う(Day 46 の要点7)---
        //
        // **キャラクターはこの問い合わせを1ステップに 10 回以上投げる**。
        // 体が 200 個あれば、Day 45 のやり方では 2,000 体ぶんの判定が走っていた。
        // 格子で絞れば、足元のマスにいる数個で済む。
        if (_gridReady)
        {
            Span<int> candidates = stackalloc int[MaxQueryCandidates];

            // **少し広げて問い合わせる**。位置の補正(Step の4段目)は
            // 格子を組んだあとに体を数ミリ動かすので、
            // ぴったりの箱で問い合わせると端の相手を落としうる。
            int found = _grid.Query(capsule.Bounds.Expanded(QueryMargin), candidates);

            for (int k = 0; k < found && count < results.Length; k++)
            {
                CapsuleQueryTests++;

                ContactManifold manifold =
                    Collision3D.CapsuleAgainst(capsule, _bodies[candidates[k]]);

                if (manifold.Hit)
                {
                    results[count++] = manifold;
                }
            }

            return count;
        }

        foreach (RigidBody body in _bodies)
        {
            if (count >= results.Length)
            {
                break;
            }

            CapsuleQueryTests++;

            ContactManifold manifold = Collision3D.CapsuleAgainst(capsule, body);
            if (manifold.Hit)
            {
                results[count++] = manifold;
            }
        }

        return count;
    }

    /// <summary>
    /// カプセルがどこかに埋まっているか。**当たったかどうかだけ**を知りたいとき。
    ///
    /// 段差を登るときに「持ち上げた先に天井が無いか」を確かめるのに使う。
    /// 押し戻す量は要らないので、1つ見つけたところで打ち切る——
    /// <b>問いが小さければ答えも早い</b>という、クエリを分けておく値打ちがここに出る。
    /// </summary>
    public bool OverlapCapsule(in Capsule3D capsule)
    {
        CapsuleQueries++;

        if (_gridReady)
        {
            Span<int> candidates = stackalloc int[MaxQueryCandidates];
            int found = _grid.Query(capsule.Bounds.Expanded(QueryMargin), candidates);

            for (int k = 0; k < found; k++)
            {
                CapsuleQueryTests++;

                if (Collision3D.CapsuleAgainst(capsule, _bodies[candidates[k]]).Hit)
                {
                    return true;
                }
            }

            return false;
        }

        foreach (RigidBody body in _bodies)
        {
            CapsuleQueryTests++;

            if (Collision3D.CapsuleAgainst(capsule, body).Hit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>直前のフレームに投げたカプセルの問い合わせの回数。**HUD 用**。</summary>
    public int CapsuleQueries { get; private set; }

    /// <summary>その問い合わせで試した体の延べ数。**Day 46 で減る数字**。</summary>
    public long CapsuleQueryTests { get; private set; }

    /// <summary>問い合わせの計測を 0 に戻す。**フレームの頭で呼ぶ**。</summary>
    public void ResetQueryStats()
    {
        CapsuleQueries = 0;
        CapsuleQueryTests = 0;
    }

    public void Clear()
    {
        _bodies.Clear();
        _contacts.Clear();
        _manifolds.Clear();
        Array.Clear(_sourceCounts);
        PairTests = 0;
        BruteForcePairs = 0;
        MaxPenetration = 0.0f;

        // **格子は体の番号で中身を持っている**(Day 46)。
        // 体を全部捨てたのに格子が残っていると、
        // 次の問い合わせが<b>存在しない番号を引く</b>。
        _gridReady = false;

        // **持ち越しも捨てる**(Day 47)。キャッシュの鍵は体の番号なので、
        // 体を入れ替えると<b>まったく別の組のインパルスを引き継ぐ</b>。
        // 筋書きを切り替えた1フレーム目だけ物が弾け飛ぶ、という形で出る。
        _cache.Clear();
        IslandCount = 0;
    }

    /// <summary>
    /// 1ステップ進める。**固定 dt で呼ぶこと**。
    ///
    /// 可変 dt で呼ぶと、フレームレートが落ちた瞬間に貫通が起き、
    /// 同じ操作をしても違う結果になる(Day 19 の固定ステップの話がそのまま効く)。
    /// だから <c>Program</c> はこれを <c>FixedUpdate</c> から呼んでいる。
    /// </summary>
    public void Step(float dt)
    {
        if (dt <= 0.0f)
        {
            return;
        }

        // --- 1. 速度の積分 ---
        //
        // **Day 43 でいちばん大事だった分岐**が、今日ここで形を変えた(要点4)。
        //
        // Day 46 まではこの段で速度と位置をまとめて進めていた。
        // <b>位置を「解く前の速度」で進めていた</b>ことになる——
        // 重力を足したばかりの速度なので、床に置いてある物も毎ステップ
        // <c>g·dt² ≈ 2.7mm</c> だけ沈み、それを位置の補正が押し返す、という
        // 賽の河原のような釣り合いになっていた。
        //
        // <b>摩擦を入れてはじめてそれが表に出た</b>。
        // 傾いた面の上では、沈むぶんのうち<b>面に沿った成分</b>を
        // 位置の補正が戻せない(補正は法線の向きにしか押さないので)。
        // 結果、止まっているはずの箱が毎ステップ
        // <c>g·dt²·sinθ</c> ずつ坂を下る——
        // <b>μ をいくら上げても、速度 0 のまま滑り落ちる</b>。
        //
        // 直し方は順番を入れ替えるだけ。
        // <b>解いたあとの速度で位置を進める</b>ようにすると、
        // 速度が 0 に解けている物は位置も動かない。
        // Box2D も Bullet もこの順で、Day 43 の並びのほうが例外だった。
        if (Integrator == IntegratorMode.Explicit)
        {
            // **陽的オイラー**。位置を「1ステップ前の速度」で進めてから速度を更新する。
            // 直感的にはこちらのほうが自然に見えるのに、エネルギーが増え続ける。
            // <b>比べるために残してある道</b>なので、順番の入れ替えはしない。
            IntegratePositions(dt);
        }

        IntegrateVelocities(dt);

        // --- 2. 判定 ---
        GenerateContacts();

        // --- 3. 下ごしらえ(Day 47 の要点1・2)---
        //
        // 実効質量を先に出しておき、前のステップのインパルスを掛け直す。
        // **この1段があるかないかで、同じ反復回数でも柱の沈み方が変わる**。
        PrepareContacts();

        // --- 4. 速度の解決 ---
        //
        // **組の間は順番に、組の中は同時に**(Day 44 の要点7)。
        // 外側の周回(組から組へ)はガウス・ザイデル——直前の組の結果を見て解くので、
        // 柱の下から上へ荷重が伝わる。
        // 内側(1つの組の4点)はヤコビ——同じ状態から一度に決めるので、
        // 対称な接触では4点が完全に同じ大きさになり、余計なトルクが立たない。
        //
        // **今日、1周の中身が2段になった**(要点3)。
        // 摩擦を先に解いてから法線を解く——順番に理由がある。
        for (int iteration = 0; iteration < VelocityIterations; iteration++)
        {
            foreach ((int start, int count) in _manifolds)
            {
                if (FrictionEnabled)
                {
                    SolveFriction(start, count);
                }

                SolveManifold(start, count);
            }
        }

        // --- 5. 位置の積分(要点4)---
        //
        // **解いたあとの速度で進める**。ここが Day 46 から動いた1行。
        // 接触で速度が 0 に解けた物は、位置も 1mm も動かない。
        if (Integrator == IntegratorMode.SemiImplicit)
        {
            IntegratePositions(dt);
        }

        // --- 6. 位置の補正 ---
        if (PositionCorrection)
        {
            CorrectPositions();
        }

        // --- 7. 持ち越し(Day 47 の要点2)---
        StoreImpulses();

        // --- 8. 眠り(Day 47 の要点5)---
        UpdateSleep(dt);

        // --- 9. 後始末 ---
        foreach (RigidBody body in _bodies)
        {
            body.ClearAccumulators();
        }
    }

    /// <summary>
    /// 解いている間だけ接触点を書き換えるための窓(Day 47)。
    ///
    /// <c>List&lt;T&gt;</c> の添字は<b>値のコピー</b>を返すので、
    /// <c>_contacts[i].NormalImpulse += x</c> とは書けない
    /// (構造体を <c>class</c> にすれば書けるが、接触点は毎ステップ数百個生まれるので
    /// それだけの数のゴミが出る)。
    /// <see cref="CollectionsMarshal.AsSpan"/> は<b>リストの内部配列をそのまま指す</b>ので、
    /// 添字で書き換えたものがリストに残る。
    ///
    /// <para>
    /// <b>要素を足したり消したりしている間に使ってはいけない</b>。
    /// 内部配列が作り直されると、手元の <c>Span</c> は古い配列を指したままになる。
    /// ここでは<b>判定が終わってから</b>しか使わないので安全。
    /// </para>
    /// </summary>
    private Span<ContactPoint> ContactSpan => CollectionsMarshal.AsSpan(_contacts);

    private void IntegrateVelocities(float dt)
    {
        foreach (RigidBody body in _bodies)
        {
            body.IntegrateVelocity(dt, Gravity);
        }
    }

    private void IntegratePositions(float dt)
    {
        foreach (RigidBody body in _bodies)
        {
            body.IntegratePosition(dt);
        }
    }

    /// <summary>
    /// 接触を集める。**総当たり**。
    ///
    /// Day 43 は「体どうし」と「体と平面」の2つのループに分かれていた。
    /// 平面が体になったので<b>ループが1つになった</b>——
    /// 形の組み合わせを見て判定関数を選ぶのは
    /// <see cref="Collision3D.Collide"/> の仕事で、ここは知らない。
    ///
    /// <para>
    /// 1組から出る接触点は<b>最大4つ</b>。
    /// 組の数(<see cref="ContactPairs"/>)と点の数(<c>Contacts.Count</c>)が
    /// 別々なのは今日からで、床に置いた箱1つで
    /// 「1組・4点」になるのが正しい姿になる。
    /// </para>
    /// </summary>
    private void GenerateContacts()
    {
        _contacts.Clear();
        _manifolds.Clear();
        Array.Clear(_sourceCounts);
        PairTests = 0;
        MaxPenetration = 0.0f;

        // **持ち越しの印を1つ進める**(Day 47)。
        // このあと <see cref="Add"/> が引いた組だけが新しい印を持ち、
        // 引かれなかった組は <see cref="StoreImpulses"/> の最後で捨てられる。
        _cache.BeginStep();

        // **総当たりならいくつ試すことになったか**を、常に数えておく(Day 46)。
        // 絞れた割合は「絞ったあとの数」だけでは分からない。
        long dynamicCount = DynamicCount;
        long total = _bodies.Count;
        BruteForcePairs = ((total * (total - 1)) / 2)
            - (((total - dynamicCount) * (total - dynamicCount - 1)) / 2);

        if (Broadphase == BroadphaseMode.BruteForce)
        {
            _gridReady = false;

            // --- Day 45 までのやり方 ---
            for (int i = 0; i < _bodies.Count; i++)
            {
                for (int j = i + 1; j < _bodies.Count; j++)
                {
                    TestPair(i, j);
                }
            }

            return;
        }

        // --- ブロードフェーズで絞る(Day 46 の要点5)---
        //
        // **格子は形を1つも知らない**。渡すのは外接箱の列だけで、
        // 返ってくるのは番号の組だけ。
        // だから形が増えても(今日は地形が増えた)格子は1行も変わらない。
        UpdateBounds();
        _grid.Configure(GridOrigin, GridSize, CellSize);
        _grid.Build(_bounds.AsSpan(0, _bodies.Count));
        _grid.CollectPairs(_bounds.AsSpan(0, _bodies.Count));
        _gridReady = true;

        foreach (BroadPair pair in _grid.Pairs)
        {
            TestPair(pair.A, pair.B);
        }
    }

    /// <summary>
    /// 体ごとの外接 AABB を作り直す(Day 46)。**ステップの頭で1回だけ**。
    ///
    /// 格子を組むときと候補を集めるときの2回なめるので、
    /// <b>その場で作ると2回計算することになる</b>。
    /// 箱の外接箱はクォータニオンの変換を3回含むので、地味に効く。
    /// </summary>
    private void UpdateBounds()
    {
        if (_bounds.Length < _bodies.Count)
        {
            _bounds = new Aabb3D[Math.Max(_bodies.Count * 2, 64)];
        }

        for (int i = 0; i < _bodies.Count; i++)
        {
            _bounds[i] = _bodies[i].Bounds();
        }
    }

    /// <summary>
    /// 1組を実際に当てて、当たっていれば接触点を登録する(Day 46 で切り出した)。
    ///
    /// Day 45 まではこの中身が二重ループの内側に直接書いてあった。
    /// <b>候補の作り方が2通りになった</b>ので、
    /// 「候補をどう作るか」と「候補をどう当てるか」を分けている——
    /// ブロードフェーズを足すときにいちばん先にやる整理がこれになる。
    /// </summary>
    private void TestPair(int i, int j)
    {
        RigidBody a = _bodies[i];
        RigidBody b = _bodies[j];

        // **動かないものどうしは見ない**。当たっていても何も起きない。
        if (a.IsStatic && b.IsStatic)
        {
            return;
        }

        PairTests++;

        ContactManifold manifold = Collision3D.Collide(a, b);
        if (!manifold.Hit)
        {
            return;
        }

        // **実験用の間引き**。既定では何もしない(4 が上限なので)。
        manifold.Reduce(MaxContactsPerPair);

        _sourceCounts[(int)manifold.Source]++;
        MaxPenetration = MathF.Max(MaxPenetration, manifold.MaxDepth);

        // **区切りを覚えておく**。この範囲の点は同じ組から出たもので、
        // 速度の解決ではまとめて1回で解く(Day 44 の要点7)。
        _manifolds.Add((_contacts.Count, manifold.Count));

        for (int k = 0; k < manifold.Count; k++)
        {
            Add(i, j, manifold.Normal, manifold.Points[k], a, b);
        }
    }

    /// <summary>
    /// 接触点を1つ登録する。**跳ね返り目標と、測り直し用の印をここで作る**。
    ///
    /// 反発係数は2つの体の<b>小さいほう</b>を採る。
    /// 「よく跳ねる箱」を「跳ねない粘土の床」に落としたら跳ねない、という直感に合う。
    /// (掛け算や平均を採る実装もあり、正解は無い。物性ではなくモデルの選択)。
    ///
    /// <para>
    /// <b>跳ね返り目標は点ごとに決まる</b>。同じ組の4点でも、
    /// 箱が傾いて落ちてくれば先に当たる角のほうが速い。
    /// 組でまとめて1つの目標にすると、傾いた着地が不自然になる。
    /// </para>
    /// </summary>
    private void Add(
        int indexA,
        int indexB,
        Vector3 normal,
        in ManifoldPoint point,
        RigidBody a,
        RigidBody b)
    {
        Vector3 relative = a.VelocityAt(point.Point) - b.VelocityAt(point.Point);
        float approach = Vector3.Dot(relative, normal);

        float restitution = MathF.Min(a.Restitution, b.Restitution);

        // **ゆっくりぶつかったものは跳ねない**。しきい値が無いと置いた箱が震え続ける。
        float bounce = approach < -RestitutionThreshold ? -restitution * approach : 0.0f;

        // **摩擦は相乗平均で混ぜる**(Day 47 の要点3)。
        // 反発係数(小さいほう)と違う式なのは、
        // <b>片方が 0 なら全体も 0</b> にしたいから——氷の上では何を置いても滑る。
        // 実験用の上書き(<see cref="FrictionOverride"/>)が入っていればそちらが勝つ。
        float friction = FrictionOverride >= 0.0f
            ? FrictionOverride
            : MathF.Sqrt(a.Friction * b.Friction);

        // **接触点を両方の体に貼り付けておく**(Day 44 の要点6)。
        // 位置の補正はこの2つの印がどれだけ離れたかだけを見る。
        // Day 47 からは<b>持ち越しの照合の鍵</b>にもなる。
        Vector3 localA = Vector3.Transform(
            point.Point - a.Position, Quaternion.Conjugate(a.Orientation));
        Vector3 localB = Vector3.Transform(
            point.Point - b.Position, Quaternion.Conjugate(b.Orientation));

        var contact = new ContactPoint(
            indexA, indexB, normal, point.Point, localA, localB,
            -point.Depth, bounce, friction);

        // **前のステップの同じ点を探す**(Day 47 の要点2)。
        // 見つかればインパルスを引き継ぐ。見つからなければ 0 から。
        if (WarmStartActive && _cache.TryMatch(indexA, indexB, localA, localB, out CachedPoint cached))
        {
            contact.NormalImpulse = cached.NormalImpulse;
            contact.TangentImpulse1 = cached.TangentImpulse1;
            contact.TangentImpulse2 = cached.TangentImpulse2;
            contact.WarmStarted = true;
        }

        _contacts.Add(contact);
    }

    /// <summary>
    /// 解く前の下ごしらえ(Day 47 の要点1・2)。**実効質量と、持ち越しの適用**。
    ///
    /// やることは2つ。
    ///
    /// <list type="number">
    /// <item>
    /// <b>実効質量を先に出す</b>。Day 46 までは反復のたびに計算していたが、
    /// <b>反復の間に体の姿勢は変わらない</b>ので毎回同じ値が出ていた。
    /// 8周 × 4点で 32 回だったものが1回になる。
    /// </item>
    /// <item>
    /// <b>持ち越したインパルスを掛ける</b>。
    /// 覚えているのは「前のステップで最終的に必要だった大きさ」なので、
    /// <b>それを掛けた状態から反復を始める</b>と、
    /// 反復は差分だけを直せばよくなる。
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <b>掛けるのは接線も一緒</b>。法線だけ持ち越して摩擦を 0 から始めると、
    /// 積んだ箱が毎ステップ少しずつ横へずれる——
    /// 摩擦こそ「前と同じ力で押さえ続ける」性格のものだから、
    /// 温存の効きがいちばん大きい。
    /// </para>
    /// </summary>
    private void PrepareContacts()
    {
        Span<ContactPoint> contacts = ContactSpan;

        for (int i = 0; i < contacts.Length; i++)
        {
            ref ContactPoint contact = ref contacts[i];

            RigidBody a = _bodies[contact.A];
            RigidBody b = _bodies[contact.B];

            Vector3 rA = contact.Point - a.Position;
            Vector3 rB = contact.Point - b.Position;

            contact.NormalMass = EffectiveMass(a, b, rA, rB, contact.Normal);
            contact.TangentMass1 = EffectiveMass(a, b, rA, rB, contact.Tangent1);
            contact.TangentMass2 = EffectiveMass(a, b, rA, rB, contact.Tangent2);

            if (!contact.WarmStarted)
            {
                continue;
            }

            // 摩擦を切っているなら、接線ぶんは持ち越さない——
            // **切った瞬間に前の摩擦が1回だけ効く**のを防ぐ。
            if (!FrictionEnabled)
            {
                contact.TangentImpulse1 = 0.0f;
                contact.TangentImpulse2 = 0.0f;
            }

            Vector3 impulse =
                (contact.Normal * contact.NormalImpulse)
                + (contact.Tangent1 * contact.TangentImpulse1)
                + (contact.Tangent2 * contact.TangentImpulse2);

            a.ApplyImpulseAtPoint(impulse, contact.Point);
            b.ApplyImpulseAtPoint(-impulse, contact.Point);
        }
    }

    /// <summary>
    /// この向きの実効質量の逆数。**分母をひっくり返したもの**(Day 47 の要点1)。
    ///
    /// Day 43 から使ってきた分母
    /// <c>(1/mA + 1/mB) + n·((I⁻¹(rA×n))×rA) + n·((I⁻¹(rB×n))×rB)</c>
    /// をそのまま逆数にしただけ。**割り算を1回で済ませるため**に、
    /// 逆数の形で持っておく——逆質量を持つのと同じ発想(Day 43 の要点3)。
    ///
    /// <para>
    /// <b>眠っている体は逆質量 0 として扱う</b>(<see cref="RigidBody.SolverInverseMass"/>)。
    /// そうしないと「動くはず」と見積もった分母で小さすぎるインパルスを出し、
    /// 眠っている体に乗ったものが沈む。
    /// </para>
    /// </summary>
    private static float EffectiveMass(
        RigidBody a, RigidBody b, Vector3 rA, Vector3 rB, Vector3 direction)
    {
        float denominator = a.SolverInverseMass + b.SolverInverseMass;
        denominator += AngularTerm(a, rA, direction);
        denominator += AngularTerm(b, rB, direction);

        return denominator > 0.0f ? 1.0f / denominator : 0.0f;
    }

    /// <summary>
    /// 1組ぶんの接触点の**法線方向**をまとめて解く。**4点は同時に決める**(Day 44 の要点7)。
    ///
    /// Day 43 は接触が1点しか無かったので、順番に解けばそれで済んだ。
    /// Day 44 から1組で4点出るので、<b>その4点をどう解くか</b>という問題が生まれた。
    ///
    /// <para>
    /// <b>順番に解くと壊れる</b>。1点目を解いた時点で接触点の相対速度は 0 になり、
    /// 残り3点は「もう離れている」と見て何もしない——
    /// つまり<b>1点目だけが箱の重さを全部背負う</b>。
    /// 角1つで支えているのと変わらないので、そこを中心にトルクが立つ。
    /// 摩擦の無い世界では、傾いた接触は横向きに押すことになるので、
    /// <b>積んだ箱がじりじり滑って崩れる</b>(実際に3段で崩れた)。
    /// </para>
    ///
    /// <para>
    /// 直し方は素直で、<b>4点ぶんを同じ状態から計算してから、まとめて掛ける</b>。
    /// 対称に置かれた箱なら4点の値が完全に一致するので、トルクが立ちようがない。
    /// </para>
    ///
    /// <para>
    /// 掛けるときに<b>点の数で割る</b>のは、
    /// 「1点だけで速度差を消せる大きさ」を4点ぶん足すと4倍になってしまうから。
    /// 割ったぶん1周で消せる量は減るが、反復回数で取り返す——
    /// <b>ヤコビ法(同時に更新)とガウス・ザイデル法(順に更新)の古典的なトレードオフ</b>で、
    /// ここでは「速く収束すること」より「偏らないこと」を採った。
    /// </para>
    ///
    /// <para>
    /// <b>組と組の間は今までどおり順番</b>(ガウス・ザイデル)。
    /// そちらは偏りが問題にならず、むしろ「直前の組の結果を見る」ことで
    /// 柱の下から上へ荷重が伝わる(Day 43 の要点8)。
    /// </para>
    ///
    /// <para>
    /// <b>Day 47 で中身が <see cref="SolveNormal"/> に移った</b>。
    /// ここに残っているのは「4点をどう配るか」だけで、
    /// 「1点をどう解くか」は蓄積インパルスの話になったので分けてある。
    /// </para>
    /// </summary>
    private void SolveManifold(int start, int count)
    {
        Span<ContactPoint> contacts = ContactSpan;

        // **Day 43 と同じ解き方**(<see cref="SolveContactsTogether"/> を切ったとき)。
        // 1点ずつ、直前の結果を見ながら解く。比べるために残してある。
        if (!SolveContactsTogether)
        {
            for (int k = 0; k < count; k++)
            {
                SolveNormal(ref contacts[start + k], 1);
            }

            return;
        }

        // --- 1周目: **同じ状態から**、点ごとの大きさを決める ---
        //
        // <b>Day 47 で少し形が変わった</b>。以前は「大きさを配列に溜めてから
        // まとめて掛ける」の2周だったが、蓄積インパルスは
        // <b>ContactPoint 自身が合計を持つ</b>ので、溜め場が要らなくなった。
        // 代わりに「同じ状態から」を保つために、
        // <see cref="SolveNormal"/> に点の数を渡して割らせている。
        Span<float> velocities = stackalloc float[ContactManifold.MaxPoints];

        for (int k = 0; k < count; k++)
        {
            velocities[k] = NormalVelocity(contacts[start + k]);
        }

        // --- 2周目: 同じ状態から決めた速度を使って掛ける。**点の数で割る** ---
        for (int k = 0; k < count; k++)
        {
            SolveNormal(ref contacts[start + k], count, velocities[k]);
        }
    }

    /// <summary>
    /// 接触点1つの法線方向を解く(Day 47 の要点1)。**Sequential Impulses の心臓**。
    ///
    /// <code>
    ///   λ  = (目標の離れる速度 - 今の相対速度) × 実効質量
    ///   合計 = clamp(合計 + λ, 0, ∞)      ← **ここが今日の全部**
    ///   実際に掛けるぶん = 新しい合計 - 古い合計
    /// </code>
    ///
    /// <para>
    /// <b>Day 46 までは「今掛けるぶん」を直接 0 でクランプしていた</b>。
    /// つまり<b>負の補正が出せなかった</b>——一度掛けすぎたら、それを戻す手が無い。
    /// 柱の下のほうを解くたびに上が持ち上げられ、次の周でまた押し戻される。
    /// 反復を増やしても振動するだけで、答えに近づかない。
    /// </para>
    ///
    /// <para>
    /// <b>合計をクランプすると、途中の周では負も許される</b>。
    /// 「掛けすぎたぶんを引く」ことができるので、反復が本当の答えへ収束する。
    /// 接触の条件は<b>合計が押す向きであること</b>だけで、
    /// 途中の1周がどちら向きかは物理的に意味を持たない——
    /// この一段の読み替えが Erin Catto の Sequential Impulses の核心で、
    /// コードの差は <c>MathF.Max</c> を置く場所が変わるだけになる。
    /// </para>
    ///
    /// <para>
    /// <paramref name="share"/> は<b>同じ組の点の数</b>。
    /// 4点を同時に解くとき、1点で速度差を全部消せる大きさを4点ぶん足すと4倍になる
    /// ので、割ってから掛ける(Day 44 の要点7)。
    /// </para>
    /// </summary>
    private void SolveNormal(ref ContactPoint contact, int share, float normalVelocity = float.NaN)
    {
        if (float.IsNaN(normalVelocity))
        {
            normalVelocity = NormalVelocity(contact);
        }

        float lambda = (contact.Bounce - normalVelocity) * contact.NormalMass / share;

        float applied;

        if (AccumulateImpulses)
        {
            // **合計をクランプする**。これが今日の1行。
            float previous = contact.NormalImpulse;
            contact.NormalImpulse = MathF.Max(previous + lambda, 0.0f);
            applied = contact.NormalImpulse - previous;
        }
        else
        {
            // **Day 46 までの素朴版**。今掛けるぶんを 0 でクランプする。
            // 合計は摩擦の上限に要るので、記録だけはしておく。
            applied = MathF.Max(lambda, 0.0f);
            contact.NormalImpulse += applied;
        }

        if (applied == 0.0f)
        {
            return;
        }

        Vector3 impulse = contact.Normal * applied;
        _bodies[contact.A].ApplyImpulseAtPoint(impulse, contact.Point);
        _bodies[contact.B].ApplyImpulseAtPoint(-impulse, contact.Point);
    }

    /// <summary>接触点での、法線方向の相対速度 [m/s]。**近づいていれば負**。</summary>
    private float NormalVelocity(in ContactPoint contact)
    {
        Vector3 relative =
            _bodies[contact.A].VelocityAt(contact.Point)
            - _bodies[contact.B].VelocityAt(contact.Point);

        return Vector3.Dot(relative, contact.Normal);
    }

    /// <summary>
    /// 1組ぶんの摩擦を解く(Day 47 の要点3)。**接線2方向をまとめてクランプする**。
    ///
    /// 法線方向が「押すことしかできない」のに対し、摩擦は<b>両向きに効く</b>——
    /// 滑ろうとする向きの逆に、上限まで。上限がクーロン摩擦の
    /// <c>|λt| ≤ μ λn</c> で、<b>押し付けが強いほど滑りにくい</b>ことを式にしたもの。
    ///
    /// <para>
    /// <b>2本を別々にクランプしてはいけない</b>。
    /// t1 と t2 をそれぞれ ±μλn で切ると、上限の形が円ではなく<b>正方形</b>になり、
    /// 斜め 45 度に滑るときだけ √2 倍の摩擦が出る——
    /// <b>斜めに押すと止まりやすい床</b>ができてしまう。
    /// 2本を平面上のベクトルとして扱い、<b>長さでクランプする</b>のが正しい
    /// (これを摩擦円錐と呼ぶ)。
    /// </para>
    ///
    /// <para>
    /// <b>法線より先に解く</b>のは Box2D と同じ順。摩擦の上限は
    /// <b>その時点で溜まっている法線インパルス</b>で決まるので、
    /// 「1周前の法線」を使うことになる。
    /// 逆順にすると、その周で新しく増えた法線ぶんまで摩擦の上限に入り、
    /// <b>着地の瞬間だけ摩擦が過剰に効いて物が張り付く</b>。
    /// </para>
    ///
    /// <para>
    /// 温存(要点2)がここでいちばん効く。前のステップの摩擦インパルスから
    /// 始められると、<b>1周目からすでに上限近くの力で押さえられる</b>——
    /// 坂に置いた箱が「1フレーム目だけずり落ちてから止まる」のを防ぐ。
    /// </para>
    /// </summary>
    private void SolveFriction(int start, int count)
    {
        Span<ContactPoint> contacts = ContactSpan;

        for (int k = 0; k < count; k++)
        {
            ref ContactPoint contact = ref contacts[start + k];

            if (contact.Friction <= 0.0f)
            {
                continue;
            }

            RigidBody a = _bodies[contact.A];
            RigidBody b = _bodies[contact.B];

            Vector3 relative = a.VelocityAt(contact.Point) - b.VelocityAt(contact.Point);

            // 接線2方向それぞれで「滑りを止めるのに要るぶん」を出す。
            // **点の数で割る**のは法線と同じ理由(Day 44 の要点7)。
            float lambda1 =
                -Vector3.Dot(relative, contact.Tangent1) * contact.TangentMass1 / count;
            float lambda2 =
                -Vector3.Dot(relative, contact.Tangent2) * contact.TangentMass2 / count;

            float oldX = contact.TangentImpulse1;
            float oldY = contact.TangentImpulse2;

            var sum = new Vector2(oldX + lambda1, oldY + lambda2);

            // **クーロンの上限**。押し付けている強さ × μ。
            float limit = contact.Friction * contact.NormalImpulse;

            if (sum.LengthSquared() > limit * limit)
            {
                // 上限を超えた = **滑っている**。向きはそのままで長さだけ詰める。
                sum = sum.LengthSquared() > 1e-12f
                    ? Vector2.Normalize(sum) * limit
                    : Vector2.Zero;
            }

            contact.TangentImpulse1 = sum.X;
            contact.TangentImpulse2 = sum.Y;

            Vector3 impulse =
                (contact.Tangent1 * (sum.X - oldX)) + (contact.Tangent2 * (sum.Y - oldY));

            a.ApplyImpulseAtPoint(impulse, contact.Point);
            b.ApplyImpulseAtPoint(-impulse, contact.Point);
        }
    }

    /// <summary>
    /// 分母の回転の寄与。<c>n · ((I⁻¹ (r × n)) × r)</c>。
    ///
    /// 導き方は「単位インパルス J = n を点 r に掛けたときの接触点の速度変化」を書き下すだけ。
    ///   Δω = I⁻¹ (r × n)、Δv_point = Δω × r
    /// これを n に投影したものが、そのまま分母に足される量になる。
    /// </summary>
    private static float AngularTerm(RigidBody body, Vector3 r, Vector3 normal)
    {
        // **眠っている体は零行列**(Day 47)。回転の寄与が 0 になり、
        // 静的な体とまったく同じ扱いになる。
        Vector3 angular = Vector3.Transform(Vector3.Cross(r, normal), body.SolverInverseInertia);
        return Vector3.Dot(Vector3.Cross(angular, r), normal);
    }

    /// <summary>
    /// めり込みを押し戻す。**速度ではなく位置を直接動かす**(Day 43 の要点8)。
    ///
    /// 質量に反比例して分けるので、軽いほうが多く動く。
    /// 静的な体(逆質量 0)は 1 ミリも動かない——ここでも分岐が要らない。
    ///
    /// <para>
    /// <b>測り直し方が Day 44 で変わった</b>(要点6)。Day 43 は判定関数をもう一度呼んで
    /// 「今の位置ならどれだけめり込んでいるか」を計算していた。
    /// 箱では判定そのものが重い(15 本の軸 + クリップ)ので、
    /// 4周 × 接触の数だけ呼び直すと目に見えて遅くなる。
    /// </para>
    ///
    /// <para>
    /// 代わりに、接触点を<b>両方の体に貼り付けた印</b>として覚えておき、
    /// <code>
    ///   今の隙間 = 最初の隙間 + (印A - 印B)·法線
    /// </code>
    /// で追いかける。体が離れれば印も離れるので、判定を呼ばずに測り直せる。
    /// **これは Box2D の位置ソルバとほぼ同じ手**で、
    /// 形を1つも知らずに済むのが値打ち——カプセルでも地形でもこのまま動く。
    /// </para>
    ///
    /// <para>
    /// <b>周ごとに測り直す</b>ことがここでいちばん大事なところ。
    /// 判定した時点の深さを使い回すと、
    /// 1周目で解消しためり込みを2周目も押し戻してしまい、
    /// <b>柱が上に弾け飛ぶ</b>。速度の解決(<see cref="SolveManifold"/>)が
    /// 相対速度を毎回読み直しているのと同じ理由。
    /// </para>
    ///
    /// <para>
    /// <b>向きは直さない</b>。位置だけを動かして姿勢はそのままにしてある。
    /// 箱を回して直すほうが正しいが、回すと接触点も動くので反復が収束しにくくなる。
    /// 4点で支えていれば、並進だけでも十分まっすぐ立つ。
    /// </para>
    /// </summary>
    private void CorrectPositions()
    {
        for (int iteration = 0; iteration < PositionIterations; iteration++)
        {
            for (int i = 0; i < _contacts.Count; i++)
            {
                ContactPoint contact = _contacts[i];

                RigidBody a = _bodies[contact.A];
                RigidBody b = _bodies[contact.B];

                // **眠っている体は動かさない**(Day 47)。
                // 両方眠っていれば(あるいは静的なら)和が 0 になり、この組は飛ばされる。
                float inverseMassA = a.SolverInverseMass;
                float inverseMassB = b.SolverInverseMass;

                float inverseMassSum = inverseMassA + inverseMassB;
                if (inverseMassSum <= 0.0f)
                {
                    continue;
                }

                // 印を今の姿勢で世界へ運び直す。
                Vector3 worldA = a.Position + Vector3.Transform(contact.LocalA, a.Orientation);
                Vector3 worldB = b.Position + Vector3.Transform(contact.LocalB, b.Orientation);

                float separation =
                    contact.Separation + Vector3.Dot(worldA - worldB, contact.Normal);

                float overlap = MathF.Max(-separation - Slop, 0.0f);
                if (overlap <= 0.0f)
                {
                    continue;
                }

                Vector3 correction = contact.Normal * (overlap * CorrectionRate / inverseMassSum);

                a.Position += correction * inverseMassA;
                b.Position -= correction * inverseMassB;
            }
        }
    }

    /// <summary>
    /// 溜まったインパルスをキャッシュへ書き戻す(Day 47 の要点2)。**ステップの最後に1回**。
    ///
    /// 組ごとにまとめて書くので、<see cref="_manifolds"/> の区切りをそのまま使える。
    /// 書き終えたら、このステップで触れなかった組を捨てる(<see cref="ContactCache.Prune"/>)。
    ///
    /// <para>
    /// <b>温存を切っているときは何も書かない</b>。書いておいて読まないのでも動くが、
    /// <b>切っている間に古い値が溜まる</b>と、戻した瞬間に大昔のインパルスが1回だけ効く。
    /// つまみを切り替えたときに一瞬跳ねるのは、たいていこの種の取りこぼしから来る。
    /// </para>
    /// </summary>
    private void StoreImpulses()
    {
        if (!WarmStartActive)
        {
            _cache.Clear();
            return;
        }

        foreach ((int start, int count) in _manifolds)
        {
            ContactPoint first = _contacts[start];

            for (int k = 0; k < count; k++)
            {
                ContactPoint contact = _contacts[start + k];

                _cacheScratch[k] = new CachedPoint
                {
                    LocalA = contact.LocalA,
                    LocalB = contact.LocalB,
                    NormalImpulse = contact.NormalImpulse,
                    TangentImpulse1 = contact.TangentImpulse1,
                    TangentImpulse2 = contact.TangentImpulse2,
                };
            }

            _cache.Store(first.A, first.B, _cacheScratch.AsSpan(0, count));
        }

        _cache.Prune();
    }

    /// <summary>
    /// 眠りを更新する(Day 47 の要点5)。**島ごとに、まとめて眠らせる**。
    ///
    /// 3段でやる。
    ///
    /// <list type="number">
    /// <item><b>島を組む</b> … 接触で繋がった動く体を1つにまとめる(<see cref="IslandBuilder"/>)</item>
    /// <item><b>体ごとの時計を進める</b> … 遅ければ足し、速ければ 0 に戻す</item>
    /// <item><b>島の中でいちばん浅い時計に合わせる</b> … 全部眠らせるか、全部起こすか</item>
    /// </list>
    ///
    /// <para>
    /// <b>「いちばん浅い」を採るのが肝</b>。島の中に1体でも動いているものが居れば、
    /// その体の時計は 0 なので島全体が起きたままになる。
    /// 6段の柱に7段目を落とすと、落ちてきた箱が触れた瞬間に
    /// <b>柱ぜんぶが一斉に起きる</b>——これが欲しかった挙動。
    /// </para>
    ///
    /// <para>
    /// <b>眠っている体も判定は通る</b>。ここで省いているのは
    /// 積分(<see cref="RigidBody.IntegrateVelocity"/>)と解決だけで、
    /// ブロードフェーズもナローフェーズも今までどおり動く——
    /// <b>そうしないと、眠っている体に何かがぶつかっても気づけない</b>。
    /// 判定まで省くには「眠った体を格子から抜いて、動くものが近づいたら戻す」
    /// 仕掛けが要る。効くのは体が数千個の規模からなので、今日は入れていない。
    /// </para>
    /// </summary>
    private void UpdateSleep(float dt)
    {
        // --- 切っているなら全部起こす ---
        //
        // 「眠りを切る」は「眠らせない」ではなく「今眠っているものも起こす」。
        // 切ったのに眠ったままの体が残っていると、**触っても反応しない置物**になる。
        if (!SleepEnabled)
        {
            foreach (RigidBody body in _bodies)
            {
                if (body.IsSleeping)
                {
                    body.Wake();
                }
            }

            IslandCount = 0;
            return;
        }

        // --- 1. 島を組む ---
        _islands.Begin(_bodies.Count);

        foreach ((int start, int _) in _manifolds)
        {
            ContactPoint contact = _contacts[start];

            RigidBody a = _bodies[contact.A];
            RigidBody b = _bodies[contact.B];

            // **静的な体は繋がない**。床は世界じゅうと触れているので、
            // 繋いだ瞬間に全部が1つの島になり、誰かが動いている限り誰も眠れない。
            if (a.IsStatic || b.IsStatic)
            {
                continue;
            }

            _islands.Union(contact.A, contact.B);
        }

        if (_islandSleepTimers.Length < _bodies.Count)
        {
            _islandSleepTimers = new float[Math.Max(_bodies.Count * 2, 64)];
        }

        Span<float> timers = _islandSleepTimers.AsSpan(0, _bodies.Count);
        timers.Fill(float.MaxValue);

        // --- 2. 体ごとの時計を進めて、島の最小値を集める ---
        int dynamicIslands = 0;

        for (int i = 0; i < _bodies.Count; i++)
        {
            RigidBody body = _bodies[i];
            if (body.IsStatic)
            {
                continue;
            }

            // **眠っている体の時計は進めない**(進めても意味が無い)。
            // 速度は 0 なので、進めても結果は同じだが、
            // 「起きている体だけが時計を持つ」と読めるほうが分かりやすい。
            if (!body.IsSleeping)
            {
                bool slow =
                    body.LinearVelocity.LengthSquared()
                        < SleepLinearThreshold * SleepLinearThreshold
                    && body.AngularVelocity.LengthSquared()
                        < SleepAngularThreshold * SleepAngularThreshold;

                body.SleepTimer = slow ? body.SleepTimer + dt : 0.0f;
            }

            // **眠らない体が居る島は眠らない**。時計を 0 として混ぜれば、
            // 最小値が 0 になって島ごと起きたままになる——分岐が1つも要らない。
            float timer = body.AllowSleep ? body.SleepTimer : 0.0f;

            int root = _islands.Find(i);
            if (timers[root] == float.MaxValue)
            {
                dynamicIslands++;
            }

            timers[root] = MathF.Min(timers[root], timer);
        }

        IslandCount = dynamicIslands;

        // --- 3. 島ごとに眠らせるか起こすか ---
        for (int i = 0; i < _bodies.Count; i++)
        {
            RigidBody body = _bodies[i];
            if (body.IsStatic)
            {
                continue;
            }

            bool shouldSleep = timers[_islands.Find(i)] >= SleepTime;

            if (shouldSleep && !body.IsSleeping)
            {
                body.Sleep();
            }
            else if (!shouldSleep && body.IsSleeping)
            {
                body.Wake();
            }
        }
    }

}
