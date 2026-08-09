using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SoftwareRasterizer;

/// <summary>
/// ゲームウィンドウ本体。役割は3つ。
///   1. ウィンドウを1枚出す
///   2. ゲームループ(更新 → 描画 → 転送 → 待つ)を回す
///   3. <see cref="Framebuffer"/> の中身を画面へ転送する
///
/// この先どれだけ描画が高度になっても、この3つの骨格は変わらない。
/// </summary>
internal sealed class GameWindow : Form
{
    /// <summary>目標フレームレート。60fps = 1フレームあたり約16.67msが持ち時間。</summary>
    private const double TargetFps = 60.0;

    private const double TargetFrameSeconds = 1.0 / TargetFps;

    private readonly Framebuffer _framebuffer;

    /// <summary>
    /// 三角形ラスタライザ。Day 3 以降、画面に出る絵の大半はこいつが描く。
    /// </summary>
    private readonly Rasterizer _rasterizer;

    /// <summary>
    /// フレームバッファを画面へ渡すための中継用ビットマップ。
    /// 毎フレーム new すると GDI+ ハンドルとGCを浪費するので、必ず使い回す。
    /// </summary>
    private readonly Bitmap _backBuffer;

    /// <summary>
    /// クライアント領域への描画面。ウィンドウハンドルが必要なので、Show() の後に取得する。
    /// </summary>
    private Graphics? _graphics;

    private bool _running;

    public GameWindow(int width, int height)
    {
        _framebuffer = new Framebuffer(width, height);
        _rasterizer = new Rasterizer(_framebuffer);

        // Format32bppRgb: 1ピクセル32bitで、上位8bitのアルファは「未使用」扱い。
        // Format32bppArgb にするとGDI+がアルファ合成を試みる可能性があり、
        // 画面に出すだけの用途では無駄。メモリ配置は B,G,R,X の順で、
        // Framebuffer.Rgb が作る 0xAARRGGBB とそのまま一致する。
        _backBuffer = new Bitmap(width, height, PixelFormat.Format32bppRgb);

        Text = "Day05 - ベクトルと行列";

        // WinFormsによる自動DPIスケーリングを止める。
        // これを None にしないと、高DPI環境で ClientSize が勝手に拡大され、
        // 640x480のフレームバッファが引き伸ばされて1:1で表示されなくなる。
        AutoScaleMode = AutoScaleMode.None;

        // ClientSize はウィンドウ枠を含まない「中身」のサイズ。
        // フレームバッファと同じにすることで拡大縮小なしの等倍転送になる。
        ClientSize = new Size(width, height);

        // リサイズ不可にする。可変にするとフレームバッファの再確保が必要になり、
        // Day 1 の主題から外れるため、ここでは固定サイズと割り切る。
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        // ちらつき(フリッカ)対策。
        // 既定ではOSが「背景を塗る → こちらが描く」の2段階で描画するため、
        // 一瞬背景色が見えてチラつく。画面全体を毎フレーム自前で埋めるので、
        // 背景塗りは完全に不要だと宣言してしまう。
        SetStyle(
            ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint,
            true);
    }

