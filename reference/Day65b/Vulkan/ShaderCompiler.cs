using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;

namespace MeshletRenderer;

/// <summary>
/// GLSL を SPIR-V に翻訳する(Day 62a の要点2。Day 62c から持ってきた)。
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
        //                                 メッシュシェーダ(GL_EXT_mesh_shader)も SPIR-V 1.4 以上が要る
        //                                 (device を 1.3 にしても、1.2 向けの SPIR-V はそのまま通る)
        _api.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan12);
        _api.CompileOptionsSetTargetSpirv(_options, SpirvVersion.Shaderc15);

        // 最適化を掛ける。掛けないと SPIR-V が 3〜4 倍に膨らみ、ドライバ側の翻訳も遅くなる。
        // デバッグしたいときは Zero にすると、変数名が SPIR-V に残って RenderDoc で読める。
        _api.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Performance);
    }

    /// <summary>
    /// ファイルを読んで、種類を指定して翻訳する。失敗したら <see cref="ShaderCompilationException"/>。
    ///
    /// <para>
    /// 今日の種類は3つ。<c>.vert</c>(<see cref="ShaderKind.VertexShader"/>)、
    /// <c>.mesh</c>(<see cref="ShaderKind.MeshShader"/>)、<c>.frag</c>(<see cref="ShaderKind.FragmentShader"/>)。
    /// <b>拡張子から種類を当てない</b>のは Day 62c と同じ理由で、「どの段に挿すか」は呼び出し側が知っているから。
    /// </para>
    /// </summary>
    /// <param name="path">シェーダのパス。名前はエラーメッセージに出るだけ。</param>
    /// <param name="kind">段の種類。<b>GLSL の文法が段ごとに違う</b>ので、翻訳する前に要る
    /// (<c>SetMeshOutputsEXT</c> はメッシュシェーダの中でしか書けない、など)。</param>
    /// <param name="defines">
    /// 定義するマクロ。<b>1本の GLSL から何通りかの SPIR-V を作る</b>のに使う(Day 62b で書いたもの)。
    /// C++ 側では <c>glslc -DNAME=1</c> に当たる。
    /// </param>
    public unsafe uint[] CompileFile(string path, ShaderKind kind, params string[] defines)
    {
        string source = ResolveIncludes(path);
        return Compile(source, Path.GetFileName(path), kind, defines);
    }

    /// <summary>
    /// <c>#include "..."</c> を、そのファイルの中身で置き換える(Day 62c で書いたもの)。
    ///
    /// <para>
    /// <b>GLSL に #include は無い</b>。正確には拡張(<c>GL_GOOGLE_include_directive</c>)があり、
    /// shaderc も<b>インクルードの解決を呼び出し側に丸投げする仕組み</b>
    /// (<c>shaderc_compile_options_set_include_callbacks</c>)を持っているが、
    /// C の関数ポインタを2本渡す作りで、C# から使うと 60 行ほどの受け皿が要る。
    /// </para>
    /// <para>
    /// ここでは<b>翻訳に渡す前に文字列として差し込む</b>——「SPIR-V の世界では、
    /// ソースをどう組み立てるかはアプリの責任」という Day 62a の要点2 の話が、いちばん素朴な形で出ている。
    /// 行番号がずれるのが難点だが、入れ子を許さない(1段だけ)ので実害は小さい。
    /// </para>
    /// </summary>
    private static string ResolveIncludes(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        var builder = new System.Text.StringBuilder();

        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                builder.AppendLine(line);
                continue;
            }

            int first = trimmed.IndexOf('"');
            int last = trimmed.LastIndexOf('"');
            if (first < 0 || last <= first)
            {
                throw new ShaderCompilationException($"{Path.GetFileName(path)}: #include の書き方が違います: {line}");
            }

            string name = trimmed[(first + 1)..last];
            string included = Path.Combine(directory ?? ".", name);
            if (!File.Exists(included))
            {
                throw new ShaderCompilationException($"{Path.GetFileName(path)}: {name} が見つかりません。");
            }

            // **入れ子は許さない**。許すと循環の検出が要るし、common.glsl 1本なら1段で足りる。
            builder.AppendLine($"// ---- begin {name} ----");
            builder.AppendLine(File.ReadAllText(included));
            builder.AppendLine($"// ---- end {name} ----");
        }

        return builder.ToString();
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
        // 使い回すと前回の定義が残り、2回目の翻訳に1回目のマクロが混ざる。
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
