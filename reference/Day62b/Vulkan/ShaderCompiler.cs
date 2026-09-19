using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;

namespace HardwareRayTracer;

/// <summary>
/// GLSL を SPIR-V に翻訳する(今日の要点2)。
///
/// <para>
/// OpenGL では <c>glShaderSource</c> に GLSL の文字列をそのまま渡せた。ドライバの中に
/// GLSL のコンパイラが入っていたからで、それは<b>ベンダごとに別のコンパイラ</b>が
/// 別の解釈をするということでもあった(同じシェーダが NVIDIA で通って AMD で通らない)。
/// </para>
/// <para>
/// Vulkan はそこを切った。ドライバが受け取るのは <b>SPIR-V</b> という中間表現だけで、
/// 文法の解釈は<b>アプリ側の責任</b>になった。おかげで「手元で通れば、どの GPU でも同じ形が届く」。
/// 代わりに翻訳する道具が要る——それが shaderc(glslang の包み)。
/// </para>
/// <para>
/// 本来は<b>ビルド時に</b> glslc を叩いて .spv を作り、実行時はそれを読むだけにする。
/// ここで実行時に翻訳しているのは学習用の都合で、<b>シェーダを書き換えて再実行するだけで試せる</b>
/// ようにしておきたいから(Day 61 と同じ)。エラーが出たら行番号付きで受け取れるのも都合がよい。
/// </para>
/// </summary>
internal sealed class ShaderCompiler : IDisposable
{
    private readonly Shaderc _api;
    private readonly unsafe Compiler* _compiler;
    private readonly unsafe CompileOptions* _options;

    public unsafe ShaderCompiler()
    {
        _api = Shaderc.GetApi();
        _compiler = _api.CompilerInitialize();
        _options = _api.CompileOptionsInitialize();

        // 翻訳先を指定する。**ここを間違えると通らない**。
        //   TargetEnv.Vulkan + Vulkan12 … Vulkan 1.2 向けの規則で解釈する
        //                                 (OpenGL 向けにすると descriptor set の書き方が変わる)
        //   SpirvVersion.Shaderc15     … SPIR-V 1.5。Vulkan 1.2 が受け取れる最も新しい版。
        //                                 Day 62b で使う ray query は SPIR-V 1.4 以上が要る
        _api.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan12);
        _api.CompileOptionsSetTargetSpirv(_options, SpirvVersion.Shaderc15);

        // 最適化を掛ける。掛けないと SPIR-V が 3〜4 倍に膨らみ、ドライバ側の翻訳も遅くなる。
        // デバッグしたいときは Zero にすると、変数名が SPIR-V に残って RenderDoc で読める。
        _api.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Performance);
    }

    /// <summary>
    /// ファイルを読んで翻訳する。失敗したら <see cref="ShaderCompilationException"/>。
    /// </summary>
    /// <param name="path">.comp ファイルのパス。名前はエラーメッセージに出るだけ。</param>
    /// <param name="defines">
    /// 定義するマクロ(Day 62b で追加)。<b>1本の GLSL から2通りの SPIR-V を作る</b>のに使う。
    ///
    /// <para>
    /// 今日は同じ <c>trace.comp</c> を <c>USE_RAY_QUERY</c> あり/なしの2回翻訳して、
    /// 総当たり版と ray query 版の2つのパイプラインを作る。
    /// シェーダを2本に分けると 250 行が丸ごと重複して、<b>片方だけ直して食い違う</b>
    /// 事故が起きる(実際、影の光線の <c>tMin</c> を片方だけ直しかけた)。
    /// </para>
    /// <para>
    /// C++ 側では <c>glslc -DUSE_RAY_QUERY=1</c> に当たる。
    /// </para>
    /// </param>
    public unsafe uint[] CompileComputeFile(string path, params string[] defines)
    {
        string source = File.ReadAllText(path);
        return Compile(source, Path.GetFileName(path), ShaderKind.ComputeShader, defines);
    }

    /// <summary>
    /// 翻訳の本体。
    ///
    /// <para>
    /// 返すのが <c>byte[]</c> ではなく <c>uint[]</c> なのは、SPIR-V が<b>32 ビット語の並び</b>と
    /// 定義されているから。Vulkan の <c>ShaderModuleCreateInfo.PCode</c> も <c>uint*</c> を取る。
    /// byte[] のまま渡すと、配列の先頭が 4 バイト境界に乗っている保証が無くて危うい。
    /// </para>
    /// </summary>
    public unsafe uint[] Compile(string source, string name, ShaderKind kind, params string[] defines)
    {
        byte[] sourceBytes = System.Text.Encoding.UTF8.GetBytes(source);

        // マクロは options に積むので、**呼び出しごとに複製する**。
        // 使い回すと前回の定義が残り、「1回目は総当たり、2回目も総当たり」になる。
        CompileOptions* options = defines.Length == 0 ? _options : _api.CompileOptionsClone(_options);
        var macroPointers = new List<nint>();

        // shaderc は C の API なので、文字列は ANSI のポインタで渡して自分で解放する。
        nint namePtr = Marshal.StringToHGlobalAnsi(name);
        nint entryPtr = Marshal.StringToHGlobalAnsi("main");
        try
        {
            foreach (string define in defines)
            {
                nint macro = Marshal.StringToHGlobalAnsi(define);
                nint value = Marshal.StringToHGlobalAnsi("1");
                macroPointers.Add(macro);
                macroPointers.Add(value);
                _api.CompileOptionsAddMacroDefinition(
                    options, (byte*)macro, (nuint)define.Length, (byte*)value, 1);
            }

            CompilationResult* result;
            fixed (byte* pSource = sourceBytes)
            {
                result = _api.CompileIntoSpv(
                    _compiler, pSource, (nuint)sourceBytes.Length, kind,
                    (byte*)namePtr, (byte*)entryPtr, options);
            }

            try
            {
                if (_api.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                {
                    string message = SilkMarshal.PtrToString((nint)_api.ResultGetErrorMessage(result)) ?? "(理由不明)";
                    throw new ShaderCompilationException($"{name} の翻訳に失敗しました。\n{message}");
                }

                nuint byteLength = _api.ResultGetLength(result);
                var words = new uint[byteLength / sizeof(uint)];
                fixed (uint* pWords = words)
                {
                    Buffer.MemoryCopy(_api.ResultGetBytes(result), pWords, byteLength, byteLength);
                }

                return words;
            }
            finally
            {
                _api.ResultRelease(result);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(entryPtr);
            foreach (nint pointer in macroPointers)
            {
                Marshal.FreeHGlobal(pointer);
            }

            if (options != _options)
            {
                _api.CompileOptionsRelease(options);
            }
        }
    }

    public unsafe void Dispose()
    {
        _api.CompileOptionsRelease(_options);
        _api.CompilerRelease(_compiler);
        _api.Dispose();
    }
}

/// <summary>GLSL の翻訳に失敗した。メッセージに shaderc の出力(行番号付き)がそのまま入る。</summary>
internal sealed class ShaderCompilationException(string message) : Exception(message);
