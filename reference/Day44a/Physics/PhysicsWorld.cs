using System.Numerics;

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
/// 位置の補正でめり込みを測り直すのに使う(要点4)。
/// Day 43 は判定関数をもう一度呼んで測り直していたが、
/// あれは「形が球しか無い」ことに寄りかかった書き方だった。
/// </item>
/// </list>
/// </summary>
internal readonly struct ContactPoint
{
    public readonly int A;
    public readonly int B;

    /// <summary>押し戻す向き。**A を B から引き離す向き**で長さ1。</summary>
    public readonly Vector3 Normal;

    /// <summary>接触点(世界座標)。速度の解決はこの点で相対速度を測る。</summary>
    public readonly Vector3 Point;

    /// <summary>
    /// 接触点を **A の物体座標で**表したもの。**位置の補正で測り直すのに使う**(要点4)。
    ///
    /// 体に画鋲で留めた印だと思えばよい。体が動けば印も一緒に動くので、
    /// 「印どうしがどれだけ離れたか」を見れば、判定をやり直さずに
    /// 今のめり込みが分かる。
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

    public ContactPoint(
        int a,
        int b,
        Vector3 normal,
        Vector3 point,
        Vector3 localA,
        Vector3 localB,
        float separation,
        float bounce)
    {
        A = a;
        B = b;
        Normal = normal;
        Point = point;
        LocalA = localA;
        LocalB = localB;
        Separation = separation;
        Bounce = bounce;
    }
}

/// <summary>
/// 剛体の世界。**1ステップを5段で進める**。
///
/// <list type="number">
/// <item>積分 … 力と重力から速度を、速度から位置を進める(<see cref="IntegratorMode"/>)</item>
/// <item>判定 … 総当たりで接触を集める</item>
/// <item>速度の解決 … インパルスで、めり込む向きの相対速度を消す</item>
/// <item>位置の補正 … 残っためり込みを押し戻す</item>
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
/// <b>今日もブロードフェーズを入れない</b>。総当たりなので体が N 個で N(N-1)/2 組。
/// 100 個で 4,950 組なら毎ステップ回しても平気で、
/// 空間分割(Day 26 の3D版)は Day 46 で足す。
/// **速くする前に正しくする**の順で進める。
/// </para>
///
/// <para>
/// <b>摩擦もスリープも無い</b>。だから今日の箱は転がらずに滑り、
/// 止まったように見えても計算は回り続ける。どちらも Day 47。
/// </para>
/// </summary>
internal sealed class PhysicsWorld
{
    private readonly List<RigidBody> _bodies = [];
    private readonly List<ContactPoint> _contacts = [];

    /// <summary>
    /// マニフォールドごとの範囲(<see cref="_contacts"/> の開始位置と点の数)。
    ///
    /// **点をまとめて解くために要る**(要点5)。
    /// 接触点は組ごとに続けて並んでいるので、区切りさえ覚えておけば
    /// 「この4点は同じ組」と分かる。
    /// </summary>
    private readonly List<(int Start, int Count)> _manifolds = [];

    /// <summary>接触の出どころ別の数(<see cref="ManifoldSource"/> の添字)。HUD 用。</summary>
    private readonly int[] _sourceCounts = new int[4];

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
    /// 1組の接触から採る点の数の上限。**1 にすると箱が落ち着かない**(要点3)。
    ///
    /// 実装の都合ではなく<b>実験のための道具</b>。
    /// 4 が本来の値で、1 にすると Day 43 の「1点だけの接触」に戻る——
    /// 床に置いた箱が1点で支えられ、その点まわりに傾き、
    /// 傾いた先の角が次の最深点になって、いつまでもガタガタと揺れ続ける。
    /// </summary>
    public int MaxContactsPerPair { get; set; } = ContactManifold.MaxPoints;

    /// <summary>
    /// 1組の接触点を**同時に**解くか(要点5)。**切ると床の箱が落ち着かない**。
    ///
    /// true(既定)なら、4点ぶんのインパルスを同じ状態から計算してからまとめて掛ける。
    /// false なら Day 43 と同じで、1点ずつ順に解く——
    /// <b>1点目が全部を背負い、その偏りがトルクになって、床に置いた箱が揺れ止まない</b>
    /// (積むと崩れる。Day 44b)。
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

    public IReadOnlyList<RigidBody> Bodies => _bodies;

    public IReadOnlyList<ContactPoint> Contacts => _contacts;

    /// <summary>直前のステップで試した組の数。**総当たりの代償**。</summary>
    public long PairTests { get; private set; }

    /// <summary>直前のステップで当たっていた組の数。**接触点の数とは別**。</summary>
    public int ContactPairs => _manifolds.Count;

    /// <summary>直前のステップで残っていた最大のめり込み [m]。**沈んでいるかの目安**。</summary>
    public float MaxPenetration { get; private set; }

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

