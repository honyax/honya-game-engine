using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **シーンから当たり判定を起こす**(Day 51)。今日の主役その2。
///
/// <para>
/// Day 39 の <see cref="DemoScene"/> は<b>描くもの</b>しか持っていなかった。
/// 板と glTF のパーツが平らに並んでいて、それぞれが世界行列を1つ持つ——
/// 「動かないものに階層は要らない」という判断そのものだった。
/// 今日そこをキャラクターが歩くので、<b>触れるもの</b>が要る。
/// </para>
///
/// <para>
/// <b>描画メッシュを当たり判定に使わない</b>のがここの出発点になる。
/// 街灯 1 体は 5 千枚の三角形で、カプセルと総当たりすれば 1 ステップ 5 千回。
/// キャラクターは 1 ステップに 10 回以上問い合わせる(Day 45)ので、
/// それだけで 5 万回。<b>近似の形に置き換える</b>のが唯一の道で、
/// 実際のエンジンも「描画用メッシュ」と「衝突用メッシュ」を別に持つ。
/// </para>
///
/// <para>
/// <b>置き換える形は世界軸に沿った箱(AABB)</b>にした。理由は2つ。
/// </para>
/// <list type="number">
/// <item>
/// <b>Day 44 の SAT がそのまま使える</b>。<see cref="Collider.Box"/> の
/// 静的な体を置くだけで、カプセルとの判定は
/// <see cref="Collision3D.CapsuleBox"/> が既に持っている。
/// </item>
/// <item>
/// <b>回転を持ち込まなくて済む</b>。OBB にすれば太らないが、
/// 1体が何個かのパーツに割れている(街灯 = 柱 + 笠 + ガラス)ので、
/// <b>パーツごとに違う行列を1つの向きにまとめる</b>という厄介が出る。
/// </item>
/// </list>
///
/// <para>
/// <b>代償は「太る」こと</b>。世界空間の境界箱は
/// <c>DemoScene.TransformBounds</c> が 8 隅を回して作っているので、
/// 斜めに置いたものは<b>元より必ず大きい箱</b>に包まれる。
/// 14 度傾けて立てかけた古タイヤは、実物より一回り大きい箱になる——
/// 歩くと「見た目より手前で止まる」。直すなら OBB(改造課題2)。
/// </para>
/// </summary>
internal static class SceneCollision
{
    /// <summary>
    /// 起こした箱1つぶん。**デバッグ表示のために覚えておく**。
    ///
    /// <para>
    /// 物理の側(<see cref="PhysicsWorld.Bodies"/>)にも同じものが入っているが、
    /// あちらには「どの小物から起こしたか」が残らない。
    /// 「消火栓の箱が大きすぎる」と気づくには名前が要る。
    /// </para>
    /// </summary>
    internal readonly record struct Piece(string Name, Vector3 Center, Vector3 HalfExtents, int BodyIndex);

    /// <summary>組み上げた結果のまとめ。**コンソールと HUD に出す**。</summary>
    internal readonly record struct Result(
        int BoxCount,
        int PlaneCount,
        int SkippedCount,
        int SourceItemCount,
        IReadOnlyList<Piece> Pieces);

    /// <summary>
    /// 板の厚み [m] の下限。**厚み 0 の箱は当たり判定にならない**。
    ///
    /// <para>
    /// 壁の板は XY 平面の 1x1 を回して置いたもので、Z 方向の広がりが厳密に 0。
    /// そのまま <see cref="Collider.Box"/> に渡すと、
    /// SAT の分離軸のうち 1 本が長さ 0 になり、
    /// <b>「どんなに押しても離れられる」= すり抜ける</b>面ができる。
    /// 10cm 与えておけば、壁の裏側に 5cm はみ出すだけで済む。
    /// </para>
    /// </summary>
    public const float MinThickness = 0.10f;

