using System.Numerics;

namespace HonyaEngine;

/// <summary>武器の種類。**3つだけ**だが、当たり方が全部違う。</summary>
internal enum WeaponKind
{
    /// <summary>いちばん近い敵へ弾を撃つ。Day 29 からあるもの</summary>
    Bolt,

    /// <summary>プレイヤーの周りを回る球。**弾を持たない**</summary>
    Orbit,

    /// <summary>周囲に持続ダメージ。**狙わない**</summary>
    Aura,
}

/// <summary>
/// 持っている武器1つぶんの状態。**レベルとタイマーだけ**。
///
/// 威力も間隔も個数も持っていないのがポイントで、
/// それらは <see cref="Weapons.StatsFor"/> がレベルから計算する。
/// **状態と、状態から決まるもの**を分けておくと、
/// 成長カーブを触るときに1箇所で済む。
/// 逆にここに威力を持たせると、レベルアップのたびに
/// 「どの数字をいくつ足すか」があちこちに散らばる。
/// </summary>
internal struct WeaponState
{
    public WeaponKind Kind;

    public int Level;

    /// <summary>次の発動までの残り時間。</summary>
    public float Timer;

    /// <summary>オービットの回転角。**それ以外の武器では使わない**。</summary>
    public float Angle;
}

/// <summary>
/// レベルから決まる武器の性能。**その場で計算して返す値**。
///
/// フィールドの意味が武器によって違う(<see cref="Radius"/> は
/// オービットなら周回半径、オーラなら効果範囲)のは、
/// **3種類しかないうちは、型を分けるより1つで済ませたほうが読みやすい**から。
/// 5種類を超えたあたりで割るのが目安になる(Day 29 の改造課題3)。
/// </summary>
internal readonly struct WeaponStats
{
    /// <summary>
    /// 発動間隔(秒)。**オービットでは使わない**(毎ステップ判定する)。
    ///
    /// 刻んで判定するのは、<b>判定するものがゆっくり動くとき</b>にだけ通じる。
    /// オービットの球は 1 秒に 200px 以上動くので、
    /// 0.22 秒ごとに位置を見ると **1回の判定の間に 50px 飛ぶ**——
    /// その間にいた敵は一度も判定されない。
    /// Day 25 の改造課題3(すり抜け)とまったく同じ話が、
    /// 攻撃側で起きたことになる。
    /// </summary>
    public readonly float Interval;

    /// <summary>
    /// ダメージ。**オービットだけ「毎秒」**、他は「1回あたり」。
    ///
    /// 単位が武器で違うのは気持ち悪いが、
    /// 当たり方が連続(オービット)と離散(ボルト・オーラ)で分かれている以上、
    /// **どちらかに揃えると片方が嘘になる**。
    /// 揃えるなら「全部を毎秒にする」ほうだが、
    /// そうすると「1発の威力」という分かりやすさが消える。
    /// </summary>
    public readonly float Damage;

    /// <summary>弾数(ボルト)/ 球の数(オービット)。オーラでは 1。</summary>
    public readonly int Count;

    /// <summary>周回半径(オービット)/ 効果範囲(オーラ)。ボルトでは射程。</summary>
    public readonly float Radius;

    /// <summary>弾速(ボルト)/ 角速度・ラジアン毎秒(オービット)。</summary>
    public readonly float Speed;

    public WeaponStats(float interval, float damage, int count, float radius, float speed)
    {
        Interval = interval;
        Damage = damage;
        Count = count;
        Radius = radius;
        Speed = speed;
    }
}

/// <summary>
/// 武器の定義表。**成長カーブがここに集まっている**。
///
/// Day 29 の <see cref="GameBalance"/> は「数字を1箇所に集める」ためのものだったが、
/// 武器が増えると<b>1箇所には収まらなくなる</b>。
/// 「レベル 4 のオービットは球が何個か」は数字であると同時に**式**で、
/// 定数の一覧に置くと読めなくなる。
///
/// だから今日から**カテゴリごとに1箇所**にする。
///   <see cref="GameBalance"/> … プレイヤー・敵・湧き・経験値の数字
///   <see cref="Weapons"/>     … 武器の成長カーブ
/// 「1箇所」という原則が嘘になったとき、**嘘のまま守るより、線を引き直す**ほうがよい。
///
/// 成長のさせ方には型がある。今日使っているのは3つ。
///
/// <code>
///   足し算 … ダメージ +3/Lv。**分かりやすいが、後半で効かなくなる**
///   掛け算 … 間隔 ×0.9/Lv。**複利で効く。上げすぎると壊れる**
///   段     … Lv3 と Lv5 で +1発。**節目に大きな変化が来る**
/// </code>
///
/// 3つを混ぜるのは、**レベルアップのたびに違う手応えを出す**ため。
/// 全部足し算だと「また少し強くなった」しか起きない。
/// </summary>
internal static class Weapons
{
    /// <summary>武器のレベル上限。ここまで上げたら選択肢に出さない。</summary>
    public const int MaxLevel = 6;

