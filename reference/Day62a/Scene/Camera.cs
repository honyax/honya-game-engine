using System.Numerics;

namespace HardwareRayTracer;

/// <summary>
/// 注視点のまわりを回るカメラの置き方。マウスで回す・寄るのは、この4つの値を変えること。
/// Day 59〜61 の <c>OrbitView</c> と同じもの(場面とカメラだけは持ってきた)。
/// </summary>
/// <param name="Target">注視点(m)。</param>
/// <param name="Yaw">水平の回転(ラジアン)。0 で注視点の −Z 側から +Z を向く。</param>
/// <param name="Pitch">見下ろす角度(ラジアン)。正で上から。</param>
/// <param name="Distance">注視点からの距離(m)。</param>
/// <param name="FovYDegrees">縦の画角(度)。</param>
internal readonly record struct OrbitView(Vector3 Target, float Yaw, float Pitch, float Distance, float FovYDegrees)
{
    public Vector3 Position => Target + Distance * new Vector3(
        MathF.Sin(Yaw) * MathF.Cos(Pitch),
        MathF.Sin(Pitch),
        -MathF.Cos(Yaw) * MathF.Cos(Pitch));
}

/// <summary>
/// ピンホールカメラ。<b>画素の座標から光線を1本作る</b>ための3軸を用意する。
///
/// <para>
/// Day 59〜61 の <c>Camera</c> から、GPU に渡す 4 つの値(3軸と画面の半分の大きさ)だけを残したもの。
/// <b>行列を作らない</b>のがレイトレーサらしいところで、シェーダ側は
/// <c>forward + right * a + up * b</c> の1行で光線の向きが出る。
/// </para>
/// </summary>
internal sealed class Camera
{
    public Camera(OrbitView view, int width, int height)
    {
        Width = width;
        Height = height;
        Position = view.Position;

        // 前・右・上の3軸。Day 6 の LookAt と同じ手順(外積2回)。
        // ワールドの上 (0,1,0) と前の外積で右を作り、右と前の外積で「本当の上」を作り直す。
        Forward = Vector3.Normalize(view.Target - Position);
        Right = Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));
        Up = Vector3.Cross(Right, Forward);

        HalfHeight = MathF.Tan(view.FovYDegrees * MathF.PI / 180.0f * 0.5f);
        HalfWidth = HalfHeight * width / height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>光線の始点。ピンホールなので、全部の光線がこの1点から出る。</summary>
    public Vector3 Position { get; }

    public Vector3 Forward { get; }

    public Vector3 Right { get; }

    public Vector3 Up { get; }

    /// <summary>前へ 1 進んだところでの、画面の半分の幅。</summary>
    public float HalfWidth { get; }

    /// <summary>同じく半分の高さ。</summary>
    public float HalfHeight { get; }
}
