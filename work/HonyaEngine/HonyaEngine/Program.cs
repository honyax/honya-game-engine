using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace HonyaEngine;

/// <summary>
/// エントリポイント。**Phase 3(レンダラ層)の最終日**。
///
/// Day 17 のスプライトバッチには4つの穴があった。今日はそれを全部埋める。
///   1. テクスチャを混ぜられない       → <see cref="TextureAtlas"/>
///   2. 描画順を制御できない           → <see cref="SpriteSortMode"/>
///   3. 頂点が32バイト                 → <see cref="SpriteVertex"/> を20バイトに
///   4. 切り出しの指定が UV            → <see cref="AtlasRegion"/> がピクセル寸法も持つ
///
/// 見るべきは A キー(アトラス)と S キー(ソートモード)の組み合わせ。
/// **アトラスが無いと、ソートしてもドローコールが減らない場合がある**
/// ことが分かるようにしてある(計画書の要点3)。
/// </summary>
internal static class Program
{
    private const int MaxSprites = 20000;

    /// <summary>アトラスに詰める絵。ファイル名(拡張子なし)がそのままキーになる。</summary>
    private static readonly string[] SpriteNames =
    [
        "sprite-circle",
        "sprite-ring",
        "sprite-star",
        "sprite-diamond",
    ];

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

    // --- 2D ---
    private static Shader _spriteShader = null!;
    private static SpriteBatch _spriteBatch = null!;

    /// <summary>4枚を1枚に詰めたもの。**A キーが ON のときはこちらを使う**。</summary>
    private static TextureAtlas _atlas = null!;

    /// <summary>アトラスの中の各リージョン。</summary>
    private static AtlasRegion[] _regions = null!;

    /// <summary>
    /// 詰めていない、ばらばらのテクスチャ4枚。**アトラスと比べるためだけに持っている**。
    /// 実際のゲームでこう持つ理由は無い。
    /// </summary>
    private static Texture[] _looseTextures = null!;

    private static Sprite[] _sprites = null!;
    private static int _activeSprites = 1000;

    private static float _angle;
    private static bool _paused;
    private static bool _wireframe;
    private static bool _depthTest = true;
    private static bool _culling = true;
    private static bool _draw3D = true;
    private static bool _useAtlas = true;
    private static SpriteSortMode _sortMode = SpriteSortMode.Texture;

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

    private struct Sprite
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Size;
        public float Rotation;
        public float Spin;
        public Vector4 Color;

        /// <summary>どの絵か(<see cref="SpriteNames"/> の添字)。</summary>
        public int Kind;

