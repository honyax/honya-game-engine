using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 位置と向き。**構造体で、ふるまいを持たない**。
///
/// Day 22 の <see cref="Transform"/> と比べると、消えたものが多い。
///   - 親子関係が無い(要点6)
///   - クォータニオンではなく float 1個(2D なので Z 回転しか要らない)
///   - スケールが無い(このデモでは使っていない)
///   - ダーティフラグもワールド行列のキャッシュも無い
///
/// **必要なものだけを持つ**のが ECS のやり方で、汎用の Transform を全員に配らない。
/// Day 22 で「16.8 倍のうちいくらかは汎用 Transform のコストであって
/// コンポーネント方式のせいではない」と書いたが、
/// ECS ではその汎用性を捨てられる、というのがここの含み。
/// 3D が要るところには 3D 用のコンポーネントを別に作ればよい。
///
/// 12 バイト。<see cref="Velocity2D"/> と合わせて 28 バイトで、
/// **移動の計算はこれだけ触れば済む**。
/// Day 17 からの <c>Sprite</c> 構造体は 64 バイトあって、
/// 色も種類もレイヤーも一緒に引きずってキャッシュを埋めていた。
/// </summary>
internal struct Transform2D
{
    public Vector2 Position;
    public float Rotation;
}

/// <summary>
/// 前ステップの位置と向き。描画で補間するために持つ(Day 19 要点3)。
///
/// <see cref="Transform2D"/> と同じ形なのに別の型にしてあるのは、
/// **別の配列にしたい**から。移動のシステムは前の値を読まないので、
/// 混ぜて置くとキャッシュを無駄に埋めることになる。
/// 「同じ形かどうか」ではなく「いつ一緒に触るか」で分けるのが基準。
/// </summary>
internal struct Previous2D
{
    public Vector2 Position;
    public float Rotation;
}

/// <summary>
/// 速度と回転速度。
///
/// <see cref="HalfSize"/> は <see cref="Sprite2D"/> にもある大きさの写しで、
/// **わざと重複させている**(非正規化)。
/// 壁の跳ね返りに大きさが要るからで、これを持たせないと
/// 移動のたびに <see cref="Sprite2D"/> の配列まで引きに行くことになる。
/// 3本目の配列を触ると、せっかく詰めた意味が薄れる。
///
/// 代償は「大きさを変えたら両方直す」。
/// **データベースの非正規化とまったく同じ判断**で、
/// 読む回数が多くて書く回数が少ないものほど重複させる価値がある。
/// </summary>
internal struct Velocity2D
{
    public Vector2 Linear;
    public float Spin;
    public float HalfSize;
}

/// <summary>
/// 見た目。移動のシステムは触らないので、別の配列に置いておく。
///
/// フィールドの並びは意図的に「小さいもの → 大きいもの」ではなく、
/// <c>Vector4</c> を最後にしてある。
/// 28 バイトなのでどのみち詰め物は入らないが、
/// **並べ方でサイズが変わることがある**のは頭に入れておくとよい。
/// </summary>
internal struct Sprite2D
{
    public int Kind;
    public float Size;
    public float Layer;
    public Vector4 Color;
}
