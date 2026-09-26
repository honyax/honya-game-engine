using System.Numerics;

namespace GaussianSplatting;

/// <summary>
/// 球面調和(Spherical Harmonics)。<b>見る向きによって変わる色</b>を、16 個の係数で持つ仕組み(要点5)。
///
/// <para>
/// Day 66a では、放射照度(ある面に四方から届く光)を次数 2 の 9 個の係数で持った。
/// 今日は同じ道具で、<b>楕円体が各方向へ出している色</b>を持つ。向きの関数を球面調和で近似するところは同じで、
/// 違うのは「向きが光の来る向きか、見る人の向きか」だけ。
/// </para>
/// <para>
/// 基底の式・定数・符号は、元論文の実装(graphdeco-inria/gaussian-splatting の <c>utils/sh_utils.py</c> と
/// CUDA のラスタライザ <c>computeColorFromSH</c>)と<b>1文字も違わないように</b>写してある。
/// 学習済みの .ply の係数はこの基底で学習されているので、符号が1つ違っても絵がおかしくなる
/// (自己チェック5で、16 個の基底が互いに直交し、長さ 1 であることを確かめている)。
/// </para>
/// </summary>
internal static class SphericalHarmonics
{
    public const int MaxDegree = 3;

    public const int MaxCoefficients = 16;

    // 定数は元論文の実装と同じ値。Y_l^m の正規化の係数(√((2l+1)/4π · (l−|m|)!/(l+|m|)!) に、式の中の係数を掛けたもの)。
    public const float C0 = 0.28209479177387814f;

    public const float C1 = 0.4886025119029199f;

    private static readonly float[] C2 =
    [
        1.0925484305920792f,
        -1.0925484305920792f,
        0.31539156525252005f,
        -1.0925484305920792f,
        0.5462742152960396f,
    ];

    private static readonly float[] C3 =
    [
        -0.5900435899266435f,
        2.890611442640554f,
        -0.4570457994644658f,
        0.3731763325901154f,
        -0.4570457994644658f,
        1.445305721320277f,
        -0.5900435899266435f,
    ];

    /// <summary>次数 d までの係数の個数 (d + 1)²。</summary>
    public static int CoefficientCount(int degree) => (degree + 1) * (degree + 1);

    /// <summary>
    /// 向き <paramref name="d"/>(長さ 1)での 16 個の基底の値。<paramref name="basis"/> の先頭 (次数 + 1)² 個を埋める。
    ///
    /// <para>
    /// <b>向きは「カメラから楕円体へ」</b>(元論文の実装が <c>dir = pos − campos</c> なので)。
    /// 「楕円体から目へ」と思って係数を作ると、表と裏が入れ替わった色になる。
    /// </para>
    /// </summary>
    public static void Basis(Vector3 d, int degree, Span<float> basis)
    {
        float x = d.X, y = d.Y, z = d.Z;

        basis[0] = C0;
        if (degree < 1)
        {
            return;
        }

        basis[1] = -C1 * y;
        basis[2] = C1 * z;
        basis[3] = -C1 * x;
        if (degree < 2)
        {
            return;
        }

        float xx = x * x, yy = y * y, zz = z * z;
        float xy = x * y, yz = y * z, xz = x * z;
        basis[4] = C2[0] * xy;
        basis[5] = C2[1] * yz;
        basis[6] = C2[2] * (2.0f * zz - xx - yy);
        basis[7] = C2[3] * xz;
        basis[8] = C2[4] * (xx - yy);
        if (degree < 3)
        {
            return;
        }

        basis[9] = C3[0] * y * (3.0f * xx - yy);
        basis[10] = C3[1] * xy * z;
        basis[11] = C3[2] * y * (4.0f * zz - xx - yy);
        basis[12] = C3[3] * z * (2.0f * zz - 3.0f * xx - 3.0f * yy);
        basis[13] = C3[4] * x * (4.0f * zz - xx - yy);
        basis[14] = C3[5] * z * (xx - yy);
        basis[15] = C3[6] * x * (xx - 3.0f * yy);
    }

