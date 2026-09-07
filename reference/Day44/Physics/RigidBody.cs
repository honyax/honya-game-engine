using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 剛体。**変形しない物体の、位置と向きと、その変化率**。
///
/// 「剛体」は<b>どんなに力を掛けても形が変わらない</b>という理想化。
/// 現実の物はぶつかれば凹むが、凹みを追いかけると自由度が頂点の数だけ増える。
/// 形が変わらないと決めてしまえば、物体1つの状態は
/// <b>位置3 + 向き3 = 6 自由度</b>で書き切れる。
/// ゲームの物理エンジンがまず剛体から始まるのはこのためで、
/// 「柔らかいもの」(布・髪・肉)は別枠の技術になる。
///
/// <para>
/// 持っている値は、並進と回転できれいに対応している。
/// </para>
/// <list type="table">
/// <item><term></term><description><b>並進</b> / <b>回転</b></description></item>
/// <item><term>位置</term><description><see cref="Position"/> / <see cref="Orientation"/></description></item>
/// <item><term>速度</term><description><see cref="LinearVelocity"/> / <see cref="AngularVelocity"/></description></item>
/// <item><term>動かしにくさ</term><description>質量 / 慣性テンソル</description></item>
/// <item><term>動かす原因</term><description><see cref="Force"/> / <see cref="Torque"/></description></item>
/// </list>
///
/// <para>
/// <b>「動かしにくさ」だけが対応していない</b>のが回転の難しいところ。
/// 質量はスカラー1つだが、慣性は<b>軸によって違う</b>ので 3x3 の行列(テンソル)になる。
/// 鉛筆は芯の向きには回しやすく、横に振るのは回しにくい——
/// この差を1つの数では書けない。
/// 球だけは例外的にどの軸も同じ(等方)なので1段簡単になる(Day 43 の要点3)。
/// </para>
///
/// <para>
/// <b>逆数で持つ</b>のが物理エンジンの定石(Day 43 の要点3)。
/// <see cref="InverseMass"/> が 0 なら「無限の質量」= 動かない物体で、
/// 分岐を書かずに静的な床や壁を表せる。
/// 割り算が消えて掛け算になるので、0 除算の心配も無くなる。
/// </para>
///
/// <para>
/// <b>Day 44 で形が外に出た</b>。Day 43 のこの型は <c>Radius</c> を直接持っていて、
/// 「体 = 球」と言い切った作りだった。今日 <see cref="Collider"/> を切り出したので、
/// 体が持つのは <see cref="Shape"/> ひとつになる。
/// 慣性テンソルの計算も形の側(<see cref="Collider.InertiaLocal"/>)へ移った——
/// <b>体は「重さと動き」だけを知り、寸法は形が知る</b>という分け方になる。
/// </para>
/// </summary>
internal sealed class RigidBody
{
    // ===== 状態(これが「今」を表す全部)=====

    /// <summary>重心の位置(世界座標)。**球なので中心と一致する**。</summary>
    public Vector3 Position;

    /// <summary>向き。**単位クォータニオン**(Day 41 で使ったのと同じ型)。</summary>
    public Quaternion Orientation = Quaternion.Identity;

    /// <summary>速度 [m/s]。</summary>
    public Vector3 LinearVelocity;

    /// <summary>
    /// 角速度 [rad/s]。**向きが回転軸、長さが速さ**という1本のベクトルで表す。
    ///
    /// 「x 軸まわりに 2、y 軸まわりに 3」のような足し算ができるのが、この表し方の値打ち。
    /// オイラー角(ヨー・ピッチ・ロールの3つ)で持つと足し算が成り立たないので、
    /// 角速度はほぼ必ずこの形になる。
    /// </summary>
    public Vector3 AngularVelocity;

    // ===== 描画用の1ステップ前(Day 19 の補間)=====

    /// <summary>1ステップ前の位置。**描画の補間にだけ使う**。</summary>
    public Vector3 PreviousPosition;

    /// <summary>1ステップ前の向き。同上。</summary>
    public Quaternion PreviousOrientation = Quaternion.Identity;

    // ===== 質量まわり =====

    /// <summary>
    /// 質量の逆数 [1/kg]。**0 なら動かない**(無限の質量)。
    ///
    /// 静的な物体を <c>IsStatic</c> のような bool で表すと、
    /// 解決の式のあちこちに分岐が入る。逆質量 0 なら
    /// 「インパルスを掛けても速度が 0 だけ変わる」——つまり<b>式のまま動かない</b>。
    /// </summary>
    public float InverseMass;

