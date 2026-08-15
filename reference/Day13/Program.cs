using System.Diagnostics;
using System.Text;

namespace RawGL;

/// <summary>
/// エントリポイントとゲームループ。**Phase 2 のマイルストーン**。
///
/// Day 12 で作ったコンテキストの上に、今日は
///   頂点データを GPU に置く → シェーダを書く → 描け と命じる
/// の3つを足す。これで「自作バインディングだけで三角形が出る」が達成される。
///
/// Day 14 からは Silk.NET に移り、ここまでの Win32/WGL/GL のコードは捨てる。
/// **捨てて構わないものを、それでも一度書いた**ことに意味がある。
/// </summary>
internal static class Program
{
    private const int Width = 640;
    private const int Height = 480;

    private const int GLMajorVersion = 3;
    private const int GLMinorVersion = 3;

    /// <summary>
    /// 頂点シェーダ。**頂点1個につき1回**実行される。今日は3回。
    ///
    /// Phase 1 の Rasterizer が頂点ループの中でやっていた
    /// 「MVP を掛けてクリップ座標を出す」に、そのまま対応している。
    /// </summary>
    private const string VertexShaderSource = """
        #version 330 core

        // layout(location = N) で「頂点属性の N 番」と結び付ける。
        // この番号は C# 側の glVertexAttribPointer の第1引数と一致していなければならない。
        // 一致していなくてもコンパイルは通り、実行時に絵が壊れるだけなので注意。
        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec3 aColor;

        // uniform = 1回のドローコールの間ずっと同じ値。
        // 頂点ごとに変わる in(頂点属性)との違いがここ。
        uniform mat4 uTransform;

        // out で宣言した変数は、ラスタライザが頂点間を補間してから
        // フラグメントシェーダの同名の in に届ける。
        // この補間こそ Day 4 で自分で書いたバリセントリック座標による属性補間。
        out vec3 vColor;

        void main()
        {
            // gl_Position は組み込みの出力で、ここにクリップ座標を書く。
            // この後の透視除算(Day 6)とビューポート変換は GPU が勝手にやる。
            gl_Position = uTransform * vec4(aPosition, 0.0, 1.0);

            vColor = aColor;
        }
        """;

    /// <summary>
    /// フラグメントシェーダ。**塗られるピクセル1個につき1回**実行される。
    /// 三角形が画面の半分を覆えば10万回以上走る。
    ///
    /// Phase 1 の FillTriangle の最内周そのもので、
    /// Day 9 でフォンシェーディングが重かったのは、この処理を CPU が
    /// 1コアで順番にやっていたから。GPU は数千の演算器で同時に走らせる。
    /// </summary>
    private const string FragmentShaderSource = """
        #version 330 core

        // 頂点シェーダの out と**名前と型が一致している**ことで繋がる。
        // 届く値は3頂点の色を距離で混ぜたもの。
        in vec3 vColor;

        // コアプロファイルでは出力を自分で宣言する
        // (古い GLSL の gl_FragColor は廃止された)。
        out vec4 FragColor;

        void main()
        {
            FragColor = vec4(vColor, 1.0);
        }
        """;

    /// <summary>
    /// 三角形の頂点。1頂点あたり [x, y, r, g, b] の5要素を**交互に**並べてある。
    ///
    /// 位置だけの配列と色だけの配列に分ける持ち方(SoA)もあるが、
    /// 交互(AoS)にすると「1頂点ぶんのデータが連続している」ので
    /// GPU のキャッシュに乗りやすい。実際のモデルデータもほぼこの形。
    ///
    /// 座標は NDC(正規化デバイス座標)。画面の左下が (-1,-1)、右上が (1,1) で、
    /// **Y は上が正**。Phase 1 のフレームバッファ(Y は下が正)と逆なので、
    /// Day 6 の投影行列で Y を反転していたことを思い出すとよい。
    ///
    /// 並び順は反時計回り(CCW)。OpenGL の既定では CCW が表面で、
    /// Day 10 の背面カリングと同じ話がそのまま出てくる。
    /// </summary>
    private static readonly float[] TriangleVertices =
    [
        //   x      y        r     g     b
         0.0f,  0.6f,    1.0f, 0.2f, 0.2f,   // 上   : 赤
        -0.6f, -0.5f,    0.2f, 1.0f, 0.2f,   // 左下 : 緑
         0.6f, -0.5f,    0.2f, 0.2f, 1.0f,   // 右下 : 青
    ];

    /// <summary>1頂点あたりの float 数。位置2 + 色3。</summary>
    private const int FloatsPerVertex = 5;

