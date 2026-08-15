#version 330 core

in vec2 vTexCoord;
in vec4 vColor;

out vec4 FragColor;

// sampler2D には「テクスチャユニットの番号」が入る。テクスチャ本体ではない。
uniform sampler2D uTexture;

// マテリアルの色味
uniform vec4 uTint;

void main()
{
    // texture() が Day 8 で自作したサンプリング(バイリニア補間つき)に相当する。
    // フィルタもラップも、テクスチャ側に設定したパラメータに従って
    // ハードウェアが処理してくれる。
    vec4 texel = texture(uTexture, vTexCoord);

    // テクスチャ・頂点色・マテリアル色を掛け合わせる。
    // 掛け算なので「白は素通し」になり、使わない要素を白にしておけば無効化できる。
    FragColor = texel * vColor * uTint;
}
