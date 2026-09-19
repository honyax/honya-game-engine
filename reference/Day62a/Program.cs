namespace HardwareRayTracer;

/// <summary>
/// エントリポイント。<b>教養編 Day 62a: Vulkan を立ち上げる</b>。
///
/// <para>
/// Day 62 のゴールは「ハードウェアレイトレーシング(BLAS/TLAS と RT コア)を理解する」だが、
/// そこへ行くには<b>まず Vulkan が動いていなければならない</b>。そして Vulkan を動かすのは
/// OpenGL とは比べものにならない量の儀式が要る——今日はそこだけをやる。
/// </para>
/// <para>
/// 今日書くものは、レイトレーシングとは何の関係も無い。
/// </para>
/// <list type="bullet">
/// <item>インスタンス・物理デバイス・論理デバイス(<c>Vulkan/VulkanDevice.cs</c>)</item>
/// <item>バッファとメモリ(<c>Vulkan/VulkanBuffer.cs</c>)</item>
/// <item>ストレージ画像とレイアウト(<c>Vulkan/VulkanImage.cs</c>)</item>
/// <item>ディスクリプタ・パイプライン・コマンド(<c>Vulkan/ComputeRenderer.cs</c>)</item>
/// <item>GLSL から SPIR-V への翻訳(<c>Vulkan/ShaderCompiler.cs</c>)</item>
/// </list>
/// <para>
/// <b>この5つは Day 62b でも 62c でも1行も変わらない</b>(62b で拡張が2つ増えるだけ)。
/// 今日いちばん長い日になるが、ここを越えると残りはレイトレーシングの話だけになる。
/// </para>
/// <para>
/// 絵のほうは Day 59 の Whitted 風レイトレーサをぐっと削ったもの(拡散 + 金属 + 硬い影)で、
/// 探し方は<b>総当たり</b>。1/2/3 キーで球を 8 / 120 / 720 個に増やすと素直に重くなる。
/// <b>その重さが Day 62b で消える</b>のを見るのが、今日の絵の役目。
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
    /// それでも今日は UI スレッドから投げる——1枚が数 ms で終わるので、分ける値打ちが無い。
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