    public void Clear()
    {
        _bodies.Clear();
        _contacts.Clear();
        _manifolds.Clear();
        Array.Clear(_sourceCounts);
        PairTests = 0;
        MaxPenetration = 0.0f;
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

        // --- 1. 積分 ---
        //
        // **Day 43 でいちばん大事だった分岐**。
        // 中身はどちらも同じ2つの呼び出しで、順番だけが逆になっている。
        if (Integrator == IntegratorMode.SemiImplicit)
        {
            IntegrateVelocities(dt);
            IntegratePositions(dt);
        }
        else
        {
            // **陽的オイラー**。位置を「1ステップ前の速度」で進めてから速度を更新する。
            // 直感的にはこちらのほうが自然に見えるのに、エネルギーが増え続ける。
            IntegratePositions(dt);
            IntegrateVelocities(dt);
        }

        // --- 2. 判定 ---
        GenerateContacts();

        // --- 3. 速度の解決 ---
        //
        // **組の間は順番に、組の中は同時に**(要点5)。
        // 外側の周回(組から組へ)はガウス・ザイデル——直前の組の結果を見て解くので、
        // 柱の下から上へ荷重が伝わる。
        // 内側(1つの組の4点)はヤコビ——同じ状態から一度に決めるので、
        // 対称な接触では4点が完全に同じ大きさになり、余計なトルクが立たない。
        for (int iteration = 0; iteration < VelocityIterations; iteration++)
        {
            foreach ((int start, int count) in _manifolds)
            {
                SolveManifold(start, count);
            }
        }

        // --- 4. 位置の補正 ---
        if (PositionCorrection)
        {
            CorrectPositions();
        }

        // --- 5. 後始末 ---
        foreach (RigidBody body in _bodies)
        {
            body.ClearAccumulators();
        }
    }

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

        for (int i = 0; i < _bodies.Count; i++)
        {
            RigidBody a = _bodies[i];

            for (int j = i + 1; j < _bodies.Count; j++)
            {
                RigidBody b = _bodies[j];

                // **動かないものどうしは見ない**。当たっていても何も起きない。
                if (a.IsStatic && b.IsStatic)
                {
                    continue;
                }

                PairTests++;

                ContactManifold manifold = Collision3D.Collide(a, b);
                if (!manifold.Hit)
                {
                    continue;
                }

                // **実験用の間引き**。既定では何もしない(4 が上限なので)。
                manifold.Reduce(MaxContactsPerPair);

                _sourceCounts[(int)manifold.Source]++;
                MaxPenetration = MathF.Max(MaxPenetration, manifold.MaxDepth);

                // **区切りを覚えておく**。この範囲の点は同じ組から出たもので、
                // 速度の解決ではまとめて1回で解く(要点5)。
                _manifolds.Add((_contacts.Count, manifold.Count));

                for (int k = 0; k < manifold.Count; k++)
                {
                    Add(i, j, manifold.Normal, manifold.Points[k], a, b);
                }
            }
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

        // **接触点を両方の体に貼り付けておく**(要点4)。
        // 位置の補正はこの2つの印がどれだけ離れたかだけを見る。
        Vector3 localA = Vector3.Transform(
            point.Point - a.Position, Quaternion.Conjugate(a.Orientation));
        Vector3 localB = Vector3.Transform(
            point.Point - b.Position, Quaternion.Conjugate(b.Orientation));

        _contacts.Add(new ContactPoint(
            indexA, indexB, normal, point.Point, localA, localB, -point.Depth, bounce));
    }

    /// <summary>
    /// 1組ぶんの接触点をまとめて解く。**4点は同時に決める**(要点5)。
    ///
    /// Day 43 は接触が1点しか無かったので、順番に解けばそれで済んだ。
    /// 今日は1組から4点出るので、<b>その4点をどう解くか</b>という問題が新しく生まれる。
    ///
    /// <para>
    /// <b>順番に解くと壊れる</b>。1点目を解いた時点で接触点の相対速度は 0 になり、
    /// 残り3点は「もう離れている」と見て何もしない——
    /// つまり<b>1点目だけが箱の重さを全部背負う</b>。
    /// 角1つで支えているのと変わらないので、そこを中心にトルクが立つ。
    /// 床に置いた箱1つでも、支える点の偏りが毎ステップ入れ替わって
    /// <b>揺れがいつまでも止まらない</b>。積むとこの偏りが段ごとに重なって崩れる(Day 44b)。
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
    /// </summary>
    private void SolveManifold(int start, int count)
    {
        // **Day 43 と同じ解き方**(<see cref="SolveContactsTogether"/> を切ったとき)。
        // 1点ずつ、直前の結果を見ながら解く。比べるために残してある。
        if (!SolveContactsTogether)
        {
            for (int k = 0; k < count; k++)
            {
                ApplyNormalImpulse(start + k, NormalImpulse(_contacts[start + k]));
            }

            return;
        }

        Span<float> magnitudes = stackalloc float[ContactManifold.MaxPoints];

        // --- 1周目: **同じ状態から**、点ごとの大きさを決める ---
        for (int k = 0; k < count; k++)
        {
            magnitudes[k] = NormalImpulse(_contacts[start + k]);
        }

        // --- 2周目: まとめて掛ける。**点の数で割る** ---
        for (int k = 0; k < count; k++)
        {
            ApplyNormalImpulse(start + k, magnitudes[k] / count);
        }
    }

