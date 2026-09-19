// ---------------------------------------------------------------------------------------
// 7本のシェーダが共有する部分(Day 62c で追加)。
//
// **GLSL に #include は無い**。正確には拡張があるが、shaderc はインクルードの解決を
// 呼び出し側に丸投げする作りなので、今日は C# 側(ShaderCompiler.ResolveIncludes)が
// **翻訳に渡す前に文字列として差し込んでいる**。行番号がずれるのが難点。
//
// ここに置くのは「どのシェーダから見ても同じであってほしいもの」だけ:
//   - GPU に渡す場面の形(Sphere / プッシュ定数)
//   - 光の式(sky / shadeSurface / toneMap)
//   - 球と床の交差
//
// 逆に、**ディスクリプタの宣言はここに置かない**(binding 0 の画像は rgen と comp だけ、
// binding 2 の TLAS は撃つシェーダだけが要る)。使わないものまで宣言すると、
// ディスクリプタセットレイアウトの StageFlags を無駄に広げることになる。
// ---------------------------------------------------------------------------------------

// 球1個。C# 側の GpuSphere と同じ 32 バイト(vec4 x 2)。
struct Sphere {
    vec4 centerRadius;  // xyz = 中心、w = 半径
    vec4 albedoKind;    // xyz = 色、w = 材質(0 = 拡散、1 = 金属)
};

layout(set = 0, binding = 1, std430) readonly buffer Spheres {
    Sphere spheres[];
};

// プッシュ定数。C# 側の PushConstants と1バイトも違ってはいけない。
layout(push_constant) uniform Push {
    vec4 positionHalfWidth;  // xyz = カメラ位置、w = 画面の半分の幅
    vec4 rightHalfHeight;    // xyz = 右方向、   w = 画面の半分の高さ
    vec4 up;                 // xyz = 上方向
    vec4 forward;            // xyz = 前方向
    int width;
    int height;
    int sphereCount;
    int mode;                // 0 = 陰影、1 = 法線、2 = 交差判定の回数、3 = 同(細かい目盛り)
    int diffuseCount;        // 拡散の球の数 = 金属の球が始まる位置(Day 62c で追加)
} pc;

// 反射を追う回数の上限。金属に当たるたびに1減る。
const int MaxBounces = 3;

// 太陽の向き(光が**来る**向き)。時刻を持たない場面なので決め打ち。
const vec3 SunDirection = normalize(vec3(-0.45, 0.85, -0.30));
const vec3 SunColor = vec3(1.0, 0.96, 0.88) * 1.9;

// 床は y = 0 の無限の平面。**加速構造に入れない**——無限に広がるものは箱で囲めない。
const float FloorHeight = 0.0;

// 交差判定を何回したか(mode 2 / 3 の絵)。コンピュート版でしか意味を持たない。
int gTests = 0;

/// レイトレーシングパイプラインで運ぶ荷物(Day 62c)。
/// **どのシェーダでも同じ並びでなければならない**——SPIR-V の型が一致していないと、
/// パイプラインの作成時に弾かれる(検証レイヤが無いと静かに壊れる)。
struct RayPayload {
    vec3 color;   // その光線が持ち帰った色
    int depth;    // あと何回跳ね返ってよいか
};

// -----------------------------------------------------------------------------
// 光線と球(Day 59 と同じ式。half-b の形)
//
//   |P - C|^2 = r^2 に P = O + tD を入れて oc = O - C と置くと
//   t^2 + 2(oc.D)t + (oc.oc - r^2) = 0  ... D は長さ 1 なので t^2 の係数は 1
//   b = oc.D と置くと t = -b +- sqrt(b*b - c)
// -----------------------------------------------------------------------------
bool hitSphere(vec3 origin, vec3 direction, vec3 center, float radius, float tMin, float tMax, out float t) {
    gTests++;

    vec3 oc = origin - center;
    float b = dot(oc, direction);
    float c = dot(oc, oc) - radius * radius;
    float discriminant = b * b - c;
    if (discriminant < 0.0) {
        t = 0.0;
        return false;
    }

    float root = sqrt(discriminant);

    // 近いほうから試す。始点が球の中(または球が丸ごと後ろ)なら遠いほうを見る。
    t = -b - root;
    if (t <= tMin) {
        t = -b + root;
    }
    return t > tMin && t < tMax;
}

