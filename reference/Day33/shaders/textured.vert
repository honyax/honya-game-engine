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

// 光の目から見たビュー射影行列(Day 33)。**深度パスに送るものとまったく同じ行列**。
// ShadowMap が1か所で作って、深度パスと本描画の両方へ配っている。
// ここがずれると影が丸ごとずれるので、2か所で組み立ててはいけない。
uniform mat4 uLightSpaceMatrix;

// --- マテリアルごとに変わる(Material.Apply が設定) ---
uniform vec2 uUvScale;

out vec2 vTexCoord;
out vec4 vColor;
out vec3 vNormal;

// 世界座標(Day 33)。今日は影の座標を作るのに使うだけだが、
// **点光源の方向・視線ベクトル・フォグ**など、これから要るものが軒並みここから始まる。
out vec3 vWorldPos;

// 光の座標系での位置(Day 33)。**頂点シェーダで写しておく**のが定石。
//
// 画素シェーダで vWorldPos に uLightSpaceMatrix を掛けても同じ結果になるが、
// 行列とベクトルの積を**頂点の数だけ**やるか**画素の数だけ**やるかの差になる。
// 立方体1個なら 24 回 対 数万回。線形な変換は補間しても同じ値になるので、
// 頂点側でやってラスタライザに運ばせるのが正しい
// (法線のように「補間で長さが崩れる」たぐいの問題も、位置には無い)。
out vec4 vLightSpacePos;

void main()
{
    // モデル → ビュー → 投影 の順に適用する。
    // GLSL は列ベクトル規約なので、**適用したい順とは逆に左から書く**。
    // Day 14 の要点4で見たとおり、C# 側(行ベクトル)の
    //   model * view * projection
    // と、この行は同じ変換を表している。
    // 世界座標は影にもライティングにも要るので、先に出しておく(Day 33)。
    vec4 worldPos = uModel * vec4(aPosition, 1.0);

    gl_Position = uViewProjection * worldPos;

    vWorldPos = worldPos.xyz;
    vLightSpacePos = uLightSpaceMatrix * worldPos;

    vTexCoord = aTexCoord * uUvScale;
    vColor = aColor;

    // ここで正規化しないのは、**補間で長さが崩れる**から。
    // 頂点間で線形補間された法線は短くなるので、
    // 受け取ったフラグメント側で正規化し直すのが正しい。
    vNormal = uNormalMatrix * aNormal;
}
