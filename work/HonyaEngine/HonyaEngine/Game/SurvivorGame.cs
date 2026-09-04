using System.Numerics;

namespace HonyaEngine;

/// <summary>ゲームがどの状態にあるか。</summary>
internal enum GamePhase
{
    Title,
    Playing,
    GameOver,
}

/// <summary>
/// 敵1体。**配列に詰めて回すので構造体**。
///
/// クラスにすると 1200 体ぶんの参照を辿ることになり、
/// メモリ上ばらばらの場所を読むことになる(Day 22 で実測した 17 倍がこれ)。
/// 構造体の配列なら、更新ループは連続したメモリを頭から舐めるだけで済む。
/// </summary>
internal struct Enemy
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Health;
    public float Radius;
    public float Speed;
    public float Damage;
    public int Kind;
    public int Experience;

    /// <summary>この敵が最後にダメージを受けた時刻。**光らせる**のに使う。</summary>
    public float HitAt;
}

internal struct Projectile
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Life;
    public float Damage;
}

internal struct Gem
{
    public Vector2 Position;
    public Vector2 Velocity;
    public int Value;
}

/// <summary>
/// 卒業制作。**見下ろし型の時間耐久アクション**。
///
/// Day 25〜28 で作ったものが、ここで初めて<b>ゲームの必然として</b>要る。
///
/// <code>
///   Day 25 当たり判定  … 弾と敵、敵とプレイヤー
///   Day 26 空間分割    … 数百体の敵を毎ステップ捌く。**これが無いと成立しない**
///   Day 27 音          … 敵が死ぬたびに鳴らすと、間引きが無いと即破綻する
///   Day 28 文字        … 残り時間・HP・レベル。数字が出せないとゲームにならない
/// </code>
///
/// <b>エンジンとゲームの境目</b>がこのクラスの外側にある。
/// このクラスは <c>Silk.NET</c> も <c>GL</c> も知らない——
/// 知っているのは「入力を受け取って状態を進める」ことだけで、
/// 描画は <see cref="GameView"/>、窓とループは <c>Program</c> の仕事になっている。
/// おかげで**このクラスだけを取り出してテストできる**
/// (実際、自己チェックは窓を出さずに 60 秒ぶん回している)。
///
/// <b>データの持ち方</b>は Day 23 の ECS ではなく、素の構造体配列にしてある。
/// ECS が効くのは「部品の組み合わせが実行時に変わる」ときで、
/// 今日のように<b>敵は敵、弾は弾と決まっている</b>なら、
/// 種類ごとに配列を1本持つほうが素直で速い。
/// Day 23 で「ECS は構造体の配列の一般化」と書いたが、
/// **一般化が要らない場面では特殊形のままでよい**。
/// </summary>
internal sealed class SurvivorGame
{
    private readonly Random _random = new(29);
    private readonly SpatialGrid _grid = new();

    /// <summary>敵の外接 AABB。**格子に渡すのはこれだけ**(Day 26 と同じ形)。</summary>
    private readonly Aabb2D[] _enemyBounds = new Aabb2D[GameBalance.MaxEnemies];

    /// <summary>問い合わせの答えを受け取る場所。**使い回して割り当てを避ける**。</summary>
    private readonly int[] _queryBuffer = new int[256];

    private float _spawnTimer;
    private float _fireTimer;

    public SurvivorGame()
    {
        Enemies = new Enemy[GameBalance.MaxEnemies];
        Projectiles = new Projectile[GameBalance.MaxProjectiles];
        Gems = new Gem[GameBalance.MaxGems];
    }

    // --- 状態。描画側(GameView)から読むので public ---

    public GamePhase Phase { get; private set; } = GamePhase.Title;

    /// <summary>生き延びた時間(秒)。**これがスコア**。</summary>
    public float Elapsed { get; private set; }

    public Vector2 PlayerPosition { get; private set; }

    /// <summary>最後に向いていた方向。止まっても向きを保つ。</summary>
    public Vector2 PlayerFacing { get; private set; } = Vector2.UnitX;

    public float Health { get; private set; }

    public float InvulnerableFor { get; private set; }

    public int Level { get; private set; } = 1;

    public int Experience { get; private set; }

