// 5本のシェーダ(cull.comp / scene.vert / meshlet.task / meshlet.mesh / shade.frag)が #include する共通部分。
// cull.comp は Day 65a で増えた。Day 65b で増えた depth_reduce.comp は別のセットを使うので、これを読まない。
//
// Day 65c: cull.comp と meshlet.task は、2パスの「早いパス(EARLY)」「遅いパス(LATE)」用にも翻訳する
// (定義なし = Day 65a までの1パス)。LATE のときだけ Hi-Z(binding 12)と occluded() が使える。
//
// ここに書くのは「GPU に何がどの番号で挿さっているか」の一覧。
// binding の番号は MeshRenderer.cs の buffers の並びと 1 対 1 に対応する。
// 片方だけ書き換えると、検証レイヤが無ければ**黙って別のバッファを読む**。

// ---- 0: 1フレームぶんの値(uniform buffer)------------------------------------------
// C# の FrameUniforms と1バイトも違ってはいけない。
// uniform ブロックは std140 で並ぶが、mat4 / vec4 / uint だけなら std430 と同じ並びになる。
layout(std140, set = 0, binding = 0) uniform Frame
{
    mat4 viewProj;      // 世界 → クリップ座標
    vec4 cameraPos;     // xyz = 目の位置
    uint mode;          // 0 = 陰影 / 1 = メッシュレット / 2 = 三角形
    uint meshletCount;  // 1体ぶんのメッシュレットの数(Day 64b で追加。タスクシェーダが範囲の外を捨てる)
    uint cullFlags;     // 1 = 視錐台 / 2 = 背面(Day 64b で追加)
    uint instanceCount; // 体の数(Day 65a で追加。Day 64b では詰め物だった席。cull.comp が範囲の外を捨てる)

    // ---- カリングの目(Day 64b で追加)----
    // 描く目(viewProj / cameraPos)とは別に持つ。F キーで止めると、描く目だけが動いて
    // 「どこが捨てられたか」を横から見られる。
    vec4 cullPos;       // xyz = カリングの目の位置
    vec4 frustum[6];    // 視錐台の6面。dot(xyz, p) + w >= 0 が内側(Camera.FrustumPlanes)

    // ---- 体ごとのカリング(Day 65a で追加)----
    vec4 meshSphere;    // 形全体の境界の球。xyz = 中心、w = 半径(模型の座標。MeshData.ComputeBoundingSphere)

    // ---- 隠れたものを捨てる(Day 65c で追加)----
    uvec4 screen;       // x, y = 画面の幅と高さ(画素)、z = Hi-Z の段の数、w は未使用
} frame;

// ---- 何番目のパスの一覧を読むか(Day 65c で追加。プッシュ定数)---------------------------
// 2パスでは、早いパスと遅いパスで**別々の一覧**を使う(1本のバッファの前半と後半)。
// 描く命令ごとに「一覧の何番目から読むか」を C# が vkCmdPushConstants で渡す。
// 1パスのとき(と早いパス)は 0、遅いパスは体の数。
// uniform buffer と違って、**描く命令の合間に値を変えられる**(命令の列に値が埋め込まれる)のがプッシュ定数の強み。
layout(push_constant) uniform Pass
{
    uint listOffset;
} pass;

// ---- 1: 頂点(32 バイト。GpuVertex と同じ並び)---------------------------------------
// 頂点の道はこれを「頂点バッファ」として読み、メッシュの道は「ストレージバッファ」として読む。
struct Vertex
{
    vec4 position;      // xyz だけ使う(w は境界合わせ)
    vec4 normal;        // 同じく xyz だけ
};

layout(std430, set = 0, binding = 1) readonly buffer Vertices { Vertex vertices[]; };

// ---- 2: 体ごとの置き方(模型 → 世界)---------------------------------------------------
layout(std430, set = 0, binding = 2) readonly buffer Instances { mat4 instances[]; };

// ---- 3〜5: メッシュレット(メッシュの道だけが読む)-------------------------------------
struct Meshlet
{
    uint vertexOffset;      // meshletVertices の中での先頭
    uint triangleOffset;    // meshletTriangles の中での先頭 = 描く順での三角形の通し番号
    uint vertexCount;
    uint triangleCount;
    vec4 sphere;            // 境界の球(Day 64b)。xyz = 中心、w = 半径(模型の座標)
    vec4 cone;              // 法線の円錐(Day 64b)。xyz = 軸、w = 半角の sin(使えないときは 1)
};

layout(std430, set = 0, binding = 3) readonly buffer Meshlets { Meshlet meshlets[]; };

// 局所番号 → 元の頂点番号。
layout(std430, set = 0, binding = 4) readonly buffer MeshletVertices { uint meshletVertices[]; };

