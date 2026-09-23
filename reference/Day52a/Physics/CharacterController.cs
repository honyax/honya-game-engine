using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// キネマティックなキャラクターコントローラ。**今日の主役**(要点1〜4)。
///
/// 「キネマティック」は<b>力で動かさない</b>という意味。
/// <see cref="RigidBody"/> は力とインパルスを受けて速度が変わり、
/// その速度で位置が変わった。こちらは逆で、
/// <b>行きたい場所へ位置を直に動かしてから、めり込んだぶんを押し戻す</b>。
///
/// <para>
/// <b>なぜ剛体にしないのか</b>。これが今日いちばん大事な設計の話になる。
/// 人間を剛体として物理に預けると、次のことが全部「起きてしまう」。
/// </para>
/// <list type="bullet">
/// <item>坂に立つと<b>滑り落ちる</b>(摩擦を入れても、傾きしだいで必ず滑る)</item>
/// <item>歩き出すと<b>転ぶ</b>(足元にだけ力を掛けるとトルクが立つ)</item>
/// <item>段差に当たると<b>止まる</b>(15cm の縁石を越えられない)</item>
/// <item>止まりたいのに<b>止まれない</b>(慣性が残る)</item>
/// </list>
///
/// <para>
/// どれも物理としては正しいのに、<b>操作としては全部間違っている</b>。
/// 遊ぶ人が期待しているのは「45 度までの坂は登れて、30cm の段差は乗り越えられて、
/// キーを離したらすぐ止まる」という<b>ゲームの都合</b>で、
/// これは物理法則から出てくる答えではない。
/// だからキャラクターは物理シミュレーションの<b>外</b>に置き、
/// 世界の形とだけ相談しながら自分で動く。
/// Unity の <c>CharacterController</c>、Unreal の <c>CharacterMovementComponent</c>、
/// Quake 以来の <c>PM_Move</c>——**商用エンジンはどれもこの作り**になっている。
/// </para>
///
/// <para>
/// <b>代償もはっきりしている</b>。押されない・押せない・回らない。
/// 動く床に乗っても運ばれず、箱を押しても動かない。
/// そこが要るなら、当たった相手にインパルスを掛けてやる
/// (改造課題3)か、剛体との合いの子にすることになる。
/// </para>
///
/// <para>
/// <b>位置は「足元」で持つ</b>(<see cref="Position"/>)。
/// 重心でも中心でもなく、地面に触れる点。
/// 段差の高さも坂の傾きも接地判定も、全部「足元から測った値」なので、
/// ここを中心にすると式のあちこちに <c>+ Height/2</c> が散らばる。
/// </para>
///
/// <para>
/// <b>向きを持たない</b>。カプセルは常に立っている(軸は世界の Y)。
/// <see cref="FacingYaw"/> は<b>絵のための向き</b>で、当たり判定には一切効かない——
/// 人型は回っても占める場所がほとんど変わらないので、
/// 判定を回す意味がない(むしろ回すと壁際で引っかかる)。
/// </para>
/// </summary>
internal sealed class CharacterController
{
    /// <summary>押し戻しを何周するか。**接触どうしが打ち消し合う**ので1周では足りない。</summary>
    private const int ResolvePasses = 4;

    /// <summary>1回の問い合わせで受け取る接触の上限。**同時に触る面はせいぜい数枚**。</summary>
    private const int MaxQueryResults = 8;

    /// <summary>接地を探るときに下へ伸ばす距離 [m]。**skin より十分大きく**。</summary>
    private const float GroundProbe = 0.06f;

    /// <summary>
    /// 段差を降ろすときに何回に分けるか。**1回で落とすと縁にめり込みすぎる**(要点3)。
    ///
    /// 掃引(スイープ)が無いので、段差ぶんを一度に落とすと
    /// 段の角に深くめり込んだ姿勢で押し戻すことになり、
    /// <b>接触の法線が寝てしまって「壁」に見える</b>。
    /// 小刻みに下ろして当たった時点で止めれば、
    /// 掃引に近い「触れたところで止まる」挙動になる。
    /// </summary>
    private const int DropSteps = 6;

    /// <summary>
    /// 段差を降ろしている間、「床」と見なす法線の Y の下限。
    ///
    /// 普段は <see cref="SlopeLimitCosine"/> で切るが、
    /// 段差を降ろすときは<b>わざと緩める</b>——
    /// 段の角に触れたときの法線は寝ているので、
    /// 普段の基準で見ると「登れない壁」になってしまう。
    /// </summary>
    private const float SteppingUpness = 0.1f;

