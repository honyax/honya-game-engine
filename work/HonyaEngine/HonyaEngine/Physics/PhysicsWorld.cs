using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 積分のやり方。**順番が違うだけ**なのに、結果はまるで違う(要点1)。
/// </summary>
internal enum IntegratorMode
{
    /// <summary>速度を先に進めてから位置を進める。**ゲームの標準**。</summary>
    SemiImplicit,

    /// <summary>位置を先に進めてから速度を進める。**素朴だが発散する**。</summary>
    Explicit,
}

/// <summary>
/// 1つの接触。**判定の結果 + 解決に必要な下ごしらえ**。
///
/// <see cref="Contact3D"/>(判定の生の結果)との違いは、
/// <b>どの体どうしか</b>と <b>跳ね返り目標</b>を持っていること。
/// 判定関数は形しか知らないので、体の番号は世界の側で付ける。
///
/// <para>
/// <see cref="B"/> が負のときは<b>相手が平面</b>を意味する。
/// 平面は動かないので剛体を作っていない——
/// 逆質量 0 の体を置いても同じことができるが、
/// 「無限に広い形」を体に持たせると <see cref="RigidBody.Position"/> の意味が怪しくなる。
/// <b>Day 46 で地形コライダを足すときに、この分岐は Collider 側へ畳む</b>予定。
/// </para>
/// </summary>
internal readonly struct ContactPoint
{
    public readonly int A;
    public readonly int B;

    /// <summary>相手が平面のときの番号。体どうしなら -1。**位置の補正で測り直すのに要る**。</summary>
    public readonly int Plane;

    public readonly Vector3 Normal;
    public readonly float Depth;
    public readonly Vector3 Point;

    /// <summary>
    /// 目標とする離れる速度 [m/s]。**接触を作った時点の相対速度から1回だけ決める**。
    ///
    /// 反復のたびに <c>-e·vn</c> を計算し直すと、
    /// 跳ね返りが反復回数ぶん重ね掛けされて<b>球が天井まで飛ぶ</b>。
    /// 目標値は最初に決めて固定するのが定石(要点7)。
    /// </summary>
    public readonly float Bounce;

    public ContactPoint(
        int a, int b, int plane, Vector3 normal, float depth, Vector3 point, float bounce)
    {
        A = a;
        B = b;
        Plane = plane;
        Normal = normal;
        Depth = depth;
        Point = point;
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
/// <b>3 と 4 が分かれている</b>のが引っかかりどころ。
/// 速度を直したのに位置がめり込んだままなのは、
/// インパルスは「これ以上めり込まない」ようにするだけで、
/// <b>既にめり込んだぶんは戻さない</b>から。
/// 位置を直接動かすのは物理的にはインチキだが、
/// これをやらないと重力で少しずつ沈み続ける(要点8)。
/// </para>
///
/// <para>
/// <b>今日はブロードフェーズを入れない</b>。総当たりなので体が N 個で N(N-1)/2 組。
/// 100 個で 4,950 組は毎ステップ回しても平気で、
/// 空間分割(Day 26 の3D版)は Day 46 で足す。
/// **速くする前に正しくする**の順で進める。
/// </para>
///
/// <para>
/// <b>摩擦もスリープも無い</b>。だから今日の球は転がらずに滑り、
/// 止まったように見えても計算は回り続ける。どちらも Day 47。
/// </para>
/// </summary>
internal sealed class PhysicsWorld
{
    private readonly List<RigidBody> _bodies = [];
    private readonly List<Plane3D> _planes = [];
    private readonly List<ContactPoint> _contacts = [];

    /// <summary>重力加速度 [m/s²]。地球はおよそ 9.81。</summary>
    public Vector3 Gravity { get; set; } = new(0.0f, -9.81f, 0.0f);

    /// <summary>積分のやり方。**既定はセミインプリシット**。</summary>
    public IntegratorMode Integrator { get; set; } = IntegratorMode.SemiImplicit;

    /// <summary>
    /// 速度の解決を何周するか。**1 だと積み上げが持たない**(要点8)。
    ///
    /// 接触は互いに影響し合う——下の球を押し戻すと上の球が浮く。
    /// 1周では下から上へ1段ぶんしか情報が伝わらないので、
    /// 段数ぶん回さないと柱が沈む。
    /// **これが Sequential Impulses の素朴版**で、
    /// 蓄積インパルスと温存(warm starting)を足したものが Day 47。
    /// </summary>
    public int VelocityIterations { get; set; } = 8;

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
    /// 5mm 程度の重なりを許しておけば、接触が安定して残る。
    /// </summary>
    public float Slop { get; set; } = 0.005f;

    /// <summary>
    /// この速さ [m/s] より遅い衝突では跳ね返らせない。
    ///
    /// **これが無いと、置いてある球が永久に細かく震える**。
    /// 重力は毎ステップ <c>9.81 × dt ≈ 0.16 m/s</c> を足すので、
    /// 床に乗っているだけの球にも常に小さな接近速度がある。
    /// それに反発係数を掛けると、いつまでも跳ね続けてしまう。
    /// </summary>
    public float RestitutionThreshold { get; set; } = 1.0f;

    public IReadOnlyList<RigidBody> Bodies => _bodies;

    public IReadOnlyList<Plane3D> Planes => _planes;

    public IReadOnlyList<ContactPoint> Contacts => _contacts;

    /// <summary>直前のステップで試した組の数。**総当たりの代償**。</summary>
    public long PairTests { get; private set; }

    /// <summary>直前のステップで残っていた最大のめり込み [m]。**沈んでいるかの目安**。</summary>
    public float MaxPenetration { get; private set; }

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

    public int AddBody(RigidBody body)
    {
        _bodies.Add(body);
        return _bodies.Count - 1;
    }

    public void AddPlane(Plane3D plane) => _planes.Add(plane);

    public void Clear()
    {
        _bodies.Clear();
        _planes.Clear();
        _contacts.Clear();
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
        // **今日いちばん大事な分岐がここ**(要点1)。
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
        for (int iteration = 0; iteration < VelocityIterations; iteration++)
        {
            for (int i = 0; i < _contacts.Count; i++)
            {
                SolveVelocity(_contacts[i]);
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
    /// 接触を集める。**総当たり + 平面**。
    ///
    /// 体どうしは N(N-1)/2 組、体と平面は N×M 組。
    /// 平面は動かないので平面どうしは見ない。
    /// </summary>
    private void GenerateContacts()
    {
        _contacts.Clear();
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

                Contact3D contact = Collision3D.SphereSphere(a.ToSphere(), b.ToSphere());
                if (contact.Hit)
                {
                    Add(i, j, -1, contact, a, b);
                }
            }

            if (a.IsStatic)
            {
                continue;
            }

            for (int planeIndex = 0; planeIndex < _planes.Count; planeIndex++)
            {
                PairTests++;

                Contact3D contact = Collision3D.SpherePlane(a.ToSphere(), _planes[planeIndex]);
                if (contact.Hit)
                {
                    Add(i, -1, planeIndex, contact, a, null);
                }
            }
        }
    }

    /// <summary>
    /// 接触を1つ登録する。**跳ね返り目標をここで決める**。
    ///
    /// 反発係数は2つの体の<b>小さいほう</b>を採る。
    /// 「よく跳ねる球」を「跳ねない粘土の床」に落としたら跳ねない、という直感に合う。
    /// (掛け算や平均を採る実装もあり、正解は無い。物性ではなくモデルの選択)。
    /// </summary>
    private void Add(
        int indexA, int indexB, int planeIndex, in Contact3D contact, RigidBody a, RigidBody? b)
    {
        MaxPenetration = MathF.Max(MaxPenetration, contact.Depth);

        Vector3 relative = a.VelocityAt(contact.Point)
            - (b?.VelocityAt(contact.Point) ?? Vector3.Zero);

        float approach = Vector3.Dot(relative, contact.Normal);

        float restitution = b is null
            ? a.Restitution
            : MathF.Min(a.Restitution, b.Restitution);

        // **ゆっくりぶつかったものは跳ねない**。しきい値が無いと置いた球が震え続ける。
        float bounce = approach < -RestitutionThreshold ? -restitution * approach : 0.0f;

        _contacts.Add(new ContactPoint(
            indexA, indexB, planeIndex, contact.Normal, contact.Depth, contact.Point, bounce));
    }

    /// <summary>
    /// 接触1つぶんのインパルスを掛ける。**今日の心臓部**(要点6)。
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
    /// <b>負のインパルスは掛けない</b>。接触は押すことしかできない。
    /// これを許すと、離れていく物を引き戻す(接着剤のような)力になる。
    /// </para>
    /// </summary>
    private void SolveVelocity(in ContactPoint contact)
    {
        RigidBody a = _bodies[contact.A];
        RigidBody? b = contact.B >= 0 ? _bodies[contact.B] : null;

        Vector3 rA = contact.Point - a.Position;
        Vector3 rB = b is not null ? contact.Point - b.Position : Vector3.Zero;

        Vector3 relative = a.VelocityAt(contact.Point)
            - (b?.VelocityAt(contact.Point) ?? Vector3.Zero);

        float normalVelocity = Vector3.Dot(relative, contact.Normal);

        // 目標(<see cref="ContactPoint.Bounce"/>)まで離れているなら、もう掛けるものが無い。
        float delta = contact.Bounce - normalVelocity;
        if (delta <= 0.0f)
        {
            return;
        }

        // --- 分母 ---
        //
        // 並進の寄与は逆質量の和。回転の寄与は
        //   n · ( (I⁻¹ (r × n)) × r )
        // で、「腕の長さ r で回したときに、接触点がどれだけ動くか」を測っている。
        //
        // **球どうしでは回転の寄与が必ず 0** になる。
        // 接触法線が中心を通るので r と n が平行、外積が消える。
        // それでも一般形で書いてあるのは Day 44 の箱のため(要点4)。
        float denominator = a.InverseMass + (b?.InverseMass ?? 0.0f);
        denominator += AngularTerm(a, rA, contact.Normal);

        if (b is not null)
        {
            denominator += AngularTerm(b, rB, contact.Normal);
        }

        if (denominator <= 0.0f)
        {
            // 両方とも動かない(逆質量も逆慣性も 0)。掛けても何も起きない。
            return;
        }

        float magnitude = delta / denominator;
        Vector3 impulse = contact.Normal * magnitude;

        a.ApplyImpulseAtPoint(impulse, contact.Point);
        b?.ApplyImpulseAtPoint(-impulse, contact.Point);
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
    /// めり込みを押し戻す。**速度ではなく位置を直接動かす**(要点8)。
    ///
    /// 質量に反比例して分けるので、軽いほうが多く動く。
    /// 静的な体(逆質量 0)は 1 ミリも動かない——ここでも分岐が要らない。
    ///
    /// <para>
    /// <b>これは物理ではない</b>。位置を勝手に動かすとエネルギーが湧く。
    /// それでもやるのは、めり込んだまま放っておくと重力で沈み続けるから。
    /// <see cref="CorrectionRate"/> でじわりと戻し、
    /// <see cref="Slop"/> ぶんは許して震えを避ける、というのが実用上の落とし所になる。
    /// </para>
    ///
    /// <para>
    /// <b>周ごとに深さを測り直す</b>のがここでいちばん大事なところ。
    /// 判定した時点の深さを使い回すと、
    /// 1周目で解消しためり込みを2周目も押し戻してしまい、
    /// <b>柱が上に弾け飛ぶ</b>。速度の解決(<see cref="SolveVelocity"/>)が
    /// 相対速度を毎回読み直しているのと同じ理由。
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
                RigidBody? b = contact.B >= 0 ? _bodies[contact.B] : null;

                float inverseMassSum = a.InverseMass + (b?.InverseMass ?? 0.0f);
                if (inverseMassSum <= 0.0f)
                {
                    continue;
                }

                (Vector3 normal, float depth) = Measure(contact, a, b);

                float overlap = MathF.Max(depth - Slop, 0.0f);
                if (overlap <= 0.0f)
                {
                    continue;
                }

                Vector3 correction = normal * (overlap * CorrectionRate / inverseMassSum);

                a.Position += correction * a.InverseMass;
                if (b is not null)
                {
                    b.Position -= correction * b.InverseMass;
                }
            }
        }
    }

    /// <summary>
    /// 今の位置でめり込みを測り直す。**判定関数をもう一度呼ぶだけ**。
    ///
    /// <b>形が球しか無いことに寄りかかっている</b>のが正直なところ。
    /// Day 44 で箱を足したら、ここは「形を知っている誰か」に頼むことになる——
    /// つまり <c>Collider</c> という型が必要になる、という前触れ。
    /// </summary>
    private (Vector3 Normal, float Depth) Measure(in ContactPoint contact, RigidBody a, RigidBody? b)
    {
        if (b is null)
        {
            Plane3D plane = _planes[contact.Plane];
            return (plane.Normal, a.Radius - plane.SignedDistance(a.Position));
        }

        Vector3 delta = a.Position - b.Position;
        float distance = delta.Length();

        // 中心が一致してしまったときは、判定したときの法線をそのまま使う。
        Vector3 normal = distance > 1e-6f ? delta / distance : contact.Normal;
        return (normal, a.Radius + b.Radius - distance);
    }
}
