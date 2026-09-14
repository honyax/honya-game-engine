using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// アトラスに焼かれたグリフ1つ。**切り出し位置 + 置き場所**。
///
/// <see cref="AtlasRegion"/>(Day 17)をそのまま持っているので、
/// <see cref="SpriteBatch.Draw(in AtlasRegion, Vector2, Vector2, float, Vector4, float)"/>
/// に何の変換もなく渡せる。**文字も結局スプライト1枚**でしかない。
/// </summary>
internal readonly struct Glyph
{
    public readonly AtlasRegion Region;
    public readonly GlyphMetrics Metrics;

    public Glyph(AtlasRegion region, in GlyphMetrics metrics)
    {
        Region = region;
        Metrics = metrics;
    }

    public bool HasPixels => Metrics.HasPixels;
}

/// <summary>
/// 使った文字だけを焼き溜めるテクスチャ。**Day 17 の棚詰めを実行中にやる版**。
///
/// Day 17 の <see cref="TextureAtlas"/> は、詰めるものが全部そろってから
/// **高さでソートして**棚に並べた。今日はそれができない——
/// どの文字が必要になるかは、実際に描くまで分からないから。
/// 日本語は常用漢字だけで 2136 字、JIS 第1・第2水準まで入れると 6355 字ある。
/// **全部焼くと 32px でも 6MB 超**、しかも起動時に数秒かかる。
///
/// なので「来た順に棚へ置く」。高さでソートできないぶん棚に隙間ができるが、
/// 実際に使う文字はゲーム1本でせいぜい数百字なので、512x512 で足りる。
///
/// <code>
///   +--------------------------------+
///   | A B C あ 漢 ...                |  ← 棚1(高さはこの段でいちばん高い字)
///   +--------------------------------+
///   | 字 を 追 加 す る と ...        |  ← 棚2
///   +--------------------------------+
///   |                                |  ← まだ空き
///   +--------------------------------+
/// </code>
///
/// <b>キーには大きさも入る</b>。同じ「あ」でも 16px と 32px では別の絵なので、
/// <c>(文字コード, ピクセル高さ)</c> の組で引く。
/// だから**サイズを増やすとアトラスを食う**。
/// 大きさを自由にしたいなら SDF(改造課題3)へ進むことになる。
/// </summary>
internal sealed class GlyphAtlas : IDisposable
{
    /// <summary>
    /// グリフどうしの隙間。
    ///
    /// **線形補間は隣の画素を混ぜる**ので、ぴったり詰めると
    /// 隣の字の端が薄くにじんで出る。1px 空けておけば、
    /// 拡大しても混ざるのは空白だけになる。
    /// </summary>
    private const int Padding = 1;

    private readonly GL _gl;
    private readonly FontFace _font;
    private readonly Texture _texture;

    /// <summary>(文字コード, 大きさ)→ グリフ。<b>大きさが違えば別のグリフ</b>。</summary>
    private readonly Dictionary<long, Glyph> _glyphs = [];

    /// <summary>塗るときの作業場。**使い回す**ので、焼くたびの割り当てが起きない。</summary>
    private byte[] _scratch = new byte[128 * 128];

    // --- 棚の状態。この3つだけで詰め位置が決まる ---

    /// <summary>今の棚の下端(テクスチャの下から数えた行)。</summary>
    private int _shelfY;

    /// <summary>今の棚の高さ。**その段でいちばん高い字**で決まる。</summary>
    private int _shelfHeight;

    /// <summary>今の棚の書き込み位置(左から)。</summary>
    private int _penX;

    public GlyphAtlas(GL gl, FontFace font, int size = 512)
    {
        _gl = gl;
        _font = font;
        Size = size;

        // **1チャンネルで足りる**。グリフは色を持たず、被覆率だけを持つ。
        // RGBA で持つと 4 倍のメモリを、同じ値を 4 回書くために使うことになる。
        _texture = Texture.CreateR8(gl, size, size);
        ShelfCount = 1;
    }

    public int Size { get; }

    public Texture Texture => _texture;

    public FontFace Font => _font;

    public int GlyphCount => _glyphs.Count;

    public int ShelfCount { get; private set; }

    /// <summary>このフレームで新しく焼いた数。**急に増えたらカクつく合図**。</summary>
    public int BakedThisFrame { get; private set; }

    public int BakedTotal { get; private set; }

    /// <summary>もう入らない。以降は既に焼いた字しか出せない。</summary>
    public bool IsFull { get; private set; }

    /// <summary>棚が使った高さの割合。100% に近づいたら大きくするか捨てるかを考える。</summary>
    public float Usage => (_shelfY + _shelfHeight) / (float)Size;

    /// <summary>1フレームの頭で呼ぶ。焼いた数の集計を戻す。</summary>
    public void BeginFrame() => BakedThisFrame = 0;

