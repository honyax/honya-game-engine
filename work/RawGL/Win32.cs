using System.Runtime.InteropServices;

namespace RawGL;

/// <summary>
/// Win32 API の宣言をまとめた場所。Phase 2 の土台になるファイル。
///
/// C# から OS の C 関数を呼ぶ仕組みが **P/Invoke**(Platform Invoke)。
/// <c>[DllImport]</c> を書いておくと、CLR が初回呼び出し時に
/// LoadLibrary → GetProcAddress で関数のアドレスを引き、
/// 呼び出し用のスタブ(サンク)を生成してくれる。
/// Silk.NET も MonoGame も、一番下ではこれと同じことをしている。
///
/// **そのスタブの仕事は、混同しやすい2つに分かれる**。
///
/// 1. **呼び出し規約(ABI)への準拠** — 引数を「どこに置くか」。
///    x64 Windows では整数・ポインタの第1〜4引数が RCX/RDX/R8/R9、浮動小数点が
///    XMM0〜XMM3 に載り、第5引数以降がスタックに積まれる
///    (「引数はスタックに積む」は 32bit 時代の話で、x64 ではレジスタが優先)。
///    C コンパイラが C 関数を呼ぶときと同じ作業で、全呼び出しで必ず起きる。
///
/// 2. **マーシャリング** — 引数を「何に化けさせるか」。マネージドとネイティブで
///    メモリ上の姿が違う型を詰め替える作業で、この言葉が本来指すのはこちらだけ。
///    <c>string</c> → null終端の <c>wchar_t*</c>(確保してコピーして呼び出し後に解放)、
///    <c>bool</c>(1バイト) → Win32 <c>BOOL</c>(4バイトの int)、
///    delegate → ネイティブ側から入り直すためのサンクの関数ポインタ、など。
///    逆に <c>int</c> や <c>IntPtr</c> のようにビット表現が完全に一致する型
///    (**blittable** と呼ぶ)だけで組まれた宣言なら、変換もコピーも1バイトも起きない。
///
/// 毎フレーム通る <c>PeekMessageW</c> / <c>DispatchMessageW</c> / <c>StretchDIBits</c> が
/// すべて blittable な型で済んでいるのは偶然ではない。Win32 API が「ハンドルと整数」で
/// 押し通す設計だからで、P/Invoke のコストがここで問題になりにくい理由でもある
/// (残るのは、ネイティブ実行中に GC 全体を止めないためのモード遷移くらい)。
///
/// **命名について**: このファイルの中だけは C# の命名規約(PascalCase)を捨てて、
/// Win32 の名前をそのまま使う(<c>WM_DESTROY</c>、<c>lpfnWndProc</c> など)。
/// MSDN や世に溢れる C++ のサンプルと1対1で突き合わせられることのほうが、
/// 規約に揃えることよりずっと価値があるため。
/// 「lp」は long pointer、「h」は handle、「cb」は count of bytes の略で、
/// 16bit 時代のハンガリアン記法がそのまま化石として残っている。
/// </summary>
internal static class Win32
{
    // ================= ウィンドウクラスのスタイル (CS_*) =================

    /// <summary>高さが変わったらウィンドウ全体を再描画する。</summary>
    public const uint CS_VREDRAW = 0x0001;

    /// <summary>幅が変わったらウィンドウ全体を再描画する。</summary>
    public const uint CS_HREDRAW = 0x0002;

    /// <summary>
    /// このウィンドウ専用のデバイスコンテキスト(DC)を持たせる。
    ///
    /// 既定ではDCはシステムのプールからの借り物で、GetDC のたびに状態がリセットされる。
    /// CS_OWNDC を付けると1枚のDCが窓に固定され、GetDC を1回だけ呼んで使い回せる。
    /// **Day 12 で OpenGL のコンテキストをDCに結び付けるときに効いてくる**ので、
    /// 今日のうちから付けておく。
    /// </summary>
    public const uint CS_OWNDC = 0x0020;

    // ================= ウィンドウスタイル (WS_*) =================

    public const uint WS_OVERLAPPED = 0x00000000;
    public const uint WS_CAPTION = 0x00C00000;      // タイトルバー
    public const uint WS_SYSMENU = 0x00080000;      // 左上のアイコンメニューと [x] ボタン
    public const uint WS_THICKFRAME = 0x00040000;   // サイズ変更できる枠
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;

    /// <summary>いわゆる「普通のウィンドウ」。上のフラグの寄せ集めでしかない。</summary>
    public const uint WS_OVERLAPPEDWINDOW =
        WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

    /// <summary>位置・サイズを OS に任せる。0x80000000 を int で表すのでキャストが要る。</summary>
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    // ================= ShowWindow / SetWindowPos =================

    public const int SW_SHOW = 5;

    public const uint SWP_NOZORDER = 0x0004;

