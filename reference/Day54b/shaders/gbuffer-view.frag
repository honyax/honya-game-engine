#version 330 core

// **G-Buffer を1枚ずつ見る**(Day 52。「ディファード」の F5)。
//
// ライティングパスの入力はこの4枚だけなので、光を当てた絵がおかしいときは
// 「入力が悪いのか、光の当て方が悪いのか」をまずここで切り分ける。
// 法線が黒い、粗さと金属度が入れ替わっている——どちらも光を当てると
// **それらしい絵になってしまう**ので、合成したあとからでは見つからない。

in vec2 vUv;

out vec4 FragColor;

uniform sampler2D uGAlbedo;
uniform sampler2D uGMaterial;
uniform sampler2D uGNormalDepth;
uniform sampler2D uGEmissive;

/// GBufferView の番号。1=アルベド 2=法線 3=距離 4=金属度 5=粗さ 6=AO 7=発光
uniform int uMode;

/// 距離をこの長さで割って白黒にする(ssao-view.frag と同じ流儀)。
uniform float uDepthRange;

void main()
{
    // **texelFetch で読む**。G-Buffer は画面と同じ大きさなので、
    // 画素の番号でそのまま引ける。フィルタを一切通さないので、
    // 隣の画素の法線と混ざる心配が無い(ライティングパスも同じ読み方をしている)。
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    vec4 normalDepth = texelFetch(uGNormalDepth, pixel, 0);

    // 空。**何も描かれていない印は距離 0**(Day 37 と同じ約束)。
    if (normalDepth.w <= 0.0)
    {
        FragColor = vec4(0.04, 0.04, 0.07, 1.0);
        return;
    }

    vec3 orm = texelFetch(uGMaterial, pixel, 0).rgb;
    vec3 color;

    if (uMode == 1)
    {
        // **平方根で詰めたまま出す**。詰めた値はガンマ 2 の符号化になっているので、
        // 画面(ガンマ 2.2)にそのまま出すと、ほぼ見慣れた色に見える。
        color = texelFetch(uGAlbedo, pixel, 0).rgb;
    }
    else if (uMode == 2)
    {
        // ビュー空間なので、**カメラを回すと色が変わる**のが正しい(世界の法線ではない)。
        color = (normalize(normalDepth.xyz) * 0.5) + 0.5;
    }
    else if (uMode == 3)
    {
        color = vec3(1.0 - clamp(normalDepth.w / uDepthRange, 0.0, 1.0));
    }
    else if (uMode == 4)
    {
        color = vec3(orm.b);
    }
    else if (uMode == 5)
    {
        color = vec3(orm.g);
    }
    else if (uMode == 6)
    {
        color = vec3(orm.r);
    }
    else
    {
        // 発光は 1.0 を超えるので、x / (1 + x) で畳んでから出す。
        vec3 emissive = texelFetch(uGEmissive, pixel, 0).rgb;
        color = emissive / (vec3(1.0) + emissive);
    }

    FragColor = vec4(color, 1.0);
}
