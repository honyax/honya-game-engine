#version 430 core

// ============================================================
//  Day 63a: 法線を線にして見せる
// ============================================================
//
// **三角形1枚を受け取って、線分を4本出す**。ジオメトリシェーダの「こんにちは世界」。
//
//   3本 … 頂点法線(頂点シェーダが法線行列で回したもの)。青 → 水色
//   1本 … 面法線(**3点の外積からここで作る**)。橙 → 黄
//
// <b>面法線がこの段でしか作れない</b>ところが肝。
// 頂点シェーダは自分の頂点1個しか見えないので、隣の2点が分からず外積が取れない。
// 画素シェーダなら dFdx / dFdy で近い値は出せるが、あれは**画面の上での傾き**であって
// 「この三角形の向き」そのものではない。3点まとめて受け取る段が初めてそれを持つ。
//
// <b>入る図形と出る図形の種類は別でよい</b>。ここは triangles を受けて line_strip を出す。
// 面を消して線だけ出す、点を受けて面を出す、といった取り替えが効くのがこの段の自由度で、
// そのぶん「何個出るか」が実行するまで決まらない(素通しの .geom のコメント参照)。

layout (triangles) in;

// **max_vertices は出しうる頂点の総数**(図形の数ではない)。
// 線分1本につき2頂点 × 4本 = 8。ここを小さく書くと、超えたぶんは**黙って捨てられる**
// (エラーにならない。GL_MAX_GEOMETRY_OUTPUT_VERTICES を超えるとリンクで落ちる)。
layout (line_strip, max_vertices = 8) out;

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

/// 線の長さ(m)。
uniform float uLength;

/// 面に埋もれないよう、線を少しだけ手前へ引く量(クリップ空間の z を w の割合で)。
uniform float uDepthBias;

/// 面法線(橙)を出すか。
uniform int uShowFaceNormal;

void emitVertex(vec3 worldPosition, vec4 color)
{
    OUT.worldPosition = worldPosition;
    OUT.normal = vec3(0.0, 1.0, 0.0);
    OUT.color = color;
    OUT.texCoord = vec2(0.0);

    vec4 clip = uViewProjection * vec4(worldPosition, 1.0);

    // **手前へ引く**。線の根元は面のちょうど上にあるので、
    // そのまま描くと深度が同じ値の取り合いになり、線が虫食いになる
    // (Day 33 のシャドウアクネとまったく同じ「深度の刻みの細かさ」の話)。
    // w を掛けているのは、クリップ空間の z が w に比例する量だから——
    // こうしておくと、遠くでも近くでも同じだけ「画面の奥行きで」引ける。
    clip.z -= uDepthBias * clip.w;

    gl_Position = clip;
    EmitVertex();
}

void emitLine(vec3 from, vec3 direction, vec4 rootColor, vec4 tipColor)
{
    emitVertex(from, rootColor);
    emitVertex(from + (direction * uLength), tipColor);

    // **1本ごとに切る**。切らないと 頂点1→2→3→… が1本の折れ線になり、
    // 法線の先から次の頂点へ戻る線が余分に描かれる。
    EndPrimitive();
}

void main()
{
    // --- 頂点法線(青 → 水色)---
    for (int i = 0; i < 3; ++i)
    {
        emitLine(
            IN[i].worldPosition,
            normalize(IN[i].normal),
            vec4(0.10, 0.25, 1.00, 1.0),
            vec4(0.45, 0.95, 1.20, 1.0));
    }

    if (uShowFaceNormal == 0)
    {
        return;
    }

    // --- 面法線(橙 → 黄)---
    //
    // **巻き順がそのまま向きになる**。cross(b - a, c - a) は
    // 「a → b → c が反時計回りに見える側」を向くので、
    // 背面カリングが消す向き(Day 10)と同じ約束の上に乗っている。
    // 巻き順を間違えたメッシュは、ここで面法線が内側を向くので**目で分かる**。
    vec3 a = IN[0].worldPosition;
    vec3 b = IN[1].worldPosition;
    vec3 c = IN[2].worldPosition;

    vec3 faceNormal = cross(b - a, c - a);

    // 潰れた三角形(面積 0)は外積が 0 になる。normalize すると NaN が出るので逃がす。
    if (dot(faceNormal, faceNormal) < 1e-20)
    {
        return;
    }

    emitLine(
        (a + b + c) / 3.0,
        normalize(faceNormal),
        vec4(1.30, 0.45, 0.05, 1.0),
        vec4(1.60, 1.35, 0.20, 1.0));
}
