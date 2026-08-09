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

        Text = "Day07 - Zバッファ";

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
                string test = _rasterizer.DepthTestEnabled ? "ON " : "OFF";
                Text = $"Day07 - Zバッファ  {fpsFrames / fpsElapsed:F1} fps | {TriangleCount} tri | "
                     + $"render {renderSecondsAccum / fpsFrames * 1000.0:F2} ms | 深度テスト:{test} | "
                     + $"W:ワイヤー Z:深度テスト D:深度表示 Esc:終了";
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

    /// <summary>深度バッファを白黒で表示するか(Dキー)。</summary>
    private bool _showDepth;

    /// <summary>立方体1個あたりの三角形数(6面 x 2枚)。</summary>
    private const int CubeTriangles = 12;

    /// <summary>周囲を回る立方体の数。</summary>
    private const int OrbitCubes = 3;

    /// <summary>貫通する板の枚数(三角形は1枚につき2)。</summary>
    private const int BladeCount = 2;

    /// <summary>1フレームに描く三角形の総数。</summary>
    private const int TriangleCount = CubeTriangles * (1 + OrbitCubes) + BladeCount * 2;

    /// <summary>カメラ。</summary>
    private readonly Camera _camera = new();

    /// <summary>
    /// 1フレーム分の絵をフレームバッファに描く。
    ///
    /// Day 6 との違いは2つ。
    ///   - 三角形を並べ替える処理が丸ごと消えた(Zバッファが順序を気にしなくする)
    ///   - 毎フレーム深度バッファをクリアする処理が増えた
    /// 差し引きでコードは短くなっている。**正しくなったのに簡単になった**のがZバッファの凄み。
    /// </summary>
    private void Render(double timeSeconds)
    {
        _framebuffer.Clear(Framebuffer.Rgb(12, 14, 22));

        // 色と同じく、深度も毎フレーム初期化する。
        // これを忘れると前のフレームの深度が残り、物体が虫食いに抜ける。
        _rasterizer.Depth.Clear();

        float t = (float)timeSeconds;

        float orbit = t * 0.25f;
        _camera.Position = new Vec3(MathF.Sin(orbit) * 6.0f, 2.2f, MathF.Cos(orbit) * 6.0f);
        _camera.Target = Vec3.Zero;
        _camera.AspectRatio = _framebuffer.Width / (float)_framebuffer.Height;

        Mat4 viewProjection = _camera.ViewProjection;

        // --- 中央の立方体 ---
        Mat4 centerModel = Mat4.Scale(1.15f) * Mat4.RotationY(t * 0.6f) * Mat4.RotationX(t * 0.31f);
        DrawCube(centerModel * viewProjection, 1.0f);

        // --- 立方体を貫通する板 ---
        // **画家のアルゴリズムでは絶対に解けない配置**。板と立方体は互いに相手を貫いていて、
        // 「どちらが手前か」を三角形単位では決められない。
        // Zバッファはピクセル単位で判定するので、何も特別なことをせずに正しく描ける。
        for (int i = 0; i < BladeCount; i++)
        {
            // RotationY で90度回すと板は互いに直交する。ここを RotationZ にすると
            // 板が自分の平面の中で回るだけで2枚が同一平面に重なり、
            // 深度がほぼ同じピクセルが大量にできてZファイティング(ちらつく斑点)が出る。
            Mat4 blade =
                Mat4.Scale(2.3f) *
                Mat4.RotationY(MathF.PI / 2.0f * i) *
                Mat4.RotationY(t * 0.45f);

            DrawQuad(blade * viewProjection, i == 0
                ? new Vec3(0.95f, 0.85f, 0.35f)
                : new Vec3(0.35f, 0.85f, 0.95f));
        }

        // --- 周囲を回る立方体 ---
        for (int i = 0; i < OrbitCubes; i++)
        {
            float phase = i * (MathF.PI * 2.0f / OrbitCubes);
            Mat4 model =
                Mat4.Scale(0.42f) *
                Mat4.RotationZ(t * 1.7f) *
                Mat4.Translation(new Vec3(2.9f, 0.0f, 0.0f)) *
                Mat4.RotationY(t * 0.8f + phase);

            DrawCube(model * viewProjection, 0.55f + 0.15f * i);
        }

        if (_showDepth)
        {
            VisualizeDepth();
        }
    }

    /// <summary>
    /// 立方体を1個描く。Day 6 と違い、積んで並べ替えずにその場で描いてよい。
    /// </summary>
    private void DrawCube(Mat4 mvp, float brightness)
    {
        Span<Vec3> corners = stackalloc Vec3[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = new Vec3(
                (i & 1) == 0 ? -1.0f : 1.0f,
                (i & 2) == 0 ? -1.0f : 1.0f,
                (i & 4) == 0 ? -1.0f : 1.0f);
        }

        ReadOnlySpan<int> faces = stackalloc int[]
        {
            0, 2, 6, 4,   // -X
            1, 5, 7, 3,   // +X
            0, 4, 5, 1,   // -Y
            2, 3, 7, 6,   // +Y
            0, 1, 3, 2,   // -Z
            4, 6, 7, 5,   // +Z
        };

        for (int face = 0; face < 6; face++)
        {
            Vec3 color = ColorFromHue(face / 6.0f) * brightness;
            int i0 = faces[face * 4];
            int i1 = faces[face * 4 + 1];
            int i2 = faces[face * 4 + 2];
            int i3 = faces[face * 4 + 3];

            DrawTriangle(corners[i0], corners[i1], corners[i2], color, mvp);
            DrawTriangle(corners[i0], corners[i2], corners[i3], color, mvp);
        }
    }

    /// <summary>XY 平面上の板(一辺2の正方形)を1枚描く。</summary>
    private void DrawQuad(Mat4 mvp, Vec3 color)
    {
        var lt = new Vec3(-1.0f, -1.0f, 0.0f);
        var rt = new Vec3(1.0f, -1.0f, 0.0f);
        var rb = new Vec3(1.0f, 1.0f, 0.0f);
        var lb = new Vec3(-1.0f, 1.0f, 0.0f);

        DrawTriangle(lt, rt, rb, color, mvp);
        DrawTriangle(lt, rb, lb, color * 0.8f, mvp);
    }

    /// <summary>三角形1枚を描く。ワイヤーフレーム表示にも対応する。</summary>
    private void DrawTriangle(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 color, Mat4 mvp)
    {
        _rasterizer.DrawTriangle(new Vertex(p0, color), new Vertex(p1, color), new Vertex(p2, color), mvp);

        if (_showWireframe &&
            _rasterizer.TryProjectToScreen(p0, mvp, out Vec3 s0) &&
            _rasterizer.TryProjectToScreen(p1, mvp, out Vec3 s1) &&
            _rasterizer.TryProjectToScreen(p2, mvp, out Vec3 s2))
        {
            _rasterizer.DrawTriangleWireframe(s0, s1, s2, Framebuffer.Rgb(240, 240, 240));
        }
    }

    /// <summary>深度バッファを可視化する表示範囲(カメラからの距離)。</summary>
    private const float DepthViewNear = 2.5f;

    private const float DepthViewFar = 9.5f;

    /// <summary>
    /// 深度バッファの中身を白黒で塗り直す。手前が白、奥が黒。
    ///
    /// そのまま NDC の Z を明るさにすると、ほとんど真っ白になって何も見えない。
    /// Day 6 の要点4で見たとおり、深度値は手前に極端に偏っているため
    /// (near=0.1, far=100 のとき、距離5の点でも深度は 0.99 を超える)。
    /// そこで**カメラからの距離に戻してから**表示する。この逆算の式は
    /// 深度値がどう作られたかを理解していないと書けないので、復習にちょうどよい。
    /// </summary>
    private void VisualizeDepth()
    {
        float near = _camera.NearPlane;
        float far = _camera.FarPlane;
        float[] depth = _rasterizer.Depth.Depth;
        int[] pixels = _framebuffer.Pixels;

        for (int i = 0; i < depth.Length; i++)
        {
            float ndcZ = depth[i];
            if (ndcZ >= 1.0f)
            {
                // 何も描かれていない場所は背景のままにする。
                continue;
            }

            // NDC の Z からカメラまでの距離を逆算する。
            // 投影行列が ndcZ = far/(far-near) * (1 - near/distance) を作っていたので、
            // これを distance について解いた形。
            float distance = near / (1.0f - ndcZ * (far - near) / far);

            // 見やすい範囲へ正規化して、手前を白、奥を黒にする。
            float shade = 1.0f - Math.Clamp((distance - DepthViewNear) / (DepthViewFar - DepthViewNear), 0.0f, 1.0f);
            pixels[i] = Framebuffer.Rgb(shade, shade, shade);
        }
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
        if (e.KeyCode == Keys.W)
        {
            _showWireframe = !_showWireframe;
        }

        // Z: 深度テストの ON / OFF。OFF にすると Day 6 で S を切ったときと同じ、
        // 「後から描いたものが勝つ」状態に戻る。
        if (e.KeyCode == Keys.Z)
        {
            _rasterizer.DepthTestEnabled = !_rasterizer.DepthTestEnabled;
        }

        // D: 深度バッファそのものを白黒で表示する。
        // 普段は見えないバッファを覗くと、Zバッファが何を持っているのかが一目で分かる。
        if (e.KeyCode == Keys.D)
        {
            _showDepth = !_showDepth;
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
