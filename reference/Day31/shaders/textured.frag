#version 330 core

in vec2 vTexCoord;
in vec4 vColor;

out vec4 FragColor;

// sampler2D には「テクスチャユニットの番号」が入る。テクスチャ本体ではない。
uniform sampler2D uTexture;

// マテリアルの色味
uniform vec4 uTint;

/// sRGB からリニアへ(sprite.frag と同じ。Day 31)。
vec3 SrgbToLinear(vec3 color)
{
    return pow(color, vec3(2.2));
}

void main()
{
    // texture() が Day 8 で自作したサンプリング(バイリニア補間つき)に相当する。
    // フィルタもラップも、テクスチャ側に設定したパラメータに従って
    // ハードウェアが処理してくれる。
    //
    // **Day 31 からは、この戻り値がリニアな明るさになっている**。
    // テクスチャを Srgb8Alpha8 で持つようにしたので、GPU が 2.2 乗を戻して渡してくる。
    vec4 texel = texture(uTexture, vTexCoord);

    // テクスチャ・頂点色・マテリアル色を掛け合わせる。
    // 掛け算なので「白は素通し」になり、使わない要素を白にしておけば無効化できる。
    //
    // **uTint だけは変換しない**。ここが今日の分かれ目で、
    // Day 31 から uTint は「1 を超えてよい、リニアな明るさの倍率」という意味になった。
    // 発光する物体(Program.cs の Emitters)は uTint に 6.0 や 9.0 を入れる——
    // sRGB として扱うと 6.0 が 2.2 乗されて 60 になり、指定した明るさと合わなくなる。
    //
    // 「見た目で選ぶ色」と「明るさの倍率」を同じ uniform に載せているのが本来おかしく、
    // PBR(Day 35)ではベースカラーと発光を別の入力に分ける。
    FragColor = texel * vec4(SrgbToLinear(vColor.rgb), vColor.a) * uTint;
}
