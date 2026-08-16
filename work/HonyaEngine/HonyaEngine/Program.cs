using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace HonyaEngine;

/// <summary>
/// エントリポイント。**Phase 3 の1日目**。
///
/// 画面に出るものは Day 13 と同じ「回転する三角形」だが、
/// その下にあったコードは全部入れ替わっている。
///
///   Day 11〜13: Win32Window / Wgl / GLContext / GL(自作) … 約1,500行
///   Day 14    : Silk.NET のパッケージ参照3行
///
/// Phase 2 で書いた「儀式」がまるごと消えたことを、実物で確認するのが今日の前半。
/// 後半は <see cref="Shader"/> を、使い回せる部品に育てる。
///
/// 名前空間が <c>HonyaEngine</c> になったのもここから。
/// **これ以降のDayは、この名前空間の中身を育て続ける**ことになる。
/// </summary>
internal static class Program
{
    private const int Width = 640;
    private const int Height = 480;

    // Silk.NET のコールバックは静的メソッドで受けるので、状態も静的に持つ。
    // Day 19 以降でゲームループとエンジンのクラスを作るときに整理する。
    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;
    private static Shader _shader = null!;

    private static uint _vertexArray;
    private static uint _vertexBuffer;

    private static float _angle;
    private static float _elapsedSeconds;
    private static bool _paused;
    private static bool _wireframe;

    private static double _fpsElapsed;
    private static int _fpsFrames;
    private static double _fps;

    /// <summary>
    /// 三角形の頂点。Day 13 とまったく同じデータ。
    /// [x, y, r, g, b] を交互に並べた形(AoS)。
    /// </summary>
    private static readonly float[] TriangleVertices =
    [
        //   x      y        r     g     b
         0.0f,  0.6f,    1.0f, 0.2f, 0.2f,   // 上   : 赤
        -0.6f, -0.5f,    0.2f, 1.0f, 0.2f,   // 左下 : 緑
         0.6f, -0.5f,    0.2f, 0.2f, 1.0f,   // 右下 : 青
    ];

    private static void Main()
    {
        // WindowOptions は struct。with 式で既定値から必要な項目だけ差し替える。
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(Width, Height),
            Title = "Day14 - Silk.NET へ移行",

            // **Day 12 で 300行かけてやったことが、この1行**。
            // コアプロファイルの 3.3 を要求する。ダミーウィンドウも
            // wglCreateContextAttribsARB の取得も、Silk.NET が中でやってくれる。
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),

            // Day 12 の wglSwapIntervalEXT に相当。実行中に切り替えられる。
            VSync = true,

