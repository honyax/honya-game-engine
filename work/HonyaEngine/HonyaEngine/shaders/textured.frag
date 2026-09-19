#version 330 core

in vec2 vTexCoord;
in vec4 vColor;
in vec3 vNormal;
in vec3 vWorldPos;
in vec4 vLightSpacePos;

out vec4 FragColor;

// テクスチャユニットの割り当ては Material.Apply と two-way の約束。
//   0 ベースカラー / 1 メタリック・ラフネス / 2 法線 / 3 AO / 4 発光
//   5 シャドウマップ(Day 33。**マテリアルではなく ShadowMap.Apply が刺す**)
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
/// 8=影の係数(Day 33)
uniform int uDebugChannel;

// --- Day 33: シャドウマッピング ---
//
// 5 番のユニットに刺さっている「光から見た深度」。
// 色ではなく**距離**が入っているので、sRGB 変換は絶対にかけない。
uniform sampler2D uShadowMap;

uniform int uShadowEnabled;

/// PCF の半径(テクセル)。0 なら1タップ、1 なら 3x3、2 なら 5x5。
uniform int uPcfRadius;

/// 深度バイアス。**比較の前に自分の深度を手前へずらす量**。
uniform float uShadowBias;

/// 傾きに比例して足すバイアス。0 なら傾きを見ない。
uniform float uShadowSlopeBias;

/// シャドウマップの1テクセルぶんの UV。PCF がずらす幅になる。
uniform vec2 uShadowTexelSize;

/// sRGB からリニアへ(Day 31)。
vec3 SrgbToLinear(vec3 color)
{
    return pow(color, vec3(2.2));
}

/// **その点が光から見えているか**。1.0 = 完全に当たっている、0.0 = 完全な影。
///
/// やることは3段。
///   1. 光の座標系の位置を 0〜1 のテクスチャ座標へ写す
///   2. シャドウマップに焼かれている深度と、自分の深度を比べる
///   3. 1テクセルだけでなく周りも読んで平均する(PCF)
float ShadowFactor(vec3 normal, vec3 lightDir)
{
    if (uShadowEnabled == 0)
    {
        return 1.0;
    }

    // **透視除算を自分でやる**。gl_Position と違い、こちらは GPU が割ってくれない。
    // 平行光源は正射影なので w は必ず 1.0 だが、書いておくと
    // 点光源の影(透視投影を使う)へそのまま持っていける。
    vec3 proj = vLightSpacePos.xyz / vLightSpacePos.w;

    // NDC(-1〜1)からテクスチャ座標(0〜1)へ。**深度も同じ式で写す**——
    // OpenGL の NDC は z も -1〜1 なのに対し、深度バッファは 0〜1 だから。
    // ここを xy だけ変換して z を忘れると、**全部が影になる**か**全部が影でなくなる**。
    proj = (proj * 0.5) + 0.5;

    // 光の遠クリップより奥は、そもそも焼かれていない。影なしにする。
    // xy が範囲外のときは ClampToBorder + 白 が同じことをしてくれる(Texture.CreateDepthTarget)。
    if (proj.z > 1.0)
    {
        return 1.0;
    }

    // **バイアス**。深度の比較は等号ぎりぎりなので、そのままだと
    // 「自分が自分を遮っている」と判定される点が半分くらい出る——これがシャドウアクネ。
    //
    // 原因は解像度の有限さ。シャドウマップの1テクセルは、
    // 光から見て**ある広さの領域**の深度を1つの値で代表している。
    // 斜めの面ではその領域の中で本当の深度が連続的に変わるので、
    // 代表値より手前の点と奥の点が半々に生まれ、縞模様になる。
    //
    // だから必要なバイアスは**面の傾き**に比例する。正対していればほぼ 0、
    // 光と平行に近い面では大きく要る。dot(N, L) がそのまま傾きの指標になる。
    //
    // 入れすぎると影が本体から離れる(ピーターパン)。
    // 「アクネが消えるぎりぎりまで小さく」が正解で、Ctrl+4 で境目を見られる。
    float ndotl = max(dot(normal, lightDir), 0.0);
    float bias = uShadowBias + (uShadowSlopeBias * (1.0 - ndotl));

    // --- PCF(Percentage-Closer Filtering)---
    //
    // **比較してから平均する**。深度を平均してから比較するのではない(順番が肝)。
    // 深度の平均には意味が無い(手前 0.2 と奥 0.9 の平均 0.55 はどこにも無い面)が、
    // 「影か否か」の 0/1 を平均した値は「何割が影か」という意味を持つ。
    //
    // 3x3 なら 0, 1/9, 2/9, ... 1 の 10 段。境目が 10 段の階調になるので、
    // ギザギザが「にじみ」に置き換わる。**影が柔らかくなるのではなく、
    // ギザギザが目立たなくなる**だけ、というのが正確なところ——
    // 本物の半影(光源の大きさで決まるボケ)は Day 39 以降の話になる。
    float lit = 0.0;
    int taps = 0;

    for (int x = -uPcfRadius; x <= uPcfRadius; x++)
    {
        for (int y = -uPcfRadius; y <= uPcfRadius; y++)
        {
            float closest = texture(uShadowMap, proj.xy + (vec2(x, y) * uShadowTexelSize)).r;

            // 焼かれている深度より自分が奥なら、間に何かがある = 影。
            lit += (proj.z - bias) > closest ? 0.0 : 1.0;
            taps++;
        }
    }

    return lit / float(taps);
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

    // 光の向きは「進む向き」なので、面から光へ向かうベクトルは符号を反転したもの。
    vec3 toLight = -uLightDirection;
    float shadow = ShadowFactor(normal, toLight);

    // **影の係数だけを見る**(Day 33)。白 = 当たっている、黒 = 影。
    // アクネの縞も、PCF の階調も、バイアスの入れすぎで影が痩せるのも、
    // 色を混ぜない状態のほうが圧倒的に読み取りやすい。
    if (uDebugChannel == 8) { FragColor = vec4(vec3(shadow), 1.0); return; }

    // --- ランバート反射(Day 9 の要点2)---
    //
    // 面が光に正対していれば明るく、傾くほど暗い。
    // 内積が「傾き具合」そのものになるのがこの式の気持ちよさで、
    // 裏を向いた面は負になるので 0 で止める。
    //
    // **今日は金属度も粗さも使わない**。使えるようにするのが Day 35 で、
    // ここが Cook-Torrance BRDF に置き換わる。
    float lambert = max(dot(normal, toLight), 0.0);

    // 環境光を AO で削る。**直接光には AO をかけない**——
    // AO は「まわりから回り込んでくる光がどれだけ遮られるか」なので、
    // 太陽から直接来る光とは無関係。ここを間違えると影がべったり黒くなる。
    //
    // **影は直接光にだけ掛ける**(Day 33)。理由は AO とちょうど裏返しで、
    // 影とは「太陽が遮られている」ことだから。
    // 環境光にまで掛けると影が真っ黒になり、夜のような絵になる——
    // 現実の影が黒くないのは、空や周囲からの光が回り込んでいるおかげ。
    // ここで残している uAmbientColor が、その回り込みのいちばん粗い近似になっている
    // (ちゃんと計算するのが Day 36 の IBL と Day 37 の SSAO)。
    vec3 lighting = (uLightColor * lambert * shadow) + (uAmbientColor * occlusion);

    FragColor = vec4((base.rgb * lighting) + emissive, base.a);
}