    public const int KindCount = 3;

    public static string NameOf(WeaponKind kind) => kind switch
    {
        WeaponKind.Bolt => "ボルト",
        WeaponKind.Orbit => "オービット",
        _ => "オーラ",
    };

    public static string SummaryOf(WeaponKind kind) => kind switch
    {
        WeaponKind.Bolt => "いちばん近い敵へ撃つ",
        WeaponKind.Orbit => "周りを回る球が触れた敵を削る",
        _ => "周囲の敵をまとめて削る",
    };

    /// <summary>
    /// レベルから性能を出す。**ここが成長カーブそのもの**。
    /// </summary>
    public static WeaponStats StatsFor(WeaponKind kind, int level)
    {
        int step = Math.Max(0, level - 1);

        return kind switch
        {
            // 間隔は掛け算(複利)、ダメージは足し算、弾数は段。
            // 3つの効き方が違うので、レベルごとに「何が変わったか」が伝わる。
            WeaponKind.Bolt => new WeaponStats(
                interval: GameBalance.FireInterval * MathF.Pow(0.90f, step),
                damage: GameBalance.ProjectileDamage + (3.0f * step),
                count: 1 + (level >= 3 ? 1 : 0) + (level >= 5 ? 1 : 0),
                radius: GameBalance.TargetRange,
                speed: GameBalance.ProjectileSpeed),

            // 球の数は 2 レベルごとに 1 個。**数が増えると当たる面積が増える**ので、
            // ダメージより体感が大きい。
            // ダメージは**毎秒**。触れている間ずっと削るので、1回あたりでは表せない。
            WeaponKind.Orbit => new WeaponStats(
                interval: 0.0f,
                damage: 55.0f + (22.0f * step),
                count: 1 + (step / 2),
                radius: 74.0f + (6.0f * step),
                speed: 2.4f + (0.12f * step)),

            // オーラは範囲が伸びる。**狙わなくてよい代わりに、届く範囲が命**。
            _ => new WeaponStats(
                interval: 0.5f,
                damage: 6.0f + (3.0f * step),
                count: 1,
                radius: 76.0f + (12.0f * step),
                speed: 0.0f),
        };
    }

    /// <summary>
    /// 次のレベルで何が変わるかを1行で。**選ぶ前に分かる**ようにする。
    ///
    /// 選択肢に「オービット Lv.3」とだけ出しても、遊ぶ側は判断できない。
    /// **何が増えるのかを見せる**のが選択画面の仕事になる。
    /// </summary>
    public static string DescribeNext(WeaponKind kind, int currentLevel)
    {
        if (currentLevel == 0)
        {
            return SummaryOf(kind);
        }

        WeaponStats now = StatsFor(kind, currentLevel);
        WeaponStats next = StatsFor(kind, currentLevel + 1);

        // **変わったところだけ**を並べる。全部の数字を出すと読まれない。
        var parts = new List<string>();

        if (next.Count > now.Count)
        {
            parts.Add($"数 {now.Count} → {next.Count}");
        }

        if (next.Damage > now.Damage)
        {
            string unit = kind == WeaponKind.Orbit ? "/秒" : string.Empty;
            parts.Add($"威力 {now.Damage:F0} → {next.Damage:F0}{unit}");
        }

        if (next.Interval < now.Interval - 0.001f)
        {
            parts.Add($"間隔 {now.Interval:F2} → {next.Interval:F2}秒");
        }

        if (next.Radius > now.Radius + 0.5f)
        {
            parts.Add($"範囲 {now.Radius:F0} → {next.Radius:F0}");
        }

        return string.Join("  ", parts);
    }

    /// <summary>
    /// オービットの球の位置。**ゲーム側と描画側の両方から呼ぶ**。
    ///
    /// 当たり判定と絵が別々の式で位置を出していると、
    /// **見えているところと当たるところがずれる**。
    /// しかもずれは小さいので、しばらく気づかない。
    /// 式を1つにしておけば、そもそも食い違いようがない。
    /// </summary>
    public static Vector2 OrbitPosition(Vector2 center, float angle, int index, in WeaponStats stats)
    {
        // 球は円周上に等間隔で並べる。
        float spread = MathF.Tau / stats.Count;
        float theta = angle + (spread * index);

        return center + (new Vector2(MathF.Cos(theta), MathF.Sin(theta)) * stats.Radius);
    }

    /// <summary>オービットの球の当たり判定の半径。</summary>
    public const float OrbitBallRadius = 11.0f;
}
