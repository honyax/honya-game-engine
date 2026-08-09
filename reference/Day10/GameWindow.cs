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

        Text = "Day10 - ソフトラスタライザ完成";

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
                string mode = _shadingMode switch
                {
                    ShadingMode.Flat => "フラット",
                    ShadingMode.Gouraud => "グーロー",
                    _ => "フォン　",
                };
                string cull = _rasterizer.Culling == CullMode.None ? "OFF" : "ON ";
                Text = $"Day10 - ソフトラスタライザ完成  {fpsFrames / fpsElapsed:F1} fps | "
                     + $"{_rasterizer.DrawnTriangles} 描画 / {_rasterizer.CulledTriangles} カリング | "
                     + $"render {renderSecondsAccum / fpsFrames * 1000.0:F2} ms | {mode} | 背面カリング:{cull} | "
                     + $"1/2/3:陰影 C:カリング T:テクスチャ W:ワイヤー Esc:終了";
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

    /// <summary>陰影の付け方(1/2/3キー)。</summary>
    private ShadingMode _shadingMode = ShadingMode.Phong;

    /// <summary>テクスチャを使うか(Tキー)。切ると陰影だけが見える。</summary>
    private bool _useTexture = true;

    /// <summary>カメラ。</summary>
    private readonly Camera _camera = new();

    /// <summary>光源。</summary>
    private Light _light = Light.Default;

    /// <summary>テクスチャ。</summary>
    private readonly Texture _texture = Texture.CreateTestPattern(64, 8);

    // --- メッシュは起動時に1回だけ作る ---

    /// <summary>
    /// ファイルから読み込んだモデル。**Phase 1 のマイルストーンの主役**。
    ///
    /// パスを実行ファイルからの相対ではなくソースツリー基準で解決しているのは、
    /// <c>dotnet run</c> でも <c>bin</c> から直接起動しても同じように動かすため。
    /// 資産の置き場所をどう解決するかは Day 21 のリソース管理で正面から扱う。
    /// </summary>
    private readonly Mesh _model = ObjLoader.Load(ResolveAssetPath("models/torus.obj"));

    private readonly Mesh _sphere = Mesh.CreateSphere(20, 28);

    private readonly Mesh _cube = Mesh.CreateCube();

    /// <summary>
    /// 光源の位置を示す小さな球。画面上では20ピクセルほどにしかならないので、
    /// 分割数を落としたものを別に持つ。
    ///
    /// 高精細な球を使い回すと、見えないほど小さいのに三角形1536枚ぶんの
    /// セットアップ(頂点変換・バウンディングボックス・エッジ関数の準備)を払うことになる。
    /// **画面に占める大きさに見合った精度のモデルを使う**のがLOD(Level of Detail)の考え方で、
    /// ここではその最も素朴な形をやっている。
    ///
    /// ただし実測では、三角形が 3116 枚から 1700 枚へ**ほぼ半減したのに
    /// render は 14.02ms から 13.77ms にしかならなかった**(改善は2%未満)。
    /// この場面で効いているのは三角形の枚数ではなく、塗るピクセル数のほうだった、ということ。
    /// LODが効くのは「小さいものが大量にある」場面であって、今回のように
    /// 画面の大半を数個の大きな物体が占めている場合ではない。
    /// 見積もりで最適化せず必ず測る、という Day 2 以来の教訓がまた出た形になる。
    /// </summary>
    private readonly Mesh _lightMarker = Mesh.CreateSphere(6, 10);

    /// <summary>
    /// 床。Day 9 では分割数4にしていたが、クリッピングが入ったので1枚(分割1)で足りる。
    /// カメラの後ろに回った部分は近クリップ面で切られるだけで、面が丸ごと消えることはない。
    /// </summary>
    private readonly Mesh _floor = Mesh.CreatePlane(3.6f, 5.0f, 1);

    /// <summary>
    /// 素材(assets/)のパスを解決する。
    /// 実行ディレクトリから上へ辿ってリポジトリのルートを探す。
    /// </summary>
    private static string ResolveAssetPath(string relativePath)
    {
        // 実行ファイルの場所と、現在の作業ディレクトリの両方から上へ辿る。
        // dotnet run と bin から直接起動とで基準が変わるため、両方見ておくと確実。
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "assets", relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"素材が見つからない: assets/{relativePath}");
    }

    /// <summary>
    /// 頂点をワールド座標へ変換した結果を溜める作業用配列。
    ///
    /// 索引で共有された頂点を何度も変換しないための置き場。
    /// 毎フレーム確保するとGCが動くので、一番大きいメッシュに合わせて1回だけ確保する。
    /// </summary>
    private Vertex[] _worldVertices = Array.Empty<Vertex>();

    /// <summary>いま描いているメッシュのアルベド(素の色)。シェーダから参照する。</summary>
    private Vec3 _currentAlbedo = Vec3.One;

    /// <summary>
    /// 1フレーム分の絵をフレームバッファに描く。
    /// </summary>
    private void Render(double timeSeconds)
    {
        _framebuffer.Clear(Framebuffer.Rgb(10, 12, 20));
        _rasterizer.Depth.Clear();
        _rasterizer.ResetStatistics();

        float t = (float)timeSeconds;

        // カメラは床の内側まで入り込む軌道にしてある。
        // クリッピングが無いと床が丸ごと消えたり画面が壊れたりする位置関係で、
        // Day 10 でそれが解決したことを目で確かめるためのアングル。
        float orbit = t * 0.22f;
        float distance = 5.4f + MathF.Sin(t * 0.5f) * 1.0f;
        _camera.Position = new Vec3(
            MathF.Sin(orbit) * distance,
            2.4f + MathF.Sin(t * 0.37f) * 0.5f,
            MathF.Cos(orbit) * distance);
        _camera.Target = new Vec3(0.0f, 0.95f, 0.0f);
        _camera.AspectRatio = _framebuffer.Width / (float)_framebuffer.Height;

        // 光源を回して、陰影が動くようにする。
        _light.Position = new Vec3(MathF.Cos(t * 0.7f) * 3.4f, 2.8f, MathF.Sin(t * 0.7f) * 3.4f);

        Mat4 viewProjection = _camera.ViewProjection;

        // --- 床 ---
        // 板は裏から見ることもあるのでカリングを切る。
        // 閉じていない形にカリングを掛けると、裏から見たときに消えてしまう。
        CullMode previousCulling = _rasterizer.Culling;
        _rasterizer.Culling = CullMode.None;
        _currentAlbedo = new Vec3(0.72f, 0.75f, 0.82f);
        DrawMesh(_floor, Mat4.Identity, viewProjection);
        _rasterizer.Culling = previousCulling;

        // --- 読み込んだモデル(主役)---
        _currentAlbedo = new Vec3(0.90f, 0.62f, 0.38f);
        Mat4 modelMatrix =
            Mat4.Scale(0.85f) *
            Mat4.RotationX(0.55f) *
            Mat4.RotationY(t * 0.55f) *
            Mat4.Translation(new Vec3(0.0f, 1.05f, 0.0f));
        DrawMesh(_model, modelMatrix, viewProjection);

        // --- 周囲を回る立方体と球 ---
        _currentAlbedo = new Vec3(0.42f, 0.72f, 0.55f);
        DrawMesh(
            _cube,
            Mat4.Scale(0.32f) * Mat4.RotationY(t * 1.3f) * Mat4.RotationZ(t * 0.7f) *
            Mat4.Translation(new Vec3(2.4f, 0.5f, 0.0f)) * Mat4.RotationY(t * 0.6f),
            viewProjection);

        _currentAlbedo = new Vec3(0.45f, 0.55f, 0.92f);
        DrawMesh(
            _sphere,
            Mat4.Scale(0.36f) * Mat4.Translation(new Vec3(2.4f, 0.5f, 0.0f)) * Mat4.RotationY(t * 0.6f + MathF.PI),
            viewProjection);

        // --- 光源の位置を示す小さな球 ---
        _currentAlbedo = Vec3.One;
        DrawMesh(_lightMarker, Mat4.Scale(0.1f) * Mat4.Translation(_light.Position), viewProjection, emissive: true);

        if (_showDepth)
        {
            VisualizeDepth();
        }
    }

    /// <summary>
    /// メッシュを1つ描く。
    ///
    /// 手順は2段階。
    ///   1. **全頂点**をモデル座標からワールド座標へ変換する(1回だけ)
    ///   2. 索引を3つずつ取り出して三角形を組み、ラスタライザへ渡す
    ///
    /// 索引で共有された頂点は、1 で一度変換すれば使い回せる。
    /// 球(24x32分割)なら三角形1536枚に対して頂点は825個なので、
    /// 三角形ごとに変換すると 4608 回のところが 825 回で済む。**5.6倍の節約**。
    /// GPUに頂点バッファとインデックスバッファが別々にある理由がこれ。
    /// </summary>
    private void DrawMesh(Mesh mesh, Mat4 model, Mat4 viewProjection, bool emissive = false)
    {
        if (_worldVertices.Length < mesh.Vertices.Length)
        {
            _worldVertices = new Vertex[mesh.Vertices.Length];
        }

        // --- 1. 頂点をワールドへ ---
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            Vertex v = mesh.Vertices[i];

            v.Position = Mat4.TransformPoint(v.Position, model);

            // 法線は「向き」なので W=0 の方向ベクトルとして変換する。
            // 平行移動の影響を受けてはいけない(Day 5 の要点1)。
            //
            // 注意: これが正しいのは、モデル行列が回転と一様な拡大縮小しか
            // 含まない場合に限る。軸ごとに違う倍率(非一様スケール)を掛けると
            // 法線が面に垂直でなくなるため、本来は「逆転置行列」で変換する必要がある。
            // 本Dayのデモは一様スケールしか使わないので、この簡易版で足りる。
            v.Normal = Mat4.TransformDirection(v.Normal, model).Normalized();

            // グーローシェーディングは**頂点で**光を計算し、その結果を色として持たせる。
            // あとはラスタライザが色を補間してくれるので、ピクセル側では何もしない。
            if (_shadingMode == ShadingMode.Gouraud && !emissive)
            {
                v.Color = _light.Shade(v.Position, v.Normal, _currentAlbedo, _camera.Position);
            }
            else
            {
                v.Color = _currentAlbedo;
            }

            _worldVertices[i] = v;
        }

        PixelShader? shader = emissive ? null : SelectShader();

        // --- 2. 索引から三角形を組んで描く ---
        for (int i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            Vertex v0 = _worldVertices[mesh.Indices[i]];
            Vertex v1 = _worldVertices[mesh.Indices[i + 1]];
            Vertex v2 = _worldVertices[mesh.Indices[i + 2]];

            // フラットシェーディングは**面ごとに**1回だけ光を計算する。
            // 面の法線は3頂点から外積で求める(Day 5 の要点4)。
            // 3頂点すべてに同じ色を入れるので、補間しても結果は一様になる。
            if (_shadingMode == ShadingMode.Flat && !emissive)
            {
                Vec3 faceNormal = Vec3.Cross(v1.Position - v0.Position, v2.Position - v0.Position).Normalized();
                Vec3 center = (v0.Position + v1.Position + v2.Position) * (1.0f / 3.0f);
                Vec3 lit = _light.Shade(center, faceNormal, _currentAlbedo, _camera.Position);

                v0.Color = lit;
                v1.Color = lit;
                v2.Color = lit;
            }

            _rasterizer.DrawTriangle(v0, v1, v2, viewProjection, shader);

            if (_showWireframe)
            {
                DrawWire(v0.Position, v1.Position, v2.Position, viewProjection);
            }
        }
    }

    /// <summary>いまの設定に合ったピクセルシェーダを返す。</summary>
    private PixelShader SelectShader()
        => _shadingMode == ShadingMode.Phong ? ShadePhong : ShadeInterpolated;

    /// <summary>
    /// フォンシェーディング。**ピクセルごとに**法線を補間して光を計算する。
    ///
    /// 3つのモードの中で唯一、面の内側でも正しい法線を使う。
    /// 球のハイライトが小さく丸く出るのはこれだけで、
    /// グーローだと頂点にしかハイライトが乗らないため、三角形の形に歪む。
    /// </summary>
    private int ShadePhong(in PixelInput input)
    {
        Vec3 albedo = _useTexture
            ? _texture.Sample(input.TexCoord.X, input.TexCoord.Y) * input.Color
            : input.Color;

        Vec3 lit = _light.Shade(input.World, input.Normal, albedo, _camera.Position);
        return Framebuffer.Rgb(lit.X, lit.Y, lit.Z);
    }

    /// <summary>
    /// フラット / グーロー用。光の計算は済んでいるので、テクスチャを掛けるだけ。
    /// </summary>
    private int ShadeInterpolated(in PixelInput input)
    {
        Vec3 color = _useTexture
            ? _texture.Sample(input.TexCoord.X, input.TexCoord.Y) * input.Color
            : input.Color;

        return Framebuffer.Rgb(color.X, color.Y, color.Z);
    }

    /// <summary>三角形の輪郭を描く(ワイヤーフレーム表示用)。</summary>
    private void DrawWire(Vec3 p0, Vec3 p1, Vec3 p2, Mat4 viewProjection)
    {
        if (_rasterizer.TryProjectToScreen(p0, viewProjection, out Vec3 s0) &&
            _rasterizer.TryProjectToScreen(p1, viewProjection, out Vec3 s1) &&
            _rasterizer.TryProjectToScreen(p2, viewProjection, out Vec3 s2))
        {
            _rasterizer.DrawTriangleWireframe(s0, s1, s2, Framebuffer.Rgb(255, 60, 60));
        }
    }

    /// <summary>深度バッファを可視化する表示範囲(カメラからの距離)。</summary>
    private const float DepthViewNear = 1.5f;

    private const float DepthViewFar = 11.0f;

    /// <summary>深度バッファの中身を白黒で塗り直す。手前が白、奥が黒。</summary>
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

        // P: 透視補正補間の ON / OFF。
        if (e.KeyCode == Keys.P)
        {
            _rasterizer.PerspectiveCorrect = !_rasterizer.PerspectiveCorrect;
        }

        // 1 / 2 / 3: シェーディングモデルの切り替え。今日の一番の見どころ。
        if (e.KeyCode == Keys.D1) _shadingMode = ShadingMode.Flat;
        if (e.KeyCode == Keys.D2) _shadingMode = ShadingMode.Gouraud;
        if (e.KeyCode == Keys.D3) _shadingMode = ShadingMode.Phong;

        // T: テクスチャの ON / OFF。陰影だけを見たいときに切る。
        if (e.KeyCode == Keys.T)
        {
            _useTexture = !_useTexture;
        }

        // C: 背面カリングの ON / OFF。
        // 切ると、閉じた立体の裏面まで律儀に塗るようになる(絵は変わらず遅くなるだけ)。
        if (e.KeyCode == Keys.C)
        {
            _rasterizer.Culling = _rasterizer.Culling == CullMode.None ? CullMode.Back : CullMode.None;
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
