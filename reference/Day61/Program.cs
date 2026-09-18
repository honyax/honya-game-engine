namespace CpuRayTracer;

/// <summary>
/// エントリポイント。**教養編 Day 61: GPU パストレーサ**。
///
/// Day 60 で書いたパストレーサを、**1行も式を変えずに**コンピュートシェーダへ移す。
/// 場面も、つまみも、画面へ出す道筋も Day 60 のまま。**変わるのは計算する場所だけ**で、
/// G キーで CPU 版と GPU 版を行き来しながら、同じ絵が 20〜30 倍の速さで出るのを見る。
///
/// 移すときに直面するのは、レイトレーシングの理屈ではなく**言語と実行モデルの制約**。
///   参照が無い       → 形の配列と材質の配列に分け、番号でつなぐ(Gpu/GpuScene.cs)
///   64 ビット整数が無い → 乱数器を 32 ビットの PCG に替える(Sampling/Rng.cs)
///   再帰が無い       → Day 60 の時点でループにしてあるので、そのまま写せる
///   例外も文字列も無い → 1画素を追う機能は CPU に残す
///
/// そして**移したものが本当に同じかを、絵ではなく数で確かめる**のが今日の後半(自己チェック19〜23)。
/// 乱数を CPU と GPU で1ビットまで揃えてあるので、
/// 「同じ画素の1サンプル目が同じ色になる」で突き合わせられる。
/// </summary>
internal static class Program
{
    // 計算する画像の解像度。Day 60 と同じにしてある(そうしないと CPU 版と GPU 版を並べて比べられない)。
    // GPU では 1パスが 0.7〜4ms なので、この大きさなら 4096 サンプルが 20 秒ほどで終わる。
    private const int Width = 960;
    private const int Height = 540;

    /// <summary>
    /// [STAThread] は WinForms の決まりごと(Day 1 と同じ)。
    ///
    /// <para>
    /// Day 60 までは「光線を追うのは別スレッド、UI スレッドは表示と入力だけ」だったが、
    /// GPU 版では<b>UI スレッドが GPU に仕事を投げる</b>(<see cref="GpuRenderer"/>)。
    /// OpenGL のコンテキストは作ったスレッドに貼り付くので、こうするのがいちばん素直で、
    /// 投げた仕事は GPU が勝手に進めるので UI が止まることもない。
    /// </para>
    /// </summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var window = new ViewerWindow(Width, Height);
        window.Run();
    }
}
