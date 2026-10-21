using System.Numerics;

namespace HonyaEngine;

/// <summary>どこから、どの向きへ撒くか。</summary>
internal enum ParticleShape
{
    /// <summary>1点から、円錐の中へ。焚き火・噴射口。</summary>
    Cone,

    /// <summary>球の中からランダムな向きへ。爆発。</summary>
    Sphere,

    /// <summary>水平な円盤の上から、外へ広がりながら。**足元の土埃**(Day 50 で使う)。</summary>
    Disc,
}

/// <summary>
/// 粒1つぶん。**構造体にして配列で持つ**(Day 17 の <c>SpriteInstance</c> と同じ判断)。
///
/// <para>
/// 1万個をクラスにすると、1万個のオブジェクトが個別にヒープへ散らばる。
/// 毎フレーム全部を舐める処理では、**メモリの並びがそのまま速度になる**——
/// 構造体の配列なら、次の粒は必ず隣にある。
/// </para>
///
/// <para>
/// <b>寿命を「経過」と「上限」の2つで持つ</b>のは、
/// 正規化した位置 <c>Age / Lifetime</c>(0→1)が色・大きさ・コマ番号の
/// 全部の入力になるため。残り時間で持つと毎回引き算が要るし、
/// 0→1 に正規化するのに結局 <c>Lifetime</c> が要る。
/// </para>
/// </summary>
internal struct Particle
{
    public Vector3 Position;

    public Vector3 Velocity;

    /// <summary>生まれてからの秒数。</summary>
    public float Age;

    /// <summary>寿命(秒)。これを超えたら消す。</summary>
    public float Lifetime;

    /// <summary>ビルボードの回転(ラジアン)。**煙は回さないと同じ絵の繰り返しに見える**。</summary>
    public float Rotation;

    /// <summary>回転の速さ(ラジアン/秒)。</summary>
    public float Spin;

    /// <summary>
    /// 粒ごとの揺らぎ(0〜1)。生まれたときに1回決めて、以後変えない。
    ///
    /// <para>
    /// 大きさやコマの進み方に少しずつ差を付けるのに使う。
    /// **毎フレーム乱数を引くと、粒がちらつく**——同じ粒なのに
    /// フレームごとに違う値になるため。1回だけ引いて持ち回るのが要点。
    /// </para>
    /// </summary>
    public float Seed;

    /// <summary>寿命の中での位置(0→1)。色・大きさ・コマ番号の入力になる。</summary>
    public readonly float Normalized => Lifetime <= 0.0f ? 1.0f : Math.Clamp(Age / Lifetime, 0.0f, 1.0f);
}

/// <summary>
/// **パーティクルの放出口**(Day 49)。今日の主役その1。
///
/// <para>
/// やっていることは3つだけ。<b>撒く・進める・捨てる</b>。
/// 描くほうは <see cref="ParticleRenderer"/> が持っていて、
/// ここは GL を1行も触らない——<see cref="PhysicsWorld"/> が描画を知らないのと同じ分け方。
/// </para>
///
/// <para>
/// <b>プールを1本持って、生きている粒を前に詰める</b>のが全体の骨になっている。
/// 死んだ粒は末尾の生きた粒と入れ替えて <see cref="Count"/> を1つ減らす
/// (<c>swap-remove</c>)。詰め直しが O(1) で済み、
/// **生きている粒は常に配列の先頭から連続して並ぶ**ので、
/// 描く側は <see cref="Alive"/> をそのまま舐めればよい。
/// </para>
///
/// <para>
/// 代償は<b>順番が保たれないこと</b>。粒が入れ替わるので「古い順」には並ばない。
/// 加算合成なら足し算の順番は結果を変えないので困らないが、
/// アルファ合成では奥から描く必要がある——だから
/// <see cref="ParticleRenderer"/> のほうで<b>描く直前に並べ替える</b>(要点3)。
/// </para>
/// </summary>
internal sealed class ParticleEmitter
{
    private readonly Random _random;
    private Particle[] _particles;

