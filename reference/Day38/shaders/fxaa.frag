#version 330 core

// **FXAA(Fast Approximate Anti-Aliasing)**。Day 38 の主役。
//
// ジャギー(輪郭の階段)は、1画素につき1回しか色を決めていないことから来る。
// 三角形の縁が画素の途中を通っても、その画素は「入っている」か「いない」の
// どちらかに倒れるので、斜めの縁が段々になる。
//
// 正攻法は**画素の中で何回も色を決める**こと(MSAA / SSAA)で、
// 品質はいちばん高いが、
//   - メモリと帯域が標本数に比例して増える
//   - **後処理と噛み合わない**(HDR バッファを 4 倍持つことになる)
//   - ディファードだと G バッファまで 4 倍になる(Day 51)
// という重さがある。
//
// FXAA(Timothy Lottes, NVIDIA, 2009)は発想がまるで違う。
// **もう出来上がった1枚の絵を見て、階段になっているところを探して均す**。
//   - 入力は最終画像1枚だけ。ジオメトリも深度も要らない
//   - 追加コストは**フルスクリーン1パス**のみ(1920x1080 で 0.2ms 前後)
//   - どんな絵にも効く。ポリゴンの縁だけでなく、
//     **テクスチャの模様や高輝度部の縁**にも効く(MSAA はここが効かない)
// 代わりに、**本当の情報が増えるわけではない**ので、
//   - 細い線や1画素の点が滲む(文字は特に弱い)
//   - 動くと輪郭がちらつく(時間方向の情報を持たないため)
// という弱点が付いてくる。ここを直すのが TAA(Day 53)。
//
// ここに書いてあるのは、FXAA 3.11 のうち**コンソール向けの軽い版**に
// しきい値の早期打ち切りとデバッグ表示を足したもの。
// 製品版の FXAA はこの後にさらに「エッジに沿って両端まで歩く」段があるが、
// 骨格——**輝度で段差を見つけ、エッジに沿って混ぜる**——は同じ。

in vec2 vUv;

out vec4 FragColor;

/// 合成済みの LDR 画像。**ガンマを掛けたあと**の値が入っている(あとで理由)。
uniform sampler2D uSource;

/// 1テクセルぶんの移動量(1/幅, 1/高さ)。
uniform vec2 uTexelSize;

/// 局所コントラストがこの割合を超えたら「縁」とみなす(相対しきい値)。
uniform float uEdgeThreshold;

/// 暗いところで誤検出しないための下限(絶対しきい値)。
uniform float uEdgeThresholdMin;

/// 何テクセルまで離れて混ぜてよいか。**大きいほど滑らかで、大きいほど滲む**。
uniform float uSpanMax;

/// 明るいところで混ぜる量を抑える係数。
uniform float uReduceMul;

/// 同じく下限。0 除算よけも兼ねる。
uniform float uReduceMin;

/// 0=なし 1=輝度 2=縁の検出 3=混合量
uniform int uDebug;

/// 0 なら比較なし。0 より大きいと、その x より左は**FXAA を掛けない**。
uniform float uSplit;

/// <summary>
/// **輝度**。FXAA が見る唯一の量。
///
/// 人の目は明るさの差に敏感で色の差には鈍いので、
/// 「段差が見えるか」は輝度だけで判断してよい——
/// RGB 3成分を別々に見ても、判定はほとんど変わらないのに計算は3倍になる。
///
/// 係数 (0.299, 0.587, 0.114) は NTSC 由来の重みで、
/// **リニアではなくガンマを掛けたあとの値に掛ける**ためのもの。
/// FXAA がガンマ後の画像を入力に取るのはここが理由で、
/// リニアな明るさで段差を測ると、暗部の差が小さく出すぎて縁を見逃す
/// (人の目は暗部の差にこそ敏感なので、その感覚に近いのはガンマ後の値のほう)。
/// </summary>
float Luma(vec3 color)
{
    return dot(color, vec3(0.299, 0.587, 0.114));
}

vec3 Sample(vec2 uv)
{
    return texture(uSource, uv).rgb;
}

