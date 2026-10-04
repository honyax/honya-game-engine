#version 330 core

// ============================================================
//  MToon(Day 68)。VRM 1.0 の VRMC_materials_mtoon の式を、仕様と three-vrm のとおりに書いたもの。
//
//  色は3つの足し算でできている。
//
//    直接光 = mix(影の色, ベースカラー, shading) x 光の色         ← 要点2。**N・L を掛けない**
//    環境光 = ベースカラー x 環境光(上下で均したもの)           ← 要点2の後半
//    リム   = (縁の光 + マットキャップ) x 光の強さ              ← 要点3
//
//  PBR(textured.frag)との違いは「光の強さで明るさが決まる」のではなく、
//  **「光の向きで2色のどちらかを選ぶ」**こと。暗い側も黒ではなく影の色(作者が塗った色)になる。
//
//  textured.frag の関数のうち、プローブ(ProbeSh / ProbeIrradiance)と点光源の減衰(PointAttenuation)は
//  **1文字も変えずに写してある**(自己チェックが突き合わせる)。影(ShadowFactor)は手順を同じにして、長いコメントだけ省いた。
//  どれも光の約束をそろえるため——同じ灯りに照らされたシーンとキャラで、明るさの物差しが食い違わないように。
// ============================================================

in vec2 vTexCoord;
in vec3 vNormal;
in vec3 vWorldPos;
in vec4 vLightSpacePos;
in vec3 vTangent;
in vec3 vBitangent;

out vec4 FragColor;

// --- glTF のマテリアル(MToon も同じものを使う)---

uniform sampler2D uTexture;
uniform vec4 uBaseColorFactor;
uniform sampler2D uNormalMap;
uniform int uHasNormalMap;
uniform float uNormalScale;
uniform sampler2D uEmissiveMap;
uniform int uHasEmissiveMap;
uniform vec3 uEmissiveFactor;

/// 0 = OPAQUE / 1 = MASK(しきい値で捨てる)/ 2 = BLEND(混ぜる。描く側がブレンドを入れる)。
uniform int uAlphaMode;
uniform float uAlphaCutoff;

// --- MToon のつまみ(MToon.cs と同じ名前)---

uniform vec3 uShadeColorFactor;
uniform sampler2D uShadeMultiplyTexture;
uniform float uShadingShiftFactor;
uniform sampler2D uShadingShiftTexture;
uniform int uHasShadingShiftTexture;
uniform float uShadingShiftTextureScale;
uniform float uShadingToonyFactor;
uniform float uGiEqualizationFactor;
uniform vec3 uMatcapFactor;
uniform sampler2D uMatcapTexture;
uniform int uHasMatcapTexture;
uniform vec3 uParametricRimColorFactor;
uniform sampler2D uRimMultiplyTexture;
uniform float uRimLightingMixFactor;
uniform float uParametricRimFresnelPowerFactor;
uniform float uParametricRimLiftFactor;
uniform vec3 uOutlineColorFactor;
uniform float uOutlineLightingMixFactor;

/// アウトラインの回か(mtoon.vert と同じ uniform。リンク時に1つにまとまる)。
uniform int uOutlinePass;

// --- 光(Program.ApplyMToonFrame が送る)---

uniform vec3 uLightDirection;
uniform vec3 uLightColor;
uniform vec3 uAmbientColor;

/// PBR が ON なら太陽も点光源も π 倍されて届く(Program.ApplyLighting)。MToon はランバートの 1/π を持つ式なので、割って戻す。
uniform int uPbrEnabled;

/// **キャラに届く点光源は 8 個まで**(要点5)。textured.frag の 64 個の枠とは別の、キャラ専用の枠。
const int MAX_MTOON_LIGHTS = 8;
uniform int uPointLightCount;
uniform vec4 uPointLightPositions[MAX_MTOON_LIGHTS];
uniform vec4 uPointLightColors[MAX_MTOON_LIGHTS];

uniform vec3 uCameraPosition;
uniform mat4 uView;

/// 0 = MToon / 1 = ベースカラーだけ / 2 = N・L の陰影だけ /
/// 3 = 影の度合い / 4 = 直接光だけ / 5 = 環境光だけ / 6 = リムとマットキャップだけ。
uniform int uMToonView;

// --- 影(textured.frag から写した)---

uniform sampler2D uShadowMap;
uniform int uShadowEnabled;
uniform int uPcfRadius;
uniform float uShadowBias;
uniform float uShadowSlopeBias;
uniform vec2 uShadowTexelSize;

// --- 環境光: IBL(Day 36)とプローブ(Day 66a)---

