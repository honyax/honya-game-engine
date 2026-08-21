using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// よく使う形を作るところ。Day 15 で <c>Program</c> に置いていた <c>CreateQuad</c> の引っ越し先。
///
/// 形の定義はシーンの構成とは無関係なので、<c>Program</c> に置いておく理由が無い。
/// 「立方体が欲しい」たびに24頂点を手で並べるのも現実的ではない。
/// Day 20 以降にモデルを読み込むようになっても、
/// **動作確認用の素直な形**は要り続けるので、ここに残す。
/// </summary>
internal static class Primitives
{
    /// <summary>
    /// XY 平面に置いた 1x1 の正方形。中心が原点。
    /// 床にするときは X 軸まわりに -90 度回して寝かせる。
    /// </summary>
    public static Mesh<Vertex> CreateQuad(GL gl)
    {
        Vector4 white = Vector4.One;

        ReadOnlySpan<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0.0f), new Vector2(0.0f, 0.0f), white),   // 左下
            new(new Vector3(0.5f, -0.5f, 0.0f), new Vector2(1.0f, 0.0f), white),    // 右下
            new(new Vector3(0.5f, 0.5f, 0.0f), new Vector2(1.0f, 1.0f), white),     // 右上
            new(new Vector3(-0.5f, 0.5f, 0.0f), new Vector2(0.0f, 1.0f), white),    // 左上
        ];

        ReadOnlySpan<uint> indices = [0, 1, 2, 2, 3, 0];

        return new Mesh<Vertex>(gl, vertices, indices, Vertex.Attributes);
    }

    /// <summary>
    /// 1辺 1 の立方体。中心が原点。
    ///
    /// **頂点は 8 個ではなく 24 個**になる。
    /// 立方体の角は3つの面が共有しているが、面ごとに UV が違うので
    /// 「1つの頂点が持てる UV は1組」という制約に引っかかる。
    /// Day 10 の OBJ ローダで「位置/UV/法線 の組み合わせごとに頂点を作る」と
    /// 書いたのとまったく同じ事情で、頂点は**位置ではなく属性の組で数える**。
    /// (法線を持つようになる Day 32 以降は、面ごとに向きが違うのでなおさら分けられない)
    ///
    /// 面の向きは**外から見て反時計回り(CCW)**にそろえてある。
    /// OpenGL の既定では CCW が表面なので、これで背面カリングが正しく効く。
    /// 1面でも順序を間違えると、その面だけ**内側から覗いたときに見える**という
    /// 分かりやすい壊れ方をするので、C キーでカリングを切って確かめられる。
    /// </summary>
    public static Mesh<Vertex> CreateCube(GL gl)
    {
        var vertices = new List<Vertex>(24);
        var indices = new List<uint>(36);

        // 面ごとに色味を変えておく。テクスチャに掛け算されるので、
        // 立方体の面の切れ目が見分けやすくなる。
        Vector4 front = new(1.00f, 0.55f, 0.55f, 1.0f);   // +Z 赤
        Vector4 back = new(0.55f, 1.00f, 0.65f, 1.0f);    // -Z 緑
        Vector4 right = new(0.60f, 0.70f, 1.00f, 1.0f);   // +X 青
        Vector4 left = new(1.00f, 0.95f, 0.55f, 1.0f);    // -X 黄
        Vector4 top = new(1.00f, 1.00f, 1.00f, 1.0f);     // +Y 白
        Vector4 bottom = new(0.70f, 0.70f, 0.75f, 1.0f);  // -Y 灰

        const float h = 0.5f;

        // 各面、外から見て「左下 → 右下 → 右上 → 左上」の順に渡す。
        AddFace(vertices, indices,
            new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h), front);
        AddFace(vertices, indices,
            new Vector3(h, -h, -h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(h, h, -h), back);
        AddFace(vertices, indices,
            new Vector3(h, -h, h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), right);
        AddFace(vertices, indices,
            new Vector3(-h, -h, -h), new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(-h, h, -h), left);
        AddFace(vertices, indices,
            new Vector3(-h, h, h), new Vector3(h, h, h), new Vector3(h, h, -h), new Vector3(-h, h, -h), top);
        AddFace(vertices, indices,
            new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, -h, h), new Vector3(-h, -h, h), bottom);

        return new Mesh<Vertex>(gl, vertices.ToArray(), indices.ToArray(), Vertex.Attributes);
    }

    /// <summary>四角形1面ぶんの頂点4つとインデックス6つを足す。</summary>
    private static void AddFace(
        List<Vertex> vertices,
        List<uint> indices,
        Vector3 bottomLeft,
        Vector3 bottomRight,
        Vector3 topRight,
        Vector3 topLeft,
        Vector4 color)
    {
        uint baseIndex = (uint)vertices.Count;

        vertices.Add(new Vertex(bottomLeft, new Vector2(0.0f, 0.0f), color));
        vertices.Add(new Vertex(bottomRight, new Vector2(1.0f, 0.0f), color));
        vertices.Add(new Vertex(topRight, new Vector2(1.0f, 1.0f), color));
        vertices.Add(new Vertex(topLeft, new Vector2(0.0f, 1.0f), color));

        // 四角形を三角形2枚に割る。渡された順が CCW なら、この並びも CCW になる。
        indices.Add(baseIndex + 0);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
        indices.Add(baseIndex + 0);
    }
}
