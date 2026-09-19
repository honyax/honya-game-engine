#version 460
#extension GL_EXT_ray_tracing : require

// ---------------------------------------------------------------------------------------
// 一次光線(と反射光線)が球に当たらなかったときに呼ばれる miss シェーダ。
//
// **床の陰影もここでやる**のが今日の設計(raygen の説明を参照)。
// 床は無限の平面なので BLAS に入れられず、代わりに「光線の tMax を床までにしておいて、
// miss が呼ばれたら tMax を見て床か空かを決める」という形にしてある。
//
// miss シェーダから traceRayEXT を呼べる(影の光線)のも、RT パイプラインの素直なところ。
// ---------------------------------------------------------------------------------------

layout(set = 0, binding = 2) uniform accelerationStructureEXT topLevel;

#include "common.glsl"

layout(location = 0) rayPayloadInEXT RayPayload payload;

// 影の光線の荷物。**入ってきた荷物(location 0)とは別の番号**でなければならない。
layout(location = 1) rayPayloadEXT bool shadowed;

void main() {
    vec3 direction = gl_WorldRayDirectionEXT;

    // tMax が 1e30 のままなら「床が無い方向へ飛んだ」= 空。
    if (gl_RayTmaxEXT >= 1e29) {
        payload.color = sky(direction);
        return;
    }

    // 床に当たった。位置は始点 + tMax * 向き。
    vec3 position = gl_WorldRayOriginEXT + gl_RayTmaxEXT * direction;
    vec3 normal = vec3(0.0, 1.0, 0.0);

    // 影の光線。**当たったかどうかだけ**でよいので、
    //   TerminateOnFirstHit … 最初に確定したら打ち切る
    //   SkipClosestHitShader … 当たっても closest-hit を呼ばない(色は要らない)
    // を立てる。当たらなければ shadow.rmiss(missIndex = 1)が呼ばれて false になる。
    //
    // **true で初期化しておく**のが要点。当たった場合は何も呼ばれないので、
    // 初期値がそのまま答えになる。
    shadowed = true;
    traceRayEXT(topLevel,
                gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsSkipClosestHitShaderEXT,
                0xFF, 0, 1, 1,
                position + normal * 1e-3, 1e-4, SunDirection, 1e30, 1);

    payload.color = pc.mode == 1
        ? normal * 0.5 + 0.5
        : shadeSurface(floorAlbedo(position), normal, shadowed);
}
