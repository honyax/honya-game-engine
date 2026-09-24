// 5本のシェーダ(cull.comp / scene.vert / meshlet.task / meshlet.mesh / shade.frag)が #include する共通部分。
// cull.comp は Day 65a で増えた。
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
} frame;

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
// タスクシェーダだけが書くので、宣言は meshlet.task の中に置いてある。
// **書き込むバッファを頂点シェーダから見える場所で宣言すると**、別の機能
// (vertexPipelineStoresAndAtomics)が要ることになるので、ここには書かない。

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

// 整数から、それらしくばらけた色を作る。隣り合う番号でも似ない色になるよう、ビットをよく混ぜる
// (「PCG ハッシュ」と呼ばれる定番の混ぜ方)。
vec3 hashColor(uint n)
{
    uint state = n * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    word = (word >> 22u) ^ word;
    return vec3(word & 0xFFu, (word >> 8u) & 0xFFu, (word >> 16u) & 0xFFu) / 255.0 * 0.75 + 0.25;
}
