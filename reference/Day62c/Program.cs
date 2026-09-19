namespace HardwareRayTracer;

/// <summary>
/// エントリポイント。<b>教養編 Day 62c: レイトレーシングパイプラインと SBT</b>。
///
/// <para>
/// Day 62b で「探す仕事を RT コアに渡す」ところまで来た。今日変えるのは<b>速さではなく形</b>。
/// 同じ絵を、<b>もう1つの入口</b>から出す。
/// </para>
/// <list type="table">
/// <item><term>ray query(62b)</term><description>
/// コンピュートシェーダの<b>中で関数を呼ぶ</b>。1本のシェーダに全部入っている</description></item>
/// <item><term>RT パイプライン(62c)</term><description>
/// <b>シェーダを6本に割って</b>、どれを呼ぶかを SBT(表)に書く</description></item>
/// </list>
/// <para>
/// <b>Day 62b の <c>if (hit.kind == 1)</c> という1行が、今日は
/// <c>diffuse.rchit</c> と <c>metal.rchit</c> という2本のシェーダになる</b>。
/// 分岐がコードから消えて、代わりに「BLAS のジオメトリの番号」と「SBT の並び」が決める。
/// </para>
/// <para>
/// もう1つの見どころは<b>再帰</b>。コンピュート版では GPU に再帰が無いので
/// for ループと throughput の掛け算にしていたが、<c>metal.rchit</c> は
/// <b>自分を呼び直す</b>(ハードウェアがスタックを持っている)。
/// </para>
/// <para>
/// <c>B</c> キーで3つの探し方(総当たり / ray query / RT パイプライン)を行き来しながら、
/// <b>絵が変わらないこと</b>と<b>速さがほとんど変わらないこと</b>を見る。
/// 62b で 150 倍になったような派手な差は今日は出ない——
/// 同じ RT コアを、違う呼び方で使っているだけだから。
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
