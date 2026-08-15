using System.Runtime.InteropServices;

namespace RawGL;

/// <summary>
/// OpenGL の関数を実行時にロードして呼び出せるようにする層。
/// **Silk.NET が生成しているものを手で書いたら何になるか**、がこのファイル。
///
/// なぜ <c>[DllImport("opengl32.dll")]</c> で済まないのか。
/// opengl32.dll が実際にエクスポートしているのは **OpenGL 1.1 までの関数だけ**で、
/// これは 1996年(Windows NT 4.0)に凍結されている。
/// それ以降に追加された数千の関数は**GPUドライバの DLL の中**にあり、
/// 名前も存在もコンパイル時には分からない。だから実行時に引くしかない。
///
/// この「1.1 までは静的、それ以降は動的」という二重構造が、
/// 要点5で扱う面倒の元凶になっている。
/// </summary>
internal static class GL
{
    // ================= 定数 =================

    public const uint GL_NO_ERROR = 0;
    public const uint GL_INVALID_ENUM = 0x0500;
    public const uint GL_INVALID_VALUE = 0x0501;
    public const uint GL_INVALID_OPERATION = 0x0502;

    public const uint GL_DEPTH_BUFFER_BIT = 0x00000100;
    public const uint GL_COLOR_BUFFER_BIT = 0x00004000;

    public const uint GL_VENDOR = 0x1F00;
    public const uint GL_RENDERER = 0x1F01;
    public const uint GL_VERSION = 0x1F02;
    public const uint GL_EXTENSIONS = 0x1F03;
    public const uint GL_SHADING_LANGUAGE_VERSION = 0x8B8C;

    /// <summary>
    /// 拡張の個数。コアプロファイルでは <c>glGetString(GL_EXTENSIONS)</c> が
    /// 使えなくなった代わりに、この値と <c>glGetStringi</c> で1つずつ取る(要点6)。
    /// </summary>
    public const uint GL_NUM_EXTENSIONS = 0x821D;