    /// <summary>
    /// 慣性テンソルの逆数を**物体座標で**、対角成分だけ持ったもの。
    ///
    /// 一般の慣性テンソルは 3x3 だが、物体の主軸を座標軸に取れば対角行列になる
    /// (球・箱・カプセルのような対称な形は、形の軸がそのまま主軸)。
    /// だから対角の3つだけ覚えておけば足りる。
    ///
    /// <para>
    /// これは<b>物体座標</b>の値なので、物体が回っても変わらない。
    /// 世界座標での値(<see cref="InverseInertiaWorld"/>)は毎ステップ作り直す。
    /// </para>
    /// </summary>
    public Vector3 InverseInertiaLocal;

    /// <summary>
    /// 慣性テンソルの逆数を**世界座標で**表した 3x3(4x4 の左上を使う)。
    ///
    /// <c>R^T · D · R</c> で作る(<see cref="UpdateInertiaWorld"/>)。
    /// トルクは世界座標で来るので、
    ///   1. 物体座標へ戻す
    ///   2. 対角の逆慣性を掛ける
    ///   3. 世界座標へ戻す
    /// という3段が要る。この行列はその3段を1つにまとめたもの。
    ///
    /// <para>
    /// <b>球なら回しても変わらない</b>(対角が3つとも同じなので、
    /// 回転で挟んでも元に戻る)。Day 43 のデモではこの行列は定数のままだった——
    /// それでも一般形で書いておいたのは、箱でここが効いてくるから。
    /// <b>今日その日が来た</b>。箱は3つの対角が違うので、
    /// 傾けた箱を回すたびに世界での回しにくさが変わる。
    /// </para>
    /// </summary>
    public Matrix4x4 InverseInertiaWorld = Matrix4x4.Identity;

    // ===== 力の溜め場(1ステップぶん)=====

    /// <summary>このステップに掛かっている力の合計 [N]。積分したら 0 に戻す。</summary>
    public Vector3 Force;

    /// <summary>このステップに掛かっているトルクの合計 [N·m]。同上。</summary>
    public Vector3 Torque;

    // ===== 材質のようなもの =====

    /// <summary>
    /// 反発係数 e。0 = 全く跳ねない、1 = 完全に跳ね返る(エネルギーが減らない)。
    ///
    /// **物性というより「衝突のモデル」**で、本来は相手との組み合わせで決まる。
    /// 今日は2つの物体の値の小さいほうを採る(<see cref="PhysicsWorld"/>)——
    /// 硬い球を粘土の床に落としたら跳ねない、という直感に合う。
    /// </summary>
    public float Restitution = 0.4f;

    /// <summary>
    /// 速度の減衰(1秒あたりに失う割合)。0 なら減衰しない。
    ///
    /// **摩擦でも空気抵抗でもない**。数値的な安定のための保険で、
    /// 積分誤差で少しずつ増えていくエネルギーを抜く役をしている。
    /// 本物の空気抵抗は速度の2乗に比例するので、これとは式が違う。
    /// </summary>
    public float LinearDamping;

    /// <summary>角速度の減衰。<see cref="LinearDamping"/> と同じ性格。</summary>
    public float AngularDamping;

    /// <summary>
    /// この体の形。**寸法はすべてここにある**(Day 44)。
    ///
    /// Day 43 では <c>Radius</c> という名前で体が直接持っていた。
    /// 箱には半径が無いので、形ごと外に出した(<see cref="Collider"/>)。
    /// 判定は <see cref="Collision3D.Collide"/> がこの札を見て振り分ける。
    /// </summary>
    public Collider Shape = Collider.Sphere(0.5f);

    /// <summary>逆質量が 0 かどうか。**動かない物体**。</summary>
    public bool IsStatic => InverseMass <= 0.0f;

    /// <summary>質量 [kg]。静的なら <see cref="float.PositiveInfinity"/>。</summary>
    public float Mass => InverseMass > 0.0f ? 1.0f / InverseMass : float.PositiveInfinity;

    /// <summary>運動エネルギー [J]。**並進と回転の両方**。自己チェックで使う。</summary>
    public float KineticEnergy
    {
        get
        {
            if (IsStatic)
            {
                return 0.0f;
            }

            // 並進 1/2 m v²。回転 1/2 ω·(I ω) だが、
            // 手元には I の逆しか無いので、対角の逆数を取り直して使う。
            float linear = 0.5f * Mass * LinearVelocity.LengthSquared();

            Vector3 inertia = new(
                InverseInertiaLocal.X > 0.0f ? 1.0f / InverseInertiaLocal.X : 0.0f,
                InverseInertiaLocal.Y > 0.0f ? 1.0f / InverseInertiaLocal.Y : 0.0f,
                InverseInertiaLocal.Z > 0.0f ? 1.0f / InverseInertiaLocal.Z : 0.0f);

            // 角速度を物体座標へ戻してから掛ける(慣性は物体座標の値なので)。
            Vector3 local = Vector3.Transform(
                AngularVelocity, Quaternion.Conjugate(Orientation));

            float angular = 0.5f * Vector3.Dot(local * inertia, local);
            return linear + angular;
        }
    }

