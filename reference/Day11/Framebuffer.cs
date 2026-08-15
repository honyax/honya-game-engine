namespace RawGL;

/// <summary>
/// CPU側のピクセルバッファ。Day 1 のものをそのまま持ち込んだ。
///
/// Phase 1 では線分・三角形・テクスチャの描画メソッドが生えていったが、
/// Phase 2 では「絵を描くのは GPU の仕事」になるので、
/// ここには**画面に出すために最低限必要なものだけ**を残してある
/// (Day 13 で OpenGL が描くようになると、このクラス自体が要らなくなる)。
///
/// 1ピクセル = int 1個(0xAARRGGBB)という形はここでも変えない。
/// リトルエンディアンのメモリ上では B, G, R, A の順に並び、
/// これが GDI の 32bpp DIB のバイト並びとそのまま一致する。
/// おかげで <see cref="GdiPresenter"/> の転送は
/// 「配列のアドレスを渡すだけ」で済み、詰め替えが一切要らない。
/// </summary>
internal sealed class Framebuffer
{
    public int Width { get; }

    public int Height { get; }

    /// <summary>ピクセル配列。先頭が左上、行優先。</summary>
    public int[] Pixels { get; }

    public Framebuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        Pixels = new int[width * height];
    }

    /// <summary>R,G,B(各0〜255)から1ピクセル分の値を作る。</summary>
    public static int Rgb(byte r, byte g, byte b)
        => unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b));

    /// <summary>画面全体を単色で塗る。</summary>
    public void Clear(int color) => Array.Fill(Pixels, color);

    /// <summary>矩形を塗りつぶす。範囲外にはみ出した分は描画範囲側に切り詰める。</summary>
    public void FillRect(int x, int y, int width, int height, int color)
    {
        int left = Math.Max(x, 0);
        int top = Math.Max(y, 0);
        int right = Math.Min(x + width, Width);
        int bottom = Math.Min(y + height, Height);

        for (int py = top; py < bottom; py++)
        {
            int rowOffset = py * Width;
            for (int px = left; px < right; px++)
            {
                Pixels[rowOffset + px] = color;
            }
        }
    }
}
