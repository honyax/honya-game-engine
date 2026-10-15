#version 330 core

// ============================================================
//  Day 66a: プローブを球で見る(「プローブGI」の F7)
// ============================================================
//
// 球の各点を「その向きを向いた白い面が、このプローブの位置で受ける光」で塗る。
// **補間はしない**——1個のプローブの9係数だけを見る。格子の間でどう混ざるかは本描画の側で見る。
//
// 基底の定数は textured.frag・deferred.frag の ProbeSh と SphericalHarmonics.Basis と同じ。
// **4か所のどれか1つだけ直すと、球と本描画で明るさの偏りが食い違う**(自己チェックが突き合わせる)。

in vec3 vNormal;

out vec4 FragColor;

/// 係数のテクスチャ。横 9 列が係数、縦 1 行が1プローブ(IrradianceProbes.Upload)。
uniform sampler2D uProbeSh;

/// どのプローブか(何行目か)。
uniform int uProbeIndex;

void main()
{
    vec3 n = normalize(vNormal);
    int probe = uProbeIndex;

    vec3 sum = texelFetch(uProbeSh, ivec2(0, probe), 0).rgb * 0.282095;
    sum += texelFetch(uProbeSh, ivec2(1, probe), 0).rgb * (0.488603 * n.y);
    sum += texelFetch(uProbeSh, ivec2(2, probe), 0).rgb * (0.488603 * n.z);
    sum += texelFetch(uProbeSh, ivec2(3, probe), 0).rgb * (0.488603 * n.x);
    sum += texelFetch(uProbeSh, ivec2(4, probe), 0).rgb * (1.092548 * n.x * n.y);
    sum += texelFetch(uProbeSh, ivec2(5, probe), 0).rgb * (1.092548 * n.y * n.z);
    sum += texelFetch(uProbeSh, ivec2(6, probe), 0).rgb * (0.315392 * ((3.0 * n.z * n.z) - 1.0));
    sum += texelFetch(uProbeSh, ivec2(7, probe), 0).rgb * (1.092548 * n.x * n.z);
    sum += texelFetch(uProbeSh, ivec2(8, probe), 0).rgb * (0.546274 * ((n.x * n.x) - (n.y * n.y)));

    // **白い面(アルベド 1)に当てた明るさ**。係数は 1/π 済みなので、そのまま出せば「白い紙の明るさ」になる。
    // 2次で打ち切ったぶんの波で負になる向きがあるので 0 で止める(本描画と同じ)。
    FragColor = vec4(max(sum, vec3(0.0)), 1.0);
}
