using System.Runtime.InteropServices;

namespace RawGL;

/// <summary>
/// WGL(Windows OpenGL)の宣言。**Win32 と OpenGL を繋ぐ接着剤**。
///
/// OpenGL の仕様そのものには「ウィンドウ」の概念が無い。
/// 描き先をどう用意して、どのスレッドのどのウィンドウに結び付けるかは
/// **プラットフォームごとの別APIの仕事**で、Windows では WGL、
/// Linux/X11 では GLX、macOS では CGL(現在は非推奨)が担当する。
/// GLFW や SDL、Silk.NET の Windowing が吸収しているのがまさにこの層で、
/// Day 12 はその中身を1回だけ自分で書く。
///
/// 関数の出どころが2つに分かれている点に注意。
/// - <c>wgl*</c> … opengl32.dll がエクスポートしている
/// - <c>ChoosePixelFormat</c> / <c>SetPixelFormat</c> / <c>SwapBuffers</c> … **gdi32.dll**
///   (ピクセルフォーマットは「DCの設定」なので GDI 側の管轄という整理)
/// </summary>
internal static class Wgl
{
    // ================= PIXELFORMATDESCRIPTOR のフラグ =================

    public const uint PFD_DOUBLEBUFFER = 0x00000001;
    public const uint PFD_DRAW_TO_WINDOW = 0x00000004;
    public const uint PFD_SUPPORT_OPENGL = 0x00000020;

    public const byte PFD_TYPE_RGBA = 0;
    public const byte PFD_MAIN_PLANE = 0;

