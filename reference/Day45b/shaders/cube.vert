#version 330 core

// キューブマップを焼くための頂点シェーダ(Day 36)。
//
// **立方体を内側から見て6回描く**ときに使う。
// 出すのは「その頂点が立方体のどこか」だけで、これがそのまま
// **中心から見た方向ベクトル**になる——立方体の中心を原点に置いてあるため。
//
// 位置がそのまま方向になる、というのが焼くパスをここまで短くしている理由。
// 面ごとに UV を組み立てる必要が無く、画素シェーダは
// 補間された vLocalPos を正規化するだけでよい。
layout (location = 0) in vec3 aPosition;

uniform mat4 uViewProjection;

out vec3 vLocalPos;

void main()
{
    vLocalPos = aPosition;

    // **モデル行列は無い**。立方体は原点に置いたままで、
    // 動かすのはカメラ(6方向のビュー行列)のほうだけ。
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
}