    public int ExperienceToNext => GameBalance.ExperienceForLevel(Level);

    public int Kills { get; private set; }

    /// <summary>カメラの中心。プレイヤーを追いかける。</summary>
    public Vector2 Camera { get; private set; }

    public Enemy[] Enemies { get; }

    public int EnemyCount { get; private set; }

    public Projectile[] Projectiles { get; }

    public int ProjectileCount { get; private set; }

    public Gem[] Gems { get; }

    public int GemCount { get; private set; }

    /// <summary>直前のステップで格子が出した候補の数。**効き目を画面に出す**ため。</summary>
    public long PairCandidates { get; private set; }

    public int GridColumns => _grid.Columns;

    public int GridRows => _grid.Rows;

    public int GridMaxPerCell => _grid.MaxPerCell;

    /// <summary>
    /// 音を鳴らしてほしいときに呼ぶ相手。
    ///
    /// **このクラスは <see cref="AudioSystem"/> を知らない**。
    /// 「何が起きたか」だけを外へ投げて、それを音にするかどうかは外の判断にする。
    /// こうしておくと、自己チェックで 60 秒ぶん回すときに音を切れるし、
    /// あとから画面を光らせる・振動させるといった反応を足すのも同じ場所になる。
    /// </summary>
    public Action<GameEvent, Vector2>? OnEvent { get; set; }

    /// <summary>ゲームの中で起きたこと。音や演出のきっかけになる。</summary>
    public enum GameEvent
    {
        Fire,
        EnemyKilled,
        PlayerHit,
        GemCollected,
        LevelUp,
        GameOver,
    }

    public void Start(Vector2 viewSize)
    {
        Phase = GamePhase.Playing;
        Elapsed = 0.0f;
        PlayerPosition = Vector2.Zero;
        PlayerFacing = Vector2.UnitX;
        Camera = Vector2.Zero;
        Health = GameBalance.PlayerMaxHealth;
        InvulnerableFor = 0.0f;
        Level = 1;
        Experience = 0;
        Kills = 0;

        EnemyCount = 0;
        ProjectileCount = 0;
        GemCount = 0;

        _spawnTimer = 0.0f;
        _fireTimer = 0.0f;

        ViewSize = viewSize;
    }

    public Vector2 ViewSize { get; set; } = new(960.0f, 640.0f);

    /// <summary>
    /// 1ステップ進める。**固定間隔で呼ばれる**(Day 19)。
    ///
    /// 順番に意味がある。入れ替えると壊れるものを挙げると:
    ///   - 格子は<b>敵を動かしたあと</b>に組む。動かす前に組むと 1 ステップ古い位置で判定する
    ///   - 弾の当たり判定は<b>格子を組んだあと</b>。同じ格子を使い回す
    ///   - レベルアップの判定は<b>ジェムを拾ったあと</b>
    /// </summary>
    public void Update(float dt, in InputSnapshot input)
    {
        if (Phase != GamePhase.Playing)
        {
            return;
        }

        Elapsed += dt;
        InvulnerableFor = MathF.Max(0.0f, InvulnerableFor - dt);

        UpdatePlayer(dt, input);
        Spawn(dt);
        UpdateEnemies(dt);
        BuildGrid();
        SeparateEnemies();
        UpdateWeapon(dt);
        UpdateProjectiles(dt);
        DamagePlayer(dt);
        UpdateGems(dt);
        CheckLevelUp();

        if (Health <= 0.0f)
        {
            Phase = GamePhase.GameOver;
            OnEvent?.Invoke(GameEvent.GameOver, PlayerPosition);
        }
    }

    private void UpdatePlayer(float dt, in InputSnapshot input)
    {
        // **MoveAxis は正規化済み**(Day 20)。斜めが速くなる古典的なバグは、
        // 入力を畳む側で既に潰してある。
        Vector2 axis = input.MoveAxis;

        if (axis != Vector2.Zero)
        {
            PlayerFacing = axis;
        }

        PlayerPosition += axis * (GameBalance.PlayerSpeed * dt);

        // **カメラは遅れて付いてくる**。ぴったり追従すると、
        // 背景が無い画面では自分が動いている感じがしない。
        // 指数的な追従(毎ステップ差の一定割合を詰める)は1行で書けて、
        // フレームレートに依存しない形にもできる。
        float follow = 1.0f - MathF.Exp(-GameBalance.CameraFollow * dt);
        Camera += (PlayerPosition - Camera) * follow;
    }

