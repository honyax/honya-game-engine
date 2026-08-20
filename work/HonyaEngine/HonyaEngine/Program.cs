using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace HonyaEngine;

/// <summary>
/// エントリポイント。
///
/// Day 16 の3Dシーンの**上に、スクリーン空間のスプライトを重ねる**。
/// この構成にしたのは、実際のゲームがそうなっているからというだけでなく、
/// 「2D を描くには 3D とは違う状態が要る」ことが同じフレームの中で見えるため
/// (深度テストを切る、ブレンドを入れる、投影行列を差し替える)。
///
/// 今日の主題は<see cref="SpriteBatch"/>で、見るべき数字はタイトルバーの
/// **ドローコール数**。B キーでバッチを切ると、スプライト2000枚で
/// ドローコールが 2000 回に増え、fps がはっきり落ちる。
/// </summary>
internal static class Program
{
    /// <summary>スプライトの上限。実際に描く枚数は Up/Down キーで変える。</summary>
    private const int MaxSprites = 20000;

    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;

    // --- 3D(Day 16 のまま) ---
    private static Shader _shader = null!;
    private static Texture _texture = null!;
    private static Mesh<Vertex> _cube = null!;
    private static Mesh<Vertex> _quad = null!;
    private static Material _cubeMaterial = null!;
    private static Material _floorMaterial = null!;
    private static Camera _camera = null!;
    private static OrbitCameraController _orbit = null!;

    // --- 2D(今日の主題) ---
    private static Shader _spriteShader = null!;
    private static SpriteBatch _spriteBatch = null!;
    private static Texture _circleTexture = null!;
    private static Texture _ringTexture = null!;
    private static Sprite[] _sprites = null!;
    private static int _activeSprites = 1000;

    private static float _angle;
    private static bool _paused;
    private static bool _wireframe;
    private static bool _depthTest = true;
    private static bool _culling = true;
    private static bool _draw3D = true;

    /// <summary>
    /// テクスチャごとにまとめて描くか、配列の順に描くか。
    ///
    /// **バッチが効くかどうかを決めるのはこのフラグ**。
    /// 交互に描くとテクスチャが毎回変わるので、バッチが有効でも
    /// 1枚ごとにフラッシュされ、まとめた意味が消える(要点4)。
    /// これを構造的に解決するのが Day 18 のアトラスとソート。
    /// </summary>
    private static bool _groupByTexture = true;

    private static TextureFilter _filter = TextureFilter.Linear;
    private static TextureWrap _wrap = TextureWrap.Repeat;

    private static double _fpsElapsed;
    private static int _fpsFrames;
    private static double _fps;
    private static int _drawCalls;

    private static readonly (Vector3 Position, float Scale, float Spin)[] Cubes =
    [
        (new Vector3(0.0f, 0.25f, 0.0f), 1.5f, 0.8f),
        (new Vector3(3.0f, 0.0f, 3.0f), 1.0f, 0.0f),
        (new Vector3(3.0f, 1.0f, 3.0f), 1.0f, 0.5f),
        (new Vector3(-3.0f, 0.0f, 3.0f), 1.0f, -0.4f),
        (new Vector3(3.0f, 0.0f, -3.0f), 1.0f, 0.3f),
        (new Vector3(-3.0f, 0.0f, -3.0f), 1.0f, 0.0f),
    ];

