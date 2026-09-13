#version 330 core

// **TAA の解決(resolve)**(Day 54)。今日の主役。
//
// 毎フレームやっていることは4つだけ。
//   1. 今の絵を読む。カメラは1画素未満ずらして描いてある(Camera.Jitter)
//   2. 前のフレームの結果(履歴)を、この画素が**前に居た場所**から読む(速度で引き戻す)
//   3. 履歴を疑う。今の絵の近所の色から外れていたら、近所の範囲まで引き戻す
//   4. 混ぜる(今の絵を 1 割、履歴を 9 割)。結果がそのまま次のフレームの履歴になる
//
// 1 が毎フレーム「1画素の中の違う点」を運んでくるので、4 で混ぜ続けると、
// 1画素の中を何十点も取った(スーパーサンプリングした)のと同じ値に近づく。
// 2 が無いとカメラが動いた瞬間に絵が流れ、3 が無いと動く物の後ろに影(ゴースト)が残る。
//
// **HDR のまま(トーンマップの前)で走る**。ブルームより前に置いたのは、
// ブルームの元になる明るい点がジッターで揺れると、にじみまで一緒に揺れるから。

in vec2 vUv;

out vec4 FragColor;

// 今のフレーム(シーンバッファ)。ずらして描いてある。
uniform sampler2D uCurrent;

// 前のフレームの結果。**双線形で引ける**(TemporalAA が Linear で作っている)。
uniform sampler2D uHistory;

// 速度(UV 単位、前 → 今)。MotionVectors が描いたもの。
uniform sampler2D uVelocity;

// 深度。近所でいちばん手前の画素を探すのに使う。
uniform sampler2D uDepth;

// 1 / 画面の大きさ(UV で1テクセルぶん)。
uniform vec2 uTexelSize;

// 今の絵をどれだけ混ぜるか。0.1 なら 1 割。**小さいほど滑らかで、変化への追従が遅い**。
uniform float uCurrentWeight;

// 0 なら履歴が無い(最初のフレーム・カットの直後・大きさが変わった直後)。今の絵をそのまま出す。
uniform int uHistoryValid;

// 履歴の疑い方。0: 疑わない / 1: 近所の最小〜最大の箱へ押し込む / 2: 平均 ± 標準偏差の箱へ、中心に向けて切る
uniform int uRectify;

// 分散で切るときの箱の広さ(標準偏差の何倍か)。
uniform float uVarianceGamma;

// 履歴の読み方。0: 双線形 / 1: Catmull-Rom
uniform int uHistoryFilter;

// 1 なら、混ぜる前に「明るさで畳む」(ToneWeigh)。
uniform int uToneWeighted;

// 表示。0: 通常 / 1: 揺れの素(今の絵だけ) / 2: 速度 / 3: 履歴を疑って動かした量
uniform int uDebug;

float MaxComponent(vec3 color)
{
    return max(color.r, max(color.g, color.b));
}

/// **明るさで畳む**(Karis 2014)。混ぜる前に x / (1 + 最大成分) にする。
///
/// HDR のまま混ぜると、太陽の映り込みのような 50 を超える画素が、
/// ジッターで出たり消えたりするたびに近所の平均をまるごと持っていく——
/// 1割しか混ぜなくても 5 足されるので、暗いまわりが白くちらつく。
/// 畳むと、どんな明るさでも 1 未満に収まるので、1画素が勝ちすぎない。
/// **畳んだ世界で混ぜて、最後に戻す**(Unweigh)。
///
/// 代償は偏り。明るい画素ほど軽く数えるので、明るい点の平均は本当の平均より暗くなる。
/// 自己チェックで、ちらつきが減るのと一緒にこの偏りも数字で出している。
vec3 ToneWeigh(vec3 color)
{
    return uToneWeighted == 1 ? color / (1.0 + MaxComponent(color)) : color;
}

