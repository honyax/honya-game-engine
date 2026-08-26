namespace HonyaEngine;

/// <summary>
/// 卒業制作の調整値。**数字を1箇所に集める**ためだけのクラス。
///
/// ゲームの手触りは、ほぼこの数字で決まる。
/// 「敵が速すぎる」「弾が弱い」「レベルが上がらない」——
/// 遊んで気になったことは全部ここを触ることになるので、
/// **コードの中に散らばっていると調整が苦行になる**。
///
/// 実際のゲームではこれが JSON なり専用のエディタなりになって、
/// **ビルドし直さずに触れる**ようになる(Day 24 のシリアライズがその入口)。
/// 今日は定数のままにしてあるが、置き場所を1つにしておけば
/// 外へ出すのはあとからでもできる。逆に散らばってからでは手遅れになる。
///
/// 数字の隣に**根拠**を書いておくのも大事なところ。
/// 「なぜ 180 なのか」が分からない数字は、半年後の自分が触れなくなる。
/// </summary>
internal static class GameBalance
{
    // --- プレイヤー ---

    /// <summary>移動速度(ピクセル/秒)。画面の横幅 960px を 5 秒強で横断する。</summary>
    public const float PlayerSpeed = 180.0f;

    public const float PlayerRadius = 14.0f;

    public const float PlayerMaxHealth = 100.0f;

    /// <summary>
    /// 被弾したあと無敵でいる時間(秒)。
    ///
    /// **これが無いとゲームにならない**。敵が重なって押し寄せる題材なので、
    /// 毎ステップ判定すると 1 秒で 60 回ダメージを受ける。
    /// 0.6 秒あれば、囲まれても「じわじわ減る」に収まる。
    /// </summary>
    public const float PlayerInvulnerableTime = 0.75f;

    // --- 敵 ---

    /// <summary>同時に出せる敵の上限。**配列の大きさ**でもある。</summary>
    public const int MaxEnemies = 1200;

    /// <summary>
    /// 開始時の湧き間隔(秒)。
    ///
    /// **倒す速さと釣り合わせる**のがここの肝。
    /// 発射間隔 0.30 秒で雑魚が1発なら、倒せるのは 1 秒に 3.3 体。
    /// 開始時に 1 秒 1.1 体なら、序盤は押し返せて、
    /// 湧きが詰まっていくにつれて押し負けるようになる。
    /// 最初にここを 0.5 秒(1秒に2体)にしたら、
    /// **開始 30 秒で死ぬ**ゲームになった。
    /// </summary>
    public const float SpawnIntervalStart = 0.9f;

    /// <summary>最短の湧き間隔。</summary>
    public const float SpawnIntervalMin = 0.10f;

    /// <summary>
    /// 湧き間隔がこの秒数で最短まで縮む。**難易度曲線そのもの**。
    ///
    /// 最短まで縮むと 1 秒に 20 体湧く。倒せるのは 1 秒に 3.3 体なので、
    /// **ここから先は溜まる一方**になる。
    /// この「押し返せる時間」と「押し負ける時間」の境目が、
    /// 遊んでいて盛り上がるところ。
    /// </summary>
    public const float SpawnRampSeconds = 120.0f;

    /// <summary>1回の湧きで出る数。時間とともに増える(<see cref="SpawnBurstMax"/> まで)。</summary>
    public const int SpawnBurstStart = 1;

    public const int SpawnBurstMax = 2;

    /// <summary>
    /// 画面の外側どれだけ離れた位置に湧かせるか(ピクセル)。
    ///
    /// **見えているところに湧いてはいけない**。
    /// 目の前に敵が現れるのは理不尽に感じられるし、
    /// 「湧いた」ことがはっきり見えると世界が嘘っぽくなる。
    /// </summary>
    public const float SpawnMargin = 80.0f;

    /// <summary>敵どうしが押し合う強さ。1.0 で「重なりを1ステップで完全に解消」。</summary>
    public const float EnemySeparation = 0.35f;

    /// <summary>
    /// プレイヤーからこれ以上離れた敵は消す(ピクセル)。
    ///
    /// 逃げ続けると後ろの敵が延々ついてくるので、上限が要る。
    /// **消すのは「見えていないところ」だけ**にしないと、
    /// 目の前で敵が消える。
    /// </summary>
    public const float EnemyDespawnDistance = 1400.0f;

