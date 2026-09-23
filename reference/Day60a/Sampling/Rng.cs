using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 乱数発生器(PCG32)。<b>スレッドごと・画素ごとに1つ作る</b>ので、構造体にして値で持ち回る(要点6)。
///
/// <para>
/// パストレーシングは1画素に何千本もの光線を撃ち、1本ごとに「次にどの向きへ散らすか」を乱数で決める。
/// 乱数の作り方を間違えると、<b>絵にノイズではなく模様が出る</b>——同じ数列を何画素も共有すると、
/// その画素たちが同じ向きへ散り、同じものを見て、同じ色に寄る。
/// </para>
///
/// <para>
/// <see cref="System.Random"/> を使わない理由は3つ。
/// </para>
/// <list type="number">
/// <item><b>クラスなので、画素ごとに作ると GC の仕事が増える</b>。1パスで 50 万個は作れない</item>
/// <item><b>種から数列への散らばり方が保証されていない</b>。隣り合う種 (x, y) と (x+1, y) から
/// 似た数列が出ると、隣の画素が同じ向きへ散る</item>
/// <item><b>Day 61 で GLSL に写せない</b>。GPU には状態を持つ乱数器が無いので、
/// 「番号から数を作る」形(この形)でないと移せない</item>
/// </list>
///
/// <para>
/// PCG は <b>LCG(線形合同法)で状態を進め、出すときに状態を混ぜる</b>方式(O'Neill, 2014)。
/// LCG は下位のビットの質が悪い(最下位ビットは 0 と 1 が交互に出る)ことで知られるが、
/// PCG は上位のビットを使って「どれだけ回すか」を決めてから右回転を掛けるので、
/// 掛け算1回・回転1回という安さのまま、統計的な質が実用に足りる。
/// </para>
/// </summary>
internal struct Rng
{
    /// <summary>Knuth の LCG の乗数。64 ビットの周期を持つ。</summary>
    private const ulong Multiplier = 6364136223846793005UL;

    /// <summary>LCG の加数(奇数なら何でもよい)。</summary>
    private const ulong Increment = 1442695040888963407UL;

    /// <summary>黄金比を 64 ビットに直したもの。種を混ぜるときの掛け算に使う。</summary>
    private const ulong GoldenRatio = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    private Rng(ulong state)
    {
        _state = state;
    }

    /// <summary>
    /// 画素とサンプル番号から発生器を作る。<b>同じ (x, y, sample) なら必ず同じ数列</b>になる。
    ///
    /// <para>
    /// これが今日いちばん大事な決まりごと。状態を持ち回さず番号から作り直すので、
    /// <b>どのスレッドがどの行を受け持っても絵が変わらない</b>(<see cref="Parallel.For"/> の割り当ては毎回違う)。
    /// 「同じ設定なら必ず同じ絵」でないと、NEE のあり・なしを引き算して比べる自己チェックが書けない。
    /// </para>
    /// </summary>
    /// <param name="decorrelate">
    /// false にすると<b>画素の番号を種に混ぜない</b>(K キー)。全部の画素が同じ数列を使うので、
    /// 散る向きがそろい、ノイズが<b>画面をまたいだ模様</b>になる(要点6)。
    /// </param>
    public static Rng Create(int x, int y, int sample, bool decorrelate = true)
    {
        // サンプル番号は必ず混ぜる。混ぜないと、何サンプル重ねても同じ数列で同じ光線を撃つことになり、
        // 平均を取っても値が変わらない(1サンプルの絵のまま)。
        ulong seed = (ulong)(uint)sample * GoldenRatio;

        if (decorrelate)
        {
            // 画素の番号は、そのまま足すのではなく<b>先に混ぜてから</b> XOR する。
            // (x, y) を 32 ビットずつ並べただけの値は、隣の画素と 1 ビットしか違わない。
            // LCG は「近い種から近い数列」を出すので、混ぜずに使うと隣の画素が似た向きへ散る。
            seed ^= SplitMix64(((ulong)(uint)y << 32) | (uint)x);
        }

        // SplitMix64 をもう1回通してから状態にする。PCG の出力の混ぜ方は状態の上位ビットに頼るので、
        // 種をそのまま入れると最初の1個か2個だけ質が落ちる。
        return new Rng(SplitMix64(seed));
    }

    /// <summary>次の 32 ビット。</summary>
    public uint NextUInt()
    {
        ulong old = _state;
        _state = old * Multiplier + Increment;

        // PCG-XSH-RR。上位ビットで下位を混ぜてから 32 ビットに落とし、さらに上位5ビットで決めた分だけ右へ回す。
        // 「回す量まで乱数で決める」のが PCG の肝で、これで LCG の規則正しさが表に出なくなる。
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rotate = (int)(old >> 59);
        return System.Numerics.BitOperations.RotateRight(xorshifted, rotate);
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

    /// <summary>0 以上 <paramref name="count"/> 未満の整数。光源を1つ選ぶときに使う。</summary>
    public int NextInt(int count) => (int)((ulong)NextUInt() * (ulong)count >> 32);

    /// <summary>2つまとめて。サンプリングの式はたいてい2つの数を要る(<see cref="Sampler"/>)。</summary>
    public Vector2 NextVector2() => new(NextFloat(), NextFloat());

    /// <summary>
    /// SplitMix64(Steele ほか, 2014)。<b>近い値を遠くへ散らす</b>ためだけの関数で、状態を持たない。
    /// 掛け算と排他的論理和とシフトだけでできていて、Day 61 で GLSL に写すときも同じ形で書ける。
    /// </summary>
    private static ulong SplitMix64(ulong value)
    {
        value += GoldenRatio;
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