    /// <summary>
    /// 押し戻しの結果を受け取る入れ物。**使い回す**。
    ///
    /// 1ステップで <see cref="PhysicsWorld.QueryCapsule"/> を10回以上呼ぶので、
    /// 呼ぶたびに配列を作るとそれがそのままゴミになる。
    /// フィールドに1本持っておけば、確保は起動時の1回で済む。
    /// </summary>
    private readonly ContactManifold[] _hits = new ContactManifold[MaxQueryResults];

    // ===== 形 =====

    /// <summary>**足元**の位置(世界座標)。地面に触れる点。</summary>
    public Vector3 Position;

    /// <summary>カプセルの半径 [m]。**肩幅の半分**くらい。</summary>
    public float Radius { get; set; } = 0.35f;

    /// <summary>全高 [m]。足元から頭のてっぺんまで。</summary>
    public float Height { get; set; } = 1.8f;

    /// <summary>
    /// 表面からどれだけ浮かせておくか [m]。**0 にしてはいけない**。
    ///
    /// Day 44 の <see cref="PhysicsWorld.Slop"/> と裏返しの役をしている。
    /// あちらは「めり込みを少し許す」ことで接触を安定させたが、
    /// こちらは<b>少し浮かせる</b>ことで接触が消えないようにする。
    /// ぴったり 0 を目指すと、押し出しの丸め誤差でまた当たり、
    /// 接触の付き外れが毎フレーム起きる。
    ///
    /// <para>
    /// <b>大きすぎてもいけない</b>。1フレームの移動量より大きい skin で押し出すと、
    /// 次のフレームは<b>相手に届かない</b>——当たらないので速度が削られず、
    /// 壁に張り付いたまま壁向きの速度が溜まる。
    /// 歩く速さ 3.2m/s なら1フレーム 53mm なので、
    /// その1桁下(2mm)にしてある。接地判定は
    /// <see cref="GroundProbe"/>(60mm)が別に見ているので、
    /// skin を小さくしても接地は途切れない。
    /// </para>
    /// </summary>
    public float SkinWidth { get; set; } = 0.002f;

    // ===== 動き方(全部「ゲームの都合」の数字)=====

    /// <summary>歩く速さ [m/s]。人の早歩きくらい。</summary>
    public float WalkSpeed { get; set; } = 3.2f;

    /// <summary>走る速さ [m/s]。**歩きの2倍**にしてある(アニメのブレンドが分かりやすい)。</summary>
    public float RunSpeed { get; set; } = 6.4f;

    /// <summary>接地しているときに目標の速度へ寄せる速さ [m/s²]。**大きいほどキビキビ動く**。</summary>
    public float Acceleration { get; set; } = 40.0f;

    /// <summary>
    /// 空中で目標の速度へ寄せる速さ [m/s²]。**接地時よりずっと小さい**。
    ///
    /// 空中で自由に方向転換できると、ジャンプが「飛行」になって緊張感が消える。
    /// かといって 0 にすると壁蹴りも軌道修正もできず、操作していて気持ちよくない。
    /// この数字が<b>ゲームの手触りをいちばん左右するつまみ</b>で、
    /// Quake 以来ここの調整だけで何十年も議論が続いている。
    /// </summary>
    public float AirAcceleration { get; set; } = 8.0f;

    /// <summary>ジャンプで届く高さ [m]。**速度ではなく高さで持つ**と調整しやすい。</summary>
    public float JumpHeight { get; set; } = 1.1f;

    /// <summary>
    /// 重力 [m/s²]。**現実の 9.81 より強い**。
    ///
    /// 現実の重力でジャンプさせると、滞空時間が長すぎて「月面のような」動きになる。
    /// ゲームはたいてい 2〜3 倍にして、<b>上がってすぐ落ちる</b>形にする。
    /// 物理として正しくないが、**キャラクターは物理の外に居る**ので構わない——
    /// 同じ世界の箱や球は <see cref="PhysicsWorld.Gravity"/> の 9.81 で落ちている。
    /// </summary>
    public float Gravity { get; set; } = 22.0f;

    /// <summary>
    /// 登れる坂の上限 [度](要点2)。**これを超えた面は「壁」になる**。
    ///
    /// 45〜55 度あたりが普通。この数字は<b>見た目の傾き</b>ではなく
    /// 面の法線と真上のなす角で測るので、
    /// 「50 度の坂」は法線が真上から 50 度傾いた面のこと。
    /// </summary>
    public float SlopeLimitDegrees { get; set; } = 50.0f;

    /// <summary>
    /// 乗り越えられる段差の高さ [m](要点3)。
    ///
    /// 0.35m は「階段の1段 + 少し」くらい。
    /// <b>大きくしすぎると壁をよじ登る</b>ようになり、
    /// 小さくすると床のわずかな継ぎ目で止まる。
    /// </summary>
    public float StepOffset { get; set; } = 0.35f;

