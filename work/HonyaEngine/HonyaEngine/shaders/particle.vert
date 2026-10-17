#version 330 core

// **ワールド座標**。スプライト(sprite.vert)と違ってスクリーン座標ではない。
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;

// ビュー行列と射影行列を掛けたもの。**モデル行列は無い**。
//
// 粒はそれぞれ違う場所・大きさ・回転を持つが、
// **四角形の4隅は CPU 側でワールド座標に焼き込んである**(ParticleRenderer.Append)。
// ビルボードの向き——カメラの右と上——も同じところで掛けてある。
//
// オブジェクトごとの uniform を1つでも残すと、
// その時点で「粒の数だけ glUniform を呼ぶ」ことになる。
// Day 18 のスプライトバッチが 1 万枚を1回で描けている理屈と同じで、
// **1回のドローコールに畳むには、差分を全部頂点に持たせるしかない**。
uniform mat4 uViewProjection;

out vec2 vTexCoord;
out vec4 vColor;

void main()
{
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
    vTexCoord = aTexCoord;
    vColor = aColor;
}
