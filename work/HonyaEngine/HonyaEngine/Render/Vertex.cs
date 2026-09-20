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

    /// <summary>
    /// 法線。**その頂点で面がどちらを向いているか**。Day 32 で足した。
    ///
    /// Phase 1 では Day 9 で持っていたものが、GPU へ移った Day 14 で落ちていた。
    /// 陰影を付けていなかったので要らなかった——が、
    /// glTF のモデルは必ず法線を持っており、
    /// **これが無いと読み込んだデータの半分を捨てることになる**。
    ///
    /// 単位ベクトルで持つ。長さが 1 でないと <c>N・L</c> が明るさとして意味を持たない。
    ///
    /// <b>末尾に足した</b>のは、属性の番号(location)を振り直さずに済ませるため。
    /// 「宣言順 = location の順」という <see cref="Attributes"/> の約束は保たれる
    /// (0=位置, 1=UV, 2=色, 3=法線)。位置の次に置くほうが意味の並びとしては自然だが、
    /// そうすると既存のシェーダの location を全部ずらすことになる。
    /// </summary>
    public Vector3 Normal;

    /// <summary>
    /// 接線。**UV の U が増える向きが、3D 空間ではどちらか**。Day 34 で足した。
    ///
    /// 法線マップに入っているのは「接空間での法線」——
    /// つまり**面に貼り付いた座標系での向き**であって、世界での向きではない。
    /// 世界へ戻すには、その座標系の3本の軸が要る。
    ///
    ///   T(接線)   … U が増える向き。**このフィールド**
    ///   B(従接線) … V が増える向き。<c>cross(N, T) * W</c> で作る
    ///   N(法線)   … 面の向き。<see cref="Normal"/>
    ///
    /// <b>なぜ B を持たないのか</b>。3本のうち2本が決まれば3本目は外積で出るので、
    /// 12 バイト節約できる。代わりに要るのが <see cref="Vector4.W"/> の1個で、
    /// ここには **+1 か -1 しか入らない**——外積の向きが合っているか、逆かの符号。
    ///
    /// 逆になるのは、UV が鏡像になっている面
    /// (左右対称のモデルで、片側の UV を裏返して使い回す定番の手)。
    /// **符号を落とすと、鏡像の側だけ凹凸が反転する**。
    /// 左右対称のキャラクタで「右半分だけ変」という壊れ方はたいていこれ。
    ///
    /// glTF の TANGENT はこの形(VEC4)でそのまま入っているので、
    /// 仕様に合わせた結果として 4 成分になっている。
    /// </summary>
    public Vector4 Tangent;

    public Vertex(Vector3 position, Vector2 texCoord, Vector4 color)
        : this(position, texCoord, color, Vector3.UnitZ)
    {
    }

    public Vertex(Vector3 position, Vector2 texCoord, Vector4 color, Vector3 normal)
        : this(position, texCoord, color, normal, new Vector4(1.0f, 0.0f, 0.0f, 1.0f))
    {
    }

    public Vertex(Vector3 position, Vector2 texCoord, Vector4 color, Vector3 normal, Vector4 tangent)
    {
        Position = position;
        TexCoord = texCoord;
        Color = color;
        Normal = normal;
        Tangent = tangent;
    }

    private static readonly VertexAttribute[] AttributeList =
    [
        VertexAttribute.Float(3),   // Position
        VertexAttribute.Float(2),   // TexCoord
        VertexAttribute.Float(4),   // Color
        VertexAttribute.Float(3),   // Normal
        VertexAttribute.Float(4),   // Tangent(Day 34。xyz = 接線、w = 従接線の符号)
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
    /// **Day 34 で 1頂点 64 バイト**(位置12 + UV8 + 色16 + 法線12 + 接線16)。
    /// DamagedHelmet の 14556 頂点なら 930KB。この規模なら詰める意味が無い。
    /// 法線を byte に詰める、位置を half にする、といった圧縮が効いてくるのは
    /// 数百万頂点を扱い始めてから。
    ///
    /// なお**頂点色 16 バイトがいちばん無駄**で、glTF から読むモデルは全部 (1,1,1,1) が入る。
    /// 消せば 48 バイトに戻るが、Day 15 からのデモ(面ごとに色を変えた立方体)が
    /// 使っているので残してある。
    /// </summary>
    public static ReadOnlySpan<VertexAttribute> Attributes => AttributeList;
}
