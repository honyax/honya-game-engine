using System.Buffers.Binary;
using System.IO.Compression;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **画面を PNG に落とすところ**(Day 39)。
///
/// デモを組み上げる日に足したのは、実務上の理由がある。
/// 「木箱をあと 30cm 右へ」を判断するには絵を見比べるしかないが、
/// **窓の中の絵は並べられない**。1枚ずつ落として横に並べて初めて、
/// どちらがよいかを決められる。
///
/// <para>
/// <b>PNG を自分で書く</b>。<c>StbImageSharp</c> は読み込み専用で、
/// 書き出しは別のパッケージ(<c>StbImageWriteSharp</c>)になる。
/// PNG の書き出しは<b>圧縮を .NET の <see cref="ZLibStream"/> に任せれば 100 行で済む</b>ので、
/// 依存を1つ増やすより自分で書いた。<see cref="HdrImage"/> と同じ判断。
/// </para>
///
/// <para>
/// <b>PNG の中身</b>は3つのかたまり(チャンク)だけで足りる。
/// <code>
///   署名 8 バイト
///   IHDR … 幅・高さ・ビット深度・色の種類
///   IDAT … 画素(zlib で圧縮したもの)
///   IEND … 終わり
/// </code>
/// 各チャンクは <c>長さ / 種類 / 中身 / CRC32</c> の順に並ぶ。
/// **長さに種類と CRC は含まない**が、**CRC は種類から計算する**——
/// この 2 つの非対称がいちばん間違えやすいところ。
/// </para>
///
/// <para>
/// <b>フィルタ</b>。PNG は行ごとに 1 バイトの「フィルタ種別」を先頭に置く決まりで、
/// 0 は「何もしない」。まじめにやると 5 種類を試して一番縮むものを選ぶが、
/// ここは 0 固定にした。写真的な絵では効きが小さく、
/// **フィルタの実装が主題ではない**ため。
/// </para>
/// </summary>
internal static class Screenshot
{
    /// <summary>CRC32 の表。**最初に使うときだけ作る**。</summary>
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// いま画面に出ているものを PNG で保存する。
    ///
    /// <para>
    /// <b><c>glReadPixels</c> の原点は左下</b>。PNG は上から下へ書くので、
    /// 行を逆順にして書き出す。ここを忘れると上下が逆さまの絵が出る——
    /// 見れば分かる壊れ方なので、むしろ扱いやすいほうの間違い。
    /// </para>
    ///
    /// <para>
    /// <b>読むのは既定のフレームバッファ</b>。つまり後処理を全部通したあとの絵で、
    /// トーンマップもガンマも FXAA も済んでいる。
    /// HDR のまま欲しければ <see cref="PostProcess"/> の途中から読むことになるが、
    /// **それは「見た目を確かめる」用途ではない**ので、ここでは扱わない。
    /// </para>
    /// </summary>
    /// <returns>保存したファイルのパス。</returns>
    public static unsafe string Save(GL gl, int width, int height, string directory)
    {
        Directory.CreateDirectory(directory);

        // **同じ秒に2枚撮ると名前がぶつかる**。連打すると普通に起きるので、
        // 空いている番号を探す。撮ったつもりのものが上書きされていた、が
        // いちばん腹立たしい壊れ方なので、ここは横着しない。
        string stamp = $"honya-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(directory, $"{stamp}.png");

        for (int index = 2; File.Exists(path); index++)
        {
            path = Path.Combine(directory, $"{stamp}-{index}.png");
        }

        var pixels = new byte[width * height * 3];

        // **行の詰め方を 1 バイト境界に**。既定の 4 バイト境界だと、
        // 幅 x 3 が 4 の倍数でないときに行末に詰め物が入り、
        // 絵が斜めにずれる(Texture.FromFloatPixels と同じ話)。
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);

        fixed (byte* buffer = pixels)
        {
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgb, PixelType.UnsignedByte, buffer);
        }

        gl.PixelStore(PixelStoreParameter.PackAlignment, 4);

        WritePng(path, pixels, width, height);
        return path;
    }

    /// <summary>RGB 8bit の画素列を PNG として書き出す。**行は下から上**の並びで渡す。</summary>
    private static void WritePng(string path, byte[] bottomUpRgb, int width, int height)
    {
        // --- 生データ(フィルタ種別 + 1 行ぶんの RGB)を組む ---
        int stride = width * 3;
        var raw = new byte[(stride + 1) * height];

        for (int y = 0; y < height; y++)
        {
            // 上下をここでひっくり返す。
            int source = (height - 1 - y) * stride;
            int destination = y * (stride + 1);

            raw[destination] = 0;   // フィルタ種別 0 = なし
            Array.Copy(bottomUpRgb, source, raw, destination + 1, stride);
        }

        // --- zlib で圧縮 ---
        //
        // PNG が要求するのは「zlib 形式」(RFC 1950)で、生の deflate ではない。
        // 2 バイトのヘッダと 4 バイトの Adler-32 が付くぶんが違う。
        // .NET の ZLibStream はまさにその形式なので、そのまま使える。
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        using var file = File.Create(path);

        // --- 署名 ---
        //
        // 先頭の 0x89 は「8bit 目が立っているので、7bit の経路を通ると壊れる」ことを
        // 検出するための番人。続く \r\n と \n は改行コードの変換を検出するためのもの。
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        // --- IHDR ---
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;    // ビット深度
        header[9] = 2;    // 色の種類 2 = トゥルーカラー(RGB)
        header[10] = 0;   // 圧縮方式(0 のみ)
        header[11] = 0;   // フィルタ方式(0 のみ)
        header[12] = 0;   // インターレース無し
        WriteChunk(file, "IHDR", header);

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    /// <summary>チャンクを1つ書く。<c>長さ / 種類 / 中身 / CRC32</c>。すべてビッグエンディアン。</summary>
    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        byte[] typeBytes = [(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]];
        stream.Write(typeBytes);
        stream.Write(data);

        // **CRC は種類 + 中身に対して計算する**。長さは含まない。
        uint crc = Crc32(0xFFFFFFFFu, typeBytes);
        crc = Crc32(crc, data) ^ 0xFFFFFFFFu;

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        stream.Write(checksum);
    }

    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>CRC32(多項式 0xEDB88320)の 256 段の表。PNG 仕様の付録そのまま。</summary>
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
