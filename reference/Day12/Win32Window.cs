using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RawGL;

/// <summary>
/// Win32 API だけで作ったウィンドウ。Day 1〜10 の <c>GameWindow : Form</c> の置き換え。
///
/// 役割は Day 1 のときと変わらない。
///   1. ウィンドウを1枚出す
///   2. OS からのメッセージを捌く
///   3. キーの押下状態を保持する
///
/// 違うのは「WinForms がやってくれていたことを全部自分で書く」点だけ。
/// Form を継承していた頃は 10 行程度で済んでいたが、その裏で
/// 実際に何が起きていたのかがこのファイルに全部出ている。
/// </summary>
internal sealed class Win32Window : IDisposable
{
    /// <summary>
    /// ウィンドウクラス名。プロセス内で一意ならなんでもよい。
    /// 名前が衝突すると RegisterClassEx が ERROR_CLASS_ALREADY_EXISTS で失敗する。
    /// </summary>
    private const string WindowClassName = "RawGL.Win32Window";

    /// <summary>
    /// ウィンドウプロシージャのデリゲート。
    ///
    /// **このフィールドが今日いちばん重要**。
    /// OS 側は関数のアドレスしか持っていないので、マネージド側でこのデリゲートを
    /// 参照し続けていないと GC に回収され、次のメッセージで即死する。
    /// ローカル変数のまま <c>Marshal.GetFunctionPointerForDelegate</c> に渡す実装を
    /// よく見かけるが、あれは「まだ GC が走っていないから動いている」だけの時限爆弾。
    ///
    /// なお、インスタンスメソッドをそのままデリゲート化しているので、
    /// このデリゲートは暗黙に <c>this</c> を捕まえている。おかげで
    /// ウィンドウプロシージャの中から普通にフィールドを触れる。
    /// (ウィンドウを複数出す場合はこの手が使えない。改造課題3を参照)
    /// </summary>
    private readonly Win32.WndProcDelegate _wndProc;

    private readonly IntPtr _hInstance;

    /// <summary>いま押されているか(押しっぱなしの間ずっと true)。</summary>
    private readonly bool[] _keyDown = new bool[256];

    /// <summary>このフレームで「押された瞬間」か。オートリピートは含めない。</summary>
    private readonly bool[] _keyPressed = new bool[256];

    private bool _destroyed;

    private bool _disposed;

    public IntPtr Hwnd { get; private set; }

    public int ClientWidth { get; }

    public int ClientHeight { get; }

    public Win32Window(string title, int clientWidth, int clientHeight)
    {
        ClientWidth = clientWidth;
        ClientHeight = clientHeight;

        _hInstance = Win32.GetModuleHandleW(null);
        _wndProc = WndProc;

        RegisterWindowClass();
        Hwnd = CreateWindow(title, clientWidth, clientHeight);

        Win32.ShowWindow(Hwnd, Win32.SW_SHOW);

        // 溜まっている WM_PAINT を今すぐ処理させる。
        // 無くても最初のフレームで上書きされるが、起動直後の一瞬だけ
        // 未初期化の内容が見えることがあるので、定石どおり呼んでおく。
        Win32.UpdateWindow(Hwnd);
    }

    /// <summary>
    /// ウィンドウクラス(= ウィンドウの型紙)を OS に登録する。
    /// CreateWindowEx はここで登録した名前を指定して「その型紙で1枚作れ」と頼む形になる。
    /// </summary>
    private void RegisterWindowClass()
    {
        var wc = new Win32.WNDCLASSEXW
        {
            // サイズを埋め忘れると登録が失敗する。Win32 で最も多い初歩のミス。
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),

            // CS_OWNDC は Day 12 の OpenGL 用の先行投資。
            // CS_HREDRAW / CS_VREDRAW はサイズ変更時に全面を再描画させる指定。
            style = Win32.CS_HREDRAW | Win32.CS_VREDRAW | Win32.CS_OWNDC,

            // デリゲート → 関数ポインタ。ここで OS に「呼び返す先」を教える。
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),

            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = _hInstance,
            hIcon = IntPtr.Zero,

