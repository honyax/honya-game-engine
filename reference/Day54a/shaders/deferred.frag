#version 330 core

// **ライティングパス**(Day 52)。G-Buffer を読んで光を当てる。
//
// textured.frag を「表面を決める前半」と「光を当てる後半」に切ったときの**後半**にあたる。
// 前半は textured.frag の uGBufferPass の枝がやり、G-Buffer に書いて終わる。
// ここは三角形を1つも知らない——入力は画素の位置(gl_FragCoord)と G-Buffer の4枚だけ。
//
// **同じこのファイルが2本のプログラムになる**(DeferredLighting)。
//   - fullscreen.vert と組む   … 太陽・環境光・発光。画面の全画素を1回ずつ
//   - light-volume.vert と組む … 点光源1個ぶん。その球が覆った画素だけ。加算で重ねる
//
// **入力(in)を1つも宣言しない**のはそのため。2本の頂点シェーダが出すものが違い、
// 片方にしか無いものを読むとリンクで落ちる。どちらでも使える gl_FragCoord だけで組み立てる。

out vec4 FragColor;

// --- G-Buffer(GBuffer.Apply が刺す。番号は Material の表をそのまま引き継いでいる)---
uniform sampler2D uGAlbedo;       // 0: ベースカラーの平方根
uniform sampler2D uGMaterial;     // 1: R = AO / G = 粗さ / B = 金属度
uniform sampler2D uGNormalDepth;  // 2: xyz = ビュー空間の法線 / w = 距離(0 なら空)
uniform sampler2D uGEmissive;     // 4: 発光

/// ビュー空間 → 世界。G-Buffer はビュー空間(Day 37 の形)で、光は世界で考えるので戻す。
uniform mat4 uInverseView;

/// 射影行列の対角成分の逆数(ssao.frag と同じもの)。距離から位置を戻すのに使う。
uniform vec2 uProjScale;

uniform int uOrthographic;

/// 画面の大きさ。Ssao.Apply が送る(textured.frag と同じ uniform)。
uniform vec2 uScreenSize;

// --- 点光源のパス(DeferredLighting が1個ずつ送る)---

/// 0 = 太陽のパス(全画面)、1 = 点光源のパス(球)。
uniform int uPointLightPass;

/// xyz = 位置 / w = 届く距離。light-volume.vert も同じものを読む。
uniform vec4 uPointPosition;

uniform vec3 uPointColor;

/// 光の重なりを見る(「ディファード」の F6)。
uniform int uOverdrawView;

// --- ここから下は textured.frag と同じ名前・同じ意味(Program.ApplyLighting が送る)---
//
// **名前をそろえてある**のが、フォワードとディファードで同じ関数を使い回せる理由。
// C# 側は「どちらのシェーダに送っているか」を知らずに済む。

uniform vec3 uLightDirection;
uniform vec3 uLightColor;
uniform vec3 uAmbientColor;
uniform vec3 uCameraPosition;

uniform int uPbrEnabled;
uniform float uDielectricF0;
uniform int uPerceptualRoughness;
uniform int uAmbientSpecular;

uniform sampler2D uShadowMap;
uniform int uShadowEnabled;
uniform int uPcfRadius;
uniform float uShadowBias;
uniform float uShadowSlopeBias;
uniform vec2 uShadowTexelSize;
uniform mat4 uLightSpaceMatrix;

uniform int uIblEnabled;
uniform float uIblIntensity;
uniform samplerCube uIrradianceMap;
uniform samplerCube uPrefilterMap;
uniform sampler2D uBrdfLut;
uniform float uPrefilterMaxLod;
uniform int uIblPrefilter;

uniform sampler2D uAoMap;
uniform int uSsaoEnabled;
uniform int uSsaoOnDirect;

const float PI = 3.14159265359;

/// 粗さの下限。**textured.frag と Pbr.MinRoughness と同じ値**(自己チェックが3か所を突き合わせる)。
const float MIN_ROUGHNESS = 0.045;

/// **距離から、その画素のビュー空間の位置を戻す**。ssao.frag の ViewPosition と同じ式。
///
/// G-Buffer に位置を持たせなかった(GBuffer のコメント)ぶんを、ここで払う。
/// 払うのは掛け算数回で、RGBA16F を1枚ぶん読む帯域よりずっと安い。
vec3 ViewPosition(vec2 uv, float depth)
{
    vec2 ndc = (uv * 2.0) - 1.0;
    vec2 planeXy = ndc * uProjScale;

    return uOrthographic == 1
        ? vec3(planeXy, -depth)
        : vec3(planeXy * depth, -depth);
}

// ============================================================
//  ここから CookTorrance までは textured.frag と**同じ式**(Day 35〜36)。
//  説明はそちらに書いてある。**片方だけ直さないこと**——
//  フォワードとディファードで絵が食い違い、しかもどちらもそれらしく見える。
// ============================================================