    /// <summary>
    /// 敵を湧かせる。**画面の外の円周上**に置く。
    ///
    /// 時間とともに「間隔が縮む」と「1回に出る数が増える」の両方が効くので、
    /// 湧く量は時間の2乗に近い勢いで増える。
    /// **難しくなり方が体感で分かる**ようにするには、これくらい露骨でよい。
    /// </summary>
    private void Spawn(float dt)
    {
        _spawnTimer -= dt;
        if (_spawnTimer > 0.0f)
        {
            return;
        }

        float progress = MathF.Min(Elapsed / GameBalance.SpawnRampSeconds, 1.0f);

        _spawnTimer = float.Lerp(GameBalance.SpawnIntervalStart, GameBalance.SpawnIntervalMin, progress);

        int burst = (int)float.Lerp(GameBalance.SpawnBurstStart, GameBalance.SpawnBurstMax, progress);

        // 画面の角までの距離 + 余白。**どの向きから湧いても画面の外**になる半径。
        float radius = (ViewSize.Length() * 0.5f) + GameBalance.SpawnMargin;

        for (int i = 0; i < burst && EnemyCount < GameBalance.MaxEnemies; i++)
        {
            float angle = (float)_random.NextDouble() * MathF.Tau;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

            // 種類は時間で解禁する。**最初から全種類出すと、違いが分からない**。
            int kinds = Elapsed < 25.0f ? 1 : Elapsed < 60.0f ? 2 : GameBalance.EnemyKindCount;
            int kind = _random.Next(kinds);

            (float kindRadius, float speed, float health, float damage, int experience) =
                GameBalance.EnemyKinds[kind];

            // 時間で体力だけ上げる(GameBalance のコメント参照)。
            float scale = 1.0f + (Elapsed / 60.0f * GameBalance.EnemyHealthPerMinute);

            Enemies[EnemyCount++] = new Enemy
            {
                Position = Camera + offset,
                Velocity = Vector2.Zero,
                Health = health * scale,
                Radius = kindRadius,
                Speed = speed,
                Damage = damage,
                Kind = kind,
                Experience = experience,
                HitAt = -1.0f,
            };
        }
    }

    /// <summary>
    /// 敵を動かす。**プレイヤーへまっすぐ向かうだけ**。
    ///
    /// これで十分に「押し寄せてくる」感じが出る。
    /// 経路探索も、隊列も、いっさい要らない——
    /// <b>数が多いこと自体が挙動になっている</b>のがこの題材の面白いところで、
    /// 1体ずつの賢さに使う予算を、体数と押し合い(<see cref="SeparateEnemies"/>)に回している。
    /// </summary>
    private void UpdateEnemies(float dt)
    {
        float despawnSquared = GameBalance.EnemyDespawnDistance * GameBalance.EnemyDespawnDistance;

        for (int i = 0; i < EnemyCount; i++)
        {
            ref Enemy enemy = ref Enemies[i];

            Vector2 toPlayer = PlayerPosition - enemy.Position;
            float distanceSquared = toPlayer.LengthSquared();

            // **遠すぎる敵は消す**。逃げ続けたときに後ろが延々ついてくるのを防ぐ。
            if (distanceSquared > despawnSquared)
            {
                RemoveEnemy(i);
                i--;
                continue;
            }

            if (distanceSquared > 0.0001f)
            {
                enemy.Velocity = toPlayer / MathF.Sqrt(distanceSquared) * enemy.Speed;
            }

            enemy.Position += enemy.Velocity * dt;
        }
    }

