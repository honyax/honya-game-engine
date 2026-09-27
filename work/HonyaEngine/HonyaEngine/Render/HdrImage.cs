using System.Globalization;
using System.Text;

namespace HonyaEngine;

/// <summary>
/// **Radiance の <c>.hdr</c> を読むところ**(Day 39)。今日の主役その1。
///
/// Day 36 の <see cref="SkyImage"/> には
/// 「本物の HDRI を入れるのは Day 39。そのとき <c>.hdr</c> の読み込みも書く」
/// と書いてあった。その約束を果たす回。
///
/// <para>
/// <b>なぜ既製品に任せないか</b>。PNG/JPG は <c>StbImageSharp</c> に任せている
/// (csproj のコメント)のに、こちらは自分で書く。理由は2つ。
/// </para>
///
/// <list type="number">
/// <item>
/// <b>とにかく小さい</b>。ヘッダは平文で、本体は 1 バイト単位のランレングスひとつ。
/// 仕様全体でも 200 行で書ける。JPEG の DCT やハフマンとは規模が違う。
/// </item>
/// <item>
/// <b>「HDR とは何か」がバイト列の形で分かる</b>。この形式のキモは
/// <b>RGB それぞれに指数を持たせず、3色で1つの指数を共有する</b>点で、
/// だから 1 画素 4 バイトのまま 10 の何十乗という幅が入る。
/// float を3つ並べる(12 バイト)より 3 倍小さく、
/// 8bit PNG より桁違いに広い——**その折り合いの付け方**を読むのが今日の値打ち。
/// </item>
/// </list>
///
/// <para>
/// <b>RGBE の仕組み</b>。1 画素は <c>(R, G, B, E)</c> の 4 バイトで、
/// <code>
///   実際の色 = (R, G, B) x 2^(E - 128 - 8)
/// </code>
/// E は 3 色で共有する指数。<c>E = 0</c> だけは特別扱いで「真っ黒」を表す。
/// 128 は下駄(指数を符号なしで持つため)、8 は
/// 「仮数を 0〜255 の整数で持っているぶんを 0〜1 に戻す」ぶん。
/// </para>
///
/// <para>
/// <b>弱点も見えるようにしておく</b>。指数が共有なので、
/// <b>3 色のうち一番大きい成分の精度で他の 2 色も決まる</b>。
/// 夕焼けのように R が G/B より 2 桁大きい絵では、B の下位ビットがごっそり落ちる。
/// OpenEXR(半精度 float x 3)がプロの現場で使われるのはこのため。
/// Poly Haven が <c>.hdr</c> と <c>.exr</c> の両方を配っているのも同じ事情で、
/// 今日 <c>.hdr</c> を選んだのは**自分で読み切れる大きさだから**でしかない。
/// </para>
/// </summary>
internal static class HdrImage
{
    /// <summary>読み込んだ結果。**RGB の float が横並び**(1 画素 3 要素)。</summary>
    /// <param name="Pixels">
    /// 長さ <c>Width * Height * 3</c>。<see cref="SkyImage.Create"/> が返すものと同じ形なので、
    /// <see cref="EnvironmentMap"/> はどちらが来ても同じ道を通せる。
    /// </param>
    /// <param name="Width">横。正距円筒なら <c>Height</c> の 2 倍。</param>
    /// <param name="Height">縦。**0 行目が上端**(天頂)。</param>
    /// <param name="Exposure">
    /// ヘッダの <c>EXPOSURE</c> の積。撮影時に掛けられた倍率で、
    /// 読み込みでは**割って**元の輝度に戻す。掛かっていなければ 1。
    /// </param>
    internal readonly record struct Result(float[] Pixels, int Width, int Height, float Exposure);

