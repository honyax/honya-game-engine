using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **テッセレーションの式の C# の鏡**(Day 63b)。Day 58 の <see cref="SdfScene"/> と同じ作法。
///
/// <para>
/// 割るのは固定機能の回路で、評価シェーダは<b>割った点1つにつき1回</b>走る。
/// どちらも中を覗く手段が無いので、同じ式を C# にも置いて突き合わせる。
/// 確かめたいのは3つ。
/// </para>
///
/// <list type="number">
/// <item><b>双一次補間が四隅を再現するか</b>(<c>gl_TessCoord</c> が (0,0)(1,0)(0,1)(1,1) のとき制御点と一致する)</item>
/// <item><b>変位のあとの法線が、高さの傾きと合っているか</b></item>
/// <item><b>隣り合うパッチが、共有する辺に同じレベルを出すか</b>(亀裂が出ないことの根拠)</item>
/// </list>
///
/// <para>
/// 3つ目がいちばん大事で、**亀裂は絵を見れば分かるが、「出ないこと」は絵では言えない**。
/// カメラを動かせばいつか出るかもしれない、を否定するには、
/// <b>すべての内部の辺で、両側から計算した値が一致する</b>ことを数で言うしかない。
/// </para>
/// </summary>
internal static class TessellationMath
{
    /// <summary>法線を差分で出すときの刻み(m)。**シェーダの <c>uNormalStep</c> と同じ値**。</summary>
    public const float NormalStep = 0.01f;

    /// <summary>この GL が許すレベルの上限(<c>GL_MAX_TESS_GEN_LEVEL</c>)。仕様が保証するのは 64。</summary>
    public const float MaxLevel = 64.0f;

    /// <summary>
    /// **高さの関数**。<c>tess.tese</c> の <c>height</c> と1文字ずつ同じ式。
    ///
    /// <para>
    /// 波数の違う3つの波を足しただけ。1つだと格子の目と周期が揃って
    /// 「割ったのに同じ形が並ぶ」絵になりやすい。
    /// </para>
    ///
    /// <para>
    /// <b>波長は格子の大きさに合わせてある</b>(5.7m / 2.6m / 1.26m。格子は 6m 四方)。
    /// これより細かい波を入れると、レベルを上げても形が落ち着かず、
    /// **「細かくすると滑らかになる」のが絵で読めなくなる**。
    /// </para>
    /// </summary>
    public static float Height(Vector2 p, float time)
    {
        float h = MathF.Sin((p.X * 1.1f) + time) * MathF.Cos((p.Y * 1.1f) - (time * 0.7f));
        h += 0.45f * MathF.Sin((p.X * 2.4f) + (p.Y * 1.9f) + (time * 1.3f));
        h += 0.20f * MathF.Sin((p.X * 5.0f) - (p.Y * 4.1f));
        return h;
    }

    /// <summary>四角パッチの双一次補間。**4隅の順番は (0,0) (1,0) (0,1) (1,1)**。</summary>
    public static Vector3 Bilinear(Vector3 p00, Vector3 p10, Vector3 p01, Vector3 p11, Vector2 uv) =>
        Vector3.Lerp(Vector3.Lerp(p00, p10, uv.X), Vector3.Lerp(p01, p11, uv.X), uv.Y);

    /// <summary>変位したあとの点。<c>tess.tese</c> の <c>uTopic == 0</c> の枝と同じ。</summary>
    public static Vector3 Displace(Vector3 basePosition, float time, float displacement) =>
        basePosition + (Vector3.UnitY * Height(new Vector2(basePosition.X, basePosition.Z), time) * displacement);

    /// <summary>
    /// 変位したあとの法線。<c>tess.tese</c> と同じく**高さの差分**から出す。
    ///
    /// <para>
    /// 解析的に微分すれば刻みの誤差は消えるが、**両方を同じ式にしておくほうが大事**。
    /// 式を変えたときに片方を直し忘れたら、自己チェックがすぐ落ちる
    /// (Day 58 で「同じ式が2か所にある」歪みとして書いたのと同じ話)。
    /// </para>
    /// </summary>
    public static Vector3 DisplacedNormal(Vector3 basePosition, float time, float displacement)
    {
        var p = new Vector2(basePosition.X, basePosition.Z);
        const float E = NormalStep;

        float hx = (Height(p + new Vector2(E, 0.0f), time) - Height(p - new Vector2(E, 0.0f), time)) * displacement;
        float hz = (Height(p + new Vector2(0.0f, E), time) - Height(p - new Vector2(0.0f, E), time)) * displacement;

        var tangentX = new Vector3(2.0f * E, hx, 0.0f);
        var tangentZ = new Vector3(0.0f, hz, 2.0f * E);

        return Vector3.Normalize(Vector3.Cross(tangentZ, tangentX));
    }

