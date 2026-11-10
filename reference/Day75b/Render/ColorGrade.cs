using System.Numerics;

namespace HonyaEngine;

/// <summary>絵作りの下敷き(Shift+F7)。**数字の組を1つの名前にまとめたもの**。</summary>
internal enum GradePreset
{
    /// <summary>素通し。色温度 0、コントラスト 1、彩度 1。**比較の基準**。</summary>
    Neutral,

    /// <summary>夕暮れ。暖色へ振り、コントラストと彩度を少し上げる。</summary>
    Sunset,

    /// <summary>月夜。寒色へ振り、彩度を落とす。**夜は色が見えない**という目の性質に寄せる。</summary>
    Moonlight,

    /// <summary>退色。コントラストを強く、彩度を大きく落とす(ブリーチバイパス風)。</summary>
    Bleach,

    /// <summary>モノクロ。彩度 0。</summary>
    Monochrome,
}

/// <summary>
/// **カラーグレーディング**。Day 38 のもう一人の主役。
///
/// Day 31 でトーンマッピングを入れたとき、「0〜∞ をどう畳むか」は
/// <b>好みであって物理ではない</b>と書いた。グレーディングはその続きで、
/// <b>畳む前の絵をどう味付けするか</b>を受け持つ。
///
/// <para>
/// 映画の現像(カラーコレクション)から来た言葉で、やることは3つに集約できる。
/// <list type="number">
/// <item><b>白をどこに置くか</b>(ホワイトバランス。<see cref="Temperature"/> / <see cref="Tint"/>)</item>
/// <item><b>明暗の傾きをどうするか</b>(コントラスト。<see cref="Contrast"/>)</item>
/// <item><b>色の濃さをどうするか</b>(彩度。<see cref="Saturation"/>)</item>
/// </list>
/// 実際の映画用ツールにはこの何十倍もつまみがあるが、
/// <b>3つで絵の印象のほとんどが決まる</b>ので、まずここまでを入れる。
/// </para>
///
/// <para>
/// <b>掛ける場所はトーンマップの前</b>(<c>composite.frag</c>)。
/// トーンマップは「フィルムの特性曲線」に当たるものなので、
/// 味付けはフィルムに写す前——つまり<b>まだ物理的な明るさである側</b>——で行う。
/// 後ろに置くと、すでに 0〜1 に畳まれた絵をいじることになり、
/// コントラストを上げた瞬間に白飛びと黒潰れが同時に起きる。
/// Unreal も Unity(HDRP)も、この順序は同じ。
/// </para>
///
/// <para>
/// <b>1パスも増えない</b>のがこの機能の性格。合成パス(<c>composite.frag</c>)の中に
/// 数行足すだけで済む——すでに全画素を1回触っているところに便乗する。
/// FXAA が丸ごと1パス増えるのと対照的で、
/// <b>「後処理を足す」と言っても代償はまるで違う</b>という例になっている。
/// </para>
///
/// <para>
/// <b>シーンを知らない</b>のは <see cref="PostProcess"/> / <see cref="Ssao"/> と同じ方針。
/// ここが持っているのは数字だけで、GL の資源を1つも握っていない
/// (だから <see cref="IDisposable"/> でもない)。
/// </para>
/// </summary>
internal sealed class ColorGrade
{
    /// <summary>色温度のつまみの候補(Shift+F10)。Unity と同じ -100〜100 の目盛り。</summary>
    public static readonly float[] TemperatureSteps = [-40.0f, -20.0f, 0.0f, 20.0f, 40.0f];

    /// <summary>コントラストの候補(Shift+F8)。1.0 が素通し。</summary>
    public static readonly float[] ContrastSteps = [0.8f, 1.0f, 1.2f, 1.4f];

    /// <summary>彩度の候補(Shift+F9)。0 でモノクロ、1 が素通し。</summary>
    public static readonly float[] SaturationSteps = [0.0f, 0.5f, 1.0f, 1.3f];

    /// <summary>
    /// コントラストを回す中心(リニアな明るさ)。**18% グレー**。
    ///
    /// 写真で「標準反射率」とされてきた値で、露出計はこれが適正露出になるよう作られている。
    /// <b>コントラストは「この明るさを動かさずに傾きだけ変える」操作</b>なので、
    /// 中心をどこに置くかで絵の印象がまるで変わる。
    /// 0.5 を中心にすると、コントラストを上げただけで全体が暗く沈む。
    /// </summary>
    public const float MiddleGrey = 0.18f;

