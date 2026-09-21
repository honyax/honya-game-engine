namespace HonyaEngine;

/// <summary>
/// **機能の ON/OFF を1か所に集めた表**(Day 40)。今日の主役その2。
///
/// <para>
/// Day 31〜38 で積んだ機能のスイッチは、<c>Program.OnKeyDown</c> の
/// switch 文に 8 日ぶん散らばっている。1つずつ切り替えるぶんには困らないが、
/// デモとしては<b>「全部 OFF の素の絵」と「必須構成の絵」を1キーで往復できない</b>と
/// 比較にならない。
/// </para>
///
/// <para>
/// <b>Day 39 の <c>Ctrl+Shift+F12</c> が示した問題</b>がそのまま動機になっている。
/// あそこでは決めの構図を作るために、
/// <c>_env.Enabled = true; _shadow.Enabled = true; _ssao.Enabled = true; ...</c> と
/// フラグを手で並べていた。機能が増えるたびに書き足す必要があり、
/// <b>書き忘れても絵は出る</b>——ただ「前と違う絵」になるだけなので、原因を探すのが難しい。
/// 表にしてしまえば「全部」がデータになり、書き忘れようが無くなる。
/// </para>
///
/// <para>
/// <b>値を持たない</b>のがこのクラスの設計上の要点。
/// 本当の状態は <see cref="Ssao.Enabled"/> や <see cref="PostProcess.BloomEnabled"/> の
/// ほうにあり、ここが持つのは<b>そこへの読み書きの手順</b>(<see cref="Func{TResult}"/> と
/// <see cref="Action{T}"/>)だけ。
/// 二重に持つと必ずずれる——キーで直接 <c>_ssao.Enabled</c> を触られた瞬間に、
/// 表の言う ON と絵の中の OFF が食い違う。
/// **状態の置き場所は1つ**、という Day 22 のコンポーネント設計と同じ判断。
/// </para>
/// </summary>
internal sealed class FeatureToggles
{
    /// <summary>機能1つぶん。</summary>
    internal sealed class Feature
    {
        public Feature(string name, string effect, Func<bool> get, Action<bool> set)
        {
            Name = name;
            Effect = effect;
            Get = get;
            Set = set;
        }

        /// <summary>HUD に出す短い名前。</summary>
        public string Name { get; }

        /// <summary>**OFF にすると絵がどうなるか**。切り替えたときにコンソールへ出す。</summary>
        public string Effect { get; }

        public Func<bool> Get { get; }

        public Action<bool> Set { get; }

        public bool Value
        {
            get => Get();
            set => Set(value);
        }
    }

    private readonly List<Feature> _features = [];

    /// <summary>ツアーを始める前の状態。終わったらここへ戻す。</summary>
    private bool[] _tourSnapshot = [];

    private int _tourIndex;
    private float _tourTime;
    private bool _tourShowingAll = true;

    public IReadOnlyList<Feature> Features => _features;

    /// <summary><c>Ctrl+Alt+F7</c> で動くカーソル。<c>Ctrl+Alt+F8</c> がこれを切り替える。</summary>
    public int Selected { get; private set; }

    /// <summary>全部 ON か。<c>Ctrl+Alt+F9</c> の往復で「次にどちらへ倒すか」を決めるのに使う。</summary>
    public bool AllOn => _features.All(feature => feature.Value);

    /// <summary>いま ON になっている数。</summary>
    public int OnCount => _features.Count(feature => feature.Value);

    /// <summary>機能ツアー(<c>Ctrl+Alt+F10</c>)を回しているか。</summary>
    public bool TourActive { get; private set; }

    /// <summary>ツアーが1つの絵を見せる秒数。</summary>
    public float TourInterval { get; set; } = 3.0f;

    public void Add(string name, string effect, Func<bool> get, Action<bool> set) =>
        _features.Add(new Feature(name, effect, get, set));

    /// <summary>カーソルを1つ進める(輪になっている)。</summary>
    public void SelectNext() => Selected = (Selected + 1) % Math.Max(1, _features.Count);

