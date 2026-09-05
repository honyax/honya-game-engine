#version 330 core

// 正距円筒(横長1枚)の HDR 画像から、キューブマップの1面を焼く(Day 36)。
//
// やることは1つだけ:**方向ベクトルを、横長の絵の UV に直す**。
//
//   u = atan(z, x) / 2π + 0.5     … 方位角。真横に一周
//   v = acos(y) / π               … 仰角。上が 0、下が 1
//
// SkyImage.Create がちょうど逆の変換で絵を作っているので、
// ここと向こうで**同じ約束**になっている必要がある。
// 片方だけ v を反転すると、**空と地面がひっくり返る**——
// 見た瞬間に分かる壊れ方をするので、まだ幸せなほうではある。

in vec3 vLocalPos;

out vec4 FragColor;

uniform sampler2D uEquirect;

const float PI = 3.14159265359;

vec2 DirectionToUv(vec3 direction)
{
    // atan(z, x) は -π〜π。0〜1 に写す。
    float u = (atan(direction.z, direction.x) / (2.0 * PI)) + 0.5;

    // acos(y) は 0(真上)〜π(真下)。**上が v = 0**。
    float v = acos(clamp(direction.y, -1.0, 1.0)) / PI;

    return vec2(u, v);
}

void main()
{
    // **補間されたので長さが崩れている**。正規化してから方向として使う
    // (法線を frag で正規化し直すのと同じ話。Day 32)。
    vec3 direction = normalize(vLocalPos);

    // 変換も色補正も無し。**HDR の値をそのまま運ぶ**のがこのパスの仕事。
    FragColor = vec4(texture(uEquirect, DirectionToUv(direction)).rgb, 1.0);
}
