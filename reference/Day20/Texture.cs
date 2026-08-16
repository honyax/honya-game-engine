using Silk.NET.OpenGL;
using StbImageSharp;

namespace HonyaEngine;

/// <summary>テクスチャの拡大縮小時の補間方法。Day 8 で自作したものと同じ2種類。</summary>
internal enum TextureFilter
{
    /// <summary>最近傍。ドット絵をくっきり出したいときはこちら。</summary>
    Nearest,

    /// <summary>バイリニア。Day 8 で「テクセルを4個読む」と書いたものを GPU がやる。</summary>
    Linear,
}

/// <summary>UV が 0〜1 の外に出たときの扱い。</summary>
internal enum TextureWrap
{
    /// <summary>繰り返す。タイル状に敷き詰めたいとき。</summary>
    Repeat,

    /// <summary>端の色で引き延ばす。1枚絵をそのまま貼るとき。</summary>
    ClampToEdge,
}

/// <summary>
/// GPU 上のテクスチャ。
///
/// Phase 1 の <c>Texture</c> クラスは「ピクセル配列 + 自前のサンプリング関数」だった。
/// GPU では**サンプリングはハードウェアの仕事**になるので、こちらが持つのは
///   1. 画像データを VRAM に置くこと
///   2. 「どう読ませるか」のパラメータを設定すること
/// の2つだけになる。Day 8 で書いたバイリニア補間のコードは、
/// <see cref="TextureFilter.Linear"/> という設定1つに置き換わった。
/// </summary>
internal sealed class Texture : IDisposable
{
    private readonly GL _gl;
    private bool _disposed;

    public uint Handle { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// ミップマップを持っているか。
    ///
    /// <see cref="SetFilter"/> がこれを見て縮小フィルタを選ぶ。
    /// **ミップマップが無いのに MinFilter へ LinearMipmapLinear を指定すると、
    /// そのテクスチャは「不完全(incomplete)」になり、描くと真っ黒になる**。
    /// エラーは出ない(GL 的には合法な組み合わせで、単に条件を満たしていないだけ)ので、
    /// 「なぜか黒い」の原因としてかなり上位に来る。
    /// アトラスはミップマップを作らないので(TextureAtlas 参照)、
    /// この区別が今日から必要になった。
    /// </summary>
    public bool HasMipmaps { get; }

    private Texture(GL gl, uint handle, int width, int height, bool hasMipmaps)
    {
        _gl = gl;
        Handle = handle;
        Width = width;
        Height = height;
        HasMipmaps = hasMipmaps;
    }

    /// <summary>
    /// 画像ファイルから作る。
    /// </summary>
    public static Texture FromFile(GL gl, string path, bool generateMipmaps = true)
    {
        // **上下反転**。Day 10 の要点3(OBJ の V 座標)とまったく同じ話が再来する。
        //
        // 画像ファイルは「1行目 = 画像の一番上」で保存されているのに対し、
        // OpenGL のテクスチャは「最初のテクセル = UV(0,0)」で、
        // UV の原点は左下という約束。素直に流し込むと上下が逆さまになる。
        //
        // ここで反転しておくと、UV(0,0) を四角形の左下に割り当てたときに
        // **画像ファイルで見たとおりの向き**で表示される。
        StbImage.stbi_set_flip_vertically_on_load(1);

        ImageResult image = ImageResult.FromMemory(
            File.ReadAllBytes(path),
            ColorComponents.RedGreenBlueAlpha);

        return FromPixels(gl, image.Data, image.Width, image.Height, generateMipmaps);
    }

    /// <summary>
    /// メモリ上の RGBA 配列から作る。
    ///
    /// ファイルを介さずにテクスチャを作りたい場面はいくつもある。
    /// 今日は <see cref="TextureAtlas"/> が組み立てたピクセルを流し込むために切り出した。
    /// このあとも、フォントのグリフを焼くとき(Day 21 以降)や
    /// レンダーターゲットの内容を扱うとき(Day 31)に効いてくる。
    /// </summary>
    /// <param name="rgba">1テクセル4バイト、左下から右上へ並んだピクセル列。</param>
    public static unsafe Texture FromPixels(
        GL gl, ReadOnlySpan<byte> rgba, int width, int height, bool generateMipmaps = true)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException(
                $"ピクセル数が足りません: {rgba.Length} バイト、必要なのは {width * height * 4} バイト",
                nameof(rgba));
        }

