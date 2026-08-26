using System.Numerics;
using System.Runtime.InteropServices;

namespace HonyaEngine;

/// <summary>
/// スプライト用の頂点フォーマット。
///
/// Day 17 では色を <see cref="Vector4"/>(16バイト)で持っていた。
/// Day 18 でそれを **byte 4個(4バイト)** に詰め、1頂点 32 → **20バイト** にする。
///
///   Day 17  位置(2×4) + UV(2×4) + 色(4×4) = 32 バイト
///   Day 18  位置(2×4) + UV(2×4) + 色(4×1) = 20 バイト
///
/// スプライトバッチは毎フレーム全頂点を作り直して GPU へ送るので、
/// **1頂点のバイト数がそのまま毎フレームの転送量**になる。
/// 2万枚 = 8万頂点で 2.56MB → 1.60MB。37% 減。
///
/// ……という理屈は正しいが、**速くなるとは限らない**。
/// 実際に測るのが今日の仕事の一つ(計画書の要点4)。
///
/// 色を 8bit にして情報が落ちないのかという点については、
/// **画面に出す色は最終的に 8bit** なので問題ない。
/// 中間計算で精度が要るのは HDR を扱い始める Day 31 から。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SpriteVertex
{
    /// <summary>スクリーン座標(ピクセル)。左上が (0,0)、右下が (幅, 高さ)。</summary>
    public Vector2 Position;

    /// <summary>テクスチャ座標。</summary>
    public Vector2 TexCoord;

    /// <summary>
    /// 頂点色。RGBA を各8bit に詰めたもの。
    ///
    /// <c>uint</c> 1個として持つが、GPU 側には
    /// 「unsigned byte 4個、正規化あり」として教える(<see cref="Attributes"/>)。
    /// シェーダからは今までどおり <c>vec4</c> の 0.0〜1.0 に見えるので、
    /// **GLSL 側は1文字も変わらない**。
    /// </summary>
    public uint Color;

    public SpriteVertex(Vector2 position, Vector2 texCoord, uint color)
    {
        Position = position;
        TexCoord = texCoord;
        Color = color;
    }

    private static readonly VertexAttribute[] AttributeList =
    [
        VertexAttribute.Float(2),      // Position
        VertexAttribute.Float(2),      // TexCoord
        VertexAttribute.UNormByte4(),  // Color
    ];

    /// <summary>頂点属性の記述。宣言順に並べる。</summary>
    public static ReadOnlySpan<VertexAttribute> Attributes => AttributeList;

    /// <summary>
    /// 0.0〜1.0 の色を RGBA8 に詰める。
    ///
    /// リトルエンディアンの環境では <c>uint</c> はメモリ上に
    /// 下位バイトから並ぶので、<c>r | g&lt;&lt;8 | b&lt;&lt;16 | a&lt;&lt;24</c> と書くと
    /// **バイト列としては R, G, B, A の順**になる。GL に伝えるのはバイト列なので、
    /// これで意図どおりの並びになる。
    /// (ビッグエンディアンの環境では逆になる。.NET が動く環境で
    ///  ビッグエンディアンはほぼ絶滅しているが、原理としては環境依存)
    ///
    /// +0.5f は四捨五入のため。切り捨てだと 1.0 が 254 になってしまう
    /// (255.0 を float で計算した結果がわずかに下回ることがある)。
    /// </summary>
    public static uint PackColor(Vector4 color)
    {
        uint r = (uint)(Math.Clamp(color.X, 0.0f, 1.0f) * 255.0f + 0.5f);
        uint g = (uint)(Math.Clamp(color.Y, 0.0f, 1.0f) * 255.0f + 0.5f);
        uint b = (uint)(Math.Clamp(color.Z, 0.0f, 1.0f) * 255.0f + 0.5f);
        uint a = (uint)(Math.Clamp(color.W, 0.0f, 1.0f) * 255.0f + 0.5f);

        return r | (g << 8) | (b << 16) | (a << 24);
    }
}
