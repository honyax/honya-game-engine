namespace MeshletRenderer;

/// <summary>
/// エントリポイント。<b>教養編 Day 64b: タスクシェーダと GPU カリング</b>。
///
/// <para>
/// Day 64a でメッシュシェーダの道を作った。メッシュレットは描く前から小片に分かれているので、
/// <b>小片ごとに「描く必要があるか」を GPU の上で決められる</b>。それをするのが、メッシュシェーダの手前に置く
/// <b>タスクシェーダ</b>。
/// </para>
/// <list type="table">
/// <item><term>Day 64a</term><description>
/// メッシュシェーダ(全部のメッシュレット)→ ラスタライザ(三角形1枚ずつ裏を捨てる)</description></item>
/// <item><term>Day 64b</term><description>
/// タスクシェーダ(<b>メッシュレットごとに</b>画面の外と裏を捨てる)→ メッシュシェーダ(生き残りだけ)→ ラスタライザ</description></item>
/// </list>
/// <para>
/// Day 63b のテッセレーションは「1パッチを何枚に割るか」を<b>固定機能の回路</b>が決めた。
/// タスクシェーダは「何組のメッシュシェーダを立ち上げるか」を<b>シェーダが自分で</b>決める。
/// 増やすことも(LOD を上げる)、減らすことも(捨てる)、0 にすることもできる。
/// </para>
/// <para>
/// <c>C</c> でカリングの種類を、<c>O</c> でメッシュレットの切り方を変え、<c>F</c> でカリングの目を止めて回り込むと、
/// 何が捨てられたかが穴として見える。
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
