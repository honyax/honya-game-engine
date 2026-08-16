using System.Diagnostics;
using System.Text;

namespace RawGL;

/// <summary>
/// エントリポイントとゲームループ。
///
/// Day 11 との違いは、フレームバッファと GDI 転送がまるごと消えて、
/// 代わりに <see cref="GLContext"/> が入ったこと。
/// 画面を塗る主体が **CPU から GPU に移った**のがこの日の意味で、
/// Day 1 から数えて初めて、ピクセルを1つも自分で書かなくなる。
///
/// Day 13 では、この「画面全体を単色で塗る」を「三角形を1枚描く」に差し替える。
/// </summary>
internal static class Program
{
    private const int Width = 640;
    private const int Height = 480;

    /// <summary>
    /// 要求する OpenGL のバージョン。
    ///
    /// 3.3 を選ぶ理由は、**シェーダを書くうえで必要なものがひととおり揃った
    /// 最初のバージョン**だから(VAO、レイアウト修飾子、インスタンシング)。
    /// 4.x にしか無い機能は Phase 6 以降まで使わないので、
    /// 対応環境の広い 3.3 を土台にしておく。LearnOpenGL も同じ選択をしている。
    /// </summary>
    private const int GLMajorVersion = 3;

    private const int GLMinorVersion = 3;

    private static void Main()
    {
        AttachConsole();

        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        using var window = new Win32Window("Day12 - wglコンテキスト", Width, Height);

        // ウィンドウができてから、そのウィンドウにコンテキストを結び付ける。
        // 逆順にはできない——コンテキストは HDC を必要とし、HDC は HWND を必要とする。
        using var context = new GLContext(window.Hwnd, GLMajorVersion, GLMinorVersion);

        PrintContextInfo(context);

        // ビューポート = クリップ座標を画面のどの矩形へ写すか。
        // Phase 1 の Rasterizer.ToScreen がやっていたビューポート変換そのもので、
        // GPU では固定機能として残っている数少ない部分。
        // 実はコンテキスト生成時にウィンドウ全体で初期化済みなので、
        // ここでの呼び出しは「明示しておく」以上の意味は無い。
        GL.glViewport(0, 0, window.ClientWidth, window.ClientHeight);
        GL.CheckError("glViewport");

        RunLoop(window, context);
    }

    /// <summary>
    /// ゲームループ。Day 11 から <c>WaitUntil</c> が消えているのが最大の差分。
    /// フレームレートの制限は VSync(= ドライバとモニタ)に任せる。
    /// </summary>
    private static void RunLoop(Win32Window window, GLContext context)
    {
        // 既定で VSync ON。60Hz のモニタなら SwapBuffers が
        // 「次の垂直帰線まで待つ」ので、これだけで 60fps に張り付く。
        bool vsync = true;
        context.TrySetSwapInterval(1);

        var clock = Stopwatch.StartNew();

        double previousSeconds = 0.0;
        double animationSeconds = 0.0;
        bool paused = false;

        double fpsElapsed = 0.0;
        int fpsFrames = 0;

        while (window.ProcessMessages())
        {
            double nowSeconds = clock.Elapsed.TotalSeconds;
            double deltaSeconds = nowSeconds - previousSeconds;
            previousSeconds = nowSeconds;

            // --- 入力 ---

            if (window.WasKeyPressed(Win32.VK_ESCAPE))
            {
                window.Close();
                break;
            }

            if (window.WasKeyPressed(Win32.VK_SPACE))
            {
                paused = !paused;
            }

            // V: VSync の切り替え。**今日いちばん分かりやすい実験**。
            // OFF にすると fps が数千に跳ね上がり、
            // 「60fps はモニタが決めている」ことが数字で見える。
            if (window.WasKeyPressed((int)'V'))
            {
                vsync = !vsync;
                context.TrySetSwapInterval(vsync ? 1 : 0);
            }

            if (!paused)
            {
                animationSeconds += deltaSeconds;
            }

            // --- 描画 ---
            //
            // Day 11 まではここで 307,200 ピクセルを CPU で書いていた。
            // 今日からは「この色で塗れ」と GPU に伝えるだけで、
            // 実際の塗りつぶしは GPU の中で並列に行われる。

            const double Phase = 2.0 * Math.PI / 3.0;
            float r = (float)(0.5 + (0.5 * Math.Sin(animationSeconds)));
            float g = (float)(0.5 + (0.5 * Math.Sin(animationSeconds + Phase)));
            float b = (float)(0.5 + (0.5 * Math.Sin(animationSeconds + (Phase * 2.0))));

            // glClearColor は「次に glClear したときに使う色」を**設定するだけ**。
            // 状態を設定してから実行する、というのが OpenGL 全体の様式で、
            // Day 13 のシェーダやバッファも同じ「バインドしてから使う」形になる。
            GL.glClearColor(r, g, b, 1.0f);
            GL.glClear(GL.GL_COLOR_BUFFER_BIT);

            context.SwapBuffers();

            // --- 計測表示 ---

            fpsFrames++;
            fpsElapsed += deltaSeconds;
            if (fpsElapsed >= 0.5)
            {
                window.SetTitle(
                    $"Day12 - OpenGL {GLMajorVersion}.{GLMinorVersion} Core  "
                    + $"{fpsFrames / fpsElapsed:F1} fps | VSync:{(vsync ? "ON" : "OFF")}"
                    + (paused ? " | 一時停止中" : string.Empty)
                    + " | V:VSync Space:停止 Esc:終了");

                fpsFrames = 0;
                fpsElapsed = 0.0;
            }
        }
    }

