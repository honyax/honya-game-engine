#version 330 core

in vec2 vTexCoord;
in vec4 vColor;

out vec4 FragColor;

uniform sampler2D uTexture;

void main()
{
    // **グリフのアトラスは1チャンネル**(GL_R8)。
    // 入っているのは「この画素のどれだけが字で覆われているか」だけで、色は無い。
    // 読み出せるのは r だけ(g と b は 0、a は 1 が返る)なので、
    // sprite.frag のように texture(...) * vColor と書くと真っ黒になる。
    float coverage = texture(uTexture, vTexCoord).r;

    // 色は頂点から来る。被覆率はアルファに掛ける——
    // こうすると、字の輪郭がそのまま「どれだけ濃く出すか」になる。
    // これがアンチエイリアスの正体で、輪郭画素だけが半透明になっている。
    FragColor = vec4(vColor.rgb, vColor.a * coverage);
}
