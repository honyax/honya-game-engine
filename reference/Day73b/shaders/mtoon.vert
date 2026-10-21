#version 330 core

// ============================================================
//  MToon(Day 68)の頂点シェーダ。
//
//  中身は textured.vert とほぼ同じ(スキニング・法線・接線・影の座標)。
//  違うのは1つだけ——**アウトラインの回では、頂点を法線の向きへ押し出す**(背面法)。
//
//    1回目(uOutlinePass = 0)… ふつうに描く。表を描き、裏は捨てる
//    2回目(uOutlinePass = 1)… 法線の向きへ太さぶん膨らませ、**表を捨てて裏だけ**描く
//
//  膨らんだ裏面は、元の形より一回り大きい「裏返しの殻」になる。手前の表面は1回目が描いてあるので、
//  殻のうち元の形からはみ出した縁だけが見える——それが線になる。
//  画像処理(深度や法線の段差を探す)と違い、**ポリゴンを描くだけで済む**ので、
//  ディファードにもフォワードにも、どの後処理にも依存しない。
// ============================================================

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;
layout (location = 3) in vec3 aNormal;
layout (location = 4) in vec4 aTangent;
layout (location = 5) in vec4 aJoints;
layout (location = 6) in vec4 aWeights;

uniform mat4 uViewProjection;
uniform mat4 uView;
uniform mat4 uModel;
uniform mat3 uNormalMatrix;
uniform mat4 uLightSpaceMatrix;

// textured.vert と同じ上限。VRM の 91 本はローダーが 64 本以内に詰めてある(GltfLoader.CompactJoints)。
const int MAX_JOINTS = 64;
uniform mat4 uJoints[MAX_JOINTS];
uniform int uSkinned;

/// アウトラインの回か(1 なら法線の向きへ押し出す)。画素シェーダにも同じ名前がある。
uniform int uOutlinePass;

/// 太さの単位(0 = 無し / 1 = メートル / 2 = 画面の高さに対する割合)。MToon.OutlineMode と同じ並び。
uniform int uOutlineWidthMode;

/// 太さ。
uniform float uOutlineWidthFactor;

/// 太さの掛け算(G)。**頂点シェーダでテクスチャを読む**ので、ミップは選べない(textureLod で 0 段目)。
uniform sampler2D uOutlineWidthMultiplyTexture;

/// tan(画角 / 2)。画面の割合の太さを、その距離でのメートルに直すのに使う。
uniform float uTanHalfFov;

/// UV の拡大(xy)とずらし(zw)。Day 69 の表情(textureTransformBinds)が動かす。ふだんは (1, 1, 0, 0)。
/// **頂点で1回だけ掛ける**ので、全部のテクスチャ(太さのテクスチャも)が同じだけずれる。マットキャップは UV を使わないのでずれない。
uniform vec4 uUvTransform;

out vec2 vTexCoord;
out vec3 vNormal;
out vec3 vWorldPos;
out vec4 vLightSpacePos;
out vec3 vTangent;
out vec3 vBitangent;

mat4 SkinMatrix()
{
    return (uJoints[int(aJoints.x)] * aWeights.x)
        + (uJoints[int(aJoints.y)] * aWeights.y)
        + (uJoints[int(aJoints.z)] * aWeights.z)
        + (uJoints[int(aJoints.w)] * aWeights.w);
}

void main()
{
    vec2 uv = (aTexCoord * uUvTransform.xy) + uUvTransform.zw;
    vec4 localPos = vec4(aPosition, 1.0);
    vec3 localNormal = aNormal;
    vec3 localTangent = aTangent.xyz;

    if (uSkinned == 1)
    {
        mat4 skin = SkinMatrix();
        localPos = skin * localPos;
        mat3 skin3 = mat3(skin);
        localNormal = skin3 * localNormal;
        localTangent = skin3 * localTangent;
    }

    vec4 worldPos = uModel * localPos;
    vec3 worldNormal = normalize(uNormalMatrix * localNormal);

    // --- アウトライン: 法線の向きへ押し出す ---
    //
    // **押し出すのはスキニングの後、世界空間で**。前に押し出すと、関節の行列で太さが伸び縮みする。
    if (uOutlinePass == 1)
    {
        float width = uOutlineWidthFactor * textureLod(uOutlineWidthMultiplyTexture, uv, 0.0).g;

        // 画面の割合なら「その距離で画面の縦半分が何メートルか」を掛ける(three-vrm と同じ式)。
        // 遠いほど太く押し出すので、画面の上では距離によらず同じ太さに見える。
        if (uOutlineWidthMode == 2)
        {
            float viewDepth = -(uView * worldPos).z;
            width *= viewDepth * uTanHalfFov;
        }

        worldPos.xyz += worldNormal * width;
    }

    gl_Position = uViewProjection * worldPos;

    // **線をほんの少し奥へ**。押し出しが 0 の頂点(太さのテクスチャが黒い目の周りなど)は
    // 1回目の面とぴったり同じ深度になり、どちらが勝つかが画素ごとに揺れて、面に線の色が斑に出る。
    if (uOutlinePass == 1)
    {
        gl_Position.z += 1e-6 * gl_Position.w;
    }

    vWorldPos = worldPos.xyz;
    vLightSpacePos = uLightSpaceMatrix * worldPos;
    vTexCoord = uv;
    vNormal = worldNormal;

    // 接線は法線と直交させてから従接線を作る(textured.vert と同じ。グラム・シュミット)。
    vec3 t = normalize(uNormalMatrix * localTangent);
    t = normalize(t - (worldNormal * dot(worldNormal, t)));
    vTangent = t;
    vBitangent = cross(worldNormal, t) * aTangent.w;
}
