#version 330 core

// **レイマーチング**(Day 58)。画面を覆う三角形1枚の画素シェーダだけで、場面を丸ごと描く。
//
// 頂点シェーダは後処理と同じ fullscreen.vert。**頂点は3つしかない**——形はどこにも頂点として存在せず、
// 下の map() という1本の関数の中にしかない。Shadertoy の作品がこの形で、
// 「画素ごとに光線を1本飛ばし、距離関数を頼りに歩かせて、当たったところの色を塗る」を全画素でやる。
//
// **C# の Sandbox/Sdf.cs と Sandbox/SdfScene.cs に同じ式がある**(自己チェックが突き合わせる)。
// ここを直したら向こうも直すこと(計画書「今日残した歪み」の1つ目)。

out vec4 FragColor;

// --- カメラ ---
// 画面の点 → 世界。**TAA のずらしが入った行列**をそのまま逆にしてあるので、
// TAA を入れると光線も毎フレーム半画素ずれ、SDF の縁も均される(要点3)。
uniform mat4 uInverseViewProjection;

// 世界 → クリップ。当たった点の深度を、ラスタライズと同じ約束で出すのに使う(要点7)。
uniform mat4 uViewProjection;

// シーンのバッファの大きさ(画素)。gl_FragCoord を 0〜1 に直すのに使う。
uniform vec2 uResolution;

// --- 場面と表示(C# の SdfSceneKind / SdfView / SdfShadow と同じ番号)---
uniform int uScene;      // 0 基本形と演算 / 1 無限の繰り返し / 2 ねじれ
uniform int uView;       // 0 陰影 / 1 歩数 / 2 距離の断面 / 3 法線
uniform int uShadow;     // 0 柔らかい / 1 硬い / 2 なし
uniform int uGround;     // 1 = SDF の床を置く(ラスタと混ぜるときは 0。Day 31 の床がある)
uniform int uComposite;  // 1 = ラスタと混ぜる。外れた画素は捨てて、後ろの絵(空・床・箱)を残す
uniform float uTime;
uniform float uSliceHeight;

// --- 歩き方 ---
uniform float uStepScale;   // 歩幅の係数。距離の約束が破れている場面(ねじれ)では 1 より小さく
uniform int uMaxSteps;
uniform float uMaxDistance;
uniform float uHitEpsilon;  // 当たりの幅 = uHitEpsilon × 進んだ距離

// --- 光(エンジンの太陽と同じもの。混ぜたときにラスタの床と陰影の向きが揃う)---
uniform vec3 uLightDirection;   // 光が進む向き(Day 33 からの約束。光源へ向かう向きではない)
uniform vec3 uLightColor;
uniform vec3 uAmbientColor;

// ループの上限は定数でなければならない(GLSL 3.30 のループは回数が決まっている形しか保証されない)。
// uMaxSteps で途中で抜ける。
const int MAX_STEPS = 256;
const int SHADOW_STEPS = 64;
const float SHADOW_MAX_DISTANCE = 12.0;
const float PENUMBRA = 8.0;
const float NORMAL_OFFSET = 0.001;

const float GROUND_Y = -0.5;
const float CELL_SIZE = 3.0;
const float TWIST_RATE = 2.5;
const float TWIST_HALF_WIDTH = 0.6;

// 断面を描くときだけ床を外す(床までの距離が断面の模様を塗り潰すので)。
bool gIncludeGround = true;

// ================================================================
//  部品(Sdf.cs と同じ)
// ================================================================

float sdSphere(vec3 p, float radius)
{
    return length(p) - radius;
}

