#version 330 core

in vec2 vTexCoord;
in vec4 vColor;
in vec3 vNormal;
in vec3 vWorldPos;
in vec4 vLightSpacePos;

// --- Day 34: 接空間 ---
in vec3 vTangent;
in vec3 vBitangent;
in vec3 vTangentViewDir;

out vec4 FragColor;

// テクスチャユニットの割り当ては Material.Apply と two-way の約束。
//   0 ベースカラー / 1 メタリック・ラフネス / 2 法線 / 3 AO / 4 発光
//   5 シャドウマップ(Day 33。**マテリアルではなく ShadowMap.Apply が刺す**)
//   6 高さ(Day 34)
uniform sampler2D uTexture;
uniform sampler2D uMetallicRoughnessMap;
uniform sampler2D uNormalMap;
uniform sampler2D uOcclusionMap;
uniform sampler2D uEmissiveMap;
uniform sampler2D uHeightMap;

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
uniform int uHasHeightMap;

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
/// 9=接線 T / 10=従接線 B / 11=最終法線 N / 12=高さ(Day 34)
uniform int uDebugChannel;

// --- Day 34: 法線マップと視差マッピング ---

/// 法線マップを使うか。OFF にすると頂点法線だけになる(Alt+1)。
uniform int uNormalMapping;

/// 法線の強さ。読んだ法線の **xy にだけ**掛ける(Material.NormalScale)。
uniform float uNormalScale;

/// 法線マップの緑を反転するか。**DirectX 形式の素材を貼ったときの見え方**(Alt+8)。
///
/// 法線マップには2つの流儀がある。OpenGL 系は「緑が大きい = V の増える向きへ傾く」、
/// DirectX 系はその逆。**画像としては見分けが付かない**ので、
/// 貼ってから「凹凸が逆」と気づくのが定番の踏み方になる。
uniform int uFlipGreen;

/// 視差の方式。0=なし 1=単純視差 2=急峻視差 3=視差遮蔽(POM)
uniform int uParallaxMode;

/// 視差の深さ(Material.ParallaxScale)。
uniform float uParallaxScale;

/// レイマーチの刻み数(最小・最大)。真正面は粗く、斜めほど細かく刻む。
uniform int uParallaxMinSteps;
uniform int uParallaxMaxSteps;

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

