using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using System.Runtime.InteropServices;

namespace GaussianSplatting;

/// <summary>
/// 窓。役割は3つ。
///   1. キーとマウスでカメラを飛ばし、設定を変える
///   2. 何か変わったフレームだけ、並べ替え(CPU)→ 描画(GPU)→ 読み戻しをして、画面に出す
///   3. 数字(時間・個数・重なり)と、右クリックした画素の中身・自己チェックの結果を文字で重ねる
///
/// <para>
/// Day 59〜61 の窓と同じ「自前のループ + LockBits で転送」の形。違うのは、<b>何も変わらないフレームでは描き直さない</b>こと。
/// 止まっている絵を毎フレーム描き直しても同じ絵が出るだけで、GPU を無駄に回すことになる(電源の細い機械では、それだけで落ちうる)。
/// </para>
/// </summary>
internal sealed class ViewerWindow : Form
{
    /// <summary>
    /// 表示の更新頻度。歩き回るので Day 59〜61 の 30fps より上げる。
    /// 描き直すのは動いている間だけなので、止まっていれば GPU はほぼ休んでいる。
    /// </summary>
    private const double TargetFps = 60.0;

    /// <summary>場面の数。1〜3 が自前の場面、4 が .ply。</summary>
    private const int SceneCount = SyntheticScenes.Count + 1;

    private const int PlySceneIndex = SyntheticScenes.Count;

    private readonly GpuDevice? _device;

    private readonly SplatRenderer? _renderer;

    /// <summary>GPU の素性、または使えなかった理由。</summary>
    private readonly string _gpuMessage;

    private readonly DepthSorter _sorter = new();

    /// <summary>作った / 読んだ場面を覚えておく。25 万個の場面を作り直すと 0.3 秒かかるので、切り替えのたびには作らない。</summary>
    private readonly SplatCloud?[] _clouds = new SplatCloud?[SceneCount];

    /// <summary>場面4で読む .ply。起動の引数か、assets/splats/ の中のいちばん名前の若いもの。</summary>
    private readonly string? _plyPath;

    private readonly int[] _pixels;

    private readonly Bitmap _backBuffer;

    private readonly Font _font = new("Meiryo UI", 9.0f);

    private readonly SolidBrush _textBrush = new(Color.White);

    private readonly SolidBrush _panelBrush = new(Color.FromArgb(170, 0, 0, 0));

    private readonly Pen _markerPen = new(Color.Red, 1.0f);

    /// <summary>押されたままのキー(W/A/S/D/Q/E/Shift)。KeyDown で足し、KeyUp で消す。</summary>
    private readonly HashSet<Keys> _held = [];

    private Graphics? _screen;

    private Graphics? _overlay;

    private bool _running;

    private int _sceneIndex;

    private SplatCloud? _cloud;

    private FlyView _view;

    private RenderOptions _options = new();

    /// <summary>描き直しの依頼。キー・マウス・移動で立て、ループの中で1フレームに1回だけ描く。</summary>
    private bool _dirty = true;

    private bool _hudVisible = true;

    /// <summary>移動の速さ(m/秒)。ホイールで変える。Shift を押している間は 4 倍。</summary>
    private float _speed = 1.5f;

    // --- 直前のフレームの数字(HUD に出す)---
    private RenderStats _stats;

    private double _sortMilliseconds;

    // --- 下の欄(右クリックの結果 / 自己チェック / メッセージ)---
    private string? _panelTitle;

    private IReadOnlyList<string> _panelLines = Array.Empty<string>();

    private int _panelScroll;

    private Point? _probedPixel;

    // --- マウスで見回す ---
    private bool _dragging;

    private Point _dragStart;

    private FlyView _dragStartView;

