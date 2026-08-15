using System.Numerics;
using System.Runtime.InteropServices;

namespace HonyaEngine;

/// <summary>
/// スプライト用の頂点フォーマット。
///
/// <see cref="Vertex"/>(3D用)と分けたのは、**1頂点あたりのバイト数がそのまま
/// 転送量になる**から。スプライトバッチは毎フレーム全頂点を CPU で作って
/// GPU へ送り直すので、3D の静的メッシュとは事情がまるで違う。
///
///   <see cref="Vertex"/>       位置(3) + UV(2) + 色(4) = 9 float = 36 バイト
///   <see cref="SpriteVertex"/> 位置(2) + UV(2) + 色(4) = 8 float = 32 バイト
///
/// Z を落としたのは、2D では奥行きを頂点ではなく**描く順**で決めるため
/// (深度テストは切る。要点5)。1万スプライト = 4万頂点なので、
/// 4バイトの差が毎フレーム 160KB の転送量の差になる。
///
/// 色を <see cref="Vector4"/>(16バイト)のまま残しているのは、まだ削る番ではないから。
/// byte 4個に詰めれば 32 → 20 バイトまで落ちる。**先に測る**のが順序で、
/// それは Day 18 でやる。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SpriteVertex
{
    /// <summary>スクリーン座標(ピクセル)。左上が (0,0)、右下が (幅, 高さ)。</summary>
    public Vector2 Position;

    /// <summary>テクスチャ座標。</summary>
    public Vector2 TexCoord;

    /// <summary>頂点色。スプライトごとの色付けに使う。</summary>
    public Vector4 Color;

    public SpriteVertex(Vector2 position, Vector2 texCoord, Vector4 color)
    {
        Position = position;
        TexCoord = texCoord;
        Color = color;
    }

    /// <summary>頂点属性それぞれの float の個数(<see cref="Vertex.AttributeSizes"/> と同じ約束)。</summary>
    public static ReadOnlySpan<int> AttributeSizes => [2, 2, 4];
}
