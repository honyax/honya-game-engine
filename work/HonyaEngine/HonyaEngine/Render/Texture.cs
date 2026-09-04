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
/// 復号済みの画像。**まだ GPU には載っていない**、ただのバイト列。
///
/// 非同期ロード(Day 21)でスレッドをまたいで受け渡すために切り出した型。
/// GL のハンドルを持たないので、どのスレッドで持ち回っても安全。
/// </summary>
/// <param name="Pixels">1テクセル4バイト、左下から右上へ並んだピクセル列。</param>
internal readonly record struct DecodedImage(byte[] Pixels, int Width, int Height);

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
    /// 画像ファイルから作る。**復号(CPU)とアップロード(GPU)を続けてやる**。
    /// </summary>
    public static Texture FromFile(GL gl, string path, bool generateMipmaps = true)
    {
        DecodedImage image = DecodeFile(path);
        return FromPixels(gl, image.Pixels, image.Width, image.Height, generateMipmaps);
    }

    /// <summary>
    /// 画像ファイルを読んで RGBA のバイト列にするところまで。**GL を一切呼ばない**。
    ///
    /// Day 21 で <see cref="FromFile"/> から切り出した。理由は非同期ロード——
    /// この部分はワーカースレッドで走らせられるが、
    /// <see cref="FromPixels"/> は GL を呼ぶので描画スレッドから動かせない。
    /// **「スレッドを選ぶ処理」と「選ばない処理」の境目**がここにある。
    /// </summary>
    public static DecodedImage DecodeFile(string path)
    {
        // **上下反転**。Day 10 の要点3(OBJ の V 座標)とまったく同じ話が再来する。
        //
        // 画像ファイルは「1行目 = 画像の一番上」で保存されているのに対し、
        // OpenGL のテクスチャは「最初のテクセル = UV(0,0)」で、
        // UV の原点は左下という約束。素直に流し込むと上下が逆さまになる。
        //
        // ここで反転しておくと、UV(0,0) を四角形の左下に割り当てたときに
        // **画像ファイルで見たとおりの向き**で表示される。
        //
        // なお、これは stb_image の**グローバルな設定**で、スレッドごとではない。
        // 今回は常に 1 を入れるので実害は無いが、
        // 「途中で 0 に戻す」コードをどこかに書いた瞬間、
        // 裏で走っている復号が巻き添えを食う。
        // **非同期化で最初に踏むのは、たいていライブラリ側のグローバル状態**。
        StbImage.stbi_set_flip_vertically_on_load(1);

        ImageResult image = ImageResult.FromMemory(
            File.ReadAllBytes(path),
            ColorComponents.RedGreenBlueAlpha);

        return new DecodedImage(image.Data, image.Width, image.Height);
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
    /// **1チャンネル(R8)の空テクスチャ**を作る。中身はあとから流し込む。
    ///
    /// フォントのグリフは色を持たない。持っているのは
    /// 「その画素のどれだけが字で覆われているか」という 0〜255 の値ひとつだけで、
    /// 色は描くときに頂点色から掛ける(<c>shaders/text.frag</c>)。
    /// RGBA で持つと、同じ値を 4 回書くために 4 倍のメモリを使うことになる。
    /// 512x512 なら 1MB が 256KB で済む。
    ///
    /// <b>ミップマップは作らない</b>。UI の文字は原寸で描くものなので、
    /// 縮小版は使われないまま容量だけ食う。
    /// <b>ClampToEdge にする</b>のも必須で、Repeat のままだと
    /// アトラスの端の字が反対側からにじんで出る。
    /// </summary>
    public static unsafe Texture CreateR8(GL gl, int width, int height)
    {
        uint handle = gl.GenTexture();

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, handle);

        gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.R8,
            (uint)width,
            (uint)height,
            0,
            PixelFormat.Red,
            PixelType.UnsignedByte,

            // null を渡すと「場所だけ確保して中身は未定義」になる。
            // 全面を自分で埋めるならこれでよいが、**アトラスは隙間が残る**ので、
            // 未定義のごみが端ににじむことがある。0 で埋めた配列を渡しておく。
            null);

        // 確保しただけでは中身が未定義なので、0 で塗りつぶす。
        var zero = new byte[width * height];
        fixed (byte* data = zero)
        {
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            gl.TexSubImage2D(
                TextureTarget.Texture2D, 0, 0, 0,
                (uint)width, (uint)height,
                PixelFormat.Red, PixelType.UnsignedByte, data);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        }

        var texture = new Texture(gl, handle, width, height, hasMipmaps: false);
        texture.SetFilter(TextureFilter.Linear);
        texture.SetWrap(TextureWrap.ClampToEdge);

        gl.BindTexture(TextureTarget.Texture2D, 0);

        return texture;
    }

    /// <summary>
    /// テクスチャの一部だけを書き換える。**アトラスに1文字ずつ足す**ために使う。
    ///
    /// <b>ここが今日いちばん有名な罠</b>。
    /// OpenGL は既定で「各行は4バイト境界にそろっている」と思って読む
    /// (<c>GL_UNPACK_ALIGNMENT</c> の既定値が 4)。
    /// RGBA なら1画素4バイトなので必ずそろうが、
    /// **1チャンネルだと幅が4の倍数のときしかそろわない**。
    ///
    /// 幅 15 のグリフを送ると、GL は「1行 16 バイト」のつもりで読み進めるので、
    /// 2行目以降が1バイトずつずれて**字が斜めに崩れる**。
    /// 幅がたまたま 4 の倍数の字(たとえば全角)だけ正しく出るので、
    /// 「一部の字だけ壊れる」という形で出て原因が分かりにくい。
    ///
    /// 直し方は1行。**送る前に 1 にして、終わったら戻す**。
    /// </summary>
    public unsafe void UploadR8(int x, int y, int width, int height, ReadOnlySpan<byte> coverage)
    {
        if (coverage.Length < width * height)
        {
            throw new ArgumentException(
                $"画素が足りません: {coverage.Length} バイト、必要なのは {width * height} バイト",
                nameof(coverage));
        }

        Bind();

        SetAlignment(1);

        fixed (byte* data = coverage)
        {
            _gl.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                x,
                y,
                (uint)width,
                (uint)height,
                PixelFormat.Red,
                PixelType.UnsignedByte,
                data);
        }

        // **他の描画に影響しないよう既定値へ戻す**。
        // GL の状態はグローバルなので、変えっぱなしにすると
        // 遠く離れた場所のテクスチャ読み込みが壊れる。
        SetAlignment(4);

        void SetAlignment(int alignment) =>
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, alignment);
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