    public ViewerWindow(int width, int height, string? plyPath)
    {
        _pixels = new int[width * height];
        _backBuffer = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        _plyPath = plyPath ?? FindDefaultPly();

        _device = GpuDevice.TryCreate(out _gpuMessage);
        if (_device is not null)
        {
            try
            {
                _renderer = new SplatRenderer(_device, width, height);
            }
            catch (Exception e)
            {
                // GLSL の組み立てに失敗した(書き換えて試しているときはここに来る)。
                _gpuMessage = e.Message.ReplaceLineEndings(" ");
            }
        }

        SelectScene(0);

        Text = "Day67 - 3D Gaussian Splatting 簡易ビューア";

        // Day 1 と同じ。自動の DPI 拡大を止めて、描いた1画素 = 画面の1画素にする。
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(width, height);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    private int ViewWidth => _backBuffer.Width;

    private int ViewHeight => _backBuffer.Height;

    public void Run()
    {
        Show();
        _screen = CreateGraphics();
        _overlay = Graphics.FromImage(_backBuffer);
        _overlay.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _running = true;

        var clock = Stopwatch.StartNew();
        double previousSeconds = 0.0;
        double nextFrameSeconds = 0.0;

        while (_running)
        {
            Application.DoEvents();
            if (!_running)
            {
                break;
            }

            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.1, now - previousSeconds);
            previousSeconds = now;
            MoveCamera(dt);

            // 次のフレームまでの間隔。ふだんは 60fps だが、GPU が1枚に時間をかけているときは
            // <b>描いた時間の2倍は休ませる</b>(GPU が働いているのは多くても半分の時間になる)。
            // 学習済みのシーンを「重なりの数」で見ると1枚 40ms 近くかかり、休ませずに歩き回ると GPU が全開に張り付く。
            // 電源の細い機械はそれで落ちる(このリポジトリの開発機が、Day 67 の検証中に実際に落ちた)。
            double interval = 1.0 / TargetFps;
            if (_dirty)
            {
                _dirty = false;
                RenderFrame();
                Present();
                interval = Math.Max(interval, 2.0 * _stats.GpuMilliseconds / 1000.0);
            }

            nextFrameSeconds = Math.Max(nextFrameSeconds + interval, clock.Elapsed.TotalSeconds);
            int sleepMilliseconds = (int)((nextFrameSeconds - clock.Elapsed.TotalSeconds) * 1000.0);
            if (sleepMilliseconds > 0)
            {
                Thread.Sleep(sleepMilliseconds);
            }
        }
    }

    private CameraFrame CurrentFrame() => _view.Frame(_cloud!.WorldUp, ViewWidth, ViewHeight);

    /// <summary>
    /// 押されたままのキーでカメラを動かす。前後は見ている向き、左右は右の向き、Q/E はワールドの上下。
    /// </summary>
    private void MoveCamera(float dt)
    {
        if (_cloud is null)
        {
            return;
        }

        float forward = (_held.Contains(Keys.W) ? 1.0f : 0.0f) - (_held.Contains(Keys.S) ? 1.0f : 0.0f);
        float right = (_held.Contains(Keys.D) ? 1.0f : 0.0f) - (_held.Contains(Keys.A) ? 1.0f : 0.0f);
        float up = (_held.Contains(Keys.E) ? 1.0f : 0.0f) - (_held.Contains(Keys.Q) ? 1.0f : 0.0f);
        if (forward == 0.0f && right == 0.0f && up == 0.0f)
        {
            return;
        }

        float step = _speed * dt * (_held.Contains(Keys.ShiftKey) ? 4.0f : 1.0f);
        _view = _view.Move(_cloud.WorldUp, forward * step, right * step, up * step);
        _dirty = true;
    }

    /// <summary>
    /// 1フレーム描く。<b>並べ替え(CPU)→ 描画(GPU)→ 読み戻し</b>。今日の1フレームの全部。
    /// </summary>
    private void RenderFrame()
    {
        if (_renderer is null || _cloud is null)
        {
            Array.Clear(_pixels);
            return;
        }

        CameraFrame camera = CurrentFrame();

        var clock = Stopwatch.StartNew();
        _sorter.Sort(_cloud, camera, _options.Sort);
        _sortMilliseconds = clock.Elapsed.TotalMilliseconds;

        _stats = _renderer.Render(camera, _sorter.Order, _options, _pixels);
    }

    private void Present()
    {
        CopyPixelsTo(_backBuffer);
        if (_hudVisible)
        {
            DrawHud(_overlay!);
        }

        _screen!.DrawImage(_backBuffer, new Rectangle(0, 0, ViewWidth, ViewHeight));
    }

