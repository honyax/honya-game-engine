using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 軸に平行な矩形(Axis-Aligned Bounding Box)。**いちばん安い形**。
///
/// 回らない前提を置くだけで、判定が「区間が重なっているか」を
/// X と Y で1回ずつ見るだけになる。分岐4つで終わる。
///
/// 回転しないので、キャラクターの当たり判定としては使いにくい
/// (斜めを向いた剣を表せない)。それでも AABB がどこにでも出てくるのは、
/// **ほかの形の「外接箱」として使える**から。
/// 高い判定(<see cref="Obb2D"/> の SAT など)の前に AABB で足切りしておくと、
/// ほとんどの組は安いほうで弾ける。Day 26 のグリッドもこの発想の延長になる。
/// </summary>
internal readonly struct Aabb2D
{
    public readonly Vector2 Min;
    public readonly Vector2 Max;

    public Aabb2D(Vector2 min, Vector2 max)
    {
        Min = min;
        Max = max;
    }

    public static Aabb2D FromCenter(Vector2 center, Vector2 halfSize) =>
        new(center - halfSize, center + halfSize);

    public Vector2 Center => (Min + Max) * 0.5f;

    public Vector2 HalfSize => (Max - Min) * 0.5f;

    public Vector2 Size => Max - Min;

    public bool Contains(Vector2 point) =>
        point.X >= Min.X && point.X <= Max.X && point.Y >= Min.Y && point.Y <= Max.Y;

    /// <summary>四方に広げた箱。**判定に余裕を持たせる**ときに使う。</summary>
    public Aabb2D Expanded(float amount) =>
        new(Min - new Vector2(amount), Max + new Vector2(amount));

    /// <summary>2つを包む最小の箱。木構造(BVH)を組むときの基本操作。</summary>
    public static Aabb2D Union(in Aabb2D a, in Aabb2D b) =>
        new(Vector2.Min(a.Min, b.Min), Vector2.Max(a.Max, b.Max));
}

/// <summary>
/// 円。**回転を考えなくてよい唯一の形**。
///
/// どちらを向いていても同じなので、判定は中心距離と半径の和を比べるだけ。
/// 弾、範囲攻撃、索敵、キャラクターの足元——2D ゲームの当たり判定は
/// 円で足りることが驚くほど多い。
/// 卒業制作(見下ろし型アクション)も、敵と弾はほぼ全部これになる。
/// </summary>
internal readonly struct Circle2D
{
    public readonly Vector2 Center;
    public readonly float Radius;

    public Circle2D(Vector2 center, float radius)
    {
        Center = center;
        Radius = radius;
    }

    public Aabb2D Bounds => Aabb2D.FromCenter(Center, new Vector2(Radius));
}

/// <summary>
/// 回転する矩形(Oriented Bounding Box)。**今日いちばん高い形**。
///
/// 中心・半径ベクトル・回転角で表す。
/// 4隅を持つ表現もあるが、
///   - 回すのが角度1つで済む
///   - <see cref="AxisX"/> / <see cref="AxisY"/> がそのまま分離軸になる
/// ので、SAT(<see cref="Collision2D"/>)と相性がよいこちらを使う。
///
/// **軸は「箱の辺の向き」**。矩形は向かい合う辺が平行なので、
/// 4辺あっても軸は2本しかない。だから OBB 同士の SAT は 2 + 2 = 4 軸で済む。
/// 一般の凸多角形なら辺の数だけ軸が要る(Day 44 で 3D の SAT をやるときに効いてくる)。
/// </summary>
internal readonly struct Obb2D
{
    public readonly Vector2 Center;
    public readonly Vector2 HalfSize;
    public readonly float Rotation;

    private readonly float _cos;
    private readonly float _sin;

    public Obb2D(Vector2 center, Vector2 halfSize, float rotation)
    {
        Center = center;
        HalfSize = halfSize;
        Rotation = rotation;

        // **三角関数はここで1回だけ**。
        // 軸を使うたびに MathF.Cos を呼ぶと、SAT の中で何度も同じ計算をすることになる。
        _cos = MathF.Cos(rotation);
        _sin = MathF.Sin(rotation);
    }

    /// <summary>横方向の軸(単位ベクトル)。</summary>
    public Vector2 AxisX => new(_cos, _sin);

    /// <summary>縦方向の軸。X 軸を 90 度回したもの。</summary>
    public Vector2 AxisY => new(-_sin, _cos);

    /// <summary>
    /// 外接する AABB。**足切り用**。
    ///
    /// 回した矩形をぴったり包む箱の半径は、
    /// 各軸への投影の絶対値を足したものになる。
    /// (X 方向の広がり) = |cos| * halfX + |sin| * halfY
    /// </summary>
    public Aabb2D Bounds
    {
        get
        {
            float absCos = MathF.Abs(_cos);
            float absSin = MathF.Abs(_sin);

            var extent = new Vector2(
                (absCos * HalfSize.X) + (absSin * HalfSize.Y),
                (absSin * HalfSize.X) + (absCos * HalfSize.Y));

            return Aabb2D.FromCenter(Center, extent);
        }
    }

    /// <summary>ワールド座標を、この箱を基準にした座標へ移す。</summary>
    public Vector2 ToLocal(Vector2 world)
    {
        Vector2 delta = world - Center;

        // 回転の逆変換は転置と同じ。軸との内積を取るだけで済む。
        return new Vector2(Vector2.Dot(delta, AxisX), Vector2.Dot(delta, AxisY));
    }

    /// <summary>この箱を基準にした座標を、ワールド座標へ戻す。</summary>
    public Vector2 ToWorld(Vector2 local) => Center + (AxisX * local.X) + (AxisY * local.Y);

    /// <summary>4隅。描画やデバッグ表示に使う。</summary>
    public void GetCorners(Span<Vector2> corners)
    {
        Vector2 x = AxisX * HalfSize.X;
        Vector2 y = AxisY * HalfSize.Y;

        corners[0] = Center - x - y;
        corners[1] = Center + x - y;
        corners[2] = Center + x + y;
        corners[3] = Center - x + y;
    }
}
