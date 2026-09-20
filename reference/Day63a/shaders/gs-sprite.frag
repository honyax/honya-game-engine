#version 430 core

// ============================================================
//  Day 63a: 板を丸く抜く
// ============================================================
//
// ジオメトリシェーダが四隅に入れた 0〜1 の座標(texCoord)で、円の外を捨てる。
//
// **Day 57 の粒は gl_PointCoord を使っていた**。あれはラスタライザが
// 点スプライトを広げるときに入れてくれる組み込みの座標で、頂点を増やさずに済む代わりに
// 「四角く広げる」以外のことができない。板を作った今日は、
// <b>自分で四隅に座標を入れられる</b>ので、傾けても回しても中身が付いてくる。

in Varying
{
    vec3 worldPosition;
    vec3 normal;
    vec4 color;
    vec2 texCoord;
} IN;

out vec4 FragColor;

void main()
{
    vec2 offset = (IN.texCoord * 2.0) - 1.0;
    float radiusSquared = dot(offset, offset);

    if (radiusSquared > 1.0)
    {
        discard;
    }

    // 縁を少し暗くして、板が球のように見えるようにする。
    float shade = sqrt(max(0.0, 1.0 - radiusSquared));

    FragColor = vec4(IN.color.rgb * ((0.35 * shade) + 0.65), IN.color.a);
}
