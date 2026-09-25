using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **球面調和(SH)の 2 次までを CPU で扱う**(Day 66a)。今日の数学の全部。
///
/// <para>
/// 「ある点に、どの向きからどれだけの光が来ているか」は球面上の関数で、
/// そのまま持つならキューブマップ(Day 36 の放射照度マップは 32x32 x 6 面 = 6,144 画素)になる。
/// プローブを数百個並べたいので、1個あたり 6,144 画素は持てない。
/// そこで<b>9 個の数に縮める</b>。フーリエ級数で波を低い周波数だけ残すのと同じことを、球の上でやる。
/// </para>
///
/// <list type="table">
/// <item><term>0 次(1個)</term><description>向きによらない平均。「全体としてどれだけ明るいか」</description></item>
/// <item><term>1 次(3個)</term><description>x・y・z の傾き。「どちらから多く来ているか」</description></item>
/// <item><term>2 次(5個)</term><description>xy・yz・3z²-1・xz・x²-y²。「上下から来て横からは来ない」のような偏り</description></item>
/// </list>
///
/// <para>
/// <b>2 次で打ち切ってよい理由</b>が今日の理論の芯(Ramamoorthi &amp; Hanrahan 2001)。
/// 放射照度は、入ってくる光に「余弦のこぶ」(N・L)を掛けて半球で積分したもの。
/// 余弦のこぶは<b>とても滑らか</b>なので、畳み込むと高い次数がほぼ消える。
/// 次数ごとの残り方は 1 : 2/3 : 1/4 : 0 : -1/24 : 0 : 1/64 …(π で割った値)で、
/// 3 次は<b>ちょうど 0</b>、4 次は 1/24 しか残らない。だから 9 個で平均誤差 1% 程度に収まる。
/// </para>
///
/// <para>
/// 同じ式が <c>textured.frag</c>・<c>deferred.frag</c>・<c>probe.frag</c> にもある。
/// Pbr と同じく<b>わざと2か所に書いている</b>——こちらは定義と検算、シェーダは本番。
/// 基底の定数を片方だけ直すと、絵はそれらしいまま明るさの偏りだけが変わるので、
/// 自己チェックが3本のシェーダの定数を突き合わせる。
/// </para>
/// </summary>
internal static class SphericalHarmonics
{
    /// <summary>2 次までの係数の数。(2+1)² = 9。</summary>
    public const int CoefficientCount = 9;

    // --- 基底の定数(実数の球面調和。正規直交になるように決まっている)---
    //
    // 数字の出どころは Y_lm = K_lm * P_lm(cosθ) * (cos か sin)(mφ) の K と P を掛けたもの。
    // 覚えるものではなく写すもの(Sloan "Stupid SH Tricks" の付録)。

    /// <summary>0 次: 1 / (2√π)。</summary>
    public const float Band0 = 0.282095f;

    /// <summary>1 次: √3 / (2√π)。</summary>
    public const float Band1 = 0.488603f;

    /// <summary>2 次の xy・yz・xz: √15 / (2√π)。</summary>
    public const float Band2 = 1.092548f;

    /// <summary>2 次の 3z²-1: √5 / (4√π)。</summary>
    public const float Band2Zonal = 0.315392f;

    /// <summary>2 次の x²-y²: √15 / (4√π)。</summary>
    public const float Band2Sectoral = 0.546274f;

    /// <summary>
    /// **余弦のこぶを畳み込んだあとに、次数ごとに残る割合**(π で割ったもの)。
    ///
    /// <para>
    /// 元の値は Â0 = π、Â1 = 2π/3、Â2 = π/4。<b>π で割って持つ</b>のは、
    /// Day 36 の放射照度マップが 1/π を焼き込んである(irradiance.frag)のと揃えるため。
    /// そうしておけばシェーダ側は <c>irradiance * albedo</c> の1行が IBL とプローブで同じになる。
    /// </para>
    /// </summary>
    public static float BandFactor(int coefficient) => coefficient switch
    {
        0 => 1.0f,
        < 4 => 2.0f / 3.0f,
        _ => 0.25f,
    };

