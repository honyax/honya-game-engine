#version 430 core

// 楕円体1個を、画面の上の四角形1枚にする(要点2・3)。
//
// 頂点属性は1つも使わない。
//   gl_InstanceID … 何番目に描くか。並べた順(uOrder)を引いて、どの楕円体かを知る
//   gl_VertexID   … 四角形の4隅のどれか(0〜3。TriangleStrip で2枚の三角形になる)
//
// C# の SplatProjection.Project と同じ計算。片方を直したら、もう片方も直す(自己チェック9が突き合わせる)。

struct Splat
{
    vec4 positionOpacity;   // xyz = 位置、w = 不透明度
    vec4 covarianceA;       // 3D の共分散 xx, xy, xz, yy
    vec4 covarianceB;       // yz, zz, (空き), (空き)
};

layout(std430, binding = 0) readonly buffer Splats { Splat uSplats[]; };
layout(std430, binding = 1) readonly buffer Coefficients { float uSh[]; };
layout(std430, binding = 2) readonly buffer Order { uint uOrder[]; };

uniform vec3 uCameraPosition;
uniform vec3 uRight;
uniform vec3 uUp;
uniform vec3 uForward;
uniform vec2 uFocal;            // 焦点距離(画素)
uniform vec2 uViewport;         // 画面の大きさ(画素)
uniform vec2 uTanHalf;          // 画角の半分の tan(横・縦)
uniform int uShDegree;          // 使う球面調和の次数
uniform int uShStride;          // 1個あたりの係数の float の数
uniform float uDilation;        // 0.3 の広げ(切ると 0)
uniform float uCovarianceScale; // 大きさの倍率の2乗
uniform int uMode;              // DisplayMode

out vec2 vOffset;               // 楕円の中心からのずれ(画素)。四角形の中で線形に補間される
flat out vec3 vConic;           // 2D の共分散の逆行列(xx, xy, yy)
flat out vec4 vColor;           // rgb = 色、a = 不透明度

const float kNear = 0.2;
const float kFrustumLimit = 1.3;

const vec2 kCorners[4] = vec2[](vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(-1.0, 1.0), vec2(1.0, 1.0));

// 球面調和の定数。C# の SphericalHarmonics と同じ値・同じ符号。
const float C0 = 0.28209479177387814;
const float C1 = 0.4886025119029199;
const float C2[5] = float[](1.0925484305920792, -1.0925484305920792, 0.31539156525252005, -1.0925484305920792, 0.5462742152960396);
const float C3[7] = float[](-0.5900435899266435, 2.890611442640554, -0.4570457994644658, 0.3731763325901154, -0.4570457994644658, 1.445305721320277, -0.5900435899266435);

vec3 Coefficient(uint index, int k)
{
    int o = int(index) * uShStride + k * 3;
    return vec3(uSh[o], uSh[o + 1], uSh[o + 2]);
}

// 向き d(カメラ → 楕円体)から見た色(要点5)。
vec3 ShColor(uint index, vec3 d)
{
    vec3 color = C0 * Coefficient(index, 0);
    if (uShDegree >= 1)
    {
        float x = d.x, y = d.y, z = d.z;
        color += -C1 * y * Coefficient(index, 1) + C1 * z * Coefficient(index, 2) - C1 * x * Coefficient(index, 3);
        if (uShDegree >= 2)
        {
            float xx = x * x, yy = y * y, zz = z * z;
            float xy = x * y, yz = y * z, xz = x * z;
            color += C2[0] * xy * Coefficient(index, 4)
                   + C2[1] * yz * Coefficient(index, 5)
                   + C2[2] * (2.0 * zz - xx - yy) * Coefficient(index, 6)
                   + C2[3] * xz * Coefficient(index, 7)
                   + C2[4] * (xx - yy) * Coefficient(index, 8);
            if (uShDegree >= 3)
            {
                color += C3[0] * y * (3.0 * xx - yy) * Coefficient(index, 9)
                       + C3[1] * xy * z * Coefficient(index, 10)
                       + C3[2] * y * (4.0 * zz - xx - yy) * Coefficient(index, 11)
                       + C3[3] * z * (2.0 * zz - 3.0 * xx - 3.0 * yy) * Coefficient(index, 12)
                       + C3[4] * x * (4.0 * zz - xx - yy) * Coefficient(index, 13)
                       + C3[5] * z * (xx - yy) * Coefficient(index, 14)
                       + C3[6] * x * (xx - 3.0 * yy) * Coefficient(index, 15);
            }
        }
    }

    return max(color + 0.5, 0.0);
}

// 描かない。4隅をすべてクリップの外(z > w)に置くと、三角形がまるごと捨てられて画素シェーダが1回も走らない。
void Cull()
{
    gl_Position = vec4(0.0, 0.0, 2.0, 1.0);
    vOffset = vec2(0.0);
    vConic = vec3(0.0);
    vColor = vec4(0.0);
}

void main()
{
    uint index = uOrder[gl_InstanceID];
    Splat s = uSplats[index];
    vec3 position = s.positionOpacity.xyz;

    // カメラから見た座標(右・上・前)。
    vec3 t = position - uCameraPosition;
    vec3 view = vec3(dot(t, uRight), dot(t, uUp), dot(t, uForward));
    if (view.z < kNear)
    {
        Cull();
        return;
    }

    float z = view.z;
    vec2 limit = kFrustumLimit * uTanHalf;
    vec2 tc = clamp(view.xy / z, -limit, limit) * z;

    // T = J W の2本の行。J は透視投影の1次近似(要点2)。
    vec3 row0 = (uFocal.x / z) * uRight + (-uFocal.x * tc.x / (z * z)) * uForward;
    vec3 row1 = (uFocal.y / z) * uUp + (-uFocal.y * tc.y / (z * z)) * uForward;

    mat3 sigma = mat3(
        s.covarianceA.x, s.covarianceA.y, s.covarianceA.z,
        s.covarianceA.y, s.covarianceA.w, s.covarianceB.x,
        s.covarianceA.z, s.covarianceB.x, s.covarianceB.y) * uCovarianceScale;

    // Σ' = T Σ Tᵀ(2x2)。
    float a = dot(row0, sigma * row0) + uDilation;
    float b = dot(row0, sigma * row1);
    float c = dot(row1, sigma * row1) + uDilation;

    float det = a * c - b * b;
    if (det <= 0.0)
    {
        Cull();
        return;
    }

    float mid = 0.5 * (a + c);
    float lambda1 = mid + sqrt(max(0.1, mid * mid - det));
    float radius = ceil(3.0 * sqrt(lambda1));

    // 点で見る表示は、大きさによらず半径 2 画素の四角形にする。
    if (uMode == 1)
    {
        radius = 2.0;
    }

    vec2 center = uFocal * view.xy / z;
    vec2 offset = kCorners[gl_VertexID] * radius;

    // 画素 → クリップ座標。画面の中心が 0、端が ±1。奥行きは使わないので 0 のまま。
    gl_Position = vec4((center + offset) / (0.5 * uViewport), 0.0, 1.0);
    vOffset = offset;
    vConic = vec3(c, -b, a) / det;
    vColor = vec4(ShColor(index, normalize(t)), s.positionOpacity.w);
}
