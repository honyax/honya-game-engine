#version 330 core

// **奥行きと錯乱円の地図**(Day 55)。被写界深度とモーションブラーが共通で読む。
//
// 深度バッファの値(0〜1)を奥行き [m] に戻し、そこから錯乱円(ピントの外れた点が画面に広がる円)の半径を出す。
// **Day 55 で奥行きに戻すのはここだけ**。被写界深度もモーションブラーも、この地図を読むだけにしてある——
// 同じ式を何本ものシェーダに写すと、片方だけ直す日が必ず来る(Day 54 のスキニングの式が4か所になった話)。

in vec2 vUv;

out vec2 FragField;   // x: 奥行き [m] / y: 錯乱円の半径 [画素](ピントより手前が負、奥が正)

uniform sampler2D uDepth;

// 近クリップ面と遠クリップ面 [m]。
uniform vec2 uNearFar;

// 1 なら透視投影。
uniform int uPerspective;

// レンズの係数 L [画素・m](DepthOfField.LensScale)。0 なら錯乱円は全部 0(被写界深度を掛けない)。
uniform float uLensScale;

// ピントを合わせる奥行き S [m]。
uniform float uFocusDistance;

// 錯乱円の半径の上限 [画素](BlurField.MaxRadius)。
uniform float uMaxRadius;

/// 深度バッファの値 → 奥行き [m]。
///
/// 透視投影の式は Day 50 の particle.frag の LinearizeDepth と同じもの。深度は 1/z に比例して並んでいる(Day 7)ので、
/// それを逆にたどると z が出る。平行投影は深度がそのまま奥行きに比例する(W で割らないので)。
float ViewDepth(float depth01)
{
    float ndc = (depth01 * 2.0) - 1.0;

    if (uPerspective == 1)
    {
        return 2.0 * uNearFar.x * uNearFar.y / (uNearFar.y + uNearFar.x - (ndc * (uNearFar.y - uNearFar.x)));
    }

    return uNearFar.x + (depth01 * (uNearFar.y - uNearFar.x));
}

void main()
{
    // 1画素ぶんを texelFetch で引く。深度を隣と混ぜても意味が無い(Day 33 と同じ理由)。
    // (変数の名前を distance にしないのは、GLSL の組み込み関数 distance() を隠してしまうため。)
    float viewDepth = ViewDepth(texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r);

    // **錯乱円は 1/奥行き の1次式**(計画書の要点2)。
    //   ピントの奥行き(viewDepth = S)で 0
    //   奥へ行くほど L / S に近づく(無限遠でも有限。奥のボケには上限がある)
    //   手前へ来るほど際限なく大きくなる(手前のボケには上限が無い)
    // C# の DepthOfField.CircleOfConfusion と同じ形(自己チェックが文字列で見張っている)。
    float coc = uLensScale * ((1.0 / uFocusDistance) - (1.0 / viewDepth));

    FragField = vec2(viewDepth, clamp(coc, -uMaxRadius, uMaxRadius));
}
