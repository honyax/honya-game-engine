#version 330 core

// **点光源の球(ライトボリューム)**(Day 52)。deferred.frag と組んで、
// 光が届く範囲に写っている画素だけを塗る。
//
// **位置しか宣言しない**。Vertex は 7 本の属性を持っているが、
// 宣言しなければ読まれない(depth.vert と同じ自由。Day 34 で「末尾に足す」戦略が効いた理由)。
// この球は絵に出ないので、UV も法線も要らない——要るのは「どの画素を覆うか」だけ。
layout (location = 0) in vec3 aPosition;

uniform mat4 uViewProjection;

// xyz = 光の位置 / w = 届く距離。deferred.frag も同じ uniform を読む。
uniform vec4 uPointPosition;

// 球を描く半径。届く距離を少し膨らませたもの(DeferredLighting.VolumeScale)。
// 分割した球は面が内側へ入り込むので、そのままだと光の縁が多角形に欠ける。
uniform float uVolumeRadius;

void main()
{
    // **モデル行列を作らない**。球は回しても同じ形なので、要るのは位置と大きさだけ。
    // Primitives の球は直径 1(半径 0.5)なので、向きだけ取り出して半径を掛け直す。
    vec3 world = uPointPosition.xyz + (normalize(aPosition) * uVolumeRadius);

    gl_Position = uViewProjection * vec4(world, 1.0);
}
