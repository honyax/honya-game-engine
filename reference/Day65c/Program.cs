namespace MeshletRenderer;

/// <summary>
/// エントリポイント。<b>教養編 Day 65c: GPU 駆動レンダリング(3) — Hi-Z で隠れたものを捨てる2パス</b>。
///
/// <para>
/// Day 65a で「描く命令の引数を GPU が書く」骨格を、Day 65b で深度の山(Hi-Z)を作った。
/// 今日は2つを組み合わせて、<b>CPU が持っていない情報——深度——で選ぶ</b>。
/// </para>
/// <list type="number">
/// <item>早いパス: 前のフレームで見えていたものを描く(見えた印は GPU の上に残してある)</item>
/// <item>その深度から Hi-Z を作る(Day 65b)</item>
/// <item>遅いパス: 全部を Hi-Z と比べ直し、確実に隠れているものは捨て、早いパスで描いていない見えるものを描き足す</item>
/// </list>
/// <para>
/// <c>V</c> で低い視点にすると、手前の体の管が奥の体を大きく隠し、メッシュレットの半分近くが「隠れている」で捨てられる。
/// ただし<b>体が丸ごと隠れることはほとんど無い</b>(トーラスノットは穴だらけなので)。
/// 細かい単位(メッシュレット)で隠れを調べるのが効く——Nanite がクラスタ単位で同じことをしている理由。
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
