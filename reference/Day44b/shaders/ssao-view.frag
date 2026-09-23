#version 330 core

in vec2 vUv;

out vec4 FragColor;

uniform sampler2D uAo;

/// 1パス目の中身(RGB = ビュー法線、A = 距離)。
uniform sampler2D uGeometry;

/// 1=AO / 2=ビュー空間の法線 / 3=距離。
uniform int uMode;

/// 距離を色に写すときの上限(メートル)。近いほど白い。
uniform float uDepthRange;

void main()
{
    if (uMode == 2)
    {
        // 法線は -1〜1 なので 0〜1 に写して色として出す。
        //
        // **見どころは「面ごとに一様な色か」**。
        // カメラを回すと色が変わるのが正しい——ビュー空間の法線なので、
        // 世界に固定された色にはならない。
        // ここが世界空間の法線(textured.frag の成分 2)と違うところで、
        // **回しても色が変わらないなら uNormalMatrix が効いていない**。
        vec3 normal = texture(uGeometry, vUv).rgb;
        FragColor = vec4((normalize(normal) * 0.5) + 0.5, 1.0);
        return;
    }

    if (uMode == 3)
    {
        // 距離。**線形なのでそのまま割ればよい**(shadow-view.frag のような
        // 非線形の戻しが要らない)。1パス目が -z をそのまま書いているおかげ。
        float depth = texture(uGeometry, vUv).a;
        FragColor = vec4(vec3(1.0 - clamp(depth / uDepthRange, 0.0, 1.0)), 1.0);
        return;
    }

    // AO そのもの。**白 = 遮られていない、黒 = 遮られている**。
    //
    // これを1枚で見るのが今日いちばん大事なデバッグ手段になる。
    // 合成したあとの絵では「なんとなく締まった」以上のことが言えず、
    //   - 半径が大きすぎて壁一面が暗い
    //   - バイアスが 0 で平らな面に縞が出ている
    //   - ノイズのタイルが残っている
    // のどれも、混ざった絵からは読み取れない。
    FragColor = vec4(vec3(texture(uAo, vUv).r), 1.0);
}
