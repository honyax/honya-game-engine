using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RawGL;

/// <summary>
/// OpenGL のレンダリングコンテキスト。**Day 12 の「儀式」の本体**。
///
/// GLFW の <c>glfwCreateWindow</c> 1行、Silk.NET の <c>Window.Create</c> 1行の裏で
/// 起きていることが、そのままこのファイルの長さになっている。
/// 手順を並べると次のとおりで、**1〜5 がまるごと「捨てるための準備」**。
///
///   1. ダミーウィンドウを作る(表示しない)
///   2. 古い様式でピクセルフォーマットを設定する
///   3. 古い様式でコンテキストを作り、カレントにする
///   4. wglGetProcAddress で WGL の拡張関数を引く
///   5. ダミーを全部破棄する
///   6. 本番ウィンドウに、拡張関数でピクセルフォーマットを設定する
///   7. 拡張関数で 3.3 コアプロファイルのコンテキストを作る
///   8. カレントにして、OpenGL の関数をロードする
///
/// なぜこんな鶏と卵になるのかは要点3を参照。
/// </summary>
internal sealed class GLContext : IDisposable
{
    // ================= 拡張関数(プロセスに1組あればよい)=================

    private static bool _extensionsLoaded;
    private static Wgl.WglChoosePixelFormatARB? _choosePixelFormatARB;
    private static Wgl.WglCreateContextAttribsARB? _createContextAttribsARB;
    private static Wgl.WglSwapIntervalEXT? _swapIntervalEXT;

    /// <summary>
    /// ダミーウィンドウ用のウィンドウプロシージャ。
    /// 何も処理せず DefWindowProc に流すだけだが、**static フィールドで保持する**
    /// 必要があるのは Day 11 の要点4とまったく同じ理由(GC 対策)。
    /// </summary>
    private static readonly Win32.WndProcDelegate DummyWndProc =
        (hWnd, msg, wParam, lParam) => Win32.DefWindowProcW(hWnd, msg, wParam, lParam);

    private const string DummyClassName = "RawGL.WglBootstrap";

    private readonly IntPtr _hwnd;
    private readonly IntPtr _hdc;
    private IntPtr _hglrc;
    private bool _disposed;

    /// <summary>実際に得られたコンテキストの情報。タイトルバーとコンソールに出す。</summary>
    public string Vendor { get; private set; } = string.Empty;

    public string Renderer { get; private set; } = string.Empty;

    public string Version { get; private set; } = string.Empty;

    public string ShadingLanguageVersion { get; private set; } = string.Empty;

    public int ExtensionCount { get; private set; }

    public GLContext(IntPtr hwnd, int majorVersion, int minorVersion)
    {
        // 手順 1〜5。プロセスで最初の1回だけ走る。
        EnsureExtensionsLoaded();

        _hwnd = hwnd;
        _hdc = Win32.GetDC(hwnd);
        if (_hdc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC に失敗した");
        }

        // --- 手順 6: 本番のピクセルフォーマット ---
        SetupPixelFormat();

        // --- 手順 7: バージョンとプロファイルを指定してコンテキストを作る ---
        int[] contextAttributes =
        [
            Wgl.WGL_CONTEXT_MAJOR_VERSION_ARB, majorVersion,
            Wgl.WGL_CONTEXT_MINOR_VERSION_ARB, minorVersion,

            // コアプロファイル = 固定機能パイプライン(glBegin/glVertex 等)を封印する。
            // Phase 1 で自分で書いた頂点変換とラスタライズを、Day 13 からは
            // シェーダとして GPU 側に書き直すことになるので、
            // 古いAPIに逃げ道を残さないほうが学習には都合がよい。
            Wgl.WGL_CONTEXT_PROFILE_MASK_ARB, Wgl.WGL_CONTEXT_CORE_PROFILE_BIT_ARB,

            0,   // 終端。**書き忘れるとドライバが配列の外を読み続ける**
        ];

        _hglrc = _createContextAttribsARB!(_hdc, IntPtr.Zero, contextAttributes);
        if (_hglrc == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"OpenGL {majorVersion}.{minorVersion} コアプロファイルのコンテキストを作れない");
        }

