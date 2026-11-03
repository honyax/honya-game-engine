#version 430 core

// ============================================================
//  Day 63a: 点を板にする(増やすほうの使い方)
// ============================================================
//
// **頂点1個を受け取って、カメラを向いた四角形を出す**。1 → 4 の増幅。
// GL_POINTS で描いたメッシュの頂点が、そのまま板の中心になる。
//
// <b>これが GS のいちばん有名な使い道</b>だった。板を CPU で作ると
// 1粒につき4頂点を毎フレーム書き出して送ることになる(Day 49 のパーティクルがそれ)。
// 点を送って GPU に広げさせれば、送るのは1粒1頂点で済む。
//
// <b>ただし今はもっと安い手が2つある</b>。
//   - <b>点スプライト</b>(gl_PointSize + gl_PointCoord)。Day 57 の 100 万粒はこちら。
//     板を回せない・大きさが画素単位・実装ごとに上限がある、という制限と引き換えに、
//     ラスタライザが直接広げるので段が1つも増えない
//   - <b>インスタンシング</b>。四角形1枚を gl_InstanceID で何万回も描く
// 「増幅が目的なら GS」という時代は終わっていて、**残っているのは
// 『図形の中身を見て判断する』ほう**(このフォルダの silhouette がそれ)。

layout (points) in;
layout (triangle_strip, max_vertices = 4) out;

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

uniform mat4 uView;
uniform mat4 uViewProjection;

/// 板の1辺の長さ(m)。
uniform float uSize;

/// <param name="corner">板の四隅。-1〜+1 で渡す。</param>
void emitCorner(vec3 center, vec3 right, vec3 up, vec2 corner, vec4 color)
{
    vec3 world = center + (right * corner.x) + (up * corner.y);

    OUT.worldPosition = world;
    OUT.normal = normalize(cross(right, up));
    OUT.color = color;

    // 板の中の位置(0〜1)。丸く抜くのに画素シェーダが使う。
    OUT.texCoord = (corner * 0.5) + 0.5;

    gl_Position = uViewProjection * vec4(world, 1.0);
    EmitVertex();
}

void main()
{
    // **ビュー行列の中に、カメラの右と上が入っている**。
    // ビュー行列はワールドをカメラの座標系へ回す行列なので、その回転部分の
    // **行**がカメラの軸そのもの(列ではない——転置になっている)。
    // GLSL の mat4 は列優先で添字を引くので、uView[列][行]。
    // 「1行目」を取るには uView[0][0], uView[1][0], uView[2][0] を並べる。
    //
    // カメラが回ればここも回るので、板は常に画面と正対する(ビルボード)。
    vec3 right = vec3(uView[0][0], uView[1][0], uView[2][0]) * (uSize * 0.5);
    vec3 up = vec3(uView[0][1], uView[1][1], uView[2][1]) * (uSize * 0.5);

    vec3 center = IN[0].worldPosition;

    // 法線を色にする。どの頂点がどこへ行ったかが見えるので、
    // 「板1枚 = 元のメッシュの頂点1つ」が絵から読める。
    vec4 color = vec4((normalize(IN[0].normal) * 0.5) + 0.5, 1.0) * IN[0].color;

    // **triangle_strip の順番は Z 字**。左下 → 右下 → 左上 → 右上 と出すと、
    // (左下,右下,左上) と (右下,左上,右上) の2枚になって四角形が埋まる。
    // 時計回りに4点を並べると、2枚目が裏返って砂時計になる。
    emitCorner(center, right, up, vec2(-1.0, -1.0), color);
    emitCorner(center, right, up, vec2(1.0, -1.0), color);
    emitCorner(center, right, up, vec2(-1.0, 1.0), color);
    emitCorner(center, right, up, vec2(1.0, 1.0), color);

    EndPrimitive();
}