/// **視線に合わせて UV をずらす**(Day 34)。これが視差マッピングの全部。
///
/// 考え方は単純で、**平らな板を斜めから見たとき、
/// 本当に凹凸があったなら見えていたはずの点**を探しに行く。
///
/// ```
///        視線 V
///         \
///   ───────\──────────  ← 実際のポリゴン(平ら)
///           \
///            *          ← 本当はここが見えるはず(高さマップの谷)
/// ```
///
/// 板は平らなままなので、**形は1ミリも変わらない**。
/// 変わるのは「どのテクセルを読むか」だけ。だから輪郭は平らなままで、
/// 面の中だけが立体に見える。この嘘がばれるのが板の縁で、
/// 深さを上げるほど縁の破綻が目立つようになる。
vec2 ParallaxUv(vec2 uv, vec3 viewDir)
{
    if (uParallaxMode == 0 || uHasHeightMap == 0)
    {
        return uv;
    }

    // 面の裏側から見ている(法線と逆)ときは何もしない。
    // z が 0 に近いと下の除算が発散するので、そこも止める。
    if (viewDir.z <= 0.001)
    {
        return uv;
    }

    // --- 方式1: 単純視差(Parallax Mapping)---
    //
    // **1回読むだけ**。その場の高さを見て、視線の傾きぶんだけ UV をずらす。
    //
    //   ずらす量 = (視線の xy / 視線の z) * 高さ * 深さ
    //
    // 「視線の xy / z」が傾きで、真正面から見ていれば 0(ずれない)、
    // 寝かせるほど大きくなる。
    //
    // 速いが、**深い溝では大きく外す**。1回しか読んでいないので、
    // ずらした先がまた谷だったことに気づけない。
    if (uParallaxMode == 1)
    {
        float height = texture(uHeightMap, uv).r;

        // 高さは「1 が表面」なので、へこみ量は 1 - height。
        float depth = (1.0 - height) * uParallaxScale;
        return uv - ((viewDir.xy / viewDir.z) * depth);
    }

    // --- 方式2・3: レイマーチ ---
    //
    // 視線を接空間で少しずつ進めながら、**高さマップの下へ潜った瞬間**を探す。
    // 「レイを飛ばして地形とぶつける」を、UV 平面の上でやっているだけ。
    //
    // **刻み数を視線の角度で変える**のが定番の節約。
    // 真正面から見ている面はずれ自体が小さいので粗くてよく、
    // 寝ている面ほど長い距離を進むので細かく刻む必要がある。
    float steps = mix(float(uParallaxMaxSteps), float(uParallaxMinSteps), abs(viewDir.z));
    float layerDepth = 1.0 / steps;

    // 1ステップで進む UV の量。深さと傾きから決まる。
    vec2 step = (viewDir.xy / viewDir.z) * uParallaxScale / steps;

    vec2 currentUv = uv;
    float currentLayer = 0.0;
    float currentDepth = 1.0 - texture(uHeightMap, currentUv).r;

    // **レイのほうが深くなるまで進む**。GLSL のループなので、
    // 上限は steps で必ず止まる(無限ループにはならない)。
    while (currentLayer < currentDepth && currentLayer < 1.0)
    {
        currentUv -= step;
        currentDepth = 1.0 - texture(uHeightMap, currentUv).r;
        currentLayer += layerDepth;
    }

    // --- 方式2: 急峻視差(Steep Parallax Mapping)---
    //
    // 潜った時点の UV をそのまま使う。刻みが粗いと**段差が縞になって見える**——
    // レイが「層」の境目でしか止まれないため。
    if (uParallaxMode == 2)
    {
        return currentUv;
    }

    // --- 方式3: 視差遮蔽(Parallax Occlusion Mapping)---
    //
    // **最後の1歩を線形補間で戻す**。潜る直前と直後の2点があるので、
    // 「高さの差が 0 になる場所」を直線で近似して求める。
    //
    // 足すのは3行だけなのに、方式2の縞がきれいに消える。
    // レイマーチ系の技法でこの「最後だけ補間」はほぼ定石になっていて、
    // Day 37 の SSAO でも同じ発想が出てくる。
    vec2 previousUv = currentUv + step;

    float afterDepth = currentDepth - currentLayer;
    float beforeDepth = (1.0 - texture(uHeightMap, previousUv).r) - currentLayer + layerDepth;

    float weight = afterDepth / (afterDepth - beforeDepth);
    return mix(currentUv, previousUv, weight);
}

