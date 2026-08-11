namespace SoftwareRasterizer;

/// <summary>
/// 三角形ラスタライザ。
///
/// なぜ <see cref="Framebuffer"/> に足さずクラスを分けるのか:
/// 三角形の塗りつぶしは、この先 Day 10 まで育ち続ける中心コードになる。
/// バリセントリック補間(Day 4)、透視除算(Day 6)、Zバッファ(Day 7)、
/// テクスチャ(Day 8)、シェーディング(Day 9)は全部この中に積み上がっていく。
/// Framebuffer は「ピクセル配列とその上の素朴な2D描画」に留めておき、
/// 「3Dパイプラインの出口」であるここと役割を分けておくと、
/// 後のDayの差分がこのファイルにまとまって読みやすくなる。
/// </summary>
internal sealed class Rasterizer
{
    private readonly Framebuffer _target;

    public Rasterizer(Framebuffer target)
    {
        _target = target;
    }

    /// <summary>
    /// top-left rule(要点4)を適用するか。既定は true。
    /// Day 3 の実験用トグルで、切ると共有辺が二重に塗られる様子が見える。Day 4 で消す。
    /// </summary>
    public bool UseTopLeftRule { get; set; } = true;

    /// <summary>
    /// 加算合成で描くか。既定は false(上書き)。
    /// 「同じピクセルが何回塗られたか」を明るさとして可視化するための実験用。Day 4 で消す。
    /// </summary>
    public bool AdditiveBlend { get; set; }

    /// <summary>
    /// エッジ関数。線分 a→b に対して点 p がどちら側にあるかを返す。
    ///
    /// 中身は2次元の外積 (b - a) × (p - a) で、返る値は
    /// 「a, b, p が作る平行四辺形の符号付き面積」でもある。この2つの意味を持つのが強力な点で、
    ///   - 符号  … 三角形の内外判定に使う(今日)
    ///   - 大きさ … そのままバリセントリック座標の分子になる(Day 4)
    /// 内外判定のために計算した値が、次のDayでは補間の重みとしてそのまま再利用できる。
    ///
    /// 画面座標系は y が下向きなので、数学の紙の上とは符号の向きが逆になる。
    /// a=(0,0), b=(1,0), p=(0,1) のとき戻り値は +1 で、
    /// 「p が辺 a→b より画面上で下側にあると正」という向きになっている。
    /// </summary>
    private static int EdgeFunction(int ax, int ay, int bx, int by, int px, int py)
        => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    /// <summary>
    /// 辺 a→b が「上の辺」または「左の辺」かを判定する(top-left rule)。
    ///
    /// 巻き方向を正に正規化した後、この座標系では
    ///   - 上の辺 … 水平(ay == by)で、右向き(bx > ax)のもの
    ///   - 左の辺 … 画面上で上に向かって進むもの(by &lt; ay)
    /// になる。三角形 (0,0)-(10,0)-(0,10) で確かめると、
    /// 上辺 (0,0)→(10,0) は水平かつ右向き、左辺 (0,10)→(0,0) は上向きで、
    /// 斜辺 (10,0)→(0,10) は下向きなのでどちらでもない。
    /// </summary>
    private static bool IsTopLeft(int ax, int ay, int bx, int by)
        => (ay == by && bx > ax) || by < ay;