    // ================= GetSystemMetrics =================

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    // ================= メッセージ (WM_*) =================
    //
    // ウィンドウに起きたことは全部この番号で伝わってくる。
    // 番号は Windows 1.0 の時代から変わっていないので、今でも WM_DESTROY は 2 番。

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;

    // ================= 仮想キーコード (VK_*) =================
    //
    // キーボードの物理的な位置ではなく「意味」に振られた番号。
    // 英字と数字は ASCII と同じ値なので、'W' や '1' をそのままキャストして使える。

    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;

    // ================= PeekMessage =================

    /// <summary>取り出したメッセージをキューから消す。消さない PM_NOREMOVE(0)もある。</summary>
    public const uint PM_REMOVE = 0x0001;

    // ================= カーソル =================

    /// <summary>
    /// 標準の矢印カーソル。本来 <c>MAKEINTRESOURCE(32512)</c> という
    /// 「ポインタのふりをした整数」なので、IntPtr に詰めて渡す。
    /// これを指定し忘れると、クライアント領域に入った瞬間に
    /// カーソルが直前のウィンドウのもの(砂時計やリサイズ矢印)のまま残る。
    /// </summary>
    public static readonly IntPtr IDC_ARROW = new(32512);

    // ================= DIB / BitBlt =================

    /// <summary>無圧縮。ビットマップは大昔から RLE 圧縮も選べるが今は使わない。</summary>
    public const uint BI_RGB = 0;

    /// <summary>カラーテーブルは RGB の実値(パレット索引ではない)。</summary>
    public const uint DIB_RGB_COLORS = 0;

    /// <summary>転送元をそのまま複写するラスタオペレーション。</summary>
    public const uint SRCCOPY = 0x00CC0020;

    // ================= 高DPI =================

    /// <summary>
    /// 「モニタごとのDPIを自分で面倒見る」宣言。値 -4 の疑似ハンドル。
    ///
    /// これを宣言しないと Windows は勝手に画面を引き伸ばし(DPI仮想化)、
    /// 640x480 のフレームバッファがぼやけて表示される。
    /// Day 1〜10 では csproj の ApplicationHighDpiMode=PerMonitorV2 が
    /// 裏でこれと同じ設定をしていた。
    /// </summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // ================= 構造体 =================

    /// <summary>
    /// 画面上の点。Win32 の座標は徹底して int(ピクセル)。
    ///
    /// LayoutKind.Sequential は「宣言した順にメモリへ並べろ」の指示。
    /// これが無いと CLR はフィールドを詰め替えてよいことになっており、
    /// C の構造体とメモリ配置が一致しなくなる。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    /// <summary>
    /// 矩形。**right と bottom は「含まない」**(半開区間)ので、幅は right - left。
    /// Day 3 のバウンディングボックスと同じ流儀。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /// <summary>
    /// メッセージ1件。OS のメッセージキューから PeekMessage で取り出す器。
    ///
    /// wParam / lParam は「メッセージごとに意味が変わる汎用の入れ物」で、
    /// たとえば WM_KEYDOWN なら wParam に仮想キーコード、
    /// lParam にリピート回数やスキャンコードがビット単位で詰まっている。
    /// 型安全とは正反対の設計だが、これが Windows の ABI そのもの。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    /// <summary>
    /// ウィンドウ「クラス」の定義。ここで言うクラスは OOP のクラスではなく、
    /// 「同じ見た目・同じ挙動のウィンドウを作るための型紙」。
    /// ボタンもエディットボックスも、OS にあらかじめ登録されたウィンドウクラスでしかない。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        /// <summary>
        /// 自分自身のバイト数。**呼ぶ前に必ず埋めること**。
        /// 構造体にフィールドが増えても古いアプリが動くように、
        /// Win32 はサイズをバージョン番号代わりに使う。0 のままだと登録が失敗する。
        /// </summary>
        public uint cbSize;

        public uint style;

        /// <summary>ウィンドウプロシージャの関数ポインタ。ここが今日の心臓部。</summary>
        public IntPtr lpfnWndProc;

        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;

        /// <summary>
        /// 背景を塗るブラシ。**NULL にすると OS は背景を塗らない**。
        /// Day 1 で <c>SetStyle(ControlStyles.Opaque, true)</c> と書いたのはこれと同じ話で、
        /// 毎フレーム全面を自分で埋めるならOSの塗りは無駄でしかない
        /// (塗ってから上書きすると一瞬背景色が見えてちらつく)。
        /// </summary>
        public IntPtr hbrBackground;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;

