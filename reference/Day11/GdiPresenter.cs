using System.Runtime.InteropServices;

namespace RawGL;

/// <summary>
/// <see cref="Framebuffer"/> の中身をウィンドウへ転送する係。
/// Day 1 の <c>Bitmap.LockBits</c> + <c>Graphics.DrawImage</c> の置き換え。
///
/// Day 1 で使った GDI+(System.Drawing)は、GDI の上に乗った
/// 「アンチエイリアスも変形もできる高機能な描画ライブラリ」。
/// ここで使う <c>StretchDIBits</c> はその下の素の GDI の関数で、
/// できることは「メモリ上のピクセル配列をDCへ流し込む」だけ。
/// 今回の用途では余計な機能は全部無駄なので、下の層を直接叩くほうが速い。
///
/// このクラスは **Day 12〜13 で消える**。
/// OpenGL のコンテキストを持てば、画面への転送は wglSwapBuffers 1発になる。
/// </summary>
internal sealed class GdiPresenter : IDisposable
{
    private readonly IntPtr _hwnd;

    /// <summary>
    /// デバイスコンテキスト。「どこに、どういう設定で描くか」を束ねた GDI のハンドル。
    ///
    /// 毎フレーム GetDC / ReleaseDC する実装も見かけるが、ウィンドウクラスに
    /// CS_OWNDC を付けてあるので、このウィンドウ専用のDCが1枚固定で割り当たっている。
    /// 1回取って持ち続けるのが正しい使い方。
    /// </summary>
    private readonly IntPtr _hdc;

    /// <summary>
    /// ピクセル配列の「読み方」を GDI に教える説明書。毎フレーム同じ内容なので使い回す。
    /// StretchDIBits に ref で渡すため、readonly にはできない。
    /// </summary>
    private Win32.BITMAPINFO _bitmapInfo;

    private bool _disposed;

    public GdiPresenter(IntPtr hwnd, int width, int height)
    {
        _hwnd = hwnd;
        _hdc = Win32.GetDC(hwnd);

        if (_hdc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC に失敗した");
        }

        _bitmapInfo.bmiHeader = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
            biWidth = width,

            // 負 = トップダウン。配列の先頭が画像の一番上の行、という宣言。
            // ここを正のままにすると、絵が上下逆さまに表示される。
            biHeight = -height,

            // プレーン数。RGB を別々の面に分けて持っていた時代の名残で、常に 1。
            biPlanes = 1,

            // 32bpp。1ピクセル = int 1個という Framebuffer の設計とそのまま対応する。
            biBitCount = 32,

            biCompression = Win32.BI_RGB,
        };
    }

    /// <summary>
    /// フレームバッファを画面へ転送する(GPU で言う Present / SwapBuffers)。
    ///
    /// 転送元と転送先で同じ幅・高さを渡しているので、拡大縮小は起きない
    /// (等倍なら StretchDIBits は内部で単純なコピー経路を通る)。
    /// クライアント領域のサイズをフレームバッファに合わせてあるのが前提。
    /// </summary>
    public void Present(Framebuffer framebuffer)
    {
        Win32.StretchDIBits(
            _hdc,
            0, 0, framebuffer.Width, framebuffer.Height,   // 転送先(クライアント座標)
            0, 0, framebuffer.Width, framebuffer.Height,   // 転送元(ビットマップ座標)
            framebuffer.Pixels,
            ref _bitmapInfo,
            Win32.DIB_RGB_COLORS,
            Win32.SRCCOPY);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // CS_OWNDC のDCは ReleaseDC しても解放されない(ウィンドウと寿命を共にする)が、
        // GDI ハンドルは 1プロセス 10,000個の上限があるリソースなので、
        // 「取ったら返す」を習慣にしておくほうがよい。
        if (_hdc != IntPtr.Zero)
        {
            Win32.ReleaseDC(_hwnd, _hdc);
        }
    }
}
