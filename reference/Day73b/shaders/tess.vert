#version 430 core

// ============================================================
//  Day 63b: パッチの制御点を、そのまま後ろへ渡す
// ============================================================
//
// **ここでは何もしない**。ワールドへ持ち上げるだけで、投影もしない。
//
// テッセレーションの前では、頂点は<b>「形の点」ではなく「形を決める数」</b>になる。
// 四角パッチの4隅は、割ったあとの点をどこに置くかを決める材料でしかなく、
// **そのままでは画面に出ない**(出るのは割ったあとの点だけ)。
// だから投影は割り終えたあと(tess.tese)で掛ける。
//
// <b>頂点シェーダはパッチの制御点1つにつき1回走る</b>。
// 8x8 の格子なら 64 パッチ x 4 隅 = 256 回。割ったあとの点が何万個になろうと、
// この段の仕事は 256 回のまま——**ここがテッセレーションの値打ち**で、
// CPU から送る頂点も、頂点シェーダが触る頂点も、割る前の数しかない。

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;
layout (location = 3) in vec3 aNormal;

uniform mat4 uModel;

out Control
{
    vec3 worldPosition;
    vec2 texCoord;
    vec4 color;
    vec3 normal;
} OUT;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);

    OUT.worldPosition = world.xyz;
    OUT.texCoord = aTexCoord;
    OUT.color = aColor;
    OUT.normal = mat3(uModel) * aNormal;

    // **gl_Position は書かない**。書いても制御シェーダが読むだけで、
    // ラスタライザへは行かない(行くのは tess.tese が書いたもの)。
}
