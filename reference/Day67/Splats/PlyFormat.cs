using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace GaussianSplatting;

/// <summary>
/// 学習済みの 3DGS を入れた .ply を読み書きする(要点6)。
///
/// <para>
/// .ply はもともと点群やポリゴンのための古い形式(Stanford, 1994)で、<b>先頭に文字で「1行に何がどの順で並ぶか」を書き、
/// その後ろにバイナリの行を並べる</b>。3DGS はこれを「1行 = 楕円体1個」として使っている。
/// 元論文の実装が書き出す列は次のとおり(次数 3 なら 62 列、すべて float)。
/// </para>
/// <code>
/// x y z                   位置
/// nx ny nz                法線(使っていない。いつも 0。点群の道具で開けるように置いてあるだけ)
/// f_dc_0 f_dc_1 f_dc_2    球面調和の0番の係数(RGB)
/// f_rest_0 … f_rest_44    1〜15番の係数。<b>R の 15 個、G の 15 個、B の 15 個の順</b>(係数ごとに RGB ではない)
/// opacity                 不透明度。<b>シグモイドを掛ける前</b>の値
/// scale_0 scale_1 scale_2 大きさ。<b>log を取った</b>値
/// rot_0 rot_1 rot_2 rot_3 回転の四元数。<b>w, x, y, z の順</b>で、長さ 1 とは限らない
/// </code>
/// <para>
/// 不透明度・大きさ・回転が「そのままの形」で入っていないのは、<b>学習のときに好きな値へ動かしても壊れない形</b>で持っているから。
/// シグモイドを掛ければ必ず 0〜1、exp を取れば必ず正、正規化すれば必ず回転になる。読むときにこの3つを掛けて戻す。
/// </para>
/// </summary>
internal static class PlyFormat
{
    /// <summary>1回に読む行の数。行ごとに Read を呼ぶと遅く、全部を一度に読むと数百 MB の配列が要るので、間を取る。</summary>
    private const int RowsPerChunk = 65536;

    /// <summary>
    /// .ply を読む。3DGS の列が無い、テキスト形式(ascii)である、などで読めなければ例外を投げる(窓は落とさず HUD に出す)。
    /// </summary>
    public static SplatCloud Read(string path)
    {
        var clock = Stopwatch.StartNew();
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);

        Header header = ReadHeader(stream);

        // 列の名前 → 行の中の位置(バイト)。3DGS の .ply はすべて float だが、念のため型の大きさを見て位置を数える。
        var offsets = new Dictionary<string, int>();
        var types = new Dictionary<string, string>();
        int rowBytes = 0;
        foreach (var (type, name) in header.Properties)
        {
            offsets[name] = rowBytes;
            types[name] = type;
            rowBytes += SizeOf(type);
        }

