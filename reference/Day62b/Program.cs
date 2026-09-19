namespace HardwareRayTracer;

/// <summary>
/// エントリポイント。<b>教養編 Day 62b: 加速構造(BLAS/TLAS)と ray query</b>。
///
/// <para>
/// Day 62a で Vulkan を立ち上げた。今日はそこに<b>ハードウェアレイトレーシングを足す</b>——
/// といっても、足すのは3つだけ。
/// </para>
/// <list type="number">
/// <item>拡張3つと機能3つを有効にする(<c>Vulkan/VulkanDevice.cs</c> に 30 行)</item>
/// <item><b>BLAS と TLAS を建てる</b>(<c>Vulkan/AccelerationStructure.cs</c>。今日の主役)</item>
/// <item>シェーダの <c>traceScene</c> を <c>rayQueryEXT</c> に差し替える(<c>shaders/trace.comp</c>)</item>
/// </list>
/// <para>
/// <b>絵は1画素も変わらない</b>。変わるのは「どう探すか」だけで、
/// レンダリングの理屈(光線の作り方、材質、影、トーンマップ)には一切触れていない。
/// <c>B</c> キーで総当たりと ray query を行き来しながら、
/// <b>球を 5000 個に増やしても ray query 側の時間がほとんど動かない</b>のを見る。
/// </para>
/// <para>
/// 「RT コアが何を肩代わりするか」の答えは <c>shaders/trace.comp</c> の
/// <c>traceScene</c> にある。肩代わりするのは<b>木をたどること・箱との交差・近い順の管理</b>で、
/// <b>球の式は肩代わりしてくれない</b>(三角形なら全部やってくれる)。
/// </para>
/// </summary>
internal static class Program
{
    // Day 59〜61 と同じ大きさ。CPU 版・OpenGL 版と並べて比べられるようにしておく。
    private const int Width = 960;
    private const int Height = 540;

    /// <summary>
    /// [STAThread] は WinForms の決まりごと(Day 1 と同じ)。
    ///
    /// <para>
    /// Vulkan のキューは、OpenGL のコンテキストと違って<b>スレッドに貼り付かない</b>。
    /// 別のスレッドから submit してもよい(同時に触らなければよい)。
    /// それでも UI スレッドから投げる——1枚が数 ms で終わるので、分ける値打ちが無い。
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
