namespace MeshletRenderer;

/// <summary>
/// エントリポイント。<b>教養編 Day 65b: GPU 駆動レンダリング(2) — 深度を畳んで Hi-Z を作る</b>。
///
/// <para>
/// Day 65a で「描く命令の引数を GPU が書く」骨格を作ったが、選ぶのに使ったのは CPU でも分かる情報(体の置き場所と視錐台)だけだった。
/// Day 65c では<b>CPU が持っていない情報——深度——で、隠れたものを捨てる</b>。今日はその下ごしらえで、
/// 描き終わった深度を縦横半分ずつ畳み、「その範囲でいちばん奥の深度」を残した画像の山(Hi-Z)を毎フレーム作る。
/// </para>
/// <para>
/// <c>Z</c> で Hi-Z の段を 0 から順に画面に出すと、段を上がるたびに絵が粗いモザイクになり、
/// 体の輪郭が「奥(暗い)」の側へ膨らんでいくのが見える。「いちばん奥」を残しているから。
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
