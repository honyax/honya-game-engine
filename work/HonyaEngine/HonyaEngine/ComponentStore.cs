namespace HonyaEngine;

/// <summary>
/// エンティティの番号を渡すと中身を消せる、型を問わない入口。
/// <see cref="World.DestroyEntity"/> が全ストアを回るために要る。
/// </summary>
internal interface IComponentStore
{
    int Count { get; }

    string ComponentName { get; }

    bool Remove(int entityIndex);

    void Clear();
}

/// <summary>
/// **1種類のコンポーネントだけを、隙間なく並べて持つ**入れ物。
///
/// 中身は3本の配列でできている(いわゆる sparse set)。
///
/// <code>
///   _dense       [ Pos0 ][ Pos1 ][ Pos2 ] ...   ← 実体。隙間なし。ここを舐める
///   _denseToEntity [  7  ][  3  ][ 12  ] ...   ← 密な添字 → エンティティ番号
///   _entityToDense [ .. ][ .. ][  1  ][ .. ]   ← エンティティ番号 → 密な添字(-1 で無し)
/// </code>
///
/// **`_dense` が隙間なく詰まっている**のがすべて。
/// システムはここを頭から順に舐めるだけなので、
///   - CPU のプリフェッチが効く(次に読む場所が予測できる)
///   - キャッシュラインを無駄なく使える(64 バイトに何個も入る)
///   - SIMD 化の余地も残る
/// Day 22 の GameObject 方式は、この逆——1個ぶんがまとまっている代わりに、
/// **同じ種類のものが散らばっている**。
///
/// 引き換えに払うのが `_entityToDense` の1段。
/// 「このエンティティの位置は?」を引くには
/// エンティティ番号 → 密な添字 → 実体、と2回たどる。
/// これが**結合(join)のコスト**で、計画書の要点4で測る。
/// </summary>
internal sealed class ComponentStore<T> : IComponentStore
    where T : struct
{
    private T[] _dense = new T[64];
    private int[] _denseToEntity = new int[64];
    private int[] _entityToDense = [];
    private int _count;

    public int Count => _count;

    public string ComponentName => typeof(T).Name;

    /// <summary>
    /// 実体の並び。**システムはこれを頭から舐める**。
    ///
    /// <c>Span&lt;T&gt;</c> で返しているので、呼び出し側は
    /// <c>ref</c> で書き換えられるうえ、境界チェックが1回で済む。
    /// <c>List&lt;T&gt;</c> の添字アクセスだと、構造体は**コピーが返る**ので
    /// その場で書き換えられない(ここが値型を配列で持つときの落とし穴)。
    /// </summary>
    public Span<T> Values => _dense.AsSpan(0, _count);

    /// <summary>密な添字に対応するエンティティ番号の並び。<see cref="Values"/> と同じ長さ。</summary>
    public ReadOnlySpan<int> Entities => _denseToEntity.AsSpan(0, _count);

    public void Add(int entityIndex, in T value)
    {
        EnsureEntityCapacity(entityIndex);

        if (_entityToDense[entityIndex] >= 0)
        {
            _dense[_entityToDense[entityIndex]] = value;
            return;
        }

        if (_count == _dense.Length)
        {
            Array.Resize(ref _dense, _dense.Length * 2);
            Array.Resize(ref _denseToEntity, _denseToEntity.Length * 2);
        }

        _dense[_count] = value;
        _denseToEntity[_count] = entityIndex;
        _entityToDense[entityIndex] = _count;
        _count++;
    }

    public bool Has(int entityIndex) =>
        (uint)entityIndex < (uint)_entityToDense.Length && _entityToDense[entityIndex] >= 0;

    /// <summary>
    /// 中身への**参照**を返す。無ければ例外。
    ///
    /// <c>ref</c> で返すのが肝心なところ。値を返すとコピーになり、
    /// 書き換えても元に戻らない。ECS のシステムは
    /// 「引いて、書き換えて、そのまま」が基本なので、参照でないと話にならない。
    /// </summary>
    public ref T Get(int entityIndex)
    {
        int dense = _entityToDense[entityIndex];
        if (dense < 0)
        {
            throw new InvalidOperationException($"エンティティ {entityIndex} は {ComponentName} を持っていません");
        }

        return ref _dense[dense];
    }

    /// <summary>
    /// 密な添字を返す。**結合の内側で使う**。
    /// <see cref="Get"/> と違って例外を投げないので、分岐で捌ける。
    /// </summary>
    public int DenseIndexOf(int entityIndex) =>
        (uint)entityIndex < (uint)_entityToDense.Length ? _entityToDense[entityIndex] : -1;

    /// <summary>密な添字で直接引く。<see cref="DenseIndexOf"/> と組で使う。</summary>
    public ref T AtDense(int denseIndex) => ref _dense[denseIndex];

    /// <summary>
    /// 取り除く。**末尾と入れ替えて縮める**ので O(1)。
    ///
    /// 代わりに**並び順が変わる**。
    ///   - 密な添字を覚えておいてはいけない(次のフレームには別人かもしれない)
    ///   - システムの処理順は「生成順」ですらなくなる
    /// 後者は Day 22 の「実行順が決まっていない」と同じ問題に見えるが、性質が違う。
    /// ECS では**同じシステムの中では全員が同じ処理を受ける**ので、
    /// 個体どうしの順序が結果に効くこと自体がまれ
    /// (効くなら、それは共有状態を触っている証拠なので設計を疑う)。
    /// </summary>
    public bool Remove(int entityIndex)
    {
        if (!Has(entityIndex))
        {
            return false;
        }

        int dense = _entityToDense[entityIndex];
        int last = _count - 1;

        if (dense != last)
        {
            _dense[dense] = _dense[last];
            int movedEntity = _denseToEntity[last];
            _denseToEntity[dense] = movedEntity;
            _entityToDense[movedEntity] = dense;
        }

        _entityToDense[entityIndex] = -1;
        _count--;
        return true;
    }

    public void Clear()
    {
        Array.Fill(_entityToDense, -1);
        _count = 0;
    }

    private void EnsureEntityCapacity(int entityIndex)
    {
        if (entityIndex < _entityToDense.Length)
        {
            return;
        }

        int old = _entityToDense.Length;
        int size = Math.Max(64, old);
        while (size <= entityIndex)
        {
            size *= 2;
        }

        Array.Resize(ref _entityToDense, size);

        // **-1 で埋め直す**。Array.Resize は 0 で埋めるので、
        // そのままだと「全員が密な添字 0 を持っている」ことになる。
        Array.Fill(_entityToDense, -1, old, size - old);
    }
}
