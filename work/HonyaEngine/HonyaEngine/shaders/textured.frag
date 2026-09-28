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

// 骨の重みを混ぜた色(Day 41)。成分 22 でそのまま出す。
in vec3 vSkinTint;

out vec4 FragColor;

// テクスチャユニットの割り当ては Material.Apply と two-way の約束。
//   0 ベースカラー / 1 メタリック・ラフネス / 2 法線 / 3 AO / 4 発光
//   5 シャドウマップ(Day 33。**マテリアルではなく ShadowMap.Apply が刺す**)
//   6 高さ(Day 34)
//   7〜9 IBL(Day 36。EnvironmentMap.Apply が刺す)
//   10 スクリーンスペース環境遮蔽(Day 37。Ssao.Apply が刺す)
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
/// 13=拡散のみ / 14=鏡面のみ / 15=フレネル F / 16=法線分布 D / 17=幾何減衰 G(Day 35)
/// 18=放射照度 / 19=事前フィルタ(映り込み)/ 20=BRDF の表(Day 36)
/// 21=スクリーンスペース環境遮蔽(Day 37)
uniform int uDebugChannel;

// --- Day 37: スクリーンスペース環境遮蔽(SSAO)---

/// 画面に貼られた遮蔽率。**1成分(R)しか入っていない**。
///
/// マテリアルの AO(3番)との違いは<b>誰が作ったか</b>だけで、
/// 意味はまったく同じ「環境光がどれだけ届かないか」。
///   - マテリアルの AO … モデルを作った人が焼いた。**細かいが、その物の中だけ**
///   - こちらの AO     … 実行時に画面から作る。**粗いが、物どうしの関係が入る**
/// 立方体を床に置いたときの接地の暗がりは、後者にしか作れない。
uniform sampler2D uAoMap;

uniform int uSsaoEnabled;

/// 直接光にも掛けるか(Ctrl+F11)。**通常は 0**。1 は間違いを見るための窓。
uniform int uSsaoOnDirect;

/// 画面(フレームバッファ)の大きさ。gl_FragCoord を UV に直すのに使う。
uniform vec2 uScreenSize;

// --- Day 36: イメージベースドライティング ---

/// IBL を使うか(Ctrl+Alt+1)。OFF で Day 35 の「環境光は定数」に戻る。
uniform int uIblEnabled;

/// 環境光の強さ(Ctrl+Alt+3)。
uniform float uIblIntensity;

/// **拡散ぶん**。法線を渡すと、その向きの面に入ってくる光の合計が返る。
/// 1/π は焼くときに済ませてある(irradiance.frag)ので、ここでは albedo を掛けるだけ。
uniform samplerCube uIrradianceMap;

/// **鏡面ぶんの環境側**。ミップの段が粗さに対応している。
uniform samplerCube uPrefilterMap;

/// **鏡面ぶんの材質側**。横が N・V、縦が粗さ。R が F0 に掛ける係数、G が足す値。
uniform sampler2D uBrdfLut;

/// 事前フィルタのいちばん粗い段の番号。粗さ 0〜1 をこの範囲へ写す。
uniform float uPrefilterMaxLod;

/// 事前フィルタを使うか(Ctrl+Alt+8)。0 なら**粗さを無視して原寸を引く**。
/// ぼかしていない環境を鏡面に使うとどうなるか——を見るためのスイッチ。
uniform int uIblPrefilter;

// --- Day 35: 物理ベースレンダリング(Cook-Torrance)---

/// カメラの位置。**視線ベクトル V が要る**のが Day 34 との大きな違い。
///
/// ランバート反射は N と L しか見ないので、どこから見ても同じ明るさだった。
/// 鏡面反射は「反射した光がこちらへ来るか」を問うので、視線が入る。
/// vert 側にも同じ uniform があるが(視差マッピング用)、
/// **同じ名前の uniform を両方のステージに書いてよい**——リンク時に1つにまとめられる。
uniform vec3 uCameraPosition;

/// Cook-Torrance を使うか。OFF にすると Day 34 のランバート反射に戻る。
uniform int uPbrEnabled;

/// 非金属(誘電体)の F0。既定は 0.04。
uniform float uDielectricF0;

/// α の作り方。1 なら α = roughness²(glTF の流儀)、0 なら α = roughness。
uniform int uPerceptualRoughness;