    /// <summary>D65(昼光。sRGB の基準白)の x 色度。</summary>
    private const float D65X = 0.31271f;

    /// <summary>グレーディングを掛けるか(Shift+F6)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 色温度(-100〜100)。**正が暖色**(白熱灯の側)、負が寒色(曇天の側)。
    ///
    /// 「暖かい絵にしたいから赤を足す」ではなく、
    /// <b>「何色の光の下で撮ったことにするか」を決める</b>のがホワイトバランス。
    /// 撮影時の光源が電球(色温度が低い=赤い)なら、
    /// 白を白に写すためにカメラは青を持ち上げる——だから
    /// <b>つまみを暖色へ回すと絵は暖色になる</b>(補正の逆が絵に出る)。
    /// </summary>
    public float Temperature { get; set; }

    /// <summary>色合い(-100〜100)。正が紫、負が緑。**蛍光灯の緑かぶりを抜く**ためのつまみ。</summary>
    public float Tint { get; set; }

    /// <summary>コントラスト(Shift+F8)。1.0 が素通し。</summary>
    public float Contrast { get; set; } = 1.0f;

    /// <summary>彩度(Shift+F9)。0 でモノクロ、1 が素通し、1 より大きいと派手になる。</summary>
    public float Saturation { get; set; } = 1.0f;

    /// <summary>
    /// 色フィルタ。**レンズの前に色ガラスを置くのと同じ**で、単なる掛け算。
    ///
    /// ホワイトバランスと違って白点を動かさないので、
    /// <b>白いものにも色が付く</b>。夕日の赤みのように
    /// 「空気そのものが色を持つ」表現向け。
    /// </summary>
    public Vector3 Filter { get; set; } = Vector3.One;

    /// <summary>いま選ばれている下敷き(Shift+F7)。HUD の表示用。</summary>
    public GradePreset Preset { get; private set; } = GradePreset.Neutral;

    /// <summary>
    /// **素通しの設定か**。自己チェックが「恒等なら絵が変わらない」を確かめるために使う。
    ///
    /// つまみが全部初期値なら、グレーディングの有無で1ビットも変わらないはず——
    /// これが成り立たないなら、どこかの式が中立点を外している。
    /// </summary>
    public bool IsIdentity =>
        Temperature == 0.0f
        && Tint == 0.0f
        && Contrast == 1.0f
        && Saturation == 1.0f
        && Filter == Vector3.One;

    /// <summary>いま効いている白の増幅率。HUD と自己チェックが見る。</summary>
    public Vector3 WhiteBalance => ComputeWhiteBalance(Temperature, Tint);

    /// <summary>下敷きを当てる(Shift+F7)。**個別のつまみは全部上書きされる**。</summary>
    public void ApplyPreset(GradePreset preset)
    {
        Preset = preset;

        (Temperature, Tint, Contrast, Saturation, Filter) = preset switch
        {
            GradePreset.Sunset => (25.0f, 5.0f, 1.15f, 1.10f, new Vector3(1.05f, 0.98f, 0.92f)),
            GradePreset.Moonlight => (-35.0f, -5.0f, 1.05f, 0.75f, new Vector3(0.88f, 0.96f, 1.14f)),
            GradePreset.Bleach => (5.0f, 0.0f, 1.35f, 0.45f, Vector3.One),
            GradePreset.Monochrome => (0.0f, 0.0f, 1.20f, 0.00f, Vector3.One),
            _ => (0.0f, 0.0f, 1.00f, 1.00f, Vector3.One),
        };
    }

    /// <summary>つまみを手で動かしたときに呼ぶ。**下敷きの名前を外す**だけ。</summary>
    public void MarkCustom()
    {
        if (!IsIdentity)
        {
            Preset = GradePreset.Neutral;
        }
    }

    /// <summary>合成シェーダへ数字を送る。<see cref="Ssao.Apply"/> と同じ形。</summary>
    public void Apply(Shader shader)
    {
        shader.SetInt("uGradeEnabled", Enabled ? 1 : 0);
        shader.SetVector3("uWhiteBalance", WhiteBalance);
        shader.SetVector3("uColorFilter", Filter);
        shader.SetFloat("uContrast", Contrast);
        shader.SetFloat("uSaturation", Saturation);
    }