    /// <summary>
    /// **距離からレベルを決める式**。<c>tess.tesc</c> の <c>levelAt</c> と同じ。
    ///
    /// <para>
    /// 距離に反比例させるのは、画面に写る大きさが距離に反比例するから。
    /// 「割ったあとの三角形が、画面でだいたい同じ大きさになる」ようにしたい。
    /// </para>
    ///
    /// <para>
    /// <b>そのあと 2 の冪へ落とす</b>。カメラが少し動くたびにレベルが揺れるのを防ぐ実務の形で、
    /// 代わりに<b>隣り合うパッチのレベルが 2 倍違いうる</b>——
    /// だから「辺を隣と揃える」かどうかで絵がはっきり変わる。
    /// </para>
    /// </summary>
    public static float LevelAt(Vector3 worldPosition, Vector3 eye, float level, float lodDistance)
    {
        float distance = MathF.Max(0.001f, Vector3.Distance(eye, worldPosition));
        float raw = Math.Clamp(level * lodDistance / distance, 1.0f, MaxLevel);

        return MathF.Pow(2.0f, MathF.Floor(MathF.Log2(raw)));
    }

    /// <summary>
    /// 割り方に合わせて、レベルを「実際に使われる値」へ丸める。
    ///
    /// <para>
    /// <b>ここは GL の仕様そのもの</b>。テッセレータは渡されたレベルをそのまま使わず、
    /// 割り方ごとに決まったやり方で丸める。
    /// </para>
    /// </summary>
    public static float Round(float level, TessellationSpacing spacing) => spacing switch
    {
        // 整数へ切り上げ。1 刻みでしか変われないので、変わる瞬間に頂点が一斉に動く。
        TessellationSpacing.Equal => MathF.Ceiling(Math.Clamp(level, 1.0f, MaxLevel)),

        // **奇数へ切り上げ**。端の2本だけを伸び縮みさせて、間を埋める。
        // 1 から連続に変えられるのはこれだけ。
        TessellationSpacing.FractionalOdd => OddCeiling(Math.Clamp(level, 1.0f, MaxLevel)),

        // 偶数へ切り上げ。**レベル 1 が作れない**(最低 2)。
        _ => EvenCeiling(Math.Clamp(level, 2.0f, MaxLevel)),
    };

    private static float OddCeiling(float level)
    {
        float ceiling = MathF.Ceiling(level);
        return ceiling % 2.0f == 0.0f ? ceiling + 1.0f : ceiling;
    }

    private static float EvenCeiling(float level)
    {
        float ceiling = MathF.Ceiling(level);
        return ceiling % 2.0f == 0.0f ? ceiling : ceiling + 1.0f;
    }

    /// <summary>
    /// **等分に割ったときの、辺の上の点**(0〜1)。<paramref name="segments"/> 等分の <paramref name="index"/> 番目。
    /// 亀裂の隙間を測るのに使う。
    /// </summary>
    public static float EdgeSample(int segments, int index) => index / (float)segments;

    /// <summary>
    /// **亀裂の隙間**。同じ辺を <paramref name="a"/> 等分した点と <paramref name="b"/> 等分した点の、
    /// いちばん離れた距離(辺の長さに対する割合)。
    ///
    /// <para>
    /// 直線の辺なら点はずれても同じ線の上に乗るので隙間は開かない。
    /// <b>隙間が開くのは変位したあと</b>——辺の上の違う場所の高さを取るので、
    /// 片方が山の上、片方が谷の途中、ということが起きる。
    /// ここで返すのは「どれだけ違う場所を取っているか」で、そこに高さの差が掛かる。
    /// </para>
    /// </summary>
    public static float WorstEdgeMismatch(int a, int b)
    {
        float worst = 0.0f;

        for (int i = 0; i <= a; i++)
        {
            float t = EdgeSample(a, i);

            // b 等分の点のうち、いちばん近いものまでの距離。
            float nearest = float.MaxValue;
            for (int j = 0; j <= b; j++)
            {
                nearest = MathF.Min(nearest, MathF.Abs(t - EdgeSample(b, j)));
            }

            worst = MathF.Max(worst, nearest);
        }

        return worst;
    }
}
