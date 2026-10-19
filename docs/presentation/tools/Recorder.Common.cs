// ============================================================
//  発表用の動画を撮るための差し込み(共通部分)
//
//  record.ps1 が reference/DayXX の「コピー」に、このファイルと
//  Recorder.Silk.cs(Day 14 以降)か Recorder.WinForms.cs(Day 10)のどちらかを置いてビルドする。
//  reference 本体には入れない(教材の答えを濁さないため)。
//
//  ここに置くのは、窓の作りに依らない3つ:
//  - 撮影の表(環境変数 HONYA_RECORD が指す JSON)を読む
//  - キーの操作を時刻順に並べ、窓へ WM_KEYDOWN / WM_KEYUP を送る
//  - 1/30 秒の升目に合わせて絵を ffmpeg の標準入力へ流す
// ============================================================
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PresentationRecording;

internal sealed class KeyStep
{
    public double At { get; set; }
    public string? Tap { get; set; }
    public string? Down { get; set; }
    public string? Up { get; set; }
    public int Times { get; set; } = 1;
    public double Every { get; set; } = 0.15;
    public string[]? Mods { get; set; }

    // マウス(カメラを回す・寄せる)。wheel はホイールの目盛り(正で奥へ = 寄る)、
    // drag は左ドラッグの移動量 [dx, dy](画素)。duration 秒かけて少しずつ動かす
    public int Wheel { get; set; }
    public double[]? Drag { get; set; }
    public double Duration { get; set; } = 0.3;
}

internal sealed class Clip
{
    public string Out { get; set; } = "";
    public double Warmup { get; set; } = 6.0;
    public double Seconds { get; set; } = 10.0;
    public int Fps { get; set; } = 30;
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public string Ffmpeg { get; set; } = "ffmpeg";
    public KeyStep[] Keys { get; set; } = [];

    // 縮める前に切り抜く範囲(ffmpeg の crop の引数。例: "iw*2/3:ih*2/3:iw/6:ih/3")。
    // 消せない画面上の表示(2D のスプライトなど)を外すときに使う。無ければ切り抜かない
    public string? Crop { get; set; }

    // 圧縮のしかた(ffmpeg の出力側の引数)。record.ps1 が形式ごとに渡す。無ければ H.264
    public string[] Codec { get; set; } = ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart"];

    /// <summary>撮影の表を読む。環境変数が無ければ null(ふつうに起動したときは何もしない)。</summary>
    public static Clip? FromEnvironment()
    {
        string? path = Environment.GetEnvironmentVariable("HONYA_RECORD");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Clip>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"撮影の表を読めません: {path}");
    }
}

/// <summary>
/// キーの操作を時刻どおりに窓へ送る。
///
/// GLFW(Silk.NET の窓の中身)も WinForms も、窓に届いた WM_KEYDOWN / WM_KEYUP からキーの状態を作るので、
/// 押した瞬間のスイッチも、押しっぱなしの移動も効く。窓が最前面でなくても届く。
/// </summary>
internal sealed class KeyScript
{
    // Msg が 0 でなければ、キーではなくそのままのメッセージ(マウス)を送る
    private readonly record struct KeyEvent(double At, string Key, bool Down, bool Sys, uint Msg = 0, nint WParam = 0, nint LParam = 0);

    private readonly List<KeyEvent> _events;
    private int _next;

    public KeyScript(KeyStep[] steps) => _events = Expand(steps);

    /// <summary>撮影開始を 0 とした時刻 <paramref name="now"/> までに来た操作を、まとめて送る。</summary>
    public void SendDue(nint hwnd, double now)
    {
        while (_next < _events.Count && _events[_next].At <= now)
        {
            Send(hwnd, _events[_next++]);
        }
    }

