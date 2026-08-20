using System.Numerics;
using System.Runtime.InteropServices;

namespace HonyaEngine;

/// <summary>
/// 標準の頂点フォーマット。位置・UV・色を持つ。
///
/// Day 14 では <c>float[]</c> に [x, y, r, g, b] を手で並べていた。
/// 構造体にすると次の3つが手に入る。
///   - **意味のある名前**で書ける(要素5個の並びを覚えなくてよい)
///   - コンパイラが**サイズとオフセットを計算**してくれる
///   - 型が違う頂点(Day 17 のスプライト用など)を別の構造体として区別できる
///
/// <see cref="StructLayout"/> で Sequential を明示するのは、
/// **GPU に渡すメモリの並びを宣言順に固定するため**。
/// これが無いと CLR がフィールドを詰め替えてよいことになっており、
/// <see cref="AttributeSizes"/> で教えるオフセットと食い違う可能性がある。
/// Day 11 で Win32 の構造体に付けたのとまったく同じ理由。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Vertex
{
    /// <summary>位置。Day 15 までは Z を 0 のまま使っていたが、Day 16 のカメラで奥行きが効き始める。</summary>
    public Vector3 Position;

    /// <summary>テクスチャ座標。左下が (0,0)、右上が (1,1)(要点4)。</summary>
    public Vector2 TexCoord;

    /// <summary>頂点色。マテリアルの色とは別に、頂点ごとに色を付けたいとき用。</summary>
    public Vector4 Color;

    public Vertex(Vector3 position, Vector2 texCoord, Vector4 color)
    {
        Position = position;
        TexCoord = texCoord;
        Color = color;
    }

    /// <summary>
    /// 頂点属性それぞれの float の個数。宣言順に並べる。
    ///
    /// <see cref="Mesh{TVertex}"/> はこれを見て
    /// <c>glVertexAttribPointer</c> のオフセットとストライドを組み立てる。
    /// **全部 float 前提の簡易版**で、byte に詰めた色などは扱えない。
    /// Day 17 のスプライトバッチで頂点を小さくしたくなったら、そこで拡張する。
    /// </summary>
    public static ReadOnlySpan<int> AttributeSizes => [3, 2, 4];
}
