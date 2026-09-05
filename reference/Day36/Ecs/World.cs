namespace HonyaEngine;

/// <summary>
/// ECS の器。**エンティティの番号を配り、種類ごとの <see cref="ComponentStore{T}"/> を束ねる**。
///
/// Day 22 の <see cref="Scene"/> と役割は同じだが、中身の持ち方が裏返っている。
///
/// <code>
///   Scene  : GameObject[] → それぞれが Component[] を持つ   (1個ぶんがまとまる)
///   World  : ComponentStore ごとに配列                       (同じ種類がまとまる)
/// </code>
///
/// そして**ふるまいがここに無い**。<see cref="Scene.FixedUpdate"/> に相当するものは
/// <see cref="EcsSystems"/> の側にあり、World はデータを預かるだけ。
///   - データ(コンポーネント)… 構造体。ふるまいを持たない
///   - ふるまい(システム)   … 静的メソッド。状態を持たない
/// の分離が ECS の名前の由来(Entity / Component / System)で、
/// 「どのシステムをどの順で回すか」を呼び出し側が明示的に書くことになる。
/// **Day 22 で決まっていなかった実行順が、ここでは書かないと動かない**。
/// </summary>
internal sealed class World
{
    /// <summary>エンティティ番号 → 世代。0 は「この枠は空いている」。</summary>
    private uint[] _versions = new uint[64];

    /// <summary>空いた番号。**若い番号から詰め直す**ので、番号が無闇に増えない。</summary>
    private readonly Stack<int> _free = new();

    /// <summary>まだ一度も使っていない番号の先頭。</summary>
    private int _nextIndex;

    private readonly Dictionary<Type, IComponentStore> _stores = [];

    /// <summary>破棄のときに全部回るための一覧。<see cref="_stores"/> と同じ中身。</summary>
    private readonly List<IComponentStore> _storeList = [];

    public int AliveCount { get; private set; }

    public int SlotCount => _nextIndex;

    public IReadOnlyList<IComponentStore> Stores => _storeList;

    public Entity CreateEntity()
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop();
        }
        else
        {
            if (_nextIndex == _versions.Length)
            {
                Array.Resize(ref _versions, _versions.Length * 2);
            }

            index = _nextIndex++;
        }

        if (index > Entity.MaxIndex)
        {
            throw new InvalidOperationException($"エンティティが {Entity.MaxIndex} を超えました");
        }

        // 世代 0 は無効の予約なので、初回は 1 から始める。
        if (_versions[index] == 0)
        {
            _versions[index] = 1;
        }

        AliveCount++;
        return new Entity(index, _versions[index]);
    }

    public bool IsAlive(Entity entity) =>
        entity.IsValid
        && (uint)entity.Index < (uint)_nextIndex
        && _versions[entity.Index] == entity.Version;

    /// <summary>
    /// エンティティを消す。**全ストアから抜く**。
    ///
    /// Day 22 の <see cref="Scene.Destroy"/> と違って、予約ではなく即座に消している。
    /// システムの途中で消すと、そのシステムが舐めている密な配列が
    /// 目の前で入れ替わるので危ない。
    /// **消すのはシステムとシステムの間**、という約束にしておくのが素直で、
    /// システムの順番が明示的な ECS ではそれが書きやすい
    /// (「消す専門のシステム」を最後に1つ置けばよい)。
    /// </summary>
    public bool DestroyEntity(Entity entity)
    {
        if (!IsAlive(entity))
        {
            return false;
        }

        for (int i = 0; i < _storeList.Count; i++)
        {
            _storeList[i].Remove(entity.Index);
        }

        // 世代を進める。この1行で、この番号を握っていた全員が無効になる
        // (Day 21 の ResourcePool.Release とまったく同じ理屈)。
        uint next = _versions[entity.Index] + 1;
        _versions[entity.Index] = next > Entity.MaxVersion ? 1u : next;

        _free.Push(entity.Index);
        AliveCount--;
        return true;
    }

    /// <summary>
    /// 型に対応するストアを取り出す(無ければ作る)。
    ///
    /// <c>Dictionary</c> を引くので**安くはない**。
    /// システムの中のループで毎回呼ぶのではなく、
    /// **ループの外で1回引いて手元に持つ**のが前提の作り
    /// (Day 22 の <c>GetComponent</c> と同じ話がここでも出てくる)。
    /// </summary>
    public ComponentStore<T> Store<T>()
        where T : struct
    {
        if (_stores.TryGetValue(typeof(T), out IComponentStore? existing))
        {
            return (ComponentStore<T>)existing;
        }

        var store = new ComponentStore<T>();
        _stores[typeof(T)] = store;
        _storeList.Add(store);
        return store;
    }

    public void Add<T>(Entity entity, in T value)
        where T : struct
    {
        if (!IsAlive(entity))
        {
            throw new InvalidOperationException($"{entity} はもう生きていません");
        }

        Store<T>().Add(entity.Index, value);
    }

    public bool Has<T>(Entity entity)
        where T : struct => IsAlive(entity) && Store<T>().Has(entity.Index);

    public ref T Get<T>(Entity entity)
        where T : struct => ref Store<T>().Get(entity.Index);

    public bool Remove<T>(Entity entity)
        where T : struct => IsAlive(entity) && Store<T>().Remove(entity.Index);

    public void Clear()
    {
        foreach (IComponentStore store in _storeList)
        {
            store.Clear();
        }

        // 世代は**進めたまま残す**。配列ごと捨てて 0 に戻すと、
        // 前のシーンのエンティティが新しいシーンで蘇りかねない。
        for (int i = 0; i < _nextIndex; i++)
        {
            uint next = _versions[i] + 1;
            _versions[i] = next > Entity.MaxVersion ? 1u : next;
        }

        _free.Clear();
        _nextIndex = 0;
        AliveCount = 0;
    }

    /// <summary>デバッグ表示用。ストアごとの件数。</summary>
    public string DescribeStores() =>
        string.Join(" ", _storeList.Select(store => $"{store.ComponentName}:{store.Count}"));
}
