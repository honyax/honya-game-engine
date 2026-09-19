using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace HardwareRayTracer;

/// <summary>
/// 窓。Day 1〜10 の GameWindow と同じ「自前のループ + LockBits で転送」の形。
///
/// <para>
/// <b>Vulkan は画面に一切関わっていない</b>のがここの見どころ。<c>VkSwapchainKHR</c> も
/// <c>VkSurfaceKHR</c> も出てこない。GPU が作った画素を <c>int[]</c> で受け取り、
/// それを Day 1 と同じやり方で貼るだけ。
/// </para>
/// <para>
/// 本気で速くするならスワップチェーンを使って GPU から直接画面に出す(引き取りの往復が消える)。
/// ここで遠回りしているのは<b>スワップチェーンの儀式がもう 300 行ある</b>からで、
/// 今日の主題(device・メモリ・ディスクリプタ・コマンド)から目を逸らしたくない。
/// Day 62b・62c も同じ窓のまま進む。
/// </para>
/// </summary>
internal sealed class ViewerWindow : Form
{
    private const double TargetFps = 60.0;

    private readonly int _width;
    private readonly int _height;

    private readonly VulkanDevice? _device;

    /// <summary>GPU の素性、または使えなかった理由。HUD に出す。</summary>
    private readonly string _deviceMessage;

    private readonly ShaderCompiler? _compiler;

    private readonly int[] _pixels;
    private readonly Bitmap _backBuffer;

    private readonly Font _font = new("Meiryo UI", 9.0f);
    private readonly SolidBrush _textBrush = new(Color.White);
    private readonly SolidBrush _panelBrush = new(Color.FromArgb(170, 0, 0, 0));

    private Graphics? _screen;
    private Graphics? _overlay;
    private bool _running;

    private ComputeRenderer? _renderer;

    /// <summary>シェーダの翻訳やパイプラインの作成に失敗したときの説明。HUD に出す。</summary>
    private string? _error;

    private int _presetIndex = 1;
    private int _mode;
    private OrbitView _view = SceneData.DefaultView;
    private bool _hudVisible = true;

    /// <summary>直近 30 フレームの所要時間の平均(ms)。1フレームだけ見ると揺れが大きい。</summary>
    private double _averageMilliseconds;

    // --- マウスで回す ---
    private bool _dragging;
    private Point _dragStart;
    private OrbitView _dragStartView;

