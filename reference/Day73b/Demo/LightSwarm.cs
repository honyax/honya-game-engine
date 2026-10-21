using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **裏通りを漂う点光源の群れ**(Day 52)。ディファードを試すための光の置き場。
///
/// <para>
/// 今日知りたいのは「光の数を増やすと、フォワードとディファードの代償がどう変わるか」。
/// それには<b>数だけを変えて、他を変えない</b>並べ方が要る。
/// 数を 16 → 1024 に増やしながら半径を同じにすると、通りが光の球で埋まって
/// 1画素を何百個もの球が塗り、ディファードでも重くなる——それは数の比べではなく重なりの比べになる。
/// </para>
///
/// <para>
/// ディファードの代償は<b>球が画面を覆う面積</b>で決まり、それは半径² に比例する。
/// そこで<b>半径を個数の平方根に反比例させる</b>(<see cref="RadiusFor"/>)。
/// 個数 × 半径² が一定なら、球が塗る画素の総数がおおむね揃う。
/// </para>
///
/// <code>
///   16 個 x 半径 4.0m    64 個 x 2.0m    256 個 x 1.0m    1024 個 x 0.5m
///   → どれも「球が画面を塗る量」がほぼ同じ(「ディファード」の F6 で見える)
/// </code>
///
/// <para>
/// こうしておくと、光を増やしたときに<b>伸びるのはフォワードだけ</b>になる。
/// フォワードは半径が小さくなっても、全部の光について「届くかどうか」を画素ごとに確かめるので、
/// 代償は個数にそのまま比例する。
/// </para>
///
/// <para>
/// <b>GL を知らない</b>(<see cref="ParticleEmitter"/> と同じ判断)。
/// 位置はこのクラスが決め、描くのは <see cref="DeferredLighting"/> と
/// <c>textured.frag</c>。おかげで自己チェックの半分は窓を開かずに走る。
/// </para>
/// </summary>
internal sealed class LightSwarm
{
    /// <summary>数の段(「ディファード」の F3)。**0 は Day 51 までの絵**。</summary>
    public static readonly int[] CountSteps = [0, 16, 64, 256, 1024];

    /// <summary>16 個のときの届く距離 [m]。通りの幅(約 7m)の半分ちょっと。</summary>
    public const float BaseRadius = 4.0f;

    /// <summary><see cref="BaseRadius"/> を使う個数。ここから数に合わせて縮める。</summary>
    public const int BaseCount = 16;

    /// <summary>
    /// 色の見本。**暖色を多めに**しておく——裏通りの提灯やランタンのつもり。
    /// 寒色を少し混ぜるのは、光が重なったところで色が混ざるのを見せるため
    /// (加算で重なるので、橙と青が重なると白っぽくなる)。
    /// </summary>
    private static readonly Vector3[] Palette =
    [
        new(1.00f, 0.55f, 0.20f),
        new(1.00f, 0.32f, 0.12f),
        new(1.00f, 0.78f, 0.45f),
        new(0.30f, 0.62f, 1.00f),
        new(0.45f, 1.00f, 0.55f),
        new(0.95f, 0.35f, 0.85f),
    ];

    /// <summary>
    /// 光1つの動き方。**生まれたときに1回だけ決めて、あとは時刻の関数**。
    /// 高さだけは 0〜1 の割合で持つ——実際の高さは半径で決まる(<see cref="HeightRange"/>)。
    /// </summary>
    private readonly record struct Seed(
        Vector2 Center, Vector2 Amplitude, Vector3 Speed, Vector3 Phase, float Height, Vector3 Color);

    private readonly Seed[] _seeds;
    private readonly PointLight[] _lights;

    /// <param name="capacity">確保しておく数。<see cref="SetCount"/> はこれを超えられない。</param>
    /// <param name="seed">
    /// 乱数の種。**固定する**のは SSAO のカーネル(Day 37)と同じ理由——
    /// 起動のたびに光の並びが変わると、フォワードとディファードの見比べも、
    /// 計測の数字も、何と比べているのか分からなくなる。
    /// </param>
    public LightSwarm(int capacity, int seed = 20520101)
    {
        _seeds = new Seed[Math.Max(0, capacity)];
        _lights = new PointLight[_seeds.Length];

        var random = new Random(seed);

        for (int i = 0; i < _seeds.Length; i++)
        {
            // **揺れ幅ぶん内側に中心を置く**。外側に置いて後から箱に押し込むと、
            // 壁際の光が壁に張り付いたまま動かなくなる。
            var amplitude = new Vector2(
                0.3f + (random.NextSingle() * 0.9f),
                0.3f + (random.NextSingle() * 1.2f));

            var low = new Vector2(BoundsMin.X + amplitude.X, BoundsMin.Z + amplitude.Y);
            var high = new Vector2(BoundsMax.X - amplitude.X, BoundsMax.Z - amplitude.Y);
            var center = new Vector2(
                low.X + (random.NextSingle() * (high.X - low.X)),
                low.Y + (random.NextSingle() * (high.Y - low.Y)));

            // 角速度 [rad/s]。0.15〜0.5 なので、1往復に 12〜40 秒。**漂う程度**に遅くしておく——
            // 速いと光のちらつきが気になって、見比べたい影の形が追えない。
            var speed = new Vector3(
                0.15f + (random.NextSingle() * 0.35f),
                0.30f + (random.NextSingle() * 0.40f),
                0.15f + (random.NextSingle() * 0.35f));

            var phase = new Vector3(
                random.NextSingle() * MathF.Tau,
                random.NextSingle() * MathF.Tau,
                random.NextSingle() * MathF.Tau);

            Vector3 color = Palette[i % Palette.Length] * (0.8f + (random.NextSingle() * 0.4f));

            _seeds[i] = new Seed(center, amplitude, speed, phase, random.NextSingle(), color);
        }

        SetCount(0);
    }

