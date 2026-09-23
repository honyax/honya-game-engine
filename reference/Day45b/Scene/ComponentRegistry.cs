namespace HonyaEngine;

/// <summary>
/// **型名とクラスの対応表**。シーンファイルを読み書きするために要る。
///
/// <c>List&lt;Component&gt;</c> をそのまま書き出そうとすると必ずここで詰まる。
/// 「この要素は <see cref="SpriteRenderer"/> なのか <see cref="BouncingMover"/> なのか」を
/// ファイルの中に書いておかないと、読むときに何を作ればいいか分からない。
/// これが**多態のシリアライズ**の本質的な問題で、
/// 解決策は「型を表す印(判別子)を一緒に書く」の一択になる。
///
/// では印に何を書くか。素直に思いつくのは <c>type.FullName</c> や
/// <c>Type.GetType(name)</c> だが、**どちらも使ってはいけない**。
///
/// **1. リファクタでファイルが壊れる**
/// クラス名を変えた瞬間、過去に保存したシーンが全部読めなくなる。
/// 名前空間を変えただけでも壊れる。
/// ファイルに書く名前は**コードの都合から切り離しておく**必要がある。
///
/// **2. 任意の型を作られてしまう**
/// <c>Type.GetType(ファイルに書いてある文字列)</c> は、
/// **書いてある名前のクラスなら何でも**作れてしまう。
/// 他人が作ったシーンやセーブデータを読むなら、これは穴になる
/// (.NET の <c>BinaryFormatter</c> が非推奨になった理由も突き詰めるとこれ)。
///
/// だから**ここに列挙したものしか読み書きしない**。
/// 名前は自分で決めた短い文字列で、クラス名とは独立に管理する。
/// コンポーネントを増やしたら <see cref="Register{T}"/> を1行足す——
/// 面倒に見えるが、この面倒さが上の2つを防いでいる。
/// </summary>
internal static class ComponentRegistry
{
    private static readonly Dictionary<string, Type> TypeByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, string> NameByType = [];

    static ComponentRegistry()
    {
        // **ここに無いものはファイルに出ない**。
        // 名前を変えると過去のファイルが読めなくなるので、一度決めたら固定する。
        Register<SpriteRenderer>("SpriteRenderer");
        Register<BouncingMover>("BouncingMover");
        Register<OrbitMover>("OrbitMover");
        Register<PlayerController>("PlayerController");
        Register<LifecycleLogger>("LifecycleLogger");
    }

    public static IEnumerable<string> Names => TypeByName.Keys;

    public static void Register<T>(string name)
        where T : Component, new()
    {
        TypeByName[name] = typeof(T);
        NameByType[typeof(T)] = name;
    }

    /// <summary>クラス → ファイルに書く名前。</summary>
    public static string NameOf(Type type) =>
        NameByType.TryGetValue(type, out string? name)
            ? name
            : throw new InvalidOperationException(
                $"{type.Name} は ComponentRegistry に登録されていません。Register を1行足してください");

    /// <summary>ファイルに書いてある名前 → クラス。知らない名前なら null。</summary>
    public static Type? TypeOf(string name) =>
        TypeByName.TryGetValue(name, out Type? type) ? type : null;
}
