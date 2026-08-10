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

    /// <summary>
    /// 2点を結ぶ線分を描く(両端を含む)。Bresenham のアルゴリズム。
    /// 整数の加算・比較・符号反転だけで完結する。
    ///
    /// 考え方: 「長い方の軸を必ず1ずつ進め、短い方は進むか留まるかを毎回選ぶ」。
    /// その選択を、理想の直線と現在位置とのズレ(誤差)を持ち回ることで決める。
    /// 誤差は本来 dy/dx という分数だが、全体を dx 倍しておけば分数が消えて整数だけで扱える。
    /// これが「割り算も浮動小数も出てこない」理由。
    ///
    /// 下の実装は8方向すべてを1つのループで処理する一般形。
    /// dx を正、dy を負にそろえ、進む向きを sx / sy に追い出すことで、
    /// 「右上がり/右下がり」「急/緩」の場合分けをコードから消している。
    /// </summary>
    public void DrawLine(int x0, int y0, int x1, int y1, int color)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);   // 符号を反転させておくのがこの一般形の定石
        int sx = x0 < x1 ? 1 : -1;     // x を進める向き
        int sy = y0 < y1 ? 1 : -1;     // y を進める向き

        // err は「今いるピクセルが理想の直線からどちら側にどれだけズレているか」。
        // dx 倍のスケールで持っているので整数のまま扱える。
        int err = dx + dy;

        while (true)
        {
            SetPixel(x0, y0, color);

            // 終点も塗ってから抜ける(両端を含む閉区間)。
            // 折れ線を描くと隣り合う線分の継ぎ目が二重に塗られるが、
            // 不透明色で塗る限り見た目に影響はない。半透明合成を始めると問題になる。
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            // 2倍するのは、本来 0.5 と比べたい判定を整数のまま行うため
            // (両辺を2倍すれば小数が消える)。この手口はラスタライザ全体で何度も出てくる。
            int e2 = err * 2;

            // dy は負なので、この比較は「x を1進めても誤差が許容範囲」の意味になる。
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            // 同じ判定を y 側にも行う。両方成立するときは斜めに1歩進む(45度に近い線)。
            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    /// <summary>
    /// 折れ線を描く。<paramref name="closed"/> が true なら終点と始点を結んで閉じる(多角形の輪郭)。
    ///
    /// 線が1本引けるようになると、多角形も曲線も「短い線分の集まり」として全部描けるようになる。
    /// 曲線を曲線のまま描く仕組みは要らない、というのがラスタライザの基本的な割り切り。
    ///
    /// 座標をタプルの Span で受けているのは、Day 5 で自作するベクトル型に置き換えるまでのつなぎ。
    /// 配列でも stackalloc でも渡せて、呼び出し側に余計なメモリ確保を強いない。
    /// </summary>
    public void DrawPolyline(ReadOnlySpan<(int X, int Y)> points, int color, bool closed = false)
    {
        if (points.Length == 0)
        {
            return;
        }

        if (points.Length == 1)
        {
            SetPixel(points[0].X, points[0].Y, color);
            return;
        }

        for (int i = 0; i + 1 < points.Length; i++)
        {
            DrawLine(points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y, color);
        }

        if (closed)
        {
            DrawLine(points[^1].X, points[^1].Y, points[0].X, points[0].Y, color);
        }
    }
}