vec3 ToneUnweigh(vec3 color)
{
    // 分母が 0 に近づくのは畳んだ値が 1 に近い(もとが極端に明るい)とき。1000 倍で頭打ちにする。
    return uToneWeighted == 1 ? color / max(1.0 - MaxComponent(color), 0.001) : color;
}

/// **YCoCg に回す**。明るさ(Y)と色味(Co・Cg)を分ける。
///
/// 近所の色を「箱」で囲むとき、RGB のままだと3軸が互いに似た動きをする(明るくなると全部上がる)ので、
/// 箱が斜めに伸びた雲を軸に沿って囲むことになり、**隙間だらけの大きな箱**になる。
/// 明るさと色味に分けると雲が軸に沿うので、箱が小さく締まる——疑いが鋭くなる。
/// 足し算と引き算だけで回せて、戻すのも同じくらい安い。
vec3 ToYCoCg(vec3 color)
{
    return vec3(
        dot(color, vec3(0.25, 0.5, 0.25)),
        dot(color, vec3(0.5, 0.0, -0.5)),
        dot(color, vec3(-0.25, 0.5, -0.25)));
}

vec3 FromYCoCg(vec3 color)
{
    return vec3(
        color.x + color.y - color.z,
        color.x + color.z,
        color.x - color.y - color.z);
}

/// **履歴を Catmull-Rom で読む**。4x4 の 16 テクセルを、双線形 9 回で読む。
///
/// 双線形は「近い4テクセルの平均」なので、**読むたびに少しぼける**。
/// TAA は毎フレーム履歴を読み直すので、カメラが動いているあいだ、
/// 半画素ずれた場所を読むたびにぼけが積み重なっていく(絵がじわじわ眠くなる)。
/// Catmull-Rom は外側に負の重みを持つ3次の曲線で、ぼけずに間を埋める。
///
/// 重みの和は 1 なので、真ん中の2本(w1 と w2)を「間の1点を双線形で読む」にまとめられる。
/// 縦横それぞれ 4 本が 3 本になり、4x4 = 16 回が 3x3 = 9 回になる。
vec3 SampleCatmullRom(vec2 uv)
{
    vec2 size = 1.0 / uTexelSize;
    vec2 position = uv * size;

    // いちばん近い左下のテクセルの中心と、そこからのずれ(0〜1)。
    vec2 center = floor(position - 0.5) + 0.5;
    vec2 f = position - center;

    // テクセル center-1, center, center+1, center+2 の重み。w0 と w3 は負になる。
    vec2 w0 = f * (-0.5 + (f * (1.0 - (0.5 * f))));
    vec2 w1 = 1.0 + (f * f * (-2.5 + (1.5 * f)));
    vec2 w2 = f * (0.5 + (f * (2.0 - (1.5 * f))));
    vec2 w3 = f * f * (-0.5 + (0.5 * f));

    // 真ん中の2本を双線形1回に。center から w2 / (w1 + w2) だけ進んだ点を読めば、
    // GPU の補間が w1 : w2 で混ぜてくれる。
    vec2 w12 = w1 + w2;
    vec2 offset12 = w2 / w12;

    vec2 uv0 = (center - 1.0) * uTexelSize;
    vec2 uv12 = (center + offset12) * uTexelSize;
    vec2 uv3 = (center + 2.0) * uTexelSize;

    vec3 result = vec3(0.0);
    result += texture(uHistory, vec2(uv0.x, uv0.y)).rgb * (w0.x * w0.y);
    result += texture(uHistory, vec2(uv12.x, uv0.y)).rgb * (w12.x * w0.y);
    result += texture(uHistory, vec2(uv3.x, uv0.y)).rgb * (w3.x * w0.y);
    result += texture(uHistory, vec2(uv0.x, uv12.y)).rgb * (w0.x * w12.y);
    result += texture(uHistory, vec2(uv12.x, uv12.y)).rgb * (w12.x * w12.y);
    result += texture(uHistory, vec2(uv3.x, uv12.y)).rgb * (w3.x * w12.y);
    result += texture(uHistory, vec2(uv0.x, uv3.y)).rgb * (w0.x * w3.y);
    result += texture(uHistory, vec2(uv12.x, uv3.y)).rgb * (w12.x * w3.y);
    result += texture(uHistory, vec2(uv3.x, uv3.y)).rgb * (w3.x * w3.y);

    // 負の重みがあるので、明暗の境目では 0 を下回ることがある。色に負は無い。
    return max(result, vec3(0.0));
}

