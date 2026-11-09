#version 330 core

// **位置しか使わない**。UV も色も法線も、深度を書くのには要らない。
//
// 頂点バッファ(VAO)は本描画と同じものを使うので、4本の属性が刺さっているが、
// ここで宣言しなかった属性は GPU が読み飛ばすだけで、何も起きない。
// **属性の番号(location)がずれていなければ、宣言しない自由がある**——
// 末尾に足す流儀(Day 32 で法線を location 3 にした理由)がここでも効いている。
layout (location = 0) in vec3 aPosition;

// **スキニングのぶんだけ属性が増えた**(Day 41)。
// 影も本描画と同じ形で落とさなければならないので、
// 「位置しか使わない」の位置が動くぶん、関節と重みが要る。
//
// ここを足さないと何が起きるか: **キャラクタは動くのに、影がバインドポーズのまま**。
// 深度パスと本描画で頂点の位置が食い違うので、自己遮蔽の縞も出る。
// スキニングを入れた日にいちばん出やすい抜けがこれ。
layout (location = 5) in vec4 aJoints;
layout (location = 6) in vec4 aWeights;

// 光の目から見たビュー射影行列。ShadowMap.Begin が作って送る。
// **本描画に送るものとまったく同じ行列**でなければならない。
uniform mat4 uLightSpaceMatrix;

uniform mat4 uModel;

// textured.vert とまったく同じ約束。値も同じものを送る(ShadowMap.Draw)。
const int MAX_JOINTS = 64;
uniform mat4 uJoints[MAX_JOINTS];
uniform int uSkinned;

void main()
{
    vec4 localPos = vec4(aPosition, 1.0);

    if (uSkinned == 1)
    {
        // 混ぜ方の説明は textured.vert の SkinMatrix にある。
        // **法線は要らない**ので位置だけ。深度パスが軽いのはこういうところ。
        mat4 skin = (uJoints[int(aJoints.x)] * aWeights.x)
            + (uJoints[int(aJoints.y)] * aWeights.y)
            + (uJoints[int(aJoints.z)] * aWeights.z)
            + (uJoints[int(aJoints.w)] * aWeights.w);

        localPos = skin * localPos;
    }

    gl_Position = uLightSpaceMatrix * uModel * localPos;
}
