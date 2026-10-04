using System.Numerics;
using System.Text.Json.Serialization;

namespace HonyaEngine;

/// <summary>
/// 「何の絵を、どう出すか」だけを持つ。**ふるまいは一切持たない**。
///
/// 描画そのものはここではやらない。やろうとすると
/// <c>SpriteBatch</c> やアトラスへの参照をコンポーネントが握ることになり、
/// 「絵のデータ」と「描き方」が癒着する。
/// <see cref="Scene"/> を歩いてこれを集め、まとめて積むのは呼び出し側の仕事
/// (Day 17 で「まとめる仕事はバッチ側に移った」と書いたのと同じ理由)。
///
/// **中身がデータだけのコンポーネント**は珍しくない。
/// Day 23 の ECS では、むしろこれが標準の形になる。
/// </summary>
internal sealed class SpriteRenderer : Component
{
    /// <summary>どの絵か(Program の <c>SpriteNames</c> の添字)。</summary>
    public int Kind { get; set; }

    public float Size { get; set; } = 32.0f;

    public Vector4 Color { get; set; } = Vector4.One;

    /// <summary>重ね順。</summary>
    public float Layer { get; set; }
}

/// <summary>
/// 画面の中を等速で飛び回り、端で跳ね返る。Day 17 からのスプライトの動きを
/// **そのままコンポーネントにしたもの**。
///
/// 見比べる相手は Program の <c>UpdateSprites</c>(構造体の配列を回すほう)。
/// やっている計算はほぼ同じで、違うのは
///   - 状態が構造体の配列ではなく、ヒープ上のオブジェクトに散らばっている
///   - 呼び出しが直接ではなく仮想呼び出しになる
///   - 回転がクォータニオン経由になる(<c>SetLocalRotationZ</c> の sin/cos)
/// の3点。**これで 2万個の更新が 0.08ms から 1.37ms になる**(計画書の要点5)。
/// </summary>
internal sealed class BouncingMover : Component
{
    private float _angle;
    private float _halfSize;

    public Vector2 Velocity { get; set; }

    public float SpinSpeed { get; set; }

    /// <summary>
    /// <see cref="SpriteRenderer"/> から大きさをもらう。
    ///
    /// **これが <c>GetComponent</c> の正しい使い方**。
    /// 毎フレーム引くのではなく、<c>Start</c> で1回引いて手元に持つ。
    /// <c>Awake</c> ではなく <c>Start</c> なのは、
    /// <c>AddComponent</c> の順番に依存しないため(<see cref="Component.Start"/> 参照)。
    /// </summary>
    protected internal override void Start()
    {
        _halfSize = (GameObject.GetComponent<SpriteRenderer>()?.Size ?? 32.0f) * 0.5f;
        _angle = 2.0f * MathF.Atan2(Transform.LocalRotation.Z, Transform.LocalRotation.W);
    }

    protected internal override void FixedUpdate(float deltaTime)
    {
        Vector3 position = Transform.LocalPosition;
        Vector2 velocity = Velocity;

        position.X += velocity.X * deltaTime;
        position.Y += velocity.Y * deltaTime;

        Vector2 bounds = GameObject.Scene.Bounds;

        if (position.X < _halfSize)
        {
            position.X = _halfSize;
            velocity.X = -velocity.X;
        }
        else if (position.X > bounds.X - _halfSize)
        {
            position.X = bounds.X - _halfSize;
            velocity.X = -velocity.X;
        }

        if (position.Y < _halfSize)
        {
            position.Y = _halfSize;
            velocity.Y = -velocity.Y;
        }
        else if (position.Y > bounds.Y - _halfSize)
        {
            position.Y = bounds.Y - _halfSize;
            velocity.Y = -velocity.Y;
        }

        Velocity = velocity;
        Transform.LocalPosition = position;

        _angle += SpinSpeed * deltaTime;
        Transform.SetLocalRotationZ(_angle);
    }
}

/// <summary>
/// 親のまわりを回る。**階層のありがたみを見るためだけの部品**。
///
/// 自分は「原点から半径 R の円周上を回る」としか書いていない。
/// それが画面のどこに出るかは**親が決める**。
/// 親を動かせば子はついてくるし、親の親を動かせば孫までついてくる。
/// これを親子関係なしで書くと、子が親の位置を知っていなければならなくなる。
/// </summary>
internal sealed class OrbitMover : Component
{
    private float _angle;
    private float _spin;

