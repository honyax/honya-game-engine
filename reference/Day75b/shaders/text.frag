#version 330 core

in vec2 vTexCoord;
in vec4 vColor;

out vec4 FragColor;

uniform sampler2D uTexture;

/// sprite.frag と同じもの。**GLSL には #include が無い**ので、こうして写すしかない。
/// 実際のエンジンはシェーダを読み込むときに自前で #include を展開する
/// (テキストを差し込むだけなので、実装は数十行で済む)。
vec3 SrgbToLinear(vec3 color)
{
    return pow(color, vec3(2.2));
}

void main()
{
    // **グリフのアトラスは1チャンネル**(GL_R8)。
    // 入っているのは「この画素のどれだけが字で覆われているか」だけで、色は無い。
    // 読み出せるのは r だけ(g と b は 0、a は 1 が返る)ので、
    // sprite.frag のように texture(...) * vColor と書くと真っ黒になる。
    //
    // **被覆率は sRGB ではない**ので、アトラスは R8 のままでよい(Day 31)。
    // 面積の割合であって明るさではないため、ガンマの話が入る余地が無い。
    float coverage = texture(uTexture, vTexCoord).r;

    // 色は頂点から来る。被覆率はアルファに掛ける——
    // こうすると、字の輪郭がそのまま「どれだけ濃く出すか」になる。
    // これがアンチエイリアスの正体で、輪郭画素だけが半透明になっている。
    FragColor = vec4(SrgbToLinear(vColor.rgb), vColor.a * coverage);
}
