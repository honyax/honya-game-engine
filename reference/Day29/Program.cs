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
///
/// **Day 26 での変更**: その O(n^2) から抜ける。
/// F10 で総当たりと均一グリッドを切り替えられる。
/// 1000 体で「組 499,500 / 判定 12ms」だったものが
/// 「候補 2万弱 / 判定 0.5ms」になり、**2万体まで 60fps で回る**ようになる。
/// F11 でマスを可視化すると、どこに何個入っているかがそのまま見える。
/// F12 は自己チェックと掃引ベンチ——
/// **総当たりとグリッドが同じ接触集合を出すこと**を確かめてから速さを測る。
/// ブロードフェーズの取りこぼしは絵に出ないので、確かめる側を先に書く。
///
/// **Day 27 での変更**: 音が出る。
/// WAV を自分で読み(<see cref="WavFile"/>)、OpenAL で鳴らす(<see cref="AudioSystem"/>)。
/// 6 キーで、体が壁に当たるたびに音が鳴るようになる。
/// **2000 体だと 1 ステップに数十回の再生要求**が飛ぶので、
/// 発音数の上限とピッチの揺らぎが無いと音として成立しない。
/// Day 26 で 2 万体を動かせるようにしたことが、そのまま音の設計問題になっている。
///
/// **Day 28 での変更**: 文字が出る。
/// システムのフォント(メイリオ等)を探し、使った文字だけをその場で焼いて
/// 1枚のアトラスに詰める(<see cref="GlyphAtlas"/>)。
/// セミコロンで、タイトルバーに出していた数字が**画面の中**へ移る。
/// 3 回押すと見本帳が出て、日本語・大きさ・整列・カーニング・
/// ピクセル丸めの効き目を並べて見られる。
/// **文字も結局スプライト1枚**なので、Day 18 のバッチにそのまま乗る。
///
/// **Day 29 での変更**: 卒業制作が始まった。
/// Enter で <see cref="SurvivorGame"/> に切り替わり、
/// 見下ろし型の時間耐久アクションが動く。
/// Day 25〜28 で作ったものが、ここで**ゲームの必然として**要る——
/// 数百体の敵を捌く格子(Day 26)、倒したときの音(Day 27)、
/// 残り HP と時間の表示(Day 28)。
/// **エンジンとゲームの境目**がはっきり見えるように、
/// ゲームのコードは <c>Game/</c> に分けて、GL も窓も知らない形にしてある。
/// </summary>
internal static class Program
{
    private const int MaxSprites = 20000;

    /// <summary>
    /// 衝突デモの体数の上限。**Day 25 は 2000 だった**。
    ///
    /// グリッドを入れたので 10 倍に上げられる。
    /// ただし 20000 体では当たり判定だけで 15.8ms 使うので、
    /// **新しい壁がちょうどここに来る**(Day 25 の壁は 1000〜2000 体だった)。
    /// </summary>
    private const int MaxBodies = 20000;

    /// <summary>
    /// 体の大きさを決めるときの基準になる体数(<see cref="InitializeBodies"/>)。
    /// これを超えたぶんは、**面積の合計が変わらないように小さくする**。
    /// Day 25 の計測(最大 2000 体)とそのまま比べられるように、境目をそこに置いてある。
    /// </summary>
    private const int DensityReferenceBodies = 2000;

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

    /// <summary>環の絵。Day 29 では硬い敵に使う。</summary>
    private const int RingSprite = 1;

    /// <summary>星の絵。Day 29 ではプレイヤーに使う。</summary>
    private const int StarSprite = 2;

    /// <summary>菱形の絵。Day 29 では経験値のジェムに使う。</summary>
    private const int DiamondSprite = 3;

    /// <summary>箱の絵。枠が見えるので**重なりが分かる**。Day 29 では HUD の帯にも使う。</summary>
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

    // --- Day 25 からの当たり判定のデモ。今日はここに「組の絞り込み」が入る ---

    /// <summary>衝突デモを動かしているか(F6)。</summary>
    private static bool _collisionDemo;

    /// <summary>めり込みを押し戻すか(F8)。切ると判定だけして重なったままになる。</summary>
    private static bool _resolveOverlap = true;

    /// <summary>形の組み合わせ(F7)。0 = 混在 / 1 = 円だけ / 2 = 矩形だけ / 3 = 回転矩形だけ。</summary>
    private static int _shapeMix;

    private static Body[] _bodies = [];
    private static int _activeBodies = 120;

    /// <summary>
    /// 直前のステップでナローフェーズを呼んだ回数。
    /// 総当たりなら n(n-1)/2、グリッドなら**候補の数**になる。
    /// </summary>
    private static long _pairTests;

    /// <summary>直前のステップで当たっていた組の数。</summary>
    private static int _contacts;

    /// <summary>当たり判定にかかった時間(ミリ秒)の移動平均。</summary>
    private static double _collisionMilliseconds;

    // --- 今日の主役: ブロードフェーズ ---

    /// <summary>組の絞り込み方(F10)。</summary>
    private enum Broadphase
    {
        /// <summary>Day 25 のやり方。全部の組を試す</summary>
        BruteForce,

        /// <summary>今日のやり方。同じマスにいるものだけ試す</summary>
        UniformGrid,
    }

    private static Broadphase _broadphase = Broadphase.UniformGrid;

    private static readonly SpatialGrid Grid = new();

    /// <summary>
    /// 体の外接 AABB。**毎ステップ作り直して、グリッドにはこれだけを渡す**。
    ///
    /// ブロードフェーズに <see cref="Body"/> を渡さないのがポイントで、
    /// こうしておくと <see cref="SpatialGrid"/> は形も速度も知らずに済む。
    /// Day 46 で 3D の物体に付け替えるときも、ここを差し替えるだけになる。
    /// </summary>
    private static Aabb2D[] _bodyBounds = [];

    /// <summary>マスを可視化するか(F11)。</summary>
    private static bool _showCells;

    /// <summary>
    /// マスの大きさ。0 なら**平均の大きさから自動**(<see cref="SpatialGrid.SuggestCellSize"/>)。
    /// カンマ / ピリオドで手動の段階に切り替わる。
    /// </summary>
    private static float _cellSizeOverride;

    /// <summary>手動で選べるマスの大きさ。両端は「わざと外した」値。</summary>
    private static readonly float[] CellSizeSteps = [4.0f, 8.0f, 16.0f, 32.0f, 64.0f, 128.0f, 256.0f];

    /// <summary>ブロードフェーズ(構築 + 候補列挙)にかかった時間の移動平均。表示用。</summary>
    private static double _broadphaseMilliseconds;

    /// <summary>直前のステップのブロードフェーズ時間(なましていない生の値)。ベンチが読む。</summary>
    private static double _broadphaseLastMilliseconds;

    /// <summary>
    /// 体を増やしても大きさを変えないか(- キー)。
    /// **ON にすると密度が上がり、グリッドでも O(n^2) に戻っていく**のが見える。
    /// </summary>
    private static bool _fixedBodySize;

    /// <summary>記録を始めた時点のプレイヤーの状態。再生時にここへ巻き戻す。</summary>
    private static (Vector2 Position, Vector2 Velocity, float Angle, float DashCooldown) _recordStart;

    /// <summary>記録を終えた時点のプレイヤー状態のハッシュ。再生後に突き合わせる。</summary>
    private static ulong _recordEndHash;

    // --- 今日の主役: 卒業制作 ---

    private static SurvivorGame _game = null!;
    private static GameView _gameView = null!;

    /// <summary>ゲームモードに入っているか(Enter)。デモとは排他。</summary>
    private static bool _playing;

    /// <summary>ゲームの1ステップにかかった時間(ミリ秒)の移動平均。</summary>
    private static double _gameMilliseconds;

    // --- Day 28 からの文字 ---

    /// <summary>見つかったフォント。見つからなければ null(文字なしで動く)。</summary>
    private static FontFace? _font;

    private static GlyphAtlas? _glyphAtlas;
    private static TextRenderer? _text;

    /// <summary>
    /// 文字専用のバッチ。**スプライトとは別に持つ**。
    ///
    /// グリフのアトラスは1チャンネル(R8)なので、
    /// <c>sprite.frag</c>(RGBA をそのまま掛ける)では真っ黒になる。
    /// シェーダが違えば同じバッチには積めない——
    /// バッチは「同じ設定で描けるものをまとめる」仕組みなので、当然の帰結。
    /// UI を最後に別パスで描くのは、実際のエンジンでも普通の形。
    /// </summary>
    private static SpriteBatch? _textBatch;

    private static Handle<Shader> _textShader;

    /// <summary>画面内の表示(セミコロン)。0=なし 1=情報 2=情報+アトラス 3=見本帳。</summary>
    private static int _overlay = 1;

    /// <summary>文字を積むのにかかった時間(ミリ秒)の移動平均。</summary>
    private static double _textMilliseconds;

    /// <summary>UI の文字の大きさ(ピクセル)。</summary>
    private const int UiFontSize = 16;

    // --- Day 27 からの音 ---

    private static AudioSystem _audio = null!;

    /// <summary>効果音。**壁に当たった / 体が当たった / 拾った**の3つ。</summary>
    private static Handle<AudioClip> _bounceClip;
    private static Handle<AudioClip> _hitClip;
    private static Handle<AudioClip> _pickupClip;

    /// <summary>ステレオの実演用。**定位が効かない**ことを確かめるために置いてある。</summary>
    private static Handle<AudioClip> _stereoClip;

    private static Handle<AudioClip> _musicClip;

    /// <summary>BGM を鳴らしているボイス。止めるために札を持っておく(効果音は持たない)。</summary>
    private static VoiceId _musicVoice = VoiceId.None;

    /// <summary>壁に当たったときに音を鳴らすか(6)。</summary>
    private static bool _collisionSfx;

    /// <summary>体の位置で左右に振るか(9)。</summary>
    private static bool _panning = true;

    /// <summary>このステップで音を要求した回数。**絞る前の数**。</summary>
    private static int _soundRequests;

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
            Title = "Day28 - テキスト描画",
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

        // --- 文字 ---
        //
        // **頂点シェーダは sprite.vert を使い回す**。
        // 位置と UV と色を渡してスクリーン座標に写す、という仕事は
        // スプライトと文字でまったく同じで、違うのは「色をどう作るか」だけ。
        // シェーダを組み合わせで作れるのは、この2段が分かれているおかげ。
        _textShader = _resources.LoadShader(
            Path.Combine(shaderDirectory, "sprite.vert"),
            Path.Combine(shaderDirectory, "text.frag"));

        _font = SystemFonts.Open();

        if (_font is not null)
        {
            // 512x512 の 1 チャンネル = 256KB。
            // ゲーム1本で実際に使う文字はせいぜい数百字なので、これで足りる。
            _glyphAtlas = new GlyphAtlas(_gl, _font, size: 512);
            _text = new TextRenderer(_glyphAtlas);

            // 文字は1フレームに数百枚も積まないので、容量は小さくてよい。
            _textBatch = new SpriteBatch(_gl, _resources.GetShader(_textShader), capacity: 4096);

            Console.WriteLine();
            Console.WriteLine($"フォント: {_font.Name}  {_font.Path}");
            Console.WriteLine(
                $"  ファイル内のフォント数 {_font.FaceCount} / 使用 {_font.FaceIndex} 番目"
                + $" / 日本語 {(_font.HasGlyph(0x3042) ? "あり" : "なし")}");

            float scale = _font.ScaleFor(UiFontSize);
            Console.WriteLine(
                $"  {UiFontSize}px: ascent {_font.Ascent(scale):F2} / descent {_font.Descent(scale):F2}"
                + $" / 行送り {_font.LineHeight(scale):F2}");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("フォント: 見つかりませんでした(文字なしで続行します)");
        }

        // --- 卒業制作 ---
        //
        // **ゲームは絵の種類しか知らない**(添字だけ受け取る)。
        // テクスチャもアトラスも GL も知らないので、
        // 描き方を差し替えてもゲームのコードは動かない。
        _game = new SurvivorGame();
        _gameView = new GameView(_game, CircleSprite, RingSprite, StarSprite, DiamondSprite, BoxSprite);