    public float Radius { get; set; } = 100.0f;

    public float AngularSpeed { get; set; } = 1.0f;

    public float StartAngle { get; set; }

    public float SpinSpeed { get; set; }

    protected internal override void Start() => _angle = StartAngle;

    protected internal override void FixedUpdate(float deltaTime)
    {
        _angle += AngularSpeed * deltaTime;
        _spin += SpinSpeed * deltaTime;

        Transform.LocalPosition = new Vector3(
            MathF.Cos(_angle) * Radius,
            MathF.Sin(_angle) * Radius,
            0.0f);

        Transform.SetLocalRotationZ(_spin);
    }
}

/// <summary>
/// 矢印キーで動くプレイヤー。Day 20 の <c>UpdatePlayer</c> を移してきたもの。
///
/// 計算は1文字も変えていない。変わったのは**置き場所**だけ。
///   - Day 21 まで … Program の static フィールド6個 + static メソッド
///   - 今日        … この1クラス
/// 「プレイヤーをもう1人出す」が、Program をいじらずに
/// <c>AddComponent&lt;PlayerController&gt;()</c> だけでできるようになった。
/// **合成で組む見返りは、たいていこういう地味な形で返ってくる**。
/// </summary>
internal sealed class PlayerController : Component
{
    private const float Acceleration = 2600.0f;
    private const float MaxSpeed = 480.0f;
    private const float Friction = 6.0f;
    private const float DashSpeed = 1200.0f;
    private const float DashInterval = 0.45f;
    private const float HalfSize = 34.0f;

    /// <summary>
    /// 速度。**これは保存する**。
    ///
    /// シーンファイルは「初期状態」を書くものなので、
    /// 「最初から右へ飛んでいる敵」を置きたければ速度は初期値の一部になる。
    /// </summary>
    public Vector2 Velocity { get; set; }

    /// <summary>
    /// ダッシュの残り時間。**保存しない**。
    ///
    /// <see cref="Velocity"/> との違いが要点3。
    /// これは「今まさに走っている最中の一時的な値」で、
    /// シーンの初期状態としては意味を持たない。
    /// セーブデータ(途中から再開する)なら保存するが、それはシーンとは別の話。
    /// **同じ形式で両方をまかなおうとすると、必ずどちらかが歪む**。
    /// </summary>
    [JsonIgnore]
    public float DashCooldown { get; private set; }

    /// <summary>
    /// 向き(ラジアン)。<see cref="Transform"/> にも入れるが、こちらでも持っている。
    ///
    /// クォータニオンから角度を取り出すには <c>atan2</c> が要るうえ、
    /// 取り出した角度は必ず -π〜π に折り返される。
    /// **足し続ける値は、素の float で持っておくほうが素直**。
    ///
    /// 保存しない。向きは <see cref="Transform"/> 側に入っているので二重になる。
    /// </summary>
    [JsonIgnore]
    public float Angle { get; private set; }

    /// <summary>再生や巻き戻しのために、状態をまとめて出し入れする。</summary>
    [JsonIgnore]
    public (Vector2 Position, Vector2 Velocity, float Angle, float DashCooldown) State
    {
        get => (new Vector2(Transform.LocalPosition.X, Transform.LocalPosition.Y), Velocity, Angle, DashCooldown);
        set
        {
            Transform.LocalPosition = new Vector3(value.Position.X, value.Position.Y, 0.0f);
            Velocity = value.Velocity;
            Angle = value.Angle;
            DashCooldown = value.DashCooldown;
            Transform.SetLocalRotationZ(Angle);

            // 巻き戻したら補間の起点もそろえる。
            // そろえないと、戻した瞬間に元の位置から線を引いて飛ぶ。
            Transform.Snapshot();
        }
    }

    private SpriteRenderer? _renderer;

    /// <summary>
    /// 同じ <c>GameObject</c> に付いている絵を1回だけ引いて持つ。
    ///
    /// **コンポーネントどうしが話す典型的な形**。
    /// 「入力を読んで動く」係が「絵の色を変える」係を直接知らずに済むよう、
    /// 間に <see cref="SpriteRenderer"/> というデータを挟んでいる。
    /// </summary>
    protected internal override void Start() => _renderer = GameObject.GetComponent<SpriteRenderer>();

