namespace HonyaEngine;

/// <summary>
/// **自動まばたき**(Day 69)。ランダムな間隔で、表情 <c>blink</c> の重みを 0 → 1 → 0 と動かす。
///
/// <para>
/// 人は1分に 15〜20 回まばたきする(3〜4 秒に1回)。<b>一定の間隔にすると機械に見える</b>ので、
/// 待ち時間を毎回 <see cref="MinInterval"/>〜<see cref="MaxInterval"/> から選び直す(平均 3.5 秒)。
/// 閉じるのは速く、開くのは遅い——まぶたは筋肉で一気に下ろし、ゆっくり上がる。
/// 閉じる 0.06 秒・閉じたまま 0.03 秒・開く 0.12 秒で、1回は約 0.2 秒になる。
/// </para>
///
/// <para>
/// <b>表情のことは何も知らない</b>。出すのは「blink の重み」1つだけで、それを表情に書くのは呼ぶ側の仕事。
/// happy で目を細めている間にまばたきを止めたいなら、それは表情の <c>overrideBlink</c> が決める(ここで止めない)。
/// 作者の意図(override)とアプリの都合(自動まばたき)が、別々の場所に書けるようにしてある。
/// </para>
/// </summary>
internal sealed class AutoBlink
{
    public const float CloseSeconds = 0.06f;

    public const float HoldSeconds = 0.03f;

    public const float OpenSeconds = 0.12f;

    /// <summary>1回のまばたきの長さ。</summary>
    public const float BlinkSeconds = CloseSeconds + HoldSeconds + OpenSeconds;

    /// <summary>次のまばたきまでの待ち時間の幅(開き終わってから数える)。</summary>
    public const float MinInterval = 1.5f;

    public const float MaxInterval = 5.5f;

    private readonly Random _random;

    /// <summary>次のまばたきまでの残り時間。負になったらまばたき中。</summary>
    private float _wait;

    /// <summary>まばたきが始まってからの時間。</summary>
    private float _elapsed;

    private bool _blinking;

    /// <param name="seed">乱数の種。同じ種なら同じ間隔の並びになる(自己チェックと見比べのため)。</param>
    public AutoBlink(int seed)
    {
        _random = new Random(seed);
        _wait = NextInterval();
    }

    /// <summary>いまの blink の重み(0〜1)。</summary>
    public float Weight { get; private set; }

    /// <summary>始めたまばたきの回数。</summary>
    public int BlinkCount { get; private set; }

    /// <summary>直前に選んだ待ち時間(内訳用)。</summary>
    public float LastInterval { get; private set; }

    /// <summary>時間を進めて、重みを返す。</summary>
    public float Update(float deltaSeconds)
    {
        if (!_blinking)
        {
            _wait -= deltaSeconds;
            if (_wait > 0.0f)
            {
                Weight = 0.0f;
                return Weight;
            }

            // 待ちを使い切った。余ったぶんはまばたきの経過時間へ繰り越す(フレームの刻みで間隔が揺れないように)。
            _blinking = true;
            _elapsed = -_wait;
            BlinkCount++;
        }
        else
        {
            _elapsed += deltaSeconds;
        }

        if (_elapsed >= BlinkSeconds)
        {
            _blinking = false;
            _wait = NextInterval() - (_elapsed - BlinkSeconds);
            Weight = 0.0f;
            return Weight;
        }

        Weight = Curve(_elapsed);
        return Weight;
    }

    /// <summary>
    /// **まばたきの形**。始まってからの時間 → 重み。閉じる側は一定の速さ、開く側は滑らかに(終わりで速度 0)。
    /// </summary>
    public static float Curve(float t)
    {
        if (t <= 0.0f || t >= BlinkSeconds)
        {
            return 0.0f;
        }

        if (t < CloseSeconds)
        {
            return t / CloseSeconds;
        }

        if (t < CloseSeconds + HoldSeconds)
        {
            return 1.0f;
        }

        float x = (t - CloseSeconds - HoldSeconds) / OpenSeconds;
        return 1.0f - (x * x * (3.0f - (2.0f * x)));
    }

    private float NextInterval()
    {
        LastInterval = MinInterval + ((MaxInterval - MinInterval) * _random.NextSingle());
        return LastInterval;
    }
}