    // ===== 実験用のつまみ =====

    /// <summary>坂の上限を効かせるか。**切ると垂直の壁も登れる**(要点2)。</summary>
    public bool UseSlopeLimit { get; set; } = true;

    /// <summary>段差の乗り越えを効かせるか。**切ると 15cm の段で止まる**(要点3)。</summary>
    public bool UseStepOffset { get; set; } = true;

    // ===== 状態(読むだけ)=====

    /// <summary>速度 [m/s]。**押し戻しのたびに書き換わる**ので、外から入れても消える。</summary>
    public Vector3 Velocity;

    /// <summary>接地しているか(要点4)。</summary>
    public bool IsGrounded { get; private set; }

    /// <summary>立っている面の法線。空中では真上。</summary>
    public Vector3 GroundNormal { get; private set; } = Vector3.UnitY;

    /// <summary>立っている面の傾き [度]。**HUD に出す**と坂の実験が読める。</summary>
    public float GroundSlopeDegrees =>
        MathF.Acos(Math.Clamp(GroundNormal.Y, -1.0f, 1.0f)) * 180.0f / MathF.PI;

    /// <summary>直前のステップで押し戻された回数。**引っかかっていると増える**。</summary>
    public int ResolvedContacts { get; private set; }

    /// <summary>直前のステップで段差を乗り越えたか。</summary>
    public bool SteppedUp { get; private set; }

    /// <summary>直前に乗り越えた段差の高さ [m]。</summary>
    public float LastStepHeight { get; private set; }

    /// <summary>直前のステップで「壁」と判定された面に当たったか(坂の上限を超えた面を含む)。</summary>
    public bool TouchedWall { get; private set; }

    /// <summary>
    /// 向いている方角 [rad]。**絵のためだけの値**で、当たり判定には効かない。
    ///
    /// 移動している向きへ滑らかに回す。Day 42 のブレンドと同じで、
    /// <b>いきなり向きを変えると絵が跳ねる</b>ので、少しずつ寄せる。
    /// </summary>
    public float FacingYaw { get; private set; }

    /// <summary>水平方向の速さ [m/s]。**Day 42 のブレンド木に渡す値**。</summary>
    public float HorizontalSpeed => new Vector2(Velocity.X, Velocity.Z).Length();

    /// <summary>登れる坂の上限を、法線の Y 成分に直したもの。**比較はこの形が速い**。</summary>
    public float SlopeLimitCosine =>
        MathF.Cos(SlopeLimitDegrees * MathF.PI / 180.0f);

    /// <summary>線分の半分の長さ [m]。**全高から半径2つぶんを引いた残りの半分**。</summary>
    public float HalfHeight => MathF.Max((Height * 0.5f) - Radius, 0.0f);

    /// <summary>今の当たり判定の形。</summary>
    public Capsule3D ToCapsule() => ToCapsule(Position);

    /// <summary>
    /// 足元をここに置いたときの形。**「もしそこへ動いたら」を試すのに使う**。
    ///
    /// 段差の乗り越えも接地の探りも、
    /// 「別の場所に置いたカプセル」を作って当ててみるだけでできている。
    /// <b>形が値(<c>readonly struct</c>)であることの利き所</b>で、
    /// 本体を動かさずに何度でも試せる。
    /// </summary>
    public Capsule3D ToCapsule(Vector3 footPosition)
    {
        Vector3 lower = footPosition + new Vector3(0.0f, Radius, 0.0f);
        Vector3 upper = footPosition + new Vector3(0.0f, Height - Radius, 0.0f);
        return new Capsule3D(lower, upper, Radius);
    }