    /// <summary>
    /// 三角形を単色で塗りつぶす。
    ///
    /// 手順は4段階。
    ///   1. 巻き方向を正にそろえる(負なら頂点を2つ入れ替える)
    ///   2. 3頂点を囲む矩形(バウンディングボックス)を求め、画面内に切り詰める
    ///   3. top-left rule のバイアスを辺ごとに決める
    ///   4. 矩形の中を全部走査し、3つのエッジ関数がすべて非負なら塗る
    /// </summary>
    public void FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, int color)
    {
        // --- 1. 巻き方向の正規化 ---
        // 3頂点が時計回りか反時計回りかで、エッジ関数の符号が全部反転してしまう。
        // 「内側なら3つとも正」という単純な判定にするために、
        // 負だったら頂点を入れ替えて向きをそろえてしまうのが手っ取り早い。
        // (Day 10 の背面カリングでは、この符号そのものを「表か裏か」の判定に使う。
        //  つまり今は捨てている情報が、後で意味を持つようになる)
        int area = EdgeFunction(x0, y0, x1, y1, x2, y2);
        if (area == 0)
        {
            // 3点が一直線上にある(面積0)。塗るピクセルは無いので何もしない。
            // ここで弾いておかないと、この後のゼロ除算やバイアス計算が意味を失う。
            return;
        }

        if (area < 0)
        {
            (x1, y1, x2, y2) = (x2, y2, x1, y1);
        }

        // --- 2. バウンディングボックス ---
        // 画面全体を走査すると 307,200 ピクセル分の判定が要るが、
        // 三角形を囲む矩形だけなら実際に必要な範囲で済む。
        // 同時に画面内へ切り詰めておくことで、内側のループから範囲チェックを追い出せる。
        int minX = Math.Max(Math.Min(x0, Math.Min(x1, x2)), 0);
        int maxX = Math.Min(Math.Max(x0, Math.Max(x1, x2)), _target.Width - 1);
        int minY = Math.Max(Math.Min(y0, Math.Min(y1, y2)), 0);
        int maxY = Math.Min(Math.Max(y0, Math.Max(y1, y2)), _target.Height - 1);

        // --- 3. top-left rule のバイアス ---
        // 辺の真上に乗ったピクセル(エッジ関数がちょうど0)を塗るかどうかの取り決め。
        // 「上の辺と左の辺なら塗る、下の辺と右の辺なら塗らない」と決めておくと、
        // 辺を共有する2つの三角形で、境界のピクセルがちょうど1回ずつ塗られる。
        // 0 のまま(塗る)か -1 する(0 を負にして弾く)かで表現する。
        int bias0 = !UseTopLeftRule || IsTopLeft(x1, y1, x2, y2) ? 0 : -1;
        int bias1 = !UseTopLeftRule || IsTopLeft(x2, y2, x0, y0) ? 0 : -1;
        int bias2 = !UseTopLeftRule || IsTopLeft(x0, y0, x1, y1) ? 0 : -1;

        int[] pixels = _target.Pixels;
        int width = _target.Width;

        // --- 4. 走査 ---
        for (int y = minY; y <= maxY; y++)
        {
            int rowOffset = y * width;

            for (int x = minX; x <= maxX; x++)
            {
                // 各頂点の「向かい側の辺」に対する符号を求める。
                // w0 は辺 1→2、w1 は辺 2→0、w2 は辺 0→1 に対応する。
                // この対応はDay 4 の重み(w0 が頂点0の重みになる)に繋がるので、
                // 今のうちにこの並びに慣れておくとよい。
                int w0 = EdgeFunction(x1, y1, x2, y2, x, y) + bias0;
                int w1 = EdgeFunction(x2, y2, x0, y0, x, y) + bias1;
                int w2 = EdgeFunction(x0, y0, x1, y1, x, y) + bias2;

                // 3つとも非負なら内側。符号ビットだけを見ればよいので、
                // OR を取って1回比較するだけで「3つとも非負か」を判定できる
                // (負の数は最上位ビットが立つので、1つでも負なら OR も負になる)。
                if ((w0 | w1 | w2) >= 0)
                {
                    int index = rowOffset + x;
                    pixels[index] = AdditiveBlend ? AddSaturate(pixels[index], color) : color;
                }
            }
        }
    }

    /// <summary>
    /// 加算合成(飽和加算)。重なりを可視化するための実験用ヘルパー。
    /// 各チャンネルを別々に足して 255 で頭打ちにする。
    /// </summary>
    private static int AddSaturate(int destination, int source)
    {
        int r = Math.Min(((destination >> 16) & 0xFF) + ((source >> 16) & 0xFF), 255);
        int g = Math.Min(((destination >> 8) & 0xFF) + ((source >> 8) & 0xFF), 255);
        int b = Math.Min((destination & 0xFF) + (source & 0xFF), 255);
        return Framebuffer.Rgb((byte)r, (byte)g, (byte)b);
    }

    /// <summary>
    /// 三角形の輪郭だけを描く(ワイヤーフレーム)。中身は Day 2 の線分描画3回。
    /// 塗りつぶしの結果と重ねると「どのピクセルまでが三角形の内側と判定されたか」が見える。
    /// </summary>
    public void DrawTriangleWireframe(int x0, int y0, int x1, int y1, int x2, int y2, int color)
    {
        _target.DrawLine(x0, y0, x1, y1, color);
        _target.DrawLine(x1, y1, x2, y2, color);
        _target.DrawLine(x2, y2, x0, y0, color);
    }
}
