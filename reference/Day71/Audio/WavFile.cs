namespace HonyaEngine;

/// <summary>
/// WAV から読み出した中身。**PCM のバイト列とその読み方**。
///
/// <see cref="Data"/> は生のバイト列のまま持つ。
/// <c>float[]</c> に展開してから渡すこともできるが、
/// OpenAL に渡すときに結局バイト列へ戻すことになるので、
/// **変換を挟まないほうが速いし、ずれる余地も無い**。
/// </summary>
internal readonly struct WavData
{
    public readonly byte[] Data;
    public readonly int SampleRate;
    public readonly int Channels;
    public readonly int BitsPerSample;

    public WavData(byte[] data, int sampleRate, int channels, int bitsPerSample)
    {
        Data = data;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
    }

    /// <summary>1フレーム(全チャンネルぶん1組)のバイト数。</summary>
    public int BytesPerFrame => Channels * (BitsPerSample / 8);

    public int FrameCount => BytesPerFrame == 0 ? 0 : Data.Length / BytesPerFrame;

    /// <summary>秒。<b>再生時間はバイト数から決まる</b>ので、ヘッダに書いてある必要がない。</summary>
    public float Duration => SampleRate == 0 ? 0.0f : (float)FrameCount / SampleRate;
}

/// <summary>
/// WAV(RIFF)の読み込み。**自分で書く**。
///
/// PNG のデコード(Day 16)は既製品(StbImageSharp)に任せたのに、
/// WAV は自分で書く。理由は難しさが2桁違うから——
/// PNG は zlib 展開とフィルタの復元が要るが、
/// **WAV(非圧縮 PCM)はヘッダを読み飛ばして残りをそのまま渡すだけ**で終わる。
/// 100 行で書けるものを外部依存にする理由は無いし、
/// 「音のデータが実際どう並んでいるか」を一度は自分で見ておく価値がある。
///
/// RIFF の構造は**入れ子の箱**になっている。
///
/// <code>
///   "RIFF" | 全体の長さ | "WAVE"
///     "fmt " | 長さ | フォーマット情報(16〜40 バイト)
///     "LIST" | 長さ | 制作者名など。**読み飛ばす**
///     "data" | 長さ | PCM 本体
/// </code>
///
/// 実装で外してはいけないところが3つある。
///
/// 1. <b>知らないチャンクは長さぶん読み飛ばす</b>。
///    「fmt の次は data」と決め打ちすると、
///    多くの編集ソフトが書き込む <c>LIST</c> / <c>fact</c> / <c>cue </c> で破綻する。
///    **未知のものを安全に飛ばせる**のがチャンク形式の値打ちそのもの
/// 2. <b>チャンクは偶数バイト境界にそろう</b>。
///    長さが奇数のときは 1 バイトのパディングが入る。
///    これを忘れると、その次のチャンク名が 1 バイトずれて読めなくなる
/// 3. <b>8bit は符号なし、16bit は符号付き</b>。
///    8bit は 0〜255 で中央が 128、16bit は -32768〜32767 で中央が 0。
///    歴史的な事情でこうなっているだけだが、間違えると
///    「8bit の音だけ盛大に歪む」という形で出る
///
/// リトルエンディアン固定なので <see cref="BitConverter"/> がそのまま使える
/// (x86/ARM はどちらもリトルエンディアン)。
/// </summary>
internal static class WavFile
{
    /// <summary>非圧縮 PCM のフォーマット番号。**これ以外は扱わない**。</summary>
    private const ushort FormatPcm = 1;

    /// <summary>浮動小数点 PCM。番号だけ知っておくとエラーメッセージが親切になる。</summary>
    private const ushort FormatFloat = 3;

    public static WavData Load(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>
    /// バイト列から読む。**ファイルから切り離してある**ので、
    /// 自己チェックがメモリ上で組み立てた WAV をそのまま流し込める。
    /// </summary>
    public static WavData Parse(ReadOnlySpan<byte> bytes, string name = "(メモリ)")
    {
        if (bytes.Length < 12
            || bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F'
            || bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
        {
            throw new InvalidDataException($"WAV ではありません: {name}");
        }

        int sampleRate = 0;
        int channels = 0;
        int bits = 0;
        byte[]? data = null;

        // 12 バイト目("WAVE" の直後)からチャンクが並ぶ。
        int offset = 12;

        while (offset + 8 <= bytes.Length)
        {
            ReadOnlySpan<byte> id = bytes.Slice(offset, 4);
            uint size = BitConverter.ToUInt32(bytes.Slice(offset + 4, 4));
            int body = offset + 8;

            // 壊れたファイルで「長さが残りより大きい」ことがある。
            // そのまま Slice すると例外の内容が読み取り不能になるので、ここで丸める。
            int available = Math.Min((int)size, bytes.Length - body);

            if (Matches(id, "fmt "))
            {
                ushort format = BitConverter.ToUInt16(bytes.Slice(body, 2));
                channels = BitConverter.ToUInt16(bytes.Slice(body + 2, 2));
                sampleRate = (int)BitConverter.ToUInt32(bytes.Slice(body + 4, 4));

                // body + 8 は「1秒あたりのバイト数」、body + 12 は「1フレームのバイト数」。
                // どちらも他の値から計算できるので読まない(**冗長なフィールドは信じない**)。
                bits = BitConverter.ToUInt16(bytes.Slice(body + 14, 2));

                if (format != FormatPcm)
                {
                    throw new NotSupportedException(
                        format == FormatFloat
                            ? $"float PCM は未対応: {name}"
                            : $"非圧縮 PCM 以外は未対応(format={format}): {name}");
                }
            }
            else if (Matches(id, "data"))
            {
                data = bytes.Slice(body, available).ToArray();
            }

            // **知らないチャンクはここで飛ぶ**。長さを足すだけでよい。
            offset = body + available;

            // 奇数長のチャンクの後ろには 1 バイトの詰め物が入る。
            if ((available & 1) != 0)
            {
                offset++;
            }
        }

        if (data is null || channels == 0 || sampleRate == 0)
        {
            throw new InvalidDataException($"fmt か data が見つかりません: {name}");
        }

        if (bits != 8 && bits != 16)
        {
            // 24bit / 32bit は OpenAL の基本セットに対応する形式が無く、
            // 変換が要る。今日の範囲では扱わない。
            throw new NotSupportedException($"{bits}bit は未対応(8 か 16 のみ): {name}");
        }

        if (channels != 1 && channels != 2)
        {
            throw new NotSupportedException($"{channels}ch は未対応: {name}");
        }

        return new WavData(data, sampleRate, channels, bits);
    }

    private static bool Matches(ReadOnlySpan<byte> id, string text) =>
        id[0] == text[0] && id[1] == text[1] && id[2] == text[2] && id[3] == text[3];
}
