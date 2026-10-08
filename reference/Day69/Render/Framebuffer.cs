using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **画面ではなくテクスチャへ描くための入れ物**。Day 31 の土台。
///
/// Day 14 からずっと、描いた結果は「既定のフレームバッファ」——
/// ウィンドウが用意した、画面に直結した描き込み先——へ行っていた。
/// これは <c>glBindFramebuffer(GL_FRAMEBUFFER, 0)</c> の状態で、
/// 0 番は特別扱いの「窓」を指す。
///
/// 自分でフレームバッファを作ると、行き先をテクスチャに差し替えられる。
/// そうすると<b>描いた結果をもう一度読める</b>ようになり、次の一手が全部開く。
///
///   - 画面全体に効く処理(トーンマッピング、ブルーム、被写界深度、モーションブラー)
///   - 影(光の位置から深度だけを描いて保存する。Day 33)
///   - 反射・環境マップ(別の視点から描いて貼る。Day 36)
///   - 遅延レンダリング(法線・色・材質を別々のテクスチャに貯める。Day 52。位置は距離から戻す)
///
/// **1回では終わらない描画**が全部ここから始まる、というのが今日の位置づけ。
///
/// <para>
/// フレームバッファ自体は入れ物でしかなく、中身は「アタッチメント」として外から挿す。
/// ここでは最小構成の2つだけを扱う。
///   1. <b>カラーアタッチメント</b> … <see cref="Texture"/>。あとで読むのでテクスチャにする
///   2. <b>デプスアタッチメント</b> … レンダーバッファ。**読まないのでテクスチャにしない**
/// </para>
///
/// 深度をレンダーバッファにするのは、読まないものにテクスチャの機能
/// (フィルタ、ミップマップ、サンプラ)を持たせても無駄だから。
/// レンダーバッファは「描き込み専用のメモリ」で、GPU が圧縮などの最適化をかけやすい。
/// 深度を読みたくなったら(影、SSAO)そこでテクスチャに変える——Day 33 と Day 37 でそうなる。
///
/// <para>
/// <b>Day 33 でその「読みたくなったら」が来た</b>。組み合わせが1つ増える。
///   3. <b>深度テクスチャだけ</b> … カラーを挿さない。<see cref="CreateDepthOnly"/>
/// シャドウマップは「光から見た深度」しか使わないので、色を1枚も持たない。
/// 持たせないための作法が1つあり、それが <see cref="CreateDepthOnlyAttachments"/> の
/// <c>glDrawBuffer(GL_NONE)</c>。
/// </para>
///
/// <para>
/// <b>Day 52 で4つ目の形が入った</b>。
///   4. <b>カラーを何枚も挿す</b>(MRT = Multiple Render Targets)… G-Buffer
/// 画素シェーダが <c>layout(location = N)</c> で出口を分けて書くと、
/// <b>1回の描画で N 枚へ同時に書ける</b>。Day 37 の SSAO が
/// 「法線と距離を1枚に押し込む」妥協をしていたのは、この口が無かったため。
/// ここでも1行だけ呪文が要り、それが <see cref="CreateColorAttachments"/> の <c>glDrawBuffers</c>。
/// </para>
/// </summary>
internal sealed class Framebuffer : IDisposable
{
    private readonly GL _gl;
    private readonly bool _hasDepth;

    /// <summary>
    /// **深度テクスチャだけを持つ形か**(Day 33)。
    ///
    /// カラーアタッチメントが1枚も無いフレームバッファは合法だが、
    /// <see cref="Create"/> でひと手間要る(<c>glDrawBuffer(GL_NONE)</c>)。
    /// </summary>
    private readonly bool _depthOnly;

    /// <summary>
    /// カラーアタッチメントごとの形式(Day 52)。**0 番が <see cref="Format"/>**。
    ///
    /// Day 51 までは1枚しか挿せなかったので <see cref="Format"/> 1つで足りていた。
    /// 形式を1枚ずつ持つのは、G-Buffer が<b>中身ごとに違う形式を要る</b>ため
    /// (色は 8bit で足りるが、距離と発光は 1.0 を超えるので 16F が要る)。
    /// </summary>
    private readonly RenderTargetFormat[] _formats;

    /// <summary>挿したカラーテクスチャ全部。**0 番は <see cref="Color"/> と同じもの**(Day 52)。</summary>
    private readonly List<Texture> _colors = [];

    private uint _handle;

    /// <summary>深度用のレンダーバッファ。0 なら深度なし(あるいは深度テクスチャ)。</summary>
    private uint _depthBuffer;

    private bool _disposed;

