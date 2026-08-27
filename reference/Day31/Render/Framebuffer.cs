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
///   - 遅延レンダリング(位置・法線・色を別々のテクスチャに貯める。Day 51)
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
/// </summary>
internal sealed class Framebuffer : IDisposable
{
    private readonly GL _gl;
    private readonly bool _hasDepth;

    private uint _handle;

    /// <summary>深度用のレンダーバッファ。0 なら深度なし。</summary>
    private uint _depthBuffer;

    private bool _disposed;

    public Framebuffer(GL gl, int width, int height, RenderTargetFormat format, bool depth)
    {
        _gl = gl;
        _hasDepth = depth;
        Format = format;

        Create(Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>描き込み先のテクスチャ。**これを次のパスで読む**。</summary>
    public Texture Color { get; private set; } = null!;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public RenderTargetFormat Format { get; private set; }

    /// <summary>テクスチャが占める VRAM の推定バイト数。HUD に出して代償を見えるようにする。</summary>
    public long ByteSize =>
        ((long)Width * Height * (Format == RenderTargetFormat.Rgba16F ? 8 : 4))
        + (_hasDepth ? (long)Width * Height * 3 : 0);

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

    /// <summary>テクセルの持ち方を変える。大きさと同じで作り直しになる。</summary>
    public void SetFormat(RenderTargetFormat format)
    {
        if (format == Format)
        {
            return;
        }

        Format = format;

        int width = Width;
        int height = Height;
        Destroy();
        Create(width, height);
    }

    private void Create(int width, int height)
    {
        Width = width;
        Height = height;

        _handle = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _handle);

        Color = Texture.CreateTarget(_gl, width, height, Format);

        // **テクスチャを 0 番のカラーアタッチメントに挿す**。
        // 最後の 0 はミップマップのレベル。原寸に描く。
        _gl.FramebufferTexture2D(
            FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            Color.Handle,
            0);

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
        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException(
                $"フレームバッファが不完全です: {status} ({width}x{height}, {Format})");
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
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

        Color?.Dispose();
        Color = null!;
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
