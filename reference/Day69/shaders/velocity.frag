#version 330 core

// **速度を書く**(Day 54)。単位は UV(画面の幅・高さを 1 とした量)、向きは「前 → 今」。
//
// TAA は履歴を「今の UV − 速度」の場所から読む。画素の単位で持つと
// 読む側が画面の大きさを知る必要が出るので、UV で持つ。
// NDC(-1〜1)は幅が 2 なので、差を半分にすると UV の差になる。

in vec4 vCurrentClip;
in vec4 vPreviousClip;

out vec2 FragVelocity;

void main()
{
    vec2 current = vCurrentClip.xy / vCurrentClip.w;
    vec2 previous = vPreviousClip.xy / vPreviousClip.w;

    // **camera-velocity.frag と同じ1行**。約束が1文字でも違うと、
    // 動く物の画素だけ速度の向きか大きさが食い違い、そこだけ履歴が別の場所から来る。
    FragVelocity = (current - previous) * 0.5;
}