    public Framebuffer(GL gl, int width, int height, RenderTargetFormat format, bool depth)
    {
        _gl = gl;
        _hasDepth = depth;
        _depthOnly = false;
        _formats = [format];
        Format = format;

        Create(Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// **カラーを何枚も挿す**(Day 52。MRT)。<paramref name="formats"/> の順に 0, 1, 2… 番へ挿す。
    ///
    /// <para>
    /// 大きさは全部そろう——<b>MRT の約束</b>で、1枚だけ半分の大きさにはできない
    /// (不完全になる)。「アルベドは半解像度で十分」のような節約は、
    /// フレームバッファを分けないとできない。
    /// </para>
    /// </summary>
    public Framebuffer(GL gl, int width, int height, IReadOnlyList<RenderTargetFormat> formats, bool depth)
    {
        if (formats.Count == 0)
        {
            throw new ArgumentException("カラーアタッチメントが1枚も無い", nameof(formats));
        }

        _gl = gl;
        _hasDepth = depth;
        _depthOnly = false;
        _formats = [.. formats];
        Format = _formats[0];

        Create(Math.Max(1, width), Math.Max(1, height));
    }

    private Framebuffer(GL gl, int width, int height)
    {
        _gl = gl;
        _hasDepth = true;
        _depthOnly = true;
        _formats = [];
        Format = RenderTargetFormat.Rgba8;

        Create(Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>
    /// **深度テクスチャだけのフレームバッファ**を作る(Day 33)。
    ///
    /// シャドウマップは「光から見た深度」しか要らないので、色は1バイトも要らない。
    /// カラーを付けないと 1024x1024 で 4MB(24bit 深度のみ)が 3MB になる、
    /// という節約以上に、**画素シェーダが色を書く仕事ごと消える**のが効く。
    /// 深度パスが速いのはこのため(実測は計画書の「完成条件」を参照)。
    /// </summary>
    public static Framebuffer CreateDepthOnly(GL gl, int width, int height) =>
        new(gl, width, height);

    /// <summary>
    /// GL のフレームバッファ名(Day 50)。**転送(blit)の相手を指すのに要る**。
    ///
    /// バインドは <see cref="Bind"/> がやるので、普段は外から見る必要が無い。
    /// <see cref="BlitDepthTo"/> だけは<b>2つのフレームバッファを同時に指す</b>ので、
    /// 片方の名前を取り出せないと書けない。
    /// </summary>
    public uint Handle => _handle;

    /// <summary>描き込み先のテクスチャ。**これを次のパスで読む**。深度専用のときは null。</summary>
    public Texture Color { get; private set; } = null!;

    /// <summary>
    /// カラーテクスチャ全部(Day 52)。1枚しか挿していなければ <see cref="Color"/> だけが入る。
    /// </summary>
    public IReadOnlyList<Texture> Colors => _colors;

    /// <summary>
    /// 深度テクスチャ。**深度専用のときだけ入る**(Day 33)。
    ///
    /// 通常のカラー用フレームバッファでは深度はレンダーバッファなので、ここは null のまま。
    /// 「読むならテクスチャ、読まないならレンダーバッファ」の区別がそのまま型に出ている。
    /// </summary>
    public Texture? Depth { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public RenderTargetFormat Format { get; private set; }

    /// <summary>
    /// テクスチャが占める VRAM の推定バイト数。HUD に出して代償を見えるようにする。
    /// **MRT なら全部の枚数ぶん足す**(Day 52)。
    /// </summary>
    public long ByteSize => _depthOnly
        ? (long)Width * Height * 3
        : ((long)Width * Height * _formats.Sum(BytesPerPixel))
            + (_hasDepth ? (long)Width * Height * 3 : 0);

    /// <summary>
    /// 1画素あたりのバイト数。**形式が3つになったので表に分けた**(Day 37)。
    ///
    /// 三項演算子で「16F なら 8、それ以外は 4」と書いていたときに R8 を足すと、
    /// 1成分のバッファが 4 バイトとして数えられ、**HUD の VRAM が 4 倍に見える**。
    /// 数字を出すこと自体が目的の値なので、増えたぶんは必ずここに足す。
    /// </summary>
    private static int BytesPerPixel(RenderTargetFormat format) => format switch
    {
        RenderTargetFormat.Rgba16F => 8,
        RenderTargetFormat.R8 => 1,

        // Day 54 の速度バッファ。16bit x 2 で、たまたま RGBA8 と同じ 4 バイト。
        // **たまたま同じでも行を分けておく**——既定の枝に落ちて「合っている」のは、
        // 次に形式を足した日に黙って間違える形になる(Day 37 の R8 の轍)。
        RenderTargetFormat.Rg16F => 4,
        _ => 4,
    };

    /// <summary>
    /// ここへ描くように切り替える。
    ///
    /// **ビューポートも一緒に変える**のが要点。
    /// ビューポートは「クリップ座標をどのピクセル範囲に写すか」であって、
    /// フレームバッファの大きさとは独立した状態なので、
    /// 切り替えても勝手には付いてこない。
    ///
    /// 半分の大きさのバッファに描くときにこれを忘れると、
    /// **左下 1/4 にだけ絵が入り、残りが黒いまま**になる。
    /// ぼかしの途中結果がおかしいときは、まずここを疑う。
    /// </summary>
    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _handle);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    /// <summary>
    /// 画面(既定のフレームバッファ)へ戻す。
    ///
    /// **0 番は「フレームバッファを外す」ではなく「窓を指す」**。
    /// ウィンドウシステムが用意したもので、自分では作れないし壊せない。
    /// </summary>
    public static void BindDefault(GL gl, int width, int height)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
    }

    /// <summary>
    /// 大きさを変える。**中身は作り直しになる**。
    ///
    /// テクスチャの大きさは <c>glTexImage2D</c> のときに決まるので、
    /// 「あとから伸ばす」ということができない。ウィンドウをドラッグでリサイズすると
    /// 毎フレームここが呼ばれるが、確保と解放だけなので実測で 0.1ms 未満。
    /// 気になるなら「大きくなるときだけ作り直し、小さいときは一部だけ使う」
    /// という作りにもできる(実際のエンジンはそうしていることが多い)。
    /// </summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        if (width == Width && height == Height)
        {
            return;
        }

        Destroy();
        Create(width, height);
    }

    /// <summary>テクセルの持ち方を変える。大きさと同じで作り直しになる。MRT なら 0 番だけ。</summary>
    public void SetFormat(RenderTargetFormat format)
    {
        if (format == Format)
        {
            return;
        }

        Format = format;
        _formats[0] = format;

        int width = Width;
        int height = Height;
        Destroy();
        Create(width, height);
    }

    /// <summary>
    /// **深度だけを別のフレームバッファへ写す**(Day 50)。ソフトパーティクルの下ごしらえ。
    ///
    /// <para>
    /// <b>なぜコピーが要るのか</b>。ソフトパーティクルは
    /// 「いま描いている画素の奥に、どれだけ近い面があるか」を読む。
    /// その深度は<b>まさにいま描き込み先として挿さっている</b>ものなので、
    /// そのまま <c>sampler2D</c> で読むと<b>フィードバックループ</b>になる——
    /// OpenGL の仕様では結果が未定義で、
    /// ドライバによっては動き、別のドライバでは真っ黒になり、
    /// <b>どちらの場合もエラーは出ない</b>。いちばん怖い種類の間違いになる。
    /// </para>
    ///
    /// <para>
    /// 逃げ方は「読む用のコピーを1枚持つ」。実際のエンジンでも
    /// 半透明を描く直前に深度を解決(resolve)してから渡すのが定石で、
    /// Unity の <c>_CameraDepthTexture</c> はまさにこれになる。
    /// </para>
    ///
    /// <para>
    /// <c>glBlitFramebuffer</c> は<b>GPU 内のコピー</b>なので CPU は待たない。
    /// フィルタが <c>Nearest</c> でなければならないのは深度だからで、
    /// <c>Linear</c> を指定すると <c>GL_INVALID_OPERATION</c> になる
    /// (深度の平均に意味が無いのは <see cref="Texture.CreateDepthTarget"/> と同じ話)。
    /// </para>
    /// </summary>
    public void BlitDepthTo(Framebuffer destination)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _handle);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, destination.Handle);

