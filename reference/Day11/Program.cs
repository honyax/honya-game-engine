using System.Diagnostics;

namespace RawGL;

/// <summary>
/// エントリポイントとゲームループ。
///
/// Day 1 では GameWindow(= Form の派生)がループを抱えていたが、
/// Phase 2 ではウィンドウとループを分けた。
/// ウィンドウは「OS との窓口」に徹し、ループは「毎フレーム何をするか」だけを持つ。
/// Day 12 でここに OpenGL コンテキストの生成が挟まり、
/// Day 13 で Render の中身が GPU への命令に置き換わる。骨格は変わらない。
/// </summary>
internal static class Program
{
    // 解像度は Day 1 から据え置き。同じ絵を出して比較するのが今日の趣旨。
    private const int Width = 640;
    private const int Height = 480;

    private const double TargetFps = 60.0;
    private const double TargetFrameSeconds = 1.0 / TargetFps;

    /// <summary>
    /// スピン待ちに切り替える残り時間のしきい値。Day 1 の実測(5ms)をそのまま使う。
    /// Thread.Sleep(1) は実測で平均4ms前後、最悪14ms眠るので当てにならない。
    /// </summary>
    private const double SpinThresholdSeconds = 0.005;

    /// <summary>
    /// エントリポイント。
    ///
    /// **[STAThread] が消えている**。あれは WinForms が内部で使う COM(クリップボード、
    /// ファイルダイアログ、ドラッグ＆ドロップ)が STA を要求するために必要だったもので、
    /// 生の Win32 メッセージループには要らない。
    /// 「おまじない」だと思っていたものにも、ちゃんと理由があった、という一例。
    /// </summary>
    private static void Main()
    {
        // **ウィンドウを1枚も作る前に**宣言する必要がある。
        // 後から呼んでも、既に作られたウィンドウのDPI仮想化は解除されない。
        // Day 1〜10 では csproj の ApplicationHighDpiMode がこれを裏でやっていた。
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var framebuffer = new Framebuffer(Width, Height);

        using var window = new Win32Window("Day11 - Win32 P/Invoke", Width, Height);
        using var presenter = new GdiPresenter(window.Hwnd, window.ClientWidth, window.ClientHeight);

        RunLoop(window, presenter, framebuffer);
    }

