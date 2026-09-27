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

// **空を Y 軸まわりに回す**(Day 39)。ラジアン。
//
// 本物の HDRI を入れると、太陽が「その写真を撮った場所での方位」に来る。
// 組んだシーンの都合とは無関係なので、そのままだと
// 「日が当たってほしい壁に当たらない」ということが普通に起きる。
//
// 実際の対処は2つあって、
//   1. シーンのほうを回す … 太陽の方位が HDRI ごとに違うので、差し替えるたびにやり直し
//   2. **空のほうを回す** … HDRI ごとに1つの角度で済む
// 後者を採った。DCC ツール(Blender の Environment Texture の Mapping など)も同じ。
//
// 回すのは**この焼き込みパスだけ**でよい。ここで作ったキューブを
// 放射照度も事前フィルタもスカイボックスも見るので、1か所直せば全部が付いてくる。
uniform float uSkyYaw;

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

    // **引く向きに回す**(Day 39)。「世界のこの方向を見たとき、
    // 絵のどこを引くか」を求めているので、絵を +yaw 回すには
    // 引く先を -yaw 回すことになる。
    float s = sin(-uSkyYaw);
    float c = cos(-uSkyYaw);
    direction = vec3(
        (c * direction.x) + (s * direction.z),
        direction.y,
        (-s * direction.x) + (c * direction.z));

    // 変換も色補正も無し。**HDR の値をそのまま運ぶ**のがこのパスの仕事。
    FragColor = vec4(texture(uEquirect, DirectionToUv(direction)).rgb, 1.0);
}
