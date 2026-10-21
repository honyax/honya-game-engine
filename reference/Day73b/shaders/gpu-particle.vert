#version 430 core

// ============================================================
//  Day 57: SSBO をそのまま頂点として読む
// ============================================================
//
// **この頂点属性は、コンピュートが書いた SSBO そのもの**。
// 同じバッファオブジェクトを GL_SHADER_STORAGE_BUFFER と GL_ARRAY_BUFFER の
// 両方に挿してある(GpuParticles.CreateVertexArray)。
// OpenGL のバッファは**中身に型を持たない**ただのバイト列で、
// 「どう読むか」は挿した口が決める——だから写し替えは1バイトも要らない。
//
// 並びは particle-sim.comp の struct Particle と1バイトずつ同じ。
//   0..11  position (vec3)
//   12..15 age      (float)
//   16..27 velocity (vec3)
//   28..31 lifetime (float)
layout (location = 0) in vec3 aPosition;
layout (location = 1) in float aAge;
layout (location = 2) in vec3 aVelocity;
layout (location = 3) in float aLifetime;

// **ビューと射影を分けて渡す**(Day 49 の particle.vert は掛けたものを1本で渡していた)。
// 点の大きさを「カメラからの距離」で決めるのに、ビュー空間の z が要るため。
uniform mat4 uView;
uniform mat4 uProjection;

// 画素の大きさへの換算係数。C# 側で 画面の高さ / (2 * tan(fov/2)) を入れてある。
// これに「粒の大きさ(m) / 距離(m)」を掛けると画素数になる——透視投影そのものの式。
uniform float uPointScale;

uniform float uSize;
uniform int uMode;

out vec4 vColor;

void main()
{
    vec4 viewPosition = uView * vec4(aPosition, 1.0);
    gl_Position = uProjection * viewPosition;

    // 死んでいる粒(この1フレームだけ起きうる)は、大きさ 0 にして消す。
    // **discard より安い**——画素シェーダに入る前に落ちる。
    if (aAge >= aLifetime)
    {
        gl_PointSize = 0.0;
        vColor = vec4(0.0);
        return;
    }

    float t = aLifetime > 0.0 ? clamp(aAge / aLifetime, 0.0, 1.0) : 1.0;
    float speed = length(aVelocity);

    // **gl_PointSize は画素の数**(ワールドの大きさではない)。
    // 距離に反比例させないと、遠ざかっても粒が小さくならない。
    //
    // 1 画素を下限にしてあるのは、0 にすると点が消えて
    // 「遠くの粒だけ抜ける」という、原因の分かりにくい見え方になるため。
    gl_PointSize = clamp(uPointScale * uSize / max(0.05, -viewPosition.z), 1.0, 64.0);

    vec3 color;
    float alpha;

    if (uMode == 0)
    {
        // 噴水: 生まれたては白く熱く、落ちるにつれて橙 → 赤へ。
        color = mix(vec3(1.6, 1.2, 0.6), vec3(1.2, 0.18, 0.05), t * t);
        alpha = 1.0 - (t * t);
    }
    else if (uMode == 1)
    {
        // 引力: 速さで色を変える。**軌道の速い側(近日点)が白く光る**。
        float heat = clamp(speed / 9.0, 0.0, 1.0);
        color = mix(vec3(0.12, 0.35, 1.2), vec3(1.5, 1.1, 1.6), heat);
        alpha = 0.35 + (0.65 * heat);
    }
    else
    {
        // 渦: 高さで色を変える。立ち上がるほど冷える(煙)。
        float height = clamp(aPosition.y / 7.0, 0.0, 1.0);
        color = mix(vec3(0.20, 0.85, 0.75), vec3(0.15, 0.20, 0.55), height);
        alpha = (1.0 - (t * t)) * 0.7;
    }

    // **1.0 を超えた色はブルームに拾われる**(Day 31)。
    // 粒は加算合成でシーンの RGBA16F に載るので、そこがそのままにじみになる。
    vColor = vec4(color, alpha);
}
