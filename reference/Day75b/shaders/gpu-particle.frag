#version 430 core

in vec4 vColor;

uniform float uIntensity;

out vec4 FragColor;

void main()
{
    // **gl_PointCoord は点スプライトの中だけで使える 0〜1 の座標**。
    // GL_POINTS で描くと、ラスタライザが1つの頂点を四角い画素の塊に広げ、
    // その中での位置をここへ入れてくれる。
    // Day 49 は四隅を CPU で作って UV を持たせていたが、点なら**頂点が1つで済む**——
    // 100 万粒でも頂点は 100 万個(四角形なら 400 万個)。
    vec2 offset = (gl_PointCoord * 2.0) - 1.0;
    float radiusSquared = dot(offset, offset);

    // **四角い点を丸くする**。discard は「この画素は無かったことにする」命令で、
    // 深度テストより前に効く。ここでは深度を書いていないので、
    // 単に「混ぜない」という意味になる。
    if (radiusSquared > 1.0)
    {
        discard;
    }

    // 中心が明るく、縁へ向かって消える。2乗しているのは、
    // 1乗だと縁が見えて**丸い板が並んでいる**ように見えるため。
    float falloff = 1.0 - radiusSquared;
    falloff *= falloff;

    FragColor = vec4(vColor.rgb * uIntensity, vColor.a * falloff);
}
