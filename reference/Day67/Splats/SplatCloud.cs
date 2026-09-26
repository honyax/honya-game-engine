using System.Numerics;

namespace GaussianSplatting;

/// <summary>
/// 楕円体(3D ガウス)の集まり。<b>今日の「形」のすべて</b>(要点1)。
///
/// <para>
/// 1個が持つのは次の5つだけ。<b>頂点も、面も、法線も、材質も無い</b>。
/// </para>
/// <list type="table">
/// <item><term>位置</term><description>楕円体の中心(ガウス分布の平均)</description></item>
/// <item><term>大きさ</term><description>楕円体の3つの軸の長さ(標準偏差 σ。メートル)</description></item>
/// <item><term>回転</term><description>その3つの軸の向き(四元数)</description></item>
/// <item><term>不透明度</term><description>中心での不透明さ(0〜1)。中心から離れるとガウス関数で薄くなる</description></item>
/// <item><term>色</term><description>見る向きごとの色(球面調和の係数。次数 0 なら向きによらない1色)</description></item>
/// </list>
///
/// <para>
/// 配列を種類ごとに分けて持つ(1個を1つの struct にしない)。学習済みのシーンは数十万〜数百万個あり、
/// 並べ替え(<see cref="DepthSorter"/>)は位置しか読まないので、位置だけが連続して並んでいるほうがキャッシュに優しい。
/// GPU へ送るときも、色(<see cref="Sh"/>)は位置と別のバッファにする。
/// </para>
/// <para>
/// 値は<b>使う形(活性化した後)</b>で持つ。.ply の中身は学習の都合で別の形(大きさは log、不透明度はシグモイドの前)になっているので、
/// 読むときに直す(<see cref="PlyFormat"/>)。
/// </para>
/// </summary>
internal sealed class SplatCloud
{
    public SplatCloud(string name, int count, int shDegree)
    {
        if (shDegree is < 0 or > SphericalHarmonics.MaxDegree)
        {
            throw new ArgumentOutOfRangeException(nameof(shDegree));
        }

        Name = name;
        Count = count;
        ShDegree = shDegree;
        Positions = new Vector3[count];
        Scales = new Vector3[count];
        Rotations = new Quaternion[count];
        Opacities = new float[count];
        Sh = new float[count * ShFloatsPerSplat];
    }

    public string Name { get; }

    public int Count { get; }

    /// <summary>
    /// 色が持っている球面調和の次数(0〜3)。係数の数は (次数 + 1)² 個で、次数 3 なら 16 個 × RGB。
    /// </summary>
    public int ShDegree { get; }

    /// <summary>1個あたりの係数の個数((次数 + 1)²)。</summary>
    public int ShCoefficients => SphericalHarmonics.CoefficientCount(ShDegree);

    /// <summary>1個あたりの float の数。係数ごとに RGB の3つ。</summary>
    public int ShFloatsPerSplat => ShCoefficients * 3;

    public Vector3[] Positions { get; }

    /// <summary>楕円体の3つの軸の標準偏差 σ(メートル)。回転を掛ける前の、ローカルの x・y・z 軸の長さ。</summary>
    public Vector3[] Scales { get; }

    /// <summary>長さ 1 の四元数。読み込むときに正規化してある。</summary>
    public Quaternion[] Rotations { get; }

    /// <summary>中心での不透明度(0〜1)。</summary>
    public float[] Opacities { get; }

    /// <summary>
    /// 球面調和の係数。<c>Sh[i * ShFloatsPerSplat + k * 3 + c]</c> が、i 個目の k 番目の係数の色 c(0=R, 1=G, 2=B)。
    /// 色は <c>0.5 + Σ 係数 × 基底(向き)</c>(<see cref="SphericalHarmonics.Evaluate"/>)。
    /// </summary>
    public float[] Sh { get; }

    /// <summary>
    /// 起動したときのカメラ。自前の場面は作るときに決め、.ply は中身から見当をつける(<see cref="PlyFormat"/>)。
    /// </summary>
    public FlyView DefaultView { get; set; } = FlyView.Default;

    /// <summary>
    /// ワールドの「上」。自前の場面は +Y。<b>学習済みのシーンは COLMAP の座標なので、たいてい −Y が上</b>
    /// (カメラの y 軸が下向きの流儀で、最初の写真のカメラがワールドの軸を決めるため)。
    /// </summary>
    public Vector3 WorldUp { get; set; } = Vector3.UnitY;

    /// <summary>空の色。画面の何も塗られなかった部分(残りの透明度)に重ねる。学習済みのシーンは黒で学習されている。</summary>
    public Vector3 Background { get; set; } = Vector3.Zero;

    /// <summary>
    /// 読み込みにかかった時間(ミリ秒)。自前の場面は作るのにかかった時間。
    /// </summary>
    public double LoadMilliseconds { get; set; }

    /// <summary>i 個目の色の、k 番目の係数(RGB)。</summary>
    public Vector3 GetSh(int i, int k)
    {
        int o = i * ShFloatsPerSplat + k * 3;
        return new Vector3(Sh[o], Sh[o + 1], Sh[o + 2]);
    }

    public void SetSh(int i, int k, Vector3 value)
    {
        int o = i * ShFloatsPerSplat + k * 3;
        Sh[o] = value.X;
        Sh[o + 1] = value.Y;
        Sh[o + 2] = value.Z;
    }

    /// <summary>
    /// i 個目を、向き <paramref name="direction"/>(カメラ → 楕円体、長さ 1)から見た色。
    /// <paramref name="degree"/> は使う次数の上限で、持っている次数より大きければ持っている次数まで使う。
    /// GPU の頂点シェーダ(<c>shaders/splat.vert</c> の <c>ShColor</c>)と同じ計算。
    /// </summary>
    public Vector3 ColorToward(int i, Vector3 direction, int degree)
    {
        int used = Math.Min(degree, ShDegree);
        return SphericalHarmonics.Evaluate(Sh.AsSpan(i * ShFloatsPerSplat, ShFloatsPerSplat), used, direction);
    }

    /// <summary>
    /// i 個目の3D の共分散(<see cref="Covariance3"/>)。<b>形を表す5つのうち、大きさと回転をまとめたもの</b>(要点1)。
    /// GPU には毎回これを送る(大きさと回転のまま送ると、全画素……ではなく全頂点で同じ計算をやり直すことになる)。
    /// </summary>
    public Covariance3 Covariance(int i) => Covariance3.FromScaleRotation(Scales[i], Rotations[i]);
}