    /// <summary>
    /// 格子を組む。**1ステップに1回だけ**。
    ///
    /// このあと4通りに使い回す。
    ///   1. 敵どうしの押し合い(<see cref="SeparateEnemies"/>)
    ///   2. 狙う敵を探す(<see cref="UpdateWeapon"/>)
    ///   3. 弾が当たった敵を探す(<see cref="UpdateProjectiles"/>)
    ///   4. プレイヤーに触れている敵を探す(<see cref="DamagePlayer"/>)
    ///
    /// **格子の原点はカメラに合わせて動く**。世界は無限に広いので、
    /// 固定の格子は張れない。カメラの周りだけを覆えば、
    /// 遠くの敵は端のマスに丸められる——が、遠くの敵は数が少ないので実害が無い。
    /// </summary>
    private void BuildGrid()
    {
        if (EnemyCount == 0)
        {
            PairCandidates = 0;
            return;
        }

        for (int i = 0; i < EnemyCount; i++)
        {
            ref Enemy enemy = ref Enemies[i];
            _enemyBounds[i] = Aabb2D.FromCenter(enemy.Position, new Vector2(enemy.Radius));
        }

        // 湧く位置まで覆う広さを取る。余白は片側 1.5 倍。
        Vector2 extent = (ViewSize * 0.75f) + new Vector2(GameBalance.SpawnMargin * 2.0f);

        // マスの大きさは**いちばん大きい敵の直径くらい**。
        // Day 26 の掃引で「平均の直径の 1〜2 倍」が谷底だったのに合わせている。
        _grid.Configure(Camera - extent, extent * 2.0f, cellSize: 40.0f);
        _grid.Build(_enemyBounds.AsSpan(0, EnemyCount));
    }

    /// <summary>
    /// 敵どうしを押し離す。**これが「群れ」に見せている**。
    ///
    /// 押し合いが無いと、全員がプレイヤーへ最短距離を進むので
    /// **1本の線の上に完全に重なる**。何百体いても1体に見える。
    /// 押し離すだけで、勝手に前線ができて回り込みが起きる。
    ///
    /// ここが Day 26 の直接の出番。500 体なら総当たりで 124,750 組、
    /// 格子なら数千組で済む。<b>格子が無ければこの演出は載らない</b>。
    /// </summary>
    private void SeparateEnemies()
    {
        if (EnemyCount == 0)
        {
            return;
        }

        _grid.CollectPairs(_enemyBounds.AsSpan(0, EnemyCount));
        PairCandidates = _grid.PairCount;

        ReadOnlySpan<BroadPair> pairs = _grid.Pairs;

        for (int p = 0; p < pairs.Length; p++)
        {
            ref Enemy a = ref Enemies[pairs[p].A];
            ref Enemy b = ref Enemies[pairs[p].B];

            var circleA = new Circle2D(a.Position, a.Radius);
            var circleB = new Circle2D(b.Position, b.Radius);

            Contact2D contact = Collision2D.Test(circleA, circleB);
            if (!contact.Hit)
            {
                continue;
            }

            // **完全には離さない**。1ステップで重なりを消すと、
            // 密集したときに弾かれるように吹き飛ぶ。
            // 少しずつ押すほうが、押し合ってじわじわ広がる動きになる。
            Vector2 push = contact.Normal * (contact.Depth * 0.5f * GameBalance.EnemySeparation);
            a.Position -= push;
            b.Position += push;
        }
    }

    /// <summary>
    /// 自動攻撃。**いちばん近い敵へ撃つ**。
    ///
    /// この題材の要は「プレイヤーは移動しかしない」こと。
    /// 攻撃を自動にすると、遊ぶ側の判断は<b>どこへ動くか</b>だけになり、
    /// 敵の配置がそのまま問題になる。
    ///
    /// 狙う相手を探すのに、また格子を使う——
    /// 全部の敵との距離を測ると 1000 体で 1000 回になるが、
    /// <see cref="SpatialGrid.Query"/> なら射程の中だけを見ればよい。
    /// </summary>
    private void UpdateWeapon(float dt)
    {
        _fireTimer -= dt;
        if (_fireTimer > 0.0f || ProjectileCount >= GameBalance.MaxProjectiles)
        {
            return;
        }

        if (!TryFindNearestEnemy(PlayerPosition, GameBalance.TargetRange, out int target))
        {
            // 敵がいなければ撃たない。**空撃ちさせない**ことで、
            // 「敵が来た瞬間に撃つ」ようになる。
            return;
        }

        _fireTimer = GameBalance.FireInterval;

        Vector2 direction = Vector2.Normalize(Enemies[target].Position - PlayerPosition);

        Projectiles[ProjectileCount++] = new Projectile
        {
            Position = PlayerPosition,
            Velocity = direction * GameBalance.ProjectileSpeed,
            Life = GameBalance.ProjectileLife,
            Damage = GameBalance.ProjectileDamage,
        };

        OnEvent?.Invoke(GameEvent.Fire, PlayerPosition);
    }

