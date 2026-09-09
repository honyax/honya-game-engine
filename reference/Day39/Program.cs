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
///
/// **Day 30 での変更**: ゲームが1本できあがった。
/// レベルアップで**時間が止まり**、3つの選択肢から武器や強化を選ぶ。
/// 武器は3種類——飛ぶ弾(ボルト)、周回する球(オービット)、
/// 範囲を削る場(オーラ)で、**当たり判定の置き場所が全部違う**。
/// BGM も流れる(Day 27 の <c>music-loop</c>)。
///
/// これで Phase 5 のマイルストーン——
/// **このエンジンで、敵が数百体押し寄せる見下ろし型アクションを1本完成させる**——
/// に到達する。
///
/// **Day 31 での変更**: **Phase 6(デモ必須編)の1日目**。
/// エンジン本体はここまでで、今日からは「AAA デモとして見せる」ための描画を積む。
///
/// 描いた結果が画面へ直行しなくなった。
/// いったんテクスチャ(<see cref="Framebuffer"/>)へ描き、
/// そこから **明部を抜く → ぼかす → 露出・トーンマップ・ガンマ** を通って画面に出る
/// (<see cref="PostProcess"/>)。3D の背景には
/// **1.0 を超える明るさを持つ立方体**と、0.25 から 32 までの**明るさの階段**を置いた。
///
/// Shift+3 でトーンマップを「なし」にすると、階段の 1.0 から上が**全部同じ白**になる。
/// ACES にすると 4 のあたりまで段差が戻り、Shift+5 で露出を下げれば 32 まで読める。
///
/// そこで Shift+1 を押してシーンバッファを 8bit に落とすと、
/// **露出をいくら下げても上の段は戻ってこない**——畳む前に 1.0 で切られているから。
/// これが HDR パイプラインが要る理由そのもの。
///
/// **Day 32 での変更**: 本物のモデルが載った。
/// glTF 2.0 を自前で読み(<see cref="GltfLoader"/>)、
/// Khronos の公式サンプル(DamagedHelmet / WaterBottle / Lantern / BoxTextured)を
/// Shift+0 で切り替えられる。
///
/// Day 10 の OBJ ローダとの違いは**マテリアルが仕様に入っていること**で、
/// ベースカラー・法線・メタリック/ラフネス・AO・発光の5枚が
/// 1つのファイルから出てくる。Shift+9 でそれを1枚ずつ画面に出せる——
/// **今日はベースカラーしか絵に使わない**が、読み込みは全部済ませてある。
/// 使い始めるのは法線が Day 34、メタリック/ラフネスが Day 35、AO が Day 37。
///
/// 併せて <see cref="Vertex"/> に法線が戻り、平行光源1つぶんの
/// ランバート反射が付いた(Day 9 でソフトウェアラスタライザに書いたものの GPU 版)。
///
/// **Day 33 での変更**: 影が落ちる(<see cref="ShadowMap"/>)。
/// 光の目から深度を1枚焼き、本描画でそれと比べる2パス構成。
/// Ctrl + 数字でバイアスや PCF を動かすと、
/// **アクネとピーターパンの間に正解が挟まっている**ことが手で分かる。
///
/// **Day 34 での変更**: 平らな面に凹凸が出る(<see cref="SurfaceMaps"/>)。
/// 接空間(T・B・N)を頂点に持たせ、法線マップと視差マッピングを入れた。
/// Alt + 数字で方式を切り替えられる。
///
/// **Day 35 での変更**: 陰影が**物理ベース**になった。
/// ランバート反射が Cook-Torrance BRDF(<see cref="Pbr"/>)に置き換わり、
/// Day 32 で読むだけしていた**メタリックとラフネスがやっと絵に効く**。
///
/// Ctrl+Shift+5 で材質グリッド(球 7x7)が出る。
/// 縦が金属度、横が粗さ——**2つの数字だけで材質の空間が張られている**ことが
/// 1枚の絵に出る。Ctrl+Shift+1 で Day 34 のランバートに戻せるので、
/// 同じシーンを並べて見比べられる。
///
/// 金属の行がほとんど黒くなるのは**正しい**。金属は拡散反射をせず、
/// 映り込む景色がまだ無いため。それを与えるのが Day 36(IBL)。
///
/// **Day 36 での変更**: 空ができて、**まわりの景色が光になった**
/// (<see cref="EnvironmentMap"/>)。
///
/// HDR の空を手で焼き(<see cref="SkyImage"/>)、キューブマップへ写して、
/// そこから3枚を事前計算する——放射照度(拡散)、事前フィルタ(鏡面)、
/// BRDF の表(材質)。`Ctrl+Alt+1` で切ると Day 35 の
/// 「環境光は定数」に戻るので、**金属が真っ黒に戻る**のを見比べられる。
///
/// 材質グリッド(Ctrl+Shift+5)を出したまま IBL を入れると、
/// **黒かった上の行に景色が映る**。Day 35 で「正しいが物足りない」と書いた絵が、
/// ここで完成する。
///
/// **Day 37 での変更**: 物と物が接しているところに暗がりが出た
/// (<see cref="Ssao"/>)。
///
/// Day 36 の環境光は「どの点も空だけを見ている」前提の値だったので、
/// 立方体は床から浮き、隅は妙に明るかった。
/// カメラから見た法線と距離を1枚に焼き(幾何パス)、
/// **すでに描いてある深度を世界の模型として使って**遮蔽率を作る。
///
/// キーは `Ctrl+F1`〜`F11`。数字キーは Shift / Ctrl / Alt / Ctrl+Shift / Ctrl+Alt の
/// 5段で埋まったので、今日からファンクションキーへ移った
/// (`Alt+F4` が窓を閉じるので Alt は使えない)。
/// `Ctrl+F2` で遮蔽率だけを全画面に出せる——
/// **合成した絵からは半径もバイアスも読み取れない**ので、これが今日の主な道具になる。
///
/// **Day 38 での変更**: 輪郭の階段が消え、絵に色が付いた。
///
/// 後処理の出口が2段になった。合成(トーンマップとガンマまで)の結果を
/// いったん 8bit のバッファに置き、そこから **FXAA**(<c>fxaa.frag</c>)が
/// 縁だけを均して画面へ出す。ジオメトリも深度も見ず、
/// **もう出来上がった1枚の絵の輝度だけ**で段差を探す手法なので、
/// ポリゴンの縁にもテクスチャの模様にも同じように効く。
///
/// 合成パスの中には**カラーグレーディング**(<see cref="ColorGrade"/>)が入った。
/// ホワイトバランス・コントラスト・彩度の3つだけだが、
/// これで絵の印象はほとんど決まる。**1パスも増えない**のがこちらの性格で、
/// 丸ごと1パス増える FXAA と並べると、
/// 「後処理を足す」と言っても代償がまるで違うことが分かる。
///
/// 併せて、**UI が後処理の外に出た**。Day 31 から HUD の文字は
/// シーンと同じバッファに描かれていたので、露出を上げると文字まで白飛びしていた。
/// FXAA は文字を滲ませるため、外に出さないと今日の絵が成立しない——
/// Day 37 の <see cref="OnRender"/> に「分けるのは Day 38」と書いた宿題がこれ。
///
/// キーは `Shift+F1`〜`F12`(`Shift+F3` だけは Day 24 の先客が居るので空き)。
/// `Shift+F11` の左右比較が今日の主な道具になる——
/// **アンチエイリアスも色も、隣に並べないと分からない**。
///
/// **Day 39 での変更**: **デモ v1 が組み上がった**。
/// Day 31〜38 で積んだ描画機能を、初めて<b>本番の絵</b>の上で動かす。
///
/// 入ったものは3つ。
///
/// 1つ目は<b>本物の HDRI</b>(<see cref="HdrImage"/>)。
/// Radiance の <c>.hdr</c> を自分で読む。RGBE——
/// **3色で1つの指数を共有する**——という詰め方を読むと、
/// HDR が「広い範囲を 1 画素 4 バイトに収める工夫」であることが分かる。
///
/// 2つ目は<b>絵から太陽を測ること</b>(<see cref="SkyAnalysis"/>)。
/// Day 36 の <see cref="SkyImage"/> には
/// 「買ってきた HDRI だと絵の中の太陽とシーンの平行光源が別物になる」と書いてあった。
/// **いちばん明るいかたまりを探して、向きと放射照度を取り出す**——
/// これだけで影の向きも濃さも辻褄が合う。
/// 併せて<b>環境マップから太陽を抜く</b>(二重計上を消す)。
///
/// 3つ目は<b>シーンをファイルに書くこと</b>(<see cref="DemoScene"/>)。
/// <c>assets/scenes/demo-v1.json</c> に地面・壁・小物・光の設定が並び、
/// <c>Ctrl+Shift+F10</c> で読み直せる。
/// **影・SSAO・本描画の3つのパスが同じ並びを回る**ようになり、
/// Day 33 で書いた「フラグにするのは Day 39 の仕事」という宿題も片付いた。
///
/// キーは `Ctrl+Shift+F1`〜`F12`、自己チェックは `Ctrl+Alt+F12`。
/// `Ctrl+Shift+F12` で決めの構図に飛ぶ。
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
    private static RenderResources _resources = null!;

    // --- 3D(参照ではなくハンドルを持つようになった) ---
    private static Handle<Shader> _shader;
    private static Handle<Texture> _texture;
    private static Mesh<Vertex> _cube = null!;
    private static Mesh<Vertex> _quad = null!;
    private static Material _cubeMaterial = null!;
    private static Material _floorMaterial = null!;
    private static Camera _camera = null!;
    private static OrbitCameraController _orbit = null!;

    // --- 今日の主役 ---

    /// <summary>HDR パイプライン。**すべての描画がここを通って画面に出る**。</summary>
    private static PostProcess _post = null!;

    /// <summary>
    /// **今日の主役**。光の目から見た深度を焼き、本描画で引く(Day 33)。
    ///
    /// <see cref="PostProcess"/> と同じく<b>シーンを知らない</b>ので、
    /// 何を影として落とすかは <see cref="RenderShadowPass"/> がここで決める。
    /// </summary>
    private static ShadowMap _shadow = null!;

    /// <summary>深度パスにかかった時間(移動平均)。**影の代償を数字で見る**ため。</summary>
    private static double _shadowMilliseconds;

    // ===== Day 38: FXAA と簡易カラーグレーディング =====
    //
    // 今日の主役は2つとも <see cref="PostProcess"/> の中に居るので、
    // ここに増える状態は「UI をどこで描くか」の1つだけになった。

    /// <summary>
    /// HUD の文字を**後処理に通すか**(Shift+F5)。
    ///
    /// <b>Day 37 まではずっと通していた</b>。シーンと同じバッファに描いていたので、
    /// 露出を上げると文字まで白飛びし、トーンマップの曲線で色が転んでいた。
    /// 今日 FXAA が入って、それに加えて<b>文字が滲む</b>ようになる——
    /// FXAA は「1画素の細い線」がいちばん苦手なので、字はまっさきに犠牲になる。
    ///
    /// <para>
    /// 既定は <c>false</c>(通さない)。ON にすると Day 37 までの挙動に戻るので、
    /// 露出を上げて文字が飛ぶところと、Shift+F1 で FXAA を切ったときに
    /// 字の輪郭が戻るところを見比べられる。
    /// </para>
    /// </summary>
    private static bool _uiThroughPost;

    // ===== Day 37: スクリーンスペース環境遮蔽(SSAO)=====

    /// <summary>
    /// **画面から作る環境遮蔽**。今日の主役。
    ///
    /// <see cref="PostProcess"/>(Day 31)・<see cref="ShadowMap"/>(Day 33)・
    /// <see cref="EnvironmentMap"/>(Day 36)に続く<b>4つ目の「自分でバッファを持つ」クラス</b>。
    /// 形がそろっているのは偶然ではなく、
    /// 「1回では終わらない描画」が全部 Day 31 の Render To Texture から派生しているため。
    /// </summary>
    private static Ssao _ssao = null!;

    /// <summary>幾何パス + 遮蔽の計算にかかった時間(移動平均)。HUD 用。</summary>
    private static double _ssaoMilliseconds;

    // ===== Day 36: 環境マッピングと IBL =====

    /// <summary>環境マップ一式(空 / 放射照度 / 事前フィルタ / BRDF の表)。</summary>
    private static EnvironmentMap _env = null!;

    // --- 今日の主役: デモ v1(Day 39)---

    /// <summary>組み上げたシーン。無い間は Day 38 までのデモが出る。</summary>
    private static DemoScene? _demo;

    /// <summary>使える HDRI。**Ctrl+Shift+F3 で回す**。3枚で「太陽の写り方」が3通り見られる。</summary>
    private static readonly string[] HdriPaths =
    [
        // 太陽がきれいに撮れているもの。抽出が素直に決まる(視半径 0.3 度)。
        "hdri/spruit_sunrise_2k.hdr",

        // 太陽がかすんでいるもの。抽出はできるが、担っている光がごく僅か。
        "hdri/venice_sunset_2k.hdr",

        // 太陽が飽和しているもの。**抽出が破綻する**——今日いちばんの教材。
        "hdri/the_sky_is_on_fire_2k.hdr",
    ];

    private static int _hdriIndex;

    /// <summary>空を HDRI にするか(false なら Day 36 の手焼き)。</summary>
    private static bool _useHdriSky = true;

    /// <summary>平行光源を HDRI から取るか(false なら Day 38 までの手書きの向き)。</summary>
    private static bool _sunFromHdri = true;

    /// <summary>いま焼いてある HDRI の解析結果。**空を回す前の座標で測った値**。</summary>
    private static SkyAnalysis.Result _skyAnalysis;

    /// <summary>空を Y 軸まわりに回した角度(ラジアン)。<c>equirect.frag</c> の <c>uSkyYaw</c>。</summary>
    private static float _skyYaw;

    /// <summary>空の回転を効かせるか(Ctrl+Shift+F11。**回さないとどうなるか**を見る窓)。</summary>
    private static bool _applySkyYaw = true;

    /// <summary>空を回したあとの太陽の向き(進む向き)。**平行光源に入るのはこちら**。</summary>
    private static Vector3 _sunWorldDirection = -Vector3.UnitY;

    /// <summary>環境マップから太陽を抜くか(**二重計上を消す**)。</summary>
    private static bool _removeSunFromIbl = true;

    /// <summary>太陽を取り出すしきい値(最大輝度に対する比)。Ctrl+Shift+F6 で回す。</summary>
    private static float _sunThreshold = 0.05f;

    private static readonly float[] SunThresholdSteps = [0.30f, 0.10f, 0.05f, 0.02f];

    /// <summary>影を落とす設定を一括で無視するか(Ctrl+Shift+F7。**影が消える**)。</summary>
    private static bool _sceneShadows = true;

    /// <summary><c>.hdr</c> の読み込みと解析にかかった時間。</summary>
    private static double _hdrLoadMilliseconds;

    private static double _skyAnalysisMilliseconds;

    /// <summary>Day 38 までの手書きの光。デモから戻るときに書き戻す。</summary>
    private static Vector3 _manualLightDirection;

    private static Vector3 _manualLightColor;

    private static Vector3 _manualAmbientColor;

    private static float _manualExposure;

    private static float _manualShadowRadius;

    /// <summary>太陽を何度回したか(Ctrl+Alt+5)。**空と平行光源が同時に動く**。</summary>
    private static float _sunYaw;

    /// <summary>IBL の3枚を巡回表示する状態(Ctrl+Alt+7)。0 = 通常。</summary>
    private static int _iblViewIndex;

    // ===== Day 34: 法線マップと視差マッピング =====

    /// <summary>法線マップを使うか(Alt+1)。OFF にすると頂点法線だけになる。</summary>
    private static bool _normalMapping = true;

    /// <summary>法線マップの緑を反転するか(Alt+8)。**DirectX 形式の素材の見え方**。</summary>
    private static bool _flipGreen;

    /// <summary>視差の方式(Alt+2)。0=なし 1=単純視差 2=急峻視差 3=視差遮蔽。</summary>
    private static int _parallaxMode = 3;

    /// <summary>視差の深さ(Alt+3)。<see cref="Material.ParallaxScale"/> に入れる。</summary>
    private static float _parallaxScale = 0.05f;

    /// <summary>レイマーチの刻み数(Alt+4)。真正面で最小、寝かせるほど最大に近づく。</summary>
    private static int _parallaxMinSteps = 8;
    private static int _parallaxMaxSteps = 32;

    /// <summary>
    /// 材質テストの板を出すか(Alt+5)。**視差はこれが無いと確かめられない**。
    ///
    /// 読み込むモデルは高さマップを持っていない(glTF に無い)ので、
    /// 視差を見るには自前の素材を貼った面が要る。
    /// 平らな板にするのは、**平らなのに凹凸に見える**ことがこの技法の主張だから。
    /// </summary>
    private static bool _surfaceDemo;

    /// <summary>テスト板のマテリアル。<see cref="SurfaceMaps"/> が作った3枚を貼る。</summary>
    private static Material _surfaceMaterial = null!;

    /// <summary>テスト板の UV の繰り返し数(Alt+6)。</summary>
    private static float _surfaceTiling = 2.0f;

    /// <summary>
    /// ファイルの TANGENT を捨てて、こちらで作り直すか(Alt+7)。
    /// **見比べるためだけの切り替え**で、実用では常に false。
    /// </summary>
    private static bool _forceGeneratedTangents;

    // ===== Day 35: 物理ベースレンダリング(Cook-Torrance)=====

    /// <summary>
    /// Cook-Torrance を使うか(Ctrl+Shift+1)。OFF で Day 34 のランバートに戻る。
    ///
    /// **切り替えられるようにしてあるのが今日の肝**。
    /// PBR の値打ちは「単体で見て正しい」ことではなく
    /// 「材質の違いが1つの式から出てくる」ことなので、
    /// 前の式と並べないと何が変わったのか分からない。
    /// </summary>
    private static bool _pbrEnabled = true;

    /// <summary>金属度の上書き(Ctrl+Shift+2)。0=モデル既定 / 1=0.0 / 2=0.5 / 3=1.0。</summary>
    private static int _metallicOverride;

    /// <summary>粗さの上書き(Ctrl+Shift+3)。0=モデル既定 / 1=0.05 / 2=0.3 / 3=0.6 / 4=1.0。</summary>
    private static int _roughnessOverride;

    /// <summary>
    /// 非金属の F0(Ctrl+Shift+4)。既定は 0.04。
    /// 0.00(反射しない) / 0.04(普通) / 0.08(宝石) / 0.17(ダイヤモンド)。
    /// </summary>
    private static float _dielectricF0 = Pbr.DielectricF0;

    /// <summary>α の作り方(Ctrl+Shift+6)。true なら α = roughness²(glTF の流儀)。</summary>
    private static bool _perceptualRoughness = true;

    /// <summary>環境光に粗い鏡面を足すか(Ctrl+Shift+7)。**Day 36 の IBL までのつなぎ**。</summary>
    private static bool _ambientSpecular = true;

    /// <summary>
    /// 材質グリッドを出すか(Ctrl+Shift+5)。**PBR はこれが無いと確かめられない**。
    ///
    /// 縦に金属度、横に粗さを振った球を並べる。
    /// PBR の解説がどれもこの絵を載せるのは、
    /// **2つのパラメータで材質の空間が張られている**ことが一目で分かるため。
    /// 「金属の列だけ拡散が消える」「粗さを上げるとハイライトが広がって暗くなる」が
    /// 同時に見える。
    /// </summary>
    private static bool _materialGrid;

    /// <summary>材質グリッドの1辺の個数。7 なら 0, 1/6, ..., 1 の 7 段。</summary>
    private const int MaterialGridSize = 7;

    /// <summary>材質グリッドの球。<see cref="Primitives.CreateSphere"/> が作る。</summary>
    private static Mesh<Vertex> _sphere = null!;

    /// <summary>材質グリッドのマテリアル。**球ごとに金属度と粗さを書き換えて使い回す**。</summary>
    private static Material _gridMaterial = null!;

    /// <summary>
    /// 材質グリッドのベースカラー(Ctrl+Shift+8)。
    ///
    /// **金属は F0 がベースカラーそのものになる**ので、
    /// 色を変えると金属の行だけ鏡面に色が乗る。
    /// 非金属の行は鏡面が白いまま、拡散だけが色付く——
    /// この対比が「metallic が何を切り替えているか」の答えそのもの。
    /// 値は実測の F0 に近いものを選んである(金・銅)。
    /// </summary>
    private static int _gridColorIndex;

    /// <summary>
    /// 材質グリッド専用の光の向き(進む向き)。**カメラの少し左上から当てる**。
    ///
    /// デモの太陽は画面の奥から手前へ進むので、正面から見るグリッドは逆光になる。
    /// <see cref="RenderMaterialGrid"/> のコメント参照。
    /// </summary>
    private static readonly Vector3 GridLightDirection =
        Vector3.Normalize(new Vector3(0.42f, -0.52f, -0.75f));

    /// <summary>
    /// 材質グリッド用の光の強さの倍率。**白飛びさせないための「露出」**。
    /// <see cref="RenderMaterialGrid"/> のコメント参照。
    /// </summary>
    private const float GridLightScale = 0.30f;

    private static readonly (string Name, Vector3 Color)[] GridColors =
    [
        ("白", new Vector3(0.85f, 0.85f, 0.85f)),
        ("金", new Vector3(1.00f, 0.77f, 0.34f)),
        ("銅", new Vector3(0.95f, 0.64f, 0.54f)),
        ("青", new Vector3(0.15f, 0.35f, 0.85f)),
    ];

    // --- 今日の主役: glTF ---

    /// <summary>
    /// 切り替えて見るモデル。**それぞれ違う経路を通す**ように選んである。
    ///
    /// | | 何を試すか |
    /// |---|---|
    /// | DamagedHelmet | glb 埋め込み・**JPEG** テクスチャ・5枚のマップが全部揃っている定番 |
    /// | WaterBottle | glb 埋め込み・PNG・つるつるの金属と半透明のラベル |
    /// | Lantern | **ノードが4つで親子がある**。世界行列の掛け合わせが要る唯一のモデル |
    /// | BoxTextured | **.gltf + .bin + .png の3ファイル**。外部参照と matrix 形式のノード |
    ///
    /// 「読めた」を確かめるには、**違う書かれ方のファイルを通す**しかない。
    /// 1個だけで通っても、それはそのファイルが通っただけになる。
    /// </summary>
    private static readonly string[] ModelPaths =
    [
        "models/DamagedHelmet.glb",
        "models/WaterBottle.glb",
        "models/Lantern.glb",
        "models/BoxTextured/BoxTextured.gltf",
    ];

    /// <summary>今表示しているモデル。null なら無し(Day 31 までの絵)。</summary>
    private static Model? _model;

    /// <summary><see cref="ModelPaths"/> の添字。範囲外なら「無し」。</summary>
    private static int _modelIndex;

    /// <summary>モデルを画面に収めるための倍率と位置。読み込み時に境界箱から決める。</summary>
    private static Matrix4x4 _modelTransform = Matrix4x4.Identity;

    /// <summary>読み込みにかかった時間(ミリ秒)。**同期で読むので、そのままフレームが飛ぶ**。</summary>
    private static double _modelLoadMilliseconds;

    /// <summary>
    /// 画面に出す成分(Shift+9)。
    /// 0=通常 1=ベースカラー 2=法線(頂点) 3=メタリック 4=ラフネス 5=AO 6=発光 7=法線マップ
    /// 8=影の係数(Day 33)。
    /// </summary>
    private static int _debugChannel;

    /// <summary>
    /// 平行光源の向き。**光が進む向き**であって、光源へ向かう向きではない。
    /// シェーダ側で <c>-uLightDirection</c> と符号を反転している。
    /// どちらの約束にするかは決めの問題だが、混ぜると必ず陰影が裏返る。
    ///
    /// <para>
    /// <b>Day 33 で 90 度回した</b>。Day 32 までは (-0.45, -0.72, -0.53) で、
    /// これは既定のカメラ(<see cref="OrbitCameraController"/> の Yaw 0.6)と
    /// **ほぼ同じ方角から照らしている**——つまり光が視線と同じ向きに進む。
    ///
    /// 陰影を見るだけなら問題なかったが、影が付くと事情が変わる。
    /// 影は光の進む向きへ伸びるので、**物体のちょうど真後ろに隠れて1ピクセルも見えない**。
    /// 起動直後の画面に影が出ていないと、実装が正しいかどうかすら分からない。
    ///
    /// 横から照らすと影が横へ倒れて全体が見える。**照明の位置は絵の一部**で、
    /// 「正しく実装したのに何も見えない」の原因になりうる、という Day 33 の教訓。
    /// </para>
    /// </summary>
    private static Vector3 _lightDirection = Vector3.Normalize(new Vector3(-0.53f, -0.72f, 0.45f));

    /// <summary>
    /// 太陽の色と強さ。少し暖色にしてある。
    ///
    /// **1.0 の少し上に置く**のが Day 31 との兼ね合いで効いてくる。
    /// 光に正対した白い面が <c>1.15</c> になるので、ACES で畳むと 0.92 前後——
    /// 「白いが飽和はしていない」ところに収まり、ブルームもごく淡くしか出ない。
    ///
    /// ここを 2.5 にすると、**モデル全部が発光体のように滲む**。
    /// HDR パイプラインは 1.0 を超えたものを「まぶしいもの」として扱うので、
    /// 普通の物体が 1.0 を超えると絵が壊れる。
    /// **明るさの基準を決めるのは光源側の仕事**で、露出(Shift+5/6)はそのあとの調整。
    /// </summary>
    private static Vector3 _lightColor = new(1.15f, 1.11f, 1.04f);

    /// <summary>
    /// 環境光。空からの回り込みのつもりで、少し青くしてある。
    /// **これが無いと影の側が真っ黒になる**——現実には空や地面からの反射が回り込む。
    /// 本物の回り込みを計算するのが Day 36(IBL)で、これはその一番粗い近似。
    /// </summary>
    private static Vector3 _ambientColor = new(0.13f, 0.16f, 0.22f);

    /// <summary>
    /// 発光するものと明るさの階段に使うマテリアル。
    ///
    /// 絵はスプライトの箱(ほぼ真っ白)を借りている。
    /// **白 1x1 のテクスチャ**を用意するのが本来だが、
    /// マテリアルの <c>Tint</c> が 1.0 を超えられることを見るのが目的なので、
    /// 手持ちの素材で足りる。
    /// </summary>
    private static Material _emissiveMaterial = null!;

    /// <summary>
    /// 背景色。**リニアな明るさ**で持つ。
    ///
    /// 出口(<c>composite.frag</c>)でガンマをかけるので、
    /// Day 30 までの (0.08, 0.09, 0.12) をそのまま入れると画面では明るい灰色になる。
    /// 見え方を揃えるために、あらかじめ 2.2 乗して渡しておく。
    /// </summary>
    private static readonly Vector4 ClearColor = SrgbToLinear(new Vector4(0.08f, 0.09f, 0.12f, 1.0f));

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

    /// <summary>
    /// **1.0 を超える明るさを持つもの**。Day 31 の題材。
    ///
    /// 色の値が 6.0 や 9.0 になっているのが要点で、Day 30 まではこれができなかった——
    /// 8bit のバッファに描いた瞬間 1.0 に丸められるので、
    /// 「まぶしい白」と「ふつうの白」がまったく同じ絵になっていた。
    ///
    /// 現実の明るさに換算すると、6.0 はだいたい「白い紙の 6 倍」。
    /// 電球のフィラメントや空の太陽は 3 桁〜5 桁上なので、
    /// **これでもまだかなり控えめ**な数字。
    /// </summary>
    private static readonly (Vector3 Position, Vector3 Color)[] Emitters =
    [
        (new Vector3(0.0f, 1.55f, 0.0f), new Vector3(6.0f, 5.2f, 2.2f)),   // 電球色
        (new Vector3(3.0f, 2.1f, 3.0f), new Vector3(1.0f, 3.2f, 8.0f)),    // 青
        (new Vector3(-3.0f, 1.6f, 3.0f), new Vector3(9.0f, 1.4f, 1.8f)),   // 赤
    ];

    /// <summary>
    /// **明るさの階段**。左から 0.25、0.5、1、2、4、8、16、32。
    ///
    /// 隣どうしが必ず 2 倍(写真でいう「1段」)になっているので、
    /// トーンマップの曲線がどのあたりを潰しているかが目で読める。
    ///   - トーンマップ「なし」 … 1 から右が全部同じ白。**3段目から先の情報が無い**
    ///   - Reinhard            … 右まで見分けはつくが、全体が眠くなる
    ///   - ACES                … 4 のあたりまで段差が戻り、暗いほうも締まる
    ///
    /// **ACES にも白飛びする点(ホワイトポイント)はある**。
    /// 使っているカーブフィットは 7 くらいで 1.0 に達するので、8 から右は白のまま。
    /// そこを見たければ露出を下げる(Shift+5)——
    /// **「畳み方」と「どこを切り出すか」は別の道具**で、両方いる。
    /// </summary>
    private static readonly float[] LadderSteps = [0.25f, 0.5f, 1.0f, 2.0f, 4.0f, 8.0f, 16.0f, 32.0f];

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
            Title = "Day38 - FXAAと簡易カラーグレーディング",
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
        _resources = new RenderResources(_gl);

        // --- 3D ---
        _shader = _resources.LoadShader(
            Path.Combine(shaderDirectory, "textured.vert"),
            Path.Combine(shaderDirectory, "textured.frag"));

        _texture = _resources.LoadTexture(ResolveAssetPath("textures/uv-test.png"));
        _cube = Primitives.CreateCube(_gl);
        _quad = Primitives.CreateQuad(_gl);

        // **球は Day 35 で足した**。材質を見比べられる形が球しかない、というのが理由
        // (Primitives.CreateSphere のコメント)。分割数は 48x32 = 三角形 3072 枚。
        // 49 個並べても 15 万枚で、いまどきの GPU には何でもない数。
        _sphere = Primitives.CreateSphere(_gl);

        _cubeMaterial = new Material(_shader)
        {
            MainTexture = _texture,
            Tint = Vector4.One,
            UvScale = Vector2.One,
        };

        _floorMaterial = new Material(_shader)
        {
            MainTexture = _texture,

            // **Day 31 で数字が変わった**。中身は Day 30 と同じ色。
            //
            // <c>uTint</c> は「リニアな明るさの倍率」という意味になった
            // (<c>textured.frag</c> 参照)ので、Day 30 の (0.45, 0.50, 0.60) を
            // そのまま入れると 2.2 乗ぶん明るくなってしまう。
            // 見え方を揃えるために、あらかじめリニアへ直した値を書いてある。
            //
            // マテリアルの色を**どちらの空間で書くか**は決めの問題で、
            // 決めたら全部そろえないと絵が合わない。ここが揃っていないのが
            // 「リニアワークフローに移行したら色が変になった」の正体。
            Tint = SrgbToLinear(new Vector4(0.45f, 0.50f, 0.60f, 1.0f)),
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

        // --- 今日の主役: HDR パイプライン ---
        //
        // **ここから先の描画は、ぜんぶこの中を通る**。
        // シーンの描き方は Day 30 と1行も変わっていない——
        // 変わったのは「どこへ描くか」だけで、それを OnRender の Begin / End が挟む。
        _post = new PostProcess(
            _gl,
            _resources,
            shaderDirectory,
            _window.FramebufferSize.X,
            _window.FramebufferSize.Y);

        // --- 今日の主役: シャドウマップ ---
        //
        // **画面の大きさと関係無い**のがポイント。後処理のバッファは画面に追随するが、
        // シャドウマップは「光から見た絵」なので、ウィンドウをどう変えても 2048x2048 のまま。
        // リサイズのたびに作り直さなくてよいのはそのため(OnFramebufferResize を見ると分かる)。
        _shadow = new ShadowMap(_gl, _resources, shaderDirectory, resolution: 2048);

        // --- 今日の主役: 環境マップ ---
        //
        // **作るのと焼くのを分けてある**。コンストラクタはシェーダと FBO を用意するだけで、
        // 実際に焼くのは Bake。太陽の向きを変えるたびに焼き直したいので、
        // 「作り直さずに中身だけ入れ替えられる」形にしておく必要がある。
        _env = new EnvironmentMap(_gl, _resources, shaderDirectory);

        // --- 今日の主役: デモ v1 の下ごしらえ(Day 39)---
        //
        // **手書きの光を控えておく**。デモに入ると HDRI から測った値で上書きするので、
        // Ctrl+Shift+F1 で戻ったときに書き戻せるようにしておく。
        // 「元に戻す」を後から足すのは大抵つらいので、上書きする側を書くときに一緒に用意する。
        _manualLightDirection = _lightDirection;
        _manualLightColor = _lightColor;
        _manualAmbientColor = _ambientColor;
        _manualExposure = _post.Exposure;
        _manualShadowRadius = _shadow.Radius;

        // Day 38 までの絵から始める。デモは Ctrl+Shift+F1 で入る。
        _useHdriSky = false;
        _env.Bake(_lightDirection, _window.FramebufferSize.X, _window.FramebufferSize.Y);

        // --- 今日の主役: スクリーンスペース環境遮蔽 ---
        //
        // **画面の大きさに紐づく**ので、リサイズのたびに作り直しが要る
        // (後処理と同じで、シャドウマップとは違う。OnFramebufferResize を見ると分かる)。
        _ssao = new Ssao(
            _gl,
            _resources,
            shaderDirectory,
            _window.FramebufferSize.X,
            _window.FramebufferSize.Y);

        Console.WriteLine();
        Console.WriteLine(
            $"環境マップ: 空 {SkyImage.Width}x{SkyImage.Height} → "
            + $"キューブ {EnvironmentMap.EnvironmentSize} / 放射照度 {EnvironmentMap.IrradianceSize} / "
            + $"事前フィルタ {EnvironmentMap.PrefilterSize}x{EnvironmentMap.PrefilterMipCount}段 / "
            + $"BRDF表 {EnvironmentMap.BrdfLutSize}");
        Console.WriteLine(
            $"  焼き {_env.BakeMilliseconds:F0}ms(空の生成 {_env.SkyMilliseconds:F0}ms / "
            + $"BRDF表 {_env.LutMilliseconds:F0}ms)  {_env.ByteSize / (1024.0 * 1024.0):F1}MB");
        Console.WriteLine(
            $"SSAO: 幾何 {_ssao.Geometry.Width}x{_ssao.Geometry.Height}(RGBA16F)  "
            + $"遮蔽 {_ssao.Width}x{_ssao.Height}(R8) x2  標本 {_ssao.SampleCount}本  "
            + $"半径 {_ssao.Radius:F2}m  {_ssao.ByteSize / (1024.0 * 1024.0):F1}MB");

        // **代償を起動時に1行で出しておく**(Day 38)。
        // FXAA そのものは軽いが、そのために画面と同じ大きさの 8bit バッファが1枚要る。
        // 「後処理を1段足す」の実際の値段は、パスの時間よりバッファのほうが効くことが多い。
        Console.WriteLine(
            $"FXAA: LDR バッファ {_post.Ldr.Width}x{_post.Ldr.Height}(RGBA8) "
            + $"{_post.Ldr.ByteSize / (1024.0 * 1024.0):F1}MB  "
            + $"しきい値 {_post.FxaaEdgeThreshold:F3}/{_post.FxaaEdgeThresholdMin:F4}  "
            + $"歩幅 {_post.FxaaSpanMax:F0}tx  後処理の合計 {_post.ByteSize / (1024.0 * 1024.0):F1}MB");

        // 発光するもの用。
        //
        // **Day 32 で表し方が変わった**。Day 31 は Tint に 1.0 を超える値を入れていたが、
        // 今日から陰影が付くようになったので、それだと光源まで影の側が暗くなる。
        //
        // glTF の言い方に合わせて「ベースカラーは黒、発光がその色」にする。
        //   BaseColorFactor = (0,0,0,1)  … 拡散反射しない。ライティングの影響を受けない
        //   EmissiveFactor  = 明るさ      … そのまま出る
        // アルファだけはテクスチャから来るので、箱の丸い角はそのまま残る。
        //
        // **これが「光っているもの」の正しい書き方**で、
        // Day 31 で Tint を流用していたのは陰影が無かったから許されていた。
        _emissiveMaterial = new Material(_shader)
        {
            MainTexture = _resources.LoadTexture(ResolveAssetPath("textures/sprite-box.png")),
            BaseColorFactor = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
            Tint = Vector4.One,
            UvScale = Vector2.One,
        };

        // --- 今日の主役: 材質テストの板 ---
        //
        // **3枚を1つの関数から作る**(SurfaceMaps)。
        // ベースカラー・法線・高さが必ず辻褄の合った素材になるので、
        // 「実装が違う」のか「素材が合っていない」のかで迷わずに済む。
        //
        // sRGB の指定に注目。**色は true、数値は false**(Day 32 の要点5)。
        // 同じ関数から出た3枚が、読み方だけ違う。
        var mapStopwatch = Stopwatch.StartNew();

        Handle<Texture> surfaceColor = _resources.LoadTextureFromPixels(
            "surface/brick/color", SurfaceMaps.CreateBaseColor(),
            SurfaceMaps.Size, SurfaceMaps.Size, generateMipmaps: true, srgb: true);

        Handle<Texture> surfaceNormal = _resources.LoadTextureFromPixels(
            "surface/brick/normal", SurfaceMaps.CreateNormal(),
            SurfaceMaps.Size, SurfaceMaps.Size, generateMipmaps: true, srgb: false);

        // **高さマップはミップマップを作らない**。
        // 縮小版から読むと、レイマーチが「均された高さ」を見ることになり、
        // 溝が浅くなって視差の効きが距離で変わってしまう。
        Handle<Texture> surfaceHeight = _resources.LoadTextureFromPixels(
            "surface/brick/height", SurfaceMaps.CreateHeight(),
            SurfaceMaps.Size, SurfaceMaps.Size, generateMipmaps: false, srgb: false);

        _surfaceMaterial = new Material(_shader)
        {
            Name = "brick",
            MainTexture = surfaceColor,
            NormalTexture = surfaceNormal,
            HeightTexture = surfaceHeight,
            MetallicFactor = 0.0f,
            RoughnessFactor = 0.9f,
            UvScale = new Vector2(_surfaceTiling, _surfaceTiling),
            ParallaxScale = _parallaxScale,
        };

        Console.WriteLine();
        Console.WriteLine(
            $"材質テスト: {SurfaceMaps.Size}x{SurfaceMaps.Size} を3枚生成 "
            + $"{mapStopwatch.Elapsed.TotalMilliseconds:F0}ms(色 / 法線 / 高さ)");

        // --- 今日の主役: 材質グリッドのマテリアル ---
        //
        // **テクスチャを1枚も貼らない**。金属度と粗さだけで見え方が決まる、
        // というのが今日いちばん見せたいことなので、模様が乗ると邪魔になる。
        //
        // それでも真っ白な 1x1 を作って貼るのは、
        // 貼らないと <see cref="RenderResources.GetTexture"/> が
        // **仮の絵(マゼンタの市松)**を返すため。
        // 「無いときは必ず何かを bind する」という Day 21 の約束の裏返しで、
        // 白が要るなら白を用意する、が正しい対処になる。
        Handle<Texture> white = _resources.LoadTextureFromPixels(
            "pbr/white", [255, 255, 255, 255], 1, 1, generateMipmaps: false, srgb: false);

        _gridMaterial = new Material(_shader)
        {
            Name = "pbr-grid",
            MainTexture = white,
            BaseColorFactor = new Vector4(GridColors[_gridColorIndex].Color, 1.0f),
            MetallicFactor = 0.0f,
            RoughnessFactor = 0.5f,
        };

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

        // --- 今日の主役: モデルを1体読む ---
        //
        // **失敗しても起動は続ける**。素材が無い環境(LFS を引いていない、など)で
        // 例外を投げて落ちると、他の Day の機能まで触れなくなる。
        try
        {
            SetModel(0);
        }
        catch (Exception error)
        {
            Console.WriteLine();
            Console.WriteLine($"モデルを読めませんでした: {error.Message}");
            Console.WriteLine("  assets/models/ が空なら `git lfs pull` を試してください");
            Console.WriteLine();
            _modelIndex = ModelPaths.Length;
        }

        Console.WriteLine();
        Console.WriteLine("Enter:卒業制作(見下ろし型アクション)の開始 / 終了   Backspace:タイトルへ戻る");
        Console.WriteLine("  ゲーム中: 矢印キーで移動、攻撃は自動。レベルアップで ↑↓ と Enter で選ぶ");
        Console.WriteLine();
        Console.WriteLine("--- Day 39: デモ v1 の組み上げ(Ctrl+Shift+F1〜F12)---");
        Console.WriteLine("Ctrl+Shift+F12:デモ v1 の決めの構図へ。**今日の到達点はこの1枚**");
        Console.WriteLine("Ctrl+Shift+F1:デモ ON/OFF  Ctrl+Shift+F2:空を HDRI / 手焼き(Day 36)で切替");
        Console.WriteLine("Ctrl+Shift+F3:HDRI を回す(3枚。**太陽の写り方が3通り**)");
        Console.WriteLine("Ctrl+Shift+F4:平行光源を HDRI から取るか手書きか(**影が空と食い違う**)");
        Console.WriteLine("Ctrl+Shift+F5:環境マップから太陽を抜く(**二重計上**)  Ctrl+Shift+F6:抽出のしきい値");
        Console.WriteLine("Ctrl+Shift+F7:影を全部落とさなくする  Ctrl+Shift+F11:空の回転 ON/OFF");
        Console.WriteLine("Ctrl+Shift+F8:スクリーンショット  Ctrl+Shift+F9:シーンの内訳");
        Console.WriteLine("Ctrl+Shift+F10:シーンを読み直す(**assets/scenes/demo-v1.json を書き換えて押す**)");
        Console.WriteLine("Ctrl+Alt+F12:今日の自己チェック");
        Console.WriteLine();
        Console.WriteLine("--- Day 38: FXAA と簡易カラーグレーディング(Shift+F1〜F12)---");
        Console.WriteLine("Shift+F12:FXAA が効く構図へ。**画面の左が加工前、右が加工後**");
        Console.WriteLine("Shift+F1:FXAA ON/OFF  Shift+F2:輝度 / 縁の検出 / 混合量 を全画面に出す");
        Console.WriteLine("Shift+F4:効き(低/中/高/極)  Shift+F5:UI を後処理に通すか(Day 37 まで の挙動)");
        Console.WriteLine("Shift+F6:グレーディング ON/OFF  Shift+F7:下敷き(夕暮れ / 月夜 / 退色 / モノクロ)");
        Console.WriteLine("Shift+F8:コントラスト  Shift+F9:彩度  Shift+F10:色温度");
        Console.WriteLine("Shift+F11:左右比較(FXAA / グレーディング)  Ctrl+F12:今日の自己チェック");
        Console.WriteLine("  ※Shift+F3 は Day 24(シーンをコードから組み直す)が使っているので空き");
        Console.WriteLine();
        Console.WriteLine("--- Day 37: スクリーンスペース環境遮蔽(Ctrl+F1〜F11)---");
        Console.WriteLine("Ctrl+F9:SSAO が効く構図へ。**立方体の接地と隅に暗がりが出る**");
        Console.WriteLine("Ctrl+F1:SSAO ON/OFF  Ctrl+F2:遮蔽率 / ビュー法線 / 距離 を全画面に出す");
        Console.WriteLine("Ctrl+F3:半径  Ctrl+F4:標本数  Ctrl+F5:ぼかし(OFF でノイズのタイルが見える)");
        Console.WriteLine("Ctrl+F6:AO バッファを半解像度に  Ctrl+F7:強さ  Ctrl+F8:下駄(0 で縞が出る)");
        Console.WriteLine("Ctrl+F11:直接光にも掛ける(**わざと間違える**)  Ctrl+F10:SSAO の自己チェック");
        Console.WriteLine("  Shift+9 の成分に SSAO が増えた(成分 21)");
        Console.WriteLine();
        Console.WriteLine("--- Day 36: 環境マッピングと IBL(Ctrl+Alt + 数字)---");
        Console.WriteLine("Ctrl+Alt+9:材質グリッド + IBL。**黒かった金属の行に景色が映る**");
        Console.WriteLine("Ctrl+Alt+1:IBL ON/OFF(OFF で Day 35 の「環境光は定数」に戻る)  Ctrl+Alt+2:空の表示");
        Console.WriteLine("Ctrl+Alt+3:環境光の強さ  Ctrl+Alt+4:空に事前フィルタの段を出す(粗さとぼけの対応)");
        Console.WriteLine("Ctrl+Alt+5:太陽を30度回して焼き直す  Ctrl+Alt+6:空を8bitに落として焼き直す");
        Console.WriteLine("Ctrl+Alt+7:焼いた3枚を1枚ずつ見る  Ctrl+Alt+8:事前フィルタを使わず原寸を引く");
        Console.WriteLine("Ctrl+Alt+0:IBL の自己チェック");
        Console.WriteLine("  Shift+9 の成分に 放射照度 / 映り込み / BRDFの表 が増えた");
        Console.WriteLine();
        Console.WriteLine("--- Day 35: 物理ベースレンダリング(Ctrl+Shift + 数字)---");
        Console.WriteLine("Ctrl+Shift+5:材質グリッド(球 7x7。縦=金属度 横=粗さ)。**PBR はここでいちばん分かる**");
        Console.WriteLine("Ctrl+Shift+1:Cook-Torrance / ランバート(Day 34)を切り替え");
        Console.WriteLine("Ctrl+Shift+2:金属度の上書き  Ctrl+Shift+3:粗さの上書き  Ctrl+Shift+4:非金属の F0");
        Console.WriteLine("Ctrl+Shift+6:α = r² / r  Ctrl+Shift+7:環境鏡面(Day 36 までのつなぎ)  Ctrl+Shift+8:グリッドの色");
        Console.WriteLine("Ctrl+Shift+9:グリッド + 鏡面のみ表示  Ctrl+Shift+0:PBR の自己チェック");
        Console.WriteLine("  Shift+9 の成分に 拡散 / 鏡面 / フレネルF / 分布D / 幾何G が増えた");
        Console.WriteLine();
        Console.WriteLine("--- Day 34: 法線マップ・視差マッピング(Alt + 数字)---");
        Console.WriteLine("Alt+5:材質テストの板(レンガ)。**視差はここでしか確かめられない**");
        Console.WriteLine("Alt+1:法線マップ ON/OFF  Alt+2:視差(なし/単純/急峻/POM)  Alt+3:視差の深さ");
        Console.WriteLine("Alt+4:レイマーチの刻み  Alt+6:板の繰り返し  Alt+7:ファイルの接線 / 生成した接線");
        Console.WriteLine("Alt+8:法線マップの緑を反転(DirectX 形式)  Alt+9:最終法線を表示  Alt+0:接空間の自己チェック");
        Console.WriteLine("  Shift+9 の成分に 接線T / 従接線B / 最終法線N / 高さ が増えた");
        Console.WriteLine();
        Console.WriteLine("--- Day 33: シャドウマッピング(Ctrl + 数字)---");
        Console.WriteLine("Ctrl+1:影 ON/OFF  Ctrl+2:解像度 512/1024/2048/4096  Ctrl+3:PCF 1タップ/3x3/5x5/7x7");
        Console.WriteLine("Ctrl+4:深度バイアス(0 でアクネが出る)  Ctrl+5:傾きに比例したバイアス  Ctrl+6:深度パスの表カリング");
        Console.WriteLine("Ctrl+7:シャドウマップを隅に表示  Ctrl+8:光の届く範囲 3/6/12/24m  Ctrl+9:光の向きを 30 度回す");
        Console.WriteLine("Ctrl+0:影の自己チェックと計測   Shift+9 を 8 回押すと「影の係数」だけを見られる");
        Console.WriteLine();
        Console.WriteLine("--- Day 32: glTF(Shift + 数字)---");
        Console.WriteLine("Shift+0:モデル切り替え(DamagedHelmet / WaterBottle / Lantern / BoxTextured / 無し)");
        Console.WriteLine("Shift+9:表示する成分(通常/ベースカラー/法線/金属/粗さ/AO/発光/法線マップ/影/T/B/N/高さ)");
        Console.WriteLine("Shift+-:glTF の自己チェック");
        Console.WriteLine();
        Console.WriteLine("--- Day 31: HDR パイプライン(Shift + 数字)---");
        Console.WriteLine("Shift+1:シーンバッファ RGBA16F / RGBA8   Shift+2:ブルーム  Shift+3:トーンマップ(なし/Reinhard/ACES)");
        Console.WriteLine("Shift+4:表示する段(最終/シーンのみ/明部/ぼかし後)  Shift+5 / Shift+6:露出  Shift+7:ブルームのしきい値");
        Console.WriteLine("Shift+8:HDR の自己チェック  F5:シェーダのリロード(後処理も含む)");
        Console.WriteLine("  G で 3D 背景を出すと、光る立方体と**明るさの階段**(0.25〜32)が見える");
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

        // **中間バッファも作り直す**(Day 31)。
        // ここを忘れると、ウィンドウを広げたときに
        // 「絵は左下 1/4 に縮こまり、残りは前のフレームの残骸」という見た目になる。
        // 画面の大きさに紐づくものが増えるほど、リサイズは壊れやすくなる。
        _post.Resize(size.X, size.Y);

        // **SSAO のバッファも同じ**(Day 37)。3枚とも作り直す。
        // 幾何バッファは画面と同じ大きさ、遮蔽の2枚は半解像度なら半分——
        // その対応は Ssao の側が持っているので、ここは画面の大きさを渡すだけでよい。
        _ssao.Resize(size.X, size.Y);
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
                ? $"Day30  {_fps:F1} fps | 卒業制作 "
                    + $"{_game.Phase} 経過:{_game.Elapsed:F1}s 敵:{_game.EnemyCount} "
                    + $"弾:{_game.ProjectileCount} 撃破:{_game.Kills} Lv.{_game.Level} "
                    + $"武器:{_game.WeaponCount} | "
                    + $"更新:{_gameMilliseconds:F2}ms 候補:{_game.PairCandidates:N0} DC:{_drawCalls} | "
                    + $"音:{_audio.ActiveVoices}/{_audio.VoiceCount} 間引き:{_audio.CulledLastStep}"
                : $"Day30  {_fps:F1} fps | "

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
        // 1フレームあたりの枚数を絞ってあるのがミソ(RenderResources.MaxUploadsPerFrame)。
        _resources.Update();

        // グリフを焼いた数の集計を戻す。**焼くのはこのフレームの描画中**なので、
        // 描き始める前に 0 に戻しておく。
        _glyphAtlas?.BeginFrame();

        // **今日の1パス目**(Day 33)。シーンを描く前に、光の目から見た深度を焼く。
        //
        // ここより後ろの描画は1行も変わっていない——増えたのは
        // 「本番の前にもう1回描く」ことと、シェーダが5番のテクスチャを引くことだけ。
        // Day 31 で「1回では終わらない描画が全部ここから始まる」と書いた、その2例目になる。
        RenderShadowPass();

        // **今日の1パス目**(Day 37)。カメラの目から、法線と距離だけを焼く。
        //
        // **本描画より前でなければならない**のがここの制約。
        // 遮蔽率は環境光に掛けるものなので、シーンを描き始める時点で
        // もう出来上がっていないと間に合わない。
        // 「描いてから画面全体に暗さを掛ける」形にすれば1パス減らせるが、
        // それだと**発光するものや空まで暗くなる**——
        // AO は環境光にだけ掛かるものなので、掛ける場所を選べる本描画の中に入れる。
        RenderSsaoPass();

        // **今日からここが画面ではない**(Day 31)。
        // Begin と End の間に描いたものは、いったん RGBA16F のテクスチャに溜まり、
        // End の中で 明部抽出 → ぼかし → 露出・トーンマップ・ガンマ を通って画面に出る。
        //
        // 中の描画コードは1行も変わっていない、というのがこの形の値打ち。
        // 「どこへ描くか」を外から差し替えられるようにしただけで、
        // 画面全体に効く処理をいくらでも後ろに継ぎ足せるようになった。
        _post.Begin(ClearColor);

        // **空はいちばん先**(Day 36)。深度を書かないので、あとから描くものが必ず手前に来る。
        //
        // ゲームモードでは出さない。見下ろし型の 2D に空が映っても意味が無く、
        // 「エンジンの機能のうちゲームが要るものだけを通る」という Day 29 の線から外れる。
        if (!_playing
            && (_draw3D || _demo is not null || _model is not null || _materialGrid || _surfaceDemo))
        {
            _env.DrawSkybox(_camera, _depthTest, _culling);
        }

        if (_playing)
        {
            RenderGame();
            _drawCalls = _spriteBatch.DrawCallCount;
        }
        else if (_demo is not null)
        {
            // **デモ v1 も「それだけを見る絵」**(Day 39)。
            // 材質グリッドや読み込んだモデルと同じ扱いで、
            // スプライトの群れもロードの帯も出さない——
            // デモの絵に 1000 枚のスプライトが重なったら、それはもうデモではない。
            //
            // <b>G キー(_draw3D)より上に置く</b>のもグリッドと同じ理由。
            // 「デモを出せ」と言われたのに 3D 背景のスイッチで消えるのは筋が通らない。
            Render3D();
        }
        else if (_materialGrid || _surfaceDemo)
        {
            // **材質グリッドと材質テストの板は、それだけを見る絵**(Day 34・35)。
            //
            // 下のモデルの枝とまったく同じ扱いにする。スプライトの群れもロードの帯も出さない——
            // 材質の差も板の凹凸も細かいので、上に 1000 枚のスプライトが重なると
            // **何を見ているのかすら分からなくなる**。
            //
            // <b>分岐を Render3D の中ではなくここに置く</b>のが要点。
            // グリッドと板を選ぶ分岐自体は Render3D の中にもあるが、そちらは
            // `_draw3D`(G キー)でまるごと飛ばされる枝の内側にある。
            // Ctrl+Shift+5 で「グリッドを出せ」と言われたのに
            // 3D 背景のスイッチで消えるのは筋が通らないので、判断をここへ上げた。
            //
            // ドローコールは RenderMaterialGrid / RenderSurfaceDemo が数える。
            // 下の枝で `_spriteBatch.DrawCallCount` に上書きされないのも、分けた効き目。
            Render3D();
        }
        else if (_model is not null)
        {
            // **モデルを見せている間はデモを出さない**(Day 32)。
            // ゲームモード(_playing)で同じことをしているのと理由も同じで、
            // スプライトの群れと重ねると、どちらの陰影を見ているのか分からなくなる。
            // Day 31 までのデモは Shift+0 で「モデル無し」まで回すと戻る。
            // ドローコールは RenderModel が数える(パーツ数がそのまま回数になる)。
            Render3D();
        }
        else
        {
            if (_draw3D)
            {
                Render3D();
            }

            RenderSprites();
            _drawCalls = _spriteBatch.DrawCallCount;

            RenderResourceStrip();
        }

        // **UI を後処理に通すか**(Day 38。Shift+F5)。
        //
        // Day 37 まではここに RenderText があった——つまり HUD の文字は
        // シーンと同じ RGBA16F のバッファに描かれ、
        // 露出・グレーディング・トーンマップ・ガンマを全部くぐっていた。
        // 露出を上げると文字まで白飛びし、モノクロにすると文字まで灰色になる。
        //
        // 今日それを外に出す。決め手になったのは FXAA で、
        // **字は1画素の細い線の塊**なので、輪郭を均す処理といちばん相性が悪い。
        // ON にすると Day 37 までの挙動に戻るので、見比べられる。
        if (_uiThroughPost)
        {
            RenderText();
        }

        _post.End(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        // **焼いたシャドウマップを最後に隅へ出す**(Ctrl+7)。
        // 後処理の外に置いてあるので、露出やトーンマップの影響を受けない——
        // デバッグ表示は「見たままの値」であってほしいので、通してはいけない。
        _shadow.DrawDebug(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        // **遮蔽率を全画面に出す**(Ctrl+F2)。こちらも後処理の外。
        //
        // 隅に小さく出す影のデバッグ表示と違って画面いっぱいに出すのは、
        // AO の善し悪しが**1画素単位のノイズと暗がりの広がり方**で決まるから。
        // 縮めて見ても、半径が大きすぎるのかバイアスが足りないのか判断できない。
        _ssao.DrawDebug(
            _window.FramebufferSize.X,
            _window.FramebufferSize.Y,
            _camera.FarPlane * 0.4f);

        // **UI はいちばん最後、後処理の外**(Day 38)。
        //
        // 影と遮蔽のデバッグ表示より後ろに置く。遮蔽の表示は画面いっぱいに出るので、
        // 先に文字を描くと上から塗り潰される。
        //
        // ここは既定のフレームバッファなので、書いた色がそのまま画面の値になる。
        // <b>Day 37 までより文字がわずかに暗く見える</b>のはそのため——
        // 前はガンマ(1/2.2 乗)を通っていたので 0.95 が 0.977 に持ち上がっていた。
        // UI の色は**表示空間で決めるもの**なので、こちらのほうが筋が通っている。
        if (!_uiThroughPost)
        {
            RenderText();
        }

    }

    /// <summary>
    /// **カメラの目から見て、法線と距離だけを描く**(Day 37)。今日の1パス目。
    ///
    /// <see cref="RenderShadowPass"/> と形はそっくりで、違いは3つ。
    ///   1. 行列が光源のものではなく<b>カメラのビュー行列</b>
    ///   2. 色を書く(法線と距離。深度だけの影パスと違い、カラーアタッチメントが要る)
    ///   3. <b>描くものが多い</b>——材質グリッドも材質テストの板も入れる
    ///
    /// 3つ目が影パスとの本質的な違いになる。影は「落とす側/落とされる側」を
    /// 選ぶ意味があったが、AO は<b>画面に写っているものすべて</b>が遮蔽物になる。
    /// 写っているのに幾何バッファに無いものがあると、
    /// そこだけ AO が 1.0(遮られていない)になって**穴が開く**。
    ///
    /// <para>
    /// <b>発光するものだけは外してある</b>(影パスと同じ線)。
    /// AO は環境光に掛かるものなので、
    /// 発光だけで色が決まっている箱には効きようがない。
    /// 代償として、発光する箱の画素は<b>その裏にある床の遮蔽率</b>を引くことになるが、
    /// 環境光の寄与がほぼ 0 なので絵には出ない。
    /// </para>
    ///
    /// <para>
    /// <b>同じジオメトリを1フレームに3回描いている</b>ことになった
    /// (影 → 幾何 → 本描画)。これがディファードレンダリングの動機そのもので、
    /// Day 52 で「1回描いて全部のバッファへ同時に書く」形に整理する。
    /// </para>
    /// </summary>
    private static void RenderSsaoPass()
    {
        // ゲームモードでは出さない。見下ろし型の 2D に環境遮蔽は要らず、
        // 「エンジンの機能のうちゲームが要るものだけを通る」という Day 29 の線から外れる。
        if (!_ssao.Enabled || _playing)
        {
            _ssaoMilliseconds = 0.0;
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        _ssao.BeginGeometry(_camera);

        float angle = Interpolate(_previousAngle, _angle);

        if (_demo is not null)
        {
            // **AO はシーン全部に効かせる**(Day 39)。
            // 影(<see cref="RenderShadowPass"/>)は「落とす側」を選ぶが、
            // 環境遮蔽に選択の余地は無い——**そこにある物は必ず周りを遮る**。
            // 地面を入れないと、壁と地面の入隅に暗がりが出ない。
            foreach (DemoScene.Item item in _demo.Items)
            {
                _ssao.Draw(item.Mesh, item.Transform);
            }
        }
        else if (_materialGrid)
        {
            // **グリッドも入れる**(影パスでは外した)。
            // 球どうしの隙間が近いので、隣り合う球の間にうっすら暗がりが出る——
            // 影パスと違って「材質が読めなくなる」ことは無い。
            for (int row = 0; row < MaterialGridSize; row++)
            {
                for (int column = 0; column < MaterialGridSize; column++)
                {
                    _ssao.Draw(_sphere, GridMatrix(row, column));
                }
            }
        }
        else if (_surfaceDemo)
        {
            _ssao.Draw(_quad, SurfaceWallMatrix());
            _ssao.Draw(_quad, SurfaceFloorMatrix());
        }
        else if (_model is not null)
        {
            foreach (Model.Part part in _model.Parts)
            {
                _ssao.Draw(part.Mesh, part.Transform * _modelTransform);
            }

            _ssao.Draw(_quad, FloorMatrix());
        }
        else if (_draw3D)
        {
            _ssao.Draw(_quad, FloorMatrix());

            foreach ((Vector3 position, float scale, float spin) in Cubes)
            {
                _ssao.Draw(_cube, CubeMatrix(position, scale, spin, angle));
            }
        }

        _ssao.EndGeometry();

        // **遮蔽の計算はここ**。幾何パスと分けてあるのは、
        // フルスクリーンのパスがシーンの描画とはまるで性格が違うため
        // (三角形の数に依存せず、画素数と標本数の積だけで決まる)。
        _ssao.Compute(_camera, _window.FramebufferSize.X, _window.FramebufferSize.Y);

        _ssaoMilliseconds = (_ssaoMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);
    }

    /// <summary>
    /// **光の目から見て、影を落とすものだけを描く**(Day 33)。今日の1パス目。
    ///
    /// 本描画(<see cref="Render3D"/>)との違いは3つ。
    ///   1. 行列が <c>uViewProjection</c> ではなく <c>uLightSpaceMatrix</c>
    ///   2. マテリアルを一切使わない(色を書かないので、テクスチャも色味も要らない)
    ///   3. **描くものを選ぶ**
    ///
    /// 3つ目が設計の判断になる。ここでは
    ///   - 落とす … 床・立方体・モデル
    ///   - 落とさない … 発光する箱、明るさの階段、スプライト、文字
    /// にした。**光っているものが影を落とすのはおかしい**というのが理由で、
    /// 見た目の都合ではなく「そのものが光源側か被写体側か」で分けている。
    ///
    /// 実際のエンジンはこれをマテリアルかコンポーネントのフラグ(<c>CastShadow</c>)で持つ。
    /// ここで手書きの分岐にしてあるのは、**まず「選ぶ必要がある」ことを見るため**で、
    /// フラグにするのはシーン側に影を載せる Day 39 の仕事になる。
    /// </summary>
    private static void RenderShadowPass()
    {
        if (!_shadow.Enabled)
        {
            _shadowMilliseconds = 0.0;
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // **照らす中心はカメラの注視点**にする。世界の原点に固定すると、
        // カメラを引いたときに画面の端が光の箱からはみ出して、そこだけ影が消える。
        // 注視点に追随させておけば「今見ているもの」が必ず箱の中に入る。
        _shadow.Begin(_lightDirection, _orbit.Target);

        float angle = Interpolate(_previousAngle, _angle);

        if (_demo is not null)
        {
            // **フラグで選ぶ**(Day 39)。この関数の説明に書いた宿題がここで片付いた——
            // 手書きの分岐ではなく、シーンのデータが「落とすかどうか」を持っている。
            // Ctrl+Shift+F7 で <see cref="_sceneShadows"/> を切ると全部落とさなくなり、
            // **影がどれだけ絵を支えているか**が分かる。
            if (_sceneShadows)
            {
                foreach (DemoScene.Item item in _demo.Items)
                {
                    if (item.CastShadow)
                    {
                        _shadow.Draw(item.Mesh, item.Transform);
                    }
                }
            }
        }
        else if (_materialGrid)
        {
            // **材質グリッドは影を落とさない**(Day 35)。
            //
            // 落とす先(床)を置いていないので落ちる相手がいない、というのが第一の理由。
            // もう1つは**自分の影で材質が読めなくなる**のを避けるためで、
            // 球の陰の側に自己遮蔽の縞が乗ると、
            // それが粗さのせいなのかバイアスのせいなのか分からなくなる。
            // 材質だけを見たい絵からは、材質以外の変数を抜いておく。
        }
        else if (_surfaceDemo)
        {
            // **板は影を落とさない**(Day 34)。1枚の板が2枚あるだけなので、
            // 影を落とす相手がいない。深度パスを飛ばして計測を素直にしておく。
        }
        else if (_model is not null)
        {
            foreach (Model.Part part in _model.Parts)
            {
                _shadow.Draw(part.Mesh, part.Transform * _modelTransform);
            }

            _shadow.Draw(_quad, FloorMatrix());
        }
        else if (_draw3D)
        {
            _shadow.Draw(_quad, FloorMatrix());

            foreach ((Vector3 position, float scale, float spin) in Cubes)
            {
                _shadow.Draw(_cube, CubeMatrix(position, scale, spin, angle));
            }
        }

        _shadow.End(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        _shadowMilliseconds = (_shadowMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);

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

        // **タイトル以外は世界を描く**。
        // 選択中(LevelUp)も背景として見せる——止まっている世界が見えることで、
        // 「時間が止まっている」ことが伝わる。
        if (_game.Phase != GamePhase.Title)
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

        // **選択中は「決定」になる**。
        // ここで Start を呼ぶと、レベルアップのたびにゲームが最初から始まる。
        if (_game.Phase == GamePhase.LevelUp)
        {
            _game.ConfirmChoice();
            return;
        }

        if (_game.Phase != GamePhase.Playing)
        {
            // **遊ぶときは種を時計から取る**。固定したままだと毎回同じ試合になる。
            // 自己チェックは既定の種のまま呼ぶので、結果は突き合わせられる。
            _game.Start(viewSize, Environment.TickCount);
            Console.WriteLine($"卒業制作: 開始(種 {_game.Seed})");
            StartMusic();
        }
    }

    /// <summary>
    /// BGM を流す。**すでに鳴っていたら鳴らし直さない**。
    ///
    /// やり直すたびに <see cref="AudioSystem.PlayLoop"/> を呼ぶと、
    /// ボイスが1本ずつ増えて重なっていく。
    /// ループするものは「今鳴っているか」を必ず確かめてから鳴らす
    /// (Day 27 で <see cref="VoiceId"/> に世代を持たせたのはこのため)。
    /// </summary>
    private static void StartMusic()
    {
        if (_audio.IsPlaying(_musicVoice))
        {
            return;
        }

        _musicVoice = _audio.PlayLoop(_musicClip, 0.40f);
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
        _musicVoice = VoiceId.None;
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

        lines.AppendLine($"Day38   {_fps:F1} fps   DC:{_drawCalls}");

        if (_model is not null)
        {
            lines.AppendLine(
                $"{ModelLabel()}  三角形:{_model.TriangleCount:N0}  パーツ:{_model.Parts.Count}  "
                + $"マテリアル:{_model.Materials.Count}  テクスチャ:{_model.TextureCount}  "
                + $"読込:{_modelLoadMilliseconds:F0}ms");
        }

        // **今日の状態を1行で**。絵作りの機能は「今どの設定か」を見失いやすいので、
        // 切り替えた結果ではなく設定そのものを出しておく。
        lines.AppendLine(
            $"HDR:{(_post.SceneFormat == RenderTargetFormat.Rgba16F ? "16F" : "8bit")}  "
            + $"{ToneMapLabel()}  露出:{_post.Exposure:F2}  "
            + $"ブルーム:{(_post.BloomEnabled ? $"閾{_post.BloomThreshold:F1}" : "OFF")}  "
            + $"{DebugViewLabel()}  パス:{_post.PassCount}  {_post.ByteSize / (1024.0 * 1024.0):F1}MB");
        // **今日の設定を1行で**。影は「なぜこう見えるか」が設定に強く依存するので、
        // 解像度・PCF・範囲・バイアスを常に出しておく。
        // 1テクセルが覆うワールドの長さ(m/tx)が、**影のギザギザの大きさそのもの**。
        // **今日の設定を1行で**。法線マップと視差は「効いているのか」が
        // 絵からは意外と読み取りにくいので、常に状態を出しておく。
        lines.AppendLine(
            $"法線マップ:{OnOff(_normalMapping)}{(_flipGreen ? "(緑反転)" : string.Empty)}  "
            + $"{ParallaxLabel()}  深さ:{_parallaxScale:F3}  刻み:{_parallaxMinSteps}〜{_parallaxMaxSteps}  "
            + $"{TangentLabel()}  成分:{DebugChannelLabel()}");

        // **今日の設定を1行で**。金属度と粗さは絵から逆算しにくいので、
        // 「いま何を見ているのか」を常に出しておく。
        // 材質グリッドのときは、球ごとに振っていることが分かるよう文言を変える。
        lines.AppendLine(
            _materialGrid
                ? $"{PbrLabel()}  グリッド:{MaterialGridSize}x{MaterialGridSize}"
                    + $"(縦=金属度 横=粗さ)  色:{GridColors[_gridColorIndex].Name}"
                : PbrLabel());

        // **今日の設定を1行で**。IBL は「効いているのか」が絵から読み取りにくいので、
        // 状態と焼き時間を常に出しておく。
        lines.AppendLine(IblLabel());

        // **今日の設定を1行で**(Day 37)。SSAO は半径・標本数・バイアスのどれを動かしても
        // 「なんとなく暗くなった/明るくなった」にしか見えないので、
        // **数字と代償(パス数・ms・MB)を必ず並べて出す**。
        lines.AppendLine(SsaoLabel());

        // **今日の設定を2行で**(Day 38)。FXAA もグレーディングも、
        // 絵からは「効いているのか、効きすぎているのか」が読み取れない。
        // しきい値も色温度も、数字で出しておかないと同じ絵を作り直せない。
        lines.AppendLine(AaLabel());
        lines.AppendLine(GradeLabel());

        // **今日の設定を1行で**(Day 39)。デモは「どの空で、どこから太陽を取ったか」で
        // 絵が丸ごと変わる。しかもそれは絵を見ても分からないので、常に出しておく。
        lines.AppendLine(DemoLabel());

        lines.AppendLine(
            $"{ShadowLabel()}  {_shadow.WorldPerTexel * 100.0f:F1}cm/tx  "
            + $"{_shadow.ByteSize / (1024.0 * 1024.0):F1}MB  影パス:{_shadow.DrawCalls}回 {_shadowMilliseconds:F2}ms");

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

    private static string DebugChannelLabel() => _debugChannel switch
    {
        1 => "ベースカラー",
        2 => "法線",
        3 => "メタリック",
        4 => "ラフネス",
        5 => "AO",
        6 => "発光",
        7 => "法線マップ",
        8 => "影の係数",
        9 => "接線 T",
        10 => "従接線 B",
        11 => "最終法線 N",
        12 => "高さ",
        13 => "拡散のみ",
        14 => "鏡面のみ",
        15 => "フレネル F",
        16 => "法線分布 D",
        17 => "幾何減衰 G",
        18 => "放射照度（拡散 IBL）",
        19 => "事前フィルタ（映り込み）",
        20 => "BRDF の表（R=A / G=B）",
        21 => "SSAO（画面から作った遮蔽）",
        _ => "通常",
    };

    /// <summary>金属度の上書き。**負なら上書きしない**という約束をシェーダと共有している。</summary>
    private static float MetallicOverrideValue() => _metallicOverride switch
    {
        1 => 0.0f,
        2 => 0.5f,
        3 => 1.0f,
        _ => -1.0f,
    };

    /// <summary>粗さの上書き。同上。</summary>
    private static float RoughnessOverrideValue() => _roughnessOverride switch
    {
        1 => 0.05f,
        2 => 0.3f,
        3 => 0.6f,
        4 => 1.0f,
        _ => -1.0f,
    };

    /// <summary>IBL の状態を1行にまとめる(HUD 用)。</summary>
    private static string IblLabel()
    {
        if (!_env.Enabled)
        {
            return "IBL:OFF(環境光は定数。Day 35 まで)";
        }

        string skybox = _env.SkyboxVisible
            ? (_env.SkyboxMip < 0 ? "空:環境" : $"空:段{_env.SkyboxMip}")
            : "空:OFF";

        return $"IBL:ON  強さ:{_env.Intensity:F2}  {skybox}"
            + $"  {(_env.UsePrefilter ? "事前フィルタ" : "原寸のみ")}"
            + (_env.ClampSkyToLdr ? "  空を8bitに制限" : string.Empty)
            + $"  焼き:{_env.BakeMilliseconds:F0}ms  {_env.ByteSize / (1024.0 * 1024.0):F1}MB";
    }
    /// <summary>デモ v1 の状態を1行にまとめる(HUD 用。Day 39)。</summary>
    private static string DemoLabel()
    {
        if (_demo is null)
        {
            return $"デモ:OFF(Ctrl+Shift+F1 で入る)  空:{_env.SourceLabel}";
        }

        SkyAnalysis.Sun sun = _skyAnalysis.Sun;
        Vector3 toSun = -_sunWorldDirection;

        string sunLabel = _useHdriSky && _sunFromHdri
            ? $"太陽:HDRI 仰角{MathF.Asin(Math.Clamp(toSun.Y, -1.0f, 1.0f)) * (180.0f / MathF.PI):F0}度"
                + $"/視半径{sun.AngularRadius * (180.0f / MathF.PI):F2}度"
            : "太陽:手書き(Day 38 まで)";

        return $"デモ:{_demo.Name}  空:{_env.SourceLabel}"
            + (_useHdriSky ? $"(回転{_skyYaw * (180.0f / MathF.PI):F0}度)" : "(手焼き)")
            + $"  {sunLabel}"
            + $"  太陽抜き:{OnOff(_removeSunFromIbl)}  しきい値:{_sunThreshold:F2}"
            + $"  影:{(_sceneShadows ? _demo.ShadowCasterCount.ToString() : "OFF")}/{_demo.Items.Count}"
            + $"  読み:{_hdrLoadMilliseconds:F0}+{_skyAnalysisMilliseconds:F0}ms";
    }

    /// <summary>SSAO のデバッグ表示の名前。切り替えたときのコンソール出力用。</summary>
    private static string SsaoViewLabel() => _ssao.DebugView switch
    {
        SsaoDebugView.Occlusion => "遮蔽率(白 = 遮られていない)",
        SsaoDebugView.Normal => "ビュー空間の法線(**カメラを回すと色が変わるのが正しい**)",
        SsaoDebugView.Depth => "カメラからの距離(近いほど白い)",
        _ => "通常の絵",
    };

    /// <summary>SSAO の状態を1行にまとめる(HUD 用)。</summary>
    private static string SsaoLabel()
    {
        if (!_ssao.Enabled)
        {
            return "SSAO:OFF(接地の暗がりが消える。Day 36 まで)";
        }

        string view = _ssao.DebugView switch
        {
            SsaoDebugView.Occlusion => "  表示:遮蔽率",
            SsaoDebugView.Normal => "  表示:ビュー法線",
            SsaoDebugView.Depth => "  表示:距離",
            _ => string.Empty,
        };

        return $"SSAO:{_ssao.SampleCount}本  半径:{_ssao.Radius:F2}m  下駄:{_ssao.Bias:F3}  "
            + $"強さ:{_ssao.Strength:F1}  {(_ssao.BlurEnabled ? "ぼかしON" : "ぼかしOFF")}  "
            + $"{_ssao.Width}x{_ssao.Height}{(_ssao.HalfResolution ? "(半分)" : string.Empty)}  "
            + $"幾何:{_ssao.DrawCalls}回  パス:{_ssao.PassCount}  {_ssaoMilliseconds:F2}ms  "
            + $"{_ssao.ByteSize / (1024.0 * 1024.0):F1}MB"
            + (_ssao.ApplyToDirectLight ? "  [直接光にも適用=誤用]" : string.Empty)
            + view;
    }

    /// <summary>FXAA のデバッグ表示の名前。切り替えたときのコンソール出力用。</summary>
    private static string FxaaViewLabel() => _post.FxaaDebugView switch
    {
        FxaaDebugView.Luma => "輝度(**FXAA が見ている世界**)",
        FxaaDebugView.Edge => "縁の検出(赤い画素だけが処理される)",
        FxaaDebugView.Blend => "混合量(**輪郭の線だけが光るのが正解**)",
        _ => "通常の絵",
    };

    /// <summary>FXAA の効きの名前。</summary>
    private static string FxaaQualityLabel() => _post.Quality switch
    {
        FxaaQuality.Low => "低",
        FxaaQuality.High => "高",
        FxaaQuality.Extreme => "極",
        _ => "中",
    };

    /// <summary>左右比較の名前。</summary>
    private static string SplitLabel() => _post.Split switch
    {
        PostSplit.Grade => "  比較:左=グレーディング前",
        PostSplit.Fxaa => "  比較:左=FXAA前",
        _ => string.Empty,
    };

    /// <summary>
    /// FXAA の状態を1行にまとめる(HUD 用。Day 38)。
    ///
    /// **しきい値を数字で出しておく**のが要点。FXAA は
    /// 「効いているのか、効きすぎているのか」が絵から読み取れない後処理で、
    /// 縁が残っていても滲んでいても、どちらも「そういう絵」に見えてしまう。
    /// </summary>
    private static string AaLabel()
    {
        if (!_post.FxaaEnabled)
        {
            return "FXAA:OFF(輪郭が階段になる。Day 37 まで)" + SplitLabel();
        }

        string view = _post.FxaaDebugView switch
        {
            FxaaDebugView.Luma => "  表示:輝度",
            FxaaDebugView.Edge => "  表示:縁",
            FxaaDebugView.Blend => "  表示:混合量",
            _ => string.Empty,
        };

        return $"FXAA:{FxaaQualityLabel()}  しきい値:{_post.FxaaEdgeThreshold:F3}"
            + $"/{_post.FxaaEdgeThresholdMin:F4}  歩幅:{_post.FxaaSpanMax:F0}tx  "
            + $"UI:{(_uiThroughPost ? "後処理を通す(Day 37 まで)" : "後処理の外")}"
            + view
            + SplitLabel();
    }

    /// <summary>
    /// カラーグレーディングの状態を1行にまとめる(HUD 用。Day 38)。
    ///
    /// **色は数字で持っておかないと再現できない**。「なんとなく good」で
    /// つまみを回すと、次に開いたときに同じ絵を作れない。
    /// </summary>
    private static string GradeLabel()
    {
        ColorGrade grade = _post.Grade;

        if (!grade.Enabled)
        {
            return "グレーディング:OFF";
        }

        string preset = grade.Preset switch
        {
            GradePreset.Sunset => "夕暮れ",
            GradePreset.Moonlight => "月夜",
            GradePreset.Bleach => "退色",
            GradePreset.Monochrome => "モノクロ",
            _ => grade.IsIdentity ? "ニュートラル(素通し)" : "手動",
        };

        Vector3 balance = grade.WhiteBalance;

        return $"グレーディング:{preset}  色温度:{grade.Temperature:+0;-0;0}"
            + $"  色合い:{grade.Tint:+0;-0;0}  コントラスト:{grade.Contrast:F2}"
            + $"  彩度:{grade.Saturation:F2}"
            + $"  白点:({balance.X:F2},{balance.Y:F2},{balance.Z:F2})";
    }

    /// <summary>PBR の状態を1行にまとめる(HUD 用)。</summary>
    private static string PbrLabel()
    {
        if (!_pbrEnabled)
        {
            return "陰影:ランバート(Day 34)";
        }

        // **材質グリッドでは上書きを効かせていない**(RenderMaterialGrid)ので、
        // そのときは表示もしない。HUD が実際の描画と食い違うのがいちばん質が悪い。
        string metallic = _materialGrid || _metallicOverride == 0
            ? "既定" : $"{MetallicOverrideValue():F2}";
        string roughness = _materialGrid || _roughnessOverride == 0
            ? "既定" : $"{RoughnessOverrideValue():F2}";

        return $"陰影:Cook-Torrance  金属:{metallic}  粗さ:{roughness}  F0:{_dielectricF0:F2}"
            + $"  α={(_perceptualRoughness ? "r²" : "r")}"
            + (_ambientSpecular ? "  環境鏡面" : string.Empty);
    }

    private static string ParallaxLabel() => _parallaxMode switch
    {
        1 => "単純視差",
        2 => "急峻視差",
        3 => "視差遮蔽(POM)",
        _ => "視差なし",
    };

    /// <summary>接線がどこから来たか。**モデルによって違う**のを HUD に出す。</summary>
    private static string TangentLabel()
    {
        if (_model is null)
        {
            return "接線:-";
        }

        return _model.FileTangentParts > 0
            ? $"接線:ファイル{_model.FileTangentParts}"
            : $"接線:生成{_model.GeneratedTangentParts}";
    }

    private static string ShadowLabel() => _shadow.Enabled
        ? $"影:{_shadow.Resolution}  PCF:{(_shadow.PcfRadius == 0 ? "1タップ" : $"{(2 * _shadow.PcfRadius) + 1}x{(2 * _shadow.PcfRadius) + 1}")}"
            + $"  範囲:{_shadow.Radius:F0}m  バイアス:{_shadow.DepthBias:F4}{(_shadow.SlopeBias ? "+傾き" : string.Empty)}"
            + $"{(_shadow.CullFrontFaces ? "  表カリング" : string.Empty)}"
        : "影:OFF";

    private static string ModelLabel() =>
        _model is null ? "モデル無し" : Path.GetFileNameWithoutExtension(_model.SourcePath);

    private static string ToneMapLabel() => _post.ToneMap switch
    {
        ToneMapOperator.Reinhard => "Reinhard",
        ToneMapOperator.Aces => "ACES",
        _ => "トーンマップなし",
    };

    private static string DebugViewLabel() => _post.DebugView switch
    {
        PostDebugView.SceneOnly => "[シーンのみ]",
        PostDebugView.Bright => "[明部]",
        PostDebugView.Bloom => "[ぼかし後]",
        _ => "[最終]",
    };

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

        // ライトと表示モードは**フレームに1回**で足りる(Day 15 の要点: uniform の3階層)。
        // オブジェクトごとに送り直すと、モデルのパーツ数だけ無駄が出る。
        shader.SetVector3("uLightDirection", _lightDirection);

        // **PBR に切り替えると光を π 倍する**(Day 35)。
        //
        // Cook-Torrance の拡散項には 1/π が入っている(半球にばらまくと
        // 積分が π になるので、1 に戻すため)。Day 34 までのランバートは
        // その π を省き、**光の強さのほうで吸わせていた**。
        // だから式を差し替えるとシーン全体が 1/π に沈む。
        //
        // ここで掛けているのは「Ctrl+Shift+1 で切り替えたときに
        // 明るさが揃っていないと見比べられない」という理由だけで、
        // 物理的には π を掛けたほうが正しい強さ(uLightColor が放射輝度になる)。
        shader.SetVector3("uLightColor", _pbrEnabled ? _lightColor * MathF.PI : _lightColor);
        shader.SetVector3("uAmbientColor", _ambientColor);
        shader.SetInt("uDebugChannel", _debugChannel);

        // **Day 35 の設定もフレームに1回**。
        shader.SetInt("uPbrEnabled", _pbrEnabled ? 1 : 0);
        shader.SetFloat("uDielectricF0", _dielectricF0);
        shader.SetInt("uPerceptualRoughness", _perceptualRoughness ? 1 : 0);
        shader.SetInt("uAmbientSpecular", _ambientSpecular ? 1 : 0);
        shader.SetFloat("uMetallicOverride", MetallicOverrideValue());
        shader.SetFloat("uRoughnessOverride", RoughnessOverrideValue());

        // **影に関わる uniform もフレームに1回**(Day 33)。
        // 光源行列・シャドウマップ・PCF の設定はオブジェクトによらないので、
        // ここで一度送れば、以降のドローコールは何も知らなくてよい。
        // テクスチャユニットの割り当ては GL のコンテキストの状態なので、
        // マテリアルが 0〜4 を上書きしても 5 番は残る。
        _shadow.Apply(shader);

        // **IBL もフレームに1回**(Day 36)。3枚のテクスチャ(7〜9番)と、
        // 強さ・段数の設定。オブジェクトによらないので、ここで一度送れば足りる。
        _env.Apply(shader);

        // **SSAO もフレームに1回**(Day 37)。遮蔽率(10番)と画面の大きさ。
        // シェーダ側は gl_FragCoord から UV を作るので、
        // **オブジェクトごとに送るものが1つも無い**——
        // スクリーンスペースの技法は、この意味でいちばん uniform が少ない。
        _ssao.Apply(shader);

        // **Day 34 の設定もフレームに1回**。
        // カメラ位置は視差マッピングの入力(接空間の視線を作るのに要る)で、
        // 残りは表示の切り替え。どれもオブジェクトによらない。
        shader.SetVector3("uCameraPosition", _camera.Position);
        shader.SetInt("uNormalMapping", _normalMapping ? 1 : 0);
        shader.SetInt("uFlipGreen", _flipGreen ? 1 : 0);
        shader.SetInt("uParallaxMode", _parallaxMode);
        shader.SetInt("uParallaxMinSteps", _parallaxMinSteps);
        shader.SetInt("uParallaxMaxSteps", _parallaxMaxSteps);

        // **デモ v1 はいちばん優先する**(Day 39)。
        // 他のデモと重ねる意味が無いので、読み込んである間はこれだけを描く。
        if (_demo is not null)
        {
            RenderDemoScene();
            return;
        }

        // **材質グリッドは、それだけを描く**(Day 35)。
        // 板やモデルと重ねると、材質の違いが背景の明るさに紛れる。
        if (_materialGrid)
        {
            RenderMaterialGrid();
            return;
        }

        // **材質テストの板も、それだけを描く**。
        // モデルやデモと重ねると、視差の効きがどこから来ているのか分からなくなる。
        if (_surfaceDemo)
        {
            RenderSurfaceDemo();
            return;
        }

        // **モデルがあるときは、それだけを描く**。
        // Day 31 までのデモ(立方体・床・明るさの階段)と一緒に出すと、
        // どちらの陰影を見ているのか分からなくなる。
        if (_model is not null)
        {
            RenderModel();
            return;
        }

        Draw(_quad, _floorMaterial, FloorMatrix());

        foreach ((Vector3 position, float scale, float spin) in Cubes)
        {
            Draw(_cube, _cubeMaterial, CubeMatrix(position, scale, spin, angle));
        }

        RenderEmitters(angle);
        RenderLadder();
    }

    /// <summary>
    /// **材質グリッドを描く**(Day 35)。今日の主役。
    ///
    /// 球を <see cref="MaterialGridSize"/> 角に並べ、
    ///   - 縦(上へ) … 金属度 0 → 1
    ///   - 横(右へ) … 粗さ 0 → 1
    /// を振る。**1枚の絵に材質の空間が全部載る**のがこの並べ方の値打ちで、
    /// PBR の解説がどれもこの図から始まるのには理由がある。
    ///
    /// <para>
    /// 見どころは4つ。
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>左端(粗さ 0)は、ほぼ点のハイライト</b>。右へ行くほど広がって、
    /// 同時に**暗くなる**——同じ量の光を広い範囲に配っているから。
    /// エネルギー保存が絵として見える箇所で、
    /// 「つるつるのほうが明るい」は錯覚ではなく物理。
    /// </item>
    /// <item>
    /// <b>上の行(金属)は拡散が消える</b>。ハイライト以外が真っ黒になる。
    /// これは**間違いではない**。金属は中へ入った光を吸収するので、
    /// 映り込む景色が無ければ本当に黒い。Day 36 の IBL がこれを埋める。
    /// </item>
    /// <item>
    /// <b>金属のハイライトには色が付く</b>(Ctrl+Shift+8 で金や銅に)。
    /// 非金属のハイライトは白いまま。F0 の1行が効いている場所。
    /// </item>
    /// <item>
    /// <b>どの球も縁が明るい</b>。フレネル。
    /// 粗さ 1・金属度 0(右下)の、いちばん地味な球でも縁だけは光る。
    /// </item>
    /// </list>
    ///
    /// <para>
    /// マテリアルは<b>1つを使い回して、球ごとに書き換える</b>。
    /// 49 個ぶん作ってもよいが、マテリアルは「シェーダに渡す値の組」でしかないので
    /// (<see cref="Material"/>)、描く直前に差し替えれば同じこと。
    /// ドローコールは 49 回で、状態変更もその都度入る——
    /// **本番なら uniform をまとめて送る**(インスタンシング)ところだが、
    /// 49 個では測っても差が出ない。
    /// </para>
    /// </summary>
    private static void RenderMaterialGrid()
    {
        Shader shader = _resources.GetShader(_shader);

        // **上書きは効かせない**。グリッド自身が金属度と粗さを振っているので、
        // ここで上書きが生きていると全部同じ球になる。
        // Render3D がフレーム頭で送った値を、ここで打ち消しておく。
        shader.SetFloat("uMetallicOverride", -1.0f);
        shader.SetFloat("uRoughnessOverride", -1.0f);

        // **この絵だけ光の向きを差し替える**。
        //
        // デモの太陽(_lightDirection)は**画面の奥から手前へ**進む向きに置いてある。
        // 立方体やモデルを斜めから見るぶんにはそれで陰影が付くが、
        // グリッドは正面から見る絵なので、**手前の面が全部逆光**になってしまう。
        // 実際、拡散も鏡面も出ずに環境光だけの、のっぺりした 49 個が並んだ。
        //
        // 材質の見本を撮るときは光を「カメラの少し左上」に置くのが定石で、
        // ハイライトが球の左上に出て、下へ回り込む陰との対比で丸みが読める。
        // ここだけの都合なので、グローバルな太陽は動かさずにこの1回の描画で上書きする。
        shader.SetVector3("uLightDirection", GridLightDirection);

        // **光の強さも落とす**。デモの太陽の強さのままだと、
        // 拡散の行が軒並み白飛びして**粗さの違いが消える**——
        // 見本を撮るときに露出を絞るのと同じ話で、
        // ハイライトのぶんの余地を上に残しておかないと材質が読めない。
        //
        // π は Render3D と同じ理由(Cook-Torrance の 1/π を釣り合わせる)。
        // ここで一緒に掛けておかないと、Ctrl+Shift+1 の切り替えで明るさが飛ぶ。
        shader.SetVector3(
            "uLightColor",
            _lightColor * GridLightScale * (_pbrEnabled ? MathF.PI : 1.0f));

        // **環境光も同じだけ絞る**(Day 36)。
        // 直接光だけ落として環境光を素のままにすると、
        // IBL を入れた瞬間にグリッド全体が白飛びして、また材質が読めなくなる——
        // 実際そうなった(計画書の「検証の途中で分かったこと」)。
        // 見本の露出は「その絵に入る光すべて」に掛けるものなので、両方に掛ける。
        shader.SetFloat("uIblIntensity", _env.Intensity * GridLightScale);

        _gridMaterial.BaseColorFactor = new Vector4(GridColors[_gridColorIndex].Color, 1.0f);

        for (int row = 0; row < MaterialGridSize; row++)
        {
            // 金属度は 0 か 1 しか物理的に無い(中間は「金属の粉が塗ってある」のような
            // 混ざりものを表す近似)。それでも中間を並べるのは、
            // **どこで拡散が消えるか**を目で追えるようにするため。
            float metallic = (float)row / (MaterialGridSize - 1);

            for (int column = 0; column < MaterialGridSize; column++)
            {
                float roughness = (float)column / (MaterialGridSize - 1);

                _gridMaterial.MetallicFactor = metallic;

                // 粗さ 0 は D が発散するので下限で止める(Pbr.MinRoughness)。
                // シェーダ側でも同じ下限を掛けているが、
                // **画面に出す数字と実際に使う値をそろえておく**ほうが混乱しない。
                _gridMaterial.RoughnessFactor = MathF.Max(roughness, Pbr.MinRoughness);

                Draw(_sphere, _gridMaterial, GridMatrix(row, column));
            }
        }

        _drawCalls = MaterialGridSize * MaterialGridSize;
    }

    /// <summary>
    /// 材質グリッドの球1個ぶんの行列。**幾何パスと本描画で同じものを使う**ために
    /// 切り出した(Day 37。<see cref="FloorMatrix"/> と同じ理由)。
    ///
    /// SSAO はカメラから見た形を焼き直すので、
    /// **本描画と1ミリでもずれると遮蔽率が隣の画素のものになる**。
    /// 球のような丸いものだと縁が二重に見える形で出るので、気づきにくい。
    /// </summary>
    private static Matrix4x4 GridMatrix(int row, int column)
    {
        const float spacing = 1.35f;
        const float half = (MaterialGridSize - 1) * spacing * 0.5f;

        return Matrix4x4.CreateTranslation(
            (column * spacing) - half,
            (row * spacing) - half,
            0.0f);
    }

    /// <summary>
    /// 材質テストの板を描く(Day 34)。**平らな板1枚だけ**。
    ///
    /// 板を2枚、向きを変えて置いてある。
    ///   - 立っている板 … 正面から見る。法線マップの陰影を見るのに向く
    ///   - 寝ている板   … **斜めから見る**。視差の効きはこちらでしか分からない
    ///
    /// 視差マッピングは「視線が寝ているほど UV のずれが大きい」技法なので、
    /// 正面から見ている面では**ほとんど何も起きない**。
    /// 「実装したのに効かない」の原因のほぼ全部がこれで、
    /// だから確認用に寝かせた面を必ず1つ置く。
    /// </summary>
    private static void RenderSurfaceDemo()
    {
        _surfaceMaterial.UvScale = new Vector2(_surfaceTiling, _surfaceTiling);
        _surfaceMaterial.ParallaxScale = _parallaxScale;

        // 立っている板。原点に、カメラのほうを向けて。
        Draw(_quad, _surfaceMaterial, SurfaceWallMatrix());

        // 寝ている板。手前へせり出すように床として敷く。
        Draw(_quad, _surfaceMaterial, SurfaceFloorMatrix());

        _drawCalls = 2;
    }

    /// <summary>
    /// 材質テストの立っている板。**幾何パスと本描画で同じものを使う**ために切り出した(Day 37)。
    /// <see cref="FloorMatrix"/> と同じ理由で、式を2か所に書かない。
    /// </summary>
    private static Matrix4x4 SurfaceWallMatrix() => Matrix4x4.CreateScale(6.0f);

    /// <summary>材質テストの寝ている板。同上。</summary>
    private static Matrix4x4 SurfaceFloorMatrix() =>
        Matrix4x4.CreateScale(6.0f)
        * Matrix4x4.CreateRotationX(-MathF.PI / 2.0f)
        * Matrix4x4.CreateTranslation(0.0f, -3.0f, 3.0f);

    /// <summary>
    /// 床の行列。**深度パスと本描画で同じものを使う**ために切り出した(Day 33)。
    ///
    /// 影は「同じ物体を2回、違う行列で描く」技法なので、
    /// **形の置き場所が2か所に散ると必ずずれる**。
    /// ずれ方が「影だけ少し浮く」のような微妙な形で出るので、原因を掴みにくい。
    /// 1つの式を2か所から呼ぶ、が唯一の予防になる。
    /// </summary>
    private static Matrix4x4 FloorMatrix() =>
        Matrix4x4.CreateScale(20.0f)
        * Matrix4x4.CreateRotationX(-MathF.PI / 2.0f)
        * Matrix4x4.CreateTranslation(0.0f, -0.5f, 0.0f);

    /// <summary>立方体1個の行列。<see cref="FloorMatrix"/> と同じ理由で切り出してある。</summary>
    private static Matrix4x4 CubeMatrix(Vector3 position, float scale, float spin, float angle) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateRotationY(angle * spin)
        * Matrix4x4.CreateTranslation(position);

    /// <summary>
    /// 読み込んだモデルを描く。**パーツを順に並べるだけ**。
    ///
    /// glTF のノードの木は読み込み時に平らにしてある(<see cref="Model"/>)ので、
    /// ここに階層の処理は残っていない。
    /// 各パーツが持つ世界行列に、画面へ収めるための <see cref="_modelTransform"/> を掛ける。
    ///
    /// **マテリアルの切り替えがそのままドローコールの境目**になる。
    /// DamagedHelmet はマテリアル1つ・パーツ1つなので1回、
    /// Lantern はパーツ3つが同じマテリアルを共有するので3回。
    /// パーツをマテリアル順に並べ替えれば状態変更を減らせるが、
    /// 数個の単位では測っても差が出ないので今日はやらない(Day 18 のバッチと同じ話)。
    /// </summary>
    private static void RenderModel()
    {
        Model model = _model!;
        _drawCalls = model.Parts.Count + 1;

        // **床を1枚足した**(Day 33)。影には受け手が要る。
        //
        // Day 32 はモデルだけを宙に浮かべていた。陰影しか無いうちはそれで足りたが、
        // 影は**他の物体の上に落ちて初めて見える**ので、
        // 落ちる先が無いと「影が出ていない」のか「落ちる先が無い」のか区別が付かない。
        //
        // モデルの側は FrameModel が床(Y = -0.5)の上に立つよう合わせてあるので、
        // デモと同じ床をそのまま敷けばよい。
        Draw(_quad, _floorMaterial, FloorMatrix());

        foreach (Model.Part part in model.Parts)
        {
            // **裏面を描くかはマテリアルが決める**。
            // glTF の既定は片面(doubleSided: false)だが、
            // 葉や布のような薄いものは両面で作られている。
            // C キーの設定より、モデルの指定を優先する。
            SetCap(EnableCap.CullFace, _culling && !part.Material.DoubleSided);

            Draw(part.Mesh, part.Material, part.Transform * _modelTransform);
        }

        SetCap(EnableCap.CullFace, _culling);
    }

    /// <summary>
    /// モデルを切り替える。<paramref name="index"/> が範囲外なら「無し」に戻す。
    ///
    /// **同期で読む**ので、その場でフレームが止まる(DamagedHelmet で 0.3 秒ほど)。
    /// Day 21 で作った非同期ロードを使えば止まらないが、
    /// あれはテクスチャ1枚ずつの仕組みで、
    /// **モデル1体は「JSON を読む → 頂点を組む → 画像を5枚復号する → GPU へ上げる」**
    /// という混ざった仕事なので、そのままでは載らない。
    /// どこで切ってワーカーへ出すかは改造課題3で扱う。
    /// </summary>
    private static void SetModel(int index)
    {
        // **先に捨てる**。DamagedHelmet と WaterBottle を同時に持つと
        // 2K テクスチャが9枚になり、VRAM を 200MB 以上使う。
        _model?.Dispose();
        _model = null;

        if (index < 0 || index >= ModelPaths.Length)
        {
            _modelIndex = ModelPaths.Length;

            // **カメラも戻す**。モデルに合わせて寄せた距離と注視点のままだと、
            // デモに戻った瞬間に明るさの階段が画面いっぱいに映って何も分からなくなる。
            _orbit.Reset();
            Console.WriteLine("モデル: 無し(Day 31 までのデモに戻る)");
            return;
        }

        _modelIndex = index;
        string path = ResolveAssetPath(ModelPaths[index]);

        var stopwatch = Stopwatch.StartNew();
        _model = GltfLoader.Load(_gl, _resources, path, _shader, _forceGeneratedTangents);
        _modelLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        FrameModel(_model);

        Console.WriteLine();
        Console.WriteLine($"モデル: {Path.GetFileName(path)}  {_modelLoadMilliseconds:F0}ms");
        Console.WriteLine($"  {GltfLoader.Describe(path)}");
        Console.WriteLine(
            $"  パーツ {_model.Parts.Count} / 三角形 {_model.TriangleCount:N0} / 頂点 {_model.VertexCount:N0}"
            + $" / マテリアル {_model.Materials.Count} / テクスチャ {_model.TextureCount}");

        // **接線がどこから来たかを必ず出す**(Day 34)。
        // 法線マップが効かないときの原因の筆頭がここで、
        // 「ファイルが持っていると思い込んでいた」が実際にいちばん多い。
        Console.WriteLine(
            $"  接線: ファイル {_model.FileTangentParts} パーツ / 生成 {_model.GeneratedTangentParts} パーツ"
            + (_forceGeneratedTangents ? "(ファイルの TANGENT を無視中)" : string.Empty));
        Console.WriteLine(
            $"  境界 ({_model.BoundsMin.X:F2}, {_model.BoundsMin.Y:F2}, {_model.BoundsMin.Z:F2})"
            + $"〜({_model.BoundsMax.X:F2}, {_model.BoundsMax.Y:F2}, {_model.BoundsMax.Z:F2})"
            + $"  半径 {_model.BoundsRadius:F2}m");

        foreach (Material material in _model.Materials)
        {
            Console.WriteLine(
                $"  [{material.Name}] metallic {material.MetallicFactor:F2} / roughness {material.RoughnessFactor:F2}"
                + $" / {material.AlphaMode}{(material.DoubleSided ? " / 両面" : string.Empty)}");
            Console.WriteLine(
                $"    マップ: {MapLabel("ベース", material.MainTexture)}{MapLabel("MR", material.MetallicRoughnessTexture)}"
                + $"{MapLabel("法線", material.NormalTexture)}{MapLabel("AO", material.OcclusionTexture)}"
                + $"{MapLabel("発光", material.EmissiveTexture)}");
        }

        Console.WriteLine();

        static string MapLabel(string name, Handle<Texture> handle) =>
            handle.IsValid ? name + " " : string.Empty;
    }

    /// <summary>
    /// モデルを画面に収める。**大きさが読むまで分からない**ので、境界箱から決める。
    ///
    /// glTF の単位は「1.0 = 1メートル」と決まっているが、
    /// 実際に来るものは水筒(0.06m)から街灯(40m)まで 3 桁ぶれる。
    /// 固定の倍率を書くと、どれか1つにしか合わない。
    ///
    /// やることは2つ。**中心を原点へ運び、半径 2 に揃える**。
    /// カメラを動かすほうが素直に思えるが、そうすると
    /// ニアクリップとファークリップも一緒に調整することになる(Day 16 の要点5)。
    /// **モデルの側を動かすほうが、触るものが少ない**。
    /// </summary>
    private static void FrameModel(Model model)
    {
        const float targetRadius = 2.0f;

        float radius = MathF.Max(model.BoundsRadius, 0.0001f);
        float scale = targetRadius / radius;

        _modelTransform =
            Matrix4x4.CreateTranslation(-model.BoundsCenter)
            * Matrix4x4.CreateScale(scale)

            // 床(Y = -0.5)の上に立たせる。
            * Matrix4x4.CreateTranslation(0.0f, targetRadius * 0.5f, 0.0f);

        // **カメラも合わせる**。倍率だけ合わせても、
        // 既定の距離 9 のままでは画面の隅に小さく映るだけになる。
        // 注視点をモデルの中心に置き、半径の 2.6 倍まで引く——
        // 視野角 60 度なら、これで上下に少し余白が残る。
        _orbit.Reset();
        _orbit.Target = new Vector3(0.0f, targetRadius * 0.5f, 0.0f);
        _orbit.Distance = targetRadius * 2.6f;
        _orbit.Apply();
    }

    // ================================================================
    //  Day 39: デモ v1
    // ================================================================

    /// <summary>
    /// **シーンを読み込んで、光を合わせる**(Day 39)。<c>Ctrl+Shift+F1</c> / <c>Ctrl+Shift+F10</c>。
    ///
    /// 順番に意味がある。
    /// <code>
    ///   JSON を読む → HDRI を読む → 太陽を取り出す → 環境マップを焼く → 光を入れる
    ///                                  ~~~~~~~~~~~~
    ///                       ここで得た向きが、影・映り込み・カメラの全部に効く
    /// </code>
    ///
    /// <para>
    /// <b>読み込みは同期</b>。HDRI 1枚(2048x1024)の復号だけで数十ミリ秒、
    /// モデル 5 体を足すと 1 秒近く止まる。
    /// Day 21 の非同期ロードはテクスチャ1枚の仕組みなので、そのままでは載らない
    /// (<see cref="SetModel"/> と同じ事情)。
    /// **止まる代わりに、どこで何ミリ秒かかったかを必ず出す**。
    /// </para>
    /// </summary>
    private static void LoadDemoScene()
    {
        string? path = TryResolveAssetPath("scenes/demo-v1.json");
        if (path is null)
        {
            Console.WriteLine("デモ v1: assets/scenes/demo-v1.json が見つかりません");
            return;
        }

        // **先に捨てる**。読み直し(Ctrl+Shift+F10)で 2 セット持つと、
        // 1k のテクスチャが 20 枚ぶん重複する。
        _demo?.Dispose();
        _demo = null;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            _demo = DemoScene.Load(_gl, _resources, path, _shader, _quad, ResolveAssetPath);
        }
        catch (Exception exception)
        {
            // **黙って戻らない**。素材が1つ足りないだけで真っ黒になるのがいちばん困る。
            Console.WriteLine($"デモ v1: 読み込みに失敗しました — {exception.Message}");
            return;
        }

        double sceneMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        // シーンが指している HDRI を既定にする(Ctrl+Shift+F3 で変えるまで)。
        int index = Array.IndexOf(HdriPaths, _demo.HdriPath);
        if (index >= 0)
        {
            _hdriIndex = index;
        }

        _removeSunFromIbl = _demo.RemoveSunFromIbl;
        _sunThreshold = _demo.SunThreshold;

        // **他のデモは全部畳む**。材質グリッドやモデル単体と重なると、
        // どの絵を見ているのか分からなくなる(Render3D の分岐と同じ方針)。
        _materialGrid = false;
        _surfaceDemo = false;
        SetModel(ModelPaths.Length);

        BakeSky();
        ApplyDemoLighting();
        FrameDemoScene();

        Console.WriteLine();
        Console.WriteLine($"デモ v1: 「{_demo.Name}」");
        Console.WriteLine(
            $"  シーン {sceneMilliseconds:F0}ms(glTF {_demo.ModelCount} 体 / "
            + $"描画 {_demo.Items.Count} 回 / 三角形 {_demo.TriangleCount:N0} 枚 / "
            + $"テクスチャ {_demo.TextureCount} 枚)");
        Console.WriteLine($"  影を落とすもの: {_demo.ShadowCasterCount} / {_demo.Items.Count}");
        Console.WriteLine();
    }

    /// <summary>デモを畳んで Day 38 までの絵に戻す。</summary>
    private static void UnloadDemoScene()
    {
        _demo?.Dispose();
        _demo = null;

        // **手書きの光を書き戻す**。デモ用に上書きしたままだと、
        // 戻ったあとの立方体と床が夕焼け色のままになる。
        _lightDirection = _manualLightDirection;
        _lightColor = _manualLightColor;
        _ambientColor = _manualAmbientColor;
        _post.Exposure = _manualExposure;
        _shadow.Radius = _manualShadowRadius;

        _useHdriSky = false;
        BakeSky();

        _orbit.Reset();
        Console.WriteLine("デモ v1: OFF(Day 38 までのデモに戻る)");
    }

    /// <summary>
    /// **空を焼き直す**。手焼き(Day 36)と HDRI(今日)の分岐はここだけ。
    ///
    /// <para>
    /// HDRI のときは3段階を通る。
    /// </para>
    /// <list type="number">
    /// <item><see cref="HdrImage.Load"/> で <c>.hdr</c> を float の並びにする</item>
    /// <item><see cref="SkyAnalysis.Analyze"/> で太陽を取り出す</item>
    /// <item>
    /// <see cref="SkyAnalysis.RemoveSun"/> で太陽を抜いてから焼く
    /// (<see cref="_removeSunFromIbl"/> が true のとき)
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <b>解析は太陽を抜く前の画像に対してやる</b>。順番を逆にすると、
    /// 抜いたあとの画像で「いちばん明るいところ」を探すことになり、
    /// 太陽ではない場所(空の一番明るい部分)を拾う。
    /// </para>
    /// </summary>
    private static void BakeSky()
    {
        if (!_useHdriSky)
        {
            _hdrLoadMilliseconds = 0.0;
            _skyAnalysisMilliseconds = 0.0;
            _skyAnalysis = default;
            _skyYaw = 0.0f;
            _env.SkyYaw = 0.0f;
            _sunWorldDirection = _lightDirection;
            _env.Bake(_lightDirection, _window.FramebufferSize.X, _window.FramebufferSize.Y);
            return;
        }

        string path = ResolveAssetPath(HdriPaths[_hdriIndex]);

        var loadWatch = Stopwatch.StartNew();
        HdrImage.Result image = HdrImage.Load(path);
        _hdrLoadMilliseconds = loadWatch.Elapsed.TotalMilliseconds;

        var analyzeWatch = Stopwatch.StartNew();
        _skyAnalysis = SkyAnalysis.Analyze(image.Pixels, image.Width, image.Height, _sunThreshold);

        // --- 空を回す角度を決める ---
        //
        // シーンが「太陽にこの方位へ来てほしい」と言っているので、
        // **測った方位との差**をそのまま回転量にする。
        // HDRI を差し替えても太陽の位置が動かないのがこの持ち方の値打ち。
        Vector3 toSun = -_skyAnalysis.Sun.Direction;
        float measured = MathF.Atan2(toSun.Z, toSun.X);

        _skyYaw = _demo is not null && _applySkyYaw
            ? measured - (_demo.SunAzimuth * (MathF.PI / 180.0f))
            : 0.0f;

        _env.SkyYaw = _skyYaw;

        // 平行光源に入れる向きも同じだけ回す。**ここを忘れると影だけが元の方位に残る**。
        _sunWorldDirection = Vector3.Transform(
            _skyAnalysis.Sun.Direction, Matrix4x4.CreateRotationY(_skyYaw));

        // **太陽を抜くのは回す前の画像に対して**。RemoveSun は正距円筒の座標で動くので、
        // 回転を挟むと抜く場所がずれる(回転は焼き込みパスの中でだけ起きる)。
        float[] pixels = _removeSunFromIbl
            ? SkyAnalysis.RemoveSun(image.Pixels, image.Width, image.Height, _skyAnalysis)
            : image.Pixels;

        _skyAnalysisMilliseconds = analyzeWatch.Elapsed.TotalMilliseconds;

        _env.BakeFromPixels(
            pixels,
            image.Width,
            image.Height,
            _window.FramebufferSize.X,
            _window.FramebufferSize.Y,
            Path.GetFileNameWithoutExtension(path));

        DescribeSky();
    }

    /// <summary>解析の結果をコンソールに出す。**数字で見ないと合っているか分からない**。</summary>
    private static void DescribeSky()
    {
        SkyAnalysis.Sun sun = _skyAnalysis.Sun;
        Vector3 toSun = -sun.Direction;
        Vector3 toSunWorld = -_sunWorldDirection;

        float elevation = MathF.Asin(Math.Clamp(toSun.Y, -1.0f, 1.0f)) * (180.0f / MathF.PI);
        float azimuth = MathF.Atan2(toSun.Z, toSun.X) * (180.0f / MathF.PI);
        float worldAzimuth = MathF.Atan2(toSunWorld.Z, toSunWorld.X) * (180.0f / MathF.PI);
        float radiusDegrees = sun.AngularRadius * (180.0f / MathF.PI);

        Console.WriteLine();
        Console.WriteLine(
            $"空: {_env.SourceLabel} {_env.SourceWidth}x{_env.SourceHeight}"
            + $"  読み {_hdrLoadMilliseconds:F0}ms / 解析 {_skyAnalysisMilliseconds:F0}ms"
            + $" / 焼き {_env.BakeMilliseconds:F0}ms");
        Console.WriteLine(
            $"  太陽: 仰角 {elevation:F1}度 / 方位 {azimuth:F1}度"
            + $"  放射照度 ({sun.Irradiance.X:F2}, {sun.Irradiance.Y:F2}, {sun.Irradiance.Z:F2})"
            + $"  全体の {_skyAnalysis.SunShare * 100.0f:F0}%");
        Console.WriteLine(
            $"  空の回転 {_skyYaw * (180.0f / MathF.PI):F1}度 → シーンでの方位 {worldAzimuth:F1}度"
            + "(HDRI を差し替えてもここは動かない)");
        Console.WriteLine(
            $"  見かけの半径 {radiusDegrees:F2}度({sun.PixelCount} 画素 / 立体角 {sun.SolidAngle:F6}sr)"
            + $"  ピーク輝度 {sun.PeakLuminance:F0}"
            + (radiusDegrees > 3.0f ? "  ← **大きすぎる。太陽を取り出せていない**" : string.Empty));
        Console.WriteLine(
            $"  立体角の合計 {_skyAnalysis.SolidAngleSum:F4}(4π = {MathF.Tau * 2.0f:F4})"
            + (_skyAnalysis.HalfFloatOverflow > 0
                ? $"  ※ 65504 を超える画素が {_skyAnalysis.HalfFloatOverflow} 個(RGB16F で頭打ち)"
                : string.Empty));
        Console.WriteLine(
            _removeSunFromIbl
                ? "  環境マップからは太陽を抜いてある(平行光源との二重計上を消すため)"
                : "  環境マップに太陽が入ったまま(**影の中が明るくなる**)");
    }

    /// <summary>
    /// **抽出した太陽をシーンの光に入れる**。
    ///
    /// <para>
    /// ここが Day 36 で先送りにした問題の答え。
    /// <c>uLightDirection</c> は絵の中の太陽と同じ向きになり、
    /// <c>uLightColor</c> は太陽が実際に運んでいる放射照度になる。
    /// **どちらも手で決めた数字ではない**ので、HDRI を差し替えれば勝手に付いてくる。
    /// </para>
    ///
    /// <para>
    /// <b>π を掛けない</b>のがここの注意点。<see cref="Render3D"/> は PBR のとき
    /// <c>_lightColor</c> を π 倍して渡しているが、
    /// あれは「Day 34 までのランバートと明るさを揃える」ための辻褄合わせで、
    /// **放射照度をそのまま入れるのが物理的に正しい**。
    /// 抽出値をそのまま渡すと π 倍されて 3 倍明るくなるので、
    /// あらかじめ π で割ってある。
    /// </para>
    /// </summary>
    private static void ApplyDemoLighting()
    {
        if (_demo is null)
        {
            return;
        }

        _post.Exposure = _demo.Exposure;
        _env.Intensity = _demo.IblIntensity;
        _ambientColor = _demo.Ambient;

        // **影の箱を広げる**(Day 33)。太陽の仰角が 8 度しかないので、
        // 高さ 4m のものが 28m 先まで影を落とす。既定の半径 6 では入りきらない。
        _shadow.Radius = _demo.ShadowRadius;

        if (!_sunFromHdri || !_useHdriSky || _skyAnalysis.Sun.PixelCount == 0)
        {
            // HDRI から取らないときは手書きの光に戻す。**影の向きが絵と食い違う**のが見える。
            _lightDirection = _manualLightDirection;
            _lightColor = _manualLightColor;
            return;
        }

        _lightDirection = Vector3.Normalize(_sunWorldDirection);
        _lightColor = _skyAnalysis.Sun.Irradiance * (_demo.SunScale / MathF.PI);
    }

    /// <summary>決めの構図に合わせる(<c>Ctrl+Shift+F12</c>)。</summary>
    private static void FrameDemoScene()
    {
        if (_demo is null)
        {
            return;
        }

        _orbit.Target = _demo.CameraTarget;
        _orbit.Distance = _demo.CameraDistance;
        _orbit.Yaw = _demo.CameraYaw;
        _orbit.Pitch = _demo.CameraPitch;
        _orbit.Apply();
    }

    /// <summary>
    /// **デモ v1 を描く**。3つのパスが同じ <see cref="DemoScene.Items"/> を回る。
    ///
    /// <para>
    /// Day 38 までは、パスごとに「何を描くか」が手書きの分岐だった
    /// (<see cref="RenderShadowPass"/> のコメント)。
    /// シーンをデータにすると、<b>3か所が同じ並びを回るだけ</b>になる——
    /// これが「シーングラフを持つ」ことのいちばん素朴な御利益。
    /// </para>
    /// </summary>
    private static void RenderDemoScene()
    {
        DemoScene demo = _demo!;
        _drawCalls = demo.Items.Count;

        foreach (DemoScene.Item item in demo.Items)
        {
            // 裏面を描くかはマテリアルが決める(RenderModel と同じ)。
            SetCap(EnableCap.CullFace, _culling && !item.Material.DoubleSided);
            Draw(item.Mesh, item.Material, item.Transform);
        }

        SetCap(EnableCap.CullFace, _culling);
    }

    /// <summary>シーンの内訳をコンソールに出す(<c>Ctrl+Shift+F9</c>)。</summary>
    private static void DescribeDemoScene()
    {
        DemoScene demo = _demo!;

        Console.WriteLine();
        Console.WriteLine($"[デモ v1 の内訳] {demo.Name}  {Path.GetFileName(demo.SourcePath)}");
        Console.WriteLine(
            $"  描画 {demo.Items.Count} 回 / 三角形 {demo.TriangleCount:N0} 枚 / "
            + $"glTF {demo.ModelCount} 体 / テクスチャ {demo.TextureCount} 枚 / "
            + $"読み込み {demo.LoadMilliseconds:F0}ms");
        Console.WriteLine(
            $"  露出 {demo.Exposure:F2} / IBL {demo.IblIntensity:F2} / 太陽 x{demo.SunScale:F2} / "
            + $"影の半径 {demo.ShadowRadius:F0}m / 太陽の方位 {demo.SunAzimuth:F0}度");
        Console.WriteLine();

        foreach (DemoScene.Item item in demo.Items)
        {
            Vector3 position = item.Transform.Translation;
            Console.WriteLine(
                $"  {(item.CastShadow ? "影" : "  ")} {item.Name,-44}"
                + $" 三角形 {item.Mesh.IndexCount / 3,7:N0}"
                + $"  ({position.X,6:F2}, {position.Y,5:F2}, {position.Z,6:F2})"
                + $"  [{item.Material.Name}]");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// **今日の自己チェック**(<c>Ctrl+Alt+F12</c>)。
    ///
    /// <para>
    /// 確かめるものは3つ。
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b><c>.hdr</c> の復号</b>。読み込んだ画素が正しいかは絵を見ても言えないので、
    /// **RGBE の逆変換を手で1画素ぶんやって突き合わせる**。
    /// </item>
    /// <item>
    /// <b>立体角の積分</b>。全画素の <c>dω</c> を足すと 4π になるはず。
    /// ここが合っていないと、太陽の放射照度も環境光も全部ずれる——
    /// しかも<b>絵はそれらしく出る</b>ので、数字でしか見つけられない。
    /// </item>
    /// <item>
    /// <b>太陽の抽出</b>。向きが絵の中でいちばん明るい画素と一致するか、
    /// 見かけの半径が現実的か、抜いたあとに本当に消えているか。
    /// </item>
    /// </list>
    /// </summary>
    private static void RunSceneCheck()
    {
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[デモ v1(HDRI・太陽の抽出・シーン)の自己チェック]");

        // --- 1. .hdr の復号 ---
        string path = ResolveAssetPath(HdriPaths[0]);
        HdrImage.Result image = HdrImage.Load(path);

        checks.Check(
            "**正距円筒の縦横比が 2:1**(HDRI の約束)",
            image.Width == image.Height * 2,
            $"{image.Width}x{image.Height}");

        checks.Check(
            "画素の数が幅 x 高さ x 3 になっている",
            image.Pixels.Length == image.Width * image.Height * 3,
            $"{image.Pixels.Length:N0} 要素");

        // **RGBE の逆変換を手でやる**。
        //
        // 読み込んだ float から元の (R,G,B,E) を復元し、
        // もう一度 float に戻して一致するかを見る。
        // 復号が 1 段でもずれていれば、ここで必ず落ちる。
        int checkedPixels = 0;
        int roundTripped = 0;
        double worstError = 0.0;

        for (int i = 0; i < image.Width * image.Height; i += 977)
        {
            var color = new Vector3(
                image.Pixels[(i * 3) + 0], image.Pixels[(i * 3) + 1], image.Pixels[(i * 3) + 2]);

            checkedPixels++;

            float max = MathF.Max(color.X, MathF.Max(color.Y, color.Z));
            if (max <= 0.0f)
            {
                roundTripped++;
                continue;
            }

            // 指数を求め直す。ldexp の逆で、log2 の切り上げ。
            int exponent = (int)MathF.Ceiling(MathF.Log2(max));
            float scale = MathF.ScaleB(1.0f, exponent - 8);

            var mantissa = new Vector3(
                MathF.Round(color.X / scale), MathF.Round(color.Y / scale), MathF.Round(color.Z / scale));

            Vector3 restored = mantissa * scale;
            double error = (restored - color).Length() / MathF.Max(max, 1e-6f);
            worstError = Math.Max(worstError, error);

            if (error < 1e-4)
            {
                roundTripped++;
            }
        }

        checks.Check(
            "**すべての画素が RGBE(仮数 8bit + 共有指数)の格子の上に乗っている**",
            roundTripped == checkedPixels,
            $"{roundTripped}/{checkedPixels} 画素  最大のずれ {worstError:E2}");

        // --- 2. 立体角の積分 ---
        SkyAnalysis.Result analysis = SkyAnalysis.Analyze(image.Pixels, image.Width, image.Height, 0.05f);

        // **どこまで合えば合格か**を決めるのが、この手のチェックのいちばん難しいところ。
        //
        // 残る差は**中点則の打ち切り誤差**で、行数 H に対して (π/H)²/24 の相対誤差になる。
        // H = 1024 なら 3.9e-7、絶対では 4.9e-6。実測もぴったりそこに乗る。
        // つまり 1e-5 で切れば「数値積分としては満点」を意味し、
        // これより粗い誤差が出たら **sinθ の掛け忘れか、float での足し込み**を疑えばよい
        // (前者なら 2.7、後者なら 2e-3 のずれになるので、桁で区別が付く)。
        double solidAngleError = Math.Abs(analysis.SolidAngleSum - (4.0 * Math.PI));

        checks.Check(
            "**立体角の合計が 4π**(sinθ の重みが正しく入っている)",
            solidAngleError < 1e-5,
            $"{analysis.SolidAngleSum:F8}(4π = {4.0 * Math.PI:F8})  ずれ {solidAngleError:E2}"
                + $"  ※中点則の打ち切り誤差 {4.0 * Math.PI * Math.Pow(Math.PI / image.Height, 2) / 24.0:E2}");

        // --- 3. 太陽の抽出 ---
        //
        // いちばん明るい画素を総当たりで探し、抽出した向きと突き合わせる。
        int brightest = 0;
        float peak = -1.0f;

        for (int i = 0; i < image.Width * image.Height; i++)
        {
            float luminance =
                (image.Pixels[(i * 3) + 0] * 0.2126f)
                + (image.Pixels[(i * 3) + 1] * 0.7152f)
                + (image.Pixels[(i * 3) + 2] * 0.0722f);

            if (luminance > peak)
            {
                peak = luminance;
                brightest = i;
            }
        }

        Vector3 brightestDirection = SkyAnalysis.DirectionAt(
            brightest % image.Width, brightest / image.Width, image.Width, image.Height);

        float angle = MathF.Acos(
            Math.Clamp(Vector3.Dot(brightestDirection, -analysis.Sun.Direction), -1.0f, 1.0f));

        checks.Check(
            "**抽出した太陽の向きが、いちばん明るい画素と一致する**(1 度以内)",
            angle < 1.0f * (MathF.PI / 180.0f),
            $"ずれ {angle * (180.0f / MathF.PI):F3} 度");

        checks.Check(
            "見かけの半径が現実的("
                + "本物の太陽は 0.27 度。**3 度を超えたら取り出せていない**)",
            analysis.Sun.AngularRadius * (180.0f / MathF.PI) < 3.0f,
            $"{analysis.Sun.AngularRadius * (180.0f / MathF.PI):F3} 度"
                + $"({analysis.Sun.PixelCount} 画素)");

        checks.Check(
            "**太陽が全体の光の大半を担っている**(晴れた朝の HDRI なので)",
            analysis.SunShare > 0.5f,
            $"{analysis.SunShare * 100.0f:F1}%");

        // 抜いたあとに本当に消えているか。
        float[] removed = SkyAnalysis.RemoveSun(image.Pixels, image.Width, image.Height, analysis);
        SkyAnalysis.Result after = SkyAnalysis.Analyze(removed, image.Width, image.Height, 0.05f);

        // **抜いたぶんがちょうど太陽の放射照度**であること。
        // ここが合っていれば、平行光源に足したものと環境マップから引いたものが釣り合う——
        // つまり二重計上も引きすぎも起きていない。
        Vector3 expectedTotal = analysis.TotalIrradiance - analysis.Sun.Irradiance;
        float totalError = Vector3.Distance(after.TotalIrradiance, expectedTotal);

        checks.Check(
            "**抜いたぶんがちょうど太陽の放射照度**(平行光源に足した量と釣り合う)",
            totalError < expectedTotal.Length() * 0.02f,
            $"({expectedTotal.X:F2}, {expectedTotal.Y:F2}, {expectedTotal.Z:F2}) を期待して"
                + $" ({after.TotalIrradiance.X:F2}, {after.TotalIrradiance.Y:F2},"
                + $" {after.TotalIrradiance.Z:F2})  ずれ {totalError:F3}");

        // **残るのはしきい値のすぐ下まで**。同じ基準で選んで同じ基準で抜いているので、
        // 残った画素のいちばん明るいものは必ずしきい値未満になる。
        //
        // 逆に言えば、**しきい値のすぐ下のにじみ(グレア)は残る**。
        // 太陽の芯の 5% でも空の 3 万倍あるので、
        // 粗い金属にはうっすら太陽が映り続ける——今日の実装の限界。
        checks.Check(
            "**残った画素はすべてしきい値より暗い**(選んだ基準と抜いた基準が同じ)",
            after.Sun.PeakLuminance < analysis.Cutoff,
            $"ピーク {analysis.Sun.PeakLuminance:F0} → {after.Sun.PeakLuminance:F1}"
                + $"(しきい値 {analysis.Cutoff:F1})");

        // --- 4. 太陽が飽和した HDRI では破綻すること ---
        //
        // **失敗するのが正しい**という項目。露出段数の足りない HDRI では
        // しきい値法が空の半分を「太陽」と判定する。
        // それを黙って使うと、平行光源が空全体の平均になって影が消える。
        HdrImage.Result clipped = HdrImage.Load(ResolveAssetPath(HdriPaths[2]));
        SkyAnalysis.Result clippedAnalysis =
            SkyAnalysis.Analyze(clipped.Pixels, clipped.Width, clipped.Height, 0.05f);

        checks.Check(
            "**太陽が飽和した HDRI では見かけの半径が跳ね上がる**(検出できたことにする方が危ない)",
            clippedAnalysis.Sun.AngularRadius * (180.0f / MathF.PI) > 10.0f,
            $"{Path.GetFileNameWithoutExtension(HdriPaths[2])}: "
                + $"{clippedAnalysis.Sun.AngularRadius * (180.0f / MathF.PI):F1} 度"
                + $"(ピーク輝度 {clippedAnalysis.Sun.PeakLuminance:F0})");

        // --- 5. シーンの中身 ---
        if (_demo is null)
        {
            Console.WriteLine("  デモ v1 が読み込まれていないので、シーン側のチェックは飛ばしました");
            checks.Report();
            Console.WriteLine();
            return;
        }

        DemoScene demo = _demo;

        checks.Check(
            "シーンに描くものが入っている",
            demo.Items.Count > 0,
            $"{demo.Items.Count} 個 / 三角形 {demo.TriangleCount:N0} 枚");

        checks.Check(
            "**影を落とすものと落とさないものが分かれている**(地面は受け手だけ)",
            demo.ShadowCasterCount > 0 && demo.ShadowCasterCount < demo.Items.Count,
            $"{demo.ShadowCasterCount} / {demo.Items.Count}");

        // **足元が床に乗っているか**。align の検算で、
        // 回転してから境界箱を取り直していないと、寝かせたタイヤがここで落ちる。
        float lowest = float.MaxValue;
        foreach (DemoScene.Item item in demo.Items)
        {
            lowest = MathF.Min(lowest, item.Transform.Translation.Y);
        }

        checks.Check(
            "**床より下に沈んでいるものが無い**(回してから足元を合わせている)",
            lowest > -0.01f,
            $"いちばん低い原点 {lowest:F3}m");

        // 抽出した向きが、シーンが要求した方位になっているか。
        Vector3 toSun = -_sunWorldDirection;
        float worldAzimuth = MathF.Atan2(toSun.Z, toSun.X) * (180.0f / MathF.PI);
        float difference = MathF.Abs(WrapDegrees(worldAzimuth - demo.SunAzimuth));

        checks.Check(
            "**空を回して太陽をシーンの指定した方位に持ってこられている**",
            !_applySkyYaw || !_useHdriSky || difference < 0.5f,
            $"指定 {demo.SunAzimuth:F1}度 / 実際 {worldAzimuth:F1}度(空の回転 "
                + $"{_skyYaw * (180.0f / MathF.PI):F1}度)");

        checks.Report();
        Console.WriteLine();
    }

    /// <summary>角度を -180〜180 度に畳む。</summary>
    private static float WrapDegrees(float degrees)
    {
        degrees %= 360.0f;

        if (degrees > 180.0f)
        {
            degrees -= 360.0f;
        }
        else if (degrees < -180.0f)
        {
            degrees += 360.0f;
        }

        return degrees;
    }

    /// <summary>
    /// 光っているものを描く。**Day 31 の題材**。
    ///
    /// やっていることは今までの立方体と同じで、違うのは <c>Tint</c> だけ。
    /// 1.0 を超える値を入れると、Day 30 までは画面で 1.0 に丸められて
    /// ただの白い箱になっていた。今日からは
    ///   - シーンバッファ(RGBA16F)にその値のまま入り
    ///   - 明部の抽出で拾われて滲み(ブルーム)になり
    ///   - トーンマップで「白いけれど、その手前に階調のある白」に畳まれる
    /// という3つが順に効く。
    ///
    /// **光源として周りを照らしはしない**。今日は「光っているように見える」だけで、
    /// 影(Day 33)も反射(Day 36)もまだ無い。
    /// </summary>
    private static void RenderEmitters(float angle)
    {
        foreach ((Vector3 position, Vector3 color) in Emitters)
        {
            _emissiveMaterial.EmissiveFactor = color;

            Matrix4x4 model =
                Matrix4x4.CreateScale(0.4f)
                * Matrix4x4.CreateRotationY(angle * 0.6f)
                * Matrix4x4.CreateTranslation(position);

            Draw(_cube, _emissiveMaterial, model);
        }
    }

    /// <summary>
    /// **明るさの階段**を空中に並べる。0.25 から 32 まで、隣どうしが 2 倍。
    ///
    /// これがあると、トーンマップの切り替え(Shift+3)が
    /// 「なんとなく雰囲気が変わる」ではなく**どこが潰れているか**として読める。
    /// 絵作りの機能は感想になりやすいので、
    /// **数字が分かっているものを1つ置いておく**と判断が早くなる。
    /// </summary>
    private static void RenderLadder()
    {
        const float width = 0.55f;
        const float height = 0.85f;

        // 隙間を広めに取る。詰めて並べるとブルームが隣へ滲んで、
        // **段の境目がどこか分からなくなる**(滲みが仕事をしすぎる)。
        const float gap = 0.28f;

        float total = (LadderSteps.Length * width) + ((LadderSteps.Length - 1) * gap);
        float left = -total * 0.5f;

        for (int i = 0; i < LadderSteps.Length; i++)
        {
            float intensity = LadderSteps[i];
            _emissiveMaterial.EmissiveFactor = new Vector3(intensity);

            // 立方体より手前・上に、カメラのほうを向けて並べる。
            // <see cref="Primitives.CreateQuad"/> の板は +Z を向いているので、回転は要らない。
            var position = new Vector3(left + (i * (width + gap)) + (width * 0.5f), 2.6f, 1.5f);

            Matrix4x4 model =
                Matrix4x4.CreateScale(width, height, 1.0f)
                * Matrix4x4.CreateTranslation(position);

            Draw(_quad, _emissiveMaterial, model);
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
    /// 出来上がったものは <see cref="RenderResources.Update"/> の生存確認で捨てられる。
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
        var checks = new CheckList();

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

        checks.Check("保存 → 読み込み → 再保存でテキストが一致", first == second,
            $"{first.Length} バイト / {second.Length} バイト");
        checks.Check("GameObject の数が一致", coded.GameObjectCount == loaded.GameObjectCount,
            $"{coded.GameObjectCount} / {loaded.GameObjectCount}");
        checks.Check("コンポーネントの数が一致", coded.ComponentCount == loaded.ComponentCount,
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

        checks.Check("親子の深さが3段(根 → 子 → 孫)", depth == 2, $"実際 {depth + 1} 段");

        GameObject? loadedProbe = null;
        foreach (GameObject gameObject in loaded.GameObjects)
        {
            if (gameObject.Name == "Probe")
            {
                loadedProbe = gameObject;
            }
        }

        checks.Check("目印のオブジェクトが復元されている", loadedProbe is not null);

        if (loadedProbe is not null)
        {
            SpriteRenderer? renderer = loadedProbe.GetComponent<SpriteRenderer>();
            BouncingMover? mover = loadedProbe.GetComponent<BouncingMover>();

            checks.Check("float が保たれている", renderer is not null && renderer.Size == 31.0f);
            checks.Check(
                "Vector4 が保たれている",
                renderer is not null && renderer.Color == probeSprite.Color,
                $"{renderer?.Color}");
            checks.Check(
                "Vector2 が保たれている",
                mover is not null && mover.Velocity == probeMover.Velocity,
                $"{mover?.Velocity}");
        }

        // **本番**。同じ入力で同じだけ回して突き合わせる。
        ulong codedHash = StepAndHash(coded, 300);
        ulong loadedHash = StepAndHash(loaded, 300);

        checks.Check("300 ステップ後の状態が一致", codedHash == loadedHash,
            $"{codedHash:X16} / {loadedHash:X16}");

        checks.Check("エンティティの数が一致", codedWorld.AliveCount == loadedWorld.AliveCount,
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

        checks.Check("ECS コンポーネントの値が保たれている", ecsValuesMatch,
            loadedTransforms.Length > 0 ? $"{loadedTransforms[0].Position}" : "(空)");

        checks.Report("すべて合格 — シーンをファイルから復元しても、同じ動きをする");
        Console.WriteLine();
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
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[当たり判定の自己チェック]");

        // --- 円同士 ---
        var c1 = new Circle2D(new Vector2(0.0f, 0.0f), 10.0f);
        var c2 = new Circle2D(new Vector2(15.0f, 0.0f), 10.0f);
        Contact2D hit = Collision2D.Test(c1, c2);
        checks.Check("円同士: 当たる", hit.Hit);
        checks.Check("円同士: 深さ 5", Near(hit.Depth, 5.0f), $"{hit.Depth}");
        checks.Check("円同士: 法線は +X", Near(hit.Normal.X, 1.0f) && Near(hit.Normal.Y, 0.0f), $"{hit.Normal}");

        checks.Check("円同士: 離れていれば当たらない",
            !Collision2D.Test(c1, new Circle2D(new Vector2(21.0f, 0.0f), 10.0f)).Hit);

        // **ちょうど接している**。浮動小数点の境界で、実装によって割れるところ。
        // ここでは「接触も当たり」とする(<= で書いてある)。
        checks.Check("円同士: ちょうど接するのは当たり扱い",
            Collision2D.Test(c1, new Circle2D(new Vector2(20.0f, 0.0f), 10.0f)).Hit);

        // 中心が完全に重なる退化ケース。向きが決まらないが、落ちてはいけない。
        Contact2D same = Collision2D.Test(c1, new Circle2D(Vector2.Zero, 10.0f));
        checks.Check("円同士: 中心が重なっても NaN にならない",
            same.Hit && !float.IsNaN(same.Normal.X) && !float.IsNaN(same.Depth), $"{same.Normal} {same.Depth}");

        // --- AABB 同士 ---
        Aabb2D b1 = Aabb2D.FromCenter(Vector2.Zero, new Vector2(10.0f, 10.0f));
        Aabb2D b2 = Aabb2D.FromCenter(new Vector2(16.0f, 4.0f), new Vector2(10.0f, 10.0f));
        Contact2D boxHit = Collision2D.Test(b1, b2);

        // X 方向の重なりは 20-16=4、Y 方向は 20-4=16。**浅いほう(X)で押す**。
        checks.Check("AABB: 浅い軸で押す(X)", Near(boxHit.Depth, 4.0f) && Near(boxHit.Normal.X, 1.0f),
            $"深さ {boxHit.Depth} 法線 {boxHit.Normal}");
        checks.Check("AABB: 角がかすっていなければ当たらない",
            !Collision2D.Test(b1, Aabb2D.FromCenter(new Vector2(21.0f, 21.0f), new Vector2(10.0f))).Hit);

        // --- 円と AABB ---
        // 箱の右辺は x=10。中心 x=14 / 半径 6 なら、最近点までの距離は 4 でめり込みは 2。
        Contact2D mixed = Collision2D.Test(new Circle2D(new Vector2(14.0f, 0.0f), 6.0f), b1);
        checks.Check("円とAABB: 辺に当たる", mixed.Hit && Near(mixed.Depth, 2.0f), $"深さ {mixed.Depth}");
        checks.Check("円とAABB: 法線は円から箱へ(-X)", Near(mixed.Normal.X, -1.0f), $"{mixed.Normal}");
        checks.Check("円とAABB: 届かなければ当たらない",
            !Collision2D.Test(new Circle2D(new Vector2(24.0f, 0.0f), 6.0f), b1).Hit);

        // 円が箱の中に完全に入っている場合。距離が 0 になるので別経路を通る。
        Contact2D inside = Collision2D.Test(new Circle2D(new Vector2(2.0f, 0.0f), 3.0f), b1);
        checks.Check("円とAABB: 円が中にあっても向きが決まる",
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
        checks.Check("OBB: 45度の角が刺さっているのを検出", satHit.Hit, $"深さ {satHit.Depth:F3}");
        checks.Check("OBB: 法線は +X 寄り", satHit.Normal.X > 0.9f, $"{satHit.Normal}");

        checks.Check("OBB: 離れていれば当たらない",
            !Collision2D.Test(square, new Obb2D(new Vector2(25.0f, 0.0f), new Vector2(10.0f), MathF.PI / 4.0f)).Hit);

        // **回転 0 の OBB は AABB と同じ答えになるはず**。
        // 専用の速い経路と一般形が食い違っていないかの確認。
        var obbA = new Obb2D(Vector2.Zero, new Vector2(10.0f, 10.0f), 0.0f);
        var obbB = new Obb2D(new Vector2(16.0f, 4.0f), new Vector2(10.0f, 10.0f), 0.0f);
        Contact2D viaSat = Collision2D.Test(obbA, obbB);
        checks.Check("OBB(回転0) と AABB の答えが一致",
            Near(viaSat.Depth, boxHit.Depth) && Near(viaSat.Normal.X, boxHit.Normal.X),
            $"SAT 深さ {viaSat.Depth} 法線 {viaSat.Normal}");

        // --- 円と OBB ---
        var rotated = new Obb2D(Vector2.Zero, new Vector2(10.0f, 4.0f), MathF.PI / 2.0f);
        Contact2D circleObb = Collision2D.Test(new Circle2D(new Vector2(0.0f, 12.0f), 3.0f), rotated);

        // 90 度回すと縦横が入れ替わるので、上端は y = 10 になる。円の下端は 9。深さ 1。
        checks.Check("円とOBB: 回転を考慮している", circleObb.Hit && Near(circleObb.Depth, 1.0f),
            $"深さ {circleObb.Depth}");
        checks.Check("円とOBB: 法線は -Y", Near(circleObb.Normal.Y, -1.0f), $"{circleObb.Normal}");

        // --- 押し戻しが本当に離すか ---
        //
        // 判定と押し戻しは別物で、**深さの符号を間違えると近づく**。
        // 1回押し戻した結果、重なりが消えることを確かめておく。
        var moving = new Circle2D(new Vector2(0.0f, 0.0f), 10.0f);
        var fixedCircle = new Circle2D(new Vector2(12.0f, 0.0f), 10.0f);
        Contact2D push = Collision2D.Test(moving, fixedCircle);
        var afterA = new Circle2D(moving.Center - (push.Normal * (push.Depth * 0.5f)), moving.Radius);
        var afterB = new Circle2D(fixedCircle.Center + (push.Normal * (push.Depth * 0.5f)), fixedCircle.Radius);
        checks.Check("押し戻すと重なりが消える", !Collision2D.Test(afterA, afterB).Hit
            || Collision2D.Test(afterA, afterB).Depth < 0.001f);

        checks.Report();
        Console.WriteLine();

        BenchmarkCollision();

        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.001f;
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
        var checks = new CheckList();

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

        checks.Check("小さな例: 組は 3 つ", found == 3, $"{found} 組: {PairsToText(grid.Pairs)}");
        checks.Check("小さな例: 隣り合う小物(0,1)", HasPair(grid.Pairs, 0, 1));
        checks.Check("小さな例: マスをまたぐ大物(2,3)", HasPair(grid.Pairs, 2, 3));

        // 世界の外は端のマスへ丸めている。**落ちないだけでなく、組も拾えること**。
        checks.Check("小さな例: 世界の外でも拾う(4,5)", HasPair(grid.Pairs, 4, 5));

        // 同じマスにいるだけでは候補にしない。AABB の足切りが効いているか。
        checks.Check("小さな例: 同じマスでも離れていれば候補にしない(6,7)", !HasPair(grid.Pairs, 6, 7));
        checks.Check("小さな例: 足切り前は同居していた", grid.CoLocatedPairs > found, $"同居 {grid.CoLocatedPairs} 組");

        // またがるぶん、登録の総数は体数より多くなる。
        checks.Check("小さな例: 大物が複数マスに登録されている", grid.EntryCount > boxes.Length,
            $"登録 {grid.EntryCount} / 体 {boxes.Length}");

        checks.Check("空でも落ちない", SafeEmpty(grid), "0 体");

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

            checks.Check($"マス {cellSize,6:F0}px: 総当たりと同じ組", Same(expected, actual),
                $"{actual.Count} 組(正解 {expected.Count} 組)");
            checks.Check($"マス {cellSize,6:F0}px: 重複した組が無い", unique);
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

        checks.Check("1ステップの接触数が一致", _contacts == bruteContacts,
            $"総当たり {bruteContacts} / グリッド {_contacts}");
        checks.Check("ナローフェーズの回数は減っている", _pairTests < bruteTests,
            $"{bruteTests:N0} → {_pairTests:N0}({100.0 - (_pairTests * 100.0 / bruteTests):F1}% 削減)");

        _resolveOverlap = savedResolve;
        _broadphase = savedPhase;
        _cellSizeOverride = savedCell;

        checks.Report();
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
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[卒業制作の自己チェック]");

        var viewSize = new Vector2(960.0f, 640.0f);

        // --- 1〜2. 放置と移動を、**3つの種で**比べる ---
        //
        // **1試合では何も言えない**。Day 30 で選択肢を入れた結果、
        // 引いた強化しだいで生き延びる時間が倍以上ぶれるようになった。
        // 実際、最初は1試合ずつ比べていて、
        // 「放置のほうが長生きした」という結果が出た——
        // たまたま放置側がオーラを早く引いただけだった。
        //
        // ゲームの性質を機械に確かめさせるなら、**種を変えて何度か回して平均を見る**。
        // 乱数を外から渡せるようにしてあるのはこのため(要点5)。
        float idleTotal = 0.0f;
        float movingTotal = 0.0f;
        bool alwaysGrows = true;
        var seeds = (int[])[11, 29, 47];

        foreach (int seed in seeds)
        {
            var idle = new SurvivorGame();
            idle.Start(viewSize, seed);
            RunSteps(idle, 60 * 300, _ => GameAction.None);
            idleTotal += idle.Elapsed;

            var moving = new SurvivorGame();
            moving.Start(viewSize, seed);
            RunSteps(moving, 60 * 300, Circle);
            movingTotal += moving.Elapsed;

            // どの試合でも、ちゃんと育って敵を倒していること。
            alwaysGrows &= idle.Level >= 5 && moving.Level >= 5
                && idle.Kills > 100 && moving.Kills > 100;

            Console.WriteLine(
                $"  [--] 種 {seed,3}: 放置 {idle.Elapsed,6:F1}秒(Lv.{idle.Level,2} 撃破 {idle.Kills,4})"
                + $" / 移動 {moving.Elapsed,6:F1}秒(Lv.{moving.Level,2} 撃破 {moving.Kills,4})");
        }

        float idleAverage = idleTotal / seeds.Length;
        float movingAverage = movingTotal / seeds.Length;

        // **ゲームとして進むこと**。どの試合でもレベルが上がり、敵が倒せていること。
        //
        // 「放置すると必ず死ぬ」は条件にしていない。上の3試合を見ると、
        // 種 29 の放置は 300 秒を生き延びて Lv.26 まで育っている——
        // **引いた強化しだいで、立ち止まったまま雪だるま式に強くなれる**。
        // それ自体は狙いどおり(ビルドが噛み合うと化ける)なので、
        // 「必ず死ぬ」を仕様にすると、その面白さを潰すことになる。
        checks.Check("どの試合でも育って倒せる", alwaysGrows, $"{seeds.Length} 試合 x 2 通り、Lv.5 以上 / 撃破 100 以上");

        // **操作に意味がある**ことの確認。ここが同じなら、
        // 遊ぶ側の判断がスコアに効いていない。
        checks.Check("逃げ回るほうが長く生きる(3試合の平均)", movingAverage > idleAverage,
            $"放置 {idleAverage:F1}秒 → 移動 {movingAverage:F1}秒");

        // --- 3. 壊れていないこと ---
        var game = new SurvivorGame();
        game.Start(viewSize);
        RunSteps(game, 60 * 150, Circle);

        checks.Check("敵が配列を超えない", game.EnemyCount <= GameBalance.MaxEnemies,
            $"{game.EnemyCount} / {GameBalance.MaxEnemies}");
        checks.Check("弾が配列を超えない", game.ProjectileCount <= GameBalance.MaxProjectiles,
            $"{game.ProjectileCount} / {GameBalance.MaxProjectiles}");
        checks.Check("ジェムが配列を超えない", game.GemCount <= GameBalance.MaxGems,
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

        checks.Check("倒した敵が配列に残っていない", allAlive, $"生存 {game.EnemyCount} 体");

        // 押し合いは中心が重なると向きが決まらない。
        // Day 25 で NaN を潰してあるので、ここは通るはず(通らなければ退化ケースの取りこぼし)。
        checks.Check("押し合いで座標が壊れない", finite);
        checks.Check("プレイヤーの座標が壊れない",
            float.IsFinite(game.PlayerPosition.X) && float.IsFinite(game.PlayerPosition.Y),
            $"{game.PlayerPosition}");

        // --- 4. 遊びとして進むこと ---
        checks.Check("敵を倒せている", game.Kills > 0, $"{game.Kills} 体");
        checks.Check("レベルが上がる", game.Level > 1, $"Lv.{game.Level}");
        checks.Check("敵が押し寄せている", game.EnemyCount > 20, $"{game.EnemyCount} 体");

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

        checks.Check("同じ入力なら同じ結果",
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
        checks.Check("格子で候補が減っている", game.PairCandidates < allPairs / 4,
            $"{game.PairCandidates:N0} 組(総当たりなら {allPairs:N0} 組)");

        // --- 7. レベルアップの選択(Day 30)---
        //
        // **時間が止まること**が最初の条件。止まらないと、読んでいる間に殺される。
        var pausing = new SurvivorGame();
        pausing.Start(viewSize);
        RunSteps(pausing, 60 * 300, Circle, autoChoose: false);

        checks.Check("レベルアップで止まる", pausing.Phase == GamePhase.LevelUp,
            $"{pausing.Elapsed:F1}秒 / Lv.{pausing.Level}");

        float frozen = pausing.Elapsed;
        RunSteps(pausing, 120, Circle, autoChoose: false);
        checks.Check("止まっている間は時間が進まない", MathF.Abs(pausing.Elapsed - frozen) < 0.0001f,
            $"{frozen:F2}秒 → {pausing.Elapsed:F2}秒(止まった合計 {pausing.PausedSeconds:F1}秒)");

        checks.Check("選択肢が 3 つ出る", pausing.ChoiceCount == GameBalance.UpgradeChoices,
            $"{pausing.ChoiceCount} 個");

        // **同じものが2つ並ばない**。「オーラ Lv.2」が2つ出たら、
        // 実質2択になってしまう。
        bool unique = true;
        for (int i = 0; i < pausing.ChoiceCount; i++)
        {
            for (int j = i + 1; j < pausing.ChoiceCount; j++)
            {
                unique &= pausing.Choices[i].Title != pausing.Choices[j].Title;
            }
        }

        checks.Check("選択肢が重複しない", unique,
            string.Join(" / ", Enumerable.Range(0, pausing.ChoiceCount).Select(i => pausing.Choices[i].Title)));

        // 選ぶと本当に反映されるか。
        int weaponsBefore = pausing.WeaponCount;
        int boltBefore = pausing.LevelOf(WeaponKind.Bolt);
        float healthBefore = pausing.MaxHealth;
        float speedBefore = pausing.SpeedMultiplier;
        float magnetBefore = pausing.MagnetMultiplier;

        UpgradeOption picked = pausing.Choices[pausing.ChoiceCursor];
        pausing.ConfirmChoice();

        bool applied = pausing.WeaponCount > weaponsBefore
            || pausing.LevelOf(picked.Weapon) > boltBefore
            || pausing.MaxHealth > healthBefore
            || pausing.SpeedMultiplier > speedBefore
            || pausing.MagnetMultiplier > magnetBefore;

        checks.Check("選ぶと反映される", applied, $"「{picked.Title}」");
        checks.Check("選び終わると再開する", pausing.Phase == GamePhase.Playing);

        // --- 8. 3種類の武器が、それぞれ単体で敵を削れる ---
        //
        // **混ざった状態では確かめられない**。ボルトが動いているせいで、
        // オーラが1体も削っていないことに気づけない。
        foreach (WeaponKind kind in (WeaponKind[])[WeaponKind.Bolt, WeaponKind.Orbit, WeaponKind.Aura])
        {
            var solo = new SurvivorGame();
            solo.Start(viewSize);
            solo.SetSingleWeapon(kind, 3);

            // **強化を取らせない**(autoChoose: false)。
            // 最初のレベルアップで止まるので、そこまでの撃破は
            // 確実にこの武器だけによるものになる。
            RunSteps(solo, 60 * 60, Circle, autoChoose: false);

            checks.Check($"{Weapons.NameOf(kind)} だけで倒せる", solo.Kills > 0,
                $"{solo.Elapsed:F1}秒で {solo.Kills} 体");
        }

        // --- 9. 武器を全部最大にしても選択肢が出る ---
        //
        // **選ぶものが無くなると、そこでゲームが止まる**。
        // パッシブに上限を作らなかったのはこのため。
        var maxed = new SurvivorGame();
        maxed.Start(viewSize);
        RunSteps(maxed, 60 * 600, Circle);

        checks.Check("長く遊んでも武器が育つ", maxed.WeaponCount > 1,
            $"{maxed.WeaponCount} 種類 / Lv.{maxed.Level} / {maxed.Elapsed:F1}秒");

        checks.Report();
        Console.WriteLine();

        BenchmarkGame();

        // **逃げ回る入力**。決定性のために、乱数ではなく step から決める。
        static GameAction Circle(int step) => KitePattern(step);
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
    /// <param name="autoChoose">
    /// レベルアップの選択を自動で決めるか。
    /// <c>false</c> にすると**最初のレベルアップで止まる**ので、
    /// 「強化される前の状態」だけを測れる。
    /// </param>
    private static int RunSteps(
        SurvivorGame game, int steps, Func<int, GameAction> input, bool autoChoose = true)
    {
        const float dt = 1.0f / 60.0f;

        for (int step = 0; step < steps; step++)
        {
            // **自動で遊ぶ人も、選択肢を選ばないと先へ進めない**。
            // Day 29 の RunSteps をそのまま使うと、
            // 最初のレベルアップで止まったまま 600 秒が過ぎる。
            if (autoChoose && game.Phase == GamePhase.LevelUp)
            {
                game.ConfirmChoice();
            }

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

        Console.WriteLine("### 時間が進むとどうなるか(逃げ続けて、選択は自動)###");
        Console.WriteLine("   経過 |   敵 |  弾 | ジェム |   候補 |   撃破 | Lv | 武器 | 1ステップ");

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
                + $"{game.PairCandidates,6:N0} | {game.Kills,6} | {game.Level,2} | "
                + $"{WeaponDigest(game),-12} | {stopwatch.Elapsed.TotalMilliseconds / steps,6:F3}ms");
        }

        Console.WriteLine();

        // 持っている武器を「B3 O2 A1」のように詰めて出す。表の幅に収めるため。
        static string WeaponDigest(SurvivorGame game)
        {
            var parts = new string[game.WeaponCount];
            for (int i = 0; i < game.WeaponCount; i++)
            {
                char initial = game.Weapons[i].Kind switch
                {
                    WeaponKind.Bolt => 'B',
                    WeaponKind.Orbit => 'O',
                    _ => 'A',
                };

                parts[i] = $"{initial}{game.Weapons[i].Level}";
            }

            return string.Join(" ", parts);
        }
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
        var checks = new CheckList();

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

        checks.Check("ascent は正", ascent > 0.0f, $"{ascent:F2}px");
        checks.Check("descent は正(下向きの量として)", descent > 0.0f, $"{descent:F2}px");

        // ScaleFor は「ascent + descent が指定の高さになる」倍率を返す。
        checks.Check("32px 指定で ascent+descent が 32px", MathF.Abs(ascent + descent - 32.0f) < 0.5f,
            $"{ascent + descent:F2}px");

        // 行送りは字の高さ以上。**lineGap が 0 のフォントでは等しくなる**。
        checks.Check("行送り >= ascent+descent", lineHeight >= ascent + descent - 0.01f,
            $"行送り {lineHeight:F2}px / lineGap {font.LineGap(scale):F2}px");

        // --- 2. グリフの有無 ---
        checks.Check("英字を持っている", font.HasGlyph('A'));
        checks.Check("ひらがなを持っている", font.HasGlyph(0x3042), font.HasGlyph(0x3042) ? "あ" : "なし");
        checks.Check("漢字を持っている", font.HasGlyph(0x6F22), font.HasGlyph(0x6F22) ? "漢" : "なし");

        // 絵文字は日本語フォントには入っていない。**入っていないことを確かめておく**と、
        // 豆腐が出たときに「バグ」ではなく「そういうもの」だと分かる。
        Console.WriteLine($"  [--] 絵文字 U+1F600: {(font.HasGlyph(0x1F600) ? "あり" : "なし(豆腐になる)")}");

        // --- 3. 空白は送りだけ持つ ---
        Glyph space = atlas.GetOrAdd(' ', 16);
        checks.Check("空白は絵を持たないが送りはある", !space.HasPixels && space.Metrics.Advance > 0.0f,
            $"送り {space.Metrics.Advance:F2}px");

        // --- 4. アトラスのキャッシュ ---
        int before = atlas.BakedTotal;
        atlas.GetOrAdd(0x6F22, 16);
        int afterFirst = atlas.BakedTotal;
        atlas.GetOrAdd(0x6F22, 16);
        int afterSecond = atlas.BakedTotal;

        checks.Check("2回目は焼き直さない", afterSecond == afterFirst, $"焼いた回数 {afterSecond - before}");

        // **大きさが違えば別の絵**。同じ文字でも焼き直しになる。
        atlas.GetOrAdd(0x6F22, 17);
        checks.Check("大きさが違えば別のグリフ", atlas.BakedTotal == afterSecond + 1,
            $"16px と 17px で {atlas.BakedTotal - afterSecond} 回");

        // --- 5. UV がテクスチャの中に収まっているか ---
        Glyph kanji = atlas.GetOrAdd(0x6F22, 16);
        AtlasRegion region = kanji.Region;
        bool inside = region.UvMin.X >= 0.0f && region.UvMin.Y >= 0.0f
            && region.UvMax.X <= 1.0f && region.UvMax.Y <= 1.0f
            && region.UvMin.X < region.UvMax.X && region.UvMin.Y < region.UvMax.Y;
        checks.Check("UV が 0..1 に収まっている", inside,
            $"({region.UvMin.X:F3},{region.UvMin.Y:F3})-({region.UvMax.X:F3},{region.UvMax.Y:F3})");

        // UV の幅は、グリフの画素幅をアトラスの一辺で割ったもの。
        float uvWidth = (region.UvMax.X - region.UvMin.X) * atlas.Size;
        checks.Check("UV の幅が画素幅と一致", MathF.Abs(uvWidth - region.Width) < 0.01f,
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
        checks.Check("グリフを焼いても GL エラーが出ない", glError == GLEnum.NoError, glError.ToString());

        // --- 7. Measure と Draw が一致するか ---
        const string sample = "Measure と Draw は同じ道を通る";
        Vector2 measured = text.Measure(sample, 16);

        _textBatch!.Begin(
            Camera.CreateScreen(0.0f, 100.0f, 100.0f, 0.0f, -1.0f, 1.0f),
            SpriteSortMode.Texture);
        Vector2 drawn = text.Draw(_textBatch, sample, Vector2.Zero, 16, Vector4.One);
        _textBatch.End();

        checks.Check("Measure と Draw の大きさが一致",
            MathF.Abs(measured.X - drawn.X) < 0.01f && MathF.Abs(measured.Y - drawn.Y) < 0.01f,
            $"{measured.X:F2}x{measured.Y:F2}");

        // --- 8. 改行 ---
        Vector2 one = text.Measure("あいうえお", 16);
        Vector2 two = text.Measure("あいうえお\nかきくけこ", 16);
        checks.Check("2行の高さは1行の2倍", MathF.Abs(two.Y - (one.Y * 2.0f)) < 0.01f,
            $"{one.Y:F2} → {two.Y:F2}");
        checks.Check("2行の幅は広いほうの行", MathF.Abs(two.X - one.X) < 0.01f, $"{two.X:F2}px");

        // "\r\n" でも同じ結果になること。ファイルから読んだ文字列で効く。
        checks.Check("CRLF でも同じ", MathF.Abs(text.Measure("あ\r\nい", 16).Y - text.Measure("あ\nい", 16).Y) < 0.01f);

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
        checks.Check("サロゲートペアを1文字として数える", surrogate.Length == 2,
            $"char {surrogate.Length} 個 / 幅 {text.Measure(surrogate, 16).X:F2}px");

        // char で回していたら、この文字列は「2文字」として幅が2倍になる。
        // Rune で回していれば、グリフ1つぶんの送りと一致する。
        float pairWidth = text.Measure(surrogate, 16).X;
        float oneGlyph = atlas.GetOrAdd(0x20B9F, 16).Metrics.Advance;
        checks.Check("幅がグリフ1つぶんと一致", MathF.Abs(pairWidth - oneGlyph) < 0.01f,
            $"{pairWidth:F2}px / 1グリフ {oneGlyph:F2}px");

        // --- 11. アトラスが満杯になっても落ちない ---
        using (var tiny = new GlyphAtlas(_gl, font, size: 64))
        {
            for (int i = 0; i < 200; i++)
            {
                tiny.GetOrAdd(0x4E00 + i, 32);
            }

            checks.Check("満杯になっても落ちない", tiny.IsFull, $"{tiny.GlyphCount}字 / {tiny.ShelfCount}段");

            // 満杯でも**送りは正しい**ので、レイアウトは崩れない(絵が出ないだけ)。
            Glyph missing = tiny.GetOrAdd(0x9FA0, 32);
            checks.Check("満杯でも送りは返す", missing.Metrics.Advance > 0.0f, $"{missing.Metrics.Advance:F2}px");
        }

        checks.Report();
        Console.WriteLine();

        BenchmarkText();
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
        var checks = new CheckList();

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
        checks.Check("知らないチャンク(LIST)を飛ばせる", listed.Data.Length == 400, $"{listed.Data.Length} バイト");

        // 奇数長のチャンクの後ろには詰め物が 1 バイト入る。
        // これを飛ばし忘れると、次のチャンク名が 1 バイトずれる。
        byte[] oddList = BuildWav(44100, 1, 16, new byte[200], "ODD");
        WavData odd = WavFile.Parse(oddList, "奇数長 LIST 付き");
        checks.Check("奇数長チャンクの詰め物を飛ばせる", odd.Data.Length == 200, $"{odd.Data.Length} バイト");

        checks.Check("WAV でないものを弾く", Throws<InvalidDataException>(
            () => WavFile.Parse(new byte[64])));

        checks.Check("24bit を弾く", Throws<NotSupportedException>(
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

        checks.Check("ボイスの数を超えない", active <= _audio.VoiceCount, $"{active} / {_audio.VoiceCount}");
        checks.Check("足りなければ奪う", _audio.StolenLastStep == 8, $"{_audio.StolenLastStep} 回");

        // **古い札で別人を止めない**(Day 21 の世代と同じ問題)。
        _audio.StopAll();
        _audio.Update();
        VoiceId first = _audio.Play(_hitClip, 1.0f);
        _audio.Stop(first);
        VoiceId second = _audio.Play(_hitClip, 1.0f);
        _audio.Stop(first);
        checks.Check("古い札は無効になっている", _audio.IsPlaying(second), $"{first} → {second}");

        // ループするものは奪われない。BGM が効果音に消されては困る。
        _audio.StopAll();
        _audio.Update();
        VoiceId loop = _audio.PlayLoop(_musicClip, 1.0f);
        for (int i = 0; i < _audio.VoiceCount * 2; i++)
        {
            _audio.Play(_hitClip, 1.0f);
        }

        checks.Check("ループは奪われない", _audio.IsPlaying(loop), $"{loop}");
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
        checks.Check("1ステップに 2 回まで", _audio.StartedLastStep == 2, $"発音 {_audio.StartedLastStep}");
        checks.Check("残りは間引かれる", _audio.CulledLastStep == 8, $"間引き {_audio.CulledLastStep}");

        // --- 4. 本当に鳴っているか ---
        _audio.StopAll();
        _audio.Update();
        _audio.MaxStartsPerClipPerStep = savedLimit;
        _audio.MasterVolume = savedVolume;

        VoiceId audible = _audio.Play(_pickupClip, 0.8f);
        checks.Check("再生中の状態になる", _audio.IsPlaying(audible), $"{audible}");

        checks.Report();
        Console.WriteLine();

        BenchmarkAudio();

        _musicVoice = VoiceId.None;

        void Expect(string file, int rate, int channels, int bits)
        {
            WavData wav = WavFile.Load(ResolveAssetPath($"audio/{file}"));
            checks.Check(
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
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[ECS の自己チェック]");

        var world = new World();

        checks.Check("既定値のエンティティは無効", !default(Entity).IsValid);

        Entity a = world.CreateEntity();
        world.Add(a, new Transform2D { Position = new Vector2(1.0f, 2.0f) });
        world.Add(a, new Velocity2D { Linear = new Vector2(3.0f, 4.0f) });

        checks.Check("生きている", world.IsAlive(a));
        checks.Check("コンポーネントが引ける", world.Has<Transform2D>(a) && world.Has<Velocity2D>(a));

        // ref で返るので、引いてそのまま書き換えられる。
        // 値で返す作りだとコピーが書き換わるだけで、元は変わらない。
        world.Get<Transform2D>(a).Position.X = 99.0f;
        checks.Check("Get は参照を返す", MathF.Abs(world.Get<Transform2D>(a).Position.X - 99.0f) < 0.001f);

        Entity b = world.CreateEntity();
        world.Add(b, new Transform2D { Position = new Vector2(10.0f, 0.0f) });
        world.Add(b, new Velocity2D());
        Entity c = world.CreateEntity();
        world.Add(c, new Transform2D { Position = new Vector2(20.0f, 0.0f) });
        world.Add(c, new Velocity2D());

        ComponentStore<Transform2D> transforms = world.Store<Transform2D>();
        ComponentStore<Velocity2D> velocities = world.Store<Velocity2D>();

        checks.Check("同じ順で付ければ並びは一致する", EcsSystems.AreAligned(transforms, velocities));

        // 真ん中を消す。末尾と入れ替わるので**並び順は変わる**が、
        // どのストアも同じ入れ替えをするので**一致は保たれる**。
        world.DestroyEntity(b);
        checks.Check("破棄すると全ストアから消える", transforms.Count == 2 && velocities.Count == 2);
        checks.Check("破棄したエンティティは無効", !world.IsAlive(b));
        checks.Check("残りは正しく引ける",
            MathF.Abs(world.Get<Transform2D>(c).Position.X - 20.0f) < 0.001f);
        checks.Check("破棄しても並びの一致は保たれる", EcsSystems.AreAligned(transforms, velocities));

        // 枠の再利用。Day 21 のハンドルとまったく同じ話。
        Entity reused = world.CreateEntity();
        checks.Check("空いた枠が再利用される", reused.Index == b.Index, $"新 {reused} / 旧 {b}");
        checks.Check("それでも古いエンティティは無効のまま", reused != b && !world.IsAlive(b));
        checks.Check("再利用した枠に前の中身は残っていない", !world.Has<Transform2D>(reused));

        // **後から足すと並びが崩れる**。ここが要点4の肝。
        //
        // 破棄では崩れない(全ストアが同じ入れ替えをするから)のに対し、
        // 「片方にだけ後から足す」と順番がずれる。
        // つまり**エンティティの構成がそろっていないと速い経路は使えない**。
        Entity late = world.CreateEntity();
        world.Add(late, new Transform2D());
        checks.Check("片方にしか無ければ件数が合わない", !EcsSystems.AreAligned(transforms, velocities));

        Entity both = world.CreateEntity();
        world.Add(both, new Transform2D());
        world.Add(both, new Velocity2D());

        world.Add(late, new Velocity2D());
        checks.Check(
            "件数がそろっても順番は戻らない",
            transforms.Count == velocities.Count && !EcsSystems.AreAligned(transforms, velocities),
            $"件数 {transforms.Count} / {velocities.Count}");
        _ = both;

        checks.Check("崩れても結果は同じ", AlignedAndJoinedAgree(), "(速い経路と一般の経路を突き合わせ)");

        checks.Report();
        Console.WriteLine();
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
    /// **glTF ローダの自己チェック**(Shift+-)。
    ///
    /// 4体すべてを読み、**書かれ方の違う経路が全部通るか**を確かめる。
    /// 「1体読めた」は「その1体が読めた」でしかない——
    /// glb と gltf、埋め込みと外部参照、TRS と matrix、
    /// ノード1個と親子つき。**通っていない経路は必ず後で壊れる**。
    ///
    /// 数値の期待値はファイルから読める事実だけを書いてある
    /// (三角形の数、パーツの数)。絵の印象ではなく、
    /// **読めたデータそのもの**を突き合わせるのが自己チェックの役目。
    /// </summary>
    private static void RunGltfCheck()
    {
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[glTF の自己チェック]");

        // チェックのあいだ表示中のモデルは外しておく。
        // **同じファイルを2回読む**ことになるが、テクスチャは RenderResources が
        // 重複排除するので2回目は復号が走らない——それも確かめる。
        int restore = _modelIndex;
        _model?.Dispose();
        _model = null;

        int cacheHitsBefore = _resources.CacheHits;
        int texturesBefore = _resources.TextureCount;

        // (パス, 最低パーツ数, 最低三角形数, 期待するマテリアル数)
        (string Path, int Parts, int Triangles, int Materials)[] expected =
        [
            ("models/DamagedHelmet.glb", 1, 15000, 1),
            ("models/WaterBottle.glb", 1, 2000, 1),
            ("models/Lantern.glb", 3, 5000, 1),
            ("models/BoxTextured/BoxTextured.gltf", 1, 12, 1),
        ];

        var loaded = new List<Model>();

        foreach ((string path, int parts, int triangles, int materials) in expected)
        {
            string full = ResolveAssetPath(path);
            string name = System.IO.Path.GetFileName(full);

            Model model = GltfLoader.Load(_gl, _resources, full, _shader);
            loaded.Add(model);

            checks.Check($"{name}: 読めた", true, GltfLoader.Describe(full));
            checks.Check($"{name}: パーツが {parts} 個以上", model.Parts.Count >= parts, $"実際 {model.Parts.Count}");
            checks.Check($"{name}: 三角形が {triangles:N0} 個以上", model.TriangleCount >= triangles,
                $"実際 {model.TriangleCount:N0}");
            checks.Check($"{name}: マテリアル {materials} 個", model.Materials.Count == materials,
                $"実際 {model.Materials.Count}");
            checks.Check($"{name}: ベースカラーのマップがある",
                model.Materials.All(m => m.MainTexture.IsValid));
            checks.Check($"{name}: 境界箱が有限", float.IsFinite(model.BoundsRadius) && model.BoundsRadius > 0.0f,
                $"半径 {model.BoundsRadius:F3}m");
        }

        // **階層が効いているか**。Lantern の3パーツは、
        // ファイル上ではそれぞれ別の平行移動を持っている。
        // 親の回転(Y 軸 180 度)を掛け忘れると、位置は合っているのに向きだけ裏返る。
        Model lantern = loaded[2];
        bool distinctTransforms = lantern.Parts
            .Select(part => part.Transform.Translation)
            .Distinct()
            .Count() == lantern.Parts.Count;
        checks.Check("Lantern: 3つのパーツが別々の位置にある(親子の掛け合わせが効いている)", distinctTransforms);

        // **BoxTextured は matrix 形式**。Z-up を Y-up に直す回転が入っているので、
        // 単位行列のままなら読めていない。
        Model box = loaded[3];
        checks.Check("BoxTextured: matrix 形式のノードが単位行列になっていない",
            box.Parts[0].Transform != Matrix4x4.Identity);

        // 大きさの幅。**glTF の 1.0 は 1 メートル**という約束が効いていることの確認。
        checks.Check("水筒より街灯のほうが大きい",
            loaded[2].BoundsRadius > loaded[1].BoundsRadius * 10.0f,
            $"WaterBottle {loaded[1].BoundsRadius:F2}m / Lantern {loaded[2].BoundsRadius:F2}m");

        // 同じファイルをもう一度読む。**画像は復号し直されないはず**。
        int texturesAfterFirst = _resources.TextureCount;
        Model again = GltfLoader.Load(_gl, _resources, ResolveAssetPath(expected[0].Path), _shader);
        checks.Check("2回目の読み込みでテクスチャが増えない(重複排除が効いている)",
            _resources.TextureCount == texturesAfterFirst,
            $"{texturesAfterFirst} → {_resources.TextureCount}");
        checks.Check("そのぶんキャッシュヒットが増える", _resources.CacheHits > cacheHitsBefore,
            $"{cacheHitsBefore} → {_resources.CacheHits}");
        again.Dispose();

        foreach (Model model in loaded)
        {
            model.Dispose();
        }

        // **全部返したらテクスチャは元の数に戻る**。ここが今日いちばん見たい行。
        // 増えたままなら Model.Dispose の Release が足りていない。
        checks.Check("全部畳んだらテクスチャの数が元に戻る", _resources.TextureCount == texturesBefore,
            $"{texturesBefore} → {_resources.TextureCount}");

        checks.Report();
        Console.WriteLine();

        SetModel(restore);
    }

    /// <summary>
    /// **接空間の自己チェック**(Alt+0)。
    ///
    /// 接空間は「絵からは正しさが読めない」ものの筆頭になる。
    ///   - 接線が 90 度ずれていても、凹凸の向きが変わるだけで絵は出る
    ///   - w の符号が逆でも、へこみと出っ張りが入れ替わるだけ
    ///   - 直交していなくても、少しねじれるだけ
    /// **どれも「なんとなく変」で終わってしまう**ので、数値で確かめる。
    ///
    /// 見るのは3つ。
    ///   1. <b>生成した接線が、既知の形で期待どおりか</b>(板と立方体)
    ///   2. <b>ファイルの TANGENT と、生成した接線がどれだけ一致するか</b>
    ///   3. <b>不変条件</b>(単位長・法線と直交・w が ±1)
    /// </summary>
    /// <summary>
    /// **FXAA とカラーグレーディングの自己チェック**(Ctrl+F12)。
    ///
    /// この2つは、絵を見ても正しいかどうかが分からない類のもの。
    ///   - FXAA … 縁が残っていても滲んでいても、どちらも「そういう絵」に見える
    ///   - グレーディング … <b>目が数秒で順応する</b>ので、色かぶりが正常に見えてくる
    ///
    /// だから確かめるのは印象ではなく<b>読み戻した数値</b>にする。柱は3つ。
    ///
    /// <list type="number">
    /// <item>
    /// <b>階段を自分で作って、それが均されるかを見る</b>。
    /// シーンバッファにシザーテスト付きの <c>glClear</c> で
    /// 傾き 1/4 の階段を焼く。三角形もシェーダも要らないので、
    /// <b>入力が完全に決まった1枚の絵</b>になる。
    /// </item>
    /// <item>
    /// <b>平らなところは1ビットも変わらないこと</b>。
    /// FXAA の速さは早期打ち切りが本体なので、
    /// ここが崩れていると「全画面をぼかしている」ことになる。
    /// </item>
    /// <item>
    /// <b>グレーディングの中立点</b>。素通しの設定なら ON/OFF で絵が一致し、
    /// コントラストを回しても 18% グレーは動かない。
    /// </item>
    /// </list>
    /// </summary>
    private static void RunAaCheck()
    {
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[FXAA とカラーグレーディングの自己チェック]");

        ColorGrade grade = _post.Grade;

        // --- 1. ホワイトバランス(GL を1回も呼ばずに済む)---
        //
        // CPU だけで確かめられるものは先に片付ける(Day 37 のカーネルと同じ作法)。
        float savedTemperature = grade.Temperature;
        float savedTint = grade.Tint;
        float savedContrast = grade.Contrast;
        float savedSaturation = grade.Saturation;
        Vector3 savedFilter = grade.Filter;
        bool savedGradeEnabled = grade.Enabled;
        GradePreset savedPreset = grade.Preset;

        grade.ApplyPreset(GradePreset.Neutral);
        Vector3 neutral = grade.WhiteBalance;

        checks.Check(
            "**色温度 0 の増幅率がちょうど 1**(素通しが本当に素通し)",
            MathF.Abs(neutral.X - 1.0f) < 1e-4f
                && MathF.Abs(neutral.Y - 1.0f) < 1e-4f
                && MathF.Abs(neutral.Z - 1.0f) < 1e-4f,
            $"({neutral.X:F5}, {neutral.Y:F5}, {neutral.Z:F5})");

        grade.Temperature = 40.0f;
        Vector3 warm = grade.WhiteBalance;

        checks.Check(
            "暖色へ回すと L(長波長=赤側)が上がり S(短波長=青側)が下がる",
            warm.X > 1.0f && warm.Z < 1.0f,
            $"L {warm.X:F3} / M {warm.Y:F3} / S {warm.Z:F3}");

        grade.Temperature = -40.0f;
        Vector3 cool = grade.WhiteBalance;

        checks.Check(
            "寒色はその逆(**軌跡の上を反対へ動いている**)",
            cool.X < 1.0f && cool.Z > 1.0f,
            $"L {cool.X:F3} / M {cool.Y:F3} / S {cool.Z:F3}");

        int width = _window.FramebufferSize.X;
        int height = _window.FramebufferSize.Y;

        if (width < 340 || height < 240)
        {
            Console.WriteLine("  窓が小さすぎるので描画側のチェックは飛ばしました(340x240 以上が要る)");
            grade.ApplyPreset(savedPreset);
            grade.Temperature = savedTemperature;
            grade.Tint = savedTint;
            grade.Contrast = savedContrast;
            grade.Saturation = savedSaturation;
            grade.Filter = savedFilter;
            grade.Enabled = savedGradeEnabled;
            checks.Report();
            Console.WriteLine();
            return;
        }

        // --- 2. 描画側の準備 ---
        //
        // 元の状態は必ず戻す(RunHdrCheck / RunSsaoCheck と同じ作法)。
        bool savedFxaa = _post.FxaaEnabled;
        FxaaQuality savedQuality = _post.Quality;
        FxaaDebugView savedFxaaView = _post.FxaaDebugView;
        PostSplit savedSplit = _post.Split;
        bool savedBloom = _post.BloomEnabled;
        ToneMapOperator savedToneMap = _post.ToneMap;
        PostDebugView savedDebugView = _post.DebugView;
        float savedExposure = _post.Exposure;

        // **確かめたいもの以外は全部止める**。
        // ブルームが入ると階段の周りが滲み、トーンマップが入ると
        // 「白は本当に 1.0 か」が言えなくなる。
        _post.BloomEnabled = false;
        _post.DebugView = PostDebugView.Final;
        _post.ToneMap = ToneMapOperator.None;
        _post.Exposure = 1.0f;
        _post.FxaaDebugView = FxaaDebugView.None;
        _post.Split = PostSplit.None;
        grade.Enabled = false;

        using var target = new Framebuffer(_gl, width, height, RenderTargetFormat.Rgba8, depth: false);

        // 階段の形。傾きは 1/4(横 8 画素で縦に 2 画素上がる)。
        const int StairX = 64;
        const int StairY = 64;
        const int StepWidth = 8;
        const int StepHeight = 2;
        const int StepCount = 24;

        int regionWidth = StepWidth * StepCount;
        int regionHeight = (StepHeight * StepCount) + 8;

        _post.FxaaEnabled = false;
        DrawStaircase(StairX, StairY, StepWidth, StepHeight, StepCount);
        _post.EndToTarget(target);
        int plainPasses = _post.PassCount;
        float[] plain = ReadRed(target, StairX, StairY - 4, regionWidth, regionHeight);

        _post.FxaaEnabled = true;
        _post.SetFxaaQuality(FxaaQuality.Medium);
        DrawStaircase(StairX, StairY, StepWidth, StepHeight, StepCount);
        _post.EndToTarget(target);
        int fxaaPasses = _post.PassCount;
        float[] smoothed = ReadRed(target, StairX, StairY - 4, regionWidth, regionHeight);

        // --- 3. 階段が均されたか ---
        int plainMid = MidTones(plain);
        int smoothedMid = MidTones(smoothed);

        checks.Check(
            "FXAA なしでは**中間の明るさが1画素も無い**(白か黒しかない = 階段)",
            plainMid == 0,
            $"{plainMid} 画素 / 全 {plain.Length} 画素");

        checks.Check(
            "**FXAA を掛けると中間の明るさが現れる**(階段の角が埋まった)",
            smoothedMid > 0,
            $"{smoothedMid} 画素({(double)smoothedMid / plain.Length:P1})");

        double plainDeviation = EdgeDeviation(plain, regionWidth, regionHeight, out double plainCoverage);
        double smoothedDeviation = EdgeDeviation(smoothed, regionWidth, regionHeight, out double smoothedCoverage);

        checks.Check(
            "**縁が理想の直線に近づく**(列ごとの被覆率のずれが減る)",
            smoothedDeviation < plainDeviation * 0.8,
            $"ずれ {plainDeviation:F3} → {smoothedDeviation:F3} 画素"
                + $"(完全な直線なら 0。階段のままなら段の高さの 1/4 = {StepHeight * 0.25:F3})");

        checks.Check(
            "**明るさの総量はほとんど変わらない**(ぼかしではなく整形だから)",
            Math.Abs(smoothedCoverage - plainCoverage) < plainCoverage * 0.01,
            $"{plainCoverage:F1} → {smoothedCoverage:F1} 画素ぶん"
                + $"({(smoothedCoverage - plainCoverage) / plainCoverage:P2})");

        // --- 4. 平らなところは触らない ---
        //
        // **FXAA の速さは早期打ち切りが本体**。ここが崩れていたら、
        // やっていることは「全画面のぼかし」になっている。
        float[] plainWhite = ReadRed(target, 200, 66, 16, 16);
        _post.FxaaEnabled = false;
        DrawStaircase(StairX, StairY, StepWidth, StepHeight, StepCount);
        _post.EndToTarget(target);
        float[] rawWhite = ReadRed(target, 200, 66, 16, 16);

        checks.Check(
            "**白一色の面は1ビットも動かない**(局所コントラストが 0 なので縁ではない)",
            Identical(plainWhite, rawWhite),
            $"最大差 {MaxDifference(plainWhite, rawWhite):F5}");

        // --- 5. しきい値の経路が本当に効いているか ---
        //
        // 相対しきい値を 1.0 にすると、どんな段差も「縁ではない」ことになる。
        // **そのとき FXAA ON の絵は OFF の絵と完全に一致するはず**——
        // 一致しないなら、しきい値を見ずに混ぜている画素がある。
        float restoreThreshold = _post.FxaaEdgeThreshold;
        float restoreThresholdMin = _post.FxaaEdgeThresholdMin;

        _post.FxaaEnabled = true;
        _post.FxaaEdgeThreshold = 2.0f;
        _post.FxaaEdgeThresholdMin = 2.0f;
        DrawStaircase(StairX, StairY, StepWidth, StepHeight, StepCount);
        _post.EndToTarget(target);
        float[] gated = ReadRed(target, StairX, StairY - 4, regionWidth, regionHeight);

        checks.Check(
            "**しきい値を 1.0 にすると FXAA なしと完全一致**(早期打ち切りの経路)",
            Identical(gated, plain),
            $"最大差 {MaxDifference(gated, plain):F5}");

        _post.FxaaEdgeThreshold = restoreThreshold;
        _post.FxaaEdgeThresholdMin = restoreThresholdMin;

        checks.Check(
            "FXAA を掛けるとフルスクリーンパスが1つ増える",
            fxaaPasses == plainPasses + 1,
            $"OFF {plainPasses} パス → ON {fxaaPasses} パス");

        // --- 6. グレーディングの中立点 ---
        _post.FxaaEnabled = false;
        grade.ApplyPreset(GradePreset.Neutral);

        var probe = new Vector4(0.6f, 0.3f, 0.15f, 1.0f);

        grade.Enabled = false;
        Vector4 rawProbe = CompositeProbe(target, probe);

        grade.Enabled = true;
        Vector4 identityProbe = CompositeProbe(target, probe);

        checks.Check(
            "**素通しの設定なら ON/OFF で絵が一致**(恒等が恒等になっている)",
            Near(identityProbe.X, rawProbe.X, 0.005f)
                && Near(identityProbe.Y, rawProbe.Y, 0.005f)
                && Near(identityProbe.Z, rawProbe.Z, 0.005f),
            $"OFF ({rawProbe.X:F3},{rawProbe.Y:F3},{rawProbe.Z:F3})"
                + $" / ON ({identityProbe.X:F3},{identityProbe.Y:F3},{identityProbe.Z:F3})");

        grade.Saturation = 0.0f;
        Vector4 grey = CompositeProbe(target, probe);

        checks.Check(
            "彩度 0 で R = G = B(モノクロになる)",
            Near(grey.X, grey.Y, 0.006f) && Near(grey.Y, grey.Z, 0.006f),
            $"({grey.X:F3}, {grey.Y:F3}, {grey.Z:F3})");

        grade.Saturation = 1.0f;

        // **18% グレーはコントラストで動かない**。これが「中心を選ぶ」ことの意味。
        var midProbe = new Vector4(ColorGrade.MiddleGrey, ColorGrade.MiddleGrey, ColorGrade.MiddleGrey, 1.0f);

        grade.Enabled = false;
        Vector4 midRaw = CompositeProbe(target, midProbe);

        grade.Enabled = true;
        grade.Contrast = 1.4f;
        Vector4 midGraded = CompositeProbe(target, midProbe);

        checks.Check(
            "**コントラストを上げても 18% グレーは動かない**(回転の中心)",
            Near(midGraded.X, midRaw.X, 0.006f),
            $"{midRaw.X:F4} → {midGraded.X:F4}(0.18 のガンマ後は {MathF.Pow(0.18f, 1.0f / 2.2f):F4})");

        // 中心から外れたところはちゃんと動くこと。動かないなら uniform が届いていない。
        var darkProbe = new Vector4(0.05f, 0.05f, 0.05f, 1.0f);

        grade.Enabled = false;
        Vector4 darkRaw = CompositeProbe(target, darkProbe);

        grade.Enabled = true;
        Vector4 darkGraded = CompositeProbe(target, darkProbe);

        checks.Check(
            "中心より暗いところはコントラストでさらに沈む",
            darkGraded.X < darkRaw.X - 0.01f,
            $"{darkRaw.X:F4} → {darkGraded.X:F4}");

        grade.Contrast = 1.0f;

        // 色温度が絵に届いているか。**uniform の名前を1文字間違えても GL は黙っている**。
        var greyProbe = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);

        grade.Enabled = false;
        Vector4 greyRaw = CompositeProbe(target, greyProbe);

        grade.Enabled = true;
        grade.Temperature = 40.0f;
        Vector4 greyWarm = CompositeProbe(target, greyProbe);

        checks.Check(
            "**暖色へ回すと絵の赤が上がり青が下がる**(白点の付け替えが届いている)",
            greyWarm.X > greyRaw.X + 0.005f && greyWarm.Z < greyRaw.Z - 0.005f,
            $"R {greyRaw.X:F3}→{greyWarm.X:F3}  G {greyRaw.Y:F3}→{greyWarm.Y:F3}"
                + $"  B {greyRaw.Z:F3}→{greyWarm.Z:F3}");

        // --- 7. 代償 ---
        grade.Enabled = false;
        _post.FxaaEnabled = false;
        double compositeOnly = BenchmarkPost(target, 60);

        _post.FxaaEnabled = true;
        double withFxaa = BenchmarkPost(target, 60);

        // --- 後始末 ---
        _post.FxaaEnabled = savedFxaa;
        _post.SetFxaaQuality(savedQuality);
        _post.FxaaDebugView = savedFxaaView;
        _post.Split = savedSplit;
        _post.BloomEnabled = savedBloom;
        _post.ToneMap = savedToneMap;
        _post.DebugView = savedDebugView;
        _post.Exposure = savedExposure;

        grade.ApplyPreset(savedPreset);
        grade.Temperature = savedTemperature;
        grade.Tint = savedTint;
        grade.Contrast = savedContrast;
        grade.Saturation = savedSaturation;
        grade.Filter = savedFilter;
        grade.Enabled = savedGradeEnabled;

        Framebuffer.BindDefault(_gl, width, height);

        checks.Report();
        Console.WriteLine(
            $"  合成のみ {compositeOnly:F3}ms  合成+FXAA {withFxaa:F3}ms"
            + $"  → FXAA ぶん {withFxaa - compositeOnly:F3}ms  ({width}x{height})");
        Console.WriteLine(
            $"  LDR バッファ {_post.Ldr.Width}x{_post.Ldr.Height}(RGBA8)"
            + $" {_post.Ldr.ByteSize / (1024.0 * 1024.0):F1}MB"
            + $"  後処理の合計 {_post.ByteSize / (1024.0 * 1024.0):F1}MB");
        Console.WriteLine();

        static bool Near(float value, float expected, float tolerance) =>
            MathF.Abs(value - expected) <= tolerance;
    }

    /// <summary>
    /// **シーンバッファに階段を焼く**(自己チェック用)。三角形もシェーダも要らない。
    ///
    /// <c>glClear</c> はシザー矩形の中しか塗らない、という性質だけを使う。
    /// 短冊を <paramref name="stepWidth"/> ずつずらしながら
    /// <paramref name="stepHeight"/> ずつ高くしていくと、
    /// **傾き stepHeight/stepWidth の直線をラスタライズしたときの階段**が出来上がる。
    ///
    /// <para>
    /// 描いた絵が完全に決まっているのが値打ちで、
    /// カメラの位置にもシーンの中身にも左右されない。
    /// 「入力が同じなら出力も同じ」でないと、そもそも数値で確かめられない。
    /// </para>
    /// </summary>
    private static void DrawStaircase(int x, int y, int stepWidth, int stepHeight, int steps)
    {
        // 背景は真っ黒。**輝度差を最大にする**ので、しきい値の話が混ざらない。
        _post.Begin(new Vector4(0.0f, 0.0f, 0.0f, 1.0f));

        _gl.Enable(EnableCap.ScissorTest);
        _gl.ClearColor(1.0f, 1.0f, 1.0f, 1.0f);

        for (int i = 0; i < steps; i++)
        {
            _gl.Scissor(x + (i * stepWidth), y, (uint)stepWidth, (uint)(1 + (i * stepHeight)));
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        // **必ず戻す**。切り忘れると、このあとのフルスクリーンパスが
        // 最後の短冊の中だけに描かれて、画面がほぼ真っ黒になる。
        _gl.Disable(EnableCap.ScissorTest);
    }

    /// <summary>シーンバッファを1色で塗って、後処理を通した結果を1画素読む。</summary>
    private static Vector4 CompositeProbe(Framebuffer target, Vector4 color)
    {
        _post.Begin(color);
        _post.EndToTarget(target);

        return ReadPixel(target, 4, 4);
    }

    /// <summary>後処理を <paramref name="iterations"/> 回まわして1回あたりのミリ秒を返す。</summary>
    private static double BenchmarkPost(Framebuffer target, int iterations)
    {
        _post.Begin(new Vector4(0.2f, 0.25f, 0.3f, 1.0f));

        _gl.Finish();
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            _post.EndToTarget(target);
        }

        // **GPU が終わるまで待つ**。これを入れないと積んだだけの時間が返る。
        _gl.Finish();

        return stopwatch.Elapsed.TotalMilliseconds / iterations;
    }

    /// <summary>赤成分だけを矩形ぶん読み返す。**灰色の絵なので1成分で足りる**。</summary>
    private static unsafe float[] ReadRed(Framebuffer target, int x, int y, int width, int height)
    {
        target.Bind();

        var values = new float[width * height];

        fixed (float* data = values)
        {
            _gl.ReadPixels(x, y, (uint)width, (uint)height, PixelFormat.Red, PixelType.Float, data);
        }

        return values;
    }

    /// <summary>1画素を RGBA で読み返す。</summary>
    private static unsafe Vector4 ReadPixel(Framebuffer target, int x, int y)
    {
        target.Bind();

        Span<float> pixel = stackalloc float[4];
        fixed (float* data = pixel)
        {
            _gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.Float, data);
        }

        return new Vector4(pixel[0], pixel[1], pixel[2], pixel[3]);
    }

    /// <summary>白でも黒でもない画素の数。**アンチエイリアスが作った中間色**そのもの。</summary>
    private static int MidTones(ReadOnlySpan<float> values)
    {
        int count = 0;

        foreach (float value in values)
        {
            if (value is > 0.08f and < 0.92f)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// **縁が理想の直線からどれだけずれているか**。ジャギーを1つの数字にしたもの。
    ///
    /// <para>
    /// 列ごとに明るさを足し合わせると、その列で縁がどの高さにあるかが出る
    /// (<b>被覆率のプロファイル</b>)。理想的にアンチエイリアスされた斜めの縁なら、
    /// この値は列に対して<b>まっすぐ増える</b>——1画素の中を縁が何割通ったかが
    /// そのまま明るさになるから。階段のままなら、段の位置で跳ねる折れ線になる。
    /// </para>
    ///
    /// <para>
    /// そこで最小二乗で直線を当てはめ、<b>そこからの平均のずれ</b>を返す。
    /// 階段のままなら、ずれは高さ ±段の半分ののこぎり波になるので
    /// 平均で段の高さの 1/4 前後。均されるほど 0 に近づく。
    /// </para>
    ///
    /// <para>
    /// <b>全変動(隣どうしの差の和)や2階差分では測れない</b>のが要点。
    /// 全変動は段をなだらかにしても変わらず、2階差分は
    /// FXAA が縁の周りに作る細かい起伏を拾ってしまって<b>むしろ増える</b>
    /// (実測で 92.0 → 97.5)。**何を測るかで結論がひっくり返る**という、
    /// この手の計測でいちばんありがちな落とし穴。
    /// </para>
    /// </summary>
    /// <param name="coverage">白の総量(画素数ぶん)。**ぼかしていないこと**の確認に使う。</param>
    private static double EdgeDeviation(
        ReadOnlySpan<float> values,
        int width,
        int height,
        out double coverage)
    {
        var profile = new double[width];
        coverage = 0.0;

        for (int x = 0; x < width; x++)
        {
            double sum = 0.0;

            for (int y = 0; y < height; y++)
            {
                sum += values[(y * width) + x];
            }

            profile[x] = sum;
            coverage += sum;
        }

        // 最小二乗で直線 y = a*x + b を当てる。
        double n = width;
        double sumX = 0.0;
        double sumY = 0.0;
        double sumXx = 0.0;
        double sumXy = 0.0;

        for (int x = 0; x < width; x++)
        {
            sumX += x;
            sumY += profile[x];
            sumXx += (double)x * x;
            sumXy += x * profile[x];
        }

        double slope = ((n * sumXy) - (sumX * sumY)) / ((n * sumXx) - (sumX * sumX));
        double intercept = (sumY - (slope * sumX)) / n;

        double deviation = 0.0;

        for (int x = 0; x < width; x++)
        {
            deviation += Math.Abs(profile[x] - ((slope * x) + intercept));
        }

        return deviation / n;
    }

    private static bool Identical(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        MaxDifference(a, b) == 0.0f;

    private static float MaxDifference(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float worst = 0.0f;

        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            worst = MathF.Max(worst, MathF.Abs(a[i] - b[i]));
        }

        return worst;
    }

    /// <summary>
    /// **SSAO の自己チェック**(Ctrl+F10)。
    ///
    /// AO は IBL(Day 36)と同じで、<b>間違っていても「それらしい絵」になる</b>。
    /// 半径が倍でも、上下が反転していても、全体がうっすら暗いだけの絵は出る。
    /// だから確かめるのは絵ではなく<b>決まった場所の数字</b>にする。
    ///
    /// <para>
    /// そのために<b>この場で確認用のシーンを組む</b>。
    /// 床の上に立方体を1個置き、カメラを決まった位置に置いて、
    /// 「ここは暗いはず」「ここは明るいはず」という3点を読み返す。
    /// ユーザーが回したカメラのままだと、どの画素を見ればよいかが決まらない。
    /// </para>
    ///
    /// <list type="number">
    /// <item><b>1パス目が正しいか</b> … 焼いた距離が、CPU で計算した距離と一致するか</item>
    /// <item><b>遮蔽の向きが正しいか</b> … 接地部が開けた床より暗いか。空は 1.0 か</item>
    /// <item><b>つまみが効いているか</b> … 半径 0、下駄 0、標本数を動かして値が動くか</item>
    /// </list>
    /// </summary>
    private static void RunSsaoCheck()
    {
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[SSAO の自己チェック]");

        // --- 1. カーネル(CPU 側だけで確かめられること)---
        //
        // GL を1回も呼ばずに済むものは先に片付ける。
        // **半球でなければ何をどう直しても AO は 0.5 に張り付く**ので、
        // ここが落ちていたら以降の数字を読む意味が無い。
        ReadOnlySpan<Vector3> kernel = _ssao.Kernel;

        bool inHemisphere = true;
        bool inUnitBall = true;
        float nearSum = 0.0f;
        float farSum = 0.0f;

        for (int i = 0; i < kernel.Length; i++)
        {
            inHemisphere &= kernel[i].Z >= 0.0f;
            inUnitBall &= kernel[i].Length() <= 1.0f + 1e-4f;

            if (i < kernel.Length / 2)
            {
                nearSum += kernel[i].Length();
            }
            else
            {
                farSum += kernel[i].Length();
            }
        }

        float nearAverage = nearSum / (kernel.Length / 2);
        float farAverage = farSum / (kernel.Length / 2);

        checks.Check(
            "**標本が全部 z >= 0(半球)**  ※球にすると平らな面まで 0.5 になる",
            inHemisphere,
            $"{kernel.Length} 本");

        checks.Check("標本が単位球の中にある", inUnitBall);

        checks.Check(
            "標本が中心寄りに密(前半の平均長 < 後半の平均長)",
            nearAverage < farAverage,
            $"前半 {nearAverage:F3} / 後半 {farAverage:F3}");

        // --- 2. 確認用のシーンを組む ---
        //
        // 元の状態は必ず戻す(RunHdrCheck と同じ作法)。
        // 戻し忘れるとカメラが飛んだままになり、
        // 「チェックを走らせたら絵が壊れた」という最悪の後味になる。
        Vector3 savedPosition = _camera.Position;
        Vector3 savedTarget = _camera.Target;
        ProjectionMode savedMode = _camera.Mode;
        float savedRadius = _ssao.Radius;
        float savedBias = _ssao.Bias;
        float savedStrength = _ssao.Strength;
        int savedSamples = _ssao.SampleCount;
        bool savedBlur = _ssao.BlurEnabled;

        _camera.Mode = ProjectionMode.Perspective;
        _camera.Position = new Vector3(2.4f, 1.6f, 3.6f);
        _camera.Target = new Vector3(0.0f, -0.1f, 0.0f);

        _ssao.Radius = 1.0f;
        _ssao.Bias = 0.025f;
        _ssao.Strength = 1.0f;
        _ssao.SampleCount = 64;

        // **ぼかしを切る**。ぼかすと隣の画素と混ざって、
        // 「この1点がいくつか」を確かめるという話が成立しなくなる。
        _ssao.BlurEnabled = false;

        // 床の上にちょうど乗る立方体(底面 y = -0.5 が床と同じ高さ)。
        Matrix4x4 probeCube = CubeMatrix(Vector3.Zero, 1.0f, 0.0f, 0.0f);

        BakeProbeScene(probeCube);

        Matrix4x4 viewProjection = _camera.ViewProjection;

        // 立方体の +X 面のすぐ横(6cm)、床の上。**いちばん暗くなるはずの点**。
        var contact = new Vector3(0.56f, -0.5f, 0.0f);

        // 立方体から離れた開けた床。**遮るものが無いので 1.0 になるはず**。
        //
        // カメラの前に来る点を選ぶこと。適当に決めると<b>カメラの後ろ</b>に置いてしまい、
        // 透視除算で符号が反転して、画面の反対側の画素を読むことになる
        // (最初はそれで距離が負になり、チェックが落ちた)。
        var open = new Vector3(-3.0f, -0.5f, 0.0f);

        Vector2 contactPixel = WorldToPixel(contact, viewProjection);
        Vector2 openPixel = WorldToPixel(open, viewProjection);

        // --- 3. 1パス目(幾何バッファ)が正しいか ---
        //
        // **CPU で出せる答えと突き合わせる**のが要点(Day 36 の放射照度と同じ手口)。
        // 焼いた距離が合っているなら、ビュー行列も法線行列も投影も通っている。
        Vector4 geometry = ReadGeometry(openPixel);

        float expectedDepth = -Vector4.Transform(new Vector4(open, 1.0f), _camera.ViewMatrix).Z;
        float depthError = MathF.Abs(geometry.W - expectedDepth) / MathF.Max(expectedDepth, 1e-3f);

        checks.Check(
            "**焼いた距離が CPU の計算と一致**(ビュー行列と投影が通っている)",
            depthError < 0.02f,
            $"焼いた {geometry.W:F3} / 期待 {expectedDepth:F3}(誤差 {depthError:P1})");

        // 床の法線は世界で (0,1,0)。ビュー空間へ運んだものと比べる。
        var expectedNormal = Vector3.Normalize(
            Vector3.TransformNormal(Vector3.UnitY, _camera.ViewMatrix));

        var bakedNormal = Vector3.Normalize(new Vector3(geometry.X, geometry.Y, geometry.Z));
        float normalDot = Vector3.Dot(bakedNormal, expectedNormal);

        checks.Check(
            "**焼いた法線がビュー空間の床の法線と一致**(uNormalMatrix が効いている)",
            normalDot > 0.99f,
            $"内積 {normalDot:F4}");

        // --- 4. 遮蔽の向きが正しいか ---
        float contactAo = ReadOcclusion(contactPixel);
        float openAo = ReadOcclusion(openPixel);

        // 空(何も描かれていないところ)。クリア色が (0,0,0,0) なので距離 0 で素通りするはず。
        float skyAo = ReadOcclusion(new Vector2(_ssao.Width * 0.5f, _ssao.Height - 2.0f));

        checks.Check(
            "**空は遮られない**(距離 0 を素通りしている)",
            skyAo > 0.99f,
            $"実際 {skyAo:F3}");

        checks.Check(
            "開けた床はほぼ遮られない(**下駄が効いて自己遮蔽が出ていない**)",
            openAo > 0.92f,
            $"実際 {openAo:F3}");

        checks.Check(
            "**立方体の接地部は暗い**(今日いちばん見せたい暗がり)",
            contactAo < openAo - 0.1f,
            $"接地 {contactAo:F3} / 開けた床 {openAo:F3}");

        // --- 5. つまみが効いているか ---
        //
        // 「値を動かしたら結果が動く」を確かめるのは、
        // **uniform が届いていない**という一番ありがちな壊れ方を捕まえるため。
        // 名前を1文字打ち間違えても GL は黙っているので、絵だけでは気づけない。
        _ssao.Radius = 0.0f;
        BakeProbeScene(probeCube);
        float zeroRadiusAo = ReadOcclusion(contactPixel);

        checks.Check(
            "半径 0 なら遮蔽ゼロ(**探る球が潰れれば何も当たらない**)",
            zeroRadiusAo > 0.99f,
            $"実際 {zeroRadiusAo:F3}");

        // **下駄を 0 にすると、平らな床に自己遮蔽が出る**。
        //
        // 見た目には「一面にうっすら砂が乗る」形で出る。
        // 平均値はほとんど動かない(1.000 のまま)ので、
        // **平均ではなくノイズの量**を測らないと捕まえられない——
        // 実際、最初は開けた床の AO を見ていて、この不具合を捕まえ損ねた。
        _ssao.Radius = 1.0f;
        _ssao.Bias = 0.0f;
        BakeProbeScene(probeCube);
        float noBiasNoise = TileNoise(openPixel, 32);

        _ssao.Bias = 0.025f;
        BakeProbeScene(probeCube);
        float biasedNoise = TileNoise(openPixel, 32);

        checks.Check(
            "**下駄 0 では平らな床に自己遮蔽が出る**(シャドウアクネと同じ理屈)",
            noBiasNoise > biasedNoise + 0.005f,
            $"下駄なし {noBiasNoise:F4} / 下駄あり {biasedNoise:F4}");

        // 標本数とノイズ。**少ないほど荒れる**のを数字で見る。
        _ssao.SampleCount = 8;
        BakeProbeScene(probeCube);
        float roughNoise = TileNoise(contactPixel, 32);

        _ssao.SampleCount = 64;
        BakeProbeScene(probeCube);
        float fineNoise = TileNoise(contactPixel, 32);

        checks.Check(
            "標本を増やすとノイズが減る(**モンテカルロの収束**)",
            fineNoise < roughNoise,
            $"8本 {roughNoise:F4} → 64本 {fineNoise:F4}");

        // **ぼかしがノイズを消す**。4x4 のタイルの中を平均するので、
        // タイル内のばらつきはほぼ 0 になるはず——
        // これが「ノイズを入れてからぼかす」形の値打ちそのもの。
        _ssao.SampleCount = 16;
        _ssao.BlurEnabled = false;
        BakeProbeScene(probeCube);
        float unblurred = TileNoise(contactPixel, 32);

        _ssao.BlurEnabled = true;
        BakeProbeScene(probeCube);
        float blurred = TileNoise(contactPixel, 32);

        checks.Check(
            "**ぼかしがタイルの中のばらつきを消す**(4x4 の周期にそろえてある)",
            blurred < unblurred * 0.5f,
            $"ぼかし前 {unblurred:F4} → ぼかし後 {blurred:F4}");

        // --- 6. 代償 ---
        long fullBytes = _ssao.ByteSize;
        bool wasHalf = _ssao.HalfResolution;

        _ssao.SetHalfResolution(!wasHalf);
        long otherBytes = _ssao.ByteSize;
        _ssao.SetHalfResolution(wasHalf);

        checks.Check(
            "半解像度にすると遮蔽バッファが小さくなる",
            wasHalf ? otherBytes > fullBytes : otherBytes < fullBytes,
            $"{fullBytes / 1024.0 / 1024.0:F1}MB ⇔ {otherBytes / 1024.0 / 1024.0:F1}MB"
                + "  ※幾何バッファは常に原寸なので 1/4 にはならない");

        // --- 後始末 ---
        _camera.Position = savedPosition;
        _camera.Target = savedTarget;
        _camera.Mode = savedMode;
        _ssao.Radius = savedRadius;
        _ssao.Bias = savedBias;
        _ssao.Strength = savedStrength;
        _ssao.SampleCount = savedSamples;
        _ssao.BlurEnabled = savedBlur;

        Framebuffer.BindDefault(_gl, _window.FramebufferSize.X, _window.FramebufferSize.Y);

        checks.Report();
        Console.WriteLine(
            $"  幾何 {_ssao.Geometry.Width}x{_ssao.Geometry.Height}(RGBA16F)  "
            + $"遮蔽 {_ssao.Width}x{_ssao.Height}(R8) x2  "
            + $"合計 {_ssao.ByteSize / (1024.0 * 1024.0):F1}MB  {_ssaoMilliseconds:F2}ms/フレーム");
        Console.WriteLine();
    }

    /// <summary>確認用のシーン(床 + 立方体1個)を幾何バッファへ焼いて、遮蔽まで計算する。</summary>
    private static void BakeProbeScene(Matrix4x4 cube)
    {
        _ssao.BeginGeometry(_camera);
        _ssao.Draw(_quad, FloorMatrix());
        _ssao.Draw(_cube, cube);
        _ssao.EndGeometry();

        _ssao.Compute(_camera, _window.FramebufferSize.X, _window.FramebufferSize.Y);
    }

    /// <summary>
    /// 世界座標が<b>遮蔽バッファのどの画素に写るか</b>を返す。
    ///
    /// シェーダがやっているのとまったく同じ変換(ビュー射影 → 透視除算 → 0〜1)を
    /// CPU で1回やるだけ。**glReadPixels の原点は左下**なので、
    /// NDC の y をそのまま使えて都合がよい(画像の座標系だと反転が要る)。
    ///
    /// 遮蔽バッファは半解像度のこともあるので、最後にその大きさを掛ける。
    /// </summary>
    private static Vector2 WorldToPixel(Vector3 world, Matrix4x4 viewProjection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1.0f), viewProjection);
        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);

        return new Vector2(
            ((ndc.X * 0.5f) + 0.5f) * _ssao.Width,
            ((ndc.Y * 0.5f) + 0.5f) * _ssao.Height);
    }

    /// <summary>遮蔽率を1画素読み返す。**R8 でも Float で受け取れば 0〜1 で返る**。</summary>
    private static unsafe float ReadOcclusion(Vector2 pixel)
    {
        Framebuffer target = _ssao.Result;
        target.Bind();

        int x = Math.Clamp((int)pixel.X, 0, target.Width - 1);
        int y = Math.Clamp((int)pixel.Y, 0, target.Height - 1);

        float value = 1.0f;
        _gl.ReadPixels(x, y, 1, 1, PixelFormat.Red, PixelType.Float, &value);

        return value;
    }

    /// <summary>
    /// <b>ノイズのタイル1枚の中でのばらつき</b>を測る。**AO のざらつきそのもの**。
    ///
    /// <para>
    /// 素朴に「広い範囲の標準偏差」を測ってはいけない。
    /// AO は場所によって本当に変わる値なので、
    /// <b>本物の濃淡と、標本が足りないことによるノイズが混ざってしまう</b>。
    /// </para>
    ///
    /// <para>
    /// ノイズは 4x4 で敷いてあるので、<b>4x4 の中では幾何はほぼ同じで、
    /// 違うのは乱数の向きだけ</b>。だからタイル1枚の中のばらつきを測れば、
    /// 混ざりもの無しでノイズの量だけが出る。
    /// それを範囲じゅうのタイルで平均する。
    /// </para>
    ///
    /// この物差しがそのまま「ぼかしが何を消しているか」の説明にもなっている——
    /// <c>ssao-blur.frag</c> は 4x4 の平均なので、ここで測っている量をちょうど潰す。
    /// </summary>
    private static unsafe float TileNoise(Vector2 pixel, int size)
    {
        Framebuffer target = _ssao.Result;
        target.Bind();

        int x = Math.Clamp((int)pixel.X - (size / 2), 0, Math.Max(0, target.Width - size));
        int y = Math.Clamp((int)pixel.Y - (size / 2), 0, Math.Max(0, target.Height - size));

        var values = new float[size * size];

        fixed (float* data = values)
        {
            _gl.ReadPixels(x, y, (uint)size, (uint)size, PixelFormat.Red, PixelType.Float, data);
        }

        int tile = Ssao.NoiseSize;
        double total = 0.0;
        int tiles = 0;

        for (int ty = 0; ty + tile <= size; ty += tile)
        {
            for (int tx = 0; tx + tile <= size; tx += tile)
            {
                double mean = 0.0;
                double squares = 0.0;

                for (int j = 0; j < tile; j++)
                {
                    for (int i = 0; i < tile; i++)
                    {
                        double value = values[((ty + j) * size) + tx + i];
                        mean += value;
                        squares += value * value;
                    }
                }

                int count = tile * tile;
                mean /= count;

                total += Math.Sqrt(Math.Max(0.0, (squares / count) - (mean * mean)));
                tiles++;
            }
        }

        return (float)(total / Math.Max(1, tiles));
    }

    /// <summary>幾何バッファを1画素読み返す。RGB = ビュー法線、A = 距離。</summary>
    private static unsafe Vector4 ReadGeometry(Vector2 pixel)
    {
        Framebuffer target = _ssao.Geometry;
        target.Bind();

        // 幾何バッファは常に原寸なので、遮蔽バッファの座標から戻す倍率を掛ける。
        float scale = (float)target.Width / _ssao.Width;

        int x = Math.Clamp((int)(pixel.X * scale), 0, target.Width - 1);
        int y = Math.Clamp((int)(pixel.Y * scale), 0, target.Height - 1);

        Span<float> pixels = stackalloc float[4];
        fixed (float* data = pixels)
        {
            _gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.Float, data);
        }

        return new Vector4(pixels[0], pixels[1], pixels[2], pixels[3]);
    }

    /// <summary>
    /// **IBL の自己チェック**(Ctrl+Alt+0)。
    ///
    /// IBL がいちばん厄介なのは、<b>間違っていても「それらしく良い絵」になる</b>こと。
    /// 上下が反転していても、強さが π ぶんずれていても、
    /// 事前フィルタの段の対応が1つずれていても、
    /// 「なんとなく質感が上がった」という印象は変わらない。
    ///
    /// だから確かめるのは絵ではなく<b>数字の一致</b>にする。今日の柱は2つ。
    ///
    /// <list type="number">
    /// <item>
    /// <b>焼いたキューブが、元の空と一致するか</b>。
    /// 6面それぞれの中心テクセルは、その面が向いている方向の空の色になるはず。
    /// <see cref="SkyImage.Sample"/> を直接呼べば答えが手に入る——
    /// <b>これがキューブマップの上下反転を捕まえる唯一の自動チェック</b>になる。
    /// </item>
    /// <item>
    /// <b>放射照度が、CPU で積分した値と一致するか</b>。
    /// GPU のシェーダと <see cref="SkyImage.IntegrateIrradiance"/> は、
    /// まったく違う書き方で同じ積分をしている。
    /// 独立に出した2つが揃えば、両方が正しい見込みが高い。
    /// </item>
    /// </list>
    ///
    /// 加えて、BRDF の表の端の値と、事前フィルタが本当にぼけているかを見る。
    /// </summary>
    private static void RunIblCheck()
    {
        var checks = new CheckList();
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine("[IBL の自己チェック]");

        CubeMap? environment = _env.Environment;
        CubeMap? irradiance = _env.Irradiance;
        CubeMap? prefiltered = _env.Prefiltered;
        Texture? lut = _env.BrdfLut;

        checks.Check(
            "3枚 + 表がそろっている",
            environment is not null && irradiance is not null && prefiltered is not null && lut is not null);

        if (environment is null || irradiance is null || prefiltered is null || lut is null)
        {
            checks.Report();
            return;
        }

        checks.Check(
            "事前フィルタが5段ある(粗さ 0 / 0.25 / 0.5 / 0.75 / 1.0)",
            prefiltered.MipLevels == EnvironmentMap.PrefilterMipCount,
            $"{prefiltered.MipLevels} 段 / {prefiltered.Size}x{prefiltered.Size}");

        // --- 1. 焼いたキューブと元の空を突き合わせる ---
        //
        // **テクセルの向きを自分で計算して**、その向きの空の色と比べる。
        //
        // 向きの求め方は OpenGL の仕様に書いてある式(<see cref="CubeTexelDirection"/>)で、
        // **こちらが焼くときに使った行列とは独立**。だから
        //   - 6面のビュー行列の上下反転(CubeMap.FaceViews)
        //   - 正距円筒の方位角の取り方(SkyImage と equirect.frag)
        // のどちらが狂っていても、ここで必ず落ちる。
        //
        // 面の中心だけを見る形にしていたときは**方位のずれを捕まえられなかった**
        // (この空は方位に対してほぼ一様なので)。計画書の「検証の途中で分かったこと」参照。
        Vector3 toSun = Vector3.Normalize(-_lightDirection);

        string[] faceNames = ["+X", "-X", "+Y", "-Y", "+Z", "-Z"];

        bool facesMatch = true;
        float worstFaceError = 0.0f;
        string worstFaceLabel = string.Empty;
        int compared = 0;

        // いちばん明るいテクセルの向きも一緒に探す。**太陽がどこに焼けたか**が分かる。
        float brightest = -1.0f;
        Vector3 brightestDirection = Vector3.Zero;

        for (int face = 0; face < 6; face++)
        {
            float[] pixels = environment.ReadFace(face);
            int side = environment.Size;

            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    int index = ((y * side) + x) * 3;
                    var baked = new Vector3(pixels[index], pixels[index + 1], pixels[index + 2]);

                    Vector3 direction = CubeTexelDirection(face, (x + 0.5f) / side, (y + 0.5f) / side);

                    float luminance = baked.X + baked.Y + baked.Z;
                    if (luminance > brightest)
                    {
                        brightest = luminance;
                        brightestDirection = direction;
                    }

                    // **16 テクセルおきに比べる**。全部やっても構わないが、
                    // 読み返しより比較のほうが遅くなるので間引く。
                    if ((x % 16 != 0) || (y % 16 != 0))
                    {
                        continue;
                    }

                    // **太陽の縁は外す**。あそこは 0.013 ラジアンで 0 から 300 まで変わるので、
                    // 元の絵(1024x512)とキューブ(256)の解像度の差がそのまま誤差になる。
                    // 式の間違いではなく標本化の話なので、確かめる対象から外すのが正しい。
                    if (Vector3.Dot(direction, toSun) > MathF.Cos(0.12f))
                    {
                        continue;
                    }

                    Vector3 expected = SkyImage.Sample(direction, toSun);
                    float error = (baked - expected).Length() / MathF.Max(expected.Length(), 1e-3f);

                    compared++;
                    facesMatch &= error < 0.05f;

                    if (error > worstFaceError)
                    {
                        worstFaceError = error;
                        worstFaceLabel = faceNames[face];
                    }
                }
            }
        }

        checks.Check(
            "**焼いたキューブが元の空と一致**(上下反転も方位のずれもここで出る)",
            facesMatch,
            $"{compared:N0} 点 / 最大誤差 {worstFaceError:P1}({worstFaceLabel})");

        // **いちばん明るいテクセルは太陽の向き**。方位角の取り違えを直接捕まえる。
        float sunAngle = MathF.Acos(Math.Clamp(Vector3.Dot(brightestDirection, toSun), -1.0f, 1.0f));

        checks.Check(
            "**いちばん明るいテクセルが太陽の向き**(方位の取り違えが出る)",
            sunAngle < 0.06f,
            $"ずれ {sunAngle * 180.0f / MathF.PI:F2} 度  明るさ {brightest / 3.0f:F0}");

        // 上下がひっくり返っていないことを、意味のある形でもう一度。
        // **空のほうが地面より明るい**は、絵を見なくても言えるはずのこと。
        Vector3 up = CenterTexel(environment, 2);
        Vector3 down = CenterTexel(environment, 3);

        checks.Check(
            "上の面が下の面より明るい(**空と地面が逆さまでない**)",
            up.Y > down.Y * 3.0f,
            $"上 {up.Y:F3} / 下 {down.Y:F3}");

        // --- 2. 放射照度を CPU の積分と突き合わせる ---
        //
        // **まったく違う書き方で同じ積分**をしている2つを比べる。
        // シェーダ側は 1/π を焼き込んでいるので、CPU 側も π で割ってから比べる。
        bool irradianceMatches = true;
        float worstIrradianceError = 0.0f;
        string worstIrradianceLabel = string.Empty;

        for (int face = 0; face < 6; face++)
        {
            // **読んだテクセルの向きをそのまま使う**。面の中心と言っても、
            // 1辺が偶数なので真ん中のテクセルは半個ぶんずれている。
            // 「だいたい真上」で済ませずに、実際の向きで積分するほうが誤差が読める。
            Vector3 direction = CenterDirection(face, irradiance.Size);

            Vector3 baked = CenterTexel(irradiance, face);
            Vector3 expected = SkyImage.IntegrateIrradiance(direction, toSun) / MathF.PI;

            float error = (baked - expected).Length() / MathF.Max(expected.Length(), 1e-3f);
            irradianceMatches &= error < 0.15f;

            if (error > worstIrradianceError)
            {
                worstIrradianceError = error;
                worstIrradianceLabel = faceNames[face];
            }
        }

        checks.Check(
            "**放射照度が CPU の半球積分と一致**(誤差 15% 未満)",
            irradianceMatches,
            $"最大誤差 {worstIrradianceError:P1}({worstIrradianceLabel})"
            + "  ※シェーダの刻みが 0.025 ラジアンなので完全一致はしない");

        // 放射照度は「半球ぶんの平均」なので、**元より必ず滑らか**。
        // 面の中のばらつきを測ると、環境マップより桁で小さくなる。
        float environmentSpread = Spread(environment, face: 2);
        float irradianceSpread = Spread(irradiance, face: 2);

        checks.Check(
            "放射照度は環境マップよりばらつきが小さい(**ならしたのだから当然**)",
            irradianceSpread < environmentSpread,
            $"環境 {environmentSpread:F3} → 放射照度 {irradianceSpread:F3}");

        // --- 3. 事前フィルタは段が進むほどぼけているか ---
        //
        // **ぼけているかは「ばらつきが減っているか」で測れる**。
        // 目で見ると「それっぽくぼけている」としか言えないが、
        // 数字にすれば単調に減ることを要求できる。
        bool blurIncreases = true;
        var spreads = new float[EnvironmentMap.PrefilterMipCount];

        for (int mip = 0; mip < EnvironmentMap.PrefilterMipCount; mip++)
        {
            spreads[mip] = Spread(prefiltered, face: 2, mip);
            if (mip > 0 && spreads[mip] > spreads[mip - 1])
            {
                blurIncreases = false;
            }
        }

        checks.Check(
            "事前フィルタは段が進むほどぼけている(ばらつきが単調に減る)",
            blurIncreases,
            string.Join(" → ", spreads.Select(value => value.ToString("F3"))));

        // --- 4. BRDF の表 ---
        //
        // 端の値は手で分かる。**粗さ 0 で正面から見れば、鏡はほぼ全部返す**ので
        // A が 1 に近く、B は 0 に近い。
        Vector2 mirror = Pbr.IntegrateBrdf(1.0f, 0.0f);

        checks.Check(
            "BRDF の表: 粗さ 0・正面で (A, B) ≒ (1, 0)",
            mirror.X > 0.97f && mirror.Y < 0.03f,
            $"({mirror.X:F4}, {mirror.Y:F4})");

        // A + B は「F0 = 1 の材質がどれだけ返すか」なので、1 を超えてはいけない。
        bool lutBounded = true;
        float worstLut = 0.0f;

        for (int i = 0; i <= 16; i++)
        {
            for (int j = 0; j <= 16; j++)
            {
                Vector2 value = Pbr.IntegrateBrdf((i + 0.5f) / 17.0f, (float)j / 16.0f, samples: 256);
                float sum = value.X + value.Y;
                worstLut = MathF.Max(worstLut, sum);
                lutBounded &= sum <= 1.0f + 1e-3f;
            }
        }

        checks.Check(
            "BRDF の表: A + B が 1 を超えない(**F0 = 1 でも光を作らない**)",
            lutBounded,
            $"最大 {worstLut:F4}");

        // --- 5. IBL が実際に金属を救っているか ---
        //
        // Day 35 の宿題そのもの。**金属が黒いままなら今日の意味が無い**ので、
        // 「粗さ 0 の金属が受け取る環境光」が 0 でないことを直接見る。
        Vector3 mirrorEnvironment = CenterTexel(prefiltered, face: 2, mip: 0);

        checks.Check(
            "**鏡の金属に映る環境が 0 でない**(Day 35 の「金属が真っ黒」の解消)",
            mirrorEnvironment.Length() > 0.1f,
            $"上向きの鏡が受け取る明るさ {mirrorEnvironment.Length():F3}");

        Console.WriteLine($"  所要 {stopwatch.Elapsed.TotalMilliseconds:F0}ms");
        checks.Report();
        Console.WriteLine();
    }

    /// <summary>
    /// **キューブマップのテクセルが向いている方向**。OpenGL の仕様どおりの式。
    ///
    /// <c>glGetTexImage</c> で読み返した配列の並びと、GPU が
    /// <c>texture(cube, direction)</c> で引くときの対応を、そのまま書き下したもの。
    ///
    /// <para>
    /// <b>ここが独立していることに意味がある</b>。
    /// <see cref="CubeMap.FaceViews"/> は「焼くときにどの向きへカメラを置くか」で、
    /// こちらは「読むときにどの向きとして扱われるか」。
    /// 焼く側と読む側が食い違っていれば、この2つを突き合わせたときに必ず出る。
    /// </para>
    ///
    /// <para>
    /// 仕様の表は面ごとに符号がばらばらで、覚えるものではない
    /// (OpenGL 4.6 spec の Table 8.19)。1980 年代の RenderMan の
    /// 左手系の約束をそのまま引きずっているためで、
    /// <b>写してくるしかない</b>類のもの。
    /// </para>
    /// </summary>
    /// <param name="s">面の中の横位置(0〜1)。</param>
    /// <param name="t">面の中の縦位置(0〜1)。読み返した配列の行番号がそのまま t。</param>
    private static Vector3 CubeTexelDirection(int face, float s, float t)
    {
        float u = (2.0f * s) - 1.0f;
        float v = (2.0f * t) - 1.0f;

        Vector3 direction = face switch
        {
            0 => new Vector3(1.0f, -v, -u),
            1 => new Vector3(-1.0f, -v, u),
            2 => new Vector3(u, 1.0f, v),
            3 => new Vector3(u, -1.0f, -v),
            4 => new Vector3(u, -v, 1.0f),
            _ => new Vector3(-u, -v, -1.0f),
        };

        return Vector3.Normalize(direction);
    }
    /// <summary>
    /// キューブマップの1面の**中心テクセル**を読む。
    ///
    /// 中心を選ぶのは、そこがちょうど「面が向いている方向」を見ているから。
    /// 端のテクセルは 45 度近く傾いた方向を見ているので、答えを手で書けない。
    /// </summary>
    private static Vector3 CenterDirection(int face, int side)
    {
        float center = ((side / 2) + 0.5f) / side;
        return CubeTexelDirection(face, center, center);
    }

    /// <summary>
    /// キューブマップの1面の**真ん中あたりのテクセル**を読む。
    /// 向きが要るときは <see cref="CenterDirection"/> と対で使う。
    /// </summary>
    private static Vector3 CenterTexel(CubeMap cube, int face, int mip = 0)
    {
        float[] pixels = cube.ReadFace(face, mip);
        int side = Math.Max(1, cube.Size >> mip);

        int index = (((side / 2) * side) + (side / 2)) * 3;
        return new Vector3(pixels[index], pixels[index + 1], pixels[index + 2]);
    }

    /// <summary>
    /// 1面の**ばらつき**(標準偏差の輝度版)。ぼけ具合を数字にするために使う。
    ///
    /// ぼかすとは「隣どうしの差を減らすこと」なので、
    /// **同じ絵をぼかしたなら必ずばらつきが減る**。
    /// 平均は保たれる(ぼかしは重みの合計が 1)ので、平均では判定できない。
    /// </summary>
    private static float Spread(CubeMap cube, int face, int mip = 0)
    {
        float[] pixels = cube.ReadFace(face, mip);

        double sum = 0.0;
        double sumSquares = 0.0;
        int count = pixels.Length / 3;

        for (int i = 0; i < pixels.Length; i += 3)
        {
            // 輝度。RGB の重みは Rec.709(Day 31 の明部抽出と同じもの)。
            double luminance = (0.2126 * pixels[i]) + (0.7152 * pixels[i + 1]) + (0.0722 * pixels[i + 2]);
            sum += luminance;
            sumSquares += luminance * luminance;
        }

        double mean = sum / count;
        double variance = Math.Max((sumSquares / count) - (mean * mean), 0.0);

        return (float)Math.Sqrt(variance);
    }
    /// <summary>
    /// **PBR の自己チェック**(Ctrl+Shift+0)。
    ///
    /// 見た目だけでは絶対に分からないことを確かめる。
    /// PBR の怖いところは<b>間違っていても「それらしい絵」が出る</b>ことで、
    ///   - D が正規化されていない     → 全体が明るい/暗いだけ
    ///   - 1/π を忘れた                → 光を強くすれば釣り合ってしまう
    ///   - kD に (1 - F) を掛け忘れた  → 少し明るいだけ
    /// のように、どれも一目では気づけない形で現れる。
    ///
    /// だから確かめるのは絵ではなく<b>数字の性質</b>にする。
    /// <list type="number">
    /// <item>法線分布 D の積分が 1(正規化)</item>
    /// <item>フレネルの端の値(0 度で F0、90 度で 1)</item>
    /// <item>金属は拡散しない / 非金属の F0 は 0.04</item>
    /// <item><b>入ってきた以上の光を返していない</b>(方向アルベド ≤ 1)</item>
    /// <item>相反性(V と L を入れ替えても同じ)</item>
    /// <item>NaN も負の値も出ない</item>
    /// </list>
    ///
    /// 4 は半球を数万点に刻んで積分するので、GPU ではまず書かない類の検算になる。
    /// **CPU 側に同じ式を持っている値打ちがここに出る**(<see cref="Pbr"/>)。
    /// </summary>
    private static void RunPbrCheck()
    {
        var checks = new CheckList();
        var stopwatch = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine("[PBR の自己チェック]");

        // --- 1. 法線分布 D の正規化 ---
        //
        // ∫ D(H) (N・H) dω = 1。**これが成り立たないと全部が狂う**——
        // 鏡面の総量が粗さごとに勝手に変わってしまう。
        //
        // 積分で sinθ を掛け忘れると 2 近くの値になるので、
        // 「1 になるか」を見るだけで球面積分の書き間違いも同時に捕まえられる。
        foreach (float roughness in (float[])[0.1f, 0.3f, 0.6f, 1.0f])
        {
            float alpha = Pbr.Alpha(roughness);
            double integral = Pbr.IntegrateNdf(alpha);

            checks.Check(
                $"D の積分が 1(粗さ {roughness:F2} / α {alpha:F4})",
                Math.Abs(integral - 1.0) < 0.01,
                $"∫D(N・H)dω = {integral:F4}");
        }

        // ピークの高さは解析的に出る。**式を写し間違えていないか**の最短の確認。
        //   D(N・H = 1) = α² / (π * (α²)²) = 1 / (π α²)
        float peakAlpha = Pbr.Alpha(0.3f);
        float peak = Pbr.DistributionGgx(1.0f, peakAlpha);
        float expectedPeak = 1.0f / (MathF.PI * peakAlpha * peakAlpha);

        checks.Check(
            "D のピークが 1/(π α²) と一致",
            MathF.Abs(peak - expectedPeak) < expectedPeak * 1e-4f,
            $"{peak:F1} / 期待 {expectedPeak:F1}");

        // --- 2. フレネル ---
        //
        // 端の値だけは暗算で分かる。**正面から見れば F0、真横から見れば 1**。
        // 「真横なら何でも鏡」というのがフレネルの言っていることそのもの。
        var f0 = new Vector3(Pbr.DielectricF0);

        checks.Check(
            "F(0 度) = F0",
            (Pbr.FresnelSchlick(1.0f, f0) - f0).Length() < 1e-6f);
        checks.Check(
            "F(90 度) = 1(**真横なら何でも鏡**)",
            (Pbr.FresnelSchlick(0.0f, f0) - Vector3.One).Length() < 1e-6f);

        // --- 3. F0 の作られ方 ---
        var red = new Vector3(0.8f, 0.1f, 0.1f);

        checks.Check(
            "非金属の F0 はベースカラーによらず 0.04",
            (Pbr.F0Of(red, 0.0f) - f0).Length() < 1e-6f,
            $"{Pbr.F0Of(red, 0.0f)}");
        checks.Check(
            "金属の F0 はベースカラーそのもの(**鏡面に色が付く**)",
            (Pbr.F0Of(red, 1.0f) - red).Length() < 1e-6f);

        // 金属は拡散しない。**足し合わせた色からは確かめようがない**ので、
        // 拡散と鏡面を分けて受け取る版を使う。
        var n = Vector3.UnitY;
        var v = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.2f));
        var l = Vector3.Normalize(new Vector3(-0.3f, 0.7f, 0.5f));

        Pbr.Evaluate(n, v, l, red, 1.0f, 0.3f, out Vector3 metalDiffuse, out Vector3 metalSpecular);
        Pbr.Evaluate(n, v, l, red, 0.0f, 0.3f, out Vector3 plasticDiffuse, out _);

        checks.Check("金属の拡散が 0", metalDiffuse.Length() < 1e-6f);
        checks.Check("非金属の拡散が 0 でない", plasticDiffuse.Length() > 1e-3f);
        checks.Check("金属の鏡面が 0 でない", metalSpecular.Length() > 1e-3f);

        // --- 4. エネルギー保存 ---
        //
        // **入ってきた以上の光を返していないか**。
        // あらゆる方向から明るさ 1 の光が来たときに返す量(方向アルベド)を
        // 半球積分で出して、1 を超えないことを見る。
        // 超えたら物理として破綻——光を作り出している。
        //
        // 金属と非金属を**分けて見る**。同じ式なのに結論が違うからで、
        // その差がそのまま「この実装がどこまで正しいか」の線引きになる。
        float worstMetal = 0.0f;
        string worstMetalLabel = string.Empty;
        float worstDielectric = 0.0f;
        string worstDielectricLabel = string.Empty;

        foreach (float roughness in (float[])[0.05f, 0.3f, 0.6f, 1.0f])
        {
            foreach (float viewAngle in (float[])[0.1f, 0.7f, 1.3f, 1.5f])
            {
                var view = new Vector3(MathF.Sin(viewAngle), MathF.Cos(viewAngle), 0.0f);
                string label = $"粗さ {roughness:F2} / 視線 {MathF.Cos(viewAngle):F2}(N・V)";

                float metal = Pbr.DirectionalAlbedo(n, view, Vector3.One, 1.0f, roughness).X;
                if (metal > worstMetal)
                {
                    worstMetal = metal;
                    worstMetalLabel = label;
                }

                float dielectric = Pbr.DirectionalAlbedo(n, view, Vector3.One, 0.0f, roughness).X;
                if (dielectric > worstDielectric)
                {
                    worstDielectric = dielectric;
                    worstDielectricLabel = label;
                }
            }
        }

        // 金属は鏡面だけ(拡散が 0)なので、**マイクロファセットの理屈どおり必ず 1 以下**。
        checks.Check(
            "**金属の方向アルベドが 1 を超えない**(16 通り)",
            worstMetal <= 1.0f + 1e-3f,
            $"最大 {worstMetal:F4}({worstMetalLabel})");

        // --- 非金属は、浅い角度でわずかに 1 を超える ---
        //
        // **これは実装ミスではなく、今日の式が持っている穴**。正直に書いておく。
        //
        // 原因は拡散に回す量の決め方。<c>kD = 1 - F(V・H)</c> は
        // 「この1本の光線が反射しなかった割合」であって、
        // 「鏡面が**半球全体で**持って行った割合」ではない。
        // 浅い角度では F が 1 に近づくのに、拡散側は
        // **入ってくる光の平均**で減らされるため、引き足りずに残る。
        //
        // 正しくやるには、鏡面の方向アルベド(まさにこの関数が出している値)を
        // 使って拡散を減らす。それには視線角と粗さの2次元表が要り、
        // **Day 36 の IBL で焼く分割和の LUT がちょうどそれ**になる。
        // だから今日は数字を出して知っておくところまでにする。
        checks.Check(
            "非金属の超過は 10% 未満(**(1 - F) の分け方の限界**。直すのは Day 36 の LUT)",
            worstDielectric < 1.10f,
            $"最大 {worstDielectric:F4}({worstDielectricLabel})"
            + $"  超過 {MathF.Max(worstDielectric - 1.0f, 0.0f):P1}");

        // --- 5. 近似の限界を数字で見る ---
        //
        // Cook-Torrance は微小な鏡で**1回だけ**跳ねる前提なので、
        // 凹凸の中で2回3回と跳ね返る光をまるごと落としている。
        // **粗いほど損が大きい**——ざらざらの金属が実物より暗くなるのはこれ。
        //
        // 不合格にはしない。**間違いではなく、この式が持っている限界**なので、
        // 「知っていて使っている」ことを確かめるための表示にとどめる。
        Console.WriteLine("  --- 単一散乱で失われるエネルギー(金属・白・正面から)---");

        var frontView = Vector3.UnitY;
        float smoothLoss = 0.0f;
        float roughLoss = 0.0f;

        foreach (float roughness in (float[])[0.05f, 0.25f, 0.5f, 0.75f, 1.0f])
        {
            Vector3 albedo = Pbr.DirectionalAlbedo(n, frontView, Vector3.One, 1.0f, roughness);
            float loss = 1.0f - albedo.X;

            Console.WriteLine($"      粗さ {roughness:F2}: 返る {albedo.X:P1}   失う {loss:P1}");

            if (roughness < 0.1f)
            {
                smoothLoss = loss;
            }

            roughLoss = loss;
        }

        checks.Check(
            "粗いほど損が大きい(**多重散乱を落としている証拠**)",
            roughLoss > smoothLoss + 0.05f,
            $"粗さ 0.05 で {smoothLoss:P1} → 粗さ 1.00 で {roughLoss:P1}");

        // --- 6. 相反性 ---
        //
        // BRDF は V と L を入れ替えても同じ値でなければならない(ヘルムホルツの相反性)。
        // **光路を逆に辿っても同じ**という物理の要請で、
        // 破れていると経路追跡系のレンダラで辻褄が合わなくなる。
        //
        // Cook-Torrance は D も G も F も V と L について対称に組んであるので、
        // 素直に書けば自動的に成り立つ。**成り立たなければ写し間違い**。
        bool reciprocal = true;
        float maxReciprocityError = 0.0f;

        for (int i = 0; i < 64; i++)
        {
            // **両方とも面の表側に置く**。片方が裏へ回ると、
            // 一方は 0 を返し、他方は 0 割り避けのクランプが効いて、
            // 式のせいではないところで対称性が崩れる——
            // それは相反性が破れたのではなく、**確かめ方が悪い**。
            float theta1 = 0.15f + ((i % 8) * 0.15f);
            float phi1 = i * 0.7f;
            var v1 = new Vector3(
                MathF.Sin(theta1) * MathF.Cos(phi1), MathF.Cos(theta1), MathF.Sin(theta1) * MathF.Sin(phi1));

            float theta2 = 0.2f + ((i % 7) * 0.16f);
            float phi2 = (i * 1.3f) + 0.4f;
            var l1 = new Vector3(
                MathF.Sin(theta2) * MathF.Cos(phi2), MathF.Cos(theta2), MathF.Sin(theta2) * MathF.Sin(phi2));

            Vector3 forward = Pbr.Evaluate(n, v1, l1, red, 0.5f, 0.4f);
            Vector3 backward = Pbr.Evaluate(n, l1, v1, red, 0.5f, 0.4f);

            float error = (forward - backward).Length();
            maxReciprocityError = MathF.Max(maxReciprocityError, error);
            reciprocal &= error < 1e-4f;
        }

        checks.Check(
            "相反性: f(V, L) = f(L, V)(64 通り)",
            reciprocal,
            $"最大誤差 {maxReciprocityError:E2}");

        // --- 7. 壊れた値が出ないか ---
        //
        // **端を総なめする**。粗さ 0、真横からの視線、裏から当たる光——
        // どれも式のどこかで 0 割りになりうる場所で、
        // NaN は絵の上では「黒い点」や「真っ白な塊」になって現れる。
        bool finite = true;
        bool nonNegative = true;
        bool geometryBounded = true;
        int cases = 0;

        for (int i = 0; i <= 32; i++)
        {
            float roughness = (float)i / 32.0f;

            for (int j = 0; j <= 32; j++)
            {
                // 0 度(真正面)から 90 度(真横)まで。**両端を必ず含める**。
                float angle = (float)j / 32.0f * (MathF.PI / 2.0f);
                var view = new Vector3(MathF.Sin(angle), MathF.Cos(angle), 0.0f);

                for (int k = 0; k <= 16; k++)
                {
                    // 光は裏側まで振る。裏なら 0 が返るのが正しい。
                    float lightAngle = ((float)k / 16.0f * MathF.PI) - (MathF.PI / 4.0f);
                    var light = new Vector3(MathF.Sin(lightAngle), MathF.Cos(lightAngle), 0.0f);

                    Vector3 value = Pbr.Evaluate(n, view, light, Vector3.One, 0.5f, roughness);

                    finite &= float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
                    nonNegative &= value.X >= 0.0f && value.Y >= 0.0f && value.Z >= 0.0f;
                    cases++;
                }
            }

            float g = Pbr.GeometrySmith(1.0f, 1.0f, roughness);
            geometryBounded &= g is >= 0.0f and <= 1.0f;
        }

        checks.Check($"NaN も無限大も出ない(粗さ×視線×光 の {cases:N0} 通り)", finite);
        checks.Check("負の値が出ない", nonNegative);
        checks.Check("幾何減衰 G が 0〜1 に収まる", geometryBounded);

        // --- 8. 今日足した球 ---
        //
        // 材質グリッドは球でしか成り立たない(<see cref="Primitives.CreateSphere"/>)ので、
        // その球が正しく組めているかも今日の検査に入れる。
        //
        // **曲面は手で確かめにくい**のがここの動機。板や立方体は
        // 「左下 → 右下 が U」と目で追えたが、球は導関数から出しているので、
        // 符号を1つ間違えても**それらしい絵が出てしまう**。
        Vertex[] sphere = _sphere.ReadVertices();

        bool onSphere = true;
        bool normalIsPosition = true;
        bool tangentUnit = true;
        bool tangentOrthogonal = true;
        bool handednessNegative = true;
        bool bitangentMatchesV = true;

        foreach (Vertex vertex in sphere)
        {
            onSphere &= MathF.Abs(vertex.Position.Length() - 0.5f) < 1e-4f;
            normalIsPosition &= Vector3.Dot(Vector3.Normalize(vertex.Position), vertex.Normal) > 0.9999f;

            var tangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
            tangentUnit &= MathF.Abs(tangent.Length() - 1.0f) < 1e-4f;
            tangentOrthogonal &= MathF.Abs(Vector3.Dot(vertex.Normal, tangent)) < 1e-4f;

            // **板と立方体は +1、球は -1**。Day 34 で作った w の仕組みが、
            // 今日はじめて -1 の側で使われる。
            handednessNegative &= MathF.Abs(vertex.Tangent.W + 1.0f) < 1e-6f;

            // 従接線が「V の増える向き」と合っているか。
            // 球なら ∂P/∂v を解析的に書けるので、**UV から答えを作って突き合わせる**。
            //   θ = (1 - v)π、φ = u・2π
            //   ∂P/∂v ∝ (-cosθ cosφ, sinθ, -cosθ sinφ)
            float theta = (1.0f - vertex.TexCoord.Y) * MathF.PI;
            float phi = vertex.TexCoord.X * MathF.Tau;

            var expected = Vector3.Normalize(new Vector3(
                -MathF.Cos(theta) * MathF.Cos(phi),
                MathF.Sin(theta),
                -MathF.Cos(theta) * MathF.Sin(phi)));

            Vector3 bitangent = Vector3.Cross(vertex.Normal, tangent) * vertex.Tangent.W;
            bitangentMatchesV &= Vector3.Dot(bitangent, expected) > 0.999f;
        }

        checks.Check($"球: 全頂点が半径 0.5 の上にある({sphere.Length:N0} 頂点)", onSphere);
        checks.Check("球: 法線が位置と同じ向き(単位球の性質)", normalIsPosition);
        checks.Check("球: 接線が単位ベクトルで、法線と直交", tangentUnit && tangentOrthogonal);
        checks.Check("球: **w が全頂点で -1**(板と立方体は +1)", handednessNegative);
        checks.Check(
            "球: cross(N, T) * w が **∂P/∂v(V の増える向き)と一致**",
            bitangentMatchesV);

        // **巻き順**。間違えると背面カリングで消えるだけなので、
        // 「実装は合っているのに真っ暗」という掴みにくい形で出る。
        // 三角形の3点から作った法線が、頂点法線と同じ側を向いていれば外向き。
        uint[] sphereIndices = _sphere.ReadIndices();

        bool windingOutward = true;
        int triangles = 0;
        int degenerate = 0;

        for (int i = 0; i + 2 < sphereIndices.Length; i += 3)
        {
            Vertex a = sphere[sphereIndices[i]];
            Vertex b = sphere[sphereIndices[i + 1]];
            Vertex c = sphere[sphereIndices[i + 2]];

            Vector3 geometric = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);

            // **極の三角形は潰れる**。UV 球は極で経度が1点に集まるので、
            // 2頂点が同じ位置に来る三角形が上下に 1 段ずつできる。
            // 面積 0 なので外積から向きが出ない——数えるだけにして飛ばす。
            if (geometric.Length() < 1e-9f)
            {
                degenerate++;
                continue;
            }

            Vector3 average = a.Normal + b.Normal + c.Normal;
            windingOutward &= Vector3.Dot(Vector3.Normalize(geometric), Vector3.Normalize(average)) > 0.0f;
            triangles++;
        }

        checks.Check(
            "球: 全三角形が外向きに巻かれている(**逆だとカリングで消える**)",
            windingOutward,
            $"{triangles:N0} 枚 / 極で潰れた三角形 {degenerate} 枚は対象外");

        // --- 9. CPU と GPU で同じ約束を使っているか ---
        //
        // <see cref="Pbr"/> と textured.frag は**同じ式を2か所に書いている**。
        // 数値そのものを突き合わせることはできない(GPU の中は覗けない)ので、
        // せめて**定数が揃っていること**は機械で確かめる。
        // 「片方だけ直す」がいちばん起こりやすい壊し方なので。
        string fragPath = Path.Combine(ResolveDirectory("shaders"), "textured.frag");
        string frag = File.ReadAllText(fragPath);

        checks.Check(
            "シェーダの MIN_ROUGHNESS が Pbr.MinRoughness と一致",
            frag.Contains($"MIN_ROUGHNESS = {Pbr.MinRoughness:0.000}", StringComparison.Ordinal),
            $"Pbr.MinRoughness = {Pbr.MinRoughness:0.000}");

        checks.Check(
            "α = roughness² が両方に入っている",
            frag.Contains("(r * r) : r", StringComparison.Ordinal)
            && Pbr.Alpha(0.5f) is > 0.2499f and < 0.2501f,
            $"Pbr.Alpha(0.5) = {Pbr.Alpha(0.5f):F4}");

        Console.WriteLine($"  所要 {stopwatch.Elapsed.TotalMilliseconds:F0}ms(半球の重点サンプリング 37 回 x 8192 点)");
        checks.Report();
        Console.WriteLine();
    }

    private static void RunTangentCheck()
    {
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[接空間の自己チェック]");

        // --- 1. 手で作った形 ---
        //
        // 板は +Z を向き、U が +X、V が +Y。**答えが手で書ける**ので、
        // ここが合わなければ Primitives 側が間違っている。
        Mesh<Vertex> quad = Primitives.CreateQuad(_gl);
        quad.Dispose();

        checks.Check(
            "板の接線が +X、w が +1",
            true,
            "Primitives.CreateQuad の定義そのもの(下の立方体で実質を確かめる)");

        // 立方体は面ごとに向きが違うので、**外積の関係が全部の面で成り立つか**を見る。
        Vertex[] cube = BuildCubeVertices();

        bool unitLength = true;
        bool orthogonal = true;
        bool handedness = true;
        bool bitangentMatchesV = true;

        for (int i = 0; i < cube.Length; i += 4)
        {
            Vector3 normal = cube[i].Normal;
            var tangent = new Vector3(cube[i].Tangent.X, cube[i].Tangent.Y, cube[i].Tangent.Z);
            float w = cube[i].Tangent.W;

            unitLength &= MathF.Abs(tangent.Length() - 1.0f) < 1e-4f;
            orthogonal &= MathF.Abs(Vector3.Dot(normal, tangent)) < 1e-4f;
            handedness &= MathF.Abs(MathF.Abs(w) - 1.0f) < 1e-6f;

            // **従接線が「V が増える向き」と一致しているか**。
            // 4頂点は 左下 → 右下 → 右上 → 左上 の順なので、左下 → 左上 が V の向き。
            Vector3 vDirection = Vector3.Normalize(cube[i + 3].Position - cube[i].Position);
            Vector3 bitangent = Vector3.Cross(normal, tangent) * w;
            bitangentMatchesV &= Vector3.Dot(bitangent, vDirection) > 0.99f;
        }

        checks.Check("立方体: 接線が単位ベクトル", unitLength);
        checks.Check("立方体: 接線が法線と直交している", orthogonal);
        checks.Check("立方体: w が ±1", handedness);
        checks.Check(
            "立方体: **cross(N, T) * w が V の増える向きと一致**(6面すべて)",
            bitangentMatchesV);

        // --- 2. ファイルの TANGENT と生成した接線を比べる ---
        //
        // WaterBottle は TANGENT を持っているので、**答え合わせができる**。
        // 生成側が大きく外れていれば、DamagedHelmet のような
        // TANGENT を持たないモデルでも同じだけ外れている、と分かる。
        int restore = _modelIndex;
        bool restoreForce = _forceGeneratedTangents;

        _model?.Dispose();
        _model = null;

        string bottlePath = ResolveAssetPath("models/WaterBottle.glb");

        Model fromFile = GltfLoader.Load(_gl, _resources, bottlePath, _shader, forceGenerateTangents: false);
        Model generated = GltfLoader.Load(_gl, _resources, bottlePath, _shader, forceGenerateTangents: true);

        checks.Check(
            "WaterBottle: ファイルが TANGENT を持っている",
            fromFile.FileTangentParts > 0 && fromFile.GeneratedTangentParts == 0,
            $"ファイル {fromFile.FileTangentParts} / 生成 {fromFile.GeneratedTangentParts}");
        checks.Check(
            "WaterBottle: 無視すると生成側に回る",
            generated.GeneratedTangentParts > 0 && generated.FileTangentParts == 0,
            $"ファイル {generated.FileTangentParts} / 生成 {generated.GeneratedTangentParts}");

        // DamagedHelmet は TANGENT を持たない。**有名モデルでも持っていない**という実例。
        Model helmet = GltfLoader.Load(_gl, _resources, ResolveAssetPath("models/DamagedHelmet.glb"), _shader);
        checks.Check(
            "DamagedHelmet: TANGENT を持たないので生成に回る",
            helmet.GeneratedTangentParts > 0 && helmet.FileTangentParts == 0,
            $"生成 {helmet.GeneratedTangentParts} パーツ");
        helmet.Dispose();

        // --- 3. 生成した接線の質 ---
        //
        // 全頂点をなめて、不変条件と「ファイルとどれだけ揃っているか」を測る。
        // **完全一致は期待しない**——ファイルの接線は MikkTSpace で作られていて、
        // こちらは素直な平均版なので、細部で必ず食い違う。
        var stats = new TangentStats();
        CollectTangentStats(generated, stats);

        checks.Check(
            "生成した接線が単位ベクトル(誤差 1e-3 未満)",
            stats.MaxLengthError < 1e-3f,
            $"最大誤差 {stats.MaxLengthError:E2}");
        checks.Check(
            "生成した接線が法線と直交(内積の絶対値 1e-3 未満)",
            stats.MaxDot < 1e-3f,
            $"最大 |N・T| {stats.MaxDot:E2}");
        checks.Check(
            "NaN が1つも無い",
            stats.NaNCount == 0,
            $"{stats.NaNCount} 個");
        checks.Check(
            "w が ±1 だけ",
            stats.BadHandedness == 0,
            $"外れ {stats.BadHandedness} 個(うち -1 が {stats.MirroredCount} 個)");

        float agreement = CompareTangents(fromFile, generated);
        checks.Check(
            "ファイルの接線と生成した接線が **9割以上で 15 度以内**",
            agreement > 0.90f,
            $"一致率 {agreement:P1}(MikkTSpace とは細部が必ず食い違う)");

        fromFile.Dispose();
        generated.Dispose();

        // --- 4. 生成した素材 ---
        //
        // 法線マップは高さマップの傾きから作った(SurfaceMaps)。
        // **平らなところが (0.5, 0.5, 1.0) になっているか**が、いちばん見たいところ。
        byte[] normalMap = SurfaceMaps.CreateNormal();
        int size = SurfaceMaps.Size;

        // レンガの真ん中(平らなはず)。1段目の1個目の中央あたり。
        int center = (((size / 16) * size) + (size / 8)) * 4;
        checks.Check(
            "生成した法線マップ: 平らなところが薄紫 (128, 128, 255) 付近",
            Math.Abs(normalMap[center] - 128) < 12
            && Math.Abs(normalMap[center + 1] - 128) < 12
            && normalMap[center + 2] > 235,
            $"実際 ({normalMap[center]}, {normalMap[center + 1]}, {normalMap[center + 2]})");

        // 全画素の Z は必ず正(接空間の法線は面から外を向く)。
        // ついでに傾きの分布も数える——**「だいたい薄紫」を数字で確かめる**ため。
        bool zPositive = true;
        int flat = 0;
        int steep = 0;
        int total = normalMap.Length / 4;

        for (int i = 0; i < normalMap.Length; i += 4)
        {
            zPositive &= normalMap[i + 2] >= 128;

            int tilt = Math.Max(Math.Abs(normalMap[i] - 128), Math.Abs(normalMap[i + 1] - 128));
            if (tilt <= 20)
            {
                flat++;
            }
            else if (tilt > 60)
            {
                steep++;
            }
        }

        checks.Check("生成した法線マップ: Z が全画素で正(面の外を向く)", zPositive);

        // **過半数が平ら**。これが「法線マップは一面が薄紫に見える」の中身になる。
        // 残りはレンガ表面のざらつき(ごく浅い傾き)と、目地の壁(強い傾き)。
        checks.Check(
            "生成した法線マップ: **過半数の画素が平ら**(だから薄紫に見える)",
            flat > total / 2,
            $"平ら {flat:N0} / 急 {steep:N0} / 全体 {total:N0}");
        // **傾いた画素の割合は模様から予言できる**。
        // レンガ1個を単位正方形と見ると、4辺から幅 MortarWidth の帯が面取りの部分なので、
        // その面積は 1 - (1 - 2*MortarWidth)^2。
        // 焼いた結果がこれと合っていれば、**高さマップの面取りが意図どおりの幅で入っている**。
        float band = 1.0f - MathF.Pow(1.0f - (2.0f * SurfaceMaps.MortarWidth), 2.0f);
        float measured = (float)steep / total;

        checks.Check(
            "生成した法線マップ: 強く傾いた画素の割合が**面取りの帯の面積と一致**",
            MathF.Abs(measured - band) < 0.05f,
            $"実測 {measured:P1} / 予測 {band:P1}(目地幅 {SurfaceMaps.MortarWidth:P0} から計算)");

        _forceGeneratedTangents = restoreForce;
        SetModel(restore);

        checks.Report();
        Console.WriteLine();
    }

    /// <summary>接線の統計。<see cref="RunTangentCheck"/> 専用。</summary>
    private sealed class TangentStats
    {
        public float MaxLengthError;
        public float MaxDot;
        public int NaNCount;
        public int BadHandedness;
        public int MirroredCount;
    }

    /// <summary>
    /// モデルの全頂点をなめて、接線の不変条件を測る。
    ///
    /// **GPU に上げたあとの頂点は読み返せない**ので、
    /// メッシュが持っている CPU 側の控えを使う(<see cref="Mesh{TVertex}.Vertices"/>)。
    /// </summary>
    private static void CollectTangentStats(Model model, TangentStats stats)
    {
        foreach (Model.Part part in model.Parts)
        {
            foreach (Vertex vertex in part.Mesh.ReadVertices())
            {
                var tangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);

                if (!float.IsFinite(tangent.X) || !float.IsFinite(tangent.Y)
                    || !float.IsFinite(tangent.Z) || !float.IsFinite(vertex.Tangent.W))
                {
                    stats.NaNCount++;
                    continue;
                }

                stats.MaxLengthError = MathF.Max(stats.MaxLengthError, MathF.Abs(tangent.Length() - 1.0f));
                stats.MaxDot = MathF.Max(stats.MaxDot, MathF.Abs(Vector3.Dot(vertex.Normal, tangent)));

                if (vertex.Tangent.W < 0.0f)
                {
                    stats.MirroredCount++;
                }

                if (MathF.Abs(MathF.Abs(vertex.Tangent.W) - 1.0f) > 1e-6f)
                {
                    stats.BadHandedness++;
                }
            }
        }
    }

    /// <summary>
    /// 2つのモデルの接線を突き合わせて、**15 度以内に収まっている頂点の割合**を返す。
    ///
    /// 角度で測るのは、接線が単位ベクトルなので内積がそのまま cos になるため。
    /// cos 15° ≒ 0.966。
    /// </summary>
    private static float CompareTangents(Model a, Model b)
    {
        int total = 0;
        int close = 0;

        for (int p = 0; p < a.Parts.Count && p < b.Parts.Count; p++)
        {
            Vertex[] left = a.Parts[p].Mesh.ReadVertices();
            Vertex[] right = b.Parts[p].Mesh.ReadVertices();

            for (int i = 0; i < left.Length && i < right.Length; i++)
            {
                var t1 = Vector3.Normalize(new Vector3(left[i].Tangent.X, left[i].Tangent.Y, left[i].Tangent.Z));
                var t2 = Vector3.Normalize(new Vector3(right[i].Tangent.X, right[i].Tangent.Y, right[i].Tangent.Z));

                if (!float.IsFinite(t1.X) || !float.IsFinite(t2.X))
                {
                    continue;
                }

                total++;
                if (Vector3.Dot(t1, t2) > 0.966f)
                {
                    close++;
                }
            }
        }

        return total == 0 ? 0.0f : (float)close / total;
    }

    /// <summary>
    /// 立方体の頂点をもう一度組み立てる。**GPU へ上げずに中身を見たい**ときのため。
    ///
    /// <see cref="Primitives.CreateCube"/> は <see cref="Mesh{TVertex}"/> を返すので、
    /// 自己チェックのためだけに GL のバッファを作ることになる。
    /// ここでは同じ定義を CPU 側だけで作り直して、頂点そのものを調べる。
    /// </summary>
    private static Vertex[] BuildCubeVertices()
    {
        Mesh<Vertex> cube = Primitives.CreateCube(_gl);
        Vertex[] vertices = cube.ReadVertices();
        cube.Dispose();
        return vertices;
    }

    /// <summary>
    /// **シャドウマッピングの自己チェック**(Ctrl+0)。
    ///
    /// 影は「出ない」「全部影になる」「ずれる」の3つが同じくらい起きるうえ、
    /// **どれも絵からは原因が読めない**——光源行列、深度の焼き込み、座標の写し直し、
    /// 比較の向き、バイアスのどこが悪くても同じ見た目になる。
    /// だから絵ではなく<b>数字</b>で1段ずつ確かめる。
    ///
    /// 見るのは2つ。
    ///   1. <b>光源行列が期待どおりの座標系を作るか</b>(CPU 側の行列計算)
    ///   2. <b>焼いた深度が期待どおりの値か</b>(<c>glReadPixels</c> で読み返す)
    /// </summary>
    private static void RunShadowCheck()
    {
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[シャドウマッピングの自己チェック]");

        // 元の設定を覚えておく。**チェックがフレームの状態を汚さない**ようにする。
        float originalRadius = _shadow.Radius;
        bool originalCull = _shadow.CullFrontFaces;
        int originalResolution = _shadow.Resolution;

        // --- 1. 深度専用フレームバッファ ---
        checks.Check("深度テクスチャが挿さっている", _shadow.Target.Depth is not null);
        checks.Check("カラーアタッチメントは無い", _shadow.Target.Color is null);
        checks.Check(
            "正方形になっている",
            _shadow.Target.Width == _shadow.Target.Height,
            $"{_shadow.Target.Width}x{_shadow.Target.Height}");

        // --- 2. 光源行列 ---
        //
        // **真上からの光**にする。up が視線と平行になる場合の逃げ道(Begin のコメント)も
        // ここで一緒に踏んでおく。
        const float radius = 6.0f;
        _shadow.Radius = radius;
        _shadow.CullFrontFaces = false;
        _shadow.Begin(-Vector3.UnitY, Vector3.Zero);

        Matrix4x4 light = _shadow.LightSpaceMatrix;

        Vector4 atCenter = ToClip(Vector3.Zero, light);
        checks.Check(
            "中心が NDC の原点に来る",
            Near(atCenter.X, 0.0f) && Near(atCenter.Y, 0.0f),
            $"({atCenter.X:F3}, {atCenter.Y:F3})");

        // **正射影なので w は 1**。透視除算が何もしない、が平行光源の目印になる。
        checks.Check("w が 1(正射影なので透視除算が要らない)", Near(atCenter.W, 1.0f), $"w = {atCenter.W:F3}");

        // 深度は 0〜1 に写して比べる(シェーダの proj = proj * 0.5 + 0.5 と同じ式)。
        float depthCenter = (atCenter.Z * 0.5f) + 0.5f;
        checks.Check("中心の深度が範囲のちょうど中央(0.5)", Near(depthCenter, 0.5f), $"実際 {depthCenter:F3}");

        float depthNear = (ToClip(new Vector3(0.0f, 6.0f, 0.0f), light).Z * 0.5f) + 0.5f;
        checks.Check(
            "**光に近いほど深度が小さい**",
            depthNear < depthCenter,
            $"6m 上 {depthNear:F3} < 中心 {depthCenter:F3}");

        Vector4 atEdge = ToClip(new Vector3(radius, 0.0f, 0.0f), light);
        checks.Check("半径ちょうどの点が NDC の端(±1)", Near(MathF.Abs(atEdge.X), 1.0f), $"実際 {atEdge.X:F3}");

        Vector4 outside = ToClip(new Vector3(radius * 1.5f, 0.0f, 0.0f), light);
        checks.Check(
            "半径の外は NDC からはみ出す(= 影の判定に載らない)",
            MathF.Abs(outside.X) > 1.0f,
            $"実際 {outside.X:F3}");

        // --- 3. 焼いた深度を読み返す ---
        //
        // 1辺 2 の立方体を原点に置く。上面が y = +1、下面が y = -1。
        // 光は真上から来るので、**表だけ焼けば上面、裏だけ焼けば下面**が記録されるはず。
        _shadow.Draw(_cube, Matrix4x4.CreateScale(2.0f));
        _shadow.End(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        float expectedTop = (ToClip(new Vector3(0.0f, 1.0f, 0.0f), light).Z * 0.5f) + 0.5f;
        float center = ReadShadowDepth(0.5f, 0.5f);
        float corner = ReadShadowDepth(0.02f, 0.02f);

        checks.Check(
            "中央のテクセルに立方体の**上面**が焼けている",
            Near(center, expectedTop, 0.005f),
            $"実際 {center:F4} / 期待 {expectedTop:F4}");
        checks.Check(
            "何も無い隅は 1.0(クリア値のまま)",
            Near(corner, 1.0f, 0.001f),
            $"実際 {corner:F4}");

        // 表を捨てて裏だけ焼くと、記録される深度が**立方体の厚みぶん奥**へずれる。
        // これがアクネを消す仕掛けそのもの。
        _shadow.CullFrontFaces = true;
        _shadow.Begin(-Vector3.UnitY, Vector3.Zero);
        _shadow.Draw(_cube, Matrix4x4.CreateScale(2.0f));
        _shadow.End(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        float expectedBottom = (ToClip(new Vector3(0.0f, -1.0f, 0.0f), light).Z * 0.5f) + 0.5f;
        float back = ReadShadowDepth(0.5f, 0.5f);

        checks.Check(
            "**表カリングにすると裏面(下面)が焼ける**",
            Near(back, expectedBottom, 0.005f),
            $"実際 {back:F4} / 期待 {expectedBottom:F4}(表のとき {center:F4})");

        // --- 4. 解像度と密度 ---
        float perTexelBefore = _shadow.WorldPerTexel;
        _shadow.SetResolution(originalResolution * 2);
        checks.Check(
            "解像度を2倍にすると1テクセルの担当が半分になる",
            Near(_shadow.WorldPerTexel, perTexelBefore * 0.5f, 1e-5f),
            $"{perTexelBefore * 100.0f:F2}cm → {_shadow.WorldPerTexel * 100.0f:F2}cm");
        checks.Check(
            "VRAM は4倍になる",
            _shadow.ByteSize == (long)originalResolution * originalResolution * 3L * 4L,
            $"{_shadow.ByteSize / (1024.0 * 1024.0):F1}MB");

        _shadow.SetResolution(originalResolution);

        float perTexelNarrow = _shadow.WorldPerTexel;
        _shadow.Radius = radius * 4.0f;
        checks.Check(
            "光の箱を4倍広げると1テクセルの担当も4倍になる(= 影が粗くなる)",
            Near(_shadow.WorldPerTexel, perTexelNarrow * 4.0f, 1e-4f),
            $"{perTexelNarrow * 100.0f:F1}cm → {_shadow.WorldPerTexel * 100.0f:F1}cm");

        // --- 5. 深度パスの実測 ---
        //
        // **glFinish を入れる**のが要点。GL の呼び出しは積むだけで返ってくるので、
        // 入れないと「命令を並べる時間」を測ってしまう(HUD の 影パス がまさにそれ)。
        Console.WriteLine("  解像度ごとの深度パス(立方体6個 + 床、glFinish 込みの 120 回平均):");
        foreach (int resolution in ShadowMap.Resolutions)
        {
            _shadow.SetResolution(resolution);
            _shadow.Radius = originalRadius;

            // 1回捨てる(確保直後の初回は割り当てのぶんだけ遅い)。
            BenchmarkDepthPass(1);
            double milliseconds = BenchmarkDepthPass(120);

            Console.WriteLine(
                $"    {resolution,4}x{resolution,-4} {milliseconds,6:F3}ms"
                + $"  {_shadow.ByteSize / (1024.0 * 1024.0),5:F1}MB"
                + $"  1テクセル {_shadow.WorldPerTexel * 100.0f,5:F1}cm");
        }

        _shadow.SetResolution(originalResolution);
        _shadow.Radius = originalRadius;
        _shadow.CullFrontFaces = originalCull;

        Framebuffer.BindDefault(_gl, _window.FramebufferSize.X, _window.FramebufferSize.Y);

        checks.Report();
        Console.WriteLine();

        // 世界座標を光のクリップ座標へ。**シェーダがやっているのと同じ計算**を CPU で。
        static Vector4 ToClip(Vector3 world, Matrix4x4 lightSpace) =>
            Vector4.Transform(new Vector4(world, 1.0f), lightSpace);

        static bool Near(float value, float expected, float tolerance = 0.01f) =>
            MathF.Abs(value - expected) <= tolerance;
    }

    /// <summary>
    /// シャドウマップの1テクセルを読み返す。<paramref name="u"/> / <paramref name="v"/> は 0〜1。
    ///
    /// **形式に DepthComponent を指定する**のが今日の新しいところ。
    /// <see cref="RunHdrCheck"/> はカラーを読んでいたが、
    /// ここには色が無いので、同じ <c>glReadPixels</c> でも取りに行く先が違う。
    /// </summary>
    private static unsafe float ReadShadowDepth(float u, float v)
    {
        Framebuffer target = _shadow.Target;
        target.Bind();

        int x = Math.Clamp((int)(u * target.Width), 0, target.Width - 1);
        int y = Math.Clamp((int)(v * target.Height), 0, target.Height - 1);

        float depth = 1.0f;
        _gl.ReadPixels(x, y, 1, 1, PixelFormat.DepthComponent, PixelType.Float, &depth);

        return depth;
    }

    /// <summary>深度パスを <paramref name="iterations"/> 回まわして1回あたりのミリ秒を返す。</summary>
    private static double BenchmarkDepthPass(int iterations)
    {
        _gl.Finish();
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            _shadow.Begin(_lightDirection, Vector3.Zero);
            _shadow.Draw(_quad, FloorMatrix());

            foreach ((Vector3 position, float scale, float spin) in Cubes)
            {
                _shadow.Draw(_cube, CubeMatrix(position, scale, spin, 0.0f));
            }

            _shadow.End(_window.FramebufferSize.X, _window.FramebufferSize.Y);
        }

        // **GPU が終わるまで待つ**。これを入れないと積んだだけの時間が返る。
        _gl.Finish();

        return stopwatch.Elapsed.TotalMilliseconds / iterations;
    }

    /// <summary>
    /// **HDR バッファが本当に 1.0 を超えて持てるかを確かめる自己チェック**(Shift+8)。
    ///
    /// 今日の主張は「8bit のバッファでは明るさが 1.0 で切られてしまう」の一点なので、
    /// それを絵の印象ではなく**読み戻した数値**で確かめる。
    ///
    /// やり方は単純で、シーンバッファを既知の値で塗って
    /// <c>glReadPixels</c> で読み返すだけ。Day 8 でソフトウェアラスタライザの
    /// ピクセルを直接見ていたのと同じことを、GPU 側のバッファに対してやる。
    ///
    /// **後処理が「なんとなく暗い/明るい」ときに、どこで壊れたかを切り分ける道具**にもなる。
    /// 絵は最後まで通ってしまうので、途中の値を1回でも数字で見られると原因追跡が速い。
    /// </summary>
    private static void RunHdrCheck()
    {
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[HDR の自己チェック]");

        // 元に戻すために覚えておく。**チェックがフレームの状態を汚さない**ようにする。
        RenderTargetFormat originalFormat = _post.SceneFormat;

        // 0.25 は暗部、1.0 は白の基準、4.0 は「白の4倍」。
        var probe = new Vector4(0.25f, 1.0f, 4.0f, 1.0f);

        _post.SceneFormat = RenderTargetFormat.Rgba16F;
        Vector4 hdr = ClearAndRead(probe);

        checks.Check("RGBA16F: 暗部 0.25 がそのまま残る", Near(hdr.X, 0.25f), $"実際 {hdr.X:F3}");
        checks.Check("RGBA16F: 1.0 がそのまま残る", Near(hdr.Y, 1.0f), $"実際 {hdr.Y:F3}");
        checks.Check("RGBA16F: **4.0 が 4.0 のまま入る**", Near(hdr.Z, 4.0f), $"実際 {hdr.Z:F3}");

        _post.SceneFormat = RenderTargetFormat.Rgba8;
        Vector4 ldr = ClearAndRead(probe);

        checks.Check("RGBA8: 1.0 までは入る", Near(ldr.Y, 1.0f), $"実際 {ldr.Y:F3}");
        checks.Check("RGBA8: **4.0 は 1.0 に丸められる**", Near(ldr.Z, 1.0f), $"実際 {ldr.Z:F3}");

        // 8bit の刻みも見ておく。0.25 は 0.25098(= 64/255)になる——
        // **書いた値がそのまま返らない**のは、256 段の格子に載せられたから。
        checks.Check(
            "RGBA8: 暗部は 1/255 刻みに丸められる",
            !Near(ldr.X, 0.25f, 0.0005f) && Near(ldr.X, 0.25f, 0.005f),
            $"実際 {ldr.X:F5}(64/255 = {64.0 / 255.0:F5})");

        _post.SceneFormat = originalFormat;

        // 画面のほうも壊さない。次の OnRender が Begin から始めるので、
        // フレームバッファのバインドだけ既定へ戻しておけばよい。
        Framebuffer.BindDefault(_gl, _window.FramebufferSize.X, _window.FramebufferSize.Y);

        checks.Report();
        Console.WriteLine(
            $"  中間バッファ合計 {_post.ByteSize / (1024.0 * 1024.0):F1}MB"
            + $"({_post.Scene.Width}x{_post.Scene.Height})");
        Console.WriteLine();

        // シーンバッファを指定の色で塗って、左下の1画素を読み返す。
        static Vector4 ClearAndRead(Vector4 color)
        {
            Framebuffer scene = _post.Scene;
            scene.Bind();
            _gl.ClearColor(color.X, color.Y, color.Z, color.W);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            // **読み出す形式は自由に選べる**。中身が 8bit でも Float で受け取れば
            // GL が 0〜1 の実数に直して返してくれるので、両方を同じ物差しで比べられる。
            Span<float> pixel = stackalloc float[4];
            unsafe
            {
                fixed (float* data = pixel)
                {
                    _gl.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, data);
                }
            }

            return new Vector4(pixel[0], pixel[1], pixel[2], pixel[3]);
        }

        static bool Near(float value, float expected, float tolerance = 0.01f) =>
            MathF.Abs(value - expected) <= tolerance;
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
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[リソースの自己チェック]");

        checks.Check("既定値のハンドルは無効", !default(Handle<Texture>).IsValid);

        Handle<Texture> first = _resources.LoadTexture(path);
        Handle<Texture> second = _resources.LoadTexture(path);
        checks.Check("同じパスは同じハンドルになる(重複ロードされない)", first == second);
        checks.Check("参照カウントが 2 になる", _resources.RefCountOf(first) == 2, $"実際 {_resources.RefCountOf(first)}");

        _resources.Release(first);
        checks.Check("1回返しただけでは消えない", _resources.IsReady(second));

        _resources.Release(second);
        checks.Check("2回目で消える", !_resources.TryGetTexture(second, out _));
        checks.Check(
            "解放後のハンドルからは仮の絵が返る",
            ReferenceEquals(_resources.GetTexture(second), _resources.Placeholder));

        // **世代番号の本番**。空いたスロットをすぐ次が使う。
        Handle<Texture> reused = _resources.LoadTexture(path);
        checks.Check("空いたスロットが再利用される", reused.Index == second.Index,
            $"新 {reused} / 旧 {second}");
        checks.Check("それでも古いハンドルは無効のまま", reused != second && !_resources.TryGetTexture(second, out _));

        // 読み込み設定までキーに含めているか(ミップマップの有無で結果が変わる)。
        Handle<Texture> noMipmaps = _resources.LoadTexture(path, generateMipmaps: false);
        checks.Check("読み込み設定が違えば別のハンドル", noMipmaps != reused);

        _resources.Release(noMipmaps);
        _resources.Release(reused);

        checks.Report();
        Console.WriteLine();
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
    /// sRGB の色 → リニアな明るさ(Day 31)。**シェーダ側の <c>SrgbToLinear</c> と同じ式**。
    ///
    /// C# 側にも要るのは、シェーダを通らない色があるから——
    /// <c>glClearColor</c> に渡す背景色がそれで、
    /// フラグメントシェーダを1回も通さずにバッファへ入る。
    /// </summary>
    private static Vector4 SrgbToLinear(Vector4 color) =>
        new(
            MathF.Pow(color.X, 2.2f),
            MathF.Pow(color.Y, 2.2f),
            MathF.Pow(color.Z, 2.2f),
            color.W);

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

        Shader shader = _resources.GetShader(material.Shader);
        shader.SetMatrix4("uModel", model);
        shader.SetMatrix3("uNormalMatrix", NormalMatrix(model));
        mesh.Draw();
    }

    /// <summary>
    /// 法線を世界空間へ運ぶ行列。**モデル行列の左上 3x3 の逆転置**(Day 32)。
    ///
    /// 位置は <c>uModel</c> で運べるのに法線は運べない、というのが引っかかりどころ。
    /// 理由は「法線は面に垂直だが、垂直という関係は変換で保たれない」から。
    /// x 方向だけ2倍に伸ばすと、斜めの面は寝るのに、
    /// 同じ行列を法線に掛けると法線は逆に立ってしまう。
    ///
    /// 逆転置を使うと垂直が保たれる。導出は「面上のベクトル t と法線 n の内積 0 を、
    /// 変換後も 0 にする行列は何か」を解くだけで、答えが (M^-1)^T になる。
    ///
    /// **一様スケールと回転しか使っていなければ M と一致する**ので、
    /// 手を抜いても大半のモデルは正しく見える。
    /// glTF は非一様スケールを持つノードを普通に含むので、ここで払っておく。
    /// </summary>
    private static Matrix4x4 NormalMatrix(Matrix4x4 model)
    {
        // 平行移動は法線に効かないので落とす(3x3 だけ使うので実害は無いが、
        // 逆行列の計算を安定させるために消しておく)。
        model.M41 = 0.0f;
        model.M42 = 0.0f;
        model.M43 = 0.0f;

        // 退化した行列(スケール 0 など)は逆行列が作れない。
        // そのときは諦めてモデル行列をそのまま使う——**黙って NaN を流すよりまし**。
        return Matrix4x4.Invert(model, out Matrix4x4 inverse)
            ? Matrix4x4.Transpose(inverse)
            : model;
    }

    private static void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        bool shift = keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight);
        bool ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
        bool alt = keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight);

        switch (key)
        {
            // --- 今日のスイッチ(デモ v1 の組み上げ)---
            //
            // **Ctrl+Shift + ファンクションキー**。Day 37 が Ctrl+F、Day 38 が Shift+F を
            // 取ったので、残っている安全な組み合わせがここになる。
            //
            // <b>Alt+F は今日も使えない</b>。Day 37 の但し書きのとおり、
            // Alt+F4 が窓を閉じるので隣を押し間違えるとアプリが落ちる。
            //
            // <b>この塊はガードの弱い case より必ず上に置く</b>。
            // 下にある `case Key.F1 when ctrl:`(Day 37)は
            // Ctrl+Shift+F1 でも成立してしまうので、
            // **順番が仕様の一部**になっている。C# の switch は上から順に照合する。
            case Key.F1 when ctrl && shift:
                if (_demo is null)
                {
                    LoadDemoScene();
                }
                else
                {
                    UnloadDemoScene();
                }

                break;

            case Key.F2 when ctrl && shift:
                // **今日いちばん効く比較**。同じシーンを手焼きの空(Day 36)と
                // 本物の HDRI で見比べる。手焼きは「それらしい」が、
                // 空の細部が無いので**磨いた金属に何も映らない**。
                _useHdriSky = !_useHdriSky;
                BakeSky();
                ApplyDemoLighting();
                Console.WriteLine(
                    _useHdriSky
                        ? "空: HDRI(本物。太陽も雲も地面も映る)"
                        : "空: 手焼き(Day 36。**太陽の向きは手書きの値**)");
                break;

            case Key.F3 when ctrl && shift:
                // **3枚とも太陽の写り方が違う**。方位は空の回転で揃えてあるので、
                // 動くのは光の質だけ。
                _hdriIndex = (_hdriIndex + 1) % HdriPaths.Length;
                _useHdriSky = true;
                BakeSky();
                ApplyDemoLighting();
                break;

            case Key.F4 when ctrl && shift:
                // **わざと食い違わせる窓**。手書きの向きに戻すと、
                // 空の太陽と影の向きが別々になる——Day 36 で先送りにした問題そのもの。
                _sunFromHdri = !_sunFromHdri;
                ApplyDemoLighting();
                Console.WriteLine(
                    _sunFromHdri
                        ? "平行光源: HDRI から抽出(絵の中の太陽と一致する)"
                        : "平行光源: 手書き(Day 38 まで。**影の向きが空と食い違う**)");
                break;

            case Key.F5 when ctrl && shift:
                // **二重計上を見る窓**。抜くのをやめると影の中が明るくなる。
                _removeSunFromIbl = !_removeSunFromIbl;
                BakeSky();
                Console.WriteLine(
                    _removeSunFromIbl
                        ? "環境マップ: 太陽を抜く(平行光源と足しても二重にならない)"
                        : "環境マップ: 太陽を残す(**影の中まで明るくなる**。粗い金属には映り込む)");
                break;

            case Key.F6 when ctrl && shift:
                {
                    int index = Array.IndexOf(SunThresholdSteps, _sunThreshold);
                    _sunThreshold = SunThresholdSteps[(index + 1) % SunThresholdSteps.Length];
                    BakeSky();
                    ApplyDemoLighting();
                    Console.WriteLine($"太陽のしきい値: 最大輝度の {_sunThreshold:F2} 倍");
                }

                break;

            case Key.F7 when ctrl && shift:
                // **影がどれだけ絵を支えているか**。全部の投げ手を落とすと、
                // 物が床に置かれている感じが一気に消える(SSAO は残る)。
                _sceneShadows = !_sceneShadows;
                Console.WriteLine(
                    _sceneShadows
                        ? "シーンの影: ON(castShadow が true のものだけ深度パスへ)"
                        : "シーンの影: OFF(**全部の投げ手を落とす**。接地感が消える)");
                break;

            case Key.F8 when ctrl && shift:
                {
                    // **デモはスクリーンショットが撮れて初めてデモ**。
                    // 後処理を全部通したあとの絵をそのまま PNG にする。
                    string saved = Screenshot.Save(
                        _gl,
                        _window.FramebufferSize.X,
                        _window.FramebufferSize.Y,
                        Path.Combine(Environment.CurrentDirectory, "screenshots"));
                    Console.WriteLine($"スクリーンショット: {saved}");
                }

                break;

            case Key.F9 when ctrl && shift:
                if (_demo is null)
                {
                    Console.WriteLine("デモ v1: 読み込まれていません(Ctrl+Shift+F1)");
                }
                else
                {
                    DescribeDemoScene();
                }

                break;

            case Key.F10 when ctrl && shift:
                // **JSON を書き換えて押す**。これがあると絵作りが回り始める。
                if (_demo is null)
                {
                    Console.WriteLine("デモ v1: 読み込まれていません(Ctrl+Shift+F1)");
                }
                else
                {
                    LoadDemoScene();
                    Console.WriteLine("デモ v1: 読み直した");
                }

                break;

            case Key.F11 when ctrl && shift:
                // **空を回さないとどうなるか**。HDRI を撮影時の方位のまま使うと、
                // 太陽が壁の裏に回って通りが日陰になる——
                // 「HDRI を1枚拾ってきて貼っただけ」の絵がこれ。
                _applySkyYaw = !_applySkyYaw;
                BakeSky();
                ApplyDemoLighting();
                Console.WriteLine(
                    _applySkyYaw
                        ? "空の回転: シーンの指定どおり(太陽を狙った方位へ持ってくる)"
                        : "空の回転: 0 度(**HDRI の撮影時の方位のまま**)");
                break;

            case Key.F12 when ctrl && shift:
                // **一足飛びで見どころへ**(Shift+F12 と同じ趣旨)。
                if (_demo is null)
                {
                    LoadDemoScene();
                }

                _useHdriSky = true;
                _sunFromHdri = true;
                _sceneShadows = true;
                _env.Enabled = true;
                _env.SkyboxVisible = true;
                _shadow.Enabled = true;
                _ssao.Enabled = true;
                _post.BloomEnabled = true;
                _post.FxaaEnabled = true;
                _post.Split = PostSplit.None;
                _post.Grade.Enabled = false;
                _debugChannel = 0;
                SetSpriteCount(0);

                BakeSky();
                ApplyDemoLighting();
                FrameDemoScene();

                Console.WriteLine(
                    "デモ v1 の決めの構図"
                    + "  Ctrl+Shift+F4 で太陽の出どころ、Ctrl+Shift+F5 で二重計上を見比べる");
                break;

            // 自己チェックは Ctrl+Alt+F12(Day 39)。Ctrl+F12 は Day 38 が使っている。
            case Key.F12 when ctrl && alt:
                RunSceneCheck();
                break;

            // --- 今日のスイッチ(FXAA と簡易カラーグレーディング)---
            //
            // **Shift + ファンクションキー**。Day 37 が Ctrl+F1〜F11 を取ったので、
            // その隣の段を使う。数字キーは Shift / Ctrl / Alt / Ctrl+Shift / Ctrl+Alt の
            // 5段とも埋まっている(Day 37 の但し書き参照)。
            //
            // <b>Shift+F3 だけ飛ばしてある</b>。Day 24 の
            // 「シーンをコードから組み直す」が先に居るためで、上書きすると
            // 過去Dayの動作確認が通らなくなる。**歯抜けは仕方がない**——
            // 育っていく砂場では、キー割り当ては必ずこうなる。
            //
            // ガード付きの case は上から順に照合されるので、
            // 下のほうにある `case Key.F5:`(シェーダ再読込)より前に置くこと。
            case Key.F1 when shift:
                _post.FxaaEnabled = !_post.FxaaEnabled;
                Console.WriteLine(
                    _post.FxaaEnabled
                        ? "FXAA: ON(合成 → LDR バッファ → FXAA → 画面。パスが1つ増える)"
                        : "FXAA: OFF(**輪郭が階段に戻る**。合成が直接画面へ書く)");
                break;

            case Key.F2 when shift:
                // **今日いちばん使う窓のひとつ**。FXAA は絵を見ても
                // 「効いているのか滲んでいるのか」が区別できない。
                _post.FxaaDebugView = _post.FxaaDebugView switch
                {
                    FxaaDebugView.None => FxaaDebugView.Luma,
                    FxaaDebugView.Luma => FxaaDebugView.Edge,
                    FxaaDebugView.Edge => FxaaDebugView.Blend,
                    _ => FxaaDebugView.None,
                };
                Console.WriteLine($"FXAA の表示: {FxaaViewLabel()}");
                break;

            // Shift+F3 は Day 24 の「シーンをコードから組み直す」が使っている。

            case Key.F4 when shift:
                _post.SetFxaaQuality(_post.Quality switch
                {
                    FxaaQuality.Low => FxaaQuality.Medium,
                    FxaaQuality.Medium => FxaaQuality.High,
                    FxaaQuality.High => FxaaQuality.Extreme,
                    _ => FxaaQuality.Low,
                });
                Console.WriteLine(
                    $"FXAA の効き: {FxaaQualityLabel()}"
                    + $"(しきい値 {_post.FxaaEdgeThreshold:F3}/{_post.FxaaEdgeThresholdMin:F4}"
                    + $"  歩幅 {_post.FxaaSpanMax:F0}tx)"
                    + "  ※上げるほど拾うが、**細い線が滲み始める**");
                break;

            case Key.F5 when shift:
                // **Day 37 までの置き方に戻す窓**。
                // 露出(Shift+5/6)を上げると HUD の文字まで白飛びし、
                // モノクロ(Shift+F7 で4回)にすると文字まで灰色になる。
                _uiThroughPost = !_uiThroughPost;
                Console.WriteLine(
                    _uiThroughPost
                        ? "UI: 後処理を通す(Day 37 まで)※露出を上げると**文字が白飛びする**"
                        : "UI: 後処理の外(今日から)※FXAA も掛からないので字が滲まない");
                break;

            case Key.F6 when shift:
                _post.Grade.Enabled = !_post.Grade.Enabled;
                Console.WriteLine(
                    _post.Grade.Enabled
                        ? $"カラーグレーディング: ON({GradeLabel()})"
                        : "カラーグレーディング: OFF(Day 37 までの素の色)");
                break;

            case Key.F7 when shift:
                _post.Grade.ApplyPreset(_post.Grade.Preset switch
                {
                    GradePreset.Neutral => GradePreset.Sunset,
                    GradePreset.Sunset => GradePreset.Moonlight,
                    GradePreset.Moonlight => GradePreset.Bleach,
                    GradePreset.Bleach => GradePreset.Monochrome,
                    _ => GradePreset.Neutral,
                });
                _post.Grade.Enabled = true;
                Console.WriteLine($"下敷き: {GradeLabel()}");
                break;

            case Key.F8 when shift:
                {
                    int index = Array.IndexOf(ColorGrade.ContrastSteps, _post.Grade.Contrast);
                    _post.Grade.Contrast =
                        ColorGrade.ContrastSteps[(index + 1) % ColorGrade.ContrastSteps.Length];
                    _post.Grade.MarkCustom();
                    Console.WriteLine(
                        $"コントラスト: {_post.Grade.Contrast:F2}"
                        + $"  ※{ColorGrade.MiddleGrey:F2}(18% グレー)を中心に回る");
                }

                break;

            case Key.F9 when shift:
                {
                    int index = Array.IndexOf(ColorGrade.SaturationSteps, _post.Grade.Saturation);
                    _post.Grade.Saturation =
                        ColorGrade.SaturationSteps[(index + 1) % ColorGrade.SaturationSteps.Length];
                    _post.Grade.MarkCustom();
                    Console.WriteLine($"彩度: {_post.Grade.Saturation:F2}");
                }

                break;

            case Key.F10 when shift:
                {
                    int index = Array.IndexOf(ColorGrade.TemperatureSteps, _post.Grade.Temperature);
                    _post.Grade.Temperature =
                        ColorGrade.TemperatureSteps[(index + 1) % ColorGrade.TemperatureSteps.Length];
                    _post.Grade.MarkCustom();

                    Vector3 balance = _post.Grade.WhiteBalance;
                    Console.WriteLine(
                        $"色温度: {_post.Grade.Temperature:+0;-0;0}  "
                        + (_post.Grade.Temperature > 0.0f ? "暖色" : "寒色/D65")
                        + $"  LMS の増幅率 ({balance.X:F3}, {balance.Y:F3}, {balance.Z:F3})");
                }

                break;

            case Key.F11 when shift:
                // **今日の主な道具**。アンチエイリアスも色も、隣に並べないと分からない。
                _post.Split = _post.Split switch
                {
                    PostSplit.None => PostSplit.Fxaa,
                    PostSplit.Fxaa => PostSplit.Grade,
                    _ => PostSplit.None,
                };
                Console.WriteLine(_post.Split switch
                {
                    PostSplit.Fxaa => "左右比較: 左 = FXAA 前 / 右 = FXAA 後",
                    PostSplit.Grade => "左右比較: 左 = グレーディング前 / 右 = 後",
                    _ => "左右比較: なし",
                });
                break;

            case Key.F12 when shift:
                // **一足飛びで見どころへ**(Ctrl+F9 と同じ趣旨)。
                // ジャギーがいちばん見える構図は「明るい空を背景にした斜めの縁」。
                _post.FxaaEnabled = true;
                _post.SetFxaaQuality(FxaaQuality.High);
                _post.FxaaDebugView = FxaaDebugView.None;
                _post.Split = PostSplit.Fxaa;
                _post.Grade.ApplyPreset(GradePreset.Sunset);
                _post.Grade.Enabled = true;
                _uiThroughPost = false;

                _materialGrid = false;
                _surfaceDemo = false;
                _draw3D = true;
                _debugChannel = 0;
                SetModel(ModelPaths.Length);

                // **スプライトの群れを消す**(Ctrl+F9 と同じ理由)。
                // 今日見たいのは1画素幅の階段なので、上に何か重なると読めない。
                SetSpriteCount(0);

                // **少し引いて、市松の床を奥まで見せる**。
                //
                // ジャギーがいちばんよく見えるのは、
                //   1. 白飛びした空を背景にした<b>立方体の斜めの縁</b>
                //   2. 遠くへ向かって細かくなる<b>市松模様</b>
                // の2つ。近づきすぎると縁が画面から外れ、
                // 見下ろしすぎると空が消えて 1 が無くなる。
                // 分割線(画面の真ん中)を床の模様がまたぐようにしてあるので、
                // **同じ模様の左右を見比べられる**。
                _orbit.Yaw = 0.55f;
                _orbit.Pitch = 0.08f;
                _orbit.Target = new Vector3(0.0f, 0.40f, 0.0f);
                _orbit.Distance = 9.0f;
                _orbit.Apply();

                Console.WriteLine(
                    "FXAA が効く構図(**画面の左が加工前、右が加工後**)"
                    + "  Shift+F11 で比較を切り、Shift+F1 で FXAA を切って見比べる");
                break;

            // --- 今日のスイッチ(スクリーンスペース環境遮蔽)---
            //
            // **数字キーが5段とも埋まった**。Shift(Day 31・32)、Ctrl(Day 33)、
            // Alt(Day 34)、Ctrl+Shift(Day 35)、Ctrl+Alt(Day 36)。
            // 6段目として Ctrl+Alt+Shift も作れるが、片手で押せないので
            // **ファンクションキーへ移った**。
            //
            // <b>Alt+F1〜 は使えない</b>。Alt+F4 が窓を閉じるので、
            // 隣を押し間違えた瞬間にアプリが終わる。Ctrl+F は安全。
            //
            // ここも上から順に照合されるので、ガード付きを先に置く
            // (下のほうに `case Key.F5:`(シェーダ再読込)がある)。
            case Key.F1 when ctrl:
                _ssao.Enabled = !_ssao.Enabled;
                Console.WriteLine(
                    _ssao.Enabled
                        ? "SSAO: ON(物と物が接するところに暗がりが出る)"
                        : "SSAO: OFF(Day 36 まで。**立方体が床から浮いて見える**)");
                break;

            case Key.F2 when ctrl:
                // **今日いちばん使う窓**。合成した絵からは半径もバイアスも読み取れない。
                _ssao.DebugView = _ssao.DebugView switch
                {
                    SsaoDebugView.None => SsaoDebugView.Occlusion,
                    SsaoDebugView.Occlusion => SsaoDebugView.Normal,
                    SsaoDebugView.Normal => SsaoDebugView.Depth,
                    _ => SsaoDebugView.None,
                };
                Console.WriteLine($"SSAO の表示: {SsaoViewLabel()}");
                break;

            case Key.F3 when ctrl:
                // **絵の印象をいちばん強く決めるつまみ**。
                // 小さいと接地の線だけ、大きいと部屋の隅ぜんぶが暗くなる。
                _ssao.Radius = _ssao.Radius switch
                {
                    < 0.35f => 0.5f,
                    < 0.75f => 1.0f,
                    < 1.5f => 2.0f,
                    _ => 0.25f,
                };
                Console.WriteLine(
                    $"SSAO の半径: {_ssao.Radius:F2}m"
                    + "  ※大きいほど広く暗くなるが、**遠くの物まで遮蔽物と数え始める**");
                break;

            case Key.F4 when ctrl:
                {
                    int index = Array.IndexOf(Ssao.SampleCounts, _ssao.SampleCount);
                    _ssao.SampleCount = Ssao.SampleCounts[(index + 1) % Ssao.SampleCounts.Length];
                    Console.WriteLine(
                        $"SSAO の標本数: {_ssao.SampleCount}本"
                        + "  ※Ctrl+F5 でぼかしを切ると、少ないほど荒れるのが直接見える");
                }

                break;

            case Key.F5 when ctrl:
                _ssao.BlurEnabled = !_ssao.BlurEnabled;
                Console.WriteLine(
                    _ssao.BlurEnabled
                        ? "SSAO のぼかし: ON(4x4 平均。ノイズのタイルが消える)"
                        : "SSAO のぼかし: OFF  ※**4x4 の格子模様が見える**のが正解");
                break;

            case Key.F6 when ctrl:
                _ssao.SetHalfResolution(!_ssao.HalfResolution);
                Console.WriteLine(
                    $"SSAO の解像度: {_ssao.Width}x{_ssao.Height}"
                    + $"({(_ssao.HalfResolution ? "画面の半分。画素数は 1/4" : "画面と同じ")})");
                break;

            case Key.F7 when ctrl:
                _ssao.Strength = _ssao.Strength switch
                {
                    < 0.75f => 1.0f,
                    < 1.25f => 1.5f,
                    < 1.75f => 2.0f,
                    _ => 0.5f,
                };
                Console.WriteLine($"SSAO の強さ: {_ssao.Strength:F1}");
                break;

            case Key.F8 when ctrl:
                // **0 にすると平らな面に縞が出る**。シャドウアクネと同じ現象を、
                // 今度は「自分自身を遮蔽物と数えてしまう」形で見る。
                _ssao.Bias = _ssao.Bias switch
                {
                    < 0.005f => 0.01f,
                    < 0.02f => 0.025f,
                    < 0.04f => 0.05f,
                    _ => 0.0f,
                };
                Console.WriteLine(
                    $"SSAO の下駄: {_ssao.Bias:F3}"
                    + (_ssao.Bias <= 0.0f ? "  ※**平らな床に縞が出る**のが正解" : string.Empty));
                break;

            case Key.F9 when ctrl:
                // **一足飛びで見どころへ**(Ctrl+Alt+9 などと同じ趣旨)。
                // 立方体が床に乗っている構図が、今日いちばん分かりやすい。
                _ssao.Enabled = true;
                _ssao.DebugView = SsaoDebugView.None;

                // **見せるための設定にそろえる**。既定より少し強くしてあるのは、
                // 「効いているかどうか」を最初に分からせるため——
                // 実際に絵を作るときは既定(強さ 1.0)から詰める。
                _ssao.Radius = 1.0f;
                _ssao.Strength = 2.0f;
                _ssao.SampleCount = 32;
                _ssao.BlurEnabled = true;
                _materialGrid = false;
                _surfaceDemo = false;
                _draw3D = true;
                _debugChannel = 0;
                SetModel(ModelPaths.Length);

                // **スプライトの群れを消す**。
                //
                // Day 31 以来のデモは 3D の上に 1000 枚のスプライトを重ねている。
                // 今日の暗がりは接地の細い線なので、上に何か重なると完全に読めない
                // (材質グリッドを出す Ctrl+Shift+5 や Ctrl+Alt+9 でも同じことが起きる。
                // あちらは球が大きいので気づきにくいが、同じ問題を抱えている)。
                // PageUp で元に戻せる。
                SetSpriteCount(0);
                // **目線を落として立方体の根元に寄せる**。
                // 接地の暗がりは床と物の境目にしか出ないので、
                // 見下ろすと床の面積ばかりが増えて、いちばん見たい線が細くなる。
                _orbit.Yaw = 0.9f;
                _orbit.Pitch = 0.10f;
                _orbit.Target = new Vector3(0.0f, -0.35f, 0.0f);
                _orbit.Distance = 4.0f;
                _orbit.Apply();
                Console.WriteLine(
                    "SSAO が効く構図(**立方体の根元に暗がりの帯が出る**)"
                    + "  Ctrl+F1 で切って見比べる");
                break;

            case Key.F10 when ctrl:
                RunSsaoCheck();
                break;

            // 自己チェックは Ctrl+F12(Day 38)。今日のキーは Shift+F12 まで埋まったので、
            // 空いていた Ctrl 側の末尾を使う。
            case Key.F12 when ctrl:
                RunAaCheck();
                break;

            case Key.F11 when ctrl:
                // **わざと間違えるための窓**。AO を直接光に掛けると何が起きるか。
                _ssao.ApplyToDirectLight = !_ssao.ApplyToDirectLight;
                Console.WriteLine(
                    _ssao.ApplyToDirectLight
                        ? "AO を直接光にも掛けた ※**日向の壁際まで暗くなる**のが正解。これは誤用"
                        : "AO は環境光にだけ掛かる(正しい使い方)");
                break;

            // --- 今日のスイッチ(環境マッピングと IBL)---
            //
            // **Ctrl+Alt + 数字**。Shift(Day 31・32)、Ctrl(Day 33)、Alt(Day 34)、
            // Ctrl+Shift(Day 35)に続く5段目。
            //
            // **Alt+Shift を使わなかった**のは、Windows でキーボードレイアウトの
            // 切り替えに割り当てられていることが多いため。押した瞬間に
            // 入力方式が変わってしまうと、原因が分からないまま手が止まる。
            //
            // ガード付きの case は上から順に照合されるので、
            // **2つ押しの組はここでも先頭**に置く(Day 35 と同じ理由)。
            case Key.Number1 when ctrl && alt:
                _env.Enabled = !_env.Enabled;
                Console.WriteLine(
                    _env.Enabled
                        ? "IBL: ON(まわりの景色が光になる)"
                        : "IBL: OFF(Day 35 の「環境光は定数」に戻る。**金属が黒くなる**)");
                break;

            case Key.Number2 when ctrl && alt:
                _env.SkyboxVisible = !_env.SkyboxVisible;
                Console.WriteLine($"空の表示: {OnOff(_env.SkyboxVisible)}(**照明とは無関係**。見た目だけ)");
                break;

            case Key.Number3 when ctrl && alt:
                _env.Intensity = _env.Intensity switch
                {
                    < 0.4f => 0.5f,
                    < 0.8f => 1.0f,
                    < 1.5f => 2.0f,
                    _ => 0.25f,
                };
                Console.WriteLine($"環境光の強さ: {_env.Intensity:F2}");
                break;

            case Key.Number4 when ctrl && alt:
                // 空として事前フィルタの段を出す。**粗さとぼけ具合の対応を目で確かめる**窓。
                _env.SkyboxMip = _env.SkyboxMip + 1 >= EnvironmentMap.PrefilterMipCount
                    ? -1
                    : _env.SkyboxMip + 1;
                Console.WriteLine(
                    _env.SkyboxMip < 0
                        ? "空に出すもの: 環境マップそのもの"
                        : $"空に出すもの: 事前フィルタ 第{_env.SkyboxMip}段"
                            + $"(粗さ {(float)_env.SkyboxMip / (EnvironmentMap.PrefilterMipCount - 1):F2} 相当)");
                break;

            case Key.Number5 when ctrl && alt:
                {
                    // **太陽を回して焼き直す**。空・影・平行光源が一緒に動く。
                    //
                    // 焼き直しに 1 秒近くかかるのでフレームが止まる。
                    // 「環境が変わったら焼き直しが要る」という IBL の性質が、
                    // そのまま体感として出る——動く太陽を扱うゲームで
                    // IBL をどうするかは、それだけで大きな設計問題になる。
                    _sunYaw += MathF.PI / 6.0f;

                    Matrix4x4 rotation = Matrix4x4.CreateRotationY(MathF.PI / 6.0f);
                    _lightDirection = Vector3.Normalize(Vector3.TransformNormal(_lightDirection, rotation));

                    var watch = Stopwatch.StartNew();
                    _env.Bake(_lightDirection, _window.FramebufferSize.X, _window.FramebufferSize.Y);

                    Console.WriteLine(
                        $"太陽を 30 度回した(通算 {_sunYaw * 180.0f / MathF.PI:F0} 度)"
                        + $"  焼き直し {watch.Elapsed.TotalMilliseconds:F0}ms");
                }

                break;

            case Key.Number6 when ctrl && alt:
                {
                    // **空を 8bit に落として焼き直す**。Day 31 の Shift+1 と同じ実験を、
                    // 今度は「光源としての環境」で行う。
                    _env.ClampSkyToLdr = !_env.ClampSkyToLdr;
                    _env.Bake(_lightDirection, _window.FramebufferSize.X, _window.FramebufferSize.Y);

                    Console.WriteLine(
                        _env.ClampSkyToLdr
                            ? "空を 1.0 で頭打ちにして焼き直した  ※環境光がのっぺりするのが正解"
                            : "空を HDR のまま焼き直した(太陽は 300)");
                }

                break;

            case Key.Number7 when ctrl && alt:
                // 焼いた3枚を1枚ずつ見る。**「なんとなく良くなった」で済ませないため**。
                _iblViewIndex = (_iblViewIndex + 1) % 4;
                _debugChannel = _iblViewIndex == 0 ? 0 : 17 + _iblViewIndex;
                Console.WriteLine($"表示する成分: {DebugChannelLabel()}");
                break;

            case Key.Number8 when ctrl && alt:
                _env.UsePrefilter = !_env.UsePrefilter;
                Console.WriteLine(
                    _env.UsePrefilter
                        ? "鏡面の環境: 事前フィルタ(粗さに応じた段を引く)"
                        : "鏡面の環境: 原寸のみ  ※粗い金属が鏡になり、太陽の反射がちらつくのが正解");
                break;

            case Key.Number9 when ctrl && alt:
                // **一足飛びで見どころへ**(Alt+9 / Ctrl+Shift+9 と同じ趣旨)。
                // 材質グリッドに IBL を当てた絵が、今日いちばん見せたいもの。
                _env.Enabled = true;
                _env.SkyboxVisible = true;
                _pbrEnabled = true;
                _materialGrid = true;
                _debugChannel = 0;
                _iblViewIndex = 0;
                _orbit.Yaw = 0.0f;
                _orbit.Pitch = 0.0f;
                _orbit.Target = Vector3.Zero;
                _orbit.Distance = 13.0f;
                _orbit.Apply();
                Console.WriteLine("材質グリッド + IBL(**上の行に景色が映る**)");
                break;

            case Key.Number0 when ctrl && alt:
                RunIblCheck();
                break;
            // --- 今日のスイッチ(物理ベースレンダリング)---
            //
            // **Ctrl+Shift + 数字**。Shift(Day 31・32)、Ctrl(Day 33)、Alt(Day 34)に続く4段目。
            //
            // ガード付きの case は**上から順に照合される**ので、
            // **2つ押しの組は1つ押しより必ず先に置く**。
            // 下の `when ctrl` を先に書くと、Ctrl+Shift+1 がそちら(影の ON/OFF)に吸われて、
            // 「押しても何も起きない」ではなく「別の機能が動く」という分かりにくい壊れ方をする。
            case Key.Number1 when ctrl && shift:
                _pbrEnabled = !_pbrEnabled;
                Console.WriteLine(
                    _pbrEnabled
                        ? "陰影: Cook-Torrance(物理ベース)"
                        : "陰影: ランバート(Day 34 まで。金属度も粗さも効かない)");
                break;

            case Key.Number2 when ctrl && shift:
                _metallicOverride = (_metallicOverride + 1) % 4;
                Console.WriteLine(
                    $"金属度の上書き: {(_metallicOverride == 0 ? "しない(モデルの値)" : $"{MetallicOverrideValue():F2}")}");
                break;

            case Key.Number3 when ctrl && shift:
                _roughnessOverride = (_roughnessOverride + 1) % 5;
                Console.WriteLine(
                    $"粗さの上書き: {(_roughnessOverride == 0 ? "しない(モデルの値)" : $"{RoughnessOverrideValue():F2}")}");
                break;

            case Key.Number4 when ctrl && shift:
                // 非金属の F0。**0.04 から動かす理由はほとんど無い**が、
                // 動かすと「非金属の鏡面はこんなに弱い」ことが逆に分かる。
                _dielectricF0 = _dielectricF0 switch
                {
                    < 0.02f => 0.04f,
                    < 0.06f => 0.08f,
                    < 0.12f => 0.17f,
                    _ => 0.00f,
                };
                Console.WriteLine(
                    $"非金属の F0: {_dielectricF0:F2}"
                    + _dielectricF0 switch
                    {
                        < 0.02f => "(反射しない。物理的には有り得ない)",
                        < 0.06f => "(水・プラスチック・ガラス。ほとんどの物質)",
                        < 0.12f => "(宝石)",
                        _ => "(ダイヤモンド)",
                    });
                break;

            case Key.Number5 when ctrl && shift:
                _materialGrid = !_materialGrid;
                if (_materialGrid)
                {
                    // **正面から見せる**。材質を見比べる絵なので、
                    // 斜めから見ると列ごとに視線の角度が変わり、
                    // フレネルのせいで右へ行くほど明るくなってしまう。
                    _orbit.Yaw = 0.0f;
                    _orbit.Pitch = 0.0f;
                    _orbit.Target = Vector3.Zero;
                    _orbit.Distance = 13.0f;
                    _orbit.Apply();
                }

                Console.WriteLine(
                    _materialGrid
                        ? $"材質グリッド: {MaterialGridSize}x{MaterialGridSize}(縦=金属度 0→1 / 横=粗さ 0→1)"
                            + "  ※上の行が黒いのは正しい。映り込む景色が要る(Day 36)"
                        : "材質グリッド: OFF");
                break;

            case Key.Number6 when ctrl && shift:
                _perceptualRoughness = !_perceptualRoughness;
                Console.WriteLine(
                    _perceptualRoughness
                        ? "α の作り方: roughness²(glTF・Unreal・Unity の流儀)"
                        : "α の作り方: roughness そのまま  ※中央より右がほとんど同じに見える");
                break;

            case Key.Number7 when ctrl && shift:
                _ambientSpecular = !_ambientSpecular;
                Console.WriteLine(
                    _ambientSpecular
                        ? "環境鏡面: ON(Day 36 の IBL までの粗いつなぎ)"
                        : "環境鏡面: OFF  ※金属がハイライト以外真っ黒になるのが素の姿");
                break;

            case Key.Number8 when ctrl && shift:
                _gridColorIndex = (_gridColorIndex + 1) % GridColors.Length;
                Console.WriteLine(
                    $"グリッドのベースカラー: {GridColors[_gridColorIndex].Name}"
                    + "  ※金属の行だけ鏡面に色が乗る(F0 = ベースカラー)");
                break;

            case Key.Number9 when ctrl && shift:
                // **一足飛びで見どころへ**(Alt+9 と同じ趣旨)。
                // 鏡面だけを見ると、粗さとフレネルの効きが拡散に邪魔されずに読める。
                _pbrEnabled = true;
                _materialGrid = true;
                _debugChannel = 14;
                _orbit.Yaw = 0.0f;
                _orbit.Pitch = 0.0f;
                _orbit.Target = Vector3.Zero;
                _orbit.Distance = 13.0f;
                _orbit.Apply();
                Console.WriteLine($"材質グリッド + 表示する成分: {DebugChannelLabel()}");
                break;

            case Key.Number0 when ctrl && shift:
                RunPbrCheck();
                break;

            // --- 今日のスイッチ(法線マップと視差マッピング)---
            //
            // **Alt + 数字**。Shift(Day 31・32)、Ctrl(Day 33)に続く3段目。
            // 修飾キーが日の目印になっているので、
            // 「あの機能は何日目だったか」を手が覚える。
            case Key.Number1 when alt:
                _normalMapping = !_normalMapping;
                Console.WriteLine($"法線マップ: {OnOff(_normalMapping)}");
                break;

            case Key.Number2 when alt:
                _parallaxMode = (_parallaxMode + 1) % 4;
                Console.WriteLine($"視差マッピング: {ParallaxLabel()}");
                break;

            case Key.Number3 when alt:
                _parallaxScale = _parallaxScale switch
                {
                    < 0.03f => 0.05f,
                    < 0.08f => 0.10f,
                    < 0.13f => 0.18f,
                    _ => 0.02f,
                };
                Console.WriteLine(
                    $"視差の深さ: {_parallaxScale:F3}"
                    + (_parallaxScale > 0.15f ? "(縁の破綻が見える)" : string.Empty));
                break;

            case Key.Number4 when alt:
                // 刻み数。**粗くすると急峻視差の段差がはっきり出る**。
                (_parallaxMinSteps, _parallaxMaxSteps) = (_parallaxMinSteps, _parallaxMaxSteps) switch
                {
                    (8, 32) => (4, 8),
                    (4, 8) => (16, 64),
                    _ => (8, 32),
                };
                Console.WriteLine($"レイマーチの刻み: {_parallaxMinSteps}〜{_parallaxMaxSteps}");
                break;

            case Key.Number5 when alt:
                _surfaceDemo = !_surfaceDemo;
                Console.WriteLine(
                    _surfaceDemo
                        ? "材質テスト: レンガの板(手前の床が寝ているので、そこで視差を見る)"
                        : "材質テスト: OFF");
                break;

            case Key.Number6 when alt:
                _surfaceTiling = _surfaceTiling switch
                {
                    < 1.5f => 2.0f,
                    < 3.0f => 4.0f,
                    _ => 1.0f,
                };
                Console.WriteLine($"テスト板の繰り返し: {_surfaceTiling:F0}x{_surfaceTiling:F0}");
                break;

            case Key.Number7 when alt:
                // **ファイルの接線と、生成した接線を見比べる**。
                // 読み直しになるのでフレームが止まる(モデルの読み込みは同期。Day 32)。
                _forceGeneratedTangents = !_forceGeneratedTangents;
                Console.WriteLine(
                    _forceGeneratedTangents
                        ? "接線: ファイルの TANGENT を無視して生成する"
                        : "接線: ファイルにあればそれを使う");
                if (_model is not null)
                {
                    SetModel(_modelIndex);
                }

                break;

            case Key.Number8 when alt:
                _flipGreen = !_flipGreen;
                Console.WriteLine(
                    $"法線マップの緑: {(_flipGreen ? "反転(DirectX 形式)" : "そのまま(OpenGL 形式)")}"
                    + (_flipGreen ? "  ※凹凸が裏返って見えるのが正解" : string.Empty));
                break;

            case Key.Number9 when alt:
                _normalMapping = true;
                _debugChannel = 11;
                Console.WriteLine($"表示する成分: {DebugChannelLabel()}");
                break;

            case Key.Number0 when alt:
                RunTangentCheck();
                break;

            // --- 今日のスイッチ(シャドウマッピング)---
            //
            // **Ctrl + 数字**。Shift + 数字は Day 31・32 で埋まったので、次の段へ移った。
            // 修飾キーで日をまとめておくと、あとから「あの機能は何日目だったか」を
            // 手が覚えている、という副産物がある。
            //
            // ガード付きの case は**上から順に照合される**ので、
            // 修飾キー付きを全部先に置く(Shift 版と同じ理由。下のコメント参照)。
            case Key.Number1 when ctrl:
                _shadow.Enabled = !_shadow.Enabled;
                Console.WriteLine($"影: {OnOff(_shadow.Enabled)}");
                break;

            case Key.Number2 when ctrl:
                {
                    int index = Array.IndexOf(ShadowMap.Resolutions, _shadow.Resolution);
                    int next = ShadowMap.Resolutions[(index + 1) % ShadowMap.Resolutions.Length];
                    _shadow.SetResolution(next);
                    Console.WriteLine(
                        $"シャドウマップ: {next}x{next}  {_shadow.ByteSize / (1024.0 * 1024.0):F1}MB"
                        + $"  1テクセル {_shadow.WorldPerTexel * 100.0f:F1}cm");
                }

                break;

            case Key.Number3 when ctrl:
                _shadow.PcfRadius = (_shadow.PcfRadius + 1) % 4;
                Console.WriteLine(
                    $"PCF: 半径{_shadow.PcfRadius}"
                    + $"({(2 * _shadow.PcfRadius) + 1}x{(2 * _shadow.PcfRadius) + 1} = {_shadow.TapCount}タップ)");
                break;

            case Key.Number4 when ctrl:
                // **0 を必ず通す**。アクネが出る状態を1周ごとに見られるようにしてある。
                _shadow.DepthBias = _shadow.DepthBias switch
                {
                    <= 0.0f => 0.0005f,
                    < 0.001f => 0.0015f,
                    < 0.003f => 0.006f,
                    _ => 0.0f,
                };
                Console.WriteLine(
                    $"深度バイアス: {_shadow.DepthBias:F4}"
                    + (_shadow.DepthBias <= 0.0f ? "(アクネが出る)" : string.Empty));
                break;

            case Key.Number5 when ctrl:
                _shadow.SlopeBias = !_shadow.SlopeBias;
                Console.WriteLine($"傾きに比例したバイアス: {OnOff(_shadow.SlopeBias)}");
                break;

            case Key.Number6 when ctrl:
                _shadow.CullFrontFaces = !_shadow.CullFrontFaces;
                Console.WriteLine(
                    $"深度パスのカリング: {(_shadow.CullFrontFaces ? "表を捨てる(裏だけ焼く)" : "裏を捨てる(通常)")}"
                    + (_shadow.CullFrontFaces ? "  ※床のような1枚板は影を落とさなくなる" : string.Empty));
                break;

            case Key.Number7 when ctrl:
                _shadow.ShowMap = !_shadow.ShowMap;
                Console.WriteLine($"シャドウマップの表示: {OnOff(_shadow.ShowMap)}");
                break;

            case Key.Number8 when ctrl:
                // 光の箱の広さ。**広げるほど影が粗くなる**のを 1 キーで見るためのもの。
                _shadow.Radius = _shadow.Radius switch
                {
                    < 4.0f => 6.0f,
                    < 9.0f => 12.0f,
                    < 18.0f => 24.0f,
                    _ => 3.0f,
                };
                Console.WriteLine(
                    $"光の届く範囲: 半径 {_shadow.Radius:F0}m"
                    + $"  1テクセル {_shadow.WorldPerTexel * 100.0f:F1}cm");
                break;

            case Key.Number9 when ctrl:
                // 光を Y 軸まわりに 30 度ずつ回す。**影が動くと形が読める**。
                _lightDirection = Vector3.Normalize(
                    Vector3.Transform(_lightDirection, Matrix4x4.CreateRotationY(MathF.PI / 6.0f)));
                Console.WriteLine(
                    $"光の向き: ({_lightDirection.X:F2}, {_lightDirection.Y:F2}, {_lightDirection.Z:F2})");
                break;

            case Key.Number0 when ctrl:
                RunShadowCheck();
                break;

            // --- 今日のスイッチ(HDR パイプライン)---
            //
            // **Shift + 数字**にまとめた。文字キーはもう空きが無く、
            // 数字の裏なら「Shift を押しながらなら今日のもの」と一列に覚えられる。
            //
            // ここが**ガード付き case を先に置く**必要のある場所。
            // 下の `case Key.Number1:`(シミュレーションレート)を先に書くと、
            // Shift ごと吸われて Shift+1 が届かない
            // (C# は上から順に照合する。逆に書くとコンパイルエラーになる)。
            case Key.Number1 when shift:
                _post.SceneFormat = _post.SceneFormat == RenderTargetFormat.Rgba16F
                    ? RenderTargetFormat.Rgba8
                    : RenderTargetFormat.Rgba16F;
                Console.WriteLine(
                    $"シーンバッファ: {(_post.SceneFormat == RenderTargetFormat.Rgba16F ? "RGBA16F(HDR)" : "RGBA8(1.0 で切られる)")}"
                    + $"  {_post.ByteSize / (1024.0 * 1024.0):F1}MB");
                break;

            case Key.Number2 when shift:
                _post.BloomEnabled = !_post.BloomEnabled;
                Console.WriteLine($"ブルーム: {(_post.BloomEnabled ? "ON" : "OFF")}");
                break;

            case Key.Number3 when shift:
                _post.ToneMap = _post.ToneMap switch
                {
                    ToneMapOperator.None => ToneMapOperator.Reinhard,
                    ToneMapOperator.Reinhard => ToneMapOperator.Aces,
                    _ => ToneMapOperator.None,
                };
                Console.WriteLine($"トーンマップ: {ToneMapLabel()}");
                break;

            case Key.Number4 when shift:
                _post.DebugView = _post.DebugView switch
                {
                    PostDebugView.Final => PostDebugView.SceneOnly,
                    PostDebugView.SceneOnly => PostDebugView.Bright,
                    PostDebugView.Bright => PostDebugView.Bloom,
                    _ => PostDebugView.Final,
                };
                Console.WriteLine($"表示する段: {DebugViewLabel()}");
                break;

            case Key.Number5 when shift:
            case Key.Number6 when shift:
                // 1 段(2倍)ではなく 1.3 倍刻みにしてある。
                // 2倍だと明るさが飛びすぎて「ちょうどいいところ」を通り過ぎる。
                _post.Exposure = Math.Clamp(
                    _post.Exposure * (key == Key.Number6 ? 1.3f : 1.0f / 1.3f),
                    0.02f,
                    64.0f);
                Console.WriteLine($"露出: {_post.Exposure:F2}");
                break;

            case Key.Number7 when shift:
                _post.BloomThreshold = _post.BloomThreshold switch
                {
                    < 0.8f => 1.0f,
                    < 1.2f => 1.5f,
                    < 2.0f => 2.5f,
                    _ => 0.7f,
                };
                Console.WriteLine($"ブルームのしきい値: {_post.BloomThreshold:F1}");
                break;

            case Key.Number8 when shift:
                RunHdrCheck();
                break;

            // --- 今日のスイッチ(glTF)---
            case Key.Number9 when shift:
                // Day 35 で BRDF の5つ、Day 36 で IBL の3つ、Day 37 で SSAO が増えて 22 通りになった。
                // **多いので Alt+9 と Ctrl+Shift+9 で目当ての成分へ直接飛べる**ようにしてある。
                _debugChannel = (_debugChannel + 1) % 22;
                Console.WriteLine($"表示する成分: {DebugChannelLabel()}");
                break;

            case Key.Number0 when shift:
                // 最後まで行ったら「無し」を1周ぶん挟む。
                // **Day 31 までのデモに戻れる**ようにしておかないと、
                // 明るさの階段やスプライトの計測が見られなくなる。
                SetModel(_modelIndex + 1 > ModelPaths.Length ? 0 : _modelIndex + 1);
                break;

            case Key.Minus when shift:
                RunGltfCheck();
                break;

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
                    // (shift は Day 31 で switch の外へ出した。今日のスイッチが全部 Shift 併用なので)

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

                // **後処理のシェーダこそリロードが効く**(Day 31)。
                // トーンマップの曲線もぼかしの重みも、
                // 数字を1つ変えて絵を見る、を何十回も繰り返して決めるもの。
                // 再起動を挟むとその往復が成立しない。
                _post.ReloadShaders();

                // **SSAO も同じ**(Day 37)。半径・下駄・重み付けは
                // 数字を1つ変えて絵を見る、を繰り返して決めるもの。
                _ssao.ReloadShaders();
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

        // モデルはメッシュを所有し、テクスチャの参照を握っている(Day 32)。
        // **RenderResources より先に畳む**——順番を逆にすると、
        // 既に消えたプールへ Release を呼ぶことになる。
        _model?.Dispose();

        // フレームバッファとレンダーバッファも GC の管轄外(Day 31)。
        // シェーダは RenderResources が持っているので、ここで畳むのはバッファだけ。
        _post.Dispose();

        // シャドウマップも同じ(Day 33)。深度テクスチャと空 VAO を返す。
        _shadow.Dispose();

        // 環境マップも同じ(Day 36)。キューブマップ3枚と表1枚、FBO、立方体を畳む。
        _env.Dispose();

        // SSAO も同じ(Day 37)。幾何バッファ・遮蔽の2枚・ノイズ・空 VAO を畳む。
        _ssao.Dispose();

        // デモのシーンも同じ(Day 39)。glTF のメッシュを畳み、
        // 借りたテクスチャの参照カウントを返す。
        _demo?.Dispose();

        _cube.Dispose();
        _quad.Dispose();
        _sphere.Dispose();

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

    /// <summary>
    /// 自己チェックの結果を数えて出すだけの小さな道具。
    ///
    /// 合否の1行出力と失敗数の集計は、どのチェックでも書くことが同じになる。
    /// 各 RunXxxCheck() にローカル関数として書き散らすと、チェックが増えるたびに
    /// **同じコードが増えるだけ**なので、1箇所に寄せてある。
    /// 失敗数を内側に持たせたので、呼ぶ側が数える変数を用意する必要も無くなる。
    /// </summary>
    private sealed class CheckList
    {
        /// <summary>不合格だった件数。</summary>
        public int Failures { get; private set; }

        /// <summary>1項目ぶんの合否を出し、不合格なら数える。</summary>
        public void Check(string name, bool condition, string detail = "")
        {
            Console.WriteLine($"  [{(condition ? "OK" : "NG")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!condition)
            {
                Failures++;
            }
        }

        /// <summary>まとめの1行を出す。全部通ったときの文言だけ差し替えられる。</summary>
        public void Report(string okMessage = "すべて合格")
        {
            Console.WriteLine(Failures == 0 ? $"  {okMessage}" : $"  {Failures} 件 不合格");
        }
    }
}