    // ================= 関数のシグネチャ =================
    //
    // OpenGL の関数は Windows では __stdcall(APIENTRY)。
    // CallingConvention.Winapi がプラットフォーム標準に解決してくれる。

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr GlGetString(uint name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr GlGetStringi(uint name, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void GlGetIntegerv(uint pname, out int data);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void GlClearColor(float red, float green, float blue, float alpha);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void GlClear(uint mask);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void GlViewport(int x, int y, int width, int height);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint GlGetError();

    // ================= ロード済みの関数 =================
    //
    // null! で初期化しているのは、Load() を呼ぶまで使えないことを承知のうえで
    // 呼び出し側に null チェックを強いないため。
    // コンテキストを作る前に触ったら NullReferenceException で落ちるのが正しい。

    public static GlGetString glGetString = null!;
    public static GlGetStringi glGetStringi = null!;
    public static GlGetIntegerv glGetIntegerv = null!;
    public static GlClearColor glClearColor = null!;
    public static GlClear glClear = null!;
    public static GlViewport glViewport = null!;
    public static GlGetError glGetError = null!;

    /// <summary>opengl32.dll のモジュールハンドル。1.1 の関数を引く先。</summary>
    private static IntPtr _opengl32;

    /// <summary>
    /// 関数を1つ引く。**OpenGL 関数ロードのすべての面倒がここに詰まっている**(要点5)。
    /// </summary>
    private static IntPtr GetProcAddress(string name)
    {
        // まずドライバに聞く。1.2 以降の関数はここでしか取れない。
        IntPtr address = Wgl.wglGetProcAddress(name);

        // **失敗の表し方が統一されていない**。
        // 仕様上は NULL だが、実際のドライバは 1 / 2 / 3 / -1 を返すことがある。
        // 有名な地雷で、GLAD や GLEW も同じ判定を持っている。
        if (address == IntPtr.Zero
            || address == new IntPtr(1)
            || address == new IntPtr(2)
            || address == new IntPtr(3)
            || address == new IntPtr(-1))
        {
            // ドライバが知らない = OpenGL 1.1 以前の関数。
            // これらは opengl32.dll が直接エクスポートしているので、そちらから引く。
            // **この2段構えを忘れると glClear すら取れない**。
            address = Win32.GetProcAddress(_opengl32, name);
        }

        return address;
    }

    /// <summary>
    /// 関数ポインタを C# から呼べるデリゲートに変換する。
    /// 取れなかったら即座に例外にする——後で NullReferenceException になって
    /// 「どの関数が無かったのか」が分からなくなるほうが困る。
    /// </summary>
    private static T Load<T>(string name) where T : Delegate
    {
        IntPtr address = GetProcAddress(name);
        if (address == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenGL 関数が見つからない: {name}");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    /// <summary>
    /// 今日使う関数をまとめてロードする。
    /// **カレントコンテキストがある状態で呼ぶこと**(wglGetProcAddress の前提)。
    ///
    /// Silk.NET はこれを数千関数ぶん自動生成している。
    /// 「一度やれば十分な領域」とロードマップに書いてあるのはこの作業のこと。
    /// </summary>
    public static void Load()
    {
        if (_opengl32 == IntPtr.Zero)
        {
            _opengl32 = Win32.LoadLibraryW("opengl32.dll");
            if (_opengl32 == IntPtr.Zero)
            {
                throw new InvalidOperationException("opengl32.dll を読み込めない");
            }
        }

        // --- OpenGL 1.1 の関数(wglGetProcAddress では取れず、DLL から引かれる)---
        glGetString = Load<GlGetString>("glGetString");
        glGetIntegerv = Load<GlGetIntegerv>("glGetIntegerv");
        glClearColor = Load<GlClearColor>("glClearColor");
        glClear = Load<GlClear>("glClear");
        glViewport = Load<GlViewport>("glViewport");
        glGetError = Load<GlGetError>("glGetError");

        // --- OpenGL 3.0 の関数(ドライバからしか取れない)---
        // これが取れたことが「拡張ロードの仕組みが動いている」証明になる。
        glGetStringi = Load<GlGetStringi>("glGetStringi");
    }

    /// <summary>
    /// <c>glGetString</c> の結果を C# の文字列にする。
    /// 返ってくるのは NUL 終端のバイト列へのポインタで、**解放してはいけない**
    /// (ドライバが持っている定数領域)。
    /// </summary>
    public static string GetString(uint name)
    {
        IntPtr pointer = glGetString(name);
        return pointer == IntPtr.Zero ? "(取得できず)" : Marshal.PtrToStringUTF8(pointer) ?? "(空)";
    }

    /// <summary>拡張の個数。コアプロファイルではこちらを使う。</summary>
    public static int GetExtensionCount()
    {
        glGetIntegerv(GL_NUM_EXTENSIONS, out int count);
        return count;
    }

    public static string GetExtension(int index)
    {
        IntPtr pointer = glGetStringi(GL_EXTENSIONS, (uint)index);
        return pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;
    }

    /// <summary>
    /// 直前の操作でエラーが出ていないか確認する。
    ///
    /// OpenGL は例外を投げず、**エラーを内部のフラグに積むだけで黙って続行する**。
    /// glGetError を呼ぶまで気付けず、しかも呼ぶとフラグは消える。
    /// そのため「どこまでは正常だったか」を確かめるには、要所で挟むしかない。
    /// (デバッグコンテキストと glDebugMessageCallback を使えば
    ///  エラー時にコールバックで通知してもらえる。改造課題2で扱う)
    /// </summary>
    public static void CheckError(string where)
    {
        uint error = glGetError();
        if (error == GL_NO_ERROR)
        {
            return;
        }

        string name = error switch
        {
            GL_INVALID_ENUM => "GL_INVALID_ENUM",
            GL_INVALID_VALUE => "GL_INVALID_VALUE",
            GL_INVALID_OPERATION => "GL_INVALID_OPERATION",
            _ => $"0x{error:X4}",
        };

        Console.WriteLine($"[GL エラー] {where}: {name}");
    }
}