uniform int uIblEnabled;
uniform float uIblIntensity;
uniform samplerCube uIrradianceMap;
uniform int uProbeEnabled;
uniform vec3 uProbeGridMin;
uniform float uProbeSpacing;
uniform ivec3 uProbeCount;
uniform sampler2D uProbeSh;

const float PI = 3.14159265359;

/// 1個のプローブの9係数を、向き n で評価する。SphericalHarmonics.Basis と同じ並び・同じ定数。
vec3 ProbeSh(int probe, vec3 n)
{
    vec3 sum = texelFetch(uProbeSh, ivec2(0, probe), 0).rgb * 0.282095;
    sum += texelFetch(uProbeSh, ivec2(1, probe), 0).rgb * (0.488603 * n.y);
    sum += texelFetch(uProbeSh, ivec2(2, probe), 0).rgb * (0.488603 * n.z);
    sum += texelFetch(uProbeSh, ivec2(3, probe), 0).rgb * (0.488603 * n.x);
    sum += texelFetch(uProbeSh, ivec2(4, probe), 0).rgb * (1.092548 * n.x * n.y);
    sum += texelFetch(uProbeSh, ivec2(5, probe), 0).rgb * (1.092548 * n.y * n.z);
    sum += texelFetch(uProbeSh, ivec2(6, probe), 0).rgb * (0.315392 * ((3.0 * n.z * n.z) - 1.0));
    sum += texelFetch(uProbeSh, ivec2(7, probe), 0).rgb * (1.092548 * n.x * n.z);
    sum += texelFetch(uProbeSh, ivec2(8, probe), 0).rgb * (0.546274 * ((n.x * n.x) - (n.y * n.y)));
    return sum;
}

/// **位置を囲む8点を混ぜて、向き n の放射照度 / π を返す**(IrradianceProbes.Irradiance と同じ手順)。
///
/// 重みは3軸の一次補間の積(三線形補間)。**壁があるかどうかを見ていない**——
/// 壁の向こうのプローブも、距離が近ければ同じだけ効く。これが光漏れの正体(計画書の要点5)。
/// 格子の外は端のプローブで塗る(位置を格子の中へ寄せる)。
vec3 ProbeIrradiance(vec3 worldPos, vec3 n)
{
    vec3 cell = clamp((worldPos - uProbeGridMin) / uProbeSpacing, vec3(0.0), vec3(uProbeCount - 1));

    // いちばん上の点に乗ったときは1つ手前の升に入れる(囲む相手が格子の外にならないように)。
    ivec3 base = min(ivec3(cell), uProbeCount - 2);
    vec3 t = cell - vec3(base);

    vec3 sum = vec3(0.0);

    for (int i = 0; i < 8; i++)
    {
        ivec3 corner = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        vec3 w = mix(vec3(1.0) - t, t, vec3(corner));
        ivec3 p = base + corner;

        // x がいちばん速く回る並び(IrradianceProbes.Index)。
        int probe = p.x + (uProbeCount.x * (p.y + (uProbeCount.y * p.z)));
        sum += ProbeSh(probe, n) * (w.x * w.y * w.z);
    }

    // 2次で打ち切ったぶんの波(リンギング)で負になる向きがある。光は負にならないので 0 で止める。
    return max(sum, vec3(0.0));
}

/// **その点が光から見えているか**(textured.frag と同じ)。1.0 = 当たっている、0.0 = 影。
float ShadowFactor(vec3 normal, vec3 lightDir)
{
    if (uShadowEnabled == 0)
    {
        return 1.0;
    }

    vec3 proj = vLightSpacePos.xyz / vLightSpacePos.w;
    proj = (proj * 0.5) + 0.5;

    if (proj.z > 1.0)
    {
        return 1.0;
    }

    float ndotl = max(dot(normal, lightDir), 0.0);
    float bias = uShadowBias + (uShadowSlopeBias * (1.0 - ndotl));

    float lit = 0.0;
    int taps = 0;

    for (int x = -uPcfRadius; x <= uPcfRadius; x++)
    {
        for (int y = -uPcfRadius; y <= uPcfRadius; y++)
        {
            float closest = texture(uShadowMap, proj.xy + (vec2(x, y) * uShadowTexelSize)).r;
            lit += (proj.z - bias) > closest ? 0.0 : 1.0;
            taps++;
        }
    }

    return lit / float(taps);
}

/// **点光源の減衰**(Day 52)。PointLight.Attenuation と textured.frag と同じ式。
float PointAttenuation(float distance, float radius)
{
    float ratio = distance / radius;
    float window = clamp(1.0 - (ratio * ratio * ratio * ratio), 0.0, 1.0);
    return (window * window) / ((distance * distance) + 1.0);
}