    private void CopyPixelsTo(Bitmap bitmap)
    {
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, ViewWidth, ViewHeight), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < ViewHeight; y++)
            {
                Marshal.Copy(_pixels, y * ViewWidth, data.Scan0 + y * data.Stride, ViewWidth);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void DrawHud(Graphics g)
    {
        var lines = new List<string>();
        if (_renderer is null)
        {
            lines.Add($"GPU が使えない: {_gpuMessage}");
        }
        else if (_cloud is not null)
        {
            string degree = _cloud.ShDegree == 0
                ? "0(この場面は 0 だけ)"
                : $"{Math.Min(_options.ShDegree, _cloud.ShDegree)} / {_cloud.ShDegree}";
            lines.Add($"場面 {_sceneIndex + 1}/{SceneCount}: {_cloud.Name}   楕円体 {_cloud.Count:N0} 個(カメラの前 {_sorter.InFront:N0})"
                + $"   {(_sceneIndex == PlySceneIndex ? "読み込み" : "作成")} {_cloud.LoadMilliseconds:F0} ms");
            lines.Add($"並べ替え {SortName(_options.Sort)} (O) {_sortMilliseconds:F1} ms   描画 {_stats.GpuMilliseconds:F2} ms(GPU)"
                + $"   読み戻し {_stats.ReadbackMilliseconds:F1} ms   {_renderer.Width}x{_renderer.Height}");
            lines.Add($"表示 {ModeName(_options.Mode)} (V)   球面調和の次数 {degree} (M)   0.3 の広げ {OnOff(_options.Dilate)} (F)"
                + $"   大きさ x{_options.ScaleMultiplier:F2} ([ ])");
            if (_options.Mode == DisplayMode.Overdraw)
            {
                lines.Add($"画素シェーダが走った回数: 1画素あたり平均 {_stats.AverageOverdraw:F1} 回 / 最大 {_stats.MaxOverdraw:N0} 回"
                    + "   (1 紺 / 4 青 / 16 水色 / 64 緑 / 256 黄 / 1024 赤 / 4096 以上 白)");
            }

            Vector3 p = _view.Position;
            lines.Add($"位置 ({p.X:F2}, {p.Y:F2}, {p.Z:F2})   向き {_view.Yaw * 180.0f / MathF.PI:F0} 度 / {_view.Pitch * 180.0f / MathF.PI:F0} 度"
                + $"   速さ {_speed:F2} m/秒(ホイール、Shift で 4 倍)   上 {UpName(_cloud.WorldUp)}{(_sceneIndex == PlySceneIndex ? " (U)" : "")}");
        }

        if (_panelTitle is null)
        {
            lines.Add("WASD: 移動   Q/E: 下/上   左ドラッグ: 見回す   右クリック: その画素の楕円体   R: カメラを戻す   1〜4: 場面");
            lines.Add("C: 自己チェック   P: PNG 保存   H: 文字を消す   Esc: 終了");
        }

        float lineHeight = _font.GetHeight(g);
        float y = DrawBlock(g, lines, 0.0f, lineHeight);

        if (_panelTitle is not null)
        {
            int capacity = Math.Max(2, (int)((ViewHeight - y - 10.0f) / lineHeight) - 1);
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

        if (_probedPixel is Point pixel)
        {
            g.DrawLine(_markerPen, pixel.X - 6, pixel.Y, pixel.X - 2, pixel.Y);
            g.DrawLine(_markerPen, pixel.X + 2, pixel.Y, pixel.X + 6, pixel.Y);
            g.DrawLine(_markerPen, pixel.X, pixel.Y - 6, pixel.X, pixel.Y - 2);
            g.DrawLine(_markerPen, pixel.X, pixel.Y + 2, pixel.X, pixel.Y + 6);
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

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string SortName(SortMode mode) => mode switch
    {
        SortMode.Frozen => "止めた",
        SortMode.FileOrder => "しない(ファイルの順)",
        _ => "毎フレーム",
    };

    private static string ModeName(DisplayMode mode) => mode switch
    {
        DisplayMode.Centers => "中心の点",
        DisplayMode.Solid => "くっきりした楕円(2σ、ぼかし無し)",
        DisplayMode.Outline => "楕円の輪郭(2σ)",
        DisplayMode.Overdraw => "重なりの数",
        _ => "ふつう",
    };

    private static string UpName(Vector3 up) => up.Y >= 0.0f ? "+Y" : "-Y";

    /// <summary>
    /// 場面を替える。1〜3 は作り、4 は .ply を読む。作った / 読んだものは覚えておく。
    /// </summary>
    private void SelectScene(int index)
    {
        SplatCloud? cloud = _clouds[index];
        if (cloud is null)
        {
            if (index == PlySceneIndex)
            {
                if (_plyPath is null)
                {
                    ShowMessage("場面4: .ply が見つからない", [
                        "assets/splats/ に学習済みの 3DGS の .ply を置くか、起動の引数でパスを渡す。",
                        "入手のしかたは docs/plans/Day67.md の「学習済みのシーンを手に入れる」。",
                    ]);
                    return;
                }

                try
                {
                    cloud = PlyFormat.Read(_plyPath);
                }
                catch (Exception e)
                {
                    ShowMessage("場面4: 読めなかった", [_plyPath, e.Message]);
                    return;
                }
            }
            else
            {
                cloud = SyntheticScenes.Create(index);
            }

            _clouds[index] = cloud;
        }

        _sceneIndex = index;
        _cloud = cloud;
        _view = cloud.DefaultView;
        _renderer?.Upload(cloud);
        _panelTitle = null;
        _probedPixel = null;
        _dirty = true;
    }

    /// <summary>
    /// 実行ファイルの場所から上へたどって assets/splats/ を探し、いちばん名前の若い .ply を返す(無ければ null)。
    /// Day 14 以降のエンジンが assets/ を探すのと同じ考え方。
    /// </summary>
    private static string? FindDefaultPly()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "assets", "splats");
            if (Directory.Exists(candidate))
            {
                return Directory.GetFiles(candidate, "*.ply").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }
        }

        return null;
    }

    private void ShowMessage(string title, IReadOnlyList<string> lines)
    {
        _panelTitle = title;
        _panelLines = lines;
        _panelScroll = 0;
        _probedPixel = null;
        _dirty = true;
    }

    /// <summary>
    /// 右クリックした画素に効いている楕円体を、CPU で全部調べて手前から並べる(<see cref="PixelProbe"/>)。
    /// </summary>
    private void ProbePixel(int x, int y)
    {
        if (_cloud is null || (uint)x >= (uint)ViewWidth || (uint)y >= (uint)ViewHeight)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        ProbeResult probe = PixelProbe.Probe(_cloud, CurrentFrame(), x, y, _options.ShDegree, _options.Dilate, _options.ScaleMultiplier);
        double milliseconds = clock.Elapsed.TotalMilliseconds;

        string stop = probe.EarlyStop is int n
            ? $"手前から {n} 個目で残りの透明度が 0.0001 を切る(その奥の {probe.Contributions.Count - n} 個は見えない)"
            : $"最後まで重ねても残りの透明度は {RemainingTransmittance(probe):F4}(空が透ける)";

        var lines = new List<string>
        {
            $"四角形が掛かっていた {probe.Covered} 個(= GPU の画素シェーダがこの画素で走った回数)   色を足した {probe.Contributions.Count} 個   {stop}",
            $"CPU で重ね直した色 ({probe.Color.X:F3}, {probe.Color.Y:F3}, {probe.Color.Z:F3})   調べるのに {milliseconds:F0} ms",
            "   手前から   奥行き     濃さ α   届いた割合 T    色                     寄与(色 × α × T)",
        };

        for (int i = 0; i < probe.Contributions.Count; i++)
        {
            Contribution c = probe.Contributions[i];
            Vector3 w = c.Weighted;
            lines.Add($"   {i + 1,5}   {c.Depth,7:F3} m   {c.Alpha,6:F3}   {c.Transmittance,9:F4}"
                + $"    ({c.Color.X:F2}, {c.Color.Y:F2}, {c.Color.Z:F2})   ({w.X:F4}, {w.Y:F4}, {w.Z:F4})   #{c.Index}");
        }

        _panelTitle = $"画素 ({x}, {y}) に効いている楕円体";
        _panelLines = lines;
        _panelScroll = 0;
        _probedPixel = new Point(x, y);
        _dirty = true;
    }

    private static float RemainingTransmittance(ProbeResult probe)
    {
        float t = 1.0f;
        foreach (Contribution c in probe.Contributions)
        {
            t *= 1.0f - c.Alpha;
        }

        return t;
    }

    private void RunSelfCheck()
    {
        IReadOnlyList<string> lines = SelfCheck.Run(_renderer, out string summary);

        // 自己チェックは GPU に別の場面を送って描くので、いまの場面を送り直す。
        if (_cloud is not null)
        {
            _renderer?.Upload(_cloud);
        }

        ShowMessage($"自己チェック: {summary}", lines);
    }

    private void SaveImage()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"day67-scene{_sceneIndex + 1}-{_options.Mode.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.png");

        // 文字を重ねる前の画素を保存する。
        using var bitmap = new Bitmap(ViewWidth, ViewHeight, PixelFormat.Format32bppRgb);
        CopyPixelsTo(bitmap);
        bitmap.Save(path, ImageFormat.Png);
        ShowMessage("PNG を保存した", [path]);
    }

    private void ChangeOptions(RenderOptions options)
    {
        _options = options;
        _dirty = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.W:
            case Keys.A:
            case Keys.S:
            case Keys.D:
            case Keys.Q:
            case Keys.E:
            case Keys.ShiftKey:
                _held.Add(e.KeyCode);
                break;
        }

        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                break;

            case Keys.D1:
            case Keys.D2:
            case Keys.D3:
            case Keys.D4:
                SelectScene(e.KeyCode - Keys.D1);
                break;

            case Keys.V:
                ChangeOptions(_options with { Mode = (DisplayMode)(((int)_options.Mode + 1) % 5) });
                break;

            case Keys.O:
                ChangeOptions(_options with { Sort = (SortMode)(((int)_options.Sort + 1) % 3) });
                break;

            case Keys.M:
                ChangeOptions(_options with { ShDegree = (_options.ShDegree + 1) % (SphericalHarmonics.MaxDegree + 1) });
                break;

            case Keys.F:
                // D は移動(右)に使うので、0.3 の広げは F(Filter)で切り替える。
                ChangeOptions(_options with { Dilate = !_options.Dilate });
                break;

            // 大きさの倍率。[ ] は JIS 配列でも同じ仮想キー(OEM_4 / OEM_6)。
            case Keys.OemOpenBrackets:
                ChangeOptions(_options with { ScaleMultiplier = MathF.Max(0.05f, _options.ScaleMultiplier * 0.8f) });
                break;

            case Keys.OemCloseBrackets:
                ChangeOptions(_options with { ScaleMultiplier = MathF.Min(4.0f, _options.ScaleMultiplier / 0.8f) });
                break;

            case Keys.U when _cloud is not null && _sceneIndex == PlySceneIndex:
                // 学習済みのシーンで上下が逆さまに見えたら、上を裏返す。向きは水平に戻す。
                _cloud.WorldUp = -_cloud.WorldUp;
                _view = _view with { Yaw = 0.0f, Pitch = 0.0f };
                _dirty = true;
                break;

            case Keys.R when _cloud is not null:
                _view = _cloud.DefaultView;
                _dirty = true;
                break;

            case Keys.PageUp:
                _panelScroll = Math.Max(0, _panelScroll - 10);
                _dirty = true;
                break;

            case Keys.PageDown:
                _panelScroll += 10;
                _dirty = true;
                break;

            case Keys.C:
                RunSelfCheck();
                break;

            case Keys.P:
                SaveImage();
                break;

            case Keys.X:
                _panelTitle = null;
                _probedPixel = null;
                _dirty = true;
                break;

            case Keys.H:
                _hudVisible = !_hudVisible;
                _dirty = true;
                break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _held.Remove(e.KeyCode);
        base.OnKeyUp(e);
    }

    /// <summary>窓から焦点が外れると KeyUp が届かないので、押されたままのキーを全部離したことにする。</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        _held.Clear();
        base.OnDeactivate(e);
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
            ProbePixel(e.X, e.Y);
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
                Yaw = _dragStartView.Yaw + dx * 0.005f,
                Pitch = Math.Clamp(_dragStartView.Pitch - dy * 0.005f, -FlyView.MaxPitch, FlyView.MaxPitch),
            };
            _dirty = true;
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
        _speed = Math.Clamp(_speed * (e.Delta > 0 ? 1.25f : 0.8f), 0.05f, 50.0f);
        _dirty = true;
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
            _renderer?.Dispose();
            _device?.Dispose();
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
