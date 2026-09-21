// 4本のシェーダ(scene.vert / meshlet.task / meshlet.mesh / shade.frag)が #include する共通部分。
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
    uint padding;

    // ---- カリングの目(Day 64b で追加)----
    // 描く目(viewProj / cameraPos)とは別に持つ。F キーで止めると、描く目だけが動いて
    // 「どこが捨てられたか」を横から見られる。
    vec4 cullPos;       // xyz = カリングの目の位置
    vec4 frustum[6];    // 視錐台の6面。dot(xyz, p) + w >= 0 が内側(Camera.FrustumPlanes)
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

// ---- タスクシェーダ → メッシュシェーダへの荷物(Day 64b で追加)---------------------------
// タスクのワークグループ1つが 32 個のメッシュレットを調べ、生き残ったものの番号を詰めて渡す。
// 両方の段が同じ形で宣言する(taskPayloadSharedEXT)。132 バイト。
struct TaskPayload
{
    uint instance;          // どの体か(ワークグループの y)
    uint meshlets[32];      // 生き残ったメッシュレットの番号。前から詰めてある
};

// 整数から、それらしくばらけた色を作る。隣り合う番号でも似ない色になるよう、ビットをよく混ぜる
// (「PCG ハッシュ」と呼ばれる定番の混ぜ方)。
vec3 hashColor(uint n)
{
    uint state = n * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    word = (word >> 22u) ^ word;
    return vec3(word & 0xFFu, (word >> 8u) & 0xFFu, (word >> 16u) & 0xFFu) / 255.0 * 0.75 + 0.25;
}