    public ViewerWindow(int width, int height)
    {
        _width = width;
        _height = height;
        _pixels = new int[width * height];
        _backBuffer = new Bitmap(width, height, PixelFormat.Format32bppRgb);

        // **失敗しても落とさない**。Vulkan が使えない環境でも「なぜ駄目か」を画面に出したい。
        _device = VulkanDevice.TryCreate(out _deviceMessage);
        if (_device is not null)
        {
            _compiler = new ShaderCompiler();
            RebuildRenderer();
        }

        Text = "Day62a - Vulkan を立ち上げる: コンピュートシェーダで球を撃つ";

        // Day 1 と同じ。自動の DPI 拡大を止めて、計算した1画素 = 画面の1画素にする。
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(width, height);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    public void Run()
    {
        Show();
        _screen = CreateGraphics();
        _overlay = Graphics.FromImage(_backBuffer);
        _overlay.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _running = true;

        var clock = Stopwatch.StartNew();
        double nextFrameSeconds = 0.0;

        while (_running)
        {
            Application.DoEvents();
            if (!_running)
            {
                break;
            }

            if (_renderer is not null)
            {
                _renderer.Render(new Camera(_view, _width, _height), _mode, _pixels);

                // 指数移動平均。1フレームの値は数 ms の幅で揺れるので、読める形にならす。
                _averageMilliseconds = _averageMilliseconds == 0.0
                    ? _renderer.LastFrameMilliseconds
                    : _averageMilliseconds * 0.9 + _renderer.LastFrameMilliseconds * 0.1;
            }

            Present();

            nextFrameSeconds = Math.Max(nextFrameSeconds + 1.0 / TargetFps, clock.Elapsed.TotalSeconds);
            int sleepMilliseconds = (int)((nextFrameSeconds - clock.Elapsed.TotalSeconds) * 1000.0);
            if (sleepMilliseconds > 0)
            {
                // **GPU を 100% に張り付かせない**ための休み。1フレームが 2ms で終わる設定では、
                // 休みを入れないと電源に対してかなり厳しい負荷になる(Day 61 でも同じ配慮をした)。
                Thread.Sleep(sleepMilliseconds);
            }
        }
    }

    /// <summary>
    /// 球の数を変えたので、レンダラを作り直す。
    ///
    /// <para>
    /// バッファの大きさが変わるとディスクリプタの挿し直しが要るので、<b>まるごと作り直す</b>のが素直。
    /// 作り直しに 10ms ほどかかる(ほとんどはパイプラインの作成 = SPIR-V から機械語への翻訳)。
    /// 実際のエンジンはこれを避けるために、起動時に取りうる組み合わせを全部作っておく。
    /// </para>
    /// </summary>
    private void RebuildRenderer()
    {
        if (_device is null || _compiler is null)
        {
            return;
        }

        _renderer?.Dispose();
        _renderer = null;
        _error = null;
        _averageMilliseconds = 0.0;

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "shaders", "trace.comp");
            uint[] spirv = _compiler.CompileComputeFile(path);
            _renderer = new ComputeRenderer(_device, _width, _height, SceneData.Create(SceneData.Presets[_presetIndex]), spirv);
        }
        catch (Exception e)
        {
            // GLSL を書き換えて試しているときはここに来る。落とさずに理由を画面に出す。
            _error = e.Message;
        }
    }

    private void Present()
    {
        CopyPixelsTo(_backBuffer);

        if (_hudVisible)
        {
            DrawHud(_overlay!);
        }

        _screen!.DrawImage(_backBuffer, new Rectangle(0, 0, _width, _height));
    }

    /// <summary>Day 1 の Present と同じ。行ごとに Stride で進めてコピーする。</summary>
    private void CopyPixelsTo(Bitmap bitmap)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, _width, _height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < _height; y++)
            {
                Marshal.Copy(_pixels, y * _width, data.Scan0 + y * data.Stride, _width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void DrawHud(Graphics g)
    {
        string[] modeNames = ["陰影", "法線", "交差判定の回数"];
        var lines = new List<string>
        {
            $"GPU   : {_deviceMessage}",
            $"球    : {SceneData.Presets[_presetIndex]} 個 (1/2/3 で {string.Join(" / ", SceneData.Presets)})",
            $"表示  : {modeNames[_mode]} (M)",
        };

        if (_renderer is not null)
        {
            double ms = _averageMilliseconds;

            // 2つの数を並べて出すのが今日の仕掛け。**球を増やすと伸びるのは右だけ**
            // ——左は引き取りの転送と提出の往復で、光線の本数とは関係が無い。
            lines.Add($"1枚   : {ms:F2} ms ({(ms > 0.0 ? 1000.0 / ms : 0.0):F0} 枚/秒)"
                + $"  うち光線追跡 {_renderer.LastTraceMilliseconds:F2} ms");
        }

        if (_error is not null)
        {
            // 翻訳エラーは何行にもなるので、先頭3行だけ出す(全文は標準エラーにも出ている)。
            lines.Add(string.Empty);
            foreach (string line in _error.Split('\n').Take(3))
            {
                lines.Add(line.TrimEnd());
            }
        }

        lines.Add(string.Empty);
        lines.Add("ドラッグ: 回す / ホイール: 寄る / R: 視点を戻す / H: この表示 / Esc: 終了");

        float lineHeight = _font.GetHeight(g) + 2.0f;
        var rect = new RectangleF(0.0f, 0.0f, _width, lines.Count * lineHeight + 8.0f);
        g.FillRectangle(_panelBrush, rect);

        float y = 4.0f;
        foreach (string line in lines)
        {
            g.DrawString(line, _font, _textBrush, 6.0f, y);
            y += lineHeight;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.KeyCode)
        {
            case Keys.Escape:
                _running = false;
                break;

            case Keys.D1:
            case Keys.D2:
            case Keys.D3:
                _presetIndex = e.KeyCode - Keys.D1;
                RebuildRenderer();
                break;

            case Keys.M:
                _mode = (_mode + 1) % 3;
                break;

            case Keys.R:
                _view = SceneData.DefaultView;
                break;

            case Keys.H:
                _hudVisible = !_hudVisible;
                break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStart = e.Location;
            _dragStartView = _view;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        float dx = (e.X - _dragStart.X) * 0.006f;
        float dy = (e.Y - _dragStart.Y) * 0.006f;
        _view = _dragStartView with
        {
            Yaw = _dragStartView.Yaw - dx,

            // 真上・真下を越えると、カメラの「右」を作る外積が壊れる(前とワールドの上が平行になる)。
            Pitch = Math.Clamp(_dragStartView.Pitch + dy, -1.45f, 1.45f),
        };
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float scale = MathF.Pow(0.9f, e.Delta / 120.0f);
        _view = _view with { Distance = Math.Clamp(_view.Distance * scale, 2.0f, 60.0f) };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _running = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // **順番が大事**。レンダラが持つ資源はデバイスより先に壊す
            // (Vulkan は参照を数えてくれないので、デバイスを先に壊すと落ちる)。
            _renderer?.Dispose();
            _compiler?.Dispose();
            _device?.Dispose();

            _screen?.Dispose();
            _overlay?.Dispose();
            _backBuffer.Dispose();
            _font.Dispose();
            _textBrush.Dispose();
            _panelBrush.Dispose();
        }

        base.Dispose(disposing);
    }
}