/// **法線マップを読んで、世界空間の法線に変える**(Day 34)。
///
/// 中身は3行しかない。難しいのは3行に至るまでの約束のほうで、
///   - テクセルの 0〜1 は -1〜1 を写したもの(だから `* 2 - 1`)
///   - その値は**接空間**での向き(だから TBN を掛ける)
///   - TBN の B は `cross(N, T) * w`(だから接線が w を持っている)
/// の3つが揃って初めて意味を持つ。
vec3 PerturbNormal(vec3 vertexNormal, vec2 uv)
{
    if (uNormalMapping == 0 || uHasNormalMap == 0)
    {
        return vertexNormal;
    }

    vec3 sampled = (texture(uNormalMap, uv).rgb * 2.0) - 1.0;

    // 緑を反転する流儀(DirectX 形式)への対応。uFlipGreen のコメント参照。
    if (uFlipGreen == 1)
    {
        sampled.y = -sampled.y;
    }

    // **xy にだけ強さを掛ける**。z も一緒に掛けると向きが変わらない
    // (全体を定数倍しても正規化で戻るだけ)。
    sampled.xy *= uNormalScale;

    // 接空間 → 世界空間。TBN の3本を係数として足し合わせるのが、行列を掛けることの中身。
    vec3 t = normalize(vTangent);
    vec3 b = normalize(vBitangent);
    vec3 n = normalize(vertexNormal);

    return normalize((t * sampled.x) + (b * sampled.y) + (n * sampled.z));
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
    // **UV を先に決める**(Day 34)。視差マッピングは
    // 「このピクセルはテクスチャのどこを見ているか」を書き換える技法なので、
    // ベースカラーも法線もラフネスも、**ずらしたあとの UV で読む**。
    // 1枚でも元の UV のまま読むと、その成分だけ凹凸から浮いてしまう。
    vec2 uv = ParallaxUv(vTexCoord, normalize(vTangentViewDir));

    vec4 base = texture(uTexture, uv) * uBaseColorFactor;

    // 頂点色とマテリアル色。**uTint だけは変換しない**(Day 31 の要点3)。
    base *= vec4(SrgbToLinear(vColor.rgb), vColor.a) * uTint;

    // 補間で崩れた長さを戻す(textured.vert のコメント)。
    vec3 vertexNormal = normalize(vNormal);

    // **今日の主役**。法線マップを読んで、面の向きを画素ごとに差し替える。
    // ポリゴンは平らなままなのに陰影が細かくなるのは、
    // ライティングが N しか見ていないから——**N を差し替えれば形が変わったことになる**。
    vec3 normal = PerturbNormal(vertexNormal, uv);

    // メタリック/ラフネスは **B が金属度、G が粗さ**。glTF がそう決めている。
    float metallic = uMetallicFactor;
    float roughness = uRoughnessFactor;
    if (uHasMetallicRoughnessMap == 1)
    {
        vec3 mr = texture(uMetallicRoughnessMap, uv).rgb;
        roughness *= mr.g;
        metallic *= mr.b;
    }

    float occlusion = uHasOcclusionMap == 1 ? texture(uOcclusionMap, uv).r : 1.0;

    vec3 emissive = uEmissiveFactor;
    if (uHasEmissiveMap == 1)
    {
        emissive *= texture(uEmissiveMap, uv).rgb;
    }

    // **中身を目で確かめるための窓**(Shift+9)。
    // 読み込んだデータが正しいかどうかは、絵として合成してしまうと分からない。
    // 「法線が裏返っている」「ラフネスとメタリックが入れ替わっている」は、
    // 完成した絵では**それっぽく見えてしまう**のがいちばん厄介なところ。
    if (uDebugChannel == 1) { FragColor = vec4(base.rgb, 1.0); return; }

    // 法線は -1〜1 なので、0〜1 に写して色として出す。真上向きが薄緑になる。
    // **こちらは頂点法線**(マップを当てる前)。当てたあとは成分 11。
    if (uDebugChannel == 2) { FragColor = vec4((vertexNormal * 0.5) + 0.5, 1.0); return; }
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
            ? texture(uNormalMap, uv).rgb
            : vec3(0.5, 0.5, 1.0);
        FragColor = vec4(tangentNormal, 1.0);
        return;
    }

    // --- Day 34: 接空間を目で見る ---
    //
    // **T・B・N の3本を1本ずつ色で出す**。接空間は頭の中だけで組み立てると
    // すぐ迷子になるので、実際にどちらを向いているか見られるようにしておく。
    //
    // 見どころは「**面の上で連続的に変わっているか**」。
    // 接線が三角形ごとにバラバラの色に見えたら、頂点で共有できていない
    // (足し込みか正規化を間違えている)。
    if (uDebugChannel == 9) { FragColor = vec4((normalize(vTangent) * 0.5) + 0.5, 1.0); return; }
    if (uDebugChannel == 10) { FragColor = vec4((normalize(vBitangent) * 0.5) + 0.5, 1.0); return; }

    // マップを当てたあとの最終的な法線。成分 2(頂点法線)と見比べると、
    // **法線マップがどれだけ向きを変えているか**が分かる。
    if (uDebugChannel == 11) { FragColor = vec4((normal * 0.5) + 0.5, 1.0); return; }

    // 高さマップ。白が表面、黒が谷底。視差の効きはこの明暗の落差で決まる。
    if (uDebugChannel == 12)
    {
        float height = uHasHeightMap == 1 ? texture(uHeightMap, uv).r : 1.0;
        FragColor = vec4(vec3(height), 1.0);
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