        // 起きたことを音に変える。**ゲーム側は AudioSystem を知らない**ので、
        // 対応表はここに置く(SurvivorGame.OnEvent のコメント)。
        _game.OnEvent = (kind, _) =>
        {
            switch (kind)
            {
                case SurvivorGame.GameEvent.Fire:
                    _audio.Play(_bounceClip, 0.22f, 1.5f);
                    break;

                case SurvivorGame.GameEvent.EnemyKilled:
                    // **終盤は1ステップに数十体死ぬ**。
                    // AudioSystem の間引き(Day 27 の要点5)が無いと、ここで破綻する。
                    _audio.Play(_hitClip, 0.30f, 1.2f);
                    break;

                case SurvivorGame.GameEvent.PlayerHit:
                    // 被弾は**間引かれては困る**ので優先度を上げる。
                    _audio.Play(_hitClip, 0.85f, 0.65f, 0.0f, priority: 50);
                    break;

                case SurvivorGame.GameEvent.GemCollected:
                    _audio.Play(_pickupClip, 0.30f, 1.35f);
                    break;

                case SurvivorGame.GameEvent.LevelUp:
                    _audio.Play(_pickupClip, 0.75f, 0.8f, 0.0f, priority: 60);
                    break;

                case SurvivorGame.GameEvent.GameOver:
                    _audio.Play(_hitClip, 0.9f, 0.45f, 0.0f, priority: 80);
                    break;
            }
        };

        // --- 音 ---
        //
        // **描画とは完全に別のデバイス**なので、GL とは何の関係もない。
        // 初期化に失敗しても IsAvailable が false になるだけで、以降の呼び出しは黙って無視される。
        _audio = new AudioSystem(voiceCount: 32);

        if (_audio.IsAvailable)
        {
            _bounceClip = _audio.Load(ResolveAssetPath("audio/bounce.wav"));
            _hitClip = _audio.Load(ResolveAssetPath("audio/hit.wav"));
            _pickupClip = _audio.Load(ResolveAssetPath("audio/pickup.wav"));
            _stereoClip = _audio.Load(ResolveAssetPath("audio/stereo-ping.wav"));
            _musicClip = _audio.Load(ResolveAssetPath("audio/music-loop.wav"));

            Console.WriteLine();
            Console.WriteLine($"オーディオ: {_audio.DeviceName} / {_audio.Version} / ボイス {_audio.VoiceCount}");
            foreach (Handle<AudioClip> handle in
                (Handle<AudioClip>[])[_bounceClip, _hitClip, _pickupClip, _stereoClip, _musicClip])
            {
                AudioClip? clip = _audio.TryGet(handle);
                if (clip is not null)
                {
                    Console.WriteLine(
                        $"  {clip.Name,-12} {clip.SampleRate,5}Hz {clip.Channels}ch {clip.BitsPerSample,2}bit "
                        + $"{clip.Duration,5:F2}s {clip.ByteSize,7:N0}B");
                }
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("オーディオ: 使えるデバイスがありません(音なしで続行します)");
        }

        Console.WriteLine();
        Console.WriteLine("Enter:卒業制作(見下ろし型アクション)の開始 / 終了   Backspace:タイトルへ戻る");
        Console.WriteLine("  ゲーム中: 矢印キーで移動、攻撃は自動");
        Console.WriteLine();
        Console.WriteLine("J:更新方式(構造体配列/GameObject/ECS)  H:ライフサイクルの実演  D:ECS の自己チェック");
        Console.WriteLine("F6:衝突デモ  F7:形の切り替え  F8:押し戻し  F9:衝突判定の自己チェック");
        Console.WriteLine("F10:総当たり/グリッド  F11:マスの可視化  F12:ブロードフェーズの自己チェックと計測");
        Console.WriteLine(", / . :マスの大きさ  -:体の大きさ(面積一定/固定)");
        Console.WriteLine("5:効果音  6:衝突音  7:同じ音の上限  8:ピッチ揺らぎ  9:定位  0:BGM");
        Console.WriteLine("[ / ] :音量  F1:オーディオの自己チェックと計測");
        Console.WriteLine("; :画面内の表示(なし/情報/アトラス/見本帳)  / :テキストの自己チェックと計測");
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

            _window.Title = _playing
                ? $"Day29  {_fps:F1} fps | 卒業制作 "
                    + $"{_game.Phase} 経過:{_game.Elapsed:F1}s 敵:{_game.EnemyCount} "
                    + $"弾:{_game.ProjectileCount} 撃破:{_game.Kills} Lv.{_game.Level} | "
                    + $"更新:{_gameMilliseconds:F2}ms 候補:{_game.PairCandidates:N0} DC:{_drawCalls} | "
                    + $"音:{_audio.ActiveVoices}/{_audio.VoiceCount} 間引き:{_audio.CulledLastStep}"
                : $"Day29  {_fps:F1} fps | "

                // 今日いちばん見たい2つを前に出す。タイトルバーは思ったより早く切れる。
                + $"{BackendLabel()} 更新:{_updateMilliseconds:F2}ms "
                + $"GO:{_scene.GameObjectCount} E:{_world.AliveCount} | "
                + (_collisionDemo
                    ? $"衝突:{_activeBodies}体 {BroadphaseLabel()} "

                        // **削減率を出す**のが今日の眼目。
                        // 「候補 18,432」だけでは速くなったのか分からない。
                        // 総当たりなら何組だったかと並べて初めて意味を持つ。
                        + $"候補:{_pairTests:N0}/{(long)_activeBodies * (_activeBodies - 1) / 2:N0} "
                        + $"接触:{_contacts} "
                        + $"広域:{_broadphaseMilliseconds:F2}ms 判定:{_collisionMilliseconds:F2}ms "
                        + $"形:{ShapeMixLabel()} 押戻:{OnOff(_resolveOverlap)} | "
                    : $"スプライト:{_activeSprites} DC:{_drawCalls} | ")
                + $"sim {1.0 / _loop.FixedDeltaTime:F0}Hz step:{_loop.StepsLastFrame} α:{_loop.Alpha:F2} "
                + $"遅れ:{_loop.Lag * 1000.0:F1}ms | "
                + $"補間:{OnOff(_interpolate)} 負荷:{_loadMicroseconds}us | "
                + $"{RecorderLabel()} | "
                + $"tex:{_resources.TextureCount}/待ち{_resources.PendingCount} | "
                + $"音:{_audio.ActiveVoices}/{_audio.VoiceCount} "

                // **要求と発音を並べて出す**のが今日の眼目。
                // 「1ステップに 47 回要求して、鳴ったのは 4 回」が見えていないと、
                // 間引きを外したときに何が起きるか分からない。
                + $"要求:{_soundRequests} 発音:{_audio.StartedLastStep} "
                + $"間引き:{_audio.CulledLastStep} 奪取:{_audio.StolenLastStep}";
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
        // **音の後始末は毎ステップの頭で**。
        // 終わったボイスを空きに戻し、1ステップぶんの発音予算を戻す。
        // 描画側(OnRender)ではなくここに置いたのは、
        // 音を要求するのがこの下だから——**予算を戻す場所と使う場所を近くに置く**。
        _audio.Update();
        _soundRequests = 0;

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

        // **ゲームモードのときはデモを回さない**。
        // 同じ入力を2つの世界が食い合うと、どちらも思ったとおりに動かなくなる。
        if (_playing)
        {
            var gameStopwatch = Stopwatch.StartNew();
            _game.ViewSize = bounds;
            _game.Update(dt, input);
            _gameMilliseconds = (_gameMilliseconds * 0.9) + (gameStopwatch.Elapsed.TotalMilliseconds * 0.1);

            _updateMilliseconds = (_updateMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);
            return;
        }

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

    /// <summary>
    /// マスの大きさを1段ずつ変える(カンマ / ピリオド)。両端まで行くと自動へ戻る。
    ///
    /// **手で振ってみるのが早い**。4 にすると1個が何枚にもまたがり、
    /// 256 にすると画面が数枚のマスになって総当たりに戻る。
    /// F11 の可視化と一緒に使うと、数字と絵が同時に動く。
    /// </summary>
    private static void CycleCellSize(bool larger)
    {
        if (_cellSizeOverride <= 0.0f)
        {
            // 自動から手動へ。**今の自動値にいちばん近い段**から始めると連続に見える。
            float current = Grid.CellSize;
            int nearest = 0;
            for (int i = 1; i < CellSizeSteps.Length; i++)
            {
                if (MathF.Abs(CellSizeSteps[i] - current) < MathF.Abs(CellSizeSteps[nearest] - current))
                {
                    nearest = i;
                }
            }

            _cellSizeOverride = CellSizeSteps[nearest];
        }
        else
        {
            int index = Array.IndexOf(CellSizeSteps, _cellSizeOverride) + (larger ? 1 : -1);

            // 端をはみ出したら自動へ戻す。
            _cellSizeOverride = index < 0 || index >= CellSizeSteps.Length ? 0.0f : CellSizeSteps[index];
        }

        Console.WriteLine(
            _cellSizeOverride > 0.0f
                ? $"マスの大きさ: {_cellSizeOverride:F0}px(手動)"
                : "マスの大きさ: 自動(平均の直径)");
    }

    /// <summary>タイトルバー用のブロードフェーズ表示。グリッドならマスの構成も出す。</summary>
    private static string BroadphaseLabel() => _broadphase switch
    {
        Broadphase.UniformGrid =>
            $"格子{Grid.Columns}x{Grid.Rows}@{Grid.CellSize:F0}"
            + $"{(_cellSizeOverride > 0.0f ? "手動" : "自動")} "
            + $"最大{Grid.MaxPerCell}/マス",
        _ => "総当たり",
    };

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

        // グリフを焼いた数の集計を戻す。**焼くのはこのフレームの描画中**なので、
        // 描き始める前に 0 に戻しておく。
        _glyphAtlas?.BeginFrame();

        _gl.ClearColor(0.08f, 0.09f, 0.12f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_playing)
        {
            RenderGame();
            _drawCalls = _spriteBatch.DrawCallCount;
            RenderText();
            return;
        }

        if (_draw3D)
        {
            Render3D();
        }

        RenderSprites();
        _drawCalls = _spriteBatch.DrawCallCount;

        RenderResourceStrip();

        // **文字はいちばん最後**。UI は何よりも手前に出る。
        RenderText();
    }

    /// <summary>
    /// 卒業制作を描く。
    ///
    /// **デモの描画をひとつも呼んでいない**のがポイント。
    /// 3D の背景もスプライトの群れもロードの帯も出さず、
    /// 使うのは <see cref="SpriteBatch"/> と <see cref="TextRenderer"/> だけ——
    /// つまり<b>エンジンの機能のうち、ゲームが実際に要るものだけ</b>を通っている。
    ///
    /// 描く順は 世界 → HUD。HUD はカメラの影響を受けないので、
    /// **座標系が違う**(世界座標とスクリーン座標)。
    /// 同じバッチに積めるのは、<see cref="GameView"/> の側で
    /// 世界座標をスクリーン座標に直してから渡しているため。
    /// </summary>
    private static void RenderGame()
    {
        var viewSize = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        Matrix4x4 projection = Camera.CreateScreen(
            0.0f, viewSize.X,
            viewSize.Y, 0.0f,
            -1.0f, 1.0f);

        // **奥行きで並べ替える**。ジェム → 敵 → 弾 → プレイヤー → HUD の順に出したいので、
        // 積む順ではなく layer に任せる(Day 18)。
        _spriteBatch.Begin(projection, SpriteSortMode.BackToFront);

        if (_game.Phase == GamePhase.Playing || _game.Phase == GamePhase.GameOver)
        {
            _gameView.DrawWorld(Submit, viewSize);
        }

        _gameView.DrawHudShapes(Submit, viewSize);

        _spriteBatch.End();

        // 文字は別のバッチ(シェーダが違う。Day 28)。
        // **同じ HUD が2つのバッチに分かれる**が、
        // 帯は layer 0.85〜0.9、文字はその上に出るので重なりは崩れない。
        if (_text is not null && _textBatch is not null)
        {
            _textBatch.Begin(projection, SpriteSortMode.Texture);
            _gameView.DrawHudText(_text, _textBatch, viewSize);
            _textBatch.End();
        }
    }