/// 環境光にも粗い鏡面を足すか。**Day 36(IBL)までのつなぎ**。
///
/// 平行光源1つだけだと、金属は**ハイライト以外が真っ黒**になる。
/// 拡散反射をしない材質なので、当然といえば当然の結果——
/// 現実の金属が黒く見えないのは、まわりの景色を映しているから。
/// それを本当に計算するのが Day 36 で、ここでは
/// 「環境光を F0 の色で薄く足す」だけの粗い代用を置いている。
uniform int uAmbientSpecular;

/// 金属度・粗さの上書き。**負なら上書きしない**(マテリアルの値を使う)。
/// 材質の効きを1つずつ確かめるための窓で、絵作りの機能ではない。
uniform float uMetallicOverride;
uniform float uRoughnessOverride;

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

/// **画面に貼られた遮蔽率を引く**(Day 37)。
///
/// UV が要らない。要るのは「この画素が画面のどこか」だけで、
/// それは <c>gl_FragCoord.xy</c> がそのまま持っている(画素の中心なので +0.5 済み)。
///
/// <b>スクリーンスペースの技法は全部この形になる</b>——
/// 世界のどこにあるかではなく、画面のどこに写っているかで値を引く。
/// だから同じモデルでも、カメラを動かせば違う値が返る。
///
/// AO バッファが半解像度でも、この式は変わらない。
/// 0〜1 に直して引いているので、拡大はサンプラのバイリニアが面倒を見る。
float ScreenSpaceOcclusion()
{
    if (uSsaoEnabled == 0)
    {
        return 1.0;
    }

    return texture(uAoMap, gl_FragCoord.xy / uScreenSize).r;
}

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

const float PI = 3.14159265359;

/// 粗さの下限。**0 だと D が発散する**(幅 0 の無限に高いピークになる)。
const float MIN_ROUGHNESS = 0.045;

/// **法線分布関数 D**(GGX / Trowbridge-Reitz)。Pbr.DistributionGgx と同じ式。
///
/// 「微小な鏡のうち、法線がちょうど H を向いているものの密度」。
/// H が V へ光を返せる唯一の向きなので、鏡面の強さはここで決まる。
///
///   D = α² / (π * ((N・H)²(α² - 1) + 1)²)
///
/// 分母が2乗になっているのが GGX の裾の長さの正体で、
/// Phong や Beckmann(指数関数)より遠くまで減らずに残る。
float DistributionGgx(float nDotH, float alpha)
{
    float a2 = alpha * alpha;
    float d = (nDotH * nDotH * (a2 - 1.0)) + 1.0;
    return a2 / (PI * d * d);
}

/// **幾何減衰 G** の片側(Schlick-GGX)。
float GeometrySchlickGgx(float nDotX, float k)
{
    return nDotX / ((nDotX * (1.0 - k)) + k);
}

/// 入りと出の両方ぶん(Smith)。**k は知覚的な粗さから作る**(2乗する前)。
///
/// (r + 1)² / 8 は Disney の当てはめで、物理からは出てこない数字。
/// IBL 用は α / 2 で、**同じ G なのに係数が2通りある**(Day 36)。
float GeometrySmith(float nDotV, float nDotL, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return GeometrySchlickGgx(nDotV, k) * GeometrySchlickGgx(nDotL, k);
}

/// **フレネル F**(Schlick 近似)。
///
///   F = F0 + (1 - F0) * (1 - cosθ)^5
///
/// cosθ に入れるのは **V・H**(視線と微小な鏡の法線の角)。
/// N・V を入れると、粗い材質の縁だけが不自然に光る。
vec3 FresnelSchlick(float cosTheta, vec3 f0)
{
    return f0 + ((1.0 - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0));
}

/// **粗さを考えたフレネル**(Day 36)。環境光の鏡面の割合を出すのに使う。
///
/// 素の FresnelSchlick は浅い角度で必ず 1 に飛ぶ。直接光ならそれでよい(光線1本の話)が、
/// 環境光に使うと**粗い材質の縁が真っ白に光る**——
/// ざらざらの面では、浅く当たった光もあちこちへ散るので実際はそこまで返らない。
///
/// そこで上限を 1 - roughness で抑える。物理から導いた式ではなく、
/// Lagarde が「破綻しない」ように置いた当てはめ(Pbr.FresnelSchlickRoughness と同じ)。
vec3 FresnelSchlickRoughness(float cosTheta, vec3 f0, float roughness)
{
    vec3 ceiling = max(vec3(1.0 - roughness), f0);
    return f0 + ((ceiling - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0));
}