void main()
{
    vec3 center = Sample(vUv);

    // --- 1. 近傍5点の輝度を集める -------------------------------------------
    //
    // 中心と、**斜め4点**。上下左右ではなく斜めなのは、
    // このあと「縁がどちらへ伸びているか」を斜めの差から出すため。
    vec3 rgbNw = Sample(vUv + (vec2(-1.0, -1.0) * uTexelSize));
    vec3 rgbNe = Sample(vUv + (vec2(1.0, -1.0) * uTexelSize));
    vec3 rgbSw = Sample(vUv + (vec2(-1.0, 1.0) * uTexelSize));
    vec3 rgbSe = Sample(vUv + (vec2(1.0, 1.0) * uTexelSize));

    float lumaNw = Luma(rgbNw);
    float lumaNe = Luma(rgbNe);
    float lumaSw = Luma(rgbSw);
    float lumaSe = Luma(rgbSe);
    float lumaM = Luma(center);

    float lumaMin = min(lumaM, min(min(lumaNw, lumaNe), min(lumaSw, lumaSe)));
    float lumaMax = max(lumaM, max(max(lumaNw, lumaNe), max(lumaSw, lumaSe)));

    // --- 2. 縁かどうかを決める(早期打ち切り)-------------------------------
    //
    // **平らなところでは何もしない**のが FXAA の速さの本体。
    // 普通の絵で縁として拾われるのは全画素の数%〜十数%しかないので、
    // 残りはここで抜ける。GPU は 2x2 の画素をまとめて処理するため、
    // 「4画素とも平ら」でないと本当には抜けないが、それでも大半が抜ける。
    //
    // しきい値が2つあるのは役割が違うから。
    //   - 相対 … 明るいところほど大きな差でないと縁とみなさない
    //            (人の目の性質。明所では相対差でしか段差を感じない)
    //   - 絶対 … 真っ暗なところで、わずかなノイズを縁と誤認しないための床
    float range = lumaMax - lumaMin;
    bool isEdge = range >= max(uEdgeThresholdMin, lumaMax * uEdgeThreshold);

    // --- 3. 縁の向きを斜めの差から出す --------------------------------------
    //
    // (NW+NE) - (SW+SE) は「上と下の明るさの差」、
    // (NW+SW) - (NE+SE) は「左と右の明るさの差」。
    // 前者が大きいなら**横に走る縁**なので、混ぜたい方向は横——
    // だから dir.x に上下差を、dir.y に左右差を入れる(**入れ替わっている**)。
    //
    // ここが FXAA でいちばん誤解されやすいところ。混ぜるのは
    // <b>縁をまたぐ方向ではなく、縁に沿う方向</b>。
    // 縁をまたいで混ぜたらただのぼかしになる。
    // 縁に沿って混ぜると、階段の1段ぶん先の色が入ってきて、
    // **段差の角が中間色で埋まる**——これがジャギーの消え方の正体。
    vec2 dir;
    dir.x = -((lumaNw + lumaNe) - (lumaSw + lumaSe));
    dir.y = ((lumaNw + lumaSw) - (lumaNe + lumaSe));

    // 明るいところでは混ぜる量を抑える。
    // 暗部のわずかな段差は目立ち、明部の段差は目立たない、という重み付け。
    float reduce = max((lumaNw + lumaNe + lumaSw + lumaSe) * 0.25 * uReduceMul, uReduceMin);

    // **小さいほうの成分で正規化する**。
    // こうすると、斜め 45 度の縁では両成分が 1 テクセル、
    // ほとんど水平な縁では横成分だけが大きく伸びる——
    // つまり<b>縁の傾きがそのまま歩幅になる</b>。
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + reduce);

    dir = clamp(dir * rcpDirMin, vec2(-uSpanMax), vec2(uSpanMax)) * uTexelSize;

    // --- 4. 縁に沿って4点を混ぜる -------------------------------------------
    //
    // 内側2点(±1/6)の平均が rgbA、それに外側2点(±1/2)を足したものが rgbB。
    // **広く混ぜた rgbB のほうが滑らか**だが、遠くまで取りに行くぶん、
    // 縁とは関係ない色を拾ってしまうことがある。
    vec3 rgbA = 0.5 * (Sample(vUv + (dir * ((1.0 / 3.0) - 0.5)))
        + Sample(vUv + (dir * ((2.0 / 3.0) - 0.5))));

    vec3 rgbB = (rgbA * 0.5)
        + (0.25 * (Sample(vUv + (dir * -0.5)) + Sample(vUv + (dir * 0.5))));

    // **近傍の輝度の範囲から外れたら、広いほうを捨てる**。
    // これが FXAA の唯一の安全装置で、
    // これが無いと細いものの周りに縁取り(ハロー)が出る。
    float lumaB = Luma(rgbB);
    vec3 filtered = (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;

    // 縁でなければ元のまま。**混ぜないことのほうが多い**。
    vec3 result = isEdge ? filtered : center;

    // --- デバッグ表示(Shift+F2)---------------------------------------------
    //
    // FXAA は「効いているのか」がいちばん分からない後処理で、
    // 絵を見ても**滲んだのか整ったのか区別が付かない**。
    // だから中間の量を直接見られるようにしておく。
    if (uDebug == 1)
    {
        // 輝度そのもの。FXAA が見ている世界。
        result = vec3(lumaM);
    }
    else if (uDebug == 2)
    {
        // 縁と判定された画素を赤く。**画面のどれくらいが処理されているか**が分かる。
        // 空や壁の平らな面が赤くなっているなら、しきい値が低すぎる。
        result = mix(vec3(lumaM * 0.35), vec3(1.0, 0.15, 0.1), isEdge ? 1.0 : 0.0);
    }
    else if (uDebug == 3)
    {
        // 元の色からどれだけ動いたか。8 倍に持ち上げて見えるようにする。
        // **輪郭の線だけが光るのが正解**。面が光っていたら混ぜすぎ。
        result = abs(result - center) * 8.0;
    }
    else if (uSplit > 0.0)
    {
        // --- 左右比較(Shift+F11)---------------------------------------------
        //
        // **FXAA は静止画で見比べないと分からない**。切り替えて見ると、
        // 目が絵の変化を追ってしまって「変わった気がする」で終わる。
        // 同じ画面の左右に加工前と加工後を並べれば、境目の一本の縁だけを見ればよくなる。
        //
        // デバッグ表示中は出さない。あちらはすでに「中間の量」を見る窓なので、
        // 半分だけ別のものが映ると読めなくなる。
        if (vUv.x < uSplit)
        {
            result = center;
        }

        // 境目に細い線を引く。どちらが加工前かを見失わないため。
        if (abs(vUv.x - uSplit) < uTexelSize.x)
        {
            result = vec3(1.0, 0.85, 0.2);
        }
    }

    FragColor = vec4(result, 1.0);
}