            // カーソルを指定しないと、クライアント領域でカーソルが
            // 直前のウィンドウのもの(リサイズ矢印など)のまま固まる。
            hCursor = Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW),

            // NULL = 背景を塗らない。毎フレーム全面を自分で埋めるので不要
            // (塗らせるとちらつきの原因になる)。
            hbrBackground = IntPtr.Zero,

            lpszMenuName = null,
            lpszClassName = WindowClassName,
            hIconSm = IntPtr.Zero,
        };

        if (Win32.RegisterClassExW(ref wc) == 0)
        {
            // 失敗の理由は GetLastError にしか無い。SetLastError = true を
            // 付けておいたおかげでここで拾える。
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW に失敗した");
        }
    }

    /// <summary>
    /// ウィンドウを実際に1枚作る。
    /// クライアント領域をちょうど <paramref name="clientWidth"/> x <paramref name="clientHeight"/> に
    /// 合わせるところまでが仕事。
    /// </summary>
    private IntPtr CreateWindow(string title, int clientWidth, int clientHeight)
    {
        // サイズ変更と最大化を禁止する。Day 1 の FormBorderStyle.FixedSingle 相当。
        // フレームバッファを固定サイズで確保しているので、勝手に伸ばされると困る。
        const uint style = Win32.WS_OVERLAPPEDWINDOW & ~(Win32.WS_THICKFRAME | Win32.WS_MAXIMIZEBOX);

        // CreateWindowEx に渡すのは「枠を含んだ外側のサイズ」なので、
        // 欲しいクライアントサイズから逆算する。
        // WinForms の ClientSize プロパティは、この計算を裏でやっていた。
        var rect = new Win32.RECT { left = 0, top = 0, right = clientWidth, bottom = clientHeight };
        Win32.AdjustWindowRect(ref rect, style, false);

        int outerWidth = rect.right - rect.left;
        int outerHeight = rect.bottom - rect.top;

        IntPtr hwnd = Win32.CreateWindowExW(
            dwExStyle: 0,
            lpClassName: WindowClassName,
            lpWindowName: title,
            dwStyle: style,
            x: Win32.CW_USEDEFAULT,
            y: Win32.CW_USEDEFAULT,
            nWidth: outerWidth,
            nHeight: outerHeight,
            hWndParent: IntPtr.Zero,
            hMenu: IntPtr.Zero,
            hInstance: _hInstance,
            lpParam: IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW に失敗した");
        }

        // --- クライアントサイズの検算 ---
        //
        // AdjustWindowRect が使うのは **システムDPI(= 主モニタのDPI)の枠の太さ**。
        // ところが PerMonitorV2 を宣言したこのプロセスでは、枠とタイトルバーは
        // 「ウィンドウが実際に載っているモニタのDPI」で描かれる。
        // 主モニタと同じスケーリングなら一致するが、スケーリングの違う副モニタに
        // 出た場合はずれ、逆算した外側サイズでは中身が 640x480 に足りなくなる。
        // (実測: 640x480 の外側サイズは 96dpi で 656x519、192dpi では 666x551。
        //  32ピクセルぶん足りなければ、その行は画面に出ない)
        //
        // 出来上がったウィンドウに実測を聞いて、ずれていたら足す——これが確実。
        // AdjustWindowRectExForDpi + GetDpiForWindow で先回りする手もある。
        Win32.GetClientRect(hwnd, out Win32.RECT client);
        int deltaWidth = clientWidth - (client.right - client.left);
        int deltaHeight = clientHeight - (client.bottom - client.top);

        int finalWidth = outerWidth + deltaWidth;
        int finalHeight = outerHeight + deltaHeight;

        // ついでに画面中央へ寄せる。Day 1 の StartPosition = CenterScreen 相当。
        int screenWidth = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
        int screenHeight = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);

        Win32.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            (screenWidth - finalWidth) / 2,
            (screenHeight - finalHeight) / 2,
            finalWidth,
            finalHeight,
            Win32.SWP_NOZORDER);

        return hwnd;
    }

    /// <summary>
    /// 溜まっているメッセージを全部捌く。ウィンドウが閉じられたら false を返す。
    ///
    /// **PeekMessage を使うのがゲームループの要**。
    /// 教科書に載っている <c>while (GetMessage(...))</c> は
    /// 「メッセージが来るまでスレッドを眠らせる」ので、
    /// マウスもキーも動かさなければ1フレームも進まない。
    /// ゲームは入力が無くても毎フレーム絵を更新したいので、
    /// 「あるだけ処理して、無ければすぐ抜ける」PeekMessage でなければならない。
    /// Day 1 の <c>Application.DoEvents()</c> は、まさにこのループの WinForms 版だった。
    /// </summary>
    public bool ProcessMessages()
    {
        // 「押された瞬間」は1フレームだけ立てたいので、毎フレーム頭で消す。
        Array.Clear(_keyPressed);

        // 第2引数に IntPtr.Zero(= すべてのウィンドウ)を渡すのが重要。
        // WM_QUIT は特定のウィンドウ宛てではなく**スレッドのキューに直接**積まれるため、
        // ここに自分の hwnd を書いてしまうと WM_QUIT を永久に受け取れず、終了できなくなる。
        while (Win32.PeekMessageW(out Win32.MSG msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            if (msg.message == Win32.WM_QUIT)
            {
                return false;
            }

            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }

        return true;
    }

    /// <summary>
    /// ウィンドウプロシージャ。**OS からこちらへの入口**。
    ///
    /// DispatchMessage がメッセージをここへ配送する。呼び出し元はネイティブコードなので、
    /// ここから例外を投げ出してはいけない(ネイティブのスタックを巻き戻せず、
    /// 何が起きたか分からないままプロセスが死ぬ)。
    /// 実用コードでは全体を try-catch で包み、例外は記録して握り潰すのが定石。
    ///
    /// 自分で処理したメッセージは 0 を返し、処理しなかったものは
    /// <c>DefWindowProcW</c> に渡す。この「渡し漏れ」があると、
    /// ウィンドウが動かせない・閉じられない・カーソルが変わらないといった
    /// 一見不可解な不具合になる。
    /// </summary>
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // [x] ボタン、Alt+F4、システムメニューの「閉じる」で届く。
            // 「本当に閉じてよいか」を判断できる唯一の場所で、
            // 保存確認ダイアログを出すならここ。今日は素直に破棄する。
            case Win32.WM_CLOSE:
                Win32.DestroyWindow(hWnd);
                return IntPtr.Zero;

            // ウィンドウが実際に壊された後。もう描画はできない。
            // ここで PostQuitMessage を呼ばないと WM_QUIT が積まれず、
            // メッセージループが**ウィンドウの無いまま回り続ける**。
            case Win32.WM_DESTROY:
                _destroyed = true;
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;

            case Win32.WM_KEYDOWN:
                {
                    int vk = (int)wParam;
                    if ((uint)vk < 256)
                    {
                        // lParam の bit 30 は「直前のキー状態」。1 なら既に押されていた、
                        // つまり OS のオートリピートによる再送。
                        // 押しっぱなしで連射されると「押された瞬間」の判定が壊れるので弾く。
                        bool wasDown = ((long)lParam & (1L << 30)) != 0;

                        _keyDown[vk] = true;
                        if (!wasDown)
                        {
                            _keyPressed[vk] = true;
                        }
                    }

                    return IntPtr.Zero;
                }

            case Win32.WM_KEYUP:
                {
                    int vk = (int)wParam;
                    if ((uint)vk < 256)
                    {
                        _keyDown[vk] = false;
                    }

                    return IntPtr.Zero;
                }
        }

        return Win32.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>押しっぱなしの判定。移動など「押している間ずっと」の入力に使う。</summary>
    public bool IsKeyDown(int virtualKey) => _keyDown[virtualKey];

    /// <summary>押された瞬間の判定。トグルなど「1回だけ」の入力に使う。</summary>
    public bool WasKeyPressed(int virtualKey) => _keyPressed[virtualKey];

    /// <summary>タイトルバーの文字列を差し替える。FPS 表示に使う。</summary>
    public void SetTitle(string title)
    {
        if (!_destroyed)
        {
            Win32.SetWindowTextW(Hwnd, title);
        }
    }

    /// <summary>自分から閉じる(Esc キー用)。WM_DESTROY 経由で WM_QUIT まで繋がる。</summary>
    public void Close()
    {
        if (!_destroyed)
        {
            Win32.DestroyWindow(Hwnd);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_destroyed && Hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(Hwnd);
        }

        Hwnd = IntPtr.Zero;

        // ウィンドウクラスの登録も解除しておく。プロセス終了時に OS が
        // 後始末するので実害は無いが、「取ったものは返す」を守っておくと、
        // 後で同じクラス名で作り直したくなったときに詰まらない。
        Win32.UnregisterClassW(WindowClassName, _hInstance);

        // ここまでデリゲートが生きていたことを保証する。
        // JIT は「もう使われない」と判断した変数を早期に回収可能とみなすので、
        // 明示しておかないと Dispose より前に回収されうる。
        GC.KeepAlive(_wndProc);
    }
}
