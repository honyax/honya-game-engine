#version 330 core

// 空を描く(Day 36)。**立方体を内側から見ているだけ**。
layout (location = 0) in vec3 aPosition;

// **平行移動を抜いたビュー行列**を渡す約束。
// カメラがどこに居ても空は同じ、という当たり前のことを、
// 行列から平行移動を落とすだけで表せる(EnvironmentMap.DrawSkybox)。
uniform mat4 uViewProjection;

out vec3 vDirection;

void main()
{
    vDirection = aPosition;

    vec4 clip = uViewProjection * vec4(aPosition, 1.0);

    // **z を w にして、深度を必ず 1.0(いちばん奥)にする**。
    //
    // 透視除算のあと z/w = w/w = 1 になる。深度テストを LEQUAL にしておけば、
    // 「まだ何も描かれていないところ」だけが空で埋まる。
    //
    // 立方体の大きさをいくらにしても関係なくなる、というのがこの技の値打ち。
    // 大きな箱を描いて遠クリップにぶつける、という素朴なやり方は
    // **遠クリップを変えるたびに調整が要る**。
    gl_Position = clip.xyww;
}
