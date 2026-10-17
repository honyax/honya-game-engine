#version 330 core

in vec2 vTexCoord;
in vec4 vColor;

uniform sampler2D uTexture;

// 明るさの倍率。**加算合成のときだけ意味を持つ**。
//
// シーンのバッファは RGBA16F(Day 31)なので、1.0 を超える色をそのまま置ける。
// ここを 1 より大きくすると、ブルームのしきい値(既定 1.0)を越えて
// **粒がにじむ**——火花や爆発が「光っている」ように見えるのはこの効果による。
// アルファ合成では 1.0 を超えても背景と混ざるだけなので、見た目はあまり変わらない。
uniform float uIntensity;

// 事前乗算アルファか(ParticleBlend.Premultiplied)。
//
// **加算とアルファの両方を1つのブレンド式で扱う**ための仕掛け。
// RGB にあらかじめ A を掛けておくと、ブレンドは
// `src * 1 + dst * (1 - srcA)` の1通りで済み、
// **A を 0 に寄せた粒は自動的に加算に近づく**。
// 詳しくは計画書の要点2。
uniform int uPremultiply;

out vec4 FragColor;

void main()
{
    vec4 texel = texture(uTexture, vTexCoord);

    // 頂点色は「寿命に沿った色と不透明度」(ParticleEmitter が作る)。
    // テクスチャは「粒の形」。**掛け算で混ぜる**のは Day 17 のスプライトと同じ。
    vec4 color = texel * vColor;

    color.rgb *= uIntensity;

    if (uPremultiply != 0)
    {
        color.rgb *= color.a;
    }

    FragColor = color;
}
