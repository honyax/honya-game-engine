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

        Text = "Day08 - テクスチャマッピング";

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
                string filter = _texture.Filter == TextureFilter.Bilinear ? "バイリニア" : "ニアレスト　";
                string correct = _rasterizer.PerspectiveCorrect ? "ON " : "OFF";
                Text = $"Day08 - テクスチャマッピング  {fpsFrames / fpsElapsed:F1} fps | {TriangleCount} tri | "
                     + $"render {renderSecondsAccum / fpsFrames * 1000.0:F2} ms | {filter} | 透視補正:{correct} | "
                     + $"F:フィルタ P:透視補正 W:ワイヤー Esc:終了";
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
    private const int OrbitCubes = 2;

    /// <summary>1フレームに描く三角形の総数(床2 + 立方体)。</summary>
    private const int TriangleCount = 2 + CubeTriangles * (1 + OrbitCubes);

    /// <summary>カメラ。</summary>
    private readonly Camera _camera = new();

    /// <summary>
    /// テクスチャ。手続きで作った 32x32 のテストパターン1枚を使い回す。
    ///
    /// あえて小さくしてある。立方体に貼ると1テクセルが画面上で何ピクセルにも
    /// 拡大されるので、ニアレストとバイリニアの差がはっきり見える。
    /// </summary>
    private readonly Texture _texture = Texture.CreateTestPattern(32, 8);

    /// <summary>
    /// テクスチャを引くシェーダ。
    ///
    /// ラスタライザから渡ってくるのは「補間された色」と「補間されたUV」だけ。
    /// ここでは頂点色を明るさとして使い、テクスチャの色に掛けている。
    /// **色 x テクスチャ**は最も基本的な合成で、Day 9 では
    /// この「色」の部分が光の計算結果に変わる。
    /// </summary>
    private int ShadeTextured(Vec3 color, Vec2 uv)
    {
        Vec3 texel = _texture.Sample(uv.X, uv.Y);
        Vec3 result = texel * color;
        return Framebuffer.Rgb(result.X, result.Y, result.Z);
    }

    /// <summary>
    /// 1フレーム分の絵をフレームバッファに描く。
    ///
    /// 床を大きく手前まで伸ばしてあるのは、透視補正の有無を見るため。
    /// 画面の手前と奥で1ピクセルあたりの実距離が大きく違う面ほど、
    /// 補正を切ったときの歪みが目立つ。
    /// </summary>
    private void Render(double timeSeconds)
    {
        _framebuffer.Clear(Framebuffer.Rgb(12, 14, 22));
        _rasterizer.Depth.Clear();

        float t = (float)timeSeconds;

        // カメラは床の外側を回る。半径を床の対角の長さより大きく取っているのには理由がある。
        // 今の実装は「頂点が1つでもカメラの後ろにある三角形」を丸ごと捨てる(Day 6 の要点5)。
        // 床は大きな三角形2枚なので、隅がカメラの後ろに回った瞬間に床全体が消えてしまう。
        // Day 10 のクリッピングを入れるまでは、この制約の中で絵を作る。
        float orbit = t * 0.2f;
        _camera.Position = new Vec3(MathF.Sin(orbit) * 6.5f, 2.6f, MathF.Cos(orbit) * 6.5f);
        _camera.Target = new Vec3(0.0f, 0.3f, 0.0f);
        _camera.AspectRatio = _framebuffer.Width / (float)_framebuffer.Height;

        Mat4 viewProjection = _camera.ViewProjection;

        // --- 床 ---
        // UV を 0〜4 にしてテクスチャを繰り返し貼る。
        // 1より大きいUVが折り返されるのは Texture.WrapIndex の働き。
        DrawFloor(viewProjection, 3.2f, 4.0f);

        // --- 中央の立方体 ---
        Mat4 centerModel =
            Mat4.RotationY(t * 0.5f) *
            Mat4.RotationX(t * 0.27f) *
            Mat4.Translation(new Vec3(0.0f, 0.9f, 0.0f));
        DrawTexturedCube(centerModel * viewProjection, Vec3.One);

        // --- 周囲を回る立方体 ---
        for (int i = 0; i < OrbitCubes; i++)
        {
            float phase = i * MathF.PI;
            Mat4 model =
                Mat4.Scale(0.4f) *
                Mat4.RotationZ(t * 1.5f) *
                Mat4.Translation(new Vec3(2.2f, 0.6f, 0.0f)) *
                Mat4.RotationY(t * 0.7f + phase);

            DrawTexturedCube(model * viewProjection, ColorFromHue(i / (float)OrbitCubes) * 1.2f);
        }

        if (_showDepth)
        {
            VisualizeDepth();
        }
    }

    /// <summary>
    /// 床(大きな正方形1枚 = 三角形2枚)を描く。
    ///
    /// **透視補正の効果が最も分かりやすい場所**。床は手前から奥まで大きく傾いているので、
    /// 補正を切ると三角形の対角線を境にタイルがぐにゃりと折れ曲がる。
    /// </summary>
    private void DrawFloor(Mat4 viewProjection, float halfSize, float uvRepeat)
    {
        var lt = new Vertex(new Vec3(-halfSize, 0.0f, -halfSize), Vec3.One, new Vec2(0.0f, 0.0f));
        var rt = new Vertex(new Vec3(halfSize, 0.0f, -halfSize), Vec3.One, new Vec2(uvRepeat, 0.0f));
        var rb = new Vertex(new Vec3(halfSize, 0.0f, halfSize), Vec3.One, new Vec2(uvRepeat, uvRepeat));
        var lb = new Vertex(new Vec3(-halfSize, 0.0f, halfSize), Vec3.One, new Vec2(0.0f, uvRepeat));

        _rasterizer.DrawTriangle(lt, rt, rb, viewProjection, ShadeTextured);
        _rasterizer.DrawTriangle(lt, rb, lb, viewProjection, ShadeTextured);

        if (_showWireframe)
        {
            DrawWire(lt.Position, rt.Position, rb.Position, viewProjection);
            DrawWire(lt.Position, rb.Position, lb.Position, viewProjection);
        }
    }

    /// <summary>
    /// テクスチャを貼った立方体を1個描く。
    ///
    /// 面ごとに UV を (0,0)-(1,1) で貼るので、6面すべてに同じ絵が出る。
    /// 実際のモデルでは1枚の画像を面ごとに切り分けて使う(UV展開)。
    /// </summary>
    private void DrawTexturedCube(Mat4 mvp, Vec3 tint)
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

        // 面ごとにわずかに明るさを変える。ライティングはまだ無いので、
        // これが無いと立方体が真っ平らな塊に見えてしまう(Day 9 で本物の陰影が入る)。
        ReadOnlySpan<float> faceShade = stackalloc float[] { 0.72f, 0.86f, 0.60f, 1.0f, 0.78f, 0.92f };

        var uvLt = new Vec2(0.0f, 0.0f);
        var uvRt = new Vec2(1.0f, 0.0f);
        var uvRb = new Vec2(1.0f, 1.0f);
        var uvLb = new Vec2(0.0f, 1.0f);

        for (int face = 0; face < 6; face++)
        {
            Vec3 color = tint * faceShade[face];

            var v0 = new Vertex(corners[faces[face * 4]], color, uvLt);
            var v1 = new Vertex(corners[faces[face * 4 + 1]], color, uvRt);
            var v2 = new Vertex(corners[faces[face * 4 + 2]], color, uvRb);
            var v3 = new Vertex(corners[faces[face * 4 + 3]], color, uvLb);

            _rasterizer.DrawTriangle(v0, v1, v2, mvp, ShadeTextured);
            _rasterizer.DrawTriangle(v0, v2, v3, mvp, ShadeTextured);

            if (_showWireframe)
            {
                DrawWire(v0.Position, v1.Position, v2.Position, mvp);
                DrawWire(v0.Position, v2.Position, v3.Position, mvp);
            }
        }
    }

    /// <summary>三角形の輪郭を描く(ワイヤーフレーム表示用)。</summary>
    private void DrawWire(Vec3 p0, Vec3 p1, Vec3 p2, Mat4 mvp)
    {
        if (_rasterizer.TryProjectToScreen(p0, mvp, out Vec3 s0) &&
            _rasterizer.TryProjectToScreen(p1, mvp, out Vec3 s1) &&
            _rasterizer.TryProjectToScreen(p2, mvp, out Vec3 s2))
        {
            _rasterizer.DrawTriangleWireframe(s0, s1, s2, Framebuffer.Rgb(255, 60, 60));
        }
    }

    /// <summary>深度バッファを可視化する表示範囲(カメラからの距離)。</summary>
    private const float DepthViewNear = 1.5f;

    private const float DepthViewFar = 11.0f;

    /// <summary>
    /// 深度バッファの中身を白黒で塗り直す。手前が白、奥が黒。
    /// NDC の Z は手前に極端に偏っているので、カメラからの距離に逆算してから表示する。
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
                continue;
            }

            float distance = near / (1.0f - ndcZ * (far - near) / far);
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
        if (e.KeyCode == Keys.D)
        {
            _showDepth = !_showDepth;
        }

        // F: テクスチャフィルタの切り替え(ニアレスト / バイリニア)。
        if (e.KeyCode == Keys.F)
        {
            _texture.Filter = _texture.Filter == TextureFilter.Bilinear
                ? TextureFilter.Nearest
                : TextureFilter.Bilinear;
        }

        // P: 透視補正補間の ON / OFF。今日の一番の見どころ。
        // OFF にすると、床のタイルが三角形の対角線で折れ曲がって見える。
        if (e.KeyCode == Keys.P)
        {
            _rasterizer.PerspectiveCorrect = !_rasterizer.PerspectiveCorrect;
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