    private static List<KeyEvent> Expand(KeyStep[] steps)
    {
        var events = new List<KeyEvent>();
        foreach (KeyStep step in steps)
        {
            string[] mods = step.Mods ?? [];

            // Alt を押している間のキーは WM_SYSKEYDOWN で届くのが本物の動き
            bool sys = mods.Any(m => m.Equals("Alt", StringComparison.OrdinalIgnoreCase));
            if (step.Down is not null)
            {
                events.Add(new KeyEvent(step.At, step.Down, true, sys));
            }

            if (step.Up is not null)
            {
                events.Add(new KeyEvent(step.At, step.Up, false, sys));
            }

            if (step.Wheel != 0)
            {
                // GLFW は wParam の上位 16 ビット(1 目盛り = 120)だけを見る
                events.Add(new KeyEvent(step.At, "", true, false, WmMouseWheel, (nint)(step.Wheel * 120 << 16), 0));
            }

            if (step.Drag is { Length: 2 } drag)
            {
                // 位置は「押した瞬間のカーソルからの差」で持っておき、送るときに足す(Send 参照)
                int moves = Math.Max(1, (int)(step.Duration * 60));
                events.Add(new KeyEvent(step.At, DragKey, true, false, WmLButtonDown, MkLButton, Point(0, 0)));
                for (int i = 1; i <= moves; i++)
                {
                    double t = (double)i / moves;
                    int dx = (int)Math.Round(drag[0] * t);
                    int dy = (int)Math.Round(drag[1] * t);
                    events.Add(new KeyEvent(step.At + (step.Duration * t), DragKey, true, false, WmMouseMove, MkLButton, Point(dx, dy)));
                }

                events.Add(new KeyEvent(step.At + step.Duration, DragKey, true, false, WmLButtonUp, 0, Point((int)Math.Round(drag[0]), (int)Math.Round(drag[1]))));
            }

            if (step.Tap is not null)
            {
                // 修飾キーは同じ時刻に押して離す(OnKeyDown は届いた順に同期で走るので足りる)
                for (int i = 0; i < step.Times; i++)
                {
                    double at = step.At + (i * step.Every);
                    foreach (string mod in mods)
                    {
                        events.Add(new KeyEvent(at, mod, true, sys));
                    }

                    events.Add(new KeyEvent(at, step.Tap, true, sys));
                    events.Add(new KeyEvent(at, step.Tap, false, sys));
                    foreach (string mod in mods.Reverse())
                    {
                        events.Add(new KeyEvent(at, mod, false, sys));
                    }
                }
            }
        }

        // 同じ時刻のものは書いた順を保つ(List.Sort は安定でないので OrderBy を使う)
        return events.OrderBy(e => e.At).ToList();
    }

    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmMouseWheel = 0x020A;
    private const nint MkLButton = 0x0001;

    private static nint Point(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref NativePoint point);

    private const string DragKey = "(drag)";

    // ドラッグを始めた瞬間の本物のカーソルの位置(窓の中の座標)
    private static NativePoint _dragBase;

    private static void Send(nint hwnd, KeyEvent e)
    {
        if (e.Msg != 0)
        {
            nint position = e.LParam;
            if (e.Key == DragKey)
            {
                // GLFW は押した瞬間の位置を本物のカーソル(GetCursorPos)から取り、移動はメッセージの座標で受ける。
                // 本物のカーソルを起点にして差を足さないと、最初の移動で差分が跳ねてカメラが飛ぶ
                if (e.Msg == WmLButtonDown)
                {
                    GetCursorPos(out _dragBase);
                    ScreenToClient(hwnd, ref _dragBase);
                }

                int dx = (short)(e.LParam & 0xFFFF);
                int dy = (short)((e.LParam >> 16) & 0xFFFF);
                position = Point(_dragBase.X + dx, _dragBase.Y + dy);
            }

            PostMessageW(hwnd, e.Msg, e.WParam, position);
            return;
        }

        (uint vk, bool extended, uint fixedScancode) = VirtualKey(e.Key);

        // GLFW はキーをスキャンコードで見分ける。記号のキーは配列(日本語 / 英語)で仮想キーとの対応が変わるので、
        // GLFW が期待する英語配列のスキャンコードを直接渡す
        uint scancode = fixedScancode != 0 ? fixedScancode : MapVirtualKeyW(vk, 0);

        // lParam: 繰り返し回数 1 / スキャンコード / 拡張キー / 離すときは「前は押されていた」と「離した」の2ビット
        long lParam = 1 | (scancode << 16) | (extended ? 1L << 24 : 0);
        if (!e.Down)
        {
            lParam |= (1L << 30) | (1L << 31);
        }

        bool sys = e.Sys || vk == 0x12;
        uint msg = e.Down ? (sys ? WmSysKeyDown : WmKeyDown) : (sys ? WmSysKeyUp : WmKeyUp);
        PostMessageW(hwnd, msg, (nint)vk, (nint)lParam);
    }

    private static (uint Vk, bool Extended, uint Scancode) VirtualKey(string name)
    {
        string key = name.Trim();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return (char.ToUpperInvariant(key[0]), false, 0);
        }

