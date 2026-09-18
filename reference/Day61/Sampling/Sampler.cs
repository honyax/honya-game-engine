using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 向きの選び方(サンプリング)と、その<b>確率密度</b>(pdf)。
/// <see cref="Optics"/> と同じく、形も場面も知らない純粋な関数だけを置く。
///
/// <para>
/// モンテカルロ積分では、<b>選んだ向きの値を、その向きが選ばれる確率密度で割る</b>(要点2)。
/// だからサンプリングの関数と pdf の関数は必ず<b>対で</b>書き、
/// 自己チェック(12・13)で「作った向きの分布が、名乗っている pdf と合っているか」を数えて確かめる。
/// </para>
/// <para>
/// 立体角の pdf の単位は 1/sr(ステラジアン分の1)。半球全体の立体角は 2π sr なので、
/// 半球に一様な pdf は 1/(2π) になる。
/// </para>
/// </summary>
internal static class Sampler
{
    /// <summary>
    /// 法線 <paramref name="n"/> を Z 軸とする正規直交基底を作る(Duff ほか, 2017 の分岐なしの方法)。
    ///
    /// <para>
    /// サンプリングの式は「Z 軸が上」の座標で書くほうが短い。できた向きをこの基底で世界の向きに直す。
    /// 素朴に <c>cross(n, 上)</c> で接線を作ると、n が上に近いとき外積が 0 に潰れて向きが定まらない。
    /// ここでは n.Z の符号で式を切り替えることで、<b>どの n でも精度が落ちない</b>(論文は
    /// 「最悪でも 1 ulp」と示している)。
    /// </para>
    /// </summary>
    public static void OrthonormalBasis(Vector3 n, out Vector3 tangent, out Vector3 bitangent)
    {
        float sign = MathF.CopySign(1.0f, n.Z);
        float a = -1.0f / (sign + n.Z);
        float b = n.X * n.Y * a;
        tangent = new Vector3(1.0f + sign * n.X * n.X * a, sign * b, -sign * n.X);
        bitangent = new Vector3(b, sign + n.Y * n.Y * a, -n.Y);
    }

    /// <summary>
    /// 半球に<b>一様</b>な向き。pdf は <c>1/(2π)</c>(どの向きも同じ確率)。
    /// 今日は比べる相手としてしか使わない(要点3。自己チェック13)。
    /// </summary>
    public static Vector3 UniformHemisphere(Vector3 n, ref Rng rng)
    {
        // cosθ を 0〜1 に一様に取ると、立体角に一様になる。
        // (立体角の要素 dω = sinθ dθ dφ = -d(cosθ) dφ なので、cosθ が一様 ⇔ 立体角が一様)
        float cosTheta = rng.NextFloat();
        float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
        float phi = 2.0f * MathF.PI * rng.NextFloat();

        OrthonormalBasis(n, out Vector3 tangent, out Vector3 bitangent);
        return Vector3.Normalize(
            tangent * (sinTheta * MathF.Cos(phi)) + bitangent * (sinTheta * MathF.Sin(phi)) + n * cosTheta);
    }

    /// <summary>半球に一様な向きの pdf(立体角あたり)。</summary>
    public static float UniformHemispherePdf => 1.0f / (2.0f * MathF.PI);

    /// <summary>
    /// 半球に<b>cos で重みを付けた</b>向き(要点3)。pdf は <c>cosθ/π</c>。
    ///
    /// <para>
    /// 拡散面が跳ね返す光の量には <c>cosθ</c>(面が斜めから受ける光ほど薄まる)が掛かる。
    /// <b>掛かる重みと同じ形で向きを選べば</b>、推定量の中でその重みが約分されて消える——
    /// これが重点サンプリング(importance sampling)。結果、拡散面の散乱は
    /// <c>BRDF × cosθ / pdf = (albedo/π) × cosθ / (cosθ/π) = albedo</c> の1行になる。
    /// </para>
    /// <para>
    /// 作り方は <b>Malley の方法</b>: 円板の上に一様に点を打ち、それを半球へまっすぐ持ち上げる。
    /// 円板に一様な点の高さの分布が、ちょうど cos になる。
    /// </para>
    /// </summary>
    public static Vector3 CosineHemisphere(Vector3 n, ref Rng rng)
    {
        float u1 = rng.NextFloat();
        float u2 = rng.NextFloat();

        // 半径は √u。u をそのまま半径にすると中心に偏る(円の面積が半径の2乗に比例するため)。
        float radius = MathF.Sqrt(u1);
        float phi = 2.0f * MathF.PI * u2;
        float x = radius * MathF.Cos(phi);
        float y = radius * MathF.Sin(phi);

        // 持ち上げる高さ。x² + y² = u1 なので z = √(1 − u1) で長さ 1 になる。
        float z = MathF.Sqrt(MathF.Max(0.0f, 1.0f - u1));

        OrthonormalBasis(n, out Vector3 tangent, out Vector3 bitangent);
        return Vector3.Normalize(tangent * x + bitangent * y + n * z);
    }

    /// <summary>cos 重み付きの pdf(立体角あたり)。半球で積分すると 1 になる。</summary>
    public static float CosineHemispherePdf(float cosTheta) => MathF.Max(cosTheta, 0.0f) / MathF.PI;

    /// <summary>
    /// 軸 <paramref name="axis"/> を中心とする<b>円錐の中に一様</b>な向き(要点4)。
    /// 面光源の球へ影の光線を撃つときに使う。
    ///
    /// <para>
    /// 球の面の上に点を一様に打ってもよいが、それだと<b>裏側の点も半分選ばれて</b>、
    /// そこへ向かう光線は必ず球自身に遮られて無駄になる(分散が2倍になる)。
    /// 「球が空を覆っている円錐」の中だけから選べば、選んだ向きは必ず球に当たる。
    /// </para>
    /// </summary>
    /// <param name="cosThetaMax">円錐の半頂角の cos。1 に近いほど細い円錐。</param>
    public static Vector3 UniformCone(Vector3 axis, float cosThetaMax, ref Rng rng)
    {
        // cosθ を cosThetaMax〜1 に一様に取る(半球の一様サンプリングと同じ理屈で、これが立体角に一様)。
        float cosTheta = 1.0f - rng.NextFloat() * (1.0f - cosThetaMax);
        float sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
        float phi = 2.0f * MathF.PI * rng.NextFloat();

        OrthonormalBasis(axis, out Vector3 tangent, out Vector3 bitangent);
        return Vector3.Normalize(
            tangent * (sinTheta * MathF.Cos(phi)) + bitangent * (sinTheta * MathF.Sin(phi)) + axis * cosTheta);
    }

    /// <summary>
    /// 円錐に一様な向きの pdf(立体角あたり)。円錐の立体角は <c>2π(1 − cosθmax)</c> なので、その逆数。
    /// 球が遠い(円錐が細い)ほど pdf は大きくなり、割ったときの寄与が小さくなる——これが逆2乗則の正体。
    /// </summary>
    public static float UniformConePdf(float cosThetaMax) => 1.0f / (2.0f * MathF.PI * (1.0f - cosThetaMax));

    /// <summary>球面に一様な向き(自己チェックで使う)。pdf は <c>1/(4π)</c>。</summary>
    public static Vector3 UniformSphere(ref Rng rng)
    {
        float z = 1.0f - 2.0f * rng.NextFloat();
        float r = MathF.Sqrt(MathF.Max(0.0f, 1.0f - z * z));
        float phi = 2.0f * MathF.PI * rng.NextFloat();
        return new Vector3(r * MathF.Cos(phi), r * MathF.Sin(phi), z);
    }
}