            // サイズ変更を禁止する。Day 11 で WS_THICKFRAME を落としたのと同じ意図。
            WindowBorder = WindowBorder.Fixed,
        };

        _window = Window.Create(options);

        // Silk.NET はイベント駆動。自分でメッセージループを回すのではなく、
        // 「このタイミングで呼んでくれ」と登録して Run() に制御を渡す。
        // Day 11 で書いた PeekMessage のループは Run() の中にある。
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;

        _window.Run();

        // Run() を抜けた後に呼ぶ。ウィンドウ本体の解放。
        _window.Dispose();
    }

    /// <summary>
    /// ウィンドウとコンテキストが出来た直後に1回呼ばれる。
    /// **GL の関数はここより前には使えない**(コンテキストがまだ無い)。
    /// </summary>
    private static void OnLoad()
    {
        // GL の関数テーブルを取る。Day 12 で自作した
        // 「wglGetProcAddress と opengl32.dll の2段構え」がこの1行に畳まれている。
        _gl = GL.GetApi(_window);

        _input = _window.CreateInput();
        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
        }

        Console.WriteLine($"GL_RENDERER : {_gl.GetStringS(StringName.Renderer)}");
        Console.WriteLine($"GL_VERSION  : {_gl.GetStringS(StringName.Version)}");
        Console.WriteLine($"GLSL        : {_gl.GetStringS(StringName.ShadingLanguageVersion)}");
        Console.WriteLine();

        string shaderDirectory = ResolveDirectory("shaders");
        _shader = new Shader(
            _gl,
            Path.Combine(shaderDirectory, "basic.vert"),
            Path.Combine(shaderDirectory, "basic.frag"));

        Console.WriteLine($"シェーダを読み込みました: {shaderDirectory}");
        Console.WriteLine("F5 でシェーダを再読み込みします(実行したまま編集できます)");
        Console.WriteLine();

        CreateTriangle();
    }

    /// <summary>
    /// 頂点データを GPU に置く。**中身は Day 13 と同じ手順**。
    /// VAO(読み方の記録)→ VBO(バイト列)→ 属性の設定、の順。
    ///
    /// 関数名から gl 接頭辞が消え、定数が enum になっただけで、
    /// やっていることは1対1で対応する。
    /// Day 15 でこの手順を <c>Mesh</c> クラスに包む。
    /// </summary>
    private static unsafe void CreateTriangle()
    {
        _vertexArray = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArray);

        _vertexBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);

        // fixed で配列を固定して先頭アドレスを渡す。
        // Day 13 では CLR の自動ピン留めに任せていたが、Silk.NET の API は
        // ポインタを取るので明示的に固定する。unsafe が要るのはこのため。
        fixed (float* data = TriangleVertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(TriangleVertices.Length * sizeof(float)),
                data,
                BufferUsageARB.StaticDraw);
        }

        const uint Stride = 5 * sizeof(float);

        // 属性0: 位置(float 2個、オフセット 0)
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Stride, (void*)0);
        _gl.EnableVertexAttribArray(0);

        // 属性1: 色(float 3個、オフセット 8バイト)
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, Stride, (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>
    /// 更新。描画とは別のコールバックに分かれているのが Silk.NET の作りで、
    /// **Day 19 の固定タイムステップ**(更新は固定間隔、描画は可変)への布石になっている。
    /// 今日はどちらも毎フレーム同じ回数呼ばれる。
    /// </summary>
    private static void OnUpdate(double deltaSeconds)
    {
        _elapsedSeconds += (float)deltaSeconds;

        if (!_paused)
        {
            // 1秒で1ラジアン(約57度)。Day 13 と同じ速さ。
            _angle += (float)deltaSeconds;
        }

        _fpsFrames++;
        _fpsElapsed += deltaSeconds;
        if (_fpsElapsed >= 0.5)
        {
            _fps = _fpsFrames / _fpsElapsed;
            _fpsFrames = 0;
            _fpsElapsed = 0.0;

            _window.Title =
                $"Day14 - Silk.NET へ移行  {_fps:F1} fps | "
                + $"VSync:{(_window.VSync ? "ON" : "OFF")} | {(_wireframe ? "ワイヤー" : "塗り")}"
                + (_paused ? " | 一時停止中" : string.Empty)
                + " | F5:シェーダ再読込 W:ワイヤー V:VSync Space:停止 Esc:終了";
        }
    }

    private static void OnRender(double deltaSeconds)
    {
        _gl.ClearColor(0.10f, 0.11f, 0.13f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        _shader.Use();

        // **System.Numerics を使い始める**(ロードマップでは Phase 4 からだが、
        // 行列を1つ送るだけのために自作するのは本末転倒なのでここから使う)。
        //
        // 掛ける順序に注意。System.Numerics は**行ベクトル規約**なので
        // 「A * B」は「A を適用してから B」の意味になる。
        // ここでは回転してから、アスペクト比の補正で横を縮める。
        // Phase 1 の自作 Mat4(列ベクトル規約)とは順序が逆になる点に注意。
        float aspect = (float)Width / Height;
        Matrix4x4 transform =
            Matrix4x4.CreateRotationZ(_angle)
            * Matrix4x4.CreateScale(1.0f / aspect, 1.0f, 1.0f);

        _shader.SetMatrix4("uTransform", transform);
        _shader.SetFloat("uTime", _elapsedSeconds);

        _gl.BindVertexArray(_vertexArray);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private static void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        switch (key)
        {
            case Key.Escape:
                _window.Close();
                break;

            case Key.Space:
                _paused = !_paused;
                break;

            case Key.V:
                // Day 12 の wglSwapIntervalEXT に相当。プロパティ1つになった。
                _window.VSync = !_window.VSync;
                break;

            case Key.W:
                _wireframe = !_wireframe;
                _gl.PolygonMode(
                    TriangleFace.FrontAndBack,
                    _wireframe ? PolygonMode.Line : PolygonMode.Fill);
                break;

            case Key.F5:
                // **今日の目玉**。アプリを止めずにシェーダを作り直す。
                // shaders/*.frag を書き換えて保存 → F5、で即座に反映される。
                _shader.TryReload();
                break;
        }
    }

    private static void OnClosing()
    {
        // GPU 側のリソースは GC が面倒を見ない。閉じる前に自分で返す。
        _gl.DeleteBuffer(_vertexBuffer);
        _gl.DeleteVertexArray(_vertexArray);
        _shader.Dispose();
        _input.Dispose();
    }

    /// <summary>
    /// 実行ディレクトリから上へ辿って、指定した名前のフォルダを探す。
    ///
    /// <c>bin/Debug/net10.0-windows</c> にコピーしたものを読むのではなく
    /// **ソースツリー側のファイルを直接読む**のが狙い。
    /// そうしないと、シェーダを編集してもビルドし直すまで F5 が効かず、
    /// ホットリロードの意味が無くなる。
    ///
    /// 資産のパスをどう解決するかは Day 21 のリソース管理で正面から扱う。
    /// それまでは Phase 1 の ObjLoader と同じこの手で済ませる。
    /// </summary>
    private static string ResolveDirectory(string name)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, name);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException($"フォルダが見つかりません: {name}");
    }
}
