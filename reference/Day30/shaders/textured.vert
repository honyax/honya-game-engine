#version 330 core

// 属性の番号は Vertex 構造体のフィールド順(位置 → UV → 色)と対応している。
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;

// --- フレームごとに変わる(カメラが設定。1フレームに1回) ---
// ビュー行列と投影行列を掛け合わせたもの。オブジェクトが何個あっても同じ値なので、
// 描画のたびに送り直す必要が無い。uniform は**プログラムに紐づく状態**で、
// glUseProgram を呼び直しても値は消えないため、フレーム頭で1回設定すれば足りる。
uniform mat4 uViewProjection;

// --- オブジェクトごとに変わる(描画のたびに設定) ---
// モデル行列。その物体を「世界のどこに、どんな向き・大きさで置くか」。
uniform mat4 uModel;

// --- マテリアルごとに変わる(Material.Apply が設定) ---
uniform vec2 uUvScale;

out vec2 vTexCoord;
out vec4 vColor;

void main()
{
    // モデル → ビュー → 投影 の順に適用する。
    // GLSL は列ベクトル規約なので、**適用したい順とは逆に左から書く**。
    // Day 14 の要点4で見たとおり、C# 側(行ベクトル)の
    //   model * view * projection
    // と、この行は同じ変換を表している。
    gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0);

    vTexCoord = aTexCoord * uUvScale;
    vColor = aColor;
}
