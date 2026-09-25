using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **符号付き距離関数(SDF)の部品**(Day 58)。<c>raymarch.frag</c> の同じ名前の関数と1行ずつ対応する。
///
/// <para>
/// SDF は「点 p を渡すと、いちばん近い面までの距離を返す関数」。<b>外なら正、中なら負、面の上で 0</b>。
/// メッシュが「面を三角形の並びで持つ」のに対して、こちらは<b>面を式で持つ</b>——
/// 頂点も索引も無く、形の置き場所はこの関数の中にしかない。
/// </para>
///
/// <para>
/// <b>なぜ C# にも同じものがあるのか</b>。シェーダの中で起きたことは、画素の色からしか覗けない。
/// 同じ式を C# に写しておくと、「この点の距離はいくつか」「傾きは 1 を超えていないか」を
/// 数字で確かめられる(自己チェック)。Day 57 の <see cref="GpuParticles.StepFountainOnCpu"/> と同じ役回りで、
/// <b>2か所に同じ式がある</b>のは今日残した歪みの1つ目になる。
/// </para>
///
/// <para>
/// 式はどれも Inigo Quilez の記事(<c>iquilezles.org/articles/distfunctions/</c>)の形そのまま。
/// </para>
/// </summary>
internal static class Sdf
{
    /// <summary>
    /// 球。**いちばん素直な SDF**——中心からの距離から半径を引くだけ。
    ///
    /// <para>
    /// これが「本当の距離」になっているのは、球の面でいちばん近い点が
    /// 必ず「中心と p を結んだ線の上」にあるから。
    /// </para>
    /// </summary>
    public static float Sphere(Vector3 p, float radius) => p.Length() - radius;

