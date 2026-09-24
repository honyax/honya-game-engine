namespace MeshletRenderer;

/// <summary>
/// エントリポイント。<b>教養編 Day 65a: GPU 駆動レンダリング(indirect の描く命令と、体ごとのカリング)</b>。
///
/// <para>
/// Day 64b のタスクシェーダは、描く命令の<b>中で</b>「メッシュシェーダを何組立ち上げるか」を GPU に決めさせた。
/// ただ、描く命令そのもの(何体ぶん描くか)は、まだ CPU が数を書き込んでいた。
/// 今日はその数も GPU に書かせる。<b>描く前にコンピュートシェーダが体を選び、描く命令の引数をバッファに書く</b>。
/// CPU は「引数はあのバッファに書いてある」と言うだけ(indirect の描く命令)。
/// </para>
/// <list type="table">
/// <item><term>Day 64b</term><description>
/// CPU が「256 体描け」→ タスクシェーダ(メッシュレットごとに捨てる)→ メッシュシェーダ → ラスタライザ</description></item>
/// <item><term>Day 65a</term><description>
/// コンピュートシェーダ(<b>体ごとに</b>捨て、引数を書く)→ CPU は「引数を読んで描け」→ タスクシェーダ → ...</description></item>
/// </list>
/// <para>
/// <c>G</c> で体の選び方を GPU / CPU / 選ばない と回すと、<b>絵も数も同じなのに、答えを知っているのが誰か</b>だけが変わる。
/// それが何の役に立つのかは Day 65b・65c(深度の山 Hi-Z を作り、前のフレームの深度で隠れた体を捨てる)で分かる。
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
