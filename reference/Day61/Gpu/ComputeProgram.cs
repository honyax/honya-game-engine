using System.Numerics;
using Silk.NET.OpenGL;

namespace CpuRayTracer;

/// <summary>
/// コンピュートシェーダ1本ぶんのプログラム。<b>読む・組み立てる・uniform を渡す</b>だけの薄い皮。
///
/// <para>
/// Day 14 の <c>Shader</c> と同じ作りだが、ここは<b>ステージが1つしかない</b>のでずっと短い。
/// 頂点シェーダもフラグメントシェーダも、頂点バッファも、ラスタライザも、深度バッファも出てこない——
/// <b>「配列を読んで、配列に書く」だけのプログラム</b>として GPU を使う(今日の要点1)。
/// </para>
/// <para>
/// uniform の場所(location)は名前で毎回問い合わせると遅いので、1回引いて辞書に覚える。
/// <b>名前を打ち間違えても黙って無視される</b>のが GL の怖いところなので、
/// 引けなかった名前は覚えておいて <see cref="MissingUniforms"/> から見られるようにしてある
/// (自己チェック19 がこれを見て、名前のずれを捕まえる)。
/// </para>
/// </summary>
internal sealed class ComputeProgram : IDisposable
{
    private readonly GL _gl;

    private readonly uint _program;

    private readonly Dictionary<string, int> _locations = new();

    private readonly List<string> _missing = new();

    private ComputeProgram(GL gl, uint program)
    {
        _gl = gl;
        _program = program;
    }

    /// <summary>見つからなかった uniform の名前。空であることを自己チェック19 が確かめる。</summary>
    public IReadOnlyList<string> MissingUniforms => _missing;

    /// <summary>
    /// GLSL を組み立てる。失敗したら <see cref="InvalidOperationException"/> を投げ、
    /// <b>ドライバのエラーメッセージをそのまま</b>載せる(行番号が付くので、それが無いと直せない)。
    /// </summary>
    public static ComputeProgram Compile(GL gl, string source)
    {
        uint shader = gl.CreateShader(ShaderType.ComputeShader);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"コンピュートシェーダの組み立てに失敗した:\n{log}");
        }

        uint program = gl.CreateProgram();
        gl.AttachShader(program, shader);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);

        // リンクが済めばシェーダ本体は要らない(プログラムが中身を持っている)。
        gl.DetachShader(program, shader);
        gl.DeleteShader(shader);

        if (linked == 0)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"コンピュートシェーダのリンクに失敗した:\n{log}");
        }

        return new ComputeProgram(gl, program);
    }

    /// <summary>出力先のフォルダから GLSL を読んで組み立てる。</summary>
    public static ComputeProgram Load(GL gl, string relativePath)
        => Compile(gl, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, relativePath)));

    public void Use() => _gl.UseProgram(_program);

    public void Set(string name, int value) => _gl.Uniform1(Location(name), value);

    public void Set(string name, float value) => _gl.Uniform1(Location(name), value);

    public void Set(string name, bool value) => _gl.Uniform1(Location(name), value ? 1 : 0);

    public void Set(string name, Vector3 value) => _gl.Uniform3(Location(name), value.X, value.Y, value.Z);

    public void Set(string name, Vector2 value) => _gl.Uniform2(Location(name), value.X, value.Y);

    /// <summary>
    /// ワークグループを <paramref name="groupsX"/> × <paramref name="groupsY"/> 個走らせる。
    ///
    /// <para>
    /// <b>「何画素ぶん」ではなく「何グループぶん」を渡す</b>のがコンピュートシェーダの決まり
    /// (Day 57 の要点1)。GLSL 側の <c>local_size_x/y</c> と掛け算した数が呼び出しの総数になる。
    /// </para>
    /// </summary>
    public void Dispatch(uint groupsX, uint groupsY) => _gl.DispatchCompute(groupsX, groupsY, 1);

    /// <summary>
    /// <b>書いたものが読めるようになるまで待たせる</b>(<c>glMemoryBarrier</c>)。
    ///
    /// <para>
    /// コンピュートシェーダの書き込みは、ディスパッチが返ってきた時点ではまだ
    /// 「他の誰からも見える」状態になっていない。<b>どういう読み方をするつもりかを宣言する</b>と、
    /// ドライバがその読み方に必要なぶんだけキャッシュを流す。宣言を忘れると、
    /// <b>たまに1パスぶん古い値が読める</b>という、再現しないバグになる(Day 57 の要点4)。
    /// </para>
    /// </summary>
    public void Barrier(MemoryBarrierMask mask) => _gl.MemoryBarrier(mask);

    private int Location(string name)
    {
        if (_locations.TryGetValue(name, out int location))
        {
            return location;
        }

        location = _gl.GetUniformLocation(_program, name);
        _locations[name] = location;
        if (location < 0)
        {
            // −1 は「その名前の uniform が無い」。GL は −1 に値を入れても<b>黙って何もしない</b>ので、
            // ここで覚えておかないと「渡したつもりで渡っていない」に一生気づけない。
            // (使っていない uniform は最適化で消えることがあるので、これ自体は必ずしも間違いではない。)
            _missing.Add(name);
        }

        return location;
    }

    /// <summary>
    /// SSBO のブロック1要素のバイト数を、<b>GPU 自身に聞く</b>(Day 61 の自己チェック19)。
    ///
    /// <para>
    /// 長さの決まっていない配列(<c>Material uMaterials[];</c>)を持つブロックの
    /// <c>GL_BUFFER_DATA_SIZE</c> は「要素1個ぶん」になる。C# 側の
    /// <c>Unsafe.SizeOf&lt;GpuMaterial&gt;()</c> と突き合わせれば、
    /// <b>std430 の詰め方の思い違い</b>がその場で分かる。見つからなければ −1。
    /// </para>
    /// </summary>
    public int StorageBlockStride(string blockName)
    {
        uint index = _gl.GetProgramResourceIndex(_program, ProgramInterface.ShaderStorageBlock, blockName);
        if (index == uint.MaxValue)
        {
            return -1;
        }

        Span<GLEnum> properties = [GLEnum.BufferDataSize];
        Span<int> values = [0];
        _gl.GetProgramResource(_program, ProgramInterface.ShaderStorageBlock, index, 1, properties, 1, Span<uint>.Empty, values);
        return values[0];
    }

    public void Dispose() => _gl.DeleteProgram(_program);
}
