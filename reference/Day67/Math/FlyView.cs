using System.Numerics;

namespace GaussianSplatting;

/// <summary>
/// 飛び回るカメラの姿勢。<b>位置と、左右の向き(Yaw)と、上下の向き(Pitch)</b>だけを持つ。
///
/// <para>
/// Day 59〜61 は注視点の周りを回るカメラだったが、今日は<b>シーンの中に入って歩き回る</b>のが見どころなので、
/// ゲームの一人称視点と同じ形にした。3DGS は撮影したカメラの通り道の近くでしか正しく見えない(要点7)——
/// それを確かめるには、自分でそこから外れてみるのがいちばん早い。
/// </para>
/// <para>
/// 「上」はワールドごとに違う(<see cref="SplatCloud.WorldUp"/>)。自前の場面は +Y、学習済みのシーンはたいてい −Y。
/// そこで向きは「上を軸にした Yaw と Pitch」で持ち、軸は毎回 <see cref="Frame"/> で組み立てる。
/// </para>
/// </summary>
internal readonly record struct FlyView(Vector3 Position, float Yaw, float Pitch, float FovYDegrees)
{
    public static FlyView Default => new(new Vector3(0.0f, 0.0f, 5.0f), 0.0f, 0.0f, 50.0f);

    /// <summary>上下を向ける限界。真上・真下を向くと「右」が決まらなくなるので、少し手前で止める。</summary>
    public const float MaxPitch = 1.5f;

    /// <summary>
    /// Yaw = 0・Pitch = 0 のときの前。「上」に垂直な向きを1つ決める。
    /// +Y が上なら −Z(OpenGL の慣習どおり奥を見る)、−Y が上なら +Z(COLMAP のカメラと同じ向き)になる。
    /// </summary>
    public static Vector3 ReferenceForward(Vector3 worldUp)
    {
        Vector3 side = MathF.Abs(worldUp.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        return Vector3.Normalize(Vector3.Cross(worldUp, side));
    }

    /// <summary>
    /// カメラの3つの軸(右・上・前)を組み立てる。Day 59 の <c>Camera</c> と同じく、外積2回で作る。
    /// </summary>
    public (Vector3 Right, Vector3 Up, Vector3 Forward) Axes(Vector3 worldUp)
    {
        Vector3 f0 = ReferenceForward(worldUp);
        Vector3 r0 = Vector3.Normalize(Vector3.Cross(f0, worldUp));

        Vector3 horizontal = MathF.Cos(Yaw) * f0 + MathF.Sin(Yaw) * r0;
        Vector3 forward = Vector3.Normalize(MathF.Cos(Pitch) * horizontal + MathF.Sin(Pitch) * worldUp);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, worldUp));
        Vector3 up = Vector3.Cross(right, forward);
        return (right, up, forward);
    }

    /// <summary>
    /// 前・右・上の量だけ動かす(W/S・A/D・Q/E)。前後は<b>見ている向きそのまま</b>(下を見て W を押せば下へ進む)、
    /// 上下はワールドの上に沿って動く。
    /// </summary>
    public FlyView Move(Vector3 worldUp, float forward, float right, float up)
    {
        var (r, _, f) = Axes(worldUp);
        return this with { Position = Position + f * forward + r * right + worldUp * up };
    }

    /// <summary>画面の大きさを決めて、投影に要る値をまとめる。</summary>
    public CameraFrame Frame(Vector3 worldUp, int width, int height)
    {
        var (right, up, forward) = Axes(worldUp);

        // 焦点距離を画素で表したもの。縦の画角の半分の tan と、画面の高さの半分の比。
        // 「1m 先の 1m は、画面で何画素になるか」(Day 6 の射影行列の [1][1] に、画面の高さの半分を掛けたもの)。
        float tanHalfY = MathF.Tan(FovYDegrees * MathF.PI / 360.0f);
        float focal = height * 0.5f / tanHalfY;
        float tanHalfX = tanHalfY * width / height;

        return new CameraFrame(Position, right, up, forward, focal, focal, width, height, tanHalfX, tanHalfY);
    }
}

/// <summary>
/// 投影に要る値をひとまとめにしたもの。<b>行列は使わず、軸3本と焦点距離で写す</b>(要点2)。
///
/// <para>
/// カメラから見た点の座標は <c>x = (p − 位置)·右</c>、<c>y = (p − 位置)·上</c>、<c>z = (p − 位置)·前</c>。
/// 画面の中心からの画素は <c>(Fx · x / z, Fy · y / z)</c>(y は上向き)。
/// これは Day 6 のビュー行列 → 射影行列 → ビューポートを1行にまとめたのと同じで、
/// <b>z で割る</b>ところが、要点2のヤコビアンの出どころになる。
/// </para>
/// </summary>
internal readonly record struct CameraFrame(
    Vector3 Position,
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward,
    float Fx,
    float Fy,
    int Width,
    int Height,
    float TanHalfX,
    float TanHalfY)
{
    /// <summary>これより近い楕円体は描かない(元論文の実装と同じ 0.2)。</summary>
    public const float Near = 0.2f;

    /// <summary>ワールドの点を、カメラから見た座標(右・上・前)にする。</summary>
    public Vector3 ToView(Vector3 world)
    {
        Vector3 t = world - Position;
        return new Vector3(Vector3.Dot(t, Right), Vector3.Dot(t, Up), Vector3.Dot(t, Forward));
    }
}
