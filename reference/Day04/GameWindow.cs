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

        Text = "Day04 - 属性補間";

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
                Text = $"Day04 - 属性補間  {fpsFrames / fpsElapsed:F1} fps | {TriangleCount} tri | "
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

    /// <summary>円盤を構成する三角形の枚数。</summary>
    private const int DiscTriangles = 64;

    /// <summary>1フレームに描く三角形の総数(グラデーション1枚 + 市松1枚 + 円盤)。</summary>
    private const int TriangleCount = 1 + 1 + DiscTriangles;

    /// <summary>
    /// 1フレーム分の絵をフレームバッファに描く。
    ///
    /// Day 4 の題材は「3頂点が持つ値を内部へ配り直す」ことの3つの見せ方。
    ///   - グラデーション三角形 … 色を補間するとどう見えるか(定番の絵)
    ///   - 市松模様の三角形     … 補間した値を色以外の用途に使う(Day 8 のUVの予告)
    ///   - 円盤                 … 隣の三角形と頂点色を共有すると継ぎ目が消える
    /// </summary>
    private void Render(double timeSeconds)
    {
        _framebuffer.Clear(Framebuffer.Rgb(12, 14, 22));

        DrawGradientTriangle(timeSeconds);
        DrawBarycentricPattern(timeSeconds);
        DrawSmoothDisc(timeSeconds);
    }

    /// <summary>
    /// 3頂点に赤・緑・青を割り当てた三角形。グラフィックスの「Hello, World」。
    ///
    /// 各頂点の色が最も濃く出て、内部では滑らかに混ざる。
    /// 3色が等しく混ざる点(重心)がちょうど灰色になっていれば、
    /// 3つの重みの合計が 1 になっている証拠。
    /// </summary>
    private void DrawGradientTriangle(double timeSeconds)
    {
        const double radius = 96.0;
        int centerX = 150;
        int centerY = 132;

        Span<Vertex> v = stackalloc Vertex[3];
        for (int i = 0; i < 3; i++)
        {
            double angle = timeSeconds * 0.7 + i * (2.0 * Math.PI / 3.0);
            int x = centerX + (int)Math.Round(Math.Cos(angle) * radius);
            int y = centerY + (int)Math.Round(Math.Sin(angle) * radius);

            // 頂点0 = 赤、頂点1 = 緑、頂点2 = 青
            v[i] = new Vertex(x, y, i == 0 ? 1.0f : 0.0f, i == 1 ? 1.0f : 0.0f, i == 2 ? 1.0f : 0.0f);
        }

        _rasterizer.FillTriangle(v[0], v[1], v[2]);

        if (_showWireframe)
        {
            _rasterizer.DrawTriangleWireframe(v[0].X, v[0].Y, v[1].X, v[1].Y, v[2].X, v[2].Y, Framebuffer.Rgb(255, 255, 255));
        }
    }

    /// <summary>
    /// バリセントリック座標を「色」ではなく「模様の材料」として使う。
    ///
    /// 頂点1・頂点2に (1,0) と (0,1) を持たせて補間すると、
    /// 三角形の内部に「頂点0を原点とする斜めの座標系」ができる。
    /// その値を整数化して偶奇で色を変えると市松模様になる。
    ///
    /// これはまさに Day 8 のテクスチャマッピングそのもので、
    /// 違いは「模様を計算で作るか、画像から読むか」だけ。
    /// 補間した値を色として直接使わずに、何かを引く鍵として使う——という
    /// 発想の転換が今日の一番の収穫かもしれない。
    ///
    /// 実装の都合として、ここでは Vertex の R, G を座標の入れ物として流用している
    /// (Day 8 で Vertex に専用の U, V を足すまでのつなぎ)。
    /// </summary>
    private void DrawBarycentricPattern(double timeSeconds)
    {
        const double radius = 96.0;
        const int checkerDivisions = 8;

        int centerX = 470;
        int centerY = 132;

        Span<Vertex> v = stackalloc Vertex[3];
        for (int i = 0; i < 3; i++)
        {
            double angle = -timeSeconds * 0.5 + i * (2.0 * Math.PI / 3.0);
            int x = centerX + (int)Math.Round(Math.Cos(angle) * radius);
            int y = centerY + (int)Math.Round(Math.Sin(angle) * radius);

            // R を U、G を V として使う。頂点0 が原点、頂点1 が U 軸、頂点2 が V 軸。
            v[i] = new Vertex(x, y, i == 1 ? 1.0f : 0.0f, i == 2 ? 1.0f : 0.0f, 0.0f);
        }

        // ここだけは補間結果を自前で使いたいので、ラスタライザには任せず
        // 「補間された値を受け取って色を決める」という形で書く。
        // Day 9 で「ピクセルごとの色の決め方」を差し替えられるようにするときの原型になる。
        _rasterizer.FillTriangle(v[0], v[1], v[2], (u, vv, _) =>
        {
            int cell = (int)(u * checkerDivisions) + (int)(vv * checkerDivisions);
            return (cell & 1) == 0
                ? Framebuffer.Rgb(0.95f, 0.80f, 0.35f)
                : Framebuffer.Rgb(0.25f, 0.30f, 0.45f);
        });

        if (_showWireframe)
        {
            _rasterizer.DrawTriangleWireframe(v[0].X, v[0].Y, v[1].X, v[1].Y, v[2].X, v[2].Y, Framebuffer.Rgb(255, 255, 255));
        }
    }

    /// <summary>
    /// 円盤を頂点カラーで滑らかに塗る。
    ///
    /// Day 3 では扇形1枚が単色だったので、境界に色の段差(マッハバンド)が見えていた。
    /// 今日は隣り合う扇形が縁の頂点色を共有しているので、境界で色が連続し、
    /// 継ぎ目が完全に消える。三角形の枚数は同じなのに滑らかに見えるのがポイント。
    ///
    /// これは Day 9 のグーローシェーディングと原理的にまったく同じこと。
    /// 「頂点で計算して内部は補間」という手抜きが、なぜあれほど効果的なのかがここで分かる。
    /// </summary>
    private void DrawSmoothDisc(double timeSeconds)
    {
        const double radius = 110.0;
        int centerX = _framebuffer.Width / 2;
        int centerY = 350;

        // 中心の頂点は白。縁の頂点は角度に応じた虹色。
        var center = new Vertex(centerX, centerY, 1.0f, 1.0f, 1.0f);

        for (int i = 0; i < DiscTriangles; i++)
        {
            double a0 = -timeSeconds * 0.3 + i * (2.0 * Math.PI / DiscTriangles);
            double a1 = -timeSeconds * 0.3 + (i + 1) * (2.0 * Math.PI / DiscTriangles);

            var e0 = RimVertex(centerX, centerY, radius, a0, i / (double)DiscTriangles);
            var e1 = RimVertex(centerX, centerY, radius, a1, (i + 1) / (double)DiscTriangles);

            _rasterizer.FillTriangle(center, e0, e1);

            if (_showWireframe)
            {
                _rasterizer.DrawTriangleWireframe(center.X, center.Y, e0.X, e0.Y, e1.X, e1.Y, Framebuffer.Rgb(20, 20, 20));
            }
        }
    }

    /// <summary>円盤の縁の頂点を1つ作る。位置は角度から、色は色相から。</summary>
    private static Vertex RimVertex(int centerX, int centerY, double radius, double angle, double hue01)
    {
        int color = HueColor(hue01);
        return Vertex.FromPackedColor(
            centerX + (int)Math.Round(Math.Cos(angle) * radius),
            centerY + (int)Math.Round(Math.Sin(angle) * radius),
            color);
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
