#version 330 core

in vec2 vTexCoord;
in vec4 vColor;

uniform sampler2D uTexture;

// 明るさの倍率。**加算合成のときだけ意味を持つ**。
//
// シーンのバッファは RGBA16F(Day 31)なので、1.0 を超える色をそのまま置ける。
// ここを 1 より大きくすると、ブルームのしきい値(既定 1.0)を越えて
// **粒がにじむ**——火花や爆発が「光っている」ように見えるのはこの効果による。
// アルファ合成では 1.0 を超えても背景と混ざるだけなので、見た目はあまり変わらない。
uniform float uIntensity;

// 事前乗算アルファか(ParticleBlend.Premultiplied)。
//
// **加算とアルファの両方を1つのブレンド式で扱う**ための仕掛け。
// RGB にあらかじめ A を掛けておくと、ブレンドは
// `src * 1 + dst * (1 - srcA)` の1通りで済み、
// **A を 0 に寄せた粒は自動的に加算に近づく**。
// 詳しくは Day 49 の計画書の要点2。
uniform int uPremultiply;

// ============================================================
//  今日の主役: ソフトパーティクル(Day 50)
// ============================================================
//
// **シーンの深度のコピー**。いま描き込んでいるフレームバッファに挿さっている
// 深度そのものではない(それを読むのはフィードバックループで未定義)。
// PostProcess.CaptureDepth() が glBlitFramebuffer で写したものが来る。
uniform sampler2D uSceneDepth;

// 何メートル手前から薄れ始めるか。**0 ならソフトパーティクルを切ってある**。
//
// 0 のときは texture() を1回も呼ばないので、深度が挿さっていなくても安全。
uniform float uSoftFade;

// カメラの near と far。深度を距離へ戻すのに要る。
uniform vec2 uNearFar;

// 画面(シーンバッファ)の大きさ。gl_FragCoord.xy を UV に直すのに要る。
//
// **textureSize(uSceneDepth, 0) でも取れる**が、
// 深度のコピーが画面と違う大きさ(半解像度など)になった日に嘘をつく。
// 「何を基準に UV を作るか」は呼ぶ側が決めるべきものなので uniform で渡す。
uniform vec2 uScreenSize;

out vec4 FragColor;

// **深度バッファの値を「カメラからの距離(m)」へ戻す**。
//
// 射影行列は z を 1/z に近い形に潰して深度バッファへ書く。
// 手前に精度を厚く配るための仕掛けだが、そのぶん**値の差が距離の差に比例しない**。
// near=0.1 / far=100 だと、深度 0.5 はもう 0.2m の地点にある。
// 距離で比べたいので、まず両方をここで戻す。
//
// C# 側の ParticleRenderer.LinearizeDepth と1文字ずつ同じ式にしてある。
float LinearizeDepth(float depth01)
{
    float ndc = depth01 * 2.0 - 1.0;
    return 2.0 * uNearFar.x * uNearFar.y
        / (uNearFar.y + uNearFar.x - ndc * (uNearFar.y - uNearFar.x));
}

// 手前にある不透明な面にどれだけ近いか。**1 でそのまま、0 で消える**。
float SoftFadeFactor()
{
    if (uSoftFade <= 0.0)
    {
        return 1.0;
    }

    // gl_FragCoord.xy は画素の中心なので、割るだけで UV になる(0.5 を足さない)。
    vec2 screenUv = gl_FragCoord.xy / uScreenSize;

    float sceneDistance = LinearizeDepth(texture(uSceneDepth, screenUv).r);

    // **粒自身の深度は gl_FragCoord.z**。深度を書いていなくても読めるのが要点で、
    // ラスタライザが補間した値がそのまま入っている。
    float particleDistance = LinearizeDepth(gl_FragCoord.z);

    return clamp((sceneDistance - particleDistance) / uSoftFade, 0.0, 1.0);
}

void main()
{
    vec4 texel = texture(uTexture, vTexCoord);

    // 頂点色は「寿命に沿った色と不透明度」(ParticleEmitter が作る)。
    // テクスチャは「粒の形」。**掛け算で混ぜる**のは Day 17 のスプライトと同じ。
    vec4 color = texel * vColor;

    // **薄めるのはアルファだけ**(Day 50)。
    //
    // RGB を薄めると、アルファ合成では「黒へ寄る」ことになって
    // 土埃が地面の手前で**黒ずむ**。加算では rgb を薄めても同じ結果になるが、
    // アルファでも正しいのはこちらなので、片方に揃える。
    // 事前乗算(下)より前に掛けるのも同じ理由——後だと二重に掛かる。
    color.a *= SoftFadeFactor();

    color.rgb *= uIntensity;

    if (uPremultiply != 0)
    {
        color.rgb *= color.a;
    }

    FragColor = color;
}
