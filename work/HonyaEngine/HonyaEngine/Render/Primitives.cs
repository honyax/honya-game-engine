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

        // 板は +Z を向いているので、法線は4頂点とも同じ(Day 32 で足した)。
        Vector3 normal = Vector3.UnitZ;

        // 接線(Day 34)。**U が増える向き**を入れる。
        // この板は左下 (0,0) → 右下 (1,0) で U が増えるので、+X がそのまま接線になる。
        //
        // w = +1 は「従接線 = cross(N, T) で合っている」の意味。
        // 確かめると cross((0,0,1), (1,0,0)) = (0,1,0) = +Y で、
        // 左下 → 左上 で V が増える向きと一致する。**合っているか必ず手で確かめる**——
        // 符号を間違えても絵は出て、凹凸だけが裏返る。
        var tangent = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);

        ReadOnlySpan<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0.0f), new Vector2(0.0f, 0.0f), white, normal, tangent),   // 左下
            new(new Vector3(0.5f, -0.5f, 0.0f), new Vector2(1.0f, 0.0f), white, normal, tangent),    // 右下
            new(new Vector3(0.5f, 0.5f, 0.0f), new Vector2(1.0f, 1.0f), white, normal, tangent),     // 右上
            new(new Vector3(-0.5f, 0.5f, 0.0f), new Vector2(0.0f, 1.0f), white, normal, tangent),    // 左上
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

        // **法線は面から計算する**(Day 32)。
        // 立方体の頂点が 8 個ではなく 24 個なのは面ごとに UV が違うからだったが、
        // 法線も面ごとに違うので、いずれにせよ分けるしかなかった
        // (角を共有させると、その頂点の法線は3面の平均になり、
        //  立方体の角が丸く陰影付けされてしまう)。
        //
        // 外積の順は「反時計回りに並んだ3点」から外向きが出るように取る。
        // 渡される4点は外から見て CCW という約束なので、
        // (右下 - 左下) x (左上 - 左下) が外を向く。
        Vector3 normal = Vector3.Normalize(
            Vector3.Cross(bottomRight - bottomLeft, topLeft - bottomLeft));

        // **接線も面から出る**(Day 34)。渡される4点は「左下 → 右下 → 右上 → 左上」で、
        // UV も同じ順に (0,0) → (1,0) → (1,1) → (0,1) を割り当てているので、
        // **左下 → 右下 が U の増える向き**そのものになる。
        //
        // w = +1 でよいことは、板(CreateQuad)と同じ確かめ方でどの面でも成り立つ——
        // cross(N, T) が「左下 → 左上」を向く。UV の割り当てを1面でも変えると崩れるので、
        // 面ごとに UV を変えたくなったときはここも見直すこと。
        Vector3 tangent = Vector3.Normalize(bottomRight - bottomLeft);
        var tangent4 = new Vector4(tangent, 1.0f);

        vertices.Add(new Vertex(bottomLeft, new Vector2(0.0f, 0.0f), color, normal, tangent4));
        vertices.Add(new Vertex(bottomRight, new Vector2(1.0f, 0.0f), color, normal, tangent4));
        vertices.Add(new Vertex(topRight, new Vector2(1.0f, 1.0f), color, normal, tangent4));
        vertices.Add(new Vertex(topLeft, new Vector2(0.0f, 1.0f), color, normal, tangent4));

        // 四角形を三角形2枚に割る。渡された順が CCW なら、この並びも CCW になる。
        indices.Add(baseIndex + 0);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
        indices.Add(baseIndex + 0);
    }
}
