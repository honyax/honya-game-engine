#version 330 core

in vec2 vTexCoord;
in vec4 vColor;

out vec4 FragColor;

uniform sampler2D uTexture;

void main()
{
    // アルファも掛け算する。テクスチャの α が 0 のところは
    // 頂点色に関係なく透明になり、切り抜きとして働く。
    FragColor = texture(uTexture, vTexCoord) * vColor;
}
