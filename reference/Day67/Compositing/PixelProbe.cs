using System.Numerics;

namespace GaussianSplatting;

/// <summary>1画素に効いている楕円体1個ぶん。</summary>
/// <param name="Index">楕円体の番号。</param>
/// <param name="Depth">奥行き(m)。</param>
/// <param name="Alpha">この画素での濃さ(0〜0.99)。</param>
/// <param name="Transmittance">この楕円体より手前の全部を通り抜けて、ここまで届いた割合(残りの透明度)。</param>
/// <param name="Color">この楕円体の色(この向きから見た色)。</param>
internal readonly record struct Contribution(int Index, float Depth, float Alpha, float Transmittance, Vector3 Color)
{
    /// <summary>画素の色への寄与 = 色 × 濃さ × 残りの透明度。</summary>
    public Vector3 Weighted => Color * (Alpha * Transmittance);
}

/// <summary>1画素を CPU で重ね直した結果。</summary>
/// <param name="Covered">その画素に四角形が掛かっていた楕円体の数 = <b>GPU の画素シェーダがその画素で走った回数</b>。</param>
/// <param name="Contributions">実際に色を足した楕円体(濃さ 1/255 以上)。<b>手前から順</b>。</param>
/// <param name="EarlyStop">
/// 手前から重ねていって、残りの透明度が 1/10000 を切ったのが何個目か(切らなければ <c>null</c>)。
/// 元論文のタイル方式はここで打ち切る(要点8)。
/// </param>
/// <param name="Color">空まで重ねた画素の色。GPU の絵と同じになるはず(自己チェック9)。</param>
internal sealed record ProbeResult(int Covered, IReadOnlyList<Contribution> Contributions, int? EarlyStop, Vector3 Color);

/// <summary>
/// 1画素に効いている楕円体を<b>全部 CPU で調べて、手前から重ね直す</b>(右クリック)。
///
/// <para>
/// GPU は奥から重ねる(並べ替えの向きがそうなので)。ここではわざと<b>手前から</b>重ねる。
/// 重ね方の式を変形すると、手前から重ねても同じ色になる(要点3)——そして手前から重ねると、
/// 「残りの透明度」がほぼ 0 になった時点で、それより奥の楕円体は<b>何を足しても見えない</b>ことが分かる。
/// この画素で GPU が画素シェーダを何回走らせたか(<see cref="ProbeResult.Covered"/>)と、
/// 本当に要ったのは何個目までか(<see cref="ProbeResult.EarlyStop"/>)を並べて見るのが、この機能の目的。
/// </para>
/// </summary>
internal static class PixelProbe
{
    /// <summary>元論文の実装が打ち切る残りの透明度。</summary>
    public const float StopTransmittance = 0.0001f;

    /// <summary>
    /// 窓の画素 (<paramref name="px"/>, <paramref name="py"/>)(左上が原点、y は下向き)を調べる。
    /// 描くときの設定(次数・0.3 の広げ・大きさの倍率)は GPU と揃えて渡す。
    /// </summary>
    public static ProbeResult Probe(SplatCloud cloud, in CameraFrame camera, int px, int py, int shDegree, bool dilate, float scaleMultiplier)
    {
        // 画素の真ん中を、画面の中心からの座標(y は上向き)にする。GPU の画素シェーダが計算するのも画素の真ん中。
        var pixel = new Vector2(px + 0.5f - camera.Width * 0.5f, camera.Height * 0.5f - (py + 0.5f));
        float covarianceScale = scaleMultiplier * scaleMultiplier;

        var hits = new List<Contribution>();
        int covered = 0;
        for (int i = 0; i < cloud.Count; i++)
        {
            ProjectedSplat? projected = SplatProjection.Project(cloud.Positions[i], cloud.Covariance(i).Scaled(covarianceScale), camera, dilate);
            if (projected is not ProjectedSplat s)
            {
                continue;
            }

            // GPU と同じく、中心から縦横 Radius 画素の正方形に入っているかで「四角形が掛かっているか」を決める。
            Vector2 offset = pixel - s.Center;
            if (MathF.Abs(offset.X) > s.Radius || MathF.Abs(offset.Y) > s.Radius)
            {
                continue;
            }

            covered++;
            float alpha = s.AlphaAt(offset, cloud.Opacities[i]);
            if (alpha <= 0.0f)
            {
                continue;
            }

            Vector3 direction = Vector3.Normalize(cloud.Positions[i] - camera.Position);
            hits.Add(new Contribution(i, s.Depth, alpha, 0.0f, cloud.ColorToward(i, direction, shDegree)));
        }

        // 手前から並べて、残りの透明度 T を掛けながら重ねる。C += 色 × α × T、T ×= (1 − α)。
        hits.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        var result = new List<Contribution>(hits.Count);
        float transmittance = 1.0f;
        Vector3 color = Vector3.Zero;
        int? earlyStop = null;
        foreach (Contribution hit in hits)
        {
            var contribution = hit with { Transmittance = transmittance };
            result.Add(contribution);
            color += contribution.Weighted;
            transmittance *= 1.0f - hit.Alpha;
            if (earlyStop is null && transmittance < StopTransmittance)
            {
                earlyStop = result.Count;
            }
        }

        // 最後まで残った透明度のぶんだけ空が見える。
        color += cloud.Background * transmittance;
        return new ProbeResult(covered, result, earlyStop, color);
    }
}