    /// <summary>
    /// 向き <paramref name="n"/>(長さ 1)での9つの基底の値。**シェーダの <c>ShBasis</c> と同じ並び**。
    /// </summary>
    public static void Basis(Vector3 n, Span<float> y)
    {
        y[0] = Band0;
        y[1] = Band1 * n.Y;
        y[2] = Band1 * n.Z;
        y[3] = Band1 * n.X;
        y[4] = Band2 * n.X * n.Y;
        y[5] = Band2 * n.Y * n.Z;
        y[6] = Band2Zonal * ((3.0f * n.Z * n.Z) - 1.0f);
        y[7] = Band2 * n.X * n.Z;
        y[8] = Band2Sectoral * ((n.X * n.X) - (n.Y * n.Y));
    }

    /// <summary>
    /// **キューブマップのテクセルが向いている方向**。OpenGL の仕様の表(Table 8.19)そのまま。
    ///
    /// <para>
    /// Day 36 の <c>Program.CubeTexelDirection</c> と同じ式。あちらは自己チェックの側に置いてあり、
    /// エンジンの側(ここ)からは呼べないので写してある。
    /// <b>読み返した配列の行番号がそのまま t</b>(0 が下の行)——<c>glReadPixels</c> で
    /// 2D のフレームバッファから読んでも、<c>glGetTexImage</c> でキューブの面から読んでも同じ並びになる。
    /// </para>
    /// </summary>
    /// <param name="face">面の番号(+X, -X, +Y, -Y, +Z, -Z の順)。</param>
    /// <param name="s">面の中の横位置(0〜1)。</param>
    /// <param name="t">面の中の縦位置(0〜1)。</param>
    public static Vector3 CubeTexelDirection(int face, float s, float t)
    {
        float u = (2.0f * s) - 1.0f;
        float v = (2.0f * t) - 1.0f;

        Vector3 direction = face switch
        {
            0 => new Vector3(1.0f, -v, -u),
            1 => new Vector3(-1.0f, -v, u),
            2 => new Vector3(u, 1.0f, v),
            3 => new Vector3(u, -1.0f, -v),
            4 => new Vector3(u, -v, 1.0f),
            _ => new Vector3(-u, -v, -1.0f),
        };

        return Vector3.Normalize(direction);
    }

    /// <summary>
    /// **キューブの1テクセルが占める立体角**(厳密な式)。
    ///
    /// <para>
    /// 面の真ん中のテクセルは正面から見ているので大きく、隅のテクセルは斜めから見ているので小さい。
    /// 32x32 の面なら<b>真ん中と隅で 4.9 倍</b>違う。等しい重みで足すと、隅(= 立方体の角の向き)を
    /// 5 倍近く重く数えることになり、<b>面の境目の向きから来る光を大きく見積もる</b>。
    /// </para>
    ///
    /// <para>
    /// 式は「原点から見た、平面上の長方形 [0,x]×[0,y] の立体角」<c>atan2(xy, √(x²+y²+1))</c> を
    /// 4 隅で足し引きしたもの。近似式 <c>4/(n²(1+u²+v²)^1.5)</c> も広く使われるが、
    /// こちらなら6面ぶん足すと<b>ちょうど 4π</b> になる(自己チェック)。
    /// </para>
    /// </summary>
    public static float TexelSolidAngle(int x, int y, int size)
    {
        float inverse = 1.0f / size;

        // テクセルの4隅を -1〜1 の座標で。
        float x0 = (2.0f * x * inverse) - 1.0f;
        float y0 = (2.0f * y * inverse) - 1.0f;
        float x1 = (2.0f * (x + 1) * inverse) - 1.0f;
        float y1 = (2.0f * (y + 1) * inverse) - 1.0f;

        return AreaElement(x0, y0) - AreaElement(x0, y1) - AreaElement(x1, y0) + AreaElement(x1, y1);
    }

    private static float AreaElement(float x, float y) =>
        MathF.Atan2(x * y, MathF.Sqrt((x * x) + (y * y) + 1.0f));

