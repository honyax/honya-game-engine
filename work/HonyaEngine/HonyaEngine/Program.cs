using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace HonyaEngine;

/// <summary>
/// エントリポイント。
///
/// Day 15 までの絵は「画面に貼り付いた2枚の四角形」だった。
/// アスペクト比の補正を <c>Program</c> が直接行列に混ぜ込んでいて、
/// **カメラという概念が存在しなかった**。
///
/// 今日それを <see cref="Camera"/> に切り出し、奥行きのあるシーンを描く。
/// Phase 1(Day 6・Day 7・Day 10)でやったことを GPU 側で再現する回で、
/// 新しい概念はほとんど出てこない。**同じ話が置き換わるだけ**なのを確認するのが目的。
/// </summary>
internal static class Program
{
    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;

    private static Shader _shader = null!;
    private static Texture _texture = null!;

    private static Mesh<Vertex> _cube = null!;
    private static Mesh<Vertex> _quad = null!;

    private static Material _cubeMaterial = null!;
    private static Material _floorMaterial = null!;

    private static Camera _camera = null!;
    private static OrbitCameraController _orbit = null!;

    private static float _angle;
    private static bool _paused;
    private static bool _wireframe;
    private static bool _depthTest = true;
    private static bool _culling = true;
    private static TextureFilter _filter = TextureFilter.Linear;
    private static TextureWrap _wrap = TextureWrap.Repeat;

    private static double _fpsElapsed;
    private static int _fpsFrames;
    private static double _fps;

    /// <summary>
    /// シーンに置く立方体。位置・大きさ・自転の速さだけを持つ。
    ///
    /// 「何を、どこに、どう置くか」がデータになると、
    /// 描画ループは**その一覧をなぞるだけ**になる。
    /// これを本格的に整えたものが Day 22 の GameObject / Day 23 の ECS で、
    /// 今日はその原型を配列で置いてある。
    /// </summary>
    private static readonly (Vector3 Position, float Scale, float Spin)[] Cubes =
    [
        (new Vector3(0.0f, 0.25f, 0.0f), 1.5f, 0.8f),     // 中央。大きめでゆっくり自転
        (new Vector3(3.0f, 0.0f, 3.0f), 1.0f, 0.0f),      // 手前右。この上に1つ積む
        (new Vector3(3.0f, 1.0f, 3.0f), 1.0f, 0.5f),      // 積んだぶん(深度の前後関係が見える)
        (new Vector3(-3.0f, 0.0f, 3.0f), 1.0f, -0.4f),
        (new Vector3(3.0f, 0.0f, -3.0f), 1.0f, 0.3f),
        (new Vector3(-3.0f, 0.0f, -3.0f), 1.0f, 0.0f),
    ];