    /// <summary>
    /// ゲームループ本体。
    ///
    /// Application.Run(form) を使わないのは、あれが「メッセージが来るまで待つ」
    /// イベント駆動のループだから。ゲームは入力が無くても毎フレーム絵を更新したいので、
    /// 自前で回しっぱなしのループを持ち、その中でメッセージ処理を呼ぶ形にする。
    /// </summary>
    public void Run()
    {
        Show();

        // ハンドル生成後でないと描画面を取れないため、Show() の直後に取得する。
        _graphics = CreateGraphics();
        _running = true;

        // Stopwatch は OS の高分解能タイマを使う。DateTime.Now は分解能が粗く(約16ms)、
        // 60fpsの計測には全く足りないので、時間計測には必ずこちらを使う。
        var clock = Stopwatch.StartNew();

        double previousSeconds = 0.0;
        double nextFrameSeconds = 0.0;

        // FPS表示用の集計
        double fpsElapsed = 0.0;
        int fpsFrames = 0;

        // Render だけにかかった時間の集計(FPSと同じく0.5秒ぶんを平均する)
        double renderSecondsAccum = 0.0;

        while (_running)
        {
            // OSから届いたメッセージ(マウス、キー、ウィンドウ移動、閉じるボタン…)を処理する。
            // これを呼ばないとウィンドウが「応答なし」になる。
            Application.DoEvents();

            // DoEvents の中で閉じられた可能性があるので、ここで抜ける。
            // 破棄済みのフォームに触ると例外になる。
            if (!_running)
            {
                break;
            }

            double nowSeconds = clock.Elapsed.TotalSeconds;
            double deltaSeconds = nowSeconds - previousSeconds;
            previousSeconds = nowSeconds;

            // Render の所要時間だけを切り出して測る。
            // Day 1 の実測で「一番重いのは自分の描画ではなくGDI+の画面転送(約6ms)」と
            // 分かっているので、線を何本描いても Render 側にはまだ余裕がある、という確認になる。
            // 三角形は線分と違って「面積ぶん」のピクセルを塗るので、
            // 線を描いていたDay 2 とは桁が変わる。その実感を数字で持っておく。
            double renderStartSeconds = clock.Elapsed.TotalSeconds;
            Render(nowSeconds);
            renderSecondsAccum += clock.Elapsed.TotalSeconds - renderStartSeconds;

            Present();

            // FPSは毎フレーム表示すると数字が暴れて読めないので、0.5秒ぶんを平均する。
            fpsFrames++;
            fpsElapsed += deltaSeconds;
            if (fpsElapsed >= 0.5)
            {
                Text = $"Day05 - ベクトルと行列  {fpsFrames / fpsElapsed:F1} fps | {TriangleCount} tri | "
                     + $"render {renderSecondsAccum / fpsFrames * 1000.0:F2} ms | W:ワイヤー Esc:終了";
                fpsFrames = 0;
                fpsElapsed = 0.0;
                renderSecondsAccum = 0.0;
            }

            // 次フレームの開始時刻を決める。
            // 「現在時刻 + 16.67ms」ではなく「前回の目標時刻 + 16.67ms」を積むのがポイント。
            // 前者だと毎フレームの待ち誤差がそのまま累積し、平均フレームレートが目標より下がる。
            nextFrameSeconds += TargetFrameSeconds;

            double current = clock.Elapsed.TotalSeconds;
            if (nextFrameSeconds < current)
            {
                // 何らかの理由で大きく遅れた場合(重い処理、ウィンドウのドラッグ等)。
                // 遅れを取り戻そうと連続実行すると挙動が暴れるので、諦めて現在時刻に合わせ直す。
                nextFrameSeconds = current;
            }

            WaitUntil(clock, nextFrameSeconds);
        }
    }

    /// <summary>ワイヤーフレームを重ねて表示するか(Wキー)。</summary>
    private bool _showWireframe;

    /// <summary>公転する子の数。</summary>
    private const int OrbitChildren = 3;

    /// <summary>1フレームに描く三角形の総数(グラデーション1 + 市松1 + 中心2 + 子と孫)。</summary>
    private const int TriangleCount = 1 + 1 + 2 + OrbitChildren * 2;

    /// <summary>
    /// 1フレーム分の絵をフレームバッファに描く。
    ///
    /// Day 5 の見どころは絵そのものよりも、**図形の頂点をどう決めているか**。
    /// 今日から図形は「原点まわりの単純な形」として定義し、
    /// 画面のどこにどんな大きさ・向きで置くかは行列に任せる。
    /// 図形の定義と配置が分離される、というのが行列を導入する一番の効能で、
    /// Day 6 でこれがそのまま3Dのモデル行列になる。
    /// </summary>
    private void Render(double timeSeconds)
    {
        _framebuffer.Clear(Framebuffer.Rgb(12, 14, 22));

        float t = (float)timeSeconds;

        DrawGradientTriangle(t);
        DrawBarycentricPattern(t);
        DrawOrbitSystem(t);
    }

    /// <summary>
    /// 3頂点に赤・緑・青を割り当てた三角形。Day 4 と同じ絵だが、作り方が違う。
    ///
    /// Day 4 では毎フレーム三角関数で頂点位置を計算していた。
    /// 今日は「原点まわりの正三角形」を1回だけ書き、
    /// 回転と拡大と移動は行列に任せている。
    /// 拡大が時間で脈動するので、行列の合成順序(拡大 → 回転 → 移動)も確認できる。
    /// </summary>
    private void DrawGradientTriangle(float t)
    {
        Span<Vertex> shape = stackalloc Vertex[3];
        UnitTriangle(shape, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1));

        // 拡大 → 回転 → 移動、の順に適用される(行ベクトル規約なので左から順)。
        // 順序を入れ替えると別物になる。例えば移動を先にすると、
        // 原点から離れた位置を軸にぐるっと公転してしまう。
        float pulse = 88.0f + 10.0f * MathF.Sin(t * 2.0f);
        Mat4 transform =
            Mat4.Scale(pulse) *
            Mat4.RotationZ(t * 0.7f) *
            Mat4.Translation(new Vec3(150.0f, 132.0f, 0.0f));

