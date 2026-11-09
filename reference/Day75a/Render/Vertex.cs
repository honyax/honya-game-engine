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

    /// <summary>
    /// この頂点を動かす関節の番号。**最大4本**。Day 41 で足した。
    ///
    /// glTF の <c>JOINTS_0</c> がそのまま入る。整数なのに <see cref="Vector4"/>(float)で
    /// 持っているのは、**属性を float で通すほうが手数が少ない**から。
    /// 正しくは <c>glVertexAttribIPointer</c> + <c>uvec4</c> で整数のまま送るのが筋で、
    /// そのぶん頂点が 32 バイトから 8 バイトに縮む(unsigned byte 4個)。
    /// ここでは <see cref="Mesh{TVertex}"/> の属性設定を触らずに済ませることを優先した。
    ///
    /// <b>関節が 24 個(Fox)なら float の精度は問題にならない</b>。
    /// float が整数を正確に表せるのは 2^24 までなので、
    /// 関節の番号がそこを超えることは無い。
    ///
    /// 重みが 0 の枠には 0 が入る。**0 番の関節を指したまま重みだけ 0**、というのが
    /// glTF の書き方で、「使わない枠は -1」ではない。
    /// </summary>
    public Vector4 Joints;

    /// <summary>
    /// 上の4本それぞれの効き具合。**合計が 1 になる**。Day 41 で足した。
    ///
    /// これが「頂点ブレンディング」の全部で、
    /// 頂点の最終位置は <c>Σ weight[i] * jointMatrix[joints[i]] * position</c> になる。
    /// 肘の内側のように2本の骨が奪い合う場所で 0.5 / 0.5 のような値になり、
    /// 骨の真ん中では 1.0 / 0 になる。
    ///
    /// <b>合計が 1 でないファイルも来る</b>(書き出し側の丸め、あるいは正規化忘れ)。
    /// 合計が 0.9 なら**その頂点だけ原点に 10% 引き寄せられる**ので、
    /// 読み込み時に正規化しておく(<c>GltfLoader.ReadPrimitive</c>)。
    ///
    /// スキンを持たないメッシュではここが全部 0 になる。
    /// シェーダ側は <c>uSkinned</c> で経路を分けるので、0 のままでも壊れない。
    /// </summary>
    public Vector4 Weights;

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
        VertexAttribute.Float(4),   // Joints(Day 41。関節の番号4つ)
        VertexAttribute.Float(4),   // Weights(Day 41。上の4つの重み)
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
    /// **Day 41 で 1頂点 96 バイト**(位置12 + UV8 + 色16 + 法線12 + 接線16
    /// + 関節16 + 重み16)。Day 34 の 64 バイトから 1.5 倍になった。
    /// DamagedHelmet の 14556 頂点なら 1.4MB。この規模ならまだ詰める意味が無い。
    ///
    /// <b>ただし今回の 32 バイトは性質が悪い</b>。法線や接線は全部のメッシュが使うが、
    /// **関節と重みを使うのはスキンを持つメッシュだけ**で、
    /// デモシーンの地面も壁も小物も、全部 0 が詰まった 32 バイトを運ぶことになる。
    /// 本来は「スキン付きの頂点型」を別に定義して、
    /// <c>Mesh&lt;SkinnedVertex&gt;</c> として分けるのが正しい(改造課題3)。
    /// ここで型を分けなかったのは、描く側(<c>Program.Draw</c> /
    /// <see cref="ShadowMap"/> / <see cref="Ssao"/>)が全部
    /// <c>Mesh&lt;Vertex&gt;</c> で書かれていて、
    /// **型を1つ増やすと今日の主題と関係の無い差分が広がる**ため。
    ///
    /// なお**頂点色 16 バイトがいちばん無駄**で、glTF から読むモデルは全部 (1,1,1,1) が入る。
    /// 消せば 80 バイトに戻るが、Day 15 からのデモ(面ごとに色を変えた立方体)が
    /// 使っているので残してある。
    /// </summary>
    public static ReadOnlySpan<VertexAttribute> Attributes => AttributeList;
}
