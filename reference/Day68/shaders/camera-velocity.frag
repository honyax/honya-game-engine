#version 330 core

// **カメラが動いたぶんの速度を、深度から全画面に書く**(Day 54)。速度バッファの1段目。
//
// 止まっている物の画素は、「その点が前のフレームで画面のどこに写っていたか」が
// **カメラの動きだけで決まる**。点の世界の位置は深度から戻せる(Day 52 のディファードの位置の復元と同じ発想)ので、
// 前のフレームのビュー射影行列で写し直せば、前の画面の位置が出る。
//
// 行列は2本を CPU で1本に掛けておく(MotionVectors.Render の uReprojection)。
//   今の画面の点(NDC + 深度)──[ずらした VP の逆]──▶ 世界 ──[前のフレームの VP]──▶ 前のクリップ座標
// 画素ごとに逆行列を作るわけにはいかないし、2回掛けるより1回のほうが安い。
//
// 自分で動いた物(キャラクター・転がる球・回る箱)はここでは分からない——深度には
// 「今どこにあるか」しか入っていないので、前のフレームに居た場所は出てこない。
// そういう画素は、2段目(velocity.vert / velocity.frag)が上から描き直す。

in vec2 vUv;

out vec2 FragVelocity;

uniform sampler2D uDepth;

// 今の画面の点 → 前のフレームのクリップ座標。
uniform mat4 uReprojection;

// このフレームのずらし(NDC)。今の点の「ずらす前の」位置を出すのに要る。
uniform vec2 uJitter;

void main()
{
    // 深度バッファの値(0〜1)。**gl_FragCoord で1画素ぶんを引く**。
    // vUv で texture() を引くと隣と混ざることがあり、深度の平均には意味が無い(Day 33 と同じ理由)。
    float depth = texelFetch(uDepth, ivec2(gl_FragCoord.xy), 0).r;

    // この画素の中心を NDC(-1〜1)と深度(-1〜1)で表す。
    // 空(深度 1.0)は遠クリップ面の点になる。無限遠ではないが、100m 先ならカメラの平行移動による
    // ずれは小さく、回転だけならぴったり合う。
    vec4 ndc = vec4((vUv * 2.0) - 1.0, (depth * 2.0) - 1.0, 1.0);

    // **同次座標のまま掛ける**。途中の「世界の点」を w で割って正規化しなくてよいのは、
    // 最後に w で割る(透視除算)ときに、途中で掛かった倍率ごと消えるから。
    vec4 previousClip = uReprojection * ndc;
    vec2 previous = previousClip.xy / previousClip.w;

    // 今の位置も「ずらす前」にそろえる。**ずらしは動きではない**——
    // そろえないと、止まった絵の速度が毎フレーム半画素ずつ揺れて出てくる。
    vec2 current = ndc.xy - uJitter;

    // 単位は UV(画面の幅・高さを 1)。NDC は幅が 2 なので半分にする。向きは「前 → 今」。
    // **velocity.frag と同じ1行**(自己チェックが文字列で見張っている)。
    FragVelocity = (current - previous) * 0.5;
}
