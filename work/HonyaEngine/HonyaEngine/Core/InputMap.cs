using Silk.NET.Input;

namespace HonyaEngine;

/// <summary>
/// ゲームの中での「やりたいこと」。**キーそのものではない**。
///
/// ゲームのコードが <c>Key.Left</c> を直接見ると、次のことが全部できなくなる。
///   - キーコンフィグ(ユーザーがキーを変えられない)
///   - ゲームパッド対応(スティックは Key ではない)
///   - 複数割り当て(矢印キーと WASD の両方で動かす)
///   - リプレイ(記録したいのは「左に動きたかった」であって「左キー」ではない)
///
/// だから間に1枚挟む。**入力デバイス → アクション → ゲームのロジック**。
/// <see cref="InputMap"/> が前半、<see cref="InputSnapshot"/> が後半の境界になる。
///
/// <see cref="FlagsAttribute"/> にして 1bit ずつ割り当ててあるのは、
/// 「今どのアクションが有効か」を <c>uint</c> 1個で表せるようにするため。
/// 集合演算(AND / OR)で判定でき、そのまま記録もできる(<see cref="InputRecorder"/>)。
/// **32個までしか作れない**が、それを超えるならカテゴリごとに分けるべき合図。
/// </summary>
[Flags]
internal enum GameAction : uint
{
    None = 0,

    MoveLeft = 1u << 0,
    MoveRight = 1u << 1,
    MoveUp = 1u << 2,
    MoveDown = 1u << 3,

    /// <summary>押した瞬間だけ効くアクション。「押されている」では表せない(要点2)。</summary>
    Dash = 1u << 4,
}

/// <summary>
/// キーとアクションの対応表。
///
/// 1つのアクションに複数のキーを割り当てられるようにしてある。
/// 逆に1つのキーに複数のアクションを割り当てることもできる
/// (「決定」と「攻撃」が同じキー、のような場面)。
/// </summary>
internal sealed class InputMap
{
    private readonly List<(Key Key, GameAction Action)> _bindings = [];

    public IReadOnlyList<(Key Key, GameAction Action)> Bindings => _bindings;

    public void Bind(Key key, GameAction action) => _bindings.Add((key, action));

    /// <summary>
    /// キーに割り当てられたアクションを引く。割り当てが無ければ <see cref="GameAction.None"/>。
    ///
    /// 素朴な線形探索。**キーは押されている数だけしか引かれない**(1フレームに数回)ので、
    /// 辞書にする価値が無い。数十件の配列を数回なめるほうがキャッシュに乗って速い。
    /// 「とりあえず Dictionary」を選ぶ前に、呼ばれる回数を数える癖をつけたい。
    /// </summary>
    public GameAction Resolve(Key key)
    {
        GameAction result = GameAction.None;

        foreach ((Key bound, GameAction action) in _bindings)
        {
            if (bound == key)
            {
                result |= action;
            }
        }

        return result;
    }

    /// <summary>既定の割り当て。矢印キーで移動、X でダッシュ。</summary>
    public static InputMap CreateDefault()
    {
        var map = new InputMap();

        map.Bind(Key.Left, GameAction.MoveLeft);
        map.Bind(Key.Right, GameAction.MoveRight);
        map.Bind(Key.Up, GameAction.MoveUp);
        map.Bind(Key.Down, GameAction.MoveDown);
        map.Bind(Key.X, GameAction.Dash);

        // WASD も割り当てたいところだが、このデモでは
        // W(ワイヤーフレーム)・A(アトラス)・S(ソート)がデバッグ用に埋まっている。
        // **デバッグキーとゲームのキーが衝突する**のは実際によく起きる問題で、
        // 本来はデバッグ用の入力を別のマップに分け、
        // 「デバッグモードのときだけ有効」にするのが筋。

        return map;
    }
}
