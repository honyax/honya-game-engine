using System.Numerics;

namespace GaussianSplatting;

/// <summary>
/// 楕円体1個を画面に写した結果。<b>画面の上の2D のガウス(楕円)</b>。
/// </summary>
/// <param name="Center">楕円の中心。画面の真ん中からの画素(x は右、y は上)。</param>
/// <param name="Depth">カメラの前向きの距離(m)。並べ替えに使う。</param>
/// <param name="A">2D の共分散の xx。</param>
/// <param name="B">2D の共分散の xy。</param>
/// <param name="C">2D の共分散の yy。</param>
/// <param name="Conic">2D の共分散の逆行列(xx, xy, yy)。画素ごとの濃さの計算に使う。</param>
/// <param name="Radius">楕円を包む正方形の半分の辺(画素)。長い軸の 3σ。</param>
internal readonly record struct ProjectedSplat(Vector2 Center, float Depth, float A, float B, float C, Vector3 Conic, float Radius)
{
    /// <summary>
    /// 中心から <paramref name="offset"/>(画素)だけ離れた点での濃さ(0〜0.99)。
    /// <b>GPU の画素シェーダ(<c>shaders/splat.frag</c>)と同じ式</b>(要点3)。
    ///
    /// <para>
    /// <c>exp(−½ dᵀ Σ'⁻¹ d)</c> がガウス関数。Σ'⁻¹(<see cref="Conic"/>)を前もって作っておくのは、
    /// 画素ごとに逆行列を計算し直さないため。
    /// </para>
    /// </summary>
    public float AlphaAt(Vector2 offset, float opacity)
    {
        float power = -0.5f * (Conic.X * offset.X * offset.X + Conic.Z * offset.Y * offset.Y) - Conic.Y * offset.X * offset.Y;
        if (power > 0.0f)
        {
            // 数値の誤差で逆行列が正定値でなくなったときの保険(元論文の実装と同じ)。
            return 0.0f;
        }

        // 0.99 で頭打ちにするのも元論文のとおり。1 にすると、それより奥が完全に消えて、
        // 学習のときに奥へ勾配が届かなくなる(ここでは描くだけだが、学習した値と同じ式で描かないと絵が変わる)。
        float alpha = MathF.Min(0.99f, opacity * MathF.Exp(power));
        return alpha < SplatProjection.MinAlpha ? 0.0f : alpha;
    }
}

/// <summary>
/// 楕円体を画面へ写す。<b>今日の理論の中心</b>(要点2)。GPU の頂点シェーダ(<c>shaders/splat.vert</c>)と
/// <b>同じ計算を CPU でもう一度書いたもの</b>で、右クリックの「その画素に効いている楕円体」(<see cref="PixelProbe"/>)と
/// 自己チェックが使う。
/// </summary>
internal static class SplatProjection
{
    /// <summary>
    /// 2D の共分散の対角に足す値(画素²)。<b>「0.3 の広げ」</b>(要点4)。
    /// 楕円がどんなに小さくても σ が √0.3 ≒ 0.55 画素より細くならないようにする。
    /// </summary>
    public const float Dilation = 0.3f;

    /// <summary>これより薄い画素は塗らない(1/255。8 ビットの色で見えない濃さ)。</summary>
    public const float MinAlpha = 1.0f / 255.0f;

    /// <summary>
    /// 画面の外へ大きくはみ出した楕円体でヤコビアンが暴れないように、x/z と y/z を画角の 1.3 倍で止める(元論文の実装と同じ)。
    /// </summary>
    public const float FrustumLimit = 1.3f;

    /// <summary>
    /// 1個を写す。カメラの後ろ(近すぎる)と、潰れて面積の無い楕円は <c>null</c>。
    ///
    /// <para>
    /// <b>やっていること</b>: Σ' = J W Σ Wᵀ Jᵀ(元論文の式 (5)。Zwicker ほか 2001 の EWA splatting)。
    /// </para>
    /// <list type="bullet">
    /// <item>W はワールド → カメラの回転。行が「右・上・前」の3本</item>
    /// <item>J は<b>透視投影(z で割る)を、楕円体の中心のまわりで1次式に近似したもの</b>(ヤコビアン)。
    /// 透視投影は線形ではないので、楕円体を写した形は厳密には楕円にならない。中心の近くで接線を引いて「楕円ということにする」</item>
    /// </list>
    /// </summary>
    public static ProjectedSplat? Project(Vector3 position, Covariance3 covariance, in CameraFrame camera, bool dilate)
    {
        Vector3 t = camera.ToView(position);
        if (t.Z < CameraFrame.Near)
        {
            return null;
        }

        float z = t.Z;

        // x/z と y/z を画角の 1.3 倍で止める。止めるのは J の中だけで、中心の位置は止めない。
        float limX = FrustumLimit * camera.TanHalfX;
        float limY = FrustumLimit * camera.TanHalfY;
        float tx = Math.Clamp(t.X / z, -limX, limX) * z;
        float ty = Math.Clamp(t.Y / z, -limY, limY) * z;

        // u = Fx · x / z を x と z で微分すると、∂u/∂x = Fx / z、∂u/∂z = −Fx · x / z²。v も同じ。
        //     J = | Fx/z   0     −Fx·x/z² |
        //         | 0      Fy/z  −Fy·y/z² |
        // W の行(右・上・前)と掛けておくと、T = J W の2本の行がワールドの向きとして出る。
        Vector3 row0 = (camera.Fx / z) * camera.Right + (-camera.Fx * tx / (z * z)) * camera.Forward;
        Vector3 row1 = (camera.Fy / z) * camera.Up + (-camera.Fy * ty / (z * z)) * camera.Forward;

        // Σ' = T Σ Tᵀ。2x2 の対称行列なので3つだけ計算する。
        float a = covariance.Sandwich(row0, row0);
        float b = covariance.Sandwich(row0, row1);
        float c = covariance.Sandwich(row1, row1);

        if (dilate)
        {
            a += Dilation;
            c += Dilation;
        }

        float det = a * c - b * b;
        if (det <= 0.0f)
        {
            return null;
        }

        // 逆行列 | c −b; −b a | / det。
        var conic = new Vector3(c / det, -b / det, a / det);

        // 長い軸の長さ = 大きいほうの固有値の平方根。その3倍(3σ)で四角形の大きさを決める。
        // 3σ の外は exp(−4.5) ≒ 0.011 で、不透明度を掛けると 1/255 をほぼ下回る。
        float mid = 0.5f * (a + c);
        float lambda1 = mid + MathF.Sqrt(MathF.Max(0.1f, mid * mid - det));
        float radius = MathF.Ceiling(3.0f * MathF.Sqrt(lambda1));

        var center = new Vector2(camera.Fx * t.X / z, camera.Fy * t.Y / z);
        return new ProjectedSplat(center, z, a, b, c, conic, radius);
    }
}
