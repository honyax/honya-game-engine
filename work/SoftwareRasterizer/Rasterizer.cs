namespace SoftwareRasterizer;

/// <summary>
/// 補間された3つの属性から、そのピクセルの色を決める関数。
///
/// GPUで言う「ピクセルシェーダ(フラグメントシェーダ)」に相当する。
/// ラスタライザの仕事を「どのピクセルか」と「その点での属性値はいくつか」までに留め、
/// **色をどう決めるかは呼び出し側に委ねる**、という役割分担を作るための仕組み。
///
/// Day 4 の時点では市松模様のデモで使うだけだが、
/// Day 8(テクスチャを引く)、Day 9(光の計算をする)は、
/// どちらも「補間された値から色を決める」という同じ形に収まる。
/// GPUがシェーダーをプログラマブルにした理由が、この分離の中に見えている。
/// </summary>
internal delegate int PixelShader(float a0, float a1, float a2);

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
    /// エッジ関数。線分 a→b に対して点 p がどちら側にあるかを返す。
    ///
    /// 中身は2次元の外積 (b - a) × (p - a) で、返る値は
    /// 「a, b, p が作る平行四辺形の符号付き面積」でもある。この2つの意味を持つのが強力な点で、
    ///   - 符号  … 三角形の内外判定に使う(Day 3)
    ///   - 大きさ … そのままバリセントリック座標の分子になる(Day 4 = 今日)
    /// Day 3 で内外判定のために計算した値が、今日は補間の重みとしてそのまま再利用される。
    /// 追加の計算は「面積で割る」だけで、そのための面積も正規化のときに計算済み。
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
    /// 三角形を塗りつぶす。3頂点の色をバリセントリック座標で補間する。
    ///
    /// Day 3 の単色版との違いは、内外判定に使ったエッジ関数の値を捨てずに、
    /// 面積で割って重みとして使うところだけ。judge と blend が同じ数字を共有している。
    /// </summary>
    public void FillTriangle(Vertex v0, Vertex v1, Vertex v2)
        => FillTriangle(v0, v1, v2, null);

    /// <summary>
    /// 三角形を塗りつぶし、色の決定を <paramref name="shader"/> に委ねる。
    /// null なら補間した色をそのまま使う(通常の頂点カラー描画)。
    /// </summary>
    public void FillTriangle(Vertex v0, Vertex v1, Vertex v2, PixelShader? shader)
    {
        // --- 1. 巻き方向の正規化 ---
        // 頂点を入れ替えるときは、位置だけでなく色も一緒に入れ替わる点が今日は重要。
        // Vertex 構造体ごと交換しているので自動的にそうなっているが、
        // 位置と色を別々の配列で持つ設計にしていると、ここで取り違える事故が起きる。
        int area = EdgeFunction(v0.X, v0.Y, v1.X, v1.Y, v2.X, v2.Y);
        if (area == 0)
        {
            return;
        }

        if (area < 0)
        {
            (v1, v2) = (v2, v1);
            area = -area;
        }

        // --- 2. バウンディングボックス ---
        int minX = Math.Max(Math.Min(v0.X, Math.Min(v1.X, v2.X)), 0);
        int maxX = Math.Min(Math.Max(v0.X, Math.Max(v1.X, v2.X)), _target.Width - 1);
        int minY = Math.Max(Math.Min(v0.Y, Math.Min(v1.Y, v2.Y)), 0);
        int maxY = Math.Min(Math.Max(v0.Y, Math.Max(v1.Y, v2.Y)), _target.Height - 1);

        // --- 3. top-left rule のバイアス ---
        int bias0 = IsTopLeft(v1.X, v1.Y, v2.X, v2.Y) ? 0 : -1;
        int bias1 = IsTopLeft(v2.X, v2.Y, v0.X, v0.Y) ? 0 : -1;
        int bias2 = IsTopLeft(v0.X, v0.Y, v1.X, v1.Y) ? 0 : -1;

        // 面積の逆数を先に作っておく。割り算はピクセルごとにやると高くつくので、
        // 三角形につき1回だけにして、内側のループでは掛け算で済ませる。
        float invArea = 1.0f / area;

        int[] pixels = _target.Pixels;
        int width = _target.Width;

        // --- 4. 走査 ---
        for (int y = minY; y <= maxY; y++)
        {
            int rowOffset = y * width;

            for (int x = minX; x <= maxX; x++)
            {
                // エッジ関数の生の値。w0 は頂点0の「向かい側の辺」に対する値で、
                // これが頂点0の重みになる(頂点0から遠いほど 0 に近づく)。
                int w0 = EdgeFunction(v1.X, v1.Y, v2.X, v2.Y, x, y);
                int w1 = EdgeFunction(v2.X, v2.Y, v0.X, v0.Y, x, y);
                int w2 = EdgeFunction(v0.X, v0.Y, v1.X, v1.Y, x, y);

                // 内外判定にはバイアスを足した値を使い、補間には生の値を使う。
                // バイアスは「辺の上のピクセルを塗るか」の取り決めのための ±1 でしかなく、
                // 重みとしては意味を持たない。混ぜると三角形の縁で色がわずかにずれる。
                if (((w0 + bias0) | (w1 + bias1) | (w2 + bias2)) < 0)
                {
                    continue;
                }

                // バリセントリック座標。3つの重みは必ず合計1になる
                // (w0 + w1 + w2 == area がエッジ関数の性質として成り立つため)。
                float l0 = w0 * invArea;
                float l1 = w1 * invArea;
                float l2 = w2 * invArea;

                // 属性の補間。頂点の値に重みを掛けて足すだけ。
                // 変数名を r, g, b ではなく a0, a1, a2(attribute)にしているのは、
                // 中身が色とは限らないため。市松模様のデモでは UV として使っている。
                // 属性がUVや法線に変わっても、この式の形は一切変わらない。
                float a0 = l0 * v0.R + l1 * v1.R + l2 * v2.R;
                float a1 = l0 * v0.G + l1 * v1.G + l2 * v2.G;
                float a2 = l0 * v0.B + l1 * v1.B + l2 * v2.B;

                // shader が無ければ補間結果をそのまま色として使う。
                // 分岐がピクセルごとに入るが、常に同じ側へ進むので分岐予測がほぼ外さない。
                // (デリゲート呼び出しのほうがずっと高い。市松模様のデモで実測できる)
                pixels[rowOffset + x] = shader is null
                    ? Framebuffer.Rgb(a0, a1, a2)
                    : shader(a0, a1, a2);
            }
        }
    }

    /// <summary>
    /// 単色で三角形を塗る。3頂点に同じ色を持たせて補間版へ渡すだけ。
    /// 補間は行われるが、3頂点が同じ値なので結果は一様になる。
    /// </summary>
    public void FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, int color)
        => FillTriangle(
            Vertex.FromPackedColor(x0, y0, color),
            Vertex.FromPackedColor(x1, y1, color),
            Vertex.FromPackedColor(x2, y2, color));

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
