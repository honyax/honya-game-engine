#version 430 core

// ============================================================
//  Day 63a: 面を押し出す(面ごとの計算)
// ============================================================
//
// **三角形を、自分の向いている方へ押し出し、重心へ縮める**。
// 絵としては「モデルが破裂する」定番の演出だが、見たいのはそこではなく
// <b>「面ごとに1つだけ決まる値」を使って描ける</b>という一点。
//
// 押し出す向きも、平らな陰影も、ここで作る**面法線**で決まる。
// 頂点シェーダで同じことをしようとすると、
//   - 押し出す向き … 頂点法線で代用するしかない(丸い形では面がばらけず膨らむだけ)
//   - 平らな陰影   … 頂点ごとに法線が違うので、面の中で色が変わってしまう
// となり、どちらも「近いが違うもの」にしかならない。
//
// なお平らな陰影だけが目的なら、今は <b>flat 補間子</b>(provoking vertex の値をそのまま配る)
// で済むし、そちらのほうがずっと安い。**GS が要るのは、頂点を動かすほう**。

layout (triangles) in;
layout (triangle_strip, max_vertices = 3) out;

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

/// 面法線の向きへ押し出す距離(m)。
uniform float uExplode;

/// 重心へ寄せる割合(0 = そのまま、1 = 点に潰れる)。三角形の切れ目を見せるためのもの。
uniform float uShrink;

uniform vec4 uColorScale;

/// 三角形ごとに色を変えるか。**gl_PrimitiveIDIn を使う**。
uniform int uColorByPrimitive;

/// 面ごとの色。gl_PrimitiveIDIn(何枚目の三角形か)から適当に散らす。
///
/// <b>この番号はこの段にしか来ない</b>。頂点シェーダには「何枚目の三角形か」という
/// 概念そのものが無い(同じ頂点が何枚もの三角形に使われるので、決めようがない)。
/// 画素シェーダには gl_PrimitiveID として届くが、それは
/// **ジオメトリシェーダがあれば、この段が gl_PrimitiveID に入れた値**になる。
vec3 primitiveColor(int id)
{
    // 黄金比で回すと、隣の番号どうしがいちばん離れた色になる。
    float hue = fract(float(id) * 0.61803399);
    vec3 wheel = abs(fract(hue + vec3(0.0, 0.6666667, 0.3333333)) * 6.0 - 3.0) - 1.0;
    return clamp(wheel, 0.0, 1.0);
}

void main()
{
    vec3 a = IN[0].worldPosition;
    vec3 b = IN[1].worldPosition;
    vec3 c = IN[2].worldPosition;

    vec3 faceNormal = cross(b - a, c - a);
    float lengthSquared = dot(faceNormal, faceNormal);

    // 潰れた三角形はそのまま出す(押し出す向きが決まらない)。
    vec3 direction = lengthSquared < 1e-20 ? vec3(0.0) : normalize(faceNormal);
    // **変数名に centroid は使えない**。GLSL では補間の修飾子として予約されている
    // (centroid in / centroid out)。「三角形の中心」のつもりで書くと
    // 「型の後ろに修飾子が来た」と読まれ、見当のつかないエラーになる——
    // OpenGL does not allow 'centroid' after a type specifier。
    vec3 middle = (a + b + c) / 3.0;

    for (int i = 0; i < 3; ++i)
    {
        vec3 world = mix(IN[i].worldPosition, middle, uShrink) + (direction * uExplode);

        OUT.worldPosition = world;

        // **面法線をそのまま渡す**。3頂点とも同じ値なので、
        // 補間しても面の中で変わらない = 平らな陰影になる。
        OUT.normal = direction;

        OUT.color = uColorByPrimitive == 1
            ? vec4(primitiveColor(gl_PrimitiveIDIn), IN[i].color.a) * uColorScale
            : IN[i].color * uColorScale;
        OUT.texCoord = IN[i].texCoord;

        gl_Position = uViewProjection * vec4(world, 1.0);
        EmitVertex();
    }

    EndPrimitive();
}
