namespace HonyaEngine;

/// <summary>
/// 簡易アニメーションステートマシン(Day 42)。
/// **離散的な状態を1つ持ち、切り替わるときだけクロスフェードする**。
///
/// <para>
/// <see cref="BlendTree1D"/> と同じ入力(移動速度)を受けるが、出す答えの性格が違う。
/// <code>
///   ブレンドツリー    速度 1.8 → 歩き 0.50 / 走り 0.50   **常に混ざっている**
///   ステートマシン    速度 1.8 → 走り 1.00               **どれか1つ。移り変わる瞬間だけ混ざる**
/// </code>
/// どちらが正しいというものではなく、**題材で使い分ける**。
/// </para>
///
/// <list type="bullet">
/// <item>移動の歩容(歩き↔走り)は連続量なので**ブレンドツリーが向く**</item>
/// <item>攻撃・被弾・ジャンプのように「起きるか起きないか」のものは**ステートマシンが向く**
/// ——半分だけ攻撃している状態に意味は無い</item>
/// </list>
///
/// <para>
/// <b>ヒステリシスがこのクラスの肝</b>。しきい値をそのまま使うと、
/// 速度がちょうど境目にあるとき**毎フレーム状態が行き来する**(チャタリング)。
/// クロスフェードが始まっては打ち切られるので、絵がぶるぶる震える。
/// 上がるときと下がるときでしきい値をずらすと、これが構造的に消える。
/// </para>
///
/// <para>
/// <b>簡易版と断っている理由</b>。本物は「遷移中に別の遷移が起きたとき、
/// 今混ざっている姿勢をそのまま出発点にする」——つまりポーズを1枚保存してから
/// フェードする。ここでは**直前の状態を1つ覚えるだけ**なので、
/// 走り出してすぐ止まると、混ざりかけの姿勢が捨てられて少し飛ぶ。
/// 気になる規模になるのは遷移が重なる作りにしてからで、そのときに直せばよい。
/// </para>
/// </summary>
internal sealed class AnimationStateMachine
{
    /// <summary>状態1つ。</summary>
    /// <param name="Name">表示用。</param>
    /// <param name="Clip"><see cref="Model.Animations"/> の添字。</param>
    /// <param name="Threshold">
    /// この状態に**上がる**ための入力の下限。昇順に並べる。
    /// 下がるのは <c>Threshold - Hysteresis</c> を下回ったとき。
    /// </param>
    internal readonly record struct State(string Name, int Clip, float Threshold);

    private readonly State[] _states;

    private int _current;
    private int _previous = -1;

    /// <summary>クロスフェードの進み具合(0〜1)。1 なら移行済み。</summary>
    private float _fade = 1.0f;

    public AnimationStateMachine(IEnumerable<State> states)
    {
        _states = states.OrderBy(state => state.Threshold).ToArray();
    }

    public IReadOnlyList<State> States => _states;

    /// <summary>今の状態。</summary>
    public int Current => _current;

    public string CurrentName => _states.Length > 0 ? _states[_current].Name : "なし";

    /// <summary>移行中なら直前の状態。移行が終わっていれば -1。</summary>
    public int Previous => _fade < 1.0f ? _previous : -1;

    public string PreviousName =>
        Previous >= 0 && Previous < _states.Length ? _states[Previous].Name : "-";

    /// <summary>クロスフェードの進み具合(0〜1)。</summary>
    public float Fade => _fade;

    /// <summary>
    /// クロスフェードにかける秒数。**0 なら瞬間で切り替わる**。
    ///
    /// 0 にすると「足の位置がワープする」のがはっきり見えるので、
    /// クロスフェードが何をしているかの確認に使える(Shift+Alt+F8)。
    /// 長くしすぎる(0.5 秒など)と、今度は
    /// **走り出しているのに歩きの脚がまだ残る**ので、締まりが無くなる。
    /// 実務では 0.1〜0.3 秒あたりに落ち着くことが多い。
    /// </summary>
    public float FadeSeconds { get; set; } = 0.25f;

    /// <summary>
    /// しきい値の下側にずらす幅。**チャタリング止め**。
    ///
    /// 0 にすると、速度をしきい値ぴったりに置いたときに
    /// 状態が毎フレーム往復する。自己チェックがこれを数字で見ている。
    /// </summary>
    public float Hysteresis { get; set; } = 0.25f;

    /// <summary>状態を強制的に置き直す(切り替えの瞬間を作らない)。</summary>
    public void Reset(int state)
    {
        _current = Math.Clamp(state, 0, Math.Max(_states.Length - 1, 0));
        _previous = -1;
        _fade = 1.0f;
    }

    /// <summary>
    /// 入力 <paramref name="parameter"/> で状態を更新し、重みを <paramref name="destination"/> に書く。
    /// 書いた本数を返す。
    ///
    /// <b>状態の決め方は「上へ1段ずつ、下へ1段ずつ」</b>。一気に2段飛ぶ入力が来ても、
    /// while で必要なだけ進む。1フレームで Idle から Run へ飛ぶことはありうるので、
    /// 「隣としか行き来しない」と決め打ちにはしない。
    /// </summary>
    public int Update(float deltaSeconds, float parameter, Span<ClipWeight> destination)
    {
        if (_states.Length == 0 || destination.Length == 0)
        {
            return 0;
        }

        int target = _current;

        // **上がるときは Threshold をそのまま**。
        while (target + 1 < _states.Length && parameter >= _states[target + 1].Threshold)
        {
            target++;
        }

        // **下がるときは Hysteresis のぶん深く**。
        // ここを Threshold のままにすると、境目でチャタリングが起きる。
        while (target > 0 && parameter < _states[target].Threshold - Hysteresis)
        {
            target--;
        }

        if (target != _current)
        {
            _previous = _current;
            _current = target;

            // FadeSeconds が 0 なら移行済みとして扱う(0 で割らない)。
            _fade = FadeSeconds > 1e-4f ? 0.0f : 1.0f;
        }

        if (_fade < 1.0f)
        {
            _fade = MathF.Min(1.0f, _fade + (deltaSeconds / MathF.Max(FadeSeconds, 1e-4f)));
        }

        int count = 0;

        // **前の状態は「まだ残っている」ぶんだけ**。
        if (_fade < 1.0f && _previous >= 0 && _previous != _current && destination.Length > 1)
        {
            destination[count++] = new ClipWeight(_states[_previous].Clip, 1.0f - _fade);
        }

        // **重み 0 で書かない**。SetBlend は 0 の項を捨てるので、
        // フェードが始まった瞬間(dt が 0 のコマ送りなど)に1本も残らなくなる。
        destination[count++] = new ClipWeight(_states[_current].Clip, MathF.Max(_fade, 1e-3f));
        return count;
    }
}
