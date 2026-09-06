using System.Numerics;
using Silk.NET.Input;

namespace HonyaEngine;

/// <summary>
/// 注視点のまわりを回るカメラ操作。マウスの入力を <see cref="Camera"/> の位置に変換する。
///
/// **カメラとカメラ操作を分ける**のがこのクラスの存在理由。
/// <see cref="Camera"/> は「どこから、どこを、どんなレンズで見るか」しか知らず、
/// 入力デバイスのことを何も知らない。おかげで同じ Camera を、
/// 一人称視点でも、三人称追従(Day 50)でも、シネマティックの固定カメラでも使い回せる。
/// ここを混ぜると「カメラを使いたいだけなのにマウスが必要」という妙な依存が生まれる。
///
/// 位置は**球面座標**で持つ。直交座標 (x, y, z) をそのまま動かすと
/// 「注視点からの距離を保ったまま回す」のが面倒になるのに対し、
/// 球面座標なら回転は角度の足し算、ズームは距離の掛け算で済む。
/// </summary>
internal sealed class OrbitCameraController
{
    private readonly Camera _camera;

    private IMouse? _mouse;
    private bool _dragging;
    private Vector2 _lastMousePosition;

    /// <summary>水平方向の角度(ラジアン)。0 のときカメラは +Z 側に居る。</summary>
    public float Yaw { get; set; } = 0.6f;

    /// <summary>垂直方向の角度(ラジアン)。正で見下ろし、負で見上げ。</summary>
    public float Pitch { get; set; } = 0.4f;

    /// <summary>注視点までの距離。</summary>
    public float Distance { get; set; } = 9.0f;

    /// <summary>注視点。カメラはこの点のまわりを回る。</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>マウスの移動量1ピクセルあたり何ラジアン回すか。</summary>
    public float RotateSpeed { get; set; } = 0.008f;

    /// <summary>ホイール1目盛りあたりの倍率。</summary>
    public float ZoomSpeed { get; set; } = 0.12f;

    public float MinDistance { get; set; } = 2.0f;

    public float MaxDistance { get; set; } = 40.0f;

    public OrbitCameraController(Camera camera)
    {
        _camera = camera;
        Apply();
    }

    /// <summary>
    /// マウスの入力を受け取り始める。
    ///
    /// Silk.NET の <see cref="IMouse"/> は**イベントとポーリングの両方**を持つ。
    /// ここではドラッグの開始・終了という「瞬間」を扱うのでイベントを使い、
    /// 移動量は前フレームとの差から求める。
    /// (「押されている間ずっと」を扱うならポーリングのほうが素直。Day 20 で整理する)
    /// </summary>
    public void Attach(IMouse mouse)
    {
        _mouse = mouse;
        mouse.MouseDown += OnMouseDown;
        mouse.MouseUp += OnMouseUp;
        mouse.MouseMove += OnMouseMove;
        mouse.Scroll += OnScroll;
    }

    public void Detach()
    {
        if (_mouse is null)
        {
            return;
        }

        _mouse.MouseDown -= OnMouseDown;
        _mouse.MouseUp -= OnMouseUp;
        _mouse.MouseMove -= OnMouseMove;
        _mouse.Scroll -= OnScroll;
        _mouse = null;
    }

    /// <summary>初期状態に戻す。</summary>
    public void Reset()
    {
        Yaw = 0.6f;
        Pitch = 0.4f;
        Distance = 9.0f;
        Target = Vector3.Zero;
        Apply();
    }

    /// <summary>
    /// 球面座標からカメラの位置を計算して <see cref="Camera"/> に書き込む。
    ///
    /// 半径 d・水平角 yaw・仰角 pitch から直交座標へ:
    ///   x = d * cos(pitch) * sin(yaw)
    ///   y = d * sin(pitch)
    ///   z = d * cos(pitch) * cos(yaw)
    /// cos(pitch) が x と z の両方に掛かるのは、**上を向くほど水平方向の半径が縮む**から。
    /// 地球儀の緯線が極に近いほど短くなるのと同じ理屈。
    /// </summary>
    public void Apply()
    {
        // 仰角は ±90度の手前で止める。真上・真下に来ると視線と Up が平行になり、
        // LookAt が軸を作れずに絵が壊れる(Camera.Up のコメント参照)。
        const float pitchLimit = 1.5f;   // 約86度
        Pitch = Math.Clamp(Pitch, -pitchLimit, pitchLimit);
        Distance = Math.Clamp(Distance, MinDistance, MaxDistance);

        float cosPitch = MathF.Cos(Pitch);

        _camera.Target = Target;
        _camera.Position = Target + new Vector3(
            Distance * cosPitch * MathF.Sin(Yaw),
            Distance * MathF.Sin(Pitch),
            Distance * cosPitch * MathF.Cos(Yaw));

        // 平行投影のときは「視野角」に相当するものが無いので、
        // 距離から画面に収める高さを決める。こうしておくと
        // 透視 ⇔ 平行を切り替えても**注視点まわりの見かけの大きさが揃う**ので、
        // 遠近感の有無だけを比べられる。
        _camera.OrthographicHeight = 2.0f * Distance * MathF.Tan(_camera.FieldOfView * 0.5f);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button != MouseButton.Left)
        {
            return;
        }

        _dragging = true;
        _lastMousePosition = mouse.Position;
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            _dragging = false;
        }
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (!_dragging)
        {
            // ドラッグしていない間も位置を覚えておく。
            // そうしないと、離れた場所で押し直したときに差分が跳ねてカメラが飛ぶ。
            _lastMousePosition = position;
            return;
        }

        Vector2 delta = position - _lastMousePosition;
        _lastMousePosition = position;

        // 右へドラッグしたら世界が左へ回る = カメラが右回りに移動する、と感じる向きにする。
        Yaw -= delta.X * RotateSpeed;

        // 画面の Y は下向きが正。上へドラッグしたら見下ろしたいので、そのまま足す。
        Pitch += delta.Y * RotateSpeed;

        Apply();
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        // 加算ではなく**乗算**でズームする。
        // 加算だと近くでは大きすぎ、遠くでは小さすぎる動きになる。
        // 「今の距離に対する割合」で動かすと、どの距離でも同じ操作感になる。
        Distance *= MathF.Pow(1.0f - ZoomSpeed, wheel.Y);
        Apply();
    }
}
