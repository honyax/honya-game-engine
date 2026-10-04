#version 330 core

in vec2 vUv;

// 出す先は R8(1成分)。**遮蔽率という数字ひとつ**しか書かない。
out vec4 FragColor;

/// 1パス目の結果。RGB = ビュー空間の法線、A = カメラからの距離。
uniform sampler2D uGeometry;

/// 4x4 のランダムな向き。**画面にタイル状に敷いて使う**。
uniform sampler2D uNoise;

/// 半球状に散らした標本点(接空間)。長さは 0〜1 で、中心寄りに密。
uniform vec3 uKernel[64];

/// 実際に使う標本数(Ctrl+F4)。**配列の長さより少なくてよい**。
uniform int uSampleCount;

/// 配列を何本おきに読むか(= 64 / uSampleCount)。
///
/// **先頭から順に取ってはいけない**。カーネルは中心寄りに密になるよう
/// 「短いものから長いもの」の順で作ってあるので(<c>Ssao.BuildKernel</c>)、
/// 先頭 8 本を使うと**いちばん短い 8 本だけ**になり、
/// 半径をいくら広げても手元しか探らなくなる。
/// 間引いて取れば、本数を減らしても長さの分布はそのまま残る。
uniform int uSampleStride;

/// 探る球の半径(ワールド単位 = メートル)。
uniform float uRadius;

/// 自己遮蔽よけの下駄。**これが 0 だと平らな面が縞になる**。
uniform float uBias;

/// 遮蔽の効き(Ctrl+F7)。1.0 が素の計算どおり。
uniform float uStrength;

/// 遮蔽率にかける指数。**大きいほど暗がりが締まる**。絵作りのつまみ。
uniform float uPower;

/// カメラの射影行列。**標本点を画面のどこに写るか調べる**のに使う。
uniform mat4 uProjection;

/// 射影行列の対角成分の逆数 (1/M11, 1/M22)。深度から位置を戻すのに要る。
uniform vec2 uProjScale;

/// 平行投影か。**位置の戻し方が変わる**(下の ViewPosition)。
uniform int uOrthographic;

/// ノイズを敷き詰める倍率 = AO バッファの大きさ / 4。
uniform vec2 uNoiseScale;

/// **深度から、そのピクセルのビュー空間の位置を戻す**。
///
/// 1パス目が書いたのは「距離」だけで、位置そのものは書いていない。
/// 位置を3成分そのまま持つほうが素直だが、**深度1つから戻せる**ので持たない。
/// これは後処理の常套手段で、G バッファの帯域はそのまま速度に効く。
///
/// 戻し方は投影の種類で変わる。
///
/// ```
///   透視投影    x_view = ndc.x * (1/M11) * depth      … 遠いほど1画素が広い
///   平行投影    x_view = ndc.x * (1/M11)              … どこでも同じ幅
/// ```
///
/// **透視除算があるかどうか**の違いが、そのままここに出ている。
/// 透視投影の M11 は 1/(tan(fov/2)*アスペクト) なので、
/// その逆数を掛けることは「画面の端を視野角の端へ開く」ことにあたる。
/// 平行投影の M11 は 2/幅 なので、逆数は「画面の半分の幅」そのもの。
vec3 ViewPosition(vec2 uv, float depth)
{
    vec2 ndc = (uv * 2.0) - 1.0;
    vec2 planeXy = ndc * uProjScale;

    // ビュー空間は -Z が前(Day 6 から変わらない規約)。
    return uOrthographic == 1
        ? vec3(planeXy, -depth)
        : vec3(planeXy * depth, -depth);
}

