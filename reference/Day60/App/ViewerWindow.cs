using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CpuRayTracer;

/// <summary>
/// 窓。役割は3つ。
///   1. 描画スレッド(<see cref="ProgressiveRenderer"/>)が作った画素を画面へ出す
///   2. キーとマウスを受けて、設定やカメラを変え、描き直しを頼む
///   3. 進み具合・光線の数・1画素の木・自己チェックの結果を文字で重ねる
///
/// <para>
/// Day 1〜10 の GameWindow と同じ「自前のループ + LockBits で転送」の形だが、<b>このクラス自身は1本も光線を追わない</b>。
/// ラスタライザは毎フレーム全部を描き直したが、レイトレーサは 64 サンプル重ねるのに秒単位でかかるので、
/// 描くのは別スレッドに任せ、窓は「できたところまで」を見せ続けるだけにする。
/// </para>
/// </summary>
internal sealed class ViewerWindow : Form
{
    /// <summary>
    /// 表示の更新頻度。絵は1パス(Release で数十 ms)ごとにしか変わらないので 30fps で十分。
    /// 余った CPU は描画スレッドへ回したい。
    /// </summary>
    private const double TargetFps = 30.0;

    private const int MaxDepthLimit = 32;

    private readonly ProgressiveRenderer _renderer;

    /// <summary>描画スレッドから受け取った画素。受け取るたびに上書きする。</summary>
    private readonly int[] _pixels;

    private readonly Bitmap _backBuffer;

    private readonly Font _font = new("Meiryo UI", 9.0f);

    private readonly SolidBrush _textBrush = new(Color.White);

    private readonly SolidBrush _panelBrush = new(Color.FromArgb(170, 0, 0, 0));

    private readonly Pen _markerPen = new(Color.Red, 1.0f);

    private Graphics? _screen;

    private Graphics? _overlay;

    private bool _running;

    private int _displayVersion = -1;

    /// <summary>起動時は場面4(コーネルボックス)。今日のゴールの絵。</summary>
    private int _sceneIndex = 3;

    /// <summary>右クリックで1画素を追うたびに増やす番号。パストレーサは<b>押すたびに違う道</b>になる。</summary>
    private int _traceSample;

    /// <summary>下の欄の先頭行(PageUp / PageDown で動かす)。自己チェックが 36 行あって入りきらないため。</summary>
    private int _panelScroll;

    private Scene _scene;

    private OrbitView _view;

    private RenderSettings _settings = new();

    /// <summary>
    /// 描き直しの依頼。キーやマウスで立て、ループの頭で1回だけ <see cref="Restart"/> する。
    /// マウスを動かすと1秒に何百回もイベントが来るので、その度に描画を止めて始め直すと、下見すら終わらない。
    /// </summary>
    private bool _restartRequested;

    private bool _hudVisible = true;

    // --- 下の欄(1画素を追った結果 / 自己チェック)---
    private string? _panelTitle;

    private IReadOnlyList<string> _panelLines = Array.Empty<string>();

    /// <summary>1画素を追った画素。赤い十字で印を付ける。</summary>
    private Point? _tracedPixel;

    // --- マウスで回す ---
    private bool _dragging;

    private Point _dragStart;

    private OrbitView _dragStartView;

    public ViewerWindow(int width, int height)
    {
        _renderer = new ProgressiveRenderer(width, height);
        _pixels = new int[width * height];
        _backBuffer = new Bitmap(width, height, PixelFormat.Format32bppRgb);

        _scene = SceneLibrary.Create(_sceneIndex);
        _view = _scene.DefaultView;

        Text = "Day60 - CPU レイトレーサ(2): パストレーシング、モンテカルロ積分、BVH";

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
        _restartRequested = true;

        var clock = Stopwatch.StartNew();
        double nextFrameSeconds = 0.0;

        while (_running)
        {
            Application.DoEvents();
            if (!_running)
            {
                break;
            }

            if (_restartRequested)
            {
                _restartRequested = false;
                Restart();
            }

            _renderer.CopyDisplay(_pixels, ref _displayVersion);
            Present();

            // Day 1 のスピン待ちはしない。ぴったり 30fps に合わせるより、描画スレッドに CPU を譲るほうが大事。
            nextFrameSeconds = Math.Max(nextFrameSeconds + 1.0 / TargetFps, clock.Elapsed.TotalSeconds);
            int sleepMilliseconds = (int)((nextFrameSeconds - clock.Elapsed.TotalSeconds) * 1000.0);
            if (sleepMilliseconds > 0)
            {
                Thread.Sleep(sleepMilliseconds);
            }
        }

        _renderer.Stop();
    }