    protected internal override void FixedUpdate(float deltaTime)
    {
        InputSnapshot input = GameObject.Scene.Input;

        Vector2 position = new(Transform.LocalPosition.X, Transform.LocalPosition.Y);
        Vector2 velocity = Velocity;

        Vector2 axis = input.MoveAxis;
        velocity += axis * Acceleration * deltaTime;

        // ダッシュは**押した瞬間だけ**。Held で書くと押しっぱなしで加速し続ける(Day 20 要点2)。
        DashCooldown = MathF.Max(0.0f, DashCooldown - deltaTime);
        if (input.WasPressed(GameAction.Dash) && DashCooldown <= 0.0f)
        {
            Vector2 direction = axis != Vector2.Zero
                ? axis
                : (velocity != Vector2.Zero ? Vector2.Normalize(velocity) : Vector2.UnitX);

            velocity += direction * DashSpeed;
            DashCooldown = DashInterval;
        }

        // 摩擦。速度に比例して減速させる。
        // (1 - friction*dt) は指数減衰の1次近似で、**dt が固定だから安心して使える**。
        velocity *= MathF.Max(0.0f, 1.0f - (Friction * deltaTime));

        float speed = velocity.Length();
        if (speed > MaxSpeed && DashCooldown <= 0.0f)
        {
            velocity = velocity / speed * MaxSpeed;
        }

        position += velocity * deltaTime;

        // 向きではなく速さで回す。向き(atan2)で回すと -π と π をまたいだ瞬間に
        // 補間が画面を1周してしまう(Day 19 要点3の「瞬間移動」と同じ問題)。
        Angle += speed * deltaTime * 0.012f;

        Vector2 bounds = GameObject.Scene.Bounds;

        if (position.X < HalfSize)
        {
            position.X = HalfSize;
            velocity.X = MathF.Abs(velocity.X) * 0.4f;
        }
        else if (position.X > bounds.X - HalfSize)
        {
            position.X = bounds.X - HalfSize;
            velocity.X = -MathF.Abs(velocity.X) * 0.4f;
        }

        if (position.Y < HalfSize)
        {
            position.Y = HalfSize;
            velocity.Y = MathF.Abs(velocity.Y) * 0.4f;
        }
        else if (position.Y > bounds.Y - HalfSize)
        {
            position.Y = bounds.Y - HalfSize;
            velocity.Y = -MathF.Abs(velocity.Y) * 0.4f;
        }

        Velocity = velocity;
        Transform.LocalPosition = new Vector3(position.X, position.Y, 0.0f);
        Transform.SetLocalRotationZ(Angle);

        // ダッシュのクールダウン中は色を落として、
        // **押した瞬間しか効かないアクション**が視覚的に分かるようにしてある。
        if (_renderer is not null)
        {
            _renderer.Color = DashCooldown > 0.0f
                ? new Vector4(1.00f, 0.55f, 0.30f, 0.95f)
                : new Vector4(1.00f, 0.95f, 0.35f, 1.00f);
        }
    }
}

/// <summary>
/// ライフサイクルの呼ばれ方をコンソールに出すだけの部品。
///
/// <c>Awake</c> / <c>OnEnable</c> / <c>Start</c> / <c>FixedUpdate</c> /
/// <c>OnDisable</c> / <c>OnDestroy</c> が**どの順で、いつ**呼ばれるかは
/// 説明を読むより1回流したほうが早い。
/// </summary>
internal sealed class LifecycleLogger : Component
{
    private int _steps;

    public string Label { get; set; } = "obj";

    protected internal override void Awake() => Log("Awake       (AddComponent した直後)");

    protected internal override void OnEnable() => Log("OnEnable");

    protected internal override void Start() => Log("Start       (最初の FixedUpdate の直前)");

    protected internal override void FixedUpdate(float deltaTime)
    {
        _steps++;
        if (_steps <= 2)
        {
            Log($"FixedUpdate ({_steps} 回目)");
        }
    }

    protected internal override void OnDisable() => Log("OnDisable");

    protected internal override void OnDestroy() => Log($"OnDestroy   (合計 {_steps} ステップ動いた)");

    private void Log(string message) => Console.WriteLine($"    [{Label}] {message}");
}