        public IntPtr hIconSm;
    }

    /// <summary>
    /// DIB(Device Independent Bitmap)のヘッダ。ピクセル配列の読み方を GDI に教える。
    /// これも先頭が biSize で、やはりサイズがバージョン代わり。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;

        /// <summary>
        /// 高さ。**負の値にすると「トップダウン」**、つまり
        /// メモリの先頭が画像の一番上の行になる。
        ///
        /// 正の値(ボトムアップ)が Windows ビットマップの既定だが、これは
        /// 数学の座標系(下が原点)を採った歴史的経緯によるもの。
        /// こちらの <see cref="Framebuffer"/> は配列の先頭が左上なので、
        /// 負にしないと上下が逆さまに表示される。
        /// Day 10 の要点3(OBJ の V 座標反転)と根っこは同じ「原点の流儀の違い」。
        /// </summary>
        public int biHeight;

        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    /// <summary>
    /// ヘッダ + カラーテーブル。
    /// 32bpp の BI_RGB ではカラーテーブルを使わないが、
    /// C の定義が <c>RGBQUAD bmiColors[1]</c> を持つので同じ大きさにしておく。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    // ================= ウィンドウプロシージャ =================

    /// <summary>
    /// ウィンドウプロシージャの型。OS がこちらのコードを**呼び返してくる**(コールバック)。
    ///
    /// CallingConvention.Winapi は「そのプラットフォームの標準」の意味で、
    /// x64 Windows では x64 呼び出し規約になる。
    ///
    /// **最重要の落とし穴**: このデリゲートのインスタンスを GC から守らないと、
    /// OS が関数アドレスを保持したままマネージド側が回収され、
    /// 次にメッセージが来た瞬間にアクセス違反で落ちる。しかも
    /// 「GC が走ったとき」に初めて落ちるので、起動直後は動いて数分後に死ぬ。
    /// 対策は <see cref="Win32Window"/> のフィールドで持ち続けること。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ================= kernel32 =================

    /// <summary>
    /// 実行中モジュールのハンドル(= EXE がロードされたベースアドレス)。
    /// 引数に null を渡すと自分自身が返る。ウィンドウクラスの持ち主として要る。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // ================= user32 =================
    //
    // bool の戻り値は DllImport の既定で 4バイトの Win32 BOOL として扱われるので、
    // 特別な属性は要らない(1バイトの C++ bool とは別物)。
    //
    // SetLastError = true を付けた関数は、失敗時に
    // Marshal.GetLastWin32Error() でエラーコードを取れる。付け忘れると
    // 「失敗したことは分かるが理由が分からない」状態になる。

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    /// <summary>成功すると「クラスアトム」という 16bit の識別子が返る。失敗は 0。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>
    /// 既定のウィンドウ処理。自分で処理しないメッセージは**必ず**ここへ流す。
    /// ウィンドウの移動・システムメニュー・[x] ボタン・Alt+F4 といった
    /// 「普通のウィンドウの挙動」は、全部この関数が持っている。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool UpdateWindow(IntPtr hWnd);

    /// <summary>
    /// キューにメッセージがあれば取り出す。**無ければ即座に false で戻る**。
    /// 待ち続ける GetMessage との違いが、そのまま
    /// 「ゲームループ」と「イベント駆動アプリ」の違いになる。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PeekMessageW(
        out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    /// <summary>
    /// キー入力を文字入力(WM_CHAR)に翻訳してキューへ積み直す。
    /// 今日は WM_CHAR を使わないので実質何もしないが、
    /// テキスト入力を扱う日が来たときに無いと困るので定石どおり呼んでおく。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    /// <summary>メッセージを宛先ウィンドウのウィンドウプロシージャへ配送する。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    /// <summary>キューに WM_QUIT を積む。メッセージループを終わらせる正規の手段。</summary>
    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    /// <summary>
    /// 「クライアント領域をこの大きさにしたい」から「ウィンドウ全体の大きさ」を逆算する。
    /// 枠とタイトルバーのぶんだけ矩形が外側へ広がる。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AdjustWindowRect(ref RECT lpRect, uint dwStyle, bool bMenu);

    /// <summary>クライアント領域の矩形。left/top は常に 0 で、right/bottom が幅と高さになる。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    /// <summary>デバイスコンテキストを得る。GDI の描画は全部これを通す。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // ================= gdi32 =================

    /// <summary>
    /// メモリ上のピクセル配列を、拡大縮小しながらDCへ転送する。
    ///
    /// 第10引数の <c>int[]</c> は blittable(マネージドとネイティブでメモリ表現が同じ)な
    /// 配列なので、CLR はコピーを作らず**配列をピン留めして先頭アドレスを渡すだけ**で済ませる。
    /// 640x480x4 = 1.2MB を毎フレーム複製されたら話にならないので、これは重要な性質。
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern int StretchDIBits(
        IntPtr hdc,
        int xDest, int yDest, int destWidth, int destHeight,
        int xSrc, int ySrc, int srcWidth, int srcHeight,
        int[] lpBits,
        ref BITMAPINFO lpbmi,
        uint iUsage,
        uint rop);
}