// 三角形1枚 = 局所番号3つを 8 ビットずつ詰めたもの(i0 | i1 << 8 | i2 << 16)。
layout(std430, set = 0, binding = 5) readonly buffer MeshletTriangles { uint meshletTriangles[]; };

// ---- 6: 三角形 → メッシュレット(画素シェーダが色分けに使う)----------------------------
layout(std430, set = 0, binding = 6) readonly buffer TriangleMeshlets { uint triangleMeshlet[]; };

// ---- 7: カリングの数え上げ(Day 64b で追加)---------------------------------------------
// **書き込むバッファを頂点シェーダから見える場所で宣言すると**、別の機能
// (vertexPipelineStoresAndAtomics)が要ることになるので、書くシェーダ(meshlet.task と cull.comp)だけが宣言する。
// Day 64b では meshlet.task の中にあった。Day 65c で cull.comp も書くようになったので、ここへ移して定義で切り替える。
// C# 側(MeshRenderer)が毎フレーム 0 に戻し、描き終わったら読む。
#if defined(CULL_INSTANCES) || defined(TASK_STAGE)
layout(std430, set = 0, binding = 7) buffer CullCounters
{
    uint frustumCulled;     // 視錐台の外で捨てたメッシュレット(1パス / 遅いパス)
    uint backfaceCulled;    // 丸ごと裏向きで捨てたメッシュレット(同上)
    uint visible;           // 生き残ったメッシュレット(同上)
    uint occluded;          // 隠れていて捨てたメッシュレット(Day 65c。遅いパスだけ)
    uint earlyDrawn;        // 早いパスで描いたメッシュレット(Day 65c)
    uint lateDrawn;         // 遅いパスで描き足したメッシュレット(Day 65c)
    uint occludedInstances; // 隠れていて丸ごと捨てた体(Day 65c。cull.comp の遅いパス)
    uint frustumInstances;  // 視錐台の外で丸ごと捨てた体(Day 65c。cull.comp の遅いパス)
} counters;
#endif

// ---- 8: 描く体の番号の一覧(Day 65a で追加)---------------------------------------------
// 描く側(頂点・タスク・メッシュ)は「何番目に描く体か」をこの一覧で**本当の体の番号**に引き直す。
//   頂点の道   : instances[visibleInstances[gl_InstanceIndex]]
//   メッシュの道: instances[visibleInstances[gl_WorkGroupID.y]]
// 一覧を書くのは、GPU で選ぶときは cull.comp、CPU で選ぶとき(と選ばないとき)は C# の側(CmdUpdateBuffer)。
// **書くのは cull.comp だけ**なので、binding 7 と同じ理由で、書ける宣言は cull.comp の中だけにする
// (cull.comp は CULL_INSTANCES を定義してからこのファイルを読む)。
#ifdef CULL_INSTANCES
layout(std430, set = 0, binding = 8) writeonly buffer VisibleInstances { uint visibleInstances[]; };
#else
layout(std430, set = 0, binding = 8) readonly buffer VisibleInstances { uint visibleInstances[]; };

// 何番目に描く体か → 本当の体の番号(Day 65c で関数にした)。
// 遅いパスの一覧は、いちばん上のビットに「前のフレームでも見えていた」の印を載せている(cull.comp)。
// 印は落として番号だけを返す。
uint drawnInstance(uint i)
{
    return visibleInstances[pass.listOffset + i] & 0x7FFFFFFFu;
}

// 前のフレームでも見えていた体か(Day 65c)。遅いパスのタスクシェーダだけが使う。
bool drawnInstanceWasVisible(uint i)
{
    return (visibleInstances[pass.listOffset + i] >> 31) != 0u;
}
#endif

// ---- タスクシェーダ → メッシュシェーダへの荷物(Day 64b で追加)---------------------------
// タスクのワークグループ1つが 32 個のメッシュレットを調べ、生き残ったものの番号を詰めて渡す。
// 両方の段が同じ形で宣言する(taskPayloadSharedEXT)。132 バイト。
struct TaskPayload
{
    uint instance;          // どの体か(本当の体の番号。Day 65a から、ワークグループの y を一覧で引き直したもの)
    uint meshlets[32];      // 生き残ったメッシュレットの番号。前から詰めてある
};

// 球が視錐台の外にあるか。6枚の面のどれか1枚の「完全に外側」にあれば外。
// (角のあたりで「どの面の外でもないのに視錐台に入っていない」球は捨て損なうが、捨てすぎることは無い)
// Day 64b では meshlet.task の中にあった。Day 65a で cull.comp も使うのでここへ移した。
// 同じ式が C# の Camera.IsOutside にもある(CPU で体を選ぶとき用)。
bool outsideFrustum(vec3 center, float radius)
{
    for (int i = 0; i < 6; i++)
    {
        if (dot(frame.frustum[i].xyz, center) + frame.frustum[i].w < -radius)
        {
            return true;
        }
    }

    return false;
}

