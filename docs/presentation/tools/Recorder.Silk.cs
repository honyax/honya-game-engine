// ============================================================
//  発表用の動画を撮るための差し込み(Silk.NET の Day 14 以降用)
//
//  record.ps1 が reference/DayXX の「コピー」に Recorder.Common.cs と一緒に置いてビルドする。
//  reference 本体には入れない(教材の答えを濁さないため)。
//
//  - [ModuleInitializer] は Main より前に1回だけ呼ばれるので、元のコードのどこからも呼ばなくて動く
//  - 環境変数 HONYA_RECORD が無ければ何もしない
//  - Program._window の Render に後から登録し、アプリが描き終わった直後の絵を glReadPixels で読む
// ============================================================
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace PresentationRecording;

internal static class SilkRecorder
{
    private static Clip _clip = null!;
    private static KeyScript _keys = null!;
    private static FrameSink _sink = null!;
    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static nint _hwnd;
    private static readonly Stopwatch Clock = new();
    private static int _frameWidth;
    private static int _frameHeight;
    private static bool _closing;

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

        // Main が窓を作り、OnRender を登録して Run に入るのを待つ。
        // Render への登録をそれより先にすると、アプリが描く前の絵を読んでしまう
        var thread = new Thread(WaitAndAttach) { IsBackground = true, Name = "PresentationRecorder" };
        thread.Start();
    }

    private static void WaitAndAttach()
    {
        Type program = typeof(SilkRecorder).Assembly.GetTypes().First(t => t.Name == "Program");
        FieldInfo windowField = program.GetField("_window", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException("Program._window が見つかりません");
        FieldInfo glField = program.GetField("_gl", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException("Program._gl が見つかりません");

        while (true)
        {
            if (windowField.GetValue(null) is IWindow window && window.IsInitialized && window.Time > 0.0
                && glField.GetValue(null) is GL gl)
            {
                _window = window;
                _gl = gl;
                break;
            }

            Thread.Sleep(50);
        }

        _hwnd = _window.Native?.Win32?.Hwnd ?? throw new InvalidOperationException("窓のハンドルが取れません");
        _window.Render += OnRender;
        Console.WriteLine($"[録画] 準備できました。{_clip.Warmup:F1} 秒待ってから {_clip.Seconds:F1} 秒撮ります → {_clip.Out}");
    }

    private static void OnRender(double deltaSeconds)
    {
        if (_sink.Finished)
        {
            if (!_closing)
            {
                _closing = true;
                _window.Close();
            }

            return;
        }

        if (!Clock.IsRunning)
        {
            Clock.Start();

            // 窓の縦横比が出力(16:9)と違うと、縮めたときに黒い帯が出る(Day 40 までは 960x640)。
            // 撮る前に窓を出力の大きさへ変えておけば、拡大のぼけも帯も出ない。後処理の作り直しは準備の時間に済む
            Vector2D<int> fb = _window.FramebufferSize;
            if (Math.Abs(((double)fb.X / fb.Y) - ((double)_clip.Width / _clip.Height)) > 0.01)
            {
                _window.Size = new Vector2D<int>(_clip.Width, _clip.Height);
            }
        }

        // 撮影開始の時刻を 0 とした「今」。準備の間は負になる
        double now = Clock.Elapsed.TotalSeconds - _clip.Warmup;
        _keys.SendDue(_hwnd, now);
        if (now < 0.0)
        {
            return;
        }

        if (!_sink.Started)
        {
            Vector2D<int> size = _window.FramebufferSize;
            _frameWidth = size.X;
            _frameHeight = size.Y;

            // GL の原点は左下なので上下を返す。縮める先の縦横比が違っても歪めないよう、収めてから余白を足す
            _sink.Start(_frameWidth, _frameHeight, "rgb24",
                $"vflip,{(_clip.Crop is null ? "" : $"crop={_clip.Crop},")}scale={_clip.Width}:{_clip.Height}:force_original_aspect_ratio=decrease:flags=lanczos,"
                + $"pad={_clip.Width}:{_clip.Height}:(ow-iw)/2:(oh-ih)/2");
        }

        _sink.Tick(now, ReadFrame);
    }

    private static byte[] ReadFrame()
    {
        // アプリが変えたままの状態を壊さないよう、触るものは読んでから戻す
        _gl.GetInteger(GetPName.ReadFramebufferBinding, out int readFramebuffer);
        _gl.GetInteger(GetPName.PackAlignment, out int packAlignment);

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        _gl.ReadBuffer(ReadBufferMode.Back);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);

        var pixels = new byte[_frameWidth * _frameHeight * 3];
        _gl.ReadPixels(0, 0, (uint)_frameWidth, (uint)_frameHeight, PixelFormat.Rgb, PixelType.UnsignedByte, pixels.AsSpan());

        _gl.PixelStore(PixelStoreParameter.PackAlignment, packAlignment);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)readFramebuffer);
        return pixels;
    }
}
