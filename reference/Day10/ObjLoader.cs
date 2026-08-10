using System.Globalization;

namespace SoftwareRasterizer;

/// <summary>
/// Wavefront OBJ 形式のモデルを読み込む。
///
/// OBJ を選ぶ理由は、**テキストで書かれていて仕様が小さい**こと。
/// バイナリ形式(glTF、FBX)のほうが実用的だが、パーサが数百行では済まない。
/// 「モデルファイルとは要するに頂点と面の一覧である」ことを実感するには、
/// テキストエディタで中身を開ける形式が一番よい。
///
/// 実装するのは実際に使う範囲だけで、マテリアル(mtl)、グループ、
/// スムージンググループ、曲面(curv / surf)などは無視する。
/// </summary>
internal static class ObjLoader
{
    /// <summary>
    /// OBJ ファイルを読み込んで <see cref="Mesh"/> を作る。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    /// <param name="flipV">
    /// V座標を反転するか。
    ///
    /// OBJ(と OpenGL)は**画像の左下**を原点とし、V が上に向かって増える。
    /// 一方、本リポジトリのテクスチャは配列の先頭が左上なので、V は下に向かって増える。
    /// この食い違いを吸収するのが 1 - v。既定で有効にしてある。
    /// 「テクスチャが上下逆さまになる」のはグラフィックスで最も頻繁に踏むバグの一つで、
    /// 原因はたいていここ。
    /// </param>
    public static Mesh Load(string path, bool flipV = true)
    {
        var positions = new List<Vec3>();
        var texCoords = new List<Vec2>();
        var normals = new List<Vec3>();

        var vertices = new List<Vertex>();
        var indices = new List<int>();

        // 「位置/UV/法線 の組み合わせ」から頂点番号を引く辞書。
        //
        // OBJ は位置・UV・法線を別々の配列で持ち、面がそれぞれを独立に参照する。
        // 一方こちらの Mesh は「1つの頂点が位置もUVも法線も持つ」形なので、
        // 出てきた組み合わせごとに頂点を作る必要がある。
        // ただし同じ組み合わせが再登場したら使い回さないと、
        // 索引を使う意味(Day 9 の要点5)が無くなってしまう。
        var vertexCache = new Dictionary<(int Position, int TexCoord, int Normal), int>();

        // 面の頂点番号を一時的に溜める場所(多角形を三角形に分割するため)。
        var face = new List<int>();

        foreach (string rawLine in File.ReadLines(path))
        {
            ReadOnlySpan<char> line = rawLine.AsSpan().Trim();

            // 空行とコメントは飛ばす。
            if (line.IsEmpty || line[0] == '#')
            {
                continue;
            }

            // 行頭のキーワードで分岐する。OBJ の文法はこれだけ。
            if (StartsWithToken(line, "v "))
            {
                positions.Add(ParseVec3(line[2..]));
            }
            else if (StartsWithToken(line, "vt "))
            {
                Vec2 uv = ParseVec2(line[3..]);
                texCoords.Add(flipV ? new Vec2(uv.X, 1.0f - uv.Y) : uv);
            }
            else if (StartsWithToken(line, "vn "))
            {
                normals.Add(ParseVec3(line[3..]));
            }
            else if (StartsWithToken(line, "f "))
            {
                face.Clear();

                foreach (Range tokenRange in SplitWhitespace(line[2..]))
                {
                    ReadOnlySpan<char> token = line[2..][tokenRange];
                    if (token.IsEmpty)
                    {
                        continue;
                    }

                    (int p, int t, int n) = ParseFaceToken(token, positions.Count, texCoords.Count, normals.Count);

                    if (!vertexCache.TryGetValue((p, t, n), out int index))
                    {
                        var vertex = new Vertex(
                            positions[p],
                            Vec3.One,
                            t >= 0 ? texCoords[t] : Vec2.Zero)
                        {
                            // 法線が無いファイルもあるので、その場合は後でまとめて計算する。
                            Normal = n >= 0 ? normals[n] : Vec3.Zero,
                        };

                        index = vertices.Count;
                        vertices.Add(vertex);
                        vertexCache[(p, t, n)] = index;
                    }

                    face.Add(index);
                }

                // 多角形を三角形に分割する(トライアングルファン)。
                // OBJ の面は3頂点とは限らず、四角形以上もありうる。
                // 凸多角形ならこの単純な分割で正しく、モデリングツールの出力はほぼ凸。
                for (int i = 1; i + 1 < face.Count; i++)
                {
                    indices.Add(face[0]);
                    indices.Add(face[i]);
                    indices.Add(face[i + 1]);
                }
            }
        }

        Vertex[] vertexArray = vertices.ToArray();
        int[] indexArray = indices.ToArray();

        if (normals.Count == 0)
        {
            ComputeNormals(vertexArray, indexArray);
        }

        return new Mesh(vertexArray, indexArray);
    }

