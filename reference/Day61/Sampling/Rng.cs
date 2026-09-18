using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 乱数発生器(PCG32)。<b>スレッドごと・画素ごとに1つ作る</b>ので、構造体にして値で持ち回る(Day 60 の要点6)。
///
/// <para>
/// パストレーシングは1画素に何千本もの光線を撃ち、1本ごとに「次にどの向きへ散らすか」を乱数で決める。
/// 乱数の作り方を間違えると、<b>絵にノイズではなく模様が出る</b>——同じ数列を何画素も共有すると、
/// その画素たちが同じ向きへ散り、同じものを見て、同じ色に寄る。
/// </para>
///
/// <para>
/// <b>Day 61 で 64 ビット版から 32 ビット版に替えた</b>(今日の要点2)。理由は1つだけ——
/// <b>GLSL に 64 ビット整数が(拡張なしでは)無い</b>。
/// Day 60 の PCG-XSH-RR は状態が <c>ulong</c> で、<see cref="SplitMix64"/> も 64 ビットの掛け算を使っていたので、
/// そのままではコンピュートシェーダに写せない。
/// </para>
/// <para>
/// 32 ビットに落としたうえで<b>CPU と GPU で1ビットも違わない数列</b>にしてある。これが今日の検証の土台で、
/// 乱数がずれていると「GPU 版の絵が違う」のが<b>移植の間違いなのか、ただのノイズの違いなのか</b>が
/// 区別できなくなる(自己チェック20)。揃えてあるからこそ「同じ道を歩いて、同じ色になる」で突き合わせられる。
/// </para>
/// <list type="number">
/// <item>本体は <b>PCG-RXS-M-XS 32</b>(O'Neill, 2014)。状態も出力も 32 ビット。
/// 周期は 2³² で、1画素が使う十数個には十分</item>
/// <item>種を散らす関数は <b>triple32</b>(Wellons)。<see cref="SplitMix64"/> の 32 ビット版にあたる</item>
/// <item><see cref="NextInt"/> は剰余に替えた。<c>(ulong)v * count >> 32</c> が 64 ビットを要るため</item>
/// </list>
/// </summary>
internal struct Rng
{
    /// <summary>PCG32 の LCG の乗数(32 ビット版)。</summary>
    private const uint Multiplier = 747796405u;

    /// <summary>LCG の加数(奇数なら何でもよい)。</summary>
    private const uint Increment = 2891336453u;

    /// <summary>黄金比を 32 ビットに直したもの。サンプル番号を散らす掛け算に使う。</summary>
    private const uint GoldenRatio = 0x9E3779B9u;

    private uint _state;

    private Rng(uint state)
    {
        _state = state;
    }

    /// <summary>
    /// 画素とサンプル番号から発生器を作る。<b>同じ (x, y, sample) なら必ず同じ数列</b>になる。
    ///
    /// <para>
    /// 状態を持ち回さず番号から作り直すので、<b>どのスレッドがどの行を受け持っても絵が変わらない</b>。
    /// Day 60 では「<see cref="Parallel.For"/> の割り当てが毎回違うから」だったが、
    /// GPU ではもっと効く——<b>何万本のスレッドがどの順に走るかは一切決まっていない</b>ので、
    /// 状態を持ち回す乱数器はそもそも書けない(要点2)。
    /// </para>
    /// </summary>
    /// <param name="decorrelate">
    /// false にすると<b>画素の番号を種に混ぜない</b>(K キー)。全部の画素が同じ数列を使うので、
    /// 散る向きがそろい、ノイズが<b>画面をまたいだ模様</b>になる(Day 60 の要点6)。
    /// </param>
    public static Rng Create(int x, int y, int sample, bool decorrelate = true)
    {
        // サンプル番号は必ず混ぜる。混ぜないと、何サンプル重ねても同じ数列で同じ光線を撃つことになり、
        // 平均しても1本目のまま(絵が滑らかにならない)。
        uint seed = (uint)sample * GoldenRatio;

        if (decorrelate)
        {
            // 画素の番号は、そのまま足すのではなく<b>先に混ぜてから</b> XOR する。
            // (x, y) を並べただけの値は隣の画素と 1 ビットしか違わず、LCG は「近い種から近い数列」を出す。
            //
            // 16 ビットずつ詰めているので、画面は 65536 画素四方まで。今日の 960x540 には十分で、
            // **GLSL 側もまったく同じ式**になる(64 ビットのシフトだと写せない)。
            seed ^= Mix32(((uint)y << 16) ^ (uint)x);
        }

        // もう1回通してから状態にする。PCG の出力の混ぜ方は状態の上位ビットに頼るので、
        // 種をそのまま入れると最初の1個か2個だけ質が落ちる。
        return new Rng(Mix32(seed));
    }

