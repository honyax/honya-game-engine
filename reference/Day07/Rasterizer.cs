namespace SoftwareRasterizer;

/// <summary>
/// 補間された3つの属性から、そのピクセルの色を決める関数。
///
/// GPUで言う「ピクセルシェーダ(フラグメントシェーダ)」に相当する。
/// ラスタライザの仕事を「どのピクセルか」と「その点での属性値はいくつか」までに留め、
/// **色をどう決めるかは呼び出し側に委ねる**、という役割分担を作るための仕組み。
///
/// Day 5 で属性が Vec3 1つにまとまったので、引数も1つで済むようになった。
/// Day 8(テクスチャを引く)、Day 9(光の計算をする)は、
/// どちらも「補間された値から色を決める」というこの形に収まる。
/// </summary>
internal delegate int PixelShader(Vec3 attribute);

/// <summary>
/// 三角形ラスタライザ。
///
/// なぜ <see cref="Framebuffer"/> に足さずクラスを分けるのか:
/// 三角形の塗りつぶしは、この先 Day 10 まで育ち続ける中心コードになる。
/// バリセントリック補間(Day 4)、透視除算(Day 6)、Zバッファ(Day 7)、
/// テクスチャ(Day 8)、シェーディング(Day 9)は全部この中に積み上がっていく。
/// </summary>
internal sealed class Rasterizer
{
    private readonly Framebuffer _target;

    /// <summary>深度バッファ。フレームバッファと同じ大きさで一緒に持つ。</summary>
    public DepthBuffer Depth { get; }

    /// <summary>
    /// 深度テストを行うか。既定は true。
    ///
    /// 実験用のトグルではなく、これ自体が正式な描画設定。
    /// OpenGL の <c>glEnable(GL_DEPTH_TEST)</c> に相当する。
    /// 半透明のものを描くときや、常に手前に出したいUIを描くときに切る。
    /// </summary>
    public bool DepthTestEnabled { get; set; } = true;

    public Rasterizer(Framebuffer target)
    {
        _target = target;
        Depth = new DepthBuffer(target.Width, target.Height);
    }

    /// <summary>
    /// エッジ関数。線分 a→b に対して点 (px, py) がどちら側にあるかを返す。
    ///
    /// 中身は2次元の外積 (b - a) × (p - a) で、
    ///   - 符号  … 三角形の内外判定に使う(Day 3)
    ///   - 大きさ … そのままバリセントリック座標の分子になる(Day 4)
    /// という二役を持つ。
    ///
    /// Day 5 で int から float になった。値の意味は変わらないが、
    /// 「ちょうど0」になる場面の扱いが変わる(要点5)。
    /// </summary>
    private static float EdgeFunction(Vec3 a, Vec3 b, float px, float py)
        => (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);

    /// <summary>
    /// 辺 a→b が「上の辺」または「左の辺」かを判定する(top-left rule)。
    ///
    /// 巻き方向を正に正規化した後、この座標系では
    ///   - 上の辺 … 水平(a.Y == b.Y)で、右向き(b.X &gt; a.X)のもの
    ///   - 左の辺 … 画面上で上に向かって進むもの(b.Y &lt; a.Y)
    /// になる。
    /// </summary>
    private static bool IsTopLeft(Vec3 a, Vec3 b)
        => (a.Y == b.Y && b.X > a.X) || b.Y < a.Y;

    /// <summary>
    /// top-left rule で「塗らない」側にするためのバイアス。
    ///
    /// float.Epsilon は float で表せる最小の正の数(約 1.4e-45)。
    /// これを引くと、**ちょうど0だった値だけが負になる**。
    /// 通常の大きさの値(エッジ関数の値は普通10の3乗〜5乗程度)からこれを引いても、
    /// 浮動小数の刻みのほうがずっと粗いので値は1ビットも変わらない。
    /// 「0のときだけ効く補正」を、分岐を増やさずに書くための手口。
    /// </summary>
    private const float TopLeftBias = float.Epsilon;

    /// <summary>
    /// クリップ座標の W がこれ以下の頂点は、カメラの真横〜後ろにあるとみなして捨てる。
    ///
    /// W はカメラからの奥行きそのもの(投影行列が Z をコピーしたもの)なので、
    /// 0 以下は「カメラと同じ位置か、後ろ」を意味する。
    /// そのまま透視除算すると 0 除算か符号反転が起き、
    /// 三角形が画面の反対側へ裏返って飛んでいく。
    ///
    /// 本来は「三角形を近クリップ面で切って、手前の部分だけ描く」のが正しい対処で、
    /// それが Day 10 のクリッピング。今日は**三角形ごと捨てる**手抜きで済ませる
    /// (カメラに近づきすぎると面が丸ごと消えるのはこのため)。
    /// </summary>
    private const float MinClipW = 1e-5f;

