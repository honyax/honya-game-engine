#version 330 core

// **被写界深度の下ごしらえ**(Day 55)。原寸の 2x2 画素を、半分の大きさの1画素にまとめる。
//
// ぼけたものは縮めても分からない(Day 31 のブルームと同じ理屈)。集める仕事(dof-gather.frag)は
// 1画素につき 48 回読むので、画素の数が 1/4 になる効き目は大きい。
// さらに 2x2 を平均しておくと、集めるときの1点が「4画素ぶんの平均」になり、まばらな点のあいだが埋まる。

in vec2 vUv;

out vec4 FragColor;   // rgb: 2x2 の平均の色 / a: 2x2 でいちばん手前の錯乱円 [原寸の画素]

uniform sampler2D uScene;   // 原寸の絵(TAA のあと)
uniform sampler2D uField;   // blur-field.frag の地図(y が錯乱円)

void main()
{
    ivec2 origin = ivec2(gl_FragCoord.xy) * 2;
    ivec2 last = textureSize(uScene, 0) - 1;

    vec3 color = vec3(0.0);
    float coc = 1.0e9;

    for (int y = 0; y < 2; y++)
    {
        for (int x = 0; x < 2; x++)
        {
            ivec2 pixel = min(origin + ivec2(x, y), last);
            color += texelFetch(uScene, pixel, 0).rgb;

            // **錯乱円は4つの平均ではなく、いちばん手前のもの**。
            // 錯乱円は奥行きについて増える一方の関数なので、「いちばん小さい(負に大きい)」=「いちばん手前」。
            // ピントの合った物と奥のボケが同じ 2x2 に入ったら、ピントの合った物(0)が勝つ——
            // この画素は奥の層に数えられなくなり、ピントの合った物の色が奥のボケににじみ出さない。
            // 手前のボケと奥が入ったら手前が勝つ——手前のボケは外へ広がるべきものなので、取りこぼさない。
            coc = min(coc, texelFetch(uField, pixel, 0).y);
        }
    }

    FragColor = vec4(color * 0.25, coc);
}