/// **箱の中心へ向かって切る**(Playdead の INSIDE の TAA、2016)。
///
/// 箱の外にある履歴を、箱の中心と結んだ線の上で、箱の縁まで引き戻す。
/// 成分ごとに箱へ押し込む(clamp)と、3つの成分が別々に動くので**色味が変わる**
/// (青い履歴の Co だけが押し込まれて紫になる、など)。線の上を動かせば、色味の向きは保たれる。
vec3 ClipTowardCenter(vec3 color, vec3 low, vec3 high)
{
    vec3 center = 0.5 * (high + low);
    vec3 extents = (0.5 * (high - low)) + vec3(0.000001);
    vec3 offset = color - center;
    vec3 units = abs(offset / extents);
    float farthest = max(units.x, max(units.y, units.z));

    return farthest > 1.0 ? center + (offset / farthest) : color;
}

/// 速度を色に。**止まっているところは黒**、横に動くほど赤、縦に動くほど緑。4 画素で振り切れる。
vec3 VelocityColor(vec2 velocity)
{
    vec2 pixels = abs(velocity / uTexelSize);
    return vec3(pixels * 0.25, 0.0);
}

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    ivec2 lastPixel = textureSize(uCurrent, 0) - 1;

    vec3 current = texelFetch(uCurrent, pixel, 0).rgb;

    // **揺れの素**。履歴を混ぜずに今の絵だけを出すと、毎フレームずらしている様子がそのまま見える。
    // 履歴が無いときも同じ(最初のフレームは今の絵しか無い)。
    if (uDebug == 1 || uHistoryValid == 0)
    {
        FragColor = vec4(current, 1.0);
        return;
    }

    // --- 近所(3x3)を見る: 色の箱と、いちばん手前の画素 ---
    //
    // 色は「畳んで(ToneWeigh)YCoCg に回した」世界で集める。混ぜるのも同じ世界。
    vec3 sum = vec3(0.0);
    vec3 sumOfSquares = vec3(0.0);
    vec3 boxMin = vec3(1.0e30);
    vec3 boxMax = vec3(-1.0e30);

    float closestDepth = 2.0;
    ivec2 closest = pixel;

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            ivec2 neighbor = clamp(pixel + ivec2(x, y), ivec2(0), lastPixel);
            vec3 color = ToYCoCg(ToneWeigh(texelFetch(uCurrent, neighbor, 0).rgb));

            sum += color;
            sumOfSquares += color * color;
            boxMin = min(boxMin, color);
            boxMax = max(boxMax, color);

            float depth = texelFetch(uDepth, neighbor, 0).r;
            if (depth < closestDepth)
            {
                closestDepth = depth;
                closest = neighbor;
            }
        }
    }

    // --- 速度: **近所でいちばん手前の画素のもの**を使う ---
    //
    // 動く物の輪郭の画素は、半分が物で半分が背景。自分の画素の速度だけを見ると、
    // ジッターでたまたま背景を取ったフレームは「背景の速度」で履歴を引き、輪郭がちぎれて残る。
    // 手前(=動く物のほう)の速度に寄せると、**輪郭が物と一緒に動く**。
    vec2 velocity = texelFetch(uVelocity, closest, 0).rg;

    if (uDebug == 2)
    {
        FragColor = vec4(VelocityColor(velocity), 1.0);
        return;
    }

    // この画素が前のフレームに居た場所。
    vec2 historyUv = vUv - velocity;

    // **前のフレームでは画面の外に居た**。履歴が無いので今の絵をそのまま出す
    // (カメラを振った瞬間の画面の端)。
    if (any(lessThan(historyUv, vec2(0.0))) || any(greaterThan(historyUv, vec2(1.0))))
    {
        FragColor = vec4(current, 1.0);
        return;
    }

    vec3 history = uHistoryFilter == 1
        ? SampleCatmullRom(historyUv)
        : texture(uHistory, historyUv).rgb;

    vec3 historyColor = ToYCoCg(ToneWeigh(history));
    vec3 currentColor = ToYCoCg(ToneWeigh(current));
    vec3 rectified = historyColor;

    // --- 履歴を疑う ---
    //
    // 今の絵の近所 9 画素が作る色の範囲を「この画素がとりうる色」とみなす。
    // 履歴がそこから外れていたら、それは**もうここに無いもの**(動いた物の残像・当たらなくなった光)なので、
    // 範囲まで引き戻す。範囲の中なら、それは「ジッターで見え隠れしている細部」かもしれないので、そのまま信じる。
    if (uRectify == 1)
    {
        // 最小〜最大の箱。3x3 に1画素でも明るいものがあると箱が大きく伸び、疑いが甘くなる。
        // 今の画素も9画素の1つなので、箱は必ず今の色を含む。
        rectified = clamp(historyColor, boxMin, boxMax);
    }
    else if (uRectify == 2)
    {
        // **分散で箱を作る**(Salvi 2016)。平均 ± 標準偏差。
        // 9 画素のうち1画素だけ飛び抜けていても、標準偏差はそれほど伸びないので、箱が締まる。
        // 最小〜最大の箱からはみ出さないように両方の内側を取る。
        vec3 mean = sum / 9.0;
        vec3 sigma = sqrt(max((sumOfSquares / 9.0) - (mean * mean), vec3(0.0)));
        vec3 low = max(mean - (uVarianceGamma * sigma), boxMin);
        vec3 high = min(mean + (uVarianceGamma * sigma), boxMax);

        // **今の画素そのものは、必ず箱に入れる**。
        //
        // 締まった箱は、近所から浮いた1画素(レンガの目地、光の点)を箱の外に置いてしまう。
        // すると止まった絵でも、履歴(= 前のフレームのその画素)が毎フレーム箱の縁まで切られ、
        // 「0.9 × 箱の縁 + 0.1 × 今の値」に落ち着く——暗い地の明るい1画素なら 1.0 が 0.54 まで沈む。
        // 今の絵の色は疑う理由が無いので、箱をそこまで広げる(計画書「検証の途中で分かったこと」)。
        low = min(low, currentColor);
        high = max(high, currentColor);

        rectified = ClipTowardCenter(historyColor, low, high);
    }

    if (uDebug == 3)
    {
        // **どれだけ疑ったか**。動かした量を明るさ(Y)の割合で出し、今の絵の暗い灰色に赤で重ねる。
        // 動かすと、動く物の縁(隠れていた所が現れる場所)が赤く光る。止めていても、目地のような1画素の模様には
        // 赤い点が散る——ずらすたびに近所の色の範囲が変わるので、履歴がときどき範囲から出る(細かい模様のちらつきの素)。
        float moved = length(rectified - historyColor) / max(historyColor.x, 0.02);
        vec3 base = vec3(currentColor.x * 0.4);
        FragColor = vec4(mix(base, vec3(4.0, 0.3, 0.1), clamp(moved * 2.0, 0.0, 1.0)), 1.0);
        return;
    }

    // --- 混ぜる ---
    //
    // 指数移動平均。N フレーム前の絵は (1 − 重み)^N の重さで残る(0.1 なら 10 フレームで 35%、30 フレームで 4%)。
    vec3 resolved = mix(rectified, currentColor, uCurrentWeight);

    FragColor = vec4(max(ToneUnweigh(FromYCoCg(resolved)), vec3(0.0)), 1.0);
}
