#version 330 core

in vec3 vDirection;

out vec4 FragColor;

uniform samplerCube uEnvironment;

/// どのミップを出すか。**事前フィルタの段を目で見るため**(Ctrl+Alt+4)。
/// 0 なら原寸。粗さごとにどれだけぼけているかが分かる。
uniform float uMip;

/// 明るさの倍率。IBL の強さと合わせて動かす。
uniform float uIntensity;

void main()
{
    // **トーンマップは通さない**……のではなく、通る。
    // 空も後処理(PostProcess)の中で描いているので、
    // 露出とトーンマップと、必要ならブルームまで一緒にかかる。
    //
    // これは狙ってそうしている。空だけ別扱いにすると、
    // **太陽の周りだけブルームが出ない**というちぐはぐな絵になる。
    // 「シーンに出るものは全部 HDR で持って、出口で1回だけ畳む」が
    // Day 31 で引いた線で、空もその中に居る。
    vec3 color = textureLod(uEnvironment, normalize(vDirection), uMip).rgb * uIntensity;

    FragColor = vec4(color, 1.0);
}