        DrawTransformed(shape, transform, null);
    }

    /// <summary>
    /// バリセントリック座標を「模様の材料」として使う(Day 4 と同じ趣旨)。
    ///
    /// 頂点1・頂点2に (1,0) と (0,1) を持たせて補間すると、
    /// 三角形の内部に「頂点0を原点とする斜めの座標系」ができる。
    /// これはまさに Day 8 のテクスチャ座標そのもの。
    /// </summary>
    private void DrawBarycentricPattern(float t)
    {
        const int checkerDivisions = 8;

        Span<Vertex> shape = stackalloc Vertex[3];
        // 属性を色ではなく UV として使うので、(0,0), (1,0), (0,1) を入れる。
        UnitTriangle(shape, new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0));

        Mat4 transform =
            Mat4.Scale(96.0f) *
            Mat4.RotationZ(-t * 0.5f) *
            Mat4.Translation(new Vec3(470.0f, 132.0f, 0.0f));

        DrawTransformed(shape, transform, attribute =>
        {
            int cell = (int)(attribute.X * checkerDivisions) + (int)(attribute.Y * checkerDivisions);
            return (cell & 1) == 0
                ? Framebuffer.Rgb(0.95f, 0.80f, 0.35f)
                : Framebuffer.Rgb(0.25f, 0.30f, 0.45f);
        });
    }

    /// <summary>
    /// 行列の合成そのものを見せるデモ。中心の四角のまわりを子が公転し、
    /// 子はさらに自転しながら、その子(孫)を連れている。
    ///
    /// 親の変換に子の変換を掛けるだけで、親が動けば子もついてくる。
    /// この「親の行列に自分の行列を掛ける」構造が、
    /// Day 22 で作る Transform コンポーネントの階層(シーングラフ)そのものになる。
    /// 太陽 - 惑星 - 衛星の関係を2Dでやっているだけだが、3Dでも構造は完全に同じ。
    /// </summary>
    private void DrawOrbitSystem(float t)
    {
        float centerX = _framebuffer.Width / 2.0f;
        float centerY = 348.0f;

        // --- 親(中心の四角)---
        // 自転しながら中心に居座る。
        Mat4 parent =
            Mat4.RotationZ(t * 0.4f) *
            Mat4.Translation(new Vec3(centerX, centerY, 0.0f));

        Span<Vertex> square = stackalloc Vertex[6];
        UnitSquare(square, new Vec3(0.85f, 0.75f, 0.45f));
        DrawTransformed(square, Mat4.Scale(34.0f) * parent, null);

        // --- 子(公転する三角形)と孫 ---
        // stackalloc はループの外に出す。ループの中で書くと、
        // 反復のたびにスタックを消費したまま解放されず、回数が増えると溢れる
        // (このメソッドが返るまでスタックは戻らない)。
        // 解析器も CA2014 として警告してくれる。
        Span<Vertex> child = stackalloc Vertex[3];
        Span<Vertex> moon = stackalloc Vertex[3];

        for (int i = 0; i < OrbitChildren; i++)
        {
            float phase = i * (MathF.PI * 2.0f / OrbitChildren);

            // 公転半径ぶん移動 → 公転 → 親の変換。ここまでが「子がぶら下がる座標系」。
            // 自分の見た目(自転と大きさ)を含まないので、孫の親としてそのまま使える。
            Mat4 childFrame =
                Mat4.Translation(new Vec3(104.0f, 0.0f, 0.0f)) *
                Mat4.RotationZ(t * 0.9f + phase) *
                parent;

            Vec3 color = ColorFromHue(i / (float)OrbitChildren);
            UnitTriangle(child, color, color * 0.55f, color * 0.25f);

            // 縮小 → 自転、のあとに上の座標系へ乗せる。
            DrawTransformed(child, Mat4.Scale(26.0f) * Mat4.RotationZ(t * 2.5f) * childFrame, null);

            // --- 孫(子のまわりを回る小さな三角形)---
            // 子の座標系をそのまま親として使えるのが、行列で階層を作る利点。
            UnitTriangle(moon, Vec3.One, Vec3.One * 0.6f, Vec3.One * 0.3f);

            Mat4 moonTransform =
                Mat4.Scale(9.0f) *
                Mat4.RotationZ(-t * 4.0f) *
                Mat4.Translation(new Vec3(44.0f, 0.0f, 0.0f)) *
                Mat4.RotationZ(t * 3.0f) *
                childFrame;

            DrawTransformed(moon, moonTransform, null);
        }
    }

    /// <summary>
    /// モデル座標の頂点列を行列で変換して描く。
    /// 頂点数は3の倍数で、3つずつが1枚の三角形になっている(トライアングルリスト)。
    ///
    /// この「頂点を変換してからラスタライザに渡す」という2段構えが、
    /// Day 6 以降ずっと続くパイプラインの原型になる。
    /// GPUで言えば前半が頂点シェーダ、後半がラスタライザ + ピクセルシェーダ。
    /// </summary>
    private void DrawTransformed(ReadOnlySpan<Vertex> shape, Mat4 transform, PixelShader? shader)
    {
        Span<Vertex> transformed = stackalloc Vertex[shape.Length];
        for (int i = 0; i < shape.Length; i++)
        {
            transformed[i] = new Vertex(TransformPoint2D(shape[i].Position, transform), shape[i].Color);
        }

        for (int i = 0; i + 2 < transformed.Length; i += 3)
        {
            _rasterizer.FillTriangle(transformed[i], transformed[i + 1], transformed[i + 2], shader);

            if (_showWireframe)
            {
                _rasterizer.DrawTriangleWireframe(
                    transformed[i].Position, transformed[i + 1].Position, transformed[i + 2].Position,
                    Framebuffer.Rgb(255, 255, 255));
            }
        }
    }

    /// <summary>
    /// 2次元の点を 4x4 行列で変換する。z = 0 の点として扱い、結果の x, y を取り出す。
    /// 2Dなのに 4x4 を使うのは無駄に見えるが、Day 6 でそのまま3Dへ移れる利点のほうが大きい。
    /// </summary>
    private static Vec2 TransformPoint2D(Vec2 p, Mat4 m)
    {
        Vec3 r = Mat4.TransformPoint(new Vec3(p.X, p.Y, 0.0f), m);
        return new Vec2(r.X, r.Y);
    }

    /// <summary>原点を中心とする半径1の正三角形。</summary>
    private static void UnitTriangle(Span<Vertex> destination, Vec3 c0, Vec3 c1, Vec3 c2)
    {
        Span<Vec3> colors = stackalloc Vec3[3];
        colors[0] = c0;
        colors[1] = c1;
        colors[2] = c2;

        for (int i = 0; i < 3; i++)
        {
            float angle = -MathF.PI / 2.0f + i * (MathF.PI * 2.0f / 3.0f);
            destination[i] = new Vertex(new Vec2(MathF.Cos(angle), MathF.Sin(angle)), colors[i]);
        }
    }

    /// <summary>原点を中心とする一辺2の正方形。三角形2枚(6頂点)で表す。</summary>
    private static void UnitSquare(Span<Vertex> destination, Vec3 color)
    {
        var lt = new Vec2(-1.0f, -1.0f);
        var rt = new Vec2(1.0f, -1.0f);
        var rb = new Vec2(1.0f, 1.0f);
        var lb = new Vec2(-1.0f, 1.0f);

        // 角ごとに明るさをずらして、四角が回っていることが分かるようにする。
        destination[0] = new Vertex(lt, color);
        destination[1] = new Vertex(rt, color * 0.75f);
        destination[2] = new Vertex(rb, color * 0.5f);
        destination[3] = new Vertex(lt, color);
        destination[4] = new Vertex(rb, color * 0.5f);
        destination[5] = new Vertex(lb, color * 0.75f);
    }

    /// <summary>0〜1 の色相を Vec3 の RGB に変換する(簡易HSV)。</summary>
    private static Vec3 ColorFromHue(float hue01)
    {
        int packed = HueColor(hue01);
        return new Vec3(
            ((packed >> 16) & 0xFF) / 255.0f,
            ((packed >> 8) & 0xFF) / 255.0f,
            (packed & 0xFF) / 255.0f);
    }

    /// <summary>
    /// 0〜1 の値を虹色に割り当てる(彩度・明度を最大に固定した簡易HSV)。
    /// 放射状の線を1本ずつ見分けるためだけのデモ用ヘルパー。
    /// </summary>
    private static int HueColor(double hue01)
    {
        // 色相環を6つの区間に割り、区間内では1色だけが直線的に増減する、と考えると
        // 分岐6本で書ける。区間の境界(0, 1/6, 2/6 …)で必ず原色になる。
        double h = (hue01 - Math.Floor(hue01)) * 6.0;
        int sector = (int)h;
        double f = h - sector;

        byte up = (byte)(f * 255.0);
        byte down = (byte)((1.0 - f) * 255.0);

        return sector switch
        {
            0 => Framebuffer.Rgb(255, up, 0),
            1 => Framebuffer.Rgb(down, 255, 0),
            2 => Framebuffer.Rgb(0, 255, up),
            3 => Framebuffer.Rgb(0, down, 255),
            4 => Framebuffer.Rgb(up, 0, 255),
            _ => Framebuffer.Rgb(255, 0, down),
        };
    }

    /// <summary>
    /// フレームバッファの内容を画面へ転送する(GPUで言うところの Present / SwapBuffers)。
    /// マネージドの int[] → GDI+ のアンマネージドメモリ → ウィンドウ、の2段構え。
    /// </summary>
    private void Present()
    {
        int width = _framebuffer.Width;
        int height = _framebuffer.Height;
        var rect = new Rectangle(0, 0, width, height);

        // LockBits: ビットマップの生メモリを直接触らせてもらうためのAPI。
        // GDI+ はビットマップを内部で好きに管理しているので、
        // 「今からここを書くので動かさないでくれ」と宣言する必要がある。
        // WriteOnly を指定すると、既存の内容を読み出す処理が省かれて速い。
        BitmapData data = _backBuffer.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                // Stride は「1行あたりの実バイト数」で、幅×4 とは限らない。
                // GDI+ は行の先頭を4バイト境界に揃えるためパディングを入れることがあるため、
                // 行単位でコピーして毎行 Stride で進めるのが確実。
                // (幅640・32bppなら実際は Stride == 640*4 になるが、
                //  一般の幅でも壊れないコードにしておく)
                IntPtr destination = data.Scan0 + y * data.Stride;
                Marshal.Copy(_framebuffer.Pixels, y * width, destination, width);
            }
        }
        finally
        {
            // Unlock を忘れるとビットマップが固定されたままになり、以降の描画が壊れる。
            // 途中で例外が出ても必ず解放されるよう finally に置く。
            _backBuffer.UnlockBits(data);
        }

        // 転送先の矩形を明示的に渡すのが重要。
        // DrawImage(bitmap, 0, 0) というオーバーロードはビットマップのDPIと
        // 描画先のDPIの比で勝手に拡大縮小してしまい、等倍にならないことがある。
        _graphics!.DrawImage(_backBuffer, rect);
    }

    /// <summary>
    /// スピン待ちに切り替える残り時間のしきい値。
    ///
    /// この5msという値には実測の根拠がある。Windowsのタイマ分解能は既定で粗く、
    /// Thread.Sleep(1) は実測で平均4ms前後、最悪14ms眠ることがあった。
    /// しきい値が2msだと Sleep が目標時刻を飛び越し、その遅れが Present の時間と重なって
    /// フレームを溢れさせ、実測58.8fpsまで落ちた。5msにすると59.9fpsになる。
    /// 「Sleepは当てにならないので、最後の数msは自分で数える」というのがここの結論。
    /// </summary>
    private const double SpinThresholdSeconds = 0.005;

    /// <summary>
    /// 指定時刻まで待つ。ソフトウェアレンダリングなのでVSync(垂直同期)は使えず、
    /// フレームレート制限は自前でやるしかない。
    /// </summary>
    private static void WaitUntil(Stopwatch clock, double targetSeconds)
    {
        while (true)
        {
            double remaining = targetSeconds - clock.Elapsed.TotalSeconds;
            if (remaining <= 0.0)
            {
                return;
            }

            if (remaining > SpinThresholdSeconds)
            {
                // まだ十分余裕があるうちだけ寝て、CPUを他のスレッドへ譲る。
                Thread.Sleep(1);
            }
            else
            {
                // 残りわずかはSleepの誤差のほうが大きいので、CPUを回して待つ(スピンウェイト)。
                // 電力とCPU時間を捨てる代わりにフレームの立ち上がりが正確になる。
                // 「正確さのためにCPUを燃やす」のはゲームループの定番の割り切り。
                Thread.SpinWait(50);
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escで終了。全画面表示を試すときなど、閉じる手段があると便利。
        if (e.KeyCode == Keys.Escape)
        {
            Close();
        }

        // W: 塗りつぶしの上にワイヤーフレームを重ねる。
        // 「エッジ関数がどこまでを内側と判定したか」を輪郭と見比べられる。
        if (e.KeyCode == Keys.W)
        {
            _showWireframe = !_showWireframe;
        }

        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // ここでフラグを倒すことで、Run() のループが次の判定で抜ける。
        _running = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // GDI+ のオブジェクトはOSリソース(GDIハンドル)を掴んでいる。
            // GC任せにすると解放が遅れるので、明示的に捨てる。
            _graphics?.Dispose();
            _backBuffer.Dispose();
        }

        base.Dispose(disposing);
    }
}
