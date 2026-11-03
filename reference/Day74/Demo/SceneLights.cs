using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **シーンに置いた灯り**(Day 56)。今日の主役。
///
/// <para>
/// Day 52 の <see cref="LightSwarm"/> は「光の数を比べるための道具」で、
/// 漂う箱の大きさまで数字で持っていた(壁の位置は JSON に書いてあるのに、手で写していた)。
/// 今日の灯りは<b>シーンの持ち物</b>で、JSON の <c>lights</c> に1つずつ書く。
/// 街灯・窓・看板のように、<b>そこに灯りがある理由が絵の中にある</b>光だけを置く。
/// </para>
///
/// <para>
/// <b>灯りは2つのものでできている</b>。ゲームの絵ではこの2つが別々の仕組みで動く。
/// </para>
/// <list type="table">
/// <item><term>照らす側</term><description>
/// 点光源(<see cref="PointLight"/>)。壁や床を明るくする。<b>自分は画面に写らない</b>
/// </description></item>
/// <item><term>見える側</term><description>
/// 光る材質(<see cref="Material.EmissiveFactor"/>)。ガラスや窓が光って見える。<b>まわりは1画素も照らさない</b>
/// </description></item>
/// </list>
///
/// <para>
/// 片方だけだと、「光っているのに何も照らさない窓」か「何も無いところから差す光」になる。
/// そこで<b>1つの項目に書いて、色と明るさの揺らぎを共有させる</b>(<see cref="Update"/>)。
/// 炎が揺らげばガラスも床の光だまりも同じだけ揺らぎ、消せば両方が同時に消える。
/// 2か所に書くと、片方だけ直す日が必ず来る——Day 40 の機能表で「状態の置き場所は1つ」と決めたのと同じ判断。
/// </para>
///
/// <para>
/// <b>GL を知らない</b>(<see cref="LightSwarm"/> と同じ)。点光源の数字を並べ、材質の数字を書き換えるだけで、
/// 描くのは今までどおり <c>textured.frag</c>(フォワード / Forward+)と <c>deferred.frag</c>。
/// おかげで自己チェックの半分は窓を開かずに走る。
/// </para>
/// </summary>
internal sealed class SceneLights
{
    /// <summary>
    /// 揺らぎの速さ [回/秒]。**炎のちらつきの見た目に合わせた値**。
    /// 速すぎると点滅に見え、遅すぎると「明るさを誰かが手で回している」ように見える。
    /// </summary>
    public const float FlickerRate = 9.0f;

    /// <summary>
    /// 灯り1つ。**照らす側と見える側を1つにまとめたもの**。
    ///
    /// <para>
    /// 色は 0〜1 で持ち、明るさは照らす側(<see cref="Intensity"/>)と見える側(<see cref="Glow"/>)で別に持つ。
    /// <b>色だけは必ず共有する</b>——橙の窓から青い光が差すことは無い。
    /// 明るさを分けてあるのは、同じ灯りでも「床に届く量」と「ガラスがどれだけ眩しいか」は
    /// 露出やブルームとの兼ね合いで別々に決めることになるため(計画書の要点3)。
    /// </para>
    /// </summary>
    internal sealed class Fixture
    {
        public Fixture(
            string name,
            Vector3 position,
            Vector3 color,
            float intensity,
            float radius,
            float flicker,
            float glow,
            IReadOnlyList<Material> glowMaterials)
        {
            Name = name;
            Position = position;
            Color = color;
            Intensity = intensity;
            Radius = radius;
            Flicker = Math.Clamp(flicker, 0.0f, 1.0f);
            Glow = glow;
            GlowMaterials = glowMaterials;
        }

        /// <summary>HUD と内訳に出す名前。</summary>
        public string Name { get; }

        /// <summary>照らす側の位置 [m](世界)。</summary>
        public Vector3 Position { get; }

        /// <summary>色(0〜1)。**照らす側と見える側で共有する**。</summary>
        public Vector3 Color { get; }

        /// <summary>照らす側の強さ。<see cref="PointLight.Color"/> = 色 × これ(Day 52 と同じ単位)。</summary>
        public float Intensity { get; }

        /// <summary>届く距離 [m](<see cref="PointLight.Radius"/>)。</summary>
        public float Radius { get; }

        /// <summary>揺らぎの深さ(0〜1)。0 なら揺らがない。ランタンの炎は浅く、切れかけた蛍光灯は深く。</summary>
        public float Flicker { get; }

        /// <summary>見える側の明るさ。<see cref="Material.EmissiveFactor"/> = 色 × これ。</summary>
        public float Glow { get; }

        /// <summary>
        /// 光らせる材質。小物の部品(街灯のガラス)か、灯りのために足した板(窓・看板)。
        /// **材質は小物と共有している**ので、同じ材質を2つの灯りに割り当てると後に書いたほうが勝つ。
        /// </summary>
        public IReadOnlyList<Material> GlowMaterials { get; }
    }

    private readonly Fixture[] _fixtures;
    private readonly PointLight[] _lights;

