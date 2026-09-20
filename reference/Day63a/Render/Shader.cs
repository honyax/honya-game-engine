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

    /// <summary>
    /// このプログラムを作る段(ステージ)の一覧。**Day 57 でここが配列になった**。
    ///
    /// <para>
    /// 昨日までは頂点と画素の2本で固定だった。コンピュートシェーダは
    /// <b>1本だけのプログラム</b>——頂点も画素も無い——なので、
    /// 「2本」を型に焼き込んでいると入る場所が無い。
    /// GL の側は元から <c>glAttachShader</c> を何回でも呼べる形なので、
    /// <b>段の並びを持つ</b>ようにするだけで、両方が同じ道を通る。
    /// </para>
    /// </summary>
    private readonly (ShaderType Type, string Path)[] _stages;

    /// <summary>
    /// ソースの <c>#version</c> の次の行へ差し込む <c>#define</c> の並び(Day 57)。
    ///
    /// <para>
    /// <b>同じソースから中身の違うプログラムを作る</b>ための、いちばん素朴な仕掛け。
    /// コンピュートのワークグループの大きさ(<c>layout (local_size_x = ...)</c>)は
    /// <b>コンパイル時に決まる定数</b>で uniform にできないので、
    /// 64 と 256 を見比べるには<b>コンパイルし直すしかない</b>。
    /// </para>
    ///
    /// <para>
    /// 実際のエンジンはこれを大規模にやっていて(「シェーダバリアント」と呼ばれる)、
    /// 影の有無・光の数・品質段階の組み合わせぶんだけプログラムを焼く。
    /// ここでやっているのは、その仕組みのいちばん小さい形。
    /// </para>
    /// </summary>
    private string[] _defines;

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

    /// <summary>頂点 + 画素の、普通の描画用プログラム(Day 14 からの入口)。</summary>
    public Shader(GL gl, string vertexPath, string fragmentPath)
        : this(
            gl,
            [(ShaderType.VertexShader, vertexPath), (ShaderType.FragmentShader, fragmentPath)],
            [])
    {
    }

    /// <summary>
    /// **頂点 + ジオメトリ + 画素**の3段のプログラム(Day 63a)。
    ///
    /// <para>
    /// 足したのは<b>引数を1つ挟んだコンストラクタ1本</b>だけで、中身は1行も要らなかった。
    /// Day 57 で段の並びを配列(<see cref="_stages"/>)にしてあるので、
    /// 「2本」でも「1本」でも「3本」でも同じ道を通る。
    /// <b>拡張のために書いたのではなく、コンピュートを入れるために仕方なく配列にした</b>ものが、
    /// 6日後に別の用途でそのまま使えた、という形になっている。
    /// </para>
    ///
    /// <para>
    /// <b>順番には意味がある</b>。GL 自体は <c>glAttachShader</c> の順を気にしないが、
    /// ここでは並びをそのままパイプラインの順にしてある——
    /// コンパイルエラーが出たときに、ログが**流れる順**で並んでいるほうが読みやすい。
    /// </para>
    /// </summary>
    public Shader(GL gl, string vertexPath, string geometryPath, string fragmentPath)
        : this(
            gl,
            [
                (ShaderType.VertexShader, vertexPath),
                (ShaderType.GeometryShader, geometryPath),
                (ShaderType.FragmentShader, fragmentPath),
            ],
            [])
    {
    }

    /// <summary>
    /// **コンピュートシェーダ1本だけのプログラム**(Day 57)。
    ///
    /// <para>
    /// <c>glCreateProgram</c> → <c>glAttachShader</c> → <c>glLinkProgram</c> の手順は
    /// 描画用とまったく同じ。違うのは<b>付ける段が1本だけ</b>であることと、
    /// 出来たプログラムを <c>glDrawElements</c> ではなく
    /// <c>glDispatchCompute</c> で動かすこと(<see cref="Dispatch"/>)。
    /// </para>
    ///
    /// <para>
    /// <b>コンピュートは他の段と混ぜられない</b>——<c>GL_COMPUTE_SHADER</c> を
    /// 頂点や画素と一緒にリンクするとエラーになる、と仕様に書いてある。
    /// 「絵を描くパイプライン」と「ただ計算するパイプライン」は別物、という線がここに出ている。
    /// </para>
    /// </summary>
    /// <param name="defines">ソースへ差し込む <c>#define</c>(<c>"LOCAL_SIZE 256"</c> のような形)。</param>
    public Shader(GL gl, string computePath, params string[] defines)
        : this(gl, [(ShaderType.ComputeShader, computePath)], defines)
    {
    }

    private Shader(GL gl, (ShaderType Type, string Path)[] stages, string[] defines)
    {
        _gl = gl;
        _stages = stages;
        _defines = defines;

        // 起動時だけは失敗を許さない。最初の1本が通らないなら設定の問題なので、
        // 黙って進むより早く落ちたほうがよい。
        if (!TryCreateProgram(out uint program, out string error))
        {
            throw new InvalidOperationException($"シェーダの作成に失敗した:\n{error}");
        }

        _program = program;
    }

    /// <summary>コンピュートシェーダ1本で出来ているか(Day 57)。</summary>
    public bool IsCompute => _stages.Length == 1 && _stages[0].Type == ShaderType.ComputeShader;

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

    /// <summary>
    /// **コンピュートを走らせる**(Day 57)。描画で言えば <c>glDrawElements</c> にあたる1行。
    ///
    /// <para>
    /// 渡すのは<b>ワークグループの数</b>であって、呼び出しの数ではない。
    /// <c>local_size_x = 256</c> のシェーダを <c>Dispatch(16, 1, 1)</c> で回すと、
    /// 走るのは 16 × 256 = 4096 個の呼び出し。
    /// <b>数え違いがいちばん起きるのがここ</b>で、粒の数をそのまま渡すと 256 倍走る。
    /// </para>
    ///
    /// <para>
    /// <b>この呼び出しも「積むだけ」</b>。返ってきた時点では GPU はまだ何もしていない可能性が高い。
    /// 結果を使う前には <see cref="Barrier"/> が要る。
    /// </para>
    /// </summary>
    public void Dispatch(uint groupsX, uint groupsY = 1, uint groupsZ = 1)
    {
        if (!IsCompute)
        {
            throw new InvalidOperationException("コンピュートではないプログラムは Dispatch できない");
        }

        _gl.DispatchCompute(groupsX, groupsY, groupsZ);
    }

    /// <summary>
    /// **書いたものが読めるようになるまで待たせる**(Day 57)。<c>glMemoryBarrier</c> の薄い皮。
    ///
    /// <para>
    /// コンピュートが SSBO へ書いた値を、そのあとの描画が頂点属性として読む——
    /// この2つの間には<b>順番の保証が無い</b>。GL は「命令を積んだ順」に実行するが、
    /// <b>メモリへの書き込みが他の経路から見えるようになる時期</b>までは約束していないため。
    /// (画素シェーダの出力のような「決まった出口」は GL が面倒を見るが、
    ///  シェーダが自分で書いた場所は<b>書いた本人にしか分からない</b>)
    /// </para>
    ///
    /// <para>
    /// <b>外しても動いてしまうことが多い</b>のがいちばん厄介なところ。
    /// GPU もドライバも世代で違うので、手元で動いたことは何の保証にもならない。
    /// 「絵が出ているから合っている」が通じない種類の間違い。
    /// </para>
    /// </summary>
    public void Barrier(MemoryBarrierMask mask) => _gl.MemoryBarrier(mask);

    /// <summary>
    /// <c>#define</c> を差し替えて焼き直す(Day 57)。**中身が同じなら何もしない**。
    ///
    /// <para>
    /// 失敗したら古いプログラムのまま(<see cref="TryReload"/> と同じ作法)。
    /// ワークグループの大きさを切り替えるのに使う。
    /// </para>
    /// </summary>
    public bool SetDefines(params string[] defines)
    {
        if (_defines.SequenceEqual(defines))
        {
            return true;
        }

        string[] previous = _defines;
        _defines = defines;

        if (TryReload())
        {
            return true;
        }

        _defines = previous;
        return false;
    }

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

    /// <summary>
    /// <b>uint</b> を送る(Day 57)。粒の数と、撒き直しの種に使う。
    ///
    /// <para>
    /// コンピュートの添字(<c>gl_GlobalInvocationID</c>)は <c>uvec3</c> なので、
    /// 比べる相手も <c>uint</c> にしておくのが素直。<c>int</c> と混ぜると
    /// GLSL は<b>暗黙に変換せずエラーにする</b>(<c>i &lt; uCount</c> が通らない)。
    /// <see cref="SetInt3"/> と同じで、型は GLSL の宣言と揃える。
    /// </para>
    /// </summary>
    public void SetUInt(string name, uint value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            _gl.Uniform1(location, value);
        }
    }

    /// <summary>
    /// <b>ivec3</b> を送る(Day 53)。クラスターの升目の数(横・縦・奥行き)用。
    ///
    /// <para>
    /// <c>System.Numerics</c> には整数のベクトルが無いので、成分を1つずつ受け取る。
    /// float の <see cref="SetVector3"/> で送ってシェーダ側で <c>int()</c> に直すこともできるが、
    /// <b>uniform の型は GLSL の宣言と揃えないと黙って無視される</b>
    /// (<c>ivec3</c> に <c>glUniform3f</c> を送るとエラーになり、値は入らない)。
    /// 型を揃えるほうが、あとで読む人が迷わない。
    /// </para>
    /// </summary>
    public void SetInt3(string name, int x, int y, int z)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            _gl.Uniform3(location, x, y, z);
        }
    }

    /// <summary><b>ivec2</b> を送る(Day 55)。窓の中の最大(<c>blur-max.frag</c>)の歩幅・始まり・大きさ用。<see cref="SetInt3"/> と同じ理由で成分を1つずつ受け取る。</summary>
    public void SetInt2(string name, int x, int y)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            _gl.Uniform2(location, x, y);
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

    /// <summary>
    /// <b>vec3 の配列</b>をまとめて送る(Day 37)。SSAO のカーネル(64 本)用。
    ///
    /// <para>
    /// <b>配列の uniform は「先頭の位置 + 個数」で書ける</b>。
    /// GLSL の <c>uniform vec3 uKernel[64];</c> は、内部では
    /// <c>uKernel[0]</c> 〜 <c>uKernel[63]</c> の 64 個の uniform として並んでおり、
    /// 位置は連続することが仕様で保証されている。だから先頭の位置さえ分かれば
    /// <c>glUniform3fv(location, 64, data)</c> の1回で全部送れる。
    /// </para>
    ///
    /// <para>
    /// <b>名前は配列名そのままでよい</b>。<c>glGetUniformLocation("uKernel")</c> は
    /// 配列の場合に要素 0 の位置を返す、と仕様に書いてある
    /// (<c>"uKernel[0]"</c> と書いても同じ)。
    /// </para>
    ///
    /// <para>
    /// これが無いと <see cref="SetVector3"/> を 64 回呼ぶことになる。
    /// 動きはするが、**文字列を 64 本組み立てて辞書を 64 回引く**のが毎フレーム乗る。
    /// 実測で 0.02ms 程度なので速度の問題ではなく、
    /// 「配列は配列として送れる」という API の形を知っておくためのもの。
    /// </para>
    /// </summary>
    public unsafe void SetVector3Array(string name, ReadOnlySpan<Vector3> values)
    {
        int location = GetUniformLocation(name);
        if (location < 0 || values.Length == 0)
        {
            return;
        }

        // Vector3 は float 3 個が隙間なく並んだ構造体なので、
        // 配列の先頭アドレスをそのまま float* として渡せる
        // (SetMatrix4 と同じ理屈。**パディングが入る型では通用しない**)。
        fixed (Vector3* pointer = values)
        {
            _gl.Uniform3(location, (uint)values.Length, (float*)pointer);
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
    /// <b>vec4 の配列</b>をまとめて送る(Day 52)。フォワードの点光源(位置と色)用。
    ///
    /// <para>
    /// 理屈は <see cref="SetVector3Array"/> と同じ。
    /// <b>vec3 ではなく vec4 にして w に1つ詰める</b>のが点光源での使い方で、
    /// 「位置 + 届く距離」を1本で送れる。uniform の枠は vec3 でも vec4 1本ぶん食う
    /// (4成分単位で並ぶ実装が多い)ので、w を空けておくのはもったいない。
    /// </para>
    /// </summary>
    public unsafe void SetVector4Array(string name, ReadOnlySpan<Vector4> values)
    {
        int location = GetUniformLocation(name);
        if (location < 0 || values.Length == 0)
        {
            return;
        }

        fixed (Vector4* pointer = values)
        {
            _gl.Uniform4(location, (uint)values.Length, (float*)pointer);
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
    /// <b>4x4 行列の配列</b>をまとめて送る(Day 41)。スキニングの関節行列用。
    ///
    /// <para>
    /// 理屈は <see cref="SetVector3Array"/> と同じで、
    /// <c>uniform mat4 uJoints[64];</c> の先頭の位置さえ分かれば
    /// <c>glUniformMatrix4fv(location, count, false, data)</c> の1回で全部送れる。
    /// </para>
    ///
    /// <para>
    /// <b>ここが今日いちばん太いデータ経路</b>になる。関節 19 本(CesiumMan)なら
    /// 19 × 64 バイト = 1.2KB を**毎フレーム、パスの数だけ**送る
    /// (本描画・影・SSAO で3回)。1体なら誤差だが、
    /// 100 体並べると 360KB/フレームになり、ここが素直に効いてくる。
    /// 実際のエンジンが UBO やテクスチャに関節行列を置くのはこれが理由で、
    /// **uniform 配列はいちばん素朴だが、いちばん台数に弱い**。
    /// </para>
    ///
    /// <para>
    /// <b>個数の上限に注意</b>。GLSL の uniform には「頂点シェーダで使える float の総数」
    /// という上限があり(<c>GL_MAX_VERTEX_UNIFORM_COMPONENTS</c>)、
    /// OpenGL 3.3 が保証するのは 1024 個 = mat4 なら 64 個ぶん。
    /// 他の uniform も同じ枠を食うので、実際に置けるのはもっと少ない。
    /// リンクが通るかどうかは実行時にしか分からないので、
    /// <c>AnimationPlayer.MaxJoints</c> で上限を持って、超えたら知らせる。
    /// </para>
    /// </summary>
    public unsafe void SetMatrix4Array(string name, ReadOnlySpan<Matrix4x4> values)
    {
        int location = GetUniformLocation(name);
        if (location < 0 || values.Length == 0)
        {
            return;
        }

        // Matrix4x4 は 16 個の float が隙間なく並んだ構造体なので、
        // 配列の先頭アドレスをそのまま float* として渡せる(SetMatrix4 と同じ理屈)。
        // transpose: false でよい理由も同じ。
        fixed (Matrix4x4* pointer = values)
        {
            _gl.UniformMatrix4(location, (uint)values.Length, false, (float*)pointer);
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

        // **段の数だけ回すようになった**(Day 57)。中身は Day 14 のまま——
        // 読んで、コンパイルして、付けて、リンクする。
        // 頂点 + 画素なら2周、コンピュートなら1周する、という違いしかない。
        var compiled = new List<uint>(_stages.Length);

        foreach ((ShaderType type, string path) in _stages)
        {
            string source;
            try
            {
                // 保存直後は別プロセスがまだファイルを掴んでいることがある。
                // 読めなかったのも「失敗」として扱い、次のリロードに任せる。
                source = File.ReadAllText(path);
            }
            catch (IOException ex)
            {
                DeleteAll(compiled);
                error = $"ファイルを読めません: {ex.Message}";
                return false;
            }

            if (!TryCompile(type, InjectDefines(source), path, out uint shader, out error))
            {
                // 途中まで出来ている状態を残さない。
                DeleteAll(compiled);
                return false;
            }

            compiled.Add(shader);
        }

        uint handle = _gl.CreateProgram();

        foreach (uint shader in compiled)
        {
            _gl.AttachShader(handle, shader);
        }

        _gl.LinkProgram(handle);

        _gl.GetProgram(handle, ProgramPropertyARB.LinkStatus, out int linked);

        // リンクが済めば個々のシェーダオブジェクトは不要。C/C++ の .obj と同じ。
        DeleteAll(compiled);

        if (linked == 0)
        {
            error = $"リンクに失敗しました:\n{_gl.GetProgramInfoLog(handle)}";
            _gl.DeleteProgram(handle);
            return false;
        }

        program = handle;
        return true;
    }

    private void DeleteAll(List<uint> shaders)
    {
        foreach (uint shader in shaders)
        {
            _gl.DeleteShader(shader);
        }

        shaders.Clear();
    }

    /// <summary>
    /// <c>#define</c> をソースへ差し込む(Day 57)。**<c>#version</c> の直後でなければならない**。
    ///
    /// <para>
    /// GLSL は「<c>#version</c> はコメントと空白を除いてソースの先頭」と決めているので、
    /// 前に何か置くとコンパイルが通らない。逆に<b>直後なら何を置いてもよい</b>。
    /// </para>
    ///
    /// <para>
    /// <b>行番号がずれる</b>のが代償になる——差し込んだぶんだけ、コンパイルエラーの行番号が
    /// ファイルの行番号より大きくなる。<b><c>#line 2</c> を最後に置いて戻す</b>のがその対策で、
    /// 「次の行は2行目だ」と数え直させる。これでエラーの行番号がファイルの行番号と一致するので、
    /// ログからエディタへそのまま飛べる(Day 14 の <see cref="TryCompile"/> のコメント参照)。
    /// </para>
    /// </summary>
    private string InjectDefines(string source)
    {
        if (_defines.Length == 0)
        {
            return source;
        }

        int versionEnd = source.IndexOf('\n');
        if (versionEnd < 0 || !source.TrimStart().StartsWith("#version", StringComparison.Ordinal))
        {
            // #version が無いソースには差し込めない(前に何か置くとコンパイルが通らない)。
            Console.WriteLine("[警告] #version が先頭に無いので #define を差し込めません");
            return source;
        }

        var injected = new System.Text.StringBuilder();
        injected.Append(source, 0, versionEnd + 1);

        foreach (string define in _defines)
        {
            injected.Append("#define ").Append(define).Append('\n');
        }

        injected.Append("#line 2\n");
        injected.Append(source, versionEnd + 1, source.Length - versionEnd - 1);
        return injected.ToString();
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
