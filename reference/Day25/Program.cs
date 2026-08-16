using System.Diagnostics;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace HonyaEngine;

/// <summary>
/// エントリポイント。**Phase 5(ゲームが作れる状態に)の1日目**。
///
/// Day 22 で GameObject + Component に移したら、2万個の更新が
/// 0.08ms から 1.37ms(17倍)になった。今日はそれを**3通り目**の書き方で埋める。
///
/// J キーで切り替わる3つは、まったく同じ動きをする。
///   1. 構造体の配列   … Day 17 からのやり方。専用コード
///   2. GameObject     … Day 22。1個ぶんがまとまっている
///   3. ECS            … 今日。**同じ種類がまとまっている**
///
/// 3 は 1 の一般化になっている、というのが今日いちばん腑に落ちてほしいところ。
/// 「構造体の配列を種類ごとに並べて、エンティティ番号で串刺しにする」だけで、
/// 専用コードの速さと GameObject の柔軟さを両取りできる。
///
/// プレイヤーと階層の実演は3つのどれでも GameObject のまま。
/// **ECS は全部を置き換えるものではない**——少数で込み入ったものは
/// オブジェクトのほうが書きやすい。
///
/// **Day 24 での変更**: シーンがコードから出た。
/// 起動時に <c>assets/scenes/demo.scene.json</c> を読み、
/// そこに書いてあるとおりに GameObject とコンポーネントを組み立てる。
/// ファイルが無いときだけ <see cref="CreateDemoScene"/> がコードで組む。
///
/// **Phase 4 のマイルストーン**——
/// 「シーンをロードし、コンポーネント付きエンティティが動く」——には Day 24 で到達した。
/// F4 で、コードで組んだシーンと保存して読み直したシーンが
/// **300 ステップ後までビット単位で一致する**ことを確かめられる。
///
/// **Day 25 での変更**: 当たり判定が入った。
/// F6 で衝突デモに切り替わり、円・矩形・回転矩形が飛び交って
/// 当たると色が変わる。F8 で押し戻しを入れると重ならなくなる。
/// タイトルバーに**組の数と判定時間**が出るので、
/// 総当たりが O(n^2) で膨らむ様子がそのまま見える(Day 26 の動機)。
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
        "sprite-box",
    ];

    /// <summary>
    /// 背景のスプライトが使う絵の種類数。
    /// **箱(4番)は衝突デモ専用**なので、背景の巡回からは外してある。
    /// </summary>
    private const int BackgroundSpriteKinds = 4;

    /// <summary>円の絵(<see cref="SpriteNames"/> の添字)。</summary>
    private const int CircleSprite = 0;

    /// <summary>箱の絵。枠が見えるので**重なりが分かる**。</summary>
    private const int BoxSprite = 4;

    /// <summary>
    /// ロードの実演に使う素材。**1枚 1024x1024** で、復号にそれぞれ 6ms 前後かかる。
    /// スプライト用の小さな絵では一瞬で終わってしまい、同期と非同期の差が見えない。
    /// </summary>
    private static readonly string[] DemoTextureNames =
    [
        "ground-grid",
        "wall-brick",
        "wood-planks",
        "metal-plate",
        "stone-tiles",
        "fabric-weave",
    ];

    /// <summary>スプライトの更新を誰がやるか。</summary>
    private enum SpriteBackend
    {
        /// <summary>Day 17 からのやり方。構造体の配列を直接回す</summary>
        StructArray,

        /// <summary>Day 22。GameObject + Component</summary>
        GameObject,

        /// <summary>Day 23。エンティティ番号 + 種類ごとの配列</summary>
        Ecs,
    }

    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;

    /// <summary>今日の主役。すべてのリソースはここを通して出入りする。</summary>
    private static ResourceManager _resources = null!;

    // --- 3D(参照ではなくハンドルを持つようになった) ---
    private static Handle<Shader> _shader;
    private static Handle<Texture> _texture;
    private static Mesh<Vertex> _cube = null!;
    private static Mesh<Vertex> _quad = null!;
    private static Material _cubeMaterial = null!;
    private static Material _floorMaterial = null!;
    private static Camera _camera = null!;
    private static OrbitCameraController _orbit = null!;

    // --- 2D ---
    private static Handle<Shader> _spriteShader;
    private static SpriteBatch _spriteBatch = null!;

    /// <summary>4枚を1枚に詰めたもの。**A キーが ON のときはこちらを使う**。</summary>
    private static TextureAtlas _atlas = null!;

    /// <summary>アトラスの中の各リージョン。</summary>
    private static AtlasRegion[] _regions = null!;

    /// <summary>
    /// 詰めていない、ばらばらのテクスチャ4枚。**アトラスと比べるためだけに持っている**。
    /// 実際のゲームでこう持つ理由は無い。
    /// </summary>
    private static Handle<Texture>[] _looseTextures = null!;

    /// <summary>ロードの実演に使う6枚(1024x1024)。最初は空。</summary>
    private static Handle<Texture>[] _demoTextures = [];

    // --- ロードの計測 ---
    private static bool _watchingLoad;
    private static bool _watchAsync;
    private static double _watchCallMilliseconds;
    private static double _watchElapsed;
    private static double _watchWorstFrameMilliseconds;
    private static double _watchReadyAt = -1.0;

    private static Sprite[] _sprites = null!;
    private static int _activeSprites = 1000;

    // --- 今日の主役 ---
    private static InputMap _inputMap = null!;
    private static InputSystem _inputSystem = null!;
    private static InputRecorder _recorder = null!;

    // --- 今日の主役 ---
    private static Scene _scene = null!;

    /// <summary>矢印キーで動く1枚。**もう Program のフィールドではなく GameObject**。</summary>
    private static PlayerController _player = null!;

    /// <summary>階層の実演に使う根。子・孫がぶら下がっている。</summary>
    private static GameObject _orbitRoot = null!;

    /// <summary>スプライトの更新を誰がやるか(J キー)。</summary>
    private static SpriteBackend _backend = SpriteBackend.StructArray;

    /// <summary>今シーンに入っている跳ね回るスプライトの数。</summary>
    private static int _sceneSpriteCount;

    // --- 今日の主役 ---
    private static World _world = null!;

    /// <summary>ECS 側に入っているスプライトの数。</summary>
    private static int _ecsSpriteCount;

    /// <summary>
    /// 4つのストアが同じ順に並んでいるか。
    /// **並んでいれば添字をそのまま使える**ので、結合の1段が消える(要点4)。
    /// エンティティを作り直したときにだけ確かめる。
    /// </summary>
    private static bool _ecsAligned;

    /// <summary>1ステップぶんの更新にかかった時間(ミリ秒)の移動平均。</summary>
    private static double _updateMilliseconds;

    // --- 今日の主役: 当たり判定のデモ ---

    /// <summary>衝突デモを動かしているか(F6)。</summary>
    private static bool _collisionDemo;

    /// <summary>めり込みを押し戻すか(F8)。切ると判定だけして重なったままになる。</summary>
    private static bool _resolveOverlap = true;

    /// <summary>形の組み合わせ(F7)。0 = 混在 / 1 = 円だけ / 2 = 矩形だけ / 3 = 回転矩形だけ。</summary>
    private static int _shapeMix;

    private static Body[] _bodies = [];
    private static int _activeBodies = 120;

    /// <summary>直前のステップで試した組の数。**n(n-1)/2 になる**。</summary>
    private static long _pairTests;

    /// <summary>直前のステップで当たっていた組の数。</summary>
    private static int _contacts;

    /// <summary>当たり判定にかかった時間(ミリ秒)の移動平均。</summary>
    private static double _collisionMilliseconds;

    /// <summary>記録を始めた時点のプレイヤーの状態。再生時にここへ巻き戻す。</summary>
    private static (Vector2 Position, Vector2 Velocity, float Angle, float DashCooldown) _recordStart;

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

    /// <summary>当たり判定に使う形の種類。</summary>
    private enum BodyShape
    {
        Circle,
        Box,
        RotatedBox,
    }

    /// <summary>
    /// 衝突デモで飛び回る1体。
    ///
    /// **形の種類を enum で持ち、判定のときに分岐する**という素朴な作りにしてある。
    /// 実際のエンジンは仮想関数や、形ごとに別の配列(Day 23 の ECS 的な持ち方)を使うが、
    /// まずは「形の組み合わせごとに関数が要る」ことを目で見るのが先。
    /// </summary>
    private struct Body
    {
        public Vector2 Position;
        public Vector2 Velocity;

        /// <summary>矩形なら半径ベクトル。円なら X を半径として使う。</summary>
        public Vector2 HalfSize;

        public float Rotation;
        public float Spin;
        public BodyShape Shape;

        /// <summary>このステップで当たった相手の数。色に使う。</summary>
        public int Contacts;
    }

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
            Title = "Day25 - 2D衝突判定",
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

        // **今日からリソースは全部ここを通る**。
        // 直接 new / FromFile を呼ぶ場所が残っていると、そのぶんだけ
        // 「誰が持っているか分からないもの」が生き残る。
        _resources = new ResourceManager(_gl);

        // --- 3D ---
        _shader = _resources.LoadShader(
            Path.Combine(shaderDirectory, "textured.vert"),
            Path.Combine(shaderDirectory, "textured.frag"));

        _texture = _resources.LoadTexture(ResolveAssetPath("textures/uv-test.png"));
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
        _spriteShader = _resources.LoadShader(
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
            .Select(path => _resources.LoadTexture(path, generateMipmaps: false))
            .ToArray();
        foreach (Handle<Texture> handle in _looseTextures)
        {
            _resources.GetTexture(handle).SetWrap(TextureWrap.ClampToEdge);
        }

        // 容量を 20000 にして、**2万枚でもフラッシュが起きない**ようにしてある。
        // 並べ替えモードでは、容量を超えるとそこでソートが分断されるため
        // (SpriteBatch.Draw のコメント参照)、
        // 「1フレームで積む最大枚数」を確保しておくのが素直。
        // 20000 × 4頂点 × 20バイト = 1.6MB。積んだ配列と並べ替え後で2本ぶん必要。
        _spriteBatch = new SpriteBatch(_gl, _resources.GetShader(_spriteShader), capacity: MaxSprites);

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

        SetupScene();

        Console.WriteLine();
        Console.WriteLine("J:更新方式(構造体配列/GameObject/ECS)  H:ライフサイクルの実演  D:ECS の自己チェック");
        Console.WriteLine("F6:衝突デモ  F7:形の切り替え  F8:押し戻し  F9:衝突判定の自己チェック");
        Console.WriteLine("F2:シーンを保存  F3:読み込み(Shift併用でコードから組み直し)  F4:往復の自己チェック");
        Console.WriteLine("Q:非同期ロード  E:同期ロード  U:アンロード  T:ハンドルの自己チェック");
        Console.WriteLine("矢印キー:移動  X:ダッシュ(押した瞬間)  M:入力を記録/停止  N:再生");
        Console.WriteLine("1/2/3/4:シミュレーション 120/60/20/5Hz   I:補間  L:負荷  K:余剰破棄  Y:決定性チェック");
        Console.WriteLine("A:アトラス  S:ソートモード  B:バッチ  O:オーファニング  G:3D背景");
        Console.WriteLine("PageUp/PageDown:スプライト数 +-1000 (Shift併用で+-10000)  左ドラッグ:カメラ  ホイール:ズーム");
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
                Kind = i % BackgroundSpriteKinds,

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

    /// <summary>
    /// シーンを組み立てる。**Program がやるのはここまで**で、
    /// あとは <see cref="Scene.FixedUpdate"/> が全部回してくれる。
    /// </summary>
    /// <summary>
    /// 起動時のシーンを用意する。**ファイルがあればそれを読む**。
    ///
    /// Day 23 まではここでコードを実行してシーンを組んでいた。
    /// 今日からは組み立て手順がファイルの中にあり、
    /// このメソッドは「どこから読むか」を決めるだけになる。
    /// **エンジンとゲームの境目**がここに引かれた、ということでもある。
    /// </summary>
    private static void SetupScene()
    {
        _world = new World();

        var bounds = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        string? path = TryResolveAssetPath("scenes/demo.scene.json");

        if (path is not null)
        {
            _scene = SceneSerializer.LoadFromFile(path, _world);
            _scene.Bounds = bounds;
            Console.WriteLine($"シーンを読み込みました: {Path.GetFileName(path)}");
        }
        else
        {
            _scene = CreateDemoScene(bounds);
            Console.WriteLine("シーンをコードから組みました(assets/scenes/demo.scene.json が見つかりません)");
        }

        BindSceneObjects();

        Console.WriteLine(
            $"  GameObject {_scene.GameObjectCount} 個 / コンポーネント {_scene.ComponentCount} 個");
    }

    /// <summary>
    /// 読み込んだシーンの中から、Program が名指しで使うものを探して覚える。
    ///
    /// **ここがコードとデータの継ぎ目**。
    /// シーンがファイルになった以上、Program は「Player という名前のものがいる」
    /// くらいのゆるい前提しか置けない。
    /// 名前で探すのは素朴だが、実際のエンジンでも
    /// タグや型で探す仕組み(<c>FindObjectOfType</c> の類)は必ず用意されている。
    /// </summary>
    private static void BindSceneObjects()
    {
        _player = null!;
        _orbitRoot = null!;

        foreach (GameObject gameObject in _scene.GameObjects)
        {
            if (gameObject.GetComponent<PlayerController>() is { } controller)
            {
                _player = controller;
            }

            if (gameObject.Name == "OrbitRoot")
            {
                _orbitRoot = gameObject;
            }
        }

        if (_player is null)
        {
            // ファイルを手で編集してプレイヤーを消してしまった場合の逃げ道。
            // **落とさずに、何が足りないかを言う**。
            Console.WriteLine("[scene] PlayerController が見つかりません。コードから組み直します");
            _scene = CreateDemoScene(_scene.Bounds);
            BindSceneObjects();
            return;
        }

        // スプライトの数え直し。ファイルから読んだぶんも勘定に入れる。
        int sceneSprites = 0;
        foreach (GameObject gameObject in _scene.GameObjects)
        {
            if (gameObject.Name.StartsWith("Sprite", StringComparison.Ordinal))
            {
                sceneSprites++;
            }
        }

        _sceneSpriteCount = sceneSprites;
        _ecsSpriteCount = _world.AliveCount;
        RefreshEcsAlignment();
    }

    /// <summary>
    /// デモのシーンをコードで組む。**ファイルが無いときの後ろ盾**であり、
    /// <c>assets/scenes/demo.scene.json</c> の出どころでもある。
    /// </summary>
    private static Scene CreateDemoScene(Vector2 bounds)
    {
        var scene = new Scene { Bounds = bounds };

        // --- プレイヤー ---
        GameObject player = scene.CreateGameObject("Player");
        player.Transform.LocalPosition = new Vector3(bounds.X * 0.5f, bounds.Y * 0.5f, 0.0f);
        player.Transform.Snapshot();

        SpriteRenderer playerSprite = player.AddComponent<SpriteRenderer>();
        playerSprite.Kind = 2;              // sprite-star
        playerSprite.Size = 68.0f;
        playerSprite.Layer = 1.0f;

        player.AddComponent<PlayerController>();

        // --- 階層の実演 ---
        //
        // 根 → 子3つ → それぞれの孫1つ、という3段の木にする。
        // **子も孫も「親のまわりを回る」としか書いていない**。
        // 根が画面を移動すれば全部ついてくるし、
        // 子が回れば孫はその子を中心に回る。
        // 位置の合成は Transform が引き受けるので、部品側には何も要らない。
        GameObject orbitRoot = scene.CreateGameObject("OrbitRoot");
        orbitRoot.Transform.LocalPosition = new Vector3(770.0f, 170.0f, 0.0f);
        orbitRoot.Transform.Snapshot();

        SpriteRenderer rootSprite = orbitRoot.AddComponent<SpriteRenderer>();
        rootSprite.Kind = 0;
        rootSprite.Size = 72.0f;
        rootSprite.Color = new Vector4(1.00f, 0.80f, 0.15f, 1.0f);
        rootSprite.Layer = 0.95f;

        for (int i = 0; i < 3; i++)
        {
            GameObject child = scene.CreateGameObject($"Orbit{i}", orbitRoot.Transform);

            SpriteRenderer childSprite = child.AddComponent<SpriteRenderer>();
            childSprite.Kind = 1;
            childSprite.Size = 46.0f;
            childSprite.Color = new Vector4(0.20f, 0.75f, 1.00f, 1.0f);
            childSprite.Layer = 0.94f;

            OrbitMover childOrbit = child.AddComponent<OrbitMover>();
            childOrbit.Radius = 86.0f;
            childOrbit.AngularSpeed = 1.1f;
            childOrbit.StartAngle = i * MathF.Tau / 3.0f;

            GameObject grandChild = scene.CreateGameObject($"Orbit{i}-moon", child.Transform);

            SpriteRenderer moonSprite = grandChild.AddComponent<SpriteRenderer>();
            moonSprite.Kind = 3;
            moonSprite.Size = 26.0f;
            moonSprite.Color = new Vector4(1.00f, 0.30f, 0.60f, 1.0f);
            moonSprite.Layer = 0.93f;

            OrbitMover moonOrbit = grandChild.AddComponent<OrbitMover>();
            moonOrbit.Radius = 32.0f;
            moonOrbit.AngularSpeed = -3.4f;
            moonOrbit.StartAngle = i * 1.7f;
        }

        return scene;
    }

    /// <summary>
    /// 跳ね回るスプライトを <paramref name="count"/> 体ぶんエンティティにする。
    ///
    /// <see cref="EnsureSceneSprites"/> と同じ初期値を <c>_sprites</c> から写す。
    /// **3つの経路がまったく同じ絵から始まる**ことを保証するため。
    /// </summary>
    private static void EnsureEcsSprites(int count)
    {
        if (_ecsSpriteCount == count)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        _world.Clear();

        long before = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < count; i++)
        {
            ref Sprite source = ref _sprites[i];

            Entity entity = _world.CreateEntity();

            // **付ける順番をそろえる**。全員を同じ順で作れば、
            // 4つのストアの密な配列が同じ並びになる(要点4)。
            _world.Add(entity, new Transform2D { Position = source.Position, Rotation = source.Rotation });
            _world.Add(entity, new Previous2D { Position = source.Position, Rotation = source.Rotation });
            _world.Add(entity, new Velocity2D
            {
                Linear = source.Velocity,
                Spin = source.Spin,
                HalfSize = source.Size * 0.5f,
            });
            _world.Add(entity, new Sprite2D
            {
                Kind = source.Kind,
                Size = source.Size,
                Layer = source.Layer,
                Color = source.Color,
            });
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        _ecsSpriteCount = count;
        RefreshEcsAlignment();

        Console.WriteLine(
            $"[ECS] スプライト {count} 体をエンティティ化: {stopwatch.Elapsed.TotalMilliseconds:F1}ms / "
            + $"{allocated / 1024.0:F0}KB ({(count > 0 ? allocated / (double)count : 0.0):F0} バイト/体) / "
            + $"{_world.DescribeStores()} / 並び {(_ecsAligned ? "一致" : "不一致")}");
    }

    /// <summary>
    /// 4つのストアの並びが一致しているか確かめ直す。
    /// **O(n) かかる**ので、エンティティの増減があったときだけ呼ぶ。
    /// </summary>
    private static void RefreshEcsAlignment()
    {
        ComponentStore<Transform2D> transforms = _world.Store<Transform2D>();

        _ecsAligned =
            EcsSystems.AreAligned(transforms, _world.Store<Previous2D>())
            && EcsSystems.AreAligned(transforms, _world.Store<Velocity2D>())
            && EcsSystems.AreAligned(transforms, _world.Store<Sprite2D>());
    }

    /// <summary>
    /// 跳ね回るスプライトを <paramref name="count"/> 個ぶん GameObject にする。
    ///
    /// 初期値は <c>_sprites</c>(構造体の配列)からそのまま写す。
    /// **同じ乱数から同じ値を作り直すのではなく、同じ配列を写す**ことで、
    /// 2つの経路がまったく同じ絵から始まることを保証している。
    /// </summary>
    private static void EnsureSceneSprites(int count)
    {
        if (_sceneSpriteCount == count)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // いったん全部消してから作り直す。差分で増減させたほうが速いが、
        // ここは「作る・壊す」のコストを見たい場所でもあるので素直に書く。
        foreach (GameObject gameObject in _scene.GameObjects)
        {
            if (gameObject.Name.StartsWith("Sprite", StringComparison.Ordinal))
            {
                _scene.Destroy(gameObject);
            }
        }

        _scene.FixedUpdate(0.0f);   // 破棄の予約をここで消化する

        long before = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < count; i++)
        {
            ref Sprite source = ref _sprites[i];

            GameObject gameObject = _scene.CreateGameObject("Sprite");
            gameObject.Transform.LocalPosition = new Vector3(source.Position.X, source.Position.Y, 0.0f);
            gameObject.Transform.SetLocalRotationZ(source.Rotation);
            gameObject.Transform.Snapshot();

            SpriteRenderer renderer = gameObject.AddComponent<SpriteRenderer>();
            renderer.Kind = source.Kind;
            renderer.Size = source.Size;
            renderer.Color = source.Color;
            renderer.Layer = source.Layer;

            BouncingMover mover = gameObject.AddComponent<BouncingMover>();
            mover.Velocity = source.Velocity;
            mover.SpinSpeed = source.Spin;
        }

        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        _sceneSpriteCount = count;

        Console.WriteLine(
            $"[シーン] スプライト {count} 個を GameObject 化: {stopwatch.Elapsed.TotalMilliseconds:F1}ms / "
            + $"{allocated / 1024.0:F0}KB ({(count > 0 ? allocated / (double)count : 0.0):F0} バイト/個) / "
            + $"合計 GameObject {_scene.GameObjectCount} 個、コンポーネント {_scene.ComponentCount} 個");
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

        UpdateLoadWatch(deltaSeconds);

        _fpsFrames++;
        _fpsElapsed += deltaSeconds;
        if (_fpsElapsed >= 0.5)
        {
            _fps = _fpsFrames / _fpsElapsed;
            _fpsFrames = 0;
            _fpsElapsed = 0.0;

            _window.Title =
                $"Day25  {_fps:F1} fps | "

                // 今日いちばん見たい2つを前に出す。タイトルバーは思ったより早く切れる。
                + $"{BackendLabel()} 更新:{_updateMilliseconds:F2}ms "
                + $"GO:{_scene.GameObjectCount} E:{_world.AliveCount} | "
                + (_collisionDemo
                    ? $"衝突:{_activeBodies}体 組:{_pairTests:N0} 接触:{_contacts} "
                        + $"判定:{_collisionMilliseconds:F2}ms 形:{ShapeMixLabel()} 押戻:{OnOff(_resolveOverlap)} | "
                    : $"スプライト:{_activeSprites} DC:{_drawCalls} | ")
                + $"sim {1.0 / _loop.FixedDeltaTime:F0}Hz step:{_loop.StepsLastFrame} α:{_loop.Alpha:F2} "
                + $"遅れ:{_loop.Lag * 1000.0:F1}ms | "
                + $"補間:{OnOff(_interpolate)} 負荷:{_loadMicroseconds}us | "
                + $"{RecorderLabel()} | "
                + $"tex:{_resources.TextureCount}/待ち{_resources.PendingCount}";
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

        _previousAngle = _angle;
        _angle += dt;

        // --- ここから下が今日の比較対象 ---
        //
        // 同じ計算を3通りで回す。**測っているのはどれも「1ステップの更新」**。
        //   構造体の配列 … UpdateSprites。連続したメモリを順に舐めるだけ
        //   GameObject   … Scene.FixedUpdate。オブジェクトを辿って仮想呼び出し
        //   ECS          … システムを順に呼ぶ。種類ごとの配列を舐める
        // プレイヤーと階層の実演は、どのモードでも Scene 側にいる。
        var stopwatch = Stopwatch.StartNew();

        var bounds = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        _scene.Input = input;
        _scene.Bounds = bounds;
        _scene.FixedUpdate(dt);

        if (_collisionDemo)
        {
            UpdateBodies(dt, bounds);
        }

        switch (_backend)
        {
            case SpriteBackend.StructArray:
                UpdateSprites(dt);
                break;

            case SpriteBackend.Ecs:
                // **呼ぶ順番をここに書く**のが ECS の作法。
                // 控えてから動かす。逆にすると補間が1ステップぶんずれる。
                EcsSystems.Snapshot(_world, _ecsAligned);
                EcsSystems.Move(_world, dt, bounds, _ecsAligned);
                break;

            case SpriteBackend.GameObject:
                // Scene.FixedUpdate が済ませている
                break;
        }

        // 移動平均。1ステップぶんの値はばらつくので、なまして表示する。
        _updateMilliseconds = (_updateMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);
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
        // **描画スレッドでしか GL を呼べない**ので、
        // 裏で復号し終えたぶんの GPU アップロードはここで消化する。
        // 1フレームあたりの枚数を絞ってあるのがミソ(ResourceManager.MaxUploadsPerFrame)。
        _resources.Update();

        _gl.ClearColor(0.08f, 0.09f, 0.12f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_draw3D)
        {
            Render3D();
        }

        RenderSprites();
        _drawCalls = _spriteBatch.DrawCallCount;

        RenderResourceStrip();
    }

    /// <summary>
    /// 画面の下にロード中のテクスチャを並べる。
    ///
    /// **仮の絵(紫の市松)が本物に差し替わる瞬間**を見るための場所。
    /// ここが持っているのはハンドルだけで、何が入っているかは知らない。
    /// 非同期ロードが完了したかどうかを問い合わせるコードすら要らない
    /// ——毎フレームハンドルを解けば、そのとき入っているものが出る。
    /// </summary>
    private static void RenderResourceStrip()
    {
        if (_demoTextures.Length == 0)
        {
            return;
        }

        Matrix4x4 projection = Camera.CreateScreen(
            0.0f, _window.FramebufferSize.X,
            _window.FramebufferSize.Y, 0.0f,
            -1.0f, 1.0f);

        // **別のバッチにして Immediate で描く**。
        // 本編と同じバッチに混ぜると、ソートモードによっては
        // スプライトの海に沈んで見えなくなる。
        // 6種類のテクスチャなので6ドローコールになるが、UI の枚数はたかが知れている。
        _spriteBatch.Begin(projection, SpriteSortMode.Immediate);

        const float size = 116.0f;
        const float gap = 12.0f;
        float total = (_demoTextures.Length * size) + ((_demoTextures.Length - 1) * gap);
        float x = (_window.FramebufferSize.X - total + size) * 0.5f;
        float y = _window.FramebufferSize.Y - (size * 0.5f) - 20.0f;

        foreach (Handle<Texture> handle in _demoTextures)
        {
            // 読めていないものは少し暗く出して、差し替わった瞬間を分かりやすくする。
            float shade = _resources.IsReady(handle) ? 1.0f : 0.65f;

            _spriteBatch.Draw(
                _resources.GetTexture(handle),
                new Vector2(x, y),
                new Vector2(size, size),
                0.0f,
                new Vector4(shade, shade, shade, 1.0f));

            x += size + gap;
        }

        _spriteBatch.End();
    }

    private static void Render3D()
    {
        // 立方体の回転も補間する。**描画は「ステップとステップの間」を映す**。
        float angle = Interpolate(_previousAngle, _angle);

        Shader shader = _resources.GetShader(_shader);
        shader.Use();
        shader.SetMatrix4("uViewProjection", _camera.ViewProjection);

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

        if (_backend == SpriteBackend.StructArray)
        {
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
        }
        else if (_backend == SpriteBackend.Ecs)
        {
            RenderEcsSprites();
        }

        RenderLayerTest();
        RenderScene();

        if (_collisionDemo)
        {
            RenderBodies();
        }

        _spriteBatch.End();
    }

    /// <summary>
    /// シーンを歩いて <see cref="SpriteRenderer"/> を積む。
    ///
    /// **毎フレーム・全オブジェクトぶんに <c>GetComponent</c> が走る**。
    /// <see cref="BouncingMover.Start"/> でやっている「1回引いて持つ」の逆で、
    /// 意図的にそうしてある——GameObject 方式で描画側が背負う典型的な形だから。
    /// 2万個で実測 0.20ms。あらかじめ配列に集めておけば 0.04ms なので、
    /// **0.16ms をこの書き方に払っている**ことになる。
    ///
    /// 実際のエンジンは、描画対象を別のリストに登録しておく
    /// (<c>AddComponent</c> のときにシーンへ通知する)ことでこれを避ける。
    /// Day 23 の ECS は、そのリストを**設計の中心**に据えたもの、とも言える。
    /// </summary>
    private static void RenderScene()
    {
        float alpha = _interpolate ? (float)_loop.Alpha : 1.0f;
        IReadOnlyList<GameObject> gameObjects = _scene.GameObjects;

        for (int i = 0; i < gameObjects.Count; i++)
        {
            GameObject gameObject = gameObjects[i];
            if (!gameObject.ActiveInHierarchy)
            {
                continue;
            }

            SpriteRenderer? renderer = gameObject.GetComponent<SpriteRenderer>();
            if (renderer is null || !renderer.Enabled)
            {
                continue;
            }

            Transform transform = gameObject.Transform;
            Vector3 position = transform.GetInterpolatedWorldPosition(alpha);

            Submit(
                renderer.Kind,
                new Vector2(position.X, position.Y),
                new Vector2(renderer.Size, renderer.Size),
                transform.GetInterpolatedWorldRotationZ(alpha),
                renderer.Color,
                renderer.Layer);
        }
    }

    /// <summary>
    /// ECS のスプライトを積む。
    ///
    /// Day 22 の <see cref="RenderScene"/> と見比べると差が分かりやすい。
    /// あちらは**オブジェクトを1個ずつ辿って <c>GetComponent</c>** していた。
    /// こちらは3本の配列を頭から並走するだけで、
    /// 「絵を持っているか」の判定すら要らない(持っていないものは配列に入っていない)。
    /// </summary>
    private static void RenderEcsSprites()
    {
        ComponentStore<Sprite2D> sprites = _world.Store<Sprite2D>();
        ComponentStore<Transform2D> transforms = _world.Store<Transform2D>();
        ComponentStore<Previous2D> previous = _world.Store<Previous2D>();

        Span<Sprite2D> s = sprites.Values;
        Span<Transform2D> t = transforms.Values;
        Span<Previous2D> p = previous.Values;
        ReadOnlySpan<int> entities = sprites.Entities;

        float alpha = _interpolate ? (float)_loop.Alpha : 1.0f;

        for (int i = 0; i < s.Length; i++)
        {
            // 並びが一致していれば添字がそのまま使える。
            // していなければエンティティ番号を経由する(要点4)。
            int ti = _ecsAligned ? i : transforms.DenseIndexOf(entities[i]);
            int pi = _ecsAligned ? i : previous.DenseIndexOf(entities[i]);
            if (ti < 0 || pi < 0)
            {
                continue;
            }

            Submit(
                s[i].Kind,
                Vector2.Lerp(p[pi].Position, t[ti].Position, alpha),
                new Vector2(s[i].Size, s[i].Size),
                p[pi].Rotation + ((t[ti].Rotation - p[pi].Rotation) * alpha),
                s[i].Color,
                s[i].Layer);
        }
    }

    /// <summary>
    /// 衝突デモの1体を作る。
    ///
    /// 大きさと速度は乱数だが、**種は固定**にしてある。
    /// 当たり判定の不具合は「たまに起きる」形で出ることが多いので、
    /// 同じ配置を何度でも作り直せるようにしておかないと追えない。
    /// </summary>
    private static void InitializeBodies(int count)
    {
        var random = new Random(20260825);
        _bodies = new Body[count];

        float width = _window.FramebufferSize.X;
        float height = _window.FramebufferSize.Y;

        for (int i = 0; i < count; i++)
        {
            // **画面に対して詰め込みすぎない**。
            // 面積の合計が画面の 2 割を超えたあたりから常時ほぼ全員が接触状態になり、
            // 「当たったら赤」の表示が意味をなさなくなる。
            float size = 9.0f + ((float)random.NextDouble() * 12.0f);
            float speed = 40.0f + ((float)random.NextDouble() * 90.0f);
            float direction = (float)random.NextDouble() * MathF.Tau;

            _bodies[i] = new Body
            {
                Position = new Vector2(
                    size + ((float)random.NextDouble() * (width - (size * 2.0f))),
                    size + ((float)random.NextDouble() * (height - (size * 2.0f)))),
                Velocity = new Vector2(MathF.Cos(direction), MathF.Sin(direction)) * speed,
                HalfSize = new Vector2(size, size * (0.6f + ((float)random.NextDouble() * 0.6f))),
                Rotation = (float)random.NextDouble() * MathF.Tau,
                Spin = ((float)random.NextDouble() - 0.5f) * 1.6f,
                Shape = PickShape(i),
            };
        }
    }

    /// <summary>F7 の設定に従って形を決める。</summary>
    private static BodyShape PickShape(int index) => _shapeMix switch
    {
        1 => BodyShape.Circle,
        2 => BodyShape.Box,
        3 => BodyShape.RotatedBox,
        _ => (BodyShape)(index % 3),
    };

    /// <summary>
    /// 衝突デモの1ステップ。**動かして、壁で跳ねて、総当たりで当てる**。
    ///
    /// 総当たりの二重ループがこの日の主役。
    /// 組の数は n(n-1)/2 で、120 体なら 7,140 組、500 体なら 124,750 組。
    /// **体数を2倍にすると判定はほぼ4倍**になる。
    /// タイトルバーの「組」と「判定」を見ながら PageUp を押すと、
    /// O(n^2) が実際にどう効いてくるかが分かる。これが Day 26 の出発点になる。
    /// </summary>
    private static void UpdateBodies(float deltaTime, Vector2 bounds)
    {
        var stopwatch = Stopwatch.StartNew();
        int count = Math.Min(_activeBodies, _bodies.Length);

        // --- 動かす ---
        for (int i = 0; i < count; i++)
        {
            ref Body body = ref _bodies[i];
            body.Contacts = 0;
            body.Position += body.Velocity * deltaTime;
            body.Rotation += body.Spin * deltaTime;

            // 壁。外接 AABB で見るので、回転していてもはみ出さない。
            Vector2 extent = BoundsExtent(body);

            if (body.Position.X < extent.X)
            {
                body.Position.X = extent.X;
                body.Velocity.X = MathF.Abs(body.Velocity.X);
            }
            else if (body.Position.X > bounds.X - extent.X)
            {
                body.Position.X = bounds.X - extent.X;
                body.Velocity.X = -MathF.Abs(body.Velocity.X);
            }

            if (body.Position.Y < extent.Y)
            {
                body.Position.Y = extent.Y;
                body.Velocity.Y = MathF.Abs(body.Velocity.Y);
            }
            else if (body.Position.Y > bounds.Y - extent.Y)
            {
                body.Position.Y = bounds.Y - extent.Y;
                body.Velocity.Y = -MathF.Abs(body.Velocity.Y);
            }
        }

        // --- 総当たり ---
        //
        // j は i+1 から始める。**同じ組を2回試さないため**で、これだけで半分になる。
        // それでも O(n^2) であることは変わらない。
        long pairs = 0;
        int contacts = 0;

        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                pairs++;

                Contact2D contact = Test(in _bodies[i], in _bodies[j]);
                if (!contact.Hit)
                {
                    continue;
                }

                contacts++;
                _bodies[i].Contacts++;
                _bodies[j].Contacts++;

                if (!_resolveOverlap)
                {
                    continue;
                }

                // **半分ずつ押し戻す**。片方だけ動かすと、
                // 壁際で押された側が壁にめり込む。
                // 質量を持たせるなら比率を変えるが、それは Phase 7 の話。
                Vector2 push = contact.Normal * (contact.Depth * 0.5f);
                _bodies[i].Position -= push;
                _bodies[j].Position += push;
            }
        }

        _pairTests = pairs;
        _contacts = contacts;
        _collisionMilliseconds = (_collisionMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);
    }

    /// <summary>
    /// 2体の当たり判定。**形の組み合わせごとに関数を選ぶ**。
    ///
    /// 3種類で6通り。ここを見ると「種類を1つ足すと表が1列増える」のが分かる。
    /// 3D で球・箱・カプセル・平面・地形と増やすと 15 通りになり、
    /// **その表を埋めることが物理エンジンを書くこと**になる(Phase 7)。
    ///
    /// 法線の向きは <see cref="Contact2D"/> の約束どおり
    /// 「a から b へ向かう向き」にそろえてある。
    /// <see cref="Collision2D.Test(in Circle2D, in Aabb2D)"/> のように
    /// 引数の順番が逆になる組み合わせでは、**符号を反転して返す**必要がある。
    /// </summary>
    private static Contact2D Test(in Body a, in Body b)
    {
        switch (a.Shape, b.Shape)
        {
            case (BodyShape.Circle, BodyShape.Circle):
                return Collision2D.Test(ToCircle(a), ToCircle(b));

            case (BodyShape.Box, BodyShape.Box):
                return Collision2D.Test(ToAabb(a), ToAabb(b));

            case (BodyShape.Circle, BodyShape.Box):
                return Collision2D.Test(ToCircle(a), ToAabb(b));

            case (BodyShape.Box, BodyShape.Circle):
                return Flip(Collision2D.Test(ToCircle(b), ToAabb(a)));

            case (BodyShape.Circle, BodyShape.RotatedBox):
                return Collision2D.Test(ToCircle(a), ToObb(b));

            case (BodyShape.RotatedBox, BodyShape.Circle):
                return Flip(Collision2D.Test(ToCircle(b), ToObb(a)));

            default:
                // 残りは全部 OBB 同士に寄せる。
                // **回らない矩形は「回転角 0 の OBB」**なので、SAT でそのまま扱える。
                // 専用の速い経路を持ちつつ、組み合わせの穴は一般形で埋めるのが定石。
                return Collision2D.Test(ToObb(a), ToObb(b));
        }

        static Contact2D Flip(Contact2D contact) =>
            contact.Hit ? Contact2D.Touching(-contact.Normal, contact.Depth) : Contact2D.None;
    }

    private static Circle2D ToCircle(in Body body) => new(body.Position, body.HalfSize.X);

    private static Aabb2D ToAabb(in Body body) => Aabb2D.FromCenter(body.Position, body.HalfSize);

    private static Obb2D ToObb(in Body body) =>
        new(body.Position, body.HalfSize, body.Shape == BodyShape.RotatedBox ? body.Rotation : 0.0f);

    /// <summary>壁に使う「外接する半径」。回転矩形は外接 AABB で見る。</summary>
    private static Vector2 BoundsExtent(in Body body) => body.Shape switch
    {
        BodyShape.Circle => new Vector2(body.HalfSize.X),
        BodyShape.Box => body.HalfSize,
        _ => ToObb(body).Bounds.HalfSize,
    };

    /// <summary>
    /// 衝突デモを描く。**当たっている体は赤くする**。
    ///
    /// 円は円の絵、矩形は枠の見える箱の絵。
    /// 箱の絵に枠を入れてあるのは、**押し戻しを切ったときに重なりが見える**ようにするため。
    /// </summary>
    private static void RenderBodies()
    {
        int count = Math.Min(_activeBodies, _bodies.Length);

        for (int i = 0; i < count; i++)
        {
            ref Body body = ref _bodies[i];

            Vector4 color = body.Contacts > 0
                ? new Vector4(1.00f, 0.30f, 0.28f, 0.95f)
                : new Vector4(0.45f, 0.85f, 1.00f, 0.85f);

            if (body.Shape == BodyShape.Circle)
            {
                Submit(
                    CircleSprite,
                    body.Position,
                    new Vector2(body.HalfSize.X * 2.0f),
                    0.0f,
                    color,
                    0.6f);
            }
            else
            {
                Submit(
                    BoxSprite,
                    body.Position,
                    body.HalfSize * 2.0f,
                    body.Shape == BodyShape.RotatedBox ? body.Rotation : 0.0f,
                    color,
                    0.6f);
            }
        }
    }

    private static string ShapeMixLabel() => _shapeMix switch
    {
        1 => "円",
        2 => "矩形",
        3 => "回転矩形",
        _ => "混在",
    };

    /// <summary>体数を変える。衝突デモが動いていれば作り直す。</summary>
    private static void SetBodyCount(int count)
    {
        _activeBodies = Math.Clamp(count, 0, 2000);
        InitializeBodies(_activeBodies);
        Console.WriteLine(
            $"[collision] {_activeBodies} 体 / 組 {(long)_activeBodies * (_activeBodies - 1) / 2:N0}");
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
        _recordStart = _player.State;
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

        // State の setter が補間の起点までそろえてくれる(PlayerController 参照)。
        _player.State = _recordStart;

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

        (Vector2 position, Vector2 velocity, float angle, _) = _player.State;

        Mix(ref hash, BitConverter.SingleToUInt32Bits(position.X));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(position.Y));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(velocity.X));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(velocity.Y));
        Mix(ref hash, BitConverter.SingleToUInt32Bits(angle));

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
    /// 実演用のテクスチャ6枚を読む。Q = 非同期、E = 同期。
    ///
    /// 読むファイルも枚数もまったく同じで、**違うのは経路だけ**。
    /// 同じ仕事をどこのスレッドでやるかで、体感がここまで変わる。
    /// </summary>
    private static void LoadDemoTextures(bool useAsync)
    {
        if (_demoTextures.Length > 0)
        {
            Console.WriteLine("[ロード] すでに読み込み済み。U キーで解放してから試してください");
            return;
        }

        string[] paths = DemoTextureNames
            .Select(name => ResolveAssetPath($"textures/{name}.png"))
            .ToArray();

        _watchAsync = useAsync;
        _watchingLoad = true;
        _watchElapsed = 0.0;
        _watchWorstFrameMilliseconds = 0.0;
        _watchReadyAt = -1.0;

        // **ここで測っているのは「呼び出しが返ってくるまで」**。
        // 同期ならロード時間そのもの、非同期なら Task を投げる時間しかない。
        var stopwatch = Stopwatch.StartNew();
        _demoTextures = useAsync
            ? paths.Select(path => _resources.LoadTextureAsync(path)).ToArray()
            : paths.Select(path => _resources.LoadTexture(path)).ToArray();
        _watchCallMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        Console.WriteLine();
        Console.WriteLine(
            $"[ロード開始] {(useAsync ? "非同期" : "同期")} {paths.Length} 枚 / "
            + $"呼び出しが返るまで {_watchCallMilliseconds:F1}ms");
    }

    /// <summary>
    /// 実演用のテクスチャを解放する(U キー)。
    ///
    /// **読み込み中に押しても壊れない**のが地味に重要なところ。
    /// スロットは即座に空き、世代が進む。裏で走っている復号は完走するが、
    /// 出来上がったものは <see cref="ResourceManager.Update"/> の生存確認で捨てられる。
    /// 参照を配る設計だと、ここで解放済みのオブジェクトへ書き込むことになる。
    /// </summary>
    private static void UnloadDemoTextures()
    {
        if (_demoTextures.Length == 0)
        {
            Console.WriteLine("[解放] 読み込み済みのものがありません");
            return;
        }

        foreach (Handle<Texture> handle in _demoTextures)
        {
            _resources.Release(handle);
        }

        _demoTextures = [];
        Console.WriteLine($"[解放] 残りテクスチャ {_resources.TextureCount} 件 / 待ち {_resources.PendingCount} 件");
    }

    /// <summary>
    /// ロード前後のフレーム時間を見張って、落ち着いたところで報告する。
    /// </summary>
    private static void UpdateLoadWatch(double deltaSeconds)
    {
        if (!_watchingLoad)
        {
            return;
        }

        _watchElapsed += deltaSeconds;
        _watchWorstFrameMilliseconds = Math.Max(_watchWorstFrameMilliseconds, deltaSeconds * 1000.0);

        if (_watchReadyAt < 0.0 && _resources.PendingCount == 0)
        {
            _watchReadyAt = _watchElapsed;
        }

        // しばらく様子を見てから出す。差し替えは数フレームに分かれて起きるので、
        // 直後に打ち切ると最悪値を取り逃がす。
        if (_watchElapsed < 1.5)
        {
            return;
        }

        _watchingLoad = false;

        Console.WriteLine(
            $"[ロード完了] {(_watchAsync ? "非同期" : "同期")} / "
            + $"呼び出し {_watchCallMilliseconds:F1}ms / "
            + $"全部そろうまで {_watchReadyAt * 1000.0:F0}ms / "
            + $"最悪フレーム {_watchWorstFrameMilliseconds:F1}ms");
        Console.WriteLine(
            _watchWorstFrameMilliseconds > 16.6
                ? "  → 60fps の1フレーム(16.6ms)を超えた。ユーザーには「固まった」と見える"
                : "  → 1フレームの予算に収まっている。読み込んでいることに気づかせない");
    }

    /// <summary>保存先。リポジトリを汚さないよう一時フォルダに置く。</summary>
    private static string SavedScenePath => Path.Combine(Path.GetTempPath(), "honya-scene.json");

    /// <summary>今のシーンをファイルに書き出す(F2)。</summary>
    private static void SaveSceneToFile()
    {
        var stopwatch = Stopwatch.StartNew();
        SceneSerializer.SaveToFile(_scene, _world, SavedScenePath, "saved");
        double elapsed = stopwatch.Elapsed.TotalMilliseconds;

        long size = new FileInfo(SavedScenePath).Length;

        Console.WriteLine();
        Console.WriteLine($"[scene] 保存: {SavedScenePath}");
        Console.WriteLine(
            $"  GameObject {_scene.GameObjectCount} 個 / エンティティ {_world.AliveCount} 体 / "
            + $"{size / 1024.0:F1}KB / {elapsed:F1}ms");

        if (_world.AliveCount > 0)
        {
            Console.WriteLine(
                $"  → エンティティ1体あたり {size / (double)_world.AliveCount:F0} バイト。"
                + "JSON は人が読める代わりに太る(要点7)");
        }
    }

    /// <summary>F2 で保存したファイルを読み込む(F3)。</summary>
    private static void LoadSavedScene()
    {
        if (!File.Exists(SavedScenePath))
        {
            Console.WriteLine($"[scene] {SavedScenePath} がありません。先に F2 で保存してください");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _scene = SceneSerializer.LoadFromFile(SavedScenePath, _world);
        _scene.Bounds = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        BindSceneObjects();
        double elapsed = stopwatch.Elapsed.TotalMilliseconds;

        Console.WriteLine();
        Console.WriteLine(
            $"[scene] 読み込み: GameObject {_scene.GameObjectCount} 個 / "
            + $"エンティティ {_world.AliveCount} 体 / {elapsed:F1}ms");
    }

    /// <summary>
    /// **シリアライズの往復を確かめる自己チェック**(F4)。Phase 4 のマイルストーン。
    ///
    /// 3つ確かめる。
    ///   1. 保存 → 読み込み → 再保存 で、テキストが1バイトも変わらないこと
    ///   2. 数(GameObject とコンポーネント)が合っていること
    ///   3. **300 ステップ動かした結果が一致すること**
    ///
    /// 3 がいちばん大事で、1 と 2 が通っても 3 が落ちることはある
    /// (保存し忘れたフィールドがあると、見た目は同じで動きだけ変わる)。
    /// 「シーンをロードし、コンポーネント付きエンティティが動く」を
    /// 目視ではなく数字で確かめるのがここ。
    /// </summary>
    private static void RunSceneRoundTrip()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("[シーンの往復チェック]");

        var bounds = new Vector2(960.0f, 640.0f);

        Scene coded = CreateDemoScene(bounds);

        // **値が抜けていないか**を確かめるための1体を足しておく。
        // 数も形も合っているのに中身だけ空、という壊れ方が実際にありうる
        // (SceneSerializer の ComponentOptions のコメント参照)。
        // 既定値のままだと「保存できていない」と「たまたま既定値」の区別がつかないので、
        // **わざと変な値**を入れる。
        GameObject probe = coded.CreateGameObject("Probe");
        SpriteRenderer probeSprite = probe.AddComponent<SpriteRenderer>();
        probeSprite.Size = 31.0f;
        probeSprite.Color = new Vector4(0.125f, 0.25f, 0.375f, 0.5f);
        BouncingMover probeMover = probe.AddComponent<BouncingMover>();
        probeMover.Velocity = new Vector2(123.0f, -45.0f);
        probeMover.SpinSpeed = 2.5f;

        // ECS 側も一緒に確かめる。**フィールドで持つ型は書き漏らしやすい**ので、
        // GameObject 側だけ見ていると気づけない(要点5)。
        var codedWorld = new World();
        for (int i = 0; i < 3; i++)
        {
            Entity entity = codedWorld.CreateEntity();
            codedWorld.Add(entity, new Transform2D
            {
                Position = new Vector2(10.0f + i, 20.0f + i),
                Rotation = 0.5f * i,
            });
            codedWorld.Add(entity, new Previous2D());
            codedWorld.Add(entity, new Velocity2D { Linear = new Vector2(-1.5f, 2.5f), Spin = 1.0f, HalfSize = 8.0f });
            codedWorld.Add(entity, new Sprite2D { Kind = i, Size = 12.0f, Layer = 0.5f, Color = Vector4.One });
        }

        string first = SceneSerializer.Save(coded, codedWorld, "roundtrip");

        var loadedWorld = new World();
        Scene loaded = SceneSerializer.Load(first, loadedWorld);
        loaded.Bounds = bounds;
        string second = SceneSerializer.Save(loaded, loadedWorld, "roundtrip");

        Check("保存 → 読み込み → 再保存でテキストが一致", first == second,
            $"{first.Length} バイト / {second.Length} バイト");
        Check("GameObject の数が一致", coded.GameObjectCount == loaded.GameObjectCount,
            $"{coded.GameObjectCount} / {loaded.GameObjectCount}");
        Check("コンポーネントの数が一致", coded.ComponentCount == loaded.ComponentCount,
            $"{coded.ComponentCount} / {loaded.ComponentCount}");

        // 親子が復元できているか。孫までたどれれば、参照の復元は効いている。
        int depth = 0;
        foreach (GameObject gameObject in loaded.GameObjects)
        {
            int d = 0;
            for (Transform? t = gameObject.Transform.Parent; t is not null; t = t.Parent)
            {
                d++;
            }

            depth = Math.Max(depth, d);
        }

        Check("親子の深さが3段(根 → 子 → 孫)", depth == 2, $"実際 {depth + 1} 段");

        GameObject? loadedProbe = null;
        foreach (GameObject gameObject in loaded.GameObjects)
        {
            if (gameObject.Name == "Probe")
            {
                loadedProbe = gameObject;
            }
        }

        Check("目印のオブジェクトが復元されている", loadedProbe is not null);

        if (loadedProbe is not null)
        {
            SpriteRenderer? renderer = loadedProbe.GetComponent<SpriteRenderer>();
            BouncingMover? mover = loadedProbe.GetComponent<BouncingMover>();

            Check("float が保たれている", renderer is not null && renderer.Size == 31.0f);
            Check(
                "Vector4 が保たれている",
                renderer is not null && renderer.Color == probeSprite.Color,
                $"{renderer?.Color}");
            Check(
                "Vector2 が保たれている",
                mover is not null && mover.Velocity == probeMover.Velocity,
                $"{mover?.Velocity}");
        }

        // **本番**。同じ入力で同じだけ回して突き合わせる。
        ulong codedHash = StepAndHash(coded, 300);
        ulong loadedHash = StepAndHash(loaded, 300);

        Check("300 ステップ後の状態が一致", codedHash == loadedHash,
            $"{codedHash:X16} / {loadedHash:X16}");

        Check("エンティティの数が一致", codedWorld.AliveCount == loadedWorld.AliveCount,
            $"{codedWorld.AliveCount} / {loadedWorld.AliveCount}");

        Span<Transform2D> codedTransforms = codedWorld.Store<Transform2D>().Values;
        Span<Transform2D> loadedTransforms = loadedWorld.Store<Transform2D>().Values;
        Span<Velocity2D> loadedVelocities = loadedWorld.Store<Velocity2D>().Values;

        bool ecsValuesMatch = codedTransforms.Length == loadedTransforms.Length;
        for (int i = 0; ecsValuesMatch && i < codedTransforms.Length; i++)
        {
            ecsValuesMatch =
                codedTransforms[i].Position == loadedTransforms[i].Position
                && codedTransforms[i].Rotation == loadedTransforms[i].Rotation
                && loadedVelocities[i].HalfSize == 8.0f;
        }

        Check("ECS コンポーネントの値が保たれている", ecsValuesMatch,
            loadedTransforms.Length > 0 ? $"{loadedTransforms[0].Position}" : "(空)");

        Console.WriteLine(failures == 0
            ? "  すべて合格 — シーンをファイルから復元しても、同じ動きをする"
            : $"  {failures} 件 不合格");
        Console.WriteLine();

        void Check(string name, bool condition, string detail = "")
        {
            Console.WriteLine($"  [{(condition ? "OK" : "NG")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!condition)
            {
                failures++;
            }
        }
    }

    /// <summary>シーンを指定ステップ動かして、全 Transform のハッシュを取る。</summary>
    private static ulong StepAndHash(Scene scene, int steps)
    {
        float dt = (float)_loop.FixedDeltaTime;

        for (int i = 0; i < steps; i++)
        {
            // 入力は空。**再現したいのはシーンの復元であって操作ではない**ので、
            // 外から入る値は固定しておく。
            scene.Input = InputSnapshot.Empty;
            scene.FixedUpdate(dt);
        }

        ulong hash = 14695981039346656037UL;

        foreach (GameObject gameObject in scene.GameObjects)
        {
            Vector3 position = gameObject.Transform.WorldPosition;
            Quaternion rotation = gameObject.Transform.LocalRotation;

            Mix(ref hash, BitConverter.SingleToUInt32Bits(position.X));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(position.Y));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(rotation.Z));
            Mix(ref hash, BitConverter.SingleToUInt32Bits(rotation.W));
        }

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
    /// **当たり判定の自己チェック**(F9)。
    ///
    /// 衝突判定は「見た目で合っていそう」がいちばん当てにならない領域。
    /// 手で計算できる配置を並べて、法線と深さまで含めて突き合わせる。
    /// 特に確かめたいのは3つ。
    ///   - 法線の向きが約束どおり(a から b へ)そろっているか
    ///   - **ちょうど接している**ときにどちらに転ぶか
    ///   - 中心が重なるような退化した配置で NaN が出ないか
    /// </summary>
    private static void RunCollisionCheck()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("[当たり判定の自己チェック]");

        // --- 円同士 ---
        var c1 = new Circle2D(new Vector2(0.0f, 0.0f), 10.0f);
        var c2 = new Circle2D(new Vector2(15.0f, 0.0f), 10.0f);
        Contact2D hit = Collision2D.Test(c1, c2);
        Check("円同士: 当たる", hit.Hit);
        Check("円同士: 深さ 5", Near(hit.Depth, 5.0f), $"{hit.Depth}");
        Check("円同士: 法線は +X", Near(hit.Normal.X, 1.0f) && Near(hit.Normal.Y, 0.0f), $"{hit.Normal}");

        Check("円同士: 離れていれば当たらない",
            !Collision2D.Test(c1, new Circle2D(new Vector2(21.0f, 0.0f), 10.0f)).Hit);

        // **ちょうど接している**。浮動小数点の境界で、実装によって割れるところ。
        // ここでは「接触も当たり」とする(<= で書いてある)。
        Check("円同士: ちょうど接するのは当たり扱い",
            Collision2D.Test(c1, new Circle2D(new Vector2(20.0f, 0.0f), 10.0f)).Hit);

        // 中心が完全に重なる退化ケース。向きが決まらないが、落ちてはいけない。
        Contact2D same = Collision2D.Test(c1, new Circle2D(Vector2.Zero, 10.0f));
        Check("円同士: 中心が重なっても NaN にならない",
            same.Hit && !float.IsNaN(same.Normal.X) && !float.IsNaN(same.Depth), $"{same.Normal} {same.Depth}");

        // --- AABB 同士 ---
        Aabb2D b1 = Aabb2D.FromCenter(Vector2.Zero, new Vector2(10.0f, 10.0f));
        Aabb2D b2 = Aabb2D.FromCenter(new Vector2(16.0f, 4.0f), new Vector2(10.0f, 10.0f));
        Contact2D boxHit = Collision2D.Test(b1, b2);

        // X 方向の重なりは 20-16=4、Y 方向は 20-4=16。**浅いほう(X)で押す**。
        Check("AABB: 浅い軸で押す(X)", Near(boxHit.Depth, 4.0f) && Near(boxHit.Normal.X, 1.0f),
            $"深さ {boxHit.Depth} 法線 {boxHit.Normal}");
        Check("AABB: 角がかすっていなければ当たらない",
            !Collision2D.Test(b1, Aabb2D.FromCenter(new Vector2(21.0f, 21.0f), new Vector2(10.0f))).Hit);

        // --- 円と AABB ---
        // 箱の右辺は x=10。中心 x=14 / 半径 6 なら、最近点までの距離は 4 でめり込みは 2。
        Contact2D mixed = Collision2D.Test(new Circle2D(new Vector2(14.0f, 0.0f), 6.0f), b1);
        Check("円とAABB: 辺に当たる", mixed.Hit && Near(mixed.Depth, 2.0f), $"深さ {mixed.Depth}");
        Check("円とAABB: 法線は円から箱へ(-X)", Near(mixed.Normal.X, -1.0f), $"{mixed.Normal}");
        Check("円とAABB: 届かなければ当たらない",
            !Collision2D.Test(new Circle2D(new Vector2(24.0f, 0.0f), 6.0f), b1).Hit);

        // 円が箱の中に完全に入っている場合。距離が 0 になるので別経路を通る。
        Contact2D inside = Collision2D.Test(new Circle2D(new Vector2(2.0f, 0.0f), 3.0f), b1);
        Check("円とAABB: 円が中にあっても向きが決まる",
            inside.Hit && !float.IsNaN(inside.Normal.X) && MathF.Abs(inside.Normal.X) > 0.5f,
            $"{inside.Normal} 深さ {inside.Depth}");

        // --- OBB 同士(SAT)---
        //
        // 45 度傾けた正方形(半径 10)は、角が中心から 10*sqrt(2) ≒ 14.14 出る。
        // 軸に平行な正方形(半径 10)と X 方向に並べると、
        // 中心距離 24 ではまだ重なり、25 では離れる。
        var square = new Obb2D(Vector2.Zero, new Vector2(10.0f, 10.0f), 0.0f);
        var tilted = new Obb2D(new Vector2(23.0f, 0.0f), new Vector2(10.0f, 10.0f), MathF.PI / 4.0f);

        Contact2D satHit = Collision2D.Test(square, tilted);
        Check("OBB: 45度の角が刺さっているのを検出", satHit.Hit, $"深さ {satHit.Depth:F3}");
        Check("OBB: 法線は +X 寄り", satHit.Normal.X > 0.9f, $"{satHit.Normal}");

        Check("OBB: 離れていれば当たらない",
            !Collision2D.Test(square, new Obb2D(new Vector2(25.0f, 0.0f), new Vector2(10.0f), MathF.PI / 4.0f)).Hit);

        // **回転 0 の OBB は AABB と同じ答えになるはず**。
        // 専用の速い経路と一般形が食い違っていないかの確認。
        var obbA = new Obb2D(Vector2.Zero, new Vector2(10.0f, 10.0f), 0.0f);
        var obbB = new Obb2D(new Vector2(16.0f, 4.0f), new Vector2(10.0f, 10.0f), 0.0f);
        Contact2D viaSat = Collision2D.Test(obbA, obbB);
        Check("OBB(回転0) と AABB の答えが一致",
            Near(viaSat.Depth, boxHit.Depth) && Near(viaSat.Normal.X, boxHit.Normal.X),
            $"SAT 深さ {viaSat.Depth} 法線 {viaSat.Normal}");

        // --- 円と OBB ---
        var rotated = new Obb2D(Vector2.Zero, new Vector2(10.0f, 4.0f), MathF.PI / 2.0f);
        Contact2D circleObb = Collision2D.Test(new Circle2D(new Vector2(0.0f, 12.0f), 3.0f), rotated);

        // 90 度回すと縦横が入れ替わるので、上端は y = 10 になる。円の下端は 9。深さ 1。
        Check("円とOBB: 回転を考慮している", circleObb.Hit && Near(circleObb.Depth, 1.0f),
            $"深さ {circleObb.Depth}");
        Check("円とOBB: 法線は -Y", Near(circleObb.Normal.Y, -1.0f), $"{circleObb.Normal}");

        // --- 押し戻しが本当に離すか ---
        //
        // 判定と押し戻しは別物で、**深さの符号を間違えると近づく**。
        // 1回押し戻した結果、重なりが消えることを確かめておく。
        var moving = new Circle2D(new Vector2(0.0f, 0.0f), 10.0f);
        var fixedCircle = new Circle2D(new Vector2(12.0f, 0.0f), 10.0f);
        Contact2D push = Collision2D.Test(moving, fixedCircle);
        var afterA = new Circle2D(moving.Center - (push.Normal * (push.Depth * 0.5f)), moving.Radius);
        var afterB = new Circle2D(fixedCircle.Center + (push.Normal * (push.Depth * 0.5f)), fixedCircle.Radius);
        Check("押し戻すと重なりが消える", !Collision2D.Test(afterA, afterB).Hit
            || Collision2D.Test(afterA, afterB).Depth < 0.001f);

        Console.WriteLine(failures == 0 ? "  すべて合格" : $"  {failures} 件 不合格");
        Console.WriteLine();

        BenchmarkCollision();

        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.001f;

        void Check(string name, bool condition, string detail = "")
        {
            Console.WriteLine($"  [{(condition ? "OK" : "NG")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!condition)
            {
                failures++;
            }
        }
    }

    /// <summary>
    /// 形ごとの判定コストを測る。**どれを選ぶかの根拠**になる。
    ///
    /// 「円がいちばん安い」は誰でも言うが、
    /// **何倍安いのか**を知らないと設計の判断ができない。
    /// 2倍なら好きな形を使えばよいし、10倍なら円で済ませる工夫をする価値がある。
    /// </summary>
    private static void BenchmarkCollision()
    {
        const int n = 2_000_000;
        var random = new Random(7);

        // 半分くらいが当たる配置にする。**全部外れだと早期脱出ばかり測ることになる**。
        var circles = new Circle2D[64];
        var boxes = new Aabb2D[64];
        var obbs = new Obb2D[64];

        for (int i = 0; i < circles.Length; i++)
        {
            var center = new Vector2(
                (float)random.NextDouble() * 40.0f,
                (float)random.NextDouble() * 40.0f);

            circles[i] = new Circle2D(center, 12.0f);
            boxes[i] = Aabb2D.FromCenter(center, new Vector2(12.0f, 9.0f));
            obbs[i] = new Obb2D(center, new Vector2(12.0f, 9.0f), (float)random.NextDouble() * MathF.Tau);
        }

        Console.WriteLine("### 判定 1 回あたりのコスト ###");

        // **当たったかどうかだけ**の版。法線と深さを出さないぶん安い。
        // 「当たったら消える弾」のように結果しか要らない場面では、こちらを使う。
        Measure("円 と 円 (判定のみ)", i => Collision2D.Overlap(circles[i & 63], circles[(i + 7) & 63]));
        Measure("AABB (判定のみ)   ", i => Collision2D.Overlap(boxes[i & 63], boxes[(i + 7) & 63]));

        Measure("円 と 円      ", i => Collision2D.Test(circles[i & 63], circles[(i + 7) & 63]).Hit);
        Measure("AABB と AABB  ", i => Collision2D.Test(boxes[i & 63], boxes[(i + 7) & 63]).Hit);
        Measure("円 と AABB    ", i => Collision2D.Test(circles[i & 63], boxes[(i + 7) & 63]).Hit);
        Measure("円 と OBB     ", i => Collision2D.Test(circles[i & 63], obbs[(i + 7) & 63]).Hit);
        Measure("OBB と OBB    ", i => Collision2D.Test(obbs[i & 63], obbs[(i + 7) & 63]).Hit);

        Console.WriteLine();

        void Measure(string name, Func<int, bool> test)
        {
            // ウォームアップ(JIT とデリゲートの解決)
            int warm = 0;
            for (int i = 0; i < 10000; i++)
            {
                warm += test(i) ? 1 : 0;
            }

            var stopwatch = Stopwatch.StartNew();
            int hits = 0;
            for (int i = 0; i < n; i++)
            {
                hits += test(i) ? 1 : 0;
            }

            double nanoseconds = stopwatch.Elapsed.TotalMilliseconds * 1e6 / n;
            Console.WriteLine($"  {name}: {nanoseconds,5:F1}ns  (当たり {hits * 100.0 / n:F0}% / warm {warm})");
        }
    }

    /// <summary>
    /// **ECS の不変条件を確かめる自己チェック**(D キー)。
    ///
    /// Day 19 の決定性、Day 21 のハンドル、Day 22 の階層と同じ趣旨。
    /// いちばん見たいのは**ストアの並びがいつ崩れるか**で、
    /// これが分かっていないと、速い経路(要点4)を安全に使えない。
    /// </summary>
    private static void RunEcsCheck()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("[ECS の自己チェック]");

        var world = new World();

        Check("既定値のエンティティは無効", !default(Entity).IsValid);

        Entity a = world.CreateEntity();
        world.Add(a, new Transform2D { Position = new Vector2(1.0f, 2.0f) });
        world.Add(a, new Velocity2D { Linear = new Vector2(3.0f, 4.0f) });

        Check("生きている", world.IsAlive(a));
        Check("コンポーネントが引ける", world.Has<Transform2D>(a) && world.Has<Velocity2D>(a));

        // ref で返るので、引いてそのまま書き換えられる。
        // 値で返す作りだとコピーが書き換わるだけで、元は変わらない。
        world.Get<Transform2D>(a).Position.X = 99.0f;
        Check("Get は参照を返す", MathF.Abs(world.Get<Transform2D>(a).Position.X - 99.0f) < 0.001f);

        Entity b = world.CreateEntity();
        world.Add(b, new Transform2D { Position = new Vector2(10.0f, 0.0f) });
        world.Add(b, new Velocity2D());
        Entity c = world.CreateEntity();
        world.Add(c, new Transform2D { Position = new Vector2(20.0f, 0.0f) });
        world.Add(c, new Velocity2D());

        ComponentStore<Transform2D> transforms = world.Store<Transform2D>();
        ComponentStore<Velocity2D> velocities = world.Store<Velocity2D>();

        Check("同じ順で付ければ並びは一致する", EcsSystems.AreAligned(transforms, velocities));

        // 真ん中を消す。末尾と入れ替わるので**並び順は変わる**が、
        // どのストアも同じ入れ替えをするので**一致は保たれる**。
        world.DestroyEntity(b);
        Check("破棄すると全ストアから消える", transforms.Count == 2 && velocities.Count == 2);
        Check("破棄したエンティティは無効", !world.IsAlive(b));
        Check("残りは正しく引ける",
            MathF.Abs(world.Get<Transform2D>(c).Position.X - 20.0f) < 0.001f);
        Check("破棄しても並びの一致は保たれる", EcsSystems.AreAligned(transforms, velocities));

        // 枠の再利用。Day 21 のハンドルとまったく同じ話。
        Entity reused = world.CreateEntity();
        Check("空いた枠が再利用される", reused.Index == b.Index, $"新 {reused} / 旧 {b}");
        Check("それでも古いエンティティは無効のまま", reused != b && !world.IsAlive(b));
        Check("再利用した枠に前の中身は残っていない", !world.Has<Transform2D>(reused));

        // **後から足すと並びが崩れる**。ここが要点4の肝。
        //
        // 破棄では崩れない(全ストアが同じ入れ替えをするから)のに対し、
        // 「片方にだけ後から足す」と順番がずれる。
        // つまり**エンティティの構成がそろっていないと速い経路は使えない**。
        Entity late = world.CreateEntity();
        world.Add(late, new Transform2D());
        Check("片方にしか無ければ件数が合わない", !EcsSystems.AreAligned(transforms, velocities));

        Entity both = world.CreateEntity();
        world.Add(both, new Transform2D());
        world.Add(both, new Velocity2D());

        world.Add(late, new Velocity2D());
        Check(
            "件数がそろっても順番は戻らない",
            transforms.Count == velocities.Count && !EcsSystems.AreAligned(transforms, velocities),
            $"件数 {transforms.Count} / {velocities.Count}");
        _ = both;

        Check("崩れても結果は同じ", AlignedAndJoinedAgree(), "(速い経路と一般の経路を突き合わせ)");

        Console.WriteLine(failures == 0 ? "  すべて合格" : $"  {failures} 件 不合格");
        Console.WriteLine();

        void Check(string name, bool condition, string detail = "")
        {
            Console.WriteLine($"  [{(condition ? "OK" : "NG")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!condition)
            {
                failures++;
            }
        }
    }

    /// <summary>
    /// 速い経路(並びが一致している前提)と一般の経路(番号で引く)で、
    /// **100 ステップ回した結果が 1 ビットも違わない**ことを確かめる。
    ///
    /// 速い経路は「前提が崩れたら静かに間違う」種類の最適化なので、
    /// 正しいときには完全に一致することを押さえておきたい。
    /// </summary>
    private static bool AlignedAndJoinedAgree()
    {
        var bounds = new Vector2(960.0f, 640.0f);
        var random = new Random(4242);

        ulong Run(bool aligned)
        {
            var world = new World();
            for (int i = 0; i < 64; i++)
            {
                Entity entity = world.CreateEntity();
                world.Add(entity, new Transform2D
                {
                    Position = new Vector2((float)random.NextDouble() * 900.0f, (float)random.NextDouble() * 600.0f),
                    Rotation = (float)random.NextDouble(),
                });
                world.Add(entity, new Previous2D());
                world.Add(entity, new Velocity2D
                {
                    Linear = new Vector2((float)random.NextDouble() * 200.0f - 100.0f, 80.0f),
                    Spin = 1.0f,
                    HalfSize = 16.0f,
                });
            }

            for (int step = 0; step < 100; step++)
            {
                EcsSystems.Snapshot(world, aligned);
                EcsSystems.Move(world, 1.0f / 60.0f, bounds, aligned);
            }

            ulong hash = 14695981039346656037UL;
            foreach (Transform2D transform in world.Store<Transform2D>().Values)
            {
                Mix(ref hash, BitConverter.SingleToUInt32Bits(transform.Position.X));
                Mix(ref hash, BitConverter.SingleToUInt32Bits(transform.Position.Y));
                Mix(ref hash, BitConverter.SingleToUInt32Bits(transform.Rotation));
            }

            return hash;
        }

        // 同じ乱数列から作りたいので、種を戻して2回作る。
        ulong fast = Run(true);
        random = new Random(4242);
        ulong general = Run(false);
        return fast == general;

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
    /// ライフサイクルの呼ばれ方を実演する(H キー)。
    ///
    /// GameObject を1つ作って、有効・無効を切り替えて、破棄する。
    /// **どれが即座に呼ばれ、どれがステップの境界まで待たされるか**が見える。
    /// 破棄がその場では起きないこと(要点4)が、いちばん引っかかりやすい。
    /// </summary>
    private static void RunLifecycleDemo()
    {
        Console.WriteLine();
        Console.WriteLine("[ライフサイクルの実演]");
        Console.WriteLine("  CreateGameObject + AddComponent");

        GameObject demo = _scene.CreateGameObject("LifecycleDemo");
        LifecycleLogger logger = demo.AddComponent<LifecycleLogger>();

        // **ラベルを入れるのは AddComponent のあと**。
        // Awake は AddComponent の中で走ってしまうので、
        // 下の2行が出た時点ではまだ既定値("obj")のまま。
        // 「Awake の中で、外から設定した値をあてにしてはいけない」のがこれ。
        logger.Label = "demo";
        Console.WriteLine("  ↑ ラベルはまだ obj。Awake は AddComponent の中で走るので、");
        Console.WriteLine("     プロパティを入れる前に呼ばれている");

        Console.WriteLine("  SetActive(false) → SetActive(true)");
        demo.SetActive(false);
        demo.SetActive(true);

        Console.WriteLine("  ここから4ステップ動かす(Start は最初のステップの直前)");
        for (int i = 0; i < 4; i++)
        {
            _scene.FixedUpdate((float)_loop.FixedDeltaTime);
        }

        Console.WriteLine("  Destroy を予約 → まだ生きている");
        _scene.Destroy(demo);
        Console.WriteLine($"    IsDestroyed = {demo.IsDestroyed} / シーンにはまだ {_scene.GameObjectCount} 個");

        Console.WriteLine("  次のステップの終わりで実際に消える");
        _scene.FixedUpdate((float)_loop.FixedDeltaTime);
        Console.WriteLine($"    シーンは {_scene.GameObjectCount} 個になった");
        Console.WriteLine();
    }

    /// <summary>
    /// **ハンドルの不変条件を確かめる自己チェック**(T キー)。
    ///
    /// Day 19 の決定性チェック、Day 20 のリプレイ検証と同じ趣旨で、
    /// 「そういう設計になっているはず」を実際に走らせて確かめる。
    /// 特に見たいのは**世代番号が本当に効いているか**——
    /// スロットが再利用されたときに、古いハンドルが蘇らないこと。
    /// </summary>
    private static void RunResourceCheck()
    {
        string path = ResolveAssetPath("textures/sprite-diamond.png");
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("[リソースの自己チェック]");

        Check("既定値のハンドルは無効", !default(Handle<Texture>).IsValid);

        Handle<Texture> first = _resources.LoadTexture(path);
        Handle<Texture> second = _resources.LoadTexture(path);
        Check("同じパスは同じハンドルになる(重複ロードされない)", first == second);
        Check("参照カウントが 2 になる", _resources.RefCountOf(first) == 2, $"実際 {_resources.RefCountOf(first)}");

        _resources.Release(first);
        Check("1回返しただけでは消えない", _resources.IsReady(second));

        _resources.Release(second);
        Check("2回目で消える", !_resources.TryGetTexture(second, out _));
        Check(
            "解放後のハンドルからは仮の絵が返る",
            ReferenceEquals(_resources.GetTexture(second), _resources.Placeholder));

        // **世代番号の本番**。空いたスロットをすぐ次が使う。
        Handle<Texture> reused = _resources.LoadTexture(path);
        Check("空いたスロットが再利用される", reused.Index == second.Index,
            $"新 {reused} / 旧 {second}");
        Check("それでも古いハンドルは無効のまま", reused != second && !_resources.TryGetTexture(second, out _));

        // 読み込み設定までキーに含めているか(ミップマップの有無で結果が変わる)。
        Handle<Texture> noMipmaps = _resources.LoadTexture(path, generateMipmaps: false);
        Check("読み込み設定が違えば別のハンドル", noMipmaps != reused);

        _resources.Release(noMipmaps);
        _resources.Release(reused);

        Console.WriteLine(failures == 0 ? "  すべて合格" : $"  {failures} 件 不合格");
        Console.WriteLine();

        void Check(string name, bool condition, string detail = "")
        {
            Console.WriteLine($"  [{(condition ? "OK" : "NG")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!condition)
            {
                failures++;
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
            // **ハンドルを解いてから渡す**。プールの配列を1回引くだけなので、
            // 2万枚ぶん繰り返しても実測で誤差の範囲(計画書の要点4)。
            _spriteBatch.Draw(_resources.GetTexture(_looseTextures[kind]), center, size, rotation, color, layer);
        }
    }

    private static void Draw(Mesh<Vertex> mesh, Material material, Matrix4x4 model)
    {
        material.Apply(_resources);
        _resources.GetShader(material.Shader).SetMatrix4("uModel", model);
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

            case Key.J:
                _backend = _backend switch
                {
                    SpriteBackend.StructArray => SpriteBackend.GameObject,
                    SpriteBackend.GameObject => SpriteBackend.Ecs,
                    _ => SpriteBackend.StructArray,
                };
                ApplyBackend();
                Console.WriteLine($"更新方式: {BackendLabel()}");
                break;

            case Key.D:
                RunEcsCheck();
                break;

            case Key.F2:
                SaveSceneToFile();
                break;

            case Key.F3 when keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight):
                _scene = CreateDemoScene(_scene.Bounds);
                BindSceneObjects();
                Console.WriteLine($"[scene] コードから組み直しました({_scene.GameObjectCount} 個)");
                break;

            case Key.F3:
                LoadSavedScene();
                break;

            case Key.F4:
                RunSceneRoundTrip();
                break;

            case Key.F6:
                _collisionDemo = !_collisionDemo;
                if (_collisionDemo && _bodies.Length == 0)
                {
                    InitializeBodies(_activeBodies);
                }

                Console.WriteLine(
                    $"衝突デモ: {OnOff(_collisionDemo)}"
                    + (_collisionDemo ? "  (G で3D背景、PageDown でスプライトを消すと見やすい)" : string.Empty));
                break;

            case Key.F7:
                _shapeMix = (_shapeMix + 1) % 4;
                InitializeBodies(_activeBodies);
                Console.WriteLine($"形: {ShapeMixLabel()}");
                break;

            case Key.F8:
                _resolveOverlap = !_resolveOverlap;
                Console.WriteLine($"押し戻し: {OnOff(_resolveOverlap)}");
                break;

            case Key.F9:
                RunCollisionCheck();
                break;

            case Key.H:
                RunLifecycleDemo();
                break;

            case Key.Q:
                LoadDemoTextures(useAsync: true);
                break;

            case Key.E:
                LoadDemoTextures(useAsync: false);
                break;

            case Key.U:
                UnloadDemoTextures();
                break;

            case Key.T:
                RunResourceCheck();
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
            case Key.PageDown:
                {
                    // Shift を押しながらだと10倍動く。
                    // 2万個まで 1000 刻みで上げるのは19回かかって、さすがに試す気が失せる。
                    bool shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);

                    // 衝突デモ中は体数を動かす。**今見ているものを増減させる**ほうが素直。
                    if (_collisionDemo)
                    {
                        int bodyStep = shift ? 500 : 60;
                        SetBodyCount(_activeBodies + (key == Key.PageUp ? bodyStep : -bodyStep));
                        break;
                    }

                    int step = shift ? 10000 : 1000;
                    SetSpriteCount(_activeSprites + (key == Key.PageUp ? step : -step));
                    break;
                }

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
                _resources.GetTexture(_texture).SetFilter(_filter);
                _atlas.Texture.SetFilter(_filter);
                break;

            case Key.R:
                _wrap = _wrap == TextureWrap.Repeat ? TextureWrap.ClampToEdge : TextureWrap.Repeat;
                _resources.GetTexture(_texture).SetWrap(_wrap);
                break;

            case Key.F5:
                _resources.GetShader(_shader).TryReload();
                _resources.GetShader(_spriteShader).TryReload();
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
    /// <summary>
    /// スプライトの数を変える。GameObject モードならシーンのほうもそろえる。
    ///
    /// 上限から <c>LayerTest</c> のぶんと階層の実演のぶんを引いてあるのは、
    /// バッチの容量(<see cref="MaxSprites"/>)を超えるとそこでフラッシュが
    /// 割り込んで、ソートが分断されるため(Day 18 の <c>SpriteBatch.Draw</c> 参照)。
    /// </summary>
    private static void SetSpriteCount(int count)
    {
        _activeSprites = Math.Clamp(count, 0, MaxSprites - LayerTest.Length - 16);

        ApplyBackend();
    }

    /// <summary>今のモードに合わせて、GameObject 側と ECS 側の中身をそろえる。</summary>
    private static void ApplyBackend()
    {
        EnsureSceneSprites(_backend == SpriteBackend.GameObject ? _activeSprites : 0);
        EnsureEcsSprites(_backend == SpriteBackend.Ecs ? _activeSprites : 0);
    }

    private static string BackendLabel() => _backend switch
    {
        SpriteBackend.StructArray => "構造体の配列",
        SpriteBackend.GameObject => "GameObject + Component",
        _ => "ECS",
    };

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

        _cube.Dispose();
        _quad.Dispose();

        // **テクスチャとシェーダの Dispose がここから消えた**。
        // 誰が何を持っているかを1箇所に集めた結果、後始末も1行になる。
        // Day 20 まではここに並べ忘れるとそのままリークしていた。
        _resources.Dispose();

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

    /// <summary>
    /// <see cref="ResolveAssetPath"/> の、無くても例外を投げない版。
    /// **「あれば使う」もの**(シーンファイルなど)はこちらで探す。
    /// </summary>
    private static string? TryResolveAssetPath(string relativePath)
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

        return null;
    }
}