        foreach (string required in new[] { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" })
        {
            if (!offsets.ContainsKey(required))
            {
                throw new InvalidDataException($"3DGS の列 {required} がありません(点群やメッシュの .ply かもしれません)");
            }
        }

        // f_rest の数から次数を決める。次数 d なら 3 × ((d + 1)² − 1) 列。
        int restCount = 0;
        while (offsets.ContainsKey($"f_rest_{restCount}"))
        {
            restCount++;
        }

        int degree = restCount switch
        {
            0 => 0,
            9 => 1,
            24 => 2,
            45 => 3,
            _ => throw new InvalidDataException($"f_rest が {restCount} 列あります(0 / 9 / 24 / 45 のどれかのはず)"),
        };

        var cloud = new SplatCloud(Path.GetFileName(path), header.VertexCount, degree);
        int coefficients = cloud.ShCoefficients;
        int restPerChannel = coefficients - 1;

        // 列の位置は先に数に直しておく。行ごとに名前で辞書を引いたり、f_rest_{番号} の文字列を作ったりすると、
        // 74 万行 × 62 列で数千万回になり、読み込みが数秒から十数秒に延びる。
        Column Col(string name) => new(offsets[name], types[name]);
        Column[] position = [Col("x"), Col("y"), Col("z")];
        Column[] scale = [Col("scale_0"), Col("scale_1"), Col("scale_2")];
        Column[] rotation = [Col("rot_0"), Col("rot_1"), Col("rot_2"), Col("rot_3")];
        Column opacity = Col("opacity");
        var sh = new Column[coefficients, 3];
        for (int c = 0; c < 3; c++)
        {
            sh[0, c] = Col($"f_dc_{c}");
            for (int k = 1; k < coefficients; k++)
            {
                // f_rest は「R の全部、G の全部、B の全部」の順。k 番目の係数の色 c は f_rest_{c × 15 + (k − 1)}。
                sh[k, c] = Col($"f_rest_{c * restPerChannel + k - 1}");
            }
        }

        var chunk = new byte[RowsPerChunk * rowBytes];
        for (int start = 0; start < header.VertexCount; start += RowsPerChunk)
        {
            int rows = Math.Min(RowsPerChunk, header.VertexCount - start);
            stream.ReadExactly(chunk, 0, rows * rowBytes);

            for (int r = 0; r < rows; r++)
            {
                ReadOnlySpan<byte> row = chunk.AsSpan(r * rowBytes, rowBytes);
                int i = start + r;
                cloud.Positions[i] = new Vector3(position[0].Read(row), position[1].Read(row), position[2].Read(row));

                // 大きさは log を取った値 → exp で戻す。
                cloud.Scales[i] = new Vector3(MathF.Exp(scale[0].Read(row)), MathF.Exp(scale[1].Read(row)), MathF.Exp(scale[2].Read(row)));

                // 回転は (w, x, y, z)。System.Numerics の Quaternion は (x, y, z, w) の順なので並べ替える。
                var q = new Quaternion(rotation[1].Read(row), rotation[2].Read(row), rotation[3].Read(row), rotation[0].Read(row));
                float length = q.Length();
                cloud.Rotations[i] = length > 0.0f ? q * (1.0f / length) : Quaternion.Identity;

                // 不透明度はシグモイドを掛ける前の値 → 1 / (1 + e^−x) で 0〜1 に戻す。
                cloud.Opacities[i] = 1.0f / (1.0f + MathF.Exp(-opacity.Read(row)));

                for (int k = 0; k < coefficients; k++)
                {
                    cloud.SetSh(i, k, new Vector3(sh[k, 0].Read(row), sh[k, 1].Read(row), sh[k, 2].Read(row)));
                }
            }
        }

        cloud.WorldUp = -Vector3.UnitY;
        cloud.DefaultView = GuessView(cloud);
        cloud.LoadMilliseconds = clock.Elapsed.TotalMilliseconds;
        return cloud;
    }

    /// <summary>
    /// 元論文の実装と同じ列で書き出す。自己チェック7(書いて読み直すと元に戻るか)で使う。
    /// 読むときの逆(log・シグモイドの逆・並べ替え)を掛けて書く。
    /// </summary>
    public static void Write(Stream stream, SplatCloud cloud)
    {
        int coefficients = cloud.ShCoefficients;
        int restPerChannel = coefficients - 1;

        var names = new List<string> { "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2" };
        for (int j = 0; j < 3 * restPerChannel; j++)
        {
            names.Add($"f_rest_{j}");
        }

        names.AddRange(["opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"]);

        var header = new StringBuilder();
        header.Append("ply\nformat binary_little_endian 1.0\n");
        header.Append(CultureInfo.InvariantCulture, $"element vertex {cloud.Count}\n");
        foreach (string name in names)
        {
            header.Append(CultureInfo.InvariantCulture, $"property float {name}\n");
        }

        header.Append("end_header\n");
        stream.Write(Encoding.ASCII.GetBytes(header.ToString()));

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        for (int i = 0; i < cloud.Count; i++)
        {
            Vector3 p = cloud.Positions[i];
            writer.Write(p.X);
            writer.Write(p.Y);
            writer.Write(p.Z);
            writer.Write(0.0f);
            writer.Write(0.0f);
            writer.Write(0.0f);

            Vector3 dc = cloud.GetSh(i, 0);
            writer.Write(dc.X);
            writer.Write(dc.Y);
            writer.Write(dc.Z);
            for (int c = 0; c < 3; c++)
            {
                for (int k = 1; k < coefficients; k++)
                {
                    Vector3 sh = cloud.GetSh(i, k);
                    writer.Write(c == 0 ? sh.X : c == 1 ? sh.Y : sh.Z);
                }
            }

            // シグモイドの逆(ロジット)。0 と 1 ちょうどは無限大になるので、少し内側で止める。
            float opacity = Math.Clamp(cloud.Opacities[i], 1e-6f, 1.0f - 1e-6f);
            writer.Write(MathF.Log(opacity / (1.0f - opacity)));

            Vector3 s = cloud.Scales[i];
            writer.Write(MathF.Log(s.X));
            writer.Write(MathF.Log(s.Y));
            writer.Write(MathF.Log(s.Z));

            Quaternion q = cloud.Rotations[i];
            writer.Write(q.W);
            writer.Write(q.X);
            writer.Write(q.Y);
            writer.Write(q.Z);
        }
    }

    /// <summary>
    /// 読み込んだシーンの最初のカメラを見当で決める。<b>撮影したカメラの位置は .ply に入っていない</b>ので、
    /// 楕円体の位置の中央値(外れ値に強い)を「シーンの真ん中」とみなし、そこから少し引いた所に立つ。
    /// </summary>
    private static FlyView GuessView(SplatCloud cloud)
    {
        // 全部を並べると数百 ms かかるので、間引いて中央値を取る。
        int step = Math.Max(1, cloud.Count / 20000);
        var xs = new List<float>();
        var ys = new List<float>();
        var zs = new List<float>();
        for (int i = 0; i < cloud.Count; i += step)
        {
            xs.Add(cloud.Positions[i].X);
            ys.Add(cloud.Positions[i].Y);
            zs.Add(cloud.Positions[i].Z);
        }

        xs.Sort();
        ys.Sort();
        zs.Sort();
        var center = new Vector3(xs[xs.Count / 2], ys[ys.Count / 2], zs[zs.Count / 2]);

        // 真ん中の半分(25%〜75%)の広がりを、シーンの大きさの目安にする。
        float extent = MathF.Max(xs[xs.Count * 3 / 4] - xs[xs.Count / 4], zs[zs.Count * 3 / 4] - zs[zs.Count / 4]);

        // 上が −Y なので、Yaw = 0 の前は +Z。そこから extent の 0.6 倍だけ引き、少し上(−Y)に立って、やや見下ろす。
        // 引きすぎると、撮影したカメラの外側に出て、学習で使われなかった楕円体(浮いたゴミ)の中に立つことになる(要点7)。
        // 0.6 は Tanks and Temples の train で決めた値で、ほかのシーンでは外れることもある(R で戻るのはこの位置)。
        Vector3 position = center + new Vector3(0.0f, -0.2f * extent, -0.6f * extent);
        return new FlyView(position, 0.0f, -0.15f, 50.0f);
    }

    private sealed record Header(int VertexCount, List<(string Type, string Name)> Properties);

    /// <summary>
    /// 先頭の文字の部分を読む。<c>end_header</c> の行で終わり、そのすぐ後ろからバイナリが始まる。
    /// 1バイトずつ読むのは、BufferedStream の位置をバイナリの先頭ぴったりに残すため。
    /// </summary>
    private static Header ReadHeader(Stream stream)
    {
        string first = ReadLine(stream);
        if (first != "ply")
        {
            throw new InvalidDataException(".ply ではありません(先頭が ply でない)");
        }

        int vertexCount = -1;
        var properties = new List<(string, string)>();
        string? currentElement = null;
        while (true)
        {
            string line = ReadLine(stream);
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] is "comment" or "obj_info")
            {
                continue;
            }

            if (parts[0] == "end_header")
            {
                break;
            }

            switch (parts[0])
            {
                case "format" when parts[1] != "binary_little_endian":
                    throw new InvalidDataException($"形式 {parts[1]} は読めません(binary_little_endian だけ)");

                case "element":
                    currentElement = parts[1];
                    if (currentElement == "vertex")
                    {
                        vertexCount = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    }
                    else if (vertexCount < 0)
                    {
                        throw new InvalidDataException("vertex より前に別の要素があります");
                    }

                    break;

                case "property" when currentElement == "vertex":
                    if (parts[1] == "list")
                    {
                        throw new InvalidDataException("vertex に可変長の列(list)があります");
                    }

                    properties.Add((parts[1], parts[2]));
                    break;
            }
        }