    private Camera CreateCamera() => new(_view, _renderer.Width, _renderer.Height);

    /// <summary>
    /// 描き直す。<b>場面を作り直す(BVH を組む)前に、必ず描画を止める</b>(Day 60 で追加)。
    ///
    /// <para>
    /// <see cref="Scene"/> は描画中「変わらないもの」として全スレッドで共有している。
    /// <see cref="ProgressiveRenderer.Start"/> は中で <c>Stop()</c> してくれるが、
    /// それより先に <see cref="Scene.Prepare"/> を呼ぶと、<b>走っているスレッドの足元で形の並びが変わる</b>。
    /// </para>
    /// </summary>
    private void Restart()
    {
        _renderer.Stop();
        _scene.Prepare(_settings.Acceleration);
        _renderer.Start(Tracer.Create(_scene, _settings), CreateCamera(), _settings);
    }

    /// <summary>
    /// 設定を変えて描き直す。下の欄(1画素の木)は消さずに残す——設定を変える前の木と、
    /// 同じ画素を右クリックし直した木を見比べられるように。
    /// </summary>
    private void ChangeSettings(RenderSettings settings)
    {
        _settings = settings;
        _restartRequested = true;
    }

    private void Present()
    {
        CopyPixelsTo(_backBuffer);

        if (_hudVisible)
        {
            DrawHud(_overlay!);
        }

        _screen!.DrawImage(_backBuffer, new Rectangle(0, 0, _renderer.Width, _renderer.Height));
    }