    /// <summary>
    /// 前へ進む(Enter)。デモ → タイトル → 開始 → やり直し。
    ///
    /// **ゲームモードに入るとデモは回らなくなる**(<see cref="FixedUpdate"/>)。
    /// 2万個のスプライトを裏で更新したまま遊ぶと、
    /// 「ゲームが重い」のか「デモが重い」のか分からなくなる。
    /// </summary>
    private static void EnterGame()
    {
        var viewSize = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        if (!_playing)
        {
            _playing = true;
            _game.ReturnToTitle();
            _game.ViewSize = viewSize;

            Console.WriteLine("卒業制作: タイトル(Enter で開始 / Backspace でデモへ戻る)");
            Console.WriteLine("  矢印キーで移動。攻撃は自動。Tab で自己チェックと計測");
            return;
        }

        if (_game.Phase != GamePhase.Playing)
        {
            _game.Start(viewSize);
        }
    }

    /// <summary>後ろへ戻る(Backspace)。プレイ中 → タイトル → デモ。</summary>
    private static void LeaveGame()
    {
        if (!_playing)
        {
            return;
        }

        if (_game.Phase == GamePhase.Playing)
        {
            _game.ReturnToTitle();
            Console.WriteLine("卒業制作: タイトルへ戻りました");
            return;
        }

        _playing = false;
        _audio.StopAll();
        Console.WriteLine("卒業制作: 終了(デモへ戻りました)");
    }

    /// <summary>
    /// 文字を描く。**今日の出口**。
    ///
    /// やることは <see cref="RenderSprites"/> とまったく同じ形——
    /// スクリーンの平行投影を作り、<c>Begin</c> して積んで <c>End</c>。
    /// 違うのはバッチとシェーダだけで、**文字専用の描画経路は無い**。
    /// </summary>
    private static void RenderText()
    {
        if (_overlay == 0 || _text is null || _textBatch is null || _glyphAtlas is null)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        Matrix4x4 projection = Camera.CreateScreen(
            0.0f, _window.FramebufferSize.X,
            _window.FramebufferSize.Y, 0.0f,
            -1.0f, 1.0f);

        _textBatch.Begin(projection, SpriteSortMode.Texture);

        if (_overlay == 3)
        {
            DrawTextSample();
        }
        else
        {
            DrawOverlayInfo();
        }

        if (_overlay == 2)
        {
            DrawAtlasView();
        }

        _textBatch.End();

        _textMilliseconds = (_textMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);
    }

    /// <summary>
    /// タイトルバーに出していた数字を画面の中へ。
    ///
    /// **1回の <c>Draw</c> で複数行を渡している**のが要点。
    /// 行ごとに呼んでもよいが、改行の扱いを <see cref="TextRenderer"/> に閉じ込めておくと
    /// 行送りの計算が1箇所で済む。呼ぶ側が <c>y += 18</c> のような数字を持ち始めると、
    /// フォントを変えた瞬間に全部ずれる。
    /// </summary>
    private static void DrawOverlayInfo()
    {
        var lines = new System.Text.StringBuilder();

        lines.AppendLine($"Day28   {_fps:F1} fps   DC:{_drawCalls}");
        lines.AppendLine(
            $"{BackendLabel()}  更新:{_updateMilliseconds:F2}ms  "
            + $"GO:{_scene.GameObjectCount}  E:{_world.AliveCount}  スプライト:{_activeSprites}");

        if (_collisionDemo)
        {
            lines.AppendLine(
                $"衝突:{_activeBodies}体  {BroadphaseLabel()}  "
                + $"候補:{_pairTests:N0}  接触:{_contacts}  判定:{_collisionMilliseconds:F2}ms");
        }

        if (_audio.IsAvailable)
        {
            lines.AppendLine(
                $"音:{_audio.ActiveVoices}/{_audio.VoiceCount}  要求:{_soundRequests}  "
                + $"発音:{_audio.StartedLastStep}  間引き:{_audio.CulledLastStep}");
        }

        GlyphAtlas atlas = _glyphAtlas!;
        lines.Append(
            $"文字:{atlas.GlyphCount}字  棚{atlas.ShelfCount}段  使用率{atlas.Usage:P1}  "
            + $"焼:{atlas.BakedThisFrame}  積:{_text!.GlyphsDrawn}枚  描画:{_textMilliseconds:F2}ms"
            + (atlas.IsFull ? "  [満杯]" : string.Empty));

        _text.Draw(
            _textBatch!,
            lines.ToString(),
            new Vector2(12.0f, 10.0f),
            UiFontSize,
            new Vector4(0.95f, 0.97f, 1.00f, 1.0f));
    }

    /// <summary>
    /// 見本帳。**目で確かめたいことを全部1画面に並べる**。
    ///
    /// 数字の表より、隣り合わせに置いて見比べるほうが早いものがある——
    /// カーニングの効き目、ピクセル丸めのにじみ、字が抜けたときの豆腐。
    /// </summary>
    private static void DrawTextSample()
    {
        TextRenderer text = _text!;
        SpriteBatch batch = _textBatch!;

        var white = new Vector4(0.96f, 0.97f, 1.00f, 1.0f);
        var dim = new Vector4(0.55f, 0.62f, 0.72f, 1.0f);
        var warn = new Vector4(1.00f, 0.72f, 0.35f, 1.0f);

        float y = 14.0f;
        float width = _window.FramebufferSize.X;

        // --- 大きさ ---
        y += Line("見本帳(; でもどる)", 16, dim, y);
        y += Line("日本語も出る ひらがな カタカナ 漢字 記号 ①②③ 〜！？", 24, white, y) + 2.0f;
        y += Line("The quick brown fox jumps over the lazy dog 0123456789", 16, white, y) + 8.0f;
        y += Line("48px の見出し", 48, white, y) + 6.0f;

        // --- 整列 ---
        y += Line("整列 ↓(同じ y に3つ)", 16, dim, y);
        float alignY = y;
        text.Draw(batch, "左ぞろえ", new Vector2(14.0f, alignY), 16, white);
        text.Draw(batch, "中央ぞろえ", new Vector2(width * 0.5f, alignY), 16, white, TextAlign.Center);
        text.Draw(batch, "右ぞろえ", new Vector2(width - 14.0f, alignY), 16, white, TextAlign.Right);
        y += text.LineHeight(16) + 10.0f;

        // --- カーニング ---
        //
        // "AV" "To" "Ya" は、送りのとおりに並べると離れて見える組み合わせ。
        // 日本語は全角送りなのでほとんど動かない。
        y += Line("カーニング(上=あり / 下=なし)", 16, dim, y);
        text.Kerning = true;
        y += Line("AVATAR Two Ya WAVE To.", 32, white, y);
        text.Kerning = false;
        y += Line("AVATAR Two Ya WAVE To.", 32, warn, y) + 10.0f;
        text.Kerning = true;

        // --- ピクセル丸め ---
        //
        // **0.5px ずらして描く**と、丸めていない側だけがにじむ。
        y += Line("ピクセル丸め(上=あり / 下=なし。0.5px ずらして描画)", 16, dim, y);
        text.Draw(batch, "細い線ほど差が出る ABC 漢字", new Vector2(14.0f, y), 16, white);
        y += text.LineHeight(16);
        text.PixelSnap = false;
        text.Draw(batch, "細い線ほど差が出る ABC 漢字", new Vector2(14.5f, y + 0.5f), 16, warn);
        text.PixelSnap = true;
        y += text.LineHeight(16) + 10.0f;

        // --- 持っていない文字 ---
        //
        // 絵文字はメイリオにも游ゴシックにも入っていないので、豆腐(.notdef)になる。
        // **黙って消えるより、抜けが見えるほうがよい**。
        Line("フォントに無い文字は豆腐になる → 😀🎮  (絵文字は別フォントが要る)", 16, dim, y);
    }

    /// <summary>
    /// アトラスの中身をそのまま画面に出す。**棚詰めが目に見える**。
    ///
    /// 数字(使用率・段数)だけでは、隙間がどこにできているかが分からない。
    /// 大きさの違う字を混ぜてから見ると、段の高さが「その段でいちばん高い字」で
    /// 決まっていることがはっきり分かる。
    /// </summary>
    private static void DrawAtlasView()
    {
        GlyphAtlas atlas = _glyphAtlas!;

        // 画面に収まるように縮める。等倍で出すと 512px 占める。
        float size = MathF.Min(_window.FramebufferSize.Y - 160.0f, 384.0f);
        var center = new Vector2(
            _window.FramebufferSize.X - (size * 0.5f) - 16.0f,
            _window.FramebufferSize.Y - (size * 0.5f) - 16.0f);

        _textBatch!.Draw(
            atlas.Texture,
            center,
            new Vector2(size),
            0.0f,
            new Vector4(0.65f, 0.85f, 1.00f, 1.0f),
            0.5f);

        _text!.Draw(
            _textBatch,
            $"アトラス {atlas.Size}x{atlas.Size} R8 / {atlas.GlyphCount}字 / {atlas.ShelfCount}段",
            new Vector2(center.X, center.Y - (size * 0.5f) - 20.0f),
            UiFontSize,
            new Vector4(0.65f, 0.85f, 1.00f, 1.0f),
            TextAlign.Center);
    }

    /// <summary>見本帳のための短縮形。1行描いて、その高さを返す。</summary>
    private static float Line(string content, int pixelHeight, Vector4 color, float y)
    {
        _text!.Draw(_textBatch!, content, new Vector2(14.0f, y), pixelHeight, color);
        return _text.LineHeight(pixelHeight);
    }

    private static string OverlayLabel() => _overlay switch
    {
        1 => "情報",
        2 => "情報+アトラス",
        3 => "見本帳",
        _ => "なし",
    };

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
        _bodyBounds = new Aabb2D[count];

        float width = _window.FramebufferSize.X;
        float height = _window.FramebufferSize.Y;

        // **体を増やすときは、面積の合計が変わらないように小さくする**。
        //
        // 画面の広さは変わらないので、大きさをそのままに 2 万体を撒くと
        // 画面が体で埋まり、「どのマスにも大量に入っている」状態になる。
        // そうなるとグリッドで絞っても候補が減らない——
        // **空間分割が効くのは「疎である」ときだけ**、という前提がここにある。
        //
        // 面積を n に反比例させる = 一辺は sqrt(n) に反比例させる。
        // - キーでこの補正を切ると、密度が上がったときに何が起きるかが見られる。
        float sizeScale = _fixedBodySize || count <= DensityReferenceBodies
            ? 1.0f
            : MathF.Sqrt((float)DensityReferenceBodies / count);

        for (int i = 0; i < count; i++)
        {
            // **画面に対して詰め込みすぎない**。
            // 面積の合計が画面の 2 割を超えたあたりから常時ほぼ全員が接触状態になり、
            // 「当たったら赤」の表示が意味をなさなくなる。
            float size = (9.0f + ((float)random.NextDouble() * 12.0f)) * sizeScale;
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
    /// 衝突デモの1ステップ。**動かして、壁で跳ねて、組を絞って、当てる**。
    ///
    /// Day 25 との違いは真ん中の「組を絞る」だけ。
    /// 動かす部分と当てる部分は**1文字も変えていない**——
    /// ブロードフェーズは「どの組を調べるか」しか決めないので、
    /// 判定そのもの(<see cref="Collision2D"/>)には手が入らない。
    /// Day 25 で判定を状態のない <c>static</c> にしておいた効果がここで出る。
    ///
    /// 3段の時間はそれぞれ別に測る。
    ///   移動        … O(n)。ここは方式によらず同じ
    ///   ブロードフェーズ … 総当たりなら 0(絞らない)、グリッドなら構築 + 候補列挙
    ///   ナローフェーズ  … 候補の数だけ <see cref="Test(in Body, in Body)"/> を呼ぶ
    /// **速くなったのはナローフェーズの回数が減ったから**であって、
    /// 判定1回が速くなったからではない。この区別が付くように測り分ける。
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
            bool bounced = false;

            if (body.Position.X < extent.X)
            {
                body.Position.X = extent.X;
                body.Velocity.X = MathF.Abs(body.Velocity.X);
                bounced = true;
            }
            else if (body.Position.X > bounds.X - extent.X)
            {
                body.Position.X = bounds.X - extent.X;
                body.Velocity.X = -MathF.Abs(body.Velocity.X);
                bounced = true;
            }

            if (body.Position.Y < extent.Y)
            {
                body.Position.Y = extent.Y;
                body.Velocity.Y = MathF.Abs(body.Velocity.Y);
                bounced = true;
            }
            else if (body.Position.Y > bounds.Y - extent.Y)
            {
                body.Position.Y = bounds.Y - extent.Y;
                body.Velocity.Y = -MathF.Abs(body.Velocity.Y);
                bounced = true;
            }

            if (bounced && _collisionSfx)
            {
                PlayBounce(in body, bounds);
            }
        }

