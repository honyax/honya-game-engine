#version 430 core

// 四角形の中の1画素で、ガウス関数の濃さを計算して塗る(要点3)。
// C# の ProjectedSplat.AlphaAt と同じ式。

in vec2 vOffset;
flat in vec3 vConic;
flat in vec4 vColor;

uniform int uMode;

out vec4 fragColor;

void main()
{
    // 重なりの数: 走ったら 1 を足す(ブレンドは One, One)。捨てる前に数えるのは、
    // 「濃さが 0 で結局何も塗らなかった画素」も、画素シェーダを走らせた手間には入るから。
    if (uMode == 4)
    {
        fragColor = vec4(1.0, 0.0, 0.0, 0.0);
        return;
    }

    // 中心の点: 半径 1.5 画素の丸を不透明に。
    if (uMode == 1)
    {
        if (dot(vOffset, vOffset) > 2.25)
        {
            discard;
        }

        fragColor = vec4(vColor.rgb, 1.0);
        return;
    }

    // −½ dᵀ Σ'⁻¹ d。ガウス関数の指数。
    float power = -0.5 * (vConic.x * vOffset.x * vOffset.x + vConic.z * vOffset.y * vOffset.y) - vConic.y * vOffset.x * vOffset.y;
    if (power > 0.0)
    {
        discard;
    }

    // マハラノビス距離 m(「σ 何個ぶん離れているか」)は √(−2 × power)。
    // くっきりした楕円: 2σ の内側(m < 2、つまり power > −2)を、ぼかさずに、その楕円体の不透明度で一様に塗る。
    // 不透明度まで 1 にすると、学習済みのシーンでは手前の大きくて薄い楕円体が画面を埋めてしまい、何も見えない。
    if (uMode == 2)
    {
        if (power < -2.0)
        {
            discard;
        }

        float uniformAlpha = min(0.99, vColor.a);
        fragColor = vec4(vColor.rgb * uniformAlpha, uniformAlpha);
        return;
    }

    // 輪郭: m = 2 の線。線の太さは、隣の画素との m の差(fwidth)で決めると、楕円の大きさによらず約1画素になる。
    if (uMode == 3)
    {
        float m = sqrt(-2.0 * power);
        if (abs(m - 2.0) > fwidth(m))
        {
            discard;
        }

        fragColor = vec4(vColor.rgb, 1.0);
        return;
    }

    float alpha = min(0.99, vColor.a * exp(power));
    if (alpha < 1.0 / 255.0)
    {
        discard;
    }

    // 乗算済みアルファ(色に α を掛けて出す)。ブレンドは One, OneMinusSrcAlpha。
    fragColor = vec4(vColor.rgb * alpha, alpha);
}