    // --- 敵の種類 ---
    //
    // 3種類だけ。**役割が違うものを混ぜる**のが大事で、
    // 同じ敵を強くしていくだけでは「数が増えた」以上の感想が出てこない。

    /// <summary>雑魚。遅くて弱い。数で押す</summary>
    public const int KindGrunt = 0;

    /// <summary>速い敵。ばらけて追ってくるので、固まって避けられない</summary>
    public const int KindRunner = 1;

    /// <summary>硬い敵。壁になる。押し合いで前が詰まる</summary>
    public const int KindBrute = 2;

    public const int EnemyKindCount = 3;

    /// <summary>種類ごとの (半径, 速度, 体力, 接触ダメージ, 経験値)。</summary>
    public static readonly (float Radius, float Speed, float Health, float Damage, int Experience)[] EnemyKinds =
    [
        (10.0f, 52.0f, 10.0f, 6.0f, 1),
        (8.0f, 96.0f, 6.0f, 5.0f, 2),
        (17.0f, 34.0f, 34.0f, 12.0f, 5),
    ];

    /// <summary>
    /// 時間とともに敵の体力を上げる倍率(1分あたり)。
    ///
    /// **速度は上げない**。速度を上げると避けられなくなって理不尽になるが、
    /// 体力なら「倒すのに時間がかかる」だけなので、
    /// プレイヤーの成長(Day 30 の武器)で対抗できる。
    /// </summary>
    public const float EnemyHealthPerMinute = 0.30f;

    // --- 弾 ---

    public const int MaxProjectiles = 400;

    /// <summary>
    /// 発射間隔(秒)。Day 30 でレベルアップにより縮む。
    ///
    /// <see cref="ProjectileDamage"/> と組で「1秒に何体倒せるか」を決める。
    /// **この2つと <see cref="SpawnIntervalStart"/> の釣り合いがゲームの寿命**になる。
    /// </summary>
    public const float FireInterval = 0.30f;

    public const float ProjectileSpeed = 420.0f;

    public const float ProjectileRadius = 6.0f;

    /// <summary>
    /// 弾のダメージ。**雑魚(体力 10)を1発で倒せる**値にしてある。
    ///
    /// 1発で倒せるかどうかは、体感がまるで違う。
    /// 2発必要だと「撃っているのに減らない」と感じるので、
    /// **いちばん数の多い敵は1発**にして、硬い敵で差を付ける。
    /// </summary>
    public const float ProjectileDamage = 12.0f;

    /// <summary>弾の寿命(秒)。**距離ではなく時間で切る**ほうが、速度を変えたときに壊れない。</summary>
    public const float ProjectileLife = 1.1f;

    /// <summary>この距離までの敵を狙う(ピクセル)。画面の外は狙わない。</summary>
    public const float TargetRange = 460.0f;

    // --- 経験値 ---

    public const int MaxGems = 600;

    /// <summary>
    /// ジェムを吸い寄せ始める距離。
    ///
    /// **狭すぎると経験値が溜まらない**。96px で試したら 150 秒で Lv.5 までしか行かず、
    /// 倒したのに拾えていないジェムが 70 個も残った。
    /// 逃げながら戦う遊びなので、倒した場所へ戻る余裕は無い。
    /// </summary>
    public const float GemMagnetRange = 130.0f;

    public const float GemMagnetSpeed = 340.0f;

    /// <summary>拾ったとみなす距離。</summary>
    public const float GemPickupRange = 18.0f;

    /// <summary>
    /// レベル N に上がるのに必要な累計経験値。
    ///
    /// **最初は速く、あとはゆっくり**にするのが定石。
    /// 序盤で何も起きないゲームは、遊ぶ側が上達を実感できない。
    /// </summary>
    public static int ExperienceForLevel(int level) => 4 + (level * level * 3);

    // --- 見た目 ---

    /// <summary>カメラがプレイヤーに追いつく速さ(1秒あたりの追従率)。</summary>
    public const float CameraFollow = 8.0f;
}