    /// <summary>選んでいる機能を反転して、その機能を返す。</summary>
    public Feature ToggleSelected()
    {
        Feature feature = _features[Selected];
        feature.Value = !feature.Value;
        return feature;
    }

    /// <summary>全部まとめて倒す。**素の絵と必須構成の絵の往復**がこれ1回で済む。</summary>
    public void SetAll(bool on)
    {
        foreach (Feature feature in _features)
        {
            feature.Value = on;
        }
    }

    /// <summary>いまの ON/OFF を控える。</summary>
    public bool[] Capture() => [.. _features.Select(feature => feature.Value)];

    /// <summary>
    /// 控えた状態へ戻す。
    ///
    /// <para>
    /// **長さが違ったら何もしない**。機能を足したあとの古い控えを流し込むと、
    /// 添字がずれて「SSAO を切ったつもりが FXAA が消えた」ということが起きる。
    /// 黙って半分だけ適用するより、何もしないほうが原因を追いやすい。
    /// </para>
    /// </summary>
    public void Restore(bool[] state)
    {
        if (state.Length != _features.Count)
        {
            return;
        }

        for (int i = 0; i < _features.Count; i++)
        {
            _features[i].Value = state[i];
        }
    }

    /// <summary>
    /// **機能ツアー**を始める(<c>Ctrl+Alt+F10</c>)。
    ///
    /// <para>
    /// 「全部 ON の絵」と「1つだけ OFF の絵」を交互に出す。
    /// <b>差分でしか見えないもの</b>——SSAO の接地の暗がり、FXAA の縁、
    /// 法線マップの凹凸——は、並べて出すのではなく
    /// <b>同じ構図で入れたり切ったりする</b>のがいちばん分かりやすい。
    /// </para>
    /// </summary>
    public string BeginTour()
    {
        _tourSnapshot = Capture();
        _tourIndex = 0;
        _tourTime = 0.0f;
        _tourShowingAll = false;
        TourActive = true;

        SetAll(true);
        return ApplyTourStep();
    }

    /// <summary>ツアーを止めて、始める前の状態に戻す。</summary>
    public void EndTour()
    {
        if (!TourActive)
        {
            return;
        }

        TourActive = false;
        Restore(_tourSnapshot);
    }

    /// <summary>
    /// ツアーを進める。切り替えが起きたときだけ文言を返す(コンソールに出す用)。
    ///
    /// <para>
    /// <b>可変 dt で呼ばれる</b>(<c>Program.OnUpdate</c>)。
    /// カメラワークと同じで、見せ方だけの処理なので固定ステップに載せる必要が無い。
    /// </para>
    /// </summary>
    public string? Update(float deltaSeconds)
    {
        if (!TourActive || _features.Count == 0)
        {
            return null;
        }

        _tourTime += deltaSeconds;

        if (_tourTime < TourInterval)
        {
            return null;
        }

        _tourTime = 0.0f;

        if (_tourShowingAll)
        {
            // 全部 ON を見せ終わったので、次の機能を落とす。
            _tourIndex = (_tourIndex + 1) % _features.Count;
            _tourShowingAll = false;
        }
        else
        {
            _tourShowingAll = true;
        }

        return ApplyTourStep();
    }

    /// <summary>HUD に出す1行。**ON は <c>+</c>、OFF は <c>-</c>、選択中は角括弧**。</summary>
    public string Describe()
    {
        return string.Join(
            ' ',
            _features.Select((feature, index) =>
            {
                string body = (feature.Value ? "+" : "-") + feature.Name;
                return index == Selected ? $"[{body}]" : body;
            }));
    }

    /// <summary>ツアーの現在地を1行で。</summary>
    public string TourLabel()
    {
        if (!TourActive)
        {
            return "ツアー:OFF";
        }

        return _tourShowingAll
            ? "ツアー:全部 ON(戻した絵)"
            : $"ツアー:{_features[_tourIndex].Name} だけ OFF";
    }

    private string ApplyTourStep()
    {
        SetAll(true);

        if (_tourShowingAll)
        {
            return "  全部 ON に戻す";
        }

        Feature feature = _features[_tourIndex];
        feature.Value = false;

        return $"  {feature.Name} を OFF: {feature.Effect}";
    }
}
