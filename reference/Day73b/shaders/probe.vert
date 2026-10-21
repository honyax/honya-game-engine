#version 330 core

// ============================================================
//  Day 66a: プローブを球で見る(「プローブGI」の F7)
// ============================================================
//
// 球を1個ずつ、プローブの位置へ置くだけ。**大きさは uRadius で決める**。
// Primitives.CreateSphere の球は半径 0.5 なので、2倍してから掛ける。

layout (location = 0) in vec3 aPosition;
layout (location = 3) in vec3 aNormal;

uniform mat4 uViewProjection;

/// プローブの位置(世界)。
uniform vec3 uCenter;

/// 球の半径 [m]。
uniform float uRadius;

out vec3 vNormal;

void main()
{
    // **球の法線はそのまま世界の向き**。回しも伸ばしもしないので、行列を通す必要が無い。
    vNormal = aNormal;

    vec3 world = uCenter + (aPosition * (2.0 * uRadius));
    gl_Position = uViewProjection * vec4(world, 1.0);
}
