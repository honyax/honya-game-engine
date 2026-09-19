#version 460
#extension GL_EXT_ray_tracing : require

// ---------------------------------------------------------------------------------------
// **金属の球**に当たったときに呼ばれる closest-hit シェーダ(SBT の hit グループ 1)。
//
// **今日いちばんの見どころは再帰**。このシェーダが traceRayEXT を呼ぶと、
// 反射した先でまたこのシェーダが呼ばれることがある——**シェーダが自分を呼び直す**。
//
// Day 62a・62b のコンピュート版では、GPU に再帰が無いので for ループにして、
// 反射率を throughput に掛け算で貯めていた。RT パイプラインは**ハードウェアがスタックを持つ**
// ので、素直な再帰で書ける。
//
//   コンピュート版: for (bounce...) { throughput *= f0; origin = ...; continue; }
//   RT パイプライン: payload.color = f0 * (自分を呼び直した結果)
//
// 深さの上限は**パイプラインを作るときに宣言する**(maxPipelineRayRecursionDepth)。
// 宣言より深く潜ると未定義動作で、シェーダ側でも depth を数えて止める義務がある。
// ---------------------------------------------------------------------------------------

layout(set = 0, binding = 2) uniform accelerationStructureEXT topLevel;

#include "common.glsl"

layout(location = 0) rayPayloadInEXT RayPayload payload;

// **反射の光線の荷物**。入ってきた荷物(location 0)とは別の番号でなければならない。
// 再帰しても、呼び出しごとに別の実体になる。
layout(location = 2) rayPayloadEXT RayPayload reflected;

// 影の光線の荷物。打ち切ったときに拡散として塗るのに使う。
layout(location = 1) rayPayloadEXT bool shadowed;

hitAttributeEXT vec3 hitNormal;

void main() {
    int index = sphereIndex(gl_GeometryIndexEXT, gl_PrimitiveID);
    vec3 f0 = spheres[index].albedoKind.xyz;

    if (pc.mode == 1) {
        payload.color = hitNormal * 0.5 + 0.5;
        return;
    }

    vec3 position = gl_WorldRayOriginEXT + gl_HitTEXT * gl_WorldRayDirectionEXT;
    vec3 direction = reflect(gl_WorldRayDirectionEXT, hitNormal);
    vec3 origin = position + hitNormal * 1e-3;

    // これ以上潜れないなら打ち切る。
    // **ここを忘れると、宣言した深さを超えて潜って未定義動作になる**。
    //
    // 打ち切り方は**コンピュート版とそろえてある**(trace.comp の for ループは
    // bounce == MaxBounces で金属を拡散として塗る)。そろえないと、
    // 4回目の反射が当たった画素だけ色が食い違って、3つの探し方を突き合わせられなくなる。
    if (payload.depth <= 0) {
        shadowed = true;
        traceRayEXT(topLevel,
                    gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT | gl_RayFlagsSkipClosestHitShaderEXT,
                    0xFF, 0, 1, 1,
                    origin, 1e-4, SunDirection, 1e30, 1);
        payload.color = shadeSurface(f0, hitNormal, shadowed);
        return;
    }

    // 床は加速構造に入っていないので、反射の光線でも tMax を床までにしておく(raygen と同じ)。
    float tMax = 1e30;
    float floorT = floorDistance(origin, direction);
    if (floorT > 1e-4) {
        tMax = floorT;
    }

    reflected.color = vec3(0.0);
    reflected.depth = payload.depth - 1;

    traceRayEXT(topLevel, gl_RayFlagsOpaqueEXT, 0xFF, 0, 1, 0,
                origin, 1e-4, direction, tMax, 2);

    payload.color = f0 * reflected.color;
}
