#version 430 core

// ============================================================
//  Day 63b: 評価シェーダ — 割った点1つずつの居場所を決める
// ============================================================
//
// テッセレータ(固定機能)が 0〜1 の升目の上に点を作り、その点1つにつき1回ここが呼ばれる。
// 受け取るのは <b>gl_TessCoord</b>(四角パッチなら (u, v)、三角パッチなら重心座標)で、
// <b>制御点は gl_in[] で全部見える</b>。**「升目の (u,v) を、形の上の点に翻訳する」**のがこの段の仕事。
//
// レベル 64 の四角パッチなら 65 x 65 = 4,225 回走る。**CPU が送った頂点は4つだけ**。
//
// ------------------------------------------------------------
//  layout の3つ
// ------------------------------------------------------------
//
//   quads               … パッチの形。triangles / isolines も選べる
//   SPACING             … 割り方。**コンパイル時に決まる**ので uniform にできない
//   ccw                 … 出てくる三角形の巻き順
//
// <b>割り方が uniform にできない</b>のが今日の仕掛けどころ。
// 3通りを見比べるには<b>同じソースを3回コンパイルする</b>しかなく、
// Day 57 でコンピュートのワークグループの大きさに使った <c>#define</c> の差し込みが、
// ここでそのまま効く(今日 Shader に描画用の defines を通した理由)。
//
//   equal_spacing           … レベルを**整数に切り上げ**て等分。レベルが変わると**カクッと切り替わる**
//   fractional_odd_spacing  … 奇数に丸め、端の2本だけを伸び縮みさせる。**1 から連続に変えられる**
//   fractional_even_spacing … 偶数に丸める。同じく連続だが、**レベル 1 が作れない**(最低 2)
//
// LOD で細かさを連続に変えたいなら fractional_odd が定石。equal だと、
// レベルが 4 → 5 に変わった瞬間に**面全体の頂点が一斉に動く**(ポッピング)。

#ifndef SPACING
#define SPACING equal_spacing
#endif

layout (quads, SPACING, ccw) in;

in Control
{
    vec3 worldPosition;
    vec2 texCoord;
    vec4 color;
    vec3 normal;
} IN[];

out Varying
{
    vec3 worldPosition;
    vec3 normal;
    vec4 color;
    vec2 texCoord;
} OUT;

uniform mat4 uViewProjection;

/// 0 = 平らな格子に高さを付ける / 1 = 立方体を膨らませて球にする。
uniform int uTopic;

/// 変位の強さ(m)。0 にすると平らなまま。
uniform float uDisplacement;

/// 波の時計(秒)。
uniform float uTime;

/// 球の半径(m)。
uniform float uRadius;

/// 高さを測る刻み(m)。法線を差分で出すのに使う。**C# の鏡と同じ値**。
uniform float uNormalStep;

/// パッチごとに色を変えるか。**gl_PrimitiveID がここでは「何枚目のパッチか」**になる。
uniform int uColorByPatch;

/// パッチの番号から色を作る。黄金比で回すと、隣の番号どうしがいちばん離れた色になる。
vec3 patchColor(int id)
{
    float hue = fract(float(id) * 0.61803399);
    vec3 wheel = abs((fract(hue + vec3(0.0, 0.6666667, 0.3333333)) * 6.0) - 3.0) - 1.0;
    return (clamp(wheel, 0.0, 1.0) * 0.55) + 0.35;
}

/// **高さの関数**。C# の <c>TessellationMath.Height</c> と1文字ずつ同じ式。
///
/// 波数の違う3つの波を足しただけ。1つだと格子の目と周期が揃って
/// 「割ったのに同じ形が並ぶ」絵になりやすい。
///
/// **波長は格子の大きさに合わせてある**。格子は 6m 四方なので、
/// いちばん長い波が 5.7m、いちばん短い波が 1.26m。これより細かい波を入れると、
/// レベルを上げても形が落ち着かず、**「細かくすると滑らかになる」のが絵で読めなくなる**
/// (割る細かさと、割って写し取る形の細かさは別の話)。
float height(vec2 p)
{
    float h = sin((p.x * 1.1) + uTime) * cos((p.y * 1.1) - (uTime * 0.7));
    h += 0.45 * sin((p.x * 2.4) + (p.y * 1.9) + (uTime * 1.3));
    h += 0.20 * sin((p.x * 5.0) - (p.y * 4.1));
    return h;
}

