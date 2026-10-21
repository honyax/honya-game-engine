using System.Runtime.InteropServices;
using StbTrueTypeSharp;

namespace HonyaEngine;

/// <summary>
/// フォントファイルを探す。**入っているものを使う**という割り切り。
///
/// フォントを同梱すれば環境に左右されないが、日本語フォントは 6〜14MB あって
/// リポジトリが一気に重くなる(今の <c>assets/</c> 全部で 1.5MB)。
/// Windows には日本語フォントが必ず入っているので、そちらを借りる。
///
/// **フォールバックの並び**を持つのは、実際のエンジンでも同じ。
/// 「この文字はこのフォントに無いので次のフォントで出す」まで面倒を見るのが
/// フォントフォールバックで、CJK と絵文字が混ざる文章では避けて通れない。
/// ここでは「最初に見つかった1つを使う」ところまでにして、
/// 本格的なフォールバックは改造課題にしてある。
/// </summary>
internal static class SystemFonts
{
    /// <summary>
    /// 探す順。**日本語を持つものを先に**置く。
    ///
    /// <c>.ttc</c> は TrueType Collection——1つのファイルに複数のフォントが入っている形式。
    /// メイリオなら「メイリオ / メイリオ イタリック / Meiryo UI / Meiryo UI イタリック」の4つ。
    /// 読むときにどれを使うか(添字)を指定する必要がある(<see cref="FontFace"/>)。
    /// </summary>
    private static readonly (string File, string Label)[] Preferred =
    [
        ("meiryo.ttc", "メイリオ"),
        ("YuGothM.ttc", "游ゴシック Medium"),
        ("YuGothR.ttc", "游ゴシック"),
        ("BIZ-UDGothicR.ttc", "BIZ UDゴシック"),
        ("msgothic.ttc", "MS ゴシック"),

        // ここから下は日本語を持たない。**それでも英数字は出る**ので、
        // 「何も出ない」より「日本語だけ豆腐になる」ほうがましと考えて残す。
        ("segoeui.ttf", "Segoe UI"),
        ("consola.ttf", "Consolas"),
        ("arial.ttf", "Arial"),
    ];

    public static string Directory =>
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    /// <summary>
    /// 使えるフォントを開く。
    ///
    /// <paramref name="requiredCodepoint"/> を持たないフォントは**いったん飛ばす**が、
    /// どれも持っていなければ、開けたものの中で最初のものを返す。
    /// 「日本語が出せないくらいなら落ちる」より「英数字だけでも出す」を選ぶ。
    /// </summary>
    /// <param name="requiredCodepoint">これを持つフォントを優先する。既定は「あ」。</param>
    public static FontFace? Open(int requiredCodepoint = 0x3042)
    {
        FontFace? fallback = null;

        foreach ((string file, string label) in Preferred)
        {
            string path = Path.Combine(Directory, file);
            if (!File.Exists(path))
            {
                continue;
            }

            FontFace face;
            try
            {
                face = new FontFace(path, label);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[font] {file} を開けませんでした: {exception.Message}");
                continue;
            }

            if (face.HasGlyph(requiredCodepoint))
            {
                fallback?.Dispose();
                return face;
            }

            // 日本語は無いが英数字はある。**候補としては取っておく**。
            if (fallback is null)
            {
                fallback = face;
            }
            else
            {
                face.Dispose();
            }
        }

        return fallback;
    }
}

/// <summary>
/// グリフ1つの寸法。**全部ピクセル単位**(フォント単位ではない)。
///
/// 座標系は<b>画面と同じ「下が正」</b>にそろえてある。
/// stb_truetype が返す箱もこの向き(y が下向き)なので、そのまま持つ。
/// 上向きに直すと <c>BearingY</c> の符号で必ず一度は間違えるので、
/// **画面の向きに合わせておくほうが事故が少ない**。
///
/// <code>
///          OffsetX
///         |&lt;---&gt;|
///   ------+-----+--------+------  ← ベースライン + OffsetY (OffsetY は負)
///         |     |  絵    |
///         |     +--------+
///   ======+===============------  ← ベースライン (y = 0)
///         |
///         |&lt;----- Advance ----&gt;|  次の文字の原点まで
/// </code>
/// </summary>
internal readonly struct GlyphMetrics
{
    /// <summary>絵の幅(ピクセル)。空白なら 0。</summary>
    public readonly int Width;

    public readonly int Height;

    /// <summary>原点から絵の左端まで。**負にもなる**(j や y のように左へはみ出す字)。</summary>
    public readonly int OffsetX;

    /// <summary>ベースラインから絵の上端まで。上へ伸びるので**普通は負**。</summary>
    public readonly int OffsetY;

    /// <summary>次の文字の原点までの距離。**絵の幅とは別物**。</summary>
    public readonly float Advance;

    public GlyphMetrics(int width, int height, int offsetX, int offsetY, float advance)
    {
        Width = width;
        Height = height;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Advance = advance;
    }

    /// <summary>空白のように、送りはあるが絵が無い文字。</summary>
    public bool HasPixels => Width > 0 && Height > 0;
}

