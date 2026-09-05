using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// シェーダの管理クラス。**Day 14 の中心**。
///
/// Day 13 の <c>Shader</c> は「C# の定数からコンパイルする」だけだったが、
/// エンジンの部品として使うにはそれでは足りない。ここで足すのは3つ。
///
///   1. **ファイルから読む** — シェーダをコードから追い出す。
///      GLSL はC#と別言語なので、別ファイルにしたほうが編集も差分も素直になる
///   2. **ホットリロード** — 実行したまま作り直す。
///      シェーダを書き換えるたびに再起動していると、1日に何十回も待つことになる
///   3. **型別の uniform 設定と場所のキャッシュ** — 呼び出し側が
///      <c>glGetUniformLocation</c> や float の並びを意識しなくて済むようにする
///
/// **失敗しても落ちない**ことが特に重要。書き換えたシェーダが通らなかったときに
/// アプリが終了してしまうと、結局再起動と同じで意味が半減する。
/// コンパイルに失敗したら**古いプログラムを使い続ける**。
/// </summary>
internal sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly string _vertexPath;
    private readonly string _fragmentPath;

    /// <summary>
    /// uniform の場所を名前で引いた結果の記憶。
    /// <c>GetUniformLocation</c> は文字列比較を伴うので毎フレーム呼ぶものではない。
    /// **リロードすると場所は変わりうる**ので、そのたびに捨てる。
    /// </summary>
    private readonly Dictionary<string, int> _uniformLocations = [];

    /// <summary>一度警告した uniform を覚えておく(毎フレーム同じ警告を出さないため)。</summary>
    private readonly HashSet<string> _warnedUniforms = [];

    private uint _program;
    private bool _disposed;

    public Shader(GL gl, string vertexPath, string fragmentPath)
    {
        _gl = gl;
        _vertexPath = vertexPath;
        _fragmentPath = fragmentPath;

        // 起動時だけは失敗を許さない。最初の1本が通らないなら設定の問題なので、
        // 黙って進むより早く落ちたほうがよい。
        if (!TryCreateProgram(out uint program, out string error))
        {
            throw new InvalidOperationException($"シェーダの作成に失敗した:\n{error}");
        }

        _program = program;
    }

    /// <summary>
    /// ファイルを読み直してシェーダを作り直す。**失敗しても現状を壊さない**。
    /// </summary>
    /// <returns>成功したら true。false のときは古いシェーダのまま。</returns>
    public bool TryReload()
    {
        if (!TryCreateProgram(out uint newProgram, out string error))
        {
            Console.WriteLine($"[リロード失敗] 古いシェーダを使い続けます:\n{error}");
            return false;
        }

        // **新しいものが出来てから古いものを消す**。
        // 先に消してしまうと、失敗したときに描くものが無くなる。
        _gl.DeleteProgram(_program);
        _program = newProgram;

        // 場所は作り直したプログラムでは変わりうる。キャッシュは必ず捨てる。
        _uniformLocations.Clear();
        _warnedUniforms.Clear();

        Console.WriteLine("[リロード成功]");
        return true;
    }

    /// <summary>
    /// このシェーダを使う状態にする。
    /// uniform の設定は**この後**に行うこと(uniform はプログラムごとの状態なので)。
    /// </summary>
    public void Use() => _gl.UseProgram(_program);

    public void SetFloat(string name, float value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    public void SetInt(string name, int value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    public void SetVector2(string name, Vector2 value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            _gl.Uniform2(location, value.X, value.Y);
        }
    }

    public void SetVector3(string name, Vector3 value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            _gl.Uniform3(location, value.X, value.Y, value.Z);
        }
    }

    public void SetVector4(string name, Vector4 value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            _gl.Uniform4(location, value.X, value.Y, value.Z, value.W);
        }
    }

    /// <summary>
    /// 4x4 行列を送る。
    ///
    /// **転置フラグが false でよい理由**(Day 13 の要点5の続き)。
    /// <see cref="Matrix4x4"/> はメモリ上で行優先(M11,M12,M13,M14,M21,...)に並び、
    /// 掛け算の規約も行ベクトル(v * M)。一方 OpenGL は列優先で読み、
    /// GLSL では列ベクトル(M * v)で使う。
    ///
    /// この**2つの食い違いはちょうど打ち消し合う**。
    /// 行優先のメモリを列優先として読むと転置になり、
    /// 転置は行ベクトル規約を列ベクトル規約に変換する操作そのものだから。
    /// 結果として <c>transpose: false</c> のまま素直に渡せばよい。
    /// (実際に描いて確かめたので、疑わしければ改造課題1で true にしてみるとよい)
    /// </summary>
    public unsafe void SetMatrix4(string name, in Matrix4x4 value)
    {
        int location = GetUniformLocation(name);
        if (location < 0)
        {
            return;
        }

        // Matrix4x4 は 16個の float が隙間なく並んだ構造体なので、
        // 先頭アドレスをそのまま float* として渡せる。
        fixed (Matrix4x4* pointer = &value)
        {
            _gl.UniformMatrix4(location, 1, false, (float*)pointer);
        }
    }


    /// <summary>
    /// 3x3 行列を送る。**法線行列専用**(Day 32)。
    ///
    /// <see cref="Matrix4x4"/> のような 3x3 の型が System.Numerics に無いので、
    /// 4x4 を受け取って左上 3x3 だけを取り出す。
    ///
    /// <b>ここで詰め直しが要る</b>のが 4x4 との違い。
    /// GLSL の mat3 は「3 float の列が3本」で詰めて並ぶが、
    /// 4x4 のメモリから左上を取ると 4 float ごとに飛び飛びになる。
    /// <c>SetMatrix4</c> のようにポインタをそのまま渡すと、
    /// **2列目以降が1つずつずれた行列**になり、法線が妙な向きを向く。
    /// </summary>
    public unsafe void SetMatrix3(string name, in Matrix4x4 value)
    {
        int location = GetUniformLocation(name);
        if (location < 0)
        {
            return;
        }

        Span<float> packed =
        [
            value.M11, value.M12, value.M13,
            value.M21, value.M22, value.M23,
            value.M31, value.M32, value.M33,
        ];

        fixed (float* pointer = packed)
        {
            // transpose: false のままでよい理由は SetMatrix4 と同じ
            // (行優先のメモリを列優先で読ませると転置になり、規約の違いと打ち消し合う)。
            _gl.UniformMatrix3(location, 1, false, pointer);
        }
    }
    private int GetUniformLocation(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int cached))
        {
            return cached;
        }

        int location = _gl.GetUniformLocation(_program, name);

        // **-1 はエラーとは限らない**。名前の間違いのほかに、
        // 「宣言はあるが結果に影響しないので最適化で削除された」場合もこうなる。
        // 判別できないので、気付けるように1回だけ知らせる。
        if (location < 0 && _warnedUniforms.Add(name))
        {
            Console.WriteLine($"[警告] uniform '{name}' が見つかりません(未使用で削除された可能性)");
        }

        _uniformLocations[name] = location;
        return location;
    }

    /// <summary>
    /// ファイルを読んでコンパイル・リンクする。
    /// 例外を投げずに <paramref name="error"/> で返すのは、
    /// ホットリロードの経路で「失敗したがそのまま続ける」を素直に書けるようにするため。
    /// </summary>
    private bool TryCreateProgram(out uint program, out string error)
    {
        program = 0;
        error = string.Empty;

        string vertexSource;
        string fragmentSource;
        try
        {
            // 保存直後は別プロセスがまだファイルを掴んでいることがある。
            // 読めなかったのも「失敗」として扱い、次のリロードに任せる。
            vertexSource = File.ReadAllText(_vertexPath);
            fragmentSource = File.ReadAllText(_fragmentPath);
        }
        catch (IOException ex)
        {
            error = $"ファイルを読めません: {ex.Message}";
            return false;
        }

        if (!TryCompile(ShaderType.VertexShader, vertexSource, _vertexPath, out uint vertexShader, out error))
        {
            return false;
        }

        if (!TryCompile(ShaderType.FragmentShader, fragmentSource, _fragmentPath, out uint fragmentShader, out error))
        {
            // 片方だけ出来ている状態を残さない。
            _gl.DeleteShader(vertexShader);
            return false;
        }

        uint handle = _gl.CreateProgram();
        _gl.AttachShader(handle, vertexShader);
        _gl.AttachShader(handle, fragmentShader);
        _gl.LinkProgram(handle);

        _gl.GetProgram(handle, ProgramPropertyARB.LinkStatus, out int linked);

        // リンクが済めば個々のシェーダオブジェクトは不要。C/C++ の .obj と同じ。
        _gl.DeleteShader(vertexShader);
        _gl.DeleteShader(fragmentShader);

        if (linked == 0)
        {
            error = $"リンクに失敗しました:\n{_gl.GetProgramInfoLog(handle)}";
            _gl.DeleteProgram(handle);
            return false;
        }

        program = handle;
        return true;
    }

    private bool TryCompile(ShaderType type, string source, string path, out uint shader, out string error)
    {
        error = string.Empty;
        shader = _gl.CreateShader(type);

        // Silk.NET が string を受け取ってくれる。Day 13 で自分で書いていた
        // 「UTF-8 のバイト列にして char** に詰める」処理は、この1行に吸収された。
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled != 0)
        {
            return true;
        }

        // ログの行番号はファイルの行番号と一致するので、ファイル名を添えておくと
        // エディタからそのまま飛べる。
        error = $"{Path.GetFileName(path)} のコンパイルに失敗しました:\n{_gl.GetShaderInfoLog(shader)}";
        _gl.DeleteShader(shader);
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // GPU のリソースは GC の管轄外。自分で消す。
        _gl.DeleteProgram(_program);
    }
}
