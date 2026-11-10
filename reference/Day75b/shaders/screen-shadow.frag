#version 330 core

// **画面の影**(Day 75b)。画素ごとに「太陽から見えているか」を2通り求め、RG8 の1枚に並べて書く。
//
//   R = 世界の影 … 世界のシャドウマップをそのまま引く。deferred.frag の ShadowFactor と同じ式・同じバイアス。
//                  シーンのライティングパスはこれを読む(Day 75a までと同じ答え)
//   G = キャラの影 … キャラ専用のシャドウマップ(細かい)× R。MToon はこれを読む
//
// 入力は G-Buffer の2枚目(法線と距離)だけ。三角形を1つも知らないのはライティングパスと同じ。

out vec4 FragColor;

// --- 位置を戻す材料(GBuffer.Apply が送る。deferred.frag と同じ名前)---
uniform sampler2D uGNormalDepth;  // xyz = ビュー空間の法線 / w = 距離(0 なら空)
uniform mat4 uInverseView;
uniform vec2 uProjScale;
uniform int uOrthographic;

/// 画面の大きさ(ScreenShadow.Render が送る。deferred.frag は Ssao.Apply から同じ名前で受け取る)。
uniform vec2 uScreenSize;

/// 光の**進む**向き(世界のシャドウマップの Begin で決めた向き)。
uniform vec3 uLightDirection;

// --- 世界のシャドウマップ(ShadowMap.Apply が送る。deferred.frag と同じ名前)---
uniform sampler2D uShadowMap;
uniform int uShadowEnabled;
uniform int uPcfRadius;
uniform float uShadowBias;
uniform float uShadowSlopeBias;
uniform vec2 uShadowTexelSize;
uniform mat4 uLightSpaceMatrix;

// --- キャラ専用のシャドウマップ(ScreenShadow.Render が送る)---
uniform sampler2D uCharacterShadowMap;
uniform int uCharacterShadowEnabled;
uniform int uCharacterPcfRadius;
uniform float uCharacterBias;
uniform float uCharacterSlopeBias;
uniform vec2 uCharacterTexelSize;
uniform mat4 uCharacterMatrix;

/// 距離からビュー空間の位置を戻す。deferred.frag・ssao.frag の ViewPosition と同じ式。
vec3 ViewPosition(vec2 uv, float depth)
{
    vec2 ndc = (uv * 2.0) - 1.0;
    vec2 planeXy = ndc * uProjScale;

    return uOrthographic == 1
        ? vec3(planeXy, -depth)
        : vec3(planeXy * depth, -depth);
}

/// PCF。deferred.frag の ShadowFactor のループと同じ数え方(1.0 = 当たっている)。
/// シャドウマップを引数にしたのは、同じループで2枚を引くため。
float Pcf(sampler2D map, vec3 proj, float bias, int radius, vec2 texelSize)
{
    float lit = 0.0;
    int taps = 0;

    for (int x = -radius; x <= radius; x++)
    {
        for (int y = -radius; y <= radius; y++)
        {
            float closest = texture(map, proj.xy + (vec2(x, y) * texelSize)).r;
            lit += (proj.z - bias) > closest ? 0.0 : 1.0;
            taps++;
        }
    }

    return lit / float(taps);
}

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    vec4 normalDepth = texelFetch(uGNormalDepth, pixel, 0);

    // **空は光が当たっている扱い**。距離 0 は「何も描かれていない」(Day 37 の約束)。
    if (normalDepth.w <= 0.0)
    {
        FragColor = vec4(1.0, 1.0, 0.0, 1.0);
        return;
    }

    // **uv の作り方まで deferred.frag と同じにする**。fullscreen.vert の vUv でも同じ画素の中心を指すが、
    // 補間の丸めで最後の桁が違い、PCF の 25 タップのうち1つがちょうど境目で入れ替わる画素が出た(R が Day 75a と食い違う)。
    vec2 uv = gl_FragCoord.xy / uScreenSize;
    vec3 viewPosition = ViewPosition(uv, normalDepth.w);
    vec3 worldPos = (uInverseView * vec4(viewPosition, 1.0)).xyz;
    vec3 normal = normalize(mat3(uInverseView) * normalDepth.xyz);

    vec3 toLight = -uLightDirection;
    float ndotl = max(dot(normal, toLight), 0.0);

    // --- R: 世界の影(deferred.frag の ShadowFactor と同じ) ---
    vec4 lightSpace = uLightSpaceMatrix * vec4(worldPos, 1.0);
    vec3 proj = (lightSpace.xyz / lightSpace.w * 0.5) + 0.5;
    float worldBias = uShadowBias + (uShadowSlopeBias * (1.0 - ndotl));

    float world = uShadowEnabled == 1 && proj.z <= 1.0
        ? Pcf(uShadowMap, proj, worldBias, uPcfRadius, uShadowTexelSize)
        : 1.0;

    // --- G: キャラの影 ---
    float character = world;

    if (uCharacterShadowEnabled == 1)
    {
        // キャラ専用のシャドウマップ。箱が小さいので1テクセルが細かい(顔の上の前髪の影まで描ける)。
        vec4 characterSpace = uCharacterMatrix * vec4(worldPos, 1.0);
        vec3 characterProj = (characterSpace.xyz / characterSpace.w * 0.5) + 0.5;

        // **箱の奥より遠い受け手は、箱の奥の面に置く**。光の空間で見ると、キャラの影はどこに落ちてもキャラの真後ろにある。
        // 後ろの壁は箱の奥(中心から半径の 1.5 倍)より遠いが、キャラの影が落ちる先。1.0 で止めれば、キャラが焼けたテクセルとは
        // 比べて影になり、箱の横の外(境界色 1.0)とは「1.0 - バイアス > 1.0」が成り立たないので当たっている扱いになる。
        // 止めないと z が 1 を超え、箱の横の外まで全部影になる(世界のシャドウマップは z > 1 を当たっている扱いにして避けた。Day 33)。
        characterProj.z = min(characterProj.z, 1.0);

        float characterBias = uCharacterBias + (uCharacterSlopeBias * (1.0 - ndotl));
        float self = Pcf(uCharacterShadowMap, characterProj, characterBias, uCharacterPcfRadius, uCharacterTexelSize);

        // **世界の影を掛ける**。建物や街灯の影の中に立てば、キャラも影に入る(キャラ専用のシャドウマップにはキャラしか居ない)。
        // 世界のシャドウマップにはキャラ自身も焼かれているので、粗いキャラの影も一緒に掛かる——ただ、世界のバイアス(7cm)が
        // キャラの凹凸より大きく、裏通りの夕暮れではキャラの影にすでに含まれる分しか出ない(計画書の「今日残した歪み」)。
        character = self * world;
    }

    FragColor = vec4(world, character, 0.0, 1.0);
}
