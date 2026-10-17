#version 330 core

// **原寸で重ねる**(Day 55)。被写界深度の最後の段。
//
// 3枚を奥から順に重ねる。
//   1. 原寸の絵(ピントの合ったところはこれがそのまま出る。半分の大きさの絵では1画素の細部が残らない)
//   2. 奥の層(その画素が奥にぼけている分だけ、原寸の絵と差し替える)
//   3. 手前の層(半透明の膜として上から被せる)
// 奥と手前を1枚にまとめて持つと、ピントの合った画素に手前のボケが3割被さったとき、
// 「どこまでが手前の色か」が分からなくなって、被さる量が足りなくなる——2枚に分けたのはそのため。

in vec2 vUv;

out vec4 FragColor;

uniform sampler2D uScene;   // 原寸の絵
uniform sampler2D uField;   // 奥行きと錯乱円の地図(原寸。y が錯乱円)
uniform sampler2D uFar;     // 奥の層(半分の大きさ。a が有効)
uniform sampler2D uNear;    // 手前の層(半分の大きさ。前掛けのアルファ)
uniform vec2 uHalfTexel;    // 1 / 半分の大きさ
uniform int uScatterAsGather;
uniform float uMaxRadius;

// 1 なら錯乱円を色で見せる(手前は青、奥は橙、ピントの帯は元の絵を暗く)。
uniform int uDebug;

/// 半分の大きさの絵を、双線形 4 回で**テントの形に**読む。
/// 1回の双線形だと、半分の大きさの升目がそのまま見える。4回ずらして平均すると、隣の升目となめらかに繋がる。
vec4 Tent(sampler2D source)
{
    vec2 d = uHalfTexel * 0.5;

    return 0.25 * (
        texture(source, vUv + vec2(-d.x, -d.y))
        + texture(source, vUv + vec2(d.x, -d.y))
        + texture(source, vUv + vec2(-d.x, d.y))
        + texture(source, vUv + vec2(d.x, d.y)));
}

vec3 CircleOfConfusionColor(vec3 sharp, float coc)
{
    // ピントの帯(半径 1 画素未満)は元の絵を暗く。そこだけ絵が見えるので、どこにピントが来ているかが分かる。
    vec3 base = vec3(dot(sharp, vec3(0.2126, 0.7152, 0.0722)) * 0.3);

    if (abs(coc) < 1.0)
    {
        return base;
    }

    float t = clamp(abs(coc) / uMaxRadius, 0.0, 1.0);
    vec3 tint = coc < 0.0 ? vec3(0.15, 0.45, 1.6) : vec3(1.6, 0.6, 0.15);
    return mix(base, tint, 0.25 + (0.75 * t));
}

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    vec3 sharp = texelFetch(uScene, pixel, 0).rgb;

    // **原寸の錯乱円**で切り替える。半分の大きさの錯乱円を使うと、ピントの帯の縁が 2x2 のギザギザになる。
    float coc = texelFetch(uField, pixel, 0).y;

    if (uDebug == 1)
    {
        FragColor = vec4(CircleOfConfusionColor(sharp, coc), 1.0);
        return;
    }

    // 奥の層。**有効な画素だけで平均する**(a で割る)。ピントの合った物の画素は a = 0 なので、
    // 双線形で読んでも物の色が奥のボケに混ざらない。有効な画素が1つも無ければ原寸の絵のまま。
    vec4 farLayer = Tent(uFar);
    vec3 far = farLayer.a > 0.001 ? farLayer.rgb / farLayer.a : sharp;

    // 半径 1〜2 画素でなめらかに切り替える。**半径 1 画素より小さいボケは、原寸の絵のまま**——
    // 半分の大きさの絵は 2 画素より細かいものを持っていないので、そこで差し替えると逆にぼけすぎる。
    // 素朴に集めたときは、手前も奥も自分の半径でぼかしたものが奥の層に入っているので、|錯乱円| で切り替える。
    float farAmount = smoothstep(1.0, 2.0, uScatterAsGather == 1 ? coc : abs(coc));
    vec3 color = mix(sharp, far, farAmount);

    // 手前の層を被せる(前掛けのアルファ: 下の色 × (1 − 覆い) + 手前の色 × 覆い)。
    vec4 near = Tent(uNear);
    color = (color * (1.0 - near.a)) + near.rgb;

    FragColor = vec4(color, 1.0);
}
