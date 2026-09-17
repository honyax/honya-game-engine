namespace CpuRayTracer;

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
    /// <summary>光線の木を最後まで追った色。</summary>
    Shaded,

    /// <summary>最初に当たった面の法線(光線の側を向けたもの)。形と表裏の確認用。</summary>
    Normal,

    /// <summary>1画素のために飛ばした光線の数(カメラ・影・反射・屈折の合計)。<b>どこが高いか</b>を見る。</summary>
    RayCount,
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
    /// 反射・屈折を何段まで追うか([ / ] キー)。0 なら反射も屈折も追わず、鏡とガラスはハイライトだけになる。
    /// 上限に着いた枝は黒を返す(要点8)。
    /// </summary>
    public int MaxDepth { get; init; } = 5;

    /// <summary>影の光線を飛ばすか(S キー)。切ると、光源へ向いている面は全部照らされる。</summary>
    public bool Shadows { get; init; } = true;

    /// <summary>フレネルの求め方(F キー)。</summary>
    public FresnelMode Fresnel { get; init; } = FresnelMode.Exact;

    /// <summary>
    /// 次の光線の始点を面から浮かせるか(E キー)。切ると<b>影のにきび</b>(shadow acne)が出る(要点7)。
    /// </summary>
    public bool OffsetOrigin { get; init; } = true;

    /// <summary>
    /// 1画素の中で光線を通す位置をサンプルごとにずらすか(J キー)。
    /// 切ると何サンプル重ねても毎回同じ光線になり、ギザギザが消えない(要点6)。
    /// </summary>
    public bool Jitter { get; init; } = true;

    /// <summary>表示(V キー)。</summary>
    public ViewMode View { get; init; } = ViewMode.Shaded;

    /// <summary>1画素に何サンプル重ねたら止めるか。</summary>
    public int MaxSamples { get; init; } = 64;
}
