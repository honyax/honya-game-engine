#version 430 core

// ============================================================
//  Day 63a: 素通しのジオメトリシェーダ
// ============================================================
//
// **受け取った三角形を、そのまま1枚出すだけ**。絵は1画素も変わらない。
// 変わらないものをわざわざ置くのは、**この段を通す値段**を測るため。
//
// 同じ球を「GS 無し」「この素通しの GS」で描いて GPU の時間を比べると、
// 何もしていないのに時間が増える。理由は段の性質そのものにある:
//
//   1. **出る数が実行してみるまで分からない**。頂点シェーダは「1入れたら1出る」が、
//      この段は max_vertices までの何個でも出せる。だから GPU は
//      **出力をいったん溜める場所**を用意して、そこへ書かせる
//   2. **出た順を守らなければならない**。GL は「描いた順にラスタライズされる」と
//      約束しているので、あとの三角形が先に終わっても先に出せない。
//      並列に走らせながら順番を保つ = **待ち合わせが要る**
//
// 頂点シェーダの出力は決まった数なので、この2つがどちらも要らない。
// ここが Day 64 のメッシュシェーダが「GS をやめて作り直した」理由でもある。

layout (triangles) in;
layout (triangle_strip, max_vertices = 3) out;

in Varying
{
    vec3 worldPosition;
    vec3 normal;
    vec4 color;
    vec2 texCoord;
} IN[];

out Varying
{
    vec3 worldPosition;
    vec3 normal;
    vec4 color;
    vec2 texCoord;
} OUT;

// 下地として薄く描くときに掛ける係数(1.0 でそのまま)。
uniform vec4 uColorScale;

void main()
{
    for (int i = 0; i < 3; ++i)
    {
        OUT.worldPosition = IN[i].worldPosition;
        OUT.normal = IN[i].normal;
        OUT.color = IN[i].color * uColorScale;
        OUT.texCoord = IN[i].texCoord;

        // **頂点シェーダが計算したクリップ座標をそのまま使う**。
        // gl_in[i].gl_Position は「手前の段が gl_Position に入れた値」で、
        // 段をまたいでも消えない組み込み変数。
        gl_Position = gl_in[i].gl_Position;

        // **EmitVertex は「いま OUT に入っている値で、頂点を1つ出す」**。
        // 代入の時点では何も起きず、この行で初めて1つ確定する。
        // 出し終えた OUT の中身は**未定義**になる(次の頂点で全部入れ直すこと)。
        EmitVertex();
    }

    // **EndPrimitive は「ここでひと繋がりを切る」**。
    // triangle_strip なので、切らずに4つ目を出すと1枚目と繋がった帯になる。
    // 最後の EndPrimitive は省いてもよい(段の終わりで自動的に切れる)が、
    // 書いておくほうが「1枚出した」と読める。
    EndPrimitive();
}