        uint handle = gl.GenTexture();

        // ActiveTexture は「これから設定するテクスチャユニット」の指定。
        // 生成直後の設定でも、どこかのユニットにバインドしないと何も設定できない。
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, handle);

        fixed (byte* data = rgba)
        {
            gl.TexImage2D(
                TextureTarget.Texture2D,
                0,                              // ミップマップのレベル。0 が原寸
                InternalFormat.Rgba8,           // GPU 側での持ち方
                (uint)width,
                (uint)height,
                0,                              // border。常に 0(過去の遺物)
                PixelFormat.Rgba,               // 渡すデータの並び
                PixelType.UnsignedByte,
                data);
        }

        if (generateMipmaps)
        {
            // ミップマップ = 縮小版をあらかじめ作っておく仕組み。
            // 遠くの物体を描くとき、原寸から間引くとちらつく(エイリアシング)ので、
            // 縮小済みのものから読む。
            gl.GenerateMipmap(TextureTarget.Texture2D);
        }

        var texture = new Texture(gl, handle, width, height, generateMipmaps);
        texture.SetFilter(TextureFilter.Linear);
        texture.SetWrap(TextureWrap.Repeat);

        gl.BindTexture(TextureTarget.Texture2D, 0);

        return texture;
    }

    /// <summary>
    /// 指定したテクスチャユニットに結び付ける。
    ///
    /// **ユニットという段が挟まる**のが Phase 1 との大きな違い。
    /// シェーダは「何番のユニットを見ろ」としか知らず、
    /// そのユニットに何が刺さっているかは C# 側が決める。
    /// おかげで1回の描画で複数のテクスチャを使える(法線マップ等。Day 34)。
    /// </summary>
    public void Bind(TextureUnit unit = TextureUnit.Texture0)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    /// <summary>
    /// 拡大縮小時の補間方法を変える。
    ///
    /// 縮小時(MinFilter)にミップマップを使う指定があるのに対し、
    /// 拡大時(MagFilter)には無い。**拡大するときに縮小版は要らない**ので当然だが、
    /// MagFilter に LinearMipmapLinear を指定すると GL_INVALID_ENUM になる。
    /// </summary>
    public void SetFilter(TextureFilter filter)
    {
        Bind();

        // ミップマップが無いテクスチャに *MipmapNearest / *MipmapLinear を指定すると
        // 描画結果が真っ黒になる(HasMipmaps のコメント参照)。ここで振り分ける。
        (int min, int mag) = (filter, HasMipmaps) switch
        {
            (TextureFilter.Linear, true) => ((int)TextureMinFilter.LinearMipmapLinear, (int)TextureMagFilter.Linear),
            (TextureFilter.Linear, false) => ((int)TextureMinFilter.Linear, (int)TextureMagFilter.Linear),
            (_, true) => ((int)TextureMinFilter.NearestMipmapNearest, (int)TextureMagFilter.Nearest),
            (_, false) => ((int)TextureMinFilter.Nearest, (int)TextureMagFilter.Nearest),
        };

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, min);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, mag);
    }

    /// <summary>UV が 0〜1 の外に出たときの扱いを変える。</summary>
    public void SetWrap(TextureWrap wrap)
    {
        Bind();

        int mode = wrap == TextureWrap.Repeat
            ? (int)TextureWrapMode.Repeat
            : (int)TextureWrapMode.ClampToEdge;

        // S が横(U)、T が縦(V)。数学で x,y,z を使っているため
        // テクスチャ座標には s,t,r を当てる、という命名の慣習。
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, mode);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, mode);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteTexture(Handle);
    }
}
