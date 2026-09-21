#version 460

// 頂点シェーダ。**比べるための従来の道**。
//
// 入力の回路(Input Assembler)が索引を1つ読むたびに、1回ずつ呼ばれる。
// 自分がどの三角形のどの角なのかは知らないし、隣の頂点も見えない。
// ただし同じ頂点が何度も索引に出てくると、GPU は**直前に変換した結果を覚えていて**使い回す
// (post-transform cache)。何回呼ばれたかは HUD の「頂点シェーダ」の数で見える。
#include "common.glsl"

// 頂点バッファから入力の回路が取り出して渡してくれる(GraphicsPipeline.cs の属性の宣言どおり)。
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec3 vWorld;
layout(location = 2) flat out uint vInstance;

void main()
{
    // gl_InstanceIndex が「何体目か」。vkCmdDrawIndexed の instanceCount ぶん繰り返される。
    mat4 model = instances[gl_InstanceIndex];

    // メッシュシェーダと**まったく同じ式・同じ順**で計算する。
    // 順を変える(viewProj * model を先に掛ける)と丸め方が変わって、2つの道の絵が最下位ビットでずれる。
    vec4 world = model * vec4(inPosition, 1.0);
    gl_Position = frame.viewProj * world;
    vNormal = mat3(model) * inNormal;
    vWorld = world.xyz;
    vInstance = uint(gl_InstanceIndex);
}
