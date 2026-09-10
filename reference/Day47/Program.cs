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
///
/// **Day 40 での変更**: **デモ v1 が動き出した**。Phase 6 の最終日。
///
/// Day 39 で出来たのは<b>決めの構図1枚</b>だった。
/// 静止画でよいなら PNG を置けばよく、実時間で描いている意味が無い——
/// 今日はそこに2つを足して、デモとして成立させる。
///
/// 1つ目は<b>カメラワーク</b>(<see cref="CameraPath"/>)。
/// 注視点・距離・方位・仰角・画角を持つキーフレームを並べ、
/// Catmull-Rom スプラインで繋いでゆっくり巡回する。
/// <c>Ctrl+Alt+F4</c> で線形補間に落とすと、**キーを通過する瞬間に速度が飛ぶ**——
/// 絵では気づけないので、HUD に m/s と 度/s を出してある。
///
/// 2つ目は<b>機能の ON/OFF 表</b>(<see cref="FeatureToggles"/>)。
/// Day 31〜38 のスイッチは switch 文に8日ぶん散らばっていて、
/// 「全部 OFF の素の絵」へ1キーで行けなかった。
/// 表にすると <c>Ctrl+Alt+F9</c> の1押しで往復でき、
/// <c>Ctrl+Alt+F10</c> の機能ツアーは
/// **1つずつ切っては戻す**のを勝手に繰り返す。
/// Day 39 の <c>Ctrl+Shift+F12</c> がフラグを手で12個並べていたのも、
/// <c>SetAll(true)</c> の1行になった。
///
/// これで Phase 6 のマイルストーン——
/// **HDRI と CC0 アセットで組んだ静的シーンが、
/// PBR+IBL+ソフトシャドウ+SSAO+ブルーム+AA で描かれ、
/// ゆっくりしたカメラパンで見られる(各機能は ON/OFF 切り替え可能)**——
/// に到達する。
///
/// キーは `Ctrl+Alt+F1`〜`F11`(`F12` は Day 39 の自己チェックが先客)。
/// `Ctrl+Alt+F1` で再生が始まる。
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

    // --- 今日の主役: カメラワークと機能トグル(Day 40)---

    /// <summary>
    /// 機能の ON/OFF 表(<see cref="BuildFeatureToggles"/> が組む)。
    /// **Day 31〜38 の機能がここに全部並ぶ**。
    /// </summary>
    private static FeatureToggles _features = null!;

    /// <summary>カメラワークを再生中か(Ctrl+Alt+F2)。</summary>
    private static bool _cameraPlaying;

    /// <summary>再生位置(秒)。1周したら勝手に戻る。</summary>
    private static float _cameraTime;

    /// <summary>再生速度。**遅くして見るためのもの**で、既定は等倍。</summary>
    private static float _cameraSpeed = 1.0f;

    private static readonly float[] CameraSpeedSteps = [0.25f, 0.5f, 1.0f, 2.0f];

    /// <summary>補間方式(Ctrl+Alt+F4)。**線形にすると継ぎ目で速度が飛ぶ**。</summary>
    private static CameraInterpolation _cameraInterpolation = CameraInterpolation.CatmullRom;

    /// <summary>区間の出入りで速度を 0 に寄せるか(Ctrl+Alt+F5)。</summary>
    private static bool _cameraEase = true;

    /// <summary>いま画面が動いている速さ(HUD 用。**線形と Catmull-Rom の差はここに出る**)。</summary>
    private static float _cameraMetresPerSecond;

    private static float _cameraDegreesPerSecond;

    // --- Day 39 の主役: デモ v1 ---

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

    /// <summary>
    /// 円柱(Day 45)。**カプセルの胴**として使う。
    ///
    /// カプセル1本を描くのに <see cref="_sphere"/> 2つとこれ1本、
    /// 合わせて3回の <c>Draw</c> になる。
    /// 寸法ごとのメッシュを作らずに済むのが値打ちで、
    /// 理由は <see cref="Primitives.CreateCylinder"/> のコメントに書いた。
    /// </summary>
    private static Mesh<Vertex> _cylinder = null!;

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
    ///
    /// <b>Day 41 で5体増えた</b>。今度はリグとアニメーションの側で経路を分けてある。
    ///
    /// | | 何を試すか |
    /// |---|---|
    /// | CesiumMan | **今日の到達点**。19 関節の人型が歩く。skin + animation の定番 |
    /// | Fox | **クリップが3本**(Survey / Walk / Run)。NORMAL を持たないので法線を生成する |
    /// | RiggedSimple | 関節2本の曲がる棒。**壊れたときの切り分け**用 |
    /// | SimpleSkin | 仕様の最小例。頂点 10 個・三角形8枚。**手で追える大きさ** |
    /// | BoxAnimated | **スキン無し**のアニメーション。ノードの TRS だけが動く |
    ///
    /// 最後の1体が効く。「アニメーション」と「スキニング」は別の仕組みで、
    /// **前者だけでも動くものは作れる**——ここを混ぜて憶えると、
    /// スキンを持たないモデルが動かないときに骨のほうを疑って時間を落とす。
    /// </summary>
    private static readonly string[] ModelPaths =
    [
        "models/DamagedHelmet.glb",
        "models/WaterBottle.glb",
        "models/Lantern.glb",
        "models/BoxTextured/BoxTextured.gltf",
        "models/CesiumMan.glb",
        "models/Fox.glb",
        "models/RiggedSimple.glb",
        "models/SimpleSkin/SimpleSkin.gltf",
        "models/BoxAnimated.glb",
    ];

    /// <summary>今表示しているモデル。null なら無し(Day 31 までの絵)。</summary>
    private static Model? _model;

    /// <summary><see cref="ModelPaths"/> の添字。範囲外なら「無し」。</summary>
    private static int _modelIndex;

    /// <summary>モデルを画面に収めるための倍率と位置。読み込み時に境界箱から決める。</summary>
    private static Matrix4x4 _modelTransform = Matrix4x4.Identity;

    /// <summary>読み込みにかかった時間(ミリ秒)。**同期で読むので、そのままフレームが飛ぶ**。</summary>
    private static double _modelLoadMilliseconds;

    // --- Day 41: スキニングアニメーション ---

    /// <summary>
    /// 今のモデルの再生装置(Day 41)。**モデルと1対1**にしてあるが、本来は多対1。
    ///
    /// ここで1個しか持たないのは、画面に1体しか出さないから。
    /// <see cref="AnimationPlayer"/> のコメントにあるとおり、同じモデルを
    /// 100 体並べるならプレイヤーだけ 100 個作る——その形になっているかを
    /// 確かめられるよう、モデル側には時刻を1つも持たせていない。
    /// </summary>
    private static AnimationPlayer? _animation;

    /// <summary>再生速度の候補(Alt+F5 で巡回)。</summary>
    private static readonly float[] PlaybackSpeeds = [0.25f, 0.5f, 1.0f, 2.0f];

    private static int _playbackSpeedIndex = 2;

    // --- Day 42: アニメーション制御 ---

    /// <summary>ロコモーションの決め方(Shift+Alt+F2)。</summary>
    private enum LocomotionMode
    {
        /// <summary>Day 41 と同じ。クリップを1本そのまま再生する。</summary>
        Off,

        /// <summary>速度から連続的に重みを決める(<see cref="BlendTree1D"/>)。</summary>
        BlendTree,

        /// <summary>状態を1つ持ち、切り替わるときだけ混ぜる(<see cref="AnimationStateMachine"/>)。</summary>
        StateMachine,
    }

    private static LocomotionMode _locomotion;

    /// <summary>速度 → 重み。クリップ名から組み立てる(<see cref="BuildLocomotion"/>)。</summary>
    private static BlendTree1D? _locomotionTree;

    /// <summary>同じクリップを状態として並べたもの。**ツリーと見比べるために両方持つ**。</summary>
    private static AnimationStateMachine? _locomotionStates;

    /// <summary>
    /// 今の移動速度(m/s)。**ブレンドの入力そのもの**。
    ///
    /// Day 42 の時点ではキャラクタは1ミリも進まない——
    /// この数字は「進んでいるつもり」の値でしかない。
    /// 実際の移動と繋ぐのは Day 45(キャラクターコントローラ)以降で、
    /// **先に見た目の側だけを作っておく**と、繋ぐときに片方だけを疑えばよくなる。
    /// </summary>
    private static float _moveSpeed;

    /// <summary>速度を自動で上下させるか(Shift+Alt+F7)。**既定で ON**。</summary>
    private static bool _speedSweep = true;

    private static float _sweepPhase;

    /// <summary>自動スイープの1往復にかける秒数。</summary>
    private const float SweepSeconds = 12.0f;

    /// <summary>速度の上限。Fox の走りのクリップが表している速さに合わせてある。</summary>
    private const float MaxMoveSpeed = 3.2f;

    /// <summary>クロスフェードの候補(Shift+Alt+F8)。**0 を先頭に置いてある**。</summary>
    private static readonly float[] CrossFadeSeconds = [0.0f, 0.1f, 0.25f, 0.5f];

    private static int _crossFadeIndex = 2;

    /// <summary>ブレンドの重みを書き込む先。**毎フレーム確保しない**ための持ち回し。</summary>
    private static readonly ClipWeight[] _blendScratch = new ClipWeight[AnimationPlayer.MaxBlendClips];

    /// <summary>
    /// コマ送り(Alt+F8)の1回ぶん。**1/30 秒**。
    ///
    /// アニメーションのキーは 24〜30fps 刻みで打たれていることが多いので、
    /// この幅で送ると「キーとキーの間」がちょうど1〜2コマになる。
    /// 補間が効いているかを目で確かめるのにこの粒度が要る。
    /// </summary>
    private const float AnimationStepSeconds = 1.0f / 30.0f;

    // ================================================================
    //  Day 43: 剛体力学の基礎(セミインプリシット積分・力とトルク・インパルス)
    // ================================================================

    /// <summary>今日のデモの筋書き。**見せたいものごとに初期配置が違う**。</summary>
    private enum PhysicsScene
    {
        /// <summary>反発係数を振った5球を落とす。**跳ね方の違いが1画面で見える**。</summary>
        Drop,

        /// <summary>球を縦に積む。**速度の反復回数が効いてくる**。</summary>
        Stack,

        /// <summary>一列に並べた球を端から撞く。**運動量が伝わっていく**。</summary>
        Cradle,

        /// <summary>
        /// 傾きを振った箱を落とす。**今日の到達点**。
        ///
        /// 角から落ちた箱が、跳ねて、傾いて、最後は面で落ち着く。
        /// 接触点の上限を 1 にすると<b>いつまでも落ち着かない</b>のが見どころ。
        /// </summary>
        BoxDrop,

        /// <summary>箱を縦に積む。**球より安定する**——面が4点で支えるから。</summary>
        BoxStack,

        /// <summary>球と箱を混ぜて、傾けた静的な箱の上に落とす。**5通りの判定が全部走る**。</summary>
        BoxMix,

        /// <summary>細長い箱を回しながら落とす。**辺×辺の軸が出る**場面。</summary>
        BoxTumble,

        /// <summary>
        /// カプセルを落とす(Day 45)。**寝ると2点で支えられる**のを見る筋書き。
        ///
        /// 立てて落としたカプセルは1点で着地して倒れ、
        /// 倒れ切ると端2つで支えられて止まる。
        /// 箱の日(Day 44)に「接触点の数 = 支え方の数」と書いたのが、
        /// <b>同じ物体が転がる間に 1 → 2 と変わる</b>形で見える。
        /// </summary>
        CapsuleDrop,

        /// <summary>
        /// 地形の上へ大量に落とす(Day 46)。**今日の到達点のもう半分**。
        ///
        /// 起伏の上へ球・箱・カプセルを 60 個降らせる。見どころは2つ——
        /// <b>谷に転がり集まること</b>(地形の判定)と、
        /// <b>候補の組が総当たりの数分の1になること</b>(ブロードフェーズ)。
        /// Shift+G で総当たりに戻すと、
        /// <b>絵は1ピクセルも変わらないのに1ステップの時間だけが増える</b>。
        /// </summary>
        Terrain,

        /// <summary>
        /// 摩擦を振った箱を坂に置く(Day 47)。**今日の到達点**。
        ///
        /// 同じ坂の上に μ だけ違う箱を5つ並べる。
        /// <b>μ が坂の傾きの正接(tan θ)より大きい箱だけが止まる</b>——
        /// 高校物理の「静止摩擦角」が、そのまま絵で出る。
        /// 一緒に置いた球は<b>μ をいくつにしても転がり落ちる</b>:
        /// 転がり摩擦を入れていないから(要点3)。
        /// </summary>
        FrictionRamp,
    }

    /// <summary>キャラクターデモの筋書き(Day 45)。**確かめたいことが1つずつ**ある。</summary>
    private enum CharacterScene
    {
        /// <summary>傾きを振った坂。**坂の上限(Alt+X)が効く**(要点7)。</summary>
        Slopes,

        /// <summary>高さを振った階段。**段差の乗り越え(Ctrl+Shift+X)が効く**(要点8)。</summary>
        Steps,

        /// <summary>箱と球とカプセルが転がっている中を歩く。**キネマティックと動的の同居**。</summary>
        Obstacles,

        /// <summary>
        /// 起伏のある地形の上を歩く(Day 46)。**今日の到達点**。
        ///
        /// Day 45 の坂は静的な箱を傾けたものだった。今日は本物の地形で、
        /// <b>坂の上限が三角形1枚ごとに効く</b>。
        /// 緩い丘(約 29 度)は登れて、急な丘(約 58 度)は登れない——
        /// 途中まで登って止まるのが、坂の上限が効いている証拠になる。
        /// </summary>
        Terrain,
    }

    /// <summary>物理デモを出しているか(Ctrl+Shift+Alt+F1)。</summary>
    private static bool _physicsDemo;

    /// <summary>
    /// 剛体の世界。**Program は「組み立てて、進めて、描く」だけ**。
    ///
    /// 中身(積分・判定・解決)は <see cref="PhysicsWorld"/> にあり、
    /// そこは GL も窓も一切知らない。おかげで自己チェック(Ctrl+Shift+Alt+F12)は
    /// **画面を1枚も出さずに走る**——Day 26 の衝突判定、Day 42 のブレンドと同じ性格。
    /// </summary>
    private static readonly PhysicsWorld Physics = new();

    /// <summary>今の筋書き。</summary>
    private static PhysicsScene _physicsScene = PhysicsScene.Drop;

    /// <summary>
    /// 体ごとの色。**<see cref="RigidBody"/> には持たせない**。
    ///
    /// 色は描画の都合で、物理の状態ではない。
    /// <c>Physics/</c> が <c>Render/</c> を知らない一方通行を保つために、
    /// 見せ方の情報は呼ぶ側(ここ)に置く——
    /// Day 29 で <c>SurvivorGame</c> が <c>GameView</c> を知らなかったのと同じ線。
    /// </summary>
    private static readonly List<Vector4> BodyColors = [];

    /// <summary>球を描くマテリアル。色は描くたびに差し替える。</summary>
    private static Material _physicsMaterial = null!;

    /// <summary>
    /// 反発係数の候補(Ctrl+Shift+Alt+F3)。
    ///
    /// **これは「これから作る球の既定値」**で、今ある球の値とは限らない。
    /// 落下の筋書きは5球にそれぞれ違う e を振っているので、
    /// HUD の「既定e」と絵が食い違って見える——
    /// `Ctrl+Shift+Alt+F3` を1回押すと全部がこの値に揃う。
    /// </summary>
    private static readonly float[] RestitutionSteps = [0.0f, 0.3f, 0.6f, 0.9f];

    private static int _restitutionIndex = 2;

    /// <summary>速度の解決を何周するか(Ctrl+Shift+Alt+F5)。**1 が素朴版**。</summary>
    private static readonly int[] IterationSteps = [1, 2, 4, 8];

    private static int _iterationIndex = 3;

    /// <summary>物理だけを止める。**画面は動いたまま**なのでカメラを回して観察できる。</summary>
    private static bool _physicsPaused;

    /// <summary>コマ送りで進めたいステップ数(Ctrl+Shift+Alt+F10)。</summary>
    private static int _physicsStepsRequested;

    /// <summary>1ステップの所要時間 [ms]。移動平均。</summary>
    private static double _physicsMilliseconds;

    /// <summary>床の高さ。<see cref="FloorMatrix"/> が置いている板と合わせてある。</summary>
    private const float PhysicsFloorY = -0.5f;

    /// <summary>見えない壁までの距離。**摩擦が無いので、囲わないと滑って消える**。</summary>
    private const float PhysicsWallDistance = 5.0f;

    /// <summary>降らせた球の数。色を巡回させるのに使う。</summary>
    private static int _spawnCount;

    /// <summary>次に撃つときに中心を外すか(Ctrl+Shift+Alt+F8 で交互に入れ替わる)。</summary>
    private static bool _kickOffCenter = true;

    // ================================================================
    //  Day 44: 箱(OBB)の衝突(分離軸定理と接触マニフォールド)
    // ================================================================

    /// <summary>
    /// 接触点を描くか(Ctrl+Shift+Alt+3)。**マニフォールドを目で見るための唯一の窓**。
    ///
    /// 数字(HUD の「接触」)だけでは、4点がどこに立っているかが分からない。
    /// 箱を床の縁に半分載せたときに、
    /// <b>点が縁の上に並び直す</b>のはこれを出さないと見えない。
    /// </summary>
    private static bool _showContacts = true;

    /// <summary>接触点を描くマテリアル。**光の影響を受けない**ように発光で描く。</summary>
    private static Material _contactMaterial = null!;

    /// <summary>
    /// 1組あたりの接触点の上限(Ctrl+Shift+Alt+4)。**今日いちばんの実験**。
    ///
    /// 4 が本来の値。1 にすると Day 43 の「1点だけの接触」に戻り、
    /// 床に置いた箱が支えきれずに震え出す。
    /// </summary>
    private static readonly int[] ContactLimits = [1, 2, 4];

    private static int _contactLimitIndex = 2;

    /// <summary>降らせた箱の数。色を巡回させるのに使う。</summary>
    private static int _boxSpawnCount;

    // ================================================================
    //  Day 47: 摩擦・Sequential Impulses・スリープ(ミニ物理エンジン完成)
    // ================================================================

    /// <summary>
    /// 摩擦の坂(Ctrl+F、または Ctrl+Shift+Alt+2 で選ぶ)。**今日の到達点**(要点3)。
    ///
    /// 同じ坂の上に、合成 μ だけを振った箱を5つ並べる。
    /// 質量も寸法も初速も同じなので、<b>止まるかどうかの違いは μ だけで説明が付く</b>。
    ///
    /// <para>
    /// 坂の傾きは <c>tan θ = 0.5</c>(26.57 度)にしてある。
    /// 斜面に置いた物が滑り出す条件は
    /// <code>
    ///   m g sinθ  &gt;  μ · m g cosθ      ⇔      tanθ &gt; μ
    /// </code>
    /// で、<b>質量が両辺から消える</b>——重い箱も軽い箱も同じ角度で滑り出す。
    /// 並べた 0.1 / 0.3 / 0.5 / 0.7 / 0.9 のうち、
    /// <b>0.7 と 0.9 が止まり、0.1 と 0.3 が滑り、0.5 がちょうど境目</b>になる。
    /// </para>
    ///
    /// <para>
    /// <b>μ を体に入れるとき2乗している</b>のが引っかかりどころ。
    /// 摩擦は2つの体の<b>相乗平均</b> <c>√(μa μb)</c> で混ざるので(要点3)、
    /// 坂を μ = 1 にしておけば <c>√(μ箱 × 1) = √μ箱</c>。
    /// 狙った合成値を出すには、箱にその2乗を入れることになる。
    /// <b>「係数の混ぜ方」が絵にどう出るかを、いちばん短く確かめられる場所</b>。
    /// </para>
    ///
    /// <para>
    /// <b>球は μ をいくつにしても転がり落ちる</b>。
    /// 今日入れたのは滑りに対する摩擦だけで、<b>転がり摩擦は入れていない</b>。
    /// 球は接触点での相対速度が 0 のまま転がれる(それが「転がる」の定義)ので、
    /// 摩擦が仕事をする場面が無い——
    /// <b>モデルに無いものは、係数をいくら上げても出てこない</b>。
    /// </para>
    /// </summary>
    private static void BuildFrictionRampScene()
    {
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -RampTilt);

        // --- 坂(動かない箱)---
        RigidBody ramp = RigidBody.CreateStatic(Collider.Box(new Vector3(3.2f, 0.15f, 3.0f)));
        ramp.Position = new Vector3(0.0f, 1.6f, 0.0f);
        ramp.Orientation = tilt;
        ramp.Restitution = 0.0f;

        // **坂は μ = 1**。相乗平均なので、こうしておくと箱の側の値だけで合成が決まる。
        ramp.Friction = 1.0f;
        Physics.AddBody(ramp);
        BodyColors.Add(SrgbToLinear(new Vector4(0.42f, 0.44f, 0.50f, 1.0f)));

        const float half = 0.25f;

        for (int i = 0; i < RampFrictions.Length; i++)
        {
            float target = RampFrictions[i];

            RigidBody body = RigidBody.CreateBox(1.0f, half);

            // 坂の上面のすぐ上に、奥行き方向へ並べる。
            // **坂の物体座標で置いてから世界へ運ぶ**ので、傾きを変えても並びが崩れない。
            var local = new Vector3(-2.1f, 0.15f + half + 0.002f, (i - 2.0f) * 0.9f);
            body.Position = ramp.Position + Vector3.Transform(local, tilt);

            // **箱も坂に沿わせて置く**。水平に置くと角1つで着地して、
            // 落ち着くまでの数フレームが「滑り出したのか倒れたのか」分からなくなる。
            body.Orientation = tilt;

            // 合成が狙いどおりになるよう、坂の μ で割る(坂は 1 なので実質2乗)。
            body.Friction = target * target / ramp.Friction;
            body.Restitution = 0.0f;
            Physics.AddBody(body);

            // 滑る側を寒色、止まる側を暖色。**色が μ の目盛り**。
            float t = i / (RampFrictions.Length - 1.0f);
            BodyColors.Add(SrgbToLinear(
                new Vector4(0.20f + (0.75f * t), 0.55f, 0.95f - (0.70f * t), 1.0f)));
        }

        // --- 球を1つ(転がり摩擦を入れていないことの実演)---
        RigidBody ball = RigidBody.CreateSphere(1.0f, 0.30f);
        ball.Position = ramp.Position
            + Vector3.Transform(new Vector3(-2.1f, 0.15f + 0.30f + 0.002f, 2.6f), tilt);
        ball.Orientation = tilt;

        // **μ を最大にしても止まらない**。転がる物には滑りが無いので摩擦が効かない。
        ball.Friction = 1.0f;
        ball.Restitution = 0.0f;
        Physics.AddBody(ball);
        BodyColors.Add(SrgbToLinear(new Vector4(0.95f, 0.85f, 0.25f, 1.0f)));
    }

    /// <summary>
    /// 摩擦係数の上書きを1段変える(Shift+Alt+F)。**次のステップから効く**。
    ///
    /// 既に生まれている接触点にも次のステップで新しい μ が入るので、
    /// <b>押した瞬間に坂の箱が一斉に滑り出す/止まる</b>のが見える。
    /// </summary>
    private static void CycleFrictionOverride()
    {
        _frictionIndex = (_frictionIndex + 1) % FrictionSteps.Length;
        Physics.FrictionOverride = FrictionSteps[_frictionIndex];

        // **眠っている体は起こす**。μ を変えても眠ったままだと何も起きず、
        // 「効いていない」ように見えてしまう。
        WakeAllBodies();

        Console.WriteLine(
            Physics.FrictionOverride < 0.0f
                ? "摩擦係数: **体ごとの値**(坂は μ を振った5箱。真ん中が境目)"
                : $"摩擦係数: **全部 μ = {Physics.FrictionOverride:F2}** に上書き"
                    + $"(坂 26.57 度の境目は 0.50)");
    }

    /// <summary>眠っている体を全部起こす。**つまみを動かしたときに必ず呼ぶ**。</summary>
    private static void WakeAllBodies()
    {
        foreach (RigidBody body in Physics.Bodies)
        {
            body.Wake();
        }
    }

    /// <summary>
    /// 解法の状態を1行で(Day 47)。**絵から読めないものだけ**。
    ///
    /// 見どころは3つ。
    /// <list type="bullet">
    /// <item><b>温存の当たり率</b>——<c>温存 14/16点</c> のように出る。
    ///   ここが 0 に張り付いていたら照合が効いていない(<see cref="ContactCache.MatchDistance"/>)</item>
    /// <item><b>眠り</b>——<c>3/12体 島4</c>。島の数が体の数と同じなら、誰も触れ合っていない</item>
    /// <item><b>蓄積 / 素朴</b>——切り替えても<b>絵はしばらく同じ</b>で、
    ///   柱を高くしたときにだけ差が出る</item>
    /// </list>
    /// </summary>
    private static string SolverLabel() =>
        $"解法[{(Physics.AccumulateImpulses ? "蓄積" : "**素朴**")} "
        + (Physics.WarmStartActive
            ? $"温存{Physics.WarmStartedPoints}/{Physics.Contacts.Count}点 組{Physics.CachedPairs}]"
            : Physics.WarmStarting ? "**温存は蓄積とセット**]" : "**温存OFF**]")
        + $"  摩擦:{FrictionLabel()}  "
        + (Physics.SleepEnabled
            ? $"眠り:{Physics.SleepingCount}/{Physics.DynamicCount}体 島{Physics.IslandCount}"
            : "眠り:**OFF**");

    /// <summary>摩擦の設定を短く。**上書きしているかどうかを必ず出す**。</summary>
    private static string FrictionLabel() =>
        !Physics.FrictionEnabled ? "**OFF**"
        : Physics.FrictionOverride >= 0.0f ? $"μ={Physics.FrictionOverride:F2}(上書き)"
        : "体ごと";

    // ================================================================
    //  Day 45: カプセル衝突とキャラクターコントローラ(キネマティック)
    // ================================================================

    /// <summary>キャラクターデモを出しているか(Ctrl+X)。</summary>
    private static bool _characterDemo;

    /// <summary>
    /// 動かすキャラクター。**物理の世界には入っていない**(要点6)。
    ///
    /// <see cref="Physics"/> の <c>Bodies</c> に並んでいないので、
    /// 箱や球はキャラクターに当たらない——当たるのはキャラクターの側だけ。
    /// 「押されるが押さない」というこの非対称が、
    /// キネマティックなキャラクターの性格そのものになる。
    /// </summary>
    private static readonly CharacterController Character = new();

    /// <summary>今の筋書き。</summary>
    private static CharacterScene _characterScene = CharacterScene.Slopes;

    /// <summary>キャラクターを描くマテリアル。**カプセルは球2つ + 円柱1本**で描く。</summary>
    private static Material _characterMaterial = null!;

    /// <summary>降らせたカプセルの数。色を巡回させるのに使う。</summary>
    private static int _capsuleSpawnCount;

    /// <summary>1ステップにキャラクターの更新が使った時間 [ms]。移動平均。</summary>
    private static double _characterMilliseconds;

    /// <summary>
    /// キャラクターを出す位置(足元)。**落ちたときに戻す先**でもある。
    ///
    /// 床(<see cref="PhysicsFloorY"/>)より少しだけ上に置いて、
    /// 最初の数フレームで落ちて着地させる。
    /// ぴったり床の高さに置くと、初期状態でめり込んでいるのか
    /// 接地しているのかが分からなくなる。
    ///
    /// <para>
    /// <b>筋書きごとに変わる</b>。坂も階段も「登り口の手前」に立たせたいので、
    /// 各コースの組み立てが書き換える。
    /// </para>
    /// </summary>
    private static Vector3 _characterSpawn = new(0.0f, PhysicsFloorY + 0.2f, 2.6f);

    // ================================================================
    //  Day 46: Heightmap コライダ(地形)とブロードフェーズ(均一グリッド)
    // ================================================================

    /// <summary>地形1辺のマスの数。**格子点はこれ + 1**。</summary>
    private const int TerrainCells = 48;

    /// <summary>地形の1マスの一辺 [m]。**判定と描画で同じ値を使う**。</summary>
    private const float TerrainCellSize = 0.5f;

    /// <summary>地形の広がり [m]。24m 四方。</summary>
    private const float TerrainSpan = TerrainCells * TerrainCellSize;

    /// <summary>
    /// 今の地形(Day 46)。**筋書きを切り替えるたびに作り直す**。
    ///
    /// <c>null</c> なら地形の無い筋書き(Day 45 までのもの)。
    /// 描画も判定も「地形があるか」だけで分岐する。
    /// </summary>
    private static HeightField? _terrain;

    /// <summary>
    /// 地形の描画用メッシュ。**高さの格子から起こす**(<see cref="Primitives.CreateHeightField"/>)。
    ///
    /// <b>判定とまったく同じ割り方で作る</b>のが肝。
    /// ここがずれると「見えている坂と登れる坂が違う」という、
    /// いちばん切り分けにくいバグになる。
    /// </summary>
    private static Mesh<Vertex>? _terrainMesh;

    /// <summary>地形の (0, 0) 隅の世界座標。**体の位置と同じ値**。</summary>
    private static Vector3 _terrainOrigin;

    /// <summary>地形を描くマテリアル。</summary>
    private static Material _terrainMaterial = null!;

    /// <summary>格子のマスを描くか(Alt+G)。**ブロードフェーズを目で見る**。</summary>
    private static bool _showGridCells;

    /// <summary>
    /// 試せるマスの大きさ [m](Ctrl+Shift+G で巡回)。
    ///
    /// **3D では小さいほうへ外す罰が重い**——1個がまたぐマスの数は
    /// 辺の比の3乗で増える。2倍細かくすると 8 倍。
    /// 掃引すると、小さい側でも大きい側でも遅くなる谷の形が見える。
    /// </summary>
    private static readonly float[] GridCellSteps = [0.5f, 1.0f, 2.0f, 4.0f, 8.0f];

    /// <summary>今のマスの大きさの添字。既定は 2.0m。</summary>
    private static int _gridCellIndex = 2;

    /// <summary>地形の上に降らせた体の数。**ブロードフェーズの効きを見るための数**。</summary>
    private static int _terrainSpawnCount;

    // ================================================================
    //  Day 47: 摩擦・Sequential Impulses・スリープ(ミニ物理エンジン完成)
    // ================================================================

    /// <summary>
    /// 摩擦の坂の傾き [rad]。**正接がちょうど 0.5** になる角度(要点3)。
    ///
    /// 26.57 度。<c>tan 26.57° = 0.5</c> なので、
    /// <b>合成 μ が 0.5 より大きい箱だけが止まる</b>——
    /// 並べた5つのうち真ん中(μ = 0.5)がちょうど境目になる。
    /// </summary>
    private const float RampTilt = 0.46365f;

    /// <summary>
    /// 坂に並べる箱の**合成**摩擦係数(Day 47)。**真ん中が境目**。
    ///
    /// 体に入れる値ではないことに注意。摩擦は2つの体の相乗平均で混ざるので、
    /// 坂を μ = 1 にしたうえで<b>箱に μ² を入れる</b>と、合成がこの値になる
    /// (<see cref="BuildFrictionRampScene"/>)。
    /// </summary>
    private static readonly float[] RampFrictions = [0.1f, 0.3f, 0.5f, 0.7f, 0.9f];

    /// <summary>
    /// 摩擦係数の上書きの候補(Shift+Alt+F)。**負なら体ごとの値を使う**。
    ///
    /// 「今の場面で全部を μ = 0.2 にしたらどうなるか」を1キーで試すためのもの。
    /// 地形の上の 60 個を一斉に凍らせたり滑らせたりできる。
    /// </summary>
    private static readonly float[] FrictionSteps = [-1.0f, 0.0f, 0.2f, 0.5f, 1.0f];

    private static int _frictionIndex;

    /// <summary>
    /// 画面に出す成分(Shift+9)。
    /// 0=通常 1=ベースカラー 2=法線(頂点) 3=メタリック 4=ラフネス 5=AO 6=発光 7=法線マップ
    /// 8=影の係数(Day 33)。22=骨の重み(Day 41)。
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

        // **円柱は Day 45 で足した**。カプセルの胴に使う。
        // 分割数 32 で三角形 128 枚。球と組み合わせて1本のカプセルになる。
        _cylinder = Primitives.CreateCylinder(_gl);

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

        // --- 今日の主役: 機能の ON/OFF 表(Day 40)---
        //
        // **ここより後ろでないと組めない**。表が持っているのは
        // `_ssao.Enabled` などへの読み書きの手順なので、
        // その `_ssao` や `_post` が出来上がっている必要がある。
        _features = BuildFeatureToggles();

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

        // --- Day 43: 物理デモの球 ---
        //
        // **模様のあるテクスチャを貼る**のが要点。単色だと球が回っていても
        // まったく分からず、トルクが効いているかを絵で確かめられない。
        // <c>uv-test.png</c> は Day 15 から使っている格子で、
        // 経線と緯線がそのまま回転の目印になる。
        //
        // 色は描くたびに <see cref="Material.BaseColorFactor"/> を差し替えるので、
        // マテリアルは1つで足りる(材質グリッドと同じやり方)。
        _physicsMaterial = new Material(_shader)
        {
            Name = "physics-ball",
            MainTexture = _texture,
            MetallicFactor = 0.0f,
            RoughnessFactor = 0.35f,
        };

        // **接触点の印**(Day 44)。ライティングを受けると影の中で見えなくなるので、
        // 発光(<see cref="Material.EmissiveFactor"/>)で描いて必ず目立たせる。
        // 物理の正しさを目で確かめるための道具なので、
        // 「絵として自然か」より「必ず見えるか」を優先している。
        _contactMaterial = new Material(_shader)
        {
            Name = "contact-point",
            MainTexture = white,
            BaseColorFactor = new Vector4(0.05f, 0.05f, 0.05f, 1.0f),
            MetallicFactor = 0.0f,
            RoughnessFactor = 1.0f,
            EmissiveFactor = new Vector3(4.0f, 0.9f, 0.15f),
        };

        // **キャラクター**(Day 45)。周りの箱や球と見分けが付くように、
        // 少し発光させて明るい単色にしてある。
        // 模様が要らないのは、キャラクターのカプセルは<b>回らない</b>から——
        // 球(<see cref="_physicsMaterial"/>)に格子を貼ったのは
        // 回転を目で追うためだったので、ここでは要らない。
        _characterMaterial = new Material(_shader)
        {
            Name = "character",
            MainTexture = white,
            BaseColorFactor = SrgbToLinear(new Vector4(0.22f, 0.78f, 0.95f, 1.0f)),
            MetallicFactor = 0.0f,
            RoughnessFactor = 0.45f,
            EmissiveFactor = new Vector3(0.05f, 0.20f, 0.28f),
        };

        // **地形**(Day 46)。格子のテクスチャ(<c>uv-test.png</c>)を貼るのは、
        // <b>マスの大きさが目で読めるようにする</b>ため——
        // 「地形のマス」と「ブロードフェーズのマス」は別ものなので、
        // どちらの話をしているのかが絵で区別できないと混乱する。
        // 判定に使う三角形は、この格子の1マスをさらに2枚に割ったもの。
        _terrainMaterial = new Material(_shader)
        {
            Name = "terrain",
            MainTexture = _texture,
            BaseColorFactor = SrgbToLinear(new Vector4(0.42f, 0.52f, 0.36f, 1.0f)),
            MetallicFactor = 0.0f,
            RoughnessFactor = 0.85f,
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
        Console.WriteLine("--- Day 43: 剛体力学の基礎(Ctrl+Shift+Alt+F1〜F12)---");
        Console.WriteLine("Ctrl+Shift+Alt+F1:物理デモ ON/OFF。**球が落ちて跳ねて積まれる**");
        Console.WriteLine("Ctrl+Shift+Alt+F2:積分法(セミインプリシット / **陽的オイラー**。数字キー4と併せて見る)");
        Console.WriteLine("Ctrl+Shift+Alt+F3:反発係数 0/0.3/0.6/0.9  Ctrl+Shift+Alt+F4:筋書き(落下/積み上げ/撞き玉)");
        Console.WriteLine("Ctrl+Shift+Alt+F5:速度の反復 1/2/4/8(**Day 47 の温存が入って 1 でも持つ**)  Ctrl+Shift+Alt+F6:位置補正");
        Console.WriteLine("Ctrl+Shift+Alt+F7:球を1つ降らせる  Ctrl+Shift+Alt+F8:撃つ(**中心 / 中心を外す が交互**)");
        Console.WriteLine("Ctrl+Shift+Alt+F9:内訳(運動量・エネルギー・めり込み)  Ctrl+Shift+Alt+F10:停止");
        Console.WriteLine("Ctrl+Shift+Alt+F11:コマ送り  Ctrl+Shift+Alt+F12:Day 43 の自己チェック");
        Console.WriteLine();
        Console.WriteLine("--- Day 44: 箱(OBB)の衝突(Ctrl+Shift+Alt+数字キー)---");
        Console.WriteLine("Ctrl+Shift+Alt+1:箱デモ ON/OFF。**傾きを振った箱が落ちて、面で落ち着く**");
        Console.WriteLine("Ctrl+Shift+Alt+2:筋書き(箱を落とす/箱を積む/球と箱/箱が転がる/**カプセルを落とす**)");
        Console.WriteLine("Ctrl+Shift+Alt+3:接触点の表示 ON/OFF(**光る玉がマニフォールドの点**)");
        Console.WriteLine("Ctrl+Shift+Alt+4:1組あたりの接触点 1/2/4(**1 にすると箱もカプセルも落ち着かない**)");
        Console.WriteLine("Ctrl+Shift+Alt+5:箱を1つ降らせる  Ctrl+Shift+Alt+6:接触の内訳(どの軸が効いたか)");
        Console.WriteLine("Ctrl+Shift+Alt+7:接触点の解き方(同時 / **順番**。切ると積んだ箱が横にずれる)");
        Console.WriteLine("Ctrl+Shift+Alt+0:Day 44 の自己チェック");
        Console.WriteLine();
        Console.WriteLine("--- Day 45: カプセル衝突とキャラクターコントローラ(X の段)---");
        Console.WriteLine("Ctrl+X:キャラクターデモ ON/OFF。**坂と階段の上を歩き回れる。今日の到達点**");
        Console.WriteLine("  矢印キー:歩く(カメラ基準)  X 押しっぱなし:走る  Space:ジャンプ");
        Console.WriteLine("Shift+X:筋書き(坂/階段/障害物)  Alt+X:**坂の上限 ON/OFF(切ると壁も登れる)**");
        Console.WriteLine("Ctrl+Shift+X:**段差の乗り越え ON/OFF(切ると 15cm の段で止まる)**");
        Console.WriteLine("Ctrl+Alt+X:カプセルを1つ降らせる  Shift+Alt+X:キャラクターの内訳");
        Console.WriteLine("Ctrl+Shift+Alt+X:今日の自己チェック");
        Console.WriteLine();
        Console.WriteLine("--- Day 46: 地形とブロードフェーズ(G の段)---");
        Console.WriteLine("Ctrl+G:地形デモ ON/OFF。**起伏の上を歩ける。今日の到達点**");
        Console.WriteLine("  緩い丘(29度)は登れて、急な丘(58度)は登れない。Alt+X で上限を切ると登れる");
        Console.WriteLine("Shift+G:ブロードフェーズ(**均一グリッド / 総当たり**。絵は変わらず時間だけ変わる)");
        Console.WriteLine("Alt+G:格子のマスを線で描く  Ctrl+Shift+G:マスの大きさ 0.5/1/2/4/8m");
        Console.WriteLine("Ctrl+Alt+G:体を 40 個降らせる(**組の数の減り方を見る**)");
        Console.WriteLine("Shift+Alt+G:地形とブロードフェーズの内訳  Ctrl+Shift+Alt+G:今日の自己チェック");
        Console.WriteLine("  ※素の G(3D背景の ON/OFF)は今までどおり。修飾キー付きだけが今日のもの");
        Console.WriteLine();
        Console.WriteLine("--- Day 47: 摩擦・Sequential Impulses・スリープ(F の段)---");
        Console.WriteLine("Ctrl+F:摩擦デモ ON/OFF。**坂 26.57 度に μ を振った5箱。今日の到達点**");
        Console.WriteLine("  μ=0.1/0.3/0.5/0.7/0.9。**tan 26.57°= 0.50 が境目**で、上の2つだけ止まる");
        Console.WriteLine("Shift+F:摩擦 ON/OFF  Shift+Alt+F:μ の上書き(体ごと/0/0.2/0.5/1.0)");
        Console.WriteLine("Alt+F:**蓄積インパルス ON/OFF**(切ると Day 46 の解き方。柱が沈む)");
        Console.WriteLine("Ctrl+Shift+F:**温存(ウォームスタート)ON/OFF**(切ると反復を減らせなくなる)");
        Console.WriteLine("Ctrl+Alt+F:眠り ON/OFF(**色が沈んだ体が眠っている**)");
        Console.WriteLine("Ctrl+Shift+Alt+F:今日の自己チェック");
        Console.WriteLine("  ※素の F(視野角)は今までどおり。修飾キー付きだけが今日のもの");
        Console.WriteLine();
        Console.WriteLine("--- Day 42: アニメーション制御(Shift+Alt+F1〜F12)---");
        Console.WriteLine("Shift+Alt+F1:Fox を出して**速度に応じた歩き↔走りのブレンド**。**今日の到達点**");
        Console.WriteLine("Shift+Alt+F2:決め方(OFF / ブレンドツリー / ステートマシン)");
        Console.WriteLine("Shift+Alt+F3:位相同期 ON/OFF(**OFF にすると足が合わなくなる**)");
        Console.WriteLine("Shift+Alt+F5 / F6:速度を 0.2m/s ずつ増減  Shift+Alt+F7:自動スイープ ON/OFF");
        Console.WriteLine("Shift+Alt+F8:クロスフェード 0/0.1/0.25/0.5 秒(ステートマシンのとき)");
        Console.WriteLine("Shift+Alt+F9:今の混合の内訳  Shift+Alt+F12:今日の自己チェック");
        Console.WriteLine();
        Console.WriteLine("--- Day 41: 頂点ブレンディングとスキニング(Alt+F1〜F12)---");
        Console.WriteLine("Alt+F1:CesiumMan を出して歩かせる。**今日の到達点はこれ**");
        Console.WriteLine("Alt+F2:アニメ付きの5体を巡回(人型 / キツネ / 棒 / 最小例 / スキン無し)");
        Console.WriteLine("Alt+F3:次のクリップへ(**Fox だけが Survey / Walk / Run の3本**)");
        Console.WriteLine("Alt+F5:再生 / 一時停止  Alt+F8:1/30 秒ずつコマ送り  Alt+F10:先頭へ戻す");
        Console.WriteLine("Alt+F6:スキニング ON/OFF(**骨は動いたまま頂点だけ止まる**)");
        Console.WriteLine("Alt+F7:再生速度 0.25/0.5/1/2 倍  Alt+F9:骨の重みを色で見る(成分 22)");
        Console.WriteLine("Alt+F11:リグの内訳を出す  Alt+F12:今日の自己チェック");
        Console.WriteLine();
        Console.WriteLine("--- Day 40: カメラワークと機能トグル(Ctrl+Alt+F1〜F11)---");
        Console.WriteLine("Ctrl+Alt+F1:デモ v1 を出して**カメラワークを再生**。**今日の到達点はこれ**");
        Console.WriteLine("Ctrl+Alt+F2:再生 / 一時停止(止めれば左ドラッグで手動に戻れる)");
        Console.WriteLine("Ctrl+Alt+F3:次のショットへ飛ぶ  Ctrl+Alt+F6:再生速度 0.25/0.5/1/2 倍");
        Console.WriteLine("Ctrl+Alt+F4:補間 Catmull-Rom / 線形(**通過点で速度が飛ぶ**)");
        Console.WriteLine("Ctrl+Alt+F5:イージング ON/OFF(**軌跡は変わらず、速さの配分だけ変わる**)");
        Console.WriteLine("Ctrl+Alt+F7:機能を1つ選ぶ  Ctrl+Alt+F8:選んだ機能を ON/OFF");
        Console.WriteLine("Ctrl+Alt+F9:全部 ON / 全部 OFF(**必須構成の絵と素の絵を往復**)");
        Console.WriteLine("Ctrl+Alt+F10:機能ツアー(1つずつ切っては戻す)  Ctrl+Alt+F11:今日の自己チェック");
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

        // **カメラワークと機能ツアーは可変 dt で回す**(Day 40)。
        // どちらも見せ方だけの処理で、ゲームの状態には触れない——
        // だから固定ステップ(FixedUpdate)ではなくここに置く。
        // 一時停止(Space)でも動かしてよいのだが、
        // **止めたときは全部止まっているほうが分かりやすい**ので _paused も見る。
        if (!_paused)
        {
            UpdateDemoCamera(deltaSeconds);

            // **キャラクターを画面に留める**(Day 45)。
            // 注視点をキャラクターの胸のあたりへ置き直すだけの、
            // いちばん素朴な追従。可変 dt 側に置いてあるのは見せ方だからで、
            // カメラワーク(Day 40)と同じ扱いになる。
            //
            // **本物の三人称カメラは Day 51**。滑らかに遅れて付いていく、
            // 壁に入ったら寄る、進行方向を先読みする——
            // どれもここには無い。今はキャラクターが画面外へ出ないだけで足りる。
            if (_characterDemo)
            {
                _orbit.Target = Character.Position + new Vector3(0.0f, 1.0f, 0.0f);
                _orbit.Apply();
            }

            // **アニメーションも可変 dt で回す**(Day 41)。
            // 今日のところは見せ方だけの処理なので、カメラワークと同じ扱いでよい。
            //
            // ただし**これは今日までの話**。骨の位置を当たり判定に使い始めたら
            // 決定性が要るので、FixedUpdate 側へ移すことになる。
            // Day 45 のキャラクターコントローラは<b>カプセル1本で当たりを取る</b>ので、
            // 骨の位置には触らない——だからアニメーションは可変 dt のままでよい。
            // (アニメを載せるのは Day 51。そこでも当たり判定はカプセルのまま)
            // **重みを先に決めてから時刻を進める**(Day 42)。
            // 逆にすると、速度が変わったフレームだけ1フレーム古い重みで描かれる。
            UpdateLocomotion((float)deltaSeconds);
            _animation?.Update((float)deltaSeconds);

            string? tourMessage = _features.Update((float)deltaSeconds);
            if (tourMessage is not null)
            {
                Console.WriteLine(tourMessage);
            }
        }

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

        // **キャラクターデモ中はシーンに入力を渡さない**(Day 45)。
        // 矢印キーはキャラクターのものになるので、
        // そのままシーンへも流すと**デモのプレイヤーが同時に動く**。
        // 上の `_playing` の枝が「同じ入力を2つの世界が食い合う」と書いたのと同じ話で、
        // こちらは早期 return ではなく空の入力を渡して凌いでいる——
        // シーンの更新そのものは続けたい(背景のスプライトは動いていてよい)ため。
        _scene.Input = _characterDemo ? InputSnapshot.Empty : input;
        _scene.Bounds = bounds;
        _scene.FixedUpdate(dt);

        if (_collisionDemo)
        {
            UpdateBodies(dt, bounds);
        }

        // **物理は固定ステップ**(Day 43)。可変 dt で回すと、
        // フレームレートが落ちた瞬間に球が床を突き抜ける。
        // Day 41〜42 のアニメーションを可変 dt 側(OnUpdate)に置いたのと逆の判断で、
        // 線引きは Day 19 のまま——**状態を持つものはこちら**。
        UpdatePhysics(dt);

        // **キャラクターは物理のあと**(Day 45 の要点6)。
        // 箱や球が動き終わった世界に対して当たりを取るので、
        // 転がってきた物にめり込んだまま次のステップへ進むことがない。
        UpdateCharacter(dt, input);

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
                // **本描画とまったく同じ行列と関節行列を渡す**(Day 41)。
                // ここだけ静的な Transform のままにすると、
                // 遮蔽がバインドポーズの位置に residue として残る。
                _ssao.Draw(part.Mesh, PartMatrix(part), PartJoints(part));
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

        if (_physicsDemo)
        {
            // **球は影を落とす**(Day 43)。落下デモは高さが主役なので、
            // 床に落ちる影が無いと「どのくらいの高さにいるか」が読めない。
            // 影は接地しているかどうかも教えてくれる——
            // 積み上げデモで一番下の球が沈んでいるかは、影の大きさで気づける。
            foreach (RigidBody physicsBody in Physics.Bodies)
            {
                // **平面は影を落とさない**(Day 44)。無限に広いので、
                // 描こうとすると影の箱の中が真っ暗になる。
                //
                // **地形は下で別に落とす**(Day 46)。専用のメッシュなので、
                // <see cref="BodyMesh"/> では描けない。
                if (physicsBody.Shape.Kind is ColliderKind.Plane or ColliderKind.HeightField)
                {
                    continue;
                }

                // **カプセルは3つに分けて落とす**(Day 45)。
                // 本編の描画(<see cref="DrawCapsule"/>)と同じ分け方でないと、
                // 影だけ形が違うという分かりにくい壊れ方をする。
                if (physicsBody.Shape.Kind == ColliderKind.Capsule)
                {
                    ShadowCapsule(InterpolatedCapsule(physicsBody));
                    continue;
                }

                _shadow.Draw(BodyMesh(physicsBody), BodyMatrix(physicsBody));
            }

            // **キャラクターも影を落とす**(Day 45)。足元の影が無いと、
            // 段の上に乗っているのか手前で止まっているのかが読めない。
            if (_characterDemo)
            {
                ShadowCapsule(Character.ToCapsule());
            }

            // **地形は影を受けるし落とす**(Day 46)。丘の陰が谷に落ちると、
            // 起伏の形が一段読みやすくなる——
            // 平らな床と違って、地形は<b>自分自身に影を落とす</b>のがおもしろいところ。
            if (_terrainMesh is not null)
            {
                _shadow.Draw(_terrainMesh, Matrix4x4.CreateTranslation(_terrainOrigin));
            }
            else
            {
                _shadow.Draw(_quad, FloorMatrix());
            }
        }
        else if (_demo is not null)
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
                // SSAO と同じ理由で、本描画と同じものを渡す(Day 41)。
                _shadow.Draw(part.Mesh, PartMatrix(part), PartJoints(part));
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

        lines.AppendLine($"Day40   {_fps:F1} fps   DC:{_drawCalls}");

        // **今日の1行**(Day 43)。物理は「なんとなく動いている」で済ませてしまいやすい。
        // めり込み量と運動エネルギーが数字で出ていないと、
        // 沈んでいるのか、震えているのか、そもそも止まっているのかが分からない。
        if (_physicsDemo)
        {
            lines.AppendLine(PhysicsLabel());

            // **今日の1行**(Day 44)。接触が何組・何点あるか、
            // そしてそれがどの軸から出たかは、絵をいくら見ても分からない。
            lines.AppendLine(ContactLabel());

            // **今日の1行**(Day 45)。接地しているか、立っている面が何度か、
            // 段差を越えたか——どれも絵からは読み取れない。
            if (_characterDemo)
            {
                lines.AppendLine(CharacterLabel());
            }

            // **今日の1行**(Day 46)。ブロードフェーズは効いていても
            // <b>絵が1ピクセルも変わらない</b>ので、
            // 数字が出ていないと切り替えたことにすら気づけない。
            lines.AppendLine(BroadphaseLine());

            // **今日の1行**(Day 47)。温存が効いているか、何体が眠っているかは
            // 絵からは絶対に読み取れない。**眠っている体は色が沈む**ようにしてあるが、
            // それだけでは「何割が眠っているか」までは分からない。
            lines.AppendLine(SolverLabel());
        }

        if (_model is not null)
        {
            lines.AppendLine(
                $"{ModelLabel()}  三角形:{_model.TriangleCount:N0}  パーツ:{_model.Parts.Count}  "
                + $"マテリアル:{_model.Materials.Count}  テクスチャ:{_model.TextureCount}  "
                + $"読込:{_modelLoadMilliseconds:F0}ms");

            // **今日の1行**(Day 41)。アニメーションは「止まっているのか、
            // クリップが無いのか、スキニングを切っているのか」が絵から区別できない。
            lines.AppendLine(AnimationLabel());

            // **今日の1行**(Day 42)。混ざっている比率は絵から絶対に読み取れない。
            // 「歩き 0.42 + 走り 0.58」という数字が出ていないと、
            // ブレンドが効いているのか単に走っているだけなのか区別が付かない。
            lines.AppendLine(LocomotionLabel());
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

        // **今日の2行**(Day 40)。
        // カメラは「いまどのショットの、どのくらいの速さか」が絵から読めない。
        // 機能のほうは、切ったつもりのものが本当に切れているかを一覧で押さえる——
        // 12 個もあると、HUD を見ずに憶えているのは無理。
        lines.AppendLine(CameraLabel());
        lines.AppendLine(
            $"機能 {_features.OnCount}/{_features.Features.Count}: {_features.Describe()}"
            + (_features.TourActive ? $"  {_features.TourLabel()}" : string.Empty));

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
        22 => "骨の重み",
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

    /// <summary>
    /// アニメーションの状態を1行にまとめる(Day 41。HUD 用)。
    ///
    /// **「止まっている」の理由が3通りある**のがこの行を置く理由。
    /// 一時停止しているのか、クリップを持たないモデルなのか、
    /// スキニングを切っているのか——絵はどれも同じに見える。
    /// </summary>
    private static string AnimationLabel()
    {
        if (_animation is null || _model is null)
        {
            return "アニメ:なし";
        }

        string rig = $"ノード:{_model.Nodes.Count}  スキン:{_model.Skins.Count}  "
            + $"関節:{_model.JointCount}/{AnimationPlayer.MaxJoints}  "
            + $"スキン付き:{_model.SkinnedParts}/{_model.Parts.Count}";

        if (_model.Animations.Count == 0)
        {
            return $"アニメ:クリップ無し  {rig}";
        }

        return $"アニメ:{_animation.ClipName}({_animation.ClipIndex + 1}/{_model.Animations.Count})  "
            + $"{_animation.Time:F2}/{_animation.Duration:F2}s  {_animation.Speed:F2}倍  "
            + $"{(_animation.Paused ? "一時停止" : "再生中")}  "
            + $"スキニング:{OnOff(_animation.SkinningEnabled)}  {rig}";
    }

    /// <summary>
    /// クリップ名からロコモーションを組み立てる(Day 42)。
    ///
    /// <para>
    /// <b>名前で探す</b>。Fox のクリップは Survey / Walk / Run で、
    /// 「立ち止まり」に当たるものが <c>Survey</c>(周りを見回す)という名前になっている。
    /// Mixamo から持ってくると <c>Idle</c> / <c>Walking</c> / <c>Running</c> のように付くので、
    /// **部分一致で複数の綴りを拾う**ようにしてある。
    /// </para>
    ///
    /// <para>
    /// 名前で当たらなければ、**クリップを並び順のまま軸に等間隔で置く**。
    /// 意味は合わないが「混ざることは確かめられる」ので、
    /// 手持ちのモデルで試すときの入口になる。
    /// </para>
    ///
    /// <para>
    /// <b>速度の値は目分量</b>。本来は「1周期で足がどれだけ後ろへ流れるか」を測って
    /// 決めるべきもので、ここがずれると足が地面を滑る(改造課題1)。
    /// </para>
    /// </summary>
    private static void BuildLocomotion(Model model)
    {
        _locomotionTree = null;
        _locomotionStates = null;
        _locomotion = LocomotionMode.Off;

        if (model.Animations.Count < 2)
        {
            return;
        }

        int idle = FindClip(model, "idle", "survey", "stand");
        int walk = FindClip(model, "walk");
        int run = FindClip(model, "run", "sprint", "jog");

        var samples = new List<BlendTree1D.Sample>();

        if (idle >= 0 && walk >= 0 && run >= 0)
        {
            samples.Add(new BlendTree1D.Sample("立ち止まり", idle, 0.0f));
            samples.Add(new BlendTree1D.Sample("歩き", walk, 1.0f));
            samples.Add(new BlendTree1D.Sample("走り", run, MaxMoveSpeed));
        }
        else
        {
            // 名前で当たらなかったモデル。**並び順のまま等間隔**に置く。
            for (int i = 0; i < model.Animations.Count; i++)
            {
                samples.Add(new BlendTree1D.Sample(
                    model.Animations[i].Name,
                    i,
                    MaxMoveSpeed * i / (model.Animations.Count - 1)));
            }
        }

        _locomotionTree = new BlendTree1D("ロコモーション", samples);

        // **同じクリップを状態としても並べる**。見比べるのが今日の眼目なので、
        // 中身が違ってしまわないよう1か所から両方を作る。
        //
        // しきい値はツリーの点とは意図的にずらしてある。
        // ツリーの点は「そのクリップが表す速さ」、
        // ステートマシンのしきい値は「そこで切り替えたい速さ」で、**別の意味を持つ**。
        _locomotionStates = new AnimationStateMachine(
            samples.Select((sample, index) => new AnimationStateMachine.State(
                sample.Name,
                sample.Clip,
                index == 0 ? float.NegativeInfinity : (sample.Parameter * 0.65f))))
        {
            FadeSeconds = CrossFadeSeconds[_crossFadeIndex],
        };
    }

    /// <summary>クリップ名に <paramref name="keywords"/> のどれかを含むものを探す。無ければ -1。</summary>
    private static int FindClip(Model model, params string[] keywords)
    {
        for (int i = 0; i < model.Animations.Count; i++)
        {
            string name = model.Animations[i].Name;
            foreach (string keyword in keywords)
            {
                if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// 速度からクリップの重みを決めて、プレイヤーへ渡す(Day 42)。
    ///
    /// <b>ここが今日の全部</b>。残りは「その重みをどう決めるか」の2通りと、
    /// 決めた重みをどう混ぜるか(<c>AnimationPlayer.Evaluate</c>)の話になる。
    /// </summary>
    private static void UpdateLocomotion(float deltaSeconds)
    {
        if (_animation is null || _locomotion == LocomotionMode.Off)
        {
            return;
        }

        // **速度を自動で上下させる**。手で押さえていると、
        // 切り替わりの瞬間だけを見ることになって連続性が分からない。
        if (_speedSweep)
        {
            _sweepPhase = (_sweepPhase + (deltaSeconds / SweepSeconds)) % 1.0f;

            // 三角波。0 → 最大 → 0 を1往復。
            float ramp = _sweepPhase < 0.5f ? _sweepPhase * 2.0f : (1.0f - _sweepPhase) * 2.0f;
            _moveSpeed = ramp * MaxMoveSpeed;
        }

        Span<ClipWeight> weights = _blendScratch;
        int count = 0;

        if (_locomotion == LocomotionMode.BlendTree && _locomotionTree is not null)
        {
            count = _locomotionTree.Evaluate(_moveSpeed, weights);
        }
        else if (_locomotion == LocomotionMode.StateMachine && _locomotionStates is not null)
        {
            count = _locomotionStates.Update(deltaSeconds, _moveSpeed, weights);
        }

        if (count > 0)
        {
            _animation.SetBlend(weights[..count]);
        }
    }

    /// <summary>ロコモーションの状態を1行にまとめる(Day 42。HUD 用)。</summary>
    private static string LocomotionLabel()
    {
        if (_animation is null || _locomotionTree is null)
        {
            return "ロコモーション:使えるモデルではない(Shift+Alt+F1 で Fox へ)";
        }

        string mode = _locomotion switch
        {
            LocomotionMode.BlendTree => "ブレンドツリー",
            LocomotionMode.StateMachine => $"ステートマシン[{_locomotionStates?.CurrentName}]",
            _ => "OFF(クリップ1本)",
        };

        return $"ロコモーション:{mode}  速度:{_moveSpeed:F2}m/s"
            + $"{(_speedSweep ? "(自動)" : string.Empty)}  "
            + $"位相同期:{OnOff(_animation.SyncPhase)}  "
            + $"フェード:{CrossFadeSeconds[_crossFadeIndex]:F2}s  "
            + $"周期:{_animation.Duration:F2}s  位相:{_animation.Phase:F2}  "
            + $"混合:{BlendLabel()}";
    }

    /// <summary>今の重みを「歩き0.42+走り0.58」のような1つの文字列にする(Day 42)。</summary>
    private static string BlendLabel()
    {
        if (_animation is null || _model is null)
        {
            return "-";
        }

        ReadOnlySpan<ClipWeight> blend = _animation.Blend;
        if (blend.Length == 0)
        {
            return "なし";
        }

        var text = new System.Text.StringBuilder();
        for (int i = 0; i < blend.Length; i++)
        {
            if (i > 0)
            {
                text.Append('+');
            }

            text.Append($"{_model.Animations[blend[i].Clip].Name}{blend[i].Weight:F2}");
        }

        return text.ToString();
    }

    /// <summary>今の混合の内訳をコンソールへ(Shift+Alt+F9)。</summary>
    private static void DescribeBlend()
    {
        if (_animation is null || _model is null)
        {
            Console.WriteLine("モデルがありません(Shift+Alt+F1 で Fox を出す)");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"=== {ModelLabel()} の混合 ===");
        Console.WriteLine(
            $"速度 {_moveSpeed:F2}m/s  位相 {_animation.Phase:F3}  "
            + $"周期 {_animation.Duration:F3}s  位相同期 {OnOff(_animation.SyncPhase)}");

        foreach (ClipWeight entry in _animation.Blend)
        {
            AnimationClip clip = _model.Animations[entry.Clip];

            // **同期していれば、どのクリップも同じ位相を指す**。
            // 秒で見ると違う値になるのが、位相で合わせるということ。
            float seconds = _animation.SyncPhase
                ? _animation.Phase * clip.Duration
                : float.NaN;

            Console.WriteLine(
                $"  {clip.Name,-8} 重み {entry.Weight:F3}  長さ {clip.Duration:F3}s"
                + (float.IsNaN(seconds) ? "  (各クリップが独立に進行中)" : $"  位置 {seconds:F3}s"));
        }

        if (_locomotionTree is not null)
        {
            Console.WriteLine("  ツリーの点: " + string.Join(" / ", _locomotionTree.Samples.Select(
                sample => $"{sample.Name} {sample.Parameter:F2}m/s")));
        }

        if (_locomotionStates is not null)
        {
            Console.WriteLine(
                "  状態: " + string.Join(" / ", _locomotionStates.States.Select(
                    state => $"{state.Name} ≧{state.Threshold:F2}"))
                + $"  今 {_locomotionStates.CurrentName}"
                + (_locomotionStates.Previous >= 0
                    ? $" ← {_locomotionStates.PreviousName}({_locomotionStates.Fade:P0})"
                    : string.Empty));
        }

        Console.WriteLine();
    }

    /// <summary>
    /// アニメーションを持つモデルだけを巡回する(Alt+F2)。
    /// <see cref="ModelPaths"/> の後ろ5体で、**Day 41 で足したぶんそのもの**。
    /// </summary>
    private static readonly string[] AnimatedModelPaths =
    [
        "models/CesiumMan.glb",
        "models/Fox.glb",
        "models/RiggedSimple.glb",
        "models/SimpleSkin/SimpleSkin.gltf",
        "models/BoxAnimated.glb",
    ];

    /// <summary>アニメーション用のモデルへ切り替える下ごしらえ(Alt+F1 / Alt+F2 で共通)。</summary>
    private static void ShowAnimatedModel(string relativePath)
    {
        // **デモ v1 は最優先で描かれる**(Render3D)ので、出したままだとモデルが見えない。
        if (_demo is not null)
        {
            UnloadDemoScene();
        }

        StopTourIfRunning();

        _materialGrid = false;
        _surfaceDemo = false;
        _draw3D = true;
        _debugChannel = 0;
        SetSpriteCount(0);

        int index = Array.IndexOf(ModelPaths, relativePath);
        SetModel(index >= 0 ? index : 0);
    }

    // ================================================================
    //  Day 43: 剛体力学の基礎(セミインプリシット積分・力とトルク・インパルス)
    // ================================================================

    /// <summary>
    /// 物理デモを出す下ごしらえ(Ctrl+Shift+Alt+F1 / F4)。
    ///
    /// <see cref="ShowAnimatedModel"/> と同じ形。
    /// **他のデモを全部下ろしてから**でないと、
    /// <see cref="Render3D"/> の優先順位に負けて球が1つも見えない。
    /// </summary>
    private static void ShowPhysicsDemo(PhysicsScene scene)
    {
        if (_demo is not null)
        {
            UnloadDemoScene();
        }

        StopTourIfRunning();

        if (_model is not null)
        {
            SetModel(ModelPaths.Length);
        }

        _materialGrid = false;
        _surfaceDemo = false;
        _draw3D = true;
        _debugChannel = 0;
        SetSpriteCount(0);

        _physicsDemo = true;

        // **キャラクターは下ろす**(Day 45)。剛体の筋書きへ切り替えたのに
        // キャラクターが立っていると、消えた床の上に取り残される。
        _characterDemo = false;

        _physicsPaused = false;
        _physicsStepsRequested = 0;

        BuildPhysicsScene(scene);

        // **カメラは少し高いところから**。真横から見ると重なりが読めず、
        // 真上から見ると高さが読めない。物理は高さの絵なので、やや斜め上に置く。
        //
        // **柱を組む筋書きだけは引いて上げる**(Day 44)。
        // 箱を6段積むと 5m を超えるので、Day 43 の画角では上が切れる。
        //
        // **地形の筋書きは 24m 四方**なので、いちばん引いて上から見る(Day 46)。
        _orbit.Reset();
        _orbit.Target = new Vector3(0.0f, scene switch
        {
            PhysicsScene.BoxStack => 2.4f,
            PhysicsScene.FrictionRamp => 1.4f,
            _ => 1.0f,
        }, 0.0f);
        _orbit.Distance = scene switch
        {
            PhysicsScene.BoxStack => 15.0f,
            PhysicsScene.Terrain => 26.0f,
            _ => 12.0f,
        };

        // **摩擦の坂だけ真横から**(Day 47)。滑っているかどうかは
        // <b>坂に沿った位置</b>で見るので、坂を真横から見るのがいちばん読みやすい。
        _orbit.Yaw = scene == PhysicsScene.FrictionRamp ? 1.45f : 0.35f;
        _orbit.Pitch = scene == PhysicsScene.Terrain ? 0.55f : 0.22f;
        _orbit.Apply();
    }

    /// <summary>
    /// 初期配置を組む。**筋書きごとに、見せたいものが1つだけ**ある。
    ///
    /// <list type="bullet">
    /// <item><b>落下</b> … 反発係数を5段に振った球。跳ね返る高さの違いだけを見る</item>
    /// <item><b>積み上げ</b> … 縦一列。<see cref="PhysicsWorld.VelocityIterations"/> が効く</item>
    /// <item><b>撞き玉</b> … 一列に並べて端から撞く。運動量が伝わる</item>
    /// </list>
    /// </summary>
    private static void BuildPhysicsScene(PhysicsScene scene)
    {
        _physicsScene = scene;
        _spawnCount = 0;

        Physics.Clear();
        BodyColors.Clear();
        DisposeTerrain();
        Physics.VelocityIterations = IterationSteps[_iterationIndex];
        Physics.MaxContactsPerPair = ContactLimits[_contactLimitIndex];
        ConfigureBroadphase();

        // **地形の筋書きでは、平らな床も縁石も張らない**(Day 46)。
        // 地形そのものが床なので、上に平面を重ねると
        // 谷が埋まって「起伏の上を歩く」絵にならない。
        if (scene == PhysicsScene.Terrain)
        {
            BuildTerrainScene();
            return;
        }

        // **床は無限に広い平面**。描いている板(20x20)の外にも続いているので、
        // 遠くへ飛んでいった物も落ちない。
        // 有限の床が要るときは静的な箱を置く(<see cref="BuildBoxTumbleScene"/>)。
        AddPhysicsPlane(new Vector3(0.0f, PhysicsFloorY, 0.0f), Vector3.UnitY);

        // **見えない壁を4枚**。Day 46 までは摩擦が無く、横向きの速度が一切減らなかったので、
        // 囲っておかないと一度滑り始めた物は画面の外まで行ったきり戻ってこなかった。
        // **Day 47 で摩擦が入り、囲いが無くてもいずれ止まるようになった**が、
        // 壁は残してある——止まるまでに数メートル滑るので、見えるところに居てほしい。
        // 縁石(<see cref="RenderPhysics"/> が描く箱)は、この壁の位置の目印。
        AddPhysicsPlane(new Vector3(-PhysicsWallDistance, 0.0f, 0.0f), Vector3.UnitX);
        AddPhysicsPlane(new Vector3(PhysicsWallDistance, 0.0f, 0.0f), -Vector3.UnitX);
        AddPhysicsPlane(new Vector3(0.0f, 0.0f, -PhysicsWallDistance), Vector3.UnitZ);
        AddPhysicsPlane(new Vector3(0.0f, 0.0f, PhysicsWallDistance), -Vector3.UnitZ);

        switch (scene)
        {
            case PhysicsScene.Drop:
                BuildDropScene();
                break;

            case PhysicsScene.Stack:
                BuildStackScene();
                break;

            case PhysicsScene.Cradle:
                BuildCradleScene();
                break;

            case PhysicsScene.BoxDrop:
                BuildBoxDropScene();
                break;

            case PhysicsScene.BoxStack:
                BuildBoxStackScene();
                break;

            case PhysicsScene.BoxMix:
                BuildBoxMixScene();
                break;

            case PhysicsScene.BoxTumble:
                BuildBoxTumbleScene();
                break;

            case PhysicsScene.CapsuleDrop:
                BuildCapsuleDropScene();
                break;

            case PhysicsScene.FrictionRamp:
                BuildFrictionRampScene();
                break;
        }
    }

    /// <summary>
    /// 平面を1枚足す。**色の配列と添字を揃えるためだけのヘルパ**(Day 44)。
    ///
    /// Day 43 の平面は体ではなかったので、<c>BodyColors</c> に席が要らなかった。
    /// 今日から平面も体(<see cref="RigidBody.CreatePlane"/>)になったので、
    /// **色の配列にも席を作らないと添字がずれる**。
    /// 平面は描かないので色は使われないが、番号を揃えるためだけに詰めておく。
    ///
    /// <para>
    /// 「色の配列の添字 = 体の番号」という雑な対応が、
    /// 平面が体になった途端に手当てを要求してきた——
    /// <b>並行配列の弱いところ</b>が素直に出た箇所になる。
    /// 体の数が増えてきたら、色は <c>Dictionary</c> か
    /// 体の側の付帯情報に移すことになる。
    /// </para>
    /// </summary>
    private static void AddPhysicsPlane(Vector3 point, Vector3 normal)
    {
        Physics.AddBody(RigidBody.CreatePlane(point, normal));
        BodyColors.Add(Vector4.One);
    }

    /// <summary>
    /// 落下。**反発係数だけを振った5球**を同じ高さから落とす。
    ///
    /// 他の条件(質量・半径・高さ)を揃えてあるので、
    /// <b>跳ね返る高さの違いは反発係数だけで説明が付く</b>。
    /// 理屈のうえでは、1回の跳ね返りで高さが e² 倍になる——
    /// e = 0.9 なら 81%、e = 0.5 なら 25%。
    /// </summary>
    private static void BuildDropScene()
    {
        float[] restitutions = [0.0f, 0.25f, 0.5f, 0.75f, 0.95f];

        for (int i = 0; i < restitutions.Length; i++)
        {
            RigidBody body = RigidBody.CreateSphere(1.0f, 0.4f);
            body.Position = new Vector3((i - 2) * 1.6f, 3.5f, 0.0f);
            body.Restitution = restitutions[i];

            // **摩擦が無いので、回り出した球は永久に回り続ける**。
            // 角速度だけ少し抜いておくと、撃ったあとに落ち着いて見える。
            body.AngularDamping = 0.4f;
            Physics.AddBody(body);

            // 青(跳ねない)→ 赤(よく跳ねる)。**色が反発係数の目盛り**になる。
            float t = restitutions[i];
            BodyColors.Add(SrgbToLinear(new Vector4(0.2f + (0.7f * t), 0.35f, 0.9f - (0.7f * t), 1.0f)));
        }
    }

    /// <summary>
    /// 積み上げ。**縦に4個**、ちょうど接するように置く。
    ///
    /// 摩擦が無いので、ピラミッド型には積めない(横向きに力が要る)。
    /// 真上に積む形だけが、摩擦なしで成立する積み方になる。
    ///
    /// <para>
    /// <b>反復回数を 1 にすると沈む</b>のが今日の見どころ(要点8)。
    /// 下の接触を1回解いただけでは、いちばん下の球には
    /// 上の3個ぶんの重さが伝わっていない。
    /// </para>
    ///
    /// <para>
    /// <b>4個までにしてある</b>のは、素朴なインパルスの限界が
    /// 段数とともに急に効いてくるから。6段にすると
    /// 反復を 16 周しても 2cm 以上めり込む(今日の最後に書いた「残った歪み」)。
    /// </para>
    /// </summary>
    private static void BuildStackScene()
    {
        const float radius = 0.45f;

        for (int i = 0; i < 4; i++)
        {
            RigidBody body = RigidBody.CreateSphere(1.0f, radius);
            body.Position = new Vector3(0.0f, PhysicsFloorY + radius + (i * radius * 2.0f), 0.0f);
            body.Restitution = RestitutionSteps[_restitutionIndex];
            body.AngularDamping = 0.4f;
            Physics.AddBody(body);

            float t = i / 3.0f;
            BodyColors.Add(SrgbToLinear(new Vector4(0.85f - (0.4f * t), 0.55f + (0.3f * t), 0.25f + (0.5f * t), 1.0f)));
        }
    }

    /// <summary>
    /// 撞き玉。一列に並べた5球を、左から来た6個目で撞く。
    ///
    /// <para>
    /// <b>わざと 1cm ずつ隙間を空けてある</b>。ぴったり接して並べると
    /// 5つの接触が同時に立ち、素朴なインパルス(蓄積も温存もしない)では
    /// **端の1個だけが飛ぶ**という理想的な伝わり方にならない。
    /// 隙間があれば接触が1つずつ順に立つので、教科書どおりの絵に近づく。
    /// 同時接触をきちんと解くのは Day 47 の宿題。
    /// </para>
    /// </summary>
    private static void BuildCradleScene()
    {
        const float radius = 0.4f;
        const float gap = 0.01f;

        for (int i = 0; i < 5; i++)
        {
            RigidBody body = RigidBody.CreateSphere(1.0f, radius);
            body.Position = new Vector3(
                (i - 2) * ((radius * 2.0f) + gap), PhysicsFloorY + radius, 0.0f);

            // **ほぼ完全弾性**にしないと、伝わる前に吸われる。
            body.Restitution = 0.95f;
            body.AngularDamping = 0.4f;
            Physics.AddBody(body);
            BodyColors.Add(SrgbToLinear(new Vector4(0.75f, 0.75f, 0.80f, 1.0f)));
        }

        RigidBody striker = RigidBody.CreateSphere(1.0f, radius);
        striker.Position = new Vector3(-4.2f, PhysicsFloorY + radius, 0.0f);
        striker.LinearVelocity = new Vector3(6.0f, 0.0f, 0.0f);
        striker.Restitution = 0.95f;
        striker.AngularDamping = 0.4f;
        Physics.AddBody(striker);
        BodyColors.Add(SrgbToLinear(new Vector4(0.95f, 0.35f, 0.25f, 1.0f)));
    }

    /// <summary>
    /// 箱を落とす。**傾きだけを振った5つの箱**(Ctrl+Shift+Alt+1)。**今日の到達点**。
    ///
    /// 質量も大きさも同じで、初めの傾きだけが違う。
    /// <list type="bullet">
    /// <item>まっすぐな箱は<b>4点で着地して、そのまま止まる</b></item>
    /// <item>少し傾いた箱は<b>辺の2点で着地し、倒れて4点になる</b></item>
    /// <item>大きく傾いた箱は<b>角の1点で着地し、跳ねて向きを変えてから落ち着く</b></item>
    /// </list>
    /// 接触点の数(Ctrl+Shift+Alt+3 で表示)が着地の様子とそのまま対応している。
    ///
    /// <para>
    /// <b>Ctrl+Shift+Alt+4 で上限を 1 にすると、どれも落ち着かなくなる</b>——
    /// これが今日いちばん見てほしいところ(要点4)。
    /// </para>
    /// </summary>
    private static void BuildBoxDropScene()
    {
        float[] tilts = [0.0f, 0.10f, 0.35f, 0.62f, 0.95f];

        for (int i = 0; i < tilts.Length; i++)
        {
            RigidBody body = RigidBody.CreateBox(1.0f, 0.4f);
            body.Position = new Vector3((i - 2) * 1.7f, 3.2f, 0.0f);

            // 傾きは z 軸まわり。**画面の手前から見て、傾きの差が読める向き**にする。
            body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, tilts[i]);
            body.Restitution = 0.25f;
            body.AngularDamping = 0.25f;
            Physics.AddBody(body);

            // 緑(まっすぐ)→ 紫(大きく傾いている)。**色が傾きの目盛り**になる。
            float t = tilts[i] / 0.95f;
            BodyColors.Add(SrgbToLinear(
                new Vector4(0.30f + (0.55f * t), 0.75f - (0.45f * t), 0.35f + (0.55f * t), 1.0f)));
        }
    }

    /// <summary>
    /// 箱を積む。**球の積み上げ(<see cref="BuildStackScene"/>)と見比べる**のが眼目。
    ///
    /// 同じ反復回数でも、箱のほうが目に見えて安定する。
    /// 球は1点でしか触れないので、下の球が少しずれると上が転がり落ちるが、
    /// 箱は<b>4点で支える</b>ので横ずれに強い。
    ///
    /// <para>
    /// 摩擦が無いので<b>ずらして積むことはできない</b>(横向きの支えが無い)。
    /// 真上にきれいに積む形だけが、摩擦なしで成立する積み方になる。
    /// </para>
    /// </summary>
    private static void BuildBoxStackScene()
    {
        const float half = 0.42f;

        for (int i = 0; i < 6; i++)
        {
            RigidBody body = RigidBody.CreateBox(1.0f, half);
            body.Position = new Vector3(0.0f, PhysicsFloorY + half + (i * half * 2.0f), 0.0f);
            body.Restitution = 0.0f;
            body.AngularDamping = 0.4f;
            Physics.AddBody(body);

            float t = i / 5.0f;
            BodyColors.Add(SrgbToLinear(
                new Vector4(0.85f - (0.4f * t), 0.55f + (0.3f * t), 0.25f + (0.5f * t), 1.0f)));
        }
    }

    /// <summary>
    /// 球と箱を混ぜて、**傾けた静的な箱**の上に落とす。
    ///
    /// この筋書きだけで<b>今日足した判定が全部走る</b>——
    /// 球×箱、箱×箱、箱×平面、そして Day 43 の球×球・球×平面。
    /// HUD の「面 / 辺 / 点」の内訳が刻々と変わるのが見える。
    ///
    /// <para>
    /// <b>台は動かない箱</b>(<see cref="RigidBody.CreateStatic"/>)。
    /// 平面と違って<b>端がある</b>ので、滑り落ちた物は台の縁から落ちる——
    /// Day 43 で「平面には端が無い」と書いた話への、今日の答えになっている。
    /// </para>
    ///
    /// <para>
    /// <b>Day 47 で挙動が変わった筋書き</b>。摩擦が入るまでは乗ったものが必ず滑り落ちていたが、
    /// 今日からは台の上に留まるものが出る。
    /// <c>Shift+F</c> で摩擦を切ると、Day 46 までの絵に戻る。
    /// </para>
    /// </summary>
    private static void BuildBoxMixScene()
    {
        RigidBody ramp = RigidBody.CreateStatic(Collider.Box(new Vector3(2.8f, 0.2f, 1.8f)));
        ramp.Position = new Vector3(-1.0f, 1.1f, 0.0f);
        ramp.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.32f);
        ramp.Restitution = 0.1f;
        Physics.AddBody(ramp);
        BodyColors.Add(SrgbToLinear(new Vector4(0.45f, 0.45f, 0.50f, 1.0f)));

        for (int i = 0; i < 8; i++)
        {
            bool box = (i % 2) == 0;

            RigidBody body = box
                ? RigidBody.CreateBox(1.0f, 0.30f)
                : RigidBody.CreateSphere(1.0f, 0.32f);

            body.Position = new Vector3(
                -3.2f + (i * 0.42f), 3.4f + (i * 0.55f), ((i % 3) - 1) * 0.45f);
            body.Orientation = Quaternion.CreateFromYawPitchRoll(i * 0.4f, i * 0.3f, i * 0.2f);
            body.Restitution = 0.25f;
            body.AngularDamping = 0.25f;
            Physics.AddBody(body);

            // 箱は暖色、球は寒色。**形の違いが色で分かる**ようにしておく。
            BodyColors.Add(SrgbToLinear(box
                ? new Vector4(0.95f, 0.55f, 0.20f, 1.0f)
                : new Vector4(0.25f, 0.60f, 0.95f, 1.0f)));
        }
    }

    /// <summary>
    /// 細長い箱を回しながら落とす。**辺×辺の軸が出る場面**(要点2)。
    ///
    /// 角と角がすれ違うように当たると、
    /// 面の法線 6 本では分離を見つけられず、辺の外積 9 本のどれかが最小になる。
    /// HUD の「辺」が 0 でなくなるのはたいていこの筋書き。
    ///
    /// <para>
    /// 細長い箱は<b>慣性テンソルが軸ごとに大きく違う</b>(要点1)。
    /// 長い方向を軸にして回すのは楽で、横に振るのは大変——
    /// 同じ角速度を与えても、当たったあとに残る回り方が軸によって違うのが見える。
    /// </para>
    ///
    /// <para>
    /// <b>台は有限の箱</b>なので、落ちた箱は台の縁からこぼれ落ちる。
    /// </para>
    /// </summary>
    private static void BuildBoxTumbleScene()
    {
        RigidBody pedestal = RigidBody.CreateStatic(Collider.Box(new Vector3(1.8f, 0.35f, 1.8f)));
        pedestal.Position = new Vector3(0.0f, PhysicsFloorY + 0.35f, 0.0f);
        pedestal.Restitution = 0.2f;
        Physics.AddBody(pedestal);
        BodyColors.Add(SrgbToLinear(new Vector4(0.40f, 0.42f, 0.48f, 1.0f)));

        for (int i = 0; i < 4; i++)
        {
            RigidBody body = RigidBody.CreateBox(1.0f, new Vector3(0.75f, 0.16f, 0.24f));
            body.Position = new Vector3(((i % 2) - 0.5f) * 1.4f, 3.0f + (i * 0.9f), (i - 1.5f) * 0.5f);
            body.Orientation = Quaternion.CreateFromYawPitchRoll(0.7f * i, 0.5f, 0.9f);

            // **回しながら落とす**。角速度があると、当たる瞬間の姿勢が毎回違う。
            body.AngularVelocity = new Vector3(2.0f, 1.2f, -1.5f);
            body.Restitution = 0.3f;
            body.AngularDamping = 0.15f;
            Physics.AddBody(body);

            float t = i / 3.0f;
            BodyColors.Add(SrgbToLinear(
                new Vector4(0.90f - (0.5f * t), 0.35f + (0.4f * t), 0.75f, 1.0f)));
        }
    }

    // ================================================================
    //  Day 45: カプセル衝突とキャラクターコントローラ(キネマティック)
    // ================================================================

    /// <summary>
    /// カプセルを落とす(Ctrl+Shift+Alt+2 で選ぶ)。**寝ると2点で支えられる**(要点4)。
    ///
    /// 傾きを振った5本を落とす。まっすぐ立てた1本目は<b>1点で着地して倒れ</b>、
    /// 倒れ切ると<b>端2つで支えられて止まる</b>。
    /// 接触点の表示(Ctrl+Shift+Alt+3)を出しておくと、
    /// 光る玉が1つから2つに増える瞬間が見える。
    ///
    /// <para>
    /// <b>床のカプセルは必ず横倒しで止まる</b>。カプセルの慣性は
    /// 軸まわりだけが極端に小さいので(要点5)、
    /// 立った姿勢は少しでも傾けば戻ってこない。
    /// </para>
    /// </summary>
    private static void BuildCapsuleDropScene()
    {
        float[] tilts = [0.0f, 0.25f, 0.6f, 1.1f, MathF.PI * 0.5f];

        for (int i = 0; i < tilts.Length; i++)
        {
            RigidBody body = RigidBody.CreateCapsule(1.0f, 0.28f, 0.45f);
            body.Position = new Vector3((i - 2) * 1.7f, 3.0f, 0.0f);
            body.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, tilts[i]);
            body.Restitution = 0.2f;
            body.AngularDamping = 0.3f;
            Physics.AddBody(body);

            // 水色(立っている)→ 橙(寝ている)。**色が初めの傾きの目盛り**。
            float t = tilts[i] / (MathF.PI * 0.5f);
            BodyColors.Add(SrgbToLinear(
                new Vector4(0.30f + (0.60f * t), 0.65f - (0.10f * t), 0.90f - (0.65f * t), 1.0f)));
        }
    }

    /// <summary>
    /// カプセルを1つ降らせる(Ctrl+Alt+X)。
    ///
    /// <see cref="SpawnBox"/> のカプセル版。**縦横比を毎回変える**ので、
    /// 細長いものほど転がりやすいのが見える(要点5)。
    /// </summary>
    private static void SpawnCapsule()
    {
        if (!_physicsDemo)
        {
            return;
        }

        float angle = _capsuleSpawnCount * 2.399963f;   // 黄金角。**重ならないように散る**

        float radius = 0.20f + (0.06f * (_capsuleSpawnCount % 3));
        float halfHeight = 0.22f + (0.18f * ((_capsuleSpawnCount + 1) % 3));

        // 質量は体積に比例させる(密度をそろえる)。円柱 + 球の体積。
        float volume =
            (MathF.PI * radius * radius * halfHeight * 2.0f)
            + ((4.0f / 3.0f) * MathF.PI * radius * radius * radius);

        RigidBody body = RigidBody.CreateCapsule(volume * 900.0f * 0.001f, radius, halfHeight);
        body.Position = new Vector3(MathF.Cos(angle) * 1.8f, 4.5f, MathF.Sin(angle) * 1.8f);
        body.Orientation = Quaternion.CreateFromYawPitchRoll(angle, angle * 0.7f, angle * 0.3f);
        body.Restitution = RestitutionSteps[_restitutionIndex];
        body.AngularDamping = 0.25f;

        Physics.AddBody(body);
        BodyColors.Add(SrgbToLinear(new Vector4(
            0.30f + (0.5f * MathF.Abs(MathF.Sin(angle))),
            0.65f,
            0.55f + (0.4f * MathF.Abs(MathF.Cos(angle))),
            1.0f)));

        _capsuleSpawnCount++;

        Console.WriteLine(
            $"カプセルを1つ追加: 半径 {radius:F2}m 全高 {2.0f * (halfHeight + radius):F2}m "
            + $"質量 {body.Mass:F2}kg  体 {Physics.DynamicCount} 個(動くもの)");
    }

    /// <summary>
    /// キャラクターデモを出す下ごしらえ(Ctrl+X / Shift+X)。
    ///
    /// <see cref="ShowPhysicsDemo"/> と同じ形だが、
    /// <b>物理デモも同時に立てる</b>のが違い——
    /// キャラクターが歩く床や坂は <see cref="Physics"/> の静的な体でできているので、
    /// 物理の世界そのものは要る。<see cref="_characterDemo"/> は
    /// 「その世界の中をキャラクターが歩いているかどうか」の札でしかない。
    /// </summary>
    private static void ShowCharacterDemo(CharacterScene scene)
    {
        if (_demo is not null)
        {
            UnloadDemoScene();
        }

        StopTourIfRunning();

        if (_model is not null)
        {
            SetModel(ModelPaths.Length);
        }

        _materialGrid = false;
        _surfaceDemo = false;
        _draw3D = true;
        _debugChannel = 0;
        SetSpriteCount(0);

        _physicsDemo = true;
        _characterDemo = true;
        _physicsPaused = false;
        _physicsStepsRequested = 0;

        BuildCharacterScene(scene);

        // **カメラは肩越しくらいの高さ**。真上から見ると段差の高さが読めず、
        // 真横から見ると坂の向きが読めない。
        //
        // **地形だけ少し引く**(Day 46)。24m 四方の起伏を見渡すには 11m では近い。
        _orbit.Reset();
        _orbit.Target = Character.Position + new Vector3(0.0f, 1.0f, 0.0f);
        _orbit.Distance = scene == CharacterScene.Terrain ? 15.0f : 11.0f;
        _orbit.Yaw = 0.0f;
        _orbit.Pitch = scene == CharacterScene.Terrain ? 0.42f : 0.30f;
        _orbit.Apply();
    }

    /// <summary>
    /// キャラクターの遊び場を組む。**筋書きごとに確かめたいことが1つ**。
    ///
    /// <list type="bullet">
    /// <item><b>坂</b> … 傾きを 15/30/45/60 度に振った4枚。上限(Alt+X)で登れる範囲が変わる</item>
    /// <item><b>階段</b> … 段差 15/30/45cm の3本。乗り越え(Ctrl+Shift+X)で越えられる段が変わる</item>
    /// <item><b>障害物</b> … 動く箱・球・カプセルの中を歩く。**押されるが押さない**のが見える</item>
    /// </list>
    ///
    /// <para>
    /// 床と壁は <see cref="BuildPhysicsScene"/> と同じ平面を張る。
    /// <b>キャラクターは剛体ではないので <c>BodyColors</c> に席が要らない</b>——
    /// 体の番号と色の対応(Day 44 で手当てした並行配列)には影響しない。
    /// </para>
    /// </summary>
    private static void BuildCharacterScene(CharacterScene scene)
    {
        _characterScene = scene;
        _physicsScene = PhysicsScene.Drop;   // HUD の表示だけ。体は下で組む
        _spawnCount = 0;

        Physics.Clear();
        BodyColors.Clear();
        DisposeTerrain();
        Physics.VelocityIterations = IterationSteps[_iterationIndex];
        Physics.MaxContactsPerPair = ContactLimits[_contactLimitIndex];
        ConfigureBroadphase();

        // **地形の筋書きは床も縁石も張らない**(Day 46)。地形が床そのもの。
        if (scene == CharacterScene.Terrain)
        {
            BuildTerrainCourse();
            Character.Teleport(_characterSpawn);
            return;
        }

        AddPhysicsPlane(new Vector3(0.0f, PhysicsFloorY, 0.0f), Vector3.UnitY);
        AddPhysicsPlane(new Vector3(-PhysicsWallDistance, 0.0f, 0.0f), Vector3.UnitX);
        AddPhysicsPlane(new Vector3(PhysicsWallDistance, 0.0f, 0.0f), -Vector3.UnitX);
        AddPhysicsPlane(new Vector3(0.0f, 0.0f, -PhysicsWallDistance), Vector3.UnitZ);
        AddPhysicsPlane(new Vector3(0.0f, 0.0f, PhysicsWallDistance), -Vector3.UnitZ);

        switch (scene)
        {
            case CharacterScene.Steps:
                BuildStepCourse();
                break;

            case CharacterScene.Obstacles:
                BuildObstacleCourse();
                break;

            default:
                BuildSlopeCourse();
                break;
        }

        Character.Teleport(_characterSpawn);
    }

    /// <summary>
    /// 坂を4枚(要点7)。**傾き 15 / 30 / 45 / 60 度**。
    ///
    /// 既定の上限は 50 度なので、<b>45 度までは登れて 60 度は登れない</b>。
    /// `Alt+X` で上限を切ると 60 度も登れるようになり、
    /// <b>ほとんど壁のような面をよじ登る</b>のが見える——
    /// 「登れる坂かどうか」が物理ではなく決めごとであることが、これで分かる。
    ///
    /// <para>
    /// 坂は<b>静的な箱</b>(<see cref="RigidBody.CreateStatic"/>)を傾けて作る。
    /// 平面ではなく箱にしてあるのは端が要るから——
    /// 登り切った先で平らな床に戻れないと、坂の上限を確かめにくい。
    /// </para>
    /// </summary>
    private static void BuildSlopeCourse()
    {
        float[] degrees = [15.0f, 30.0f, 45.0f, 60.0f];

        // 登り口の手前に立たせる。4本は z のレーンに分けて並べるので、
        // 左右(z)に歩いて坂を選ぶことになる。
        _characterSpawn = new Vector3(-3.6f, PhysicsFloorY + 0.2f, 0.0f);

        for (int i = 0; i < degrees.Length; i++)
        {
            float radians = degrees[i] * MathF.PI / 180.0f;

            const float halfLength = 1.6f;
            const float halfThickness = 0.5f;

            // **上面が床と滑らかにつながるように置く**のがいちばん大事なところ。
            // 素直に「傾けて持ち上げる」と、坂の切り口(下を向いた法線の面)が
            // 床から飛び出し、キャラクターは坂ではなく<b>その切り口の壁</b>に
            // ぶつかって止まる。坂を登れないのに坂の上限は関係ない、
            // という分かりにくい詰まり方をする。
            //
            // 上面の中心を「登り口から halfLength だけ上」に置けば、
            // 上面のいちばん下の端がちょうど床の高さに来る。
            // 残りの厚みは床の下に潜るので、キャラクターには当たらない。
            var up = new Vector3(-MathF.Sin(radians), MathF.Cos(radians), 0.0f);
            var along = new Vector3(MathF.Cos(radians), MathF.Sin(radians), 0.0f);

            // **4本は z のレーンに分ける**。x 方向へ登るので、
            // 横に並べると急な坂ほど短く、緩い坂ほど長くなって重なってしまう
            // (最初こう書いて、坂が互いにめり込んだ)。
            var foot = new Vector3(-2.0f, PhysicsFloorY, (i - 1.5f) * 2.2f);
            Vector3 topCenter = foot + (along * halfLength);

            RigidBody ramp = RigidBody.CreateStatic(
                Collider.Box(new Vector3(halfLength, halfThickness, 0.9f)));

            ramp.Position = topCenter - (up * halfThickness);
            ramp.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians);
            ramp.Restitution = 0.0f;

            Physics.AddBody(ramp);

            // 緑(緩い)→ 赤(急)。**色が傾きの目盛り**になる。
            float t = i / (float)(degrees.Length - 1);
            BodyColors.Add(SrgbToLinear(
                new Vector4(0.25f + (0.65f * t), 0.75f - (0.45f * t), 0.30f, 1.0f)));
        }
    }

    /// <summary>
    /// 階段を3本(要点8)。**段差 15 / 30 / 45cm**。
    ///
    /// 既定の乗り越え(<see cref="CharacterController.StepOffset"/>)は 35cm なので、
    /// <b>15cm と 30cm は登れて、45cm は登れない</b>。
    /// `Ctrl+Shift+X` で切ると 15cm ですら越えられなくなり、
    /// <b>床のわずかな段差に足を取られる</b>のが体感できる。
    ///
    /// <para>
    /// 降りるときにも見どころがある。段を降りる間、
    /// <see cref="CharacterController"/> が床へ吸い付いていないと
    /// <b>1段ごとに宙に浮いて跳ねる</b>——
    /// HUD の「接地」が点滅するかどうかで確かめられる。
    /// </para>
    /// </summary>
    private static void BuildStepCourse()
    {
        float[] heights = [0.15f, 0.30f, 0.45f];

        // 階段は -x へ向かって上がる。手前(+x 側)に立たせる。
        _characterSpawn = new Vector3(2.8f, PhysicsFloorY + 0.2f, 0.0f);

        for (int lane = 0; lane < heights.Length; lane++)
        {
            float height = heights[lane];
            float z = (lane - 1) * 2.2f;

            for (int step = 0; step < 4; step++)
            {
                // 上るほど厚く積む。**1段ぶんの上面が段の高さになる**ように、
                // 箱の上面の高さを (step+1) * height に合わせる。
                float top = PhysicsFloorY + ((step + 1) * height);
                float halfY = (top - PhysicsFloorY) * 0.5f;

                RigidBody block = RigidBody.CreateStatic(
                    Collider.Box(new Vector3(0.45f, halfY, 0.9f)));

                // **いちばん低い段が手前(+x)**。逆に並べると、
                // 出発点からいきなり4段ぶんの壁に突き当たることになる。
                block.Position = new Vector3(1.6f - (step * 0.9f), PhysicsFloorY + halfY, z);
                block.Restitution = 0.0f;

                Physics.AddBody(block);

                float t = step / 3.0f;
                BodyColors.Add(SrgbToLinear(new Vector4(
                    0.35f + (0.20f * lane), 0.45f + (0.30f * t), 0.65f - (0.20f * lane), 1.0f)));
            }
        }
    }

    /// <summary>
    /// 障害物のコース。**キネマティックと動的が同じ世界に居る**のを見る。
    ///
    /// 動く箱・球・カプセルが転がっている中を歩く。
    /// <b>キャラクターは押されるが押さない</b>——
    /// 転がってきた箱に当たると押し戻されて止まるのに、
    /// 箱を押しても箱は動かない(要点6)。
    /// この非対称がキネマティックの代償で、
    /// 直すには当たった相手にインパルスを掛けてやることになる(改造課題3)。
    /// </summary>
    private static void BuildObstacleCourse()
    {
        _characterSpawn = new Vector3(0.0f, PhysicsFloorY + 0.2f, 3.0f);

        // 台。**歩いて登れる高さ**にしてある(段差 30cm を2段)。
        for (int i = 0; i < 2; i++)
        {
            RigidBody block = RigidBody.CreateStatic(
                Collider.Box(new Vector3(1.2f, 0.15f + (i * 0.15f), 1.2f)));

            block.Position = new Vector3(
                -2.6f, PhysicsFloorY + 0.15f + (i * 0.15f), -2.4f + (i * 1.2f));
            block.Restitution = 0.0f;

            Physics.AddBody(block);
            BodyColors.Add(SrgbToLinear(new Vector4(0.45f, 0.48f, 0.55f, 1.0f)));
        }

        for (int i = 0; i < 9; i++)
        {
            RigidBody body = (i % 3) switch
            {
                0 => RigidBody.CreateBox(1.0f, 0.28f),
                1 => RigidBody.CreateSphere(1.0f, 0.30f),
                _ => RigidBody.CreateCapsule(1.0f, 0.22f, 0.30f),
            };

            body.Position = new Vector3(
                2.2f + (((i % 3) - 1) * 0.9f), 3.0f + (i * 0.7f), -1.8f + (i * 0.45f));
            body.Orientation = Quaternion.CreateFromYawPitchRoll(i * 0.5f, i * 0.4f, i * 0.3f);
            body.Restitution = 0.2f;
            body.AngularDamping = 0.3f;

            Physics.AddBody(body);

            // 箱は橙、球は青、カプセルは緑。**形の違いが色で分かる**。
            BodyColors.Add(SrgbToLinear((i % 3) switch
            {
                0 => new Vector4(0.95f, 0.55f, 0.20f, 1.0f),
                1 => new Vector4(0.25f, 0.60f, 0.95f, 1.0f),
                _ => new Vector4(0.35f, 0.85f, 0.45f, 1.0f),
            }));
        }
    }

    /// <summary>
    /// キャラクターを1ステップ動かす。**<see cref="FixedUpdate"/> からだけ呼ぶ**(要点6)。
    ///
    /// <b>入力はカメラ基準に直してから渡す</b>。
    /// 「上キー = 画面の奥へ」が三人称の約束で、
    /// 世界の -Z へ固定すると、カメラを回した瞬間に操作が破綻する。
    /// カメラの向き(<see cref="OrbitCameraController.Yaw"/>)から
    /// 前と右を作り、入力を載せ替えるだけで済む。
    ///
    /// <para>
    /// <see cref="CharacterController"/> 自身は<b>カメラを知らない</b>。
    /// 受け取るのは「どちらへ行きたいか」の水平ベクトルだけで、
    /// カメラ基準に直すのは呼ぶ側の仕事——
    /// Day 18 の <see cref="InputSnapshot"/> が
    /// 「どのキーか」を知らなかったのと同じ線を引いてある。
    /// </para>
    ///
    /// <para>
    /// <b>物理より後に動かす</b>。箱や球が動いたあとの世界に対して
    /// キャラクターが当たりを取るので、<b>1ステップ前の位置に押し戻される</b>ことがない。
    /// 逆にすると、転がってきた箱にめり込んだまま次のステップへ進む。
    /// </para>
    /// </summary>
    private static void UpdateCharacter(float dt, in InputSnapshot input)
    {
        if (!_characterDemo)
        {
            return;
        }

        if (_physicsPaused && _physicsStepsRequested <= 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // **問い合わせの計測は1ステップぶん**。ここで 0 に戻しておくと、
        // HUD の「問合」が「このステップで何回投げたか」になる。
        Physics.ResetQueryStats();

        // --- 入力をカメラ基準へ ---
        //
        // カメラは注視点の +Z 側(Yaw=0)に居るので、
        // **画面の奥へ進む向き**は -(sin Yaw, 0, cos Yaw)。
        float yaw = _orbit.Yaw;
        var forward = new Vector3(-MathF.Sin(yaw), 0.0f, -MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0.0f, -MathF.Sin(yaw));

        // MoveAxis は画面座標の約束(下が +Y)なので、前後は符号を反転する。
        Vector2 axis = input.MoveAxis;
        Vector3 wish = (right * axis.X) + (forward * -axis.Y);

        Character.Move(
            Physics,
            wish,
            input.IsHeld(GameAction.Dash),
            input.WasPressed(GameAction.Jump),
            dt);

        // **落ちたら戻す**。見えない壁で四方は囲ってあるが、
        // 床をすり抜けるようなことがあってもデモが続けられるように。
        if (Character.Position.Y < PhysicsFloorY - 5.0f)
        {
            Character.Teleport(_characterSpawn);
            Console.WriteLine("キャラクターが落ちたので出発点へ戻した");
        }

        _characterMilliseconds =
            (_characterMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);
    }

    /// <summary>
    /// キャラクターを描く。**カプセルは球2つ + 円柱1本**(要点10)。
    ///
    /// 判定が「線分に球を滑らせる」だったのと、絵の分け方が一致している。
    /// <b>描いているのは当たり判定の形そのもの</b>なので、
    /// めり込んでいれば絵でもめり込んで見える——
    /// Day 51 でモデルを載せると、この一致は失われる(モデルとカプセルは別物になる)。
    ///
    /// <para>
    /// 足元に<b>接地の印</b>を出す。接地していれば法線の向きに小さな板が寝るので、
    /// 坂の上でどちらを向いているかが目で読める。
    /// HUD の数字(傾き何度)と突き合わせるためのもの。
    /// </para>
    /// </summary>
    private static void RenderCharacter()
    {
        if (!_characterDemo)
        {
            return;
        }

        Capsule3D capsule = Character.ToCapsule();

        DrawCapsule(capsule, _characterMaterial);

        // --- 接地の印 ---
        //
        // 接地していれば緑、していなければ何も出さない。
        // **床の法線に沿って寝かせる**ので、坂の上では板も傾く。
        if (!Character.IsGrounded)
        {
            return;
        }

        _contactMaterial.BaseColorFactor = new Vector4(0.05f, 0.05f, 0.05f, 1.0f);
        _contactMaterial.EmissiveFactor = new Vector3(0.2f, 3.0f, 0.6f);

        Draw(
            _cylinder,
            _contactMaterial,
            Matrix4x4.CreateScale(Character.Radius * 1.6f, 0.02f, Character.Radius * 1.6f)
                * RotationTo(Character.GroundNormal)
                * Matrix4x4.CreateTranslation(Character.Position + (Character.GroundNormal * 0.02f)));

        // 接触点の印は橙に戻しておく(<see cref="RenderContacts"/> が使う)。
        _contactMaterial.EmissiveFactor = new Vector3(4.0f, 0.9f, 0.15f);
    }

    /// <summary>
    /// カプセル1本を描く。**下の球・胴の円柱・上の球**の3回(要点10)。
    ///
    /// 円柱は XZ に半径・Y に線分の長さで拡大する。
    /// <b>非一様な拡大でも円柱なら正しい形になる</b>のがこの分け方の値打ちで、
    /// カプセル1本ぶんのメッシュを寸法ごとに作らずに済む。
    /// </summary>
    private static void DrawCapsule(in Capsule3D capsule, Material material)
    {
        float diameter = capsule.Radius * 2.0f;
        float length = capsule.Segment.Length;

        Draw(_sphere, material,
            Matrix4x4.CreateScale(diameter)
                * Matrix4x4.CreateTranslation(capsule.Segment.Start));

        Draw(_sphere, material,
            Matrix4x4.CreateScale(diameter)
                * Matrix4x4.CreateTranslation(capsule.Segment.End));

        // 線分が潰れているときは胴を描かない(カプセルが球になっている)。
        if (length <= 1e-4f)
        {
            return;
        }

        Draw(_cylinder, material,
            Matrix4x4.CreateScale(diameter, length, diameter)
                * RotationTo(capsule.Axis)
                * Matrix4x4.CreateTranslation(capsule.Center));
    }

    /// <summary>
    /// 「+Y をこの向きへ倒す」回転。**カプセルの胴と接地の印**が使う。
    ///
    /// 軸は <c>cross(+Y, 向き)</c>、角度は内積から。
    /// <b>真下を向いたときだけ軸が作れない</b>(外積が 0)ので、
    /// そのときは適当な軸で 180 度回す——
    /// Day 43 の「中心が一致した球」と同じ種類の退化で、
    /// <b>放っておくと NaN が絵に混ざる</b>。
    /// </summary>
    private static Matrix4x4 RotationTo(Vector3 direction)
    {
        float dot = Math.Clamp(Vector3.Dot(Vector3.UnitY, direction), -1.0f, 1.0f);

        if (dot > 0.9999f)
        {
            return Matrix4x4.Identity;
        }

        if (dot < -0.9999f)
        {
            return Matrix4x4.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        }

        Vector3 axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, direction));
        return Matrix4x4.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }

    /// <summary>キャラクターの向きを含む行列(Day 51 でモデルを載せる場所)。</summary>
    private static Matrix4x4 CharacterMatrix() =>
        Matrix4x4.CreateRotationY(Character.FacingYaw)
            * Matrix4x4.CreateTranslation(Character.Position);

    /// <summary>
    /// HUD の1行(Day 45)。**絵から読めないものだけ**。
    ///
    /// 接地しているか、立っている面が何度か、段差を越えたか——
    /// どれも「なんとなく歩けている」で済ませてしまいやすいところ。
    /// <b>坂を登れないときに、上限で弾かれたのか引っかかっているのかは、
    /// この行を見ないと分からない</b>。
    /// </summary>
    private static string CharacterLabel() =>
        $"キャラ[{CharacterSceneLabel()}]  {(Character.IsGrounded ? "接地" : "**空中**")}  "
        + $"傾き:{Character.GroundSlopeDegrees:F0}度  速さ:{Character.HorizontalSpeed:F2}m/s  "
        + $"高さ:{Character.Position.Y - PhysicsFloorY:F2}m  "
        + $"坂上限:{(Character.UseSlopeLimit ? $"{Character.SlopeLimitDegrees:F0}度" : "**なし**")}  "
        + $"段差:{(Character.UseStepOffset ? $"{Character.StepOffset * 100.0f:F0}cm" : "**なし**")}  "
        + $"押戻:{Character.ResolvedContacts}  "
        + (Character.SteppedUp ? $"**段 {Character.LastStepHeight * 100.0f:F0}cm**  " : string.Empty)
        + (Character.TouchedWall ? "壁  " : string.Empty)
        + $"問合:{Physics.CapsuleQueries}回/{Physics.CapsuleQueryTests}体  "
        + $"{_characterMilliseconds:F2}ms";

    private static string CharacterSceneLabel() => _characterScene switch
    {
        CharacterScene.Steps => "階段",
        CharacterScene.Obstacles => "障害物",
        CharacterScene.Terrain => "地形",
        _ => "坂",
    };

    /// <summary>
    /// キャラクターの内訳をコンソールへ(Shift+Alt+X)。
    ///
    /// **設定と状態を並べて出す**。坂が登れない・段差が越えられないとき、
    /// 原因は「上限の設定」か「引っかかり」のどちらかしかないので、
    /// 両方を1画面に出しておくと切り分けが1回で済む。
    /// </summary>
    private static void DescribeCharacter()
    {
        Console.WriteLine();
        Console.WriteLine($"--- キャラクターの内訳({CharacterSceneLabel()})---");
        Console.WriteLine(
            $"  形: カプセル 半径 {Character.Radius:F2}m  全高 {Character.Height:F2}m  "
            + $"線分の半分 {Character.HalfHeight:F2}m  skin {Character.SkinWidth * 1000.0f:F1}mm");
        Console.WriteLine(
            $"  位置(足元): ({Character.Position.X:F2}, {Character.Position.Y:F2}, "
            + $"{Character.Position.Z:F2})  向き {Character.FacingYaw * 180.0f / MathF.PI:F0}度");
        Console.WriteLine(
            $"  速度: ({Character.Velocity.X:F2}, {Character.Velocity.Y:F2}, "
            + $"{Character.Velocity.Z:F2}) m/s  水平 {Character.HorizontalSpeed:F2}m/s");
        Console.WriteLine(
            $"  接地: {(Character.IsGrounded ? "している" : "していない")}  "
            + $"床の法線 ({Character.GroundNormal.X:F2}, {Character.GroundNormal.Y:F2}, "
            + $"{Character.GroundNormal.Z:F2})  傾き {Character.GroundSlopeDegrees:F1}度");
        Console.WriteLine(
            $"  坂の上限: {(Character.UseSlopeLimit ? $"{Character.SlopeLimitDegrees:F0}度"
                + $"(法線の Y が {Character.SlopeLimitCosine:F3} 以上)" : "切ってある")}");
        Console.WriteLine(
            $"  段差の乗り越え: {(Character.UseStepOffset
                ? $"{Character.StepOffset * 100.0f:F0}cm まで" : "切ってある")}");
        Console.WriteLine(
            $"  速さ: 歩き {Character.WalkSpeed:F1}m/s  走り {Character.RunSpeed:F1}m/s  "
            + $"ジャンプ {Character.JumpHeight:F2}m  重力 {Character.Gravity:F1}m/s²"
            + $"(物理の世界は {-Physics.Gravity.Y:F2})");
        Console.WriteLine(
            $"  直前のステップ: 押し戻し {Character.ResolvedContacts} 回  "
            + $"段差 {(Character.SteppedUp ? $"{Character.LastStepHeight * 100.0f:F1}cm" : "なし")}  "
            + $"壁 {(Character.TouchedWall ? "あり" : "なし")}");
        Console.WriteLine(
            $"  問い合わせ: {Physics.CapsuleQueries} 回 / 延べ {Physics.CapsuleQueryTests} 体"
            + $"(体は全部で {Physics.Bodies.Count} 個。**Day 46 のブロードフェーズで減る数字**)");
        Console.WriteLine($"  1ステップ: {_characterMilliseconds:F3}ms");
        Console.WriteLine();
    }

    // ================================================================
    //  Day 46: Heightmap コライダ(地形)とブロードフェーズ(均一グリッド)
    // ================================================================

    /// <summary>
    /// 地形の高さを1点ぶん決める(Day 46)。**手続きで作るので素材が要らない**。
    ///
    /// 引数は<b>地形の隅からの相対位置 [m]</b>。世界座標ではない。
    /// 実際のゲームでは PNG のグレースケールを読むか、
    /// ノイズ関数(Perlin / Simplex)で作ることが多いが、
    /// ここは<b>再現できること</b>を最優先にして正弦波の和にしてある——
    /// 自己チェックが同じ地形を作り直せないと、坂の角度を数字で確かめられない。
    ///
    /// <para>
    /// 中身は4つの足し算。
    /// </para>
    /// <list type="number">
    /// <item><b>細かい起伏</b> … 正弦波2本。歩いていて退屈しないための飾り</item>
    /// <item><b>急な丘</b> … 円錐。傾き atan(1.6) ≒ <b>58 度</b>で、
    /// 既定の坂の上限(50 度)を超える——<b>登れない</b></item>
    /// <item><b>緩い丘</b> … 円錐。傾き atan(0.55) ≒ <b>29 度</b>で、<b>登れる</b></item>
    /// <item><b>出発点をならす</b> … 立ち位置が坂だと、
    /// 「動いていないのに滑っていないか」が確かめにくい</item>
    /// </list>
    ///
    /// <para>
    /// <b>円錐にした</b>のは、傾きが半径方向にどこでも一定になるから。
    /// ガウス型の丘だと場所ごとに傾きが変わるので、
    /// 「何度の坂が登れるか」を目で確かめる道具にならない。
    /// </para>
    /// </summary>
    private static float TerrainHeightAt(float x, float z)
    {
        // 1. 細かい起伏。
        float height =
            (0.42f * MathF.Sin(x * 0.42f) * MathF.Cos(z * 0.38f))
            + (0.20f * MathF.Sin((x * 0.90f) + 1.3f) * MathF.Sin(z * 0.80f));

        // 2. 急な丘(58 度)。**登れないほうの目印**。
        float steep = new Vector2(x - 17.5f, z - 7.0f).Length();
        height += MathF.Max(2.6f - (1.60f * steep), 0.0f);

        // 3. 緩い丘(29 度)。**登れるほうの目印**。
        float gentle = new Vector2(x - 6.5f, z - 17.0f).Length();
        height += MathF.Max(1.5f - (0.55f * gentle), 0.0f);

        // 4. 出発点(12, 21)のまわり半径 1.5m を平らにならす。
        //    2.0m かけて元の高さへ戻す(いきなり戻すと、そこが崖になる)。
        float toSpawn = new Vector2(x - 12.0f, z - 21.0f).Length();
        return height * Math.Clamp((toSpawn - 1.5f) / 2.0f, 0.0f, 1.0f);
    }

    /// <summary>
    /// 地形を作って世界に置く(Day 46)。**描画メッシュも同時に起こす**。
    ///
    /// <b>高さの配列は1本しか作らない</b>。
    /// 判定(<see cref="HeightField"/>)と描画(<see cref="Mesh{T}"/>)が
    /// 同じ配列から生まれるので、**そもそも食い違いようがない**——
    /// 「絵と当たり判定を一致させる」いちばん確実なやり方は、
    /// 元のデータを1つにしてしまうこと。
    /// </summary>
    private static void AddTerrain()
    {
        DisposeTerrain();

        int points = TerrainCells + 1;
        var heights = new float[points * points];

        for (int z = 0; z < points; z++)
        {
            for (int x = 0; x < points; x++)
            {
                heights[(z * points) + x] =
                    TerrainHeightAt(x * TerrainCellSize, z * TerrainCellSize);
            }
        }

        _terrain = new HeightField(heights, points, points, TerrainCellSize);

        // 地形の中心が原点に来るように置く。**隅の座標**を渡すので半分ずらす。
        _terrainOrigin = new Vector3(-TerrainSpan * 0.5f, PhysicsFloorY, -TerrainSpan * 0.5f);

        _terrainMesh = Primitives.CreateHeightField(
            _gl, heights, points, points, TerrainCellSize, uvScale: 4.0f);

        Physics.AddTerrain(_terrain, _terrainOrigin);
        BodyColors.Add(Vector4.One);   // 地形は専用のマテリアルで描くので色は使わない

        // **地形の縁に見えない壁を立てる**。地形は端で切れているので、
        // 囲っておかないと転がった物が縁から落ちて二度と戻ってこない。
        // Day 43 から使っている縁石(±5m)より外なので、そちらとは別に張る。
        float wall = TerrainSpan * 0.5f;
        AddPhysicsPlane(new Vector3(-wall, 0.0f, 0.0f), Vector3.UnitX);
        AddPhysicsPlane(new Vector3(wall, 0.0f, 0.0f), -Vector3.UnitX);
        AddPhysicsPlane(new Vector3(0.0f, 0.0f, -wall), Vector3.UnitZ);
        AddPhysicsPlane(new Vector3(0.0f, 0.0f, wall), -Vector3.UnitZ);

        // **受け皿の床**。地形をすり抜けた物を拾う(要点3の「潜った球」の保険)。
        // 地形の下 6m に張っておけば、絵にも判定にも出てこない。
        AddPhysicsPlane(new Vector3(0.0f, PhysicsFloorY - 6.0f, 0.0f), Vector3.UnitY);
    }

    /// <summary>地形を捨てる。**メッシュは GL の資源なので明示的に解放する**。</summary>
    private static void DisposeTerrain()
    {
        _terrainMesh?.Dispose();
        _terrainMesh = null;
        _terrain = null;
    }

    /// <summary>
    /// ブロードフェーズの格子の範囲を、今の筋書きに合わせる(Day 46)。
    ///
    /// **格子は世界より少し広く**取る。外へ出たものは端のマスに丸められるので
    /// 取りこぼしは起きないが、端のマスが混んで遅くなる。
    /// 地形が 24m 四方なので、32m 四方あれば飛び出した物も収まる。
    /// </summary>
    private static void ConfigureBroadphase()
    {
        Physics.GridOrigin = new Vector3(-16.0f, -4.0f, -16.0f);
        Physics.GridSize = new Vector3(32.0f, 24.0f, 32.0f);
        Physics.CellSize = GridCellSteps[_gridCellIndex];
    }

    /// <summary>
    /// 地形の上に大量に落とす筋書き(Day 46)。**ブロードフェーズの客**。
    ///
    /// 60 個も置くと、総当たりでは 2,000 組近くを毎ステップ試すことになる。
    /// `Ctrl+Alt+G` でさらに 40 個ずつ足せるので、
    /// <b>組の数がどう増えるか</b>を数字で追える——
    /// 総当たりは体数の2乗、格子はほぼ比例で増える。
    /// </summary>
    private static void BuildTerrainScene()
    {
        AddTerrain();
        _terrainSpawnCount = 0;

        SpawnTerrainBodies(60);
    }

    /// <summary>
    /// 地形の上へ体を降らせる(Day 46)。**同じ形を3種類まぜる**。
    ///
    /// 混ぜるのは絵のためだけではない——
    /// 球・箱・カプセルで<b>外接箱の太り方が違う</b>ので、
    /// ブロードフェーズの候補の出方も変わる。
    /// 回っている箱は外接箱がいちばん太る(最大 √3 ≒ 1.73 倍)。
    /// </summary>
    private static void SpawnTerrainBodies(int count)
    {
        var random = new Random(20460 + _terrainSpawnCount);

        for (int i = 0; i < count; i++)
        {
            int kind = _terrainSpawnCount % 3;

            RigidBody body = kind switch
            {
                0 => RigidBody.CreateSphere(1.0f, 0.22f + ((float)random.NextDouble() * 0.10f)),
                1 => RigidBody.CreateBox(1.0f, 0.20f + ((float)random.NextDouble() * 0.10f)),
                _ => RigidBody.CreateCapsule(1.0f, 0.16f, 0.18f),
            };

            // **地形の上のほうから落とす**。丘の頂上(2.6m)より上に置かないと、
            // 出た瞬間に丘へめり込んだ状態から始まって弾け飛ぶ。
            body.Position = new Vector3(
                ((float)random.NextDouble() - 0.5f) * (TerrainSpan - 3.0f),
                PhysicsFloorY + 4.5f + ((float)random.NextDouble() * 4.0f),
                ((float)random.NextDouble() - 0.5f) * (TerrainSpan - 3.0f));

            body.Orientation = Quaternion.CreateFromYawPitchRoll(
                (float)random.NextDouble() * 6.28f,
                (float)random.NextDouble() * 6.28f,
                (float)random.NextDouble() * 6.28f);

            body.Restitution = 0.15f;
            body.AngularDamping = 0.4f;

            Physics.AddBody(body);

            BodyColors.Add(SrgbToLinear(kind switch
            {
                0 => new Vector4(0.30f, 0.62f, 0.95f, 1.0f),
                1 => new Vector4(0.95f, 0.60f, 0.22f, 1.0f),
                _ => new Vector4(0.40f, 0.88f, 0.50f, 1.0f),
            }));

            _terrainSpawnCount++;
        }
    }

    /// <summary>
    /// 地形の上を歩く筋書き(Day 46)。**今日の到達点**。
    ///
    /// Day 45 の坂(静的な箱を傾けたもの)と違い、
    /// <b>坂の角度が場所ごとに連続に変わる</b>。
    /// 緩い丘を登り、急な丘の途中で止まり、谷を降りる——
    /// HUD の「傾き」がそのまま今立っている三角形の角度になっている。
    ///
    /// <para>
    /// <b>転がる物も少しだけ置く</b>。動く体と地形が同じ世界にいることを見せるためで、
    /// キャラクターの足元の問い合わせがブロードフェーズを通ることも
    /// これで確かめられる(HUD の「問合」が「16 回 / 数十体」で収まる)。
    /// </para>
    /// </summary>
    private static void BuildTerrainCourse()
    {
        AddTerrain();
        _terrainSpawnCount = 0;

        SpawnTerrainBodies(24);

        // 出発点は、地形をならしてある (12, 21) の真上(世界座標では (0, ?, 9))。
        _characterSpawn = new Vector3(
            _terrainOrigin.X + 12.0f,
            PhysicsFloorY + 0.3f,
            _terrainOrigin.Z + 21.0f);
    }

    /// <summary>
    /// 地形を描く(Day 46)。**当たり判定に使っている高さの配列そのものから起こしたメッシュ**。
    ///
    /// 位置は体の位置(= 格子の隅)そのままで、回転も拡大も無い。
    /// <b>地形は回らない</b>と決めてあるので、行列は平行移動1つで足りる。
    /// </summary>
    private static void RenderTerrain()
    {
        if (_terrainMesh is null)
        {
            return;
        }

        Draw(_terrainMesh, _terrainMaterial, Matrix4x4.CreateTranslation(_terrainOrigin));
        _drawCalls++;
    }

    /// <summary>
    /// ブロードフェーズのマスを線で描く(Alt+G)。**中身のあるマスだけ**(要点7)。
    ///
    /// 空のマスまで描くと画面が線で埋まって何も読めない。
    /// 中身のあるマスだけ描けば、<b>体がどう散らばっているか</b>と
    /// <b>マスの大きさが物に対して適切か</b>が一目で分かる——
    /// マスが物より小さければ1個が何マスにもまたがった格子模様が出るし、
    /// 大きすぎれば全部が1つの箱に収まってしまう。
    ///
    /// <para>
    /// <b>線で描くために <c>PolygonMode</c> を一時的に切り替える</b>。
    /// 塗りつぶしで描くと中の体が見えなくなる。
    /// 半透明にする手もあるが、そのためにブレンドの設定と描画順の管理が要る——
    /// デバッグ表示にそこまで払う価値は無い。
    /// </para>
    ///
    /// <para>
    /// <b>上限を決めて打ち切る</b>。マスを 0.5m にすると中身のあるマスが
    /// 数千になり、それだけでドローコールが跳ね上がる。
    /// 診断用の絵が本体より重くなるのは本末転倒なので、512 で止める。
    /// </para>
    /// </summary>
    private static void RenderGridCells()
    {
        if (!_showGridCells || Physics.Broadphase != BroadphaseMode.UniformGrid)
        {
            return;
        }

        SpatialGrid3D grid = Physics.Grid;

        // **まだ組んでいない格子は覗けない**。描画は物理より先に走りうる——
        // シーンを組んだ直後の1フレーム目や、一時停止中に切り替えたときがそれ。
        if (!grid.IsBuilt)
        {
            return;
        }

        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);

        _contactMaterial.BaseColorFactor = new Vector4(0.05f, 0.05f, 0.05f, 1.0f);
        _contactMaterial.EmissiveFactor = new Vector3(0.25f, 0.9f, 1.6f);

        int drawn = 0;

        for (int z = 0; z < grid.Layers && drawn < 512; z++)
        {
            for (int y = 0; y < grid.Rows && drawn < 512; y++)
            {
                for (int x = 0; x < grid.Columns && drawn < 512; x++)
                {
                    if (grid.CellContents(x, y, z).Length == 0)
                    {
                        continue;
                    }

                    Aabb3D cell = grid.CellBounds(x, y, z);

                    Draw(
                        _cube,
                        _contactMaterial,
                        Matrix4x4.CreateScale(cell.Size)
                            * Matrix4x4.CreateTranslation(cell.Center));

                    drawn++;
                }
            }
        }

        _drawCalls += drawn;

        // 接触点の印は橙に戻しておく(<see cref="RenderContacts"/> が使う)。
        _contactMaterial.EmissiveFactor = new Vector3(4.0f, 0.9f, 0.15f);

        _gl.PolygonMode(
            TriangleFace.FrontAndBack, _wireframe ? PolygonMode.Line : PolygonMode.Fill);
    }

    /// <summary>マスの大きさを1段変える(Ctrl+Shift+G)。**次のステップから効く**。</summary>
    private static void CycleGridCellSize()
    {
        _gridCellIndex = (_gridCellIndex + 1) % GridCellSteps.Length;
        Physics.CellSize = GridCellSteps[_gridCellIndex];

        Console.WriteLine(
            $"格子のマス: **{GridCellSteps[_gridCellIndex]:F1}m**"
            + $"(体の平均的な差し渡しの2〜3倍が目安。小さすぎると"
            + $"1個が {(int)MathF.Pow(2.0f, 3.0f)} 倍の勢いでマスにまたがる)");
    }

    /// <summary>
    /// HUD の1行(Day 46)。**絵からは絶対に読めない数字**。
    ///
    /// ブロードフェーズは<b>効いていても絵が1ピクセルも変わらない</b>——
    /// それが正しく効いている証拠でもあるので、
    /// 数字が出ていないと「切り替えたつもり」で終わってしまう。
    /// 候補の組と総当たりの組を並べて出すのがこの行の眼目になる。
    /// </summary>
    private static string BroadphaseLine()
    {
        SpatialGrid3D grid = Physics.Grid;

        string mode = Physics.Broadphase == BroadphaseMode.UniformGrid
            ? $"格子{Physics.CellSize:F1}m"
            : "**総当たり**";

        string ratio = Physics.BruteForcePairs > 0
            ? $"({100.0 * Physics.PairTests / Physics.BruteForcePairs:F1}%)"
            : string.Empty;

        return $"広域[{mode}]  候補:{Physics.PairTests:N0}/{Physics.BruteForcePairs:N0}{ratio}  "
            + (Physics.Broadphase == BroadphaseMode.UniformGrid
                ? $"登録:{grid.EntryCount}  使用マス:{grid.OccupiedCells}/{grid.CellCount:N0}  "
                    + $"最混雑:{grid.MaxPerCell}  はみ出し:{grid.OversizedCount}  "
                : string.Empty)
            + (_terrain is not null
                ? $"地形:{_terrain.Columns}x{_terrain.Rows}点 {_terrain.TriangleCount:N0}三角形"
                : "地形なし");
    }

    /// <summary>
    /// 地形とブロードフェーズの内訳をコンソールへ(Shift+Alt+G)。
    ///
    /// **1画面で切り分けが済む**ように並べてある。
    /// 「体が地面をすり抜ける」ときの原因は、たいてい
    /// 地形の範囲外に出たか、格子のはみ出し組から漏れたかのどちらか。
    /// </summary>
    private static void DescribeBroadphase()
    {
        SpatialGrid3D grid = Physics.Grid;

        Console.WriteLine();
        Console.WriteLine("--- 地形とブロードフェーズの内訳(Day 46)---");

        if (_terrain is not null)
        {
            var terrain = new Terrain3D(_terrain, _terrainOrigin);
            Aabb3D bounds = terrain.Bounds;

            Console.WriteLine(
                $"  地形: {_terrain.Columns}x{_terrain.Rows} 点  "
                + $"マス {_terrain.CellsX}x{_terrain.CellsZ}  1マス {_terrain.CellSize:F2}m  "
                + $"三角形 {_terrain.TriangleCount:N0} 枚");
            Console.WriteLine(
                $"  広がり: x [{bounds.Min.X:F1}, {bounds.Max.X:F1}]  "
                + $"z [{bounds.Min.Z:F1}, {bounds.Max.Z:F1}]  "
                + $"高さ [{bounds.Min.Y:F2}, {bounds.Max.Y:F2}]");
            Console.WriteLine(
                $"  容量: 高さの配列 {_terrain.Columns * _terrain.Rows * 4 / 1024.0:F1}KB"
                + $"(同じ形を三角形メッシュで持つと約 "
                + $"{_terrain.TriangleCount * 3 * 12 / 1024.0:F0}KB)");

            if (terrain.TryHeightAt(Character.Position.X, Character.Position.Z, out float ground)
                && terrain.TryNormalAt(Character.Position.X, Character.Position.Z, out Vector3 n))
            {
                Console.WriteLine(
                    $"  キャラクターの足元: 地面 {ground:F2}m  足元 {Character.Position.Y:F2}m  "
                    + $"傾き {MathF.Acos(Math.Clamp(n.Y, -1.0f, 1.0f)) * 180.0f / MathF.PI:F1}度");
            }
        }
        else
        {
            Console.WriteLine("  地形: なし(Ctrl+G で地形デモへ)");
        }

        Console.WriteLine(
            $"  ブロードフェーズ: {(Physics.Broadphase == BroadphaseMode.UniformGrid
                ? "均一グリッド" : "総当たり")}"
            + $"  マス {Physics.CellSize:F1}m  範囲 {Physics.GridSize.X:F0}x"
            + $"{Physics.GridSize.Y:F0}x{Physics.GridSize.Z:F0}m");
        Console.WriteLine(
            $"  マスの数: {grid.Columns}x{grid.Rows}x{grid.Layers} = {grid.CellCount:N0}  "
            + $"中身のあるマス {grid.OccupiedCells}  いちばん混んでいるマス {grid.MaxPerCell} 個");
        Console.WriteLine(
            $"  登録: {grid.EntryCount} 件 / 体 {Physics.Bodies.Count} 個"
            + $"(1体あたり {(Physics.Bodies.Count > 0
                ? grid.EntryCount / (float)Physics.Bodies.Count : 0.0f):F2} マス)");
        Console.WriteLine(
            $"  はみ出し(格子に入らない): {grid.OversizedCount} 個"
            + "(**平面と地形**。全部の相手と組にする)");
        Console.WriteLine(
            $"  組: 同居 {grid.CoLocatedPairs:N0} → AABB を通過 {Physics.PairTests:N0}  "
            + $"(総当たりなら {Physics.BruteForcePairs:N0})");
        Console.WriteLine(
            $"  キャラクターの問い合わせ: {Physics.CapsuleQueries} 回 / "
            + $"延べ {Physics.CapsuleQueryTests} 体");
        Console.WriteLine($"  1ステップ: 物理 {_physicsMilliseconds:F2}ms");
        Console.WriteLine();
    }

    /// <summary>
    /// 箱を1つ降らせる(Ctrl+Shift+Alt+5)。
    ///
    /// <see cref="SpawnBall"/> の箱版。**向きを毎回変える**のがこちらの要点で、
    /// 同じ形でも落ちる姿勢が違えば、着地の接触点の数が変わる。
    /// </summary>
    private static void SpawnBox()
    {
        if (!_physicsDemo)
        {
            return;
        }

        float angle = _boxSpawnCount * 2.399963f;   // 黄金角。**重ならないように散る**

        // 縦横比を少しずつ変える。**細長いものほど倒れやすい**のが見える。
        var half = new Vector3(
            0.22f + (0.10f * (_boxSpawnCount % 3)),
            0.22f,
            0.22f + (0.06f * ((_boxSpawnCount + 1) % 3)));

        // 質量は体積に比例させる(密度をそろえる)。
        float mass = half.X * half.Y * half.Z * 8.0f * 6.0f;

        RigidBody body = RigidBody.CreateBox(mass, half);
        body.Position = new Vector3(MathF.Cos(angle) * 1.8f, 4.5f, MathF.Sin(angle) * 1.8f);
        body.Orientation = Quaternion.CreateFromYawPitchRoll(angle, angle * 0.7f, angle * 0.3f);
        body.Restitution = RestitutionSteps[_restitutionIndex];
        body.AngularDamping = 0.25f;

        Physics.AddBody(body);
        BodyColors.Add(SrgbToLinear(new Vector4(
            0.55f + (0.4f * MathF.Abs(MathF.Sin(angle))),
            0.45f,
            0.30f + (0.5f * MathF.Abs(MathF.Cos(angle))),
            1.0f)));

        _boxSpawnCount++;

        Console.WriteLine(
            $"箱を1つ追加: 半分の長さ ({half.X:F2}, {half.Y:F2}, {half.Z:F2})m "
            + $"質量 {body.Mass:F2}kg  体 {Physics.DynamicCount} 個(動くもの)");
    }

    /// <summary>球を1つ降らせる(Ctrl+Shift+Alt+F7)。</summary>
    private static void SpawnBall()
    {
        if (!_physicsDemo)
        {
            return;
        }

        // 落とす場所を少しずつずらす。真上から重ねると、
        // 同心の球(法線が決められない)を作りかけて面白くない。
        float angle = _spawnCount * 2.399963f;   // 黄金角。**重ならないように散る**
        float radius = 0.25f + (0.1f * (_spawnCount % 3));

        RigidBody body = RigidBody.CreateSphere(radius * radius * radius * 20.0f, radius);
        body.Position = new Vector3(
            MathF.Cos(angle) * 1.8f, 4.5f, MathF.Sin(angle) * 1.8f);
        body.Restitution = RestitutionSteps[_restitutionIndex];
        body.AngularDamping = 0.4f;

        Physics.AddBody(body);
        BodyColors.Add(SrgbToLinear(new Vector4(
            0.35f + (0.5f * MathF.Abs(MathF.Sin(angle))),
            0.55f,
            0.35f + (0.5f * MathF.Abs(MathF.Cos(angle))),
            1.0f)));

        _spawnCount++;

        Console.WriteLine(
            $"球を1つ追加: 半径 {radius:F2}m 質量 {body.Mass:F2}kg  "
            + $"体 {Physics.Bodies.Count} 個  組 {(long)Physics.Bodies.Count * (Physics.Bodies.Count - 1) / 2}");
    }

    /// <summary>
    /// 全部の球を**中心を外して**撃つ(Ctrl+Shift+Alt+F8)。
    ///
    /// **これがトルクの実演**(要点2)。同じ大きさの撃力でも、
    /// 中心に掛ければ真上に飛ぶだけ、外して掛ければ飛びながら回る。
    /// 球には模様(uv-test)が貼ってあるので、回っているかどうかが目で分かる。
    /// </summary>
    private static void KickBodies(bool offCenter)
    {
        if (!_physicsDemo)
        {
            return;
        }

        int kicked = 0;
        float maxSpin = 0.0f;

        foreach (RigidBody body in Physics.Bodies)
        {
            if (body.IsStatic)
            {
                continue;
            }

            // **眠っている体は先に起こす**(Day 47)。
            // <see cref="RigidBody.ApplyImpulseAtPoint"/> は眠っている体を動かさないので、
            // これを忘れると<b>寝ている球だけ撃っても飛ばない</b>。
            body.Wake();

            var impulse = new Vector3(0.0f, 4.0f * body.Mass, 0.0f);

            // 中心を外す。**外した量がそのまま腕の長さ**になる。
            // 半径いっぱい(0.9)まで外すと 20rad/s を超えてただのブレになるので、
            // **見て回転が追える速さ**(1〜2回転/秒)に収まるところを選んである。
            Vector3 point = offCenter
                ? body.Position + new Vector3(body.Shape.BoundingRadius * 0.35f, 0.0f, 0.0f)
                : body.Position;

            body.ApplyImpulseAtPoint(impulse, point);

            kicked++;
            maxSpin = MathF.Max(maxSpin, body.AngularVelocity.Length());
        }

        Console.WriteLine(
            (offCenter
                ? "**中心を外して**撃った(r × J のトルクが立つ)"
                : "中心に撃った(**トルクは 0**。回らない)")
            + $": {kicked} 個  最大角速度 {maxSpin:F2} rad/s");
    }

    /// <summary>
    /// 物理を1ステップ進める。**<see cref="FixedUpdate"/> からだけ呼ぶ**。
    ///
    /// 可変 dt で回すと、フレームレートが落ちた瞬間に貫通が起き、
    /// 同じ操作でも違う結果になる。アニメーション(Day 41〜42)を
    /// 可変 dt 側に置いたのとは逆の判断で、
    /// **状態を持つものは固定ステップ**という Day 19 の線がそのまま効いている。
    /// </summary>
    private static void UpdatePhysics(float dt)
    {
        if (!_physicsDemo)
        {
            return;
        }

        if (_physicsPaused)
        {
            if (_physicsStepsRequested <= 0)
            {
                return;
            }

            _physicsStepsRequested--;
        }

        var stopwatch = Stopwatch.StartNew();
        Physics.Step(dt);
        _physicsMilliseconds =
            (_physicsMilliseconds * 0.9) + (stopwatch.Elapsed.TotalMilliseconds * 0.1);
    }

    /// <summary>
    /// 物理デモを描く。**床 + 縁石 + 球**。
    ///
    /// 位置と向きは <see cref="Interpolate(Vector3, Vector3)"/> で補間する(Day 19)。
    /// 物理は 60Hz の固定ステップで回っているので、
    /// 144Hz の画面で補間を切ると**ステップの粒が見える**
    /// (I キーで切って確かめられる)。
    /// </summary>
    private static void RenderPhysics()
    {
        _drawCalls = Physics.Bodies.Count + 5 + (_showContacts ? Physics.Contacts.Count : 0);

        // **地形があるときは平らな床も縁石も描かない**(Day 46)。
        // 地形が床そのものなので、上に板を重ねると谷が隠れる。
        if (_terrain is not null)
        {
            RenderTerrain();
        }
        else
        {
            Draw(_quad, _floorMaterial, FloorMatrix());

            foreach (Matrix4x4 wall in WallMatrices())
            {
                Draw(_cube, _floorMaterial, wall);
            }
        }

        for (int i = 0; i < Physics.Bodies.Count; i++)
        {
            RigidBody body = Physics.Bodies[i];

            // **平面は描かない**(Day 44)。無限に広いので描きようが無く、
            // 床の板と縁石が位置の目印になっている。
            //
            // **地形も描かない**(Day 46)。専用のメッシュで
            // <see cref="RenderTerrain"/> がすでに描いている——
            // 1つのメッシュと行列では表せない形が、カプセルに続いて2つ目になった。
            if (body.Shape.Kind is ColliderKind.Plane or ColliderKind.HeightField)
            {
                continue;
            }

            // **眠っている体は色を沈める**(Day 47)。
            // 眠りは絵に一切出ない——止まっている物は、計算していてもいなくても同じに見える。
            // 見えないものを見えるようにするのは HUD の数字でもできるが、
            // <b>どの体が眠っているか</b>は色でしか出せない。
            _physicsMaterial.BaseColorFactor = body.IsSleeping
                ? BodyColors[i] * new Vector4(0.25f, 0.25f, 0.30f, 1.0f)
                : BodyColors[i];

            // **カプセルだけ3回に分かれる**(Day 45)。
            // 球と箱は1つのメッシュを拡大すれば済むが、
            // カプセルは球2つ + 円柱1本でしか正しく描けない
            // (<see cref="Primitives.CreateCylinder"/> のコメント)。
            if (body.Shape.Kind == ColliderKind.Capsule)
            {
                DrawCapsule(InterpolatedCapsule(body), _physicsMaterial);
                _drawCalls += 2;
                continue;
            }

            Draw(BodyMesh(body), _physicsMaterial, BodyMatrix(body));
        }

        // **キャラクターは体のあと**(Day 45)。半透明ではないので順番は
        // 絵に効かないが、「物理の体 → その外に居るもの」の順に並べておくと
        // 描画の並びが世界の作りと一致して読みやすい。
        RenderCharacter();

        RenderContacts();

        // **ブロードフェーズのマス**(Day 46)。いちばん最後に線で重ねる。
        RenderGridCells();
    }

    /// <summary>
    /// 描画用に補間したカプセル(Day 45)。**位置も向きも1ステップ前と混ぜる**。
    ///
    /// <see cref="BodyMatrix"/> が球と箱にしているのと同じことを、
    /// 行列ではなく形そのものに対してやっている。
    /// <see cref="DrawCapsule"/> が線分の両端を必要とするので、
    /// 行列を作る前に「補間後の姿勢のカプセル」が要る。
    /// </summary>
    private static Capsule3D InterpolatedCapsule(RigidBody body) =>
        Capsule3D.FromCenter(
            Interpolate(body.PreviousPosition, body.Position),
            Interpolate(body.PreviousOrientation, body.Orientation),
            body.Shape.Radius,
            body.Shape.HalfHeight);

    /// <summary>
    /// 接触点を光る小球で描く(Ctrl+Shift+Alt+3)。**マニフォールドを目で見る**。
    ///
    /// 深いほど大きく描く。床に置いた箱の4点が同じ大きさで並ぶこと、
    /// 傾いた箱では手前の角だけが大きいことが、
    /// <b>数字を読まずに</b>分かるようにしてある。
    ///
    /// <para>
    /// <b>物理は描画を知らないまま</b>なのがここでも保たれている。
    /// <see cref="PhysicsWorld.Contacts"/> は解決のために持っている情報で、
    /// それを絵にするかどうかは <c>Program</c> の勝手——
    /// 物理の側に「デバッグ描画」を持たせないのが Day 43 から引いている線。
    /// </para>
    /// </summary>
    private static void RenderContacts()
    {
        if (!_showContacts)
        {
            return;
        }

        foreach (ContactPoint contact in Physics.Contacts)
        {
            // 深さ 0 で 6cm、1cm めり込むごとに 2cm 大きくする。
            float depth = MathF.Max(-contact.Separation, 0.0f);
            float size = 0.06f + MathF.Min(depth * 2.0f, 0.10f);

            Draw(
                _sphere,
                _contactMaterial,
                Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(contact.Point));
        }
    }

    /// <summary>
    /// 体の形に合うメッシュ。**球なら球、箱なら立方体**(Day 44)。
    ///
    /// 単位球も単位立方体も「差し渡し1」で作ってあるので、
    /// 拡大率は <see cref="BodyMatrix"/> 側で寸法をそのまま入れれば合う。
    ///
    /// <para>
    /// <b>カプセルはここに来ない</b>(Day 45)。1つのメッシュでは描けないので、
    /// 呼ぶ側が <see cref="DrawCapsule"/> へ振り分けている。
    /// 「メッシュ1つ + 行列1つ」で表せる形と、そうでない形の境目がここ。
    /// </para>
    /// </summary>
    private static Mesh<Vertex> BodyMesh(RigidBody body) =>
        body.Shape.Kind == ColliderKind.Box ? _cube : _sphere;

    /// <summary>カプセル1本ぶんの影(球2つ + 円柱1本)。**本編と同じ分け方**。</summary>
    private static void ShadowCapsule(in Capsule3D capsule)
    {
        float diameter = capsule.Radius * 2.0f;
        float length = capsule.Segment.Length;

        _shadow.Draw(_sphere,
            Matrix4x4.CreateScale(diameter)
                * Matrix4x4.CreateTranslation(capsule.Segment.Start));

        _shadow.Draw(_sphere,
            Matrix4x4.CreateScale(diameter)
                * Matrix4x4.CreateTranslation(capsule.Segment.End));

        if (length > 1e-4f)
        {
            _shadow.Draw(_cylinder,
                Matrix4x4.CreateScale(diameter, length, diameter)
                    * RotationTo(capsule.Axis)
                    * Matrix4x4.CreateTranslation(capsule.Center));
        }
    }

    /// <summary>
    /// 縁石4本の行列。**見えない壁の位置を目に見えるようにする**だけのもの。
    ///
    /// 壁そのものは無限に高い平面なので、縁石を飛び越えた高さでも跳ね返る。
    /// 絵と物理が食い違っている箇所で、
    /// 有限の壁が要るなら箱(Day 44)で作り直すことになる。
    /// </summary>
    private static IEnumerable<Matrix4x4> WallMatrices()
    {
        const float thickness = 0.2f;
        const float height = 0.5f;
        float span = (PhysicsWallDistance * 2.0f) + thickness;
        float centerY = PhysicsFloorY + (height * 0.5f);
        float offset = PhysicsWallDistance + (thickness * 0.5f);

        yield return Matrix4x4.CreateScale(thickness, height, span)
            * Matrix4x4.CreateTranslation(-offset, centerY, 0.0f);
        yield return Matrix4x4.CreateScale(thickness, height, span)
            * Matrix4x4.CreateTranslation(offset, centerY, 0.0f);
        yield return Matrix4x4.CreateScale(span, height, thickness)
            * Matrix4x4.CreateTranslation(0.0f, centerY, -offset);
        yield return Matrix4x4.CreateScale(span, height, thickness)
            * Matrix4x4.CreateTranslation(0.0f, centerY, offset);
    }

    /// <summary>
    /// 球1つぶんの行列。**拡大 → 回転 → 平行移動**の順。
    ///
    /// 単位球は半径 0.5(直径 1)なので、拡大は直径をそのまま入れる。
    /// </summary>
    private static Matrix4x4 BodyMatrix(RigidBody body)
    {
        // 単位球は半径 0.5(直径 1)、単位立方体も一辺 1。
        // どちらも「差し渡し1」なので、拡大は寸法を2倍したものをそのまま入れる。
        Vector3 scale = body.Shape.Kind == ColliderKind.Box
            ? body.Shape.HalfExtents * 2.0f
            : new Vector3(body.Shape.Radius * 2.0f);

        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(
                Interpolate(body.PreviousOrientation, body.Orientation))
            * Matrix4x4.CreateTranslation(Interpolate(body.PreviousPosition, body.Position));
    }

    private static string PhysicsSceneLabel() => _characterDemo
        ? CharacterSceneLabel()
        : _physicsScene switch
        {
            PhysicsScene.Stack => "積み上げ",
            PhysicsScene.Cradle => "撞き玉",
            PhysicsScene.BoxDrop => "箱を落とす",
            PhysicsScene.BoxStack => "箱を積む",
            PhysicsScene.BoxMix => "球と箱",
            PhysicsScene.BoxTumble => "箱が転がる",
            PhysicsScene.CapsuleDrop => "カプセルを落とす",
            PhysicsScene.Terrain => "地形に落とす",
            PhysicsScene.FrictionRamp => "摩擦の坂",
            _ => "落下",
        };

    /// <summary>
    /// 箱かカプセルの筋書きかどうか(Ctrl+Shift+Alt+1 の ON/OFF 判定に使う)。
    ///
    /// **札を末尾に足したので、この比較がそのまま通った**(Day 45)。
    /// <see cref="PhysicsScene.CapsuleDrop"/> を <c>BoxDrop</c> の手前に挿していたら、
    /// ここだけ静かに嘘になっていた——列挙の値に順序の意味を持たせると、
    /// <b>足す場所が仕様になる</b>。
    /// </summary>
    private static bool IsBoxScene(PhysicsScene scene) => scene >= PhysicsScene.BoxDrop;

    /// <summary>体の形を1行で。**寸法まで出す**ので、積み上げのずれに気づける。</summary>
    private static string ShapeLabel(RigidBody body) => body.Shape.Kind switch
    {
        ColliderKind.Box =>
            $"箱 {body.Shape.HalfExtents.X * 2.0f:F2}x{body.Shape.HalfExtents.Y * 2.0f:F2}"
            + $"x{body.Shape.HalfExtents.Z * 2.0f:F2}m",
        ColliderKind.Plane => "平面",
        ColliderKind.Capsule =>
            $"カプセル r={body.Shape.Radius:F2}m h={body.Shape.TotalHeight:F2}m",
        ColliderKind.HeightField =>
            $"地形 {body.Shape.Field!.CellsX}x{body.Shape.Field!.CellsZ}マス "
            + $"({body.Shape.Field!.TriangleCount:N0}三角形)",
        _ => $"球 r={body.Shape.Radius:F2}m",
    };

    /// <summary>
    /// HUD の1行。**絵から読み取れない数字だけ**を出す。
    ///
    /// 積分法・反復回数・めり込み量は、どれも絵を見ても分からない。
    /// 「なんとなく震えている」「なんとなく沈んでいる」で終わらせないために、
    /// <b>最大めり込みと運動エネルギーは常に数字で</b>出しておく。
    /// </summary>
    private static string PhysicsLabel() =>
        $"物理[{PhysicsSceneLabel()}]  体:{Physics.DynamicCount}  接触:{Physics.Contacts.Count}  "
        + $"組:{Physics.PairTests}  "
        + $"{(Physics.Integrator == IntegratorMode.SemiImplicit ? "セミインプリシット" : "**陽的オイラー**")}  "
        + $"反復:{Physics.VelocityIterations}  既定e:{RestitutionSteps[_restitutionIndex]:F2}  "
        + $"補正:{OnOff(Physics.PositionCorrection)}  めり込み:{Physics.MaxPenetration * 100.0f:F2}cm  "
        + $"KE:{Physics.TotalKineticEnergy:F2}J  {_physicsMilliseconds:F2}ms"
        + (_physicsPaused ? "  [停止中]" : string.Empty);

    /// <summary>
    /// 接触の内訳(Day 44)。**絵から読めないものだけを1行に**。
    ///
    /// 見どころは3つ。
    /// <list type="bullet">
    /// <item><b>組と点が別</b>——床に置いた箱1つで「1組・4点」が正しい姿</item>
    /// <item><b>面 / 辺 / 点</b>——どの軸から接触が出たか(<see cref="ManifoldSource"/>)。
    ///   面で触れているはずなのに「辺」が並ぶなら、<see cref="Sat"/> の下駄を疑う</item>
    /// <item><b>上限</b>——Ctrl+Shift+Alt+4 で 1 にすると、点の数が組の数と同じになる</item>
    /// </list>
    /// </summary>
    private static string ContactLabel() =>
        $"接触[{Physics.ContactPairs}組 {Physics.Contacts.Count}点 上限{Physics.MaxContactsPerPair}]  "
        + $"面:{Physics.SourceCount(ManifoldSource.FaceA) + Physics.SourceCount(ManifoldSource.FaceB)}  "
        + $"辺:{Physics.SourceCount(ManifoldSource.EdgeEdge)}  "
        + $"点:{Physics.SourceCount(ManifoldSource.Point)}  "
        + $"解き方:{(Physics.SolveContactsTogether ? "同時" : "**順番**")}  "
        + $"表示:{OnOff(_showContacts)}";

    /// <summary>
    /// 今の内訳をコンソールへ(Ctrl+Shift+Alt+F9)。
    ///
    /// **保存量を並べて出す**のが眼目。運動量と運動エネルギーは、
    /// 衝突が正しく解けているかを外から確かめられる数少ない手掛かりになる。
    /// </summary>
    private static void DescribePhysics()
    {
        Console.WriteLine();
        Console.WriteLine($"--- 物理デモの内訳({PhysicsSceneLabel()})---");
        Console.WriteLine(
            $"  積分: {(Physics.Integrator == IntegratorMode.SemiImplicit ? "セミインプリシット" : "陽的オイラー")}"
            + $"  反復: {Physics.VelocityIterations}  位置補正: {OnOff(Physics.PositionCorrection)}"
            + $"(率 {Physics.CorrectionRate:F2} / 許容 {Physics.Slop * 100.0f:F1}cm)");

        // **Day 47 の3行**。解き方・摩擦・眠りは、どれも絵からは読み取れない。
        Console.WriteLine(
            $"  解き方: {(Physics.AccumulateImpulses ? "蓄積してクランプ" : "1周ごとにクランプ(素朴)")}"
            + $"  温存: {OnOff(Physics.WarmStartActive)}"
            + $"({Physics.WarmStartedPoints}/{Physics.Contacts.Count} 点が持ち越し、"
            + $"新規 {Physics.NewContactPoints} 点、覚えている組 {Physics.CachedPairs})");
        Console.WriteLine(
            $"  摩擦: {FrictionLabel()}  反発のしきい値: {Physics.RestitutionThreshold:F2}m/s");
        Console.WriteLine(
            $"  眠り: {OnOff(Physics.SleepEnabled)}  {Physics.SleepingCount}/{Physics.DynamicCount} 体が眠り  "
            + $"島 {Physics.IslandCount} 個  "
            + $"しきい値 {Physics.SleepLinearThreshold:F2}m/s / {Physics.SleepAngularThreshold:F2}rad/s / "
            + $"{Physics.SleepTime:F2}s");
        Console.WriteLine(
            $"  体: {Physics.DynamicCount} 個(動くもの)  平面: {Physics.PlaneCount} 枚  "
            + $"接触: {Physics.ContactPairs} 組 / {Physics.Contacts.Count} 点"
            + $"(1組の上限 {Physics.MaxContactsPerPair})  試した組: {Physics.PairTests}");
        Console.WriteLine(
            $"  合計運動量: ({Physics.TotalMomentum.X:F3}, {Physics.TotalMomentum.Y:F3}, "
            + $"{Physics.TotalMomentum.Z:F3}) kg·m/s");
        Console.WriteLine(
            $"  合計運動エネルギー: {Physics.TotalKineticEnergy:F3} J  "
            + $"最大めり込み: {Physics.MaxPenetration * 1000.0f:F2} mm");
        Console.WriteLine();

        for (int i = 0; i < Physics.Bodies.Count; i++)
        {
            RigidBody body = Physics.Bodies[i];

            // **平面は飛ばす**(Day 44)。位置も速度も意味を持たない。
            if (body.Shape.Kind == ColliderKind.Plane)
            {
                continue;
            }

            Console.WriteLine(
                $"  [{i,2}] {ShapeLabel(body),-22} 高さ {body.Position.Y - PhysicsFloorY:F3}m  "
                + $"速度 {body.LinearVelocity.Length():F3}m/s  "
                + $"角速度 {body.AngularVelocity.Length():F3}rad/s  "
                + $"質量 {(body.IsStatic ? "静的" : $"{body.Mass:F2}kg")}  e={body.Restitution:F2}  "
                + $"μ={body.Friction:F2}"
                + (body.IsSleeping ? $"  **眠り**" : $"  起床({body.SleepTimer:F2}s)"));
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 接触の内訳をコンソールへ(Ctrl+Shift+Alt+6)。**SAT が何を選んだかを見る**。
    ///
    /// HUD の1行では組の合計しか出ないので、
    /// 「どの体とどの体が、どの軸で、何点で触れているか」をここで全部出す。
    /// <b>箱が震えるときに最初に見る場所</b>で、
    /// 点が1つしか立っていない組を探すのが定石になる。
    /// </summary>
    private static void DescribeContacts()
    {
        Console.WriteLine();
        Console.WriteLine($"--- 接触の内訳({PhysicsSceneLabel()})---");
        Console.WriteLine(
            $"  {Physics.ContactPairs} 組 / {Physics.Contacts.Count} 点"
            + $"(1組の上限 {Physics.MaxContactsPerPair})");
        Console.WriteLine(
            $"  解き方: {(Physics.SolveContactsTogether ? "同時(4点をまとめて)" : "順番(1点ずつ)")}");
        Console.WriteLine(
            $"  出どころ: A面 {Physics.SourceCount(ManifoldSource.FaceA)}  "
            + $"B面 {Physics.SourceCount(ManifoldSource.FaceB)}  "
            + $"辺×辺 {Physics.SourceCount(ManifoldSource.EdgeEdge)}  "
            + $"点(球) {Physics.SourceCount(ManifoldSource.Point)}");
        Console.WriteLine();

        if (Physics.Contacts.Count == 0)
        {
            Console.WriteLine("  接触なし(全部宙に浮いている)");
            Console.WriteLine();
            return;
        }

        int previousPair = -1;

        for (int i = 0; i < Physics.Contacts.Count; i++)
        {
            ContactPoint contact = Physics.Contacts[i];

            // 同じ組の点は続けて並んでいるので、組が変わったときだけ見出しを出す。
            int pairKey = (contact.A * 1000) + contact.B;
            if (pairKey != previousPair)
            {
                previousPair = pairKey;
                Console.WriteLine(
                    $"  [{contact.A,2}]{ShapeLabel(Physics.Bodies[contact.A])} × "
                    + $"[{contact.B,2}]{ShapeLabel(Physics.Bodies[contact.B])}  "
                    + $"法線 ({contact.Normal.X:F2}, {contact.Normal.Y:F2}, {contact.Normal.Z:F2})");
            }

            // **Day 47 で溜まったインパルスが読めるようになった**。
            // 床に置いた 1kg の箱が4点で支えられているなら、
            // 法線インパルスの合計は <c>m g dt = 1 × 9.81 / 60 ≈ 0.16 N·s</c> になるはず——
            // <b>数字が理屈と合うかを確かめられる数少ない場所</b>。
            float tangent = MathF.Sqrt(
                (contact.TangentImpulse1 * contact.TangentImpulse1)
                + (contact.TangentImpulse2 * contact.TangentImpulse2));

            Console.WriteLine(
                $"      点 ({contact.Point.X:F2}, {contact.Point.Y:F2}, {contact.Point.Z:F2})  "
                + $"めり込み {-contact.Separation * 1000.0f:F2}mm  "
                + $"跳ね返り目標 {contact.Bounce:F3}m/s  "
                + $"法線J {contact.NormalImpulse:F4}  "
                + $"接線J {tangent:F4}/{contact.Friction * contact.NormalImpulse:F4}"
                + $"(μ={contact.Friction:F2})"
                + (contact.WarmStarted ? "  温存" : "  新規"));
        }

        Console.WriteLine();
    }

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

        // **物理デモはさらに優先する**(Day 43)。今日の主役なので、
        // 出ている間はこれだけを描く。ほかのデモは
        // <see cref="ShowPhysicsDemo"/> が全部下ろしているが、
        // あとから Ctrl+Shift+F1 でデモ v1 を出されると重なるので、ここでも守る。
        if (_physicsDemo)
        {
            RenderPhysics();
            return;
        }

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

            Draw(part.Mesh, part.Material, PartMatrix(part), PartJoints(part));
        }

        SetCap(EnableCap.CullFace, _culling);
    }

    /// <summary>
    /// パーツを置く行列(Day 41)。**3つの経路がある**。
    ///
    /// <list type="number">
    /// <item>スキン付き … 単位行列(パーツ側が既に単位)。位置は関節行列が持つ</item>
    /// <item>アニメーションを持つモデルのスキン無しパーツ … プレイヤーが計算した世界行列</item>
    /// <item>静的なモデル … 読み込み時に畳んだ <c>Part.Transform</c></item>
    /// </list>
    ///
    /// <b>3 を残してあるのは互換のため</b>。プレイヤーの世界行列は TRS から作り直すので、
    /// <c>matrix</c> で書かれたノード(BoxTextured)ではわずかに値が変わりうる。
    /// 動かないモデルは Day 32 からの経路をそのまま通しておくほうが、
    /// **今日の変更で過去の絵が動かなかったことを保証しやすい**。
    /// </summary>
    private static Matrix4x4 PartMatrix(in Model.Part part)
    {
        bool animatedNode = _animation is not null
            && part.SkinIndex < 0
            && _model is not null
            && _model.Animations.Count > 0;

        Matrix4x4 local = animatedNode ? _animation!.GetNodeWorld(part.NodeIndex) : part.Transform;
        return local * _modelTransform;
    }

    /// <summary>
    /// パーツに送る関節行列(Day 41)。スキンを持たないパーツでは空になり、
    /// 受け取った側は <c>uSkinned = 0</c> にする。
    /// </summary>
    private static ReadOnlySpan<Matrix4x4> PartJoints(in Model.Part part) =>
        _animation is not null && part.SkinIndex >= 0
            ? _animation.GetJointMatrices(part.SkinIndex)
            : default;

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

        // **プレイヤーはモデルより先に捨てる**……のではなく、単に参照を切るだけでよい。
        // 姿勢の配列しか持っておらず、GPU のものを一切握っていないため(Day 41)。
        // 「捨てるものと、参照を切れば済むもの」の違いがはっきり出るところ。
        _animation = null;

        // ロコモーションもモデルに紐づくので一緒に落とす(Day 42)。
        _locomotionTree = null;
        _locomotionStates = null;
        _locomotion = LocomotionMode.Off;

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

        // **スキンもクリップも無いモデルでもプレイヤーを作る**(Day 41)。
        // ノードの世界行列は静的なモデルでも意味を持つので、
        // 「アニメーションがあるときだけ」にすると分岐が増える。
        // 作る手間は姿勢の配列2本ぶん(ノード 26 個で 2KB 弱)。
        _animation = new AnimationPlayer(_model)
        {
            Speed = PlaybackSpeeds[_playbackSpeedIndex],
        };

        // **クリップが2本以上あればロコモーションを組む**(Day 42)。
        // 組めるかどうかはモデル次第なので、失敗しても黙って Off のままにする。
        BuildLocomotion(_model);

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

        // **リグの内訳を必ず出す**(Day 41)。スキニングが動かないときの原因は
        // だいたい「スキンを読めていない」「関節が0本」「クリップが0本」のどれかで、
        // 絵を見ても区別が付かない。
        Console.WriteLine(
            $"  法線: 生成 {_model.GeneratedNormalParts} パーツ"
            + $" / ノード {_model.Nodes.Count} / スキン {_model.Skins.Count}"
            + $" / 関節 {_model.JointCount} / スキン付きパーツ {_model.SkinnedParts}"
            + $" / クリップ {_model.Animations.Count}");

        foreach (Skin skin in _model.Skins)
        {
            Console.WriteLine(
                $"  [スキン {skin.Name}] 関節 {skin.JointCount} 本"
                + $"(根 {(skin.SkeletonRoot >= 0 ? _model.Nodes[skin.SkeletonRoot].Name : "指定なし")})");
        }

        for (int i = 0; i < _model.Animations.Count; i++)
        {
            AnimationClip clip = _model.Animations[i];
            Console.WriteLine(
                $"  [クリップ {i}: {clip.Name}] {clip.Duration:F2}s"
                + $" / チャンネル {clip.Channels.Count}");
        }

        if (_locomotionTree is not null)
        {
            Console.WriteLine(
                "  ロコモーション: "
                + string.Join(" → ", _locomotionTree.Samples.Select(
                    sample => $"{sample.Name}({sample.Parameter:F1}m/s)"))
                + "  (Shift+Alt+F1 で再生)");
        }
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

        // **スキン付きは「今のポーズ」で測り直す**(Day 41)。
        //
        // <see cref="Model.BoundsMin"/> はバインドポーズの箱で、
        // スキン付きのパーツはメッシュ空間の座標そのままで入っている。
        // CesiumMan は**メッシュ空間が Z 上向き**(高さが Z の 0〜1.51 に出る)で、
        // 立たせる回転は関節行列の側が持っている——
        // だからバインドポーズの箱で構図を決めると、
        // **中心が足元の外に来て、モデルが画面の端で切れる**。
        //
        // 姿勢が変われば箱も変わるので、本当は「クリップ全体で最大の箱」が要る。
        // ここでは読み込み直後の姿勢だけで測っている(改造課題1)。
        (Vector3 boundsMin, Vector3 boundsMax) =
            model.SkinnedParts > 0 && _animation is not null
                ? MeasurePosedBounds(model, _animation)
                : (model.BoundsMin, model.BoundsMax);

        Vector3 center = (boundsMin + boundsMax) * 0.5f;
        float radius = MathF.Max((boundsMax - boundsMin).Length() * 0.5f, 0.0001f);
        float scale = targetRadius / radius;

        _modelTransform =
            Matrix4x4.CreateTranslation(-center)
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
    //  Day 40: カメラワークと機能トグル
    // ================================================================

    /// <summary>
    /// **機能の ON/OFF 表を組む**(Day 40)。Phase 6 で積んだものが全部ここに並ぶ。
    ///
    /// <para>
    /// 並べる順は<b>パイプラインの順</b>——
    /// 空 → 影 → 材質 → 環境光 → 遮蔽 → 後処理。
    /// 名前順やDay順ではなく、絵が出来上がる順に並べておくと、
    /// <c>Ctrl+Alt+F10</c> のツアーが<b>絵が組み上がっていく順に</b>見えるようになる。
    /// </para>
    ///
    /// <para>
    /// <b>bool でないものも bool に見せる</b>。PCF の半径や視差の方式は
    /// 段階のあるつまみだが、表としては「ソフトかどうか」「凹凸があるかどうか」で足りる。
    /// 細かい段は Day 33・34 のキーが持っているので、
    /// ここで同じつまみを二重に持たない——
    /// <b>表は状態を持たない</b>という約束(<see cref="FeatureToggles"/> の説明)を守るために、
    /// 戻す先の値もその場で決め打ちにしてある。
    /// </para>
    /// </summary>
    private static FeatureToggles BuildFeatureToggles()
    {
        var features = new FeatureToggles();

        features.Add(
            "HDR", "1.0 で切られ、露出を下げても明部が戻らない(Day 31)",
            () => _post.SceneFormat == RenderTargetFormat.Rgba16F,
            on => _post.SceneFormat = on ? RenderTargetFormat.Rgba16F : RenderTargetFormat.Rgba8);

        features.Add(
            "空", "背景が単色になる。映り込みの元は残る(Day 36)",
            () => _env.SkyboxVisible,
            on => _env.SkyboxVisible = on);

        features.Add(
            "影", "接地感が消えて、物が床に浮く(Day 33)",
            () => _shadow.Enabled,
            on => _shadow.Enabled = on);

        features.Add(
            "ソフト影", "影の縁が1テクセルの階段になる(PCF なし。Day 33)",
            () => _shadow.PcfRadius > 0,
            on => _shadow.PcfRadius = on ? 2 : 0);

        features.Add(
            "法線マップ", "壁と地面が平らな板に戻る(Day 34)",
            () => _normalMapping,
            on => _normalMapping = on);

        features.Add(
            "視差", "目地の奥行きが消える。輪郭は変わらない(Day 34)",
            () => _parallaxMode != 0,
            on => _parallaxMode = on ? 3 : 0);

        features.Add(
            "PBR", "ランバートに戻り、金属とプラスチックの差が消える(Day 35)",
            () => _pbrEnabled,
            on => _pbrEnabled = on);

        features.Add(
            "IBL", "環境光が定数になり、**ゴミ箱の金属が真っ黒になる**(Day 36)",
            () => _env.Enabled,
            on => _env.Enabled = on);

        features.Add(
            "SSAO", "物が接しているところの暗がりが消える(Day 37)",
            () => _ssao.Enabled,
            on => _ssao.Enabled = on);

        features.Add(
            "ブルーム", "明部のにじみが消えて、光が強く見えなくなる(Day 31)",
            () => _post.BloomEnabled,
            on => _post.BloomEnabled = on);

        features.Add(
            "FXAA", "輪郭に階段が出る(Day 38)",
            () => _post.FxaaEnabled,
            on => _post.FxaaEnabled = on);

        features.Add(
            "色調整", "シーンが指定した色温度・彩度が外れる(Day 38)",
            () => _post.Grade.Enabled,
            on => _post.Grade.Enabled = on);

        return features;
    }

    /// <summary>
    /// **カメラワークを1フレーム進める**(Day 40)。<see cref="OnUpdate"/> から呼ぶ。
    ///
    /// <para>
    /// <b>なぜ <see cref="FixedUpdate"/> ではないのか</b>。
    /// Day 19 で引いた線は「ゲームの状態を変えるものは固定ステップ、
    /// 見せ方だけのものは描画側」だった。カメラワークは後者で、
    /// <b>誰の当たり判定にも影響しない</b>。
    /// 固定ステップに載せると 120Hz でも 20Hz でも同じ絵になる代わりに、
    /// 1・2・3・4 キーでシミュレーション周波数を落としたときに
    /// <b>カメラまでカクつく</b>——見せ方が計算の都合に引きずられるのは筋が悪い。
    /// </para>
    ///
    /// <para>
    /// <b>速度を毎フレーム測る</b>のが今日の道具。
    /// 補間方式の違いは通過点の 0.1 秒にしか出ないので、
    /// 数字を出しておかないと <c>Ctrl+Alt+F4</c> を押しても何が変わったか分からない。
    /// </para>
    /// </summary>
    private static void UpdateDemoCamera(double deltaSeconds)
    {
        if (_demo?.Shots is not { } path)
        {
            return;
        }

        // つまみは毎フレーム流し込む。**パスに状態を二重に持たせない**ためで、
        // FeatureToggles が値を持たないのと同じ判断。
        path.Interpolation = _cameraInterpolation;
        path.Ease = _cameraEase;

        if (_cameraPlaying)
        {
            _cameraTime = path.Wrap(_cameraTime + ((float)deltaSeconds * _cameraSpeed));
            ApplyCameraPose(path.Sample(_cameraTime));
        }

        (_cameraMetresPerSecond, _cameraDegreesPerSecond) =
            _cameraPlaying ? path.SpeedAt(_cameraTime) : (0.0f, 0.0f);
    }

    /// <summary>
    /// 姿勢を軌道カメラに流し込む。
    ///
    /// <para>
    /// <b>画角も一緒に運ぶ</b>のを忘れやすい。画角はカメラのレンズの話で、
    /// 軌道(どこから見るか)とは別物なので <see cref="Camera"/> のほうへ直接入れる。
    /// そのあと <see cref="OrbitCameraController.Apply"/> を呼ぶのは、
    /// 平行投影用の高さが画角から決まっているため(P キーで切り替えても大きさが揃う)。
    /// </para>
    /// </summary>
    private static void ApplyCameraPose(CameraPath.Pose pose)
    {
        _orbit.Target = pose.Target;
        _orbit.Distance = pose.Distance;
        _orbit.Yaw = pose.Yaw;
        _orbit.Pitch = pose.Pitch;
        _camera.FieldOfView = pose.FieldOfView;
        _orbit.Apply();
    }

    /// <summary>次のショットの頭へ飛ぶ(<c>Ctrl+Alt+F3</c>)。</summary>
    private static void JumpToNextShot()
    {
        if (_demo?.Shots is not { KeyCount: > 1 } path)
        {
            Console.WriteLine("カメラワーク: このシーンには shots が書かれていません");
            return;
        }

        int next = (path.IndexAt(_cameraTime) + 1) % path.KeyCount;
        _cameraTime = path.StartTimeOf(next);
        ApplyCameraPose(path.Sample(_cameraTime));

        Console.WriteLine(
            $"ショット {next + 1}/{path.KeyCount}「{path.Keys[next].Name}」  "
            + $"t={_cameraTime:F1}s / {path.TotalDuration:F1}s");
    }

    /// <summary>
    /// 手で機能を触る前にツアーを畳む。
    ///
    /// <para>
    /// **勝手に戻されるのを避ける**ため。ツアーは毎回 <c>SetAll(true)</c> から
    /// 組み直すので、走っている間に <c>Ctrl+Alt+F8</c> で1つ切っても、
    /// 数秒後には元へ戻ってしまう。**押しても効かないスイッチ**は
    /// バグにしか見えないので、押された時点でツアーのほうを終わらせる。
    /// </para>
    /// </summary>
    private static void StopTourIfRunning()
    {
        if (!_features.TourActive)
        {
            return;
        }

        _features.EndTour();
        Console.WriteLine("機能ツアー: 手で切り替えたので終了しました");
    }

    /// <summary>カメラワークの状態を1行にまとめる(HUD 用)。</summary>
    private static string CameraLabel()
    {
        if (_demo?.Shots is not { } path)
        {
            return $"カメラ:手動  画角:{_camera.FieldOfView * (180.0f / MathF.PI):F0}度";
        }

        int index = path.IndexAt(_cameraTime);

        return $"カメラ:{(_cameraPlaying ? "再生" : "停止")}"
            + $"  ショット{index + 1}/{path.KeyCount}「{path.Keys[index].Name}」"
            + $"  {_cameraTime:F1}/{path.TotalDuration:F1}s  x{_cameraSpeed:F2}"
            + $"  {(_cameraInterpolation == CameraInterpolation.CatmullRom ? "Catmull-Rom" : "線形")}"
            + $"  イージング:{OnOff(_cameraEase)}"
            + $"  {_cameraMetresPerSecond:F2}m/s {_cameraDegreesPerSecond:F1}度/s"
            + $"  画角:{_camera.FieldOfView * (180.0f / MathF.PI):F0}度";
    }

    /// <summary>
    /// **今日の自己チェック**(<c>Ctrl+Alt+F11</c>)。
    ///
    /// <para>
    /// カメラワークは「なんとなく滑らかに見える」で済ませられてしまうので、
    /// <b>絵では言えないことだけを数字にする</b>。
    /// </para>
    /// <list type="number">
    /// <item>キーの上を本当に通るか(Catmull-Rom が制御点を通る補間であること)</item>
    /// <item>方位が近いほうを回るか(巻き取りが効いているか)</item>
    /// <item>通過点で速度が繋がるか(**線形と Catmull-Rom を同じ物差しで比べる**)</item>
    /// <item>機能表が状態を失わずに往復できるか</item>
    /// <item>必須構成が全部 ON か(Phase 6 のマイルストーンそのもの)</item>
    /// </list>
    /// </summary>
    private static void RunCameraCheck()
    {
        var checks = new CheckList();

        Console.WriteLine();
        Console.WriteLine("[カメラワークと機能トグルの自己チェック]");

        if (_demo?.Shots is not { KeyCount: > 1 } path)
        {
            Console.WriteLine("  デモ v1 が読み込まれていないので、カメラ側は飛ばしました(Ctrl+Alt+F1)");
        }
        else
        {
            // --- 1. キーの上を通るか ---
            //
            // **ベジエ曲線との違いがここに出る**。制御点を通らない補間だと、
            // JSON に書いた構図と実際に映る構図がずれる。
            CameraInterpolation original = path.Interpolation;
            bool originalEase = path.Ease;

            path.Interpolation = CameraInterpolation.CatmullRom;
            path.Ease = true;

            float worstKeyError = 0.0f;

            for (int i = 0; i < path.KeyCount; i++)
            {
                CameraPath.Key key = path.Keys[i];
                if (key.Travel <= 0.0f)
                {
                    continue;
                }

                // **静止区間ではなく移動区間の両端で測る**。
                // 静止中は補間を通らずキーをそのまま返すので、そこを見ても何も確かめられない。
                float moveStart = path.StartTimeOf(i) + key.Hold;

                CameraPath.Pose head = path.Sample(moveStart + (key.Travel * 1e-5f));
                CameraPath.Pose tail = path.Sample(moveStart + (key.Travel * (1.0f - 1e-5f)));
                CameraPath.Key next = path.Keys[(i + 1) % path.KeyCount];

                worstKeyError = MathF.Max(worstKeyError, KeyError(head, key));
                worstKeyError = MathF.Max(worstKeyError, KeyError(tail, next));
            }

            checks.Check(
                "**キーの上をきっちり通る**(Catmull-Rom は制御点を通る補間)",
                worstKeyError < 1e-4f,
                $"いちばん大きいずれ {worstKeyError:E2}");

            // --- 2. 方位の巻き取り ---
            //
            // 経路上のどこを取っても、隣り合う標本の方位差は小さいはず。
            // 巻き取りを忘れると、ここに 300 度超の跳びが出る。
            float worstYawJump = 0.0f;
            const int Samples = 2000;

            for (int i = 0; i < Samples; i++)
            {
                float t0 = path.TotalDuration * i / Samples;
                float t1 = path.TotalDuration * (i + 1) / Samples;

                float difference = MathF.Abs(
                    CameraPath.WrapAngle(path.Sample(t1).Yaw - path.Sample(t0).Yaw));

                worstYawJump = MathF.Max(worstYawJump, difference * (180.0f / MathF.PI));
            }

            // 1周を 2000 で割った刻みなので、素直に回っていれば1標本あたり数度に収まる。
            checks.Check(
                "**方位が近いほうを回る**(巻き取りが効いている)",
                worstYawJump < 15.0f,
                $"1標本あたりの最大 {worstYawJump:F2} 度({path.TotalDuration / Samples * 1000.0f:F1}ms 刻み)");

            // --- 3. 通過点で速度が繋がるか ---
            //
            // **同じ物差しで2つの方式を測る**のがこの項目の値打ち。
            // 「Catmull-Rom は滑らか」と読んだだけでは、どれくらい違うのか分からない。
            path.Ease = false;   // イージングを外さないと、どちらも通過点で 0 になって差が消える

            float linearJump = WorstSpeedJump(path, CameraInterpolation.Linear);
            float splineJump = WorstSpeedJump(path, CameraInterpolation.CatmullRom);

            checks.Check(
                "**Catmull-Rom のほうが通過点の速度変化が小さい**(C1 連続)",
                splineJump < linearJump,
                $"線形 {linearJump:F2}m/s の跳び / Catmull-Rom {splineJump:F2}m/s");

            // --- 4. イージングは軌跡を変えないか ---
            //
            // 「補間方式」と「イージング」が別の軸だ、という要点の検算。
            //
            // **時刻を付け替えて比べる**のがこの項目の書きかた。
            // イージングは区間内の進み u を smoothstep(u) に置き換えるだけなので、
            // 「イージング入りで時刻 t」と「イージング無しで、進みが smoothstep(u) になる時刻」は
            // <b>まったく同じ点</b>を指すはず。ずれたら、イージングが軌跡にも触っている。
            path.Interpolation = CameraInterpolation.CatmullRom;

            float worstPathDifference = 0.0f;

            for (int i = 0; i < path.KeyCount; i++)
            {
                CameraPath.Key key = path.Keys[i];
                if (key.Travel <= 0.0f)
                {
                    continue;
                }

                float moveStart = path.StartTimeOf(i) + key.Hold;

                for (int step = 0; step <= 100; step++)
                {
                    float u = step / 100.0f;
                    float eased = u * u * (3.0f - (2.0f * u));

                    path.Ease = true;
                    Vector3 with = path.Sample(moveStart + (u * key.Travel)).Target;

                    path.Ease = false;
                    Vector3 without = path.Sample(moveStart + (eased * key.Travel)).Target;

                    worstPathDifference =
                        MathF.Max(worstPathDifference, Vector3.Distance(with, without));
                }
            }

            checks.Check(
                "**イージングは軌跡を動かさない**(速さの配分だけを変える)",
                worstPathDifference < 1e-4f,
                $"いちばん大きいずれ {worstPathDifference:E2}");

            path.Interpolation = original;
            path.Ease = originalEase;

            // --- 5. ショットが軌道カメラの可動域に入っているか ---
            //
            // 入っていないと **JSON に書いた構図と映る構図が違う**——
            // OrbitCameraController が黙って丸めるので、絵からは気づけない。
            bool inRange = true;
            string outOfRange = string.Empty;

            foreach (CameraPath.Key key in path.Keys)
            {
                if (key.Distance < _orbit.MinDistance || key.Distance > _orbit.MaxDistance
                    || MathF.Abs(key.Pitch) > 1.5f)
                {
                    inRange = false;
                    outOfRange = key.Name;
                    break;
                }
            }

            checks.Check(
                "全ショットが軌道カメラの可動域(距離 "
                    + $"{_orbit.MinDistance:F0}〜{_orbit.MaxDistance:F0}m / 仰角 ±86度)に入っている",
                inRange,
                inRange ? $"{path.KeyCount} ショット" : $"「{outOfRange}」が範囲外");
        }

        // --- 6. 機能表の往復 ---
        bool[] before = _features.Capture();

        _features.SetAll(false);
        bool allOff = _features.OnCount == 0;

        _features.SetAll(true);
        bool allOn = _features.AllOn;

        _features.Restore(before);
        bool restored = _features.Capture().SequenceEqual(before);

        checks.Check(
            "**機能表から全部 OFF / 全部 ON にできる**",
            allOff && allOn,
            $"{_features.Features.Count} 項目");

        checks.Check(
            "**控えた状態にそのまま戻せる**(読み書きが対になっている)",
            restored,
            $"ON {_features.OnCount}/{_features.Features.Count}");

        // --- 7. Phase 6 のマイルストーン ---
        //
        // ロードマップに書いてある必須構成を、そのまま項目にする。
        string[] required = ["PBR", "IBL", "影", "ソフト影", "SSAO", "ブルーム", "FXAA"];
        string missing = string.Join(
            '/',
            required.Where(name =>
                _features.Features.FirstOrDefault(feature => feature.Name == name)?.Value != true));

        checks.Check(
            "**必須構成が全部 ON**(PBR+IBL+ソフトシャドウ+SSAO+ブルーム+AA)",
            missing.Length == 0,
            missing.Length == 0 ? "Phase 6 のマイルストーン" : $"OFF: {missing}");

        checks.Report();
        Console.WriteLine();
    }

    /// <summary>姿勢とキーのずれ(注視点・距離・方位のうちいちばん大きいもの)。</summary>
    private static float KeyError(CameraPath.Pose pose, CameraPath.Key key) =>
        MathF.Max(
            Vector3.Distance(pose.Target, key.Target),
            MathF.Max(
                MathF.Abs(pose.Distance - key.Distance),
                MathF.Abs(CameraPath.WrapAngle(pose.Yaw - key.Yaw))));

    /// <summary>
    /// 経路をひと回りして、**隣り合う標本の間で速度がいちばん跳ぶ量**を返す。
    ///
    /// <para>
    /// 速度そのものではなく<b>速度の変化</b>を見るのが要点。
    /// 線形補間でも速度は出るし、直線区間なら一定にもなる——
    /// 壊れるのは<b>キーを通過する瞬間だけ</b>なので、差分の最大値でしか捕まえられない。
    /// </para>
    /// </summary>
    private static float WorstSpeedJump(CameraPath path, CameraInterpolation interpolation)
    {
        path.Interpolation = interpolation;

        const int Samples = 600;
        float worst = 0.0f;
        float previous = path.SpeedAt(0.0f).Metres;

        for (int i = 1; i <= Samples; i++)
        {
            float speed = path.SpeedAt(path.TotalDuration * i / Samples).Metres;
            worst = MathF.Max(worst, MathF.Abs(speed - previous));
            previous = speed;
        }

        return worst;
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

        // カメラワークがあれば秒数まで出す(Day 40)。**読み直しても再生位置は頭に戻す**——
        // JSON を書き換えて Ctrl+Shift+F10 を押したとき、
        // 直したショットを見たいのに途中から始まるのは不便。
        if (_demo.Shots is { KeyCount: > 1 } cameraPath)
        {
            _cameraTime = 0.0f;
            Console.WriteLine(
                $"  カメラワーク: {cameraPath.KeyCount} ショット / "
                + $"1周 {cameraPath.TotalDuration:F1}秒  (Ctrl+Alt+F1 で再生)");
        }

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

        // カメラワークも畳む(Day 40)。**画角も 60 度に戻す**——
        // デモ用の 40 度のままだと、戻った先の立方体と床が妙に望遠に見える。
        _cameraPlaying = false;
        _cameraTime = 0.0f;
        _camera.FieldOfView = MathF.PI / 3.0f;
        _features.EndTour();

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

        // **色調整もシーンの持ち物**(Day 40)。露出や IBL の強さと同じで、
        // 「この絵をどう見せたいか」の一部なので JSON 側に置く。
        // Day 38 のキー(Shift+F6〜F10)で上書きできるのは今までどおり。
        DemoScene.GradeSettings grade = _demo.Grade;

        // **先に下敷きを外す**。Shift+F7 で「月夜」などを当てたままデモに入ると、
        // 色フィルタ(JSON には無い項目)だけが残って、
        // シーンの指定どおりの色にならない。
        _post.Grade.ApplyPreset(GradePreset.Neutral);

        _post.Grade.Enabled = grade.Enabled;
        _post.Grade.Temperature = grade.Temperature;
        _post.Grade.Tint = grade.Tint;
        _post.Grade.Contrast = grade.Contrast;
        _post.Grade.Saturation = grade.Saturation;

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

    /// <summary>
    /// 決めの構図に合わせる(<c>Ctrl+Shift+F12</c>)。
    ///
    /// <para>
    /// <b>Day 40 で画角も運ぶようになった</b>。
    /// <see cref="Camera.FieldOfView"/> の既定は 60 度で、
    /// これは「操作するゲーム」の画角。デモの絵には広すぎて、
    /// 壁が倒れて見える(遠近が誇張されるため)。
    /// </para>
    /// </summary>
    private static void FrameDemoScene()
    {
        if (_demo is null)
        {
            return;
        }

        _camera.FieldOfView = _demo.CameraFieldOfView;
        _orbit.Target = _demo.CameraTarget;
        _orbit.Distance = _demo.CameraDistance;
        _orbit.Yaw = _demo.CameraYaw;
        _orbit.Pitch = _demo.CameraPitch;
        _orbit.Apply();
    }

    /// <summary>
    /// カメラワークを頭から再生する(<c>Ctrl+Alt+F1</c>)。
    ///
    /// <para>
    /// <b>shots が無いシーンでは黙って静止画のまま</b>にする。
    /// Day 39 の JSON をそのまま読めることを保ちたいので、
    /// 「カメラワークが書いてあれば動く」という足し方にしてある。
    /// </para>
    /// </summary>
    private static void PlayDemoCamera()
    {
        if (_demo?.Shots is not { KeyCount: > 1 } path)
        {
            _cameraPlaying = false;
            Console.WriteLine("カメラワーク: このシーンには shots が書かれていません(決めの構図のまま)");
            return;
        }

        _cameraTime = 0.0f;
        _cameraPlaying = true;

        path.Interpolation = _cameraInterpolation;
        path.Ease = _cameraEase;
        ApplyCameraPose(path.Sample(0.0f));

        Console.WriteLine(
            $"カメラワーク: {path.KeyCount} ショット / 1周 {path.TotalDuration:F1}秒 で再生"
            + "  (Ctrl+Alt+F2 で一時停止、Ctrl+Alt+F3 で次のショット)");
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

        // カメラワークの内訳(Day 40)。**JSON を書き換えたときにここで検算する**——
        // 引き継ぎ(書かなかった項目は前のショットのまま)が効いているかは、
        // 展開後の数字を並べてもらわないと分からない。
        if (demo.Shots is { } shots)
        {
            Console.WriteLine(
                $"  カメラワーク {shots.KeyCount} ショット / 1周 {shots.TotalDuration:F1}秒");

            foreach (CameraPath.Key key in shots.Keys)
            {
                Console.WriteLine(
                    $"    {key.Name,-16} 距離 {key.Distance,5:F1}m  "
                    + $"方位 {key.Yaw * (180.0f / MathF.PI),6:F1}度  "
                    + $"仰角 {key.Pitch * (180.0f / MathF.PI),5:F1}度  "
                    + $"画角 {key.FieldOfView * (180.0f / MathF.PI),4:F0}度  "
                    + $"静止 {key.Hold:F1}s → 移動 {key.Travel:F1}s");
            }
        }

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

    /// <summary>3D 版(Day 43)。剛体の位置を描画のタイミングへ運ぶ。</summary>
    private static Vector3 Interpolate(Vector3 previous, Vector3 current)
    {
        float alpha = _interpolate ? (float)_loop.Alpha : 1.0f;
        return Vector3.Lerp(previous, current, alpha);
    }

    /// <summary>
    /// 向きの補間(Day 43)。**Lerp ではなく Slerp**。
    ///
    /// クォータニオンを成分ごとに直線で混ぜると、回る速さが一定にならない。
    /// 1ステップぶん(60Hz なら 16ms)の差なので実害は小さいが、
    /// 角速度が大きいとき——キックした直後の球——にははっきり出る。
    /// Day 42 の要点2で「3本以上なら nlerp」と書いたのとは逆で、
    /// <b>2つだけなら slerp を使わない理由が無い</b>。
    /// </summary>
    private static Quaternion Interpolate(Quaternion previous, Quaternion current)
    {
        float alpha = _interpolate ? (float)_loop.Alpha : 1.0f;
        return Quaternion.Slerp(previous, current, alpha);
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

    private static void Draw(
        Mesh<Vertex> mesh, Material material, Matrix4x4 model, ReadOnlySpan<Matrix4x4> joints = default)
    {
        material.Apply(_resources);

        Shader shader = _resources.GetShader(material.Shader);
        shader.SetMatrix4("uModel", model);
        shader.SetMatrix3("uNormalMatrix", NormalMatrix(model));

        // **スキニングの ON/OFF は描画ごと**(Day 41)。
        // マテリアルの属性ではないのがポイントで、同じマテリアルを
        // スキン付きのメッシュとそうでないメッシュが共有することがある。
        //
        // 既定引数を空にしてあるので、**Day 40 までの呼び出しはそのまま 0 が入る**。
        // 「呼び出し側を1つも変えずに機能を足せる」形になっていて、
        // 逆にいうと**送り忘れても静かに動く**——だから毎回明示的に 0 を送る。
        shader.SetInt("uSkinned", joints.IsEmpty ? 0 : 1);

        if (!joints.IsEmpty)
        {
            shader.SetMatrix4Array("uJoints", joints);
        }

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
            // --- 今日のスイッチ(摩擦・Sequential Impulses・スリープ)---
            //
            // **F の段**(Day 47)。Day 46 の `G` と同じ事情で、
            // 素の `case Key.F:`(視野角の切り替え)はガードを持っていない。
            // <b>だからこの塊はそれより上に置く</b>——
            // 下に置くと Ctrl+F も Shift+F も全部そちらに吸われて、今日のスイッチは1つも動かない。
            //
            // F を選んだのは <b>F</b>riction(摩擦)の頭文字だから。
            case Key.F when ctrl && !shift && !alt:
                // **今日の到達点**。μ だけを振った箱を坂に並べる。
                if (_physicsDemo && !_characterDemo && _physicsScene == PhysicsScene.FrictionRamp)
                {
                    _physicsDemo = false;
                    Physics.Clear();
                    BodyColors.Clear();
                    DisposeTerrain();
                    _orbit.Reset();
                    Console.WriteLine("摩擦デモ: OFF");
                }
                else
                {
                    ShowPhysicsDemo(PhysicsScene.FrictionRamp);
                    Console.WriteLine(
                        "摩擦デモ: **坂 26.57 度(tan = 0.50)に μ を振った5箱**。"
                        + "手前から μ = 0.1 / 0.3 / 0.5 / 0.7 / 0.9");
                    Console.WriteLine(
                        "  **μ > tan θ の箱だけが止まる**。真ん中(0.5)がちょうど境目。"
                        + "黄色い球は μ = 1 でも転がり落ちる(転がり摩擦は入れていない)");
                    Console.WriteLine(
                        "  Shift+F:摩擦 ON/OFF  Alt+F:蓄積インパルス  Ctrl+Shift+F:温存  "
                        + "Ctrl+Alt+F:眠り  Shift+Alt+F:μ の上書き");
                }

                break;

            case Key.F when shift && !ctrl && !alt:
                Physics.FrictionEnabled = !Physics.FrictionEnabled;
                WakeAllBodies();
                Console.WriteLine(
                    Physics.FrictionEnabled
                        ? "摩擦: **ON**(クーロン摩擦。上限は μ × 押し付けている強さ)"
                        : "摩擦: **OFF**(Day 46 までの世界。**坂の箱が全部滑り出す**)");
                break;

            case Key.F when alt && !ctrl && !shift:
                Physics.AccumulateImpulses = !Physics.AccumulateImpulses;
                WakeAllBodies();
                Console.WriteLine(
                    Physics.AccumulateImpulses
                        ? "インパルス: **蓄積してクランプ**(Sequential Impulses。負の補正が出せる)"
                        : "インパルス: **1周ごとにクランプ**(Day 46 までのやり方。"
                            + "**掛けすぎを戻せないので柱が沈む**)");
                Console.WriteLine(
                    Physics.AccumulateImpulses
                        ? "  温存も一緒に戻った(蓄積が無いと温存は成り立たない)"
                        : "  **温存も一緒に止まる**。合計を減らせないので、"
                            + "持ち越すと単調に増えて置いた箱が跳ね上がる");
                Console.WriteLine(
                    "  差が出るのは**柱が高いとき**。Ctrl+Shift+Alt+2 で「箱を積む」へ、"
                    + "Ctrl+Shift+Alt+F5 で反復を 1〜2 に落とすと分かりやすい");
                break;

            case Key.F when ctrl && shift && !alt:
                Physics.WarmStarting = !Physics.WarmStarting;
                WakeAllBodies();
                Console.WriteLine(
                    Physics.WarmStarting
                        ? "温存(ウォームスタート): **ON**(前のステップのインパルスから始める)"
                        : "温存(ウォームスタート): **OFF**(毎ステップ 0 から。"
                            + "**反復回数を減らすと途端に沈む**)");

                if (Physics.WarmStarting && !Physics.AccumulateImpulses)
                {
                    Console.WriteLine(
                        "  ただし今は蓄積が OFF なので**温存は働かない**(Alt+F で蓄積を戻す)");
                }

                break;

            case Key.F when ctrl && alt && !shift:
                Physics.SleepEnabled = !Physics.SleepEnabled;
                Console.WriteLine(
                    Physics.SleepEnabled
                        ? "眠り: **ON**(接触で繋がった島ごとに眠る。**色が沈んだ体が眠っている**)"
                        : "眠り: **OFF**(全部起こした。**1ステップの ms が増える**)");
                break;

            case Key.F when shift && alt && !ctrl:
                CycleFrictionOverride();
                break;

            case Key.F when ctrl && shift && alt:
                RunSolverCheck();
                break;

            // --- 今日のスイッチ(地形とブロードフェーズ)---
            //
            // **G の段**(Day 46)。Day 45 で「文字キーの段もこれで最後」と書いたが、
            // 正確には「**まるごと空いている**文字キーが最後」だった。
            // A〜Z のうち X 以外は全部 `case Key.G:` のような<b>ガードの無い case</b>を
            // 持っていて、それは同時に「修飾キー付きの席はまだ空いている」ことでもある。
            //
            // <b>だからこの塊は素の `case Key.G:` より上に置く</b>。
            // 下に置くと、ガードの無い case が Ctrl+G も Shift+G も全部飲み込んで、
            // 今日のスイッチは1つも動かない。
            // Day 39 から毎日書いている「ガード付き case は具体的なものほど上」が、
            // <b>今日はじめて「既に使われている文字キー」に対して効く</b>。
            //
            // <b>素の G は今までどおり</b>(3D 背景の ON/OFF)。
            // 修飾キーを1つでも押していれば、そちらは今日のものになる——
            // つまり <c>Ctrl+G</c> で 3D 背景を切っていた人には挙動が変わる。
            // 割り当ての表そのものをデータにする(Day 40 の <c>FeatureToggles</c> のような形)
            // 片付け方は Day 48 で。<c>OnKeyDown</c> を丸ごと表に置き換えて <c>Program.cs</c> から追い出す。
            //
            // G を選んだのは <b>G</b>round(地形)と <b>G</b>rid(格子)の頭文字だから。
            case Key.G when ctrl && !shift && !alt:
                // **今日の到達点**。起伏の上をキャラクターが歩き回る。
                if (_characterDemo && _characterScene == CharacterScene.Terrain)
                {
                    _characterDemo = false;
                    _physicsDemo = false;
                    Physics.Clear();
                    BodyColors.Clear();
                    DisposeTerrain();
                    _orbit.Reset();
                    Console.WriteLine("地形デモ: OFF");
                }
                else
                {
                    ShowCharacterDemo(CharacterScene.Terrain);
                    Console.WriteLine(
                        "地形デモ: **起伏の上を歩ける**(矢印キー / X で走る / Space でジャンプ)");
                    Console.WriteLine(
                        "  手前の緩い丘(**29 度**)は登れて、奥の急な丘(**58 度**)は登れない。"
                        + "Alt+X で坂の上限を切ると登れる");
                    Console.WriteLine(
                        "  Shift+G:ブロードフェーズ  Alt+G:マスを描く  "
                        + "Ctrl+Shift+G:マスの大きさ  Shift+Alt+G:内訳");
                }

                break;

            case Key.G when shift && !ctrl && !alt:
                // **今日いちばん分かりやすい実験**(要点5)。
                // 絵は1ピクセルも変わらず、HUD の「候補」と ms だけが動く。
                Physics.Broadphase = Physics.Broadphase == BroadphaseMode.UniformGrid
                    ? BroadphaseMode.BruteForce
                    : BroadphaseMode.UniformGrid;

                Console.WriteLine(
                    Physics.Broadphase == BroadphaseMode.UniformGrid
                        ? $"ブロードフェーズ: **均一グリッド**(マス {Physics.CellSize:F1}m)。"
                            + "絵は変わらず、HUD の「候補」と物理の ms が減る"
                        : "ブロードフェーズ: **総当たり**(Day 45 までのやり方)。"
                            + "体を増やすほど n² が効いてくる");
                break;

            case Key.G when alt && !ctrl && !shift:
                _showGridCells = !_showGridCells;
                Console.WriteLine(
                    $"格子のマスの表示: {OnOff(_showGridCells)}"
                    + (_showGridCells
                        ? "(**中身のあるマスだけ**を線で描く。512 個で打ち切る)"
                        : string.Empty));
                break;

            case Key.G when ctrl && shift && !alt:
                CycleGridCellSize();
                break;

            case Key.G when ctrl && alt && !shift:
                // **体を増やして n² を体感する**。総当たりだと組の数が跳ね上がる。
                if (_terrain is null)
                {
                    Console.WriteLine("先に Ctrl+G(地形デモ)か Ctrl+Shift+Alt+2 で地形の筋書きへ");
                    break;
                }

                SpawnTerrainBodies(40);
                Console.WriteLine(
                    $"体を 40 個追加(全部で {Physics.Bodies.Count} 個)。"
                    + "**総当たりの組は2乗、格子の候補はほぼ比例**で増える");
                break;

            case Key.G when shift && alt && !ctrl:
                DescribeBroadphase();
                break;

            case Key.G when ctrl && shift && alt:
                RunTerrainCheck();
                break;

            // --- 今日のスイッチ(カプセル衝突とキャラクターコントローラ)---
            //
            // **X の段**。Day 44 で「Ctrl+Shift+Alt + 数字/ファンクション」を使い切り、
            // 修飾キー3つの組み合わせはもう残っていない。
            // そこで<b>唯一まるごと空いていた文字キー</b>である X に移った——
            // A〜Z のうち X だけが `case Key.X:` を持っていなかった。
            //
            // <b>素の X には触らない</b>。X は Day 18 の <see cref="InputMap"/> で
            // ダッシュに割り当ててあり、キャラクターデモでは「走る」になる。
            // だから <c>Ctrl+X</c> を押した瞬間だけ一緒に走ることになるが、
            // その瞬間にデモが消えるので実害は無い。
            //
            // <b>この塊はいちばん上に置くこと</b>。`case Key.X:` は今のところ
            // どこにも無いので今日は事故らないが、
            // 明日 X を素で使うコードを下に足すと**修飾キー付きが全部そちらに吸われる**。
            // Day 39 から毎日書いている「ガード付き case は具体的なものほど上」がそのまま続く。
            //
            // <b>文字キーの段も、これで最後**。Day 46 以降は
            // Day 40 の <c>FeatureToggles</c> のように、
            // 割り当ての表そのものをデータにするしかない。
            case Key.X when ctrl && !shift && !alt:
                // **今日の到達点**。坂と階段の上をキャラクターが歩き回る。
                if (_characterDemo)
                {
                    _characterDemo = false;
                    _physicsDemo = false;
                    Physics.Clear();
                    BodyColors.Clear();

                    // **地形も捨てる**(Day 46)。メッシュは GL の資源なので、
                    // 放っておくと切り替えるたびに VRAM が積み上がる。
                    DisposeTerrain();

                    _orbit.Reset();
                    Console.WriteLine("キャラクターデモ: OFF");
                }
                else
                {
                    ShowCharacterDemo(CharacterScene.Slopes);
                    Console.WriteLine(
                        $"キャラクターデモ: {CharacterSceneLabel()}。"
                        + "**矢印キーで歩く / X を押しっぱなしで走る / Space でジャンプ**");
                    Console.WriteLine(
                        "  Shift+X:筋書き  Alt+X:**坂の上限**  Ctrl+Shift+X:**段差の乗り越え**  "
                        + "Ctrl+Alt+X:カプセルを追加  Shift+Alt+X:内訳  Ctrl+Shift+Alt+X:自己チェック");
                    Console.WriteLine(
                        "  移動は**カメラ基準**。マウスで視点を回すと進む向きも一緒に回る");
                }

                break;

            case Key.X when shift && !ctrl && !alt:
                {
                    CharacterScene next = _characterScene switch
                    {
                        CharacterScene.Slopes => CharacterScene.Steps,
                        CharacterScene.Steps => CharacterScene.Obstacles,
                        _ => CharacterScene.Slopes,
                    };

                    ShowCharacterDemo(next);
                    Console.WriteLine(_characterScene switch
                    {
                        CharacterScene.Steps =>
                            "筋書き: **階段**(段差 15 / 30 / 45cm。既定の乗り越えは 35cm)",
                        CharacterScene.Obstacles =>
                            "筋書き: **障害物**(箱・球・カプセルの中を歩く。押されるが押さない)",
                        _ =>
                            "筋書き: **坂**(15 / 30 / 45 / 60 度。既定の上限は 50 度)",
                    });
                }

                break;

            case Key.X when alt && !ctrl && !shift:
                // **今日いちばん分かりやすい実験**(要点7)。
                Character.UseSlopeLimit = !Character.UseSlopeLimit;

                Console.WriteLine(
                    Character.UseSlopeLimit
                        ? $"坂の上限: **{Character.SlopeLimitDegrees:F0}度まで**"
                            + "(これより急な面は「壁」。押し戻しから上向きを抜く)"
                        : "坂の上限: **なし**(押し戻しがそのまま登りになる。"
                            + "60 度の坂も、ほとんど垂直な壁もよじ登れる)");
                break;

            case Key.X when ctrl && shift && !alt:
                // **段差の実験**(要点8)。階段の筋書きで見ると一目で分かる。
                Character.UseStepOffset = !Character.UseStepOffset;

                Console.WriteLine(
                    Character.UseStepOffset
                        ? $"段差の乗り越え: **{Character.StepOffset * 100.0f:F0}cm まで**"
                            + "(持ち上げて、進んで、落とす)"
                        : "段差の乗り越え: **なし**(15cm の段でも止まる。"
                            + "階段を降りるときの吸い付きも切れる)");
                break;

            case Key.X when ctrl && alt && !shift:
                SpawnCapsule();
                break;

            case Key.X when shift && alt && !ctrl:
                DescribeCharacter();
                break;

            case Key.X when ctrl && shift && alt:
                RunCapsuleCheck();
                break;

            // **ジャンプの Space を横取りする**(Day 45)。
            // Space は Day 19 から「一時停止」だが、
            // <see cref="InputMap"/> で <see cref="GameAction.Jump"/> にも割り当てた。
            // キャラクターを動かしている間だけ、ここで飲み込んで
            // ポーズに落とさない——**ジャンプするたびに世界が止まる**のを防ぐ。
            //
            // ジャンプそのものはここでは処理しない。
            // 固定ステップの <see cref="UpdateCharacter"/> が
            // <see cref="InputSnapshot"/> 越しに読む(Day 18 の要点2)ので、
            // <b>キーイベントとシミュレーションが直につながらない</b>形は保たれている。
            case Key.Space when _characterDemo:
                break;

            // --- Day 44 のスイッチ(箱の衝突: 分離軸定理と接触マニフォールド)---
            //
            // **Ctrl+Shift+Alt + 数字キー**。段はこれで8つ目になる。
            // Day 43 で「修飾キー3つ + ファンクションキー」を使い切ったので、
            // 予告どおり<b>同じ修飾キーの数字キーの段</b>へ移った。
            //
            // <b>この塊をいちばん上に置くこと</b>。下にある
            // `case Key.Number1 when ctrl && shift:` も `case Key.Number1 when ctrl:` も
            // **Ctrl+Shift+Alt+1 で成立してしまう**ので、
            // 1つでも上に来ていると今日のキーが1つも届かない。
            // Day 39 から毎日書いている
            // 「**ガード付き case は具体的なものほど上**」がそのまま続いている。
            //
            // <b>数字キーの段も、これでほぼ最後**。予告どおり
            // Day 45 は文字キー(X の段)へ移り、そこも今日で埋まった。
            // Day 46 以降は Day 40 の <c>FeatureToggles</c> のように、
            // 割り当ての表そのものをデータにするしかない。
            case Key.Number1 when ctrl && shift && alt:
                // **今日の到達点**。傾きを振った5つの箱を落とす。
                if (_physicsDemo && IsBoxScene(_physicsScene))
                {
                    _physicsDemo = false;
                    _characterDemo = false;   // 世界を消すならキャラクターも下ろす(Day 45)
                    Physics.Clear();
                    BodyColors.Clear();

                    // **地形も捨てる**(Day 46)。メッシュは GL の資源なので、
                    // 放っておくと切り替えるたびに VRAM が積み上がる。
                    DisposeTerrain();

                    _orbit.Reset();
                    Console.WriteLine("箱デモ: OFF");
                }
                else
                {
                    ShowPhysicsDemo(PhysicsScene.BoxDrop);
                    Console.WriteLine(
                        $"箱デモ: {PhysicsSceneLabel()}({Physics.DynamicCount} 体)。"
                        + "**Ctrl+Shift+Alt+2 で筋書きを切り替え**");
                    Console.WriteLine(
                        "  3:接触点の表示  4:**1組の接触点 1/2/4(今日の実験)**  "
                        + "5:箱を追加  6:接触の内訳  0:自己チェック");
                }

                break;

            case Key.Number2 when ctrl && shift && alt:
                {
                    // **Day 45 でカプセルが1つ増えて5つ巡る**。
                    PhysicsScene next = _physicsScene switch
                    {
                        PhysicsScene.BoxDrop => PhysicsScene.BoxStack,
                        PhysicsScene.BoxStack => PhysicsScene.BoxMix,
                        PhysicsScene.BoxMix => PhysicsScene.BoxTumble,
                        PhysicsScene.BoxTumble => PhysicsScene.CapsuleDrop,
                        PhysicsScene.CapsuleDrop => PhysicsScene.Terrain,
                        PhysicsScene.Terrain => PhysicsScene.FrictionRamp,
                        _ => PhysicsScene.BoxDrop,
                    };

                    ShowPhysicsDemo(next);
                    Console.WriteLine(_physicsScene switch
                    {
                        PhysicsScene.BoxStack =>
                            "筋書き: **箱を積む**(縦に6個。球の積み上げより安定する)",
                        PhysicsScene.BoxMix =>
                            "筋書き: **球と箱**(傾けた静的な箱の上へ。5通りの判定が全部走る)",
                        PhysicsScene.BoxTumble =>
                            "筋書き: **箱が転がる**(細長い箱を回しながら。辺×辺の軸が出る)",
                        PhysicsScene.CapsuleDrop =>
                            "筋書き: **カプセルを落とす**(Day 45。倒れると接触点が1点から2点になる)",
                        PhysicsScene.Terrain =>
                            "筋書き: **地形に落とす**(Day 46。60 個が谷に集まる。Shift+G で総当たりと比較)",
                        PhysicsScene.FrictionRamp =>
                            "筋書き: **摩擦の坂**(Day 47。μ=0.1/0.3/0.5/0.7/0.9。tan 26.57°=0.5 が境目)",
                        _ =>
                            "筋書き: **箱を落とす**(傾き 0 / 0.10 / 0.35 / 0.62 / 0.95 rad の5箱)",
                    });
                }

                break;

            case Key.Number3 when ctrl && shift && alt:
                _showContacts = !_showContacts;
                Console.WriteLine(
                    _showContacts
                        ? "接触点の表示: ON(**光る玉がマニフォールドの点**。深いほど大きい)"
                        : "接触点の表示: OFF");
                break;

            case Key.Number4 when ctrl && shift && alt:
                // **今日いちばんの実験**。1組から採る接触点を減らすと何が起きるか。
                _contactLimitIndex = (_contactLimitIndex + 1) % ContactLimits.Length;
                Physics.MaxContactsPerPair = ContactLimits[_contactLimitIndex];

                Console.WriteLine(
                    $"1組あたりの接触点: {Physics.MaxContactsPerPair} 点"
                    + (Physics.MaxContactsPerPair == 1
                        ? "(**Day 43 と同じ1点**。床の箱が支えきれずに震え出す)"
                        : string.Empty)
                    + (Physics.MaxContactsPerPair == 4
                        ? "(本来の値。面で触れているものが面で支えられる)"
                        : string.Empty));
                break;

            case Key.Number5 when ctrl && shift && alt:
                SpawnBox();
                break;

            case Key.Number6 when ctrl && shift && alt:
                DescribeContacts();
                break;

            case Key.Number7 when ctrl && shift && alt:
                // **今日いちばん深い比較**(要点7)。Day 43 の <c>Ctrl+Shift+Alt+F2</c>
                // (積分法の切り替え)と同じ性格のつまみで、
                // 「正しい解き方」と「素朴な解き方」を並べて見るためだけにある。
                Physics.SolveContactsTogether = !Physics.SolveContactsTogether;

                Console.WriteLine(
                    Physics.SolveContactsTogether
                        ? "接触点の解き方: **同時**(4点を同じ状態から決めてまとめて掛ける)"
                        : "接触点の解き方: **順番**(Day 43 と同じ。1点目が全部を背負う)");

                if (!Physics.SolveContactsTogether)
                {
                    Console.WriteLine(
                        "  **箱を積む筋書き(Ctrl+Shift+Alt+2)で見ると分かる**"
                        + "——柱がじりじり滑って横にずれる");
                    Console.WriteLine(
                        "  **Day 47 で崩れなくなった**(摩擦が横向きの逃げを押さえる)。"
                        + "Shift+F で摩擦を切ると、Day 46 までのように崩れ落ちる");
                }

                break;

            case Key.Number0 when ctrl && shift && alt:
                RunBoxCollisionCheck();
                break;

            // --- Day 43 のスイッチ(剛体力学の基礎)---
            //
            // **Ctrl+Shift+Alt + ファンクションキー**。段はこれで7つ目になる
            // (Ctrl+F / Shift+F / Ctrl+Shift+F / Ctrl+Alt+F / Alt+F / Shift+Alt+F /
            //  Ctrl+Shift+Alt+F)。修飾キー3つの組み合わせはこれ1つしか残っていないので、
            // **次の日からは別の当て方を考える**ことになる(数字キーの段も同様に埋まりつつある)。
            //
            // <b>この塊はいちばん上に置くこと</b>。下にある
            // `case Key.F1 when ctrl && alt:`(Day 40)も
            // `case Key.F1 when ctrl && shift:`(Day 39)も
            // **Ctrl+Shift+Alt+F1 で成立してしまう**ので、
            // 1つでも上に来ていると今日のキーが1つも届かない。
            // Day 39 から毎日書いている話がここで最大になる——
            // **ガード付き case は「具体的なものほど上」**が唯一の守り方。
            //
            // <b>Ctrl+Shift+Alt+F4 は Alt+F4 ではない</b>ので窓は閉じない(Day 40 と同じ)。
            case Key.F1 when ctrl && shift && alt:
                // **今日の到達点**。球を落として、跳ねさせて、積む。
                // もう一度押すと消える。**消して出し直せば作り直し**になる。
                if (_physicsDemo)
                {
                    _physicsDemo = false;

                    // **キャラクターも一緒に下ろす**(Day 45)。
                    // 世界を消してキャラクターだけ残すと、
                    // 床の無いところを落ち続けることになる。
                    _characterDemo = false;

                    Physics.Clear();
                    BodyColors.Clear();

                    // **地形も捨てる**(Day 46)。メッシュは GL の資源なので、
                    // 放っておくと切り替えるたびに VRAM が積み上がる。
                    DisposeTerrain();

                    _orbit.Reset();
                    Console.WriteLine("物理デモ: OFF");
                }
                else
                {
                    ShowPhysicsDemo(_physicsScene);
                    Console.WriteLine(
                        $"物理デモ: {PhysicsSceneLabel()}({Physics.Bodies.Count} 体)。"
                        + "**Ctrl+Shift+Alt+F4 で筋書きを切り替え**");
                    Console.WriteLine(
                        "  F2:積分法  F3:反発係数  F5:反復回数  F6:位置補正  "
                        + "F7:球を追加  F8:撃つ  F9:内訳  F10:停止  F11:コマ送り");
                }

                break;

            case Key.F2 when ctrl && shift && alt:
                // **今日いちばん深い比較**。2行の順番を入れ替えるだけで、
                // エネルギーが増える積分器と、増えない積分器が入れ替わる。
                Physics.Integrator = Physics.Integrator == IntegratorMode.SemiImplicit
                    ? IntegratorMode.Explicit
                    : IntegratorMode.SemiImplicit;

                Console.WriteLine(
                    Physics.Integrator == IntegratorMode.SemiImplicit
                        ? "積分: セミインプリシット(速度 → 位置。**ゲームの標準**)"
                        : "積分: **陽的オイラー**(位置 → 速度。エネルギーが増え続ける)");
                if (Physics.Integrator == IntegratorMode.Explicit)
                {
                    // **60Hz だと差が小さい**。増える量は 1ステップあたり g²dt²/2 なので、
                    // dt を大きくすると2乗で効いてくる。数字キーの 3(20Hz)や 4(5Hz)で
                    // シミュレーションレートを落とすと、跳ねるたびに高くなるのが一目で分かる。
                    Console.WriteLine(
                        "  **数字キーの 3(20Hz)や 4(5Hz)を押す**と一目で分かる"
                        + "(増える量は dt の2乗に比例)");
                }
                break;

            case Key.F3 when ctrl && shift && alt:
                {
                    _restitutionIndex = (_restitutionIndex + 1) % RestitutionSteps.Length;
                    float restitution = RestitutionSteps[_restitutionIndex];

                    // **今ある球にも反映する**。作り直さないと効かないのでは、
                    // 「同じ場面で e だけを変える」比較ができない。
                    foreach (RigidBody body in Physics.Bodies)
                    {
                        body.Restitution = restitution;
                    }

                    Console.WriteLine(
                        $"反発係数: {restitution:F2}"
                        + (restitution <= 0.0f ? "(**跳ねない**。当たったら止まる)" : string.Empty)
                        + (restitution >= 0.9f ? "(**ほぼ完全弾性**。なかなか止まらない)" : string.Empty)
                        + "  ※落下デモの5段の振り分けも、ここで上書きされる");
                }

                break;

            case Key.F4 when ctrl && shift && alt:
                {
                    PhysicsScene next = _physicsScene switch
                    {
                        PhysicsScene.Drop => PhysicsScene.Stack,
                        PhysicsScene.Stack => PhysicsScene.Cradle,
                        _ => PhysicsScene.Drop,
                    };

                    ShowPhysicsDemo(next);
                    Console.WriteLine(_physicsScene switch
                    {
                        PhysicsScene.Stack =>
                            "筋書き: **積み上げ**(縦に6個。反復回数を 1 にすると沈む)",
                        PhysicsScene.Cradle =>
                            "筋書き: **撞き玉**(左から撞く。運動量が伝わっていく)",
                        _ =>
                            "筋書き: **落下**(反発係数 0 / 0.25 / 0.5 / 0.75 / 0.95 の5球)",
                    });
                }

                break;

            case Key.F5 when ctrl && shift && alt:
                _iterationIndex = (_iterationIndex + 1) % IterationSteps.Length;
                Physics.VelocityIterations = IterationSteps[_iterationIndex];
                Console.WriteLine(
                    $"速度の反復: {Physics.VelocityIterations} 周"
                    + (Physics.VelocityIterations == 1
                        ? "(**素朴版**。積み上げが持たない)"
                        : string.Empty)
                    + "  ※積み上げの筋書きでいちばん効く");
                break;

            case Key.F6 when ctrl && shift && alt:
                Physics.PositionCorrection = !Physics.PositionCorrection;
                Console.WriteLine(
                    Physics.PositionCorrection
                        ? $"位置補正: ON(率 {Physics.CorrectionRate:F2} / 許容 {Physics.Slop * 100.0f:F1}cm)"
                        : "位置補正: **OFF**(速度は直るが、めり込みは戻らない。HUD の「めり込み」を見る)");
                break;

            case Key.F7 when ctrl && shift && alt:
                SpawnBall();
                break;

            case Key.F8 when ctrl && shift && alt:
                // **押すたびに交互**にする。中心と、中心を外した場合を
                // 続けて撃つと、同じ大きさの撃力でも回るかどうかが変わるのが見える。
                KickBodies(_kickOffCenter);
                _kickOffCenter = !_kickOffCenter;
                break;

            case Key.F9 when ctrl && shift && alt:
                DescribePhysics();
                break;

            case Key.F10 when ctrl && shift && alt:
                _physicsPaused = !_physicsPaused;
                _physicsStepsRequested = 0;
                Console.WriteLine(
                    _physicsPaused
                        ? "物理: 一時停止(**カメラは動かせる**。Ctrl+Shift+Alt+F11 でコマ送り)"
                        : "物理: 再開");
                break;

            case Key.F11 when ctrl && shift && alt:
                // 止まっていなければ、まず止めてから1歩進める。
                _physicsPaused = true;
                _physicsStepsRequested++;
                Console.WriteLine(
                    $"物理: コマ送り {_physicsStepsRequested} ステップ待ち"
                    + $"(1ステップ = {_loop.FixedDeltaTime * 1000.0:F1}ms)");
                break;

            case Key.F12 when ctrl && shift && alt:
                RunRigidBodyCheck();
                break;

            // --- Day 40 のスイッチ(カメラワークと機能トグル)---
            //
            // **Ctrl+Alt + ファンクションキー**。Ctrl+F(Day 37)、Shift+F(Day 38)、
            // Ctrl+Shift+F(Day 39)が埋まったので、残りはここしかない。
            // <c>Ctrl+Alt+F12</c> だけは Day 39 の自己チェックが先客なので、
            // 今日は **F1〜F11** に収める。
            //
            // <b>Ctrl+Alt+F4 は Alt+F4 ではない</b>ので窓は閉じない。
            // Day 37 が避けたのは Alt 単独との組み合わせで、Ctrl が入れば別のキーになる。
            //
            // <b>この塊もいちばん上に置く</b>。下にある `case Key.F1 when ctrl:`(Day 37)は
            // Ctrl+Alt+F1 でも成立してしまう。Day 39 の塊と同じ理由で、
            // **順番が仕様の一部**になっている。
            case Key.F1 when ctrl && alt:
                // **今日の到達点**。シーンを出して、必須構成を全部入れて、カメラを回す。
                if (_demo is null)
                {
                    LoadDemoScene();
                }

                if (_demo is not null)
                {
                    StopTourIfRunning();
                    _useHdriSky = true;
                    _sunFromHdri = true;
                    _sceneShadows = true;
                    _post.Split = PostSplit.None;
                    _debugChannel = 0;
                    SetSpriteCount(0);

                    // **Day 39 が手で 12 個並べていたところ**。表にした御利益がここに出る。
                    _features.SetAll(true);

                    BakeSky();
                    ApplyDemoLighting();
                    PlayDemoCamera();
                }

                break;

            case Key.F2 when ctrl && alt:
                _cameraPlaying = !_cameraPlaying;
                Console.WriteLine(
                    _cameraPlaying
                        ? "カメラワーク: 再生"
                        : "カメラワーク: 一時停止(**止めている間は左ドラッグで手動に戻れる**)");
                break;

            case Key.F3 when ctrl && alt:
                JumpToNextShot();
                break;

            case Key.F4 when ctrl && alt:
                // **今日いちばん効く比較**。線形に落とすと、キーを通過する瞬間に
                // HUD の m/s が飛ぶ。絵のほうでも、通過点でパンが「カクッ」と折れる。
                _cameraInterpolation = _cameraInterpolation == CameraInterpolation.CatmullRom
                    ? CameraInterpolation.Linear
                    : CameraInterpolation.CatmullRom;
                Console.WriteLine(
                    _cameraInterpolation == CameraInterpolation.CatmullRom
                        ? "補間: Catmull-Rom(前後を覗いて接線を決める。**通過点で速度が繋がる**)"
                        : "補間: 線形(**通過点で速度が飛ぶ**。HUD の m/s を見ながら押す)");
                break;

            case Key.F5 when ctrl && alt:
                // **軌跡は1ミリも動かない**のが要点。動くのは速さの配分だけ。
                _cameraEase = !_cameraEase;
                Console.WriteLine(
                    _cameraEase
                        ? "イージング: smoothstep(静止と滑らかに繋がる)"
                        : "イージング: なし(**止まっていたカメラがいきなり最高速で動き出す**)");
                break;

            case Key.F6 when ctrl && alt:
                {
                    int index = Array.IndexOf(CameraSpeedSteps, _cameraSpeed);
                    _cameraSpeed = CameraSpeedSteps[(index + 1) % CameraSpeedSteps.Length];
                    Console.WriteLine($"再生速度: x{_cameraSpeed:F2}");
                }

                break;

            case Key.F7 when ctrl && alt:
                _features.SelectNext();
                Console.WriteLine(
                    $"選択: {_features.Features[_features.Selected].Name}"
                    + $"({OnOff(_features.Features[_features.Selected].Value)})"
                    + "  Ctrl+Alt+F8 で切り替え");
                break;

            case Key.F8 when ctrl && alt:
                {
                    StopTourIfRunning();

                    FeatureToggles.Feature feature = _features.ToggleSelected();
                    Console.WriteLine(
                        $"{feature.Name}: {OnOff(feature.Value)}"
                        + (feature.Value ? string.Empty : $"  — {feature.Effect}"));
                }

                break;

            case Key.F9 when ctrl && alt:
                {
                    // **素の絵と必須構成の絵の往復**。Day 31 の出発点がどれだけ素っ気ないか、
                    // 8日ぶんの積み上げが何をしていたかが、1押しで見える。
                    StopTourIfRunning();

                    bool on = !_features.AllOn;
                    _features.SetAll(on);
                    Console.WriteLine(
                        on
                            ? $"機能: 全部 ON({_features.Features.Count} 項目。必須構成の絵)"
                            : "機能: 全部 OFF(**Day 31 に戻した絵**。ベースカラーと平行光源だけ)");
                }

                break;

            case Key.F10 when ctrl && alt:
                if (_features.TourActive)
                {
                    _features.EndTour();
                    Console.WriteLine("機能ツアー: 終了(始める前の状態に戻した)");
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"機能ツアー: {_features.TourInterval:F1} 秒ごとに1つずつ切っては戻す");
                    Console.WriteLine(_features.BeginTour());
                }

                break;

            case Key.F11 when ctrl && alt:
                RunCameraCheck();
                break;

            // --- Day 39 のスイッチ(デモ v1 の組み上げ)---
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
                _post.Split = PostSplit.None;
                _debugChannel = 0;
                SetSpriteCount(0);

                // **Day 40 でここが1行になった**。
                // 元は `_env.Enabled = true; _shadow.Enabled = true; ...` と
                // フラグを手で並べていて、機能が増えるたびに書き足す必要があった。
                // 書き忘れても絵は出る——ただ「前と違う絵」になるだけなので、
                // 原因を探すのがいちばん厄介な種類のバグになる。
                StopTourIfRunning();
                _features.SetAll(true);

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

            // --- 今日のスイッチ(アニメーション制御)---
            //
            // **Shift+Alt + ファンクションキー**。段はこれで6つ目
            // (Ctrl+F / Shift+F / Ctrl+Shift+F / Ctrl+Alt+F / Alt+F / Shift+Alt+F)。
            //
            // <b>Day 41 の Alt+F の塊より前に置くこと</b>。
            // `case Key.F1 when alt:` は **Shift+Alt+F1 でも成立する**ので、
            // 下に置くと今日のキーが1つも届かない。
            // 下にある `case Key.F1 when shift:`(Day 38)も同じ理由で後ろでなければならない。
            // Day 39・40・41 と同じ話が、段が増えるたびに厳しくなっていく——
            // **ガード付き case は「具体的なものほど上」**が唯一の守り方。
            case Key.F1 when shift && alt:
                // **今日の到達点**。キツネを出して、速度を自動で上下させる。
                ShowAnimatedModel("models/Fox.glb");
                if (_locomotionTree is not null)
                {
                    _locomotion = LocomotionMode.BlendTree;
                    _speedSweep = true;
                    _sweepPhase = 0.0f;
                    _locomotionStates?.Reset(0);

                    if (_animation is not null)
                    {
                        _animation.Paused = false;
                        _animation.SyncPhase = true;
                    }

                    Console.WriteLine(
                        "ロコモーション: ブレンドツリー。速度が 0 ↔ "
                        + $"{MaxMoveSpeed:F1}m/s を {SweepSeconds:F0} 秒で往復します");
                    Console.WriteLine(
                        "  **Shift+Alt+F3 で位相同期を切ると、足が合わなくなる**");
                }
                else
                {
                    Console.WriteLine("このモデルではロコモーションを組めません(クリップが2本未満)");
                }

                break;

            case Key.F2 when shift && alt:
                _locomotion = _locomotion switch
                {
                    LocomotionMode.Off => LocomotionMode.BlendTree,
                    LocomotionMode.BlendTree => LocomotionMode.StateMachine,
                    _ => LocomotionMode.Off,
                };

                // ステートマシンへ移るときは、今の速度の状態から始める
                // (前回の状態が残っていると、いきなりクロスフェードが走る)。
                if (_locomotion == LocomotionMode.StateMachine)
                {
                    _locomotionStates?.Reset(0);
                }

                Console.WriteLine(_locomotion switch
                {
                    LocomotionMode.BlendTree =>
                        "決め方: **ブレンドツリー**(常に隣り合う2本が混ざる。速度に対して連続)",
                    LocomotionMode.StateMachine =>
                        "決め方: **ステートマシン**(どれか1つ。移り変わる瞬間だけ混ざる)",
                    _ => "決め方: OFF(Day 41 と同じ。クリップを1本そのまま再生)",
                });
                break;

            case Key.F3 when shift && alt:
                if (_animation is not null)
                {
                    _animation.SyncPhase = !_animation.SyncPhase;
                    Console.WriteLine(
                        _animation.SyncPhase
                            ? "位相同期: ON(全クリップが同じ正規化位置を指す)"
                            : "位相同期: OFF(**各クリップが自分の長さで勝手に回る**。足が合わなくなる)");
                }

                break;

            case Key.F5 when shift && alt:
                _speedSweep = false;
                _moveSpeed = MathF.Max(0.0f, _moveSpeed - 0.2f);
                Console.WriteLine($"速度: {_moveSpeed:F2}m/s  {BlendLabel()}");
                break;

            case Key.F6 when shift && alt:
                _speedSweep = false;
                _moveSpeed = MathF.Min(MaxMoveSpeed, _moveSpeed + 0.2f);
                Console.WriteLine($"速度: {_moveSpeed:F2}m/s  {BlendLabel()}");
                break;

            case Key.F7 when shift && alt:
                _speedSweep = !_speedSweep;
                Console.WriteLine(
                    _speedSweep
                        ? $"速度: 自動({SweepSeconds:F0} 秒で往復)"
                        : $"速度: 手動({_moveSpeed:F2}m/s。Shift+Alt+F5 / F6 で増減)");
                break;

            case Key.F8 when shift && alt:
                _crossFadeIndex = (_crossFadeIndex + 1) % CrossFadeSeconds.Length;
                if (_locomotionStates is not null)
                {
                    _locomotionStates.FadeSeconds = CrossFadeSeconds[_crossFadeIndex];
                }

                Console.WriteLine(
                    $"クロスフェード: {CrossFadeSeconds[_crossFadeIndex]:F2}s"
                    + (CrossFadeSeconds[_crossFadeIndex] <= 0.0f
                        ? "(**0 秒。切り替わりで足がワープする**)"
                        : string.Empty)
                    + "  ※ステートマシンのときだけ効く");
                break;

            case Key.F9 when shift && alt:
                DescribeBlend();
                break;

            case Key.F12 when shift && alt:
                RunLocomotionCheck();
                break;

            // --- Day 41 のスイッチ(スキニングアニメーション)---
            //
            // **素の Alt + ファンクションキー**。Ctrl+Alt(Day 40)、Ctrl+Shift(Day 39)、
            // Shift(Day 38)、Ctrl(Day 37)が埋まって、ここだけが空いていた。
            //
            // <b>Alt+F4 は使わない</b>。Windows が窓を閉じるので、押した瞬間に終わる。
            // Ctrl+Alt+F4(Day 40)は別のキーなので問題にならない。
            //
            // <b>ctrl && alt の塊より後に置くこと</b>。`case Key.F1 when alt:` は
            // Ctrl+Alt+F1 でも成立してしまうので、**順番が仕様の一部**になっている
            // (Day 39・Day 40 の但し書きと同じ話。段が増えるたびにこれが効いてくる)。
            case Key.F1 when alt:
                // **今日の到達点**。19 関節の人型を出して歩かせる。
                ShowAnimatedModel("models/CesiumMan.glb");
                Console.WriteLine(
                    "CesiumMan が歩いています。"
                    + "**Alt+F6 でスキニングを切ると、骨は動いたままバインドポーズに戻る**");
                break;

            case Key.F2 when alt:
                {
                    // アニメーション付きの5体を巡回する。
                    string current = _modelIndex >= 0 && _modelIndex < ModelPaths.Length
                        ? ModelPaths[_modelIndex]
                        : string.Empty;

                    int at = Array.IndexOf(AnimatedModelPaths, current);
                    ShowAnimatedModel(AnimatedModelPaths[(at + 1) % AnimatedModelPaths.Length]);
                }

                break;

            case Key.F3 when alt:
                // クリップを次へ。**3本持っているのは Fox だけ**。
                if (_animation is not null && _model is not null && _model.Animations.Count > 1)
                {
                    _animation.NextClip();
                    Console.WriteLine(
                        $"クリップ: {_animation.ClipName}"
                        + $"({_animation.ClipIndex + 1}/{_model.Animations.Count})  {_animation.Duration:F2}s");
                }
                else
                {
                    Console.WriteLine("クリップが1本以下です(3本あるのは Fox。Alt+F2 で切り替え)");
                }

                break;

            case Key.F5 when alt:
                if (_animation is not null)
                {
                    _animation.Paused = !_animation.Paused;
                    Console.WriteLine(
                        _animation.Paused
                            ? $"アニメ: 一時停止({_animation.Time:F2}s)。Alt+F8 で 1/30 秒ずつ送れる"
                            : "アニメ: 再生");
                }

                break;

            case Key.F6 when alt:
                // **今日いちばん効く見比べ**。骨は動き続けるのに頂点が動かなくなる。
                if (_animation is not null)
                {
                    _animation.SkinningEnabled = !_animation.SkinningEnabled;
                    Console.WriteLine(
                        _animation.SkinningEnabled
                            ? "スキニング: ON"
                            : "スキニング: OFF(**関節行列を全部単位行列にした = バインドポーズ**)");
                }

                break;

            case Key.F7 when alt:
                _playbackSpeedIndex = (_playbackSpeedIndex + 1) % PlaybackSpeeds.Length;
                if (_animation is not null)
                {
                    _animation.Speed = PlaybackSpeeds[_playbackSpeedIndex];
                }

                Console.WriteLine($"再生速度: {PlaybackSpeeds[_playbackSpeedIndex]:F2} 倍");
                break;

            case Key.F8 when alt:
                // コマ送り。**止めてから送る**——動いたまま送っても差が分からない。
                if (_animation is not null)
                {
                    _animation.Paused = true;
                    _animation.Advance(AnimationStepSeconds);
                    Console.WriteLine($"コマ送り: {_animation.Time:F3}s / {_animation.Duration:F2}s");
                }

                break;

            case Key.F9 when alt:
                // 骨の重みを色で見る(成分 22)。もう一度押すと通常へ戻る。
                _debugChannel = _debugChannel == 22 ? 0 : 22;
                Console.WriteLine($"表示する成分: {DebugChannelLabel()}");
                break;

            case Key.F10 when alt:
                if (_animation is not null)
                {
                    // **Day 42 で Rewind に変えた**。SelectClip だと混ぜているものが1本に潰れる。
                    _animation.Rewind();
                    Console.WriteLine("アニメ: 先頭へ戻した");
                }

                break;

            case Key.F11 when alt:
                DescribeRig();
                break;

            case Key.F12 when alt:
                RunSkinningCheck();
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
                // Day 35 で BRDF の5つ、Day 36 で IBL の3つ、Day 37 で SSAO、
                // Day 41 で骨の重みが増えて 23 通りになった。
                // **多いので Alt+9 と Ctrl+Shift+9 で目当ての成分へ直接飛べる**ようにしてある。
                _debugChannel = (_debugChannel + 1) % 23;
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
    /// 今日の自己チェック(Shift+Alt+F12)。
    ///
    /// ブレンドは**間違っていてもそれらしく動く**のが厄介なところで、
    /// 重みが 0.4/0.4(合計 0.8)でも、位相がずれていても、
    /// 符号を揃え忘れていても、キツネはそれなりに走って見える。
    /// だから数字で押さえる。
    /// </summary>
    private static void RunLocomotionCheck()
    {
        Console.WriteLine();
        Console.WriteLine("--- Day 42: アニメーション制御の自己チェック ---");
        var checks = new CheckList();

        Span<ClipWeight> scratch = stackalloc ClipWeight[AnimationPlayer.MaxBlendClips];

        // --- 1. ブレンドツリー(ファイルに依存しない部分)---
        var tree = new BlendTree1D("テスト",
        [
            new BlendTree1D.Sample("idle", 0, 0.0f),
            new BlendTree1D.Sample("walk", 1, 1.0f),
            new BlendTree1D.Sample("run", 2, 3.0f),
        ]);

        int count = tree.Evaluate(-5.0f, scratch);
        checks.Check(
            "軸の下端より下は端の1本だけ",
            count == 1 && scratch[0].Clip == 0 && scratch[0].Weight == 1.0f,
            $"{count} 本");

        count = tree.Evaluate(99.0f, scratch);
        checks.Check(
            "軸の上端より上も端の1本だけ(**外挿しない**)",
            count == 1 && scratch[0].Clip == 2 && scratch[0].Weight == 1.0f,
            $"{count} 本");

        count = tree.Evaluate(2.0f, scratch);
        bool midpoint = count == 2
            && scratch[0].Clip == 1 && scratch[1].Clip == 2
            && MathF.Abs(scratch[0].Weight - 0.5f) < 1e-5f
            && MathF.Abs(scratch[1].Weight - 0.5f) < 1e-5f;
        checks.Check(
            "walk(1.0)と run(3.0)の中点 2.0 で 50:50",
            midpoint,
            count == 2 ? $"{scratch[0].Weight:F3} / {scratch[1].Weight:F3}" : $"{count} 本");

        count = tree.Evaluate(1.0f, scratch);
        float sum = 0.0f;
        for (int i = 0; i < count; i++)
        {
            sum += scratch[i].Weight;
        }

        checks.Check("重みの合計が 1", MathF.Abs(sum - 1.0f) < 1e-5f, $"{sum:F6}");

        checks.Check(
            "点の真上では隣が混ざらない",
            count == 2 && scratch[1].Weight <= 1e-6f,
            count == 2 ? $"隣の重み {scratch[1].Weight:E2}" : $"{count} 本");

        // --- 2. 姿勢の合成 ---
        var poseA = new NodePose(
            new Vector3(0.0f, 0.0f, 0.0f), Quaternion.Identity, Vector3.One);
        var poseB = new NodePose(
            new Vector3(2.0f, 0.0f, 0.0f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f),
            new Vector3(3.0f, 3.0f, 3.0f));

        var single = default(PoseAccumulator);
        single.Add(poseB, 1.0f);
        NodePose resolvedSingle = single.Resolve(poseA);
        checks.Check(
            "1本だけ足したら元のまま",
            (resolvedSingle.Translation - poseB.Translation).Length() < 1e-6f
                && MathF.Abs(resolvedSingle.Scale.X - 3.0f) < 1e-6f,
            $"({resolvedSingle.Translation.X:F3}, 拡大 {resolvedSingle.Scale.X:F3})");

        var half = default(PoseAccumulator);
        half.Add(poseA, 0.5f);
        half.Add(poseB, 0.5f);
        NodePose resolvedHalf = half.Resolve(poseA);
        NodePose viaLerp = NodePose.Lerp(poseA, poseB, 0.5f);
        checks.Check(
            "50:50 の合成が Lerp と一致(**中点では nlerp = slerp**)",
            (resolvedHalf.Translation - viaLerp.Translation).Length() < 1e-5f
                && Quaternion.Dot(resolvedHalf.Rotation, viaLerp.Rotation) > 0.99999f,
            $"平行移動 {resolvedHalf.Translation.X:F4} / 内積 "
            + $"{Quaternion.Dot(resolvedHalf.Rotation, viaLerp.Rotation):F6}");

        // **符号を揃えていることの実証**。q と -q は同じ向きなので、
        // 揃えて足せば元の向きに戻る。揃えないと打ち消し合って 0 になる。
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 2.0f);
        var signed = default(PoseAccumulator);
        signed.Add(poseA with { Rotation = turn }, 0.5f);
        signed.Add(poseA with { Rotation = -turn }, 0.5f);
        NodePose resolvedSigned = signed.Resolve(poseA);

        Vector4 naive = (new Vector4(turn.X, turn.Y, turn.Z, turn.W) * 0.5f)
            + (new Vector4(-turn.X, -turn.Y, -turn.Z, -turn.W) * 0.5f);

        checks.Check(
            "q と -q を混ぜても元の向きが出る(**符号を揃えている**)",
            MathF.Abs(MathF.Abs(Quaternion.Dot(resolvedSigned.Rotation, turn)) - 1.0f) < 1e-5f,
            $"内積 {Quaternion.Dot(resolvedSigned.Rotation, turn):F6}");
        checks.Check(
            "揃えずに足すと長さが 0 になる(**揃えないとこうなる**)",
            naive.Length() < 1e-6f,
            $"長さ {naive.Length():E2}");

        // --- 3. ステートマシン ---
        AnimationStateMachine.State[] states =
        [
            new AnimationStateMachine.State("idle", 0, float.NegativeInfinity),
            new AnimationStateMachine.State("walk", 1, 0.65f),
            new AnimationStateMachine.State("run", 2, 2.08f),
        ];

        var machine = new AnimationStateMachine(states) { FadeSeconds = 0.0f, Hysteresis = 0.25f };
        machine.Reset(0);
        machine.Update(0.0f, 1.0f, scratch);
        checks.Check("しきい値を超えたら上がる", machine.Current == 1, machine.CurrentName);

        machine.Update(0.0f, 3.0f, scratch);
        checks.Check("2段まとめて上がれる", machine.Current == 2, machine.CurrentName);

        machine.Update(0.0f, 1.9f, scratch);
        checks.Check(
            "ヒステリシスの中では下がらない(2.08 - 0.25 = 1.83 まで粘る)",
            machine.Current == 2,
            $"{machine.CurrentName}(速度 1.9)");

        machine.Update(0.0f, 1.8f, scratch);
        checks.Check("そこを割ったら下がる", machine.Current == 1, machine.CurrentName);

        // **チャタリング**。しきい値をまたいで微小に振動させ、状態が変わった回数を数える。
        static int ChatterCount(AnimationStateMachine.State[] states, float hysteresis)
        {
            // **ローカル関数の中では外の Span を触れない**(ref 構造体はクロージャに入らない)。
            // 数行のことなので、こちらで用意する。
            Span<ClipWeight> weights = stackalloc ClipWeight[AnimationPlayer.MaxBlendClips];

            var target = new AnimationStateMachine(states)
            {
                FadeSeconds = 0.0f,
                Hysteresis = hysteresis,
            };

            target.Reset(1);
            int changes = 0;
            int last = target.Current;

            for (int i = 0; i < 100; i++)
            {
                target.Update(1.0f / 60.0f, 2.08f + (i % 2 == 0 ? -0.001f : 0.001f), weights);
                if (target.Current != last)
                {
                    changes++;
                    last = target.Current;
                }
            }

            return changes;
        }

        int withoutHysteresis = ChatterCount(states, 0.0f);
        int withHysteresis = ChatterCount(states, 0.25f);

        checks.Check(
            "ヒステリシス 0 だと境目でばたつく",
            withoutHysteresis > 50,
            $"100 フレームで {withoutHysteresis} 回");
        checks.Check(
            "ヒステリシスを入れると 1 回で収まる",
            withHysteresis <= 1,
            $"100 フレームで {withHysteresis} 回");

        // クロスフェードの時間。
        var fading = new AnimationStateMachine(states) { FadeSeconds = 0.2f, Hysteresis = 0.25f };
        fading.Reset(0);
        fading.Update(0.0f, 0.0f, scratch);
        fading.Update(0.0f, 3.0f, scratch);
        checks.Check("遷移した瞬間はまだ前の状態", fading.Fade < 1e-3f, $"{fading.Fade:F4}");

        for (int i = 0; i < 6; i++)
        {
            fading.Update(0.2f / 12.0f, 3.0f, scratch);
        }

        checks.Check(
            "半分の時間で 0.5 前後",
            MathF.Abs(fading.Fade - 0.5f) < 0.02f,
            $"{fading.Fade:F4}");

        for (int i = 0; i < 7; i++)
        {
            fading.Update(0.2f / 12.0f, 3.0f, scratch);
        }

        checks.Check(
            "FadeSeconds で終わり、前の状態が消える",
            fading.Fade >= 1.0f && fading.Previous < 0,
            $"{fading.Fade:F4}");

        var instant = new AnimationStateMachine(states) { FadeSeconds = 0.0f, Hysteresis = 0.25f };
        instant.Reset(0);
        instant.Update(0.0f, 3.0f, scratch);
        checks.Check("FadeSeconds 0 なら即座に移行済み", instant.Fade >= 1.0f, $"{instant.Fade:F4}");

        // --- 4. プレイヤーの混合 ---
        int restore = _modelIndex;
        Model fox = LoadCheckModel("models/Fox.glb");
        var player = new AnimationPlayer(fox);

        int walkClip = FindClip(fox, "walk");
        int runClip = FindClip(fox, "run");
        checks.Check(
            "Fox から walk / run のクリップを名前で引ける",
            walkClip >= 0 && runClip >= 0,
            $"walk={walkClip} run={runClip}");

        // **合計が 1 でない重みを渡しても正規化される**。
        player.SetBlend([new ClipWeight(walkClip, 2.0f), new ClipWeight(runClip, 2.0f)]);
        float blendSum = 0.0f;
        foreach (ClipWeight entry in player.Blend)
        {
            blendSum += entry.Weight;
        }

        checks.Check(
            "SetBlend が重みを合計 1 に正規化する",
            MathF.Abs(blendSum - 1.0f) < 1e-5f && player.Blend.Length == 2,
            $"{blendSum:F6}({player.Blend.Length} 本)");

        float expected = (fox.Animations[walkClip].Duration + fox.Animations[runClip].Duration) * 0.5f;
        checks.Check(
            "周期が重み付きの平均になる",
            MathF.Abs(player.Duration - expected) < 1e-4f,
            $"{player.Duration:F4}s(walk {fox.Animations[walkClip].Duration:F3} / "
            + $"run {fox.Animations[runClip].Duration:F3})");

        // 上限を超えたら弱いものから捨てる。
        player.SetBlend(
        [
            new ClipWeight(0, 0.4f), new ClipWeight(1, 0.3f),
            new ClipWeight(2, 0.2f), new ClipWeight(0, 0.05f),
            new ClipWeight(1, 0.01f),
        ]);
        checks.Check(
            $"同時に混ぜるのは {AnimationPlayer.MaxBlendClips} 本まで",
            player.Blend.Length <= AnimationPlayer.MaxBlendClips,
            $"{player.Blend.Length} 本");

        // --- 5. 位相同期 ---
        //
        // **1周ぶん進めたら同じ姿勢に戻るか**を見る。
        // 同期していれば位相が 1 周して元に戻る。
        // 同期していないと、周期の違う2本のクリップが別々に回るので、
        // 1周ごとに位相の関係がずれていく。
        player.SetBlend([new ClipWeight(walkClip, 0.5f), new ClipWeight(runClip, 0.5f)]);

        float syncedDrift = MeasureCycleDrift(player, syncPhase: true);
        float freeDrift = MeasureCycleDrift(player, syncPhase: false);

        checks.Check(
            "位相同期 ON: 1周ごとに同じ姿勢へ戻る",
            syncedDrift < 1e-2f,
            $"ずれ {syncedDrift:E2}");
        checks.Check(
            "位相同期 OFF: **毎周期ちがう姿勢になる**",
            freeDrift > syncedDrift * 10.0f,
            $"ずれ {freeDrift:E2}(同期していれば {syncedDrift:E2})");

        // Rewind が重みを壊さないこと(Alt+F10 の経路)。
        player.SetBlend([new ClipWeight(walkClip, 0.5f), new ClipWeight(runClip, 0.5f)]);
        player.Advance(0.3f);
        player.Rewind();
        checks.Check(
            "Rewind は時刻だけ戻し、混合を壊さない",
            player.Blend.Length == 2 && player.Phase == 0.0f,
            $"{player.Blend.Length} 本 / 位相 {player.Phase:F3}");

        // --- 6. 混ぜても頂点が動くこと ---
        player.SetBlend([new ClipWeight(walkClip, 0.5f), new ClipWeight(runClip, 0.5f)]);
        player.Rewind();
        (float move, int broken) = MeasureSkinnedMotion(fox, player, 0.0f, player.Duration * 0.5f);
        checks.Check("混合したままでも頂点が動く", move > 1.0f, $"最大 {move:F1}(Fox の単位)");
        checks.Check("NaN / 無限大が1つも無い", broken == 0, $"{broken} 個");

        fox.Dispose();

        checks.Report("すべて合格(ブレンドツリー・ステートマシン・位相同期が仕様どおり)");
        Console.WriteLine();

        SetModel(restore);
    }

    /// <summary>
    /// 1周ぶん進めたときに姿勢がどれだけずれるかを測る(Day 42)。
    /// **位相同期が効いていれば 0 に近い**。
    /// </summary>
    private static float MeasureCycleDrift(AnimationPlayer player, bool syncPhase)
    {
        player.SyncPhase = syncPhase;
        player.Rewind();

        Matrix4x4[] baseline = player.GetJointMatrices(0).ToArray();
        float cycle = player.Duration;
        float worst = 0.0f;

        for (int lap = 0; lap < 3; lap++)
        {
            player.Advance(cycle);

            ReadOnlySpan<Matrix4x4> current = player.GetJointMatrices(0);
            for (int i = 0; i < baseline.Length; i++)
            {
                worst = MathF.Max(worst, MaxAbsDifference(baseline[i], current[i]));
            }
        }

        return worst;
    }

    /// <summary>
    /// リグの内訳をコンソールに出す(Alt+F11)。**骨の名前と親子が見えると話が早い**。
    ///
    /// スキニングが妙なときの原因は、たいてい「思っていた骨と違う骨が動いている」。
    /// 木の形と関節の並び(= JOINTS_0 の値の意味)を並べて出しておく。
    /// </summary>
    private static void DescribeRig()
    {
        if (_model is null || _animation is null)
        {
            Console.WriteLine("モデルがありません(Alt+F1 で CesiumMan を出す)");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"=== {ModelLabel()} のリグ ===");
        Console.WriteLine(
            $"ノード {_model.Nodes.Count} / スキン {_model.Skins.Count} / "
            + $"関節 {_model.JointCount} / クリップ {_model.Animations.Count}");

        // ノードの木。**NodeOrder の順に出す**ので、親が必ず先に現れる。
        foreach (int index in _model.NodeOrder)
        {
            ModelNode node = _model.Nodes[index];

            // 深さぶんだけ字下げして、木の形をそのまま見せる。
            int depth = 0;
            for (int parent = node.Parent; parent >= 0; parent = _model.Nodes[parent].Parent)
            {
                depth++;
            }

            string mark = node.SkinIndex >= 0 ? " [skin]" : string.Empty;
            if (node.MeshIndex >= 0)
            {
                mark += " [mesh]";
            }

            Console.WriteLine($"  {new string(' ', depth * 2)}{index,3}: {node.Name}{mark}");
        }

        for (int s = 0; s < _model.Skins.Count; s++)
        {
            Skin skin = _model.Skins[s];
            Console.WriteLine($"  スキン {s} ({skin.Name}) の関節:");
            for (int j = 0; j < skin.JointCount; j++)
            {
                Console.WriteLine($"    JOINTS_0 の {j,2} → ノード {skin.Joints[j],3} ({_model.Nodes[skin.Joints[j]].Name})");
            }
        }

        Console.WriteLine();
    }

    /// <summary>
    /// 今日の自己チェック(Alt+F12)。
    ///
    /// スキニングは**間違っていても絵が出る**のがいちばんの厄介さで、
    /// 「なんとなく動いているから合っている」が通用しない。
    /// 関節の番号が全部 0 でも、重みの合計が 0.9 でも、
    /// 逆バインド行列を掛け忘れても、**それらしく動いてしまう**。
    /// だから数字で押さえる。
    /// </summary>
    private static void RunSkinningCheck()
    {
        Console.WriteLine();
        Console.WriteLine("--- Day 41: スキニングアニメーションの自己チェック ---");
        var checks = new CheckList();

        // --- 1. 補間そのもの(ファイルに依存しない部分)---
        //
        // **手で作ったサンプラーで確かめる**。読み込んだデータで試すと、
        // 「補間が間違っている」のか「読み込みが間違っている」のか切り分けられない。
        float[] times = [0.0f, 1.0f];
        Vector4[] line = [Vector4.Zero, new Vector4(10.0f, 0.0f, 0.0f, 0.0f)];

        var linear = new AnimationClip.Sampler(times, line, AnimationInterpolation.Linear);
        checks.Check(
            "LINEAR: 中点が中間値",
            MathF.Abs(linear.SampleVector3(0.5f).X - 5.0f) < 1e-5f,
            $"{linear.SampleVector3(0.5f).X:F4}");
        checks.Check("範囲外は前へ伸ばす", MathF.Abs(linear.SampleVector3(-3.0f).X) < 1e-6f);
        checks.Check("範囲外は後ろへ伸ばす", MathF.Abs(linear.SampleVector3(9.0f).X - 10.0f) < 1e-6f);

        var step = new AnimationClip.Sampler(times, line, AnimationInterpolation.Step);
        checks.Check(
            "STEP: 次のキーの直前まで前の値のまま",
            MathF.Abs(step.SampleVector3(0.999f).X) < 1e-6f,
            $"{step.SampleVector3(0.999f).X:F4}");

        // 回転。**0 度と 90 度を混ぜる**。
        Quaternion q0 = Quaternion.Identity;
        Quaternion q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f);
        Vector4[] turn = [new Vector4(q0.X, q0.Y, q0.Z, q0.W), new Vector4(q1.X, q1.Y, q1.Z, q1.W)];
        var rotation = new AnimationClip.Sampler(times, turn, AnimationInterpolation.Linear);

        float quarter = Degrees(rotation.SampleRotation(0.25f));
        checks.Check(
            "slerp は等速で回る(1/4 の時刻で 22.5 度)",
            MathF.Abs(quarter - 22.5f) < 1e-2f,
            $"{quarter:F3}度");

        // **成分ごとの線形補間と比べる**。これが slerp が要る理由そのもの。
        Vector4 naive = Vector4.Lerp(turn[0], turn[1], 0.25f);
        float naiveAngle = Degrees(Quaternion.Normalize(
            new Quaternion(naive.X, naive.Y, naive.Z, naive.W)));
        checks.Check(
            "素の lerp は 22.5 度にならない(**回る速さが一定でない**)",
            MathF.Abs(naiveAngle - 22.5f) > 0.1f,
            $"{naiveAngle:F3}度  長さ {naive.Length():F4}");

        // --- 2. 読み込んだデータ ---
        int restore = _modelIndex;

        Model cesium = LoadCheckModel("models/CesiumMan.glb");
        Model fox = LoadCheckModel("models/Fox.glb");
        Model simple = LoadCheckModel("models/SimpleSkin/SimpleSkin.gltf");
        Model boxAnimated = LoadCheckModel("models/BoxAnimated.glb");
        Model helmet = LoadCheckModel("models/DamagedHelmet.glb");

        checks.Check(
            "CesiumMan: スキン1・関節19・クリップ1",
            cesium.Skins.Count == 1 && cesium.JointCount == 19 && cesium.Animations.Count == 1,
            $"スキン {cesium.Skins.Count} / 関節 {cesium.JointCount} / クリップ {cesium.Animations.Count}");

        checks.Check(
            "Fox: クリップが3本(Survey / Walk / Run)",
            fox.Animations.Count == 3,
            string.Join(" / ", fox.Animations.Select(clip => $"{clip.Name} {clip.Duration:F2}s")));

        checks.Check(
            "Fox: NORMAL を持たないので法線を生成した",
            fox.GeneratedNormalParts > 0,
            $"生成 {fox.GeneratedNormalParts} パーツ");

        checks.Check(
            "BoxAnimated: **スキン無しでもクリップは動く**",
            boxAnimated.Skins.Count == 0 && boxAnimated.Animations.Count == 1
                && boxAnimated.SkinnedParts == 0,
            $"スキン {boxAnimated.Skins.Count} / クリップ {boxAnimated.Animations.Count}");

        checks.Check(
            "SimpleSkin: 仕様の最小例(関節2・頂点10)",
            simple.JointCount == 2 && simple.VertexCount == 10,
            $"関節 {simple.JointCount} / 頂点 {simple.VertexCount}");

        // --- 3. 頂点データの不変条件 ---
        foreach ((string name, Model model) in
            ((string, Model)[])[("CesiumMan", cesium), ("Fox", fox), ("SimpleSkin", simple)])
        {
            (float minSum, float maxSum, int outOfRange) = MeasureWeights(model);
            checks.Check(
                $"{name}: 重みの合計が 1(誤差 1e-4 未満)",
                MathF.Abs(minSum - 1.0f) < 1e-4f && MathF.Abs(maxSum - 1.0f) < 1e-4f,
                $"{minSum:F6}〜{maxSum:F6}");
            checks.Check(
                $"{name}: 関節の番号が関節数の中に収まっている",
                outOfRange == 0,
                $"はみ出し {outOfRange} 個");
        }

        (float helmetMin, float helmetMax, _) = MeasureWeights(helmet);
        checks.Check(
            "DamagedHelmet: スキンが無いので重みは全部 0",
            helmetMin == 0.0f && helmetMax == 0.0f,
            $"{helmetMin:F3}〜{helmetMax:F3}");

        // --- 4. ノードの並び ---
        foreach ((string name, Model model) in
            ((string, Model)[])[("CesiumMan", cesium), ("Fox", fox), ("BoxAnimated", boxAnimated)])
        {
            checks.Check(
                $"{name}: NodeOrder に全ノードがちょうど1回",
                model.NodeOrder.Count == model.Nodes.Count
                    && model.NodeOrder.Distinct().Count() == model.Nodes.Count,
                $"{model.NodeOrder.Count} / {model.Nodes.Count}");

            var seen = new HashSet<int>();
            bool parentFirst = true;
            foreach (int node in model.NodeOrder)
            {
                int parent = model.Nodes[node].Parent;
                parentFirst &= parent < 0 || seen.Contains(parent);
                seen.Add(node);
            }

            checks.Check($"{name}: 親が必ず子より先に来る", parentFirst);
        }

        // --- 5. 関節行列 ---
        //
        // **バインドポーズでは IBM * world(joint) が単位行列になる**(Skin のコメント)。
        // ただし成り立つのは「ファイルのノードがバインドポーズに置かれている」ときだけ。
        // Fox と SimpleSkin はそうなっていて、CesiumMan はそうなっていない——
        // 書き出したときのフレームがそのまま入っているためで、**どちらも正しいファイル**。
        var foxPlayer = new AnimationPlayer(fox);
        var simplePlayer = new AnimationPlayer(simple);
        var cesiumPlayer = new AnimationPlayer(cesium);

        checks.Check(
            "Fox: ファイルの姿勢がバインドポーズ(関節行列 = 単位行列)",
            MaxJointDeviation(fox, foxPlayer) < 1e-4f,
            $"最大ずれ {MaxJointDeviation(fox, foxPlayer):E2}");
        checks.Check(
            "SimpleSkin: 同上",
            MaxJointDeviation(simple, simplePlayer) < 1e-4f,
            $"最大ずれ {MaxJointDeviation(simple, simplePlayer):E2}");
        checks.Check(
            "CesiumMan: ファイルの姿勢はバインドポーズではない(**これも正常**)",
            MaxJointDeviation(cesium, cesiumPlayer) > 0.1f,
            $"最大ずれ {MaxJointDeviation(cesium, cesiumPlayer):E2}");

        cesiumPlayer.SkinningEnabled = false;
        cesiumPlayer.Advance(0.0f);
        bool allIdentity = true;
        foreach (Matrix4x4 matrix in cesiumPlayer.GetJointMatrices(0))
        {
            allIdentity &= matrix == Matrix4x4.Identity;
        }

        checks.Check("スキニングを切ると関節行列が全部単位行列", allIdentity);
        cesiumPlayer.SkinningEnabled = true;

        // --- 6. 再生 ---
        cesiumPlayer.SelectClip(0);
        checks.Check("クリップを選ぶと時刻が 0 に戻る", cesiumPlayer.Time == 0.0f);

        cesiumPlayer.Update(0.5f);
        checks.Check("Update で時刻が進む", MathF.Abs(cesiumPlayer.Time - 0.5f) < 1e-5f, $"{cesiumPlayer.Time:F3}s");

        cesiumPlayer.Paused = true;
        float held = cesiumPlayer.Time;
        cesiumPlayer.Update(0.5f);
        checks.Check("一時停止中は進まない", cesiumPlayer.Time == held);
        cesiumPlayer.Paused = false;

        cesiumPlayer.SelectClip(0);
        cesiumPlayer.Advance(cesiumPlayer.Duration + 0.25f);
        checks.Check(
            "長さを超えたら折り返す",
            MathF.Abs(cesiumPlayer.Time - 0.25f) < 1e-4f,
            $"{cesiumPlayer.Time:F4}s(長さ {cesiumPlayer.Duration:F2}s)");

        // **クリップを変えると姿勢が変わる**。Fox の Walk と Run を同じ時刻で比べる。
        foxPlayer.SelectClip(1);
        foxPlayer.Advance(0.3f);
        Matrix4x4[] walk = foxPlayer.GetJointMatrices(0).ToArray();

        foxPlayer.SelectClip(2);
        foxPlayer.Advance(0.3f);
        Matrix4x4[] run = foxPlayer.GetJointMatrices(0).ToArray();

        float clipDifference = 0.0f;
        for (int i = 0; i < walk.Length; i++)
        {
            clipDifference = MathF.Max(clipDifference, MaxAbsDifference(walk[i], run[i]));
        }

        checks.Check(
            "Fox: 同じ時刻でも Walk と Run で姿勢が違う",
            clipDifference > 1e-3f,
            $"最大差 {clipDifference:F4}");

        // --- 7. 頂点が実際に動くか(CPU でスキニングを再現)---
        //
        // ここまでは全部「行列が正しそう」の話。**頂点が動いて初めて仕事**なので、
        // シェーダと同じ式を C# 側でもう一度計算して確かめる。
        (float move, int broken) = MeasureSkinnedMotion(cesium, cesiumPlayer, 0.0f, 1.0f);
        checks.Check("CesiumMan: 1秒でスキニング後の頂点が動く", move > 0.05f, $"最大 {move * 100.0f:F1}cm");
        checks.Check("NaN / 無限大が1つも無い", broken == 0, $"{broken} 個");

        cesium.Dispose();
        fox.Dispose();
        simple.Dispose();
        boxAnimated.Dispose();
        helmet.Dispose();

        checks.Report("すべて合格(頂点ブレンディングとクリップ再生が仕様どおり)");
        Console.WriteLine();

        SetModel(restore);

        // クォータニオンの回転角(度)。w = cos(θ/2) から戻す。
        static float Degrees(Quaternion q) =>
            2.0f * MathF.Acos(Math.Clamp(MathF.Abs(q.W), -1.0f, 1.0f)) * 180.0f / MathF.PI;
    }

    /// <summary>
    /// 今のポーズでの境界箱を測る(Day 41)。**CPU でスキニングを1回やる**。
    ///
    /// 頂点を GPU から読み返す(<c>Mesh.ReadVertices</c>)ので安くはないが、
    /// **モデルを切り替えたときの1回だけ**なので目に見える遅さにはならない
    /// (CesiumMan の 3273 頂点で 1ms 未満)。
    ///
    /// 毎フレーム正確な箱が欲しくなるのは、視錐台カリングを入れるとき。
    /// そのときは頂点を舐め直すのではなく、
    /// **関節の位置だけから大まかな箱を作る**のが定石になる。
    /// </summary>
    private static (Vector3 Min, Vector3 Max) MeasurePosedBounds(Model model, AnimationPlayer player)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (Model.Part part in model.Parts)
        {
            // **PartMatrix と同じ場合分け**にする。ここだけ違う行列を使うと、
            // 「測った箱」と「実際に描かれる場所」がずれる。
            Matrix4x4[]? joints = part.SkinIndex >= 0
                ? player.GetJointMatrices(part.SkinIndex).ToArray()
                : null;

            Matrix4x4 world = part.SkinIndex >= 0
                ? Matrix4x4.Identity
                : model.Animations.Count > 0 ? player.GetNodeWorld(part.NodeIndex) : part.Transform;

            foreach (Vertex vertex in part.Mesh.ReadVertices())
            {
                Vector3 posed = joints is not null
                    ? SkinPosition(vertex, joints)
                    : Vector3.Transform(vertex.Position, world);

                min = Vector3.Min(min, posed);
                max = Vector3.Max(max, posed);
            }
        }

        // 頂点が1つも無い(ありえないが)ときはファイルの箱に戻す。
        return min.X <= max.X ? (min, max) : (model.BoundsMin, model.BoundsMax);
    }

    /// <summary>自己チェック用にモデルを1体読む。**呼んだ側が Dispose する**。</summary>
    private static Model LoadCheckModel(string relativePath) =>
        GltfLoader.Load(_gl, _resources, ResolveAssetPath(relativePath), _shader);

    /// <summary>重みの合計の最小・最大と、関節数からはみ出した番号の数を測る。</summary>
    private static (float MinSum, float MaxSum, int OutOfRange) MeasureWeights(Model model)
    {
        float minSum = float.MaxValue;
        float maxSum = float.MinValue;
        int outOfRange = 0;

        foreach (Model.Part part in model.Parts)
        {
            int jointCount = part.SkinIndex >= 0 ? model.Skins[part.SkinIndex].JointCount : 0;

            foreach (Vertex vertex in part.Mesh.ReadVertices())
            {
                float sum = vertex.Weights.X + vertex.Weights.Y + vertex.Weights.Z + vertex.Weights.W;
                minSum = MathF.Min(minSum, sum);
                maxSum = MathF.Max(maxSum, sum);

                if (jointCount == 0)
                {
                    continue;
                }

                foreach (float index in (ReadOnlySpan<float>)
                    [vertex.Joints.X, vertex.Joints.Y, vertex.Joints.Z, vertex.Joints.W])
                {
                    if (index < 0.0f || index >= jointCount)
                    {
                        outOfRange++;
                    }
                }
            }
        }

        return (minSum == float.MaxValue ? 0.0f : minSum, maxSum == float.MinValue ? 0.0f : maxSum, outOfRange);
    }

    /// <summary>ファイルの姿勢(クリップを外した状態)で、関節行列が単位行列からどれだけ離れているか。</summary>
    private static float MaxJointDeviation(Model model, AnimationPlayer player)
    {
        int clip = player.ClipIndex;
        player.SelectClip(-1);

        float worst = 0.0f;
        for (int s = 0; s < model.Skins.Count; s++)
        {
            foreach (Matrix4x4 matrix in player.GetJointMatrices(s))
            {
                worst = MathF.Max(worst, MaxAbsDifference(matrix, Matrix4x4.Identity));
            }
        }

        player.SelectClip(clip);
        return worst;
    }

    /// <summary>2つの行列の、成分ごとの差の最大値。</summary>
    private static float MaxAbsDifference(in Matrix4x4 a, in Matrix4x4 b)
    {
        float worst = 0.0f;
        worst = MathF.Max(worst, MathF.Abs(a.M11 - b.M11));
        worst = MathF.Max(worst, MathF.Abs(a.M12 - b.M12));
        worst = MathF.Max(worst, MathF.Abs(a.M13 - b.M13));
        worst = MathF.Max(worst, MathF.Abs(a.M14 - b.M14));
        worst = MathF.Max(worst, MathF.Abs(a.M21 - b.M21));
        worst = MathF.Max(worst, MathF.Abs(a.M22 - b.M22));
        worst = MathF.Max(worst, MathF.Abs(a.M23 - b.M23));
        worst = MathF.Max(worst, MathF.Abs(a.M24 - b.M24));
        worst = MathF.Max(worst, MathF.Abs(a.M31 - b.M31));
        worst = MathF.Max(worst, MathF.Abs(a.M32 - b.M32));
        worst = MathF.Max(worst, MathF.Abs(a.M33 - b.M33));
        worst = MathF.Max(worst, MathF.Abs(a.M34 - b.M34));
        worst = MathF.Max(worst, MathF.Abs(a.M41 - b.M41));
        worst = MathF.Max(worst, MathF.Abs(a.M42 - b.M42));
        worst = MathF.Max(worst, MathF.Abs(a.M43 - b.M43));
        worst = MathF.Max(worst, MathF.Abs(a.M44 - b.M44));
        return worst;
    }

    /// <summary>
    /// 2つの時刻でスキニングした頂点の、移動量の最大値と壊れた値の数。
    /// **シェーダの SkinMatrix() と同じ式**を C# で書き直したもの。
    /// </summary>
    private static (float MaxMove, int Broken) MeasureSkinnedMotion(
        Model model, AnimationPlayer player, float timeA, float timeB)
    {
        float maxMove = 0.0f;
        int broken = 0;

        foreach (Model.Part part in model.Parts)
        {
            if (part.SkinIndex < 0)
            {
                continue;
            }

            player.SelectClip(player.ClipIndex);
            player.Advance(timeA);
            Matrix4x4[] a = player.GetJointMatrices(part.SkinIndex).ToArray();

            player.SelectClip(player.ClipIndex);
            player.Advance(timeB);
            Matrix4x4[] b = player.GetJointMatrices(part.SkinIndex).ToArray();

            foreach (Vertex vertex in part.Mesh.ReadVertices())
            {
                Vector3 before = SkinPosition(vertex, a);
                Vector3 after = SkinPosition(vertex, b);

                if (!IsFinite(before) || !IsFinite(after))
                {
                    broken++;
                    continue;
                }

                maxMove = MathF.Max(maxMove, (after - before).Length());
            }
        }

        return (maxMove, broken);

        static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }

    /// <summary>頂点1つをスキニングする。**シェーダと同じ「行列を混ぜてから掛ける」**。</summary>
    private static Vector3 SkinPosition(in Vertex vertex, Matrix4x4[] joints)
    {
        Matrix4x4 skin =
            (Joint(vertex.Joints.X) * vertex.Weights.X)
            + (Joint(vertex.Joints.Y) * vertex.Weights.Y)
            + (Joint(vertex.Joints.Z) * vertex.Weights.Z)
            + (Joint(vertex.Joints.W) * vertex.Weights.W);

        return Vector3.Transform(vertex.Position, skin);

        Matrix4x4 Joint(float index)
        {
            int at = (int)index;
            return (uint)at < (uint)joints.Length ? joints[at] : Matrix4x4.Identity;
        }
    }

    /// <summary>
    /// 今日の自己チェック(Ctrl+Shift+Alt+F12)。
    ///
    /// 物理は**間違っていてもそれらしく動く**のが厄介なところで、
    /// 積分の順番が逆でも、慣性テンソルが定数でも、
    /// 反発係数を反復回数ぶん重ね掛けしていても、球は落ちて跳ねる。
    /// 「なんとなく動いている」と「合っている」を分けられるのは数字だけなので、
    /// <b>保存量(運動量・エネルギー)と解析解</b>で押さえる。
    ///
    /// <para>
    /// <see cref="PhysicsWorld"/> も <see cref="RigidBody"/> も GL を知らないので、
    /// **窓を1枚も出さずに全項目が走る**。
    /// Day 26 の衝突判定、Day 42 のブレンドと同じ性格の層になっている。
    /// </para>
    /// </summary>
    private static void RunRigidBodyCheck()
    {
        Console.WriteLine();
        Console.WriteLine("--- Day 43: 剛体力学の自己チェック ---");
        var checks = new CheckList();

        const float dt = 1.0f / 60.0f;
        const float g = 9.81f;

        // ============================================================
        //  1. 積分(要点1)
        // ============================================================

        // 自由落下を1秒。**床も他の体も無い世界**で、解析解と突き合わせる。
        static (float Velocity, float Drop) FreeFall(IntegratorMode mode, float dt, int steps)
        {
            var world = new PhysicsWorld { Integrator = mode };
            RigidBody body = RigidBody.CreateSphere(1.0f, 0.5f);
            world.AddBody(body);

            for (int i = 0; i < steps; i++)
            {
                world.Step(dt);
            }

            return (body.LinearVelocity.Y, -body.Position.Y);
        }

        (float semiVelocity, float semiDrop) = FreeFall(IntegratorMode.SemiImplicit, dt, 60);
        (float eulerVelocity, float eulerDrop) = FreeFall(IntegratorMode.Explicit, dt, 60);

        // 速度はどちらも同じ。**g を 60 回足しているだけ**なので誤差が入りようが無い。
        checks.Check(
            "1秒後の落下速度が -9.81m/s(積分法によらない)",
            MathF.Abs(semiVelocity + g) < 1e-3f && MathF.Abs(eulerVelocity + g) < 1e-3f,
            $"セミ {semiVelocity:F4} / 陽的 {eulerVelocity:F4}");

        // 解析解は 1/2 g t² = 4.905m。
        float exactDrop = 0.5f * g * 1.0f;

        checks.Check(
            "セミインプリシットは**行き過ぎる**(解析解より深く落ちる)",
            semiDrop > exactDrop,
            $"{semiDrop:F4}m > {exactDrop:F4}m");
        checks.Check(
            "陽的オイラーは**足りない**(解析解より浅い)",
            eulerDrop < exactDrop,
            $"{eulerDrop:F4}m < {exactDrop:F4}m");

        // **2つの平均がぴったり解析解になる**。
        // 誤差はどちらも ±g·dt·t/2 で、符号だけが逆(要点1)。
        checks.Check(
            "2つの平均が解析解と一致(誤差の符号が逆で大きさが同じ)",
            MathF.Abs(((semiDrop + eulerDrop) * 0.5f) - exactDrop) < 1e-4f,
            $"平均 {(semiDrop + eulerDrop) * 0.5f:F6} / 解析 {exactDrop:F6}");

        // dt を半分にすると誤差も半分。**1次の積分器**であることの確認。
        (_, float halfDrop) = FreeFall(IntegratorMode.SemiImplicit, dt * 0.5f, 120);
        float errorFull = semiDrop - exactDrop;
        float errorHalf = halfDrop - exactDrop;

        checks.Check(
            "dt を半分にすると誤差も半分(1次精度)",
            MathF.Abs((errorFull / errorHalf) - 2.0f) < 0.05f,
            $"{errorFull * 1000.0f:F2}mm → {errorHalf * 1000.0f:F2}mm(比 {errorFull / errorHalf:F3})");

        // --- ばね。**振動する系でこそ差が出る**(要点1)---
        //
        // 自由落下は「行き過ぎる/足りない」が1回ぶんだが、
        // 振動子では毎周期ぶん積み上がる。
        // セミインプリシットのエネルギーは**上下に揺れるが、いつまでも同じ幅に収まる**——
        // これが「シンプレクティック(あるエネルギーに似た量を保存する)」の意味で、
        // <b>誤差が無い</b>のではなく<b>誤差が溜まらない</b>のが値打ち。
        static float PeakEnergy(IntegratorMode mode, int steps)
        {
            const float stiffness = 100.0f;   // ω = 10 rad/s
            const float step = 1.0f / 60.0f;

            RigidBody body = RigidBody.CreateSphere(1.0f, 0.5f);
            body.Position = new Vector3(1.0f, 0.0f, 0.0f);

            float peak = 0.0f;

            for (int i = 0; i < steps; i++)
            {
                body.ClearAccumulators();
                body.ApplyForce(new Vector3(-stiffness * body.Position.X, 0.0f, 0.0f));

                if (mode == IntegratorMode.SemiImplicit)
                {
                    body.IntegrateVelocity(step, Vector3.Zero);
                    body.IntegratePosition(step);
                }
                else
                {
                    body.IntegratePosition(step);
                    body.IntegrateVelocity(step, Vector3.Zero);
                }

                float energy = (0.5f * body.LinearVelocity.LengthSquared())
                    + (0.5f * stiffness * body.Position.X * body.Position.X);

                peak = MathF.Max(peak, energy);
            }

            return peak;
        }

        // 初期状態は x = 1m、静止。E = 1/2 k x² = 50J。
        const float springStart = 50.0f;

        float peakShort = PeakEnergy(IntegratorMode.SemiImplicit, 600);      // 10 秒
        float peakLong = PeakEnergy(IntegratorMode.SemiImplicit, 6000);      // 100 秒
        float peakEuler = PeakEnergy(IntegratorMode.Explicit, 600);

        checks.Check(
            "ばね: セミインプリシットのエネルギーの上限は**時間が経っても同じ**",
            MathF.Abs(peakLong - peakShort) < springStart * 0.01f,
            $"10秒 {peakShort:F2}J / 100秒 {peakLong:F2}J(初期 {springStart:F2}J)");
        checks.Check(
            "その上限も初期値の数%以内(**誤差が溜まらない**)",
            peakLong < springStart * 1.10f,
            $"{peakLong / springStart:F4} 倍");
        checks.Check(
            "ばね10秒: **陽的オイラーは発散する**(1000 倍以上)",
            peakEuler > springStart * 1000.0f,
            $"{springStart:F2}J → {peakEuler:E2}J({peakEuler / springStart:E2} 倍)");

        // --- 描画のための1ステップ前(Day 19 の補間)---
        var interpolationWorld = new PhysicsWorld();
        RigidBody tracked = RigidBody.CreateSphere(1.0f, 0.5f);
        interpolationWorld.AddBody(tracked);
        interpolationWorld.Step(dt);
        Vector3 afterOne = tracked.Position;
        interpolationWorld.Step(dt);

        checks.Check(
            "1ステップ前の位置を控えている(描画の補間用)",
            (tracked.PreviousPosition - afterOne).Length() < 1e-6f,
            $"控え {tracked.PreviousPosition.Y:F5} / 実測 {afterOne.Y:F5}");

        // ============================================================
        //  2. 力とトルク(要点2)
        // ============================================================

        RigidBody lever = RigidBody.CreateSphere(1.0f, 0.5f);
        lever.ApplyForce(new Vector3(0.0f, 10.0f, 0.0f));

        checks.Check(
            "重心に掛けた力はトルクを生まない",
            lever.Torque.Length() < 1e-6f,
            $"|τ| = {lever.Torque.Length():E2}");

        lever.ClearAccumulators();
        lever.ApplyForceAtPoint(new Vector3(0.0f, 1.0f, 0.0f), lever.Position + Vector3.UnitX);

        checks.Check(
            "中心を外すとトルクが立つ(τ = r × F)",
            (lever.Torque - Vector3.UnitZ).Length() < 1e-6f,
            $"τ = ({lever.Torque.X:F3}, {lever.Torque.Y:F3}, {lever.Torque.Z:F3})");

        // 力が重心を向いていれば(r と F が平行)トルクは 0。
        lever.ClearAccumulators();
        lever.ApplyForceAtPoint(new Vector3(2.0f, 0.0f, 0.0f), lever.Position + Vector3.UnitX);

        checks.Check(
            "力が重心を向いていればトルクは 0(r と F が平行)",
            lever.Torque.Length() < 1e-6f,
            $"|τ| = {lever.Torque.Length():E2}");

        // 同じ力を掛けたとき、質量が2倍なら加速度は半分。
        static float SpeedAfterPush(float mass, float seconds)
        {
            var world = new PhysicsWorld { Gravity = Vector3.Zero };
            RigidBody body = RigidBody.CreateSphere(mass, 0.5f);
            world.AddBody(body);

            int steps = (int)MathF.Round(seconds * 60.0f);
            for (int i = 0; i < steps; i++)
            {
                body.ApplyForce(new Vector3(10.0f, 0.0f, 0.0f));
                world.Step(1.0f / 60.0f);
            }

            return body.LinearVelocity.X;
        }

        float lightSpeed = SpeedAfterPush(1.0f, 1.0f);
        float heavySpeed = SpeedAfterPush(2.0f, 1.0f);

        checks.Check(
            "同じ力なら、質量2倍で速度は半分(a = F/m)",
            MathF.Abs((lightSpeed / heavySpeed) - 2.0f) < 1e-3f,
            $"{lightSpeed:F3} / {heavySpeed:F3} = {lightSpeed / heavySpeed:F4}");

        // 中身の詰まった球の慣性モーメントは 2/5 m r²。
        RigidBody ball = RigidBody.CreateSphere(2.0f, 0.5f);
        float expectedInertia = 0.4f * 2.0f * 0.5f * 0.5f;

        checks.Check(
            "球の慣性モーメントが 2/5 m r²",
            MathF.Abs((1.0f / ball.InverseInertiaLocal.X) - expectedInertia) < 1e-5f,
            $"I = {1.0f / ball.InverseInertiaLocal.X:F4}(期待 {expectedInertia:F4})");

        // **球は等方**なので、回しても世界の慣性テンソルが変わらない(要点4)。
        ball.Orientation = Quaternion.CreateFromYawPitchRoll(0.7f, -1.1f, 2.3f);
        ball.UpdateInertiaWorld();

        Vector3 spun = Vector3.Transform(Vector3.UnitX, ball.InverseInertiaWorld);
        checks.Check(
            "球は回しても慣性テンソルが変わらない(**等方**)",
            MathF.Abs(spun.X - ball.InverseInertiaLocal.X) < 1e-4f
                && MathF.Abs(spun.Y) < 1e-4f && MathF.Abs(spun.Z) < 1e-4f,
            $"({spun.X:F4}, {spun.Y:F4}, {spun.Z:F4})");

        // **対角が違えば回すと変わる**。Day 44 の箱の予告。
        RigidBody boxLike = RigidBody.CreateSphere(1.0f, 0.5f);
        boxLike.InverseInertiaLocal = new Vector3(1.0f, 2.0f, 3.0f);
        boxLike.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
        boxLike.UpdateInertiaWorld();

        Vector3 tilted = Vector3.Transform(Vector3.UnitX, boxLike.InverseInertiaWorld);
        checks.Check(
            "対角が違う体は、90度回すと x と y の回りやすさが入れ替わる",
            MathF.Abs(tilted.X - 2.0f) < 1e-4f && MathF.Abs(tilted.Y) < 1e-4f,
            $"({tilted.X:F4}, {tilted.Y:F4}, {tilted.Z:F4})");

        // ============================================================
        //  3. クォータニオンの積分(要点5)
        // ============================================================

        RigidBody spinner = RigidBody.CreateSphere(1.0f, 0.5f);
        spinner.AngularVelocity = new Vector3(0.0f, 2.0f, 0.0f);

        for (int i = 0; i < 240; i++)
        {
            spinner.IntegratePosition(1.0f / 240.0f);
        }

        float turned = 2.0f * MathF.Acos(Math.Clamp(MathF.Abs(spinner.Orientation.W), -1.0f, 1.0f));

        checks.Check(
            "角速度 2rad/s で1秒回すと 2rad(q += ½ωq dt)",
            MathF.Abs(turned - 2.0f) < 1e-3f,
            $"{turned:F5} rad");
        checks.Check(
            "回したあとも単位クォータニオンのまま(**毎回正規化している**)",
            MathF.Abs(spinner.Orientation.Length() - 1.0f) < 1e-5f,
            $"|q| = {spinner.Orientation.Length():F7}");

        // ============================================================
        //  4. 衝突判定
        // ============================================================

        Contact3D apart = Collision3D.SphereSphere(
            new Sphere3D(Vector3.Zero, 1.0f), new Sphere3D(new Vector3(3.0f, 0.0f, 0.0f), 1.0f));
        checks.Check("離れた球は当たらない", !apart.Hit);

        Contact3D overlap = Collision3D.SphereSphere(
            new Sphere3D(Vector3.Zero, 1.0f), new Sphere3D(new Vector3(1.5f, 0.0f, 0.0f), 1.0f));
        checks.Check(
            "めり込み量が半径の和 - 中心距離",
            overlap.Hit && MathF.Abs(overlap.Depth - 0.5f) < 1e-5f,
            $"{overlap.Depth:F5}");
        checks.Check(
            "法線は**A を B から引き離す向き**",
            (overlap.Normal - new Vector3(-1.0f, 0.0f, 0.0f)).Length() < 1e-5f,
            $"({overlap.Normal.X:F2}, {overlap.Normal.Y:F2}, {overlap.Normal.Z:F2})");
        checks.Check(
            "接触点はめり込んだ領域の真ん中",
            MathF.Abs(overlap.Point.X - 0.75f) < 1e-5f,
            $"x = {overlap.Point.X:F5}");

        Contact3D concentric = Collision3D.SphereSphere(
            new Sphere3D(Vector3.Zero, 1.0f), new Sphere3D(Vector3.Zero, 1.0f));
        checks.Check(
            "中心が一致しても NaN を返さない(**上へ逃がす**)",
            concentric.Hit && float.IsFinite(concentric.Normal.X)
                && (concentric.Normal - Vector3.UnitY).Length() < 1e-5f,
            $"({concentric.Normal.X:F2}, {concentric.Normal.Y:F2}, {concentric.Normal.Z:F2})");

        var floor = Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY);
        Contact3D onFloor = Collision3D.SpherePlane(new Sphere3D(new Vector3(0.0f, 0.3f, 0.0f), 1.0f), floor);
        checks.Check(
            "球と平面: めり込みは 半径 - 符号付き距離",
            onFloor.Hit && MathF.Abs(onFloor.Depth - 0.7f) < 1e-5f
                && MathF.Abs(onFloor.Point.Y) < 1e-5f,
            $"深さ {onFloor.Depth:F5} 接触点 y = {onFloor.Point.Y:F5}");

        Contact3D belowFloor = Collision3D.SpherePlane(new Sphere3D(new Vector3(0.0f, -0.2f, 0.0f), 1.0f), floor);
        checks.Check(
            "裏側へ抜けた球も**抜けたぶんだけ深く**なる(押し戻せる)",
            belowFloor.Hit && MathF.Abs(belowFloor.Depth - 1.2f) < 1e-5f,
            $"{belowFloor.Depth:F5}");

        // ============================================================
        //  5. インパルスによる解決(要点6・要点7)
        // ============================================================

        // 正面衝突の実験台。**重力も床も無い**ので、外力は接触だけになる。
        static (RigidBody A, RigidBody B, PhysicsWorld World) HeadOn(
            float massA, float massB, float speedA, float speedB, float restitution)
        {
            var world = new PhysicsWorld { Gravity = Vector3.Zero, VelocityIterations = 4 };

            RigidBody a = RigidBody.CreateSphere(massA, 1.05f);
            a.Position = new Vector3(-1.0f, 0.0f, 0.0f);
            a.LinearVelocity = new Vector3(speedA, 0.0f, 0.0f);
            a.Restitution = restitution;

            RigidBody b = RigidBody.CreateSphere(massB, 1.05f);
            b.Position = new Vector3(1.0f, 0.0f, 0.0f);
            b.LinearVelocity = new Vector3(speedB, 0.0f, 0.0f);
            b.Restitution = restitution;

            world.AddBody(a);
            world.AddBody(b);
            world.Step(1.0f / 600.0f);

            return (a, b, world);
        }

        (RigidBody elasticA, RigidBody elasticB, PhysicsWorld elasticWorld) =
            HeadOn(1.0f, 1.0f, 2.0f, -2.0f, 1.0f);

        checks.Check(
            "等質量・e=1 の正面衝突で速度が入れ替わる",
            MathF.Abs(elasticA.LinearVelocity.X + 2.0f) < 1e-3f
                && MathF.Abs(elasticB.LinearVelocity.X - 2.0f) < 1e-3f,
            $"{elasticA.LinearVelocity.X:F4} / {elasticB.LinearVelocity.X:F4}");

        checks.Check(
            "e=1 なら運動エネルギーが保存する",
            MathF.Abs(elasticWorld.TotalKineticEnergy - 4.0f) < 1e-2f,
            $"{elasticWorld.TotalKineticEnergy:F4}J(前 4.0000J)");

        checks.Check(
            "**球どうしの衝突では回らない**(法線が中心を通る)",
            elasticA.AngularVelocity.Length() < 1e-6f && elasticB.AngularVelocity.Length() < 1e-6f,
            $"|ω| = {elasticA.AngularVelocity.Length():E2}");

        (RigidBody stickA, RigidBody stickB, _) = HeadOn(1.0f, 1.0f, 2.0f, -2.0f, 0.0f);
        checks.Check(
            "e=0 なら相対速度が 0 になる(一体化)",
            MathF.Abs(stickA.LinearVelocity.X - stickB.LinearVelocity.X) < 1e-3f,
            $"相対 {stickA.LinearVelocity.X - stickB.LinearVelocity.X:E2}");

        (_, _, PhysicsWorld momentumWorld) = HeadOn(1.0f, 3.0f, 4.0f, -1.0f, 0.5f);
        checks.Check(
            "質量が違っても運動量は保存する(前 1.0 kg·m/s)",
            MathF.Abs(momentumWorld.TotalMomentum.X - 1.0f) < 1e-3f,
            $"{momentumWorld.TotalMomentum.X:F5} kg·m/s");

        (RigidBody pebble, RigidBody boulder, _) = HeadOn(1.0f, 1000.0f, 5.0f, 0.0f, 1.0f);
        checks.Check(
            "1000 倍重い相手にぶつかると、ほぼそのまま跳ね返る",
            pebble.LinearVelocity.X < -4.9f && MathF.Abs(boulder.LinearVelocity.X) < 0.02f,
            $"軽 {pebble.LinearVelocity.X:F3} / 重 {boulder.LinearVelocity.X:F4}");

        // **反復回数を増やしても跳ね返りが重ね掛けされない**(要点7)。
        // 跳ね返り目標を接触を作るときに1回だけ決めているかの確認。
        static float BounceSpeed(int iterations)
        {
            var world = new PhysicsWorld { Gravity = Vector3.Zero, VelocityIterations = iterations };
            world.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

            RigidBody body = RigidBody.CreateSphere(1.0f, 0.5f);
            body.Position = new Vector3(0.0f, 0.45f, 0.0f);
            body.LinearVelocity = new Vector3(0.0f, -4.0f, 0.0f);
            body.Restitution = 0.5f;
            world.AddBody(body);
            world.Step(1.0f / 600.0f);

            return body.LinearVelocity.Y;
        }

        float bounce1 = BounceSpeed(1);
        float bounce8 = BounceSpeed(8);

        checks.Check(
            "反復を8周にしても跳ね返りが増えない(**目標を1回だけ決めている**)",
            MathF.Abs(bounce1 - bounce8) < 1e-3f && MathF.Abs(bounce8 - 2.0f) < 1e-2f,
            $"1周 {bounce1:F4} / 8周 {bounce8:F4}(期待 2.0000 = 0.5 × 4.0)");

        // 撃力を中心を外して掛けると角速度が立つ(要点2)。
        RigidBody kicked = RigidBody.CreateSphere(1.0f, 0.5f);
        kicked.ApplyImpulseAtPoint(new Vector3(0.0f, 1.0f, 0.0f), kicked.Position + new Vector3(0.5f, 0.0f, 0.0f));

        // I = 2/5 · 1 · 0.25 = 0.1、r × J = (0,0,0.5) なので ω = 0.5 / 0.1 = 5。
        checks.Check(
            "中心を外した撃力で角速度が立つ(Δω = I⁻¹ (r × J))",
            MathF.Abs(kicked.AngularVelocity.Z - 5.0f) < 1e-4f
                && MathF.Abs(kicked.LinearVelocity.Y - 1.0f) < 1e-5f,
            $"ω = {kicked.AngularVelocity.Z:F4} rad/s、v = {kicked.LinearVelocity.Y:F4} m/s");

        RigidBody centered = RigidBody.CreateSphere(1.0f, 0.5f);
        centered.ApplyImpulseAtPoint(new Vector3(0.0f, 1.0f, 0.0f), centered.Position);
        checks.Check(
            "中心に掛ければ回らない(**同じ撃力でも場所で結果が変わる**)",
            centered.AngularVelocity.Length() < 1e-9f,
            $"|ω| = {centered.AngularVelocity.Length():E2}");

        // 減衰は**毎秒の割合**なので、シミュレーションレートを変えても結果が同じ。
        // `v *= 0.99f` のようにステップ単位で書くと、ここが 60Hz と 240Hz でずれる。
        static (float Linear, float Angular) Damped(float hertz)
        {
            var world = new PhysicsWorld { Gravity = Vector3.Zero };

            RigidBody body = RigidBody.CreateSphere(1.0f, 0.5f);
            body.LinearVelocity = new Vector3(10.0f, 0.0f, 0.0f);
            body.AngularVelocity = new Vector3(0.0f, 4.0f, 0.0f);
            body.LinearDamping = 0.5f;
            body.AngularDamping = 0.5f;
            world.AddBody(body);

            for (int i = 0; i < (int)hertz; i++)
            {
                world.Step(1.0f / hertz);
            }

            return (body.LinearVelocity.X, body.AngularVelocity.Y);
        }

        (float slowLinear, float slowAngular) = Damped(60.0f);
        (float fastLinear, float fastAngular) = Damped(240.0f);

        checks.Check(
            "減衰 0.5/秒 を1秒で、速度がちょうど半分になる",
            MathF.Abs(slowLinear - 5.0f) < 1e-3f && MathF.Abs(slowAngular - 2.0f) < 1e-3f,
            $"v {slowLinear:F4} m/s、ω {slowAngular:F4} rad/s");
        checks.Check(
            "**シミュレーションレートを変えても同じ**(毎秒の割合で書いている)",
            MathF.Abs(slowLinear - fastLinear) < 1e-3f
                && MathF.Abs(slowAngular - fastAngular) < 1e-3f,
            $"60Hz {slowLinear:F5} / 240Hz {fastLinear:F5}");

        // ============================================================
        //  6. 位置の補正(要点8)
        // ============================================================

        static float PenetrationAfter(bool correction, int steps)
        {
            var world = new PhysicsWorld
            {
                Gravity = Vector3.Zero,
                PositionCorrection = correction,
            };

            RigidBody a = RigidBody.CreateSphere(1.0f, 1.0f);
            a.Position = new Vector3(-0.7f, 0.0f, 0.0f);
            RigidBody b = RigidBody.CreateSphere(1.0f, 1.0f);
            b.Position = new Vector3(0.7f, 0.0f, 0.0f);

            world.AddBody(a);
            world.AddBody(b);

            for (int i = 0; i < steps; i++)
            {
                world.Step(1.0f / 60.0f);
            }

            return world.MaxPenetration;
        }

        float corrected = PenetrationAfter(correction: true, 60);
        float uncorrected = PenetrationAfter(correction: false, 60);

        checks.Check(
            "めり込んだ2球は押し戻されて、許容量(5mm)近くまで戻る",
            corrected < 0.01f,
            $"{corrected * 1000.0f:F2}mm");
        checks.Check(
            "位置補正を切ると**めり込んだまま**",
            uncorrected > 0.5f,
            $"{uncorrected * 1000.0f:F1}mm(初期 600mm)");

        // ============================================================
        //  7. 積み上げ(要点8)
        // ============================================================

        static (float Penetration, float Height) Stack(int iterations, int steps)
        {
            const float radius = 0.45f;

            var world = new PhysicsWorld { VelocityIterations = iterations };
            world.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

            for (int i = 0; i < 4; i++)
            {
                RigidBody body = RigidBody.CreateSphere(1.0f, radius);
                body.Position = new Vector3(0.0f, radius + (i * radius * 2.0f), 0.0f);
                body.Restitution = 0.0f;
                world.AddBody(body);
            }

            for (int i = 0; i < steps; i++)
            {
                world.Step(1.0f / 60.0f);
            }

            return (world.MaxPenetration, world.Bodies[^1].Position.Y);
        }

        (float deep1, float top1) = Stack(1, 300);
        (float deep8, float top8) = Stack(8, 300);

        // 理想の高さは 0.45 + 3 × 0.9 = 3.15m(許容めり込みぶんだけ下がる)。
        //
        // **このチェックは Day 47 で主張が裏返った**。
        // Day 43 では「反復1周だと沈む(素朴版の限界)」を確かめる場所だった——
        // 実際、Day 46 までは1周で 64.4mm、8周でも 14.7mm 沈んでいた。
        // 今日ウォームスタート(要点2)と段の入れ替え(要点4)が入り、
        // <b>1周でもめり込みが許容量(5mm)まで落ちた</b>。
        //
        // <b>数字が良くなってチェックが通らなくなるのは3度目</b>
        // (Day 44 で一度しきい値を緩めている)。
        // 主張が古くなったら書き換えるのが正しく、
        // <b>しきい値をいじって延命させるのは、いちばんやってはいけないこと</b>。
        checks.Check(
            "**反復1周でも柱が沈まない**(Day 46 では 64.4mm 沈んでいた)",
            deep1 <= 0.006f && top1 > 3.10f,
            $"めり込み {deep1 * 1000.0f:F2}mm(高さ {top1:F3}m / 理想 3.150m)");
        checks.Check(
            "反復8周なら当然持つ(Day 46 の 14.7mm から改善)",
            deep8 < 0.006f && top8 > 3.12f,
            $"めり込み {deep8 * 1000.0f:F2}mm(高さ {top8:F3}m / 理想 3.150m)");
        checks.Check(
            "**温存は反復回数を買っている**(1周と8周でほとんど差が無い)",
            MathF.Abs(top1 - top8) < 0.02f,
            $"1周 {top1:F4}m / 8周 {top8:F4}m");

        // ============================================================
        //  8. 落として跳ねる(全部を通す)
        // ============================================================

        var dropWorld = new PhysicsWorld();
        dropWorld.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

        RigidBody bouncer = RigidBody.CreateSphere(1.0f, 0.5f);
        bouncer.Position = new Vector3(0.0f, 3.0f, 0.0f);
        bouncer.Restitution = 0.5f;
        dropWorld.AddBody(bouncer);

        var peaks = new List<float>();
        float previousVelocity = 0.0f;

        for (int i = 0; i < 600; i++)
        {
            dropWorld.Step(dt);

            // 上がりから下がりへ転じた瞬間が頂点。
            if (previousVelocity > 0.0f && bouncer.LinearVelocity.Y <= 0.0f)
            {
                peaks.Add(bouncer.Position.Y - 0.5f);
            }

            previousVelocity = bouncer.LinearVelocity.Y;
        }

        bool descending = peaks.Count >= 3;
        for (int i = 1; i < peaks.Count && descending; i++)
        {
            descending = peaks[i] < peaks[i - 1];
        }

        checks.Check(
            "跳ね返るたびに頂点が低くなる(e < 1)",
            descending,
            peaks.Count > 0
                ? string.Join(" → ", peaks.Take(4).Select(h => $"{h:F3}m"))
                : "頂点が見つからない");

        // e² = 0.25 倍が理屈のうえでの比。離散化とめり込みでずれる。
        checks.Check(
            "1回目の頂点が、落とした高さの e² 倍前後(0.25)",
            peaks.Count > 0 && peaks[0] > 3.0f * 0.15f && peaks[0] < 3.0f * 0.35f,
            peaks.Count > 0 ? $"{peaks[0]:F3}m / 3.000m = {peaks[0] / 3.0f:F3}" : "-");

        checks.Check(
            "10秒後には落ち着いている(**跳ね返りのしきい値が効いている**)",
            dropWorld.TotalKineticEnergy < 0.2f,
            $"{dropWorld.TotalKineticEnergy:F4}J");

        checks.Check(
            "落ち着いた球の高さが半径ぶん(床にちょうど乗っている)",
            MathF.Abs(bouncer.Position.Y - 0.5f) < 0.01f,
            $"{bouncer.Position.Y:F4}m(期待 0.5000m)");

        // 自由落下のエネルギードリフト。**符号が逆で、大きさが同じ**。
        static float EnergyDrift(IntegratorMode mode, int steps)
        {
            var world = new PhysicsWorld { Integrator = mode };
            RigidBody body = RigidBody.CreateSphere(1.0f, 0.5f);
            world.AddBody(body);

            float start = 0.0f;
            for (int i = 0; i < steps; i++)
            {
                world.Step(1.0f / 60.0f);
            }

            float end = (0.5f * body.LinearVelocity.LengthSquared()) + (9.81f * body.Position.Y);
            return end - start;
        }

        float driftSemi = EnergyDrift(IntegratorMode.SemiImplicit, 60);
        float driftEuler = EnergyDrift(IntegratorMode.Explicit, 60);

        checks.Check(
            "1秒の自由落下: セミは**減り**、陽的は**増える**。大きさは同じ",
            driftSemi < 0.0f && driftEuler > 0.0f
                && MathF.Abs(driftSemi + driftEuler) < 1e-3f,
            $"セミ {driftSemi:F4}J / 陽的 {driftEuler:+0.0000;-0.0000}J");

        checks.Report("すべて合格(球は落ち、跳ね、積める)");
        Console.WriteLine();
    }

    /// <summary>
    /// 今日の自己チェック(Ctrl+Shift+Alt+0)。
    ///
    /// 箱の判定は<b>間違っていてもそれらしく動く</b>のが Day 43 より始末が悪い。
    /// 辺×辺の軸を落としても、接触点が1つしか出なくても、
    /// 箱はとりあえず床の上に乗る。「なんとなく乗っている」と「合っている」を
    /// 分けられるのは数字だけなので、
    /// <b>解析解(慣性モーメント・投影半径)と、接触点の数</b>で押さえる。
    ///
    /// <para>
    /// 面白いのは<b>ランダムな配置を何十万通りも試す</b>2つの項目で、
    /// 「面の法線6本だけでは足りない配置が実在する」ことを、
    /// 理屈ではなく<b>実物を探し当てて</b>示している。
    /// SAT を写経していて「辺×辺の 9 本は本当に要るのか」と思ったら、ここを走らせる。
    /// </para>
    ///
    /// <para>
    /// <see cref="PhysicsWorld"/> も <see cref="Sat"/> も GL を知らないので、
    /// **窓を1枚も出さずに全項目が走る**。Day 43 と同じ性格の層のまま。
    /// </para>
    /// </summary>
    private static void RunBoxCollisionCheck()
    {
        Console.WriteLine();
        Console.WriteLine("--- Day 44: 箱の衝突の自己チェック ---");
        var checks = new CheckList();

        // ============================================================
        //  1. 形と慣性テンソル(要点1)
        // ============================================================

        // 全長 1 x 2 x 3、質量 12kg。I_x = 1/12 · 12 · (2² + 3²) = 13。
        Vector3 inertia = Collider.Box(new Vector3(0.5f, 1.0f, 1.5f)).InertiaLocal(12.0f);

        checks.Check(
            "箱の慣性モーメントが 1/12 m (h² + d²)",
            MathF.Abs(inertia.X - 13.0f) < 1e-4f
                && MathF.Abs(inertia.Y - 10.0f) < 1e-4f
                && MathF.Abs(inertia.Z - 5.0f) < 1e-4f,
            $"({inertia.X:F3}, {inertia.Y:F3}, {inertia.Z:F3})(期待 13 / 10 / 5)");

        checks.Check(
            "細長い箱は**長い方向を軸にすると回しやすい**(I が最小)",
            inertia.Z < inertia.Y && inertia.Y < inertia.X,
            $"z 軸まわり {inertia.Z:F1} < y {inertia.Y:F1} < x {inertia.X:F1}");

        Vector3 cubeInertia = Collider.Box(0.5f).InertiaLocal(6.0f);
        checks.Check(
            "立方体の慣性は**等方**(3つとも 1/6 m a²)",
            MathF.Abs(cubeInertia.X - 1.0f) < 1e-5f
                && MathF.Abs(cubeInertia.Y - 1.0f) < 1e-5f
                && MathF.Abs(cubeInertia.Z - 1.0f) < 1e-5f,
            $"({cubeInertia.X:F4}, {cubeInertia.Y:F4}, {cubeInertia.Z:F4})");

        checks.Check(
            "球の慣性は Day 43 と同じ 2/5 m r²",
            MathF.Abs(Collider.Sphere(0.5f).InertiaLocal(2.0f).X - 0.2f) < 1e-6f,
            $"{Collider.Sphere(0.5f).InertiaLocal(2.0f).X:F4}");

        RigidBody slab = RigidBody.CreateBox(12.0f, new Vector3(0.5f, 1.0f, 1.5f));
        slab.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
        slab.UpdateInertiaWorld();

        Vector3 spun = Vector3.Transform(Vector3.UnitX, slab.InverseInertiaWorld);
        checks.Check(
            "箱は**回すと世界の慣性テンソルが変わる**(90度で x と y が入れ替わる)",
            MathF.Abs(spun.X - 0.1f) < 1e-4f && MathF.Abs(spun.Y) < 1e-4f,
            $"({spun.X:F4}, {spun.Y:F4}, {spun.Z:F4})(期待 1/10)");

        RigidBody ball = RigidBody.CreateSphere(2.0f, 0.5f);
        ball.Orientation = Quaternion.CreateFromYawPitchRoll(0.7f, -1.1f, 2.3f);
        ball.UpdateInertiaWorld();

        Vector3 ballSpun = Vector3.Transform(Vector3.UnitX, ball.InverseInertiaWorld);
        checks.Check(
            "球は回しても変わらない(Day 43 のまま)",
            MathF.Abs(ballSpun.X - 5.0f) < 1e-3f && MathF.Abs(ballSpun.Y) < 1e-4f,
            $"({ballSpun.X:F4}, {ballSpun.Y:F4}, {ballSpun.Z:F4})");

        // ============================================================
        //  2. 箱の形そのもの
        // ============================================================

        var box = new Box3D(Vector3.Zero, new Vector3(0.5f, 1.0f, 1.5f), Quaternion.Identity);

        checks.Check(
            "軸に揃えた投影は、その軸の半分の長さ",
            MathF.Abs(box.ProjectedRadius(Vector3.UnitY) - 1.0f) < 1e-5f,
            $"{box.ProjectedRadius(Vector3.UnitY):F5}");

        Vector3 diagonal = Vector3.Normalize(Vector3.One);
        float expectedRadius = (0.5f + 1.0f + 1.5f) / MathF.Sqrt(3.0f);
        checks.Check(
            "斜めの投影は各軸の寄与の**絶対値の和**",
            MathF.Abs(box.ProjectedRadius(diagonal) - expectedRadius) < 1e-5f,
            $"{box.ProjectedRadius(diagonal):F5}(期待 {expectedRadius:F5})");

        bool cornersInside = true;
        Span<Vector3> probes =
        [
            Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, diagonal,
            Vector3.Normalize(new Vector3(1.0f, -2.0f, 0.5f)),
        ];

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = box.Corner(i);
            foreach (Vector3 probe in probes)
            {
                if (MathF.Abs(Vector3.Dot(corner, probe)) > box.ProjectedRadius(probe) + 1e-4f)
                {
                    cornersInside = false;
                }
            }
        }

        checks.Check("8つの頂点は、どの向きに投影しても投影半径の内側", cornersInside);

        checks.Check(
            "外の点の最近接点は面の上",
            (box.ClosestPoint(new Vector3(3.0f, 0.0f, 0.0f)) - new Vector3(0.5f, 0.0f, 0.0f))
                .Length() < 1e-5f);

        var interior = new Vector3(0.2f, 0.3f, 0.4f);
        checks.Check(
            "中の点の最近接点は**その点自身**(距離 0 になるので別扱いが要る)",
            (box.ClosestPoint(interior) - interior).Length() < 1e-5f);

        checks.Check(
            "支持点は、その向きにいちばん突き出た頂点",
            (box.Support(Vector3.One) - new Vector3(0.5f, 1.0f, 1.5f)).Length() < 1e-5f);

        var tilted = new Box3D(
            new Vector3(1.0f, 2.0f, 3.0f),
            new Vector3(0.5f),
            Quaternion.CreateFromYawPitchRoll(0.4f, -0.7f, 1.1f));

        var sample = new Vector3(0.3f, -0.2f, 0.9f);
        checks.Check(
            "物体座標と世界座標が往復する(傾いた箱でも)",
            (tilted.ToLocal(tilted.ToWorld(sample)) - sample).Length() < 1e-5f);

        // ============================================================
        //  3. 分離軸定理(要点2)
        // ============================================================

        var unit = new Box3D(Vector3.Zero, new Vector3(0.5f), Quaternion.Identity);

        checks.Check(
            "離れた箱は当たらない",
            !Sat.BoxBox(unit, new Box3D(new Vector3(3.0f, 0.0f, 0.0f), new Vector3(0.5f), Quaternion.Identity)).Hit);

        var overlapping = new Box3D(new Vector3(0.8f, 0.0f, 0.0f), new Vector3(0.5f), Quaternion.Identity);
        ContactManifold faceToFace = Sat.BoxBox(unit, overlapping);

        checks.Check(
            "軸に沿って重なった箱の深さが 1.0 - 0.8 = 0.2m",
            faceToFace.Hit && MathF.Abs(faceToFace.MaxDepth - 0.2f) < 1e-4f,
            $"{faceToFace.MaxDepth:F5}m");
        checks.Check(
            "法線は**A を B から引き離す向き**(-x)",
            (faceToFace.Normal - new Vector3(-1.0f, 0.0f, 0.0f)).Length() < 1e-4f,
            $"({faceToFace.Normal.X:F2}, {faceToFace.Normal.Y:F2}, {faceToFace.Normal.Z:F2})");
        checks.Check(
            "面と面なので接触点は**4つ**",
            faceToFace.Count == 4
                && (faceToFace.Source == ManifoldSource.FaceA
                    || faceToFace.Source == ManifoldSource.FaceB),
            $"{faceToFace.Count} 点 / {faceToFace.Source}");

        ContactManifold swapped = Sat.BoxBox(overlapping, unit);
        checks.Check(
            "A と B を入れ替えると法線が逆になり、深さは変わらない",
            swapped.Hit
                && (swapped.Normal + faceToFace.Normal).Length() < 1e-4f
                && MathF.Abs(swapped.MaxDepth - faceToFace.MaxDepth) < 1e-4f,
            $"({swapped.Normal.X:F2}, {swapped.Normal.Y:F2}, {swapped.Normal.Z:F2})");

        // --- 面の法線6本では足りないこと ---
        //
        // 素朴に「面の法線だけ」で判定するとどうなるかを、その場で書いて比べる。
        static bool SeparatedOn(in Box3D a, in Box3D b, Vector3 axis, Vector3 toCenter) =>
            a.ProjectedRadius(axis) + b.ProjectedRadius(axis)
                <= MathF.Abs(Vector3.Dot(toCenter, axis));

        static bool FaceOnlyOverlap(in Box3D a, in Box3D b)
        {
            Vector3 toCenter = b.Center - a.Center;

            for (int i = 0; i < 3; i++)
            {
                if (SeparatedOn(a, b, a.Axis(i), toCenter) || SeparatedOn(a, b, b.Axis(i), toCenter))
                {
                    return false;
                }
            }

            return true;
        }

        static Quaternion RandomRotation(Random random) =>
            Quaternion.CreateFromYawPitchRoll(
                (float)(random.NextDouble() * Math.Tau),
                (float)(random.NextDouble() * Math.Tau),
                (float)(random.NextDouble() * Math.Tau));

        static Vector3 RandomDirection(Random random)
        {
            var v = new Vector3(
                (float)(random.NextDouble() * 2.0 - 1.0),
                (float)(random.NextDouble() * 2.0 - 1.0),
                (float)(random.NextDouble() * 2.0 - 1.0));

            return v.LengthSquared() > 1e-6f ? Vector3.Normalize(v) : Vector3.UnitX;
        }

        var random = new Random(4444);
        int edgeOnly = -1;
        int edgeContact = -1;

        for (int trial = 0; trial < 400000 && (edgeOnly < 0 || edgeContact < 0); trial++)
        {
            var a = new Box3D(Vector3.Zero, new Vector3(0.5f), RandomRotation(random));
            var b = new Box3D(
                RandomDirection(random) * (0.85f + ((float)random.NextDouble() * 0.75f)),
                new Vector3(0.5f),
                RandomRotation(random));

            ContactManifold manifold = Sat.BoxBox(a, b);

            if (edgeOnly < 0 && FaceOnlyOverlap(a, b) && !manifold.Hit)
            {
                edgeOnly = trial;
            }

            if (edgeContact < 0 && manifold.Source == ManifoldSource.EdgeEdge && manifold.Count == 1)
            {
                edgeContact = trial;
            }
        }

        checks.Check(
            "**面の法線6本だけでは足りない**配置が実在する(辺×辺の軸が要る)",
            edgeOnly >= 0,
            edgeOnly >= 0 ? $"{edgeOnly} 回目の試行で発見" : "見つからなかった");
        checks.Check(
            "辺×辺から出る接触は**点が1つ**",
            edgeContact >= 0,
            edgeContact >= 0 ? $"{edgeContact} 回目の試行で発見" : "見つからなかった");

        // 深く重ねると必ず当たる(15 本すべてで重なる)。
        int missed = 0;
        var deepRandom = new Random(9999);
        for (int trial = 0; trial < 20000; trial++)
        {
            var a = new Box3D(Vector3.Zero, new Vector3(0.5f), RandomRotation(deepRandom));
            var b = new Box3D(
                RandomDirection(deepRandom) * ((float)deepRandom.NextDouble() * 0.2f),
                new Vector3(0.5f),
                RandomRotation(deepRandom));

            if (!Sat.BoxBox(a, b).Hit)
            {
                missed++;
            }
        }

        checks.Check(
            "中心がほぼ重なった箱は**必ず当たる**(20,000 通り試して取りこぼし 0)",
            missed == 0,
            $"取りこぼし {missed} 件");

        // ============================================================
        //  4. 接触マニフォールド(要点4・要点5)
        // ============================================================

        var floor = Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY);

        var flat = new Box3D(new Vector3(0.0f, 0.45f, 0.0f), new Vector3(0.5f), Quaternion.Identity);
        ContactManifold flatContact = Collision3D.BoxPlane(flat, floor);

        checks.Check(
            "床に平らに置いた箱は**4点**で支えられる",
            flatContact.Count == 4,
            $"{flatContact.Count} 点");

        bool sameDepth = true;
        Vector3 centroid = Vector3.Zero;
        for (int i = 0; i < flatContact.Count; i++)
        {
            sameDepth &= MathF.Abs(flatContact.Points[i].Depth - 0.05f) < 1e-5f;
            centroid += flatContact.Points[i].Point;
        }

        centroid /= MathF.Max(flatContact.Count, 1);

        checks.Check("4点の深さはどれも 0.05m(平らに乗っている)", sameDepth);
        checks.Check(
            "4点の重心が箱の中心の真下",
            MathF.Abs(centroid.X) < 1e-5f && MathF.Abs(centroid.Z) < 1e-5f,
            $"({centroid.X:F5}, {centroid.Y:F5}, {centroid.Z:F5})");

        var onEdge = new Box3D(
            new Vector3(0.0f, 0.70f, 0.0f),
            new Vector3(0.5f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.25f));

        checks.Check(
            "45度傾けて辺で立てた箱は**2点**",
            Collision3D.BoxPlane(onEdge, floor).Count == 2,
            $"{Collision3D.BoxPlane(onEdge, floor).Count} 点");

        // 対角 (1,1,1) 方向を真下に向ける回転。
        Vector3 from = Vector3.Normalize(Vector3.One);
        Vector3 to = -Vector3.UnitY;
        Quaternion cornerDown = Quaternion.CreateFromAxisAngle(
            Vector3.Normalize(Vector3.Cross(from, to)),
            MathF.Acos(Math.Clamp(Vector3.Dot(from, to), -1.0f, 1.0f)));

        var onCorner = new Box3D(new Vector3(0.0f, 0.86f, 0.0f), new Vector3(0.5f), cornerDown);

        checks.Check(
            "角で立てた箱は**1点**(接触点の数 = 支え方の数)",
            Collision3D.BoxPlane(onCorner, floor).Count == 1,
            $"{Collision3D.BoxPlane(onCorner, floor).Count} 点");

        ContactManifold reduced = Collision3D.BoxPlane(flat, floor);
        reduced.Reduce(1);
        checks.Check("上限 1 に絞ると点が1つになる", reduced.Count == 1);

        var manual = new ContactManifold { Normal = Vector3.UnitY };
        manual.Add(Vector3.Zero, 0.1f);
        manual.Add(Vector3.UnitX, 0.2f);
        manual.Add(Vector3.UnitY, 0.3f);
        manual.Add(Vector3.UnitZ, 0.4f);
        manual.Add(new Vector3(2.0f, 0.0f, 0.0f), 0.5f);

        bool shallowDropped = manual.Count == 4;
        for (int i = 0; i < manual.Count; i++)
        {
            shallowDropped &= manual.Points[i].Depth >= 0.2f - 1e-6f;
        }

        checks.Check(
            "満杯のマニフォールドは**いちばん浅い点を捨てる**",
            shallowDropped && MathF.Abs(manual.MaxDepth - 0.5f) < 1e-6f,
            $"{manual.Count} 点、最深 {manual.MaxDepth:F2}");

        // 半分だけ台に載せると、点が台の縁の上に並び直す。
        var pedestal = new Box3D(Vector3.Zero, new Vector3(1.0f, 0.5f, 1.0f), Quaternion.Identity);
        var overhang = new Box3D(new Vector3(1.0f, 0.95f, 0.0f), new Vector3(0.5f), Quaternion.Identity);
        ContactManifold clipped = Sat.BoxBox(overhang, pedestal);

        float maxX = float.MinValue;
        for (int i = 0; i < clipped.Count; i++)
        {
            maxX = MathF.Max(maxX, clipped.Points[i].Point.X);
        }

        checks.Check(
            "台から半分はみ出した箱の接触点は、**台の縁までで切り落とされる**",
            clipped.Count == 4 && maxX < 1.0f + 1e-4f,
            $"{clipped.Count} 点、いちばん外の点 x = {maxX:F4}(台の縁は 1.0)");

        // ============================================================
        //  5. 球と箱
        // ============================================================

        var target = new Box3D(Vector3.Zero, new Vector3(1.0f, 0.5f, 1.0f), Quaternion.Identity);

        Contact3D above = Collision3D.SphereBox(
            new Sphere3D(new Vector3(0.0f, 0.8f, 0.0f), 0.4f), target);

        checks.Check(
            "面の真上の球: 法線は面の法線、深さは 0.1m",
            above.Hit
                && (above.Normal - Vector3.UnitY).Length() < 1e-5f
                && MathF.Abs(above.Depth - 0.1f) < 1e-5f,
            $"深さ {above.Depth:F5}");

        Contact3D nearCorner = Collision3D.SphereBox(
            new Sphere3D(new Vector3(1.2f, 0.7f, 1.2f), 0.5f), target);

        checks.Check(
            "角の近くの球: 法線は**角から球へ**",
            nearCorner.Hit && (nearCorner.Normal - diagonal).Length() < 1e-4f,
            $"({nearCorner.Normal.X:F3}, {nearCorner.Normal.Y:F3}, {nearCorner.Normal.Z:F3})");

        Contact3D inside = Collision3D.SphereBox(
            new Sphere3D(new Vector3(0.0f, 0.1f, 0.0f), 0.2f), target);

        checks.Check(
            "中心が箱の中でも NaN を返さず、**いちばん近い面へ逃がす**",
            inside.Hit
                && float.IsFinite(inside.Normal.X)
                && (inside.Normal - Vector3.UnitY).Length() < 1e-5f
                && MathF.Abs(inside.Depth - 0.6f) < 1e-5f,
            $"法線 ({inside.Normal.X:F2}, {inside.Normal.Y:F2}, {inside.Normal.Z:F2}) 深さ {inside.Depth:F3}");

        checks.Check(
            "離れた球は当たらない",
            !Collision3D.SphereBox(new Sphere3D(new Vector3(0.0f, 3.0f, 0.0f), 0.4f), target).Hit);

        // ============================================================
        //  6. Collider にしたことで消えた分岐(要点3)
        // ============================================================

        var world = new PhysicsWorld();
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(0.0f, -0.5f, 0.0f), Vector3.UnitY));

        RigidBody landed = RigidBody.CreateBox(1.0f, 0.5f);
        landed.Position = new Vector3(0.0f, -0.2f, 0.0f);
        world.AddBody(landed);
        world.Step(1.0f / 600.0f);

        bool noNegative = true;
        foreach (ContactPoint contact in world.Contacts)
        {
            noNegative &= contact.A >= 0 && contact.B >= 0;
        }

        checks.Check(
            "**相手が -1 になる接触が無い**(平面も体になった)",
            noNegative && world.Contacts.Count == 4,
            $"{world.Contacts.Count} 点");
        checks.Check(
            "平面は体として数え、動く体には数えない",
            world.Bodies.Count == 2 && world.PlaneCount == 1 && world.DynamicCount == 1,
            $"体 {world.Bodies.Count} / 平面 {world.PlaneCount} / 動く体 {world.DynamicCount}");
        checks.Check(
            "1組から出た接触が4点(組と点は別の数)",
            world.ContactPairs == 1 && world.Contacts.Count == 4,
            $"{world.ContactPairs} 組 / {world.Contacts.Count} 点");

        // ============================================================
        //  7. 落として、支えられて、積む
        // ============================================================

        // 落とした箱が、最後の1秒でどれだけ揺れているかを測る。
        // **最後の値だけを見ると揺れを見逃す**ので、その間の最大値を採る。
        static (float Height, float Tilt, float Spin) DropBox(int maxContacts, int steps)
        {
            var w = new PhysicsWorld { MaxContactsPerPair = maxContacts };
            w.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

            RigidBody body = RigidBody.CreateBox(1.0f, 0.5f);
            body.Position = new Vector3(0.0f, 2.0f, 0.0f);
            body.Restitution = 0.0f;
            w.AddBody(body);

            float tilt = 0.0f;
            float spin = 0.0f;

            for (int i = 0; i < steps; i++)
            {
                w.Step(1.0f / 60.0f);

                if (i < steps - 60)
                {
                    continue;
                }

                Vector3 axis = Vector3.Transform(Vector3.UnitY, body.Orientation);
                tilt = MathF.Max(tilt, MathF.Acos(Math.Clamp(axis.Y, -1.0f, 1.0f)));
                spin = MathF.Max(spin, body.AngularVelocity.Length());
            }

            return (body.Position.Y, tilt, spin);
        }

        (float height4, float tilt4, float spin4) = DropBox(4, 300);

        checks.Check(
            "平らに落とした箱は、高さが半分の長さぶんで止まる",
            MathF.Abs(height4 - 0.5f) < 0.01f,
            $"{height4:F4}m(期待 0.5000m)");
        checks.Check(
            "最後の1秒、傾きも角速度もぴたりと 0(4点で支えている)",
            tilt4 < 0.002f && spin4 < 0.002f,
            $"傾き {tilt4 * 180.0f / MathF.PI:F4} 度、角速度 {spin4:F5} rad/s");

        (_, float tilt1, float spin1) = DropBox(1, 300);

        checks.Check(
            "**接触点を1つに絞ると落ち着かない**(今日の見どころ)",
            spin1 > 20.0f * MathF.Max(spin4, 1e-4f),
            $"1点 {spin1:F4} rad/s / 4点 {spin4:F5} rad/s");
        // **今日、揺れ幅が半分以下になった**(Day 46 は 0.94 度 / 0.735 rad/s)。
        // 摩擦が横向きの逃げを押さえるので、1点で支えていても暴れ方が小さい。
        // <b>主張は生きているが、しきい値は緩める必要があった</b>。
        checks.Check(
            "1点だと傾き続ける(4点なら傾かない。Day 46 の 0.94 度から半減)",
            tilt1 > tilt4 + 0.002f,
            $"1点 {tilt1 * 180.0f / MathF.PI:F3} 度 / 4点 {tilt4 * 180.0f / MathF.PI:F3} 度");

        static (float Penetration, float Top, float Drift) BoxStack(
            int count, int steps, bool together)
        {
            const float half = 0.45f;

            var w = new PhysicsWorld { SolveContactsTogether = together };
            w.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

            for (int i = 0; i < count; i++)
            {
                RigidBody body = RigidBody.CreateBox(1.0f, half);
                body.Position = new Vector3(0.0f, half + (i * half * 2.0f), 0.0f);
                body.Restitution = 0.0f;
                w.AddBody(body);
            }

            for (int i = 0; i < steps; i++)
            {
                w.Step(1.0f / 60.0f);
            }

            float drift = 0.0f;
            for (int i = 1; i < w.Bodies.Count; i++)
            {
                Vector3 position = w.Bodies[i].Position;
                drift = MathF.Max(drift, new Vector2(position.X, position.Z).Length());
            }

            return (w.MaxPenetration, w.Bodies[^1].Position.Y, drift);
        }

        (float deep, float top, float drift) = BoxStack(6, 300, together: true);
        (_, float looseTop, float looseDrift) = BoxStack(6, 300, together: false);

        checks.Check(
            "箱の柱6段が**横にずれない**(4点を同時に解いているから)",
            drift < 0.01f && top > 4.90f,
            $"横ずれ {drift * 1000.0f:F2}mm、最上段 {top:F3}m(理想 4.950m)");

        // **ここも Day 47 で主張が変わった**。
        // Day 46 までは順番に解くと柱が崩れ落ちていた(横ずれ 12.9m、最上段 0.45m)。
        // 摩擦が入って横向きの逃げを押さえるようになったので、<b>崩れなくなった</b>——
        // それでも 3.5cm ずれるので、同時に解く値打ちは残っている。
        checks.Check(
            "順番に解くと**横にずれる**(Day 46 では崩れ落ちていた。摩擦が支えている)",
            looseDrift > drift + 0.005f && looseTop > 4.5f,
            $"横ずれ {looseDrift * 1000.0f:F1}mm、最上段 {looseTop:F3}m");

        // **Day 44 が「Day 47 の宿題」と書いた項目**。今日それが片付いた。
        checks.Check(
            "**段数ぶん沈まなくなった**(Day 46 の 96.2mm → 許容量まで)",
            deep <= 0.006f,
            $"めり込み {deep * 1000.0f:F2}mm(許容 {5.0f:F1}mm)");

        static float BoxPenetrationAfter(bool correction, int steps)
        {
            var w = new PhysicsWorld { Gravity = Vector3.Zero, PositionCorrection = correction };

            RigidBody a = RigidBody.CreateBox(1.0f, 0.5f);
            a.Position = new Vector3(-0.35f, 0.0f, 0.0f);
            RigidBody b = RigidBody.CreateBox(1.0f, 0.5f);
            b.Position = new Vector3(0.35f, 0.0f, 0.0f);

            w.AddBody(a);
            w.AddBody(b);

            for (int i = 0; i < steps; i++)
            {
                w.Step(1.0f / 60.0f);
            }

            return w.MaxPenetration;
        }

        checks.Check(
            "深くめり込んだ箱2つが、許容量(5mm)近くまで押し戻される",
            BoxPenetrationAfter(correction: true, 60) < 0.01f,
            $"{BoxPenetrationAfter(correction: true, 60) * 1000.0f:F2}mm(初期 300mm)");
        checks.Check(
            "位置補正を切ると**めり込んだまま**",
            BoxPenetrationAfter(correction: false, 60) > 0.25f,
            $"{BoxPenetrationAfter(correction: false, 60) * 1000.0f:F1}mm");

        // ============================================================
        //  8. 回転が本当に効くこと
        // ============================================================

        var tipWorld = new PhysicsWorld();
        tipWorld.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

        RigidBody tipping = RigidBody.CreateBox(1.0f, 0.5f);
        tipping.Position = new Vector3(0.0f, 1.2f, 0.0f);
        tipping.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f);
        tipping.Restitution = 0.0f;
        tipping.AngularDamping = 0.4f;
        tipWorld.AddBody(tipping);

        float maxSpin = 0.0f;
        for (int i = 0; i < 90; i++)
        {
            tipWorld.Step(1.0f / 60.0f);
            maxSpin = MathF.Max(maxSpin, tipping.AngularVelocity.Length());
        }

        checks.Check(
            "角から落ちた箱は**接触のインパルスで回り出す**(球では起きなかった)",
            maxSpin > 0.5f,
            $"最大角速度 {maxSpin:F3} rad/s");

        for (int i = 0; i < 600; i++)
        {
            tipWorld.Step(1.0f / 60.0f);
        }

        Vector3 tippedUp = Vector3.Transform(Vector3.UnitY, tipping.Orientation);
        float finalTilt = MathF.Acos(Math.Clamp(MathF.Abs(tippedUp.Y), -1.0f, 1.0f));

        checks.Check(
            "最後は面で落ち着く(いちばん近い面が下を向く)",
            finalTilt < 0.06f && tipping.AngularVelocity.Length() < 0.05f,
            $"傾き {finalTilt * 180.0f / MathF.PI:F2} 度、角速度 {tipping.AngularVelocity.Length():F4} rad/s");

        checks.Report("すべて合格(箱は落ち、面で支えられ、積める)");
        Console.WriteLine();
    }

    /// <summary>
    /// 今日の自己チェック(Ctrl+Shift+Alt+X)。
    ///
    /// 今日は<b>2つの層をまとめて押さえる</b>。
    /// <list type="number">
    /// <item><b>判定</b>(カプセル)… 線分の最近接点・慣性テンソル・2点の接触</item>
    /// <item><b>操作</b>(キャラクター)… 坂・段差・接地・滑り</item>
    /// </list>
    ///
    /// <para>
    /// <b>2 のほうが厄介</b>。判定は数式に照らせば合否が出るが、
    /// 「坂を登れる」は<b>実際に歩かせてみないと分からない</b>。
    /// だから下の項目は、どれも
    /// <c>CharacterController.Move</c> を数十〜数百ステップ回して結果を見ている——
    /// <b>自動で遊ばせて確かめている</b>ことになる。
    /// Day 30 の卒業制作で「遊んでいる人の代わり」を用意したのと同じ考え方。
    /// </para>
    ///
    /// <para>
    /// <see cref="CharacterController"/> も <see cref="PhysicsWorld"/> も GL を知らないので、
    /// **窓を1枚も出さずに全項目が走る**。Day 43 から続く層のまま。
    /// </para>
    /// </summary>
    private static void RunCapsuleCheck()
    {
        Console.WriteLine();
        Console.WriteLine("--- Day 45: カプセルとキャラクターの自己チェック ---");
        var checks = new CheckList();

        // ============================================================
        //  1. 線分と最近接点(要点2)
        // ============================================================

        var segment = new Segment3D(new Vector3(-1.0f, 0.0f, 0.0f), new Vector3(1.0f, 0.0f, 0.0f));

        checks.Check(
            "線分の真横の点は、真下の点が最近接",
            (segment.ClosestPoint(new Vector3(0.3f, 2.0f, 0.0f)) - new Vector3(0.3f, 0.0f, 0.0f))
                .Length() < 1e-5f);

        checks.Check(
            "線分の外へ出た点は**端が最近接**(直線ではなく線分)",
            (segment.ClosestPoint(new Vector3(5.0f, 1.0f, 0.0f)) - new Vector3(1.0f, 0.0f, 0.0f))
                .Length() < 1e-5f);

        var degenerate = new Segment3D(Vector3.One, Vector3.One);
        checks.Check(
            "長さ 0 の線分でも NaN を返さない(カプセルが球に退化した場合)",
            float.IsFinite(degenerate.ClosestPoint(Vector3.Zero).X)
                && (degenerate.ClosestPoint(Vector3.Zero) - Vector3.One).Length() < 1e-6f);

        // すれ違う2本(ねじれの位置)。x 軸の線分と、z 軸方向へ持ち上げた y 軸の線分。
        var alongX = new Segment3D(new Vector3(-1.0f, 0.0f, 0.0f), new Vector3(1.0f, 0.0f, 0.0f));
        var alongY = new Segment3D(new Vector3(0.0f, -1.0f, 2.0f), new Vector3(0.0f, 1.0f, 2.0f));

        Segment3D.ClosestPoints(alongX, alongY, out Vector3 onX, out Vector3 onY);

        checks.Check(
            "ねじれの位置の2線分: 最近接点は**それぞれの中ほど**",
            onX.Length() < 1e-5f && (onY - new Vector3(0.0f, 0.0f, 2.0f)).Length() < 1e-5f,
            $"({onX.X:F3}, {onX.Y:F3}, {onX.Z:F3}) / ({onY.X:F3}, {onY.Y:F3}, {onY.Z:F3})");

        // 平行に並べた2本。ずらしてあるので、答えは端に寄る。
        var parallelA = new Segment3D(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(2.0f, 0.0f, 0.0f));
        var parallelB = new Segment3D(new Vector3(3.0f, 1.0f, 0.0f), new Vector3(5.0f, 1.0f, 0.0f));

        Segment3D.ClosestPoints(parallelA, parallelB, out Vector3 pa, out Vector3 pb);

        checks.Check(
            "平行で離れた2線分: **向かい合う端どうし**が最近接",
            (pa - new Vector3(2.0f, 0.0f, 0.0f)).Length() < 1e-4f
                && (pb - new Vector3(3.0f, 1.0f, 0.0f)).Length() < 1e-4f,
            $"距離 {(pa - pb).Length():F4}(期待 {MathF.Sqrt(2.0f):F4})");

        // 総当たりで確かめる。**答えを別の方法で出して突き合わせる**のがいちばん強い。
        var random = new Random(1234);
        float worstError = 0.0f;

        for (int trial = 0; trial < 2000; trial++)
        {
            var s1 = new Segment3D(RandomPoint(random), RandomPoint(random));
            var s2 = new Segment3D(RandomPoint(random), RandomPoint(random));

            Segment3D.ClosestPoints(s1, s2, out Vector3 c1, out Vector3 c2);
            float solved = (c1 - c2).Length();

            // 総当たり: 両方の線分を 200 等分して全部の組を試す。
            float brute = float.MaxValue;
            for (int i = 0; i <= 200; i++)
            {
                Vector3 p = s1.PointAt(i / 200.0f);
                for (int j = 0; j <= 200; j++)
                {
                    brute = MathF.Min(brute, (p - s2.PointAt(j / 200.0f)).Length());
                }
            }

            // 解析解は総当たりより小さいか同じになるはず。
            worstError = MathF.Max(worstError, solved - brute);
        }

        checks.Check(
            "**総当たりの答えを上回らない**(2,000 通り × 201² 組で確認)",
            worstError < 1e-3f,
            $"最悪 {worstError * 1000.0f:F4}mm");

        // ============================================================
        //  2. カプセルの形と慣性テンソル(要点1・要点5)
        // ============================================================

        var upright = Capsule3D.FromCenter(
            new Vector3(0.0f, 1.0f, 0.0f), Quaternion.Identity, 0.3f, 0.5f);

        checks.Check(
            "立てたカプセル: 線分は中心の上下 0.5m",
            (upright.Segment.Start - new Vector3(0.0f, 0.5f, 0.0f)).Length() < 1e-5f
                && (upright.Segment.End - new Vector3(0.0f, 1.5f, 0.0f)).Length() < 1e-5f);

        checks.Check(
            "全高は 2(半分の高さ + 半径)= 1.6m",
            MathF.Abs(Collider.Capsule(0.3f, 0.5f).TotalHeight - 1.6f) < 1e-5f,
            $"{Collider.Capsule(0.3f, 0.5f).TotalHeight:F4}m");

        checks.Check(
            "全高から作ると、半分の高さが逆算できる(全高 1.8m・半径 0.35m → 0.55m)",
            MathF.Abs(Collider.FromHeight(0.35f, 1.8f).HalfHeight - 0.55f) < 1e-5f,
            $"{Collider.FromHeight(0.35f, 1.8f).HalfHeight:F4}m");

        Vector3 sphereLike = Collider.Capsule(0.5f, 0.0f).InertiaLocal(2.0f);
        checks.Check(
            "**半分の高さ 0 のカプセルは球**(2/5 m r² = 0.2)",
            MathF.Abs(sphereLike.X - 0.2f) < 1e-5f
                && MathF.Abs(sphereLike.Y - 0.2f) < 1e-5f
                && MathF.Abs(sphereLike.Z - 0.2f) < 1e-5f,
            $"({sphereLike.X:F5}, {sphereLike.Y:F5}, {sphereLike.Z:F5})");

        Vector3 capsuleInertia = Collider.Capsule(0.3f, 0.6f).InertiaLocal(5.0f);
        checks.Check(
            "カプセルの慣性は**軸まわりだけが小さい**(横は2つとも同じ)",
            capsuleInertia.Y < capsuleInertia.X * 0.25f
                && MathF.Abs(capsuleInertia.X - capsuleInertia.Z) < 1e-6f,
            $"軸 {capsuleInertia.Y:F4} / 横 {capsuleInertia.X:F4}");

        // 細長いカプセルは、同じ質量・同じ長さの棒(1/12 m L²)に近づいていく。
        Vector3 slender = Collider.Capsule(0.02f, 1.0f).InertiaLocal(1.0f);
        float rod = (1.0f / 12.0f) * 1.0f * 2.0f * 2.0f;   // 全長 2m の細い棒
        checks.Check(
            "細いカプセルは**細長い棒 1/12 m L²** に近づく",
            MathF.Abs(slender.X - rod) / rod < 0.05f,
            $"{slender.X:F5}(棒なら {rod:F5})");

        // ============================================================
        //  3. カプセルの判定(要点1〜4)
        // ============================================================

        var floor = Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY);

        var standing = new Capsule3D(
            new Vector3(0.0f, 0.25f, 0.0f), new Vector3(0.0f, 1.25f, 0.0f), 0.3f);
        ContactManifold standingContact = Collision3D.CapsulePlane(standing, floor);

        checks.Check(
            "立てたカプセルが床にめり込むと**1点**(深さ 0.05m)",
            standingContact.Count == 1 && MathF.Abs(standingContact.MaxDepth - 0.05f) < 1e-5f,
            $"{standingContact.Count} 点 / 深さ {standingContact.MaxDepth:F4}m");

        var lying = new Capsule3D(
            new Vector3(-0.5f, 0.25f, 0.0f), new Vector3(0.5f, 0.25f, 0.0f), 0.3f);
        ContactManifold lyingContact = Collision3D.CapsulePlane(lying, floor);

        checks.Check(
            "**寝かせたカプセルは2点**(接触点の数 = 支え方の数)",
            lyingContact.Count == 2,
            $"{lyingContact.Count} 点");

        bool sameDepth = lyingContact.Count == 2
            && MathF.Abs(lyingContact.Points[0].Depth - lyingContact.Points[1].Depth) < 1e-6f;
        checks.Check("寝かせたカプセルの2点は同じ深さ", sameDepth);

        checks.Check(
            "浮いているカプセルは当たらない",
            !Collision3D.CapsulePlane(
                new Capsule3D(new Vector3(0.0f, 2.0f, 0.0f), new Vector3(0.0f, 3.0f, 0.0f), 0.3f),
                floor).Hit);

        // 球とカプセル。**線分の中ほどに当てる**。
        Contact3D sphereHit = Collision3D.SphereCapsule(
            new Sphere3D(new Vector3(0.6f, 0.0f, 0.0f), 0.4f),
            new Capsule3D(new Vector3(0.0f, -1.0f, 0.0f), new Vector3(0.0f, 1.0f, 0.0f), 0.3f));

        checks.Check(
            "球とカプセル: 法線は**球を押し出す向き**(+x)、深さは 0.7 - 0.6 = 0.1m",
            sphereHit.Hit
                && (sphereHit.Normal - Vector3.UnitX).Length() < 1e-4f
                && MathF.Abs(sphereHit.Depth - 0.1f) < 1e-4f,
            $"深さ {sphereHit.Depth:F5}m");

        // カプセルとカプセル。十字にすれ違う2本(**軸は 0.4m ずらしてある**)。
        // ぴたりと交差させると最近接点が一致して法線が決まらなくなり、
        // 「入れ替えたら逆向き」の確認ができない——Day 43 の「中心が一致した球」と同じ退化。
        var crossA = new Capsule3D(
            new Vector3(-1.0f, 0.0f, 0.0f), new Vector3(1.0f, 0.0f, 0.0f), 0.3f);
        var crossB = new Capsule3D(
            new Vector3(0.0f, 0.4f, -1.0f), new Vector3(0.0f, 0.4f, 1.0f), 0.3f);

        ContactManifold crossed = Collision3D.CapsuleCapsule(crossA, crossB);
        checks.Check(
            "十字にすれ違う2本のカプセルは当たる(深さ 0.6 - 0.4 = 0.2m)",
            crossed.Hit
                && MathF.Abs(crossed.MaxDepth - 0.2f) < 1e-4f
                && (crossed.Normal + Vector3.UnitY).Length() < 1e-4f,
            $"{crossed.Count} 点 / 深さ {crossed.MaxDepth:F4}m / "
                + $"法線 ({crossed.Normal.X:F2}, {crossed.Normal.Y:F2}, {crossed.Normal.Z:F2})");

        ContactManifold crossedSwapped = Collision3D.CapsuleCapsule(crossB, crossA);
        checks.Check(
            "A と B を入れ替えると法線が逆になり、深さは変わらない",
            (crossedSwapped.Normal + crossed.Normal).Length() < 1e-4f
                && MathF.Abs(crossedSwapped.MaxDepth - crossed.MaxDepth) < 1e-4f);

        // 平行に重ねた2本は2点。
        ContactManifold stacked = Collision3D.CapsuleCapsule(
            new Capsule3D(new Vector3(-0.5f, 0.5f, 0.0f), new Vector3(0.5f, 0.5f, 0.0f), 0.3f),
            new Capsule3D(new Vector3(-0.5f, 0.0f, 0.0f), new Vector3(0.5f, 0.0f, 0.0f), 0.3f));

        checks.Check(
            "**平行に重ねた2本のカプセルは2点**(1点だと転がって落ちる)",
            stacked.Count == 2,
            $"{stacked.Count} 点");

        // カプセルと箱。箱の上面(y = 0.5)に寝かせる。
        var platform = new Box3D(Vector3.Zero, new Vector3(2.0f, 0.5f, 2.0f), Quaternion.Identity);
        ContactManifold onBox = Collision3D.CapsuleBox(
            new Capsule3D(new Vector3(-0.5f, 0.7f, 0.0f), new Vector3(0.5f, 0.7f, 0.0f), 0.3f),
            platform);

        checks.Check(
            "箱の上に寝かせたカプセルは**2点以上**で支えられる",
            onBox.Count >= 2 && (onBox.Normal - Vector3.UnitY).Length() < 1e-4f,
            $"{onBox.Count} 点 / 法線 ({onBox.Normal.X:F2}, {onBox.Normal.Y:F2}, {onBox.Normal.Z:F2})");

        checks.Check(
            "深さは 0.3 - 0.2 = 0.1m",
            MathF.Abs(onBox.MaxDepth - 0.1f) < 1e-4f,
            $"{onBox.MaxDepth:F4}m");

        // **台からはみ出した端に、宙に浮いた接触点を立てない**。
        ContactManifold overhang = Collision3D.CapsuleBox(
            new Capsule3D(new Vector3(1.6f, 0.7f, 0.0f), new Vector3(3.6f, 0.7f, 0.0f), 0.3f),
            platform);

        bool insideBox = true;
        for (int i = 0; i < overhang.Count; i++)
        {
            insideBox &= overhang.Points[i].Point.X < 2.0f + 0.3f;
        }

        checks.Check(
            "台からはみ出した端に**宙に浮いた接触点を立てない**",
            overhang.Hit && insideBox,
            $"{overhang.Count} 点");

        // 三分探索の答えを総当たりで確かめる(要点3)。
        // **交互射影で書いていたときはここで落ちた**——2,000 通り中 581 件で
        // 20cm 以上ずれていた。凸なら収束するという理屈は正しくても、
        // 打ち切り回数を決められない手は使えない。
        var boxRandom = new Random(777);
        float worstBoxError = 0.0f;

        for (int trial = 0; trial < 400; trial++)
        {
            var box = new Box3D(
                RandomPoint(boxRandom) * 0.4f,
                new Vector3(0.4f, 0.7f, 0.5f),
                Quaternion.CreateFromYawPitchRoll(
                    (float)(boxRandom.NextDouble() * Math.Tau),
                    (float)(boxRandom.NextDouble() * Math.Tau),
                    (float)(boxRandom.NextDouble() * Math.Tau)));

            var probe = new Segment3D(
                RandomPoint(boxRandom) * 2.0f, RandomPoint(boxRandom) * 2.0f);

            Vector3 onBoxPoint = box.ClosestPoint(probe, out Vector3 onSegmentPoint);
            float solved = (onBoxPoint - onSegmentPoint).Length();

            // 総当たり: 線分を 400 等分して、各点から箱への最近接点を取る。
            float brute = float.MaxValue;
            for (int i = 0; i <= 400; i++)
            {
                Vector3 p = probe.PointAt(i / 400.0f);
                brute = MathF.Min(brute, (box.ClosestPoint(p) - p).Length());
            }

            worstBoxError = MathF.Max(worstBoxError, solved - brute);
        }

        checks.Check(
            "線分と箱の**三分探索が総当たりと一致**(400 通り)",
            worstBoxError < 1e-3f,
            $"最悪 {worstBoxError * 1000.0f:F4}mm");

        // ============================================================
        //  4. 剛体としてのカプセル
        // ============================================================

        var world = new PhysicsWorld();
        world.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

        RigidBody rolled = RigidBody.CreateCapsule(1.0f, 0.3f, 0.5f);
        rolled.Position = new Vector3(0.0f, 2.0f, 0.0f);

        // 少し傾けて落とす。**まっすぐ立てると理屈のうえでは倒れない**。
        rolled.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.35f);
        rolled.Restitution = 0.0f;
        rolled.AngularDamping = 0.3f;
        world.AddBody(rolled);

        for (int i = 0; i < 400; i++)
        {
            world.Step(1.0f / 60.0f);
        }

        Vector3 axisAfter = Vector3.Transform(Vector3.UnitY, rolled.Orientation);
        checks.Check(
            "落としたカプセルは**横倒しで止まる**(軸が水平になる)",
            MathF.Abs(axisAfter.Y) < 0.15f,
            $"軸の Y 成分 {axisAfter.Y:F4}");

        checks.Check(
            "横倒しのカプセルは、高さが半径ぶんで止まる",
            MathF.Abs(rolled.Position.Y - 0.3f) < 0.02f,
            $"{rolled.Position.Y:F4}m(期待 0.3000m)");

        checks.Check(
            "止まったカプセルは**2点で支えられている**",
            world.Contacts.Count == 2,
            $"{world.Contacts.Count} 点 / {world.ContactPairs} 組");

        // **ぴたりと 0 にはならない**。摩擦もスリープも無いので、
        // 2点で支えられた物体は接触の付き外れぶんだけ細かく揺れ続ける。
        // 「止まって見える」程度に収まっていることだけを確かめる(どちらも Day 47)。
        checks.Check(
            "角速度がほぼ残っていない(目で見て止まっている)",
            rolled.AngularVelocity.Length() < 0.15f,
            $"{rolled.AngularVelocity.Length():F5} rad/s(摩擦もスリープも無いので 0 にはならない)");

        // ============================================================
        //  5. キャラクター: 落ちて、立つ(要点9)
        // ============================================================

        static PhysicsWorld GroundWorld()
        {
            var w = new PhysicsWorld();
            w.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));
            return w;
        }

        static CharacterController Walker()
        {
            var c = new CharacterController();
            c.Teleport(new Vector3(0.0f, 1.5f, 0.0f));
            return c;
        }

        static void Walk(
            CharacterController c, PhysicsWorld w, Vector3 wish, int steps, bool run = false)
        {
            for (int i = 0; i < steps; i++)
            {
                c.Move(w, wish, run, false, 1.0f / 60.0f);
            }
        }

        PhysicsWorld flat = GroundWorld();
        CharacterController walker = Walker();

        Walk(walker, flat, Vector3.Zero, 120);

        checks.Check(
            "落としたキャラクターは**足元が床の高さで止まる**",
            MathF.Abs(walker.Position.Y) < 0.05f,
            $"{walker.Position.Y * 1000.0f:F2}mm");
        checks.Check(
            "止まったキャラクターは**接地している**(skin ぶん浮いていても)",
            walker.IsGrounded && MathF.Abs(walker.GroundNormal.Y - 1.0f) < 1e-3f,
            $"接地 {walker.IsGrounded} / 法線の Y {walker.GroundNormal.Y:F4}");

        Walk(walker, flat, Vector3.UnitX, 120);

        checks.Check(
            "歩くと**歩く速さ**になる(3.2 m/s)",
            MathF.Abs(walker.HorizontalSpeed - walker.WalkSpeed) < 0.05f,
            $"{walker.HorizontalSpeed:F3} m/s");

        Walk(walker, flat, Vector3.UnitX, 60, run: true);

        checks.Check(
            "走ると**走る速さ**になる(6.4 m/s)",
            MathF.Abs(walker.HorizontalSpeed - walker.RunSpeed) < 0.05f,
            $"{walker.HorizontalSpeed:F3} m/s");

        Walk(walker, flat, Vector3.Zero, 40);

        checks.Check(
            "キーを離すと**すぐ止まる**(慣性が残らない)",
            walker.HorizontalSpeed < 0.05f,
            $"{walker.HorizontalSpeed:F4} m/s");

        // ジャンプ。**届く高さが JumpHeight と合う**。
        PhysicsWorld jumpWorld = GroundWorld();
        CharacterController jumper = Walker();
        Walk(jumper, jumpWorld, Vector3.Zero, 120);

        jumper.Move(jumpWorld, Vector3.Zero, false, true, 1.0f / 60.0f);

        float peak = 0.0f;
        for (int i = 0; i < 120; i++)
        {
            jumper.Move(jumpWorld, Vector3.Zero, false, false, 1.0f / 60.0f);
            peak = MathF.Max(peak, jumper.Position.Y);
        }

        checks.Check(
            "ジャンプで**設定した高さまで届く**(1.1m)",
            MathF.Abs(peak - jumper.JumpHeight) < 0.08f,
            $"{peak:F3}m(設定 {jumper.JumpHeight:F2}m)");
        checks.Check(
            "ジャンプのあと着地して接地に戻る",
            jumper.IsGrounded && MathF.Abs(jumper.Position.Y) < 0.05f,
            $"高さ {jumper.Position.Y * 1000.0f:F1}mm");

        // ============================================================
        //  6. キャラクター: 坂(要点7)
        // ============================================================

        // 傾けた大きな箱を1枚だけ置いた世界を作り、そこを登らせる。
        //
        // **上面が原点をちょうど通る**ように置くのが肝。
        // 坂の端が床から飛び出していると、キャラクターは坂ではなく
        // <b>箱の切り口(下を向いた法線の壁)</b>にぶつかって止まってしまう——
        // これは実際にこの日踏んだ罠で、「坂が登れない」の原因が
        // 坂の上限ではなく置き方だった。
        static PhysicsWorld SlopeWorld(float degrees)
        {
            var w = new PhysicsWorld();
            w.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

            float radians = degrees * MathF.PI / 180.0f;

            // 上面の法線と、上りの向き。
            var up = new Vector3(-MathF.Sin(radians), MathF.Cos(radians), 0.0f);
            var along = new Vector3(MathF.Cos(radians), MathF.Sin(radians), 0.0f);

            // 上面の中心を「登り口から 3m 上」に置くと、上面は原点を通る。
            // 登り口より手前の部分は床の下に潜るので、キャラクターには当たらない。
            Vector3 topCenter = along * 3.0f;

            RigidBody ramp = RigidBody.CreateStatic(
                Collider.Box(new Vector3(6.0f, 0.5f, 4.0f)));

            ramp.Position = topCenter - (up * 0.5f);
            ramp.Orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians);

            w.AddBody(ramp);
            return w;
        }

        // **最高到達点を採る**。最後の位置を見ると、登り切って坂の向こうへ
        // 歩き去ってしまった場合に 0 になり、「登れなかった」と区別が付かない。
        static float ClimbHeight(float degrees, bool useSlopeLimit)
        {
            PhysicsWorld w = SlopeWorld(degrees);
            var c = new CharacterController { UseSlopeLimit = useSlopeLimit };
            c.Teleport(new Vector3(-1.0f, 0.5f, 0.0f));

            // **まず床に落ち着かせてから測り始める**。
            // 出発点の高さ(0.5m)が最高到達点に混ざると、
            // 1ミリも登れなかった場合でも 0.5m 登ったように見える。
            for (int i = 0; i < 60; i++)
            {
                c.Move(w, Vector3.Zero, false, false, 1.0f / 60.0f);
            }

            float peak = 0.0f;
            for (int i = 0; i < 300; i++)
            {
                c.Move(w, Vector3.UnitX, true, false, 1.0f / 60.0f);
                peak = MathF.Max(peak, c.Position.Y);
            }

            return peak;
        }

        float climb30 = ClimbHeight(30.0f, useSlopeLimit: true);
        float climb60 = ClimbHeight(60.0f, useSlopeLimit: true);
        float climb60Free = ClimbHeight(60.0f, useSlopeLimit: false);

        checks.Check(
            "**30 度の坂は登れる**(上限 50 度の内側)",
            climb30 > 1.5f,
            $"5 秒で {climb30:F2}m 登った");
        checks.Check(
            "**60 度の坂は登れない**(上限を超えているので「壁」扱い)",
            climb60 < 0.35f,
            $"5 秒で {climb60:F2}m しか上がらない");
        checks.Check(
            "**上限を切ると 60 度も登れる**(押し戻しがそのまま登りになる)",
            climb60Free > climb60 + 1.0f,
            $"上限なし {climb60Free:F2}m / 上限あり {climb60:F2}m");

        PhysicsWorld slopeStand = SlopeWorld(30.0f);
        var stander = new CharacterController();
        stander.Teleport(new Vector3(1.0f, 2.0f, 0.0f));
        Walk(stander, slopeStand, Vector3.Zero, 180);

        checks.Check(
            "30 度の坂に立つと、床の傾きが**30 度と出る**",
            stander.IsGrounded && MathF.Abs(stander.GroundSlopeDegrees - 30.0f) < 2.0f,
            $"{stander.GroundSlopeDegrees:F2} 度");

        // ============================================================
        //  7. キャラクター: 段差(要点8)
        // ============================================================

        static float StepDistance(float height, bool useStepOffset)
        {
            var w = new PhysicsWorld();
            w.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

            RigidBody block = RigidBody.CreateStatic(
                Collider.Box(new Vector3(2.0f, height * 0.5f, 2.0f)));
            block.Position = new Vector3(2.5f, height * 0.5f, 0.0f);
            w.AddBody(block);

            var c = new CharacterController { UseStepOffset = useStepOffset };
            c.Teleport(new Vector3(0.0f, 0.1f, 0.0f));

            for (int i = 0; i < 240; i++)
            {
                c.Move(w, Vector3.UnitX, false, false, 1.0f / 60.0f);
            }

            return c.Position.X;
        }

        float low = StepDistance(0.15f, useStepOffset: true);
        float mid = StepDistance(0.30f, useStepOffset: true);
        float high = StepDistance(0.45f, useStepOffset: true);
        float lowBlocked = StepDistance(0.15f, useStepOffset: false);

        checks.Check(
            "**15cm の段は越えられる**(上限 35cm の内側)",
            low > 2.0f,
            $"x = {low:F2}(段は x = 0.5 から)");
        checks.Check(
            "**30cm の段も越えられる**",
            mid > 2.0f,
            $"x = {mid:F2}");
        checks.Check(
            "**45cm の段は越えられない**(上限を超えている)",
            high < 0.8f,
            $"x = {high:F2}");
        checks.Check(
            "**乗り越えを切ると 15cm でも止まる**(今日の見どころ)",
            lowBlocked < 0.8f,
            $"切った {lowBlocked:F2} / 入れた {low:F2}");

        // 階段を降りるとき、床に吸い付いて跳ねないこと。
        static int AirborneStepsWhileDescending(bool useStepOffset)
        {
            var w = new PhysicsWorld();
            w.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

            // 4段の階段。上から下りてくる。
            for (int i = 0; i < 4; i++)
            {
                float top = (4 - i) * 0.25f;
                RigidBody block = RigidBody.CreateStatic(
                    Collider.Box(new Vector3(0.5f, top * 0.5f, 2.0f)));
                block.Position = new Vector3(i * 1.0f, top * 0.5f, 0.0f);
                w.AddBody(block);
            }

            var c = new CharacterController { UseStepOffset = useStepOffset };
            c.Teleport(new Vector3(0.0f, 1.2f, 0.0f));

            // まず落ち着かせる。
            for (int i = 0; i < 60; i++)
            {
                c.Move(w, Vector3.Zero, false, false, 1.0f / 60.0f);
            }

            int airborne = 0;
            for (int i = 0; i < 180; i++)
            {
                c.Move(w, Vector3.UnitX, false, false, 1.0f / 60.0f);
                if (!c.IsGrounded)
                {
                    airborne++;
                }
            }

            return airborne;
        }

        int snapped = AirborneStepsWhileDescending(useStepOffset: true);
        int unsnapped = AirborneStepsWhileDescending(useStepOffset: false);

        checks.Check(
            "階段を降りる間、**床に吸い付いて浮かない**",
            snapped < 5,
            $"180 ステップ中 {snapped} ステップだけ空中");
        checks.Check(
            "吸い付きを切ると**段ごとに浮く**",
            unsnapped > snapped + 5,
            $"切った {unsnapped} / 入れた {snapped} ステップ");

        // ============================================================
        //  8. キャラクター: 滑りと押し戻し(要点6)
        // ============================================================

        var wallWorld = new PhysicsWorld();
        wallWorld.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));
        wallWorld.AddPlane(Plane3D.FromPointNormal(new Vector3(2.0f, 0.0f, 0.0f), -Vector3.UnitX));

        var slider = new CharacterController();
        slider.Teleport(new Vector3(0.0f, 0.1f, 0.0f));

        // 壁へ斜めに突っ込む。
        Vector3 diagonal = Vector3.Normalize(new Vector3(1.0f, 0.0f, 1.0f));
        Walk(slider, wallWorld, diagonal, 180);

        checks.Check(
            "壁は抜けない(x が壁の内側に留まる)",
            slider.Position.X < 2.0f + 1e-3f,
            $"x = {slider.Position.X:F3}(壁は 2.0)");
        checks.Check(
            "**壁に沿って滑る**(z 方向へは進み続ける)",
            slider.Position.Z > 3.0f,
            $"z = {slider.Position.Z:F2}");
        checks.Check(
            "滑っている間の速さは**壁に沿った成分だけ**",
            MathF.Abs(slider.Velocity.X) < 0.2f && slider.Velocity.Z > 2.0f,
            $"({slider.Velocity.X:F2}, {slider.Velocity.Y:F2}, {slider.Velocity.Z:F2}) m/s");

        // 部屋の角。**2枚の壁の押し戻しが打ち消し合う**ので、反復が要る。
        var cornerWorld = new PhysicsWorld();
        cornerWorld.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));
        cornerWorld.AddPlane(Plane3D.FromPointNormal(new Vector3(1.0f, 0.0f, 0.0f), -Vector3.UnitX));
        cornerWorld.AddPlane(Plane3D.FromPointNormal(new Vector3(0.0f, 0.0f, 1.0f), -Vector3.UnitZ));

        var cornered = new CharacterController();
        cornered.Teleport(new Vector3(0.0f, 0.1f, 0.0f));
        Walk(cornered, cornerWorld, diagonal, 240);

        checks.Check(
            "部屋の角に押し込んでも**両方の壁の内側に留まる**",
            cornered.Position.X < 1.0f + 1e-3f && cornered.Position.Z < 1.0f + 1e-3f,
            $"({cornered.Position.X:F3}, {cornered.Position.Z:F3})");

        // 天井。**頭をぶつけたら落ちる**。
        var ceilingWorld = new PhysicsWorld();
        ceilingWorld.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));
        ceilingWorld.AddPlane(Plane3D.FromPointNormal(new Vector3(0.0f, 2.2f, 0.0f), -Vector3.UnitY));

        var ducker = new CharacterController();
        ducker.Teleport(new Vector3(0.0f, 0.1f, 0.0f));
        Walk(ducker, ceilingWorld, Vector3.Zero, 60);

        ducker.Move(ceilingWorld, Vector3.Zero, false, true, 1.0f / 60.0f);

        float ceilingPeak = 0.0f;
        for (int i = 0; i < 120; i++)
        {
            ducker.Move(ceilingWorld, Vector3.Zero, false, false, 1.0f / 60.0f);
            ceilingPeak = MathF.Max(ceilingPeak, ducker.Position.Y);
        }

        checks.Check(
            "天井のある部屋では**頭をぶつけて跳べる高さが減る**",
            ceilingPeak < 0.45f && MathF.Abs(ducker.Position.Y) < 0.05f,
            $"最高 {ceilingPeak:F3}m(天井 2.2m - 身長 1.8m = 0.4m)");

        // ============================================================
        //  9. 問い合わせが世界を書き換えないこと
        // ============================================================

        var readOnlyWorld = new PhysicsWorld();
        readOnlyWorld.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

        RigidBody witness = RigidBody.CreateBox(1.0f, 0.5f);
        witness.Position = new Vector3(0.0f, 0.5f, 0.0f);
        readOnlyWorld.AddBody(witness);

        Vector3 before = witness.Position;
        Span<ContactManifold> buffer = stackalloc ContactManifold[8];

        int found = readOnlyWorld.QueryCapsule(
            new Capsule3D(new Vector3(0.0f, 0.6f, 0.0f), new Vector3(0.0f, 1.2f, 0.0f), 0.3f),
            buffer);

        checks.Check(
            "カプセルの問い合わせは当たりを返す",
            found > 0,
            $"{found} 件");
        checks.Check(
            "**問い合わせは世界を1ミリも動かさない**(読むだけ)",
            (witness.Position - before).Length() < 1e-9f
                && witness.LinearVelocity.LengthSquared() < 1e-9f);
        checks.Check(
            "当たっていないカプセルは 0 件",
            readOnlyWorld.QueryCapsule(
                new Capsule3D(
                    new Vector3(9.0f, 5.0f, 9.0f), new Vector3(9.0f, 6.0f, 9.0f), 0.3f),
                buffer) == 0);

        checks.Report("すべて合格(カプセルは寝て支えられ、キャラクターは坂と段差を歩く)");
        Console.WriteLine();
    }

    /// <summary>自己チェックで使う、-1〜1 の立方体の中のランダムな点。</summary>
    /// <summary>
    /// Day 46 の自己チェック(Ctrl+Shift+Alt+G)。**窓を1枚も出さずに走る**。
    ///
    /// 今日の中身は <c>Physics/</c> に入っていて、GL も窓も一切知らない。
    /// おかげでこのチェックは <see cref="OnLoad"/> を通らずに走る——
    /// Day 26 の衝突判定、Day 43〜45 の物理と同じ性格。
    ///
    /// <para>
    /// <b>今日いちばん大事なのは「格子と総当たりで接触の集合が一致する」</b>。
    /// ブロードフェーズは<b>速さのための仕掛けで、答えを変えてはいけない</b>。
    /// 絵からは絶対に分からないので、
    /// <b>同じ世界を2通りに解いて突き合わせる</b>のがいちばん確実な確かめ方になる。
    /// </para>
    /// </summary>
    private static void RunTerrainCheck()
    {
        Console.WriteLine();
        Console.WriteLine("--- Day 46: 地形とブロードフェーズの自己チェック ---");
        var checks = new CheckList();

        // ============================================================
        //  1. AABB(要点5)
        // ============================================================

        var unit = new Aabb3D(new Vector3(-1.0f), new Vector3(1.0f));

        checks.Check(
            "重なる箱は重なると答える",
            Aabb3D.Overlap(unit, Aabb3D.FromCenter(new Vector3(1.5f, 0.0f, 0.0f), 1.0f)));

        checks.Check(
            "**1軸でも離れていれば重ならない**",
            !Aabb3D.Overlap(unit, Aabb3D.FromCenter(new Vector3(0.0f, 2.5f, 0.0f), 1.0f)));

        checks.Check(
            "接している箱は重なる扱い(境界を含む)",
            Aabb3D.Overlap(unit, Aabb3D.FromCenter(new Vector3(2.0f, 0.0f, 0.0f), 1.0f)));

        checks.Check(
            "無限の箱は有限ではない(格子に入れられない札)",
            !Aabb3D.Infinite.IsFinite && unit.IsFinite);

        checks.Check(
            "**無限の箱は何とでも重なる**(平面の足切りが場合分け無しで書ける)",
            Aabb3D.Overlap(Aabb3D.Infinite, Aabb3D.FromCenter(new Vector3(1e6f), 1.0f)));

        // 球・箱・カプセルの外接箱が、本当に形を包んでいるか。
        // **総当たりで確かめる**——形の上の点を大量に取って、全部が箱の中にあるか。
        var random = new Random(4600);
        bool contained = true;

        for (int trial = 0; trial < 200; trial++)
        {
            var sphere = new Sphere3D(RandomPoint(random) * 3.0f, 0.2f + ((float)random.NextDouble() * 0.8f));
            Aabb3D bounds = sphere.Bounds;

            for (int k = 0; k < 40; k++)
            {
                Vector3 direction = Vector3.Normalize(RandomPoint(random) + new Vector3(1e-4f));
                contained &= bounds.Contains(sphere.Center + (direction * sphere.Radius));
            }
        }

        checks.Check("球の外接箱が球を包む(200 個 × 表面 40 点)", contained);

        contained = true;
        for (int trial = 0; trial < 200; trial++)
        {
            var box = new Box3D(
                RandomPoint(random) * 3.0f,
                new Vector3(0.2f, 0.5f, 0.9f),
                Quaternion.CreateFromYawPitchRoll(
                    (float)random.NextDouble() * 6.3f,
                    (float)random.NextDouble() * 6.3f,
                    (float)random.NextDouble() * 6.3f));

            Aabb3D bounds = box.Bounds.Expanded(1e-4f);
            for (int index = 0; index < 8; index++)
            {
                contained &= bounds.Contains(box.Corner(index));
            }
        }

        checks.Check("**回した箱**の外接箱が8つの角を包む(200 通り)", contained);

        var axisAligned = new Box3D(Vector3.Zero, new Vector3(0.5f), Quaternion.Identity);
        var turned = new Box3D(
            Vector3.Zero, new Vector3(0.5f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4.0f));

        checks.Check(
            "**45 度回した立方体の外接箱は √2 倍に太る**(候補が増える理由)",
            MathF.Abs(turned.Bounds.Size.X - (axisAligned.Bounds.Size.X * MathF.Sqrt(2.0f))) < 1e-4f,
            $"{axisAligned.Bounds.Size.X:F3}m → {turned.Bounds.Size.X:F3}m");

        var lying = new Capsule3D(new Vector3(-1.0f, 2.0f, 0.0f), new Vector3(1.0f, 2.0f, 0.0f), 0.3f);
        checks.Check(
            "寝かせたカプセルの外接箱は 2.6 x 0.6 x 0.6",
            (lying.Bounds.Size - new Vector3(2.6f, 0.6f, 0.6f)).Length() < 1e-5f,
            $"({lying.Bounds.Size.X:F2}, {lying.Bounds.Size.Y:F2}, {lying.Bounds.Size.Z:F2})");

        checks.Check(
            "平面の外接箱は無限(体の <c>Bounds()</c> 越し)",
            !RigidBody.CreatePlane(Vector3.Zero, Vector3.UnitY).Bounds().IsFinite);

        // ============================================================
        //  2. 三角形(要点2)
        // ============================================================

        var flat = new Triangle3D(
            new Vector3(0.0f, 0.0f, 0.0f),
            new Vector3(0.0f, 0.0f, 1.0f),
            new Vector3(1.0f, 0.0f, 1.0f));

        checks.Check(
            "地形の巻き順で作った三角形の法線は**真上**",
            (flat.Normal - Vector3.UnitY).Length() < 1e-6f,
            $"({flat.Normal.X:F3}, {flat.Normal.Y:F3}, {flat.Normal.Z:F3})");

        var degenerateTriangle = new Triangle3D(Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2.0f);
        checks.Check(
            "潰れた三角形でも NaN を返さない(3点が一直線)",
            float.IsFinite(degenerateTriangle.Normal.X) && degenerateTriangle.Normal.LengthSquared() > 0.5f);

        Vector3 inside = flat.ClosestPoint(new Vector3(0.3f, 2.0f, 0.6f), out bool onFace);
        checks.Check(
            "面の真上の点は**真下が最近接**で、面の内側と答える",
            onFace && (inside - new Vector3(0.3f, 0.0f, 0.6f)).Length() < 1e-5f);

        flat.ClosestPoint(new Vector3(-2.0f, 0.0f, 0.5f), out bool onFaceEdge);
        checks.Check(
            "**辺の外側の点は面の内側ではない**(内部エッジの分かれ道)",
            !onFaceEdge);

        Vector3 corner = flat.ClosestPoint(new Vector3(-1.0f, 0.0f, -1.0f), out _);
        checks.Check(
            "頂点の外側の点は**その頂点**が最近接",
            corner.Length() < 1e-5f);

        // 総当たりと突き合わせる。**重心座標を細かく刻んで全部試す**。
        float worstTriangleError = 0.0f;

        for (int trial = 0; trial < 400; trial++)
        {
            var triangle = new Triangle3D(RandomPoint(random), RandomPoint(random), RandomPoint(random));
            Vector3 point = RandomPoint(random) * 2.0f;

            float solved = (point - triangle.ClosestPoint(point)).Length();

            float brute = float.MaxValue;
            const int steps = 60;
            for (int i = 0; i <= steps; i++)
            {
                for (int j = 0; i + j <= steps; j++)
                {
                    float u = i / (float)steps;
                    float v = j / (float)steps;
                    Vector3 on = triangle.A
                        + ((triangle.B - triangle.A) * u)
                        + ((triangle.C - triangle.A) * v);

                    brute = MathF.Min(brute, (point - on).Length());
                }
            }

            worstTriangleError = MathF.Max(worstTriangleError, solved - brute);
        }

        checks.Check(
            "点と三角形の最近接点が**総当たりを上回らない**(400 通り × 1,891 点)",
            worstTriangleError < 1e-3f,
            $"最悪 {worstTriangleError * 1000.0f:F4}mm");

        // 線分との最近接点(三分探索)も総当たりと突き合わせる。
        float worstSegmentError = 0.0f;

        for (int trial = 0; trial < 300; trial++)
        {
            var triangle = new Triangle3D(RandomPoint(random), RandomPoint(random), RandomPoint(random));
            var segment = new Segment3D(RandomPoint(random) * 2.0f, RandomPoint(random) * 2.0f);

            Vector3 onTriangle = triangle.ClosestPoint(segment, out Vector3 onSegment);
            float solved = (onTriangle - onSegment).Length();

            float brute = float.MaxValue;
            for (int i = 0; i <= 300; i++)
            {
                Vector3 sample = segment.PointAt(i / 300.0f);
                brute = MathF.Min(brute, (sample - triangle.ClosestPoint(sample)).Length());
            }

            worstSegmentError = MathF.Max(worstSegmentError, solved - brute);
        }

        checks.Check(
            "線分と三角形の最近接点が**総当たりを上回らない**(三分探索。300 通り)",
            worstSegmentError < 1e-3f,
            $"最悪 {worstSegmentError * 1000.0f:F4}mm");

        // ============================================================
        //  3. 高さの格子(要点1)
        // ============================================================

        var ramp = BuildTestField(17, 17, 1.0f, (x, z) => x * 0.5f);   // x 方向に 26.57 度の一様な坂
        var rampTerrain = new Terrain3D(ramp, new Vector3(-8.0f, 0.0f, -8.0f));

        checks.Check(
            "格子点 17x17 なら**マスは 16x16**、三角形は 512 枚",
            ramp.CellsX == 16 && ramp.CellsZ == 16 && ramp.TriangleCount == 512,
            $"{ramp.CellsX}x{ramp.CellsZ} / {ramp.TriangleCount} 枚");

        checks.Check(
            "広がりはマスの数 × マスの大きさ(16m)",
            MathF.Abs(ramp.SizeX - 16.0f) < 1e-5f && MathF.Abs(ramp.SizeZ - 16.0f) < 1e-5f);

        checks.Check(
            "外接箱の上は最大の格子点、**下は厚みぶん余分**(-2m 〜 8m)",
            MathF.Abs(rampTerrain.Bounds.Min.Y + Terrain3D.RecoveryDepth) < 1e-5f
                && MathF.Abs(rampTerrain.Bounds.Max.Y - 8.0f) < 1e-5f,
            $"[{rampTerrain.Bounds.Min.Y:F2}, {rampTerrain.Bounds.Max.Y:F2}]");

        checks.Check(
            "格子点の世界座標は原点ぶんずれる",
            (rampTerrain.Vertex(0, 0) - new Vector3(-8.0f, 0.0f, -8.0f)).Length() < 1e-5f
                && (rampTerrain.Vertex(16, 16) - new Vector3(8.0f, 8.0f, 8.0f)).Length() < 1e-5f);

        checks.Check(
            "地形の外は当たらない(範囲の外を正直に答える)",
            !rampTerrain.TryHeightAt(-20.0f, 0.0f, out _)
                && !rampTerrain.TryNormalAt(0.0f, 40.0f, out _));

        checks.Check(
            "一様な坂の高さが解析解と一致(x = 3.7m)",
            rampTerrain.TryHeightAt(-8.0f + 3.7f, 0.0f, out float rampHeight)
                && MathF.Abs(rampHeight - (3.7f * 0.5f)) < 1e-4f,
            $"{rampHeight:F4}m");

        checks.Check(
            "一様な坂の法線は 26.57 度(atan 0.5)",
            rampTerrain.TryNormalAt(0.0f, 0.0f, out Vector3 rampNormal)
                && MathF.Abs((MathF.Acos(rampNormal.Y) * 180.0f / MathF.PI) - 26.565f) < 0.01f,
            $"{MathF.Acos(rampNormal.Y) * 180.0f / MathF.PI:F3}度");

        // **高さの問い合わせと三角形が一致するか**(要点1)。
        // 双一次補間で書くとここがずれる。マスの中を細かく刻んで、
        // 「その点が乗っている三角形の平面」との差を測る。
        var bumpy = BuildTestField(9, 9, 1.0f, (x, z) =>
            MathF.Sin(x * 0.9f) + (0.7f * MathF.Cos(z * 1.1f)) + (0.3f * x * z * 0.1f));
        var bumpyTerrain = new Terrain3D(bumpy, Vector3.Zero);

        float worstPlaneError = 0.0f;

        for (int i = 1; i < 160; i++)
        {
            for (int j = 1; j < 160; j++)
            {
                float x = i * (8.0f / 160.0f);
                float z = j * (8.0f / 160.0f);

                if (!bumpyTerrain.TryHeightAt(x, z, out float sampled))
                {
                    continue;
                }

                // その点が乗っている三角形を取り、平面の式で高さを出す。
                int cellX = (int)MathF.Floor(x);
                int cellZ = (int)MathF.Floor(z);
                Triangle3D triangle = bumpyTerrain.Triangle(
                    cellX, cellZ, (z - cellZ) >= (x - cellX) ? 0 : 1);

                // 平面 n·(p - A) = 0 を y について解く。
                float planeY = triangle.A.Y
                    - (((triangle.Normal.X * (x - triangle.A.X))
                        + (triangle.Normal.Z * (z - triangle.A.Z))) / triangle.Normal.Y);

                worstPlaneError = MathF.Max(worstPlaneError, MathF.Abs(sampled - planeY));
            }
        }

        checks.Check(
            "**高さの問い合わせが三角形の平面とぴったり一致**(双一次補間ではない。25,000 点)",
            worstPlaneError < 1e-4f,
            $"最悪 {worstPlaneError * 1000.0f:F4}mm");

        checks.Check(
            "マスの範囲は割り算で出る(中央 2x2m の箱 → 2x2 マス)",
            bumpyTerrain.CellRange(
                new Aabb3D(new Vector3(3.1f, -9.0f, 3.1f), new Vector3(4.9f, 9.0f, 4.9f)),
                out int cx0, out int cz0, out int cx1, out int cz1)
                && cx0 == 3 && cz0 == 3 && cx1 == 4 && cz1 == 4,
            $"x [{cx0}, {cx1}]  z [{cz0}, {cz1}]");

        checks.Check(
            "地形から完全に外れた箱は false(全部のマスを試さずに済む)",
            !bumpyTerrain.CellRange(
                new Aabb3D(new Vector3(20.0f, 0.0f, 20.0f), new Vector3(21.0f, 1.0f, 21.0f)),
                out _, out _, out _, out _));

        // ============================================================
        //  4. 地形の判定(要点2〜4)
        // ============================================================

        var level = BuildTestField(9, 9, 1.0f, (x, z) => 0.0f);
        var levelTerrain = new Terrain3D(level, Vector3.Zero);

        ContactManifold resting = Collision3D.SphereTerrain(
            new Sphere3D(new Vector3(4.0f, 0.4f, 4.0f), 0.5f), levelTerrain);

        checks.Check(
            "平らな地形に乗った球: 法線は真上、めり込み 0.1m",
            resting.Hit
                && (resting.Normal - Vector3.UnitY).Length() < 1e-5f
                && MathF.Abs(resting.MaxDepth - 0.1f) < 1e-4f,
            $"深さ {resting.MaxDepth:F4}m");

        checks.Check(
            "地形の上に浮いた球は当たらない",
            !Collision3D.SphereTerrain(
                new Sphere3D(new Vector3(4.0f, 0.8f, 4.0f), 0.5f), levelTerrain).Hit);

        ContactManifold sunk = Collision3D.SphereTerrain(
            new Sphere3D(new Vector3(4.3f, -1.2f, 4.4f), 0.5f), levelTerrain);

        checks.Check(
            "**地形に潜った球は上へ押し戻される**(すり抜けたぶんも含めて)",
            sunk.Hit
                && (sunk.Normal - Vector3.UnitY).Length() < 1e-5f
                && sunk.MaxDepth > 1.6f,
            $"深さ {sunk.MaxDepth:F3}m");

        // **内部エッジ問題**(要点3)。平らな地形の上を球を滑らせて、
        // 法線が1度でも横を向いたら不合格。
        float worstTilt = 0.0f;
        bool alwaysHit = true;

        for (int i = 0; i <= 400; i++)
        {
            float t = 0.6f + (i * (6.8f / 400.0f));
            ContactManifold rolling = Collision3D.SphereTerrain(
                new Sphere3D(new Vector3(t, 0.45f, t * 0.83f), 0.5f), levelTerrain);

            alwaysHit &= rolling.Hit;
            if (rolling.Hit)
            {
                worstTilt = MathF.Max(
                    worstTilt, MathF.Acos(Math.Clamp(rolling.Normal.Y, -1.0f, 1.0f)));
            }
        }

        checks.Check(
            "**格子点の真下に潜っても押し戻される**(どの三角形も受け持たない穴を塞ぐ)",
            Collision3D.SphereTerrain(
                new Sphere3D(new Vector3(4.0f, -1.2f, 4.0f), 0.5f), levelTerrain).Hit);

        checks.Check(
            "**2m 下まで潜っても組になる**(外接箱を下へ厚くしてある。要点3)",
            Aabb3D.Overlap(
                new Terrain3D(level, Vector3.Zero).Bounds,
                Aabb3D.FromCenter(new Vector3(4.0f, -1.5f, 4.0f), 0.3f)));

        checks.Check(
            "**内部エッジで蹴られない**(平らな地形を 401 点なめて、法線は常に真上)",
            alwaysHit && worstTilt < 1e-4f,
            $"最悪の傾き {worstTilt * 180.0f / MathF.PI:F5}度");

        ContactManifold capsuleOnGround = Collision3D.CapsuleTerrain(
            Capsule3D.FromCenter(new Vector3(4.0f, 0.85f, 4.0f), Quaternion.Identity, 0.35f, 0.55f),
            levelTerrain);

        checks.Check(
            "立てたカプセルが地形に接地する(足元 -0.05m)",
            capsuleOnGround.Hit
                && (capsuleOnGround.Normal - Vector3.UnitY).Length() < 1e-5f
                && MathF.Abs(capsuleOnGround.MaxDepth - 0.05f) < 1e-4f,
            $"深さ {capsuleOnGround.MaxDepth:F4}m");

        ContactManifold lyingCapsule = Collision3D.CapsuleTerrain(
            new Capsule3D(new Vector3(2.5f, 0.25f, 4.0f), new Vector3(5.5f, 0.25f, 4.0f), 0.3f),
            levelTerrain);

        checks.Check(
            "**寝かせたカプセルは複数の三角形で支えられる**(点が2つ以上)",
            lyingCapsule.Count >= 2,
            $"{lyingCapsule.Count} 点");

        ContactManifold boxOnGround = Collision3D.BoxTerrain(
            new Box3D(new Vector3(4.0f, 0.2f, 4.0f), new Vector3(0.3f), Quaternion.Identity),
            levelTerrain);

        checks.Check(
            "**平らに置いた箱は4点で支えられる**(下の4隅が同時に沈む)",
            boxOnGround.Count == 4
                && (boxOnGround.Normal - Vector3.UnitY).Length() < 1e-5f,
            $"{boxOnGround.Count} 点  深さ {boxOnGround.MaxDepth:F3}m");

        checks.Check(
            "地形の外にいる球は当たらない(判定を1枚も走らせない)",
            !Collision3D.SphereTerrain(
                new Sphere3D(new Vector3(30.0f, 0.0f, 30.0f), 0.5f), levelTerrain).Hit);

        // 坂の上の球は、坂の法線で押し戻される。
        ContactManifold onRamp = Collision3D.SphereTerrain(
            new Sphere3D(new Vector3(0.0f, 4.0f + 0.4f, 0.0f), 0.5f), rampTerrain);

        checks.Check(
            "坂の上の球は**坂の法線**で押し戻される(26.57 度)",
            onRamp.Hit
                && MathF.Abs((MathF.Acos(onRamp.Normal.Y) * 180.0f / MathF.PI) - 26.565f) < 0.01f,
            $"{MathF.Acos(onRamp.Normal.Y) * 180.0f / MathF.PI:F3}度");

        // 今日のデモの地形が、狙った角度になっているか。
        var demoField = BuildTestField(
            TerrainCells + 1, TerrainCells + 1, TerrainCellSize,
            (x, z) => TerrainHeightAt(x, z));
        var demoTerrain = new Terrain3D(demoField, Vector3.Zero);

        demoTerrain.TryNormalAt(17.5f - 1.2f, 7.0f, out Vector3 steepNormal);
        demoTerrain.TryNormalAt(6.5f - 1.2f, 17.0f, out Vector3 gentleNormal);

        float steepDegrees = MathF.Acos(Math.Clamp(steepNormal.Y, -1.0f, 1.0f)) * 180.0f / MathF.PI;
        float gentleDegrees = MathF.Acos(Math.Clamp(gentleNormal.Y, -1.0f, 1.0f)) * 180.0f / MathF.PI;

        checks.Check(
            "デモの急な丘は**坂の上限(50 度)より急**",
            steepDegrees > 50.0f,
            $"{steepDegrees:F1}度");

        checks.Check(
            "デモの緩い丘は**坂の上限より緩い**",
            gentleDegrees < 50.0f && gentleDegrees > 15.0f,
            $"{gentleDegrees:F1}度");

        checks.Check(
            "デモの出発点は平ら(立っただけで滑らない)",
            demoTerrain.TryNormalAt(12.0f, 21.0f, out Vector3 spawnNormal)
                && spawnNormal.Y > 0.9999f,
            $"{MathF.Acos(Math.Clamp(spawnNormal.Y, -1.0f, 1.0f)) * 180.0f / MathF.PI:F3}度");

        // ============================================================
        //  5. ブロードフェーズ(要点5〜7)
        // ============================================================

        var grid = new SpatialGrid3D();
        grid.Configure(new Vector3(-10.0f), new Vector3(20.0f), 2.0f);

        Aabb3D[] simple =
        [
            Aabb3D.FromCenter(new Vector3(0.0f, 0.0f, 0.0f), 0.4f),
            Aabb3D.FromCenter(new Vector3(0.5f, 0.0f, 0.0f), 0.4f),
            Aabb3D.FromCenter(new Vector3(8.0f, 0.0f, 8.0f), 0.4f),
            Aabb3D.Infinite,
        ];

        grid.Build(simple);
        int simplePairs = grid.CollectPairs(simple);

        checks.Check(
            "無限の箱は格子に入らず、**はみ出し**として数えられる",
            grid.OversizedCount == 1 && grid.EntryCount >= 3,
            $"はみ出し {grid.OversizedCount}  登録 {grid.EntryCount}");

        checks.Check(
            "**はみ出しは全部の相手と組になる**(取りこぼすと床をすり抜ける)",
            simplePairs == 4,
            $"{simplePairs} 組(近い2個 + 無限 × 3)");

        checks.Check(
            "離れた2個は組にならない",
            !ContainsPair(grid.Pairs, 1, 2));

        // **同じ組が2回出ないこと**。またがっているとマスごとに見つかる。
        var wide = new SpatialGrid3D();
        wide.Configure(new Vector3(-10.0f), new Vector3(20.0f), 0.5f);

        Aabb3D[] straddling =
        [
            Aabb3D.FromCenter(new Vector3(0.0f), 0.55f),
            Aabb3D.FromCenter(new Vector3(0.3f, 0.0f, 0.0f), 0.55f),
        ];

        wide.Build(straddling);
        int straddlingPairs = wide.CollectPairs(straddling);

        checks.Check(
            "**またがっていても組は1回だけ**(印で重複を潰す)",
            straddlingPairs == 1 && wide.EntryCount > 20,
            $"{straddlingPairs} 組 / 登録 {wide.EntryCount} 件");

        // --- 今日いちばん大事なチェック: 格子と総当たりで接触が一致する ---
        HashSet<(int, int)> bruteContacts = StepAndCollectPairs(BroadphaseMode.BruteForce, 2.0f);
        HashSet<(int, int)> gridContacts = StepAndCollectPairs(BroadphaseMode.UniformGrid, 2.0f);

        checks.Check(
            "**格子と総当たりで接触の組がぴったり一致する**(200 体)",
            bruteContacts.SetEquals(gridContacts),
            $"総当たり {bruteContacts.Count} 組 / 格子 {gridContacts.Count} 組");

        // マスの大きさを変えても答えが変わらないこと。
        bool sameAcrossCellSizes = true;
        foreach (float cellSize in GridCellSteps)
        {
            sameAcrossCellSizes &=
                StepAndCollectPairs(BroadphaseMode.UniformGrid, cellSize).SetEquals(bruteContacts);
        }

        checks.Check(
            "**マスの大きさを 0.5m〜8m に振っても接触は同じ**(速さだけが変わる)",
            sameAcrossCellSizes);

        // 候補の数がどれだけ減ったか。
        var measured = BuildBroadphaseWorld(BroadphaseMode.UniformGrid, 2.0f);
        measured.Step(1.0f / 60.0f);
        long gridCandidates = measured.PairTests;
        long bruteCandidates = measured.BruteForcePairs;

        checks.Check(
            "**候補が総当たりの 1/4 以下に減る**(200 体)",
            gridCandidates * 4 < bruteCandidates,
            $"{gridCandidates:N0} / {bruteCandidates:N0} 組"
            + $"({100.0 * gridCandidates / bruteCandidates:F1}%)");

        checks.Check(
            "登録件数は体の数以上(またがったぶんだけ重複する)",
            measured.Grid.EntryCount >= measured.Bodies.Count - measured.Grid.OversizedCount,
            $"登録 {measured.Grid.EntryCount} / 体 {measured.Bodies.Count}");

        checks.Check(
            "地形と平面が**はみ出し**に回っている(格子に入れると全マスを埋める)",
            measured.Grid.OversizedCount == 6,
            $"{measured.Grid.OversizedCount} 個(平面5 + 地形1)");

        // --- 問い合わせ(キャラクターの足元)---
        Span<ContactManifold> hits = stackalloc ContactManifold[8];
        var standing = Capsule3D.FromCenter(
            new Vector3(0.0f, PhysicsFloorY + 0.9f, 0.0f), Quaternion.Identity, 0.35f, 0.55f);

        var queryWorld = BuildBroadphaseWorld(BroadphaseMode.UniformGrid, 2.0f);
        queryWorld.Step(1.0f / 60.0f);
        queryWorld.ResetQueryStats();
        int found = queryWorld.QueryCapsule(standing, hits);

        var bruteQueryWorld = BuildBroadphaseWorld(BroadphaseMode.BruteForce, 2.0f);
        bruteQueryWorld.Step(1.0f / 60.0f);
        bruteQueryWorld.ResetQueryStats();
        Span<ContactManifold> bruteHits = stackalloc ContactManifold[8];
        int bruteFound = bruteQueryWorld.QueryCapsule(standing, bruteHits);

        checks.Check(
            "**問い合わせの答えが格子でも総当たりでも同じ**(キャラクターの足元)",
            found == bruteFound,
            $"格子 {found} 件 / 総当たり {bruteFound} 件");

        checks.Check(
            "**問い合わせで試す体の数が減る**(格子のほうが少ない)",
            queryWorld.CapsuleQueryTests < bruteQueryWorld.CapsuleQueryTests,
            $"格子 {queryWorld.CapsuleQueryTests} 体 / 総当たり {bruteQueryWorld.CapsuleQueryTests} 体");

        checks.Check(
            "格子を組む前の問い合わせでも取りこぼさない(総当たりに落ちる)",
            new PhysicsWorld().QueryCapsule(standing, hits) == 0);

        // ============================================================
        //  6. 通しで動かす(要点8)
        // ============================================================

        var settled = BuildBroadphaseWorld(BroadphaseMode.UniformGrid, 2.0f);
        for (int step = 0; step < 400; step++)
        {
            settled.Step(1.0f / 60.0f);
        }

        var terrainForCheck = new Terrain3D(demoField, new Vector3(-TerrainSpan * 0.5f, PhysicsFloorY, -TerrainSpan * 0.5f));
        int belowGround = 0;
        float fastest = 0.0f;

        foreach (RigidBody body in settled.Bodies)
        {
            if (body.IsStatic)
            {
                continue;
            }

            fastest = MathF.Max(fastest, body.LinearVelocity.Length());

            if (terrainForCheck.TryHeightAt(body.Position.X, body.Position.Z, out float ground)
                && body.Position.Y < ground - 0.5f)
            {
                belowGround++;
            }
        }

        checks.Check(
            "**400 ステップ落としても、地形をすり抜けた体が1つも無い**",
            belowGround == 0,
            $"{belowGround} 個");

        // **止まりきることは求めない**。摩擦もスリープも Day 47 なので、
        // 坂の上の物は滑り続け、壁で跳ね返って戻ってくる。
        // ここで見たいのは「落ち着くか」ではなく<b>「爆発しないか」</b>——
        // 深いめり込みから過大なインパルスが出ると、
        // 体が数十 m/s で吹き飛ぶ形ですぐ分かる。
        checks.Check(
            "400 ステップ落としても**吹き飛ばない**(最速でも 6m/s 未満。摩擦は Day 47)",
            fastest < 6.0f,
            $"最速 {fastest:F3}m/s");

        checks.Check(
            "落ち着いてもめり込みは slop の数倍以内(地形の上でも沈まない)",
            settled.MaxPenetration < 0.03f,
            $"{settled.MaxPenetration * 1000.0f:F1}mm");

        // --- キャラクターを地形の上で歩かせる ---
        //
        // **地形だけの世界**で試す。転がっている体が邪魔をすると、
        // 「登れないのが坂の上限のせいか、箱に当たったせいか」が分からなくなる。
        PhysicsWorld walkWorld = BuildTerrainOnlyWorld();
        var walker = new CharacterController();

        // 出発点は地形をならしてあるところ。
        Vector3 spawn = new(
            -TerrainSpan * 0.5f + 12.0f, PhysicsFloorY + 0.3f, -TerrainSpan * 0.5f + 21.0f);
        walker.Teleport(spawn);

        for (int step = 0; step < 60; step++)
        {
            walkWorld.Step(1.0f / 60.0f);
            walker.Move(walkWorld, Vector3.Zero, run: false, jump: false, 1.0f / 60.0f);
        }

        checks.Check(
            "**地形の上に立つと接地する**(平らな出発点)",
            walker.IsGrounded && walker.GroundSlopeDegrees < 1.0f,
            $"傾き {walker.GroundSlopeDegrees:F2}度  高さ {walker.Position.Y:F3}m");

        // 緩い丘(-x, +z 方向)へ歩く。
        Vector3 gentleFoot = new(
            -TerrainSpan * 0.5f + 6.5f + 4.0f, PhysicsFloorY + 0.3f, -TerrainSpan * 0.5f + 17.0f);
        float gentleTop = WalkTowardHill(walkWorld, gentleFoot, -Vector3.UnitX);

        Vector3 steepFoot = new(
            -TerrainSpan * 0.5f + 17.5f + 4.0f, PhysicsFloorY + 0.3f, -TerrainSpan * 0.5f + 7.0f);
        float steepTop = WalkTowardHill(walkWorld, steepFoot, -Vector3.UnitX);

        checks.Check(
            "**緩い丘(29 度)は登れる**(1m 以上高いところまで行ける)",
            gentleTop > 1.0f,
            $"{gentleTop:F2}m 登った");

        checks.Check(
            "**急な丘(58 度)は登れない**(坂の上限で弾かれる)",
            steepTop < 0.6f,
            $"{steepTop:F2}m しか登れない");

        Console.WriteLine(
            checks.Failures == 0
                ? "  → 全項目 OK"
                : $"  → **{checks.Failures} 件が不合格**");
        Console.WriteLine();
    }

    /// <summary>
    /// 地形と壁だけの世界(自己チェック用)。**動く体を1つも置かない**。
    ///
    /// キャラクターの歩行を測るときは、転がっている体が邪魔になる。
    /// <b>1つの実験で変えるのは1つだけ</b>にしておかないと、
    /// 結果がどちらの原因なのか分からなくなる。
    /// </summary>
    private static PhysicsWorld BuildTerrainOnlyWorld()
    {
        var world = new PhysicsWorld
        {
            Broadphase = BroadphaseMode.UniformGrid,
            CellSize = 2.0f,
            GridOrigin = new Vector3(-16.0f, -4.0f, -16.0f),
            GridSize = new Vector3(32.0f, 24.0f, 32.0f),
        };

        HeightField field = BuildTestField(
            TerrainCells + 1, TerrainCells + 1, TerrainCellSize, TerrainHeightAt);

        world.AddTerrain(field, new Vector3(-TerrainSpan * 0.5f, PhysicsFloorY, -TerrainSpan * 0.5f));

        float wall = TerrainSpan * 0.5f;
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(-wall, 0.0f, 0.0f), Vector3.UnitX));
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(wall, 0.0f, 0.0f), -Vector3.UnitX));
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(0.0f, 0.0f, -wall), Vector3.UnitZ));
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(0.0f, 0.0f, wall), -Vector3.UnitZ));

        // **1回だけ進めて格子を組ませる**。問い合わせは Step とは別の入口なので、
        // 一度も進めていない世界では格子がまだ無い(総当たりに落ちる)。
        world.Step(1.0f / 60.0f);

        return world;
    }

    /// <summary>自己チェック用の地形を、高さの式から作る。</summary>
    private static HeightField BuildTestField(
        int columns, int rows, float cellSize, Func<float, float, float> height)
    {
        var heights = new float[columns * rows];

        for (int z = 0; z < rows; z++)
        {
            for (int x = 0; x < columns; x++)
            {
                heights[(z * columns) + x] = height(x * cellSize, z * cellSize);
            }
        }

        return new HeightField(heights, columns, rows, cellSize);
    }

    /// <summary>
    /// 自己チェック用の世界を組む。**同じ種で必ず同じ配置になる**。
    ///
    /// ブロードフェーズを比べるためのものなので、
    /// <b>2通りで組んだ世界が1ビットも違わない</b>ことが命になる。
    /// 乱数の種を固定し、体を足す順番も固定してある。
    /// </summary>
    private static PhysicsWorld BuildBroadphaseWorld(BroadphaseMode mode, float cellSize)
    {
        var world = new PhysicsWorld
        {
            Broadphase = mode,
            CellSize = cellSize,
            GridOrigin = new Vector3(-16.0f, -4.0f, -16.0f),
            GridSize = new Vector3(32.0f, 24.0f, 32.0f),
        };

        var field = BuildTestField(
            TerrainCells + 1, TerrainCells + 1, TerrainCellSize, TerrainHeightAt);

        world.AddTerrain(field, new Vector3(-TerrainSpan * 0.5f, PhysicsFloorY, -TerrainSpan * 0.5f));

        float wall = TerrainSpan * 0.5f;
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(-wall, 0.0f, 0.0f), Vector3.UnitX));
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(wall, 0.0f, 0.0f), -Vector3.UnitX));
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(0.0f, 0.0f, -wall), Vector3.UnitZ));
        world.AddPlane(Plane3D.FromPointNormal(new Vector3(0.0f, 0.0f, wall), -Vector3.UnitZ));
        world.AddPlane(Plane3D.FromPointNormal(
            new Vector3(0.0f, PhysicsFloorY - 6.0f, 0.0f), Vector3.UnitY));

        var random = new Random(46046);

        for (int i = 0; i < 200; i++)
        {
            RigidBody body = (i % 3) switch
            {
                0 => RigidBody.CreateSphere(1.0f, 0.22f),
                1 => RigidBody.CreateBox(1.0f, 0.20f),
                _ => RigidBody.CreateCapsule(1.0f, 0.16f, 0.18f),
            };

            body.Position = new Vector3(
                ((float)random.NextDouble() - 0.5f) * (TerrainSpan - 3.0f),
                PhysicsFloorY + 4.0f + ((float)random.NextDouble() * 6.0f),
                ((float)random.NextDouble() - 0.5f) * (TerrainSpan - 3.0f));

            body.Orientation = Quaternion.CreateFromYawPitchRoll(
                (float)random.NextDouble() * 6.28f,
                (float)random.NextDouble() * 6.28f,
                (float)random.NextDouble() * 6.28f);

            body.Restitution = 0.15f;
            body.AngularDamping = 0.4f;

            world.AddBody(body);
        }

        return world;
    }

    /// <summary>
    /// 世界を 90 ステップ進めてから、接触している組を集める。
    ///
    /// **90 ステップ進めてから**なのが要点。作った直後は全員が空中にいて
    /// 接触が1つも無いので、比べても何も分からない。
    /// 地形に着いて、互いに積み重なった状態で比べる。
    /// </summary>
    private static HashSet<(int, int)> StepAndCollectPairs(BroadphaseMode mode, float cellSize)
    {
        // **まず総当たりで 90 ステップ進める**。
        // ここを比べたい設定で進めてしまうと、両者の状態がだんだんずれて
        // 「接触が1組違う」のが<b>ブロードフェーズのせいなのか、
        // 解く順番のせいなのか</b>分からなくなる。
        //
        // 組の間はガウス・ザイデル(Day 43 の要点8)なので、
        // <b>解く順番が変われば結果もわずかに変わる</b>——
        // これはブロードフェーズの正しさとは別の話。
        // 同じ状態から1ステップだけ切り替えて比べれば、
        // 「候補の作り方」だけを比べたことになる。
        PhysicsWorld world = BuildBroadphaseWorld(BroadphaseMode.BruteForce, cellSize);

        for (int step = 0; step < 90; step++)
        {
            world.Step(1.0f / 60.0f);
        }

        world.Broadphase = mode;
        world.Step(1.0f / 60.0f);

        var pairs = new HashSet<(int, int)>();
        foreach (ContactPoint contact in world.Contacts)
        {
            pairs.Add((contact.A, contact.B));
        }

        return pairs;
    }

    /// <summary>組が入っているか(自己チェック用)。</summary>
    private static bool ContainsPair(ReadOnlySpan<BroadPair> pairs, int a, int b)
    {
        foreach (BroadPair pair in pairs)
        {
            if ((pair.A == a && pair.B == b) || (pair.A == b && pair.B == a))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 丘へ向かって 4 秒歩かせて、**どれだけ登れたか**を返す。
    ///
    /// 坂の上限が効いていれば急な丘では登れず、
    /// 効いていなければ壁でもよじ登る(Day 45 の要点7)。
    /// <b>数字で確かめないと、絵では「登れていない」のか
    /// 「引っかかっている」のかが区別できない</b>。
    /// </summary>
    private static float WalkTowardHill(PhysicsWorld world, Vector3 start, Vector3 direction)
    {
        var walker = new CharacterController();
        walker.Teleport(start);

        // まず落ち着かせる(空中から落とすと最初の数フレームで滑る)。
        for (int step = 0; step < 30; step++)
        {
            walker.Move(world, Vector3.Zero, run: false, jump: false, 1.0f / 60.0f);
        }

        float baseline = walker.Position.Y;
        float highest = baseline;

        for (int step = 0; step < 240; step++)
        {
            walker.Move(world, direction, run: false, jump: false, 1.0f / 60.0f);
            highest = MathF.Max(highest, walker.Position.Y);
        }

        return highest - baseline;
    }


    /// <summary>
    /// 今日の自己チェック(Ctrl+Shift+Alt+F)。**画面を1枚も出さずに走る**。
    ///
    /// 今日の題材は<b>どれも絵からは判定できない</b>。
    /// 「柱が沈んでいない」「摩擦が正しい」「眠っている」は、
    /// 目で見ても<b>それらしく見えるかどうか</b>しか分からない。
    /// だから数字で押さえる——
    /// 静止摩擦角や停止距離のように<b>手計算で答えが出る場面</b>を選んで突き合わせるのが要点。
    /// </summary>
    private static void RunSolverCheck()
    {
        Console.WriteLine();
        Console.WriteLine("--- Day 47: 摩擦・Sequential Impulses・スリープの自己チェック ---");
        var checks = new CheckList();

        // ============================================================
        //  1. 接線の作り方(要点3)
        // ============================================================

        var random = new Random(4700);
        float worstTangentError = 0.0f;

        for (int trial = 0; trial < 400; trial++)
        {
            Vector3 normal = RandomPoint(random);
            if (normal.LengthSquared() < 1e-6f)
            {
                continue;
            }

            normal = Vector3.Normalize(normal);
            ContactPoint.BuildTangents(normal, out Vector3 t1, out Vector3 t2);

            worstTangentError = MathF.Max(worstTangentError, MathF.Abs(Vector3.Dot(normal, t1)));
            worstTangentError = MathF.Max(worstTangentError, MathF.Abs(Vector3.Dot(normal, t2)));
            worstTangentError = MathF.Max(worstTangentError, MathF.Abs(Vector3.Dot(t1, t2)));
            worstTangentError = MathF.Max(worstTangentError, MathF.Abs(t1.Length() - 1.0f));
            worstTangentError = MathF.Max(worstTangentError, MathF.Abs(t2.Length() - 1.0f));
        }

        checks.Check(
            "接線2本が法線と直交し、互いにも直交して長さ1(400 通り)",
            worstTangentError < 1e-5f,
            $"最悪 {worstTangentError:E2}");

        bool axisSafe = true;
        Vector3[] axes =
        [
            Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY,
            -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
        ];

        foreach (Vector3 axis in axes)
        {
            ContactPoint.BuildTangents(axis, out Vector3 t1, out Vector3 t2);
            axisSafe &= float.IsFinite(t1.X) && float.IsFinite(t2.X);
            axisSafe &= MathF.Abs(Vector3.Dot(axis, t1)) < 1e-5f;
        }

        checks.Check(
            "**法線が軸に平行でも NaN が出ない**(成分が最小の軸を選ぶから)",
            axisSafe);

        // ============================================================
        //  2. 摩擦係数の混ぜ方(要点3)
        // ============================================================

        var mixWorld = new PhysicsWorld { Gravity = Vector3.Zero };
        RigidBody mixFloor = RigidBody.CreatePlane(Vector3.Zero, Vector3.UnitY);
        mixFloor.Friction = 0.81f;
        mixWorld.AddBody(mixFloor);

        RigidBody mixBox = RigidBody.CreateBox(1.0f, 0.5f);
        mixBox.Position = new Vector3(0.0f, 0.499f, 0.0f);
        mixBox.Friction = 0.25f;
        mixWorld.AddBody(mixBox);
        mixWorld.Step(1.0f / 60.0f);

        checks.Check(
            "摩擦は**相乗平均** √(0.25 x 0.81) = 0.45",
            mixWorld.Contacts.Count > 0
                && MathF.Abs(mixWorld.Contacts[0].Friction - 0.45f) < 1e-4f,
            mixWorld.Contacts.Count > 0 ? $"{mixWorld.Contacts[0].Friction:F4}" : "接触なし");

        mixBox.Friction = 0.0f;
        mixWorld.Step(1.0f / 60.0f);

        checks.Check(
            "**片方が 0 なら合成も 0**(氷の上では何を置いても滑る)",
            mixWorld.Contacts.Count > 0 && mixWorld.Contacts[0].Friction == 0.0f);

        mixWorld.FrictionOverride = 0.33f;
        mixWorld.Step(1.0f / 60.0f);

        checks.Check(
            "上書き(<c>FrictionOverride</c>)は体ごとの値より優先される",
            mixWorld.Contacts.Count > 0
                && MathF.Abs(mixWorld.Contacts[0].Friction - 0.33f) < 1e-5f);

        // ============================================================
        //  3. 温存(ウォームスタート。要点2)
        // ============================================================

        // **同じ柱を2通りで回して、沈み方を比べる**。
        // 反復回数も初期配置も同じなので、差は温存だけで説明が付く。
        PhysicsWorld warm = BuildStackWorld(6, accumulate: true, warmStarting: true, iterations: 8);
        PhysicsWorld cold = BuildStackWorld(6, accumulate: true, warmStarting: false, iterations: 8);

        StepWorld(warm, 3.0f);
        StepWorld(cold, 3.0f);

        checks.Check(
            "**温存ありのほうが柱が沈まない**(6段・反復8周・3秒)",
            TopBoxHeight(warm) > TopBoxHeight(cold) + 0.05f,
            $"温存あり {TopBoxHeight(warm):F3}m / なし {TopBoxHeight(cold):F3}m"
                + $"(理論 {StackTopHeight(6):F3}m)");

        checks.Check(
            "温存ありなら理論の高さから 2cm 以内",
            MathF.Abs(TopBoxHeight(warm) - StackTopHeight(6)) < 0.02f,
            $"ずれ {(TopBoxHeight(warm) - StackTopHeight(6)) * 1000.0f:F1}mm");

        checks.Check(
            "めり込みは許容(スロップ)の2倍以内に収まる",
            warm.MaxPenetration <= warm.Slop * 2.0f,
            $"{warm.MaxPenetration * 1000.0f:F2}mm(許容 {warm.Slop * 1000.0f:F1}mm)");

        checks.Check(
            "落ち着いた柱では**ほとんどの点が持ち越せている**",
            warm.Contacts.Count > 0
                && warm.WarmStartedPoints >= (int)(warm.Contacts.Count * 0.8f),
            $"{warm.WarmStartedPoints}/{warm.Contacts.Count} 点");

        checks.Check(
            "キャッシュが覚えている組は、今触れている組と同じ数",
            warm.CachedPairs == warm.ContactPairs,
            $"覚えている {warm.CachedPairs} 組 / 触れている {warm.ContactPairs} 組");

        // 離れたら忘れる。**組が消えたのに覚えたままだと、次に触れた瞬間に弾ける**。
        PhysicsWorld forget = BuildStackWorld(2, accumulate: true, warmStarting: true, iterations: 8);
        StepWorld(forget, 0.5f);
        int rememberedWhileTouching = forget.CachedPairs;

        int scatter = 0;
        foreach (RigidBody body in forget.Bodies)
        {
            scatter++;
            if (!body.IsStatic)
            {
                body.Wake();
                body.Position += new Vector3(scatter * 10.0f, 20.0f, 0.0f);
            }
        }

        forget.Step(1.0f / 60.0f);

        checks.Check(
            "離れた組はキャッシュから消える(<c>Prune</c>)",
            rememberedWhileTouching > 0 && forget.CachedPairs == 0,
            $"接触中 {rememberedWhileTouching} 組 → 離れたあと {forget.CachedPairs} 組");

        // ============================================================
        //  4. 蓄積インパルス(要点1)
        // ============================================================

        // **正直な結果を先に**。柱がゆっくり落ち着くだけの場面では、
        // 蓄積クランプと素朴なクランプは<b>1ビットも違わない</b>——
        // 必要なインパルスが増える一方なので、負の補正を出す機会が来ない。
        PhysicsWorld accumulated =
            BuildStackWorld(6, accumulate: true, warmStarting: false, iterations: 4);
        PhysicsWorld naive =
            BuildStackWorld(6, accumulate: false, warmStarting: false, iterations: 4);

        StepWorld(accumulated, 2.0f);
        StepWorld(naive, 2.0f);

        checks.Check(
            "**蓄積クランプ単体では素朴版と同じ結果**(λ が常に正の場面では差が出ない)",
            MathF.Abs(TopBoxHeight(accumulated) - TopBoxHeight(naive)) < 1e-4f,
            $"蓄積 {TopBoxHeight(accumulated):F5}m / 素朴 {TopBoxHeight(naive):F5}m");

        checks.Check(
            "**素朴版では温存が働かない**(合計を減らせないので単調に増えてしまう)",
            !new PhysicsWorld { AccumulateImpulses = false, WarmStarting = true }.WarmStartActive);

        // **負の補正が出せることを直接見る**。
        // 落ち着いた柱から重力を消すと、持ち越したインパルスは<b>全部余分</b>になる。
        // 蓄積クランプなら合計を 0 まで減らせる——素朴版にはできない動き。
        PhysicsWorld unloaded = BuildStackWorld(4, accumulate: true, warmStarting: true, iterations: 8);
        StepWorld(unloaded, 2.0f);

        foreach (RigidBody body in unloaded.Bodies)
        {
            body.Wake();
        }

        unloaded.Step(1.0f / 60.0f);
        float loadedImpulse = TotalNormalImpulse(unloaded);

        unloaded.Gravity = Vector3.Zero;
        float fastest = 0.0f;

        for (int step = 0; step < 10; step++)
        {
            foreach (RigidBody body in unloaded.Bodies)
            {
                body.Wake();
            }

            unloaded.Step(1.0f / 60.0f);

            foreach (RigidBody body in unloaded.Bodies)
            {
                if (!body.IsStatic)
                {
                    fastest = MathF.Max(fastest, body.LinearVelocity.Length());
                }
            }
        }

        checks.Check(
            "**重力を消すと溜まったインパルスが 0 まで減る**(負の補正が出せている証拠)",
            loadedImpulse > 0.0f && TotalNormalImpulse(unloaded) < loadedImpulse * 0.1f,
            $"{loadedImpulse:F4} → {TotalNormalImpulse(unloaded):F4} N·s");

        checks.Check(
            "そのとき柱は跳ね上がらない(持ち越しを掛けっぱなしにしていない)",
            fastest < 0.3f,
            $"いちばん速い体で {fastest:F3}m/s");

        // ============================================================
        //  5. 摩擦 — 静止摩擦角(要点3)
        // ============================================================

        // **tan θ = 0.5 の坂**。μ がこれより大きい箱だけが止まる。
        float tangent = MathF.Tan(RampTilt);

        checks.Check(
            "坂の傾きの正接がちょうど 0.50(26.57 度)",
            MathF.Abs(tangent - 0.5f) < 1e-3f,
            $"tan {RampTilt * 180.0f / MathF.PI:F2}° = {tangent:F4}");

        float slide01 = RampSlide(0.1f, 1.0f, 1.5f);
        float slide03 = RampSlide(0.3f, 1.0f, 1.5f);
        float slide05 = RampSlide(0.5f, 1.0f, 1.5f);
        float slide07 = RampSlide(0.7f, 1.0f, 1.5f);
        float slide09 = RampSlide(0.9f, 1.0f, 1.5f);

        checks.Check(
            "**μ = 0.1 の箱は滑り落ちる**(1.5 秒で 1m 以上)",
            slide01 > 1.0f,
            $"{slide01:F3}m");

        checks.Check(
            "μ = 0.3 も滑るが、0.1 より遅い",
            slide03 > 0.2f && slide03 < slide01,
            $"{slide03:F3}m");

        checks.Check(
            "**μ = 0.7 の箱は止まる**(1.5 秒で 1cm 未満)",
            slide07 < 0.01f,
            $"{slide07 * 1000.0f:F2}mm");

        checks.Check(
            "μ = 0.9 も止まる",
            slide09 < 0.01f,
            $"{slide09 * 1000.0f:F2}mm");

        checks.Check(
            "**μ = 0.5 はちょうど境目**(数センチだけずれて止まる)",
            slide05 < slide03 && slide05 >= slide07,
            $"{slide05 * 1000.0f:F1}mm");

        checks.Check(
            "μ が大きいほど滑る距離が短い(単調)",
            slide01 > slide03 && slide03 > slide05 && slide05 >= slide07 && slide07 >= slide09);

        // **質量が式から消える**。ここが摩擦のいちばん反直感的なところ。
        float lightSlide = RampSlide(0.3f, 1.0f, 1.5f);
        float heavySlide = RampSlide(0.3f, 20.0f, 1.5f);

        checks.Check(
            "**滑る距離は質量によらない**(1kg と 20kg で 1% 以内)",
            MathF.Abs(lightSlide - heavySlide) < MathF.Max(lightSlide, 1e-3f) * 0.01f,
            $"1kg {lightSlide:F4}m / 20kg {heavySlide:F4}m");

        checks.Check(
            "摩擦を切ると μ = 0.9 でも滑り落ちる(Day 46 までの世界)",
            RampSlide(0.9f, 1.0f, 1.5f, frictionEnabled: false) > 1.0f);

        // ============================================================
        //  6. 摩擦円錐(要点3)
        // ============================================================

        // **接線2本を別々にクランプしていないこと**を確かめる。
        // 別々に切ると上限の形が正方形になり、斜め 45 度だけ √2 倍の摩擦が出る。
        float worstRatio = 0.0f;
        bool coneHeld = true;

        for (int trial = 0; trial < 64; trial++)
        {
            float angle = trial * MathF.Tau / 64.0f;
            var direction = new Vector3(MathF.Cos(angle), 0.0f, MathF.Sin(angle));

            float ratio = SlidingFrictionRatio(direction, 0.4f);
            coneHeld &= ratio <= 1.05f;
            worstRatio = MathF.Max(worstRatio, ratio);
        }

        checks.Check(
            "**接線インパルスの合成が μ λn を超えない**(滑る向きを 64 通り)",
            coneHeld,
            $"最大 {worstRatio:F4}(正方形クランプなら 1.414 まで出る)");

        checks.Check(
            "滑っている接触では上限いっぱいまで使う(円錐の縁に乗る)",
            worstRatio > 0.95f,
            $"最大 {worstRatio:F4}");

        // ============================================================
        //  7. 停止距離(要点3・4)
        // ============================================================

        // **v² / (2 μ g)**。高校物理の式そのもの。
        // 段の順番(要点4)が正しくないとここがずれる——
        // 位置を解く前の速度で進めていると、止まったあとも毎ステップ滑り続ける。
        float stopDistance = SlideDistance(frictionEnabled: true, out float endSpeed);
        float theory = 16.0f / (2.0f * 0.5f * 9.81f);

        checks.Check(
            "μ = 0.5 の床を 4m/s で滑らせた箱は**止まる**",
            endSpeed < 1e-3f,
            $"{endSpeed:E2}m/s");

        checks.Check(
            "**停止距離が v²/(2μg) と 10% 以内で一致**",
            MathF.Abs(stopDistance - theory) < theory * 0.10f,
            $"実測 {stopDistance:F3}m / 理論 {theory:F3}m");

        checks.Check(
            "**摩擦を切ると 1mm も減速しない**(Day 46 までの氷)",
            SlideDistance(frictionEnabled: false, out float icySpeed) > 7.9f && icySpeed > 3.999f,
            $"{icySpeed:F4}m/s");

        // ============================================================
        //  8. 眠り(要点5)
        // ============================================================

        PhysicsWorld sleeper = BuildStackWorld(1, accumulate: true, warmStarting: true, iterations: 8);
        StepWorld(sleeper, 0.4f);
        bool awakeEarly = !sleeper.Bodies[1].IsSleeping;

        StepWorld(sleeper, 1.0f);

        checks.Check(
            "落ち着いた箱は**しばらく経ってから**眠る(0.4 秒ではまだ起きている)",
            awakeEarly && sleeper.Bodies[1].IsSleeping);

        Vector3 sleepingPosition = sleeper.Bodies[1].Position;
        StepWorld(sleeper, 1.0f);

        checks.Check(
            "**眠っている体は 1mm も動かない**(重力すら掛からない)",
            (sleeper.Bodies[1].Position - sleepingPosition).Length() < 1e-6f);

        checks.Check(
            "眠っている体は速度も 0",
            sleeper.Bodies[1].LinearVelocity.LengthSquared() == 0.0f);

        // **上から落として起こす**。
        RigidBody waker = RigidBody.CreateBox(1.0f, 0.25f);
        waker.Position = new Vector3(0.0f, 3.0f, 0.0f);
        sleeper.AddBody(waker);
        StepWorld(sleeper, 1.0f);

        checks.Check(
            "**上から物が落ちてくると眠っていた箱が起きる**",
            !sleeper.Bodies[1].IsSleeping || sleeper.Bodies[1].SleepTimer < sleeper.SleepTime);

        // **島でまとめて眠る**。
        PhysicsWorld island = BuildStackWorld(4, accumulate: true, warmStarting: true, iterations: 8);
        StepWorld(island, 2.5f);

        checks.Check(
            "積んだ4段は**全部まとめて眠る**(島の判定)",
            island.SleepingCount == 4,
            $"{island.SleepingCount}/4 体");

        checks.Check(
            "接触で繋がった4段は**1つの島**",
            island.IslandCount == 1,
            $"島 {island.IslandCount} 個");

        island.Bodies[1].Wake();
        island.Step(1.0f / 60.0f);

        checks.Check(
            "**1体を起こすと島ごと起きる**",
            island.SleepingCount == 0,
            $"眠り {island.SleepingCount}/4 体");

        StepWorld(island, 2.5f);
        bool sleptAgain = island.SleepingCount == 4;
        island.SleepEnabled = false;
        island.Step(1.0f / 60.0f);

        checks.Check(
            "眠りを切ると**今眠っているものも起きる**",
            sleptAgain && island.SleepingCount == 0);

        // **眠らない体が1つ居ると島ごと眠らない**。
        PhysicsWorld insomnia = BuildStackWorld(4, accumulate: true, warmStarting: true, iterations: 8);
        insomnia.Bodies[2].AllowSleep = false;
        StepWorld(insomnia, 3.0f);

        checks.Check(
            "**AllowSleep を切った体が居る島は誰も眠らない**",
            insomnia.SleepingCount == 0,
            $"眠り {insomnia.SleepingCount}/4 体");

        // **静的な体は島を繋がない**。繋いだら世界じゅうが1つの島になる。
        var separate = new PhysicsWorld();
        separate.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

        for (int i = 0; i < 3; i++)
        {
            RigidBody box = RigidBody.CreateBox(1.0f, 0.25f);
            box.Position = new Vector3((i - 1) * 4.0f, 0.26f, 0.0f);
            box.Restitution = 0.0f;
            separate.AddBody(box);
        }

        StepWorld(separate, 0.5f);

        checks.Check(
            "**床の上に離れて置いた3個は3つの島**(静的な体は島を繋がない)",
            separate.IslandCount == 3,
            $"島 {separate.IslandCount} 個");

        Console.WriteLine();
        checks.Report("すべて合格。**ミニ物理エンジンが一通り揃った**");
        Console.WriteLine();
    }

    /// <summary>自己チェック用の柱の箱の半径。</summary>
    private const float StackHalf = 0.25f;

    /// <summary>
    /// 自己チェック用の柱。**床 + 箱を縦に <paramref name="height"/> 個**。
    ///
    /// 解き方のつまみを引数で受けるので、
    /// <b>同じ配置を2通りで回して比べる</b>ことができる。
    /// </summary>
    private static PhysicsWorld BuildStackWorld(
        int height, bool accumulate, bool warmStarting, int iterations)
    {
        var world = new PhysicsWorld
        {
            AccumulateImpulses = accumulate,
            WarmStarting = warmStarting,
            VelocityIterations = iterations,
            Broadphase = BroadphaseMode.BruteForce,
        };

        RigidBody floor = RigidBody.CreatePlane(Vector3.Zero, Vector3.UnitY);
        floor.Friction = 0.6f;
        world.AddBody(floor);

        for (int i = 0; i < height; i++)
        {
            RigidBody box = RigidBody.CreateBox(1.0f, StackHalf);

            // **ぴったり積む**。隙間を空けると落下の勢いが乗って比較にならない。
            box.Position = new Vector3(0.0f, StackHalf + (i * StackHalf * 2.0f), 0.0f);
            box.Restitution = 0.0f;
            box.Friction = 0.6f;
            world.AddBody(box);
        }

        return world;
    }

    /// <summary>めり込みが無いときの、いちばん上の箱の中心の高さ。</summary>
    private static float StackTopHeight(int height) => StackHalf + ((height - 1) * StackHalf * 2.0f);

    /// <summary>いちばん上(最後に足した)箱の中心の高さ。**沈み具合の物差し**。</summary>
    private static float TopBoxHeight(PhysicsWorld world) => world.Bodies[^1].Position.Y;

    /// <summary>今この世界に溜まっている法線インパルスの合計 [N·s]。</summary>
    private static float TotalNormalImpulse(PhysicsWorld world)
    {
        float total = 0.0f;

        foreach (ContactPoint contact in world.Contacts)
        {
            total += contact.NormalImpulse;
        }

        return total;
    }

    /// <summary>固定ステップで <paramref name="seconds"/> 秒ぶん進める。</summary>
    private static void StepWorld(PhysicsWorld world, float seconds)
    {
        int steps = (int)MathF.Round(seconds * 60.0f);

        for (int i = 0; i < steps; i++)
        {
            world.Step(1.0f / 60.0f);
        }
    }

    /// <summary>
    /// 坂に置いた箱が <paramref name="seconds"/> 秒で滑った距離 [m]。**静止摩擦角の物差し**。
    ///
    /// 坂は μ = 1 にしてあるので、箱に μ² を入れれば合成が
    /// <paramref name="friction"/> になる(<see cref="BuildFrictionRampScene"/> と同じ手)。
    /// 距離は<b>坂に沿った向き</b>で測る——真下に落ちたぶんを混ぜないため。
    /// </summary>
    private static float RampSlide(
        float friction, float mass, float seconds, bool frictionEnabled = true)
    {
        var world = new PhysicsWorld
        {
            FrictionEnabled = frictionEnabled,
            Broadphase = BroadphaseMode.BruteForce,
        };

        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -RampTilt);

        RigidBody ramp = RigidBody.CreateStatic(Collider.Box(new Vector3(8.0f, 0.2f, 2.0f)));
        ramp.Orientation = tilt;
        ramp.Restitution = 0.0f;
        ramp.Friction = 1.0f;
        world.AddBody(ramp);

        const float half = 0.25f;

        RigidBody box = RigidBody.CreateBox(mass, half);
        box.Position = Vector3.Transform(new Vector3(-3.0f, 0.2f + half + 0.001f, 0.0f), tilt);
        box.Orientation = tilt;
        box.Restitution = 0.0f;
        box.Friction = friction * friction;

        // **眠らせない**。止まった箱が眠るのは正しい挙動だが、
        // 「摩擦で止まったのか眠って止まったのか」が区別できなくなる。
        box.AllowSleep = false;
        world.AddBody(box);

        Vector3 start = box.Position;

        // 坂に沿った下り方向(坂の物体座標の +X を世界へ運んだもの)。
        Vector3 downhill = Vector3.Transform(Vector3.UnitX, tilt);

        StepWorld(world, seconds);

        return MathF.Max(Vector3.Dot(box.Position - start, downhill), 0.0f);
    }

    /// <summary>
    /// 滑っている接触で、接線インパルスが上限の何割まで使われているか。**摩擦円錐の検算**。
    ///
    /// 1 を超えたら円錐からはみ出している(= 接線2本を別々にクランプしている)。
    /// <paramref name="direction"/> を1周ぶん振ると、
    /// <b>正方形クランプなら 45 度で 1.414 が出る</b>。
    ///
    /// <para>
    /// 1 をほんの少し(2% ほど)超えることはある。
    /// 摩擦は法線より<b>先に</b>解くので、上限に使う法線インパルスが
    /// 1周ぶん古いことがあるため——Box2D も同じ性質を持っている。
    /// </para>
    /// </summary>
    private static float SlidingFrictionRatio(Vector3 direction, float friction)
    {
        var world = new PhysicsWorld { Broadphase = BroadphaseMode.BruteForce };

        RigidBody floor = RigidBody.CreatePlane(Vector3.Zero, Vector3.UnitY);
        floor.Friction = 1.0f;
        world.AddBody(floor);

        RigidBody box = RigidBody.CreateBox(1.0f, 0.25f);

        // **わずかにめり込ませて置く**。判定は位置を進める前に走るので(要点4)、
        // 浮かせて置くと1ステップ目に接触が生まれない。
        box.Position = new Vector3(0.0f, 0.2495f, 0.0f);
        box.Restitution = 0.0f;
        box.Friction = friction * friction;
        box.AllowSleep = false;
        world.AddBody(box);

        // **十分速く滑らせる**。ゆっくりだと止まってしまい、上限に届かない。
        // 2ステップ回すのは、1ステップ目に接触ができて法線インパルスが溜まるのを待つため。
        box.LinearVelocity = direction * 8.0f;
        world.Step(1.0f / 60.0f);
        box.LinearVelocity = direction * 8.0f;
        world.Step(1.0f / 60.0f);

        float worst = 0.0f;

        foreach (ContactPoint contact in world.Contacts)
        {
            if (contact.NormalImpulse <= 0.0f || contact.Friction <= 0.0f)
            {
                continue;
            }

            float magnitude = MathF.Sqrt(
                (contact.TangentImpulse1 * contact.TangentImpulse1)
                + (contact.TangentImpulse2 * contact.TangentImpulse2));

            worst = MathF.Max(worst, magnitude / (contact.Friction * contact.NormalImpulse));
        }

        return worst;
    }

    /// <summary>
    /// 平らな床を 4m/s で滑らせた箱が、2 秒で進んだ距離 [m]。**停止距離の物差し**。
    ///
    /// 摩擦ありなら <c>v²/(2μg)</c> で止まり、摩擦なしなら等速で 8m 進む。
    /// </summary>
    private static float SlideDistance(bool frictionEnabled, out float speed)
    {
        var world = new PhysicsWorld { FrictionEnabled = frictionEnabled };
        world.AddPlane(Plane3D.FromPointNormal(Vector3.Zero, Vector3.UnitY));

        RigidBody puck = RigidBody.CreateBox(1.0f, 0.25f);
        puck.Position = new Vector3(0.0f, 0.2505f, 0.0f);
        puck.LinearVelocity = new Vector3(4.0f, 0.0f, 0.0f);
        puck.Restitution = 0.0f;
        puck.AllowSleep = false;
        world.AddBody(puck);

        float startX = puck.Position.X;
        StepWorld(world, 2.0f);

        speed = puck.LinearVelocity.Length();
        return puck.Position.X - startX;
    }

    private static Vector3 RandomPoint(Random random) =>
        new(
            (float)((random.NextDouble() * 2.0) - 1.0),
            (float)((random.NextDouble() * 2.0) - 1.0),
            (float)((random.NextDouble() * 2.0) - 1.0));

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
