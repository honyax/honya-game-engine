namespace HonyaEngine;

/// <summary>
/// 1次元ブレンドツリー(Day 42)。**1つの数値から、クリップの重みを決める**。
///
/// <para>
/// 今日の使い方は「移動速度 → 立ち止まり / 歩き / 走り の重み」。
/// <code>
///   立ち止まり(0.0 m/s)   歩き(1.0 m/s)        走り(2.6 m/s)
///        |------------------|---------------------|
///                      速度 1.8 → 歩き 0.50 / 走り 0.50
/// </code>
/// 軸の上に「そのクリップが本来表している値」を置き、
/// **入力を挟む2本だけを混ぜる**。3本以上が同時に効くことは無い。
/// </para>
///
/// <para>
/// <b>なぜ隣り合う2本だけなのか</b>。全部を距離で重み付けする作り方もあるが、
/// そうすると「歩いているのに走りが 5% 混ざる」といったことが起き、
/// **足の運びに説明のつかない癖が乗る**。
/// 隣どうしだけに限ると、軸のどこを取っても
/// 「この2つの中間」と言い切れるので、絵の理由が追える。
/// </para>
///
/// <para>
/// <b>状態を持たない</b>のがこのクラスの性格。同じ入力からは必ず同じ重みが出る。
/// 「今どの状態にいるか」を覚えるのは <see cref="AnimationStateMachine"/> の仕事で、
/// 2つを分けておくと**両方を並べて見比べられる**(Shift+Alt+F2)。
/// </para>
/// </summary>
internal sealed class BlendTree1D
{
    /// <summary>軸の上に置いた点1つ。</summary>
    /// <param name="Name">表示用。</param>
    /// <param name="Clip"><see cref="Model.Animations"/> の添字。</param>
    /// <param name="Parameter">
    /// このクリップが本来表している入力の値(今日は m/s)。
    ///
    /// <b>ここを実測に近づけるほど足の滑りが減る</b>。
    /// 歩きのクリップが実際には 1.4m/s ぶんの歩幅で作られているのに
    /// 1.0 と書くと、キャラクタが 1.0m/s で進むあいだ足は 1.4m/s ぶん動く——
    /// つまり**足が地面を滑る**。今日は目分量で置いてあるが、
    /// 本来は「1周期で足がどれだけ後ろへ流れるか」を測って決める(改造課題1)。
    /// </param>
    internal readonly record struct Sample(string Name, int Clip, float Parameter);

    private readonly Sample[] _samples;

    /// <summary>
    /// <paramref name="samples"/> は <see cref="Sample.Parameter"/> の昇順で渡すこと。
    /// **並べ替えはこちらでやる**——呼ぶ側が順番を間違えると
    /// 「速く走るほど立ち止まりが混ざる」という愉快な壊れ方をするので、
    /// 前提を要求せずに済ませる。
    /// </summary>
    public BlendTree1D(string name, IEnumerable<Sample> samples)
    {
        Name = name;
        _samples = samples.OrderBy(sample => sample.Parameter).ToArray();
    }

    public string Name { get; }

    public IReadOnlyList<Sample> Samples => _samples;

    /// <summary>軸の下端と上端。HUD 用。</summary>
    public float MinParameter => _samples.Length > 0 ? _samples[0].Parameter : 0.0f;

    public float MaxParameter => _samples.Length > 0 ? _samples[^1].Parameter : 0.0f;

    /// <summary>
    /// 入力から重みを決めて <paramref name="destination"/> に書き、書いた本数を返す。
    ///
    /// <b>両端の外側では端の1本だけ</b>になる(clamp)。
    /// 外挿して「走りを 120% 効かせる」ことも技術的にはできるが、
    /// クォータニオンの外挿は簡単に破綻するので取らない。
    /// 速度が足りないぶんは、**再生速度のほうで吸収する**のが実務の作法。
    /// </summary>
    public int Evaluate(float parameter, Span<ClipWeight> destination)
    {
        if (_samples.Length == 0 || destination.Length == 0)
        {
            return 0;
        }

        if (_samples.Length == 1 || parameter <= _samples[0].Parameter)
        {
            destination[0] = new ClipWeight(_samples[0].Clip, 1.0f);
            return 1;
        }

        if (parameter >= _samples[^1].Parameter)
        {
            destination[0] = new ClipWeight(_samples[^1].Clip, 1.0f);
            return 1;
        }

        // 挟む2点を探す。点は数個しか無いので線形で足りる。
        int upper = 1;
        while (upper < _samples.Length - 1 && _samples[upper].Parameter <= parameter)
        {
            upper++;
        }

        Sample a = _samples[upper - 1];
        Sample b = _samples[upper];

        float span = b.Parameter - a.Parameter;

        // 同じ値の点が2つ並んでいると 0 で割る。**そのときは後ろ側に寄せる**。
        float t = span > 1e-6f ? (parameter - a.Parameter) / span : 1.0f;

        if (destination.Length == 1)
        {
            // 1本しか書けないなら、重いほうだけ。
            destination[0] = new ClipWeight(t < 0.5f ? a.Clip : b.Clip, 1.0f);
            return 1;
        }

        destination[0] = new ClipWeight(a.Clip, 1.0f - t);
        destination[1] = new ClipWeight(b.Clip, t);
        return 2;
    }
}
