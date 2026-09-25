#version 430 core

// ============================================================
//  Day 63b: 制御シェーダ — 「どれだけ細かく割るか」だけを決める
// ============================================================
//
// **この段は形を作らない**。決めるのは <b>gl_TessLevelOuter[4]</b> と <b>gl_TessLevelInner[2]</b> の6つの数だけで、
// 実際に割るのは次の<b>固定機能のテッセレータ</b>(プログラムできない回路)。
// 割った結果の点1つずつに評価シェーダ(tess.tese)が呼ばれる。
//
//   頂点シェーダ  … 制御点1つにつき1回(四角パッチなら4回)
//   **制御シェーダ … 出力する制御点1つにつき1回**(ここも4回。gl_InvocationID で何番目かが分かる)
//   テッセレータ  … 固定機能。6つの数を見て、0〜1 の升目の上に点と三角形を作る
//   評価シェーダ  … **割った点1つにつき1回**(レベル 64 なら1パッチで 4,225 回)
//
// <b>レベルを決めるのはパッチごと1回でよい</b>のに、この段が4回走るのは
// 「制御点を作り変える」使い方(ベジエの制御点を計算する等)のため。
// 今日は作り変えないので、<c>gl_InvocationID == 0</c> の1回だけがレベルを書く。
//
// ------------------------------------------------------------
//  亀裂(クラック)の話 — 今日いちばんの落とし穴
// ------------------------------------------------------------
//
// 隣り合う2枚のパッチは辺を共有している。その辺に置く点の数(= その辺の outer レベル)が
// 両側で違うと、**辺の上の点が一致せず、隙間が開く**。
// パッチは互いを知らないので、<b>「隣と同じ答えになる決め方」を選ぶしかない</b>。
//
//   **中点から決める**(uMatchEdges = 1)… 共有する辺の中点は両側で同じ点なので、必ず同じ値になる
//   **中心から決める**(uMatchEdges = 0)… パッチの中心は両側で違う点。**距離が違えばレベルも違う → 亀裂**
//
// 直し方が「共有するものから決める」の一言に尽きるのが、この問題の面白いところ。

layout (vertices = 4) out;

in Control
{
    vec3 worldPosition;
    vec2 texCoord;
    vec4 color;
    vec3 normal;
} IN[];

out Control
{
    vec3 worldPosition;
    vec2 texCoord;
    vec4 color;
    vec3 normal;
} OUT[];

uniform vec3 uEyePosition;

/// 固定のレベル(距離で決めないとき)。
uniform float uLevel;

/// 距離でレベルを決めるか。
uniform int uDistanceLod;

/// 辺のレベルを中点から決めるか(0 にすると中心から決めて亀裂が出る)。
uniform int uMatchEdges;

/// 距離レベルの基準。この距離でちょうど uLevel になる。
uniform float uLodDistance;

uniform float uMaxLevel;

/// カメラからの距離でレベルを決める。**近いほど細かい**。
///
/// 距離に反比例させるのは、画面に写る大きさが距離に反比例するから——
/// 「割ったあとの三角形が、画面でだいたい同じ大きさになる」ようにしたい。
///
/// <b>そのあと 2 の冪へ落とす</b>のが実務でよくやる形。理由は2つ。
///   1. **段を粗く刻むと、カメラが少し動いただけでレベルが揺れない**
///      (連続だと毎フレーム 12.3 → 12.4 → 12.2 と変わり、equal_spacing では
///       そのたびに頂点が一斉に動いてチラつく)
///   2. 段の数が決まっていると、隣のレベルを推測しやすい
///
/// **代わりに、隣り合うパッチのレベルが 2 倍違いうる**ようになる。
/// だから「辺を隣と揃える」の効きがはっきり出る——
/// 揃えなければ、片方が 8 等分・もう片方が 16 等分の辺が並ぶ。
float levelAt(vec3 worldPosition)
{
    float distance = max(0.001, length(uEyePosition - worldPosition));
    float raw = clamp(uLevel * uLodDistance / distance, 1.0, uMaxLevel);

    return exp2(floor(log2(raw)));
}

void main()
{
    // **制御点はそのまま通す**。今日は作り変えない。
    OUT[gl_InvocationID].worldPosition = IN[gl_InvocationID].worldPosition;
    OUT[gl_InvocationID].texCoord = IN[gl_InvocationID].texCoord;
    OUT[gl_InvocationID].color = IN[gl_InvocationID].color;
    OUT[gl_InvocationID].normal = IN[gl_InvocationID].normal;

    // **レベルはパッチごとに1回**。4回とも同じ値を書いても動くが、
    // 「1回でよいものを4回書いている」と読まれるので分けてある。
    if (gl_InvocationID != 0)
    {
        return;
    }

    if (uDistanceLod == 0)
    {
        gl_TessLevelOuter[0] = uLevel;
        gl_TessLevelOuter[1] = uLevel;
        gl_TessLevelOuter[2] = uLevel;
        gl_TessLevelOuter[3] = uLevel;
        gl_TessLevelInner[0] = uLevel;
        gl_TessLevelInner[1] = uLevel;
        return;
    }

    // 四角パッチの4隅は (u,v) = (0,0) (1,0) (0,1) (1,1) の順に入れてある(TessellationLab が並べる)。
    vec3 p00 = IN[0].worldPosition;
    vec3 p10 = IN[1].worldPosition;
    vec3 p01 = IN[2].worldPosition;
    vec3 p11 = IN[3].worldPosition;

    // **外側のレベルは辺ごと**。GL の約束では
    //   Outer[0] = u が 0 の辺(p00 - p01)
    //   Outer[1] = v が 0 の辺(p00 - p10)
    //   Outer[2] = u が 1 の辺(p10 - p11)
    //   Outer[3] = v が 1 の辺(p01 - p11)
    // ここを1つでも取り違えると、**特定の向きの辺にだけ亀裂が出る**という読みにくい壊れ方をする。
    vec3 center = (p00 + p10 + p01 + p11) * 0.25;

    vec3 e0 = uMatchEdges == 1 ? (p00 + p01) * 0.5 : center;
    vec3 e1 = uMatchEdges == 1 ? (p00 + p10) * 0.5 : center;
    vec3 e2 = uMatchEdges == 1 ? (p10 + p11) * 0.5 : center;
    vec3 e3 = uMatchEdges == 1 ? (p01 + p11) * 0.5 : center;

    gl_TessLevelOuter[0] = levelAt(e0);
    gl_TessLevelOuter[1] = levelAt(e1);
    gl_TessLevelOuter[2] = levelAt(e2);
    gl_TessLevelOuter[3] = levelAt(e3);

    // **内側は外側からはみ出さない値に**。内側だけ細かくしても、
    // 辺の点の数は外側で決まっているので意味が無い(中だけ細かい歪んだ割り方になる)。
    gl_TessLevelInner[0] = max(gl_TessLevelOuter[1], gl_TessLevelOuter[3]);
    gl_TessLevelInner[1] = max(gl_TessLevelOuter[0], gl_TessLevelOuter[2]);
}
