#version 460

// 画素シェーダ。**2つの道で同じ1本を使う**。
//
// 手前の段が頂点シェーダでもメッシュシェーダでも、渡ってくる値(location 0〜2 と gl_PrimitiveID)の
// 並びが同じなら、この1本で受けられる(Day 63b の Varying ブロックと同じ考え方)。
#include "common.glsl"

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec3 vWorld;
layout(location = 2) flat in uint vInstance;

layout(location = 0) out vec4 outColor;

void main()
{
    // gl_PrimitiveID = 描く順での三角形の番号。
    //   頂点の道   … GPU が数える(1体ぶんの描画の中で何枚目か。体ごとに 0 から数え直す)
    //   メッシュの道 … メッシュシェーダが gl_MeshPrimitivesEXT[i].gl_PrimitiveID に書いた値
    // どちらも「並べ直した索引で k 番目」を指すように揃えてある。
    uint triangle = uint(gl_PrimitiveID);

    vec3 base;
    if (frame.mode == 1u)
    {
        // メッシュレットごとの色。**どこで切れているか**が見える。
        base = hashColor(triangleMeshlet[triangle]);
    }
    else if (frame.mode == 2u)
    {
        // 三角形ごとの色。細かさ(1体 32,768 枚)が見える。
        base = hashColor(triangle + 0x9E3779B9u);
    }
    else
    {
        // 陰影。体ごとに少しだけ色を変える。
        base = mix(vec3(0.85, 0.80, 0.72), hashColor(vInstance + 17u), 0.35);
    }

    // 光は1つの平行光 + 空と地面の照り返し + ハイライト。**絵の良し悪しは今日の主題ではない**ので最小限。
    vec3 n = normalize(vNormal);
    vec3 toLight = normalize(vec3(0.4, 0.8, 0.3));
    vec3 toEye = normalize(frame.cameraPos.xyz - vWorld);
    float diffuse = max(dot(n, toLight), 0.0);
    float hemisphere = 0.5 + 0.5 * n.y;
    float specular = pow(max(dot(n, normalize(toLight + toEye)), 0.0), 48.0);

    vec3 color = base * (0.18 + 0.22 * hemisphere + 0.75 * diffuse) + vec3(0.25) * specular;
    outColor = vec4(color, 1.0);
}
