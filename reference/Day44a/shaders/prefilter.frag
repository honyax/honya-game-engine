#version 330 core

// **事前フィルタ環境マップ**を焼く(Day 36)。鏡面反射のための IBL。
//
// 拡散(irradiance.frag)は法線だけで決まったので、1枚に焼けた。
// 鏡面はそうはいかない——返る光は
//
//   ∫ f(V, L) L(l) (N・L) dω
//
// で、**V と N と粗さの3つ**に依存する。3次元ぶんの表は現実的でない。
//
// そこで **分割和近似(split-sum approximation)** を使う。上の積分を
//
//   ∫ f L (N・L) dω  ≈  [ ∫ L(l) D(l) dω ] * [ ∫ f(V,L) (N・L) dω ]
//                        ~~~~~~~~~~~~~~~~     ~~~~~~~~~~~~~~~~~~~~~
//                        環境を粗さでぼかす    材質だけで決まる係数
//
// と2つの積で近似する(Karis 2013)。**厳密ではない**が、
// 破綻が見えるのは「粗い金属を極端に浅い角度から見たとき」くらいで、
// 実写と並べても分からない程度に収まる。
//
// このシェーダは左半分を担当する:**環境を粗さごとにぼかしたもの**を、
// ミップの段に1つずつ焼く。右半分は CPU で表にした(Pbr.IntegrateBrdf)。
//
// **N = V = R と置く**のが Karis の割り切りで、これが「V に依存しない」を成立させている。
// 代償は**斜めから見たときに映り込みが伸びない**こと(本当は横に伸びる)。
// 球の縁の映り込みが少し素直すぎるのはこのため。

in vec3 vLocalPos;

out vec4 FragColor;

uniform samplerCube uEnvironment;

/// このミップが担当する粗さ。0(原寸)から 1(いちばん小さいミップ)まで。
uniform float uRoughness;

/// 元の環境マップの1辺。ミップ選択(下の「ちらつき対策」)で要る。
uniform float uSourceSize;

const float PI = 3.14159265359;
const uint SAMPLE_COUNT = 1024u;

/// van der Corput 列(2進数を左右反転した小数)。Pbr.Hammersley と同じもの。
float RadicalInverseVdC(uint bits)
{
    bits = (bits << 16u) | (bits >> 16u);
    bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
    bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
    bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
    bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
    return float(bits) * 2.3283064365386963e-10;
}

vec2 Hammersley(uint i, uint count)
{
    return vec2(float(i) / float(count), RadicalInverseVdC(i));
}

/// GGX の重点サンプリング。**Pbr.ImportanceSampleGgx の GLSL 版**。
vec3 ImportanceSampleGgx(vec2 xi, vec3 n, float alpha)
{
    float a2 = alpha * alpha;

    float phi = 2.0 * PI * xi.x;
    float cosTheta = sqrt((1.0 - xi.y) / (1.0 + ((a2 - 1.0) * xi.y)));
    float sinTheta = sqrt(max(1.0 - (cosTheta * cosTheta), 0.0));

    vec3 local = vec3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta);

    vec3 up = abs(n.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, n));
    vec3 bitangent = cross(n, tangent);

    return normalize((tangent * local.x) + (bitangent * local.y) + (n * local.z));
}

float DistributionGgx(float nDotH, float alpha)
{
    float a2 = alpha * alpha;
    float d = (nDotH * nDotH * (a2 - 1.0)) + 1.0;
    return a2 / (PI * d * d);
}

void main()
{
    vec3 n = normalize(vLocalPos);

    // **N = V = R**。上のコメントの割り切りがこの2行。
    vec3 r = n;
    vec3 v = r;

    float alpha = uRoughness * uRoughness;

    vec3 color = vec3(0.0);
    float weight = 0.0;

    for (uint i = 0u; i < SAMPLE_COUNT; i++)
    {
        vec2 xi = Hammersley(i, SAMPLE_COUNT);
        vec3 h = ImportanceSampleGgx(xi, n, alpha);
        vec3 l = normalize((2.0 * dot(v, h) * h) - v);

        float nDotL = max(dot(n, l), 0.0);
        if (nDotL <= 0.0)
        {
            continue;
        }

        // --- ちらつき対策: 引くミップを粗さで選ぶ ---
        //
        // 太陽は**1テクセルで 300**という値を持つ。
        // 原寸(ミップ 0)から 1024 点引くと、太陽に当たった数点だけが
        // 桁違いに効いて、**隣のテクセルで当たり外れが変わる**——
        // 結果、ぼかしたはずの面に白い点が散る(ファイアフライ)。
        //
        // 対処は「1サンプルが担当する立体角」を見積もって、
        // それに見合った大きさのミップから引くこと。
        // 粗いほど広い範囲を担当するので、より小さいミップになる。
        // **元の環境マップにミップを作っておく**必要があるのはこのため。
        float nDotH = max(dot(n, h), 0.0);
        float hDotV = max(dot(h, v), 0.0);
        float d = DistributionGgx(nDotH, alpha);
        float pdf = ((d * nDotH) / (4.0 * hDotV)) + 0.0001;

        float texelSolidAngle = (4.0 * PI) / (6.0 * uSourceSize * uSourceSize);
        float sampleSolidAngle = 1.0 / (float(SAMPLE_COUNT) * pdf);

        float mip = uRoughness == 0.0
            ? 0.0
            : (0.5 * log2(sampleSolidAngle / texelSolidAngle));

        // **重みは N・L**。重点サンプリングは H の分布に沿って引いているので、
        // そのままだと L の分布が偏る。N・L で重み付けして割り戻すのが
        // Karis の実装以来の定番で、厳密ではないが見た目がよくなる。
        color += textureLod(uEnvironment, l, mip).rgb * nDotL;
        weight += nDotL;
    }

    FragColor = vec4(color / max(weight, 0.001), 1.0);
}
