namespace MeshletRenderer;

/// <summary>
/// エントリポイント。<b>教養編 Day 64a: メッシュシェーダ</b>。
///
/// <para>
/// Day 63a(ジオメトリシェーダ)・63b(テッセレーション)で、GPU の頂点処理の段に
/// 「図形を作り直す段」と「割る段」を挟んだ。どちらも<b>頂点シェーダの後ろに足す</b>形で、
/// 入口(索引を読んで頂点を1つずつ配る回路)はそのままだった。
/// </para>
/// <para>
/// メッシュシェーダは<b>入口ごと取り替える</b>。
/// </para>
/// <list type="table">
/// <item><term>頂点シェーダの道</term><description>
/// 索引 → [入力の回路] → 頂点シェーダ(1頂点ずつ)→ ラスタライザ</description></item>
/// <item><term>メッシュシェーダの道</term><description>
/// メッシュシェーダ(<b>32 人で小さなメッシュを1つ丸ごと</b>)→ ラスタライザ</description></item>
/// </list>
/// <para>
/// 小さなメッシュ(<b>メッシュレット</b>、頂点 64・三角形 124 まで)は起動時に CPU で切っておく。
/// <c>B</c> キーで2つの道を行き来して、<b>絵が同じこと</b>と<b>何が何回走ったか</b>を見比べる。
/// Day 64b では、メッシュの道の手前にもう1段(タスクシェーダ)を置いて、
/// 要らないメッシュレットを GPU の上で丸ごと捨てる。
/// </para>
/// </summary>
internal static class Program
{
    // Day 59〜62 と同じ大きさ。
    private const int Width = 960;
    private const int Height = 540;

    /// <summary>
    /// [STAThread] は WinForms の決まりごと(Day 1 と同じ)。
    /// Vulkan のキューはスレッドに貼り付かないが、1枚が数 ms で終わるので UI スレッドから投げる(Day 62 と同じ)。
    /// </summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var window = new ViewerWindow(Width, Height);
        window.Run();
    }
}