float sdBox(vec3 p, vec3 halfSize)
{
    // 8つの象限を1つに畳む。外なら正の成分だけの長さ、中ならいちばん近い面まで。
    vec3 q = abs(p) - halfSize;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

float sdRoundBox(vec3 p, vec3 halfSize, float radius)
{
    return sdBox(p, halfSize - vec3(radius)) - radius;
}

float sdTorus(vec3 p, float majorRadius, float minorRadius)
{
    vec2 q = vec2(length(p.xz) - majorRadius, p.y);
    return length(q) - minorRadius;
}

float sdVerticalCapsule(vec3 p, float height, float radius)
{
    p.y -= clamp(p.y, 0.0, height);
    return length(p) - radius;
}

// 滑らかな和。差が k より小さいところでだけ min から引いて、つなぎ目を膨らませる。
float smin(float a, float b, float k)
{
    float h = max(k - abs(a - b), 0.0) / k;
    return min(a, b) - h * h * k * 0.25;
}

// 点を y 軸まわりに回す。**形を +θ 回すときは、点を −θ 回して聞く**。
vec3 rotateY(vec3 p, float angle)
{
    float c = cos(angle);
    float s = sin(angle);
    return vec3(c * p.x - s * p.z, p.y, s * p.x + c * p.z);
}

// 高さに比例して回す。**ここで距離の約束が破れる**(要点5)。
vec3 twistY(vec3 p, float rate)
{
    return rotateY(p, rate * p.y);
}

// xz 平面で無限に繰り返す。round ではなく floor(x + 0.5) なのは C# と丸めを揃えるため。
vec3 repeatXZ(vec3 p, float cellSize)
{
    return vec3(
        p.x - cellSize * floor(p.x / cellSize + 0.5),
        p.y,
        p.z - cellSize * floor(p.z / cellSize + 0.5));
}

// 和。x に距離、y に材質の番号を持つ。
vec2 opUnion(vec2 a, vec2 b)
{
    return (a.x < b.x) ? a : b;
}

// ================================================================
//  場面(SdfScene.cs と同じ)
// ================================================================

vec2 mapPrimitives(vec3 p)
{
    // 1. 滑らかな和
    float swing = 0.25 + 0.45 * (0.5 + 0.5 * sin(uTime * 0.9));
    vec3 blob = p - vec3(-4.5, 0.15, 0.0);
    float left = sdSphere(blob - vec3(swing, 0.0, 0.0), 0.55);
    float right = sdSphere(blob + vec3(swing, 0.0, 0.0), 0.55);
    vec2 result = vec2(smin(left, right, 0.6), 1.0);

    // 2. 差(箱 − 球)
    vec3 carved = rotateY(p - vec3(-1.5, 0.15, 0.0), -uTime * 0.5);
    float carvedBox = max(sdRoundBox(carved, vec3(0.55), 0.05), -sdSphere(carved, 0.72));
    result = opUnion(result, vec2(carvedBox, 2.0));

    // 3. 積(箱 ∩ 球)
    vec3 both = p - vec3(1.5, 0.15, 0.0);
    float intersection = max(sdRoundBox(both, vec3(0.55), 0.05), sdSphere(both, 0.75));
    result = opUnion(result, vec2(intersection, 3.0));

    // 4. 立てたトーラス
    vec3 ring = rotateY(p - vec3(4.5, 0.15, 0.0), -uTime * 0.7);
    float torus = sdTorus(ring.xzy, 0.5, 0.18);
    return opUnion(result, vec2(torus, 4.0));
}

vec2 mapRepetition(vec3 p)
{
    vec3 q = repeatXZ(p, CELL_SIZE);

    float pillar = sdVerticalCapsule(q - vec3(0.0, GROUND_Y, 0.0), 1.9, 0.28);
    float ball = sdSphere(q - vec3(0.0, 1.75, 0.0), 0.42);
    vec2 result = vec2(smin(pillar, ball, 0.25), 3.0);

    float ringY = 0.45 + 0.5 * sin(uTime * 1.2);
    float ring = sdTorus(q - vec3(0.0, ringY, 0.0), 0.6, 0.1);
    return opUnion(result, vec2(ring, 1.0));
}

vec2 mapTwist(vec3 p)
{
    vec3 center = twistY(rotateY(p - vec3(0.0, 1.0, 0.0), -uTime * 0.4), TWIST_RATE);
    vec2 result = vec2(sdRoundBox(center, vec3(TWIST_HALF_WIDTH, 1.5, TWIST_HALF_WIDTH), 0.04), 2.0);

    vec3 left = twistY(p - vec3(-2.8, 0.5, 0.0), TWIST_RATE);
    result = opUnion(result, vec2(sdRoundBox(left, vec3(0.4, 1.0, 0.4), 0.03), 1.0));

    vec3 right = twistY(p - vec3(2.8, 0.5, 0.0), -TWIST_RATE);
    return opUnion(result, vec2(sdRoundBox(right, vec3(0.4, 1.0, 0.4), 0.03), 1.0));
}

// **場面全体が1本の関数**。光線は1歩ごとにこれを丸ごと呼ぶ。
vec2 map(vec3 p)
{
    vec2 result;

    if (uScene == 0)
    {
        result = mapPrimitives(p);
    }
    else if (uScene == 1)
    {
        result = mapRepetition(p);
    }
    else
    {
        result = mapTwist(p);
    }

    if (uGround == 1 && gIncludeGround)
    {
        result = opUnion(result, vec2(p.y - GROUND_Y, 0.0));
    }

    return result;
}

// ================================================================
//  光線を進める(SdfScene.March / Normal / Shadow / Occlusion と同じ)
// ================================================================

// **球面追跡**。返った距離だけ進む——半径 h の球の中には何も無いので、踏み越えない(要点2)。
// 戻り値: x = 進んだ距離、y = 歩数、z = 当たったら 1
vec3 march(vec3 origin, vec3 direction)
{
    float t = 0.0;
    int steps = 0;

    for (int i = 0; i < MAX_STEPS; i++)
    {
        if (i >= uMaxSteps)
        {
            break;
        }

        float h = map(origin + direction * t).x;
        steps = i + 1;

        // 当たりの幅は進んだ距離に比例させる。遠くほど1画素が大きいので、粗く止めてよい。
        if (h < uHitEpsilon * t)
        {
            return vec3(t, float(steps), 1.0);
        }

        t += h * uStepScale;

        if (t > uMaxDistance)
        {
            break;
        }
    }

    return vec3(t, float(steps), 0.0);
}

// 法線 = 距離関数の傾き。四面体の4頂点で聞けば 4 回で済む(中心差分なら 6 回)。
vec3 calcNormal(vec3 p)
{
    const vec2 k = vec2(1.0, -1.0);
    return normalize(
        k.xyy * map(p + k.xyy * NORMAL_OFFSET).x
        + k.yyx * map(p + k.yyx * NORMAL_OFFSET).x
        + k.yxy * map(p + k.yxy * NORMAL_OFFSET).x
        + k.xxx * map(p + k.xxx * NORMAL_OFFSET).x);
}

// 影の光線。柔らかい影は「途中でいちばん際どく外れたとき」の k × h / t を覚えておく(要点6)。
float softShadow(vec3 origin, vec3 toLight)
{
    float result = 1.0;
    float t = 0.02;

    for (int i = 0; i < SHADOW_STEPS; i++)
    {
        float h = map(origin + toLight * t).x;

        if (h < 0.001)
        {
            return 0.0;
        }

        if (uShadow == 0)
        {
            result = min(result, PENUMBRA * h / t);
        }

        t += clamp(h, 0.02, 0.5);

        if (t > SHADOW_MAX_DISTANCE)
        {
            break;
        }
    }

    return clamp(result, 0.0, 1.0);
}

// 環境光の遮蔽。法線の方向に離れた高さ h で、距離が h より短ければそのぶん何かが近くにある。
float occlusion(vec3 p, vec3 normal)
{
    float occ = 0.0;
    float weight = 1.0;

    for (int i = 0; i < 5; i++)
    {
        float h = 0.01 + 0.12 * float(i) / 4.0;
        float d = map(p + normal * h).x;
        occ += (h - d) * weight;
        weight *= 0.95;
    }

    return clamp(1.0 - 3.0 * occ, 0.0, 1.0);
}

// ================================================================
//  色
// ================================================================

vec3 skyColor(vec3 direction)
{
    vec3 zenith = vec3(0.16, 0.30, 0.60);
    vec3 horizon = vec3(0.60, 0.68, 0.78);
    vec3 color = mix(horizon, zenith, pow(clamp(direction.y, 0.0, 1.0), 0.6));

    float sun = max(dot(direction, -uLightDirection), 0.0);
    color += uLightColor * (pow(sun, 400.0) * 6.0 + pow(sun, 8.0) * 0.12);
    return color;
}

vec3 materialColor(float material, vec3 p, float t)
{
    if (material < 0.5)
    {
        // 床は市松模様。**奥行きとねじれが目で読める**ように。
        // 遠くでは1画素に何マスも入って模様がちらつくので、距離で灰色に寄せる。
        float checker = mod(floor(p.x) + floor(p.z), 2.0);
        return vec3(mix(0.22 + 0.16 * checker, 0.30, clamp(t / 40.0, 0.0, 1.0)));
    }

    if (material < 1.5)
    {
        return vec3(0.85, 0.40, 0.12);   // 橙
    }

    if (material < 2.5)
    {
        return vec3(0.10, 0.52, 0.52);   // 青緑
    }

    if (material < 3.5)
    {
        return vec3(0.78, 0.76, 0.68);   // 象牙
    }

    return vec3(0.72, 0.13, 0.11);       // 赤
}

vec3 shade(vec3 p, vec3 direction, float t, float material)
{
    vec3 normal = calcNormal(p);
    vec3 albedo = materialColor(material, p, t);
    vec3 toLight = -uLightDirection;

    float diffuse = max(dot(normal, toLight), 0.0);

    // 光に背を向けている面は影の光線を飛ばさない(どうせ暗い)。**影の光線がいちばん高い**ので効く(内訳)。
    float shadow = 1.0;
    if (uShadow != 2 && diffuse > 0.0)
    {
        // 面から少し浮かせてから飛ばす。浮かせないと自分自身に当たって全部影になる(Day 33 のアクネと同じ話)。
        shadow = softShadow(p + normal * 0.002, toLight);
    }

    float ao = occlusion(p, normal);

    // 上を向いた面ほど空の光を多く受ける(半球の光の雑な近似)。
    float skyLight = 0.5 + 0.5 * normal.y;

    vec3 halfVector = normalize(toLight - direction);
    float specular = pow(max(dot(normal, halfVector), 0.0), 32.0) * 0.25;

    vec3 color = albedo * uLightColor * diffuse * shadow;
    color += uLightColor * specular * diffuse * shadow;
    color += albedo * uAmbientColor * 2.5 * skyLight * ao;

    // 霧。**繰り返しの場面の地平線**を空に溶かす(遠くの市松模様のちらつきも隠れる)。
    float fog = 1.0 - exp(-0.0007 * t * t);
    return mix(color, skyColor(direction), fog);
}

// 歩数を色に。青(少ない)→ 緑 → 黄 → 赤(上限)。
vec3 heat(float x)
{
    x = clamp(x, 0.0, 1.0);
    return clamp(vec3(1.5 - abs(4.0 * x - 3.0), 1.5 - abs(4.0 * x - 2.0), 1.5 - abs(4.0 * x - 1.0)), 0.0, 1.0);
}

// 断面の色。外は橙、中は青、等高線は 0.157m おき、面の上(距離 0)に白い線。
vec3 sliceColor(float d)
{
    vec3 color = (d > 0.0) ? vec3(0.90, 0.60, 0.30) : vec3(0.65, 0.85, 1.00);
    color *= 1.0 - exp(-6.0 * abs(d));
    color *= 0.8 + 0.2 * cos(40.0 * d);
    return mix(color, vec3(1.0), 1.0 - smoothstep(0.0, 0.02, abs(d)));
}

float windowDepth(vec3 p)
{
    // **ラスタライズが頂点にやっていることと同じ**。クリップ座標 → w で割る → 0〜1 へ。
    // これを書くと、あとから描く粒・TAA の速度・被写界深度が、SDF の形をラスタの形と区別せずに扱える(要点7)。
    vec4 clip = uViewProjection * vec4(p, 1.0);
    return (clip.z / clip.w) * 0.5 + 0.5;
}

void main()
{
    // --- 画素から光線を作る(SdfScene.PixelRay と同じ)---
    vec2 ndc = (gl_FragCoord.xy / uResolution) * 2.0 - 1.0;

    vec4 nearClip = uInverseViewProjection * vec4(ndc, -1.0, 1.0);
    vec4 farClip = uInverseViewProjection * vec4(ndc, 1.0, 1.0);
    vec3 origin = nearClip.xyz / nearClip.w;
    vec3 direction = normalize(farClip.xyz / farClip.w - origin);

    gIncludeGround = true;
    vec3 result = march(origin, direction);
    float t = result.x;
    bool hit = result.z > 0.5;
    vec3 hitPoint = origin + direction * t;

    vec3 color;
    float depth = hit ? windowDepth(hitPoint) : 1.0;

    if (uView == 1)
    {
        // 歩数。**外れた画素も塗る**——空へ抜けるまでに歩いた数も手間のうち。
        color = heat(result.y / float(uMaxSteps)) * 0.8;
    }
    else if (uView == 3)
    {
        color = hit ? calcNormal(hitPoint) * 0.5 + 0.5 : skyColor(direction) * 0.3;
    }
    else
    {
        if (!hit && uComposite == 1 && uView == 0)
        {
            // **外れた画素は捨てる**。後ろに描いてある空・床・箱が残る。
            discard;
        }

        color = hit ? shade(hitPoint, direction, t, map(hitPoint).y) : skyColor(direction);

        if (uView == 2)
        {
            // 距離の断面。水平な板 y = uSliceHeight を光線で切り、板が形より手前なら距離を色にする。
            // 板は 16m 四方に限る。無限に広げると、遠くで等高線が1画素より細くなってちらつく。
            float slice = (uSliceHeight - origin.y) / direction.y;
            vec3 q = origin + direction * slice;

            if (abs(direction.y) > 1e-4 && slice > 0.0 && (!hit || slice < t)
                && abs(q.x) < 8.0 && abs(q.z) < 8.0)
            {

                gIncludeGround = false;
                color = sliceColor(map(q).x);
                gIncludeGround = true;

                depth = windowDepth(q);
            }
            else
            {
                color *= 0.35;
            }
        }
    }

    gl_FragDepth = depth;
    FragColor = vec4(color, 1.0);
}