float DistributionGgx(float nDotH, float alpha)
{
    float a2 = alpha * alpha;
    float d = (nDotH * nDotH * (a2 - 1.0)) + 1.0;
    return a2 / (PI * d * d);
}

float GeometrySchlickGgx(float nDotX, float k)
{
    return nDotX / ((nDotX * (1.0 - k)) + k);
}

float GeometrySmith(float nDotV, float nDotL, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return GeometrySchlickGgx(nDotV, k) * GeometrySchlickGgx(nDotL, k);
}

vec3 FresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 + ((1.0 - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0));
}

vec3 FresnelSchlickRoughness(float cosTheta, vec3 f0, float roughness)
{
    vec3 ceiling = max(vec3(1.0 - roughness), f0);
    return f0 + ((ceiling - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0));
}

/// textured.frag の CookTorrance から、デバッグ表示用の D・G・F の出口を外したもの。
void CookTorrance(
    vec3 n, vec3 v, vec3 l,
    vec3 albedo, float metallic, float roughness,
    out vec3 diffuse, out vec3 specular)
{
    diffuse = vec3(0.0);
    specular = vec3(0.0);

    vec3 f0 = mix(vec3(uDielectricF0), albedo, metallic);

    float nDotL = dot(n, l);
    if (nDotL <= 0.0)
    {
        return;
    }

    float nDotV = max(dot(n, v), 1e-4);

    vec3 h = normalize(v + l);
    float nDotH = max(dot(n, h), 0.0);
    float vDotH = max(dot(v, h), 0.0);

    float r = max(roughness, MIN_ROUGHNESS);
    float alpha = uPerceptualRoughness == 1 ? (r * r) : r;

    float d = DistributionGgx(nDotH, alpha);
    float g = GeometrySmith(nDotV, nDotL, r);
    vec3 f = FresnelSchlick(vDotH, f0);

    specular = (d * g * f) / (4.0 * nDotV * nDotL);

    vec3 kd = (vec3(1.0) - f) * (1.0 - metallic);
    diffuse = kd * albedo / PI;
}

/// 点光源の減衰。**textured.frag と PointLight.Attenuation と同じ式**。
float PointAttenuation(float distance, float radius)
{
    float ratio = distance / radius;
    float window = clamp(1.0 - (ratio * ratio * ratio * ratio), 0.0, 1.0);
    return (window * window) / ((distance * distance) + 1.0);
}

/// 点光源1つぶんの明るさ。**textured.frag と同じ関数**。
vec3 PointLightContribution(
    vec4 positionRadius, vec3 radiance,
    vec3 worldPos, vec3 n, vec3 v,
    vec3 albedo, float metallic, float roughness)
{
    vec3 toLight = positionRadius.xyz - worldPos;
    float distance = length(toLight);

    if (distance >= positionRadius.w)
    {
        return vec3(0.0);
    }

    vec3 l = toLight / max(distance, 1e-4);

    vec3 diffuse;
    vec3 specular;
    CookTorrance(n, v, l, albedo, metallic, roughness, diffuse, specular);

    return (diffuse + specular) * radiance
        * PointAttenuation(distance, positionRadius.w) * max(dot(n, l), 0.0);
}

