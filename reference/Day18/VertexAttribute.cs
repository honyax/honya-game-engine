using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// 頂点属性1つぶんの記述。
///
/// Day 15 からここまで、頂点属性は <c>ReadOnlySpan&lt;int&gt;</c> の
/// 「float が何個か」だけで表していた(<c>AttributeSizes => [3, 2, 4]</c>)。
/// 全部 float 前提の簡易版で、そのぶん短く書けたが、
/// **頂点を小さくしようとした瞬間に足りなくなる**。
///
/// 色を <c>Vector4</c>(16バイト)から byte 4個(4バイト)に詰めるには、
/// 「4個」以外に次の2つを <c>glVertexAttribPointer</c> に伝える必要がある。
///   - **型** … float ではなく unsigned byte
///   - **正規化するか** … 0〜255 を 0.0〜1.0 として読ませるか
///
/// この2つを足しただけの箱。Day 15 の課題3で「型情報を持つ記述に置き換える」と
/// 書いたものが、Phase 3 の締めでようやく必要になった、という順序になっている。
/// **必要になってから作る**ほうが、何のための抽象化か分かりやすい。
/// </summary>
internal readonly struct VertexAttribute
{
    private VertexAttribute(int componentCount, VertexAttribPointerType type, bool normalized, int byteSize)
    {
        ComponentCount = componentCount;
        Type = type;
        Normalized = normalized;
        ByteSize = byteSize;
    }

    /// <summary>成分の数(1〜4)。</summary>
    public int ComponentCount { get; }

    /// <summary>GPU 側に伝える成分の型。</summary>
    public VertexAttribPointerType Type { get; }

    /// <summary>
    /// 整数を 0.0〜1.0 に読み替えるか。
    ///
    /// **Day 13 の要点6で「GLboolean は1バイト」と書いた引数がこれ**。
    /// true にすると unsigned byte の 0〜255 が 0.0〜1.0 に、
    /// false だと 0.0〜255.0 のまま届く。色を byte に詰めるときは必ず true。
    /// </summary>
    public bool Normalized { get; }

    /// <summary>この属性が頂点構造体の中で占めるバイト数。</summary>
    public int ByteSize { get; }

    /// <summary>float が <paramref name="count"/> 個。これまでと同じ既定の形。</summary>
    public static VertexAttribute Float(int count)
        => new(count, VertexAttribPointerType.Float, false, count * sizeof(float));

    /// <summary>
    /// unsigned byte 4個を 0.0〜1.0 として読む。**色を4バイトに詰めるとき用**。
    /// UNorm は "unsigned normalized" の略で、この読み方の呼び名。
    /// </summary>
    public static VertexAttribute UNormByte4()
        => new(4, VertexAttribPointerType.UnsignedByte, true, 4);
}
