#version 330 core

// **自分で動いた物の速度を描く**(Day 54)。速度バッファの2段目。
//
// 1段目(camera-velocity.frag)は深度から「カメラが動いたぶん」を全画面に書いた。
// 止まっている物はそれで正しい。キャラクターや転がる球のように**自分で動いた物**だけ、
// ここで上から描き直す。「前のフレームのモデル行列と関節行列」が要るのはそのため。
//
// 本描画(textured.vert)と並べると、要るものがとても少ない。
//   - 頂点は位置と、スキニングの関節・重みだけ。UV も法線も接空間も要らない
//   - 行列は「今」と「前」の2組
layout (location = 0) in vec3 aPosition;
layout (location = 5) in vec4 aJoints;
layout (location = 6) in vec4 aWeights;

// **ずらした**ビュー射影行列。gl_Position だけはこれで作る。
//
// 本描画とまったく同じ場所・同じ深度に描かないと、深度テスト(LEQUAL)で
// 「手前に何も無いのに落ちる」「奥にあるのに通る」が起きる。
// だから textured.vert と同じ行列を、同じ掛け算の順で使う。
uniform mat4 uViewProjection;

// **ずらす前**の、今のフレームと前のフレームのビュー射影行列。速度はこの2つで測る。
// ずらした行列で比べると、毎フレーム変わるずらしの差が「動き」に化ける
// (Camera.UnjitteredProjectionMatrix のコメント)。
uniform mat4 uCurrentViewProjection;
uniform mat4 uPreviousViewProjection;

uniform mat4 uModel;
uniform mat4 uPreviousModel;

// textured.vert と同じ約束(AnimationPlayer.MaxJoints)。
//
// **前のフレームの関節も同じ数だけ要る**ので、この頂点シェーダの uniform は mat4 が 128 個
// (float 2048 個)になる。OpenGL 3.3 が保証するのは float 1024 個で、その倍。
// textured.vert の時点ですでに保証の上に乗っていた(Day 41 のコメント)のと同じで、
// 実機の余裕(この GPU は 4096)で動いている。
const int MAX_JOINTS = 64;
uniform mat4 uJoints[MAX_JOINTS];
uniform mat4 uPreviousJoints[MAX_JOINTS];
uniform int uSkinned;

// **クリップ座標のまま渡す**。w で割るのは画素シェーダで。
//
// ラスタライザの補間はクリップ座標に対して(透視補正つきで)正しく行われるが、
// 割ったあとの NDC は画面の上で線形に並ばない。頂点で割ってから補間すると、
// 手前から奥へ長く伸びた三角形(地面など)の真ん中で速度がずれる。
out vec4 vCurrentClip;
out vec4 vPreviousClip;

// 4本の関節行列を重みで混ぜる。説明は textured.vert の SkinMatrix にある。
// **同じ式が、本描画・影・SSAO に続いて4本目**になった(計画書「今日残した歪み」)。
mat4 SkinMatrix()
{
    return (uJoints[int(aJoints.x)] * aWeights.x)
        + (uJoints[int(aJoints.y)] * aWeights.y)
        + (uJoints[int(aJoints.z)] * aWeights.z)
        + (uJoints[int(aJoints.w)] * aWeights.w);
}

// 前のフレームの姿勢で同じことをする。
//
// **配列を引数で渡す1つの関数にしない**のは、GLSL の配列は値渡しだから。
// mat4 が 64 個(4KB)を頂点ごとに写すことになる。同じ形の関数を2つ書くほうが安い。
mat4 PreviousSkinMatrix()
{
    return (uPreviousJoints[int(aJoints.x)] * aWeights.x)
        + (uPreviousJoints[int(aJoints.y)] * aWeights.y)
        + (uPreviousJoints[int(aJoints.z)] * aWeights.z)
        + (uPreviousJoints[int(aJoints.w)] * aWeights.w);
}

void main()
{
    vec4 localPos = vec4(aPosition, 1.0);
    vec4 previousLocalPos = localPos;

    if (uSkinned == 1)
    {
        previousLocalPos = PreviousSkinMatrix() * localPos;
        localPos = SkinMatrix() * localPos;
    }

    // textured.vert とまったく同じ順(スキン → モデル → ビュー射影)。
    vec4 worldPos = uModel * localPos;
    vec4 previousWorldPos = uPreviousModel * previousLocalPos;

    gl_Position = uViewProjection * worldPos;

    vCurrentClip = uCurrentViewProjection * worldPos;
    vPreviousClip = uPreviousViewProjection * previousWorldPos;
}