    /// <summary>
    /// 放出の端数。**これが無いとフレームレートで粒の数が変わる**。
    ///
    /// <para>
    /// 毎秒 200 個を 60fps で撒くと 1フレームあたり 3.33 個。
    /// <c>(int)3.33 = 3</c> で切り捨てると毎秒 180 個にしかならず、
    /// 144fps では 1.39 → 1 個で毎秒 144 個。
    /// <b>fps が変わると絵が変わる</b>ことになるので、端数を持ち越す。
    /// Day 19 の固定ステップが「溜まった時間を持ち越す」のとまったく同じ形。
    /// </para>
    /// </summary>
    private float _carry;

    public ParticleEmitter(int capacity = 4096, int seed = 4900)
    {
        Capacity = capacity;
        _particles = new Particle[capacity];
        _random = new Random(seed);
    }

    /// <summary>プールの大きさ。**上限に達したら新しい粒は撒かない**(古いのを消したりしない)。</summary>
    public int Capacity { get; }

    /// <summary>いま生きている粒の数。</summary>
    public int Count { get; private set; }

    /// <summary>生きている粒。**配列の先頭から連続している**。</summary>
    public ReadOnlySpan<Particle> Alive => _particles.AsSpan(0, Count);

    /// <summary>放出口の位置(ワールド)。</summary>
    public Vector3 Position { get; set; }

    /// <summary>撒く向き(<see cref="ParticleShape.Cone"/> のときの軸)。</summary>
    public Vector3 Direction { get; set; } = Vector3.UnitY;

    public ParticleShape Shape { get; set; } = ParticleShape.Cone;

    /// <summary>円錐の半頂角(度)。0 で真っ直ぐ、90 で半球いっぱい。</summary>
    public float ConeAngleDegrees { get; set; } = 20.0f;

    /// <summary>撒き始める位置の広がり(m)。球・円盤の半径にもなる。</summary>
    public float Radius { get; set; } = 0.1f;

    /// <summary>1秒あたり何個撒くか。0 なら <see cref="Burst"/> だけ。</summary>
    public float EmitRate { get; set; } = 200.0f;

    /// <summary>撒くのを止める(生きている粒はそのまま消えるまで動く)。</summary>
    public bool Emitting { get; set; } = true;

    public float LifetimeMin { get; set; } = 0.8f;

    public float LifetimeMax { get; set; } = 1.4f;

    public float SpeedMin { get; set; } = 2.0f;

    public float SpeedMax { get; set; } = 3.5f;

    /// <summary>重力。煙は上げたいので**正の Y を入れることもある**。</summary>
    public Vector3 Gravity { get; set; } = new(0.0f, -9.81f, 0.0f);

    /// <summary>
    /// 空気抵抗(1/秒)。速度を指数で減衰させる。
    ///
    /// <para>
    /// <c>v *= exp(-Drag * dt)</c> にしてあるのは、
    /// <c>v *= (1 - Drag * dt)</c> だと <c>Drag * dt &gt; 1</c> で符号が反転し、
    /// **粒が逆走する**ため。dt が跳ねたフレームで一度でも起きると目に見える。
    /// </para>
    /// </summary>
    public float Drag { get; set; }

    public float StartSize { get; set; } = 0.25f;

    public float EndSize { get; set; } = 0.05f;

    /// <summary>大きさの揺らぎ(0〜1)。0.3 なら 0.7〜1.3 倍に散る。</summary>
    public float SizeJitter { get; set; } = 0.3f;

    /// <summary>生まれたときの色。**アルファは不透明度**で、加算合成では明るさになる。</summary>
    public Vector4 StartColor { get; set; } = new(1.0f, 0.75f, 0.35f, 1.0f);

    /// <summary>消えるときの色。ここへ向かって線形に混ざる。</summary>
    public Vector4 EndColor { get; set; } = new(0.6f, 0.15f, 0.05f, 0.0f);

    /// <summary>回転の速さの上限(ラジアン/秒)。±この範囲で散る。</summary>
    public float SpinRange { get; set; } = 2.0f;