        if (vertexCount < 0)
        {
            throw new InvalidDataException("element vertex がありません");
        }

        return new Header(vertexCount, properties);
    }

    private static string ReadLine(Stream stream)
    {
        var bytes = new List<byte>();
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
            {
                throw new InvalidDataException("ヘッダの途中でファイルが終わりました");
            }

            if (b == '\n')
            {
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            }

            bytes.Add((byte)b);
        }
    }

    private static int SizeOf(string type) => type switch
    {
        "char" or "uchar" or "int8" or "uint8" => 1,
        "short" or "ushort" or "int16" or "uint16" => 2,
        "int" or "uint" or "int32" or "uint32" or "float" or "float32" => 4,
        "double" or "float64" => 8,
        _ => throw new InvalidDataException($"知らない型 {type}"),
    };

    /// <summary>1行の中の1列。どこから何の型で読むか。</summary>
    private readonly record struct Column(int Offset, string Type)
    {
        public float Read(ReadOnlySpan<byte> row) => Type switch
        {
            "float" or "float32" => BitConverter.ToSingle(row[Offset..]),
            "double" or "float64" => (float)BitConverter.ToDouble(row[Offset..]),
            "uchar" or "uint8" => row[Offset],
            "char" or "int8" => (sbyte)row[Offset],
            "short" or "int16" => BitConverter.ToInt16(row[Offset..]),
            "ushort" or "uint16" => BitConverter.ToUInt16(row[Offset..]),
            "int" or "int32" => BitConverter.ToInt32(row[Offset..]),
            _ => BitConverter.ToUInt32(row[Offset..]),
        };
    }
}