    /// <summary>運動量 [kg·m/s]。**衝突で保存する量**。自己チェックで使う。</summary>
    public Vector3 Momentum => IsStatic ? Vector3.Zero : LinearVelocity * Mass;

    /// <summary>
    /// 中身の詰まった球を作る。慣性モーメントは <c>2/5 · m · r²</c>。
    ///
    /// この係数は「質量が中心からどれだけ離れて分布しているか」で決まる。
    /// 中身が詰まっていれば 2/5、殻だけなら 2/3——
    /// **殻のほうが回しにくい**のは、同じ質量が外側に集まっているから。
    /// フィギュアスケートで腕を縮めると速く回るのと同じ話。
    /// </summary>
    public static RigidBody CreateSphere(float mass, float radius) =>
        Create(mass, Collider.Sphere(radius));

    /// <summary>
    /// 中身の詰まった箱を作る。**半分の長さ**で渡す(要点1)。
    ///
    /// 慣性モーメントは <c>1/12 · m · (h² + d²)</c>。
    /// 球と違って<b>軸ごとに違う値</b>になるので、
    /// 細長い箱は「長い方向を軸にして回す」のは楽で、「横に振る」のは大変になる。
    /// 今日のデモで細長い箱を撃つと、**倒れる向きが偏る**のがそれ。
    /// </summary>
    public static RigidBody CreateBox(float mass, Vector3 halfExtents) =>
        Create(mass, Collider.Box(halfExtents));

    /// <summary>中身の詰まった立方体。</summary>
    public static RigidBody CreateBox(float mass, float halfSize) =>
        Create(mass, Collider.Box(halfSize));

    /// <summary>
    /// 形と質量から体を1つ作る。**形ごとの分岐はここに無い**(Day 44)。
    ///
    /// Day 43 は <c>CreateSphere</c> の中に <c>0.4f * mass * radius * radius</c> と
    /// 直に書いてあった。形が増えたので、慣性の式は形の側
    /// (<see cref="Collider.InertiaLocal"/>)へ移した——
    /// カプセル(Day 45)を足すときに、ここは1行も変わらない。
    /// </summary>
    public static RigidBody Create(float mass, in Collider shape)
    {
        Vector3 inertia = shape.InertiaLocal(mass);

        var body = new RigidBody
        {
            Shape = shape,
            InverseMass = mass > 0.0f ? 1.0f / mass : 0.0f,

            // **成分ごとに逆数を取る**。0 のまま割ると無限になるので、0 は 0 のまま。
            InverseInertiaLocal = new Vector3(
                inertia.X > 0.0f ? 1.0f / inertia.X : 0.0f,
                inertia.Y > 0.0f ? 1.0f / inertia.Y : 0.0f,
                inertia.Z > 0.0f ? 1.0f / inertia.Z : 0.0f),
        };

        body.UpdateInertiaWorld();
        return body;
    }

    /// <summary>
    /// 動かない体。**逆質量も逆慣性も 0**。
    ///
    /// 「無限に重い」ので、何をぶつけても動かないし回らない。
    /// 床の箱や、動かない障害物に使う。
    /// </summary>
    public static RigidBody CreateStatic(in Collider shape) =>
        new()
        {
            Shape = shape,
            InverseMass = 0.0f,
            InverseInertiaLocal = Vector3.Zero,

            // **反発係数は 1**。<see cref="PhysicsWorld"/> は2つの体の小さいほうを採るので、
            // 1 にしておくと<b>相手の値がそのまま通る</b>。
            // Day 43 の平面は反発係数を持っていなかった(ぶつけた球の値をそのまま使った)ので、
            // 平面を体にしても同じ意味になるように揃えてある。
            // 「跳ねない床」が欲しければ、作ったあとで 0 を入れる。
            Restitution = 1.0f,
        };

