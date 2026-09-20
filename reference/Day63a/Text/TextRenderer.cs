using System.Numerics;
using System.Text;

namespace HonyaEngine;

/// <summary>行の中でどこにそろえるか。</summary>
internal enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// 文字を並べて <see cref="SpriteBatch"/> に積む。**今日の出口**。
///
/// やっていることは、突き詰めれば
/// <b>「ペンを右へ進めながら、四角を1枚ずつ置く」</b>だけ。
/// ただし「どれだけ進めるか」に、フォントが持っている情報が3種類関わる。
///
/// <code>
///   penX += グリフの送り(Advance)          … 字ごとの幅
///   penX += カーニング(前の字との組み合わせ) … "AV" のような組だけ
///   baseline += 行送り(LineHeight)          … 改行のとき
/// </code>
///
/// <b>ベースラインが基準</b>なのが、四角を並べるのとの一番の違い。
/// 「g」や「p」は下へはみ出し、「あ」は上へ伸びる。
/// 上端をそろえて置くと、字によって上下にがたつく。
/// 揃えるのは**ベースライン**で、そこからの上下は各グリフが持っている
/// (<see cref="GlyphMetrics.OffsetY"/>)。
///
/// 描く側から見た約束は、
/// <b><paramref name="position"/> は「文字の左上」</b>。
/// UI は箱の中に置くことがほとんどなので、左上のほうが呼びやすい。
/// ベースラインは内部で <c>position.Y + Ascent</c> として求める。
/// </summary>
internal sealed class TextRenderer
{
    private readonly GlyphAtlas _atlas;

    public TextRenderer(GlyphAtlas atlas)
    {
        _atlas = atlas;
    }

    public GlyphAtlas Atlas => _atlas;

    /// <summary>
    /// カーニングを効かせるか。**切って比べると効き目が分かる**。
    /// 日本語は全角送りなのでほぼ効かない(<see cref="FontFace.Kerning"/> のコメント)。
    /// </summary>
    public bool Kerning { get; set; } = true;

    /// <summary>
    /// 描く位置を整数に丸めるか。
    ///
    /// <b>切ると文字がぼやける</b>。字の輪郭が画素の境目からずれると、
    /// 線形補間で隣の画素へにじむため。1px の線が 2px の薄い線になる。
    /// UI の文字は原寸で描くものなので、**丸めるのが既定**でよい。
    ///
    /// 逆に、文字をなめらかに動かしたい(スクロールする字幕など)ときは
    /// 丸めないほうがよい。丸めると 1px 単位でかくかく動く。
    /// **止まっている字は丸め、動く字は丸めない**が実務上の落としどころ。
    /// </summary>
    public bool PixelSnap { get; set; } = true;

    /// <summary>直前の <see cref="Draw"/> で積んだ四角の数。空白は数えない。</summary>
    public int GlyphsDrawn { get; private set; }

    public float LineHeight(int pixelHeight) => _atlas.Font.LineHeight(_atlas.Font.ScaleFor(pixelHeight));

    public float Ascent(int pixelHeight) => _atlas.Font.Ascent(_atlas.Font.ScaleFor(pixelHeight));

    /// <summary>
    /// 描かずに大きさだけ測る。**枠を先に描きたい**ときに要る。
    ///
    /// 中身は <see cref="Draw"/> とまったく同じ走査で、
    /// 四角を積むかどうかだけが違う。
    /// **同じ計算を2回書かない**ために <see cref="Layout"/> に寄せてある——
    /// measure と draw がずれると、枠から字がはみ出すという形で出る。
    /// </summary>
    public Vector2 Measure(string text, int pixelHeight)
    {
        Layout(text, pixelHeight, Vector2.Zero, default, TextAlign.Left, 0.0f, null, out Vector2 size);
        return size;
    }

    /// <summary>
    /// 文字を積む。戻り値は占めた大きさ(<see cref="Measure"/> と同じ)。
    /// </summary>
    /// <param name="position">**文字の左上**。ベースラインではない。</param>
    public Vector2 Draw(
        SpriteBatch batch,
        string text,
        Vector2 position,
        int pixelHeight,
        Vector4 color,
        TextAlign align = TextAlign.Left,
        float layer = 0.9f)
    {
        GlyphsDrawn = 0;
        Layout(text, pixelHeight, position, color, align, layer, batch, out Vector2 size);
        return size;
    }