    /// <summary>
    /// ゲームループ本体。構造は Day 1 と同一。
    ///   メッセージ処理 → 更新 → 描画 → 転送 → 次フレームまで待つ
    /// 違うのは1行目が <c>Application.DoEvents()</c> から
    /// 自前の <see cref="Win32Window.ProcessMessages"/> になったことだけ。
    /// </summary>
    private static void RunLoop(Win32Window window, GdiPresenter presenter, Framebuffer framebuffer)
    {
        var clock = Stopwatch.StartNew();

        double previousSeconds = 0.0;
        double nextFrameSeconds = 0.0;

        // アニメーション用の時計。一時停止中は進まないので、実時間とは別に持つ。
        double animationSeconds = 0.0;
        bool paused = false;

        // 矢印キーによる手動オフセット。押しっぱなしの入力が効いているかの確認用。
        double manualX = 0.0;
        double manualY = 0.0;

        // 集計(0.5秒ぶんを平均してタイトルに出す)
        double fpsElapsed = 0.0;
        int fpsFrames = 0;
        double renderSecondsAccum = 0.0;
        double presentSecondsAccum = 0.0;

        while (window.ProcessMessages())
        {
            double nowSeconds = clock.Elapsed.TotalSeconds;
            double deltaSeconds = nowSeconds - previousSeconds;
            previousSeconds = nowSeconds;

            // --- 入力 ---

            if (window.WasKeyPressed(Win32.VK_ESCAPE))
            {
                window.Close();

                // Close() は WM_DESTROY → PostQuitMessage を起こすだけで、
                // WM_QUIT を実際に受け取るのは次の ProcessMessages。
                // ウィンドウが消えた後に転送しても意味が無いので、ここで抜ける。
                break;
            }

            if (window.WasKeyPressed(Win32.VK_SPACE))
            {
                paused = !paused;
            }

            const double ManualSpeed = 240.0;   // ピクセル/秒

            if (window.IsKeyDown(Win32.VK_LEFT)) manualX -= ManualSpeed * deltaSeconds;
            if (window.IsKeyDown(Win32.VK_RIGHT)) manualX += ManualSpeed * deltaSeconds;
            if (window.IsKeyDown(Win32.VK_UP)) manualY -= ManualSpeed * deltaSeconds;
            if (window.IsKeyDown(Win32.VK_DOWN)) manualY += ManualSpeed * deltaSeconds;

            if (!paused)
            {
                animationSeconds += deltaSeconds;
            }

            // --- 描画 ---

            double renderStartSeconds = clock.Elapsed.TotalSeconds;
            Render(framebuffer, animationSeconds, manualX, manualY);
            renderSecondsAccum += clock.Elapsed.TotalSeconds - renderStartSeconds;

            // --- 転送 ---
            // Day 1 では GDI+ 経由でここに約4.6ms(Day01.md の記録では約6ms)掛かっていた。
            // 素の GDI に降りると何ms になるか、というのが今日の実測の見どころ。
            double presentStartSeconds = clock.Elapsed.TotalSeconds;
            presenter.Present(framebuffer);
            presentSecondsAccum += clock.Elapsed.TotalSeconds - presentStartSeconds;

            // --- 計測表示 ---

            fpsFrames++;
            fpsElapsed += deltaSeconds;
            if (fpsElapsed >= 0.5)
            {
                window.SetTitle(
                    $"Day11 - Win32 P/Invoke  {fpsFrames / fpsElapsed:F1} fps | "
                    + $"render {renderSecondsAccum / fpsFrames * 1000.0:F2} ms | "
                    + $"present {presentSecondsAccum / fpsFrames * 1000.0:F2} ms"
                    + (paused ? " | 一時停止中" : string.Empty)
                    + " | Space:一時停止 矢印:移動 Esc:終了");

                fpsFrames = 0;
                fpsElapsed = 0.0;
                renderSecondsAccum = 0.0;
                presentSecondsAccum = 0.0;
            }

            // --- 次フレームまで待つ ---
            // 「前回の目標時刻 + 16.67ms」を積むのは Day 1 と同じ(誤差を累積させないため)。
            nextFrameSeconds += TargetFrameSeconds;

            double current = clock.Elapsed.TotalSeconds;
            if (nextFrameSeconds < current)
            {
                nextFrameSeconds = current;
            }

            WaitUntil(clock, nextFrameSeconds);
        }
    }

    /// <summary>
    /// 1フレーム分の絵を作る。**Day 1 と同じ絵**を意図的に描いている。
    /// 「WinForms を全部剥がしても、出てくる画は1ピクセルも変わらない」ことを
    /// 目で確かめるのが今日のゴールなので、ここは変えない。
    /// </summary>
    private static void Render(Framebuffer framebuffer, double timeSeconds, double manualX, double manualY)
    {
        int width = framebuffer.Width;
        int height = framebuffer.Height;
        int[] pixels = framebuffer.Pixels;

        // 背景: X方向に赤、Y方向に緑、青は時間で明滅するグラデーション。
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

        // 動く白い四角。60fps で滑らかに動いているかを目視で確認するための的。
        const int Size = 48;
        const double SpeedX = 220.0;   // ピクセル/秒
        const double SpeedY = 130.0;

        int boxX = PingPong(timeSeconds * SpeedX, width - Size);
        int boxY = PingPong(timeSeconds * SpeedY, height - Size);

        // 矢印キーのぶんを足して、画面内に収まるよう切り詰める。
        boxX = Math.Clamp(boxX + (int)manualX, 0, width - Size);
        boxY = Math.Clamp(boxY + (int)manualY, 0, height - Size);

        framebuffer.FillRect(boxX, boxY, Size, Size, Framebuffer.Rgb(255, 255, 255));
    }

    /// <summary>0 と max の間を往復する値を返す(端で跳ね返る三角波)。</summary>
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
    /// 指定時刻まで待つ。Day 1 と同じハイブリッド方式
    /// (余裕があるうちは Sleep して CPU を譲り、最後の数msはスピンウェイト)。
    ///
    /// なお Day 12 で OpenGL に移ると、SwapBuffers を VSync(垂直同期)に同期させられるので
    /// **この待ちは GPU 側に任せられるようになる**。
    /// 「フレームレート制限を自前で書く」のはソフトウェアレンダリングならではの仕事だった。
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
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(50);
            }
        }
    }
}