    /// <summary>
    /// 光が漂う箱。**裏通りの床から 15cm 〜 2.6m、左の壁と道路バリアの向こうの間**。
    /// 数字は <c>assets/scenes/demo-v1.json</c> の壁と小物の位置から決めた。
    /// </summary>
    public static Vector3 BoundsMin => new(-3.6f, 0.15f, -12.5f);

    public static Vector3 BoundsMax => new(3.0f, 2.6f, 7.0f);

    public int Capacity => _seeds.Length;

    /// <summary>いま使っている数。</summary>
    public int Count { get; private set; }

    /// <summary>いまの届く距離 [m]。数から決まる(<see cref="RadiusFor"/>)。</summary>
    public float Radius { get; private set; } = BaseRadius;

    /// <summary>
    /// <see cref="BaseCount"/> 個のときの光1個の強さ。数を増やすと<b>半径に比例して</b>弱める。
    ///
    /// <para>
    /// 半径を縮めた光は照らす範囲が狭いぶん、1個あたりの強さを落としすぎると通りが真っ暗になる。
    /// 床に降る光の総量(個数 × 強さ × 1個が照らす量)を並べて測ると、
    /// 半径の1乗で弱めたときにおおむね揃った(計画書の「検証の途中で分かったこと」)。
    /// </para>
    /// </summary>
    public float Intensity { get; set; } = 8.0f;

    /// <summary>漂い始めてからの時刻 [s]。</summary>
    public float Time { get; private set; }

    /// <summary>いまの光。<see cref="Update"/> のたびに書き直される。</summary>
    public ReadOnlySpan<PointLight> Lights => _lights.AsSpan(0, Count);

    /// <summary>
    /// **個数から届く距離を決める**。個数 × 半径² を <see cref="BaseCount"/> × <see cref="BaseRadius"/>² に保つ。
    /// </summary>
    public static float RadiusFor(int count) =>
        count <= 0 ? BaseRadius : BaseRadius * MathF.Sqrt((float)BaseCount / count);

    /// <summary>
    /// **半径に合わせた高さの範囲**。小さな光ほど床の近くを飛ばす。
    ///
    /// <para>
    /// 半径 0.5m の光が高さ 2m を飛んでも、床にも壁にも届かず<b>何も照らさない</b>。
    /// 球は描かれるので代償だけ払って絵に出ない、という光になる。
    /// 届く距離の半分ほどの高さに収めておけば、どの数でも床に光だまりができる。
    /// </para>
    /// </summary>
    public static (float Low, float High) HeightRange(float radius) =>
        (BoundsMin.Y, Math.Clamp(0.2f + (0.55f * radius), 0.45f, BoundsMax.Y));

    /// <summary>数を変える。**半径と強さも一緒に決め直す**。</summary>
    public void SetCount(int count)
    {
        Count = Math.Clamp(count, 0, Capacity);
        Radius = RadiusFor(Count);
        Evaluate();
    }

    /// <summary>時刻を進めて、位置を決め直す。**見せ方だけなので可変 dt で呼んでよい**。</summary>
    public void Update(float deltaSeconds)
    {
        Time += deltaSeconds;
        Evaluate();
    }

    /// <summary>
    /// **位置は時刻の関数**。前のフレームの位置から積み上げない。
    ///
    /// <para>
    /// 積み上げる(速度 × dt を足していく)と、フレームの刻み方で位置がずれていく。
    /// 時刻から直接出せば、60fps で 1 秒回しても 144fps で 1 秒回しても同じ場所に来る——
    /// Day 51 の要点1(指数平滑)と同じく、<b>dt の刻み方に依らない形</b>を選んでいる。
    /// </para>
    /// </summary>
    private void Evaluate()
    {
        float strength = Intensity * (Radius / BaseRadius);
        (float low, float high) = HeightRange(Radius);

        for (int i = 0; i < Count; i++)
        {
            Seed seed = _seeds[i];

            float x = seed.Center.X + (MathF.Sin((Time * seed.Speed.X) + seed.Phase.X) * seed.Amplitude.X);
            float z = seed.Center.Y + (MathF.Cos((Time * seed.Speed.Z) + seed.Phase.Z) * seed.Amplitude.Y);

            // 高さは割合で上下させる(範囲の端から出ないよう、揺れを 0〜1 に収めてから写す)。
            float bob = Math.Clamp(seed.Height + (0.15f * MathF.Sin((Time * seed.Speed.Y) + seed.Phase.Y)), 0.0f, 1.0f);
            float y = low + ((high - low) * bob);

            _lights[i] = new PointLight(new Vector3(x, y, z), Radius, seed.Color * strength);
        }
    }
}