    /// <summary>
    /// 無限に広い平面。**必ず静的**(Day 44)。
    ///
    /// Day 43 では平面だけ <see cref="PhysicsWorld"/> が別のリストに持っていて、
    /// 接触の相手番号が <c>-1</c> になるという歪みを生んでいた。
    /// 今日、平面も「形の1つを持った動かない体」にしたので、その分岐が消えた。
    ///
    /// <para>
    /// <b><see cref="Position"/> が「重心」でなくなる</b>のは承知のうえ。
    /// 無限に広い形に重心は無いが、逆質量が 0 なので重心が使われる場所が無い——
    /// ここでは「平面が通る点」の意味になる。
    /// Bullet の <c>btStaticPlaneShape</c> もまったく同じ割り切りをしている。
    /// </para>
    /// </summary>
    public static RigidBody CreatePlane(Vector3 point, Vector3 normal) =>
        new()
        {
            Shape = Collider.Plane(normal),
            Position = point,
            InverseMass = 0.0f,
            InverseInertiaLocal = Vector3.Zero,

            // **相手の反発係数をそのまま通す**(<see cref="CreateStatic"/> と同じ理由)。
            Restitution = 1.0f,
        };

    /// <summary>球としての形。判定関数へ渡すときの受け皿。</summary>
    public Sphere3D ToSphere() => new(Position, Shape.Radius);

    /// <summary>箱としての形。**向きは体の向きがそのまま箱の向きになる**。</summary>
    public Box3D ToBox() => new(Position, Shape.HalfExtents, Orientation);

    /// <summary>
    /// 平面としての形。法線は体の向きで回した先。
    ///
    /// 平面は静的なので実際には回らないが、
    /// 「形は物体座標、世界での姿は体が決める」を3つの形で揃えておくと、
    /// 判定側が形の置き方を気にしなくてよくなる。
    /// </summary>
    public Plane3D ToPlane() =>
        Plane3D.FromPointNormal(Position, Vector3.Transform(Shape.Normal, Orientation));

    /// <summary>
    /// 慣性テンソルの逆数を世界座標へ運び直す。**向きが変わるたびに要る**。
    ///
    /// <c>R^T · D · R</c>。System.Numerics は行ベクトル規約
    /// (<c>Vector3.Transform(v, M)</c> が <c>v · M</c>)なので、
    /// 左から順に「世界→物体」「対角を掛ける」「物体→世界」と並ぶ。
    /// 列ベクトル規約の教科書では <c>R · D · R^T</c> と書かれるが、同じものを指している。
    ///
    /// <para>
    /// <b>球なら計算する意味が無かった</b>(結果が入力と同じ)。
    /// それでも Day 43 から毎ステップ呼んでいたのは、箱を足したときに
    /// <b>ここだけが正しく効いていない</b>という事故を防ぐため——
    /// 実際、今日 <see cref="Collider.InertiaLocal"/> を足すだけで箱が正しく回った。
    /// <b>使わないうちから正しい形で書いておくと、後で足すのが1行で済む</b>の実例になっている。
    /// </para>
    /// </summary>
    public void UpdateInertiaWorld()
    {
        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(Orientation);
        Matrix4x4 diagonal = Matrix4x4.CreateScale(InverseInertiaLocal);

        InverseInertiaWorld = Matrix4x4.Transpose(rotation) * diagonal * rotation;
    }

    /// <summary>重心に力を掛ける。**トルクは立たない**。</summary>
    public void ApplyForce(Vector3 force) => Force += force;

    /// <summary>
    /// 世界座標のある点に力を掛ける。**ここでトルクが生まれる**(Day 43 の要点2)。
    ///
    /// <c>τ = r × F</c>。r は重心から作用点へのベクトル。
    /// 外積なので、
    /// <list type="bullet">
    /// <item>力が重心を向いていれば(r と F が平行)トルクは 0</item>
    /// <item>重心から遠いほど、また力が r と直交するほどトルクが大きい</item>
    /// </list>
    /// ドアの取っ手が蝶番から遠いところに付いているのは、この式そのもの。
    /// </summary>
    public void ApplyForceAtPoint(Vector3 force, Vector3 worldPoint)
    {
        Force += force;
        Torque += Vector3.Cross(worldPoint - Position, force);
    }

    /// <summary>トルクだけを掛ける。</summary>
    public void ApplyTorque(Vector3 torque) => Torque += torque;

    /// <summary>
    /// 撃力(インパルス)を掛ける。**力ではなく、運動量そのものを足す**。
    ///
    /// 力 [N] は時間を掛けて初めて速度を変える。
    /// 衝突は「非常に大きな力が、非常に短い時間だけ」掛かる現象なので、
    /// 力と時間を別々に持つ意味が無い——**掛け算の結果だけ**を扱う。
    /// これがインパルス [N·s = kg·m/s] で、単位は運動量と同じ。
    ///
    /// <para>
    /// <c>Δv = J / m = J · InverseMass</c>。逆質量で持っているので掛け算1回。
    /// </para>
    /// </summary>
    public void ApplyImpulse(Vector3 impulse) => LinearVelocity += impulse * InverseMass;

