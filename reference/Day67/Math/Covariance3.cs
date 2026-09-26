using System.Numerics;

namespace GaussianSplatting;

/// <summary>
/// 3D の共分散行列 Σ(3x3 の対称行列)。<b>楕円体の形そのもの</b>(要点1)。
///
/// <para>
/// 対称なので、独立な成分は6つ(xx, xy, xz, yy, yz, zz)しか無い。GPU にもこの6つだけを送る。
/// </para>
/// </summary>
internal readonly record struct Covariance3(float Xx, float Xy, float Xz, float Yy, float Yz, float Zz)
{
    /// <summary>
    /// 大きさ(3つの軸の σ)と回転から Σ を作る。<b>Σ = R S Sᵀ Rᵀ</b>(元論文の式 (6))。
    ///
    /// <para>
    /// 読み方: 半径 1 の球(共分散が単位行列のガウス)を、まず軸ごとに σ 倍に伸ばし(S)、それから回す(R)。
    /// 伸ばして回した形の共分散が R S Sᵀ Rᵀ になる。
    /// </para>
    /// <para>
    /// <b>なぜ Σ を直接学習しないのか</b>。共分散は「正定値」(どの向きにも長さが正)でないと楕円体にならないが、
    /// 6つの数を好きに動かすと、すぐにこの条件を破る(負の長さの軸ができる)。大きさと回転に分けておけば、
    /// 大きさを exp で正に保つだけで、どう動かしても必ず楕円体のままでいられる。
    /// </para>
    /// <para>
    /// 回した軸 a_k(回転行列の k 列目)を使うと、<c>Σ = Σ_k σ_k² a_k a_kᵀ</c> と書ける。
    /// 3本の軸それぞれの向きに、その長さの2乗ぶんだけ広がっている、という意味。
    /// </para>
    /// </summary>
    public static Covariance3 FromScaleRotation(Vector3 scale, Quaternion rotation)
    {
        // System.Numerics の行列は「行ベクトルに右から掛ける」流儀なので、k 行目が「k 番目の軸を回した向き」になる。
        Matrix4x4 r = Matrix4x4.CreateFromQuaternion(rotation);
        Vector3 a0 = new(r.M11, r.M12, r.M13);
        Vector3 a1 = new(r.M21, r.M22, r.M23);
        Vector3 a2 = new(r.M31, r.M32, r.M33);

        float s0 = scale.X * scale.X;
        float s1 = scale.Y * scale.Y;
        float s2 = scale.Z * scale.Z;

        return new Covariance3(
            s0 * a0.X * a0.X + s1 * a1.X * a1.X + s2 * a2.X * a2.X,
            s0 * a0.X * a0.Y + s1 * a1.X * a1.Y + s2 * a2.X * a2.Y,
            s0 * a0.X * a0.Z + s1 * a1.X * a1.Z + s2 * a2.X * a2.Z,
            s0 * a0.Y * a0.Y + s1 * a1.Y * a1.Y + s2 * a2.Y * a2.Y,
            s0 * a0.Y * a0.Z + s1 * a1.Y * a1.Z + s2 * a2.Y * a2.Z,
            s0 * a0.Z * a0.Z + s1 * a1.Z * a1.Z + s2 * a2.Z * a2.Z);
    }

    /// <summary>
    /// ベクトル u と v で挟んだ値 uᵀ Σ v。Σ を画面へ写すとき(要点2)に、行ごとに使う。
    /// </summary>
    public float Sandwich(Vector3 u, Vector3 v)
    {
        // Σ v を先に作ってから u と内積を取る。
        Vector3 sv = new(
            Xx * v.X + Xy * v.Y + Xz * v.Z,
            Xy * v.X + Yy * v.Y + Yz * v.Z,
            Xz * v.X + Yz * v.Y + Zz * v.Z);
        return Vector3.Dot(u, sv);
    }

    /// <summary>全体を k 倍する(楕円体の大きさを √k 倍にしたのと同じ)。</summary>
    public Covariance3 Scaled(float k) => new(Xx * k, Xy * k, Xz * k, Yy * k, Yz * k, Zz * k);
}