/// smoothstep の曲がっていない版(MToon.LinearStep と同じ)。a == b なら段差。
float LinearStep(float a, float b, float x)
{
    float width = b - a;
    if (width <= 1e-6)
    {
        return x >= a ? 1.0 : 0.0;
    }

    return clamp((x - a) / width, 0.0, 1.0);
}

/// **影の度合い**(要点2)。MToon.Shading と同じ式。**N・L に足してから段を付ける**。
float Shading(float nDotL, float shift)
{
    return LinearStep(-1.0 + uShadingToonyFactor, 1.0 - uShadingToonyFactor, nDotL + shift);
}

/// 法線マップ(textured.frag の PerturbNormal と同じ手順。緑の反転と表示の切り替えは持たない)。
vec3 PerturbNormal(vec3 n, vec2 uv)
{
    if (uHasNormalMap == 0)
    {
        return n;
    }

    vec3 sampled = (texture(uNormalMap, uv).rgb * 2.0) - 1.0;
    sampled.xy *= uNormalScale;

    vec3 t = normalize(vTangent);
    vec3 b = normalize(vBitangent);
    return normalize((t * sampled.x) + (b * sampled.y) + (n * sampled.z));
}

/// **環境光**(要点2の後半)。プローブがあればプローブ、無ければ IBL、IBL も無ければ定数。
/// どれも「1/π 済みの放射照度」で、ベースカラーを掛ければそのまま拡散の明るさになる(textured.frag と同じ約束)。
vec3 Irradiance(vec3 n)
{
    if (uIblEnabled == 0)
    {
        return uAmbientColor;
    }

    return uProbeEnabled == 1
        ? ProbeIrradiance(vWorldPos, n)
        : texture(uIrradianceMap, n).rgb * uIblIntensity;
}