    /// <summary>
    /// 頂点をクリップ座標へ変換し、透視除算とビューポート変換を経て画面座標にする。
    ///
    /// 3DCGのパイプラインで一番大事な数行がここにある。
    ///
    ///   1. モデル座標 --(MVP行列)--> クリップ座標(4次元。W に奥行きが入っている)
    ///   2. クリップ座標 --(W で割る)--> 正規化デバイス座標 NDC(-1〜1 の立方体)
    ///   3. NDC --(ビューポート変換)--> 画面座標(ピクセル)
    ///
    /// **遠近感が生まれるのは 2 の割り算**。奥にあるものほど W が大きいので、
    /// 割った結果が小さくなり、画面の中心へ引き寄せられて小さく描かれる。
    /// 行列は「Z を W にコピーする」準備をしただけで、遠近感そのものは作っていない。
    /// </summary>
    public bool TryProjectToScreen(Vec3 position, Mat4 mvp, out Vec3 screen)
    {
        // 1. モデル座標 → クリップ座標。点なので W = 1 で入れる。
        Vec4 clip = Mat4.Transform(Vec4.Point(position), mvp);

        if (clip.W <= MinClipW)
        {
            screen = default;
            return false;
        }

        // 2. 透視除算。ここが遠近感の正体。
        float invW = 1.0f / clip.W;
        float ndcX = clip.X * invW;
        float ndcY = clip.Y * invW;
        float ndcZ = clip.Z * invW;

        // 3. ビューポート変換。NDC の -1〜1 を画面のピクセル範囲へ移す。
        //    Y だけ反転しているのは、NDC は上が +1 なのに対して
        //    画面座標は下へ行くほど Y が増えるため。
        screen = new Vec3(
            (ndcX * 0.5f + 0.5f) * _target.Width,
            (0.5f - ndcY * 0.5f) * _target.Height,
            ndcZ);

        return true;
    }

    /// <summary>
    /// モデル座標の三角形を、MVP行列で変換してから塗る。
    /// 今日から「絵を描く」入口はここになる。
    ///
    /// 前半(頂点の変換)がGPUの頂点シェーダ、
    /// 後半(FillTriangle)がラスタライザ + ピクセルシェーダに相当する。
    /// この2段構えは Day 5 のデモですでに作ってあったものが、3Dに拡張されただけ。
    /// </summary>
    public void DrawTriangle(Vertex v0, Vertex v1, Vertex v2, Mat4 mvp, PixelShader? shader = null)
    {
        if (!TryProjectToScreen(v0.Position, mvp, out Vec3 s0) ||
            !TryProjectToScreen(v1.Position, mvp, out Vec3 s1) ||
            !TryProjectToScreen(v2.Position, mvp, out Vec3 s2))
        {
            return;
        }

        FillTriangle(new Vertex(s0, v0.Color), new Vertex(s1, v1.Color), new Vertex(s2, v2.Color), shader);
    }

    /// <summary>
    /// 三角形を塗りつぶす。3頂点の属性をバリセントリック座標で補間する。
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
        // 頂点を入れ替えるときは、位置だけでなく属性も一緒に入れ替わる必要がある。
        // Vertex 構造体ごと交換しているので自動的にそうなっている。
        float area = EdgeFunction(v0.Position, v1.Position, v2.Position.X, v2.Position.Y);
        if (area == 0.0f)
        {
            return;
        }

        if (area < 0.0f)
        {
            (v1, v2) = (v2, v1);
            area = -area;
        }

        // --- 2. バウンディングボックス ---
        // 頂点が小数になったので、外側へ丸める(floor / ceiling)。
        // 内側へ丸めると三角形の端がわずかに欠ける。
        float minXf = MathF.Min(v0.Position.X, MathF.Min(v1.Position.X, v2.Position.X));
        float maxXf = MathF.Max(v0.Position.X, MathF.Max(v1.Position.X, v2.Position.X));
        float minYf = MathF.Min(v0.Position.Y, MathF.Min(v1.Position.Y, v2.Position.Y));
        float maxYf = MathF.Max(v0.Position.Y, MathF.Max(v1.Position.Y, v2.Position.Y));

