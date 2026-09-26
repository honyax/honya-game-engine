namespace GaussianSplatting;

/// <summary>
/// エントリポイント。<b>教養編 Day 67: 3D Gaussian Splatting 簡易ビューア</b>。
///
/// <para>
/// Day 1〜56 の絵は、すべて<b>三角形</b>でできていた。面があり、法線があり、光を当てて色を計算し、
/// 深度バッファでいちばん手前の面だけを残した。今日の絵には、そのどれも無い。
/// </para>
/// <list type="bullet">
/// <item>形は<b>半透明のぼやけた楕円体(3D ガウス分布)</b>が何十万個も重なったもの。面も法線も無い</item>
/// <item>色は<b>写真に写っていた色そのもの</b>(光は計算しない。見る向きで変わるぶんは球面調和で持つ)</item>
/// <item>深度バッファは使わない。<b>毎フレーム全部を奥から順に並べ替えて</b>、半透明のまま重ねる</item>
/// </list>
/// <para>
/// 楕円体1個を画面に写すと楕円になる(要点2)。それを四角形1枚に載せ、画素シェーダでガウス関数を計算して塗る。
/// 起動すると自前で作った場面(楕円体3つ)が出る。<c>4</c> で学習済みの .ply(<c>assets/splats/</c>)を読む。
/// </para>
/// </summary>
internal static class Program
{
    // Day 59〜66 の Labs と同じ大きさ。
    private const int Width = 960;
    private const int Height = 540;

    /// <summary>
    /// [STAThread] は WinForms の決まりごと(Day 1 と同じ)。
    /// OpenGL のコンテキストは作ったスレッドに貼り付くので、UI スレッドで作って UI スレッドからだけ触る(Day 61 と同じ)。
    /// 引数に .ply のパスを渡すと、場面4でそれを読む。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        using var window = new ViewerWindow(Width, Height, args.Length > 0 ? args[0] : null);
        window.Run();
    }
}
