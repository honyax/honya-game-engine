namespace SoftwareRasterizer;

/// <summary>
/// Phase 1(Day 2〜10)の写経用プロジェクトのエントリポイント。
///
/// ==== 最初にやること ====
///
/// このプロジェクトは Day 1 の続きから始まる。まず Phase 0 で書いたコードを持ってくる。
///
///   work/Phase0_Framebuffer/Framebuffer.cs  →  このフォルダへコピー
///   work/Phase0_Framebuffer/GameWindow.cs   →  このフォルダへコピー
///
/// コピーしたら下の Main のコメントを外せば、Day 1 と同じ絵が出るところから再開できる。
/// (もう一度手で書き直したい場合は、写経し直してももちろんよい)
///
/// ==== この先の進め方 ====
///
/// Day 2 以降はこのプロジェクトを育て続ける。フォルダは Day ごとに分けない。
/// 区切りは git のタグかコミット(day02, day03 …)で残す。
///
/// 各Dayの手順は docs/plans/DayXX.md を読む。リファレンスとの差分確認は:
///
///   git diff --no-index reference/Day02 work/Phase1_SoftwareRasterizer
///
/// 名前空間を SoftwareRasterizer で固定してあるので、上の差分には
/// その日の実装差分だけが出る(namespace 行が全ファイルに乗らない)。
///
/// ==== 実行 ====
///
///   dotnet run --project work/Phase1_SoftwareRasterizer -c Release
///
/// FPS を見るときは必ず -c Release を付けること。
/// Debug ビルドはソフトウェアラスタライザだと目に見えて遅い。
/// </summary>
internal static class Program
{
    // フレームバッファの解像度。Day 1 と同じ 640x480 から始める。
    // ソフトウェアラスタライザでは解像度がそのままCPU負荷に直結する
    // (ピクセル数は幅と高さの積なので、縦横2倍にすると負荷は4倍)。
    private const int Width = 640;

    private const int Height = 480;

    /// <summary>
    /// エントリポイント。
    ///
    /// [STAThread] は必須。WinFormsはSTA(シングルスレッドアパートメント)前提のCOMを
    /// 内部で使っており、これが無いとクリップボードやファイルダイアログ等で例外になる。
    /// </summary>
    [STAThread]
    private static void Main()
    {
        // WinFormsのソースジェネレータが生成するメソッド。
        // ビジュアルスタイルの有効化・既定フォント・DPIモード設定をまとめて行う。
        // DPIモードは csproj の ApplicationHighDpiMode から来ている。
        ApplicationConfiguration.Initialize();

        // TODO: Framebuffer.cs と GameWindow.cs をこのフォルダへ用意したら、
        //       下の2行のコメントを外す。
        //
        // using var window = new GameWindow(Width, Height);
        // window.Run();

        // ↑ の2行を有効にするまでの仮の表示。
        // 何も出ないと「ビルドは通ったが動いていない」のか区別が付かないので、
        // 最初だけメッセージを出しておく。有効化したらこのブロックごと消してよい。
        MessageBox.Show(
            $"Phase 1 の写経用プロジェクトです({Width}x{Height})。\n\n"
            + "Program.cs のコメントに従って Framebuffer.cs と GameWindow.cs を\n"
            + "用意し、Main の TODO 部分を有効にしてください。",
            "Phase1_SoftwareRasterizer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
