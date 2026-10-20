// ============================================================
//  発表用の動画を撮るための差し込み(WinForms の Day 10 用)
//
//  record.ps1 が reference/Day10 の「コピー」に Recorder.Common.cs と一緒に置いてビルドする。
//  reference 本体には入れない(教材の答えを濁さないため)。
//
//  Day 10 は Application.DoEvents() を回す自前のループで、Bitmap _backBuffer に描いて画面へ転送している。
//  裏のスレッドから Form.Invoke で頼むと、頼んだ処理はループの DoEvents の中で走る。
//  つまり**フレームの合間**に _backBuffer を写すことになり、描きかけの絵を読まない。
// ============================================================
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PresentationRecording;

internal static class WinFormsRecorder
{
    private static Clip _clip = null!;
    private static KeyScript _keys = null!;
    private static FrameSink _sink = null!;

    [ModuleInitializer]
    internal static void Init()
    {
        Clip? clip = Clip.FromEnvironment();
        if (clip is null)
        {
            return;
        }

        _clip = clip;
        _keys = new KeyScript(clip.Keys);
        _sink = new FrameSink(clip);

        var thread = new Thread(Run) { IsBackground = true, Name = "PresentationRecorder" };
        thread.Start();
    }

    private static void Run()
    {
        // 窓が出て、裏の絵(_backBuffer)を持つまで待つ
        Form form;
        Bitmap bitmap;
        while (true)
        {
            if (Application.OpenForms.Count > 0 && Application.OpenForms[0] is Form f && f.IsHandleCreated && f.Visible
                && f.GetType().GetField("_backBuffer", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(f) is Bitmap b)
            {
                form = f;
                bitmap = b;
                break;
            }

            Thread.Sleep(50);
        }

        nint hwnd = (nint)form.Invoke(() => form.Handle);
        Console.WriteLine($"[録画] 準備できました。{_clip.Warmup:F1} 秒待ってから {_clip.Seconds:F1} 秒撮ります → {_clip.Out}");

        int width = bitmap.Width;
        int height = bitmap.Height;

        // 出力の 16:9 に合わせて上下を切り、画素が見えるよう最近傍で拡大する(640x480 → 640x360 → 1280x720)
        string fit = $"crop=iw:iw*{_clip.Height}/{_clip.Width},scale={_clip.Width}:{_clip.Height}:flags=neighbor";

        var clock = Stopwatch.StartNew();
        while (!_sink.Finished)
        {
            double now = clock.Elapsed.TotalSeconds - _clip.Warmup;
            try
            {
                form.Invoke(() =>
                {
                    _keys.SendDue(hwnd, now);
                    if (now < 0.0)
                    {
                        return;
                    }

                    if (!_sink.Started)
                    {
                        _sink.Start(width, height, "bgr0", fit);
                    }

                    _sink.Tick(now, () => Copy(bitmap));
                });
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            Thread.Sleep(5);
        }

        form.BeginInvoke(form.Close);
    }

    private static byte[] Copy(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            // 32bpp なので1行の長さ(Stride)は幅 x 4 で詰まっている。並びは B, G, R, 未使用
            var pixels = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
