using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// GPU 上のジオメトリ。VAO・VBO・EBO をまとめて1つの「描けるもの」にする。
///
/// Day 14 では <c>CreateTriangle()</c> が Program.cs の中に直書きされていた。
/// メッシュが2つ3つと増えると同じ手順を書き写すことになるので、ここで箱にする。
/// 抽象化の効果は Day 17 のスプライトバッチではっきりする——
/// あそこでは毎フレーム頂点を作り直すので、**バッファの扱いを1箇所に閉じ込めておかないと
/// 破綻する**。
///
/// 型引数にしているのは、頂点フォーマットが用途ごとに変わるから。
/// 今日は <see cref="Vertex"/> だけだが、Day 17 では
/// 位置とUVだけの軽い頂点を別に定義することになる。
/// </summary>
/// <typeparam name="TVertex">
/// 頂点構造体。<c>unmanaged</c> 制約は「参照型を含まない」の意味で、
/// これが無いと**メモリをそのまま GPU に渡してよいことが保証できない**。
/// </typeparam>
internal sealed class Mesh<TVertex> : IDisposable
    where TVertex : unmanaged
{
    private readonly GL _gl;

    /// <summary>頂点配列オブジェクト。「バイト列をどう読むか」の記録(Day 13 の要点3)。</summary>
    private readonly uint _vertexArray;

    /// <summary>頂点バッファ。GPU 上のただのバイト列。</summary>
    private readonly uint _vertexBuffer;

    /// <summary>インデックスバッファ。**VAO の状態に含まれる**点が VBO と違う。</summary>
    private readonly uint _indexBuffer;

    private readonly uint _indexCount;

    /// <summary>頂点の数。<see cref="ReadVertices"/> が読み返す量を知るために覚えておく。</summary>
    private readonly int _vertexCount;

    private bool _disposed;

    public unsafe Mesh(
        GL gl,
        ReadOnlySpan<TVertex> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<VertexAttribute> attributes)
    {
        _gl = gl;
        _indexCount = (uint)indices.Length;
        _vertexCount = vertices.Length;

        // **VAO を先にバインドする**。以降の設定はカレントの VAO に記録される。
        _vertexArray = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArray);

        // --- 頂点バッファ ---
        _vertexBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);

        // Unsafe.SizeOf<T>() は構造体のパディング込みの実サイズ。
        // これがそのまま「次の頂点まで何バイト飛ぶか」= ストライドになる。
        int stride = Unsafe.SizeOf<TVertex>();

        fixed (TVertex* data = vertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * stride),
                data,
                BufferUsageARB.StaticDraw);
        }

        // --- インデックスバッファ ---
        _indexBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _indexBuffer);

        fixed (uint* data = indices)
        {
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(indices.Length * sizeof(uint)),
                data,
                BufferUsageARB.StaticDraw);
        }

        // --- 頂点属性 ---
        // 宣言順にオフセットを積み上げていくだけ。手で数えていたものを機械にやらせる。
        int offset = 0;
        for (int i = 0; i < attributes.Length; i++)
        {
            VertexAttribute attribute = attributes[i];

            _gl.VertexAttribPointer(
                (uint)i,
                attribute.ComponentCount,
                attribute.Type,
                attribute.Normalized,
                (uint)stride,
                (void*)offset);

            _gl.EnableVertexAttribArray((uint)i);

            offset += attribute.ByteSize;
        }

        // オフセットの合計が構造体のサイズと合わない = 属性の記述が間違っている。
        // 黙って絵が壊れるより、ここで気付けたほうがずっと早い。
        if (offset != stride)
        {
            throw new InvalidOperationException(
                $"頂点属性の合計 {offset} バイトが {typeof(TVertex).Name} のサイズ {stride} バイトと一致しません");
        }

        // **VAO を先に外す**。VAO をバインドしたまま ElementArrayBuffer に 0 を入れると、
        // VAO からインデックスバッファが外れてしまう(Day 13 の要点3)。
        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
    }

    /// <summary>頂点の数。</summary>
    public int VertexCount => _vertexCount;

    /// <summary>
    /// インデックスの数(Day 39)。3 で割れば三角形の枚数になる。
    /// <see cref="DemoScene"/> が「このシーンは三角形が何枚か」を数えるのに使う。
    /// </summary>
    public int IndexCount => (int)_indexCount;

    /// <summary>
    /// **GPU から頂点を読み返す**(Day 34)。自己チェック用。
    ///
    /// VBO へ送った内容は取り出せる。<c>glGetBufferSubData</c> は
    /// <c>glBufferData</c> のちょうど逆で、GPU 側のバイト列を CPU のメモリへ書き戻す。
    ///
    /// <para>
    /// <b>ただし遅い</b>。GPU から CPU への転送は、
    /// **描画キューが空になるまで待つ**(同期する)ことが多い——
    /// GL の呼び出しは普段は積むだけで返るが、
    /// 「結果をよこせ」と言った瞬間だけ待たされる。
    /// 毎フレーム呼ぶ類のものではなく、検査とデバッグのための窓口として置いてある。
    /// </para>
    ///
    /// <para>
    /// <b>CPU 側に控えを持たない</b>という判断でもある。
    /// コンストラクタで受け取った配列をそのまま保持すれば読み返しは要らないが、
    /// DamagedHelmet の 14556 頂点 × 64 バイトで 930KB を、
    /// **使うかどうか分からないのに常に払う**ことになる。
    /// 当たり判定やレイキャストで頂点が要り用になったら(Phase 7)、
    /// そのとき「持つ」へ倒すことを考えればよい。
    /// </para>
    /// </summary>
    public unsafe TVertex[] ReadVertices()
    {
        var result = new TVertex[_vertexCount];
        int stride = Unsafe.SizeOf<TVertex>();

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);

        fixed (TVertex* data = result)
        {
            _gl.GetBufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                (nuint)(_vertexCount * stride),
                data);
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        return result;
    }

    /// <summary>
    /// **インデックスを読み返す**(Day 35)。<see cref="ReadVertices"/> と対になる自己チェック用。
    ///
    /// 頂点だけ読めても<b>三角形の巻き順</b>は分からない。
    /// 巻き順を間違えると背面カリングで消えるだけなので、
    /// 「実装は正しいのに何も映らない」という、
    /// 原因の見当が付きにくい壊れ方をする(<see cref="Primitives.CreateSphere"/>)。
    ///
    /// 頂点法線と突き合わせれば機械で確かめられるので、その材料としてここに置く。
    /// 遅さと「控えを持たない」判断は <see cref="ReadVertices"/> と同じ。
    ///
    /// <para>
    /// <b>ElementArrayBuffer に結び付けてはいけない</b>。
    /// インデックスバッファの結び付けは<b>いま結び付いている VAO の記録そのもの</b>で、
    /// ここで結び付けて最後に 0 へ戻すと、直前に描いたメッシュの VAO から
    /// インデックスバッファが外れる(<see cref="Draw"/> は描いたあと VAO を外さない)。
    /// そのメッシュを次に描いた瞬間、<c>DrawElements</c> がオフセット 0 を
    /// CPU のメモリの 0 番地として読みに行き、アクセス違反で落ちる。
    /// 読むだけなら、どの VAO にも属さない <c>CopyReadBuffer</c> に結び付ければよい。
    /// 頂点バッファ(ArrayBuffer)のほうは VAO の記録ではないので、<see cref="ReadVertices"/> はそのままでよい。
    /// (Day 52 の自己チェックが、光の球を描いた直後にこれを読んで落ちたことで見つかった。
    /// それまでは直前に別の VAO が結び付いていて、たまたま踏まずに済んでいた。Day 35 まで遡って直してある)
    /// </para>
    /// </summary>
    public unsafe uint[] ReadIndices()
    {
        var result = new uint[_indexCount];

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _indexBuffer);

        fixed (uint* data = result)
        {
            _gl.GetBufferSubData(
                BufferTargetARB.CopyReadBuffer,
                0,
                (nuint)(_indexCount * sizeof(uint)),
                data);
        }

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);

        return result;
    }

    /// <summary>
    /// 描く。**シェーダとマテリアルは呼び出し側が先に設定しておくこと**。
    /// メッシュは「形」だけを持ち、「見た目」は <see cref="Material"/> の担当、
    /// という分担にしてある(要点2)。
    /// </summary>
    public void Draw() => Draw(PrimitiveType.Triangles);

    /// <summary>
    /// **図形の種類を指定して描く**(Day 63a)。
    ///
    /// <para>
    /// Day 14 からここは <c>PrimitiveType.Triangles</c> で固定だった。
    /// 「インデックスを3つずつ束ねて三角形にする」以外の読み方を、エンジンが必要としなかったため。
    /// </para>
    ///
    /// <para>
    /// ジオメトリシェーダはこの固定を破る。同じ頂点バッファを
    ///   <c>Points</c>            … 1つずつ束ねる(頂点の数だけ板を出す)
    ///   <c>TrianglesAdjacency</c> … <b>6つずつ</b>束ねる(三角形とその3辺の隣)
    /// と読み替えて描く。<b>並べ方の約束が変わるだけで、バッファの中身は同じバイト列</b>——
    /// Day 13 で見た「バッファは型を持たない」がここでも効いている。
    /// </para>
    ///
    /// <para>
    /// <b>インデックスの数え方は種類によらない</b>。<c>glDrawElements</c> に渡すのは
    /// 常に「インデックスをいくつ読むか」で、図形がいくつになるかは GL が割る。
    /// 隣接付きは三角形1枚につき6つ読むので、<b>同じ形でもインデックスバッファが2倍</b>になる。
    /// </para>
    /// </summary>
    public unsafe void Draw(PrimitiveType mode)
    {
        _gl.BindVertexArray(_vertexArray);

        // 最後の引数はインデックスバッファ内のオフセット。
        // VAO にインデックスバッファが記録されているので、0 から読ませればよい。
        _gl.DrawElements(mode, _indexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    /// <summary>
    /// **インデックスを使わずに、頂点をそのまま並べて描く**(Day 63a)。
    ///
    /// <para>
    /// <c>glDrawArrays</c> のほう。<see cref="Draw(PrimitiveType)"/> との違いは
    /// 「何を並べるか」で、こちらは<b>頂点バッファの先頭から順に</b>、
    /// あちらはインデックスバッファの指す順に読む。
    /// </para>
    ///
    /// <para>
    /// 今日これが要るのは、メッシュを<b>点の集まり</b>として描くため。
    /// 索引のまま点を描くと、三角形で何度も参照されている頂点が
    /// <b>その回数だけ点になる</b>(球なら 221 頂点に対して点が 1152 個)。
    /// 同じ場所に重なるので絵は変わらないが、
    /// 「板1枚 = 頂点1つ」という数の対応が崩れて検算にならない。
    /// </para>
    /// </summary>
    public void DrawVertices(PrimitiveType mode)
    {
        _gl.BindVertexArray(_vertexArray);
        _gl.DrawArrays(mode, 0, (uint)_vertexCount);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _gl.DeleteBuffer(_vertexBuffer);
        _gl.DeleteBuffer(_indexBuffer);
        _gl.DeleteVertexArray(_vertexArray);
    }
}
