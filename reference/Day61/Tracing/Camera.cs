using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 注視点のまわりを回るカメラの置き方。マウスで回す・寄るのは、この4つの値を変えることになる。
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
/// ピンホールカメラ。<b>画素の座標から光線を1本作る</b>(要点1)。
///
/// <para>
/// ラスタライズでは、点をビュー行列・射影行列で画面へ<b>写した</b>(Day 6)。
/// レイトレースでは逆に、画面の点から<b>世界へ向かう向き</b>を作る。行列は要らず、
/// カメラの3つの軸(右・上・前)を組み合わせるだけで済む。
/// </para>
/// <para>
/// 作ったら変えない(不変)。描画スレッドの全員が同じカメラを読むので、途中で書き換わると
/// 1枚の絵の上と下で別の視点になってしまう。動かすときは新しいカメラを作って描き直す(<see cref="ProgressiveRenderer.Start"/>)。
/// </para>
/// </summary>
internal sealed class Camera
{
    private readonly Vector3 _forward;
    private readonly Vector3 _right;
    private readonly Vector3 _up;

    /// <summary>画面の上端までの傾き <c>tan(縦の画角 / 2)</c>。前へ 1 進んだところでの、画面の半分の高さ。</summary>
    private readonly float _halfHeight;

    /// <summary>同じく右端まで。縦に縦横比を掛ける(画素を正方形に保つ)。</summary>
    private readonly float _halfWidth;

    public Camera(OrbitView view, int width, int height)
    {
        Width = width;
        Height = height;
        Position = view.Position;

        // 前・右・上の3軸を作る。Day 6 の LookAt と同じ手順(外積2回)。
        // ワールドの上 (0, 1, 0) と前の外積で右を作り、右と前の外積で「本当の上」を作り直す。
        // 見下ろしていると前とワールドの上は直交していないので、上をそのまま使うと軸が斜めになる。
        _forward = Vector3.Normalize(view.Target - Position);
        _right = Vector3.Normalize(Vector3.Cross(_forward, Vector3.UnitY));
        _up = Vector3.Cross(_right, _forward);

        _halfHeight = MathF.Tan(view.FovYDegrees * MathF.PI / 180.0f * 0.5f);
        _halfWidth = _halfHeight * width / height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>光線の始点。ピンホールなので、全部の光線がこの1点から出る。</summary>
    public Vector3 Position { get; }

    // ---- ここから4つは Day 61 で公開した ----
    // GPU 側(shaders/pathtrace.comp)でも同じ式で光線を作るので、3軸と画面の半分の大きさを渡す。
    // **行列ではなくこの4つを渡す**のがレイトレーサらしいところで、シェーダ側は
    // 「forward + right * a + up * b」の1行で済む。

    /// <summary>カメラの前方向(長さ 1)。</summary>
    public Vector3 Forward => _forward;

    /// <summary>カメラの右方向(長さ 1)。</summary>
    public Vector3 Right => _right;

    /// <summary>カメラの上方向(長さ 1)。</summary>
    public Vector3 Up => _up;

    /// <summary>前へ 1 進んだところでの、画面の半分の幅。</summary>
    public float HalfWidth => _halfWidth;

    /// <summary>同じく半分の高さ。</summary>
    public float HalfHeight => _halfHeight;

    /// <summary>
    /// 画素の座標 (<paramref name="px"/>, <paramref name="py"/>) を通る光線。
    ///
    /// <para>
    /// 座標は<b>画素の左上の角が整数</b>で、画素の真ん中は <c>(x + 0.5, y + 0.5)</c>。
    /// 小数を受け取るのは、1画素の中のどこを通すかをずらして何本も打つため(要点6のアンチエイリアス)。
    /// </para>
    /// </summary>
    public Ray GetRay(float px, float py)
    {
        // 画素の座標を −1〜+1 に直す。y は画面では下向き、世界では上向きなので反転する。
        float ndcX = px / Width * 2.0f - 1.0f;
        float ndcY = 1.0f - py / Height * 2.0f;

        // 前へ 1 進んだところにある「画面」の上の点へ向かう向き。
        Vector3 direction = _forward + _right * (ndcX * _halfWidth) + _up * (ndcY * _halfHeight);
        return new Ray(Position, Vector3.Normalize(direction));
    }
}
