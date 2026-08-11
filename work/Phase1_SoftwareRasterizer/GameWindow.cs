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

        Text = "Day03 - 三角形の塗りつぶし";

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
                string topLeft = _rasterizer.UseTopLeftRule ? "ON " : "OFF";
                Text = $"Day03 - 三角形の塗りつぶし  {fpsFrames / fpsElapsed:F1} fps | {TriangleCount} tri | "
                     + $"render {renderSecondsAccum / fpsFrames * 1000.0:F2} ms | TopLeft:{topLeft} | W:ワイヤー T:ルール Esc:終了";
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

    /// <summary>1フレームに描く三角形の総数(単体1枚 + 継ぎ目テスト + 円盤)。</summary>
    private const int TriangleCount = 1 + (SeamGrid * SeamGrid * 2) + DiscTriangles;

    /// <summary>
    /// 1フレーム分の絵をフレームバッファに描く。
    ///
    /// Day 3 の題材は3つとも三角形の塗りつぶしだが、確かめたいことが違う。
    ///   - 単体の三角形 … エッジ関数による内外判定が正しいか(ワイヤーと見比べる)
    ///   - 格子         … 辺を共有する三角形の境界が二重に塗られていないか(top-left rule)
    ///   - 円盤         … 細長い三角形が大量にあっても破綻しないか、そして速度
    /// </summary>
    private void Render(double timeSeconds)
    {
        _framebuffer.Clear(Framebuffer.Rgb(12, 14, 22));

        DrawSingleTriangle(timeSeconds);
        DrawSeamTest();
        DrawDisc(timeSeconds);
    }

    /// <summary>
    /// 回転する三角形を1枚描く。
    ///
    /// Wキーでワイヤーフレームを重ねられる。塗りつぶされた領域の縁と輪郭線が
    /// ぴったり一致していれば、エッジ関数の内外判定が正しく効いている。
    /// なお輪郭線(Bresenham)と塗りつぶし(エッジ関数)は別のアルゴリズムなので、
    /// 完全に同じピクセルにはならない。ズレるのは辺の上の1ピクセルだけのはず。
    /// </summary>
    private void DrawSingleTriangle(double timeSeconds)
    {
        const double radius = 92.0;
        int centerX = 150;
        int centerY = 130;

        Span<(int X, int Y)> v = stackalloc (int X, int Y)[3];
        for (int i = 0; i < 3; i++)
        {
            double angle = timeSeconds * 0.7 + i * (2.0 * Math.PI / 3.0);
            v[i] = (
                centerX + (int)Math.Round(Math.Cos(angle) * radius),
                centerY + (int)Math.Round(Math.Sin(angle) * radius));
        }

        _rasterizer.FillTriangle(v[0].X, v[0].Y, v[1].X, v[1].Y, v[2].X, v[2].Y, Framebuffer.Rgb(230, 140, 60));

        if (_showWireframe)
        {
            _rasterizer.DrawTriangleWireframe(v[0].X, v[0].Y, v[1].X, v[1].Y, v[2].X, v[2].Y, Framebuffer.Rgb(255, 255, 255));
        }
    }

    /// <summary>継ぎ目テストの格子(縦横の枚数)。1マスが三角形2枚。</summary>
    private const int SeamGrid = 5;

    /// <summary>継ぎ目テストの1マスの大きさ(ピクセル)。</summary>
    private const int SeamCellSize = 32;

    /// <summary>
    /// 正方形を2枚の三角形に割ったものを格子状に並べ、加算合成で描く。今日の主役の実験。
    ///
    /// 隣り合う三角形は必ず辺を共有している。その辺の上にちょうど乗ったピクセルを
    /// 両方が「自分の内側だ」と判定すると、そのピクセルは2回塗られる。
    /// 加算合成にしてあるので、2回塗られた場所は明るい線として浮かび上がる。
    /// Tキーで top-left rule を切ると、格子線と対角線がはっきり光って見える。
    ///
    /// 図形をわざと軸に沿わせているのには理由がある。斜めの辺だと
    /// 「ピクセルがちょうど辺の上に乗る」ことがめったに起きず、
    /// 二重描画が数ピクセルしか出ないので目で確認しづらい。
    /// 縦・横・45度の辺なら整数座標に必ず乗るので、問題が最大限に見える。
    ///
    /// 不透明な単色で塗っているうちは二重描画は目に見えないが、
    /// 半透明合成では色が濃くなり、Day 7 のZバッファでは深度の書き込み回数が変わる。
    /// 「見えないから放っておいてよい」種類のバグではない。
    /// </summary>
    private void DrawSeamTest()
    {
        const int originX = 390;
        const int originY = 46;

        // 加算合成に切り替える。暗めの色で塗るので、
        // 1回塗り = 落ち着いた青、2回塗り = 明るい青、と見分けがつく。
        _rasterizer.AdditiveBlend = true;
        int color = Framebuffer.Rgb(48, 72, 104);

        for (int gy = 0; gy < SeamGrid; gy++)
        {
            for (int gx = 0; gx < SeamGrid; gx++)
            {
                int left = originX + gx * SeamCellSize;
                int top = originY + gy * SeamCellSize;
                int right = left + SeamCellSize;
                int bottom = top + SeamCellSize;

                // 1マスを対角線で2枚に割る。2枚は対角線(45度)を共有し、
                // 隣のマスとは縦横の辺を共有する。
                // 頂点の並び順(巻き方向)をマスごとに交互に変えて、
                // FillTriangle 側の正規化がどちらの向きでも効くことも確認している。
                if ((gx + gy) % 2 == 0)
                {
                    _rasterizer.FillTriangle(left, top, right, top, left, bottom, color);
                    _rasterizer.FillTriangle(right, top, right, bottom, left, bottom, color);
                }
                else
                {
                    _rasterizer.FillTriangle(left, top, left, bottom, right, top, color);
                    _rasterizer.FillTriangle(right, top, left, bottom, right, bottom, color);
                }
            }
        }

        _rasterizer.AdditiveBlend = false;
    }

    /// <summary>
    /// 細長い三角形を大量に並べて円盤を作る。
    ///
    /// 頂点1つを中心に集めた「トライアングルファン」で、3Dのモデルでも
    /// 円錐や円柱の蓋によく出てくる形。中心付近では三角形が極端に細くなるので、
    /// 内外判定が甘いと中心にピンホール(塗り残しの穴)が空く。
    /// </summary>
    private void DrawDisc(double timeSeconds)
    {
        const double radius = 108.0;
        int centerX = _framebuffer.Width / 2;
        int centerY = 350;

        for (int i = 0; i < DiscTriangles; i++)
        {
            double a0 = -timeSeconds * 0.3 + i * (2.0 * Math.PI / DiscTriangles);
            double a1 = -timeSeconds * 0.3 + (i + 1) * (2.0 * Math.PI / DiscTriangles);

            int x0 = centerX + (int)Math.Round(Math.Cos(a0) * radius);
            int y0 = centerY + (int)Math.Round(Math.Sin(a0) * radius);
            int x1 = centerX + (int)Math.Round(Math.Cos(a1) * radius);
            int y1 = centerY + (int)Math.Round(Math.Sin(a1) * radius);

            _rasterizer.FillTriangle(centerX, centerY, x0, y0, x1, y1, HueColor(i / (double)DiscTriangles));

            if (_showWireframe)
            {
                _rasterizer.DrawTriangleWireframe(centerX, centerY, x0, y0, x1, y1, Framebuffer.Rgb(30, 30, 30));
            }
        }
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

        // T: top-left rule の ON / OFF。今日の一番の見どころ。
        // OFF にすると、右上の格子の継ぎ目に明るい線が浮かび上がる
        // (= 隣り合う三角形が同じピクセルを2回塗っている)。
        if (e.KeyCode == Keys.T)
        {
            _rasterizer.UseTopLeftRule = !_rasterizer.UseTopLeftRule;
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
