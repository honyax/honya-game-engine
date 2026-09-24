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

    /// <summary>
    /// <b>視錐台の6枚の面</b>を世界の座標で返す(Day 64b で追加。要点2)。
    /// 各面は (法線 xyz, 距離 w) で、<c>dot(xyz, p) + w ≥ 0</c> が「内側」。法線は長さ 1 に揃える。
    ///
    /// <para>
    /// 行列から面を直接取り出す(Gribb と Hartmann の方法)。点 p がクリップ座標で (x, y, z, w) になるとき、
    /// 画面の内側の条件は <c>−w ≤ x ≤ w</c>、<c>−w ≤ y ≤ w</c>、<c>0 ≤ z ≤ w</c>(Vulkan の深度は 0〜1)。
    /// x も w も p の一次式なので、<c>w + x ≥ 0</c> はそのまま<b>世界の中の平面の式</b>になる。
    /// </para>
    /// <para>
    /// System.Numerics は「行ベクトル × 行列」なので、x を作るのは<b>行列の1列目</b>(M11, M21, M31, M41)。
    /// y の裏返し(Projection の M22)は上と下の面が入れ替わるだけで、6枚の集まりとしては変わらない。
    /// </para>
    /// </summary>
    public Vector4[] FrustumPlanes()
    {
        Matrix4x4 m = ViewProjection;
        var column0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var column1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var column2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var column3 = new Vector4(m.M14, m.M24, m.M34, m.M44);

        Vector4[] planes =
        [
            column3 + column0,  // 左   : w + x ≥ 0
            column3 - column0,  // 右   : w − x ≥ 0
            column3 + column1,  // 下(y を裏返したので画面では上)
            column3 - column1,  // 上(同じく画面では下)
            column2,            // 手前 : z ≥ 0(Vulkan は深度 0 から)
            column3 - column2,  // 奥   : w − z ≥ 0
        ];

        // 法線の長さで割っておくと、w + 法線・p が「面までの距離(m)」になり、球の半径と比べられる。
        for (int i = 0; i < planes.Length; i++)
        {
            float length = new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();
            planes[i] /= length;
        }

        return planes;
    }

    /// <summary>
    /// 球が視錐台の外にあるか(Day 65a で追加)。<b>6枚の面のどれか1枚の完全に外側</b>にあれば外。
    ///
    /// <para>
    /// GLSL の <c>outsideFrustum</c>(common.glsl)と同じ式。Day 65a では「CPU で体を選ぶ」ときにこちらを使う。
    /// 同じ式を C# と GLSL の2か所に書くことになるが、そうしないと2つの選び方を比べられない。
    /// </para>
    /// </summary>
    /// <param name="planes"><see cref="FrustumPlanes"/> が返した6面。</param>
    public static bool IsOutside(Vector4[] planes, Vector3 center, float radius)
    {
        foreach (Vector4 plane in planes)
        {
            if (plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W < -radius)
            {
                return true;
            }
        }

        return false;
    }
}