#ifdef LATE
// ---- 12: Hi-Z(Day 65c で追加)----------------------------------------------------------
// 早いパスの深度を畳んだもの(DepthPyramid)。1画素 = その範囲でいちばん奥の深度。
// サンプラーは使わず texelFetch で読む(GL_EXT_samplerless_texture_functions。読むシェーダの頭で有効にする)。
layout(set = 0, binding = 12) uniform texture2D depthPyramid;

// 球が、早いパスで描いた物の後ろに**確実に隠れているか**(Day 65c。要点3)。
//
// 1. 球を囲む立方体の8つの角を、描く目の画面に投影する → 画面の上の箱と、いちばん手前の深度
// 2. 箱の差し渡しが Hi-Z の1画素(2^(段+1) 画素)に収まる段を選ぶ → 箱は多くても 2x2 画素にかかる
// 3. その 2x2 画素のいちばん奥の深度より、球のいちばん手前がさらに奥なら「隠れている」
//
// 立方体は球より大きいので、箱は少し大きく、深度は少し手前に出る。どちらも「隠れていない」側に倒れる(安全側)。
// 球をきっちり投影する式もある(改造課題1)が、ここは読んで分かる形を採った。
bool occluded(vec3 center, float radius)
{
    vec2 lo = vec2(1.0e30);
    vec2 hi = vec2(-1.0e30);
    float nearest = 1.0;
    for (int i = 0; i < 8; i++)
    {
        vec3 corner = center + radius * vec3(
            (i & 1) != 0 ? 1.0 : -1.0,
            (i & 2) != 0 ? 1.0 : -1.0,
            (i & 4) != 0 ? 1.0 : -1.0);
        vec4 clip = frame.viewProj * vec4(corner, 1.0);

        // 角が目の後ろ(または手前の切り口より手前)にかかると、投影が裏返って箱が壊れる。判定しない(安全側)。
        if (clip.w <= 0.0 || clip.z < 0.0)
        {
            return false;
        }

        vec3 ndc = clip.xyz / clip.w;
        lo = min(lo, ndc.xy);
        hi = max(hi, ndc.xy);
        nearest = min(nearest, ndc.z);
    }

    // NDC(−1〜1)→ 画素。Vulkan は NDC の y = −1 が画面の上(行 0)。深度の画像の行の並びと同じ向き。
    vec2 size = vec2(frame.screen.xy);
    vec2 pmin = clamp((lo * 0.5 + 0.5) * size, vec2(0.0), size - 1.0);
    vec2 pmax = clamp((hi * 0.5 + 0.5) * size, vec2(0.0), size - 1.0);

    // 段 L の1画素は、画面の 2^(L+1) 画素四方を受け持つ(各段のいちばん端の画素だけは、切り捨てで余った分も受け持つ。
    // depth_reduce.comp)。箱の差し渡し以上になる最小の段を選ぶ。
    float extent = max(pmax.x - pmin.x, pmax.y - pmin.y);
    int level = clamp(int(ceil(log2(max(extent, 1.0)))) - 1, 0, int(frame.screen.z) - 1);
    float texel = exp2(float(level + 1));

    ivec2 levelSize = textureSize(depthPyramid, level);
    ivec2 t0 = min(ivec2(floor(pmin / texel)), levelSize - 1);
    ivec2 t1 = min(ivec2(floor(pmax / texel)), levelSize - 1);

    // 箱がかかる Hi-Z の画素(ふつうは 2x2 以下)のうち、いちばん奥の深度。
    float farthest = 0.0;
    for (int y = t0.y; y <= t1.y; y++)
    {
        for (int x = t0.x; x <= t1.x; x++)
        {
            farthest = max(farthest, texelFetch(depthPyramid, ivec2(x, y), level).r);
        }
    }

    // 深度テストは「より小さいものが勝つ」(Less)。球のいちばん手前が、範囲のいちばん奥よりも奥なら、
    // 球のどの画素も、すでに描いてある物に負ける。等しいときは捨てない(安全側)。
    return nearest > farthest;
}
#endif

// 整数から、それらしくばらけた色を作る。隣り合う番号でも似ない色になるよう、ビットをよく混ぜる
// (「PCG ハッシュ」と呼ばれる定番の混ぜ方)。
vec3 hashColor(uint n)
{
    uint state = n * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    word = (word >> 22u) ^ word;
    return vec3(word & 0xFFu, (word >> 8u) & 0xFFu, (word >> 16u) & 0xFFu) / 255.0 * 0.75 + 0.25;
}
