#version 330 core

in vec2 vUv;

out vec4 FragColor;

uniform sampler2D uDepthMap;

// **絵が実際に入っている深度の範囲**。ShadowMap.Begin が計算して送る。
//
// 光の正射影は near = r*0.5、far = r*3.5 に取ってあるので、
// 球の中身(手前 r 〜 奥 3r)は 0〜1 のうち中央 2/3 にしか入っていない。
// 生のまま出すと**全体が中間の灰色**になって形が読み取れないので、
// その範囲を 0〜1 に引き伸ばして見る。
//
// 「深度バッファの絵が真っ白/真っ黒に見える」の大半はこれが原因で、
// 焼けていないのではなく**使っている範囲が狭い**だけ、ということが多い。
uniform float uDepthMin;
uniform float uDepthMax;

void main()
{
    float depth = texture(uDepthMap, vUv).r;

    // **平行光源なので線形補正が要らない**のがここの気持ちよさ。
    // 透視投影の深度は 1/z に比例して分布するため、見るときは線形へ戻す必要があるが、
    // 正射影の深度は距離にそのまま比例している。引き伸ばすだけでよい。
    float value = clamp((depth - uDepthMin) / max(uDepthMax - uDepthMin, 1e-5), 0.0, 1.0);

    // 手前が明るくなるように反転する。近いものほど白い、のほうが直感に合う。
    FragColor = vec4(vec3(1.0 - value), 1.0);
}