        // --- ブロードフェーズ: 調べる組を決める ---
        long pairs = 0;
        int contacts = 0;
        double broadphaseMilliseconds = 0.0;

        if (_broadphase == Broadphase.UniformGrid)
        {
            var broadStopwatch = Stopwatch.StartNew();

            // 外接 AABB をまとめて作る。**グリッドに渡すのはこれだけ**。
            for (int i = 0; i < count; i++)
            {
                ref Body body = ref _bodies[i];
                _bodyBounds[i] = Aabb2D.FromCenter(body.Position, BoundsExtent(body));
            }

            Span<Aabb2D> boxes = _bodyBounds.AsSpan(0, count);

            Grid.Configure(
                Vector2.Zero,
                bounds,
                _cellSizeOverride > 0.0f ? _cellSizeOverride : SpatialGrid.SuggestCellSize(boxes));

            Grid.Build(boxes);
            pairs = Grid.CollectPairs(boxes);

            broadphaseMilliseconds = broadStopwatch.Elapsed.TotalMilliseconds;

            // --- ナローフェーズ: 候補だけを本判定にかける ---
            ReadOnlySpan<BroadPair> candidates = Grid.Pairs;

            for (int p = 0; p < candidates.Length; p++)
            {
                if (Resolve(candidates[p].A, candidates[p].B))
                {
                    contacts++;
                }
            }
        }
        else
        {
            // --- Day 25 の総当たり ---
            //
            // j は i+1 から始める。**同じ組を2回試さないため**で、これだけで半分になる。
            // それでも O(n^2) であることは変わらない。
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    pairs++;

                    if (Resolve(i, j))
                    {
                        contacts++;
                    }
                }
            }
        }

        _pairTests = pairs;
        _contacts = contacts;
        _broadphaseLastMilliseconds = broadphaseMilliseconds;
        _broadphaseMilliseconds = (_broadphaseMilliseconds * 0.9) + (broadphaseMilliseconds * 0.1);
        _collisionMilliseconds = (_collisionMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);

        // 1組ぶんの判定と押し戻し。**方式が変わってもここは同じ**なので、
        // 総当たりとグリッドで結果が食い違わない(F12 で確かめる)。
        //
        // 押し戻しで位置が動くと、その体の外接 AABB は古くなる。
        // グリッドはステップの頭の位置で組まれているので、
        // **押し戻した先で新しく重なった組はこのステップでは拾えない**。
        // 押し戻し量は 1 ステップぶんの重なりぶんしかないので実用上は問題にならず、
        // 直すとしたら「押し戻しを後段にまとめる」形になる(Phase 7 のインパルス解決がその形)。
        static bool Resolve(int i, int j)
        {
            Contact2D contact = Test(in _bodies[i], in _bodies[j]);
            if (!contact.Hit)
            {
                return false;
            }

            _bodies[i].Contacts++;
            _bodies[j].Contacts++;

            if (_resolveOverlap)
            {
                // **半分ずつ押し戻す**。片方だけ動かすと、
                // 壁際で押された側が壁にめり込む。
                // 質量を持たせるなら比率を変えるが、それは Phase 7 の話。
                Vector2 push = contact.Normal * (contact.Depth * 0.5f);
                _bodies[i].Position -= push;
                _bodies[j].Position += push;
            }

            return true;
        }
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

    /// <summary>
    /// 壁に当たった音を要求する。**要求しても鳴るとは限らない**。
    ///
    /// ここでやっていることは3つ。
    ///   - <b>左右に振る</b>: 画面の X 位置を -1〜+1 に写して <c>pan</c> に渡す
    ///   - <b>大きさでピッチを変える</b>: 小さい体ほど高い音。**同じ音源が別の物に聞こえる**
    ///   - <b>速さで音量を変える</b>: 速く当たったほど大きい。物理量を音に写す最小の形
    ///
    /// このどれもが「1つの WAV を使い回す」ための工夫で、
    /// **音の種類を増やすより、1つを変化させるほうが安上がりで効果が高い**。
    /// 実際のゲームでも、足音1つに対して 4〜6 個の波形を用意して
    /// ランダムに選び、さらにピッチと音量を振る、という作りが定番になっている。
    /// </summary>
    private static void PlayBounce(in Body body, Vector2 bounds)
    {
        _soundRequests++;

        float pan = _panning
            ? Math.Clamp((body.Position.X / MathF.Max(bounds.X, 1.0f) * 2.0f) - 1.0f, -1.0f, 1.0f)
            : 0.0f;

        // 半径 4〜21px を、ピッチ 1.6 倍〜0.7 倍へ写す。
        float size = Math.Clamp(body.HalfSize.X, 4.0f, 21.0f);
        float pitch = 1.6f - ((size - 4.0f) / 17.0f * 0.9f);

        float speed = body.Velocity.Length();
        float volume = Math.Clamp(speed / 140.0f, 0.15f, 1.0f);

        _audio.Play(_bounceClip, volume * 0.5f, pitch, pan);
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

        if (_showCells && _broadphase == Broadphase.UniformGrid)
        {
            RenderCells();
        }

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

    /// <summary>
    /// グリッドのマスを描く(F11)。**混んでいるマスほど赤くする**。
    ///
    /// 数字を見るより、こちらのほうが分かることが多い。
    ///   - マスを小さくしすぎると、**1個の体が何枚ものマスにまたがる**のが見える
    ///   - マスを大きくしすぎると、**画面全体が数枚の赤いマス**になる
    ///   - 体が固まっている場所だけ赤くなり、**均一グリッドの弱点**(偏り)が見える
    ///
    /// 可視化は「性能の道具」でもある。プロファイラの数字だけでは
    /// 「なぜ遅いのか」までは分からない。
    /// </summary>
    private static void RenderCells()
    {
        float cell = Grid.CellSize;

        // まだ1ステップも回っていなければ格子は空。
        // F6 を押した直後の1フレームがここに来る(描画が更新より先に走ることがある)。
        if (Grid.EntryCount == 0)
        {
            return;
        }

        // マスが細かすぎると描画のほうが重くなるので、そこは正直に諦める。
        if (Grid.CellCount > 6000)
        {
            return;
        }

        for (int row = 0; row < Grid.Rows; row++)
        {
            for (int column = 0; column < Grid.Columns; column++)
            {
                int inCell = Grid.CellContents(column, row).Length;
                if (inCell == 0)
                {
                    continue;
                }

                // 4個で赤に振り切る。**「1マスに4個」が総当たりに戻り始める目安**なので、
                // 赤い場所が広がっていたらマスを小さくする合図になる。
                float heat = MathF.Min(inCell / 4.0f, 1.0f);

                var color = new Vector4(
                    0.20f + (0.65f * heat),
                    0.55f - (0.40f * heat),
                    0.85f - (0.70f * heat),
                    0.10f + (0.22f * heat));

                Submit(
                    BoxSprite,
                    new Vector2((column + 0.5f) * cell, (row + 0.5f) * cell),
                    new Vector2(cell - 1.0f),
                    0.0f,
                    color,

                    // 体(0.6)より奥に置く。手前だと体が見えなくなる。
                    0.2f);
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
        _activeBodies = Math.Clamp(count, 0, MaxBodies);
        InitializeBodies(_activeBodies);

        long allPairs = (long)_activeBodies * (_activeBodies - 1) / 2;
        Console.WriteLine($"[collision] {_activeBodies} 体 / 総当たりなら {allPairs:N0} 組");

        // **総当たりのまま数千体に上げると、その場で数百ミリ秒かかる**。
        // 気づかずに「グリッドも遅い」と誤解しないよう、ここで断っておく。
        if (_broadphase == Broadphase.BruteForce && _activeBodies > 2000)
        {
            Console.WriteLine("            総当たりのままです。F10 でグリッドに切り替えると軽くなります");
        }
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
    /// **ブロードフェーズの自己チェック**(F12)。今日いちばん大事な関数。
    ///
    /// ブロードフェーズのバグは<b>絵に出ない</b>。
    /// 組をひとつ取りこぼしても、その瞬間に1組がすり抜けるだけで、
    /// 何百体も飛び交っていれば誰も気づかない。
    /// 気づくのは「たまに敵が壁を抜ける」とバグ報告が来たときになる。
    ///
    /// だから確かめ方はひとつしかない——
    /// <b>総当たりと同じ答えを出すことを、機械に確かめさせる</b>。
    /// 総当たりは遅いが**絶対に正しい**ので、正解表として使える。
    /// 最適化を入れるときは、いつもこの形にする(Day 25 の「SAT と AABB の一致」も同じ)。
    /// </summary>
    private static void RunBroadphaseCheck()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("[ブロードフェーズの自己チェック]");

        var grid = new SpatialGrid();
        var world = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        // --- 1. 手で並べた小さな例 ---
        //
        // 番号と期待する組を先に決めておく。**乱数の例だけだと、
        // 落ちたときに何が悪いのか分からない**。
        Aabb2D[] boxes =
        [
            Aabb2D.FromCenter(new Vector2(50.0f, 50.0f), new Vector2(10.0f)),    // 0
            Aabb2D.FromCenter(new Vector2(64.0f, 50.0f), new Vector2(10.0f)),    // 1: 0 と重なる
            Aabb2D.FromCenter(new Vector2(300.0f, 300.0f), new Vector2(120.0f)), // 2: マスをまたぐ大物
            Aabb2D.FromCenter(new Vector2(400.0f, 400.0f), new Vector2(8.0f)),   // 3: 2 の中にいる
            Aabb2D.FromCenter(new Vector2(-40.0f, -40.0f), new Vector2(10.0f)),  // 4: 世界の外
            Aabb2D.FromCenter(new Vector2(-30.0f, -40.0f), new Vector2(10.0f)),  // 5: 世界の外で 4 と重なる
            Aabb2D.FromCenter(new Vector2(10.0f, 10.0f), new Vector2(2.0f)),     // 6: 同じマスだが
            Aabb2D.FromCenter(new Vector2(28.0f, 28.0f), new Vector2(2.0f)),     // 7: 6 とは離れている
        ];

        grid.Configure(Vector2.Zero, world, 32.0f);
        grid.Build(boxes);
        int found = grid.CollectPairs(boxes);

        Check("小さな例: 組は 3 つ", found == 3, $"{found} 組: {PairsToText(grid.Pairs)}");
        Check("小さな例: 隣り合う小物(0,1)", HasPair(grid.Pairs, 0, 1));
        Check("小さな例: マスをまたぐ大物(2,3)", HasPair(grid.Pairs, 2, 3));

        // 世界の外は端のマスへ丸めている。**落ちないだけでなく、組も拾えること**。
        Check("小さな例: 世界の外でも拾う(4,5)", HasPair(grid.Pairs, 4, 5));

        // 同じマスにいるだけでは候補にしない。AABB の足切りが効いているか。
        Check("小さな例: 同じマスでも離れていれば候補にしない(6,7)", !HasPair(grid.Pairs, 6, 7));
        Check("小さな例: 足切り前は同居していた", grid.CoLocatedPairs > found, $"同居 {grid.CoLocatedPairs} 組");

        // またがるぶん、登録の総数は体数より多くなる。
        Check("小さな例: 大物が複数マスに登録されている", grid.EntryCount > boxes.Length,
            $"登録 {grid.EntryCount} / 体 {boxes.Length}");

        Check("空でも落ちない", SafeEmpty(grid), "0 体");

        // --- 2. 乱数の配置で、総当たりと突き合わせる ---
        //
        // ここが本番。**グリッドが出した組の集合 == AABB が重なる組の集合**でなければならない。
        // 余分に出す(false positive)のは許されるが、
        // ここでは AABB で足切りまでしているので、集合として一致するのが正しい。
        InitializeBodies(1000);
        int count = 1000;
        var probe = new Aabb2D[count];
        for (int i = 0; i < count; i++)
        {
            probe[i] = Aabb2D.FromCenter(_bodies[i].Position, BoundsExtent(_bodies[i]));
        }

        List<long> expected = BruteForcePairs(probe);

        // **マスの大きさを変えても答えは変わらない**。
        // 変わるなら、それは性能の調整つまみではなく仕様バグ。
        foreach (float cellSize in (float[])[4.0f, 13.0f, 32.0f, 100.0f, 4000.0f])
        {
            grid.Configure(Vector2.Zero, world, cellSize);
            grid.Build(probe);
            grid.CollectPairs(probe);

            List<long> actual = [];
            foreach (BroadPair pair in grid.Pairs)
            {
                actual.Add(Key(pair.A, pair.B));
            }

            actual.Sort();
            bool unique = true;
            for (int i = 1; i < actual.Count; i++)
            {
                unique &= actual[i] != actual[i - 1];
            }

            Check($"マス {cellSize,6:F0}px: 総当たりと同じ組", Same(expected, actual),
                $"{actual.Count} 組(正解 {expected.Count} 組)");
            Check($"マス {cellSize,6:F0}px: 重複した組が無い", unique);
        }

        // --- 3. 押し戻しまで含めて、1ステップの結果が一致するか ---
        //
        // 組が同じでも、**呼ぶ順番が違えば押し戻しの結果は変わりうる**。
        // 総当たりは (0,1),(0,2)... の順、グリッドはマスの並び順になる。
        // 押し戻しは他の組に影響するので、順番が違えば最終位置も少しずれる。
        // ここでは押し戻しを切って「接触した組の集合」だけを比べる。
        bool savedResolve = _resolveOverlap;
        Broadphase savedPhase = _broadphase;
        float savedCell = _cellSizeOverride;
        _resolveOverlap = false;

        InitializeBodies(500);
        _activeBodies = 500;

        _broadphase = Broadphase.BruteForce;
        UpdateBodies(1.0f / 60.0f, world);
        int bruteContacts = _contacts;
        long bruteTests = _pairTests;

        InitializeBodies(500);
        _broadphase = Broadphase.UniformGrid;
        _cellSizeOverride = 0.0f;
        UpdateBodies(1.0f / 60.0f, world);

        Check("1ステップの接触数が一致", _contacts == bruteContacts,
            $"総当たり {bruteContacts} / グリッド {_contacts}");
        Check("ナローフェーズの回数は減っている", _pairTests < bruteTests,
            $"{bruteTests:N0} → {_pairTests:N0}({100.0 - (_pairTests * 100.0 / bruteTests):F1}% 削減)");

        _resolveOverlap = savedResolve;
        _broadphase = savedPhase;
        _cellSizeOverride = savedCell;

        Console.WriteLine(failures == 0 ? "  すべて合格" : $"  {failures} 件 不合格");
        Console.WriteLine();

        BenchmarkBroadphase();

        // 触った状態を戻す。
        InitializeBodies(_activeBodies);

        static long Key(int a, int b) => ((long)a << 32) | (uint)b;

        static bool HasPair(ReadOnlySpan<BroadPair> pairs, int a, int b)
        {
            foreach (BroadPair pair in pairs)
            {
                if (pair.A == a && pair.B == b)
                {
                    return true;
                }
            }

            return false;
        }

        static string PairsToText(ReadOnlySpan<BroadPair> pairs)
        {
            var text = new System.Text.StringBuilder();
            foreach (BroadPair pair in pairs)
            {
                text.Append($"({pair.A},{pair.B})");
            }

            return text.ToString();
        }

        static bool SafeEmpty(SpatialGrid grid)
        {
            grid.Build([]);
            return grid.CollectPairs([]) == 0;
        }

        static List<long> BruteForcePairs(ReadOnlySpan<Aabb2D> boxes)
        {
            List<long> pairs = [];
            for (int i = 0; i < boxes.Length; i++)
            {
                for (int j = i + 1; j < boxes.Length; j++)
                {
                    if (Collision2D.Overlap(boxes[i], boxes[j]))
                    {
                        pairs.Add(Key(i, j));
                    }
                }
            }

            pairs.Sort();
            return pairs;
        }

        static bool Same(List<long> a, List<long> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

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
    /// 総当たりとグリッドを、体数とマスの大きさの両方で振って測る。
    ///
    /// 見たいのは2つ。
    ///   1. <b>体数を倍にしたとき、時間が何倍になるか</b>
    ///      総当たりは 4 倍(O(n^2))。グリッドは**密度が一定なら 2 倍**(O(n))
    ///   2. <b>マスの大きさで、どれくらい変わるか</b>
    ///      小さすぎ・大きすぎの両側で遅くなる谷型になる
    ///
    /// **測る前に全部の構成を1回空回し**しているのは Day 25 で踏んだ罠のため。
    /// .NET は同じコードを何度も呼ぶと途中でより良い機械語に差し替える(段階的 JIT)ので、
    /// 温める前に測ると最初の1件だけ遅く出る。
    /// </summary>
    private static void BenchmarkBroadphase()
    {
        Broadphase savedPhase = _broadphase;
        float savedCell = _cellSizeOverride;
        int savedBodies = _activeBodies;
        int savedMix = _shapeMix;
        bool savedResolve = _resolveOverlap;
        bool savedFixedSize = _fixedBodySize;

        _shapeMix = 0;
        _resolveOverlap = true;
        _fixedBodySize = false;

        var world = new Vector2(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        int[] counts = [250, 500, 1000, 2000, 4000, 8000, 16000];

        // **温め**。ここを削ると最初の1〜2行だけ 2 倍遅く出る。
        //
        // .NET は起動直後、まず「そこそこの機械語」(tier 0)で走り、
        // 何度も呼ばれたものだけを後から最適化した版に差し替える。
        // 差し替えの判定は**アプリが落ち着いてから**始まるので、
        // 起動直後にいきなり測ると、最初に測った構成だけ古い機械語で走ったまま終わる。
        // Day 25 で「120 体だけ 58ns」と出たのがこれ(計画書の「検証の途中で分かったこと」)。
        Sample(1000, Broadphase.BruteForce, 0.0f, 10);
        Sample(1000, Broadphase.UniformGrid, 0.0f, 30);
        Sample(250, Broadphase.BruteForce, 0.0f, 10);
        Sample(250, Broadphase.UniformGrid, 0.0f, 10);

        Console.WriteLine("### 体数を振る(形は混在、押し戻しあり)。数秒かかります ###");
        Console.WriteLine("   体数 |     総当たり |   グリッド |   うち広域 |     候補 |  接触 |  倍率");

        foreach (int count in counts)
        {
            // 総当たりは 4000 を超えると 1 ステップに 0.2 秒かかる。**測るだけで待たされる**ので、
            // そこから上はグリッドだけにする。この「測れない」こと自体が今日の結論でもある。
            double brute = count <= 4000
                ? Sample(count, Broadphase.BruteForce, 0.0f, StepsFor(count)).Total
                : double.NaN;

            (double Total, double Broad, long Pairs, int Contacts) grid =
                Sample(count, Broadphase.UniformGrid, 0.0f, GridStepsFor(count));

            string bruteText = double.IsNaN(brute) ? "     —" : $"{brute,8:F2}ms";
            string ratio = double.IsNaN(brute) ? "    —" : $"{brute / grid.Total,5:F1}x";

            Console.WriteLine(
                $"  {count,5} | {bruteText} | {grid.Total,7:F2}ms | {grid.Broad,7:F2}ms | "
                + $"{grid.Pairs,8:N0} | {grid.Contacts,5} | {ratio}");
        }

        Console.WriteLine();
        Console.WriteLine("### マスの大きさを振る(4000 体)###");
        Console.WriteLine("   マス |    格子 |   登録 | 最大/マス |     同居 |     候補 |   広域 |    合計");

        foreach (float cellSize in (float[])[4.0f, 8.0f, 16.0f, 32.0f, 64.0f, 128.0f, 256.0f])
        {
            (double Total, double Broad, long Pairs, int Contacts) result =
                Sample(4000, Broadphase.UniformGrid, cellSize, 20);

            Console.WriteLine(
                $"  {cellSize,5:F0} | {Grid.Columns,3}x{Grid.Rows,-3} | {Grid.EntryCount,6:N0} | "
                + $"{Grid.MaxPerCell,9} | {Grid.CoLocatedPairs,8:N0} | {result.Pairs,8:N0} | "
                + $"{result.Broad,5:F2}ms | {result.Total,6:F2}ms");
        }

        Console.WriteLine();
        Console.WriteLine("  自動で選ばれる値: "
            + $"{SpatialGrid.SuggestCellSize(_bodyBounds.AsSpan(0, Math.Min(4000, _bodyBounds.Length))):F1}px");

        // --- 密度を上げるとどうなるか ---
        //
        // ここまでは体を増やすたびに小さくして、**画面の詰まり具合を一定に保っていた**。
        // 大きさを固定したまま増やすと密度が上がり、
        // 1マスに入る数が増えて、グリッドの中で総当たりが始まる。
        // **空間分割は「疎である」ことに賭けた最適化**で、賭けが外れると効かない。
        Console.WriteLine();
        Console.WriteLine("### 大きさを固定したまま増やす(密度が上がる)###");
        Console.WriteLine("   体数 |   グリッド |     候補 |   接触 | 候補/体 | 最大/マス");

        _fixedBodySize = true;
        foreach (int count in (int[])[2000, 4000, 8000])
        {
            (double Total, double Broad, long Pairs, int Contacts) dense =
                Sample(count, Broadphase.UniformGrid, 0.0f, 20);

            Console.WriteLine(
                $"  {count,5} | {dense.Total,7:F2}ms | {dense.Pairs,8:N0} | {dense.Contacts,6} | "
                + $"{(double)dense.Pairs / count,7:F1} | {Grid.MaxPerCell,9}");
        }

        _fixedBodySize = false;
        Console.WriteLine();

        _broadphase = savedPhase;
        _cellSizeOverride = savedCell;
        _activeBodies = savedBodies;
        _shapeMix = savedMix;
        _resolveOverlap = savedResolve;
        _fixedBodySize = savedFixedSize;

        // **回数は「合計でどれくらい時間を使うか」で決める**。
        // 一定回数にすると、軽い構成は測定誤差だらけになり(1ステップ 0.08ms を
        // 20 回では 1.6ms しか測っていない)、重い構成は待たされる。
        // 総当たりは組の数、グリッドは体数がだいたいの重さになるので、それで割る。
        static int StepsFor(int count)
        {
            long pairs = (long)count * (count - 1) / 2;
            return (int)Math.Clamp(20_000_000 / Math.Max(pairs, 1), 3, 60);
        }

        static int GridStepsFor(int count) => Math.Clamp(400_000 / Math.Max(count, 1), 20, 200);

        (double Total, double Broad, long Pairs, int Contacts) Sample(
            int count, Broadphase mode, float cellSize, int steps)
        {
            _broadphase = mode;
            _cellSizeOverride = cellSize;
            _activeBodies = count;
            InitializeBodies(count);

            double broad = 0.0;
            var stopwatch = Stopwatch.StartNew();

            for (int step = 0; step < steps; step++)
            {
                UpdateBodies(1.0f / 60.0f, world);
                broad += _broadphaseLastMilliseconds;
            }

            return (stopwatch.Elapsed.TotalMilliseconds / steps, broad / steps, _pairTests, _contacts);
        }
    }

    /// <summary>
    /// **卒業制作の自己チェック**(Tab)。
    ///
    /// ゲームは<b>窓を出さずに回せる</b>。
    /// <see cref="SurvivorGame"/> が GL も <c>Silk.NET</c> も知らないので、
    /// 入力を作って <c>Update</c> を呼ぶだけで何分ぶんでも進められる。
    /// **これが「エンジンとゲームを分ける」ことの実利**で、
    /// 遊んで確かめるしかない状態だと、
    /// 「5分後に敵が何体になるか」を知るのに毎回5分かかる。
    ///
    /// 見ているのは3種類。
    ///   1. <b>壊れないこと</b> — 上限を超えない、NaN が出ない、消し忘れが無い
    ///   2. <b>ゲームとして成立すること</b> — 動かなければ死ぬ、倒せばレベルが上がる
    ///   3. <b>決定性</b> — 同じ入力なら同じ結果(Day 19 の要点6)
    /// </summary>
    private static void RunGameCheck()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("[卒業制作の自己チェック]");

        var viewSize = new Vector2(960.0f, 640.0f);

        // --- 1. 何もしないと死ぬ ---
        //
        // **ゲームとして成立する最低条件**。放置して生き延びるなら、
        // 難易度曲線が壊れているか、当たり判定が効いていない。
        var idle = new SurvivorGame();
        idle.Start(viewSize);
        int idleSteps = RunSteps(idle, 60 * 600, _ => GameAction.None);

        Check("放置すると死ぬ", idle.Phase == GamePhase.GameOver,
            $"{idle.Elapsed:F1}秒で力尽きた({idleSteps} ステップ)");

        // --- 2. 動き続けると長く生きる ---
        //
        // 逃げ回れば生き延びられること。**操作に意味がある**ことの確認で、
        // ここが同じなら、遊ぶ側の判断がスコアに効いていない。
        var moving = new SurvivorGame();
        moving.Start(viewSize);
        RunSteps(moving, 60 * 600, Circle);

        Check("逃げ回ると長く生きる", moving.Elapsed > idle.Elapsed,
            $"放置 {idle.Elapsed:F1}秒 → 移動 {moving.Elapsed:F1}秒");

        // --- 3. 壊れていないこと ---
        var game = new SurvivorGame();
        game.Start(viewSize);
        RunSteps(game, 60 * 150, Circle);

        Check("敵が配列を超えない", game.EnemyCount <= GameBalance.MaxEnemies,
            $"{game.EnemyCount} / {GameBalance.MaxEnemies}");
        Check("弾が配列を超えない", game.ProjectileCount <= GameBalance.MaxProjectiles,
            $"{game.ProjectileCount} / {GameBalance.MaxProjectiles}");
        Check("ジェムが配列を超えない", game.GemCount <= GameBalance.MaxGems,
            $"{game.GemCount} / {GameBalance.MaxGems}");

        // **死んだ敵が残っていないこと**。
        // 末尾と入れ替えて縮める書き方は、`i--` を忘れると取りこぼす。
        // 体力が 0 以下の敵が残っていたら、それが起きている。
        bool allAlive = true;
        bool finite = true;
        for (int i = 0; i < game.EnemyCount; i++)
        {
            allAlive &= game.Enemies[i].Health > 0.0f;
            finite &= float.IsFinite(game.Enemies[i].Position.X)
                && float.IsFinite(game.Enemies[i].Position.Y);
        }

        Check("倒した敵が配列に残っていない", allAlive, $"生存 {game.EnemyCount} 体");

        // 押し合いは中心が重なると向きが決まらない。
        // Day 25 で NaN を潰してあるので、ここは通るはず(通らなければ退化ケースの取りこぼし)。
        Check("押し合いで座標が壊れない", finite);
        Check("プレイヤーの座標が壊れない",
            float.IsFinite(game.PlayerPosition.X) && float.IsFinite(game.PlayerPosition.Y),
            $"{game.PlayerPosition}");

        // --- 4. 遊びとして進むこと ---
        Check("敵を倒せている", game.Kills > 0, $"{game.Kills} 体");
        Check("レベルが上がる", game.Level > 1, $"Lv.{game.Level}");
        Check("敵が押し寄せている", game.EnemyCount > 20, $"{game.EnemyCount} 体");

        // --- 5. 決定性(Day 19 の要点6)---
        //
        // 同じ入力を与えれば同じ結果になること。
        // **これが崩れるとリプレイもテストも成り立たない**。
        var runA = new SurvivorGame();
        var runB = new SurvivorGame();
        runA.Start(viewSize);
        runB.Start(viewSize);
        RunSteps(runA, 60 * 45, Circle);
        RunSteps(runB, 60 * 45, Circle);

        Check("同じ入力なら同じ結果",
            runA.Kills == runB.Kills
                && runA.EnemyCount == runB.EnemyCount
                && runA.Level == runB.Level
                && runA.PlayerPosition == runB.PlayerPosition,
            $"撃破 {runA.Kills}/{runB.Kills}  敵 {runA.EnemyCount}/{runB.EnemyCount}");

        // --- 6. 格子が効いていること ---
        //
        // 押し合いの候補が、総当たりの組数よりはっきり少ないこと。
        // **ここが同じなら空間分割が働いていない**(Day 26 の要点6と同じ趣旨)。
        long allPairs = (long)game.EnemyCount * (game.EnemyCount - 1) / 2;
        Check("格子で候補が減っている", game.PairCandidates < allPairs / 4,
            $"{game.PairCandidates:N0} 組(総当たりなら {allPairs:N0} 組)");

        Console.WriteLine(failures == 0 ? "  すべて合格" : $"  {failures} 件 不合格");
        Console.WriteLine();

        BenchmarkGame();

        // **逃げ回る入力**。決定性のために、乱数ではなく step から決める。
        static GameAction Circle(int step) => KitePattern(step);

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
    /// 自己チェックと計測で使う「遊んでいる人の代わり」。
    ///
    /// **大きく回り込む**動き。1 辺 2.5 秒(450px)なので、
    /// 追ってくる敵の塊から実際に抜けられる。
    /// 最初は 1 辺 0.75 秒にしていて、それだと**その場で足踏みしているのと同じ**になり、
    /// 「逃げても放置しても同じ秒数で死ぬ」という結果が出た。
    /// 自動で遊ばせるときは、**その動きが人の遊び方に似ているか**を疑うこと。
    /// </summary>
    private static GameAction KitePattern(int step) => ((step / 200) % 8) switch
    {
        0 => GameAction.MoveRight,
        1 => GameAction.MoveRight | GameAction.MoveDown,
        2 => GameAction.MoveDown,
        3 => GameAction.MoveDown | GameAction.MoveLeft,
        4 => GameAction.MoveLeft,
        5 => GameAction.MoveLeft | GameAction.MoveUp,
        6 => GameAction.MoveUp,
        _ => GameAction.MoveUp | GameAction.MoveRight,
    };

    /// <summary>
    /// ゲームを指定ステップぶん進める。**窓も GL も要らない**。
    /// 戻り値は実際に進んだステップ数(途中で死んだらそこで止まる)。
    /// </summary>
    private static int RunSteps(SurvivorGame game, int steps, Func<int, GameAction> input)
    {
        const float dt = 1.0f / 60.0f;

        for (int step = 0; step < steps; step++)
        {
            if (game.Phase != GamePhase.Playing)
            {
                return step;
            }

            var snapshot = new InputSnapshot(
                input(step), GameAction.None, GameAction.None, Vector2.Zero, Vector2.Zero, 0.0f);

            game.Update(dt, snapshot);
        }

        return steps;
    }

    /// <summary>
    /// ゲームの重さを測る。**「何体まで捌けるか」を知るため**。
    ///
    /// 遊んでいるときの 1 ステップは 0.5ms 前後だが、
    /// それは「今その体数だから」でしかない。
    /// 時間を進めて体数が増えたときにどうなるかは、
    /// **回してみないと分からない**。
    /// </summary>
    private static void BenchmarkGame()
    {
        var viewSize = new Vector2(960.0f, 640.0f);

        // 温め。Day 26 で踏んだ段階的 JIT の罠を避ける。
        var warm = new SurvivorGame();
        warm.Start(viewSize);
        RunSteps(warm, 60 * 20, KitePattern);

        Console.WriteLine("### 時間が進むとどうなるか(逃げ続けた場合)###");
        Console.WriteLine("   経過 |   敵 |  弾 | ジェム |   候補 | 最大/マス |   撃破 | Lv | 1ステップ");

        var game = new SurvivorGame();
        game.Start(viewSize);

        int previous = 0;

        foreach (int seconds in (int[])[15, 30, 60, 90, 120, 150, 180, 210, 240, 300])
        {
            int target = seconds * 60;
            int steps = target - previous;
            previous = target;

            var stopwatch = Stopwatch.StartNew();
            RunSteps(game, steps, KitePattern);
            stopwatch.Stop();

            if (game.Phase != GamePhase.Playing)
            {
                Console.WriteLine($"  {seconds,4}s | ここで力尽きた({game.Elapsed:F1}秒 / 撃破 {game.Kills})");
                break;
            }

            Console.WriteLine(
                $"  {seconds,4}s | {game.EnemyCount,4} | {game.ProjectileCount,3} | {game.GemCount,5} | "
                + $"{game.PairCandidates,6:N0} | {game.GridMaxPerCell,9} | {game.Kills,6} | {game.Level,2} | "
                + $"{stopwatch.Elapsed.TotalMilliseconds / steps,6:F3}ms");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// **テキストの自己チェック**(スラッシュ)。
    ///
    /// 文字の不具合は目で見れば分かる——ように思えるが、
    /// 「1px にじんでいる」「行送りが 1px 足りない」は気づけない。
    /// そして**気づけないまま画面いっぱいに広がる**のがテキストの厄介なところで、
    /// UI を組んだあとに直すと全部の座標を調整し直すことになる。
    ///
    /// だから測れるものは測る。ここで見ているのは3種類。
    ///   1. フォントから読んだ数字が筋の通った値か(ascent &gt; 0 など)
    ///   2. アトラスが**同じものを2度焼かない**か
    ///   3. <c>Measure</c> と <c>Draw</c> が**同じ答え**を出すか
    /// 3 が狂うと、枠から字がはみ出したり、中央ぞろえがずれたりする。
    /// </summary>
    private static void RunTextCheck()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("[テキストの自己チェック]");

        if (_font is null || _glyphAtlas is null || _text is null)
        {
            Console.WriteLine("  フォントが無いので飛ばします");
            return;
        }

        FontFace font = _font;
        GlyphAtlas atlas = _glyphAtlas;
        TextRenderer text = _text;

        Console.WriteLine($"  フォント: {font.Name}({font.FaceCount} 面中 {font.FaceIndex} 番)");

        // --- 1. メトリクス ---
        float scale = font.ScaleFor(32.0f);
        float ascent = font.Ascent(scale);
        float descent = font.Descent(scale);
        float lineHeight = font.LineHeight(scale);

        Check("ascent は正", ascent > 0.0f, $"{ascent:F2}px");
        Check("descent は正(下向きの量として)", descent > 0.0f, $"{descent:F2}px");

        // ScaleFor は「ascent + descent が指定の高さになる」倍率を返す。
        Check("32px 指定で ascent+descent が 32px", MathF.Abs(ascent + descent - 32.0f) < 0.5f,
            $"{ascent + descent:F2}px");

        // 行送りは字の高さ以上。**lineGap が 0 のフォントでは等しくなる**。
        Check("行送り >= ascent+descent", lineHeight >= ascent + descent - 0.01f,
            $"行送り {lineHeight:F2}px / lineGap {font.LineGap(scale):F2}px");

        // --- 2. グリフの有無 ---
        Check("英字を持っている", font.HasGlyph('A'));
        Check("ひらがなを持っている", font.HasGlyph(0x3042), font.HasGlyph(0x3042) ? "あ" : "なし");
        Check("漢字を持っている", font.HasGlyph(0x6F22), font.HasGlyph(0x6F22) ? "漢" : "なし");

        // 絵文字は日本語フォントには入っていない。**入っていないことを確かめておく**と、
        // 豆腐が出たときに「バグ」ではなく「そういうもの」だと分かる。
        Console.WriteLine($"  [--] 絵文字 U+1F600: {(font.HasGlyph(0x1F600) ? "あり" : "なし(豆腐になる)")}");

        // --- 3. 空白は送りだけ持つ ---
        Glyph space = atlas.GetOrAdd(' ', 16);
        Check("空白は絵を持たないが送りはある", !space.HasPixels && space.Metrics.Advance > 0.0f,
            $"送り {space.Metrics.Advance:F2}px");

        // --- 4. アトラスのキャッシュ ---
        int before = atlas.BakedTotal;
        atlas.GetOrAdd(0x6F22, 16);
        int afterFirst = atlas.BakedTotal;
        atlas.GetOrAdd(0x6F22, 16);
        int afterSecond = atlas.BakedTotal;

        Check("2回目は焼き直さない", afterSecond == afterFirst, $"焼いた回数 {afterSecond - before}");

        // **大きさが違えば別の絵**。同じ文字でも焼き直しになる。
        atlas.GetOrAdd(0x6F22, 17);
        Check("大きさが違えば別のグリフ", atlas.BakedTotal == afterSecond + 1,
            $"16px と 17px で {atlas.BakedTotal - afterSecond} 回");

        // --- 5. UV がテクスチャの中に収まっているか ---
        Glyph kanji = atlas.GetOrAdd(0x6F22, 16);
        AtlasRegion region = kanji.Region;
        bool inside = region.UvMin.X >= 0.0f && region.UvMin.Y >= 0.0f
            && region.UvMax.X <= 1.0f && region.UvMax.Y <= 1.0f
            && region.UvMin.X < region.UvMax.X && region.UvMin.Y < region.UvMax.Y;
        Check("UV が 0..1 に収まっている", inside,
            $"({region.UvMin.X:F3},{region.UvMin.Y:F3})-({region.UvMax.X:F3},{region.UvMax.Y:F3})");

        // UV の幅は、グリフの画素幅をアトラスの一辺で割ったもの。
        float uvWidth = (region.UvMax.X - region.UvMin.X) * atlas.Size;
        Check("UV の幅が画素幅と一致", MathF.Abs(uvWidth - region.Width) < 0.01f,
            $"{uvWidth:F2}px / {region.Width}px");

        // --- 6. 幅が4の倍数でないグリフを焼いても GL がエラーを出さないか ---
        //
        // GL_UNPACK_ALIGNMENT の罠(Texture.UploadR8 のコメント)。
        // アライメントを直さないと、崩れるだけでエラーにはならないことも多いが、
        // ここでは「焼いたあとに GL のエラーが残っていない」ことを確かめておく。
        while (_gl.GetError() != GLEnum.NoError)
        {
            // 溜まっているぶんを捨てる
        }

        for (int i = 0; i < 32; i++)
        {
            atlas.GetOrAdd(0x4E00 + i, 15);
        }

        GLEnum glError = _gl.GetError();
        Check("グリフを焼いても GL エラーが出ない", glError == GLEnum.NoError, glError.ToString());

        // --- 7. Measure と Draw が一致するか ---
        const string sample = "Measure と Draw は同じ道を通る";
        Vector2 measured = text.Measure(sample, 16);

        _textBatch!.Begin(
            Camera.CreateScreen(0.0f, 100.0f, 100.0f, 0.0f, -1.0f, 1.0f),
            SpriteSortMode.Texture);
        Vector2 drawn = text.Draw(_textBatch, sample, Vector2.Zero, 16, Vector4.One);
        _textBatch.End();

        Check("Measure と Draw の大きさが一致",
            MathF.Abs(measured.X - drawn.X) < 0.01f && MathF.Abs(measured.Y - drawn.Y) < 0.01f,
            $"{measured.X:F2}x{measured.Y:F2}");

        // --- 8. 改行 ---
        Vector2 one = text.Measure("あいうえお", 16);
        Vector2 two = text.Measure("あいうえお\nかきくけこ", 16);
        Check("2行の高さは1行の2倍", MathF.Abs(two.Y - (one.Y * 2.0f)) < 0.01f,
            $"{one.Y:F2} → {two.Y:F2}");
        Check("2行の幅は広いほうの行", MathF.Abs(two.X - one.X) < 0.01f, $"{two.X:F2}px");

        // "\r\n" でも同じ結果になること。ファイルから読んだ文字列で効く。
        Check("CRLF でも同じ", MathF.Abs(text.Measure("あ\r\nい", 16).Y - text.Measure("あ\nい", 16).Y) < 0.01f);

        // --- 9. カーニング ---
        text.Kerning = false;
        float without = text.Measure("AVAV", 32).X;
        text.Kerning = true;
        float with = text.Measure("AVAV", 32).X;

        // 効かないフォントもある(GPOS しか持たない場合)ので、落とさず報告にとどめる。
        Console.WriteLine(
            $"  [--] カーニング: AVAV が {without:F2}px → {with:F2}px"
            + (with < without ? $"({without - with:F2}px 詰まった)" : "(このフォントでは効かない)"));

        // --- 10. サロゲートペア ---
        //
        // U+20B9F(𠮟)は char 2個で表される。char で回すと2文字として扱われ、
        // 両方とも豆腐になって幅が2倍になる。
        const string surrogate = "\U00020B9F";
        Check("サロゲートペアを1文字として数える", surrogate.Length == 2,
            $"char {surrogate.Length} 個 / 幅 {text.Measure(surrogate, 16).X:F2}px");

        // char で回していたら、この文字列は「2文字」として幅が2倍になる。
        // Rune で回していれば、グリフ1つぶんの送りと一致する。
        float pairWidth = text.Measure(surrogate, 16).X;
        float oneGlyph = atlas.GetOrAdd(0x20B9F, 16).Metrics.Advance;
        Check("幅がグリフ1つぶんと一致", MathF.Abs(pairWidth - oneGlyph) < 0.01f,
            $"{pairWidth:F2}px / 1グリフ {oneGlyph:F2}px");

        // --- 11. アトラスが満杯になっても落ちない ---
        using (var tiny = new GlyphAtlas(_gl, font, size: 64))
        {
            for (int i = 0; i < 200; i++)
            {
                tiny.GetOrAdd(0x4E00 + i, 32);
            }

            Check("満杯になっても落ちない", tiny.IsFull, $"{tiny.GlyphCount}字 / {tiny.ShelfCount}段");

            // 満杯でも**送りは正しい**ので、レイアウトは崩れない(絵が出ないだけ)。
            Glyph missing = tiny.GetOrAdd(0x9FA0, 32);
            Check("満杯でも送りは返す", missing.Metrics.Advance > 0.0f, $"{missing.Metrics.Advance:F2}px");
        }

        Console.WriteLine(failures == 0 ? "  すべて合格" : $"  {failures} 件 不合格");
        Console.WriteLine();

        BenchmarkText();

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
    /// 文字まわりのコストを測る。**「毎フレーム何文字まで出してよいか」を知るため**。
    ///
    /// 見たいのは「焼く」と「引く」の差。
    /// 初回だけ高くて2回目から安いなら、キャッシュが効いている証拠になる。
    /// 差が小さいなら、そもそもキャッシュを持つ意味が無い。
    /// </summary>
    private static void BenchmarkText()
    {
        FontFace font = _font!;
        TextRenderer text = _text!;

        Console.WriteLine("### 文字まわりのコスト ###");

        // --- 焼く ---
        //
        // 使い捨てのアトラスを作って、まだ焼いていない字を並べて焼く。
        // **本番のアトラスを汚さない**ようにするのと、
        // 「全部が初回」の条件をそろえるため。

        // まず ASCII だけ。**「起動時に全部焼く」が成立する側**の数字。
        using (var fresh = new GlyphAtlas(_gl, font, size: 512))
        {
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0x20; i < 0x7F; i++)
            {
                fresh.GetOrAdd(i, 16);
            }

            double total = stopwatch.Elapsed.TotalMilliseconds;
            Console.WriteLine(
                $"  焼く(ASCII 95字、16px): {total * 1000.0 / 95,7:F1}us  "
                + $"(合計 {total:F1}ms / 512px 中 使用率 {fresh.Usage:P1})");
        }

        using (var fresh = new GlyphAtlas(_gl, font, size: 1024))
        {
            // **常用漢字の数**(2136字)ぶん焼いてみる。
            // 「起動時に全部焼く」ならこれだけ待つことになる、という数字。
            const int count = 2136;
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                fresh.GetOrAdd(0x4E00 + i, 16);
            }

            double total = stopwatch.Elapsed.TotalMilliseconds;
            Console.WriteLine(
                $"  焼く(16px、初回)      : {total * 1000.0 / count,7:F1}us  "
                + $"({count}字で {total:F1}ms / 1024px 中 使用率 {fresh.Usage:P1})");
        }

        using (var fresh = new GlyphAtlas(_gl, font, size: 2048))
        {
            const int count = 500;
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                fresh.GetOrAdd(0x4E00 + i, 48);
            }

            double microseconds = stopwatch.Elapsed.TotalMilliseconds * 1000.0 / count;
            Console.WriteLine($"  焼く(48px、初回)      : {microseconds,7:F1}us");
        }

        // --- 引く(キャッシュに当たる) ---
        GlyphAtlas atlas = _glyphAtlas!;
        atlas.GetOrAdd(0x6F22, 16);
        Measure("引く(キャッシュあり)  ", 1_000_000, () => atlas.GetOrAdd(0x6F22, 16));

        // --- 測る / 積む ---
        const string line = "毎フレーム描く文字列の例 fps 512.3 更新 0.12ms";
        Measure("Measure(24文字)       ", 200_000, () => text.Measure(line, 16));

        // **カーニングを切って測り直す**。
        // stb のカーニングは、呼ぶたびに2文字ぶんのグリフ番号を引き直して
        // kern テーブルを二分探索する。1文字ごとに走るので、じわじわ効く。
        text.Kerning = false;
        Measure("Measure(カーニングなし)", 200_000, () => text.Measure(line, 16));
        text.Kerning = true;

        // --- 積む ---
        //
        // **Begin と End を計測から外す**。GL への送信はスプライトとまったく同じ経路で、
        // ここで見たいのは「文字列をクアッドの列に変えるコスト」のほうだから。
        Matrix4x4 projection = Camera.CreateScreen(0.0f, 960.0f, 640.0f, 0.0f, -1.0f, 1.0f);
        SpriteBatch batch = _textBatch!;

        text.Kerning = true;
        Console.WriteLine($"  Draw に積む(24文字)   : {MeasureDraw(),7:F0}ns");

        text.Kerning = false;
        Console.WriteLine($"  Draw(カーニングなし)  : {MeasureDraw(),7:F0}ns");
        text.Kerning = true;

        Console.WriteLine();

        double MeasureDraw()
        {
            const int rounds = 400;
            const int perRound = 100;
            var accumulated = new Stopwatch();

            for (int round = 0; round < rounds; round++)
            {
                batch.Begin(projection, SpriteSortMode.Texture);

                accumulated.Start();
                for (int i = 0; i < perRound; i++)
                {
                    text.Draw(batch, line, Vector2.Zero, 16, Vector4.One);
                }

                accumulated.Stop();
                batch.End();
            }

            return accumulated.Elapsed.TotalMilliseconds * 1e6 / (rounds * perRound);
        }

        static void Measure(string name, int count, Action action)
        {
            for (int i = 0; i < Math.Min(count / 10, 10000); i++)
            {
                action();
            }

            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                action();
            }

            double nanoseconds = stopwatch.Elapsed.TotalMilliseconds * 1e6 / count;
            Console.WriteLine($"  {name}: {nanoseconds,7:F0}ns");
        }
    }

    /// <summary>
    /// **オーディオの自己チェック**(F1)。
    ///
    /// 音のバグは「聞けば分かる」ように思えて、実はそうでもない。
    /// ボイスが枯れて鳴らなくなったのか、間引かれたのか、
    /// そもそも読み込みに失敗しているのか——**耳では区別が付かない**。
    /// 数で見られるようにしておく。
    /// </summary>
    private static void RunAudioCheck()
    {
        int failures = 0;

        Console.WriteLine();
        Console.WriteLine("[オーディオの自己チェック]");

        if (!_audio.IsAvailable)
        {
            Console.WriteLine("  デバイスが無いので飛ばします");
            return;
        }

        Console.WriteLine($"  デバイス: {_audio.DeviceName} / {_audio.Version}");

        // --- 1. WAV パーサ ---
        //
        // 素材はわざとフォーマットをばらしてある。**全部の経路を通す**ため。
        Expect("bounce.wav", 44100, 1, 16);
        Expect("hit.wav", 44100, 1, 16);
        Expect("pickup.wav", 22050, 1, 8);
        Expect("stereo-ping.wav", 44100, 2, 16);
        Expect("music-loop.wav", 22050, 1, 16);

        // **知らないチャンクを飛ばせるか**。ここが RIFF を扱ううえでの本題。
        byte[] withList = BuildWav(44100, 1, 16, new byte[400], "MADE BY HONYA");
        WavData listed = WavFile.Parse(withList, "LIST 付き");
        Check("知らないチャンク(LIST)を飛ばせる", listed.Data.Length == 400, $"{listed.Data.Length} バイト");

        // 奇数長のチャンクの後ろには詰め物が 1 バイト入る。
        // これを飛ばし忘れると、次のチャンク名が 1 バイトずれる。
        byte[] oddList = BuildWav(44100, 1, 16, new byte[200], "ODD");
        WavData odd = WavFile.Parse(oddList, "奇数長 LIST 付き");
        Check("奇数長チャンクの詰め物を飛ばせる", odd.Data.Length == 200, $"{odd.Data.Length} バイト");

        Check("WAV でないものを弾く", Throws<InvalidDataException>(
            () => WavFile.Parse(new byte[64])));

        Check("24bit を弾く", Throws<NotSupportedException>(
            () => WavFile.Parse(BuildWav(44100, 1, 24, new byte[300], null))));

        // --- 2. ボイスの管理 ---
        int savedLimit = _audio.MaxStartsPerClipPerStep;
        float savedVolume = _audio.MasterVolume;

        // 確かめている間は無音にする。**耳で聞くのはこの後**。
        _audio.MasterVolume = 0.0f;
        _audio.StopAll();
        _audio.Update();

        // 上限を外して、ボイスの数より多く鳴らす。
        _audio.MaxStartsPerClipPerStep = 0;
        for (int i = 0; i < _audio.VoiceCount + 8; i++)
        {
            _audio.Play(_hitClip, 1.0f);
        }

        int active = _audio.ActiveVoices;
        _audio.Update();

        Check("ボイスの数を超えない", active <= _audio.VoiceCount, $"{active} / {_audio.VoiceCount}");
        Check("足りなければ奪う", _audio.StolenLastStep == 8, $"{_audio.StolenLastStep} 回");

        // **古い札で別人を止めない**(Day 21 の世代と同じ問題)。
        _audio.StopAll();
        _audio.Update();
        VoiceId first = _audio.Play(_hitClip, 1.0f);
        _audio.Stop(first);
        VoiceId second = _audio.Play(_hitClip, 1.0f);
        _audio.Stop(first);
        Check("古い札は無効になっている", _audio.IsPlaying(second), $"{first} → {second}");

        // ループするものは奪われない。BGM が効果音に消されては困る。
        _audio.StopAll();
        _audio.Update();
        VoiceId loop = _audio.PlayLoop(_musicClip, 1.0f);
        for (int i = 0; i < _audio.VoiceCount * 2; i++)
        {
            _audio.Play(_hitClip, 1.0f);
        }

        Check("ループは奪われない", _audio.IsPlaying(loop), $"{loop}");
        _audio.Stop(loop);

        // --- 3. 間引き ---
        _audio.StopAll();
        _audio.Update();
        _audio.MaxStartsPerClipPerStep = 2;

        for (int i = 0; i < 10; i++)
        {
            _audio.Play(_hitClip, 1.0f);
        }

        _audio.Update();
        Check("1ステップに 2 回まで", _audio.StartedLastStep == 2, $"発音 {_audio.StartedLastStep}");
        Check("残りは間引かれる", _audio.CulledLastStep == 8, $"間引き {_audio.CulledLastStep}");

        // --- 4. 本当に鳴っているか ---
        _audio.StopAll();
        _audio.Update();
        _audio.MaxStartsPerClipPerStep = savedLimit;
        _audio.MasterVolume = savedVolume;

        VoiceId audible = _audio.Play(_pickupClip, 0.8f);
        Check("再生中の状態になる", _audio.IsPlaying(audible), $"{audible}");

        Console.WriteLine(failures == 0 ? "  すべて合格" : $"  {failures} 件 不合格");
        Console.WriteLine();

        BenchmarkAudio();

        _musicVoice = VoiceId.None;

        void Expect(string file, int rate, int channels, int bits)
        {
            WavData wav = WavFile.Load(ResolveAssetPath($"audio/{file}"));
            Check(
                $"{file,-16} {rate}Hz {channels}ch {bits}bit",
                wav.SampleRate == rate && wav.Channels == channels && wav.BitsPerSample == bits,
                $"{wav.Duration:F2}s / {wav.FrameCount:N0} フレーム");
        }

        static bool Throws<T>(Action action)
            where T : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (T)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        // メモリ上に WAV を組み立てる。**パーサを試すためだけ**の道具。
        static byte[] BuildWav(int rate, int channels, int bits, byte[] pcm, string? listText)
        {
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);

            writer.Write("RIFF"u8);
            writer.Write(0);
            writer.Write("WAVE"u8);

            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((ushort)1);
            writer.Write((ushort)channels);
            writer.Write(rate);
            writer.Write(rate * channels * bits / 8);
            writer.Write((ushort)(channels * bits / 8));
            writer.Write((ushort)bits);

            if (listText is not null)
            {
                byte[] payload = System.Text.Encoding.ASCII.GetBytes(listText);
                writer.Write("LIST"u8);
                writer.Write(payload.Length);
                writer.Write(payload);

                // **奇数長なら詰め物**。読む側と書く側の両方に同じ規則が要る。
                if ((payload.Length & 1) != 0)
                {
                    writer.Write((byte)0);
                }
            }

            writer.Write("data"u8);
            writer.Write(pcm.Length);
            writer.Write(pcm);

            byte[] bytes = stream.ToArray();
            BitConverter.TryWriteBytes(bytes.AsSpan(4), bytes.Length - 8);
            return bytes;
        }

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
    /// 音の呼び出しコストを測る。**「1ステップに何回まで呼んでよいか」を知るため**。
    ///
    /// Day 26 で 2 万体を動かせるようになったので、
    /// 「全部の衝突で音を鳴らす」と書くと 1 ステップに数万回呼ぶことになる。
    /// 1 回のコストが分かっていないと、その判断ができない。
    /// </summary>
    private static void BenchmarkAudio()
    {
        int savedLimit = _audio.MaxStartsPerClipPerStep;
        float savedVolume = _audio.MasterVolume;
        _audio.MasterVolume = 0.0f;
        _audio.StopAll();
        _audio.Update();

        Console.WriteLine("### 呼び出し 1 回あたりのコスト ###");

        // **間引かれる側**。予算を使い切ったあとの呼び出しはここを通る。
        _audio.MaxStartsPerClipPerStep = 1;
        _audio.Play(_hitClip, 1.0f);
        Measure("Play(間引かれる)", 200_000, () => _audio.Play(_hitClip, 1.0f));

        // **通る側・空きがあるとき**。ボイスが空いていれば設定して鳴らすだけ。
        // 計測の外で毎回ボイスを空にするので、奪う処理は入らない。
        _audio.MaxStartsPerClipPerStep = 0;
        MeasureInBatches("Play(空きあり)  ", 300, _audio.VoiceCount);

        // **通る側・埋まっているとき**。毎回どれかを止めて奪うことになる。
        Measure("Play(奪う)      ", 20_000, () => _audio.Play(_hitClip, 1.0f));

        Measure("Update()        ", 20_000, () => _audio.Update());

        Console.WriteLine();

        _audio.StopAll();
        _audio.Update();
        _audio.MaxStartsPerClipPerStep = savedLimit;
        _audio.MasterVolume = savedVolume;

        void MeasureInBatches(string name, int rounds, int perRound)
        {
            var stopwatch = new Stopwatch();

            for (int round = 0; round < rounds; round++)
            {
                // **ここは計測に入れない**。空きを作る手間まで含めると、
                // 「空いているときの Play」を測っていることにならない。
                _audio.StopAll();
                _audio.Update();

                stopwatch.Start();
                for (int i = 0; i < perRound; i++)
                {
                    _audio.Play(_hitClip, 1.0f);
                }

                stopwatch.Stop();
            }

            double nanoseconds = stopwatch.Elapsed.TotalMilliseconds * 1e6 / (rounds * perRound);
            Console.WriteLine($"  {name}: {nanoseconds,8:F0}ns");
        }

        static void Measure(string name, int count, Action action)
        {
            for (int i = 0; i < 1000; i++)
            {
                action();
            }

            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                action();
            }

            double nanoseconds = stopwatch.Elapsed.TotalMilliseconds * 1e6 / count;
            Console.WriteLine($"  {name}: {nanoseconds,8:F0}ns");
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

            case Key.F10:
                _broadphase = _broadphase == Broadphase.BruteForce
                    ? Broadphase.UniformGrid
                    : Broadphase.BruteForce;
                Console.WriteLine($"ブロードフェーズ: {(_broadphase == Broadphase.BruteForce ? "総当たり" : "均一グリッド")}");
                break;

            case Key.F11:
                _showCells = !_showCells;
                Console.WriteLine($"マスの可視化: {OnOff(_showCells)}");
                break;

            case Key.F12:
                RunBroadphaseCheck();
                break;

            case Key.Comma:
            case Key.Period:
                CycleCellSize(key == Key.Period);
                break;

            // --- 今日のスイッチ(音)---
            case Key.Number5:
                // **札を受け取らない**典型。鳴らしっぱなしで構わない音。
                _audio.Play(_pickupClip, 0.8f);
                break;

            case Key.Number6:
                _collisionSfx = !_collisionSfx;
                Console.WriteLine(
                    $"衝突音: {OnOff(_collisionSfx)}"
                    + (_collisionSfx && !_collisionDemo ? "  (F6 で衝突デモを出すと鳴ります)" : string.Empty));
                break;

            case Key.Number7:
                // 0 は無制限。**外すと何が起きるか**を聞くためのスイッチ。
                _audio.MaxStartsPerClipPerStep = _audio.MaxStartsPerClipPerStep switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 4,
                    4 => 8,
                    _ => 0,
                };
                Console.WriteLine(
                    _audio.MaxStartsPerClipPerStep == 0
                        ? "同じ音の上限: 無制限(割れます)"
                        : $"同じ音の上限: 1ステップに {_audio.MaxStartsPerClipPerStep} 回");
                break;

            case Key.Number8:
                _audio.PitchVariation = !_audio.PitchVariation;
                Console.WriteLine($"ピッチの揺らぎ: {OnOff(_audio.PitchVariation)}");
                break;

            case Key.Number9:
                _panning = !_panning;
                Console.WriteLine($"左右の定位: {OnOff(_panning)}");
                break;

            case Key.Number0:
                if (_audio.IsPlaying(_musicVoice))
                {
                    _audio.Stop(_musicVoice);
                    _musicVoice = VoiceId.None;
                    Console.WriteLine("BGM: 停止");
                }
                else
                {
                    // **ループするものだけが札を必要とする**。
                    // 止める相手を指せなければ、止めようがない。
                    _musicVoice = _audio.PlayLoop(_musicClip, 0.55f);
                    Console.WriteLine($"BGM: 再生 {_musicVoice}");
                }

                break;

            case Key.LeftBracket:
                _audio.MasterVolume -= 0.1f;
                Console.WriteLine($"音量: {_audio.MasterVolume:P0}");
                break;

            case Key.RightBracket:
                _audio.MasterVolume += 0.1f;
                Console.WriteLine($"音量: {_audio.MasterVolume:P0}");
                break;

            case Key.F1:
                RunAudioCheck();
                break;

            // --- 今日のスイッチ(卒業制作)---
            //
            // **キーの意味を2つに分ける**。
            //   Enter     … 前へ進む(デモ → タイトル → 開始 → やり直し)
            //   Backspace … 後ろへ戻る(プレイ中 → タイトル → デモ)
            // 1つのキーで往復させると、今どちらへ動くのかが分からなくなる。
            case Key.Enter:
            case Key.KeypadEnter:
                EnterGame();
                break;

            case Key.Backspace:
                LeaveGame();
                break;

            case Key.Tab when _playing:
                RunGameCheck();
                break;

            case Key.End when _playing && _game.Phase == GamePhase.Playing:
                // **時間を飛ばす**。時間で難しくなるゲームは、
                // 終盤を見るのに毎回そこまで遊ぶ必要が出てくる。
                // 窓を出さずに回せる作りにしてあるので(RunSteps)、
                // 30 秒ぶんの 1800 ステップは一瞬で終わる。
                RunSteps(_game, 30 * 60, _ => GameAction.None);
                Console.WriteLine($"早送り: {_game.Elapsed:F0}秒 / 敵 {_game.EnemyCount} 体");
                break;

            // --- Day 28 のスイッチ(文字)---
            case Key.Semicolon:
                _overlay = (_overlay + 1) % 4;
                Console.WriteLine($"画面内の表示: {OverlayLabel()}"
                    + (_overlay >= 2 ? "  (G と PageDown で背景を消すと見やすい)" : string.Empty));
                break;

            case Key.Slash:
                RunTextCheck();
                break;

            case Key.Minus:
                _fixedBodySize = !_fixedBodySize;
                InitializeBodies(_activeBodies);
                Console.WriteLine(
                    $"体の大きさ: {(_fixedBodySize ? "固定(増やすと密になる)" : "面積を一定に保つ")}");
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

        // **音も同じ形で1行**。ボイス → バッファ → コンテキスト → デバイスの順に畳む。
        _audio.Dispose();

        _textBatch?.Dispose();
        _glyphAtlas?.Dispose();
        _font?.Dispose();

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
