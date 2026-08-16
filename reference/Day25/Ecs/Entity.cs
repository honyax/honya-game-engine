namespace HonyaEngine;

/// <summary>
/// ECS の「もの」。**ただの番号**で、中身は何も持たない。
///
/// Day 22 の <see cref="GameObject"/> は、名前と <see cref="Transform"/> と
/// コンポーネントのリストを抱えたヒープ上のオブジェクトだった。
/// ECS ではそれが 32 ビットの整数1個になる。
/// 「位置」も「絵」も「速度」も、エンティティが**持っている**のではなく、
/// <see cref="ComponentStore{T}"/> の側が「この番号のぶん」として持つ。
///
/// この裏返しが ECS の全部と言ってよい。
///   - オブジェクトが部品を持つ → 部品の配列がエンティティ番号で引かれる
///   - 「1個ぶん」がまとまっている → 「同じ種類」がまとまっている
/// 後者のほうが、**同じ処理を全員にかける**ときに圧倒的に速い(計画書の要点1)。
///
/// 中身の作りは Day 21 の <see cref="Handle{T}"/> とまったく同じ
/// ——世代 8 ビット + 添字 24 ビットで、世代 0 を無効に予約している。
/// **「使い回される配列の枠を安全に指す」という問題は同じ**なので、答えも同じになる。
/// 別の型にしてあるのは、リソースのハンドルと混ざると意味が分からなくなるから。
/// </summary>
internal readonly struct Entity : IEquatable<Entity>
{
    private const int IndexBits = 24;
    private const uint IndexMask = (1u << IndexBits) - 1u;

    public const uint MaxVersion = (1u << (32 - IndexBits)) - 1u;
    public const int MaxIndex = (int)IndexMask;

    private readonly uint _bits;

    internal Entity(int index, uint version)
    {
        _bits = ((uint)index & IndexMask) | (version << IndexBits);
    }

    /// <summary>配列の何番目か。**コンポーネントを引くときの鍵**。</summary>
    internal int Index => (int)(_bits & IndexMask);

    /// <summary>その枠が何代目か。破棄のたびに +1 される。</summary>
    internal uint Version => _bits >> IndexBits;

    /// <summary>世代 0 は「無効」に予約。<c>default(Entity)</c> が自動的に無効になる。</summary>
    public bool IsValid => Version != 0;

    public static Entity None => default;

    public bool Equals(Entity other) => _bits == other._bits;

    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => (int)_bits;

    public static bool operator ==(Entity left, Entity right) => left._bits == right._bits;

    public static bool operator !=(Entity left, Entity right) => left._bits != right._bits;

    public override string ToString() => IsValid ? $"E{Index}.v{Version}" : "E-none";
}
