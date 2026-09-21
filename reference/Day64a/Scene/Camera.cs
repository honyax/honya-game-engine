using System.Numerics;

namespace MeshletRenderer;

/// <summary>
/// 注視点のまわりを回るカメラの置き方。マウスで回す・寄るのは、この4つの値を変えること。
/// Day 59〜62 の <c>OrbitView</c> と同じもの(Day 62c から持ってきた)。
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
/// ピンホールカメラ。Day 62 までは<b>光線を作るための3軸</b>を持っていたが、
/// 今日は<b>頂点を画面へ運ぶための行列</b>を持つ(ラスタライズに戻ってきた)。
///
/// <para>
/// レイトレーサは「画素 → 光線 → 物」の向きに進むので、行列が要らなかった。
/// ラスタライザは逆向きの「物 → 画面」で、頂点1つずつに行列を掛ける。
/// Day 3〜10 のソフトウェアラスタライザ、Day 14 以降のエンジンと同じ道。
/// </para>
/// </summary>
internal sealed class Camera
{
    /// <summary>手前の切り口(m)。小さくしすぎると深度の精度が奥で足りなくなる。</summary>
    public const float Near = 0.1f;

    /// <summary>奥の切り口(m)。今日の場面は一番広くても 64m 四方ほどなので、余裕を持たせてある。</summary>
    public const float Far = 200.0f;

    public Camera(OrbitView view, int width, int height)
    {
        Width = width;
        Height = height;
        Position = view.Position;

        // Day 62 が持っていた前・右・上の3軸(光線を作るためのもの)は要らなくなった。
        // LookAt が中で同じ外積2回をやってくれる(Day 6 と同じ手順)。
        View = Matrix4x4.CreateLookAt(Position, view.Target, Vector3.UnitY);

        // System.Numerics の透視投影は**右手系で、深度を 0〜1 に写す**。
        // OpenGL(−1〜1)ではなく Direct3D と同じ範囲で、Vulkan もこちら。だからそのまま使える。
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            view.FovYDegrees * MathF.PI / 180.0f, (float)width / height, Near, Far);

        // **Vulkan の画面は y が下向き**(OpenGL は上向き)。投影の y を裏返して合わせる。
        // 裏返すと三角形の回り方も画面上で逆になるので、表の向き(FrontFace)も合わせて選ぶ
        // (GraphicsPipeline.cs の Rasterization を参照)。
        projection.M22 = -projection.M22;
        Projection = projection;

        // System.Numerics は「行ベクトル × 行列」の流儀なので、掛ける順は View → Projection。
        ViewProjection = View * Projection;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>目の位置。</summary>
    public Vector3 Position { get; }

    /// <summary>世界 → 目の座標。</summary>
    public Matrix4x4 View { get; }

    /// <summary>目の座標 → クリップ座標(y を裏返し済み)。</summary>
    public Matrix4x4 Projection { get; }

    /// <summary>
    /// 世界 → クリップ座標。<b>このまま GPU へ渡す</b>。
    ///
    /// <para>
    /// System.Numerics の行列はメモリ上で行が先に並び、GLSL の <c>mat4</c> は列が先に並ぶ。
    /// だからそのまま渡すと GLSL 側では<b>転置されて見える</b>——そして転置した行列を
    /// 列ベクトルに左から掛ける(<c>viewProj * p</c>)と、C# 側の <c>p * ViewProjection</c> と同じ答えになる。
    /// 2つの流儀の違いが、ちょうど打ち消し合う(Day 14 のエンジンと同じ約束)。
    /// </para>
    /// </summary>
    public Matrix4x4 ViewProjection { get; }
}