    /// <summary>
    /// 射程の中でいちばん近い敵を探す。**格子で候補を絞ってから距離を測る**。
    /// </summary>
    private bool TryFindNearestEnemy(Vector2 from, float range, out int index)
    {
        index = -1;

        if (EnemyCount == 0)
        {
            return false;
        }

        var box = Aabb2D.FromCenter(from, new Vector2(range));
        int found = _grid.Query(box, _queryBuffer);

        float best = range * range;

        for (int i = 0; i < found; i++)
        {
            int candidate = _queryBuffer[i];
            float distanceSquared = (Enemies[candidate].Position - from).LengthSquared();

            if (distanceSquared < best)
            {
                best = distanceSquared;
                index = candidate;
            }
        }

        return index >= 0;
    }

    /// <summary>
    /// 弾を進めて、当たった敵を削る。
    ///
    /// 弾は**当たったら消える**(貫通は Day 30 の武器強化で入る)。
    /// 1発ごとに <see cref="SpatialGrid.Query"/> を呼ぶので、
    /// 弾 200 発 × 候補 数個 で済む。総当たりなら 200 × 500 = 10 万回になる。
    /// </summary>
    private void UpdateProjectiles(float dt)
    {
        for (int i = 0; i < ProjectileCount; i++)
        {
            ref Projectile projectile = ref Projectiles[i];

            projectile.Position += projectile.Velocity * dt;
            projectile.Life -= dt;

            if (projectile.Life <= 0.0f)
            {
                RemoveProjectile(i);
                i--;
                continue;
            }

            if (EnemyCount == 0)
            {
                continue;
            }

            var box = Aabb2D.FromCenter(projectile.Position, new Vector2(GameBalance.ProjectileRadius));
            int found = _grid.Query(box, _queryBuffer);

            var bullet = new Circle2D(projectile.Position, GameBalance.ProjectileRadius);
            bool consumed = false;

            for (int c = 0; c < found; c++)
            {
                int e = _queryBuffer[c];

                // **候補は「近い」でしかない**(Query のコメント)。ここで本判定する。
                if (!Collision2D.Overlap(bullet, new Circle2D(Enemies[e].Position, Enemies[e].Radius)))
                {
                    continue;
                }

                DamageEnemy(e, projectile.Damage);
                consumed = true;
                break;
            }

            if (consumed)
            {
                RemoveProjectile(i);
                i--;
            }
        }
    }

    private void DamageEnemy(int index, float amount)
    {
        ref Enemy enemy = ref Enemies[index];
        enemy.Health -= amount;
        enemy.HitAt = Elapsed;

        if (enemy.Health > 0.0f)
        {
            return;
        }

        Vector2 where = enemy.Position;
        int experience = enemy.Experience;

        Kills++;
        RemoveEnemy(index);

        if (GemCount < GameBalance.MaxGems)
        {
            Gems[GemCount++] = new Gem
            {
                Position = where,
                Velocity = Vector2.Zero,
                Value = experience,
            };
        }

        // **ここが Day 27 の間引きの出番**。終盤は1ステップに数十体死ぬので、
        // 素直に鳴らすと数十回の再生要求が飛ぶ。
        // AudioSystem 側で「同じ音は1ステップに4回まで」に絞られる。
        OnEvent?.Invoke(GameEvent.EnemyKilled, where);
    }