    /// <summary>1枚ぶんの状態。**描画に必要な最小限**だけを持たせる。</summary>
    private struct Sprite
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Size;
        public float Rotation;
        public float Spin;
        public Vector4 Color;
    }

    private static void Main()
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(960, 640),
            Title = "Day17 - スプライトバッチ",
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),
            VSync = false,   // **今日は fps を見るので既定で切っておく**(V キーで戻せる)
            PreferredDepthBufferBits = 24,
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

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);

        string shaderDirectory = ResolveDirectory("shaders");

        // --- 3D ---
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
            Tint = new Vector4(0.45f, 0.50f, 0.60f, 1.0f),
            UvScale = new Vector2(10.0f, 10.0f),
        };

        _camera = new Camera
        {
            FieldOfView = MathF.PI / 3.0f,
            NearPlane = 0.1f,
            FarPlane = 100.0f,
            AspectRatio = (float)_window.FramebufferSize.X / _window.FramebufferSize.Y,
        };

        _orbit = new OrbitCameraController(_camera);
        foreach (IMouse mouse in _input.Mice)
        {
            _orbit.Attach(mouse);
        }

        // --- 2D ---
        _spriteShader = new Shader(
            _gl,
            Path.Combine(shaderDirectory, "sprite.vert"),
            Path.Combine(shaderDirectory, "sprite.frag"));

        _circleTexture = Texture.FromFile(_gl, ResolveAssetPath("textures/sprite-circle.png"));
        _ringTexture = Texture.FromFile(_gl, ResolveAssetPath("textures/sprite-ring.png"));

        // スプライトは拡大縮小しかしないので、繰り返しは不要。
        // ClampToEdge にしておかないと、縮小時に反対側の端の色が
        // にじみ込むことがある(バイリニアが 0 と 1 をまたいで読むため)。
        _circleTexture.SetWrap(TextureWrap.ClampToEdge);
        _ringTexture.SetWrap(TextureWrap.ClampToEdge);

        _spriteBatch = new SpriteBatch(_gl, _spriteShader, capacity: 4000);

        InitializeSprites();

        Console.WriteLine("B:バッチ  T:テクスチャごとにまとめる  O:オーファニング  3:3D背景");
        Console.WriteLine("上下キー:スプライト数 +-1000  左ドラッグ:カメラ  ホイール:ズーム");
        Console.WriteLine("Z:深度  C:カリング  P:透視/平行  W:ワイヤー  V:VSync  Space:停止  Esc:終了");
        Console.WriteLine();
    }

    /// <summary>
    /// スプライトの初期状態を作る。
    /// 乱数の種を固定してあるので、**毎回まったく同じ絵**になる。
    /// 性能を比べるときに条件がぶれないので、測定用のデモではこうしておくとよい。
    /// </summary>
    private static void InitializeSprites()
    {
        var random = new Random(20260816);
        _sprites = new Sprite[MaxSprites];

        float width = _window.FramebufferSize.X;
        float height = _window.FramebufferSize.Y;

        for (int i = 0; i < MaxSprites; i++)
        {
            float speed = 60.0f + (float)random.NextDouble() * 120.0f;
            float direction = (float)random.NextDouble() * MathF.Tau;

            _sprites[i] = new Sprite
            {
                Position = new Vector2(
                    (float)random.NextDouble() * width,
                    (float)random.NextDouble() * height),
                Velocity = new Vector2(MathF.Cos(direction), MathF.Sin(direction)) * speed,
                Size = 14.0f + (float)random.NextDouble() * 24.0f,
                Rotation = (float)random.NextDouble() * MathF.Tau,
                Spin = ((float)random.NextDouble() - 0.5f) * 3.0f,
                Color = new Vector4(
                    0.45f + (float)random.NextDouble() * 0.55f,
                    0.45f + (float)random.NextDouble() * 0.55f,
                    0.45f + (float)random.NextDouble() * 0.55f,
                    0.85f),
            };
        }
    }

    private static void OnFramebufferResize(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        _gl.Viewport(size);
        _camera.AspectRatio = (float)size.X / size.Y;
    }

    private static void OnUpdate(double deltaSeconds)
    {
        float dt = (float)deltaSeconds;

        if (!_paused)
        {
            _angle += dt;
            UpdateSprites(dt);
        }

        _fpsFrames++;
        _fpsElapsed += deltaSeconds;
        if (_fpsElapsed >= 0.5)
        {
            _fps = _fpsFrames / _fpsElapsed;
            _fpsFrames = 0;
            _fpsElapsed = 0.0;

            _window.Title =
                $"Day17 - スプライトバッチ  {_fps:F1} fps | "
                + $"スプライト:{_activeSprites} | ドローコール:{_drawCalls} | "
                + $"バッチ:{OnOff(_spriteBatch.BatchingEnabled)} "
                + $"まとめ:{OnOff(_groupByTexture)} "
                + $"オーファン:{OnOff(_spriteBatch.UseOrphaning)} "
                + $"3D:{OnOff(_draw3D)}";
        }
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    /// <summary>
    /// スプライトを動かす。画面の端で跳ね返るだけ。
    ///
    /// **描画とは完全に分かれている**のがポイントで、
    /// ここは GPU のことを何も知らないただの配列処理。
    /// Day 23 の ECS で「データを連続に並べて一括で回す」形にすると、
    /// このループがそのままシステムになる。
    /// </summary>
    private static void UpdateSprites(float dt)
    {
        float width = _window.FramebufferSize.X;
        float height = _window.FramebufferSize.Y;

        for (int i = 0; i < _activeSprites; i++)
        {
            ref Sprite sprite = ref _sprites[i];

            sprite.Position += sprite.Velocity * dt;
            sprite.Rotation += sprite.Spin * dt;

            float half = sprite.Size * 0.5f;

            if (sprite.Position.X < half)
            {
                sprite.Position.X = half;
                sprite.Velocity.X = -sprite.Velocity.X;
            }
            else if (sprite.Position.X > width - half)
            {
                sprite.Position.X = width - half;
                sprite.Velocity.X = -sprite.Velocity.X;
            }

            if (sprite.Position.Y < half)
            {
                sprite.Position.Y = half;
                sprite.Velocity.Y = -sprite.Velocity.Y;
            }
            else if (sprite.Position.Y > height - half)
            {
                sprite.Position.Y = height - half;
                sprite.Velocity.Y = -sprite.Velocity.Y;
            }
        }
    }

    private static void OnRender(double deltaSeconds)
    {
        _gl.ClearColor(0.08f, 0.09f, 0.12f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_draw3D)
        {
            Render3D();
        }

        RenderSprites();

        _drawCalls = _spriteBatch.DrawCallCount;
    }

    private static void Render3D()
    {
        _shader.Use();
        _shader.SetMatrix4("uViewProjection", _camera.ViewProjection);

        Matrix4x4 floorModel =
            Matrix4x4.CreateScale(20.0f)
            * Matrix4x4.CreateRotationX(-MathF.PI / 2.0f)
            * Matrix4x4.CreateTranslation(0.0f, -0.5f, 0.0f);
        Draw(_quad, _floorMaterial, floorModel);

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
    /// スプライトを描く。**Begin と End の間に積むだけ**で、
    /// いつ GPU へ送るかは <see cref="SpriteBatch"/> が決める。
    /// </summary>
    private static void RenderSprites()
    {
        // 投影行列は毎フレーム作り直す。ウィンドウの大きさが変わっても追従するため。
        Matrix4x4 projection = Camera.CreateScreen(
            0.0f, _window.FramebufferSize.X,
            _window.FramebufferSize.Y, 0.0f,
            -1.0f, 1.0f);

        _spriteBatch.Begin(projection);

        if (_groupByTexture)
        {
            // 偶数番と奇数番を分けて回す。**テクスチャの切り替えが1回だけ**になるので、
            // バッチが最大限に効く(ドローコールは容量で決まる数だけ)。
            for (int i = 0; i < _activeSprites; i += 2)
            {
                Submit(i, _circleTexture);
            }

            for (int i = 1; i < _activeSprites; i += 2)
            {
                Submit(i, _ringTexture);
            }
        }
        else
        {
            // 配列の順に描く。テクスチャが1枚ごとに変わるので、
            // **バッチが有効でも1枚ずつフラッシュされる**。
            for (int i = 0; i < _activeSprites; i++)
            {
                Submit(i, (i & 1) == 0 ? _circleTexture : _ringTexture);
            }
        }

        _spriteBatch.End();
    }

    private static void Submit(int index, Texture texture)
    {
        ref Sprite sprite = ref _sprites[index];
        _spriteBatch.Draw(
            texture,
            sprite.Position,
            new Vector2(sprite.Size, sprite.Size),
            sprite.Rotation,
            sprite.Color);
    }

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

            // --- 今日のスイッチ ---
            case Key.B:
                _spriteBatch.BatchingEnabled = !_spriteBatch.BatchingEnabled;
                break;

            case Key.T:
                _groupByTexture = !_groupByTexture;
                break;

            case Key.O:
                _spriteBatch.UseOrphaning = !_spriteBatch.UseOrphaning;
                break;

            case Key.Number3:
                _draw3D = !_draw3D;
                break;

            case Key.Up:
                _activeSprites = Math.Min(_activeSprites + 1000, MaxSprites);
                break;

            case Key.Down:
                _activeSprites = Math.Max(_activeSprites - 1000, 0);
                break;

            // --- Day 16 までのスイッチ ---
            case Key.Home:
                _orbit.Reset();
                break;

            case Key.Z:
                _depthTest = !_depthTest;
                SetCap(EnableCap.DepthTest, _depthTest);
                break;

            case Key.C:
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
                _spriteShader.TryReload();
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

        _spriteBatch.Dispose();
        _circleTexture.Dispose();
        _ringTexture.Dispose();
        _spriteShader.Dispose();

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
