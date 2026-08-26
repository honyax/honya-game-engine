namespace HonyaEngine;

/// <summary>レベルアップで選べるものの種類。</summary>
internal enum UpgradeKind
{
    /// <summary>持っていない武器を手に入れる</summary>
    NewWeapon,

    /// <summary>持っている武器のレベルを上げる</summary>
    WeaponLevel,

    /// <summary>最大 HP を増やす(同時に回復する)</summary>
    MaxHealth,

    /// <summary>移動速度を上げる</summary>
    MoveSpeed,

    /// <summary>ジェムを吸い寄せる範囲を広げる</summary>
    Magnet,
}

/// <summary>
/// 選択肢1つぶん。**表示する文字まで持つ**。
///
/// 「何をするか」(<see cref="Kind"/> と <see cref="Weapon"/>)と
/// 「何と出すか」(<see cref="Title"/> と <see cref="Detail"/>)を
/// 同じところで作っているのは、**ずれると嘘になる**から。
/// 「威力 +3」と書いてあるのに +2 しか上がらない、という不具合は
/// 表示とロジックが別の場所にあると必ず起きる。
///
/// 文字を持つとゲームのコードが日本語を抱えることになるが、
/// 多言語化するときは <see cref="Title"/> を「鍵」にして
/// 外の表から引く形へ変えればよい(そこまでは今日やらない)。
/// </summary>
internal readonly struct UpgradeOption
{
    public readonly UpgradeKind Kind;

    /// <summary><see cref="UpgradeKind.NewWeapon"/> と <see cref="UpgradeKind.WeaponLevel"/> でだけ意味を持つ。</summary>
    public readonly WeaponKind Weapon;

    public readonly string Title;

    public readonly string Detail;

    public UpgradeOption(UpgradeKind kind, WeaponKind weapon, string title, string detail)
    {
        Kind = kind;
        Weapon = weapon;
        Title = title;
        Detail = detail;
    }

    public static UpgradeOption NewWeapon(WeaponKind weapon) => new(
        UpgradeKind.NewWeapon,
        weapon,
        $"{Weapons.NameOf(weapon)} を習得",
        Weapons.SummaryOf(weapon));

    public static UpgradeOption WeaponLevel(WeaponKind weapon, int currentLevel) => new(
        UpgradeKind.WeaponLevel,
        weapon,
        $"{Weapons.NameOf(weapon)} Lv.{currentLevel} → {currentLevel + 1}",
        Weapons.DescribeNext(weapon, currentLevel));

    public static UpgradeOption MaxHealth() => new(
        UpgradeKind.MaxHealth,
        default,
        "体力の器",
        $"最大 HP +{GameBalance.UpgradeMaxHealth:F0}(その場で回復)");

    public static UpgradeOption MoveSpeed() => new(
        UpgradeKind.MoveSpeed,
        default,
        "軽い足",
        $"移動速度 +{GameBalance.UpgradeMoveSpeed:P0}");

    public static UpgradeOption Magnet() => new(
        UpgradeKind.Magnet,
        default,
        "引き寄せ",
        $"ジェムを拾う範囲 +{GameBalance.UpgradeMagnet:P0}");
}