    /// <summary>
    /// **キューブの1面を SH へ射影して、係数に足し込む**。
    ///
    /// <para>
    /// 射影は「その基底とどれだけ似ているか」を球の上で積分すること:
    /// <c>L_i = ∫ L(ω) Y_i(ω) dω</c>。テクセルごとに <c>明るさ × 基底 × 立体角</c> を足すだけ。
    /// 6面ぶん足し終えたものが、その点に来ている光(放射輝度)の SH 係数になる。
    /// </para>
    /// </summary>
    /// <param name="pixels">面の画素(RGBA の float。行 0 が下)。</param>
    /// <param name="stride">1画素あたりの float の数(RGBA なら 4)。</param>
    /// <param name="size">面の1辺の画素数。</param>
    /// <param name="face">面の番号。</param>
    /// <param name="coefficients">足し込む先(9 個)。</param>
    public static void ProjectFace(
        ReadOnlySpan<float> pixels, int stride, int size, int face, Span<Vector3> coefficients)
    {
        Span<float> basis = stackalloc float[CoefficientCount];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = ((y * size) + x) * stride;
                var radiance = new Vector3(pixels[index], pixels[index + 1], pixels[index + 2]);

                Vector3 direction = CubeTexelDirection(face, (x + 0.5f) / size, (y + 0.5f) / size);
                float solidAngle = TexelSolidAngle(x, y, size);

                Basis(direction, basis);

                for (int i = 0; i < CoefficientCount; i++)
                {
                    coefficients[i] += radiance * (basis[i] * solidAngle);
                }
            }
        }
    }

    /// <summary>
    /// **放射輝度の係数を、放射照度の係数へ**。次数ごとに <see cref="BandFactor"/> を掛けるだけ。
    ///
    /// <para>
    /// 余弦のこぶとの畳み込みが<b>次数ごとの掛け算</b>で済むのが SH のいちばんの御利益
    /// (Funk-Hecke の定理)。Day 36 では同じことを画素ごとに半球を刻んで積分していた
    /// (irradiance.frag。0.025 ラジアン刻みで、1 テクセルあたり 1 万 6 千回ほどの読み)。ここでは9回の掛け算になる。
    /// </para>
    /// </summary>
    public static void ToIrradiance(Span<Vector3> coefficients)
    {
        for (int i = 0; i < CoefficientCount; i++)
        {
            coefficients[i] *= BandFactor(i);
        }
    }

    /// <summary>
    /// 係数を向き <paramref name="n"/> で評価する。放射照度の係数を渡せば<b>その向きの面が受ける光 / π</b>。
    ///
    /// <para>
    /// <b>負になりうる</b>。2 次で打ち切ると、急な明暗の境目(空と地面の境など)の反対側で
    /// 波が 0 の下へはみ出す(リンギング)。シェーダは 0 で止める。ここは止めずに返す——
    /// 自己チェックがはみ出しの大きさを測るため。
    /// </para>
    /// </summary>
    public static Vector3 Evaluate(ReadOnlySpan<Vector3> coefficients, Vector3 n)
    {
        Span<float> basis = stackalloc float[CoefficientCount];
        Basis(n, basis);

        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < CoefficientCount; i++)
        {
            sum += coefficients[i] * basis[i];
        }

        return sum;
    }

    /// <summary>
    /// **関数を直接 SH へ射影する**(自己チェック用)。キューブの画素の代わりに、向きから明るさを返す関数を渡す。
    /// 刻みは <see cref="ProjectFace"/> と同じ(面ごとに <paramref name="size"/>²)。
    /// </summary>
    public static Vector3[] ProjectFunction(Func<Vector3, Vector3> radiance, int size)
    {
        var coefficients = new Vector3[CoefficientCount];
        Span<float> basis = stackalloc float[CoefficientCount];

        for (int face = 0; face < 6; face++)
        {
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector3 direction = CubeTexelDirection(face, (x + 0.5f) / size, (y + 0.5f) / size);
                    float solidAngle = TexelSolidAngle(x, y, size);
                    Vector3 value = radiance(direction);

                    Basis(direction, basis);

                    for (int i = 0; i < CoefficientCount; i++)
                    {
                        coefficients[i] += value * (basis[i] * solidAngle);
                    }
                }
            }
        }

        return coefficients;
    }
}