    /// <summary>
    /// 法線を持たないモデルのために、面法線を頂点で平均して法線を作る。
    ///
    /// 各三角形の面法線(外積)を、その3頂点に足し込んでいき、最後に正規化する。
    /// **正規化しないまま足す**のがポイントで、外積の長さは三角形の面積に比例するので、
    /// 大きい面ほど強く効く重み付き平均になる。素朴だが妥当な結果になる。
    ///
    /// この方法だと、立方体の角のように本来は折れ目であるべき場所も
    /// 滑らかに丸められてしまう。実際のモデルではファイル側に法線を持たせるか、
    /// 「一定の角度以上なら別の法線にする」(スムージング角)処理を入れる。
    /// </summary>
    private static void ComputeNormals(Vertex[] vertices, int[] indices)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Normal = Vec3.Zero;
        }

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int i0 = indices[i];
            int i1 = indices[i + 1];
            int i2 = indices[i + 2];

            Vec3 faceNormal = Vec3.Cross(
                vertices[i1].Position - vertices[i0].Position,
                vertices[i2].Position - vertices[i0].Position);

            vertices[i0].Normal += faceNormal;
            vertices[i1].Normal += faceNormal;
            vertices[i2].Normal += faceNormal;
        }

        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Normal = vertices[i].Normal.Normalized();
        }
    }

    /// <summary>
    /// 面の1トークン("12"、"12/3"、"12//4"、"12/3/4")を解析する。
    /// 見つからない要素は -1 を返す。
    /// </summary>
    private static (int Position, int TexCoord, int Normal) ParseFaceToken(
        ReadOnlySpan<char> token, int positionCount, int texCoordCount, int normalCount)
    {
        int p = -1, t = -1, n = -1;
        int part = 0;
        int start = 0;

        for (int i = 0; i <= token.Length; i++)
        {
            if (i < token.Length && token[i] != '/')
            {
                continue;
            }

            ReadOnlySpan<char> slice = token[start..i];
            if (!slice.IsEmpty)
            {
                int value = int.Parse(slice, CultureInfo.InvariantCulture);

                // OBJ の索引は1始まり。負の値は「末尾からの相対」を意味する
                // (-1 が最後に定義された要素)。どちらも0始まりに直す。
                int count = part switch { 0 => positionCount, 1 => texCoordCount, _ => normalCount };
                int resolved = value > 0 ? value - 1 : count + value;

                switch (part)
                {
                    case 0: p = resolved; break;
                    case 1: t = resolved; break;
                    default: n = resolved; break;
                }
            }

            part++;
            start = i + 1;
        }

        return (p, t, n);
    }

    private static bool StartsWithToken(ReadOnlySpan<char> line, string token)
        => line.StartsWith(token, StringComparison.Ordinal);

    private static Vec3 ParseVec3(ReadOnlySpan<char> text)
    {
        Span<float> values = stackalloc float[3];
        ParseFloats(text, values);
        return new Vec3(values[0], values[1], values[2]);
    }

    private static Vec2 ParseVec2(ReadOnlySpan<char> text)
    {
        Span<float> values = stackalloc float[2];
        ParseFloats(text, values);
        return new Vec2(values[0], values[1]);
    }

    private static void ParseFloats(ReadOnlySpan<char> text, Span<float> destination)
    {
        int written = 0;
        foreach (Range range in SplitWhitespace(text))
        {
            if (written >= destination.Length)
            {
                break;
            }

            ReadOnlySpan<char> token = text[range];
            if (!token.IsEmpty)
            {
                // InvariantCulture を明示するのが重要。
                // 小数点をカンマで書く文化圏の環境だと、指定しないと 1.5 が解析できない。
                destination[written++] = float.Parse(token, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>空白で区切った範囲を列挙する(連続した空白は1つとして扱う)。</summary>
    private static List<Range> SplitWhitespace(ReadOnlySpan<char> text)
    {
        var ranges = new List<Range>();
        int start = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool isSpace = i == text.Length || text[i] == ' ' || text[i] == '\t';

            if (isSpace)
            {
                if (start >= 0)
                {
                    ranges.Add(new Range(start, i));
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        return ranges;
    }
}