    /// <summary>
    /// 1ステップ動かす。**固定 dt で呼ぶこと**(要点1)。
    ///
    /// 順番はこう。
    /// <list type="number">
    /// <item>入力から<b>目標の水平速度</b>を作り、加速度で寄せる</item>
    /// <item>ジャンプと重力で<b>縦の速度</b>を決める</item>
    /// <item><b>水平に動かす</b>(ここで段差を試す)</item>
    /// <item><b>縦に動かす</b></item>
    /// <item><b>接地を調べ直す</b>(必要なら地面に吸い付ける)</item>
    /// </list>
    ///
    /// <para>
    /// <b>3 と 4 を分けるのが肝</b>(要点3)。一度に動かすと、
    /// 段差に当たったときに「壁にぶつかったのか、床に着いたのか」が区別できない。
    /// 分けておけば、水平の移動が止められたときだけ段差を試せばよくなる。
    /// Quake の <c>PM_StepSlideMove</c> から続く定石で、
    /// <b>斜めに走って段を上がる</b>ような場面でも破綻しない。
    /// </para>
    ///
    /// <para>
    /// <paramref name="wish"/> は<b>長さ 0〜1 の水平ベクトル</b>。
    /// カメラを基準にするかどうかは呼ぶ側(<c>Program</c>)の仕事で、
    /// ここは「どちらへ行きたいか」しか知らない——
    /// Day 18 の <see cref="InputSnapshot"/> が
    /// 「どのキーか」を知らなかったのと同じ線を引いている。
    /// </para>
    /// </summary>
    public void Move(PhysicsWorld world, Vector3 wish, bool run, bool jump, float dt)
    {
        if (dt <= 0.0f)
        {
            return;
        }

        ResolvedContacts = 0;
        SteppedUp = false;
        LastStepHeight = 0.0f;
        TouchedWall = false;

        // --- 1. 水平の速度 ---
        //
        // **目標へ寄せる**だけ。摩擦も慣性も物理から来ていない。
        // 接地しているときだけキビキビ寄せるので、
        // 空中では方向転換が鈍くなる(<see cref="AirAcceleration"/>)。
        Vector3 target = wish * (run ? RunSpeed : WalkSpeed);
        float acceleration = IsGrounded ? Acceleration : AirAcceleration;

        var horizontal = new Vector3(Velocity.X, 0.0f, Velocity.Z);
        Vector3 delta = target - horizontal;
        float distance = delta.Length();

        if (distance > 1e-6f)
        {
            // **行き過ぎないように切り詰める**。
            // `v += (目標 - v) * a * dt` と書くと dt に依存して
            // フレームレートで手触りが変わる。
            float step = MathF.Min(acceleration * dt, distance);
            horizontal += delta * (step / distance);
        }

        Velocity.X = horizontal.X;
        Velocity.Z = horizontal.Z;

        // --- 2. 縦の速度 ---
        if (jump && IsGrounded)
        {
            // **高さから初速を出す**。v = sqrt(2 g h)。
            // 「1.1m 跳べる」と書けるほうが、「初速 6.96 m/s」より調整しやすい。
            Velocity.Y = MathF.Sqrt(2.0f * Gravity * JumpHeight);
            IsGrounded = false;
        }
        else if (IsGrounded && Velocity.Y <= 0.0f)
        {
            // **接地中は縦に動かさない**。床へ張り付ける仕事は
            // <see cref="UpdateGround"/> の吸い付きに任せる。
            //
            // ここで「少し下へ押し付ける」(たとえば -2 m/s)と書きたくなるが、
            // それをやると<b>段の縁で前へ進めなくなる</b>。
            // 押し付けたぶんだけ段の縁にめり込み、その接触は法線が寝ているので
            // 「壁」として<b>横へ押し戻される</b>——前に進んだぶんがちょうど帳消しになり、
            // しかも「押し戻された」ことが水平移動の側から見えないので
            // 段差の処理も走らない。**丸一日はまった**のがここ。
            Velocity.Y = 0.0f;
        }
        else
        {
            Velocity.Y -= Gravity * dt;
        }

        // --- 3. 水平に動かす(段差を試すのはこの中)---
        var horizontalStep = new Vector3(Velocity.X * dt, 0.0f, Velocity.Z * dt);
        MoveHorizontal(world, horizontalStep);

        // --- 4. 縦に動かす ---
        MoveAndSlide(world, new Vector3(0.0f, Velocity.Y * dt, 0.0f));

        // --- 5. 接地を調べ直す ---
        UpdateGround(world);

        // --- 向き(絵のためだけ)---
        UpdateFacing(dt);
    }

