using System.Numerics;

namespace MeshletRenderer;

/// <summary>
/// トーラスノット(結び目の形をした管)を作る。<b>今日描く形はこれ1つ</b>。
///
/// <para>
/// ファイルを読まずに作れて、<b>三角形の数を好きなだけ増やせて</b>、どの向きから見ても
/// 表と裏が入り組んでいる形が欲しかった。管の断面が円なので、1つの小さな面(メッシュレット)の中で
/// 法線がそこそこ揃う——これは Day 64b の背面カリングで効いてくる。
/// </para>
/// <para>
/// 作り方は three.js の <c>TorusKnotGeometry</c> と同じ。<b>結び目の線</b>(中心線)に沿って進みながら、
/// 各点で<b>断面の円</b>を1周ぶん置いていく。頂点は「線に沿って <c>tubular</c> 個 × 円周に <c>radial</c> 個」の格子になる。
/// </para>
/// </summary>
internal static class TorusKnot
{
    /// <summary>線に沿った刻み。多いほど曲がりが滑らか。</summary>
    public const int TubularSegments = 512;

    /// <summary>
    /// 断面の円周の刻み。<b>32 にしてあるのには理由がある</b>(要点2)。
    /// 1周ぶんの帯が「頂点 64 個・三角形 64 枚」になり、並び順のまま切ると
    /// ちょうど1本の帯が1つのメッシュレットに収まる。
    /// </summary>
    public const int RadialSegments = 32;

    /// <summary>
    /// 作る。頂点 16,384 個・三角形 32,768 枚。
    ///
    /// <para>
    /// <b>三角形は「帯ごと」に並ぶ</b>。線に沿った j 番目の円と j+1 番目の円の間を1周埋めてから、
    /// 次の帯へ進む。この並び順が、メッシュレットの切り方を比べるときの出発点になる。
    /// </para>
    /// </summary>
    /// <param name="p">線が大きな輪を何周するか。</param>
    /// <param name="q">そのあいだに管が輪をくぐる回数。(2, 3) で三つ葉結び。</param>
    /// <param name="radius">結び目全体の大きさ(m)。</param>
    /// <param name="tube">管の太さ(半径、m)。</param>
    public static MeshData Create(int p = 2, int q = 3, float radius = 1.0f, float tube = 0.28f)
    {
        var vertices = new GpuVertex[TubularSegments * RadialSegments];

        for (int j = 0; j < TubularSegments; j++)
        {
            // 線は u が 0 → 2πp で1周して閉じる(cos(u) は 2π ごと、cos(qu/p) は 2πp ごとに戻る)。
            float u = (float)j / TubularSegments * p * MathF.PI * 2.0f;
            Vector3 p1 = PointOnCurve(u, p, q, radius);
            Vector3 p2 = PointOnCurve(u + 0.01f, p, q, radius);

            // 断面を置くための2軸。T は線の向き、N と B は断面の円を張る。
            // p1 + p2 を「だいたい外向き」として使う three.js のやり方で、厳密な Frenet 標構ではないが、
            // 線が原点をまたがないこの形では向きが反転しない。
            Vector3 tangent = p2 - p1;
            Vector3 normal = p2 + p1;
            Vector3 binormal = Vector3.Normalize(Vector3.Cross(tangent, normal));
            normal = Vector3.Normalize(Vector3.Cross(binormal, tangent));

            for (int i = 0; i < RadialSegments; i++)
            {
                float v = (float)i / RadialSegments * MathF.PI * 2.0f;
                float cx = -tube * MathF.Cos(v);
                float cy = tube * MathF.Sin(v);
                Vector3 position = p1 + cx * normal + cy * binormal;

                // 管の断面は円なので、法線は「中心線から外へ」の向きそのもの。
                vertices[j * RadialSegments + i] = new GpuVertex(position, Vector3.Normalize(position - p1));
            }
        }

        var indices = new uint[TubularSegments * RadialSegments * 6];
        int k = 0;
        for (int j = 0; j < TubularSegments; j++)
        {
            // 最後の帯は最初の円へ戻る。three.js は継ぎ目の頂点を重ねて持つが(テクスチャ座標のため)、
            // 今日はテクスチャを貼らないので、剰余で巻き戻して頂点を共有する。
            int jNext = (j + 1) % TubularSegments;
            for (int i = 0; i < RadialSegments; i++)
            {
                int iNext = (i + 1) % RadialSegments;
                uint a = (uint)(j * RadialSegments + i);
                uint b = (uint)(jNext * RadialSegments + i);
                uint c = (uint)(jNext * RadialSegments + iNext);
                uint d = (uint)(j * RadialSegments + iNext);

                indices[k++] = a;
                indices[k++] = b;
                indices[k++] = d;

                indices[k++] = b;
                indices[k++] = c;
                indices[k++] = d;
            }
        }

        return new MeshData(vertices, indices);
    }

    /// <summary>結び目の中心線の上の点。<b>(p, q) トーラスノット</b>の式そのもの。</summary>
    private static Vector3 PointOnCurve(float u, int p, int q, float radius)
    {
        float qu = (float)q / p * u;
        float r = radius * (2.0f + MathF.Cos(qu)) * 0.5f;
        return new Vector3(r * MathF.Cos(u), r * MathF.Sin(u), radius * MathF.Sin(qu) * 0.5f);
    }
}
