#version 330 core

// **位置しか使わない**。UV も色も法線も、深度を書くのには要らない。
//
// 頂点バッファ(VAO)は本描画と同じものを使うので、4本の属性が刺さっているが、
// ここで宣言しなかった属性は GPU が読み飛ばすだけで、何も起きない。
// **属性の番号(location)がずれていなければ、宣言しない自由がある**——
// 末尾に足す流儀(Day 32 で法線を location 3 にした理由)がここでも効いている。
layout (location = 0) in vec3 aPosition;

// 光の目から見たビュー射影行列。ShadowMap.Begin が作って送る。
// **本描画に送るものとまったく同じ行列**でなければならない。
uniform mat4 uLightSpaceMatrix;

uniform mat4 uModel;

void main()
{
    gl_Position = uLightSpaceMatrix * uModel * vec4(aPosition, 1.0);
}
