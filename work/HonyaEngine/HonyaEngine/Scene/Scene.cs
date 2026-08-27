using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// <see cref="GameObject"/> の入れ物であり、**ライフサイクルを回す人**。
///
/// 1ステップでやることは4つ。順番に意味がある。
///
///   1. 全 Transform の値を控える(補間用)
///   2. まだ <c>Start</c> していないコンポーネントに <c>Start</c> を配る
///   3. 全コンポーネントの <c>FixedUpdate</c> を呼ぶ
///   4. 破棄の予約をまとめて処理する
///
/// **3 と 4 が分かれている**のが実装上いちばん大事なところ。
/// 更新中に <c>List</c> から要素を消すと、そこで例外になるか、
/// 運が悪いと1個飛ばして更新される。だから消すのは「予約」にして、
/// 全員の更新が終わったところでまとめて片付ける。
/// この「予約して後で」は、ゲームエンジンのあちこちに出てくる形。
///
/// **実行順は決めていない**。<c>_gameObjects</c> に入っている順に呼ぶだけで、
/// それは生成した順でしかない。
/// 「敵より先にプレイヤーを動かしたい」のような要求が出てきたら、
/// 優先度を持たせるか、フェーズ(入力 → 移動 → 当たり判定)に分けることになる。
/// **順序が要るのに順序を決めていない設計は、必ずどこかで1フレーム遅れのバグを生む**。
/// Day 23 の ECS では、この点が「システムを並べる」という形で明示的になる。
/// </summary>
internal sealed class Scene
{
    private readonly List<GameObject> _gameObjects = [];

    /// <summary>まだ <c>Start</c> を呼んでいないコンポーネント。</summary>
    private readonly List<Component> _pendingStart = [];

    /// <summary>破棄の予約。</summary>
    private readonly List<GameObject> _pendingDestroy = [];

    public IReadOnlyList<GameObject> GameObjects => _gameObjects;

    public int GameObjectCount => _gameObjects.Count;

    public int ComponentCount { get; private set; }

    /// <summary>
    /// このステップの入力(Day 20)。<see cref="FixedUpdate"/> の前に入れておく。
    ///
    /// コンポーネントから入力を触る手段が要るので、シーンにぶら下げている。
    /// **本来はサービスの登録先(<c>IServiceProvider</c> 的なもの)を用意すべき**で、
    /// このままだと「シーンに何でも生えていく」ことになる。
    /// 実際この直後に <see cref="Bounds"/> が生えた。**2つ目が生えたら合図**で、
    /// 3つ目が来る前に登録の仕組みを作るのが正しい。今日は2つで止めておく。
    /// </summary>
    public InputSnapshot Input { get; set; }

    /// <summary>遊べる範囲(ピクセル)。壁で跳ね返る部品が参照する。</summary>
    public Vector2 Bounds { get; set; } = new(960.0f, 640.0f);

    public GameObject CreateGameObject(string name, Transform? parent = null)
    {
        var gameObject = new GameObject(this, name);
        gameObject.Transform.SetParent(parent);
        _gameObjects.Add(gameObject);
        return gameObject;
    }

    /// <summary>
    /// 破棄を**予約**する。実際に消えるのはこのステップの終わり。
    ///
    /// 呼んだ直後はまだ生きているので、
    /// <c>Destroy(enemy); enemy.Transform.LocalPosition = ...;</c> と書いても落ちない。
    /// 落ちないぶん「消したはずのものが1ステップ動く」ことになるので、
    /// <see cref="GameObject.IsDestroyed"/> を見て弾く必要がある場面はある。
    /// </summary>
    public void Destroy(GameObject gameObject)
    {
        if (gameObject.IsDestroyed)
        {
            return;
        }

        gameObject.IsDestroyed = true;
        _pendingDestroy.Add(gameObject);

        // 子も道連れ。親だけ消えて子が宙に浮くほうが事故になる。
        foreach (Transform child in gameObject.Transform.Children)
        {
            Destroy(child.GameObject);
        }
    }

