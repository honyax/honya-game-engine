#version 330 core

in vec3 vColor;

out vec4 FragColor;

// 経過秒。ホットリロードの実験用に、時間で変化する要素を1つ入れてある。
uniform float uTime;

void main()
{
    // 明るさをゆっくり脈打たせる。0.75 〜 1.0 の範囲。
    float pulse = 0.875 + 0.125 * sin(uTime * 2.0);

    FragColor = vec4(vColor * pulse, 1.0);

    // --- ホットリロードを試すなら、この下の行のコメントを外して F5 ---
    // FragColor = vec4(1.0 - vColor, 1.0);
}
