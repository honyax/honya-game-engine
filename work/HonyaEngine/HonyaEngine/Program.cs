using System.Diagnostics;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace HonyaEngine;

/// <summary>
/// エントリポイント。**Phase 4(エンジンコア)の2日目**。
///
/// Day 19 で作った固定タイムステップのループに、入力を載せる。
/// 入力はイベントで不定期に飛んでくるのに対し、シミュレーションは固定間隔——
/// この**粒度の違い**を <see cref="InputSystem"/> が吸収する。
///
/// 見どころは M キー(記録)と N キー(再生)。
/// 矢印キーで操作したあと N を押すと、**寸分たがわず同じ動きが再現される**。
/// 記録しているのは画面でも座標でもなく、**入力だけ**。
/// Day 19 の決定性がここで配当を返す。
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

    // --- 今日の主役 ---
    private static InputMap _inputMap = null!;
    private static InputSystem _inputSystem = null!;
    private static InputRecorder _recorder = null!;

    // --- プレイヤー(矢印キーで動かす1枚) ---
    private static Vector2 _playerPosition;
    private static Vector2 _playerPreviousPosition;
    private static Vector2 _playerVelocity;
    private static float _playerRotation;
    private static float _playerPreviousRotation;
    private static float _dashCooldown;

    /// <summary>記録を始めた時点のプレイヤーの状態。再生時にここへ巻き戻す。</summary>
    private static (Vector2 Position, Vector2 Velocity, float Rotation, float DashCooldown) _recordStart;

    /// <summary>記録を終えた時点のプレイヤー状態のハッシュ。再生後に突き合わせる。</summary>
    private static ulong _recordEndHash;

    private static GameLoop _loop = null!;

    /// <summary>描画時に前ステップと現ステップを補間するか。OFF にすると素の更新レートが見える。</summary>
    private static bool _interpolate = true;

    /// <summary>1ステップあたりにわざと消費する時間(マイクロ秒)。処理落ちを再現するため。</summary>
    private static int _loadMicroseconds;

    /// <summary>まだ1回も <see cref="OnUpdate"/> が呼ばれていないか。</summary>
    private static bool _firstUpdate = true;

    private static float _angle;

    /// <summary>前ステップの角度。立方体の回転を補間するために持つ。</summary>
    private static float _previousAngle;
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

        /// <summary>
        /// **前ステップの状態**。補間のために持つ(要点3)。
        ///
        /// 固定ステップにすると、描画のタイミングは必ず
        /// 「あるステップと次のステップの間」になる。そこで前後の状態を混ぜるには、
        /// 片方を覚えておく必要がある。**状態を2つ持つ**のが補間の代償。
        /// </summary>
        public Vector2 PreviousPosition;

        public float PreviousRotation;
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
            Title = "Day20 - 入力システム",
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),
            VSync = false,
            PreferredDepthBufferBits = 24,
            WindowBorder = WindowBorder.Resizable,

            // **Silk.NET 自身も固定レートの仕組みを持っている**が、今日は使わない。
            // 0 にすると「上限なし = 呼べるだけ呼ぶ」になり、
            // Update も Render も1フレームに1回ずつ来る。
            // その素のフレーム時間を GameLoop に渡して、自分で固定間隔に畳む。
            // 出来合いを使わないのは、Phase 2 で wgl を自分で叩いたのと同じ理由——
            // **中で何が起きているかを一度見ておく**ため。
            UpdatesPerSecond = 0.0,
            FramesPerSecond = 0.0,
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

        _loop = new GameLoop { FixedDeltaTime = 1.0 / 60.0 };

        // --- 入力 ---
        _inputMap = InputMap.CreateDefault();
        _inputSystem = new InputSystem(_inputMap);
        _recorder = new InputRecorder();

        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            _inputSystem.Attach(keyboard);
        }

        foreach (IMouse mouse in _input.Mice)
        {
            _inputSystem.Attach(mouse);
        }

        // フォーカスを失ったら押しっぱなしを解除する。
        // これが無いと、Alt+Tab で切り替えたあと「ずっと右へ走り続ける」ことになる
        // (KeyUp が来ないまま裏に回るため。InputSystem.Clear のコメント参照)。
        _window.FocusChanged += focused =>
        {
            if (!focused)
            {
                _inputSystem.Clear();
            }
        };

        _playerPosition = new Vector2(_window.FramebufferSize.X * 0.5f, _window.FramebufferSize.Y * 0.5f);
        _playerPreviousPosition = _playerPosition;

        Console.WriteLine();
        Console.WriteLine("矢印キー:移動  X:ダッシュ(押した瞬間)  M:入力を記録/停止  N:再生");
        Console.WriteLine("1/2/3/4:シミュレーション 120/60/20/5Hz   I:補間  L:負荷  K:余剰破棄  Y:決定性チェック");
        Console.WriteLine("A:アトラス  S:ソートモード  B:バッチ  O:オーファニング  G:3D背景");
        Console.WriteLine("PageUp/PageDown:スプライト数 +-1000  左ドラッグ:カメラ  ホイール:ズーム");
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

            // 補間の初期値。1ステップ目が走る前に描画されても飛ばないよう、
            // 現在値と同じにしておく。
            _sprites[i].PreviousPosition = _sprites[i].Position;
            _sprites[i].PreviousRotation = _sprites[i].Rotation;
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

    /// <summary>
    /// フレームごとに1回。**ここではもうシミュレーションを直接動かさない**。
    /// フレーム時間を <see cref="GameLoop"/> に渡すだけで、
    /// 何回 <see cref="FixedUpdate"/> が呼ばれるかはループが決める。
    /// </summary>
    private static void OnUpdate(double deltaSeconds)
    {
        if (_firstUpdate)
        {
            // **起動直後の1フレームは異常に長い**。
            // シェーダのコンパイル、テクスチャの読み込み、アトラスの構築が
            // 丸ごとこのフレームに入っているので、数百ミリ秒になることがある。
            //
            // それをそのままシミュレーションに流すと、開幕でいきなり
            // 数百ミリ秒ぶん進み(そして上限に当たって捨てられ)、
            // 「何もしていないのに処理落ちしている」ように見える。
            // **初期化にかかった時間はゲーム内時間ではない**ので、1回だけ捨てる。
            _firstUpdate = false;
            deltaSeconds = 0.0;
        }

        if (_paused)
        {
            // 止めている間もアキュムレータを進めてしまうと、
            // 再開した瞬間に溜まったぶんが一気に消化されて飛ぶ。
            // **止めるなら時間も渡さない**。
            _loop.Advance(0.0, FixedUpdate);
        }
        else
        {
            _loop.Advance(deltaSeconds, FixedUpdate);
        }

        _fpsFrames++;
        _fpsElapsed += deltaSeconds;
        if (_fpsElapsed >= 0.5)
        {
            _fps = _fpsFrames / _fpsElapsed;
            _fpsFrames = 0;
            _fpsElapsed = 0.0;

            _window.Title =
                $"Day20 - 入力システム  {_fps:F1} fps | "
                + $"sim {1.0 / _loop.FixedDeltaTime:F0}Hz "
                + $"step:{_loop.StepsLastFrame} "
                + $"α:{_loop.Alpha:F2} "
                + $"遅れ:{_loop.Lag * 1000.0:F1}ms "
                + $"捨て:{_loop.DroppedSeconds:F2}s | "
                + $"補間:{OnOff(_interpolate)} 負荷:{_loadMicroseconds}us 破棄:{OnOff(_loop.DropExcess)} | "
                + $"{RecorderLabel()} | "
                + $"スプライト:{_activeSprites} DC:{_drawCalls}";
        }
    }

    /// <summary>
    /// **固定間隔で呼ばれる更新**。<paramref name="dt"/> は常に同じ値。
    ///
    /// ここに書くものと <see cref="OnRender"/> に書くものの線引きが、
    /// 今日いちばん大事な設計判断になる。
    ///   - ゲームの状態を変えるもの(移動、当たり判定、AI、タイマー)→ **こちら**
    ///   - 見せ方だけのもの(補間、カメラの追従、エフェクトの見た目)→ 描画側
    /// 状態を変える処理が描画側に紛れ込むと、その瞬間に決定性が壊れる。
    /// </summary>
    private static void FixedUpdate(float dt)
    {
        // 処理落ちを再現するためのダミー負荷。L キーで切り替える。
        // **本物の重い処理と同じように、フレーム時間を押し上げる**ので、
        // 死のスパイラルの入口が観察できる(Day 19 要点5)。
        BurnCpu(_loadMicroseconds);

        // --- このステップで使う入力を1つに決める ---
        //
        // 再生中は記録した入力、そうでなければ実際の入力。
        // **ここから下は、入力がどこから来たかを一切気にしない**。
        // 差し替えられるのは、入力が InputSnapshot という値に畳まれているから(要点1)。
        InputSnapshot input;

        if (_recorder.Mode == RecorderMode.Replaying)
        {
            if (_recorder.TryReplay(out input))
            {
                _inputSystem.SetCurrent(input);
            }
            else
            {
                // 記録の末尾まで来た。**その場で操作を返す**ので、
                // 再生が終わったステップからもう自分で動かせる。
                FinishReplay();
                input = _inputSystem.BeginStep();
            }
        }
        else
        {
            input = _inputSystem.BeginStep();
            _recorder.Record(input);
        }

        UpdatePlayer(dt, input);

        _previousAngle = _angle;
        _angle += dt;

        UpdateSprites(dt);
    }

    /// <summary>
    /// プレイヤーを1ステップ動かす。
    ///
    /// **入力を読むのはこの関数だけ**で、しかも引数で受け取っている。
    /// グローバルなキーボードの状態を直接見に行かないので、
    /// 記録した入力を流し込むだけで再生になる(要点5)。
    /// </summary>
    private static void UpdatePlayer(float dt, in InputSnapshot input)
    {
        const float acceleration = 2600.0f;
        const float maxSpeed = 480.0f;
        const float friction = 6.0f;
        const float dashSpeed = 1200.0f;
        const float dashInterval = 0.45f;

        _playerPreviousPosition = _playerPosition;
        _playerPreviousRotation = _playerRotation;

        Vector2 axis = input.MoveAxis;
        _playerVelocity += axis * acceleration * dt;

        // ダッシュは**押した瞬間だけ**。Held で書くと押しっぱなしで加速し続ける(要点2)。
        _dashCooldown = MathF.Max(0.0f, _dashCooldown - dt);
        if (input.WasPressed(GameAction.Dash) && _dashCooldown <= 0.0f)
        {
            Vector2 direction = axis != Vector2.Zero
                ? axis
                : (_playerVelocity != Vector2.Zero ? Vector2.Normalize(_playerVelocity) : Vector2.UnitX);

            _playerVelocity += direction * dashSpeed;
            _dashCooldown = dashInterval;
        }

        // 摩擦。速度に比例して減速させる。
        // (1 - friction*dt) は指数減衰の1次近似で、**dt が固定だから安心して使える**。
        // 可変タイムステップだと dt が大きいときに負になって速度が反転する。
        _playerVelocity *= MathF.Max(0.0f, 1.0f - (friction * dt));

        float speed = _playerVelocity.Length();
        if (speed > maxSpeed && _dashCooldown <= 0.0f)
        {
            _playerVelocity = _playerVelocity / speed * maxSpeed;
        }

        _playerPosition += _playerVelocity * dt;

        // 向きではなく速さで回す。向き(atan2)で回すと -π と π をまたいだ瞬間に
        // 補間が画面を1周してしまう(Day 19 要点3の「瞬間移動」と同じ問題)。
        _playerRotation += speed * dt * 0.012f;

        float half = 34.0f;
        float width = _window.FramebufferSize.X;
        float height = _window.FramebufferSize.Y;

        if (_playerPosition.X < half)
        {
            _playerPosition.X = half;
            _playerVelocity.X = MathF.Abs(_playerVelocity.X) * 0.4f;
        }
        else if (_playerPosition.X > width - half)
        {
            _playerPosition.X = width - half;
            _playerVelocity.X = -MathF.Abs(_playerVelocity.X) * 0.4f;
        }

        if (_playerPosition.Y < half)
        {
            _playerPosition.Y = half;
            _playerVelocity.Y = MathF.Abs(_playerVelocity.Y) * 0.4f;
        }
        else if (_playerPosition.Y > height - half)
        {
            _playerPosition.Y = height - half;
            _playerVelocity.Y = -MathF.Abs(_playerVelocity.Y) * 0.4f;
        }
    }

    /// <summary>指定したマイクロ秒だけ CPU を回して時間を潰す。</summary>
    private static void BurnCpu(int microseconds)
    {
        if (microseconds <= 0)
        {
            return;
        }

        // Thread.Sleep ではなくビジーループにする。
        // Sleep は OS のスケジューラ任せで精度が粗く、しかも CPU を明け渡すので
        // 「重い計算をしている」状況の再現にならない。
        long ticks = (long)(microseconds * (Stopwatch.Frequency / 1_000_000.0));
        long start = Stopwatch.GetTimestamp();

        while (Stopwatch.GetTimestamp() - start < ticks)
        {
        }
    }

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string RecorderLabel() => _recorder.Mode switch
    {
        RecorderMode.Recording => $"記録中 {_recorder.Count}",
        RecorderMode.Replaying => $"再生中 {_recorder.PlayHead}/{_recorder.Count}",
        _ => _recorder.Count > 0 ? $"記録 {_recorder.Count}" : "記録なし",
    };

    private static void UpdateSprites(float dt)
        => UpdateSprites(_sprites, _activeSprites, dt, _window.FramebufferSize.X, _window.FramebufferSize.Y);

    /// <summary>
    /// スプライトを1ステップ進める。
    ///
    /// **引数だけで結果が決まる形**にしてある(グローバルな状態も乱数も時刻も読まない)。
    /// こうしておくと、同じ配列と同じ dt を渡せば必ず同じ結果になる。
    /// これが決定性の実体で、<see cref="RunDeterminismCheck"/> はこの性質を確かめている。
    /// </summary>
    private static void UpdateSprites(Sprite[] sprites, int count, float dt, float width, float height)
    {
        for (int i = 0; i < count; i++)
        {
            ref Sprite sprite = ref sprites[i];

            // **動かす前に前の状態を保存する**。ここを忘れると補間が効かない
            // (前と後が同じ値になるので、常に最新の状態が描かれるだけになる)。
            sprite.PreviousPosition = sprite.Position;
            sprite.PreviousRotation = sprite.Rotation;

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
        // 立方体の回転も補間する。**描画は「ステップとステップの間」を映す**。
        float angle = Interpolate(_previousAngle, _angle);

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
                * Matrix4x4.CreateRotationY(angle * spin)
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
                Interpolate(sprite.PreviousPosition, sprite.Position),
                new Vector2(sprite.Size, sprite.Size),
                Interpolate(sprite.PreviousRotation, sprite.Rotation),
                sprite.Color,
                sprite.Layer);
        }

        RenderLayerTest();

        // プレイヤーは一番手前(レイヤー 1.0)。
        // ダッシュのクールダウン中は色を落として、
        // **押した瞬間しか効かないアクション**が視覚的に分かるようにしてある。
        Vector4 playerColor = _dashCooldown > 0.0f
            ? new Vector4(1.00f, 0.55f, 0.30f, 0.95f)
            : new Vector4(1.00f, 0.95f, 0.35f, 1.00f);

        Submit(
            2,   // sprite-star
            Interpolate(_playerPreviousPosition, _playerPosition),
            new Vector2(68.0f, 68.0f),
            Interpolate(_playerPreviousRotation, _playerRotation),
            playerColor,
            1.0f);

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
    /// 入力の記録を始める / 止める(M キー)。
    ///
    /// 開始時にプレイヤーの状態を控え、終了時にハッシュを取る。
    /// **入力列と初期状態がそろえば結果は一意に決まる**はずなので、
    /// 再生後のハッシュがこれと一致するかどうかで確かめられる。
    /// </summary>
    private static void ToggleRecording()
    {
        if (_recorder.Mode == RecorderMode.Recording)
        {
            _recorder.StopRecording();
            _recordEndHash = PlayerChecksum();
            Console.WriteLine(
                $"[記録停止] {_recorder.Count} ステップ "
                + $"({_recorder.Count * _recorder.FixedDeltaTime:F2} 秒ぶん) / 終了時ハッシュ {_recordEndHash:X16}");
            return;
        }

        _recorder.StartRecording(_loop.FixedDeltaTime);
        _recordStart = (_playerPosition, _playerVelocity, _playerRotation, _dashCooldown);
        Console.WriteLine($"[記録開始] {1.0 / _loop.FixedDeltaTime:F0}Hz。矢印キーで動かして、もう一度 M で停止");
    }

    /// <summary>記録した入力を再生する(N キー)。</summary>
    private static void StartReplay()
    {
        if (_recorder.Count == 0)
        {
            Console.WriteLine("[再生] 記録がありません。先に M キーで記録してください");
            return;
        }

        // **記録時と同じ条件に戻す**。ステップ間隔が違うと、
        // 同じ入力列でも別のシミュレーションになる(InputRecorder のコメント参照)。
        if (Math.Abs(_loop.FixedDeltaTime - _recorder.FixedDeltaTime) > 1e-9)
        {
            Console.WriteLine(
                $"[再生] ステップ間隔を記録時の {1.0 / _recorder.FixedDeltaTime:F0}Hz に戻します");
            _loop.FixedDeltaTime = _recorder.FixedDeltaTime;
            _loop.Reset();
        }

        (_playerPosition, _playerVelocity, _playerRotation, _dashCooldown) = _recordStart;
        _playerPreviousPosition = _playerPosition;
        _playerPreviousRotation = _playerRotation;

        // 押しっぱなしのキーが再生に混ざらないように捨てる。
        _inputSystem.Clear();
        _recorder.StartReplaying();

        Console.WriteLine($"[再生開始] {_recorder.Count} ステップ");
    }

    /// <summary>再生が末尾まで来たときの後始末と検証。</summary>
    private static void FinishReplay()
    {
        ulong hash = PlayerChecksum();
        bool match = hash == _recordEndHash;

        Console.WriteLine(
            $"[再生終了] ハッシュ {hash:X16} / 記録時 {_recordEndHash:X16}  {(match ? "一致" : "不一致")}");
        Console.WriteLine(
            match
                ? "  → 入力列だけで同じ結果が再現できた。これがリプレイの原理"
                : "  → 不一致。入力以外の要素(実時間、乱数、描画側の書き込み)が紛れ込んでいる");

        _recorder.Stop();
    }

    /// <summary>プレイヤーの状態のチェックサム。</summary>
    private static ulong PlayerChecksum()
    {
        ulong hash = 14695981039346656037UL;

        Mix(ref hash, BitConverter.SingleToUInt32Bits(_playerPosition.X));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(_playerPosition.Y));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(_playerVelocity.X));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(_playerVelocity.Y));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(_playerRotation));

        return hash;

        static void Mix(ref ulong hash, uint value)
        {
            for (int b = 0; b < 4; b++)
            {
                hash ^= (byte)(value >> (b * 8));
                hash *= 1099511628211UL;
            }
        }
    }

    /// <summary>
    /// **固定タイムステップの本質を確かめる自己チェック**(Y キー)。
    ///
    /// 同じ初期状態から3通りに進めて、結果を突き合わせる。
    ///   A: 固定ステップを600回、まとめて回す
    ///   B: 同じ600回を、**ばらばらのフレーム時間**に分けて <see cref="GameLoop"/> 経由で回す
    ///   C: 可変タイムステップで、同じ実時間ぶん進める
    ///
    /// 期待される結果は **A == B ≠ C**。
    ///   - A == B … フレームの刻み方が違っても、ステップ数が同じなら結果は同じ。
    ///               これが「再現する」ということ
    ///   - B != C … 可変だと、同じ実時間でも結果が変わる。
    ///               リプレイもネットワーク同期も成立しない
    ///
    /// 実際のゲームでは、これがリプレイ、ロールバックネットコード、
    /// タイムアタックの記録検証といった機能の土台になる。
    /// </summary>
    private static void RunDeterminismCheck()
    {
        const int steps = 600;
        float dt = (float)_loop.FixedDeltaTime;
        float width = _window.FramebufferSize.X;
        float height = _window.FramebufferSize.Y;
        int count = Math.Min(_activeSprites, 200);

        // フレーム時間の作り方も固定の種にしておく。
        // **測定条件そのものが再現しないと、比較に意味が無い**。
        var random = new Random(1234);
        double[] frameTimes = new double[steps * 2];
        for (int i = 0; i < frameTimes.Length; i++)
        {
            frameTimes[i] = 0.002 + random.NextDouble() * 0.030;   // 2〜32ms のばらつき
        }

        // A: まとめて600回
        Sprite[] a = Snapshot(count);
        for (int i = 0; i < steps; i++)
        {
            UpdateSprites(a, count, dt, width, height);
        }

        // B: ばらばらのフレームに分けて、GameLoop 経由で600回
        Sprite[] b = Snapshot(count);
        var loop = new GameLoop { FixedDeltaTime = dt, MaxStepsPerFrame = int.MaxValue };
        int done = 0;
        foreach (double frameTime in frameTimes)
        {
            if (done >= steps)
            {
                break;
            }

            loop.Advance(frameTime, _ =>
            {
                if (done < steps)
                {
                    UpdateSprites(b, count, dt, width, height);
                    done++;
                }
            });
        }

        // C: 可変タイムステップ。**同じ実時間**ぶん進める
        Sprite[] c = Snapshot(count);
        double target = steps * (double)dt;
        double elapsed = 0.0;
        foreach (double frameTime in frameTimes)
        {
            if (elapsed >= target)
            {
                break;
            }

            double step = Math.Min(frameTime, target - elapsed);
            UpdateSprites(c, count, (float)step, width, height);
            elapsed += step;
        }

        ulong hashA = Checksum(a, count);
        ulong hashB = Checksum(b, count);
        ulong hashC = Checksum(c, count);

        Console.WriteLine();
        Console.WriteLine($"[決定性チェック] {count} 枚 x {steps} ステップ ({dt * 1000.0:F2}ms/step)");
        Console.WriteLine($"  A 固定・まとめて      : {hashA:X16}");
        Console.WriteLine($"  B 固定・フレーム分割  : {hashB:X16}  {(hashA == hashB ? "一致" : "不一致")}");
        Console.WriteLine($"  C 可変・同じ実時間    : {hashC:X16}  {(hashA == hashC ? "一致" : "不一致")}");
        Console.WriteLine($"  → A==B なら再現性あり。A!=C は可変タイムステップでは再現しないこと");
        Console.WriteLine();
    }

    /// <summary>先頭 <paramref name="count"/> 枚の状態をコピーする。</summary>
    private static Sprite[] Snapshot(int count)
    {
        var copy = new Sprite[count];
        Array.Copy(_sprites, copy, count);
        return copy;
    }

    /// <summary>
    /// 状態のチェックサム。FNV-1a を float のビット表現に対してかける。
    ///
    /// 「だいたい合っている」ではなく**ビット単位で一致するか**を見たいので、
    /// 誤差を許す比較(<c>Math.Abs(a - b) &lt; eps</c>)ではなくハッシュにする。
    /// 決定性は「ほぼ同じ」では意味がなく、1ビットでも違えば
    /// 数千ステップ後には全く別の結果になる。
    /// </summary>
    private static ulong Checksum(Sprite[] sprites, int count)
    {
        ulong hash = 14695981039346656037UL;   // FNV offset basis

        for (int i = 0; i < count; i++)
        {
            ref Sprite sprite = ref sprites[i];
            Mix(ref hash, BitConverter.SingleToUInt32Bits(sprite.Position.X));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(sprite.Position.Y));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(sprite.Rotation));
        }

        return hash;

        static void Mix(ref ulong hash, uint value)
        {
            for (int b = 0; b < 4; b++)
            {
                hash ^= (byte)(value >> (b * 8));
                hash *= 1099511628211UL;       // FNV prime
            }
        }
    }

    /// <summary>
    /// 前ステップと現ステップの間を <see cref="GameLoop.Alpha"/> で混ぜる。
    ///
    /// 補間を切ると α = 1、つまり**常に最新のステップの状態**になる。
    /// これがシミュレーションレートそのままの絵で、
    /// 5Hz にすると1秒に5回しか動かないのが見える。
    ///
    /// 注意: 補間は「前と後を直線で結ぶ」だけなので、
    /// **瞬間移動(ワープ、リスポーン、画面切り替え)には使えない**。
    /// そういう場面では前の状態を現在値で上書きして、補間を1フレーム無効にする。
    /// </summary>
    private static float Interpolate(float previous, float current)
    {
        float alpha = _interpolate ? (float)_loop.Alpha : 1.0f;
        return previous + ((current - previous) * alpha);
    }

    private static Vector2 Interpolate(Vector2 previous, Vector2 current)
    {
        float alpha = _interpolate ? (float)_loop.Alpha : 1.0f;
        return Vector2.Lerp(previous, current, alpha);
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
            case Key.M:
                ToggleRecording();
                break;

            case Key.N:
                StartReplay();
                break;

            // --- Day 19 のスイッチ ---
            case Key.Number1:
                SetSimulationRate(120.0);
                break;

            case Key.Number2:
                SetSimulationRate(60.0);
                break;

            case Key.Number3:
                SetSimulationRate(20.0);
                break;

            case Key.Number4:
                // **ここが今日の見せ場**。5Hz にして I キーで補間を切ると
                // 1秒に5回しか絵が変わらない。補間を戻すと、
                // シミュレーションは 5Hz のままなのに滑らかに見える。
                SetSimulationRate(5.0);
                break;

            case Key.I:
                _interpolate = !_interpolate;
                break;

            case Key.L:
                // 1ステップあたりの負荷を増やしていく。
                // 60Hz では1ステップの予算が 16.67ms なので、
                // **20000us(20ms)を超えると原理的に追いつけない**。
                // そこで初めて「遅れをどう扱うか」(K キー)が意味を持つ。
                _loadMicroseconds = _loadMicroseconds switch
                {
                    0 => 2000,
                    2000 => 8000,
                    8000 => 20000,
                    _ => 0,
                };
                break;

            case Key.Y:
                RunDeterminismCheck();
                break;

            case Key.K:
                // 追いつけなかった時間を捨てるか。
                // OFF にして負荷をかけると、タイトルの「遅れ」が増え続ける。
                _loop.DropExcess = !_loop.DropExcess;
                _loop.Reset();
                break;

            // --- Day 18 までのスイッチ ---
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

            case Key.G:
                _draw3D = !_draw3D;
                break;

            // 矢印キーはプレイヤーの操作に使うので、スプライト数は PageUp/PageDown へ移した。
            case Key.PageUp:
                _activeSprites = Math.Min(_activeSprites + 1000, MaxSprites - LayerTest.Length);
                break;

            case Key.PageDown:
                _activeSprites = Math.Max(_activeSprites - 1000, 0);
                break;

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

    /// <summary>
    /// シミュレーションのレートを変える。
    ///
    /// **溜まっている時間は捨てる**(<see cref="GameLoop.Reset"/>)。
    /// 捨てないと、レートを下げた瞬間に古い間隔ぶんの時間が新しい間隔で消化され、
    /// 一瞬だけ早送りになる。
    /// </summary>
    private static void SetSimulationRate(double hertz)
    {
        _loop.FixedDeltaTime = 1.0 / hertz;
        _loop.Reset();
        Console.WriteLine($"シミュレーション: {hertz:F0}Hz (1ステップ {1000.0 / hertz:F2}ms)");
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
        _inputSystem.Detach();

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
