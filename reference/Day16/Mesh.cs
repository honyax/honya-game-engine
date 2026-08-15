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

    private bool _disposed;

    public unsafe Mesh(
        GL gl,
        ReadOnlySpan<TVertex> vertices,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<int> attributeSizes)
    {
        _gl = gl;
        _indexCount = (uint)indices.Length;

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
        for (int i = 0; i < attributeSizes.Length; i++)
        {
            int componentCount = attributeSizes[i];

            _gl.VertexAttribPointer(
                (uint)i,
                componentCount,
                VertexAttribPointerType.Float,
                false,
                (uint)stride,
                (void*)offset);

            _gl.EnableVertexAttribArray((uint)i);

            offset += componentCount * sizeof(float);
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

    /// <summary>
    /// 描く。**シェーダとマテリアルは呼び出し側が先に設定しておくこと**。
    /// メッシュは「形」だけを持ち、「見た目」は <see cref="Material"/> の担当、
    /// という分担にしてある(要点2)。
    /// </summary>
    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vertexArray);

        // 最後の引数はインデックスバッファ内のオフセット。
        // VAO にインデックスバッファが記録されているので、0 から読ませればよい。
        _gl.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, (void*)0);
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