    /// <summary>
    /// 並べる本体。<paramref name="batch"/> が <c>null</c> なら測るだけ。
    ///
    /// <b>1行ずつ2回なめている</b>——1回目で行の幅を測り、2回目で置く。
    /// 中央ぞろえ・右ぞろえには行の幅が先に要るので、こうするしかない。
    /// 左ぞろえのときも同じ道を通しているのは、**経路を分けると必ず片方だけ壊れる**から。
    /// </summary>
    private void Layout(
        string text,
        int pixelHeight,
        Vector2 position,
        Vector4 color,
        TextAlign align,
        float layer,
        SpriteBatch? batch,
        out Vector2 size)
    {
        FontFace font = _atlas.Font;
        float scale = font.ScaleFor(pixelHeight);
        float lineHeight = font.LineHeight(scale);
        float baseline = position.Y + font.Ascent(scale);

        float widest = 0.0f;
        int lineCount = 0;

        foreach (Range lineRange in SplitLines(text))
        {
            string line = text[lineRange];
            lineCount++;

            float lineWidth = MeasureLine(line, pixelHeight, scale);
            widest = MathF.Max(widest, lineWidth);

            if (batch is not null)
            {
                float startX = align switch
                {
                    TextAlign.Center => position.X - (lineWidth * 0.5f),
                    TextAlign.Right => position.X - lineWidth,
                    _ => position.X,
                };

                DrawLine(batch, line, pixelHeight, scale, startX, baseline, color, layer);
            }

            baseline += lineHeight;
        }

        size = new Vector2(widest, lineCount * lineHeight);
    }

    private float MeasureLine(string line, int pixelHeight, float scale)
    {
        float penX = 0.0f;
        int previous = 0;

        foreach (Rune rune in line.EnumerateRunes())
        {
            penX += KerningBetween(previous, rune.Value, scale);
            penX += _atlas.GetOrAdd(rune.Value, pixelHeight).Metrics.Advance;
            previous = rune.Value;
        }

        return penX;
    }

    private void DrawLine(
        SpriteBatch batch,
        string line,
        int pixelHeight,
        float scale,
        float startX,
        float baseline,
        Vector4 color,
        float layer)
    {
        float penX = startX;
        int previous = 0;

        // **サロゲートペアを1文字として回す**。
        //
        // C# の char は 16bit なので、絵文字や一部の漢字(𠮟 など)は
        // char 2個で1文字を表す。char をそのまま回すと、
        // その2個をばらばらの文字として引きに行って、両方とも豆腐になる。
        // Rune で回せば、UTF-32 のコードポイント単位で来る。
        //
        // 日本語の常用漢字は BMP(16bit で表せる範囲)に収まるので普段は困らないが、
        // **困らないうちに正しく書いておく**類の話。
        foreach (Rune rune in line.EnumerateRunes())
        {
            penX += KerningBetween(previous, rune.Value, scale);

            Glyph glyph = _atlas.GetOrAdd(rune.Value, pixelHeight);
            previous = rune.Value;

            if (glyph.HasPixels)
            {
                GlyphMetrics metrics = glyph.Metrics;

                // ベースラインからの相対位置で置く。OffsetY は普通は負(上へ)。
                float left = penX + metrics.OffsetX;
                float top = baseline + metrics.OffsetY;

                if (PixelSnap)
                {
                    left = MathF.Round(left);
                    top = MathF.Round(top);
                }

                // SpriteBatch は**中心**で受け取るので、左上から中心へ直す。
                // 丸めたあとに足すのが要点で、先に中心を出してから丸めると
                // 幅が奇数の字で 0.5px ずれる。
                batch.Draw(
                    glyph.Region,
                    new Vector2(left + (metrics.Width * 0.5f), top + (metrics.Height * 0.5f)),
                    new Vector2(metrics.Width, metrics.Height),
                    0.0f,
                    color,
                    layer);

                GlyphsDrawn++;
            }

            penX += glyph.Metrics.Advance;
        }
    }

    private float KerningBetween(int previous, int current, float scale) =>
        Kerning && previous != 0 ? _atlas.Font.Kerning(previous, current, scale) : 0.0f;

    /// <summary>
    /// 改行で切る。**<c>Split</c> を使わない**のは、
    /// 毎フレーム呼ばれる場所で string の配列を作りたくないから。
    /// <see cref="Range"/> を返せば、切り出しは必要になったときだけで済む。
    /// </summary>
    private static List<Range> SplitLines(string text)
    {
        List<Range> ranges = [];
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            // "\r\n" は \r を落とす。テキストファイルから読んだ文字列で効いてくる。
            int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            ranges.Add(start..end);
            start = i + 1;
        }

        ranges.Add(start..text.Length);
        return ranges;
    }
}