    /// <summary>
    /// 箱(中心が原点、各軸の半分の大きさ <paramref name="halfSize"/>)。
    ///
    /// <para>
    /// <c>q = |p| - b</c> で、8つの象限を1つに畳んでから考える(箱は軸ごとに左右対称なので)。
    /// q の成分が全部負なら中、どれか正なら外。
    /// </para>
    /// <list type="bullet">
    /// <item><b>外</b>: 正の成分だけを残した長さ(<c>length(max(q, 0))</c>)。面・辺・角のどこが近くても1本の式で済む</item>
    /// <item><b>中</b>: いちばん近い面までの距離(<c>max(q.x, q.y, q.z)</c>。どれも負なので、いちばん 0 に近いもの)</item>
    /// </list>
    /// </summary>
    public static float Box(Vector3 p, Vector3 halfSize)
    {
        Vector3 q = Vector3.Abs(p) - halfSize;
        float outside = Vector3.Max(q, Vector3.Zero).Length();
        float inside = MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0.0f);
        return outside + inside;
    }

    /// <summary>
    /// 角の丸い箱。**箱を縮めてから、距離から半径を引く**。
    ///
    /// <para>
    /// SDF の「膨らませる」はこの1行(<c>- radius</c>)で済む——等高線を1本内側へずらすだけだから。
    /// メッシュで角を丸めるには頂点を足すしかなく、ここが式で持つことのいちばん分かりやすい値打ち。
    /// </para>
    /// </summary>
    public static float RoundBox(Vector3 p, Vector3 halfSize, float radius) =>
        Box(p, halfSize - new Vector3(radius)) - radius;

    /// <summary>
    /// 輪(トーラス)。xz 平面に寝ている。
    ///
    /// <para>
    /// 「中心の円(半径 <paramref name="majorRadius"/>)からの距離」を2次元で出して、そこから管の太さを引く。
    /// 3次元の形を<b>2次元の距離に落としてから球と同じことをする</b>という、SDF でよく出てくる組み立て方。
    /// </para>
    /// </summary>
    public static float Torus(Vector3 p, float majorRadius, float minorRadius)
    {
        var q = new Vector2(new Vector2(p.X, p.Z).Length() - majorRadius, p.Y);
        return q.Length() - minorRadius;
    }

    /// <summary>
    /// 縦のカプセル(y = 0 から y = <paramref name="height"/> までの線分を、半径 <paramref name="radius"/> で太らせたもの)。
    ///
    /// <para>
    /// 線分の上でいちばん近い点を <c>clamp</c> で求め、そこからの距離を半径で引く。
    /// Day 45 のキャラクターのカプセル(<see cref="Collision3D"/>)と、やっていることはまったく同じ。
    /// </para>
    /// </summary>
    public static float VerticalCapsule(Vector3 p, float height, float radius)
    {
        p.Y -= Math.Clamp(p.Y, 0.0f, height);
        return p.Length() - radius;
    }

    /// <summary>
    /// **滑らかな和**(smooth minimum。2次の多項式版)。<paramref name="k"/> は「混ぜ始める距離」。
    ///
    /// <para>
    /// ただの <c>min</c> は2つの形の境目に折れ目が残る。距離の差が k より小さいところでだけ、
    /// <b>min から少し引いて</b>つなぎ目を膨らませる。引く量は差が 0 のときに最大(k/4)で、差が k で 0 になる。
    /// </para>
    ///
    /// <para>
    /// <b>引いているので、返る距離は本当の距離より短い</b>(自己チェックの2項目め)。
    /// 短く見積もるぶんには安全で、光線が少し余計に歩くだけで済む。
    /// </para>
    /// </summary>
    public static float SmoothMin(float a, float b, float k)
    {
        float h = MathF.Max(k - MathF.Abs(a - b), 0.0f) / k;
        return MathF.Min(a, b) - (h * h * k * 0.25f);
    }

    /// <summary>
    /// 点を y 軸まわりに回す(xz 平面の回転)。
    ///
    /// <para>
    /// <b>形を +θ 回したいときは、点を −θ 回して渡す</b>。SDF は「点に聞く」関数なので、
    /// 形を動かすかわりに<b>聞く側の座標を逆に動かす</b>ことになる。
    /// メッシュならモデル行列を頂点に掛けるところ、SDF では逆行列を点に掛ける——
    /// 向きが逆になるのは、ここがいちばんよく間違えるところ。
    /// </para>
    /// </summary>
    public static Vector3 RotateY(Vector3 p, float angle)
    {
        float c = MathF.Cos(angle);
        float s = MathF.Sin(angle);
        return new Vector3((c * p.X) - (s * p.Z), p.Y, (s * p.X) + (c * p.Z));
    }

    /// <summary>
    /// **y の高さに比例して回す**(ねじれ)。<paramref name="rate"/> は 1m あたりの角度(ラジアン)。
    ///
    /// <para>
    /// 回転そのものは距離を変えない(剛体の動き)が、<b>高さごとに回す角度が違う</b>と空間が伸び縮みする。
    /// 軸から r 離れた点を y 方向に 1m 動かすと、ねじれた側では横に <c>rate × r</c> 余分に動いたことになる。
    /// その結果、返る「距離」が本当の距離より<b>長く</b>なりうる——今日の要点5。
    /// </para>
    /// </summary>
    public static Vector3 TwistY(Vector3 p, float rate) => RotateY(p, rate * p.Y);

    /// <summary>
    /// **xz 平面で無限に繰り返す**。点を「いちばん近い升目の中心から見た座標」に畳む。
    ///
    /// <para>
    /// 1個の形の式に畳んだ点を渡すだけで、形が無限に並ぶ。<b>手間は何個見えていても1個ぶん</b>。
    /// ただし正しい距離になるのは、形が升目の半分に収まっていて、升目の境目に対して左右対称なときだけ
    /// (そうでないと、隣の升目の形のほうが近いのに自分の升目の形までの距離を返してしまう)。
    /// </para>
    ///
    /// <para>
    /// 丸めは <c>floor(x + 0.5)</c> で書く。GLSL の <c>round</c> は「ちょうど半分」の向きを実装に任せているので、
    /// C# の <c>MathF.Round</c>(偶数へ丸める)と食い違いうる。<b>両方で同じ書き方をする</b>ほうが確実。
    /// </para>
    /// </summary>
    public static Vector3 RepeatXZ(Vector3 p, float cellSize) =>
        new(
            p.X - (cellSize * MathF.Floor((p.X / cellSize) + 0.5f)),
            p.Y,
            p.Z - (cellSize * MathF.Floor((p.Z / cellSize) + 0.5f)));
}