    /// <summary>フリップブックの列数。1 ならコマ送りしない。</summary>
    public int FlipbookColumns { get; set; } = 1;

    /// <summary>フリップブックの行数。</summary>
    public int FlipbookRows { get; set; } = 1;

    /// <summary>コマ数(= 列 × 行)。</summary>
    public int FrameCount => Math.Max(1, FlipbookColumns * FlipbookRows);

    /// <summary>撒いた粒の総数(計測用)。</summary>
    public int TotalEmitted { get; private set; }

    /// <summary>まとめて撒く。爆発やヒットのように「1回だけ」出すもの。</summary>
    public void Burst(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Spawn();
        }
    }

    /// <summary>全部消す。端数も捨てる。</summary>
    public void Clear()
    {
        Count = 0;
        _carry = 0.0f;
        TotalEmitted = 0;
    }

    /// <summary>
    /// 1ステップ進める。**撒く → 進める → 捨てる** の順。
    ///
    /// <para>
    /// <b>可変 dt で呼んでよい</b>。粒は当たり判定を持たず、
    /// 誰にも影響を与えないので、Day 19 の固定ステップに載せる理由が無い——
    /// カメラワーク(Day 40)と同じ扱いになる。
    /// 逆に言うと、**粒に物を押させたくなった日は固定ステップへ移す**必要がある。
    /// </para>
    /// </summary>
    public void Update(float deltaSeconds)
    {
        if (deltaSeconds <= 0.0f)
        {
            return;
        }

        // --- 1. 撒く ---
        if (Emitting && EmitRate > 0.0f)
        {
            _carry += EmitRate * deltaSeconds;

            int toSpawn = (int)_carry;
            _carry -= toSpawn;

            // **1フレームで撒く数に上限を置く**。ブレークポイントで止めたあとの
            // 巨大な dt で、プールを一瞬で使い切ってしまうのを防ぐ。
            toSpawn = Math.Min(toSpawn, Capacity);

            for (int i = 0; i < toSpawn; i++)
            {
                Spawn();
            }
        }

        // --- 2. 進める / 3. 捨てる ---
        //
        // **同じループでやる**。死んだ粒を末尾と入れ替えるので、
        // 入れ替えた先はまだ進めていない粒になる。だから添字を進めない。
        float damping = Drag > 0.0f ? MathF.Exp(-Drag * deltaSeconds) : 1.0f;

        for (int i = 0; i < Count;)
        {
            ref Particle particle = ref _particles[i];

            particle.Age += deltaSeconds;

            if (particle.Age >= particle.Lifetime)
            {
                // **末尾と入れ替えて縮める**。順番は壊れるが O(1) で済む。
                Count--;
                _particles[i] = _particles[Count];
                continue;
            }

            // セミインプリシット(Day 43 の要点1)。速度を先に進めてから位置に足す。
            particle.Velocity += Gravity * deltaSeconds;
            particle.Velocity *= damping;
            particle.Position += particle.Velocity * deltaSeconds;
            particle.Rotation += particle.Spin * deltaSeconds;

            i++;
        }
    }

    /// <summary>寿命に沿った色。**線形補間1本**で足りる。</summary>
    public Vector4 ColorAt(float normalized) =>
        Vector4.Lerp(StartColor, EndColor, normalized);

    /// <summary>寿命に沿った大きさ。粒ごとの揺らぎを掛ける。</summary>
    public float SizeAt(float normalized, float seed)
    {
        float size = float.Lerp(StartSize, EndSize, normalized);
        return size * (1.0f + ((seed - 0.5f) * 2.0f * SizeJitter));
    }

    /// <summary>
    /// 寿命に沿ったコマ番号。**最後のコマで止める**。
    ///
    /// <para>
    /// 輪にして 0 へ戻すと、消えかけの粒が突然「爆発の最初」に戻って目立つ。
    /// フリップブックは1回きりの絵として使うのが普通なので、
    /// <c>FrameCount - 1</c> で頭打ちにしてある。
    /// </para>
    /// </summary>
    public int FrameAt(float normalized) =>
        Math.Clamp((int)(normalized * FrameCount), 0, FrameCount - 1);

    private void Spawn()
    {
        if (Count >= Capacity)
        {
            return;
        }

        ref Particle particle = ref _particles[Count];

        Vector3 offset;
        Vector3 direction;

        switch (Shape)
        {
            case ParticleShape.Sphere:
                direction = RandomDirection();
                offset = direction * Radius * (float)_random.NextDouble();
                break;

            case ParticleShape.Disc:
                {
                    // 水平な円盤の上。**外へ広がる向き**にするのが土埃らしさになる。
                    float angle = (float)_random.NextDouble() * MathF.Tau;

                    // **平方根を取る**のが要点。半径を一様に取ると中心に寄る——
                    // 面積は半径の2乗に比例するので、外周ほど広い場所を
                    // 同じ確率で埋めることになってしまう。
                    float radius = Radius * MathF.Sqrt((float)_random.NextDouble());

                    var flat = new Vector3(MathF.Cos(angle), 0.0f, MathF.Sin(angle));
                    offset = flat * radius;
                    direction = Vector3.Normalize(flat + (Vector3.UnitY * 0.6f));
                    break;
                }

            default:
                offset = RandomDirection() * Radius * (float)_random.NextDouble();
                direction = RandomInCone(Direction, ConeAngleDegrees);
                break;
        }

        float speed = Lerp(SpeedMin, SpeedMax);

        particle.Position = Position + offset;
        particle.Velocity = direction * speed;
        particle.Age = 0.0f;
        particle.Lifetime = MathF.Max(0.01f, Lerp(LifetimeMin, LifetimeMax));
        particle.Rotation = (float)_random.NextDouble() * MathF.Tau;
        particle.Spin = ((float)_random.NextDouble() - 0.5f) * 2.0f * SpinRange;
        particle.Seed = (float)_random.NextDouble();

        Count++;
        TotalEmitted++;
    }

    private float Lerp(float min, float max) =>
        min + ((max - min) * (float)_random.NextDouble());

    /// <summary>
    /// 球面上の一様な向き。
    ///
    /// <para>
    /// <b>高さを一様に取ってから角度を決める</b>のが要点(アルキメデスの定理)。
    /// 緯度と経度を両方一様に振ると、**極に密集する**——
    /// 地球儀の目盛りが極で詰まっているのと同じ理屈。
    /// </para>
    /// </summary>
    private Vector3 RandomDirection()
    {
        float z = ((float)_random.NextDouble() * 2.0f) - 1.0f;
        float angle = (float)_random.NextDouble() * MathF.Tau;
        float r = MathF.Sqrt(MathF.Max(0.0f, 1.0f - (z * z)));

        return new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), z);
    }

    /// <summary>
    /// 軸のまわりの円錐の中の向き。
    ///
    /// <para>
    /// こちらも <c>cos θ</c> を一様に振っている。角度 θ を一様に振ると
    /// **軸の近くに密集する**——円錐の断面積は縁に行くほど広いため。
    /// </para>
    /// </summary>
    private Vector3 RandomInCone(Vector3 axis, float halfAngleDegrees)
    {
        Vector3 forward = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitY;

        float cosMax = MathF.Cos(halfAngleDegrees * MathF.PI / 180.0f);
        float cosTheta = 1.0f - ((float)_random.NextDouble() * (1.0f - cosMax));
        float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - (cosTheta * cosTheta)));
        float phi = (float)_random.NextDouble() * MathF.Tau;

        // 軸に垂直な2本を作る。**軸と平行でない適当なベクトル**から外積で起こす。
        Vector3 helper = MathF.Abs(forward.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 right = Vector3.Normalize(Vector3.Cross(helper, forward));
        Vector3 up = Vector3.Cross(forward, right);

        return Vector3.Normalize(
            (forward * cosTheta)
            + (right * sinTheta * MathF.Cos(phi))
            + (up * sinTheta * MathF.Sin(phi)));
    }
}
