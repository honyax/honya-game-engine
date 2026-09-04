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
/// <see cref="Attributes"/> で教えるオフセットと食い違う可能性がある。
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

    private static readonly VertexAttribute[] AttributeList =
    [
        VertexAttribute.Float(3),   // Position
        VertexAttribute.Float(2),   // TexCoord
        VertexAttribute.Float(4),   // Color
    ];

    /// <summary>
    /// 頂点属性の記述。宣言順に並べる。
    ///
    /// <see cref="Mesh{TVertex}"/> はこれを見て
    /// <c>glVertexAttribPointer</c> のオフセットとストライドを組み立てる。
    /// Day 17 までは <c>int</c> の配列(float の個数だけ)だったが、
    /// Day 18 で <see cref="SpriteVertex"/> の色を byte に詰めるために
    /// 型情報を持つ <see cref="VertexAttribute"/> へ置き換えた。
    ///
    /// 3D 側は今のところ全部 float のままでよい。
    /// 法線を byte に詰める、位置を half にする、といった圧縮が要るのは
    /// 頂点数が桁違いになってから(Day 32 以降)。
    /// </summary>
    public static ReadOnlySpan<VertexAttribute> Attributes => AttributeList;
}