    /// <summary>
    /// 係数の並び(RGB が <paramref name="count"/> 組)を、向き <paramref name="direction"/>(カメラ → 楕円体、長さ 1)で評価した色。
    ///
    /// <para>
    /// <b>0.5 を足して、0 未満を切る</b>のも元論文のとおり。0.5 を足すのは、係数がすべて 0 のときに灰色(0.5)になるようにするため
    /// (学習の出発点が灰色になる)。切るのは、近似の波打ちで負の色が出るのを防ぐため(要点5)。
    /// </para>
    /// </summary>
    public static Vector3 Evaluate(ReadOnlySpan<float> coefficients, int degree, Vector3 direction)
    {
        Span<float> basis = stackalloc float[MaxCoefficients];
        Basis(direction, degree, basis);

        Vector3 color = new(0.5f);
        int count = CoefficientCount(degree);
        for (int k = 0; k < count; k++)
        {
            color += basis[k] * new Vector3(coefficients[k * 3], coefficients[k * 3 + 1], coefficients[k * 3 + 2]);
        }

        return Vector3.Max(color, Vector3.Zero);
    }

    /// <summary>
    /// <b>軸の周りに対称な山</b> <c>f(t) = max(0, t)^n</c>(t は軸と向きの cos)を、次数 3 までの係数にする(自前の場面3で使う)。
    ///
    /// <para>
    /// 軸の周りに対称な関数は、各次数 l につき<b>1つの数 z_l</b> で決まる(帯調和、zonal harmonics)。
    /// それを好きな向きへ回した係数は <c>c_lm = √(4π/(2l+1)) · z_l · Y_lm(軸)</c> で出せる
    /// (Sloan「Stupid Spherical Harmonics Tricks」の回転の式。Day 66a の資料)。
    /// 楕円体ごとに山の向きが違っても、z_l は1回計算すれば済む。
    /// </para>
    /// <para>
    /// 戻り値は l = 0〜3 の <c>√(4π/(2l+1)) · z_l</c>。これに <see cref="Basis"/>(軸) を掛ければ、その向きの係数になる。
    /// </para>
    /// </summary>
    public static float[] ZonalLobe(float exponent)
    {
        // z_l = ∫ f(ω) Y_l0(ω) dω = 2π ∫_{-1}^{1} f(t) Y_l0(t) dt。t について中点で数値積分する。
        const int Steps = 4000;
        var result = new float[MaxDegree + 1];
        for (int l = 0; l <= MaxDegree; l++)
        {
            double sum = 0.0;
            for (int s = 0; s < Steps; s++)
            {
                double t = -1.0 + (s + 0.5) * 2.0 / Steps;
                double f = t > 0.0 ? Math.Pow(t, exponent) : 0.0;
                sum += f * ZonalBasis(l, t);
            }

            double z = 2.0 * Math.PI * sum * (2.0 / Steps);
            result[l] = (float)(Math.Sqrt(4.0 * Math.PI / (2 * l + 1)) * z);
        }

        return result;
    }

    /// <summary>
    /// 帯調和 Y_l0(t)。<see cref="Basis"/> の m = 0 の基底を、軸を z に取って t = z で書いたもの
    /// (0番・2番・6番・12番の基底と同じ式)。
    /// </summary>
    private static double ZonalBasis(int l, double t) => l switch
    {
        0 => C0,
        1 => C1 * t,
        2 => C2[2] * (3.0 * t * t - 1.0),
        _ => C3[3] * t * (5.0 * t * t - 3.0),
    };

    /// <summary>基底の番号 k が何次か(0番は 0 次、1〜3番は 1 次、4〜8番は 2 次、9〜15番は 3 次)。</summary>
    public static int DegreeOf(int k) => k < 1 ? 0 : k < 4 ? 1 : k < 9 ? 2 : 3;
}