    /// <summary>
    /// 水平に動かす。**進めなかったら段差を試す**(要点3)。
    ///
    /// 手順は「まず素直に動かしてみて、進めなかったら、
    /// <b>行く先を覗いて、段だったらその高さまで持ち上げてから、もう一度進む</b>」。
    ///
    /// <code>
    ///        覗く位置(半径ぶん前・段差ぶん上)
    ///          ↓
    ///   ○──→ ┃▓▓▓▓        ○ ← 段の高さまで持ち上げてから進む
    ///   ▔▔▔▔▔▔▔▔▔▔▔      ▔▔▔┃▓▓▓▓
    /// </code>
    ///
    /// <para>
    /// <b>「持ち上げて進んで落とす」ではない</b>のが Day 45 でいちばん手こずったところ。
    /// 落とす形で書くと、カプセルの丸い底が<b>段の角に当たって止まる</b>——
    /// そのときの法線は寝ている(半径 35cm・段 30cm なら真上から 57 度)ので、
    /// 「登れない壁」と見なされて弾かれてしまう。
    /// 1フレームで進める距離(5cm)では角を越えられないので、
    /// <b>何フレームかけても越えられない</b>。
    /// </para>
    ///
    /// <para>
    /// 先に覗いてしまえばこれが起きない。
    /// <see cref="TryFindStep"/> は<b>半径ぶん前方</b>を見るので、
    /// 段なら平らな上面(法線は真上)が返り、坂なら覗く位置そのものが埋まって弾かれる。
    /// <b>「段か坂か」を、法線ではなく覗いた先の形で決めている</b>のが肝。
    /// </para>
    ///
    /// <para>
    /// 持ち上げたあと落とさないので、キャラクターは何フレームか
    /// <b>段の上の空中を歩く</b>ことになる。0.05 秒ほどで段の縁に足が乗るので、
    /// 絵としては「滑らかに段を登った」ように見える。
    /// </para>
    /// </summary>
    private void MoveHorizontal(PhysicsWorld world, Vector3 displacement)
    {
        Vector3 start = Position;
        Vector3 startVelocity = Velocity;

        // --- まず素直に ---
        MoveAndSlide(world, displacement);

        Vector3 plain = Position;
        Vector3 plainVelocity = Velocity;

        float wanted = new Vector2(displacement.X, displacement.Z).LengthSquared();
        float advanced = new Vector2(plain.X - start.X, plain.Z - start.Z).LengthSquared();

        // **ほとんど進めた**か、そもそも動くつもりが無いなら、段差の出番は無い。
        if (advanced >= wanted - 1e-8f || wanted < 1e-10f)
        {
            return;
        }

        TouchedWall = true;

        // 空中で段差を登れると、壁を蹴って上がれてしまう。
        if (!UseStepOffset || !IsGrounded)
        {
            return;
        }

        var direction = Vector3.Normalize(new Vector3(displacement.X, 0.0f, displacement.Z));

        if (!TryFindStep(world, start, direction, out float stepTop))
        {
            return;
        }

        // --- 段の高さまで持ち上げて、もう一度進む ---
        Position = new Vector3(start.X, stepTop + SkinWidth, start.Z);
        Velocity = startVelocity;

        MoveAndSlide(world, displacement);

        float stepped = new Vector2(Position.X - start.X, Position.Z - start.Z).LengthSquared();

        // 持ち上げても進めなかったなら、素直に動かした結果へ戻す。
        // **1mm ぶんの余裕**を付けて、誤差で毎フレーム行き来しないようにする。
        if (stepped <= advanced + 1e-6f)
        {
            Position = plain;
            Velocity = plainVelocity;
            return;
        }

        SteppedUp = true;
        LastStepHeight = stepTop - start.Y;

        // 段を登ったぶんの落下速度は捨てる。**残しておくと段の上で沈む**。
        if (Velocity.Y < 0.0f)
        {
            Velocity.Y = 0.0f;
        }
    }

