using System.Runtime.InteropServices;
using System.Text;

namespace RawGL;

/// <summary>
/// 頂点シェーダとフラグメントシェーダをコンパイルして1本のプログラムに繋いだもの。
///
/// **Phase 1 で自分の手で書いていた処理が、そのまま GPU 側のコードになる。**
///   - 頂点シェーダ  = Day 6 の頂点変換(MVP を掛けてクリップ座標を出す)
///   - フラグメントシェーダ = Day 8〜9 のピクセルシェーダ(色を決める)
/// 間にあるラスタライズ(Day 3 のエッジ関数)と属性補間(Day 4 のバリセントリック)は
/// **GPU の固定機能**なので、こちらからは触れない。要点2の対応表を参照。
///
/// C# の <c>Compile</c> と違って GLSL のコンパイルは実行時。
/// GPU ごとに命令セットが違うため、ドライバがその場で機械語に落とす。
/// つまり**コンパイルエラーは実行時にしか出ない**ので、
/// ログを読める状態にしておくことが決定的に重要になる(Day 12 で
/// AllocConsole を仕込んでおいたのはこのため)。
/// </summary>
internal sealed class Shader : IDisposable
{
    private readonly uint _program;

    /// <summary>
    /// uniform の場所を名前で引いた結果の記憶。
    /// <c>glGetUniformLocation</c> は文字列比較を伴うので、毎フレーム呼ぶものではない。
    /// </summary>
    private readonly Dictionary<string, int> _uniformLocations = [];

    private bool _disposed;

    public Shader(string vertexSource, string fragmentSource)
    {
        uint vertexShader = CompileShader(GL.GL_VERTEX_SHADER, vertexSource, "頂点シェーダ");
        uint fragmentShader = CompileShader(GL.GL_FRAGMENT_SHADER, fragmentSource, "フラグメントシェーダ");

        _program = GL.glCreateProgram();
        GL.glAttachShader(_program, vertexShader);
        GL.glAttachShader(_program, fragmentShader);

        // リンク = 頂点シェーダの out とフラグメントシェーダの in を突き合わせる作業。
        // 名前と型が食い違っているとここで落ちる。
        GL.glLinkProgram(_program);

        GL.glGetProgramiv(_program, GL.GL_LINK_STATUS, out int linked);
        if (linked == 0)
        {
            string log = GetProgramInfoLog(_program);
            GL.glDeleteProgram(_program);
            throw new InvalidOperationException($"シェーダのリンクに失敗した:\n{log}");
        }

        // リンクが済めば個々のシェーダオブジェクトは要らない。
        // C/C++ の .obj ファイルと同じで、実行ファイルができたら中間生成物は捨ててよい。
        // (Detach してから Delete するのが厳密だが、Delete は
        //  「参照が無くなったら消す」意味なのでこれで解放される)
        GL.glDeleteShader(vertexShader);
        GL.glDeleteShader(fragmentShader);
    }

    /// <summary>
    /// このプログラムを「使う」状態にする。
    /// OpenGL は徹底して**バインドしてから使う**様式で、
    /// 以降の描画命令はカレントのプログラムに対して実行される。
    /// </summary>
    public void Use() => GL.glUseProgram(_program);

    /// <summary>
    /// mat4 の uniform を設定する。**Use() の後に呼ぶこと**
    /// (uniform はプログラムごとの状態なので、カレントでないと書き込めない)。
    /// </summary>
    public void SetMatrix4(string name, float[] columnMajorValues)
    {
        int location = GetUniformLocation(name);
        if (location < 0)
        {
            return;
        }

        // 第3引数 transpose = GL_FALSE は「渡す配列は既に列優先である」の意味。
        // 行優先で持っている場合は GL_TRUE にすればドライバが転置してくれる。要点5。
        GL.glUniformMatrix4fv(location, 1, GL.GL_FALSE, columnMajorValues);
    }

    private int GetUniformLocation(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int cached))
        {
            return cached;
        }

        int location = GL.glGetUniformLocation(_program, name);

        // **-1 はエラーではない**。「そんな名前は無い」のほかに、
        // 「宣言はされているが結果に影響しないので最適化で消された」場合もこれになる。
        // 前者はタイプミス、後者は正常。どちらか分からないのが厄介なので、
        // 気付けるように1回だけ知らせておく。
        if (location < 0)
        {
            Console.WriteLine($"[警告] uniform '{name}' が見つからない(未使用で削除された可能性)");
        }

        _uniformLocations[name] = location;
        return location;
    }

    private static uint CompileShader(uint type, string source, string label)
    {
        uint shader = GL.glCreateShader(type);

        // ソースを UTF-8 のバイト列にして、そのポインタを渡す。
        // 自動マーシャリング(CharSet.Ansi)に任せるとシステムのコードページに
        // 変換されてしまい、GLSL のコメントに書いた日本語が化ける。
        IntPtr utf8Source = Marshal.StringToCoTaskMemUTF8(source);
        try
        {
            // 第2引数の 1 は「文字列を1本渡す」の意味。
            // 第4引数を null にすると NUL 終端として扱われる。
            GL.glShaderSource(shader, 1, [utf8Source], null);
            GL.glCompileShader(shader);
        }
        finally
        {
            // ドライバは glShaderSource の中でソースを複製するので、
            // 呼び出しが終わればこちらの領域は解放してよい。
            Marshal.FreeCoTaskMem(utf8Source);
        }

        GL.glGetShaderiv(shader, GL.GL_COMPILE_STATUS, out int compiled);
        if (compiled == 0)
        {
            string log = GetShaderInfoLog(shader);
            GL.glDeleteShader(shader);
            throw new InvalidOperationException($"{label}のコンパイルに失敗した:\n{log}");
        }

        return shader;
    }

    /// <summary>
    /// コンパイルログを取り出す。
    /// 「必要な長さを聞いてから、その長さで取りに行く」という
    /// OpenGL でよく出てくる2段構えの呼び方。
    /// </summary>
    private static string GetShaderInfoLog(uint shader)
    {
        GL.glGetShaderiv(shader, GL.GL_INFO_LOG_LENGTH, out int length);
        if (length <= 1)
        {
            return "(ログなし)";
        }

        byte[] buffer = new byte[length];
        GL.glGetShaderInfoLog(shader, length, out int written, buffer);
        return Encoding.UTF8.GetString(buffer, 0, written);
    }

    private static string GetProgramInfoLog(uint program)
    {
        GL.glGetProgramiv(program, GL.GL_INFO_LOG_LENGTH, out int length);
        if (length <= 1)
        {
            return "(ログなし)";
        }

        byte[] buffer = new byte[length];
        GL.glGetProgramInfoLog(program, length, out int written, buffer);
        return Encoding.UTF8.GetString(buffer, 0, written);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // GPU 側のリソースは GC の管轄外。放っておくとプロセスが終わるまで残る。
        // Phase 3 以降で扱うテクスチャやフレームバッファも同じで、
        // 「GPU リソースの寿命は自分で管理する」のがグラフィックスプログラミングの基本。
        GL.glDeleteProgram(_program);
    }
}