/// 四角パッチの双一次補間。**4隅の順番は (0,0) (1,0) (0,1) (1,1)**。
vec3 bilinear(vec3 p00, vec3 p10, vec3 p01, vec3 p11, vec2 uv)
{
    return mix(mix(p00, p10, uv.x), mix(p01, p11, uv.x), uv.y);
}

vec2 bilinear2(vec2 p00, vec2 p10, vec2 p01, vec2 p11, vec2 uv)
{
    return mix(mix(p00, p10, uv.x), mix(p01, p11, uv.x), uv.y);
}

vec4 bilinear4(vec4 p00, vec4 p10, vec4 p01, vec4 p11, vec2 uv)
{
    return mix(mix(p00, p10, uv.x), mix(p01, p11, uv.x), uv.y);
}

void main()
{
    vec2 uv = gl_TessCoord.xy;

    vec3 base = bilinear(
        IN[0].worldPosition, IN[1].worldPosition, IN[2].worldPosition, IN[3].worldPosition, uv);

    vec2 texCoord = bilinear2(
        IN[0].texCoord, IN[1].texCoord, IN[2].texCoord, IN[3].texCoord, uv);

    vec3 world;
    vec3 normal;

    if (uTopic == 0)
    {
        // --- 平らな格子に高さを付ける ---
        //
        // **ここが変位(displacement)**。元のメッシュには無かった凹凸が、
        // 割った点を動かすだけで生まれる。頂点バッファは 1 バイトも増えていない。
        world = base + (vec3(0.0, 1.0, 0.0) * height(base.xz) * uDisplacement);

        // 法線は**高さの傾きから**。前後左右に少しずらして高さを測り、2本の接ベクトルの外積を取る。
        // 解析的に微分することもできるが、**C# の鏡と同じ式にしておきたい**ので差分で揃えた
        // (式を変えたとき、両方を直し忘れたらすぐ落ちるように)。
        float e = uNormalStep;
        float hx = (height(base.xz + vec2(e, 0.0)) - height(base.xz - vec2(e, 0.0))) * uDisplacement;
        float hz = (height(base.xz + vec2(0.0, e)) - height(base.xz - vec2(0.0, e))) * uDisplacement;

        vec3 tangentX = vec3(2.0 * e, hx, 0.0);
        vec3 tangentZ = vec3(0.0, hz, 2.0 * e);
        normal = normalize(cross(tangentZ, tangentX));
    }
    else
    {
        // --- 立方体を膨らませて球にする ---
        //
        // **6面 x N x N のパッチを、割ったあとで正規化するだけ**。
        // 立方体の面の上の格子を球に写すので、UV 球(Day 63a)と違って
        // <b>極が詰まらない</b>(三角形の大きさが揃う)。
        //
        // これが「1枚のパッチで滑らかな曲面」のいちばん小さい形。
        // 制御点は角4つしか無いのに、レベルを上げるだけで球が滑らかになる——
        // **形を決めているのは頂点ではなく、この段の式**。
        vec3 direction = normalize(base);
        float wave = 1.0 + (uDisplacement * 0.12 * height(direction.xz * 2.0));

        world = direction * uRadius * wave;
        normal = direction;
    }

    OUT.worldPosition = world;
    OUT.normal = normal;
    // **gl_PrimitiveID はこの段では「何枚目のパッチか」**。
    // 割ったあとの三角形の番号ではないので、1枚のパッチの中では同じ色になる——
    // だから「どこまでが1パッチか」が絵で分かる。
    OUT.color = uColorByPatch == 1
        ? vec4(patchColor(gl_PrimitiveID), 1.0)
        : bilinear4(IN[0].color, IN[1].color, IN[2].color, IN[3].color, uv);
    OUT.texCoord = texCoord;

    // **ここで初めてクリップ空間へ**。割る前の制御点は画面に出ないので、
    // 投影はいちばん後ろの段で1回だけ掛ければよい。
    gl_Position = uViewProjection * vec4(world, 1.0);
}
