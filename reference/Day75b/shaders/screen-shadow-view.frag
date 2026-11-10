#version 330 core

// 画面の影の1チャンネルを白黒で出す(Day 75b。「キャラ専用の影」の F5)。
// 白 = 光が当たっている、黒 = 影。ssao-view.frag の遮蔽率と同じ見せ方。

in vec2 vUv;

out vec4 FragColor;

uniform sampler2D uScreenShadow;

/// 0 = R(世界の影)/ 1 = G(キャラの影)。
uniform int uChannel;

void main()
{
    vec2 rg = texture(uScreenShadow, vUv).rg;
    FragColor = vec4(vec3(uChannel == 0 ? rg.r : rg.g), 1.0);
}