    /// <summary>
    /// 行く先に「登れる段」があるかを覗く(要点3)。**位置は動かさない**。
    ///
    /// <list type="number">
    /// <item><b>半径ぶん前、段差ぶん上</b>にカプセルを置いてみる。埋まっていたら段ではない</item>
    /// <item>そこから<b>小刻みに下ろして</b>、最初に触れた面を探す</item>
    /// <item>その面が<b>歩ける床</b>で、高さが段差の範囲なら「段」</item>
    /// </list>
    ///
    /// <para>
    /// <b>1 が坂を弾く</b>。急な坂では、半径ぶん前方の地面が
    /// 段差ぶんより高く上がっているので、覗いた位置が坂の中に埋まる。
    /// 45 度なら 0.35m 前で 0.35m 上がるので、ちょうど境目——
    /// つまり<b>「段差ぶん持ち上げても、半径ぶん前へ進めない」勾配は段ではない</b>
    /// という、素直な基準になっている。
    /// </para>
    ///
    /// <para>
    /// <b>2 を小刻みにやるのは掃引(スイープ)の代わり</b>。
    /// 一気に下ろすと段の側面へ深くめり込んだ姿勢で当たりが出るので、
    /// 「段の上面」ではなく「段の角」を拾ってしまう。
    /// </para>
    ///
    /// <para>
    /// <b>3 の「歩ける床か」でも坂が弾かれる</b>(1 をすり抜けた緩い坂の場合)。
    /// もっとも緩い坂ならそもそも段差を使わずに登れるので、実害は無い。
    /// </para>
    /// </summary>
    /// <param name="stepTop">見つかった段の上面の高さ(足元の座標)。</param>
    private bool TryFindStep(
        PhysicsWorld world, Vector3 start, Vector3 direction, out float stepTop)
    {
        stepTop = 0.0f;

        // **半径ぶん前へ**。ここを短くすると段の角を拾ってしまい、
        // 長くすると幅の狭い段(細い足場)に乗れなくなる。
        Vector3 probe = start
            + new Vector3(0.0f, StepOffset, 0.0f)
            + (direction * (Radius + SkinWidth));

        // 覗く位置が埋まっている = 壁か、段差より急な坂。
        if (world.OverlapCapsule(ToCapsule(probe)))
        {
            return false;
        }

        float drop = StepOffset / DropSteps;
        float walkable = UseSlopeLimit ? SlopeLimitCosine : SteppingUpness;

        for (int i = 0; i < DropSteps; i++)
        {
            probe.Y -= drop;

            int count = world.QueryCapsule(ToCapsule(probe), _hits);
            if (count == 0)
            {
                continue;
            }

            // いちばん平らな面を段の上面とみなす。
            int best = 0;
            for (int k = 1; k < count; k++)
            {
                if (_hits[k].Normal.Y > _hits[best].Normal.Y)
                {
                    best = k;
                }
            }

            float upness = _hits[best].Normal.Y;
            if (upness < walkable)
            {
                // 触れたのが歩けない面(坂・壁の角)。**段ではない**。
                return false;
            }

            // めり込んだぶんを真上へ戻したところが、段の上面の高さ。
            stepTop = probe.Y + (_hits[best].MaxDepth / upness);

            return stepTop > start.Y + 1e-4f
                && stepTop <= start.Y + StepOffset + 1e-4f;
        }

        // 段差ぶん下ろしても何も無い = 段ではなく、向こうは崖。
        return false;
    }

    /// <summary>
    /// 動かして、めり込んだぶんを押し戻す。**今日の心臓部**(要点1)。
    ///
    /// <list type="number">
    /// <item>まず<b>行きたい場所へ動かしてしまう</b>(めり込んでよい)</item>
    /// <item>当たっている面を集めて、<b>法線の向きへ押し戻す</b></item>
    /// <item>速度から<b>面へ食い込む成分を抜く</b>(これが「滑る」の正体)</item>
    /// <item>2〜3 を数周する</item>
    /// </list>
    ///
    /// <para>
    /// <b>3 が滑りを作る</b>。速度を消すのではなく、
    /// 面に垂直な成分だけを取り除くので、面に沿った成分がそのまま残る——
    /// 壁に斜めに突っ込むと壁沿いに流れるのはこれ。
    /// <c>v -= n (v·n)</c> の1行しかなく、
    /// <b>Day 43 のインパルスの式から「跳ね返り」を抜いたもの</b>と見ることもできる。
    /// </para>
    ///
    /// <para>
    /// <b>数周するのは、押し戻しが打ち消し合うから</b>。
    /// 部屋の角では2枚の壁に同時に当たり、
    /// 片方を押し戻すともう片方へ深く入る。
    /// Day 44 の <see cref="PhysicsWorld.PositionIterations"/> と同じ事情で、
    /// <b>周ごとに当たり直しから測り直す</b>ことが要る。
    /// </para>
    ///
    /// <para>
    /// <b>掃引(スイープ)はしていない</b>。動かしてから押し戻すだけなので、
    /// 1ステップで自分の厚みより多く動くと薄い壁を抜ける。
    /// 走る速さ 6.4 m/s で 60Hz なら1ステップ 10.7cm、
    /// カプセルの直径 70cm なのでまだ余裕がある——
    /// **速い弾には別の手(CCD)が要る**のは Day 44 に書いたとおり。
    /// </para>
    /// </summary>
    private void MoveAndSlide(PhysicsWorld world, Vector3 displacement, bool stepping = false)
    {
        Position += displacement;

        // 「床」と見なす法線の下限。段差を降ろしている間はわざと緩める。
        float walkable = stepping
            ? SteppingUpness
            : (UseSlopeLimit ? SlopeLimitCosine : 0.3f);

        for (int pass = 0; pass < ResolvePasses; pass++)
        {
            int count = world.QueryCapsule(ToCapsule(), _hits);
            if (count == 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                ContactManifold manifold = _hits[i];

                // 法線は**カプセル(A)を体(B)から引き離す向き**。
                Vector3 normal = manifold.Normal;
                float depth = manifold.MaxDepth;

                ResolvedContacts++;

                // --- 歩ける床: **真上へ押し出す**(要点2)---
                //
                // 法線の向きへ押し戻すのが素直だが、**坂ではそれが横滑りになる**。
                // 30 度の坂に立っているだけで、重力ぶんの押し戻しが
                // 毎フレーム斜め下へ体をずらし、じりじり滑り落ちてしまう
                // (摩擦を入れていないので、止める理由が無い)。
                //
                // 真上へ <c>深さ / 法線のY</c> だけ持ち上げれば、
                // めり込みは同じだけ解けて<b>横へは1ミリも動かない</b>。
                // 平らな床(法線のY = 1)ではこれまでと同じ式になる。
                if (normal.Y >= walkable)
                {
                    Position.Y += (depth + SkinWidth) / normal.Y;

                    if (Velocity.Y < 0.0f)
                    {
                        Velocity.Y = 0.0f;
                    }

                    continue;
                }

                // --- 坂の上限(要点2)---
                //
                // 登れない急な面は「壁」として扱う。押し戻す向きから
                // **上向きの成分を抜く**と、その面に沿って登れなくなる。
                // 抜かないと、押し戻しの上向き成分が「登り」として効いてしまう——
                // <see cref="UseSlopeLimit"/> を切ると、まさにそれで
                // 垂直に近い面もよじ登れるようになる。
                if (UseSlopeLimit && !stepping && normal.Y > 0.0f)
                {
                    TouchedWall = true;

                    normal.Y = 0.0f;
                    float length = normal.Length();

                    // 真上に近い法線はここへ来ない(上の枝で拾っている)ので、
                    // 長さが 0 になるのは数値誤差のときだけ。
                    if (length < 1e-4f)
                    {
                        continue;
                    }

                    normal /= length;
                }

                // --- 押し出す ---
                //
                // **skin ぶん余分に出す**。ぴったり接するところで止めると、
                // 押し出しの丸め誤差でまた当たる。
                Position += normal * (depth + SkinWidth);

                // --- 速度を面に沿わせる ---
                float into = Vector3.Dot(Velocity, normal);
                if (into < 0.0f)
                {
                    Velocity -= normal * into;
                }
            }
        }
    }