    /// <summary>
    /// 次の 32 ビット。<b>PCG-RXS-M-XS 32</b>(状態を進めてから、出すときに混ぜる)。
    ///
    /// <para>
    /// Day 60 の PCG-XSH-RR は「64 ビットの状態を 32 ビットへ落としながら回転」だったが、
    /// 状態が 32 ビットしか無いここでは落とす先が無い。代わりに
    /// <b>上位ビットで決めた分だけ右シフトして XOR(RXS)し、掛けて(M)、もう一度 XOR でずらす(XS)</b>。
    /// どちらも「規則正しい LCG の出力を、状態の上位ビットで決めた操作で崩す」という同じ考え方。
    /// </para>
    /// </summary>
    public uint NextUInt()
    {
        uint old = _state;
        _state = old * Multiplier + Increment;

        uint word = ((old >> (int)((old >> 28) + 4u)) ^ old) * 277803737u;
        return (word >> 22) ^ word;
    }

    /// <summary>
    /// 0 以上 1 未満の float。
    ///
    /// <para>
    /// 上位 24 ビットだけを使うのは、float の仮数部が 24 ビットしか無いから。
    /// 32 ビットを 2³² で割ると、下の8ビットは丸めで消えるうえ、1.0 ちょうどが出ることがある。
    /// <b>1.0 が出ると困る</b>——<c>sqrt(1 - u)</c> が 0 になり、正規化で 0 割りになる場所がある。
    /// </para>
    /// </summary>
    public float NextFloat() => (NextUInt() >> 8) * (1.0f / (1 << 24));

    /// <summary>
    /// 0 以上 <paramref name="count"/> 未満の整数。光源を1つ選ぶときに使う。
    ///
    /// <para>
    /// Day 60 は <c>(ulong)v * count >> 32</c>(64 ビットの掛け算で上位を取る)で偏りを消していたが、
    /// GLSL に 64 ビットが無いので<b>剰余</b>に替えた。剰余には偏りがある——2³² を count で割り切れないぶん、
    /// 小さい番号がわずかに多く出る。ただしその差は <c>count / 2³²</c> 程度で、
    /// 光源が3個でも 7×10⁻¹⁰。<b>光線を 10 億本撃って1本ぶん</b>なので、ここでは気にしない。
    /// </para>
    /// </summary>
    public int NextInt(int count) => (int)(NextUInt() % (uint)count);

    /// <summary>2つまとめて。サンプリングの式はたいてい2つの数を要る(<see cref="Sampler"/>)。</summary>
    public Vector2 NextVector2() => new(NextFloat(), NextFloat());

    /// <summary>
    /// <b>triple32</b>(Wellons, 2018)。<b>近い値を遠くへ散らす</b>ためだけの関数で、状態を持たない。
    /// Day 60 の <c>SplitMix64</c> と同じ役目の 32 ビット版で、<b>GLSL に1文字も変えずに写せる</b>。
    ///
    /// <para>
    /// 掛け算3回と右シフト XOR 4回。32 ビットの全単射(どの入力も別々の出力になる)で、
    /// 入力の1ビットを変えると出力の約半分のビットが変わる(avalanche)ことが実測で確かめられている。
    /// </para>
    /// </summary>
    private static uint Mix32(uint value)
    {
        value ^= value >> 17;
        value *= 0xED5AD4BBu;
        value ^= value >> 11;
        value *= 0xAC4C1B51u;
        value ^= value >> 15;
        value *= 0x31848BABu;
        value ^= value >> 14;
        return value;
    }
}
