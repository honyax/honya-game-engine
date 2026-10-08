#version 330 core

in vec2 vTexCoord;
in vec4 vColor;

out vec4 FragColor;

uniform sampler2D uTexture;

// 頂点色を sRGB からリニアへ戻す。**Day 31 で足した**。
//
// C# 側に書いてある色(0.35, 1.00, 0.45, 1.0)は、
// カラーピッカーで選んだ値、つまり**画面に出したときの見え方**で決めた数字であって、
// 明るさそのものではない。テクスチャの中身(Day 31 で Srgb8Alpha8 にした)と
// 同じ性質のデータなので、同じように戻してやらないと片方だけリニアになってしまう。
//
// テクスチャは GPU が無料で戻してくれるが、頂点色は自分で戻すしかない。
// 実際のエンジンでは、色を読み込んだ時点(CPU 側)で1回だけ変換して
// 毎ピクセルの pow を省くことが多い。ここでは
// 「どの値がリニアで、どの値がそうでないか」が見えるように、あえてシェーダに置いてある。
vec3 SrgbToLinear(vec3 color)
{
    return pow(color, vec3(2.2));
}

void main()
{
    // アルファも掛け算する。テクスチャの α が 0 のところは
    // 頂点色に関係なく透明になり、切り抜きとして働く。
    //
    // **アルファは変換しない**。α は色ではなく「どれだけ混ぜるか」の割合で、
    // ガンマ符号化の対象ではない。ここを一緒に pow すると半透明が濃くなる。
    FragColor = texture(uTexture, vTexCoord) * vec4(SrgbToLinear(vColor.rgb), vColor.a);
}