    /// <summary>
    /// 接地しているかを調べ直し、床へ吸い付ける。**今日いちばん細かい 30 行**(要点4)。
    ///
    /// 押し戻しの結果だけで判定すると、<b>接地が毎フレーム途切れる</b>。
    /// <see cref="SkinWidth"/> ぶん浮かせているので、
    /// 立っているだけのときは床とどこにも当たっていないことになるからで、
    /// これでは「歩いているのに常に落下中」になってジャンプが出せない。
    ///
    /// <para>
    /// そこで<b>少し下げた位置のカプセル</b>を当ててみる(<see cref="Probe"/>)。
    /// 当たった面のうち、登れる傾き(<see cref="SlopeLimitDegrees"/> 以内)の
    /// いちばん平らなものを立っている床に採る。
    /// <b>登れない急な面は床に数えない</b>——数えてしまうと、
    /// 60 度の斜面に立ってジャンプできることになり、上限がただの飾りになる。
    /// </para>
    ///
    /// <para>
    /// <b>探る距離は状況で変える</b>。普段は <see cref="GroundProbe"/>(6cm)だけ見るが、
    /// 直前まで接地していて落ちようとしているときは
    /// <see cref="StepOffset"/>(35cm)まで伸ばす。
    /// これが<b>床への吸い付き</b>で、階段や坂を下るときに
    /// 「一瞬浮いて、落ちて、着地する」を繰り返さずに済む。
    /// </para>
    ///
    /// <para>
    /// <b>見つけた床までは実際に下ろす</b>。見つけただけで下ろさないと、
    /// 坂を下る間じわじわ浮いていって、6cm 溜まったところで落ちる——
    /// という周期的なガタつきになる。
    /// </para>
    ///
    /// <para>
    /// <b>ただし、下ろして後ろへ押し戻されたら取り消す</b>。
    /// 下ろす途中で段の側面に当たると横へ弾かれるので、
    /// せっかく前へ進んだぶんが帳消しになる。
    /// <b>取り消すのは「動き」だけで、「接地している」という判断は残す</b>——
    /// 真下に床があることは <see cref="Probe"/> が確かめているので、
    /// 段の縁に引っかかって少し浮いているだけなら、接地扱いのままでよい。
    /// ここを落とすと、段を登りかけたキャラクターが空中扱いになり、
    /// 段差の処理(<see cref="MoveHorizontal"/>)が走らなくなって<b>永久に登れない</b>。
    /// </para>
    /// </summary>
    private void UpdateGround(PhysicsWorld world)
    {
        bool wasGrounded = IsGrounded;

        IsGrounded = false;
        GroundNormal = Vector3.UnitY;

        // **上へ動いている間は接地しない**。ジャンプの出だしは床のすぐ上に居るので、
        // ここで拾ってしまうと1フレームで何度でも跳べる。
        if (Velocity.Y > 0.01f)
        {
            return;
        }

        // 段や坂を下るときだけ、深くまで探る(= 吸い付き)。
        float reach = wasGrounded && UseStepOffset ? StepOffset : GroundProbe;

        if (!Probe(world, reach, out Vector3 normal))
        {
            return;
        }

        // **床はある**。ここから下は「そこへ寄せる」だけの話なので、
        // 寄せられなくても接地の判断は変えない。
        IsGrounded = true;
        GroundNormal = normal;

        Vector3 before = Position;
        Vector3 beforeVelocity = Velocity;

        MoveAndSlide(world, new Vector3(0.0f, -reach, 0.0f));

        // 後ろへ押し戻されたなら、その吸い付きは取り消す。
        var moved = new Vector2(Position.X - before.X, Position.Z - before.Z);
        var heading = new Vector2(beforeVelocity.X, beforeVelocity.Z);

        if (Vector2.Dot(moved, heading) < -1e-6f)
        {
            Position = before;
            Velocity = beforeVelocity;
        }
    }