/// <summary>
/// フォントファイル1つ。**stb_truetype の薄い皮**。
///
/// PNG のデコード(Day 16)を StbImageSharp に任せたのと同じ判断で、
/// TrueType のアウトライン展開は既製品に任せる。
/// 3次ベジエの塗りつぶし、ヒンティング、複合グリフの合成——
/// どれも本ロードマップの主題ではないし、まともに書くと数千行になる。
///
/// **自分で書くのは、その手前と後ろ**——
/// どのフォントを選ぶか、どの単位で測るか、どうアトラスに詰めるか、どう並べるか。
/// そこがゲームエンジン側の仕事になる。
///
/// <b>単位の話が最初の関門</b>。TrueType の座標は「フォント単位」という
/// 解像度非依存の整数で、1em が 1000 とか 2048 とかになっている。
/// ピクセルにするには倍率を掛ける。
///
/// <code>
///   scale = ScaleFor(32.0f);        // 32px にするための倍率
///   幅ピクセル = フォント単位 * scale;
/// </code>
///
/// 倍率を掛け忘れると「文字が画面いっぱいに1つだけ出る」ことになる。
/// </summary>
internal sealed class FontFace : IDisposable
{
    private readonly byte[] _data;

    /// <summary>
    /// 配列を固定するための札。
    ///
    /// <b>stb はポインタを覚えたまま持ち続ける</b>ので、
    /// GC に配列を動かされると、次の呼び出しで見当違いの場所を読む。
    /// C# 側から見ると「たまに文字化けする」「たまに落ちる」という形で出るので、
    /// 原因にたどり着くのが難しい部類のバグになる。
    ///
    /// <c>fixed</c> はブロックを抜けると外れるので使えない。
    /// フォントを開いている間ずっと固定し続ける必要があり、
    /// それができるのが <see cref="GCHandleType.Pinned"/>。
    ///
    /// なお数MBの配列は最初から LOH(大きなオブジェクト用の領域)に置かれ、
    /// LOH は既定で圧縮されないので、**固定しても断片化の実害はほぼ無い**。
    /// </summary>
    private GCHandle _pin;

    private readonly StbTrueType.stbtt_fontinfo _info;

    private readonly int _ascentUnits;
    private readonly int _descentUnits;
    private readonly int _lineGapUnits;

    public unsafe FontFace(string path, string label, int faceIndex = 0)
    {
        Path = path;
        Name = label;
        FaceIndex = faceIndex;

        _data = File.ReadAllBytes(path);
        _pin = GCHandle.Alloc(_data, GCHandleType.Pinned);

        var pointer = (byte*)_pin.AddrOfPinnedObject();

        FaceCount = StbTrueType.stbtt_GetNumberOfFonts(pointer);

        // **TrueType Collection の中の何番目か**を、バイト位置に直してもらう。
        // 単体の .ttf なら 0 が返る。ここを飛ばすと .ttc がまるごと読めない。
        int offset = StbTrueType.stbtt_GetFontOffsetForIndex(pointer, faceIndex);
        if (offset < 0)
        {
            throw new InvalidDataException($"{faceIndex} 番のフォントがありません: {path}");
        }

        _info = new StbTrueType.stbtt_fontinfo();
        if (StbTrueType.stbtt_InitFont(_info, pointer, offset) == 0)
        {
            throw new InvalidDataException($"フォントとして読めません: {path}");
        }

        int ascent, descent, lineGap;
        StbTrueType.stbtt_GetFontVMetrics(_info, &ascent, &descent, &lineGap);
        _ascentUnits = ascent;
        _descentUnits = descent;
        _lineGapUnits = lineGap;
    }

    public string Path { get; }

    public string Name { get; }

    public int FaceIndex { get; }

    /// <summary>ファイルに入っているフォントの数。<c>.ttc</c> なら 2 以上になる。</summary>
    public int FaceCount { get; }

    /// <summary>
    /// 「大きさ <paramref name="pixelHeight"/> で描く」ための倍率。
    ///
    /// <b>ここでいう「大きさ」は ascent + |descent| の高さ</b>で、
    /// em の高さでも、実際に描かれる字の高さでもない。
    /// だから「16px で指定したのに 16px より小さく見える」ことは普通に起きる。
    /// フォントの見た目の大きさが規格化されていないのは、そもそもそういうもの。
    /// </summary>
    public float ScaleFor(float pixelHeight) =>
        StbTrueType.stbtt_ScaleForPixelHeight(_info, pixelHeight);

