namespace SoftwareRasterizer;

/// <summary>
/// CPU側のピクセルバッファ(フレームバッファ)。
///
/// GPUを使う場合の「バックバッファ」に相当するもので、これから先のDayで作る
/// 線分描画・三角形塗りつぶし・テクスチャマッピングは、すべて最終的に
/// このクラスの <see cref="Pixels"/> に値を書き込む作業に帰着する。
/// つまりソフトウェアラスタライザの出力先はここ1箇所しかない。
/// </summary>
internal sealed class Framebuffer
{
    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// ピクセル配列。1ピクセル = int 1個(32bit)で 0xAARRGGBB 形式。
    ///
    /// byte[] ではなく int[] にしている理由:
    /// - インデックス計算が y * Width + x だけで済み、*4 が要らない
    /// - 1ピクセルの読み書きが1命令で済む(byte[]だと4回に分かれる)
    /// - GDI+への転送に使う Marshal.Copy が int[] のオーバーロードを持っている
    ///
    /// 2次元配列 [y, x] ではなく1次元にしているのは、メモリが連続していて
    /// 境界チェックも1回で済み、実測でこちらが速いため。以降のDayでも一貫してこの形を使う。
    /// </summary>
    public int[] Pixels { get; }

    public Framebuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        Width = width;
        Height = height;
        Pixels = new int[width * height];
    }

    /// <summary>
    /// R,G,B(各0〜255)から1ピクセル分の値を作る。
    ///
    /// 値の並びが 0xAARRGGBB なのは、これをメモリ(リトルエンディアン)に置くと
    /// バイト列が B, G, R, A の順になり、GDI+ の 32bpp フォーマットの
    /// メモリ配置とそのまま一致するから。この一致のおかげで、後の転送は
    /// 単なるメモリコピーだけで済み、ピクセルごとの並べ替えが一切要らない。
    /// </summary>
    public static int Rgb(byte r, byte g, byte b)
        => unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b));

    /// <summary>画面全体を単色で塗る。毎フレームの描画は基本的にここから始まる。</summary>
    public void Clear(int color) => Array.Fill(Pixels, color);

    /// <summary>
    /// 1ピクセル書き込む。範囲外は黙って捨てる(クリッピング)。
    /// 「はみ出した座標が来ても落ちない」ことは、この先の描画コードを書くうえで
    /// 前提にしたい性質なので、最下層のここで面倒を見ておく。
    /// </summary>
    public void SetPixel(int x, int y, int color)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            // 負数を uint にキャストすると巨大な値になるため、
            // この1回の比較で「0未満」と「幅以上」の両方を判定できる。
            return;
        }

        Pixels[y * Width + x] = color;
    }

    /// <summary>矩形を塗りつぶす。範囲外にはみ出した分は描画範囲側に切り詰める。</summary>
    public void FillRect(int x, int y, int width, int height, int color)
    {
        int left = Math.Max(x, 0);
        int top = Math.Max(y, 0);
        int right = Math.Min(x + width, Width);
        int bottom = Math.Min(y + height, Height);

        for (int py = top; py < bottom; py++)
        {
            // 行の先頭インデックスを外側で1回だけ計算する。
            // 内側ループで毎回 py * Width をやり直さないのは、この手のループが
            // ソフトウェアラスタライザでは最内周(=最も回数が多い場所)になるため。
            int rowOffset = py * Width;
            for (int px = left; px < right; px++)
            {
                Pixels[rowOffset + px] = color;
            }
        }
    }
}