    /// <summary>
    /// 足元を <paramref name="distance"/> だけ下げたカプセルを当ててみて、
    /// **歩ける床があるか**を答える。位置は動かさない。
    /// </summary>
    private bool Probe(PhysicsWorld world, float distance, out Vector3 groundNormal)
    {
        groundNormal = Vector3.UnitY;

        Vector3 probePosition = Position - new Vector3(0.0f, distance, 0.0f);
        int count = world.QueryCapsule(ToCapsule(probePosition), _hits);

        float limit = SlopeLimitCosine;
        float flattest = UseSlopeLimit ? limit : 0.05f;
        bool found = false;

        for (int i = 0; i < count; i++)
        {
            float upness = _hits[i].Normal.Y;

            // **いちばん平らな面を採る**。坂と壁に同時に触れているとき、
            // 壁を「床」にしてしまうと傾きの表示も接地判定も嘘になる。
            if (upness > flattest)
            {
                flattest = upness;
                groundNormal = _hits[i].Normal;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// 向いている方角を更新する。**絵のためだけ**(当たり判定には効かない)。
    ///
    /// 動いている向きへ、1秒あたり一定の角度で寄せる。
    /// <b>角度は -π と π がつながっている</b>ので、
    /// 単純な線形補間だと 179 度から -179 度へ回るときに
    /// <b>ぐるりと逆回り</b>する。差を ±π に畳んでから寄せるのはそのため——
    /// Day 41 でクォータニオンを使ったのと同じ問題が、
    /// 角度1つでも起きる。
    /// </summary>
    private void UpdateFacing(float dt)
    {
        var flat = new Vector2(Velocity.X, Velocity.Z);
        if (flat.LengthSquared() < 0.04f)
        {
            return;
        }

        float wanted = MathF.Atan2(flat.X, flat.Y);
        float difference = wanted - FacingYaw;

        // ±π に畳む。
        while (difference > MathF.PI)
        {
            difference -= MathF.Tau;
        }

        while (difference < -MathF.PI)
        {
            difference += MathF.Tau;
        }

        const float turnSpeed = 12.0f;   // rad/s。**速すぎると絵がカクつく**
        float step = Math.Clamp(turnSpeed * dt, 0.0f, 1.0f);

        FacingYaw += difference * step;
    }

    /// <summary>
    /// 足元を指定の場所へ置き直す。**速度も状態も全部消す**。
    ///
    /// <para>
    /// <paramref name="facingYaw"/> は Day 51 で足した。
    /// シーンの出発点(<see cref="DemoScene.PlayerYaw"/>)には向きも書いてあるので、
    /// 置き直したあとに <see cref="FacingYaw"/> を外から書ける必要が出た——
    /// <b>省略すれば今までどおり北(+Z)向き</b>なので、Day 45〜50 の呼び出しは変わらない。
    /// </para>
    /// </summary>
    public void Teleport(Vector3 footPosition, float facingYaw = 0.0f)
    {
        Position = footPosition;
        Velocity = Vector3.Zero;
        IsGrounded = false;
        GroundNormal = Vector3.UnitY;
        FacingYaw = facingYaw;
        ResolvedContacts = 0;
        SteppedUp = false;
        LastStepHeight = 0.0f;
        TouchedWall = false;
    }
}
