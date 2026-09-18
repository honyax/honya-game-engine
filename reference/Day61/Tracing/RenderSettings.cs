namespace CpuRayTracer;

/// <summary>
/// どこで光線を追うか(G キー)。<b>Day 61 で追加。今日の目</b>。
///
/// <para>
/// 絵も設定も場面も同じで、<b>計算する場所だけ</b>が違う。だから見比べたときに
/// 「同じ絵が出て、速さだけが桁で違う」になっていなければならない——なっていなければ、
/// どちらかの式が間違っている(要点7)。
/// </para>
/// </summary>
internal enum RenderDevice
{
    /// <summary>CPU。Day 60 の <see cref="PathTracer"/> / <see cref="WhittedTracer"/> を <see cref="ProgressiveRenderer"/> が回す。</summary>
    Cpu,

    /// <summary>
    /// GPU。<c>shaders/pathtrace.comp</c> を <see cref="GpuRenderer"/> がディスパッチする。
    /// <b>パストレーシングだけ</b>(Whitted 法は移していない。木の再帰が GLSL に書けないため。要点3)。
    /// </summary>
    Gpu,
}

/// <summary>光線の追い方(A キー)。Day 60 で追加。</summary>
internal enum TraceAlgorithm
{
    /// <summary>
    /// Whitted 法(Day 59)。面に当たるたびに<b>木が枝分かれ</b>する。
    /// 拡散面は直接光だけで決め、周りから跳ね返って来る光は一定の環境光で済ませる。
    /// </summary>
    Whitted,

    /// <summary>
    /// パストレーシング(Day 60)。面に当たるたびに<b>行き先を1つだけ引く</b>(道が1本)。
    /// 何千本も撃って平均すると、レンダリング方程式の答えに近づく(要点1・2)。
    /// </summary>
    Path,
}

/// <summary>フレネル(反射と透過の割合)の求め方。F キーで回す。</summary>
internal enum FresnelMode
{
    /// <summary>正確な式(S 波と P 波の平均)。既定。</summary>
    Exact,

    /// <summary>シュリック近似。<b>屈折率 1.00 の玉の縁がうっすら光る</b>(近似が n1 = n2 を知らないため)。</summary>
    Schlick,

    /// <summary>角度によらず垂直入射の値。ガラスの縁が明るくならず、金属が金属らしく見えなくなる。</summary>
    Off,
}

/// <summary>何を画素の色にするか。V キーで回す。</summary>
internal enum ViewMode
{
    /// <summary>光線を最後まで追った色。</summary>
    Shaded,

    /// <summary>最初に当たった面の法線(光線の側を向けたもの)。形と表裏の確認用。</summary>
    Normal,

    /// <summary>1画素のために飛ばした光線の数。<b>どこが高いか</b>を見る。</summary>
    RayCount,

    /// <summary>
    /// 1画素のために行った<b>交差判定</b>の数(箱 + 形)。Day 60 で追加。
    /// <b>B キーで探し方を変えると、この絵がはっきり変わる</b>(要点8)。
    /// </summary>
    Cost,
}

/// <summary>
/// 描画の設定。<b>不変</b>(record の <c>with</c> で作り直す)。
///
/// <para>
/// 描画スレッドが走っている最中に値が書き換わると、1枚の絵の中で設定が混ざる。
/// キーを押したら新しい設定を作り、描画を止めて最初からやり直す(<see cref="ProgressiveRenderer.Start"/>)。
/// </para>
/// </summary>
internal sealed record RenderSettings
{
    /// <summary>
    /// どこで計算するか(G キー)。<b>Day 61 で追加</b>。GPU が使えないときは <see cref="RenderDevice.Cpu"/> のまま。
    /// </summary>
    public RenderDevice Device { get; init; } = RenderDevice.Gpu;

    /// <summary>光線の追い方(A キー)。Day 60 で追加。</summary>
    public TraceAlgorithm Algorithm { get; init; } = TraceAlgorithm.Path;