        if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out int f) && f is >= 1 and <= 12)
        {
            return ((uint)(0x70 + f - 1), false, 0);
        }

        // 記号の3つ目の値は英語配列のスキャンコード(0 なら仮想キーから求める)
        return key.ToLowerInvariant() switch
        {
            "enter" => (0x0D, false, 0),
            "space" => (0x20, false, 0),
            "escape" or "esc" => (0x1B, false, 0),
            "backspace" => (0x08, false, 0),
            "tab" => (0x09, false, 0),
            "shift" => (0x10, false, 0),
            "ctrl" => (0x11, false, 0),
            "alt" => (0x12, false, 0),
            "left" => (0x25, true, 0),
            "up" => (0x26, true, 0),
            "right" => (0x27, true, 0),
            "down" => (0x28, true, 0),
            "home" => (0x24, true, 0),
            "end" => (0x23, true, 0),
            "pageup" => (0x21, true, 0),
            "pagedown" => (0x22, true, 0),
            "semicolon" => (0xBA, false, 0x27),
            "comma" => (0xBC, false, 0x33),
            "period" => (0xBE, false, 0x34),
            "slash" => (0xBF, false, 0x35),
            _ => throw new ArgumentException($"知らないキー名です: {name}"),
        };
    }
}

/// <summary>
/// 絵を 1/30 秒の升目に合わせて ffmpeg へ流す。
///
/// 時間は実時間で進む。アプリが遅くて升目を飛ばしたら同じ絵を複数回書き、動画の長さを実時間に合わせる。
/// 標準入力への書き込みは別スレッドで行い、描画の流れを圧縮の速さに引きずらせない。
/// </summary>
internal sealed class FrameSink
{
    private readonly Clip _clip;
    private readonly int _framesTotal;
    private int _framesWritten;
    private Process? _ffmpeg;
    private BlockingCollection<byte[]>? _queue;
    private Thread? _writer;
    private volatile bool _finished;

    public FrameSink(Clip clip)
    {
        _clip = clip;
        _framesTotal = (int)Math.Round(clip.Seconds * clip.Fps);
    }

    public bool Started => _ffmpeg is not null;

    /// <summary>書き出しが終わり、ffmpeg も閉じた。窓を閉じてよい。</summary>
    public bool Finished => _finished;

    /// <summary>
    /// ffmpeg を起動する。<paramref name="pixelFormat"/> は流す画素の並び(rgb24 / bgr0)、
    /// <paramref name="fit"/> は縮める前にかけるフィルタ(上下反転や切り抜き)。
    /// </summary>
    public void Start(int width, int height, string pixelFormat, string fit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_clip.Out))!);

        var info = new ProcessStartInfo(_clip.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        };
        string[] args =
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", pixelFormat, "-s", $"{width}x{height}", "-r", $"{_clip.Fps}", "-i", "-",
            "-vf", fit, "-an",
            .. _clip.Codec,
            _clip.Out,
        ];
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        Process ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg を起動できません");
        var queue = new BlockingCollection<byte[]>(boundedCapacity: 16);
        _writer = new Thread(() =>
        {
            Stream input = ffmpeg.StandardInput.BaseStream;
            foreach (byte[] frame in queue.GetConsumingEnumerable())
            {
                input.Write(frame);
            }

            input.Close();
        })
        { IsBackground = true, Name = "PresentationRecorder.Writer" };
        _writer.Start();
        _ffmpeg = ffmpeg;
        _queue = queue;
    }

    /// <summary>
    /// 撮影開始を 0 とした時刻 <paramref name="now"/> までの升目を埋める。
    /// 絵が要るときだけ <paramref name="read"/> を1回呼ぶ。
    /// </summary>
    public void Tick(double now, Func<byte[]> read)
    {
        if (_framesWritten >= _framesTotal)
        {
            return;
        }

        int due = Math.Min(_framesTotal, (int)Math.Floor(now * _clip.Fps) + 1);
        if (due > _framesWritten)
        {
            byte[] frame = read();
            while (_framesWritten < due)
            {
                _queue!.Add(_framesWritten + 1 < due ? (byte[])frame.Clone() : frame);
                _framesWritten++;
            }
        }

        if (_framesWritten >= _framesTotal)
        {
            // 書き出しが終わるのを裏で待つ(窓を閉じるのは、窓の持ち主のスレッドで)
            _queue!.CompleteAdding();
            var waiter = new Thread(() =>
            {
                _writer!.Join();
                _ffmpeg!.WaitForExit();
                Console.WriteLine($"[録画] 書き出しました(終了コード {_ffmpeg.ExitCode}): {_clip.Out}");
                _finished = true;
            })
            { IsBackground = true };
            waiter.Start();
        }
    }
}