    /// <summary>Day 1 の Present と同じ。行ごとに Stride で進めてコピーする。</summary>
    private void CopyPixelsTo(Bitmap bitmap)
    {
        int width = _renderer.Width;
        int height = _renderer.Height;
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(_pixels, y * width, data.Scan0 + y * data.Stride, width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void DrawHud(Graphics g)
    {
        RenderStatus status = _renderer.Status;
        RayCounters rays = status.PassRays;
        int pixelCount = _renderer.Width * _renderer.Height;
        double raysPerSecond = status.PassSeconds > 0.0 ? rays.Total / status.PassSeconds : 0.0;

        bool path = _settings.Algorithm == TraceAlgorithm.Path;

        var lines = new List<string>
        {
            $"場面 {_sceneIndex + 1}/{SceneLibrary.Count}: {_scene.Name}    サンプル {status.Samples} / {status.MaxSamples}{(status.Finished ? "(完了)" : "")}",
            $"1パス {status.PassSeconds:F3} 秒   合計 {status.TotalSeconds:F1} 秒   毎秒 {raysPerSecond / 10000.0:F0} 万本",
            $"1パスの光線: カメラ {Man(rays.Camera)} / 影 {Man(rays.Shadow)} / 反射 {Man(rays.Reflection)} / 屈折 {Man(rays.Refraction)} / 散乱 {Man(rays.Scatter)}"
                + $"   1画素あたり {(double)rays.Total / pixelCount:F2} 本",
            $"追い方 {(path ? "パストレーシング" : "Whitted")} (A)   深さの上限 {_settings.MaxDepth} ([ ])"
                + (path
                    ? $"   NEE {OnOff(_settings.NextEventEstimation)} (N)   ロシアンルーレット {OnOff(_settings.RussianRoulette)} (U)"
                        + $"   乱数を画素ごと {OnOff(_settings.DecorrelatePixels)} (K)"
                    : $"   影 {OnOff(_settings.Shadows)} (S)"),
            AccelerationLine(rays),
            $"フレネル {FresnelName(_settings.Fresnel)} (F)   始点を浮かせる {OnOff(_settings.OffsetOrigin)} (E)"
                + $"   画素内のずらし {OnOff(_settings.Jitter)} (J)   表示 {ViewName(_settings.View)} (V)",
        };

        // 操作の一覧は、下の欄を出していないときだけ。欄を出すと縦が足りなくなる(自己チェックは 36 行ある)。
        if (_panelTitle is null)
        {
            lines.Add("1〜6: 場面   ドラッグ: 回す   ホイール: 寄る   R: カメラを戻す   右クリック: 1画素を追う");
            lines.Add("C: 自己チェック   P: PNG 保存   H: 文字を消す   Esc: 終了");
        }

        if (_settings.View == ViewMode.RayCount)
        {
            lines.Add("光線の数: 1 紺 / 2 青 / 4 水色 / 8 緑 / 16 黄 / 32 赤 / 64 以上 白");
        }
        else if (_settings.View == ViewMode.Cost)
        {
            lines.Add("交差判定の数: 1 紺 / 3 青 / 10 水色 / 32 緑 / 100 黄 / 320 赤 / 1000 以上 白");
        }

        float lineHeight = _font.GetHeight(g);
        float y = DrawBlock(g, lines, 0.0f, lineHeight);

        if (_panelTitle is not null)
        {
            // 入りきらない行は PageUp / PageDown でめくる(Day 60 で追加。自己チェックが 18 項目 36 行になったため)。
            int capacity = Math.Max(2, (int)((_renderer.Height - y - 10.0f) / lineHeight) - 1);
            int rows = Math.Max(1, capacity - 1);
            _panelScroll = Math.Clamp(_panelScroll, 0, Math.Max(0, _panelLines.Count - rows));

            var panel = new List<string>
            {
                _panelTitle + (_panelLines.Count > rows
                    ? $"   ({_panelScroll + 1}〜{Math.Min(_panelScroll + rows, _panelLines.Count)} / {_panelLines.Count} 行。PageUp PageDown でめくる、X で消す)"
                    : "   (X で消す)"),
            };
            panel.AddRange(_panelLines.Skip(_panelScroll).Take(rows));

            DrawBlock(g, panel, y + 4.0f, lineHeight);
        }

        if (_tracedPixel is Point p)
        {
            g.DrawLine(_markerPen, p.X - 6, p.Y, p.X - 2, p.Y);
            g.DrawLine(_markerPen, p.X + 2, p.Y, p.X + 6, p.Y);
            g.DrawLine(_markerPen, p.X, p.Y - 6, p.X, p.Y - 2);
            g.DrawLine(_markerPen, p.X, p.Y + 2, p.X, p.Y + 6);
        }
    }

    /// <summary>半透明の黒い帯を敷いて、その上に行を並べる。戻り値は帯の下端。</summary>
    private float DrawBlock(Graphics g, IReadOnlyList<string> lines, float top, float lineHeight)
    {
        float width = 0.0f;
        foreach (string line in lines)
        {
            width = MathF.Max(width, g.MeasureString(line, _font).Width);
        }

        float height = lines.Count * lineHeight + 6.0f;
        g.FillRectangle(_panelBrush, 0.0f, top, width + 8.0f, height);

        for (int i = 0; i < lines.Count; i++)
        {
            g.DrawString(lines[i], _font, _textBrush, 4.0f, top + 3.0f + i * lineHeight);
        }

        return top + height;
    }

    /// <summary>
    /// 探し方(B キー)と BVH の形、そして<b>光線1本あたりの交差判定の回数</b>(要点8)。
    /// この最後の数字が今日の後半の目で、B を押すと 458 回 → 15 回のように変わる。
    /// </summary>
    private string AccelerationLine(in RayCounters rays)
    {
        string tests = rays.Total > 0
            ? $"   1光線あたり 箱 {(double)rays.BoxTests / rays.Total:F1} / 形 {(double)rays.ShapeTests / rays.Total:F1} 回"
            : string.Empty;

        if (_scene.Bvh is not Bvh bvh)
        {
            return $"探し方 {AccelerationName(_settings.Acceleration)} (B)   形 {_scene.Shapes.Count} 個に総当たり{tests}";
        }

        return $"探し方 {AccelerationName(_settings.Acceleration)} (B)   形 {_scene.Shapes.Count} 個"
            + $"(BVH {bvh.ShapeCount} / 外 {_scene.UnboundedCount})"
            + $" 節点 {bvh.NodeCount} 深さ {bvh.MaxDepth} 作成 {bvh.BuildMilliseconds:F1}ms{tests}";
    }

    private static string Man(long count) => $"{count / 10000.0:F1}万";

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string FresnelName(FresnelMode mode) => mode switch
    {
        FresnelMode.Schlick => "シュリック",
        FresnelMode.Off => "なし",
        _ => "正確",
    };

    private static string ViewName(ViewMode mode) => mode switch
    {
        ViewMode.Normal => "法線",
        ViewMode.RayCount => "光線の数",
        ViewMode.Cost => "交差判定の数",
        _ => "陰影",
    };

    private static string AccelerationName(AccelerationMode mode) => mode switch
    {
        AccelerationMode.Median => "BVH(中央分割)",
        AccelerationMode.Sah => "BVH(SAH)",
        _ => "総当たり",
    };

    /// <summary>
    /// 1画素を追う(右クリック)。描画スレッドとは別に、UI スレッドでその画素の光線を1本だけ追って記録する。
    /// 場面・設定は不変なので、描画スレッドが同じものを読んでいても構わない。
    ///
    /// <para>
    /// パストレーシングでは<b>押すたびに違う道</b>が出る(サンプル番号を1つずつ増やしている)。
    /// 同じ画素を何度も押して、色が大きくばらつくのを見るのが要点2の実演になる——
    /// 画面の色は、そのばらつきを何千回も平均したもの。
    /// </para>
    /// </summary>
    private void TracePixel(int x, int y)
    {
        if ((uint)x >= (uint)_renderer.Width || (uint)y >= (uint)_renderer.Height)
        {
            return;
        }

        Tracer tracer = Tracer.Create(_scene, _settings);
        TraceLog log = tracer.TracePixel(CreateCamera(), x, y, _traceSample++, out Vector3 color, out RayCounters rays);

        _panelTitle = $"1画素を追う ({x}, {y}) {_traceSample} 回目   光線 {rays.Total} 本"
                    + $"(カメラ {rays.Camera} / 影 {rays.Shadow} / 反射 {rays.Reflection} / 屈折 {rays.Refraction} / 散乱 {rays.Scatter})"
                    + $"   色 ({color.X:F3}, {color.Y:F3}, {color.Z:F3})";
        _panelLines = log.Lines;
        _panelScroll = 0;
        _tracedPixel = new Point(x, y);
    }

    private void RunSelfCheck()
    {
        _panelLines = SelfCheck.Run(out string summary);
        _panelTitle = $"自己チェック: {summary}";
        _panelScroll = 0;
        _tracedPixel = null;
    }

    private void SaveImage()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"day60-scene{_sceneIndex + 1}-{_renderer.Status.Samples}spp-{DateTime.Now:yyyyMMdd-HHmmss}.png");

        // 文字を重ねる前の画素を保存する(_backBuffer には文字が乗っている)。
        using var bitmap = new Bitmap(_renderer.Width, _renderer.Height, PixelFormat.Format32bppRgb);
        CopyPixelsTo(bitmap);
        bitmap.Save(path, ImageFormat.Png);

        _panelTitle = "PNG を保存した";
        _panelLines = new[] { path };
        _tracedPixel = null;
    }

