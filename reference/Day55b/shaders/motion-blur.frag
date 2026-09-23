#version 330 core

// **モーションブラー**(Day 55)。McGuire, Hennessy, Bukowski, Osman 2012
// "A Reconstruction Filter for Plausible Motion Blur" の再構成フィルタ。
//
// 動いている点は、シャッターが開いている間に線(尾)に広がる——本来は「点が道のりに沿って光を配る」処理(scatter)。
// 被写界深度と同じく裏返して、「自分の画素に、誰の尾が掛かっているか」を集める(gather)。
//
// 読む向きは**近所の升目でいちばん速い速度**(BlurField)。自分の画素が止まっていても(背景)、
// すぐ隣を速い物が横切れば、その尾が自分の上まで伸びてくる。自分の速度だけで読むと、尾は物の輪郭の中に閉じ込められる。
//
// 読んだ点を混ぜてよいかは、手前・奥と、それぞれの尾の長さで決める(下の3つの場合)。

in vec2 vUv;

out vec4 FragColor;

uniform sampler2D uScene;      // ぼかす前の絵
uniform sampler2D uField;      // 奥行きと錯乱円の地図(x が奥行き [m])
uniform sampler2D uVelocity;   // 速度(UV、前 → 今)。Day 54 の MotionVectors
uniform sampler2D uTiles;      // 近所の升目(xy がいちばん速い速度 [画素/フレーム])
uniform vec2 uScreenSize;      // 画面の大きさ [画素]
uniform float uShutterOpen;    // 1フレームのうちシャッターが開いている割合(MotionBlur.OpenFraction)
uniform float uMaxRadius;      // 尾の半分の長さの上限 [画素](BlurField.MaxRadius)
uniform int uTileSize;
uniform int uUseTiles;         // 0 なら自分の速度だけで読む
uniform float uSoftDepth;      // 手前・奥をなめらかに決める幅 [m]
uniform int uDebug;            // 1 なら升目の速度を色で見せる

// 尾に沿って読む点の数(真ん中の1点は自分)。MotionBlur.SampleCount と同じ数にしておくこと。
const int SampleCount = 15;

/// 1フレームぶんの動き [画素] → **尾の半分の長さ** [画素]。上限で切る(向きは保つ)。
/// 尾は今の位置を真ん中にして前後に伸ばすので、中心から片側へは半分。
vec2 HalfExtent(vec2 pixelsPerFrame)
{
    // (変数の名前を length や distance にしないのは、GLSL の組み込み関数を隠してしまうため。)
    vec2 extent = pixelsPerFrame * (uShutterOpen * 0.5);
    float size = length(extent);

    return size > uMaxRadius ? extent * (uMaxRadius / size) : extent;
}

/// a が b より手前(か、ほぼ同じ奥行き)なら 1。a が b より uSoftDepth 以上奥なら 0。
float InFront(float a, float b)
{
    return clamp(1.0 - ((a - b) / uSoftDepth), 0.0, 1.0);
}

/// 半分の長さ extent の尾が、中心から gap だけ離れた点に掛かる度合い。**尾の先ほど薄い**(円錐)。
float Cone(float gap, float extent)
{
    return clamp(1.0 - (gap / extent), 0.0, 1.0);
}

/// 同じく、ただし尾の中は一様(円柱)。両方がぼけている場合に使う。
float Cylinder(float gap, float extent)
{
    return 1.0 - smoothstep(0.95 * extent, 1.05 * extent, gap);
}

/// 画素ごとに違う 0〜1 の値(Jimenez 2014 の interleaved gradient noise)。
/// 15 点を等間隔に読むと、明るい物が 15 枚の写しになって縞に見える。読む位置を画素ごとに少しずらすと、縞が細かいざらつきに変わる。
float Noise(vec2 pixel)
{
    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    ivec2 last = textureSize(uScene, 0) - 1;
    vec3 color = texelFetch(uScene, pixel, 0).rgb;

    vec2 own = HalfExtent(texelFetch(uVelocity, pixel, 0).rg * uScreenSize);
    vec2 dominant = uUseTiles == 1
        ? HalfExtent(texelFetch(uTiles, pixel / uTileSize, 0).xy)
        : own;

    if (uDebug == 1)
    {
        // 升目の速度。**止まっているところは元の絵を暗く**、横に速いほど赤、縦に速いほど緑。上限で振り切れる。
        // 動く物のまわりの升目まで光るのが、「尾が輪郭の外まで伸びる」ための仕掛け。
        FragColor = vec4((color * 0.25) + (vec3(abs(dominant) / uMaxRadius, 0.0) * 2.0), 1.0);
        return;
    }

    // **近所に動く物が無ければ何もしない**。止まった絵では、ここでほぼ全部の画素が帰る(代金がほとんど掛からない)。
    if (length(dominant) < 0.5)
    {
        FragColor = vec4(color, 1.0);
        return;
    }

    float depthX = texelFetch(uField, pixel, 0).x;

    // 自分の尾の半分の長さ。止まっていても 0.5 画素とみなす(自分の画素の幅の半分。0 で割らないため)。
    float extentX = max(length(own), 0.5);

    // **自分の色から始める**。重みは 1 / 尾の長さ——長い尾ほど、1画素あたりに置いていく光は薄い。
    float weight = 1.0 / extentX;
    vec3 sum = color * weight;

    float jitter = Noise(gl_FragCoord.xy) - 0.5;

    for (int i = 0; i < SampleCount; i++)
    {
        // 真ん中は自分(もう足した)。
        if (i == SampleCount / 2)
        {
            continue;
        }

        // 近所でいちばん速い向きに沿って、-1〜+1 を等間隔に。全体を画素ごとに少しずらす。
        float t = mix(-1.0, 1.0, (float(i) + jitter + 1.0) / float(SampleCount + 1));
        ivec2 other = clamp(ivec2(floor(gl_FragCoord.xy + (dominant * t))), ivec2(0), last);
        float gap = length(vec2(other - pixel));

        vec3 colorY = texelFetch(uScene, other, 0).rgb;
        float depthY = texelFetch(uField, other, 0).x;
        float extentY = max(length(HalfExtent(texelFetch(uVelocity, other, 0).rg * uScreenSize)), 0.5);

        float front = InFront(depthY, depthX);   // 読んだ点が手前
        float back = InFront(depthX, depthY);    // 読んだ点が奥

        // 混ぜてよい度合い(McGuire 2012 の3つの場合)。
        //   1. 手前の物が、自分の尾で自分(X)の上まで伸びている  → 手前の尾が掛かる
        //   2. 奥の物は、自分(X)の尾が伸びている範囲だけ見える  → 自分が動いているので、後ろが透けて見える
        //   3. 両方とも動いていて、互いの尾の中にいる          → 同じ物の中の尾(円柱で重ねる)
        // 奥で動く物の尾は、手前で止まっている物(1 も 2 も 0)には掛からない——手前が隠しているから。
        float amount =
            (front * Cone(gap, extentY))
            + (back * Cone(gap, extentX))
            + (Cylinder(gap, extentY) * Cylinder(gap, extentX) * 2.0);

        weight += amount;
        sum += colorY * amount;
    }

    FragColor = vec4(sum / weight, 1.0);
}
