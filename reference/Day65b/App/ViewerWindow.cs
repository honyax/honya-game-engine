using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace MeshletRenderer;

/// <summary>
/// 窓。Day 62c の同名のクラスから持ってきた。「自前のループ + LockBits で転送」の形はそのまま。
///
/// <para>
/// <b>Vulkan は今日も画面に一切関わっていない</b>。ラスタライズした絵も、GPU の画像から
/// <c>int[]</c> へ引き取って Day 1 と同じやり方で貼るだけ(スワップチェーンは作らない)。
/// </para>
/// <para>
/// 変わったのはキーと HUD。<c>B</c> が「探し方」から「描き方」(メッシュ / 頂点)に、
/// <c>O</c>(メッシュレットの切り方)が増え、HUD に<b>各段が何回走ったか</b>の行が増えた。
/// </para>
/// <para>
/// <b>Day 64b の差分</b>: <c>B</c> が3つ(タスク + メッシュ / メッシュ / 頂点)になり、
/// <c>C</c>(どのカリングをするか)と <c>F</c>(カリングの目を止める)が増えた。
/// HUD には「判定」の行(メッシュレットの行き先の内訳)が増えた。
/// </para>
/// <para>
/// <b>Day 65a の差分</b>: <c>G</c>(描く体をどう選ぶか。GPU → CPU → 選ばない)が増え、
/// HUD に「体」の行(何体のうち何体描いたか、選ぶのにかかった時間)が増えた。
/// </para>
/// <para>
/// <b>Day 65b の差分</b>: <c>Z</c>(Hi-Z の段を見る)が増え、HUD に「Hi-Z」の行が増えた。
/// 見ている間は、絵の代わりに Hi-Z の1段を<b>画面いっぱいに引き伸ばして</b>描く。
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

    /// <summary>
    /// 形は1つだけ。起動時に1回作って使い回す(体の数や切り方を変えても形は変わらない)。
    /// </summary>
    private readonly MeshData _mesh = TorusKnot.Create();

    private readonly int[] _pixels;
    private readonly Bitmap _backBuffer;

    private readonly Font _font = new("Meiryo UI", 9.0f);
    private readonly SolidBrush _textBrush = new(Color.White);
    private readonly SolidBrush _panelBrush = new(Color.FromArgb(170, 0, 0, 0));

    private Graphics? _screen;
    private Graphics? _overlay;
    private bool _running;

    private MeshRenderer? _renderer;

    /// <summary>シェーダの翻訳やパイプラインの作成に失敗したときの説明。HUD に出す。</summary>
    private string? _error;

    private int _presetIndex = 1;
    private int _mode;

    /// <summary>描き方。起動時はタスク + メッシュシェーダ——今日の主役なので最初から見せる。</summary>
    private DrawPath _path = DrawPath.TaskMesh;

    /// <summary>どのカリングをするか(Day 64b で追加)。起動時は両方。</summary>
    private CullingMode _culling = CullingMode.Both;

    /// <summary>描く体をどう選ぶか(Day 65a で追加)。起動時は GPU——今日の主役なので最初から見せる。</summary>
    private InstanceSelection _selection = InstanceSelection.Gpu;

    /// <summary>画面に出す Hi-Z の段(Day 65b で追加)。−1 なら出さない(ふつうの絵)。<c>Z</c> で 0 → 1 → ... → 8 → −1。</summary>
    private int _pyramidLevel = -1;

    /// <summary>
    /// カリングの目を止めているか(Day 64b で追加)。止めると <see cref="_frozenView"/> の目で捨てるものを決め、
    /// 描く目(<see cref="_view"/>)だけが動く。<b>捨てられた穴を横から眺める</b>ための仕掛け。
    /// </summary>
    private bool _cullFrozen;

    private OrbitView _frozenView;

    /// <summary>切り方。Day 64b から既定は「丸く育てる」(背面カリングが効く切り方)。</summary>
    private MeshletStrategy _strategy = MeshletStrategy.Round;
    private OrbitView _view;
    private bool _hudVisible = true;

    /// <summary>直近のフレームの所要時間の平均(ms)。1フレームだけ見ると揺れが大きい。</summary>
    private double _averageMilliseconds;

    /// <summary>同じく、描く命令だけの GPU の時間の平均(ms)。</summary>
    private double _averageDrawMilliseconds;

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
        _view = SceneData.DefaultView(SceneData.Presets[_presetIndex]);

        // **失敗しても落とさない**。Vulkan が使えない環境でも「なぜ駄目か」を画面に出したい。
        _device = VulkanDevice.TryCreate(out _deviceMessage);
        if (_device is not null)
        {
            _compiler = new ShaderCompiler();
            RebuildRenderer();
        }

        Text = "Day65b - GPU 駆動: 描き終わった深度を畳んで Hi-Z を作る";

        // Day 1 と同じ。自動の DPI 拡大を止めて、描いた1画素 = 画面の1画素にする。
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
                // 描く目と、カリングの目。止めていなければ同じもの(Day 64b)。
                var camera = new Camera(_view, _width, _height);
                Camera cullCamera = _cullFrozen ? new Camera(_frozenView, _width, _height) : camera;
                _renderer.ShownPyramidLevel = _pyramidLevel;
                _renderer.Render(camera, cullCamera, _mode, _path, _culling, _selection, _pixels);

                // Day 65b: Hi-Z を見ているなら、絵を Hi-Z の段で塗り替える。
                if (_renderer.LastPyramidLevel is float[] level)
                {
                    DrawPyramidLevel(level);
                }

                // 指数移動平均。1フレームの値は揺れるので、読める形にならす。
                _averageMilliseconds = Smooth(_averageMilliseconds, _renderer.LastFrameMilliseconds);
                _averageDrawMilliseconds = Smooth(_averageDrawMilliseconds, _renderer.LastDrawMilliseconds);
            }

            Present();

            nextFrameSeconds = Math.Max(nextFrameSeconds + 1.0 / TargetFps, clock.Elapsed.TotalSeconds);
            int sleepMilliseconds = (int)((nextFrameSeconds - clock.Elapsed.TotalSeconds) * 1000.0);
            if (sleepMilliseconds > 0)
            {
                // **GPU を 100% に張り付かせない**ための休み(Day 61・62 と同じ配慮)。
                Thread.Sleep(sleepMilliseconds);
            }
        }
    }

    private static double Smooth(double average, double sample)
        => average == 0.0 ? sample : average * 0.9 + sample * 0.1;

    /// <summary>
    /// Hi-Z の1段を、画面いっぱいに引き伸ばして描く(Day 65b で追加)。
    ///
    /// <para>
    /// 段 L の1画素は画面の 2^(L+1) 画素四方を受け持つ(いちばん右と下の画素は、切り捨てで余った分も受け持つ)。
    /// 画面の画素 (x, y) を受け持つ Hi-Z の画素は (x / 2^(L+1), y / 2^(L+1))——これは Day 65c のカリングが
    /// 「箱がかかる Hi-Z の画素」を探すときと同じ対応。
    /// </para>
    /// <para>
    /// 深度の値(0〜1)は手前に寄って詰まっている(5m 先でもう 0.98)ので、そのまま灰色にすると真っ白に近い絵になる。
    /// そこで<b>目からの距離(m)に戻して</b>から明るさにする。近いほど明るく、60m より先と「何も無い」(深度 1)は黒。
    /// </para>
    /// </summary>
    private void DrawPyramidLevel(float[] level)
    {
        int width = _renderer!.PyramidLevelWidth(_pyramidLevel);
        int height = _renderer.PyramidLevelHeight(_pyramidLevel);
        int shift = _pyramidLevel + 1;
        for (int y = 0; y < _height; y++)
        {
            int ty = Math.Min(y >> shift, height - 1);
            for (int x = 0; x < _width; x++)
            {
                int tx = Math.Min(x >> shift, width - 1);
                float depth = level[ty * width + tx];

                // Camera の透視投影(深度 0〜1)を逆にたどる: 距離 = 手前 x 奥 / (奥 − 深度 x (奥 − 手前))。
                float distance = Camera.Near * Camera.Far / (Camera.Far - depth * (Camera.Far - Camera.Near));
                int gray = (int)(Math.Clamp(1.0f - distance / 60.0f, 0.0f, 1.0f) * 255.0f);
                _pixels[y * _width + x] = (gray << 16) | (gray << 8) | gray;
            }
        }
    }

    /// <summary>
    /// 体の数か切り方を変えたので、レンダラを作り直す。
    ///
    /// <para>
    /// 作り直しの中身は「メッシュレットを切る(CPU、数十 ms)」「バッファを作って転送する」
    /// 「シェーダを翻訳してパイプラインを作る」の3つ。形(<see cref="_mesh"/>)は作り直さない。
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
        _averageDrawMilliseconds = 0.0;

        try
        {
            MeshletSet meshlets = MeshletBuilder.Build(_mesh, _strategy);
            _renderer = new MeshRenderer(
                _device, _width, _height,
                _mesh, meshlets, SceneData.CreateInstances(SceneData.Presets[_presetIndex]),
                _compiler, Path.Combine(AppContext.BaseDirectory, "shaders"));
        }
        catch (Exception e)
        {
            // GLSL を書き換えて試しているときはここに来る。落とさずに理由を画面に出す。
            _error = e.Message;
        }
    }

    /// <summary>
    /// いま実際に使われる描き方。判断は <see cref="MeshRenderer.Effective"/> に任せる(Day 64b で3つになったので)。
    /// レンダラが無いときは頂点の道と言っておく(HUD に出すだけ)。
    /// </summary>
    private DrawPath Effective() => _renderer?.Effective(_path) ?? DrawPath.VertexShader;

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
        string[] modeNames = ["陰影", "メッシュレットごとの色", "三角形ごとの色"];
        int count = SceneData.Presets[_presetIndex];

        string pathName = Effective() switch
        {
            DrawPath.TaskMesh => "タスクシェーダ + メッシュシェーダ",
            DrawPath.MeshShader => "メッシュシェーダ",
            _ => "頂点シェーダ",
        };
        string cullName = _culling switch
        {
            CullingMode.Both => "視錐台 + 背面",
            CullingMode.Frustum => "視錐台だけ",
            CullingMode.Backface => "背面だけ",
            _ => "しない",
        };

        var lines = new List<string>
        {
            $"GPU   : {_deviceMessage}",
            $"場面  : トーラスノット {count} 体 / 三角形 {(double)_mesh.TriangleCount * count / 10000.0:F0} 万枚"
                + $" (1/2/3/4 で {string.Join(" / ", SceneData.Presets)} 体)",
            $"描き方: {pathName} (B)",
            $"表示  : {modeNames[_mode]} (M)",
            $"カリング: {cullName} (C) / 目: {(_cullFrozen ? "止めてある (F で戻す)" : "描く目と同じ (F で止める)")}",
        };

        if (_renderer is not null)
        {
            MeshletSet m = _renderer.Meshlets;
            string strategyName = m.Strategy switch
            {
                MeshletStrategy.Round => "丸く育てる",
                MeshletStrategy.Grow => "育てる(頂点の数だけ)",
                _ => "並び順のまま",
            };
            lines.Add($"分割  : {strategyName} (O)"
                + $"  1体 {m.Meshlets.Length} 個 / 三角形 {m.TrianglesPerMeshlet:F1} 枚・頂点 {m.VerticesPerMeshlet:F1} 個 (個あたり)"
                + $" / 頂点の重複 x{m.VertexDuplication:F2} / {m.BuildMilliseconds:F0} ms");

            // Day 64b: 背面の判定に使えるメッシュレットの割合。並び順のまま切ると 0% になる(要点4)。
            lines.Add($"円錐  : 裏の判定に使える {m.ConeUsableFraction * 100.0:F0}% (半角の平均 {m.AverageConeAngleDegrees:F0} 度)");

            // Day 65a: 体の行き先。GPU で選んだときの数は、描き終わってから引数のバッファを読んで知ったもの。
            string selectionName = _selection switch
            {
                InstanceSelection.Gpu => "GPU で選ぶ (indirect)",
                InstanceSelection.Cpu => "CPU で選ぶ",
                _ => "選ばない",
            };
            string selectionCost = _selection switch
            {
                InstanceSelection.Gpu => $" / 選ぶのに GPU {_renderer.LastSelectMilliseconds * 1000.0:F0} us",
                InstanceSelection.Cpu => $" / 選ぶのに CPU {_renderer.LastCpuSelectMilliseconds * 1000.0:F0} us",
                _ => string.Empty,
            };
            lines.Add($"体    : {selectionName} (G)  {_renderer.InstanceCount} 体 → 描く {_renderer.LastDrawnInstances} 体{selectionCost}");

            // Day 64b: メッシュレットの行き先。タスクシェーダが数えた値(要点5)。
            CullingStatistics c = _renderer.LastCulling;
            if (Effective() == DrawPath.TaskMesh)
            {
                double percent = 100.0 / Math.Max(c.Tested, 1);
                lines.Add($"判定  : {c.Tested:N0} 個 → 画面の外 {c.FrustumCulled * percent:F1}%"
                    + $" / 裏向き {c.BackfaceCulled * percent:F1}% / 描く {c.Visible * percent:F1}%");
            }
            else
            {
                lines.Add($"判定  : {c.Tested:N0} 個 → 全部描く(捨てるのはタスクシェーダの道だけ)");
            }

            double ms = _averageMilliseconds;
            lines.Add($"1枚   : {ms:F2} ms ({(ms > 0.0 ? 1000.0 / ms : 0.0):F0} 枚/秒)"
                + $"  うち描画 {_averageDrawMilliseconds:F2} ms (GPU)");

            // Day 65b: Hi-Z。作るのは毎フレーム(見ていなくても)。見ている段の大きさと、1画素が受け持つ画面の範囲。
            string pyramidShown = _pyramidLevel < 0
                ? "見ていない (Z で段 0 から順に見る)"
                : $"段 {_pyramidLevel} を表示中 ({_renderer.PyramidLevelWidth(_pyramidLevel)} x {_renderer.PyramidLevelHeight(_pyramidLevel)}"
                    + $"、1画素 = 画面の {1 << (_pyramidLevel + 1)} 画素四方) (Z)";
            lines.Add($"Hi-Z  : {_renderer.PyramidLevels} 段 / 作るのに GPU {_renderer.LastPyramidMilliseconds * 1000.0:F0} us / {pyramidShown}");

            // 今日の目。**どの段が何回走ったか**を GPU に数えさせた値(要点6)。
            DrawStatistics s = _renderer.LastStatistics;
            // メッシュシェーダの回数だけは数えずに計算で出す(MeshRenderer の _meshStatistics の説明)。
            // タスクの道では、メッシュシェーダの組の数 = タスクシェーダが生き残らせたメッシュレットの数(Day 64b)。
            string shader = Effective() switch
            {
                // Day 65a: タスクシェーダの組の数も、描く体の数に比例するようになった(1体 = ceil(メッシュレットの数 / 32) 組)。
                DrawPath.TaskMesh => $"タスクシェーダ {_renderer.TaskGroupsFor(_renderer.LastDrawnInstances):N0} 組 x 32 人 → メッシュシェーダ {c.Visible:N0} 組",
                DrawPath.MeshShader => $"メッシュシェーダ {_renderer.MeshWorkGroups:N0} 組 x 32 人",
                _ => $"頂点シェーダ {s.VertexShaderInvocations:N0} 回",
            };
            lines.Add($"内訳  : {shader} / ラスタライザへ 三角形 {s.Triangles:N0} 枚 / 画素シェーダ {s.FragmentShaderInvocations:N0} 回");
        }

        if (_device is not null && !_device.MeshShaderSupported)
        {
            lines.Add("この GPU はメッシュシェーダに対応していません(頂点シェーダのみ)");
        }

        if (_error is not null)
        {
            // 翻訳エラーは何行にもなるので、先頭3行だけ出す。
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
            case Keys.D4:
                _presetIndex = e.KeyCode - Keys.D1;
                _view = SceneData.DefaultView(SceneData.Presets[_presetIndex]);
                RebuildRenderer();
                break;

            // 描き方を切り替える。**作り直しは起きない**——3本のパイプラインは起動時に全部できていて、
            // 束ねるハンドルが変わるだけ(Day 62b と同じ)。Day 64b で3つになった。
            case Keys.B:
                _path = (DrawPath)(((int)_path + 1) % 3);
                _averageMilliseconds = 0.0;
                _averageDrawMilliseconds = 0.0;
                break;

            // どのカリングをするか(Day 64b)。両方 → しない → 視錐台だけ → 背面だけ → 両方。
            case Keys.C:
                _culling = _culling switch
                {
                    CullingMode.Both => CullingMode.None,
                    CullingMode.None => CullingMode.Frustum,
                    CullingMode.Frustum => CullingMode.Backface,
                    _ => CullingMode.Both,
                };
                _averageDrawMilliseconds = 0.0;
                break;

            // 描く体をどう選ぶか(Day 65a)。GPU → CPU → 選ばない → GPU。
            // 3つとも起動時にできているので、作り直しは起きない。
            case Keys.G:
                _selection = _selection switch
                {
                    InstanceSelection.Gpu => InstanceSelection.Cpu,
                    InstanceSelection.Cpu => InstanceSelection.None,
                    _ => InstanceSelection.Gpu,
                };
                _averageDrawMilliseconds = 0.0;
                break;

            // Hi-Z の段を見る(Day 65b)。見ない → 段 0 → 段 1 → ... → いちばん上の段 → 見ない。
            case Keys.Z:
                _pyramidLevel = _renderer is null || _pyramidLevel + 1 >= _renderer.PyramidLevels ? -1 : _pyramidLevel + 1;
                break;

            // カリングの目を止める / 戻す(Day 64b)。止めた瞬間の目を覚えておき、以降はそれで捨てるものを決める。
            case Keys.F:
                _cullFrozen = !_cullFrozen;
                _frozenView = _view;
                break;

            // 切り方を変える。こちらはメッシュレットを切り直すので作り直す。
            // Day 64b で3つになった: 丸く育てる → 育てる(頂点の数だけ)→ 並び順のまま。
            case Keys.O:
                _strategy = _strategy switch
                {
                    MeshletStrategy.Round => MeshletStrategy.Grow,
                    MeshletStrategy.Grow => MeshletStrategy.Scan,
                    _ => MeshletStrategy.Round,
                };
                RebuildRenderer();
                break;

            case Keys.M:
                _mode = (_mode + 1) % 3;
                break;

            case Keys.R:
                _view = SceneData.DefaultView(SceneData.Presets[_presetIndex]);
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
        _view = _view with { Distance = Math.Clamp(_view.Distance * scale, 2.0f, 80.0f) };
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
