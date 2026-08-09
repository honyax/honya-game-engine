namespace SoftwareRasterizer;

/// <summary>
/// メッシュ。頂点の配列と、それを三角形に組み立てる索引の配列。
///
/// ==== なぜ索引(インデックス)を使うのか ====
///
/// 三角形をただ並べるだけなら索引は要らない(頂点を3個ずつ書けばよい)。
/// しかし立方体を考えると、8個の角が3面ずつに共有されているので、
/// 索引無しでは同じ頂点を 36 個ぶん書くことになる。索引があれば頂点は 24 個で済む
/// (法線が面ごとに違うので8個までは減らせない)。球ではもっと差が開く。
///
/// 節約できるのはメモリだけではない。**頂点変換の回数が減る**ほうが本質的で、
/// 頂点を1回変換して結果を使い回せば、共有されている回数だけ計算が浮く。
/// GPUに頂点バッファとインデックスバッファが別々にあるのは、まさにこのため。
/// 本Dayの <c>DrawMesh</c> も「まず全頂点をワールドへ変換 → 索引で三角形を組む」
/// という順序になっていて、共有された頂点は1回しか変換していない。
/// </summary>
internal sealed class Mesh
{
    public Vertex[] Vertices { get; }

    /// <summary>3つ1組で1枚の三角形を表す索引。</summary>
    public int[] Indices { get; }

    public Mesh(Vertex[] vertices, int[] indices)
    {
        Vertices = vertices;
        Indices = indices;
    }

    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// 一辺2の立方体(原点中心)。
    ///
    /// 頂点が 8 ではなく 24 あるのは、**法線とUVが面ごとに違う**から。
    /// 立方体の角は3つの面が直角に出会う場所で、そこでの「面の向き」は1つに決まらない。
    /// 位置は同じでも法線が違うなら別の頂点として持つしかない、というのが
    /// 「頂点 = 位置」ではなく「頂点 = 位置と属性の組」である理由。
    /// </summary>
    public static Mesh CreateCube()
    {
        // 6面ぶん、面ごとに4頂点。順序は左上→右上→右下→左下。
        var faceNormals = new Vec3[]
        {
            new(-1, 0, 0), new(1, 0, 0),
            new(0, -1, 0), new(0, 1, 0),
            new(0, 0, -1), new(0, 0, 1),
        };

        // 各面の4隅を、8頂点の番号で表したもの。
        int[][] faceCorners =
        {
            new[] { 0, 2, 6, 4 },   // -X
            new[] { 5, 7, 3, 1 },   // +X
            new[] { 0, 4, 5, 1 },   // -Y
            new[] { 2, 3, 7, 6 },   // +Y
            new[] { 1, 3, 2, 0 },   // -Z
            new[] { 4, 6, 7, 5 },   // +Z
        };

        var uvs = new Vec2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };

        var vertices = new Vertex[24];
        var indices = new int[36];

        for (int face = 0; face < 6; face++)
        {
            for (int corner = 0; corner < 4; corner++)
            {
                int c = faceCorners[face][corner];
                var position = new Vec3(
                    (c & 1) == 0 ? -1.0f : 1.0f,
                    (c & 2) == 0 ? -1.0f : 1.0f,
                    (c & 4) == 0 ? -1.0f : 1.0f);

                vertices[face * 4 + corner] = new Vertex(position, Vec3.One, uvs[corner])
                {
                    Normal = faceNormals[face],
                };
            }

            // 四角形を三角形2枚に割る。
            //
            // 頂点の並べ方(巻き方向)は「外から見て反時計回り」にそろえる。
            // こうしておくと Cross(v1 - v0, v2 - v0) が必ず**外向きの法線**になり、
            //   - フラットシェーディングの面法線(Day 9)
            //   - 背面カリングの表裏判定(Day 10)
            // が、格納してある頂点法線と食い違わずに済む。
            // ここを間違えると「面が真っ黒になる」「表の面が消える」形で表面化する。
            int baseIndex = face * 4;
            int t = face * 6;
            indices[t] = baseIndex;
            indices[t + 1] = baseIndex + 2;
            indices[t + 2] = baseIndex + 1;
            indices[t + 3] = baseIndex;
            indices[t + 4] = baseIndex + 3;
            indices[t + 5] = baseIndex + 2;
        }