    /// <summary>
    /// 「どんな描き先が欲しいか」の希望を書いて渡す構造体。
    ///
    /// これは **OpenGL 1.0(1992年)時代の様式**で、素の
    /// <c>ChoosePixelFormat</c> はここに書かれた項目しか見てくれない。
    /// マルチサンプル(MSAA)や sRGB フレームバッファといった後発の要求は
    /// この構造体では表現できず、そのために
    /// <c>wglChoosePixelFormatARB</c>(要点3)が後から用意された。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint dwFlags;
        public byte iPixelType;
        public byte cColorBits;
        public byte cRedBits;
        public byte cRedShift;
        public byte cGreenBits;
        public byte cGreenShift;
        public byte cBlueBits;
        public byte cBlueShift;
        public byte cAlphaBits;
        public byte cAlphaShift;
        public byte cAccumBits;
        public byte cAccumRedBits;
        public byte cAccumGreenBits;
        public byte cAccumBlueBits;
        public byte cAccumAlphaBits;
        public byte cDepthBits;
        public byte cStencilBits;
        public byte cAuxBuffers;
        public byte iLayerType;
        public byte bReserved;
        public uint dwLayerMask;
        public uint dwVisibleMask;
        public uint dwDamageMask;
    }

    // ================= gdi32: ピクセルフォーマットとスワップ =================

    /// <summary>
    /// 希望に一番近いピクセルフォーマットの**番号**を返す。0 なら失敗。
    /// 「一番近い」の判定基準はドライバ任せで、要求どおりとは限らない。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR ppfd);

    /// <summary>
    /// DC にピクセルフォーマットを設定する。
    ///
    /// **1つのウィンドウにつき生涯1回しか呼べない**。これが Day 12 の全部の元凶で、
    /// ダミーウィンドウが要る理由そのもの(要点3)。
    /// 2回目は黙って失敗するか、成功したように見えて壊れる。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR ppfd);

    /// <summary>
    /// 実際に選ばれたピクセルフォーマットの内容を問い合わせる。
    /// 「要求どおりのものが取れたか」を確認するために使う。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int DescribePixelFormat(
        IntPtr hdc, int format, uint nBytes, ref PIXELFORMATDESCRIPTOR ppfd);

    /// <summary>
    /// バックバッファとフロントバッファを入れ替える。
    /// Day 11 の <c>StretchDIBits</c> に代わる「画面に出す」操作。
    /// 転送しているのではなく**表示するバッファを差し替えている**だけなので、
    /// 解像度によらずコストがほぼ一定になる。
    /// </summary>
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool SwapBuffers(IntPtr hdc);

    // ================= opengl32.dll: コンテキスト =================

    /// <summary>
    /// レンダリングコンテキスト(HGLRC)を作る。
    /// この関数で作れるのは**古い様式のコンテキスト**だけで、
    /// バージョンやプロファイルは指定できない(要点4)。
    /// </summary>
    [DllImport("opengl32.dll", SetLastError = true)]
    public static extern IntPtr wglCreateContext(IntPtr hdc);

    [DllImport("opengl32.dll", SetLastError = true)]
    public static extern bool wglDeleteContext(IntPtr hglrc);

    /// <summary>
    /// コンテキストを**このスレッドの**カレントにする。
    ///
    /// OpenGL の状態はすべて「カレントコンテキスト」に紐付く。
    /// gl 関数に引数としてコンテキストを渡さないのはこのため
    /// (Vulkan や D3D12 が明示的にデバイスを渡すのと対照的な、古い設計)。
    /// カレントにせずに gl 関数を呼ぶと、何も起きないか落ちる。
    /// 両方に IntPtr.Zero を渡すとカレント解除。
    /// </summary>
    [DllImport("opengl32.dll", SetLastError = true)]
    public static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);

    /// <summary>
    /// 拡張関数のアドレスを引く。**カレントコンテキストが必要**。
    /// しかも OpenGL 1.1 の関数には使えない(要点5)。
    /// </summary>
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi, SetLastError = true, ExactSpelling = true)]
    public static extern IntPtr wglGetProcAddress(string lpszProc);

    // ================= WGL_ARB_pixel_format の属性 =================
    //
    // wglChoosePixelFormatARB に「キー, 値, キー, 値, ..., 0」の配列で渡す。
    // PIXELFORMATDESCRIPTOR と違い、後から属性を足せるのが利点。

    public const int WGL_DRAW_TO_WINDOW_ARB = 0x2001;
    public const int WGL_ACCELERATION_ARB = 0x2003;
    public const int WGL_SUPPORT_OPENGL_ARB = 0x2010;
    public const int WGL_DOUBLE_BUFFER_ARB = 0x2011;
    public const int WGL_PIXEL_TYPE_ARB = 0x2013;
    public const int WGL_COLOR_BITS_ARB = 0x2014;
    public const int WGL_DEPTH_BITS_ARB = 0x2022;
    public const int WGL_STENCIL_BITS_ARB = 0x2023;
    public const int WGL_FULL_ACCELERATION_ARB = 0x2027;
    public const int WGL_TYPE_RGBA_ARB = 0x202B;

    /// <summary>
    /// マルチサンプル(MSAA)の指定。今日は使わない。
    /// 画面を単色で塗るだけでは効果が見えず、意味を持つのは三角形を描く Day 13 から。
    /// 旧来の PIXELFORMATDESCRIPTOR では**表現できない**属性の代表例なので、
    /// 「なぜ ARB 版が必要になったか」の実例として宣言だけしておく。
    /// </summary>
    public const int WGL_SAMPLE_BUFFERS_ARB = 0x2041;

    public const int WGL_SAMPLES_ARB = 0x2042;

    // ================= WGL_ARB_create_context の属性 =================

    public const int WGL_CONTEXT_MAJOR_VERSION_ARB = 0x2091;
    public const int WGL_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    public const int WGL_CONTEXT_FLAGS_ARB = 0x2094;
    public const int WGL_CONTEXT_PROFILE_MASK_ARB = 0x9126;

    /// <summary>デバッグコンテキスト。glDebugMessageCallback が使えるようになる。</summary>
    public const int WGL_CONTEXT_DEBUG_BIT_ARB = 0x0001;

    public const int WGL_CONTEXT_FORWARD_COMPATIBLE_BIT_ARB = 0x0002;

    /// <summary>
    /// コアプロファイル。**固定機能パイプラインが使えなくなる**。
    /// 「使えなくなる」のは制約ではなく保証で、うっかり古いAPIを呼んだら
    /// エラーになってくれるほうが、Phase 1 で学んだ知識をGPU側に写す上で分かりやすい。
    /// </summary>
    public const int WGL_CONTEXT_CORE_PROFILE_BIT_ARB = 0x00000001;

    public const int WGL_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB = 0x00000002;

    // ================= 拡張関数のシグネチャ =================
    //
    // これらは opengl32.dll には無い。ドライバの中にあり、
    // wglGetProcAddress で実行時に引いてくるしかない。
    // だから [DllImport] ではなく「デリゲート型」として宣言する。

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool WglChoosePixelFormatARB(
        IntPtr hdc,
        int[] piAttribIList,
        float[]? pfAttribFList,
        uint nMaxFormats,
        int[] piFormats,
        out uint nNumFormats);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WglCreateContextAttribsARB(IntPtr hdc, IntPtr hShareContext, int[] attribList);

    /// <summary>
    /// VSync(垂直同期)の設定。1 で同期、0 で無制限、-1 で適応的同期。
    /// Day 11 で自前の <c>WaitUntil</c> がやっていたフレームレート制限を、
    /// ここから先はドライバに任せられる(要点7)。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool WglSwapIntervalEXT(int interval);
}
