using System.Numerics;
using Silk.NET.Input;

namespace HonyaEngine;

/// <summary>
/// デバイスのイベントを受け取り、**ステップの境界で <see cref="InputSnapshot"/> に畳む**。
///
/// 入力まわりで踏みやすい罠は、突き詰めると「時間の粒度が3つある」ことに尽きる。
///   1. OS/デバイスがイベントを投げる粒度(不定。1フレームに何回でも来る)
///   2. 描画フレームの粒度(可変。数千fps 出ることもある)
///   3. シミュレーションのステップ粒度(固定。60Hz など)
///
/// ゲームのロジックが見たいのは 3 だけ。だから 1 を溜めておいて、
/// 3 の境界で1枚のスナップショットにする。この変換をどこか1箇所に
/// 閉じ込めておかないと、「たまに入力が効かない」の原因が特定できなくなる。
/// </summary>
internal sealed class InputSystem
{
    private readonly InputMap _map;

    private readonly List<IKeyboard> _keyboards = [];
    private readonly List<IMouse> _mice = [];

    /// <summary>今この瞬間、物理的に押されているアクション。イベントで直接更新する。</summary>
    private GameAction _heldNow;

    /// <summary>次のステップ境界までに「押された」アクション。境界で消費する。</summary>
    private GameAction _pendingPressed;

    /// <summary>次のステップ境界までに「離された」アクション。</summary>
    private GameAction _pendingReleased;

    private Vector2 _mousePosition;
    private Vector2 _mouseDeltaAccumulator;
    private Vector2 _lastMousePosition;
    private float _scrollAccumulator;
    private bool _hasMousePosition;

    public InputSystem(InputMap map)
    {
        _map = map;
    }

    /// <summary>直近の <see cref="BeginStep"/> で確定したスナップショット。</summary>
    public InputSnapshot Current { get; private set; }

    public void Attach(IKeyboard keyboard)
    {
        _keyboards.Add(keyboard);
        keyboard.KeyDown += OnKeyDown;
        keyboard.KeyUp += OnKeyUp;
    }

    public void Attach(IMouse mouse)
    {
        _mice.Add(mouse);
        mouse.MouseMove += OnMouseMove;
        mouse.Scroll += OnScroll;
    }

    public void Detach()
    {
        foreach (IKeyboard keyboard in _keyboards)
        {
            keyboard.KeyDown -= OnKeyDown;
            keyboard.KeyUp -= OnKeyUp;
        }

        foreach (IMouse mouse in _mice)
        {
            mouse.MouseMove -= OnMouseMove;
            mouse.Scroll -= OnScroll;
        }

        _keyboards.Clear();
        _mice.Clear();
    }

    /// <summary>
    /// **ステップの直前に1回だけ呼ぶ**。溜まっていた入力を1枚に畳んで返す。
    ///
    /// ここが今日の心臓部。畳み方に2つの判断が入っている。
    ///
    /// **(1) 押しっぱなしの扱い**
    /// <c>Held</c> に <c>_pendingPressed</c> を足している。
    /// ステップの間に「押して離した」場合、押した瞬間には離れているので
    /// <c>_heldNow</c> にはビットが立っていない。それでも
    /// **そのステップの間は押されていた**のだから Held を立てる。
    /// こうしないと、素早いタップで移動がまったく効かないことがある。
    ///
    /// **(2) 消費してリセットする**
    /// <c>Pressed</c> と <c>Released</c> はここで空にする。
    /// これを忘れると、1フレームで複数ステップ回ったとき(Day 19 要点2)に
    /// **同じ「押した瞬間」を何度も消費**することになる。
    /// ジャンプが2回発動する、弾が2発出る、といった形で表面化する。
    /// </summary>
    public InputSnapshot BeginStep()
    {
        Current = new InputSnapshot(
            _heldNow | _pendingPressed,
            _pendingPressed,
            _pendingReleased,
            _mousePosition,
            _mouseDeltaAccumulator,
            _scrollAccumulator);

        _pendingPressed = GameAction.None;
        _pendingReleased = GameAction.None;
        _mouseDeltaAccumulator = Vector2.Zero;
        _scrollAccumulator = 0.0f;

        return Current;
    }

    /// <summary>
    /// 再生などで外から入力を差し込むとき用。
    /// <see cref="BeginStep"/> の代わりに呼ぶ。
    /// </summary>
    public void SetCurrent(InputSnapshot snapshot) => Current = snapshot;

    /// <summary>
    /// 溜まっている入力を捨てる。
    /// ウィンドウがフォーカスを失ったときや、リプレイの開始時に呼ぶ。
    ///
    /// **押しっぱなしのまま別のウィンドウへ移ると、KeyUp が来ない**。
    /// 戻ってきたときに「ずっと右へ走り続ける」のはこれが原因で、
    /// フォーカス喪失時にここを呼ぶのが定石。
    /// </summary>
    public void Clear()
    {
        _heldNow = GameAction.None;
        _pendingPressed = GameAction.None;
        _pendingReleased = GameAction.None;
        _mouseDeltaAccumulator = Vector2.Zero;
        _scrollAccumulator = 0.0f;
        Current = InputSnapshot.Empty;
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        GameAction action = _map.Resolve(key);
        if (action == GameAction.None)
        {
            return;
        }

        // **オートリピートを弾く**。
        // キーを押しっぱなしにすると、OS が KeyDown を繰り返し送ってくる
        // (Day 14 でシェーダのホットリロードを検証したときに踏んだのと同じ挙動)。
        // そのまま Pressed に流すと「押した瞬間」が毎秒何十回も発生し、
        // ジャンプが連発する。すでに押されているぶんは除いてから立てる。
        GameAction newlyPressed = action & ~_heldNow;

        _pendingPressed |= newlyPressed;
        _heldNow |= action;
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        GameAction action = _map.Resolve(key);
        if (action == GameAction.None)
        {
            return;
        }

        _pendingReleased |= action & _heldNow;
        _heldNow &= ~action;
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (_hasMousePosition)
        {
            // 差分を足し込む。カーソルを画面中央へ戻すような実装を足したときに、
            // 「戻した回だけ足さない」で済むようにするための形
            // (InputSnapshot.MouseDelta のコメント参照)。
            _mouseDeltaAccumulator += position - _lastMousePosition;
        }
        else
        {
            // 初回は前の位置が無いので、差分は 0 にする。
            // これをやらないと、起動直後にマウスが画面の端から現在位置まで
            // 一気に動いたことになり、カメラが吹き飛ぶ。
            _hasMousePosition = true;
        }

        _lastMousePosition = position;
        _mousePosition = position;
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel) => _scrollAccumulator += wheel.Y;
}