        return new Mesh(vertices, indices);
    }

    /// <summary>
    /// 半径1の球(原点中心)。緯度・経度で分割する、いわゆるUV球。
    ///
    /// **球の法線は位置そのもの**。原点中心の単位球なら、
    /// 表面の点 P における外向き法線は P を正規化したもの(= P 自身)になる。
    /// 球がシェーディングの確認に最適なのは、
    /// あらゆる向きの法線が1つの物体に揃っているから。
    ///
    /// 分割数を上げると滑らかになるが、三角形が増える。
    /// **法線を頂点で共有して補間する**(グーロー / フォン)ことで、
    /// 少ない三角形でも丸く見せられる、というのが Day 9 の眼目でもある。
    /// </summary>
    public static Mesh CreateSphere(int rings, int segments)
    {
        // 極を含めて (rings + 1) 段、経度方向は継ぎ目でUVが不連続になるので
        // (segments + 1) 列ぶんの頂点を持つ(最後の列は最初と同じ位置だがUVが違う)。
        var vertices = new Vertex[(rings + 1) * (segments + 1)];
        var indices = new int[rings * segments * 6];

        int v = 0;
        for (int ring = 0; ring <= rings; ring++)
        {
            // 緯度: 0(北極)〜 π(南極)
            float phi = MathF.PI * ring / rings;
            float y = MathF.Cos(phi);
            float ringRadius = MathF.Sin(phi);

            for (int segment = 0; segment <= segments; segment++)
            {
                // 経度: 0 〜 2π
                float theta = MathF.PI * 2.0f * segment / segments;
                var position = new Vec3(
                    ringRadius * MathF.Sin(theta),
                    y,
                    ringRadius * MathF.Cos(theta));

                var uv = new Vec2(segment / (float)segments, ring / (float)rings);

                vertices[v++] = new Vertex(position, Vec3.One, uv)
                {
                    // 単位球なので位置がそのまま法線になる。
                    Normal = position,
                };
            }
        }

        int i = 0;
        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                int current = ring * (segments + 1) + segment;
                int next = current + segments + 1;

                indices[i++] = current;
                indices[i++] = next;
                indices[i++] = current + 1;

                indices[i++] = current + 1;
                indices[i++] = next;
                indices[i++] = next + 1;
            }
        }

        return new Mesh(vertices, indices);
    }

    /// <summary>
    /// XZ平面上の平らな板(原点中心)。床用。
    ///
    /// <paramref name="divisions"/> で分割数を指定できるようにしてあるのは、
    /// 大きな三角形がカメラの後ろに回ると丸ごと消えてしまうため(Day 8 の要点7)。
    /// 細かく割っておけば、消えるのは端の一部だけで済む。
    /// Day 10 のクリッピングを入れれば分割は不要になる。
    /// </summary>
    public static Mesh CreatePlane(float halfSize, float uvRepeat, int divisions)
    {
        var vertices = new Vertex[(divisions + 1) * (divisions + 1)];
        var indices = new int[divisions * divisions * 6];

        int v = 0;
        for (int z = 0; z <= divisions; z++)
        {
            for (int x = 0; x <= divisions; x++)
            {
                float fx = x / (float)divisions;
                float fz = z / (float)divisions;

                var position = new Vec3(
                    (fx * 2.0f - 1.0f) * halfSize,
                    0.0f,
                    (fz * 2.0f - 1.0f) * halfSize);

                vertices[v++] = new Vertex(position, Vec3.One, new Vec2(fx * uvRepeat, fz * uvRepeat))
                {
                    Normal = Vec3.UnitY,
                };
            }
        }

        int i = 0;
        for (int z = 0; z < divisions; z++)
        {
            for (int x = 0; x < divisions; x++)
            {
                int current = z * (divisions + 1) + x;
                int next = current + divisions + 1;

                // 立方体と同じく、外から見て反時計回り(法線が +Y になる向き)にそろえる。
                indices[i++] = current;
                indices[i++] = next;
                indices[i++] = current + 1;

                indices[i++] = current + 1;
                indices[i++] = next;
                indices[i++] = next + 1;
            }
        }

        return new Mesh(vertices, indices);
    }
}
