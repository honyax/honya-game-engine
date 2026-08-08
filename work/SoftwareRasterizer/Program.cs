namespace SoftwareRasterizer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // ここから Day 1 の写経を始める。
        //
        // 手順は docs/plans/Day01.md を参照。作るものは2つ:
        //   Framebuffer.cs … ピクセル配列。Width / Height / Pixels と Rgb / Clear / SetPixel / FillRect
        //   GameWindow.cs  … Form派生。ゲームループと、フレームバッファの画面転送
        //
        // 詰まったら reference/Day01 を見る。差分の確認は
        //   git diff --no-index reference/Day01 work/SoftwareRasterizer
        //
        // 現状は何もしないので、実行してもウィンドウが出ずに即終了する。
        // ビルドと実行の経路が通っていることの確認だけができる状態。
    }
}