    /// <summary>
    /// 文字を引く。**無ければその場で焼く**。
    ///
    /// 呼ぶ側は「あるかどうか」を気にしない。
    /// 初回だけ焼く時間がかかり、2回目以降は辞書を引くだけになる。
    /// Day 21 で作った窓口(<see cref="RenderResources"/>、当時の名前は <c>ResourceManager</c>)の
    /// キャッシュと同じ考え方だが、
    /// **こちらは同期で焼く**——グリフ1つは 1ms もかからないので、
    /// 非同期にする価値より複雑さのほうが勝つ。
    /// </summary>
    public Glyph GetOrAdd(int codepoint, int pixelHeight)
    {
        long key = ((long)pixelHeight << 32) | (uint)codepoint;

        if (_glyphs.TryGetValue(key, out Glyph cached))
        {
            return cached;
        }

        Glyph glyph = Bake(codepoint, pixelHeight);
        _glyphs[key] = glyph;
        return glyph;
    }

    private Glyph Bake(int codepoint, int pixelHeight)
    {
        float scale = _font.ScaleFor(pixelHeight);

        // 持っていない文字はグリフ 0(.notdef)になる。**それも焼く**。
        // 多くのフォントで四角い枠(豆腐)が入っていて、
        // 「文字が抜けている」ことが目に見えるほうが、黙って消えるよりよい。
        int glyphIndex = _font.GlyphIndexOf(codepoint);
        GlyphMetrics metrics = _font.Measure(glyphIndex, scale);

        if (!metrics.HasPixels)
        {
            // 空白。送りだけ持って、絵は持たない。
            return new Glyph(default, metrics);
        }

        int width = metrics.Width;
        int height = metrics.Height;

        if (!TryAllocate(width, height, out int x, out int y))
        {
            IsFull = true;

            // 場所が無い。**送りだけは正しく返す**ので、
            // 絵は出ないがレイアウトは崩れない。
            return new Glyph(default, new GlyphMetrics(0, 0, metrics.OffsetX, metrics.OffsetY, metrics.Advance));
        }

        int needed = width * height;
        if (_scratch.Length < needed)
        {
            _scratch = new byte[Math.Max(needed * 2, 256)];
        }

        _font.Rasterize(glyphIndex, scale, _scratch, width, height, width);

        // **上下を入れ替えてから送る**。
        //
        // stb が返すのは「上の行から」の並び。OpenGL のテクスチャは
        // 左下が原点なので、そのまま送ると字が逆さまになる。
        // Day 16 で PNG を読むときに stbi_set_flip_vertically_on_load を立てたのと同じ話で、
        // **「上が先か下が先か」は毎回どこかで1回ひっくり返す**ことになる。
        FlipRows(_scratch, width, height);

        _texture.UploadR8(x, y, width, height, _scratch.AsSpan(0, needed));

        BakedThisFrame++;
        BakedTotal++;

        var region = new AtlasRegion(
            _texture,
            new Vector2(x / (float)Size, y / (float)Size),
            new Vector2((x + width) / (float)Size, (y + height) / (float)Size),
            width,
            height);

        return new Glyph(region, metrics);
    }

    /// <summary>
    /// 棚に場所を取る。**入らなければ次の段へ、それも無理なら諦める**。
    ///
    /// Day 17 との違いはここだけ。あちらは全部の絵が手元にあったので
    /// 高さの降順に並べてから詰められた(段の無駄が小さい)。
    /// こちらは来た順なので、低い字のあとに高い字が来ると段が丸ごと高くなる。
    /// **同じサイズの文字ばかり来る**という実際の使われ方に助けられている。
    /// </summary>
    private bool TryAllocate(int width, int height, out int x, out int y)
    {
        int stepX = width + Padding;
        int stepY = height + Padding;

        if (_penX + stepX > Size)
        {
            // 今の段には入らない。**次の段の下端は、今の段の高さぶん上**。
            _shelfY += _shelfHeight;
            _shelfHeight = 0;
            _penX = 0;
            ShelfCount++;
        }

        if (_shelfY + stepY > Size)
        {
            x = 0;
            y = 0;
            return false;
        }

        x = _penX;
        y = _shelfY;

        _penX += stepX;
        _shelfHeight = Math.Max(_shelfHeight, stepY);
        return true;
    }

    private static void FlipRows(byte[] pixels, int width, int height)
    {
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Span<byte> a = pixels.AsSpan(top * width, width);
            Span<byte> b = pixels.AsSpan(bottom * width, width);

            for (int i = 0; i < width; i++)
            {
                (a[i], b[i]) = (b[i], a[i]);
            }
        }
    }

    public void Dispose() => _texture.Dispose();
}
