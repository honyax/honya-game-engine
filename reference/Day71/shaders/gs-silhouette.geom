#version 430 core

// ============================================================
//  Day 63a: 輪郭を出す(隣を見る使い方)
// ============================================================
//
// **今日の本題**。三角形1枚ではなく、<b>三角形とその3辺の隣</b>を受け取る。
//
//   gl_in[0] --- gl_in[2] --- gl_in[4]   … 真ん中の三角形(0, 2, 4)
//   gl_in[1] / gl_in[3] / gl_in[5]       … 各辺の向こう側の頂点
//
//         1
//        / \
//       0---2
//      / \ / \
//     5---4---3
//
//   辺 (0,2) の隣の三角形 … (0, 1, 2)
//   辺 (2,4) の隣の三角形 … (2, 3, 4)
//   辺 (4,0) の隣の三角形 … (4, 5, 0)
//
// <b>輪郭(シルエット)とは「表の面と裏の面の境目にある辺」</b>。
// 表裏はカメラの位置で決まるので、**毎フレーム変わる**——
// つまり事前に焼いておけない。見る側で決まるものを GPU で出す、というのがこの段の芯。
//
// これはシャドウボリューム(特論 B-12 の前半)の出発点でもある。
// 輪郭の辺を光と反対へ引き伸ばすと「影の柱」ができ、その中かどうかで影を判定する。
// GS が入った頃(2006 年ごろ)、いちばん期待されていた使い道がこれだった。
// 今はシャドウマップ(Day 33)に置き換わっているが、
// **「隣を見ないと決められない量がある」**ことは変わらない。

layout (triangles_adjacency) in;
layout (line_strip, max_vertices = 6) out;

in Varying
{
    vec3 worldPosition;
    vec3 normal;
    vec4 color;
    vec2 texCoord;
} IN[];

out Varying
{
    vec3 worldPosition;
    vec3 normal;
    vec4 color;
    vec2 texCoord;
} OUT;

uniform mat4 uViewProjection;
uniform vec3 uEyePosition;
uniform float uDepthBias;
uniform vec4 uLineColor;

/// この三角形がカメラのほうを向いているか。
///
/// 巻き順から作った法線と「面からカメラへ向かうベクトル」の内積が正なら表。
/// **潰れた三角形(隣が無い辺の目印)は必ず裏として扱う**——
/// 外積が 0 なので内積も 0 になり、> 0 が偽になる。これが下の「境界の辺は必ず出る」の正体。
bool isFrontFacing(vec3 a, vec3 b, vec3 c)
{
    return dot(cross(b - a, c - a), uEyePosition - a) > 0.0;
}

void emitVertex(vec3 worldPosition)
{
    OUT.worldPosition = worldPosition;
    OUT.normal = vec3(0.0, 1.0, 0.0);
    OUT.color = uLineColor;
    OUT.texCoord = vec2(0.0);

    vec4 clip = uViewProjection * vec4(worldPosition, 1.0);

    // 輪郭の線は面のちょうど縁に乗るので、少し手前へ引かないと虫食いになる
    // (gs-normals.geom と同じ理由)。
    clip.z -= uDepthBias * clip.w;

    gl_Position = clip;
    EmitVertex();
}

void emitEdge(vec3 from, vec3 to)
{
    emitVertex(from);
    emitVertex(to);
    EndPrimitive();
}

void main()
{
    vec3 v0 = IN[0].worldPosition;
    vec3 v1 = IN[1].worldPosition;
    vec3 v2 = IN[2].worldPosition;
    vec3 v3 = IN[3].worldPosition;
    vec3 v4 = IN[4].worldPosition;
    vec3 v5 = IN[5].worldPosition;

    // **真ん中が裏を向いていたら何も出さない**。
    // 輪郭の辺は必ず「表の三角形」と「裏の三角形」で共有されているので、
    // 表の側からだけ出せば<b>1本の辺がちょうど1回</b>出る。
    // 両側から出すと、同じ線を2回描くうえに、下の「隣が裏か」の判定が
    // 裏側から見ると逆になって辻褄が合わなくなる。
    if (!isFrontFacing(v0, v2, v4))
    {
        return;
    }

    // **隣の三角形の巻き順に注意**。真ん中が (0,2,4) のとき、
    // 辺 (0,2) を共有する隣は (0,1,2) —— 共有する辺を逆向きに辿る並びになっている。
    // ここを (0,2,1) のように書くと表裏が逆に出て、**輪郭がちょうど裏返る**
    // (形の内側に線が出るので、見ればすぐ分かる壊れ方ではある)。
    if (!isFrontFacing(v0, v1, v2))
    {
        emitEdge(v0, v2);
    }

    if (!isFrontFacing(v2, v3, v4))
    {
        emitEdge(v2, v4);
    }

    if (!isFrontFacing(v4, v5, v0))
    {
        emitEdge(v4, v0);
    }
}
