#version 330 core

in vec2 vTexCoord;
in vec4 vColor;
in vec3 vNormal;

out vec4 FragColor;

// テクスチャユニットの割り当ては Material.Apply と two-way の約束。
//   0 ベースカラー / 1 メタリック・ラフネス / 2 法線 / 3 AO / 4 発光
uniform sampler2D uTexture;
uniform sampler2D uMetallicRoughnessMap;
uniform sampler2D uNormalMap;
uniform sampler2D uOcclusionMap;
uniform sampler2D uEmissiveMap;

// **そのマップを持っているか**。glTF のマテリアルは
// 「baseColor だけ」から「5枚全部」まで幅があるので、
// シェーダを枚数ごとに分けるのではなく、分岐で吸収する。
//
// 本番のエンジンは分岐ではなく**シェーダバリアント**(#define を変えて別々にコンパイル)
// を使う。分岐は GPU では両方の枝を実行することがあるうえ、
// 使わないテクスチャの読み込みが残るため。今日は本数を増やさないほうを選んでいる。
uniform int uHasMetallicRoughnessMap;
uniform int uHasNormalMap;
uniform int uHasOcclusionMap;
uniform int uHasEmissiveMap;

// マテリアルの色味
uniform vec4 uTint;

// --- Day 32: glTF の metallic-roughness ---
uniform vec4 uBaseColorFactor;
uniform float uMetallicFactor;
uniform float uRoughnessFactor;
uniform vec3 uEmissiveFactor;

// --- Day 32: 平行光源 ---
//
// **Day 9 でソフトウェアラスタライザに書いたランバート反射**が、GPU に戻ってくる。
// Day 14 で GPU へ移ったとき、陰影は一度落としていた。
// glTF のモデルは陰影が付かないと形が読めないので、ここで最小限のものを戻す。
//
// 平行光源(太陽)にするのは、位置ではなく**向きだけ**を持てばよく、
// 距離による減衰も要らないため。点光源が要るのは Day 39。
uniform vec3 uLightDirection;
uniform vec3 uLightColor;
uniform vec3 uAmbientColor;

/// 何を画面に出すか。
/// 0=通常 1=ベースカラー 2=法線(頂点) 3=メタリック 4=ラフネス 5=AO 6=発光 7=法線マップ
uniform int uDebugChannel;

/// sRGB からリニアへ(Day 31)。
vec3 SrgbToLinear(vec3 color)
{
    return pow(color, vec3(2.2));
}

void main()
{
    vec4 base = texture(uTexture, vTexCoord) * uBaseColorFactor;

    // 頂点色とマテリアル色。**uTint だけは変換しない**(Day 31 の要点3)。
    base *= vec4(SrgbToLinear(vColor.rgb), vColor.a) * uTint;

    // 補間で崩れた長さを戻す(textured.vert のコメント)。
    vec3 normal = normalize(vNormal);

    // メタリック/ラフネスは **B が金属度、G が粗さ**。glTF がそう決めている。
    float metallic = uMetallicFactor;
    float roughness = uRoughnessFactor;
    if (uHasMetallicRoughnessMap == 1)
    {
        vec3 mr = texture(uMetallicRoughnessMap, vTexCoord).rgb;
        roughness *= mr.g;
        metallic *= mr.b;
    }

    float occlusion = uHasOcclusionMap == 1 ? texture(uOcclusionMap, vTexCoord).r : 1.0;

    vec3 emissive = uEmissiveFactor;
    if (uHasEmissiveMap == 1)
    {
        emissive *= texture(uEmissiveMap, vTexCoord).rgb;
    }

    // **中身を目で確かめるための窓**(Shift+9)。
    // 読み込んだデータが正しいかどうかは、絵として合成してしまうと分からない。
    // 「法線が裏返っている」「ラフネスとメタリックが入れ替わっている」は、
    // 完成した絵では**それっぽく見えてしまう**のがいちばん厄介なところ。
    if (uDebugChannel == 1) { FragColor = vec4(base.rgb, 1.0); return; }

    // 法線は -1〜1 なので、0〜1 に写して色として出す。真上向きが薄緑になる。
    if (uDebugChannel == 2) { FragColor = vec4((normal * 0.5) + 0.5, 1.0); return; }
    if (uDebugChannel == 3) { FragColor = vec4(vec3(metallic), 1.0); return; }
    if (uDebugChannel == 4) { FragColor = vec4(vec3(roughness), 1.0); return; }
    if (uDebugChannel == 5) { FragColor = vec4(vec3(occlusion), 1.0); return; }
    if (uDebugChannel == 6) { FragColor = vec4(emissive, 1.0); return; }

    // **法線マップの生の中身**。接空間の法線が RGB に詰まっているので、
    // 平らなところは (0.5, 0.5, 1.0) = 薄い青紫になる。
    // 「一面が薄紫で、傷や凹凸のところだけ色がずれている」なら正しく読めている。
    //
    // 今日はこれを**見るだけ**で、陰影には使わない。
    // 使うには接空間の基底(接線と従接線)が要り、それは Day 34 の仕事。
    if (uDebugChannel == 7)
    {
        vec3 tangentNormal = uHasNormalMap == 1
            ? texture(uNormalMap, vTexCoord).rgb
            : vec3(0.5, 0.5, 1.0);
        FragColor = vec4(tangentNormal, 1.0);
        return;
    }

    // --- ランバート反射(Day 9 の要点2)---
    //
    // 面が光に正対していれば明るく、傾くほど暗い。
    // 内積が「傾き具合」そのものになるのがこの式の気持ちよさで、
    // 裏を向いた面は負になるので 0 で止める。
    //
    // **今日は金属度も粗さも使わない**。使えるようにするのが Day 35 で、
    // ここが Cook-Torrance BRDF に置き換わる。
    float lambert = max(dot(normal, -uLightDirection), 0.0);

    // 環境光を AO で削る。**直接光には AO をかけない**——
    // AO は「まわりから回り込んでくる光がどれだけ遮られるか」なので、
    // 太陽から直接来る光とは無関係。ここを間違えると影がべったり黒くなる。
    vec3 lighting = (uLightColor * lambert) + (uAmbientColor * occlusion);

    FragColor = vec4((base.rgb * lighting) + emissive, base.a);
}