    /// <summary>ベースラインから上へどれだけ使うか(ピクセル、正の値)。</summary>
    public float Ascent(float scale) => _ascentUnits * scale;

    /// <summary>ベースラインから下へどれだけ使うか(ピクセル、正の値に直してある)。</summary>
    public float Descent(float scale) => -_descentUnits * scale;

    /// <summary>行と行の間に足す余白。0 のフォントも多い。</summary>
    public float LineGap(float scale) => _lineGapUnits * scale;

    /// <summary>
    /// 行送り。**ascent + descent + lineGap** で決まる。
    ///
    /// 自分で「文字の高さ + 4px」のように決めてはいけない。
    /// フォントごとに字の高さも余白も違うので、フォントを差し替えた瞬間に
    /// 行がくっついたり離れたりする。**行送りはフォントが持っている情報**。
    /// </summary>
    public float LineHeight(float scale) => (_ascentUnits - _descentUnits + _lineGapUnits) * scale;

    /// <summary>
    /// この文字の絵を持っているか。
    ///
    /// 持っていない文字はグリフ番号 0(<c>.notdef</c>)に落ちる。
    /// 多くのフォントで <c>.notdef</c> は**四角い枠**、いわゆる「豆腐」になっている。
    /// </summary>
    public bool HasGlyph(int codepoint) => GlyphIndexOf(codepoint) != 0;

    /// <summary>
    /// 文字コードからグリフ番号へ。
    ///
    /// **1文字 = 1グリフではない**のがフォントの面倒なところで、
    /// 合字(fi が1つの絵になる)や異体字(<c>葛</c> の2種類)は
    /// この対応表だけでは表せない。そこまでやるには OpenType の
    /// GSUB テーブルを読む必要があり、stb_truetype は対応していない。
    /// </summary>
    public int GlyphIndexOf(int codepoint) => StbTrueType.stbtt_FindGlyphIndex(_info, codepoint);

    /// <summary>
    /// グリフの寸法を測る。**焼く前に大きさが分かる**ので、
    /// アトラスの場所を先に決められる。
    /// </summary>
    public unsafe GlyphMetrics Measure(int glyphIndex, float scale)
    {
        int advance, leftSideBearing;
        StbTrueType.stbtt_GetGlyphHMetrics(_info, glyphIndex, &advance, &leftSideBearing);

        int x0, y0, x1, y1;
        StbTrueType.stbtt_GetGlyphBitmapBox(_info, glyphIndex, scale, scale, &x0, &y0, &x1, &y1);

        return new GlyphMetrics(x1 - x0, y1 - y0, x0, y0, advance * scale);
    }

    /// <summary>
    /// グリフを塗る。**渡されたバッファに書き込む**。
    ///
    /// stb には確保まで面倒を見る <c>stbtt_GetGlyphBitmap</c> もあるが、
    /// こちらを使うと1文字ごとに malloc / free が起きる。
    /// アトラスは使い回しのバッファを1本持てば済むので、そちらに書かせる。
    ///
    /// 出てくるのは**1バイト = 被覆率(0〜255)** の並び。色は入っていない。
    /// 「その画素のどれだけが字で覆われているか」だけなので、色は描くときに掛ける。
    /// これが 1 チャンネルのテクスチャで足りる理由(<see cref="GlyphAtlas"/>)。
    /// </summary>
    /// <param name="destination">少なくとも <c>stride * height</c> バイト。</param>
    /// <param name="stride">1行のバイト数。</param>
    public unsafe void Rasterize(int glyphIndex, float scale, Span<byte> destination, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        destination.Clear();

        fixed (byte* output = destination)
        {
            StbTrueType.stbtt_MakeGlyphBitmap(_info, output, width, height, stride, scale, scale, glyphIndex);
        }
    }

    /// <summary>
    /// 2文字の間で詰める量(ピクセル)。**普通は負**(近づける)。
    ///
    /// "AV" や "To" は、送りのとおりに並べると離れて見える。
    /// フォントは「この組み合わせならこれだけ詰めろ」という表を持っていて、それがカーニング。
    ///
    /// <b>stb が読むのは古い <c>kern</c> テーブルだけ</b>。
    /// 最近のフォントは OpenType の GPOS に情報を持っていることが多く、
    /// その場合ここは常に 0 を返す。**効かないフォントがあっても実装のせいとは限らない**。
    /// 日本語は基本的に全角送りなので、そもそもカーニングをほとんど使わない。
    /// </summary>
    public float Kerning(int leftCodepoint, int rightCodepoint, float scale) =>
        StbTrueType.stbtt_GetCodepointKernAdvance(_info, leftCodepoint, rightCodepoint) * scale;

    public void Dispose()
    {
        if (_pin.IsAllocated)
        {
            _pin.Free();
        }
    }
}
