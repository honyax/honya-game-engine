using System.Text.Json.Serialization;

namespace HonyaEngine;

/// <summary>
/// <see cref="GameObject"/> に足す「ふるまい」の基底。
///
/// **継承ではなく合成でゲームを組む**というのが、この設計のたった1つの主張。
///
/// 継承で組むとどうなるかを想像すると分かりやすい。
/// <c>Entity</c> → <c>MovingEntity</c> → <c>Enemy</c> → <c>FlyingEnemy</c> と伸ばしていって、
/// あるとき「飛ばないが爆発する敵」が要る、となる。
/// <c>ExplodingEnemy</c> を作ると爆発の処理が <c>FlyingEnemy</c> と重複し、
/// かといって <c>Enemy</c> に上げると爆発しない敵まで爆発の処理を持つ。
/// **機能の組み合わせが増えるほど、木のどこに置いても間違いになる**。
///
/// 合成なら「爆発する」を1個のコンポーネントにして、必要なものに足すだけ。
/// 組み合わせは掛け算で増えるが、部品は足し算でしか増えない。
///
/// 代償もある。
///   - 部品どうしが話すには <see cref="GameObject.GetComponent{T}"/> が要る(検索が発生する)
///   - 実行順が決まらない(<see cref="Scene"/> のコメント参照)
///   - オブジェクト1個につきヒープ上のオブジェクトが何個も要る
///     (スプライト1枚が GameObject + Transform + List + 部品2個 = 実測 515 バイト)
/// この3つが Day 23 の ECS の動機になる。**今日は代償のほうも測る**——
/// 2万個の更新で、構造体の配列の 0.08ms に対し 1.37ms(17倍)。
/// </summary>
internal abstract class Component
{
    private bool _enabled = true;

    /// <summary>
    /// 付いている先。<see cref="GameObject.AddComponent{T}"/> が設定する。
    ///
    /// **保存しない**(Day 24)。ここを保存対象にすると、
    /// コンポーネント → GameObject → コンポーネント → … と無限に潜る。
    /// 親子や所有関係は「誰が誰を持っているか」を1方向だけ書けば復元できるので、
    /// 逆向きのリンクは書かない、が原則。
    /// </summary>
    [JsonIgnore]
    public GameObject GameObject { get; internal set; } = null!;

    /// <summary>付いている先の <see cref="Transform"/>。いちばんよく使うので近道を用意する。</summary>
    [JsonIgnore]
    public Transform Transform => GameObject.Transform;

    /// <summary>
    /// 有効か。切ると <see cref="FixedUpdate"/> が呼ばれなくなる。
    ///
    /// <c>GameObject</c> ごと切る(<see cref="GameObject.SetActive"/>)のとは別で、
    /// **「このふるまいだけ止めたい」**ときに使う。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;

            if (GameObject.ActiveInHierarchy)
            {
                if (value)
                {
                    OnEnable();
                }
                else
                {
                    OnDisable();
                }
            }
        }
    }

    /// <summary>
    /// 付いた直後に1回。**自分の初期化だけ**をここでやる。
    ///
    /// 他のコンポーネントを探すのは <see cref="Start"/> のほう。
    /// Awake の時点では、同じ <c>GameObject</c> に後から足される部品がまだ存在しない。
    /// </summary>
    protected internal virtual void Awake()
    {
    }

    /// <summary>
    /// 最初の <see cref="FixedUpdate"/> の直前に1回。
    ///
    /// **Awake と分かれている理由がここ**。Start が走る時点では、
    /// そのステップで生成されたオブジェクトの Awake が全部終わっている。
    /// だから「相手を探す」処理を安心して書ける
    /// ——A が B を、B が A を探しても、どちらの順で作っても動く。
    /// Awake でやると、先に作られたほうが相手を見つけられない。
    /// </summary>
    protected internal virtual void Start()
    {
    }

    protected internal virtual void OnEnable()
    {
    }

    protected internal virtual void OnDisable()
    {
    }

    /// <summary>
    /// 固定間隔の更新(Day 19)。<paramref name="deltaTime"/> は常に同じ値。
    ///
    /// 描画用の <c>Update</c> をあえて分けていない。
    /// **ゲームの状態を変える場所は1つにしておく**ほうが、
    /// 決定性(Day 19 要点7)を保ちやすい。
    /// 見せ方の調整は <see cref="Transform"/> の補間が引き受ける。
    /// </summary>
    protected internal virtual void FixedUpdate(float deltaTime)
    {
    }

    protected internal virtual void OnDestroy()
    {
    }
}