/// textured.frag の ShadowFactor と同じ式。**違いは光の座標を引数でもらうこと**。
///
/// フォワードは頂点シェーダが vLightSpacePos を作って補間で運んでいた。
/// ここには頂点が無いので、戻した世界の位置に uLightSpaceMatrix をその場で掛ける。
/// 画素の数だけ行列を掛けることになる(Day 33 のコメントで「避ける」と書いた形)が、
/// ディファードでは**避けようがない**——頂点を持たないことの代償の1つ。
float ShadowFactor(vec4 lightSpacePos, vec3 normal, vec3 lightDir)
{
    if (uShadowEnabled == 0)
    {
        return 1.0;
    }

    vec3 proj = (lightSpacePos.xyz / lightSpacePos.w * 0.5) + 0.5;

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

void main()
{
    // **texelFetch で読む**。G-Buffer は画面と同じ大きさなので、画素の番号でそのまま引ける。
    // texture() で読むとフィルタが通り、物の縁で**手前と奥の法線が混ざる**——
    // どちらの面でもない法線に光を当てることになり、輪郭に1画素の縁取りが出る。
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    vec4 normalDepth = texelFetch(uGNormalDepth, pixel, 0);

    // **距離 0 は「何も描かれていない」**(Day 37 と同じ約束)。
    // 捨てるので、先に描いてある空(DrawSkybox)がそのまま残る。
    if (normalDepth.w <= 0.0)
    {
        discard;
    }

    // --- 光の重なり(F6)---
    //
    // 球が塗るたびに一定の量を足す。太陽のパスは黒で下地を作る。
    // **塗った回数がそのまま明るさになる**ので、どこでどれだけ払っているかが見える。
    if (uOverdrawView == 1)
    {
        FragColor = uPointLightPass == 1
            ? vec4(0.08, 0.025, 0.004, 1.0)
            : vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    // --- G-Buffer を開く ---
    vec2 uv = gl_FragCoord.xy / uScreenSize;
    vec3 viewPosition = ViewPosition(uv, normalDepth.w);
    vec3 worldPos = (uInverseView * vec4(viewPosition, 1.0)).xyz;

    // 法線は向きなので mat3(平行移動を落とす)。ビュー行列は回転だけなので逆転置は要らない。
    vec3 normal = normalize(mat3(uInverseView) * normalDepth.xyz);

    // **平方根で詰めてあるので2乗して戻す**(textured.frag の書き込み側を参照)。
    vec3 encoded = texelFetch(uGAlbedo, pixel, 0).rgb;
    vec3 albedo = encoded * encoded;

    vec3 orm = texelFetch(uGMaterial, pixel, 0).rgb;
    float occlusion = orm.r;
    float roughness = orm.g;
    float metallic = orm.b;

    vec3 viewDir = normalize(uCameraPosition - worldPos);

    // --- 点光源のパス ---
    if (uPointLightPass == 1)
    {
        // **ランバートの枝には点光源を入れていない**(textured.frag と同じ扱い)。
        // 「PBR」の F2 は Day 35 以前との見比べ用の窓なので、点光源まで持ち込まない。
        if (uPbrEnabled == 0)
        {
            discard;
        }

        vec3 light = PointLightContribution(
            uPointPosition, uPointColor, worldPos, normal, viewDir, albedo, metallic, roughness);

        FragColor = vec4(light, 1.0);
        return;
    }

    // --- 太陽・環境光・発光(ここから textured.frag の後半と同じ並び)---
    vec3 emissive = texelFetch(uGEmissive, pixel, 0).rgb;

    // SSAO は**このパスの前に**出来上がっている(G-Buffer から計算したもの)。
    float ssao = uSsaoEnabled == 1 ? texture(uAoMap, uv).r : 1.0;
    float ambientOcclusion = occlusion * ssao;
    float directOcclusion = uSsaoOnDirect == 1 ? ssao : 1.0;

    vec3 toLight = -uLightDirection;
    float shadow = ShadowFactor(uLightSpaceMatrix * vec4(worldPos, 1.0), normal, toLight);

    if (uPbrEnabled == 0)
    {
        float lambert = max(dot(normal, toLight), 0.0);
        vec3 lighting =
            (uLightColor * lambert * shadow * directOcclusion)
            + (uAmbientColor * ambientOcclusion);
        FragColor = vec4((albedo * lighting) + emissive, 1.0);
        return;
    }

    vec3 diffuse;
    vec3 specular;
    CookTorrance(normal, viewDir, toLight, albedo, metallic, roughness, diffuse, specular);

    float nDotL = max(dot(normal, toLight), 0.0);
    vec3 direct = (diffuse + specular) * uLightColor * nDotL * shadow * directOcclusion;

    vec3 f0 = mix(vec3(uDielectricF0), albedo, metallic);
    float nDotV = max(dot(normal, viewDir), 0.0);

    vec3 ambientDiffuse;
    vec3 ambientSpecular;

    if (uIblEnabled == 1)
    {
        vec3 kS = FresnelSchlickRoughness(nDotV, f0, roughness);
        vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

        vec3 irradiance = texture(uIrradianceMap, normal).rgb * uIblIntensity;
        ambientDiffuse = kD * irradiance * albedo * ambientOcclusion;

        vec3 reflection = reflect(-viewDir, normal);
        float lod = uIblPrefilter == 1 ? (roughness * uPrefilterMaxLod) : 0.0;
        vec3 prefiltered = textureLod(uPrefilterMap, reflection, lod).rgb * uIblIntensity;

        vec2 ab = texture(uBrdfLut, vec2(nDotV, roughness)).rg;
        ambientSpecular = prefiltered * ((kS * ab.x) + ab.y) * ambientOcclusion;
    }
    else
    {
        ambientDiffuse = uAmbientColor * albedo * ambientOcclusion * (1.0 - metallic);

        vec3 ambientF = FresnelSchlick(nDotV, f0);
        ambientSpecular = uAmbientSpecular == 1
            ? uAmbientColor * ambientF * ambientOcclusion * (1.0 - roughness)
            : vec3(0.0);
    }

    FragColor = vec4(direct + ambientDiffuse + ambientSpecular + emissive, 1.0);
}