    /// <summary>
    /// シーンの中身を静的な体として <paramref name="world"/> に足す。
    ///
    /// <para>
    /// <b>名前で束ねる</b>のが最初の仕事。<see cref="DemoScene.Item"/> は
    /// glTF のパーツ単位に割れていて、名前が <c>"街灯/Lamp_Glass"</c> の形になっている
    /// (<c>DemoScene.AddProp</c>)。<c>/</c> の手前で束ねると、
    /// <b>JSON に書いた小物 1 つが箱 1 つ</b>になる。
    /// 束ねずにパーツごとに箱を作ると、街灯だけで 3 つの箱が重なって立つ——
    /// 動きはするが、内訳を読んでも何が起きているのか分からなくなる。
    /// </para>
    ///
    /// <para>
    /// <b>色も一緒に足す</b>のは、<c>Program.BodyColors</c> が
    /// <see cref="PhysicsWorld.Bodies"/> と<b>並行配列</b>だから(Day 44 で手当てしたところ)。
    /// 体だけ足して色を足さないと、以降の体の色が 1 つずつずれる。
    /// 並行配列のいちばん素直な壊れ方で、<b>足す側が両方の面倒を見る</b>しかない。
    /// </para>
    /// </summary>
    /// <param name="scene">Day 39 のシーン。</param>
    /// <param name="world">足す先。**呼ぶ前に <see cref="PhysicsWorld.Clear"/> しておく**。</param>
    /// <param name="colors">体と並行に持っている色の一覧。ここにも 1 つずつ足す。</param>
    public static Result Build(DemoScene scene, PhysicsWorld world, List<Vector4> colors)
    {
        var groups = new Dictionary<string, List<DemoScene.Item>>(StringComparer.Ordinal);
        var order = new List<string>();

        int planes = 0;
        int skipped = 0;

        foreach (DemoScene.Item item in scene.Items)
        {
            if (item.Collision == SceneCollisionKind.None)
            {
                skipped++;
                continue;
            }

            if (item.Collision == SceneCollisionKind.Plane)
            {
                // **平面は束ねない**。地面は 1 枚しか無いし、
                // 無限に広いので複数枚を合成する意味も無い。
                world.AddPlane(PlaneOf(item));
                colors.Add(new Vector4(0.20f, 0.22f, 0.26f, 1.0f));
                planes++;
                continue;
            }

            string group = GroupName(item.Name);

            if (!groups.TryGetValue(group, out List<DemoScene.Item>? bucket))
            {
                bucket = [];
                groups[group] = bucket;
                order.Add(group);
            }

            bucket.Add(item);
        }

        var pieces = new List<Piece>(order.Count);

        foreach (string group in order)
        {
            (Vector3 min, Vector3 max) = WorldBounds(groups[group]);

            Vector3 center = (min + max) * 0.5f;
            Vector3 half = Vector3.Max((max - min) * 0.5f, new Vector3(MinThickness * 0.5f));

            RigidBody box = RigidBody.CreateStatic(Collider.Box(half));
            box.Position = center;

            // **跳ねない**。街灯にぶつかった球が飛んでいくと、
            // 「環境に当たった」ではなく「弾かれた」に見える。
            box.Restitution = 0.0f;

            int index = world.AddBody(box);

            // 環境は灰色。**動くもの(Day 43〜47 の色)と区別が付く**ようにしてある。
            colors.Add(new Vector4(0.30f, 0.33f, 0.38f, 1.0f));

            pieces.Add(new Piece(group, center, half, index));
        }

        return new Result(pieces.Count, planes, skipped, scene.Items.Count, pieces);
    }

    /// <summary>
    /// 板 1 枚を無限平面にする。**表は板の +Z**(<see cref="Primitives.CreateQuad"/>)。
    ///
    /// <para>
    /// 向きは行列で法線を運んで作る。<b>位置ではなく向きの変換</b>なので
    /// <see cref="Vector3.TransformNormal"/> を使う——
    /// 平行移動を巻き込むと、原点から離れた床の法線が真上を向かなくなる。
    /// </para>
    /// </summary>
    private static Plane3D PlaneOf(in DemoScene.Item item)
    {
        Vector3 normal = Vector3.TransformNormal(Vector3.UnitZ, item.Transform);
        return Plane3D.FromPointNormal(item.Transform.Translation, normal);
    }

    /// <summary><c>"街灯/Lamp_Glass"</c> → <c>"街灯"</c>。<c>/</c> が無ければそのまま。</summary>
    private static string GroupName(string name)
    {
        int slash = name.IndexOf('/');
        return slash < 0 ? name : name[..slash];
    }

    /// <summary>
    /// 束ねたパーツ全部を包む箱。**もう回すものは何も無い**。
    ///
    /// <para>
    /// <see cref="DemoScene.Item.BoundsMin"/> は<b>世界空間で入っている</b>ので
    /// (Day 51 で <c>DemoScene.TransformBounds</c> を通した)、
    /// ここは最小・最大を取るだけで済む。
    /// <b>太るのはすでに済んでいる</b>——斜めに置いた小物が実物より大きい箱になるのは、
    /// この関数ではなく <c>DemoScene.TransformBounds</c> の 8 隅の変換で起きている。
    /// </para>
    /// </summary>
    private static (Vector3 Min, Vector3 Max) WorldBounds(List<DemoScene.Item> items)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (DemoScene.Item item in items)
        {
            min = Vector3.Min(min, item.BoundsMin);
            max = Vector3.Max(max, item.BoundsMax);
        }

        return (min, max);
    }
}
