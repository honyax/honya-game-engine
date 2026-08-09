namespace Framebuffer;

internal static class Program
{
    // フレームバッファの解像度。ロードマップのマイルストーンに合わせて 640x480。
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
        // DPIモードは Day01.csproj の ApplicationHighDpiMode から来ている。
        ApplicationConfiguration.Initialize();

        using var window = new GameWindow(Width, Height);
        window.Run();
    }
}