        int minX = Math.Max((int)MathF.Floor(minXf), 0);
        int maxX = Math.Min((int)MathF.Ceiling(maxXf), _target.Width - 1);
        int minY = Math.Max((int)MathF.Floor(minYf), 0);
        int maxY = Math.Min((int)MathF.Ceiling(maxYf), _target.Height - 1);

        // --- 3. top-left rule のバイアス ---
        float bias0 = IsTopLeft(v1.Position, v2.Position) ? 0.0f : -TopLeftBias;
        float bias1 = IsTopLeft(v2.Position, v0.Position) ? 0.0f : -TopLeftBias;
        float bias2 = IsTopLeft(v0.Position, v1.Position) ? 0.0f : -TopLeftBias;

        float invArea = 1.0f / area;

        int[] pixels = _target.Pixels;
        float[] depth = Depth.Depth;
        bool depthTest = DepthTestEnabled;
        int width = _target.Width;

        // --- 4. 走査 ---
        for (int y = minY; y <= maxY; y++)
        {
            int rowOffset = y * width;

            // ピクセルの「中心」で判定する。
            // ピクセル (x, y) が覆っているのは [x, x+1) x [y, y+1) の正方形なので、
            // その代表点は中心の (x + 0.5, y + 0.5) になる。
            // Day 4 まで整数位置で判定していたのは、頂点も整数だったからできた手抜きで、
            // 頂点が小数になった今は中心で測らないと、絵が半ピクセルずれる。
            float py = y + 0.5f;

            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;

                float w0 = EdgeFunction(v1.Position, v2.Position, px, py);
                float w1 = EdgeFunction(v2.Position, v0.Position, px, py);
                float w2 = EdgeFunction(v0.Position, v1.Position, px, py);

                // 内外判定にはバイアス付き、補間には生の値を使う(Day 4 の要点3)。
                if (w0 + bias0 < 0.0f || w1 + bias1 < 0.0f || w2 + bias2 < 0.0f)
                {
                    continue;
                }

                // バリセントリック座標。3つの重みの合計は必ず1になる。
                float l0 = w0 * invArea;
                float l1 = w1 * invArea;
                float l2 = w2 * invArea;

                int index = rowOffset + x;

                // --- 深度テスト ---
                // 深度は Z を補間するだけで求まる。透視除算を済ませた後の Z は
                // 画面上で線形に変化するので、バリセントリック補間がそのまま正しい値になる。
                // (色やUVはそうならない。その話が Day 8 の透視補正補間)
                //
                // 色を計算する前にテストするのが大事。落ちるピクセルのために
                // シェーダを走らせるのは丸損なので、判定は可能な限り早く行う。
                // GPUが「アーリーZ」と呼んで特別扱いしているのも同じ理由。
                float z = v0.Position.Z * l0 + v1.Position.Z * l1 + v2.Position.Z * l2;

                if (depthTest)
                {
                    // 小さいほど手前(0 = 近クリップ面)。既に描かれているものより
                    // 手前でなければ捨てる。等号を含めないのは、同じ深度なら
                    // 先に描いたほうを残すため(後勝ちにすると描画順で絵が変わってしまう)。
                    if (z >= depth[index])
                    {
                        continue;
                    }

                    depth[index] = z;
                }

                // 属性の補間。中身が色とは限らない(UVや法線のこともある)ので attribute と呼ぶ。
                Vec3 attribute = v0.Color * l0 + v1.Color * l1 + v2.Color * l2;

                pixels[index] = shader is null
                    ? Framebuffer.Rgb(attribute.X, attribute.Y, attribute.Z)
                    : shader(attribute);
            }
        }
    }

    /// <summary>
    /// 単色で三角形を塗る。3頂点に同じ色を持たせて補間版へ渡すだけ。
    /// </summary>
    public void FillTriangle(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 color)
        => FillTriangle(new Vertex(p0, color), new Vertex(p1, color), new Vertex(p2, color));

    /// <summary>
    /// 三角形の輪郭だけを描く(ワイヤーフレーム)。
    /// 線分描画は整数座標なので、ここで丸める。
    /// </summary>
    public void DrawTriangleWireframe(Vec3 p0, Vec3 p1, Vec3 p2, int color)
    {
        DrawLine(p0, p1, color);
        DrawLine(p1, p2, color);
        DrawLine(p2, p0, color);
    }

    private void DrawLine(Vec3 a, Vec3 b, int color)
        => _target.DrawLine(
            (int)MathF.Round(a.X), (int)MathF.Round(a.Y),
            (int)MathF.Round(b.X), (int)MathF.Round(b.Y),
            color);
}