    /// <summary>コンポーネントが付いた直後に <see cref="GameObject.AddComponent{T}"/> から呼ばれる。</summary>
    internal void RegisterComponent(Component component)
    {
        ComponentCount++;
        component.Awake();

        if (component.Enabled && component.GameObject.ActiveInHierarchy)
        {
            component.OnEnable();
        }

        _pendingStart.Add(component);
    }

    /// <summary>1ステップぶん進める。</summary>
    public void FixedUpdate(float deltaTime)
    {
        SnapshotTransforms();
        RunPendingStart();
        UpdateComponents(deltaTime);
        FlushDestroy();
    }

    private void SnapshotTransforms()
    {
        for (int i = 0; i < _gameObjects.Count; i++)
        {
            // 無効なオブジェクトも控える。**再開したときに前の位置から補間が始まる**
            // ようにしておかないと、有効に戻した瞬間に線を引いて飛ぶ。
            _gameObjects[i].Transform.Snapshot();
        }
    }

    private void RunPendingStart()
    {
        // Start の中でさらにオブジェクトが作られることがあるので、
        // **開始時点の件数だけ**回す。増えたぶんは次のステップに回る。
        int count = _pendingStart.Count;
        for (int i = 0; i < count; i++)
        {
            Component component = _pendingStart[i];
            if (!component.GameObject.IsDestroyed)
            {
                component.Start();
            }
        }

        _pendingStart.RemoveRange(0, count);
    }

    private void UpdateComponents(float deltaTime)
    {
        // ここも開始時点の件数で止める。
        // このステップで生まれたオブジェクトは、Start も済んでいないのに
        // FixedUpdate が呼ばれることになってしまう。
        int gameObjectCount = _gameObjects.Count;

        for (int i = 0; i < gameObjectCount; i++)
        {
            GameObject gameObject = _gameObjects[i];
            if (gameObject.IsDestroyed || !gameObject.ActiveInHierarchy)
            {
                continue;
            }

            IReadOnlyList<Component> components = gameObject.Components;
            for (int j = 0; j < components.Count; j++)
            {
                Component component = components[j];
                if (component.Enabled)
                {
                    component.FixedUpdate(deltaTime);
                }
            }
        }
    }

    private void FlushDestroy()
    {
        if (_pendingDestroy.Count == 0)
        {
            return;
        }

        foreach (GameObject gameObject in _pendingDestroy)
        {
            IReadOnlyList<Component> components = gameObject.Components;
            for (int i = 0; i < components.Count; i++)
            {
                Component component = components[i];
                if (component.Enabled && gameObject.ActiveSelf)
                {
                    component.OnDisable();
                }

                component.OnDestroy();
            }

            ComponentCount -= components.Count;

            // 親から外しておかないと、親の子リストに死んだものが残る。
            gameObject.Transform.SetParent(null);
        }

        // **まとめて1回だけ詰め直す**。1個ずつ Remove すると、
        // そのたびに後ろ全部がずれるので O(n^2) になる。
        // 大量に消える瞬間(ウェーブの全滅など)に効く。
        _gameObjects.RemoveAll(gameObject => gameObject.IsDestroyed);
        _pendingStart.RemoveAll(component => component.GameObject.IsDestroyed);
        _pendingDestroy.Clear();
    }

    /// <summary>全部消す。シーンの切り替え(Day 24)で使う。</summary>
    public void Clear()
    {
        foreach (GameObject gameObject in _gameObjects)
        {
            gameObject.IsDestroyed = true;
            foreach (Component component in gameObject.Components)
            {
                component.OnDestroy();
            }
        }

        _gameObjects.Clear();
        _pendingStart.Clear();
        _pendingDestroy.Clear();
        ComponentCount = 0;
    }
}
