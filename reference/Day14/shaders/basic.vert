#version 330 core

// Day 13 の頂点シェーダとまったく同じ内容。
// 違うのは C# の定数ではなく **ファイルとして存在している**ことだけ。
// GLSL は C# とは別の言語なので、別ファイルに置いたほうが
// エディタの色分けも git の差分も素直になる。

layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec3 aColor;

// 1回のドローコールの間ずっと同じ値。C# 側から Shader.SetMatrix4 で送る。
uniform mat4 uTransform;

// ラスタライザが頂点間を補間して、フラグメントシェーダへ渡す。
out vec3 vColor;

void main()
{
    gl_Position = uTransform * vec4(aPosition, 0.0, 1.0);
    vColor = aColor;
}