void main()
{
    vec4 geometry = texture(uGeometry, vUv);
    float depth = geometry.a;

    // **何も描かれていないところは遮られていない**。
    // 1パス目のクリア色が (0,0,0,0) なので、深度 0 が「空」の目印になる。
    // ここを忘れると空が真っ黒になる——AO は乗算で使うので、致命的に効く。
    if (depth <= 0.0)
    {
        FragColor = vec4(1.0);
        return;
    }

    vec3 position = ViewPosition(vUv, depth);
    vec3 normal = normalize(geometry.rgb);

    // --- ランダムな回転で、少ない標本をごまかす ---
    //
    // 標本が 16 本しか無いと、**同じ 16 方向を全画素で使う**ことになり、
    // 縞やまだら(バンディング)がはっきり出る。
    // そこで画素ごとに接空間をランダムに回す。
    //
    // 誤差が消えるわけではない。**規則的な誤差を、目に付きにくい砂嵐に変える**だけ。
    // 砂嵐はあとでぼかせば消えるが、縞はぼかしても縞のまま残る——
    // だから「ノイズを入れてからぼかす」が定番になる。
    // モンテカルロ積分でおなじみの手口で、Day 36 の事前フィルタとも発想は同じ。
    vec3 randomVector = texture(uNoise, vUv * uNoiseScale).xyz;

    // --- 接空間を組む(グラム・シュミット)---
    //
    // Day 34 で法線マップのために組んだのと**まったく同じ手順**。
    // あちらは UV の傾きから接線を作ったが、今日は乱数から作る。
    // 「N に垂直な向きを1本決めれば、残りは外積で決まる」構造は同じ。
    vec3 tangent = normalize(randomVector - (normal * dot(randomVector, normal)));
    vec3 bitangent = cross(normal, tangent);
    mat3 tbn = mat3(tangent, bitangent, normal);

    float occlusion = 0.0;

    for (int i = 0; i < uSampleCount; i++)
    {
        // 接空間の標本を、法線のほうを向いた半球としてワールドの長さに広げる。
        vec3 samplePosition = position + ((tbn * uKernel[i * uSampleStride]) * uRadius);

        // **標本点が画面のどこに写るか**を調べる。ここが「スクリーンスペース」の核心。
        //
        // 3D の可視判定をせずに、**すでに描いてある深度バッファを流用して**
        // 「その方向に物があるか」を答える。だから安い代わりに、
        // **画面に写っていないものは遮蔽してくれない**(画面外・手前の物の裏)。
        vec4 clip = uProjection * vec4(samplePosition, 1.0);
        vec2 offset = ((clip.xy / clip.w) * 0.5) + 0.5;

        // 画面の外に出た標本は捨てる。**端の色を引き延ばして使うと、
        // 画面の縁に沿って暗い枠が出る**(ClampToEdge の副作用)。
        if (offset.x < 0.0 || offset.x > 1.0 || offset.y < 0.0 || offset.y > 1.0)
        {
            continue;
        }

        float sampledDepth = texture(uGeometry, offset).a;

        // 標本の先が空なら、そこには何も無い。
        if (sampledDepth <= 0.0)
        {
            continue;
        }

        // 実際に描かれている面のビュー空間 z(負)。
        float sampledZ = -sampledDepth;

        // **手前に面があれば遮られている**。z は負なので、大きいほうが手前。
        //
        // uBias は「自分自身を遮蔽物と数えない」ための下駄。
        // 平らな面でも、標本点はわずかに面の内側へ入ることがある
        // (法線の補間誤差、深度の量子化)。0 にすると、
        // 平らな床一面に薄い縞が出る——シャドウアクネと同じ理屈で、
        // **同じ面を自分自身と比べているから**起きる。
        float occluded = sampledZ >= (samplePosition.z + uBias) ? 1.0 : 0.0;

        // **遠くの物に遮られたことにしない**。
        //
        // これが無いと、手前の柱の縁に沿って**背景側が黒く縁取られる**。
        // 柱の後ろのはるか遠くの壁を「手前にある」と判定してしまうため。
        // 深度の差が半径を超えたら効き目を 0 へ滑らかに落とす。
        //
        // スクリーンスペースの手法が「本当の遮蔽」ではなく
        // **深度バッファという不完全な世界の模型**を相手にしていることが、
        // いちばんはっきり出るのがこの1行。
        float rangeCheck =
            smoothstep(0.0, 1.0, uRadius / max(abs(position.z - sampledZ), 1e-4));

        occlusion += occluded * rangeCheck;
    }

    // 平均して 0〜1 に。**1 が「遮られていない」**(乗算で使うので明るいほうが 1)。
    float ambient = 1.0 - ((occlusion / float(uSampleCount)) * uStrength);

    FragColor = vec4(vec3(pow(clamp(ambient, 0.0, 1.0), uPower)), 1.0);
}