        // --- 手順 8: カレントにしてから関数をロードする ---
        if (!Wgl.wglMakeCurrent(_hdc, _hglrc))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "wglMakeCurrent に失敗した");
        }

        GL.Load();

        Vendor = GL.GetString(GL.GL_VENDOR);
        Renderer = GL.GetString(GL.GL_RENDERER);
        Version = GL.GetString(GL.GL_VERSION);
        ShadingLanguageVersion = GL.GetString(GL.GL_SHADING_LANGUAGE_VERSION);
        ExtensionCount = GL.GetExtensionCount();
    }

    /// <summary>
    /// 手順 1〜5。**ダミーウィンドウを立てて拡張関数だけ回収し、跡形もなく片付ける。**
    ///
    /// 「一時的にウィンドウを作って捨てる」という、他では滅多に見ない手口を
    /// 取らざるを得ない理由が要点3。GLFW も内部でまったく同じことをしている
    /// (<c>_glfwCreateContextWGL</c> の前に呼ばれる <c>_glfwInitWGL</c> がそれ)。
    /// </summary>
    private static void EnsureExtensionsLoaded()
    {
        if (_extensionsLoaded)
        {
            return;
        }

        IntPtr hInstance = Win32.GetModuleHandleW(null);

        var windowClass = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEXW>(),
            style = Win32.CS_OWNDC,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(DummyWndProc),
            hInstance = hInstance,
            lpszClassName = DummyClassName,
        };

        if (Win32.RegisterClassExW(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ダミーウィンドウクラスの登録に失敗した");
        }

        // 表示しない(WS_VISIBLE を付けない)。画面に一瞬映ることもない。
        IntPtr dummyHwnd = Win32.CreateWindowExW(
            0, DummyClassName, "dummy", Win32.WS_OVERLAPPED,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (dummyHwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ダミーウィンドウの作成に失敗した");
        }

        IntPtr dummyDc = Win32.GetDC(dummyHwnd);
        IntPtr dummyContext = IntPtr.Zero;

        try
        {
            // 古い様式のピクセルフォーマット。中身は何でもよく、
            // 「OpenGL が使えるDC」でありさえすればコンテキストは作れる。
            var pfd = new Wgl.PIXELFORMATDESCRIPTOR
            {
                nSize = (ushort)Marshal.SizeOf<Wgl.PIXELFORMATDESCRIPTOR>(),
                nVersion = 1,
                dwFlags = Wgl.PFD_DRAW_TO_WINDOW | Wgl.PFD_SUPPORT_OPENGL | Wgl.PFD_DOUBLEBUFFER,
                iPixelType = Wgl.PFD_TYPE_RGBA,
                cColorBits = 32,
                cDepthBits = 24,
                cStencilBits = 8,
                iLayerType = Wgl.PFD_MAIN_PLANE,
            };

            int format = Wgl.ChoosePixelFormat(dummyDc, ref pfd);
            if (format == 0 || !Wgl.SetPixelFormat(dummyDc, format, ref pfd))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ダミーのピクセルフォーマット設定に失敗した");
            }

            dummyContext = Wgl.wglCreateContext(dummyDc);
            if (dummyContext == IntPtr.Zero || !Wgl.wglMakeCurrent(dummyDc, dummyContext))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "ダミーコンテキストの作成に失敗した");
            }

            // **ここが目的**。カレントコンテキストがあって初めて拡張関数を引ける。
            _choosePixelFormatARB = LoadWglExtension<Wgl.WglChoosePixelFormatARB>("wglChoosePixelFormatARB");
            _createContextAttribsARB = LoadWglExtension<Wgl.WglCreateContextAttribsARB>("wglCreateContextAttribsARB");

            // VSync だけは「無くても動く」ので、取れなくても失敗にしない。
            // リモートデスクトップ経由などで本当に存在しないことがある。
            _swapIntervalEXT = TryLoadWglExtension<Wgl.WglSwapIntervalEXT>("wglSwapIntervalEXT");
        }
        finally
        {
            // 逆順に片付ける。カレントを外してからでないとコンテキストを消せない。
            Wgl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);

            if (dummyContext != IntPtr.Zero)
            {
                Wgl.wglDeleteContext(dummyContext);
            }

            Win32.ReleaseDC(dummyHwnd, dummyDc);
            Win32.DestroyWindow(dummyHwnd);
            Win32.UnregisterClassW(DummyClassName, hInstance);
        }

        _extensionsLoaded = true;
    }

    private static T LoadWglExtension<T>(string name) where T : Delegate
        => TryLoadWglExtension<T>(name)
           ?? throw new InvalidOperationException(
               $"必須の WGL 拡張が見つからない: {name}(OpenGL 3.0 以降に対応したドライバが要る)");

    private static T? TryLoadWglExtension<T>(string name) where T : Delegate
    {
        IntPtr address = Wgl.wglGetProcAddress(name);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    /// <summary>
    /// 手順 6。本番ウィンドウのピクセルフォーマットを、拡張のほうで選ぶ。
    /// </summary>
    private void SetupPixelFormat()
    {
        // 「キー, 値」の並びで、0 で終端。PIXELFORMATDESCRIPTOR と違って
        // 後から属性を足せるのが利点で、MSAA や sRGB もここに書き足せる。
        int[] attributes =
        [
            Wgl.WGL_DRAW_TO_WINDOW_ARB, 1,
            Wgl.WGL_SUPPORT_OPENGL_ARB, 1,
            Wgl.WGL_DOUBLE_BUFFER_ARB, 1,

            // ソフトウェア実装(Microsoft の GDI Generic)に落ちるのを防ぐ。
            // これを指定しないと、環境によっては OpenGL 1.1 相当の
            // 恐ろしく遅い実装が選ばれることがある。
            Wgl.WGL_ACCELERATION_ARB, Wgl.WGL_FULL_ACCELERATION_ARB,

            Wgl.WGL_PIXEL_TYPE_ARB, Wgl.WGL_TYPE_RGBA_ARB,
            Wgl.WGL_COLOR_BITS_ARB, 32,
            Wgl.WGL_DEPTH_BITS_ARB, 24,
            Wgl.WGL_STENCIL_BITS_ARB, 8,
            0,
        ];

        int[] formats = new int[1];
        if (!_choosePixelFormatARB!(_hdc, attributes, null, 1, formats, out uint formatCount)
            || formatCount == 0)
        {
            throw new InvalidOperationException("条件を満たすピクセルフォーマットが無い");
        }

        // SetPixelFormat は番号だけでなく PIXELFORMATDESCRIPTOR も要求する。
        // 選ばれた番号の内容を問い合わせて、そのまま渡す。
        var pfd = default(Wgl.PIXELFORMATDESCRIPTOR);
        pfd.nSize = (ushort)Marshal.SizeOf<Wgl.PIXELFORMATDESCRIPTOR>();
        Wgl.DescribePixelFormat(_hdc, formats[0], pfd.nSize, ref pfd);

        if (!Wgl.SetPixelFormat(_hdc, formats[0], ref pfd))
        {
            // ここで失敗する典型が「そのウィンドウに既に設定済み」(要点3)。
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetPixelFormat に失敗した");
        }
    }

    /// <summary>
    /// バックバッファを画面に出す。Day 11 の <c>GdiPresenter.Present</c> の後継。
    /// 転送ではなく**表示するバッファの差し替え**なので、
    /// 640x480 でも 4K でもコストはほとんど変わらない。
    /// </summary>
    public void SwapBuffers() => Wgl.SwapBuffers(_hdc);

    /// <summary>
    /// VSync の切り替え。拡張が無い環境では false を返す。
    /// ON にするとモニタのリフレッシュレートに張り付き、
    /// Day 11 まで自前で書いていた <c>WaitUntil</c> が要らなくなる(要点7)。
    /// </summary>
    public bool TrySetSwapInterval(int interval)
        => _swapIntervalEXT is not null && _swapIntervalEXT(interval);

    public bool HasSwapControl => _swapIntervalEXT is not null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 破棄の順序は生成の逆。カレントのまま削除しようとすると失敗する。
        Wgl.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);

        if (_hglrc != IntPtr.Zero)
        {
            Wgl.wglDeleteContext(_hglrc);
            _hglrc = IntPtr.Zero;
        }

        if (_hdc != IntPtr.Zero)
        {
            Win32.ReleaseDC(_hwnd, _hdc);
        }
    }
}