        /// <summary>重ね順。<see cref="SpriteSortMode.BackToFront"/> のときだけ効く。</summary>
        public float Layer;
    }

    /// <summary>
    /// ソートの効き目を目で見るための、固定配置の3枚。
    ///
    /// **わざとレイヤー順と違う順で積む**。
    ///   Immediate    … 積んだ順(緑 → 赤 → 青)なので、最後の青が一番上
    ///   BackToFront  … レイヤー順(青 → 緑 → 赤)なので、一番手前の赤が上
    ///   Texture      … 同じテクスチャなのでキーが並ぶ。**順序は不定**(要点4)
    /// </summary>
    private static readonly (Vector2 Offset, float Layer, Vector4 Color)[] LayerTest =
    [
        (new Vector2(0.0f, 0.0f), 0.5f, new Vector4(0.35f, 1.00f, 0.45f, 1.0f)),    // 緑・中間
        (new Vector2(44.0f, 22.0f), 0.9f, new Vector4(1.00f, 0.35f, 0.35f, 1.0f)),  // 赤・手前
        (new Vector2(88.0f, 44.0f), 0.1f, new Vector4(0.40f, 0.55f, 1.00f, 1.0f)),  // 青・奥
    ];

    private static void Main()
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(960, 640),
            Title = "Day18 - スプライトバッチ最適化",
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),
            VSync = false,
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

        string[] paths = SpriteNames
            .Select(name => ResolveAssetPath($"textures/{name}.png"))
            .ToArray();

        // アトラス版(絵は4種類だが、テクスチャは1枚)
        _atlas = TextureAtlas.FromFiles(_gl, paths, padding: 4);
        _regions = SpriteNames.Select(name => _atlas[name]).ToArray();

        Console.WriteLine($"アトラス: {_atlas.Width}x{_atlas.Height}、リージョン {_regions.Length} 個");
        foreach (string name in SpriteNames)
        {
            AtlasRegion region = _atlas[name];
            Console.WriteLine(
                $"  {name,-16} {region.Width,3}x{region.Height,-3} "
                + $"UV ({region.UvMin.X:F3}, {region.UvMin.Y:F3})-({region.UvMax.X:F3}, {region.UvMax.Y:F3})");
        }

        // 比較用のばらばら版。
        // **ミップマップを作らない**のはアトラスと条件をそろえるため。
        // これをそろえないと、比べているのがアトラスの効果なのか
        // ミップマップの有無なのか分からなくなる。
        _looseTextures = paths
            .Select(path => Texture.FromFile(_gl, path, generateMipmaps: false))
            .ToArray();
        foreach (Texture texture in _looseTextures)
        {
            texture.SetWrap(TextureWrap.ClampToEdge);
        }

        // 容量を 20000 にして、**2万枚でもフラッシュが起きない**ようにしてある。
        // 並べ替えモードでは、容量を超えるとそこでソートが分断されるため
        // (SpriteBatch.Draw のコメント参照)、
        // 「1フレームで積む最大枚数」を確保しておくのが素直。
        // 20000 × 4頂点 × 20バイト = 1.6MB。積んだ配列と並べ替え後で2本ぶん必要。
        _spriteBatch = new SpriteBatch(_gl, _spriteShader, capacity: MaxSprites);

        InitializeSprites();

        Console.WriteLine();
        Console.WriteLine("A:アトラス  S:ソートモード  B:バッチ  O:オーファニング  3:3D背景");
        Console.WriteLine("上下キー:スプライト数 +-1000  左ドラッグ:カメラ  ホイール:ズーム");
        Console.WriteLine("Z:深度  C:カリング  P:透視/平行  W:ワイヤー  V:VSync  Space:停止  Esc:終了");
        Console.WriteLine();
    }

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

                // **絵の種類を順ぐりに割り当てる**。
                // 隣り合うスプライトが必ず違う絵になるので、
                // アトラスもソートも無い状態が最悪ケースになる。
                Kind = i % SpriteNames.Length,

                // 重ね順はばらばら。BackToFront にすると
                // **テクスチャの並びが完全に崩れる**ので、アトラスの有無が効いてくる。
                Layer = (float)random.NextDouble(),
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
                $"Day18 - スプライトバッチ最適化  {_fps:F1} fps | "
                + $"スプライト:{_activeSprites} | ドローコール:{_drawCalls} | "
                + $"アトラス:{OnOff(_useAtlas)} ソート:{_sortMode} "
                + $"バッチ:{OnOff(_spriteBatch.BatchingEnabled)} "
                + $"オーファン:{OnOff(_spriteBatch.UseOrphaning)} "
                + $"3D:{OnOff(_draw3D)}";
        }
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

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
    /// スプライトを描く。
    ///
    /// Day 17 では**呼び出し側がテクスチャごとにまとめて渡していた**が、
    /// 今日はその工夫が要らなくなっている。ただ順に積むだけ。
    /// まとめる仕事はバッチの中(ソート)と、素材の側(アトラス)に移った。
    /// </summary>
    private static void RenderSprites()
    {
        Matrix4x4 projection = Camera.CreateScreen(
            0.0f, _window.FramebufferSize.X,
            _window.FramebufferSize.Y, 0.0f,
            -1.0f, 1.0f);

        _spriteBatch.Begin(projection, _sortMode);

        for (int i = 0; i < _activeSprites; i++)
        {
            ref Sprite sprite = ref _sprites[i];
            Submit(
                sprite.Kind,
                sprite.Position,
                new Vector2(sprite.Size, sprite.Size),
                sprite.Rotation,
                sprite.Color,
                sprite.Layer);
        }

        RenderLayerTest();

        _spriteBatch.End();
    }

    /// <summary>ソートの効き目を目で見るための3枚(<see cref="LayerTest"/>)。</summary>
    private static void RenderLayerTest()
    {
        var origin = new Vector2(120.0f, 120.0f);

        foreach ((Vector2 offset, float layer, Vector4 color) in LayerTest)
        {
            Submit(0, origin + offset, new Vector2(110.0f, 110.0f), 0.0f, color, layer);
        }
    }

    /// <summary>
    /// 1枚積む。**アトラスを使うかどうかの分岐はここだけ**。
    /// バッチから見れば <see cref="AtlasRegion"/> か <see cref="Texture"/> かの違いしかなく、
    /// どちらでも同じ経路を通る。
    /// </summary>
    private static void Submit(int kind, Vector2 center, Vector2 size, float rotation, Vector4 color, float layer)
    {
        if (_useAtlas)
        {
            _spriteBatch.Draw(_regions[kind], center, size, rotation, color, layer);
        }
        else
        {
            _spriteBatch.Draw(_looseTextures[kind], center, size, rotation, color, layer);
        }
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
            case Key.A:
                _useAtlas = !_useAtlas;
                break;

            case Key.S:
                _sortMode = _sortMode switch
                {
                    SpriteSortMode.Texture => SpriteSortMode.BackToFront,
                    SpriteSortMode.BackToFront => SpriteSortMode.Immediate,
                    _ => SpriteSortMode.Texture,
                };
                break;

            case Key.B:
                _spriteBatch.BatchingEnabled = !_spriteBatch.BatchingEnabled;
                break;

            case Key.O:
                _spriteBatch.UseOrphaning = !_spriteBatch.UseOrphaning;
                break;

            case Key.Number3:
                _draw3D = !_draw3D;
                break;

            case Key.Up:
                _activeSprites = Math.Min(_activeSprites + 1000, MaxSprites - LayerTest.Length);
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
                _atlas.Texture.SetFilter(_filter);
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
        _atlas.Dispose();
        foreach (Texture texture in _looseTextures)
        {
            texture.Dispose();
        }

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