    /// <summary>
    /// uniform に送る行列。毎フレーム new すると GC が動くので使い回す。
    /// </summary>
    private static readonly float[] Transform = new float[16];

    private static void Main()
    {
        AttachConsole();

        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        using var window = new Win32Window("Day13 - 三角形とシェーダー", Width, Height);
        using var context = new GLContext(window.Hwnd, GLMajorVersion, GLMinorVersion);

        Console.WriteLine($"GL_RENDERER : {context.Renderer}");
        Console.WriteLine($"GL_VERSION  : {context.Version}");
        Console.WriteLine($"GLSL        : {context.ShadingLanguageVersion}");
        Console.WriteLine();

        GL.glViewport(0, 0, window.ClientWidth, window.ClientHeight);

        using var shader = new Shader(VertexShaderSource, FragmentShaderSource);
        Console.WriteLine("シェーダのコンパイルとリンクに成功");

        CreateTriangle(out uint vertexArray, out uint vertexBuffer);
        GL.CheckError("三角形の準備");

        try
        {
            RunLoop(window, context, shader, vertexArray);
        }
        finally
        {
            // GPU のリソースは GC が面倒を見てくれない。自分で消す。
            GL.glDeleteBuffers(1, ref vertexBuffer);
            GL.glDeleteVertexArrays(1, ref vertexArray);
        }
    }

    /// <summary>
    /// 頂点データを GPU に置き、その読み方を教える。**今日の中心**。
    ///
    /// VBO と VAO の役割分担が分かりにくいので、先に整理しておく。
    ///   - **VBO**(頂点バッファ)= GPU 上のただのバイト列。意味は持たない
    ///   - **VAO**(頂点配列オブジェクト)= 「そのバイト列をどう解釈するか」の記録
    ///
    /// VAO があるおかげで、描画時は <c>glBindVertexArray</c> 1回で
    /// 属性の設定がまとめて復元される。VAO が無かった時代(OpenGL 2.x)は
    /// 描画のたびに全属性の <c>glVertexAttribPointer</c> を呼び直していた。
    /// </summary>
    private static void CreateTriangle(out uint vertexArray, out uint vertexBuffer)
    {
        // --- VAO を作ってバインドする ---
        // **先に VAO をバインドしておくこと**。以降の属性設定はカレントの VAO に記録される。
        // 順序を間違えると設定がどこにも残らず、画面に何も出ない。
        // コアプロファイルでは VAO は省略できない(0 番のままだと描画がエラーになる)。
        GL.glGenVertexArrays(1, out vertexArray);
        GL.glBindVertexArray(vertexArray);

        // --- VBO を作り、頂点データを転送する ---
        GL.glGenBuffers(1, out vertexBuffer);
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, vertexBuffer);

        // GL_STATIC_DRAW は「一度書いたら何度も描くのに使う」という**使い方の申告**。
        // ドライバはこれを見てメモリの置き場所(VRAM か共有メモリか)を決める。
        // 毎フレーム書き換えるなら GL_DYNAMIC_DRAW を使う(Day 17 のスプライトバッチ)。
        GL.glBufferData(
            GL.GL_ARRAY_BUFFER,
            new IntPtr(TriangleVertices.Length * sizeof(float)),
            TriangleVertices,
            GL.GL_STATIC_DRAW);

        int stride = FloatsPerVertex * sizeof(float);

        // --- 属性0: 位置(float 2個、オフセット 0)---
        // stride は「次の頂点まで何バイト飛ぶか」。交互に詰めているので20バイト。
        GL.glVertexAttribPointer(0, 2, GL.GL_FLOAT, GL.GL_FALSE, stride, IntPtr.Zero);
        GL.glEnableVertexAttribArray(0);

        // --- 属性1: 色(float 3個、オフセット 8バイト)---
        // 最後の引数は本来ポインタだが、VBO がバインドされている間は
        // 「バッファ先頭からのバイトオフセット」として解釈される。
        GL.glVertexAttribPointer(
            1, 3, GL.GL_FLOAT, GL.GL_FALSE, stride, new IntPtr(2 * sizeof(float)));
        GL.glEnableVertexAttribArray(1);

