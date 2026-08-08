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

        // Format32bppRgb: 1ピクセル32bitで、上位8bitのアルファは「未使用」扱い。
        // Format32bppArgb にするとGDI+がアルファ合成を試みる可能性があり、
        // 画面に出すだけの用途では無駄。メモリ配置は B,G,R,X の順で、
        // Framebuffer.Rgb が作る 0xAARRGGBB とそのまま一致する。
        _backBuffer = new Bitmap(width, height, PixelFormat.Format32bppRgb);

        Text = "Day01 - Framebuffer";

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

            Render(nowSeconds);
            Present();

            // FPSは毎フレーム表示すると数字が暴れて読めないので、0.5秒ぶんを平均する。
            fpsFrames++;
            fpsElapsed += deltaSeconds;
            if (fpsElapsed >= 0.5)
            {
                Text = $"Day01 - Framebuffer  {fpsFrames / fpsElapsed:F1} fps";
                fpsFrames = 0;
                fpsElapsed = 0.0;
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

    /// <summary>
    /// 1フレーム分の絵をフレームバッファに描く。
    /// Day 1 の時点では「全ピクセルを自分で埋める」ことそのものが主題。
    /// </summary>
    private void Render(double timeSeconds)
    {
        int width = _framebuffer.Width;
        int height = _framebuffer.Height;
        int[] pixels = _framebuffer.Pixels;

        // 背景: X方向に赤、Y方向に緑、青は時間で明滅するグラデーション。
        // 640x480 = 307,200ピクセルを毎フレームCPUで書いている。
        // 16.67msの持ち時間を割ると1ピクセルあたり約54ns。この予算感がPhase 1の前提になる。
        byte blue = (byte)((Math.Sin(timeSeconds * 2.0) * 0.5 + 0.5) * 255.0);

        for (int y = 0; y < height; y++)
        {
            byte g = (byte)(y * 255 / (height - 1));
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                byte r = (byte)(x * 255 / (width - 1));
                pixels[rowOffset + x] = Framebuffer.Rgb(r, g, blue);
            }
        }

        // 動く白い四角。60fpsで滑らかに動いているか、コマ落ちしていないかを
        // 目視で確認するための的。数字のFPSより体感の判断材料になる。
        const int size = 48;
        const double speedX = 220.0;   // ピクセル/秒
        const double speedY = 130.0;

        int boxX = PingPong(timeSeconds * speedX, width - size);
        int boxY = PingPong(timeSeconds * speedY, height - size);
        _framebuffer.FillRect(boxX, boxY, size, size, Framebuffer.Rgb(255, 255, 255));
    }

    /// <summary>
    /// 0 と max の間を往復する値を返す(端で跳ね返る三角波)。
    /// 経過時間から直接位置を求めているので、フレームレートが変動しても
    /// 移動速度は変わらない。「位置を毎フレーム加算する」書き方だと
    /// フレームレート依存になってしまう点は、Day 19 のゲームループ回で改めて扱う。
    /// </summary>
    private static int PingPong(double value, int max)
    {
        if (max <= 0)
        {
            return 0;
        }

        double period = max * 2.0;
        double t = value % period;
        return (int)(t <= max ? t : period - t);
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
