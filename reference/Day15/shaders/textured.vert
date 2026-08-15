#version 330 core

// Day 14 の basic.vert に UV を足したもの。
// 属性の番号は Vertex 構造体のフィールド順(位置 → UV → 色)と対応している。
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;

// オブジェクトごとに変わる(Program が設定)
uniform mat4 uTransform;

// マテリアルごとに変わる(Material が設定)
uniform vec2 uUvScale;

out vec2 vTexCoord;
out vec4 vColor;

void main()
{
    gl_Position = uTransform * vec4(aPosition, 1.0);

    // UV を倍率で拡大する。1.0 を超えるとラップモードの出番になる。
    vTexCoord = aTexCoord * uUvScale;

    vColor = aColor;
}