    /// <summary>
    /// 実際に得られたコンテキストの素性をコンソールへ出す。
    ///
    /// **要求した 3.3 が本当に取れたか**、そして
    /// **ソフトウェア実装に落ちていないか**をここで確認する。
    /// GL_RENDERER に "GDI Generic" と出たら、ハードウェアアクセラレーションが
    /// 効いていない(ピクセルフォーマットの選択に失敗している)。
    /// </summary>
    private static void PrintContextInfo(GLContext context)
    {
        Console.WriteLine("=== OpenGL コンテキスト ===");
        Console.WriteLine($"GL_VENDOR                   : {context.Vendor}");
        Console.WriteLine($"GL_RENDERER                 : {context.Renderer}");
        Console.WriteLine($"GL_VERSION                  : {context.Version}");
        Console.WriteLine($"GL_SHADING_LANGUAGE_VERSION : {context.ShadingLanguageVersion}");
        Console.WriteLine($"拡張の数                    : {context.ExtensionCount}");
        Console.WriteLine($"WGL_EXT_swap_control        : {(context.HasSwapControl ? "あり" : "なし")}");

        // コアプロファイルでは glGetString(GL_EXTENSIONS) が
        // GL_INVALID_ENUM になって NULL を返す(要点6)。実際にそうなることを見せる。
        IntPtr legacyExtensions = GL.glGetString(GL.GL_EXTENSIONS);
        Console.WriteLine(
            $"glGetString(GL_EXTENSIONS)  : {(legacyExtensions == IntPtr.Zero ? "NULL(コアプロファイルなので正常)" : "取得できた")}");
        GL.glGetError();   // 上でわざと出したエラーを捨てる

        Console.WriteLine();
        Console.WriteLine("拡張の例(先頭5個):");
        for (int i = 0; i < Math.Min(5, context.ExtensionCount); i++)
        {
            Console.WriteLine($"  {GL.GetExtension(i)}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// コンソールを1枚立てて、標準出力をそこへ繋ぎ直す。
    ///
    /// csproj が WinExe なのでこのプロセスにコンソールは無く、
    /// <c>Console.WriteLine</c> は何処にも出ない。
    /// GL の情報や、Day 13 で必ず必要になるシェーダのコンパイルエラーを
    /// 見るために、自分で作ってしまう。
    ///
    /// AllocConsole の後に標準出力を開き直しているのは、
    /// .NET が「コンソールが無い」状態で握った出力先をそのまま使い続けるため。
    /// </summary>
    private static void AttachConsole()
    {
        if (!Win32.AllocConsole())
        {
            return;
        }

        var standardOutput = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        Console.SetOut(standardOutput);
        Console.OutputEncoding = new UTF8Encoding(false);
    }
}