    private void SelectScene(int index)
    {
        _sceneIndex = index;
        _scene = SceneLibrary.Create(index);
        _view = _scene.DefaultView;
        _panelTitle = null;
        _panelScroll = 0;
        _tracedPixel = null;
        _restartRequested = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                break;

            case Keys.D1:
            case Keys.D2:
            case Keys.D3:
            case Keys.D4:
            case Keys.D5:
            case Keys.D6:
                SelectScene(e.KeyCode - Keys.D1);
                break;

            // 深さの上限。[ ] は JIS 配列でも同じ仮想キー(OEM_4 / OEM_6)になる。上下の矢印でも変えられる。
            case Keys.OemOpenBrackets:
            case Keys.Down:
                ChangeSettings(_settings with { MaxDepth = Math.Max(0, _settings.MaxDepth - 1) });
                break;

            case Keys.OemCloseBrackets:
            case Keys.Up:
                ChangeSettings(_settings with { MaxDepth = Math.Min(MaxDepthLimit, _settings.MaxDepth + 1) });
                break;

            case Keys.S:
                ChangeSettings(_settings with { Shadows = !_settings.Shadows });
                break;

            case Keys.F:
                ChangeSettings(_settings with { Fresnel = (FresnelMode)(((int)_settings.Fresnel + 1) % 3) });
                break;

            case Keys.E:
                ChangeSettings(_settings with { OffsetOrigin = !_settings.OffsetOrigin });
                break;

            case Keys.J:
                ChangeSettings(_settings with { Jitter = !_settings.Jitter });
                break;

            case Keys.V:
                ChangeSettings(_settings with { View = (ViewMode)(((int)_settings.View + 1) % 4) });
                break;

            // ---- ここから下は Day 60 で追加 ----

            // 追い方。深さの上限とサンプル数も、その追い方に合った値に戻す(RenderSettings.WithAlgorithm)。
            case Keys.A:
                ChangeSettings(_settings.WithAlgorithm(
                    _settings.Algorithm == TraceAlgorithm.Whitted ? TraceAlgorithm.Path : TraceAlgorithm.Whitted));
                break;

            case Keys.N:
                ChangeSettings(_settings with { NextEventEstimation = !_settings.NextEventEstimation });
                break;

            case Keys.U:
                ChangeSettings(_settings with { RussianRoulette = !_settings.RussianRoulette });
                break;

            case Keys.B:
                ChangeSettings(_settings with { Acceleration = (AccelerationMode)(((int)_settings.Acceleration + 1) % 3) });
                break;

            case Keys.K:
                ChangeSettings(_settings with { DecorrelatePixels = !_settings.DecorrelatePixels });
                break;

            // 下の欄をめくる。描き直しは起こさない(見ているものを消さないため)。
            case Keys.PageUp:
                _panelScroll = Math.Max(0, _panelScroll - 10);
                break;

            case Keys.PageDown:
                _panelScroll += 10;
                break;

            case Keys.R:
                _view = _scene.DefaultView;
                _restartRequested = true;
                break;

            case Keys.C:
                RunSelfCheck();
                break;

            case Keys.P:
                SaveImage();
                break;

            case Keys.X:
                _panelTitle = null;
                _panelScroll = 0;
                _tracedPixel = null;
                break;

            case Keys.H:
                _hudVisible = !_hudVisible;
                break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragStart = e.Location;
            _dragStartView = _view;
        }
        else if (e.Button == MouseButtons.Right)
        {
            TracePixel(e.X, e.Y);
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            // 押した位置からの移動量で決める(前のイベントからの差を足していくと、取りこぼしで少しずつずれる)。
            float dx = e.X - _dragStart.X;
            float dy = e.Y - _dragStart.Y;
            _view = _dragStartView with
            {
                Yaw = _dragStartView.Yaw - dx * 0.006f,
                Pitch = Math.Clamp(_dragStartView.Pitch + dy * 0.006f, 0.02f, 1.5f),
            };
            _restartRequested = true;
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = false;
        }

        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        float scale = e.Delta > 0 ? 0.9f : 1.0f / 0.9f;
        _view = _view with { Distance = Math.Clamp(_view.Distance * scale, 1.0f, 40.0f) };
        _restartRequested = true;
        base.OnMouseWheel(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _running = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _renderer.Dispose();
            _overlay?.Dispose();
            _screen?.Dispose();
            _backBuffer.Dispose();
            _font.Dispose();
            _textBrush.Dispose();
            _panelBrush.Dispose();
            _markerPen.Dispose();
        }

        base.Dispose(disposing);
    }
}
