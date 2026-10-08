#version 330 core

// **ガウスぼかし、ただし縦横に分けて2回**(分離型フィルタ)。
//
// 2次元のガウス関数は、実は縦方向と横方向の掛け算に分解できる。
//   G(x, y) = G(x) * G(y)
// だから「横だけぼかす」→「縦だけぼかす」の2回で、
// 2次元のぼかしとまったく同じ結果になる。
//
// 手間は劇的に違う。半径 N のぼかしを素直に書くと 1 ピクセルあたり
//   2次元まとめて … (2N+1)^2 回のサンプリング
//   縦横に分けて … (2N+1) * 2 回
// N=4 なら 81 回が 18 回。**画面全体で 4.5 倍**の差になる。
//
// 効くのは「分解できる」フィルタだけで、
// 中央値フィルタやバイラテラルフィルタは分解できない。
// ガウスがどこでも使われるのは、この性質があるからでもある。

in vec2 vUv;

out vec4 FragColor;

uniform sampler2D uSource;

/// 1ステップぶんの移動量。横パスなら (1/幅, 0)、縦パスなら (0, 1/高さ)。
/// **どちらのパスかをこの1本の uniform で表す**ので、シェーダは1本で済む。
uniform vec2 uDirection;

// ガウスの重み(σ ≒ 2.0、片側4タップ)。合計が 1 になるように正規化してある。
// 合計が 1 からずれると、ぼかすたびに画面全体が明るく(暗く)なっていく。
const float Weights[5] = float[](0.2270270, 0.1945946, 0.1216216, 0.0540541, 0.0162162);

void main()
{
    vec3 result = texture(uSource, vUv).rgb * Weights[0];

    for (int i = 1; i < 5; i++)
    {
        vec2 offset = uDirection * float(i);

        // 中心から左右(上下)対称に読む。ガウスは偶関数なので重みは共通。
        result += texture(uSource, vUv + offset).rgb * Weights[i];
        result += texture(uSource, vUv - offset).rgb * Weights[i];
    }

    FragColor = vec4(result, 1.0);
}