/// 床(y = 0)までの距離。当たらないなら負の値。
float floorDistance(vec3 origin, vec3 direction) {
    if (abs(direction.y) <= 1e-6) {
        return -1.0;
    }
    return (FloorHeight - origin.y) / direction.y;
}

/// 市松模様。x と z の整数部の和の偶奇で色を選ぶ(Day 59 の Material.AlbedoAt と同じ)。
vec3 floorAlbedo(vec3 position) {
    vec2 cell = floor(position.xz);
    bool even = mod(cell.x + cell.y, 2.0) < 0.5;
    return even ? vec3(0.62) : vec3(0.18);
}

/// 何にも当たらなかったときの色。上を向くほど青くなる素朴な空。
vec3 sky(vec3 direction) {
    float up = clamp(direction.y, 0.0, 1.0);
    vec3 gradient = mix(vec3(0.72, 0.80, 0.92), vec3(0.24, 0.42, 0.78), sqrt(up));

    // 太陽の見た目。反射の中に丸い光が映るようにしておくと、金属の球が金属らしく見える。
    float sun = pow(max(dot(direction, SunDirection), 0.0), 900.0);
    return gradient + SunColor * sun;
}

/// 拡散面の色。直接光 + 空からの一定の明るさ(環境光)。
vec3 shadeSurface(vec3 albedo, vec3 normal, bool shadowed) {
    float ndotl = max(dot(normal, SunDirection), 0.0);
    vec3 direct = (ndotl > 0.0 && !shadowed) ? SunColor * ndotl : vec3(0.0);

    // 空を「上から来る弱い光」として足す。影の中が真っ黒にならないようにするごまかし。
    vec3 ambient = sky(normal) * 0.13;
    return albedo * (direct + ambient);
}

/// 簡単なトーンマップ + ガンマ。線形の明るさのまま出すと、太陽の当たった面が白く飛ぶ。
vec3 toneMap(vec3 color) {
    color = color / (color + vec3(1.0));
    return pow(color, vec3(1.0 / 2.2));
}

/// 画素の中心を通る光線の向き。行列は使わず、カメラの3軸を混ぜるだけ。
vec3 cameraRay(ivec2 pixel) {
    vec2 ndc = vec2(
        (float(pixel.x) + 0.5) / float(pc.width) * 2.0 - 1.0,
        1.0 - (float(pixel.y) + 0.5) / float(pc.height) * 2.0);

    return normalize(
        pc.forward.xyz
        + pc.rightHalfHeight.xyz * (ndc.x * pc.positionHalfWidth.w)
        + pc.up.xyz * (ndc.y * pc.rightHalfHeight.w));
}

/// 「何番目のジオメトリの、何番目の図形か」から球の番号を出す(Day 62c で追加)。
///
/// BLAS のジオメトリを材質で2つに割ったので(0 = 拡散、1 = 金属)、
/// シェーダが受け取る図形の番号は**そのジオメトリの中での番号**になる。
/// 球の配列は拡散が先・金属が後ろに並べてあるので、金属側は境目を足し戻せばよい。
int sphereIndex(int geometryIndex, int primitiveIndex) {
    return geometryIndex == 0 ? primitiveIndex : pc.diffuseCount + primitiveIndex;
}

/// 交差判定の回数を色にする(mode 2 / 3)。暗い青 -> 水色 -> 緑 -> 黄 -> 赤 の順に高い。
vec3 heatColor(int tests) {
    // 1画素あたりの目安。mode 2 は球の数の3倍、mode 3 は 48 回で振り切る。
    float scale = pc.mode == 3 ? 48.0 : float(max(pc.sphereCount, 1) * 3);
    float x = clamp(float(tests) / scale, 0.0, 1.0);

    const vec3 stops[6] = vec3[6](
        vec3(0.03, 0.03, 0.12),
        vec3(0.15, 0.25, 0.85),
        vec3(0.10, 0.80, 0.85),
        vec3(0.20, 0.85, 0.25),
        vec3(0.95, 0.85, 0.15),
        vec3(0.90, 0.15, 0.10));

    float s = x * 5.0;
    int i = min(int(floor(s)), 4);
    return mix(stops[i], stops[i + 1], s - float(i));
}
