namespace SoftwareRasterizer;

/// <summary>
/// 2次元ベクトル。今日からは画面座標とテクスチャ座標(Day 8)がこの型になる。
///
/// Day 4 まで画面座標を int で持っていたのを float に変える。理由は2つ。
///   1. Day 6 で変換行列を通すと、頂点の画面座標は当然のように小数になる。
///      整数に丸めてから描くと、回転が滑らかにならず頂点がカクカク飛ぶ
///   2. 小数のまま扱えば、三角形の辺の位置をピクセルより細かく表現できる
///      (サブピクセル精度)。動きの滑らかさが目に見えて変わる
/// </summary>
internal struct Vec2
{
    public float X;

    public float Y;

    public Vec2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public static Vec2 Zero => new(0.0f, 0.0f);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);

    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);

    public static Vec2 operator *(float s, Vec2 a) => a * s;

    public static Vec2 operator /(Vec2 a, float s) => a * (1.0f / s);

    public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>
    /// 2次元の外積(のZ成分)。Day 3 のエッジ関数の正体。
    ///
    /// 3次元の外積は「両方に垂直なベクトル」を返すが、2次元では
    /// 垂直な方向が画面の手前/奥の1軸しかないので、結果は符号付きの数1つで足りる。
    /// 値は a と b が張る平行四辺形の符号付き面積。
    /// </summary>
    public static float Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    public readonly float LengthSquared() => X * X + Y * Y;

    public readonly float Length() => MathF.Sqrt(LengthSquared());

    public readonly Vec2 Normalized()
    {
        float lengthSquared = LengthSquared();
        if (lengthSquared <= 0.0f)
        {
            return this;
        }

        return this * (1.0f / MathF.Sqrt(lengthSquared));
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, float t) => a + (b - a) * t;

    public readonly override string ToString() => $"({X:F3}, {Y:F3})";
}