    private static void Main()
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(960, 640),
            Title = "Day16 - カメラと3Dシーン",
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),
            VSync = true,

            // 深度バッファを要求する。Silk.NET の既定でも 24bit が付いてくるが、
            // **3D を描くのに何が要るか**を明示しておく意味で書く。
            // Phase 1 では自分で float の二次元配列を確保していたもの(Day 7)が、
            // ここではウィンドウ生成時の1行になる。
            PreferredDepthBufferBits = 24,

            // Day 15 までは WindowBorder.Fixed で固定していた。
            // 今日からアスペクト比はカメラの持ち物になったので、
            // リサイズされても正しく追従できる(OnResize を参照)。
            WindowBorder = WindowBorder.Resizable,
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += OnFramebufferResize;
        _window.Closing += OnClosing;
        _window.Run();
        _window.Dispose();
    }

    private static void OnLoad()
    {
        _gl = GL.GetApi(_window);

        _input = _window.CreateInput();
        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
        }

        Console.WriteLine($"GL_RENDERER : {_gl.GetStringS(StringName.Renderer)}");
        Console.WriteLine($"GL_VERSION  : {_gl.GetStringS(StringName.Version)}");
        Console.WriteLine();

        // --- 3D を描くための2つのスイッチ ---

        // 深度テスト。**既定では無効**なので、これを忘れると
        // 「あとから描いたものが手前に来る」という Day 7 以前の絵になる。
        // 3D で最初に踏むバグの筆頭。
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);   // 既定値。「小さい = 手前」を明示しておく

        // 背面カリング。Day 10 で自作したものと同じ判定を GPU がやる。
        // 閉じた立体では裏面は必ず表面に隠れるので、描く前に捨ててよい。
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);   // 既定値。反時計回りが表

        // --- シェーダ・テクスチャ・メッシュ ---
        string shaderDirectory = ResolveDirectory("shaders");
        _shader = new Shader(
            _gl,
            Path.Combine(shaderDirectory, "textured.vert"),
            Path.Combine(shaderDirectory, "textured.frag"));

        _texture = Texture.FromFile(_gl, ResolveAssetPath("textures/uv-test.png"));

        _cube = Primitives.CreateCube(_gl);
        _quad = Primitives.CreateQuad(_gl);

        _cubeMaterial = new Material(_shader)
        {
            MainTexture = _texture,
            Tint = Vector4.One,
            UvScale = Vector2.One,
        };

        _floorMaterial = new Material(_shader)
        {
            MainTexture = _texture,
            Tint = new Vector4(0.45f, 0.50f, 0.60f, 1.0f),   // 暗く落として主役を立たせる
            UvScale = new Vector2(10.0f, 10.0f),             // 1枚のテクスチャを10x10 に敷き詰める
        };

        // --- カメラ ---
        _camera = new Camera
        {
            FieldOfView = MathF.PI / 3.0f,   // 60度
            NearPlane = 0.1f,
            FarPlane = 100.0f,
            AspectRatio = (float)_window.FramebufferSize.X / _window.FramebufferSize.Y,
        };

        _orbit = new OrbitCameraController(_camera);
        foreach (IMouse mouse in _input.Mice)
        {
            _orbit.Attach(mouse);
        }

        Console.WriteLine("左ドラッグ:回転  ホイール:ズーム  Home:カメラリセット");
        Console.WriteLine("Z:深度テスト  C:背面カリング  P:透視/平行  W:ワイヤー");
        Console.WriteLine("F:フィルタ  R:ラップ  V:VSync  Space:自転停止  F5:シェーダ再読込  Esc:終了");
        Console.WriteLine();
    }

    /// <summary>
    /// ウィンドウの大きさが変わったとき。
    ///
    /// やることは2つで、**どちらを忘れても絵が歪む**。
    ///   1. ビューポート … クリップ座標 → ピクセルの対応。これが古いと描画範囲がずれる
    ///   2. カメラのアスペクト比 … これが古いと絵が縦長・横長に潰れる
    ///
    /// <c>Resize</c> ではなく <c>FramebufferResize</c> を使うのは、
    /// 高DPI環境で**ウィンドウのサイズ(論理ピクセル)と描画先のサイズ(物理ピクセル)が
    /// 一致しない**ため。ビューポートは物理ピクセルで指定する。
    /// </summary>
    private static void OnFramebufferResize(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0)
        {
            return;   // 最小化するとゼロが来る。そのまま割ると NaN になる
        }

        _gl.Viewport(size);
        _camera.AspectRatio = (float)size.X / size.Y;
    }

    private static void OnUpdate(double deltaSeconds)
    {
        if (!_paused)
        {
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
                $"Day16 - カメラと3Dシーン  {_fps:F1} fps | {_camera.Mode} | "
                + $"深度:{(_depthTest ? "ON" : "OFF")} カリング:{(_culling ? "ON" : "OFF")} | "
                + $"距離:{_orbit.Distance:F1}";
        }
    }

    private static void OnRender(double deltaSeconds)
    {
        _gl.ClearColor(0.08f, 0.09f, 0.12f, 1.0f);

        // **深度バッファも毎フレーム消す**。色だけ消して深度を残すと、
        // 前のフレームの奥行きが判定に効いて、絵が虫食いになる。
        // Phase 1 で毎フレーム深度バッファを float.MaxValue で埋めていたのと同じ作業。
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // --- フレームに1回だけ送る uniform ---
        // uniform はプログラムに紐づく状態なので、Material.Apply() が
        // glUseProgram を呼び直しても、ここで入れた値は残り続ける。
        // だからオブジェクトの数だけ送り直す必要が無い。
        _shader.Use();
        _shader.SetMatrix4("uViewProjection", _camera.ViewProjection);

        // --- 床 ---
        // XY 平面の四角形を X 軸まわりに -90 度回して寝かせ、20x20 に広げる。
        Matrix4x4 floorModel =
            Matrix4x4.CreateScale(20.0f)
            * Matrix4x4.CreateRotationX(-MathF.PI / 2.0f)
            * Matrix4x4.CreateTranslation(0.0f, -0.5f, 0.0f);
        Draw(_quad, _floorMaterial, floorModel);

        // --- 立方体 ---
        foreach ((Vector3 position, float scale, float spin) in Cubes)
        {
            Matrix4x4 model =
                Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateRotationY(_angle * spin)
                * Matrix4x4.CreateTranslation(position);
            Draw(_cube, _cubeMaterial, model);
        }
    }

    /// <summary>
    /// 1つ描く。**マテリアルを適用 → モデル行列を送る → メッシュを描く**の3手。
    /// Day 15 と形は同じで、送る行列が「全部入りの1本」から
    /// 「モデル行列だけ」に減っている(残りはフレーム頭で送り済み)。
    /// </summary>
    private static void Draw(Mesh<Vertex> mesh, Material material, Matrix4x4 model)
    {
        material.Apply();
        material.Shader.SetMatrix4("uModel", model);
        mesh.Draw();
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
                _window.VSync = !_window.VSync;
                break;

            case Key.Home:
                _orbit.Reset();
                break;

            case Key.Z:
                // 切ると、あとから描いたものが無条件に手前に来る。
                // 床を最初に描いているので、床が立方体を突き抜けて見えるようになる。
                // 「描く順で解決する」のが破綻することの実演(Day 7 の動機)。
                _depthTest = !_depthTest;
                SetCap(EnableCap.DepthTest, _depthTest);
                break;

            case Key.C:
                // 切っても見た目はほとんど変わらない(閉じた立体なので裏面は隠れる)。
                // 変わるのは**描く三角形の数**で、立方体なら約半分が無駄になる。
                // ワイヤーフレーム(W)と併用すると、裏側の線が出るので違いが分かる。
                _culling = !_culling;
                SetCap(EnableCap.CullFace, _culling);
                break;

            case Key.P:
                _camera.Mode = _camera.Mode == ProjectionMode.Perspective
                    ? ProjectionMode.Orthographic
                    : ProjectionMode.Perspective;
                break;

            case Key.W:
                _wireframe = !_wireframe;
                _gl.PolygonMode(
                    TriangleFace.FrontAndBack,
                    _wireframe ? PolygonMode.Line : PolygonMode.Fill);
                break;

            case Key.F:
                _filter = _filter == TextureFilter.Linear ? TextureFilter.Nearest : TextureFilter.Linear;
                _texture.SetFilter(_filter);
                break;

            case Key.R:
                _wrap = _wrap == TextureWrap.Repeat ? TextureWrap.ClampToEdge : TextureWrap.Repeat;
                _texture.SetWrap(_wrap);
                break;

            case Key.F5:
                _shader.TryReload();
                break;
        }
    }

    private static void SetCap(EnableCap cap, bool enabled)
    {
        if (enabled)
        {
            _gl.Enable(cap);
        }
        else
        {
            _gl.Disable(cap);
        }
    }

    private static void OnClosing()
    {
        _orbit.Detach();
        _cube.Dispose();
        _quad.Dispose();
        _texture.Dispose();
        _shader.Dispose();
        _input.Dispose();
    }

    /// <summary>実行ディレクトリから上へ辿ってフォルダを探す(Day 14 と同じ)。</summary>
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

    /// <summary>リポジトリ共有の素材(<c>assets/</c>)を探す(Day 15 と同じ)。</summary>
    private static string ResolveAssetPath(string relativePath)
    {
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

        throw new FileNotFoundException($"素材が見つかりません: assets/{relativePath}");
    }
}
