#version 460
#extension GL_EXT_ray_tracing : require

// ---------------------------------------------------------------------------------------
// **拡散の球**に当たったときに呼ばれる closest-hit シェーダ(SBT の hit グループ 0)。
//
// Day 62b の trace.comp では、材質の違いは `if (hit.kind == 1)` という**1つの分岐**だった。
// RT パイプラインではそれが**シェーダの分割**になる。分岐が消えて、代わりに
// 「どのシェーダを呼ぶか」を SBT が決める。
//
// 何が嬉しいのか:
//   - 材質を増やしてもシェーダが太らない(分岐が増えない = レジスタを食い合わない)
//   - 材質ごとに**別のリソース**(テクスチャ、定数)を SBT のレコードに埋められる
//   - ドライバが材質ごとに呼び出しをまとめられる(コヒーレンスが上がる)
// 何が面倒なのか:
//   - **どのシェーダが呼ばれるかがコードを読んでも分からない**。SBT の並びを見ないと追えない
//   - ジオメトリを材質で分けて BLAS に入れる、という制約が場面の作り方に効いてくる
// ---------------------------------------------------------------------------------------

layout(set = 0, binding = 2) uniform accelerationStructureEXT topLevel;

#include "common.glsl"

layout(location = 0) rayPayloadInEXT RayPayload payload;
layout(location = 1) rayPayloadEXT bool shadowed;

// sphere.rint が書いた法線を受け取る。
hitAttributeEXT vec3 hitNormal;

void main() {
    int index = sphereIndex(gl_GeometryIndexEXT, gl_PrimitiveID);
    vec3 albedo = spheres[index].albedoKind.xyz;

    if (pc.mode == 1) {
        payload.color = hitNormal * 0.5 + 0.5;
        return;
    }

    vec3 position = gl_WorldRayOriginEXT + gl_HitTEXT * gl_WorldRayDirectionEXT;

    shadowed = true;
    traceRayEXT(topLevel,
                gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsSkipClosestHitShaderEXT,
                0xFF, 0, 1, 1,
                position + hitNormal * 1e-3, 1e-4, SunDirection, 1e30, 1);

    payload.color = shadeSurface(albedo, hitNormal, shadowed);
}
