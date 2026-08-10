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

        Text = "Day02 - 線分描画";

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
            // Space キーでの Bresenham / DDA の速度比較も、この数字を見て行う。
            double renderStartSeconds = clock.Elapsed.TotalSeconds;
            Render(nowSeconds);
            renderSecondsAccum += clock.Elapsed.TotalSeconds - renderStartSeconds;

            Present();

            // FPSは毎フレーム表示すると数字が暴れて読めないので、0.5秒ぶんを平均する。
            fpsFrames++;
            fpsElapsed += deltaSeconds;
            if (fpsElapsed >= 0.5)
            {
                string algorithm = _framebuffer.UseDdaLine ? "DDA      " : "Bresenham";
                Text = $"Day02 - 線分描画  {fpsFrames / fpsElapsed:F1} fps | {algorithm} | render {renderSecondsAccum / fpsFrames * 1000.0:F2} ms | Space:切替 Esc:終了";
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

    /// <summary>
    /// 1フレーム分の絵をフレームバッファに描く。
    ///
    /// Day 2 の題材はすべて線分1本の組み合わせでできている。
    ///   - 方眼      … 水平線・垂直線(Bresenhamの退化ケース)
    ///   - 放射線    … 全方向(8オクタント)の網羅と、画面外へのはみ出し
    ///   - 星型      … 閉じた折れ線 = 多角形の輪郭
    ///   - リサージュ… 曲線も細かい折れ線で描ける、という割り切りの実演
    /// </summary>
    private void Render(double timeSeconds)
    {
        // Day 1 のグラデーション背景は線が見えづらいので、暗い単色に変える。
        _framebuffer.Clear(Framebuffer.Rgb(12, 14, 22));

        DrawGrid();
        DrawFan(timeSeconds);
        DrawStar(timeSeconds);
        DrawLissajous(timeSeconds);
    }

    /// <summary>方眼の間隔(ピクセル)。</summary>
    private const int GridSpacing = 32;

    /// <summary>
    /// 方眼を描く。
    ///
    /// 見た目は地味だが、水平線(dy = 0)と垂直線(dx = 0)はBresenhamの退化ケースで、
    /// 誤差項の更新が片側だけになる。ここを踏み外していると「線が1本消える」
    /// 「1ピクセルずれる」といった形ですぐ表に出るので、実質的なテストになっている。
    /// </summary>
    private void DrawGrid()
    {
        int width = _framebuffer.Width;
        int height = _framebuffer.Height;
        int color = Framebuffer.Rgb(34, 38, 52);

        for (int x = 0; x < width; x += GridSpacing)
        {
            _framebuffer.DrawLine(x, 0, x, height - 1, color);
        }

        for (int y = 0; y < height; y += GridSpacing)
        {
            _framebuffer.DrawLine(0, y, width - 1, y, color);
        }
    }

    /// <summary>放射状に伸ばす線の本数。</summary>
    private const int FanSpokes = 36;

    /// <summary>
    /// 中心から放射状に線を伸ばし、ゆっくり回転させる。
    ///
    /// 狙いは「1つの DrawLine で8方向すべてを描けているか」の目視確認。
    /// 傾きの急/緩、右向き/左向き、上向き/下向きのどれか1つでも取りこぼしていると、
    /// 回転の途中でその向きの線だけが消えたり、階段の形が明らかに崩れたりして一目で分かる。
    /// 「テストコードの代わりに絵で確かめる」のはグラフィックスで最も効率のよい検証方法。
    /// </summary>
    private void DrawFan(double timeSeconds)
    {
        int centerX = _framebuffer.Width / 4;
        int centerY = _framebuffer.Height / 4;
        double baseAngle = timeSeconds * 0.5;

        for (int i = 0; i < FanSpokes; i++)
        {
            double angle = baseAngle + i * (2.0 * Math.PI / FanSpokes);

            // 3本に1本は画面の外まで突き抜けさせる。
            // 範囲外の座標が来ても SetPixel が黙って捨てるので破綻しない、という確認と、
            // 同時に「画面外なのにループは回り続けている」という無駄の実演でもある
            // (この無駄をなくすのが改造課題2のクリッピング)。
            double radius = (i % 3 == 0) ? 340.0 : 104.0;

            int endX = centerX + (int)Math.Round(Math.Cos(angle) * radius);
            int endY = centerY + (int)Math.Round(Math.Sin(angle) * radius);

            _framebuffer.DrawLine(centerX, centerY, endX, endY, HueColor(i / (double)FanSpokes));
        }
    }

    /// <summary>
    /// 5つの頂点を1つ飛ばしで結んだ星型多角形({5/2}星形)を回転させる。
    ///
    /// 頂点を「2つ進む」順序で並べておけば、あとは素直に閉じた折れ線を描くだけで星になる。
    /// 多角形を描く側は「頂点をどう並べるか」だけを考えればよく、
    /// 線を引く仕事は DrawLine に任せきる、という層の分け方をここで作っておく。
    /// </summary>
    private void DrawStar(double timeSeconds)
    {
        const int vertexCount = 5;
        const double radius = 92.0;

        int centerX = _framebuffer.Width * 3 / 4;
        int centerY = _framebuffer.Height / 4;

        // 頂点数が少なく寿命もこのメソッド内だけなので、ヒープではなくスタックに置く。
        // 毎フレーム呼ばれる描画コードでGCを動かさないための基本的な作法。
        Span<(int X, int Y)> vertices = stackalloc (int X, int Y)[vertexCount];

        double angleOffset = -Math.PI / 2.0 + timeSeconds * 0.8;
        for (int i = 0; i < vertexCount; i++)
        {
            double angle = angleOffset + i * 2 * (2.0 * Math.PI / vertexCount);
            vertices[i] = (
                centerX + (int)Math.Round(Math.Cos(angle) * radius),
                centerY + (int)Math.Round(Math.Sin(angle) * radius));
        }

        _framebuffer.DrawPolyline(vertices, Framebuffer.Rgb(255, 214, 110), closed: true);
    }

    /// <summary>リサージュ曲線の分割数。大きいほど滑らかで、そのぶん線分が増える。</summary>
    private const int LissajousSegments = 160;

    /// <summary>
    /// リサージュ曲線(x と y を別々の周波数の正弦波で動かした軌跡)を折れ線で描く。
    ///
    /// ラスタライザは曲線を曲線のまま扱わない。細かく分割して直線に置き換えるだけ。
    /// 分割数を減らすとカクカクになり、増やすと滑らかになる代わりに線分の本数が増える
    /// ——この「滑らかさと処理量のトレードオフ」は、この先メッシュの分割や
    /// テッセレーションでまったく同じ形で再登場する。
    /// </summary>
    private void DrawLissajous(double timeSeconds)
    {
        int centerX = _framebuffer.Width / 2;
        int centerY = _framebuffer.Height * 3 / 4;
        double amplitudeX = _framebuffer.Width / 2.0 - 24.0;
        double amplitudeY = _framebuffer.Height / 4.0 - 24.0;

        // x 側の位相だけを時間で動かすと、図形が閉じたり開いたりしながら変化する。
        double phase = timeSeconds * 0.6;

        Span<(int X, int Y)> points = stackalloc (int X, int Y)[LissajousSegments + 1];
        for (int i = 0; i <= LissajousSegments; i++)
        {
            double t = i / (double)LissajousSegments * (2.0 * Math.PI);
            points[i] = (
                centerX + (int)Math.Round(Math.Sin(3.0 * t + phase) * amplitudeX),
                centerY + (int)Math.Round(Math.Sin(2.0 * t) * amplitudeY));
        }

        _framebuffer.DrawPolyline(points, Framebuffer.Rgb(120, 216, 255));
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

        // Spaceで線分描画アルゴリズムを切り替える。
        // 見た目はほとんど変わらない(実測でピクセル全体の0.5%しかずれない)が、
        // 「ほとんど」であって「完全一致」ではない、というのが今日の見どころ。
        // 切り替えた瞬間に線がわずかにチラつく箇所があれば、それがDDAの累積誤差。
        if (e.KeyCode == Keys.Space)
        {
            _framebuffer.UseDdaLine = !_framebuffer.UseDdaLine;
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