/// **Cook-Torrance を1回評価する**。拡散と鏡面を**分けて返す**。
///
/// 分けているのは、Shift+9 で片方ずつ見られるようにするため。
/// 「金属なのに拡散が残っている」「非金属なのに鏡面が色付き」のような
/// 取り違えは、合成した絵ではまず気づけない。
///
/// D・G・F も外へ出しているのは同じ理由。3つの項がどう効いているかは
/// **1枚ずつ画面に出すのがいちばん早い**。
void CookTorrance(
    vec3 n, vec3 v, vec3 l,
    vec3 albedo, float metallic, float roughness,
    out vec3 diffuse, out vec3 specular,
    out float outD, out float outG, out vec3 outF)
{
    diffuse = vec3(0.0);
    specular = vec3(0.0);
    outD = 0.0;
    outG = 0.0;

    // **F0 が metallic-roughness の心臓部**。
    //   非金属 → 0.04 の白っぽい鏡面。色は拡散が担う
    //   金属   → ベースカラーが鏡面の色になる。拡散はしない
    vec3 f0 = mix(vec3(uDielectricF0), albedo, metallic);
    outF = f0;

    float nDotL = dot(n, l);
    if (nDotL <= 0.0)
    {
        // 光が裏から当たっている面。鏡面も拡散も 0。
        // **早期に返す**のは、下の式が nDotL で割るため。
        return;
    }

    // 0 割りを避ける。真横から見ている画素(縁)で効く。
    float nDotV = max(dot(n, v), 1e-4);

    vec3 h = normalize(v + l);
    float nDotH = max(dot(n, h), 0.0);
    float vDotH = max(dot(v, h), 0.0);

    float r = max(roughness, MIN_ROUGHNESS);
    float alpha = uPerceptualRoughness == 1 ? (r * r) : r;

    float d = DistributionGgx(nDotH, alpha);
    float g = GeometrySmith(nDotV, nDotL, r);
    vec3 f = FresnelSchlick(vDotH, f0);

    outD = d;
    outG = g;
    outF = f;

    specular = (d * g * f) / (4.0 * nDotV * nDotL);

    // **反射しなかったぶんだけが中に入る**。これがエネルギー保存の要。
    // さらに金属は中へ入った光を吸収してしまうので (1 - metallic)。
    vec3 kd = (vec3(1.0) - f) * (1.0 - metallic);

    // ランバートの 1/π。**半球に均等にばらまくと積分が π になる**ので割る。
    // Day 34 まではこの π を省いていた(光の強さのほうで吸収していた)ので、
    // PBR に切り替えると**全体が π 分の 1 に暗くなる**——
    // uLightColor を上げて釣り合いを取り直しているのはそのため。
    diffuse = kd * albedo / PI;
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

    // **上書き**(Day 35)。負なら素通し。
    // モデルが持っている値を止めて、金属度と粗さだけを動かせるようにする窓。
    // 「DamagedHelmet がなぜあの見え方なのか」は、
    // 値を固定して1軸ずつ動かさないと分からない。
    if (uMetallicOverride >= 0.0)
    {
        metallic = uMetallicOverride;
    }

    if (uRoughnessOverride >= 0.0)
    {
        roughness = uRoughnessOverride;
    }

    float occlusion = uHasOcclusionMap == 1 ? texture(uOcclusionMap, uv).r : 1.0;

    // --- Day 37: 画面から作った遮蔽率を混ぜる ---
    //
    // **掛け算1回で終わる**のが今日の差分の要で、
    // 環境光に occlusion を掛ける形は Day 32 からずっと同じだった。
    // そこへ「モデルが持ってきた AO」と「画面から作った AO」を
    // 掛け合わせたものを流し込むだけで済む。
    //
    // 2つを掛けるのは厳密には正しくない(同じ遮蔽を二重に数えうる)が、
    // 実務ではこうする。**細かさの担当範囲が違う**ためで、
    // 焼いた AO は物の中の溝、SSAO は物どうしの隙間を受け持っている。
    float ssao = ScreenSpaceOcclusion();
    float ambientOcclusion = occlusion * ssao;

    // **AO を直接光に掛けるのは間違い**(Ctrl+F11 で実演)。
    //
    // AO は「まわりから回り込んでくる光がどれだけ遮られるか」なので、
    // 太陽から一直線に来る光とは関係が無い。遮っているものがあるなら、
    // それは影(Day 33)が担当する仕事。
    // 掛けてしまうと、日向の壁際まで black になり、**絵全体が薄汚れる**。
    float directOcclusion = uSsaoOnDirect == 1 ? ssao : 1.0;

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

    // **画面から作った遮蔽率だけ**(Day 37)。成分 5(焼いた AO)と見比べる窓。
    // 全画面で見たいときは Ctrl+F2 のほうが速い(こちらは物体の上にしか出ない)。
    if (uDebugChannel == 21) { FragColor = vec4(vec3(ssao), 1.0); return; }

    // **骨の重み**(Day 41)。関節ごとの色を重みで混ぜたもの。
    // 単色の帯が骨の受け持ち、色が混ざっている帯が「2本以上が奪い合っている」場所。
    // グラデーションの幅がそのまま**曲がったときの滑らかさ**になる——
    // 狭いと折り目が立ち、広いと関節がぐにゃりと伸びる。
    // スキンを持たないものは灰色(0.15)で塗られる。
    if (uDebugChannel == 22) { FragColor = vec4(vSkinTint, 1.0); return; }

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

    // --- Day 34 までのランバート反射(Day 9 の要点2)---
    //
    // 面が光に正対していれば明るく、傾くほど暗い。**金属度も粗さも使わない**。
    // Ctrl+Shift+1 でこちらに戻せるようにしてあるのは、
    // **同じシーンを並べて見比べる**のが PBR の値打ちを掴むいちばんの近道だから。
    //
    // **成分 13〜17(BRDF の中身)はこの枝では出ない**。
    // ランバートには D も G も F も無いので、見せるものが無い。
    if (uPbrEnabled == 0)
    {
        float lambert = max(dot(normal, toLight), 0.0);

        // 環境光を AO で削る。**直接光には AO をかけない**——
        // AO は「まわりから回り込んでくる光がどれだけ遮られるか」なので、
        // 太陽から直接来る光とは無関係。ここを間違えると影がべったり黒くなる。
        //
        // **影は直接光にだけ掛ける**(Day 33)。理由は AO とちょうど裏返しで、
        // 影とは「太陽が遮られている」ことだから。
        vec3 lighting =
            (uLightColor * lambert * shadow * directOcclusion)
            + (uAmbientColor * ambientOcclusion);
        FragColor = vec4((base.rgb * lighting) + emissive, base.a);
        return;
    }

    // --- 今日の主役: Cook-Torrance BRDF ---
    //
    // 出ていく明るさは、レンダリング方程式のいちばん素朴な形:
    //
    //   Lo = f(V, L) * Li * (N・L)
    //
    // f が BRDF(拡散 + 鏡面)、Li が光の強さ、(N・L) が**面の傾きによる薄まり**。
    // 光源が1つしか無いので積分は要らず、掛け算1回で済む。
    // 光源が増えれば L ごとに足す(Day 39)、あらゆる方向から来るなら積分する(Day 36)。
    vec3 viewDir = normalize(uCameraPosition - vWorldPos);

    vec3 diffuse;
    vec3 specular;
    float ndfD;
    float geomG;
    vec3 fresnelF;
    CookTorrance(
        normal, viewDir, toLight,
        base.rgb, metallic, roughness,
        diffuse, specular, ndfD, geomG, fresnelF);

    float nDotL = max(dot(normal, toLight), 0.0);

    // **3項を1枚ずつ見る窓**(Day 35)。
    // D は 1 を大きく超えるので、そのままだと真っ白に飛ぶ。
    // 20 で割って「ハイライトの形」が見える程度に落としている——
    // **絶対値ではなく分布の形を見るための表示**。
    if (uDebugChannel == 15) { FragColor = vec4(fresnelF, 1.0); return; }
    if (uDebugChannel == 16) { FragColor = vec4(vec3(ndfD / 20.0), 1.0); return; }
    if (uDebugChannel == 17) { FragColor = vec4(vec3(geomG), 1.0); return; }

    vec3 directDiffuse = diffuse * uLightColor * nDotL * shadow * directOcclusion;
    vec3 directSpecular = specular * uLightColor * nDotL * shadow * directOcclusion;

    // --- 環境光 ---
    //
    // **ここが今日の差分の全部**。Day 35 までは「環境光は定数」だった。
    // 定数の環境光には向きが無いので、金属は何も映せず真っ黒のままだった。
    //
    // **フレネルは V・H ではなく N・V で引く**。
    // 環境光には特定の L が無いので H が作れない、というのが理屈だが、
    // 実際上も大事で、直接光の F を流用すると
    // **光の当たる境目(N・L = 0)で F が飛んで、そこに線が出る**。
    vec3 f0 = mix(vec3(uDielectricF0), base.rgb, metallic);
    float nDotV = max(dot(normal, viewDir), 0.0);

    vec3 ambientDiffuse;
    vec3 ambientSpecular;

    if (uIblEnabled == 1)
    {
        // --- 今日の主役: IBL ---
        //
        // 鏡面が持って行った残りが拡散に回る、という分け前は Day 35 と同じ。
        // 違うのは**環境光にも向きがある**こと。
        vec3 kS = FresnelSchlickRoughness(nDotV, f0, roughness);
        vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

        // **拡散**: 法線を渡すだけ。半球ぶんの積分は焼くときに済ませてある。
        vec3 irradiance = texture(uIrradianceMap, normal).rgb * uIblIntensity;
        ambientDiffuse = kD * irradiance * base.rgb * ambientOcclusion;

        // **鏡面**: 分割和の2つを掛け合わせる。
        //
        //   1. 環境側 … 反射方向を、粗さに応じてぼかした段から引く
        //   2. 材質側 … N・V と粗さで表を引き、F0 * A + B を作る
        //
        // reflect(-V, N) が「鏡だったらどこが映るか」。
        // **-V なのは reflect が「入ってくる向き」を取る**ため——
        // ここを V のまま渡すと映り込みが裏返り、
        // 球の左右が入れ替わったような、微妙に気持ち悪い絵になる。
        vec3 reflection = reflect(-viewDir, normal);

        // 粗さ 0〜1 を段 0〜uPrefilterMaxLod へ。**焼いた側と同じ対応**でなければならない
        // (EnvironmentMap.Bake のループ)。
        float lod = uIblPrefilter == 1 ? (roughness * uPrefilterMaxLod) : 0.0;
        vec3 prefiltered = textureLod(uPrefilterMap, reflection, lod).rgb * uIblIntensity;

        vec2 ab = texture(uBrdfLut, vec2(nDotV, roughness)).rg;

        // **鏡面にも掛けている**。厳密には拡散と同じ遮蔽率でよいはずがなく、
        // 鏡面が見ているのは半球全体ではなく反射方向のまわりの狭い範囲なので、
        // 粗さに応じて効き目を弱めるのが正しい(鏡面遮蔽)。
        // ここで同じ値を使っているのは、**掛けないと金属だけ接地が浮く**から——
        // 近似としては掛けるほうが絵が合う、という実務寄りの判断。
        ambientSpecular = prefiltered * ((kS * ab.x) + ab.y) * ambientOcclusion;

        // **中身を1枚ずつ見る窓**(Day 36)。
        // IBL は「なんとなく良くなった」で済ませやすいので、
        // 3枚がそれぞれ何を返しているかを直接見られるようにしておく。
        if (uDebugChannel == 18) { FragColor = vec4(irradiance, 1.0); return; }
        if (uDebugChannel == 19) { FragColor = vec4(prefiltered, 1.0); return; }
        if (uDebugChannel == 20) { FragColor = vec4(ab, 0.0, 1.0); return; }
    }
    else
    {
        // --- Day 35 まで: 定数の環境光 ---
        //
        // 金属は拡散しないので (1 - metallic) で落とす。
        // その結果ハイライト以外が真っ黒になるのが、Day 36 が要る理由そのものだった。
        ambientDiffuse = uAmbientColor * base.rgb * ambientOcclusion * (1.0 - metallic);

        // Day 35 で置いた粗いつなぎ(Ctrl+Shift+7)。
        // 環境光の色 × F × (粗いほど弱く)で代用していただけで、物理的な裏付けは無い。
        vec3 ambientF = FresnelSchlick(nDotV, f0);

        ambientSpecular = uAmbientSpecular == 1
            ? uAmbientColor * ambientF * ambientOcclusion * (1.0 - roughness)
            : vec3(0.0);

        if (uDebugChannel == 18 || uDebugChannel == 19 || uDebugChannel == 20)
        {
            // IBL が OFF のときは、見せるものが無いことが分かるように黒で塗る。
            FragColor = vec4(0.0, 0.0, 0.0, 1.0);
            return;
        }
    }

    if (uDebugChannel == 13) { FragColor = vec4(directDiffuse + ambientDiffuse, 1.0); return; }
    if (uDebugChannel == 14) { FragColor = vec4(directSpecular + ambientSpecular, 1.0); return; }

    vec3 color = directDiffuse + directSpecular + ambientDiffuse + ambientSpecular;

    FragColor = vec4(color + emissive, base.a);
}
