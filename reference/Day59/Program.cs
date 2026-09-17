namespace CpuRayTracer;

/// <summary>
/// エントリポイント。**教養編 Day 59: CPU レイトレーサ(1)**。
///
/// 画素ごとに光線を1本飛ばし、当たった面から「影の光線」「反射の光線」「屈折の光線」を枝分かれさせて、
/// 画素の色を**光線の木**で決める(Whitted, 1980)。GPU は使わない。
///
/// Day 1〜56 のラスタライズとは向きが逆になる。
///   ラスタライズ … 三角形を1枚ずつ画面へ写し、覆った画素を塗る(物体 → 画素)
///   レイトレース … 画素から光線を出し、どの物体に当たるかを探す(画素 → 物体)
/// 向きを逆にしたおかげで、**「その点から光が見えるか」「その点に何が映るか」をもう1本の光線で聞ける**。
/// ラスタライズで影にシャドウマップ(Day 33)、映り込みに環境マップ(Day 36)が要ったのは、
/// この問いを直接聞く手段が無かったから。
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