    /// <summary>いま点いている数。消していれば 0(<see cref="Update"/> が決める)。</summary>
    private int _litCount;

    public SceneLights(IReadOnlyList<Fixture> fixtures)
    {
        _fixtures = [.. fixtures];
        _lights = new PointLight[_fixtures.Length];
    }

    public IReadOnlyList<Fixture> Fixtures => _fixtures;

    /// <summary>置いてある灯りの数(点いているかどうかに依らない)。</summary>
    public int Count => _fixtures.Length;

    /// <summary>揺らぎの時刻 [s]。</summary>
    public float Time { get; private set; }

    /// <summary>
    /// いまの照らす側。<see cref="Update"/> のたびに書き直される。**消していれば空**——
    /// 0 の光を並べて渡すと、Forward+ の升目や光の球を空回しすることになる。
    /// </summary>
    public ReadOnlySpan<PointLight> Lights => _lights.AsSpan(0, _litCount);

    /// <summary>
    /// 時刻を進め、**照らす側と見える側を同じ明るさで**書き直す。毎フレーム1回。
    ///
    /// <para>
    /// <b>点け消しもここで受け取る</b>(状態は <c>Program</c> が1つだけ持つ)。
    /// 一時停止中も呼ぶ(<paramref name="deltaSeconds"/> は 0)——止めていても SSAO を切れば消えるのと同じで、
    /// 灯りを消したら止めていても消えてほしいから。
    /// </para>
    /// </summary>
    /// <param name="deltaSeconds">可変 dt。一時停止中は 0。</param>
    /// <param name="on">点けるか。</param>
    public void Update(float deltaSeconds, bool on)
    {
        Time += deltaSeconds;
        Evaluate(on);
    }

    /// <summary>
    /// 灯り <paramref name="index"/> の、時刻 <paramref name="time"/> での明るさの倍率(0〜1)。
    ///
    /// <para>
    /// <b>時刻の関数</b>にしてある(<see cref="LightSwarm"/> と同じ判断)。前のフレームから積み上げないので、
    /// 60fps でも 144fps でも同じ時刻に同じ明るさになり、一時停止すれば止まる。
    /// </para>
    ///
    /// <para>
    /// <b>揺らぎを2乗してから引く</b>。そのまま引くと平均して半分暗くなる。
    /// 2乗すると 0 の近くに寄るので、**ふだんはほぼ満点で、ときどき沈む**——
    /// 炎も切れかけた蛍光灯も、暗い時間より明るい時間のほうが長い。
    /// </para>
    /// </summary>
    public float Brightness(int index, float time)
    {
        Fixture fixture = _fixtures[index];

        if (fixture.Flicker <= 0.0f)
        {
            return 1.0f;
        }

        float noise = Noise(time * FlickerRate, index);
        return 1.0f - (fixture.Flicker * noise * noise);
    }

    /// <summary>
    /// 0〜1 のなめらかな揺らぎ(値ノイズ)。整数の刻みごとに乱数を1つ置き、間を smoothstep でつなぐ。
    ///
    /// <para>
    /// <b><see cref="Random"/> を使わない</b>のが要点。<see cref="Random"/> は「次の1つ」しか返せないので、
    /// 時刻から直接引けない(引いた回数で値が変わる)。整数から乱数を作る関数(ハッシュ)なら、
    /// 同じ刻みには何回聞いても同じ値が返る。
    /// </para>
    /// </summary>
    /// <param name="t">刻みの単位の時刻(秒 × <see cref="FlickerRate"/>)。</param>
    /// <param name="seed">灯りの番号。**灯りごとに揺らぎがずれる**ように混ぜる。</param>
    public static float Noise(float t, int seed)
    {
        float floor = MathF.Floor(t);
        float fraction = t - floor;
        int step = (int)floor;

        float a = Hash(step, seed);
        float b = Hash(step + 1, seed);
        float smooth = fraction * fraction * (3.0f - (2.0f * fraction));

        return a + ((b - a) * smooth);
    }

    /// <summary>整数2つから 0〜1 の乱数を1つ作る。**同じ入力には必ず同じ値**。</summary>
    private static float Hash(int step, int seed)
    {
        unchecked
        {
            uint h = ((uint)step * 374761393u) + ((uint)seed * 668265263u);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / (float)0xFFFFFFu;
        }
    }

    private void Evaluate(bool on)
    {
        _litCount = on ? _fixtures.Length : 0;

        for (int i = 0; i < _fixtures.Length; i++)
        {
            Fixture fixture = _fixtures[i];
            float brightness = on ? Brightness(i, Time) : 0.0f;

            _lights[i] = new PointLight(fixture.Position, fixture.Radius, fixture.Color * (fixture.Intensity * brightness));

            // **見える側も同じ倍率で**。ここが今日の「1つの項目に書く」の中身。
            // 消したときは 0 にする——光るのをやめたガラスは、ただのガラスに戻る。
            Vector3 emissive = fixture.Color * (fixture.Glow * brightness);

            foreach (Material material in fixture.GlowMaterials)
            {
                material.EmissiveFactor = emissive;
            }
        }
    }
}
