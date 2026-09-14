#version 330 core

// 属性の番号は Vertex 構造体のフィールド順(位置 → UV → 色 → 法線)と対応している。
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;

// Day 32 で足した。**末尾に足す**ことで 0〜2 の番号を動かさずに済ませている。
layout (location = 3) in vec3 aNormal;

// --- フレームごとに変わる(カメラが設定。1フレームに1回) ---
// ビュー行列と投影行列を掛け合わせたもの。オブジェクトが何個あっても同じ値なので、
// 描画のたびに送り直す必要が無い。uniform は**プログラムに紐づく状態**で、
// glUseProgram を呼び直しても値は消えないため、フレーム頭で1回設定すれば足りる。
uniform mat4 uViewProjection;

// --- オブジェクトごとに変わる(描画のたびに設定) ---
// モデル行列。その物体を「世界のどこに、どんな向き・大きさで置くか」。
uniform mat4 uModel;

// 法線を世界空間へ運ぶための行列(Day 32)。**モデル行列をそのまま使えない**。
//
// 位置は uModel で正しく運べるが、法線は「向き」なので事情が違う。
// 非一様スケール(x だけ 2 倍など)をかけると、
// **面は傾くのに法線は同じだけ傾かない**——むしろ逆向きに傾く。
// 正しい変換は「モデル行列の左上 3x3 の逆行列の転置」で、
// これを法線行列と呼ぶ。CPU 側で作って送る(Program.Draw)。
//
// 一様スケールと回転だけなら uModel の 3x3 と一致するので、
// 「動いているから正しい」が言えない類の話。立方体を潰すと差が出る。
uniform mat3 uNormalMatrix;

// --- マテリアルごとに変わる(Material.Apply が設定) ---
uniform vec2 uUvScale;

out vec2 vTexCoord;
out vec4 vColor;
out vec3 vNormal;

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

    // ここで正規化しないのは、**補間で長さが崩れる**から。
    // 頂点間で線形補間された法線は短くなるので、
    // 受け取ったフラグメント側で正規化し直すのが正しい。
    vNormal = uNormalMatrix * aNormal;
}
