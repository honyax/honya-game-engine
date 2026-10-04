using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **1ステップぶんの入力**を固めたもの。
///
/// 入力はイベントで、ばらばらのタイミングで飛んでくる。
/// 一方シミュレーションは固定間隔で回る(Day 19)。
/// この2つを噛み合わせるのが今日の主題で、答えは
/// **「ステップの境界で入力を1枚の値に畳む」**こと。
///
/// 畳んでしまえば、ゲームのロジックから見た入力は
/// 「毎ステップ渡ってくる、変わらない1個の値」になる。おかげで
///   - ステップの途中で入力が変わらない(判定が安定する)
///   - **そのまま記録できる**(<see cref="InputRecorder"/> → リプレイ)
///   - テストで好きな値を差し込める
/// が全部手に入る。
///
/// <c>readonly struct</c> にしてあるのは、**渡した先で書き換えられないため**。
/// 入力は読むものであって書くものではない、という意図を型で表しておく。
/// </summary>
internal readonly struct InputSnapshot
{
    public InputSnapshot(
        GameAction held,
        GameAction pressed,
        GameAction released,
        Vector2 mousePosition,
        Vector2 mouseDelta,
        float scroll)
    {
        Held = held;
        Pressed = pressed;
        Released = released;
        MousePosition = mousePosition;
        MouseDelta = mouseDelta;
        Scroll = scroll;
    }

    /// <summary>このステップの間、押されていたアクション。</summary>
    public GameAction Held { get; }

    /// <summary>
    /// このステップで**新たに押された**アクション。
    ///
    /// <see cref="Held"/> との違いが今日いちばん大事なところ(要点2)。
    /// 「押されている」は移動やチャージ、「押した瞬間」はジャンプや発射。
    /// 後者を <see cref="Held"/> で書くと、押しっぱなしで連射になる。
    /// </summary>
    public GameAction Pressed { get; }

    /// <summary>このステップで離されたアクション。長押しの終わりを取るのに使う。</summary>
    public GameAction Released { get; }

    /// <summary>マウスの位置(スクリーン座標、ピクセル)。</summary>
    public Vector2 MousePosition { get; }

    /// <summary>
    /// 前のステップからのマウスの移動量の合計。
    ///
    /// 注意しておくと、**絶対座標だけを見ている限り、これは
    /// 「今の位置 - 前のステップの位置」と同じ値になる**。
    /// 途中の差分を足すと打ち消し合って両端だけが残るので、当然そうなる
    /// (行って戻れば合計もゼロ)。
    ///
    /// それでも合計の形で持つのは、視点操作(マウスルック)を入れた瞬間に
    /// 両者が食い違うから。カーソルを毎フレーム画面中央へ戻す実装では、
    /// **戻した瞬間に座標が飛ぶ**ので「今 - 前」だとその飛びが移動量に混ざる。
    /// イベントごとの差分を足す形にしておけば、戻した回だけ足さずに済む。
    ///
    /// なお、カーソルが画面の端に張り付くと座標が変わらなくなる問題は
    /// どちらの方式でも防げない。本気でやるなら OS の生入力(raw input)を使う。
    /// </summary>
    public Vector2 MouseDelta { get; }

    /// <summary>前のステップからのホイールの回転量の合計。</summary>
    public float Scroll { get; }

    public bool IsHeld(GameAction action) => (Held & action) != 0;

    public bool WasPressed(GameAction action) => (Pressed & action) != 0;

    public bool WasReleased(GameAction action) => (Released & action) != 0;

    /// <summary>
    /// 移動の入力を -1〜1 のベクトルにしたもの。
    ///
    /// **「キーが押されているか」から「どちらへ動きたいか」への翻訳**がここ。
    /// ゲームのロジックはこちらだけを見ればよく、
    /// 入力元がキーボードでもスティックでも AI でも同じ形になる。
    ///
    /// 斜めを正規化しているのは、そうしないと斜め移動が √2 倍速くなるため。
    /// 「斜めに歩くと速い」は 2D ゲームの古典的なバグ。
    /// </summary>
    public Vector2 MoveAxis
    {
        get
        {
            var axis = new Vector2(
                (IsHeld(GameAction.MoveRight) ? 1.0f : 0.0f) - (IsHeld(GameAction.MoveLeft) ? 1.0f : 0.0f),
                (IsHeld(GameAction.MoveDown) ? 1.0f : 0.0f) - (IsHeld(GameAction.MoveUp) ? 1.0f : 0.0f));

            // Normalize() は長さ0のときに NaN を返す。**入力が無い状態が最も頻繁**なので、
            // ここを踏むと毎フレーム NaN が撒かれることになる。
            return axis == Vector2.Zero ? Vector2.Zero : Vector2.Normalize(axis);
        }
    }

    /// <summary>何も入力されていない状態。</summary>
    public static InputSnapshot Empty => default;
}