void main()
{
    vec2 uv = vTexCoord;
    vec4 base = texture(uTexture, uv) * uBaseColorFactor;

    // --- アルファ。**光を計算する前に捨てる**(捨てる画素の光を計算しても無駄)---
    if (uAlphaMode == 1 && base.a < uAlphaCutoff)
    {
        discard;
    }

    float alpha = uAlphaMode == 2 ? base.a : 1.0;

    vec3 n = normalize(vNormal);

    // **両面のマテリアルは裏から見たら法線を裏返す**。髪や服の裾は1枚の板なので、
    // 裏を表の法線のまま照らすと、光の側を向いた裏面が「明るい側」になる(three-vrm も同じ)。
    // textured.frag はこれをしていない(Day 66a の歪みの4つ目)。
    if (!gl_FrontFacing)
    {
        n = -n;
    }

    // **アウトラインの回は法線を裏返す**。描いているのは押し出した殻の裏面なので、
    // 法線はカメラと反対を向いている。表へ向け直してから照らす(outlineLightingMix が 1 のときに効く)。
    if (uOutlinePass == 1)
    {
        n = -n;
    }

    n = PerturbNormal(n, uv);

    vec3 v = normalize(uCameraPosition - vWorldPos);
    vec3 l = normalize(-uLightDirection);

    // --- 比べる描き方(F3)---
    if (uMToonView == 1)
    {
        // KHR_materials_unlit の見た目。VRM を MToon も PBR も無しで読んだビューアは、こう見える。
        FragColor = vec4(base.rgb, alpha);
        return;
    }

    // 光の色。PBR が ON なら π 倍されて届くので戻す(上の uPbrEnabled のコメント)。
    float lightScale = uPbrEnabled == 1 ? 1.0 / PI : 1.0;
    vec3 sunColor = uLightColor * lightScale;

    if (uMToonView == 2)
    {
        // **N・L の陰影だけ**(ランバート)。写実の道具でいちばん素朴な陰影。MToon と見比べるための基準。
        float nDotL = max(dot(n, l), 0.0);
        vec3 lambert = base.rgb * (sunColor * nDotL * ShadowFactor(n, l) + Irradiance(n));
        FragColor = vec4(lambert, alpha);
        return;
    }

    // --- 影の色と、境目のずらし ---
    vec3 shadeColor = uShadeColorFactor * texture(uShadeMultiplyTexture, uv).rgb;

    float shift = uShadingShiftFactor;
    if (uHasShadingShiftTexture == 1)
    {
        shift += texture(uShadingShiftTexture, uv).r * uShadingShiftTextureScale;
    }

    // --- 直接光: 太陽 ---
    //
    // **影(シャドウマップ)は shading に掛ける**。光が遮られたところは「明るい側」から「影の側」へ寄る——
    // 黒くなるのではなく、影の色になる。トゥーンの影がにごらないのはこのため。
    float sunShading = Shading(dot(n, l), shift) * ShadowFactor(n, l);
    vec3 direct = mix(shadeColor, base.rgb, sunShading) * sunColor;

    // リムを照らす「届いている光」。直接光は N・L を掛けない色のまま足す(three-vrm の directSpecular)。
    vec3 lightForRim = sunColor;

    // --- 直接光: 点光源(8 個まで)---
    //
    // 太陽と同じ式で、光の色を距離で減衰させるだけ。**点光源にも影の色が出る**——
    // 灯りに背を向けた面は、影の色 x 灯りの色で照らされる(three-vrm も同じ扱い)。
    for (int i = 0; i < uPointLightCount; i++)
    {
        vec3 toLight = uPointLightPositions[i].xyz - vWorldPos;
        float distance = length(toLight);

        if (distance >= uPointLightPositions[i].w)
        {
            continue;
        }

        vec3 pointColor = uPointLightColors[i].rgb * lightScale
            * PointAttenuation(distance, uPointLightPositions[i].w);
        float pointShading = Shading(dot(n, toLight / max(distance, 1e-4)), shift);

        direct += mix(shadeColor, base.rgb, pointShading) * pointColor;
        lightForRim += pointColor;
    }

    // --- 環境光: 上下で均す(giEqualization)---
    //
    // 法線の向きの環境光と、真上と真下の平均を混ぜる。1 に近いほど向きによらない一様な明るさになり、
    // **環境光が作る陰影が消える**——トゥーンの陰影は直接光の2色で決めたいので、環境光の陰影は邪魔になる。
    vec3 giDirectional = Irradiance(n);
    vec3 giUniform = (Irradiance(vec3(0.0, 1.0, 0.0)) + Irradiance(vec3(0.0, -1.0, 0.0))) * 0.5;
    vec3 gi = mix(giDirectional, giUniform, uGiEqualizationFactor);

    // 環境光は影の色を使わない(ベースカラーだけ)。three-vrm の RE_IndirectDiffuse_MToon と同じ。
    vec3 indirect = gi * base.rgb;

    // --- リムとマットキャップ ---
    float nDotV = dot(n, v);
    vec3 rim = uParametricRimColorFactor
        * pow(clamp(1.0 - nDotV + uParametricRimLiftFactor, 0.0, 1.0), uParametricRimFresnelPowerFactor);

    if (uHasMatcapTexture == 1)
    {
        // **ビュー空間の法線で引く**。ただし真正面の軸ではなく、視線そのものを軸にした基底を作る。
        // 画面の端のほうの物は斜めから見ているので、真正面の軸で引くとマットキャップが横へずれる(three-vrm と同じ作り方)。
        vec3 viewDir = normalize(mat3(uView) * v);
        vec3 viewNormal = normalize(mat3(uView) * n);
        vec3 x = normalize(vec3(viewDir.z, 0.0, -viewDir.x));
        vec3 y = cross(viewDir, x);
        vec2 matcapUv = 0.5 + (0.5 * vec2(dot(x, viewNormal), -dot(y, viewNormal)));

        // テクスチャは上下を反転して読んでいる(Texture のコメント)ので、V も反転して引く。
        rim += uMatcapFactor * texture(uMatcapTexture, vec2(matcapUv.x, 1.0 - matcapUv.y)).rgb;
    }

    rim *= texture(uRimMultiplyTexture, uv).rgb;
    rim *= mix(vec3(1.0), lightForRim + gi, uRimLightingMixFactor);

    vec3 emissive = uEmissiveFactor;
    if (uHasEmissiveMap == 1)
    {
        emissive *= texture(uEmissiveMap, uv).rgb;
    }

    // --- 成分を1つずつ見る(F4)---
    if (uMToonView == 3) { FragColor = vec4(vec3(sunShading), alpha); return; }
    if (uMToonView == 4) { FragColor = vec4(direct, alpha); return; }
    if (uMToonView == 5) { FragColor = vec4(indirect, alpha); return; }
    if (uMToonView == 6) { FragColor = vec4(rim, alpha); return; }

    vec3 color = direct + indirect + rim + emissive;

    // --- アウトラインの回: 線の色 ---
    //
    // outlineLightingMix が 0 なら線の色のまま、1 なら照らした色を掛ける。
    // 今日のアバターは顔・体・服とも 0(暗い所でも線の色が変わらない)。
    if (uOutlinePass == 1)
    {
        color = uOutlineColorFactor * mix(vec3(1.0), direct + indirect, uOutlineLightingMixFactor);
    }

    FragColor = vec4(color, alpha);
}