    /// <summary>
    /// 世界座標のある点に撃力を掛ける。**角速度も変わる**。
    ///
    /// <c>Δω = I⁻¹ (r × J)</c>。力とトルクの関係(<see cref="ApplyForceAtPoint"/>)を、
    /// そのまま撃力と角運動量に置き換えた形。
    ///
    /// <para>
    /// <b>球同士の衝突では、これで角速度が変わらない</b>のが面白いところ。
    /// 球の接触法線は必ず中心を通る(r と J が平行)ので <c>r × J = 0</c>。
    /// 摩擦(Day 47)を入れて初めて、ぶつかった球が回り始める。
    /// 今日デモで球を回すのに<b>わざと中心を外して撃つ</b>キーを用意したのは、
    /// この式が効いていることを絵で見せるため。
    /// </para>
    /// </summary>
    public void ApplyImpulseAtPoint(Vector3 impulse, Vector3 worldPoint)
    {
        LinearVelocity += impulse * InverseMass;

        Vector3 angularImpulse = Vector3.Cross(worldPoint - Position, impulse);
        AngularVelocity += Vector3.Transform(angularImpulse, InverseInertiaWorld);
    }

    /// <summary>
    /// 物体上のある点の速度。**回っている物の表面は、中心より速い**。
    ///
    /// <c>v_point = v + ω × r</c>。
    /// 衝突の解決では「接触点での相対速度」が要るので、この式が中心に来る。
    /// </summary>
    public Vector3 VelocityAt(Vector3 worldPoint) =>
        LinearVelocity + Vector3.Cross(AngularVelocity, worldPoint - Position);

    /// <summary>溜めた力とトルクを捨てる。**積分したら必ず呼ぶ**。</summary>
    public void ClearAccumulators()
    {
        Force = Vector3.Zero;
        Torque = Vector3.Zero;
    }

    /// <summary>
    /// 速度を進める。**力 → 加速度 → 速度**。
    ///
    /// <c>a = F/m + g</c>、<c>v += a·dt</c>。
    /// 重力を力ではなく加速度で足しているのは、重力が質量に比例するから——
    /// <c>F = mg</c> を <c>a = F/m</c> で割り戻すと m が消える。
    /// **重いものも軽いものも同じ速さで落ちる**という、あの話がここに出る。
    /// </summary>
    public void IntegrateVelocity(float dt, Vector3 gravity)
    {
        if (IsStatic)
        {
            return;
        }

        LinearVelocity += ((Force * InverseMass) + gravity) * dt;
        AngularVelocity += Vector3.Transform(Torque, InverseInertiaWorld) * dt;

        // **減衰は毎秒の割合で書く**。`v *= 0.99f` と書くと
        // ステップ数に依存してしまい、60Hz と 120Hz で挙動が変わる。
        if (LinearDamping > 0.0f)
        {
            LinearVelocity *= MathF.Pow(1.0f - LinearDamping, dt);
        }

        if (AngularDamping > 0.0f)
        {
            AngularVelocity *= MathF.Pow(1.0f - AngularDamping, dt);
        }
    }

    /// <summary>
    /// 位置と向きを進める。**速度 → 位置**。
    ///
    /// 並進は <c>x += v·dt</c> でよいが、回転はクォータニオンなので
    /// <c>q += ½ ω q dt</c> という見慣れない式になる(Day 43 の要点4)。
    ///
    /// <para>
    /// 足し算をした時点で単位クォータニオンから外れる(長さが 1 でなくなる)ので、
    /// **必ず正規化する**。忘れると向きに拡大縮小が混ざり、
    /// モデルが少しずつ膨らんだり潰れたりする。
    /// </para>
    /// </summary>
    public void IntegratePosition(float dt)
    {
        // **描画の補間用に、進める前の値を控える**(Day 19)。
        // 物理は固定ステップで回り、描画は可変なので、
        // 控えておかないと 120Hz の画面で 60Hz のカクつきが見える。
        PreviousPosition = Position;
        PreviousOrientation = Orientation;

        if (IsStatic)
        {
            return;
        }

        Position += LinearVelocity * dt;

        var spin = new Quaternion(AngularVelocity, 0.0f);
        Orientation += spin * Orientation * (0.5f * dt);
        Orientation = Quaternion.Normalize(Orientation);

        UpdateInertiaWorld();
    }
}