        _gl.BlitFramebuffer(
            0, 0, Width, Height,
            0, 0, destination.Width, destination.Height,
            ClearBufferMask.DepthBufferBit,
            BlitFramebufferFilter.Nearest);

        // **借りた状態は返す**。読み書きの割り当てを残したまま次の描画へ行くと、
        // 「なぜかシーンではなくコピーへ描かれている」という形で後から出る。
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
    }

    private void Create(int width, int height)
    {
        Width = width;
        Height = height;

        _handle = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _handle);

        if (_depthOnly)
        {
            CreateDepthOnlyAttachments(width, height);
        }
        else
        {
            CreateColorAttachments(width, height);
        }

        // **完全性(completeness)の確認は必須**。
        //
        // フレームバッファは組み合わせが自由なぶん、GPU が描けない組み合わせも作れてしまう。
        // 不完全なフレームバッファに描いても**エラーは出ず、ただ何も起きない**——
        // 画面が真っ黒になるだけで、原因を教えてもらえない。
        // ここで一度確認しておけば、少なくとも「作った時点で壊れていた」かは分かる。
        //
        // よくある不完全の原因:
        //   - カラーもデプスも挿していない(アタッチメントが1つも無い)
        //   - カラーとデプスで大きさが違う
        //   - GPU がその内部形式にレンダリングできない(古い環境の Rgba16f など)
        //   - **深度だけ挿したのに glDrawBuffer(GL_NONE) を忘れた**(Day 33。下を参照)
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException(
                $"フレームバッファが不完全です: {status} ({width}x{height}, {(_depthOnly ? "深度のみ" : Format.ToString())})");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// カラーテクスチャ + 深度レンダーバッファ。Day 31 からの形。
    /// **Day 52 から何枚でも挿せる**(1枚なら Day 51 までとまったく同じ動きになる)。
    /// </summary>
    private void CreateColorAttachments(int width, int height)
    {
        for (int i = 0; i < _formats.Length; i++)
        {
            Texture texture = Texture.CreateTarget(_gl, width, height, _formats[i]);

            // **テクスチャを i 番のカラーアタッチメントに挿す**。
            // 最後の 0 はミップマップのレベル。原寸に描く。
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0 + i,
                TextureTarget.Texture2D,
                texture.Handle,
                0);

            _colors.Add(texture);
        }

        Color = _colors[0];

        // **挿しただけでは 0 番にしか描かれない**(Day 52)。
        //
        // フレームバッファの既定の「描く先」は COLOR_ATTACHMENT0 の1枚だけで、
        // 1〜3 番を挿しても、画素シェーダの location = 1〜3 の出口は<b>どこにも繋がっていない</b>。
        // glDrawBuffers で「location N → アタッチメント N」の対応表を渡して初めて全部に書かれる。
        //
        // 忘れたときの症状が厄介で、**完全性チェックは通る**(挿し方は合法)うえ、
        // 0 番のアルベドだけは正しく出る。法線と材質だけが真っ黒のまま、エラーは1つも出ない。
        // Day 33 の glDrawBuffer(GL_NONE) と同じ種類の呪文で、こちらは「何枚に描くか」を言う。
        if (_colors.Count > 1)
        {
            Span<DrawBufferMode> buffers = stackalloc DrawBufferMode[_colors.Count];
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = DrawBufferMode.ColorAttachment0 + i;
            }

            _gl.DrawBuffers((uint)buffers.Length, buffers);
        }

        if (_hasDepth)
        {
            _depthBuffer = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthBuffer);
            _gl.RenderbufferStorage(
                RenderbufferTarget.Renderbuffer,
                InternalFormat.DepthComponent24,
                (uint)width,
                (uint)height);

            _gl.FramebufferRenderbuffer(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer,
                _depthBuffer);

            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        }
    }

    /// <summary>
    /// **深度テクスチャだけ**を挿す(Day 33)。
    ///
    /// ここに1行だけ、他では出てこない呪文が要る。
    ///
    /// <code>
    /// glDrawBuffer(GL_NONE);
    /// glReadBuffer(GL_NONE);
    /// </code>
    ///
    /// OpenGL のフレームバッファは既定で「0 番のカラーアタッチメントへ描く」つもりでいる。
    /// カラーを1枚も挿していないのにそのままにしておくと、
    /// **「描く先が無い」で不完全(GL_FRAMEBUFFER_INCOMPLETE_DRAW_BUFFER)**になる。
    /// 「色を出力しない」と明示して初めて完全になる、という理屈。
    ///
    /// これを忘れたときの症状が分かりやすくて、
    /// <see cref="Create"/> の完全性チェックがそのまま例外で教えてくれる。
    /// チェックを入れていなければ「影が出ない」だけになり、
    /// 光源行列を疑って何時間も溶かすことになる——Day 31 でチェックを書いた配当がここで出る。
    /// </summary>
    private void CreateDepthOnlyAttachments(int width, int height)
    {
        Depth = Texture.CreateDepthTarget(_gl, width, height);

        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D,
            Depth.Handle,
            0);

        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
    }

    private void Destroy()
    {
        if (_depthBuffer != 0)
        {
            _gl.DeleteRenderbuffer(_depthBuffer);
            _depthBuffer = 0;
        }

        if (_handle != 0)
        {
            _gl.DeleteFramebuffer(_handle);
            _handle = 0;
        }

        // **全部の枚数を捨てる**(Day 52)。Color は _colors[0] と同じものなので二重には捨てない。
        foreach (Texture texture in _colors)
        {
            texture.Dispose();
        }

        _colors.Clear();
        Color = null!;

        Depth?.Dispose();
        Depth = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Destroy();
    }
}