    /// <summary>
    /// **色温度・色合いから、LMS 空間での増幅率を出す**。今日いちばん理屈の濃いところ。
    ///
    /// <para>
    /// ホワイトバランスは「この色を白とみなす」という宣言で、
    /// 直感的には <b>RGB を適当な比率で掛ければよさそう</b>に思える。
    /// ところがそれをやると色相が引きずられて、肌色が緑や紫に転ぶ。
    /// </para>
    ///
    /// <para>
    /// 人の目の順応(白いものが白く見え続けること)は、
    /// RGB ではなく<b>3種類の錐体それぞれの感度が独立に変わる</b>ことで起きている、
    /// というのが<b>フォン・クリースの順応則</b>(1902)。
    /// 錐体の応答を並べた空間が <b>LMS</b>(Long / Medium / Short 波長)で、
    /// そこで各成分に定数を掛けるのが、いちばん人の見え方に近い白の付け替えになる。
    /// </para>
    ///
    /// <list type="number">
    /// <item>つまみから、目標の白点の色度 (x, y) を作る</item>
    /// <item>その色度を LMS へ変換する</item>
    /// <item><b>基準の白(色温度 0 のときの白)</b>との比を取る。これが増幅率</item>
    /// </list>
    ///
    /// <para>
    /// 実際に掛けるのはシェーダ側(<c>composite.frag</c> の <c>WhiteBalance</c>)。
    /// ここで計算するのは<b>1フレームに1回で済む定数</b>なので CPU に置く——
    /// 画素ごとに変わらないものをシェーダで毎回計算するのは、
    /// 200 万画素ぶんの無駄になる。
    /// </para>
    /// </summary>
    private static Vector3 ComputeWhiteBalance(float temperature, float tint)
    {
        // 65 で割るのは Unity(HDRP)と同じ目盛り合わせ。
        // -100〜100 のつまみが、だいたい ±1.5 の内側に収まる。
        float t1 = temperature / 65.0f;
        float t2 = tint / 65.0f;

        // **黒体放射の軌跡に沿って白点を動かす**。
        //
        // 色温度を上げ下げするというのは、本来は
        // 「何 K の黒体が出す光を白とみなすか」を変えることで、
        // その白点は CIE xy 平面上の1本の曲線(プランク軌跡)に載っている。
        // ここではその曲線を x の1次式で近似し、暖色側と寒色側で傾きを変えている
        // (寒色側のほうが軌跡の動きが大きいので係数が倍)。
        float x = D65X - (t1 * (t1 < 0.0f ? 0.10f : 0.05f));

        // 色合いは軌跡から**垂直に**外す。緑↔紫の方向で、色温度とは独立に効く。
        float y = StandardIlluminantY(x) + (t2 * 0.05f);

        Vector3 target = CieXyToLms(x, y);

        // **基準の白も同じ式から作る**。
        //
        // ここで D65 の LMS を定数表から持ってくると、上の近似式との
        // わずかなずれが残り、**色温度 0 でも絵が 0.1% ほど動く**。
        // 絵には出ないが、「素通しなら1ビットも変わらない」という
        // 自己チェック(<see cref="IsIdentity"/>)が書けなくなる。
        Vector3 reference = CieXyToLms(D65X, StandardIlluminantY(D65X));

        return new Vector3(
            reference.X / target.X,
            reference.Y / target.Y,
            reference.Z / target.Z);
    }

    /// <summary>標準の光の軌跡を x から y へ。**プランク軌跡の2次近似**。</summary>
    private static float StandardIlluminantY(float x) =>
        (2.87f * x) - (3.0f * x * x) - 0.275509f;

    /// <summary>
    /// CIE xy 色度(明るさ Y = 1)を LMS へ。
    ///
    /// xyY → XYZ は定義どおりの割り算で、XYZ → LMS は
    /// <b>Hunt-Pointer-Estevez 行列</b>。
    /// 錐体の分光感度を測って作られた行列なので、**測定値であって設計値ではない**。
    /// </summary>
    private static Vector3 CieXyToLms(float x, float y)
    {
        const float Y = 1.0f;
        float bigX = Y * x / y;
        float bigZ = Y * (1.0f - x - y) / y;

        return new Vector3(
            (0.7328f * bigX) + (0.4296f * Y) - (0.1624f * bigZ),
            (-0.7036f * bigX) + (1.6975f * Y) + (0.0061f * bigZ),
            (0.0030f * bigX) + (0.0136f * Y) + (0.9834f * bigZ));
    }
}