    /// <summary>
    /// 何段まで追うか([ / ] キー)。
    ///
    /// <para>
    /// Whitted では<b>木の深さ</b>(0 なら反射も屈折も追わない)。
    /// パストレーシングでは<b>道の長さ</b>(0 なら最初に当たった面の直接光だけ)。
    /// どちらも「カメラの光線が 0 段目」で、上限に着いた枝はそこで打ち切る。
    /// </para>
    /// <para>
    /// パストレーシングで上限を切ると<b>絵が暗くなる</b>(打ち切った先の光が届かなくなる)。
    /// Whitted のように黒い穴が開くのではなく、全体がじわっと暗くなるのが違い——
    /// 上限を上げていったときに、どこで暗さが止まるかを見る(完成条件6)。
    /// </para>
    /// </summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>影の光線を飛ばすか(S キー)。Whitted 専用。切ると、光源へ向いている面は全部照らされる。</summary>
    public bool Shadows { get; init; } = true;

    /// <summary>フレネルの求め方(F キー)。</summary>
    public FresnelMode Fresnel { get; init; } = FresnelMode.Exact;

    /// <summary>
    /// 次の光線の始点を面から浮かせるか(E キー)。切ると<b>影のにきび</b>(shadow acne)が出る(Day 59 の要点7)。
    /// </summary>
    public bool OffsetOrigin { get; init; } = true;

    /// <summary>
    /// 1画素の中で光線を通す位置をサンプルごとにずらすか(J キー)。
    /// 切ると何サンプル重ねても毎回同じ画素内の位置を通り、ギザギザが消えない(Day 59 の要点6)。
    /// </summary>
    public bool Jitter { get; init; } = true;

    /// <summary>表示(V キー)。</summary>
    public ViewMode View { get; init; } = ViewMode.Shaded;

    /// <summary>1画素に何サンプル重ねたら止めるか。</summary>
    public int MaxSamples { get; init; } = 4096;

    // ---- ここから下は Day 60 で追加(パストレーシング) ----

    /// <summary>
    /// <b>NEE</b>(next event estimation。直接光のサンプリング。N キー)。要点4。
    ///
    /// <para>
    /// 拡散面に当たるたびに、光源へ影の光線を1本つなぐ。<b>切っても答えは同じ</b>——
    /// 散乱した光線が偶然光源に当たる経路だけで、同じ明るさに収束する。変わるのは<b>ノイズの量</b>だけ
    /// (自己チェック16)。ただし<b>点光源は大きさが 0 なので偶然当たることが無く、
    /// 切ると点光源の光が丸ごと消える</b>(要点4)。
    /// </para>
    /// </summary>
    public bool NextEventEstimation { get; init; } = true;

    /// <summary>
    /// <b>ロシアンルーレット</b>(U キー)。要点5。
    ///
    /// <para>
    /// 暗くなった道を確率で打ち切り、生き残った道をその確率で割って重くする。
    /// <b>平均は変わらないまま</b>、光線の数が減る。切ると深さの上限までまっすぐ追う。
    /// </para>
    /// </summary>
    public bool RussianRoulette { get; init; } = true;

    /// <summary>形の探し方(B キー)。要点7・8。</summary>
    public AccelerationMode Acceleration { get; init; } = AccelerationMode.Sah;

    /// <summary>
    /// 乱数の種に<b>画素の番号を混ぜる</b>か(K キー)。要点6。
    ///
    /// <para>
    /// 切ると全部の画素が同じ数列を使い、同じ向きへ散る。ノイズが画素ごとにばらけず、
    /// <b>画面をまたいだ模様</b>になる。平均としては正しい値に収束するので「間違い」ではないが、
    /// 同じサンプル数での見た目がはっきり悪くなる。
    /// </para>
    /// </summary>
    public bool DecorrelatePixels { get; init; } = true;

    /// <summary>
    /// 追い方に合わせた既定値。A キーで切り替えるときに使う。
    ///
    /// <para>
    /// Whitted は 64 サンプルで完全に止まる(乱数を使わないので、画素内のずらしを撃ち尽くしたら
    /// それ以上は変わらない)。パストレーシングは撃つほど滑らかになるので、実質止めない値にしてある。
    /// </para>
    /// </summary>
    public RenderSettings WithAlgorithm(TraceAlgorithm algorithm) => this with
    {
        Algorithm = algorithm,

        // Whitted 法は GPU へ移していない(枝分かれする木を GLSL の再帰なしで書くのは別の話になる。要点3)。
        // A で Whitted に切り替えたら、計算する場所も黙って CPU へ戻す。
        Device = algorithm == TraceAlgorithm.Whitted ? RenderDevice.Cpu : Device,
        MaxDepth = algorithm == TraceAlgorithm.Whitted ? 5 : 8,
        MaxSamples = algorithm == TraceAlgorithm.Whitted ? 64 : 4096,
    };
}