    /// <summary>
    /// プレイヤーに触れている敵からダメージを受ける。
    ///
    /// **無敵時間が要る**(<see cref="GameBalance.PlayerInvulnerableTime"/>)。
    /// 囲まれると毎ステップ複数体と接触するので、
    /// そのまま処理すると 1 秒で数百のダメージになる。
    /// </summary>
    private void DamagePlayer(float dt)
    {
        if (InvulnerableFor > 0.0f || EnemyCount == 0)
        {
            return;
        }

        var box = Aabb2D.FromCenter(PlayerPosition, new Vector2(GameBalance.PlayerRadius + 24.0f));
        int found = _grid.Query(box, _queryBuffer);

        var player = new Circle2D(PlayerPosition, GameBalance.PlayerRadius);

        for (int i = 0; i < found; i++)
        {
            int e = _queryBuffer[i];

            if (!Collision2D.Overlap(player, new Circle2D(Enemies[e].Position, Enemies[e].Radius)))
            {
                continue;
            }

            Health -= Enemies[e].Damage;
            InvulnerableFor = GameBalance.PlayerInvulnerableTime;
            OnEvent?.Invoke(GameEvent.PlayerHit, PlayerPosition);

            // **1回受けたら抜ける**。同じステップで何体にも触っているが、
            // ダメージは1回ぶんにする。
            break;
        }
    }

    /// <summary>
    /// 経験値のジェムを吸い寄せて拾う。
    ///
    /// **吸い寄せ**があるかどうかで手触りがまるで変わる。
    /// 落ちた場所まで取りに行かせると、敵の中へ突っ込むことになって
    /// 「倒したのに損をする」感じになる。
    /// 近づいたら勝手に来るようにすると、倒すこと自体が報酬になる。
    /// </summary>
    private void UpdateGems(float dt)
    {
        float magnetSquared = GameBalance.GemMagnetRange * GameBalance.GemMagnetRange;
        float pickupSquared = GameBalance.GemPickupRange * GameBalance.GemPickupRange;

        for (int i = 0; i < GemCount; i++)
        {
            ref Gem gem = ref Gems[i];

            Vector2 toPlayer = PlayerPosition - gem.Position;
            float distanceSquared = toPlayer.LengthSquared();

            if (distanceSquared <= pickupSquared)
            {
                Experience += gem.Value;
                RemoveGem(i);
                i--;
                OnEvent?.Invoke(GameEvent.GemCollected, PlayerPosition);
                continue;
            }

            if (distanceSquared > magnetSquared)
            {
                continue;
            }

            // 近いほど速く寄る。**等速だと最後の1歩が遅く感じる**。
            float distance = MathF.Sqrt(distanceSquared);
            float pull = 1.0f - (distance / GameBalance.GemMagnetRange);
            gem.Position += toPlayer / distance * (GameBalance.GemMagnetSpeed * (0.35f + pull) * dt);
        }
    }

    /// <summary>
    /// レベルアップ。**今日は数字が上がるだけ**。
    ///
    /// Day 30 でここが「武器を選ぶ」に変わる。
    /// 先に経験値とレベルの器を作っておくと、
    /// 明日は「上がった瞬間に何をするか」だけを足せばよくなる。
    /// </summary>
    private void CheckLevelUp()
    {
        while (Experience >= ExperienceToNext)
        {
            Experience -= ExperienceToNext;
            Level++;

            // ささやかな報酬。Day 30 まではこれだけ。
            Health = MathF.Min(GameBalance.PlayerMaxHealth, Health + 8.0f);

            OnEvent?.Invoke(GameEvent.LevelUp, PlayerPosition);
        }
    }

    // --- 配列から消す。**末尾と入れ替えて縮める** ---
    //
    // 前へ詰めると O(n) かかるうえ、走査中の添字が全部ずれる。
    // 末尾と入れ替えれば O(1) で済む。順番は変わるが、
    // **敵に順番の意味は無い**ので困らない。
    // Day 23 の ComponentStore がまったく同じことをしている(あちらの要点4)。
    //
    // 呼んだあとは **i-- して同じ添字をもう一度見る**のを忘れないこと。
    // 入れ替えで新しく来たものを飛ばしてしまう。

    private void RemoveEnemy(int index) => Enemies[index] = Enemies[--EnemyCount];

    private void RemoveProjectile(int index) => Projectiles[index] = Projectiles[--ProjectileCount];

    private void RemoveGem(int index) => Gems[index] = Gems[--GemCount];

    /// <summary>タイトルとゲームオーバーから始めるとき用。</summary>
    public void ReturnToTitle() => Phase = GamePhase.Title;
}