    /// <summary>接触点1つにインパルスを掛ける。**押すだけ**(負なら何もしない)。</summary>
    private void ApplyNormalImpulse(int index, float magnitude)
    {
        if (magnitude <= 0.0f)
        {
            return;
        }

        ContactPoint contact = _contacts[index];
        Vector3 impulse = contact.Normal * magnitude;

        _bodies[contact.A].ApplyImpulseAtPoint(impulse, contact.Point);
        _bodies[contact.B].ApplyImpulseAtPoint(-impulse, contact.Point);
    }

    /// <summary>
    /// 接触点1つに掛けるべきインパルスの大きさ。**Day 43 の心臓部がそのまま生きている**。
    ///
    /// <code>
    ///   j = (目標の離れる速度 - 今の相対速度) / (逆質量と逆慣性から決まる係数)
    /// </code>
    ///
    /// 分母は「この向きに単位インパルスを掛けたら、接触点の相対速度がどれだけ変わるか」。
    /// 軽いものほど、また回りやすいものほど大きくなる——
    /// つまり<b>動かしやすいものほど、同じ速度差を作るのに要るインパルスが小さい</b>。
    ///
    /// <para>
    /// <b>今日から回転の項が本当に効く</b>。Day 43 の球どうしでは
    /// 法線が必ず中心を通るので <c>r × n = 0</c>、回転の寄与は常に 0 だった。
    /// 箱は接触点が中心からずれるので、
    /// <b>同じ大きさのインパルスでも掛ける場所で効き方が変わる</b>——
    /// 角から着地した箱が起き上がるのは、この項があるから。
    /// </para>
    ///
    /// <para>
    /// <b>負の値は返さない</b>。接触は押すことしかできない。
    /// これを許すと、離れていく物を引き戻す(接着剤のような)力になる。
    /// </para>
    /// </summary>
    private float NormalImpulse(in ContactPoint contact)
    {
        RigidBody a = _bodies[contact.A];
        RigidBody b = _bodies[contact.B];

        Vector3 rA = contact.Point - a.Position;
        Vector3 rB = contact.Point - b.Position;

        Vector3 relative = a.VelocityAt(contact.Point) - b.VelocityAt(contact.Point);
        float normalVelocity = Vector3.Dot(relative, contact.Normal);

        // 目標(<see cref="ContactPoint.Bounce"/>)まで離れているなら、もう掛けるものが無い。
        float delta = contact.Bounce - normalVelocity;
        if (delta <= 0.0f)
        {
            return 0.0f;
        }

        // --- 分母 ---
        //
        // 並進の寄与は逆質量の和。回転の寄与は
        //   n · ( (I⁻¹ (r × n)) × r )
        // で、「腕の長さ r で回したときに、接触点がどれだけ動くか」を測っている。
        float denominator = a.InverseMass + b.InverseMass;
        denominator += AngularTerm(a, rA, contact.Normal);
        denominator += AngularTerm(b, rB, contact.Normal);

        // 両方とも動かない(逆質量も逆慣性も 0)。掛けても何も起きない。
        return denominator > 0.0f ? delta / denominator : 0.0f;
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
        Vector3 angular = Vector3.Transform(Vector3.Cross(r, normal), body.InverseInertiaWorld);
        return Vector3.Dot(Vector3.Cross(angular, r), normal);
    }

    /// <summary>
    /// めり込みを押し戻す。**速度ではなく位置を直接動かす**(Day 43 の要点8)。
    ///
    /// 質量に反比例して分けるので、軽いほうが多く動く。
    /// 静的な体(逆質量 0)は 1 ミリも動かない——ここでも分岐が要らない。
    ///
    /// <para>
    /// <b>測り直し方が Day 44 で変わった</b>(要点4)。Day 43 は判定関数をもう一度呼んで
    /// 「今の位置ならどれだけめり込んでいるか」を計算していた。
    /// 箱どうしの判定は重い(Day 44b で 15 本の軸 + クリップになる)ので、
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

                float inverseMassSum = a.InverseMass + b.InverseMass;
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

                a.Position += correction * a.InverseMass;
                b.Position -= correction * b.InverseMass;
            }
        }
    }
}