        // 後片付け。バインドを外しても、VAO に記録した属性の設定は残る。
        //
        // **GL_ARRAY_BUFFER のバインドは VAO の状態に含まれない**。
        // VAO が覚えているのは glVertexAttribPointer の内容(どのバッファの
        // どこを、どう読むか)であって、「今どのバッファがバインドされているか」ではない。
        // なのでここで外す順序は問題にならない。
        //
        // ただし**インデックスバッファ(GL_ELEMENT_ARRAY_BUFFER)は VAO に含まれる**。
        // VAO をバインドしたまま 0 を入れると VAO から外れてしまうので、
        // Day 14 以降で glDrawElements を使い始めたら注意すること。
        GL.glBindVertexArray(0);
        GL.glBindBuffer(GL.GL_ARRAY_BUFFER, 0);
    }

    private static void RunLoop(Win32Window window, GLContext context, Shader shader, uint vertexArray)
    {
        bool vsync = true;
        context.TrySetSwapInterval(1);

        bool wireframe = false;
        bool paused = false;

        var clock = Stopwatch.StartNew();
        double previousSeconds = 0.0;
        double angleSeconds = 0.0;

        double fpsElapsed = 0.0;
        int fpsFrames = 0;

        float aspect = (float)window.ClientWidth / window.ClientHeight;

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

            if (window.WasKeyPressed((int)'V'))
            {
                vsync = !vsync;
                context.TrySetSwapInterval(vsync ? 1 : 0);
            }

            // W: ワイヤーフレーム。**GPU のラスタライザが見える**。
            // Day 2 で Bresenham を書き、Day 3 で塗りつぶしを書いたが、
            // GPU ではこの切り替えが設定1つで済む(固定機能なので)。
            if (window.WasKeyPressed((int)'W'))
            {
                wireframe = !wireframe;
                GL.glPolygonMode(GL.GL_FRONT_AND_BACK, wireframe ? GL.GL_LINE : GL.GL_FILL);
            }

            if (!paused)
            {
                angleSeconds += deltaSeconds;
            }

            // --- 描画 ---

            GL.glClearColor(0.10f, 0.11f, 0.13f, 1.0f);
            GL.glClear(GL.GL_COLOR_BUFFER_BIT);

            // 「使うものをバインドしてから描け」が OpenGL の一貫した様式。
            shader.Use();

            FillTransform((float)angleSeconds, aspect);
            shader.SetMatrix4("uTransform", Transform);

            GL.glBindVertexArray(vertexArray);

            // **これが1回のドローコール**。
            // 「GL_TRIANGLES として、0番目から3頂点を描け」。
            // CPU が送るのはこの命令だけで、3頂点の変換も
            // 十数万ピクセルの塗りつぶしも、この1行の向こう側で起きる。
            GL.glDrawArrays(GL.GL_TRIANGLES, 0, 3);

            context.SwapBuffers();

            // --- 計測表示 ---

            fpsFrames++;
            fpsElapsed += deltaSeconds;
            if (fpsElapsed >= 0.5)
            {
                window.SetTitle(
                    $"Day13 - 三角形とシェーダー  {fpsFrames / fpsElapsed:F1} fps | "
                    + $"VSync:{(vsync ? "ON" : "OFF")} | {(wireframe ? "ワイヤー" : "塗り")}"
                    + (paused ? " | 一時停止中" : string.Empty)
                    + " | W:ワイヤー V:VSync Space:停止 Esc:終了");

                fpsFrames = 0;
                fpsElapsed = 0.0;
            }
        }
    }

    /// <summary>
    /// Z軸まわりの回転 + アスペクト比の補正を1つの行列にまとめる。
    ///
    /// **列優先で詰めるのが肝**(要点5)。<c>m[列 * 4 + 行]</c> の順に並べる。
    /// アスペクト補正が要るのは、NDC が縦横とも -1〜1 の正方形だから。
    /// 640x480 のウィンドウにそのまま写すと横に 4:3 だけ間延びする。
    /// Day 6 の透視投影行列が fovy とアスペクトから作られていたのと同じ話。
    /// </summary>
    private static void FillTransform(float angleRadians, float aspect)
    {
        float cos = MathF.Cos(angleRadians);
        float sin = MathF.Sin(angleRadians);
        float scaleX = 1.0f / aspect;

        // 第0列
        Transform[0] = scaleX * cos;
        Transform[1] = sin;
        Transform[2] = 0.0f;
        Transform[3] = 0.0f;

        // 第1列
        Transform[4] = scaleX * -sin;
        Transform[5] = cos;
        Transform[6] = 0.0f;
        Transform[7] = 0.0f;

        // 第2列
        Transform[8] = 0.0f;
        Transform[9] = 0.0f;
        Transform[10] = 1.0f;
        Transform[11] = 0.0f;

        // 第3列(平行移動。今日は動かさない)
        Transform[12] = 0.0f;
        Transform[13] = 0.0f;
        Transform[14] = 0.0f;
        Transform[15] = 1.0f;
    }

    /// <summary>
    /// コンソールを1枚立てて標準出力を繋ぎ直す(Day 12 と同じ)。
    /// 今日からは**シェーダのコンパイルエラーの出し先**として本領を発揮する。
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