    /// <summary>
    /// <c>.hdr</c> を1枚読む。
    ///
    /// <para>
    /// 手順は3段。<b>ヘッダ(平文)→ 解像度行 → 走査線</b>。
    /// 平文とバイナリが1つのファイルに同居しているので、
    /// <see cref="StreamReader"/> のような「文字として読む道具」で開くと
    /// バッファの先読みで位置がずれる。**全部バイト列として読んで、自分で歩く**のが安全。
    /// </para>
    /// </summary>
    public static Result Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int cursor = 0;

        // --- 1. 識別行 ---
        //
        // "#?RADIANCE" か "#?RGBE"。後者は古い書き出しで出てくる。
        string magic = ReadLine(bytes, ref cursor);
        if (!magic.StartsWith("#?", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)}: Radiance の .hdr ではありません(先頭が \"{magic}\")");
        }

        // --- 2. ヘッダ(平文の key=value が空行まで続く)---
        string format = string.Empty;
        float exposure = 1.0f;

        while (true)
        {
            string line = ReadLine(bytes, ref cursor);

            // **空行がヘッダの終わり**。ここを取り違えると解像度行を1行ぶん読み飛ばす。
            if (line.Length == 0)
            {
                break;
            }

            if (line[0] == '#')
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string key = line[..equals].Trim().ToUpperInvariant();
            string value = line[(equals + 1)..].Trim();

            switch (key)
            {
                case "FORMAT":
                    format = value;
                    break;

                case "EXPOSURE":
                    // **積で効く**。仕様上 EXPOSURE は複数行書けて、掛け算で溜まっていく
                    // (露出を変える工程を通るたびに1行足される、という設計)。
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float step)
                        && step != 0.0f)
                    {
                        exposure *= step;
                    }

                    break;
            }
        }

        // 中身は RGBE のみを想定。XYZE(CIE 色空間版)は変換が別に要る。
        if (format.Length > 0 && !format.Contains("rgbe", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"{Path.GetFileName(path)}: FORMAT={format} は未対応です(32-bit_rle_rgbe のみ)");
        }

        // --- 3. 解像度行 ---
        //
        // "-Y 1024 +X 2048" の形。**符号が走査の向き**を表していて、
        // "-Y" は「Y が減る向きに並んでいる」= 上の行から先に入っている。
        // 配布されている HDRI はほぼ全部この向きで、正距円筒の 0 行目が天頂になる
        // (<see cref="SkyImage.Create"/> と同じ約束)。
        string resolution = ReadLine(bytes, ref cursor);
        string[] tokens = resolution.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length != 4 || tokens[2] is not "+X")
        {
            throw new NotSupportedException(
                $"{Path.GetFileName(path)}: 解像度行 \"{resolution}\" は未対応です(-Y h +X w のみ)");
        }

        bool topDown = tokens[0] switch
        {
            "-Y" => true,
            "+Y" => false,
            _ => throw new NotSupportedException(
                $"{Path.GetFileName(path)}: 解像度行 \"{resolution}\" は未対応です(-Y h +X w のみ)"),
        };

        int height = int.Parse(tokens[1], CultureInfo.InvariantCulture);
        int width = int.Parse(tokens[3], CultureInfo.InvariantCulture);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: 解像度が不正です({width}x{height})");
        }

        // --- 4. 走査線 ---
        //
        // 復号先は**成分ごとに固めた並び**(R が width 個、G が width 個…)。
        // 新形式の RLE がその順でデータを持っているので、そのまま受けるほうが素直。
        var scanline = new byte[width * 4];
        var pixels = new float[width * height * 3];

        // 露出が掛かっていれば**割って戻す**。EXPOSURE は「この絵は元の何倍か」なので、
        // 物理量として使うには外しておく必要がある。
        float inverseExposure = 1.0f / exposure;

        for (int y = 0; y < height; y++)
        {
            ReadScanline(bytes, ref cursor, scanline, width, path);

            // 上下の向きをここで吸収する。以降のコードは
            // 「0 行目が上端」だけを知っていればよくなる。
            int row = topDown ? y : height - 1 - y;
            int destination = row * width * 3;

            for (int x = 0; x < width; x++)
            {
                byte e = scanline[(3 * width) + x];

                if (e == 0)
                {
                    // **E = 0 は真っ黒**。2^(0-136) を掛けるのではなく、特別扱いで 0 にする。
                    // ここを分けないと、黒い画素が 10 の -41 乗という無意味に小さい値になる。
                    pixels[destination + (x * 3) + 0] = 0.0f;
                    pixels[destination + (x * 3) + 1] = 0.0f;
                    pixels[destination + (x * 3) + 2] = 0.0f;
                    continue;
                }

                // 2^(E - 128 - 8)。**ScaleB は 2 の冪を掛ける専用の関数**で、
                // MathF.Pow(2, n) より速く、丸め誤差も入らない。
                //
                // なお Radiance の原典は (R + 0.5) と半歩ずらしてから掛けている
                // (量子化の中央値を取る)。stb_image をはじめ大半の実装はずらさないので、
                // **他のツールと突き合わせやすいほう**に合わせてある。差は 1/512 未満。
                float scale = MathF.ScaleB(1.0f, e - (128 + 8)) * inverseExposure;

                pixels[destination + (x * 3) + 0] = scanline[(0 * width) + x] * scale;
                pixels[destination + (x * 3) + 1] = scanline[(1 * width) + x] * scale;
                pixels[destination + (x * 3) + 2] = scanline[(2 * width) + x] * scale;
            }
        }

        return new Result(pixels, width, height, exposure);
    }

    /// <summary>
    /// 走査線を1本復号する。**2つの形式が同じファイルに混ざりうる**。
    ///
    /// <para>
    /// 新形式(1991 年以降)は成分ごとに固めてから RLE を掛ける。
    /// 先頭 4 バイトが <c>(2, 2, 幅の上位, 幅の下位)</c> という目印になっていて、
    /// これが立っていない走査線は旧形式として読む。
    /// </para>
    ///
    /// <para>
    /// <b>なぜ成分ごとに固めるのか</b>。RGBE を画素順に並べると、
    /// 隣の画素と R・G・B・E が交互に来るので、同じ値が続きにくい。
    /// 成分ごとに分けると、たとえば E(指数)は空の広い面積でほぼ同じ値になり、
    /// **ランが一気に長くなる**。これだけで .hdr は 2〜3 倍縮む。
    /// </para>
    /// </summary>
    private static void ReadScanline(byte[] bytes, ref int cursor, byte[] scanline, int width, string path)
    {
        // 新形式は幅 8〜32767 のときだけ使われる、と仕様で決まっている。
        // 範囲外なら見るまでもなく旧形式。
        bool newFormat =
            width >= 8
            && width <= 0x7FFF
            && cursor + 4 <= bytes.Length
            && bytes[cursor] == 2
            && bytes[cursor + 1] == 2
            && (bytes[cursor + 2] & 0x80) == 0;

        if (!newFormat)
        {
            ReadFlatScanline(bytes, ref cursor, scanline, width, path);
            return;
        }

        int declared = (bytes[cursor + 2] << 8) | bytes[cursor + 3];
        if (declared != width)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)}: 走査線の幅が {declared} で、ヘッダの {width} と合いません");
        }

        cursor += 4;

        for (int plane = 0; plane < 4; plane++)
        {
            int x = 0;
            int offset = plane * width;

            while (x < width)
            {
                if (cursor >= bytes.Length)
                {
                    throw new EndOfStreamException($"{Path.GetFileName(path)}: 走査線の途中でファイルが尽きました");
                }

                int count = bytes[cursor++];

                if (count > 128)
                {
                    // **128 より大きければ「同じ値の繰り返し」**。
                    // 128 を引いた数だけ、次の 1 バイトを並べる。
                    count -= 128;
                    byte value = bytes[cursor++];

                    if (x + count > width)
                    {
                        throw new InvalidDataException($"{Path.GetFileName(path)}: ランが走査線からはみ出しました");
                    }

                    for (int i = 0; i < count; i++)
                    {
                        scanline[offset + x++] = value;
                    }
                }
                else
                {
                    // **128 以下なら「そのまま並んでいる」**。count 個ぶん写す。
                    // count = 0 は仕様上あり得ない(進まないので無限ループになる)ので弾いておく。
                    if (count == 0 || x + count > width)
                    {
                        throw new InvalidDataException($"{Path.GetFileName(path)}: 走査線の長さが不正です");
                    }

                    for (int i = 0; i < count; i++)
                    {
                        scanline[offset + x++] = bytes[cursor++];
                    }
                }
            }
        }
    }

    /// <summary>
    /// 旧形式の走査線。**RGBE が画素の順にそのまま並ぶ**。
    ///
    /// ただし1つだけ圧縮があって、<c>(1, 1, 1, n)</c> という画素は
    /// 「直前の画素を n 回繰り返す」という意味になる。
    /// さらに繰り返しが 255 を超えるときは <c>(1,1,1,n)</c> が連続し、
    /// **2 回目以降は 8 ビットずつ左にずれる**という決まりがある。
    ///
    /// <para>
    /// 今どきの書き出しでこの形式に当たることはまずないが、
    /// <b>新形式のファイルでも走査線ごとに旧形式へ落ちる</b>ことがあるので
    /// (幅が 8 未満、あるいは先頭バイトがたまたま 2 でない)、
    /// 片方だけでは読めないファイルが存在しうる。
    /// </para>
    /// </summary>
    private static void ReadFlatScanline(byte[] bytes, ref int cursor, byte[] scanline, int width, string path)
    {
        int x = 0;
        int repeatShift = 0;

        while (x < width)
        {
            if (cursor + 4 > bytes.Length)
            {
                throw new EndOfStreamException($"{Path.GetFileName(path)}: 走査線の途中でファイルが尽きました");
            }

            byte r = bytes[cursor++];
            byte g = bytes[cursor++];
            byte b = bytes[cursor++];
            byte e = bytes[cursor++];

            if (r == 1 && g == 1 && b == 1 && x > 0)
            {
                int repeat = e << repeatShift;

                for (int i = 0; i < repeat && x < width; i++)
                {
                    scanline[(0 * width) + x] = scanline[(0 * width) + x - 1];
                    scanline[(1 * width) + x] = scanline[(1 * width) + x - 1];
                    scanline[(2 * width) + x] = scanline[(2 * width) + x - 1];
                    scanline[(3 * width) + x] = scanline[(3 * width) + x - 1];
                    x++;
                }

                repeatShift += 8;
            }
            else
            {
                scanline[(0 * width) + x] = r;
                scanline[(1 * width) + x] = g;
                scanline[(2 * width) + x] = b;
                scanline[(3 * width) + x] = e;
                x++;

                repeatShift = 0;
            }
        }
    }

    /// <summary>
    /// バイト列から1行読む。**改行は LF だけを見る**。
    ///
    /// Radiance のヘッダは仕様上 LF 区切りだが、Windows で書き出されたものが
    /// CRLF になっていることがあるので、末尾の CR は落としておく。
    /// ここを落とし忘れると <c>FORMAT=32-bit_rle_rgbe</c> の末尾に CR が残り、
    /// 「未対応の形式」で弾かれる。
    /// </summary>
    private static string ReadLine(byte[] bytes, ref int cursor)
    {
        int start = cursor;

        while (cursor < bytes.Length && bytes[cursor] != (byte)'\n')
        {
            cursor++;
        }

        int end = cursor;
        if (end > start && bytes[end - 1] == (byte)'\r')
        {
            end--;
        }

        if (cursor < bytes.Length)
        {
            cursor++;
        }

        // ヘッダは ASCII と決まっている。Latin-1 で読んでおけば
        // 非 ASCII が混ざっても例外にはならない。
        return Encoding.Latin1.GetString(bytes, start, end - start);
    }
}
