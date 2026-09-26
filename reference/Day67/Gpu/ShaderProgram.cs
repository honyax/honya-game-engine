using System.Numerics;
using Silk.NET.OpenGL;

namespace GaussianSplatting;

/// <summary>
/// 頂点シェーダとフラグメントシェーダを組み立てたプログラム。Day 14 の <c>Shader</c> と Day 61 の <c>ComputeProgram</c> を合わせた薄い皮。
///
/// <para>
/// uniform の場所は名前で1回引いて覚える。<b>名前を打ち間違えても GL は黙って無視する</b>ので、
/// 引けなかった名前は <see cref="MissingUniforms"/> に残し、自己チェックで空であることを確かめる(Day 61 と同じ)。
/// </para>
/// </summary>
internal sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;

    private readonly uint _program;

    private readonly Dictionary<string, int> _locations = new();

    private readonly List<string> _missing = new();

    private ShaderProgram(GL gl, uint program)
    {
        _gl = gl;
        _program = program;
    }

    public IReadOnlyList<string> MissingUniforms => _missing;

    /// <summary>出力先の shaders/ から読んで組み立てる。失敗したらドライバのメッセージを載せて例外を投げる。</summary>
    public static ShaderProgram Load(GL gl, string vertexPath, string fragmentPath)
    {
        string Read(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, path));

        uint vertex = CompileStage(gl, ShaderType.VertexShader, Read(vertexPath), vertexPath);
        uint fragment = CompileStage(gl, ShaderType.FragmentShader, Read(fragmentPath), fragmentPath);

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vertex);
        gl.AttachShader(program, fragment);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);

        gl.DetachShader(program, vertex);
        gl.DetachShader(program, fragment);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);

        if (linked == 0)
        {
            string log = gl.GetProgramInfoLog(program);
            gl.DeleteProgram(program);
            throw new InvalidOperationException($"シェーダのリンクに失敗した:\n{log}");
        }

        return new ShaderProgram(gl, program);
    }

    private static uint CompileStage(GL gl, ShaderType type, string source, string name)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"{name} の組み立てに失敗した:\n{log}");
        }

        return shader;
    }

    public void Use() => _gl.UseProgram(_program);

    public void Set(string name, int value) => _gl.Uniform1(Location(name), value);

    public void Set(string name, float value) => _gl.Uniform1(Location(name), value);

    public void Set(string name, Vector2 value) => _gl.Uniform2(Location(name), value.X, value.Y);

    public void Set(string name, Vector3 value) => _gl.Uniform3(Location(name), value.X, value.Y, value.Z);

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
            _missing.Add(name);
        }

        return location;
    }

    public void Dispose() => _gl.DeleteProgram(_program);
}
