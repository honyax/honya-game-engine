using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **システム = 状態を持たない、配列を舐めるだけの手続き**。
///
/// Day 22 の <see cref="Component"/> は「1個ぶんのふるまい」で、
/// エンジンがオブジェクトを回りながら1個ずつ呼んでいた。
/// ECS のシステムは逆で、**全員ぶんをまとめて処理する1つのループ**になる。
///
/// <code>
///   GameObject : for each object { for each component { component.FixedUpdate() } }
///   ECS        : MoveSystem(world, dt);  ← この中に for が1つあるだけ
/// </code>
///
/// 静的メソッドにしてあるのは、状態を持たせないため。
/// システムが状態を持ち始めると、それは結局オブジェクトになる。
/// 状態はコンポーネント(= World の中)にしか置かない、が原則。
///
/// そして**呼ぶ順番を呼び出し側が書く**。Day 22 では「リストの順」でしかなく
/// 誰も決めていなかったものが、ここでは書かないと動かない。
/// 「入力 → 移動 → 当たり判定 → 描画」を明示するのが ECS の作法で、
/// Day 22 の要点7で挙げた弱点が、設計の前提に変わる。
/// </summary>
internal static class EcsSystems
{
    /// <summary>
    /// 現在の <see cref="Transform2D"/> を <see cref="Previous2D"/> に控える。補間の下準備。
    ///
    /// **2つの配列を突き合わせる(結合する)**必要がある。
    /// 素直にやるとエンティティ番号を経由して1個ずつ引くことになるが、
    /// 両者が同じ順に並んでいるなら添字をそのまま使える。
    /// その判定は <see cref="World"/> の外で1回やって、
    /// <paramref name="aligned"/> で渡す(<see cref="AreAligned{TA, TB}"/>)。
    /// </summary>
    public static void Snapshot(World world, bool aligned)
    {
        ComponentStore<Transform2D> transforms = world.Store<Transform2D>();
        ComponentStore<Previous2D> previous = world.Store<Previous2D>();

        Span<Transform2D> current = transforms.Values;

        if (aligned)
        {
            // **並びが同じなら、ただのコピー**。番号を経由しない。
            Span<Previous2D> target = previous.Values;
            for (int i = 0; i < current.Length; i++)
            {
                target[i].Position = current[i].Position;
                target[i].Rotation = current[i].Rotation;
            }

            return;
        }

        ReadOnlySpan<int> entities = transforms.Entities;
        for (int i = 0; i < current.Length; i++)
        {
            int dense = previous.DenseIndexOf(entities[i]);
            if (dense < 0)
            {
                continue;
            }

            ref Previous2D target = ref previous.AtDense(dense);
            target.Position = current[i].Position;
            target.Rotation = current[i].Rotation;
        }
    }

    /// <summary>
    /// 動かして、画面の端で跳ね返らせる。
    /// Day 17 からの <c>UpdateSprites</c>、Day 22 の <c>BouncingMover</c> と同じ計算。
    ///
    /// 触るのは <see cref="Transform2D"/>(12バイト)と
    /// <see cref="Velocity2D"/>(16バイト)だけ。
    /// **色も種類もレイヤーも読まない**ので、そのぶんキャッシュが空く。
    /// 構造体の配列版は1枚 64 バイトを丸ごと引きずっていたので、
    /// 「ECS のほうが速い」ことすらありうる(計画書の要点3)。
    /// </summary>
    public static void Move(World world, float deltaTime, Vector2 bounds, bool aligned)
    {
        ComponentStore<Transform2D> transforms = world.Store<Transform2D>();
        ComponentStore<Velocity2D> velocities = world.Store<Velocity2D>();

        Span<Transform2D> t = transforms.Values;
        Span<Velocity2D> v = velocities.Values;

        if (aligned)
        {
            for (int i = 0; i < t.Length; i++)
            {
                Step(ref t[i], ref v[i], deltaTime, bounds);
            }

            return;
        }

        // 一般の場合。**片方を舐めて、もう片方は番号で引く**。
        // この1段が sparse set の結合コストで、
        // アーキタイプ方式の ECS は「同じ構成のエンティティを同じ配列に固める」ことで
        // 上の <c>aligned</c> の状態を**構造的に保証**している(要点4)。
        ReadOnlySpan<int> entities = transforms.Entities;
        for (int i = 0; i < t.Length; i++)
        {
            int dense = velocities.DenseIndexOf(entities[i]);
            if (dense < 0)
            {
                continue;
            }

            Step(ref t[i], ref velocities.AtDense(dense), deltaTime, bounds);
        }
    }

    /// <summary>1体ぶんの移動。<c>MethodImpl</c> を付けなくても JIT が展開する程度に小さい。</summary>
    private static void Step(ref Transform2D transform, ref Velocity2D velocity, float deltaTime, Vector2 bounds)
    {
        Vector2 position = transform.Position + (velocity.Linear * deltaTime);
        float half = velocity.HalfSize;

        if (position.X < half)
        {
            position.X = half;
            velocity.Linear.X = -velocity.Linear.X;
        }
        else if (position.X > bounds.X - half)
        {
            position.X = bounds.X - half;
            velocity.Linear.X = -velocity.Linear.X;
        }

        if (position.Y < half)
        {
            position.Y = half;
            velocity.Linear.Y = -velocity.Linear.Y;
        }
        else if (position.Y > bounds.Y - half)
        {
            position.Y = bounds.Y - half;
            velocity.Linear.Y = -velocity.Linear.Y;
        }

        transform.Position = position;
        transform.Rotation += velocity.Spin * deltaTime;
    }

    /// <summary>
    /// 2つのストアが**同じエンティティを同じ順で**持っているか。
    ///
    /// 判定そのものが O(n) なので、毎フレーム呼ぶ意味は無い。
    /// エンティティを作った直後・消した直後に1回だけ確かめて、
    /// 結果を持ち回るのが使い方。
    ///
    /// 全員を同じ手順で作り、途中で1体だけ消したりしなければ、並びは一致する。
    /// **そこが崩れた瞬間に静かに間違うのが怖いところ**で、
    /// だからアーキタイプ方式はこれを人間の注意力ではなく構造で守る。
    /// </summary>
    public static bool AreAligned<TA, TB>(ComponentStore<TA> a, ComponentStore<TB> b)
        where TA : struct
        where TB : struct
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        ReadOnlySpan<int> left = a.Entities;
        ReadOnlySpan<int> right = b.Entities;

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }
}
