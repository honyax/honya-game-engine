namespace CpuRayTracer;

/// <summary>
/// エントリポイント。**教養編 Day 60: CPU レイトレーサ(2)**。
///
/// Day 59 は、面に当たるたびに光線を枝分かれさせて画素の色を決めた(Whitted の**光線の木**)。
/// 今日は**枝分かれをやめ、行き先を乱数で1つだけ引く**。1本ずつはノイズだらけだが、
/// 何千本も撃って平均すると**レンダリング方程式**(Kajiya, 1986)の答えに近づく——パストレーシング。
///
/// これで Day 59 の3つのごまかしが消える。
///   環境光の一定値 → 周りから跳ね返って来る光を本当に数える(色移り)
///   点光源のハイライト → 大きさのある光源が本当に鏡に映り、影の縁が本当にぼける
///   ガラスの真っ黒な影 → ガラスを通った光が床に集まる(集光)
/// </summary>
internal static class Program
{
    // 計算する画像の解像度。16:9 で、1パス(全画素に1本ずつ)が Release・11 スレッドで 15〜35ms に収まる大きさ
    // (Debug ビルドだと約8倍かかる)。
    // レイトレーサの手間は「画素の数 × 1画素の光線の数」なので、幅と高さを2倍にすると4倍重くなる。
    private const int Width = 960;
    private const int Height = 540;

    /// <summary>
    /// [STAThread] は WinForms の決まりごと(Day 1 と同じ)。
    /// 光線を追うのは別スレッド(<see cref="ProgressiveRenderer"/>)で、UI スレッドは表示と入力だけを受け持つ。
    /// </summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var window = new ViewerWindow(Width, Height);
        window.Run();
    }
}
