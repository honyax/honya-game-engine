#version 330 core

// 属性の番号は Vertex 構造体のフィールド順(位置 → UV → 色 → 法線)と対応している。
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;

// Day 32 で足した。**末尾に足す**ことで 0〜2 の番号を動かさずに済ませている。
layout (location = 3) in vec3 aNormal;

// Day 34。同じ理由でさらに末尾。xyz が接線、w が従接線の符号(+1 / -1)。
layout (location = 4) in vec4 aTangent;

// Day 41。スキニング。**やはり末尾**なので 0〜4 の番号は動いていない。
//   aJoints  … この頂点を動かす関節の番号(最大4本)。整数だが float で届く
//   aWeights … その4本の効き具合。合計 1(読み込み時に正規化してある)
layout (location = 5) in vec4 aJoints;
layout (location = 6) in vec4 aWeights;

// --- フレームごとに変わる(カメラが設定。1フレームに1回) ---
// ビュー行列と投影行列を掛け合わせたもの。オブジェクトが何個あっても同じ値なので、
// 描画のたびに送り直す必要が無い。uniform は**プログラムに紐づく状態**で、
// glUseProgram を呼び直しても値は消えないため、フレーム頭で1回設定すれば足りる。
uniform mat4 uViewProjection;

// --- オブジェクトごとに変わる(描画のたびに設定) ---
// モデル行列。その物体を「世界のどこに、どんな向き・大きさで置くか」。
uniform mat4 uModel;

// 法線を世界空間へ運ぶための行列(Day 32)。**モデル行列をそのまま使えない**。
//
// 位置は uModel で正しく運べるが、法線は「向き」なので事情が違う。
// 非一様スケール(x だけ 2 倍など)をかけると、
// **面は傾くのに法線は同じだけ傾かない**——むしろ逆向きに傾く。
// 正しい変換は「モデル行列の左上 3x3 の逆行列の転置」で、
// これを法線行列と呼ぶ。CPU 側で作って送る(Program.Draw)。
//
// 一様スケールと回転だけなら uModel の 3x3 と一致するので、
// 「動いているから正しい」が言えない類の話。立方体を潰すと差が出る。
uniform mat3 uNormalMatrix;

// 光の目から見たビュー射影行列(Day 33)。**深度パスに送るものとまったく同じ行列**。
// ShadowMap が1か所で作って、深度パスと本描画の両方へ配っている。
// ここがずれると影が丸ごとずれるので、2か所で組み立ててはいけない。
uniform mat4 uLightSpaceMatrix;

// カメラの位置(Day 34)。視差マッピングは「どこから見ているか」で
// UV のずらし方が変わるので、視線ベクトルが要る。
uniform vec3 uCameraPosition;

// --- マテリアルごとに変わる(Material.Apply が設定) ---
uniform vec2 uUvScale;

// --- Day 41: スキニング ---
//
// **AnimationPlayer.MaxJoints と必ず同じ値**にする。
// C# 側が 64 個送るつもりで、こちらが 32 個しか宣言していないと、
// 33 番以降の関節に属する頂点が原点へ吸い込まれる。
const int MAX_JOINTS = 64;

// 関節行列。IBM * world(joint) を CPU 側で作って送る(AnimationPlayer.Evaluate)。
//
// <b>頂点シェーダの uniform 枠をいちばん食うのがこれ</b>。
// mat4 が 64 個 = float 1024 個で、OpenGL 3.3 が保証する下限ぴったり。
// 実機はもっと持っているので通るが、**保証の上に乗っているわけではない**。
uniform mat4 uJoints[MAX_JOINTS];

// スキニングを効かせるか。0 なら aJoints / aWeights を一切見ない。
//
// **描画のたびに設定する**(マテリアルごとではない)。同じシェーダで
// 地面(スキン無し)とキャラクタ(スキンあり)の両方を描くため。
uniform int uSkinned;

out vec2 vTexCoord;
out vec4 vColor;
out vec3 vNormal;

// 世界座標(Day 33)。今日は影の座標を作るのに使うだけだが、
// **点光源の方向・視線ベクトル・フォグ**など、これから要るものが軒並みここから始まる。
out vec3 vWorldPos;

// 光の座標系での位置(Day 33)。**頂点シェーダで写しておく**のが定石。
//
// 画素シェーダで vWorldPos に uLightSpaceMatrix を掛けても同じ結果になるが、
// 行列とベクトルの積を**頂点の数だけ**やるか**画素の数だけ**やるかの差になる。
// 立方体1個なら 24 回 対 数万回。線形な変換は補間しても同じ値になるので、
// 頂点側でやってラスタライザに運ばせるのが正しい
// (法線のように「補間で長さが崩れる」たぐいの問題も、位置には無い)。
out vec4 vLightSpacePos;

// --- Day 34: 接空間 ---
//
// **世界空間の TBN をそのまま渡す**。法線マップから読んだ接空間の法線を
// 世界へ持ち上げるのに使う(frag 側で mat3(T, B, N) を組む)。
//
// 行列 1 本(mat3)で渡してもよいが、3本のベクトルとして渡すのと同じこと。
// 分けておくと**接線だけ・従接線だけを色に出す**デバッグ表示が書きやすい。
out vec3 vTangent;
out vec3 vBitangent;

/// 接空間から見た視線の向き(面 → カメラ)。**視差マッピングの入力そのもの**。
///
/// 世界空間のまま frag へ渡して、frag で接空間へ回してもよい。
/// ここで回しておくのは、**行列とベクトルの積を頂点の数だけで済ませる**ため
/// (Day 33 の vLightSpacePos と同じ判断)。
out vec3 vTangentViewDir;

// --- Day 41: 骨の重みを色で見る(成分 22 / Alt+F7)---
//
// **どの骨がどこを動かしているか**は、完成した絵からはまったく読めない。
// 関節ごとに色を割り当てて、重みで混ぜたものを流しておく。
// 骨の真ん中は単色、関節のまわりは2色のグラデーションになり、
// **頂点ブレンディングが効いている範囲がそのまま見える**。
out vec3 vSkinTint;

/// 関節の番号から色を作る。**隣り合う番号がなるべく違う色**になればよいので、
/// 位相をずらした3本の余弦波で散らす(いわゆる cosine palette)。
vec3 JointColor(float index)
{
    return 0.5 + (0.5 * cos(6.2831853 * ((index * 0.13) + vec3(0.0, 0.33, 0.67))));
}

/// 4本の関節行列を重みで混ぜる。**これが「頂点ブレンディング」の全部**。
///
/// 行列を混ぜてから頂点に掛けるのと、頂点に掛けてから混ぜるのは同じ結果になる
/// (行列とベクトルの積は線形なので分配できる)。
/// 混ぜてから掛けるほうが、掛け算が1回で済むぶん速い。
///
/// **混ぜた結果は回転行列ではない**のがこの手法の弱点で、
/// 肘を 180 度曲げると内側の頂点が潰れる(candy-wrapper と呼ばれる)。
/// 直すには二重四元数スキニング(dual quaternion skinning)が要るが、
/// 実際のゲームは「潰れないように関節を足す」ことで回避することが多い。
mat4 SkinMatrix()
{
    return (uJoints[int(aJoints.x)] * aWeights.x)
        + (uJoints[int(aJoints.y)] * aWeights.y)
        + (uJoints[int(aJoints.z)] * aWeights.z)
        + (uJoints[int(aJoints.w)] * aWeights.w);
}

void main()
{
    // --- Day 41: まずスキニング ---
    //
    // **モデル行列より先**。関節行列はメッシュ空間の頂点を
    // 「骨格の今の姿」へ運ぶ変換なので、uModel(その個体をどこに置くか)は
    // そのあとに掛ける。順序を逆にすると、モデルを動かした瞬間に
    // キャラクタが原点へ引き戻される。
    vec4 localPos = vec4(aPosition, 1.0);
    vec3 localNormal = aNormal;
    vec3 localTangent = aTangent.xyz;

    vSkinTint = vec3(0.15);

    if (uSkinned == 1)
    {
        mat4 skin = SkinMatrix();
        localPos = skin * localPos;

        // **法線と接線もスキニングする**。ここを忘れると、
        // 形は正しく曲がるのに陰影だけがバインドポーズのまま張り付く——
        // 「腕を上げたのに影が動かない」という気味の悪い絵になる。
        //
        // 厳密には法線には逆転置が要るが、関節行列はほぼ回転と平行移動
        // (非一様スケールを入れたリグは珍しい)なので、mat3 で足りる。
        // 平行移動の成分が落ちるのが mat3 にする理由でもある——
        // 向きに平行移動は効かない。
        mat3 skin3 = mat3(skin);
        localNormal = skin3 * localNormal;
        localTangent = skin3 * localTangent;

        vSkinTint = (JointColor(aJoints.x) * aWeights.x)
            + (JointColor(aJoints.y) * aWeights.y)
            + (JointColor(aJoints.z) * aWeights.z)
            + (JointColor(aJoints.w) * aWeights.w);
    }

    // モデル → ビュー → 投影 の順に適用する。
    // GLSL は列ベクトル規約なので、**適用したい順とは逆に左から書く**。
    // Day 14 の要点4で見たとおり、C# 側(行ベクトル)の
    //   model * view * projection
    // と、この行は同じ変換を表している。
    // 世界座標は影にもライティングにも要るので、先に出しておく(Day 33)。
    vec4 worldPos = uModel * localPos;

    gl_Position = uViewProjection * worldPos;

    vWorldPos = worldPos.xyz;
    vLightSpacePos = uLightSpaceMatrix * worldPos;

    vTexCoord = aTexCoord * uUvScale;
    vColor = aColor;

    // ここで正規化しないのは、**補間で長さが崩れる**から。
    // 頂点間で線形補間された法線は短くなるので、
    // 受け取ったフラグメント側で正規化し直すのが正しい。
    vNormal = uNormalMatrix * localNormal;

    // --- Day 34: 接空間の3本の軸を世界空間へ ---
    //
    // **接線も法線行列で運ぶ**。位置ではなく向きなので uModel ではない……
    // というのは半分だけ正しくて、接線は「面に沿ったベクトル」なので
    // 本当は uModel の 3x3 で運ぶのが厳密。
    // 法線だけが逆転置を要る特別扱い(Day 32 の要点6)で、接線は違う。
    //
    // それでも uNormalMatrix を使っているのは、
    // **直後に法線と直交させ直す**ので、一様スケールと回転の範囲では結果が同じになるため。
    // 非一様スケールでは厳密には差が出るが、そのときは法線マップ自体が伸びるので、
    // 接線だけ正しくしても意味が無い。
    vec3 t = normalize(uNormalMatrix * localTangent);
    vec3 n = normalize(vNormal);

    // **グラム・シュミット**。補間と変換で直交が崩れているので、その場で立て直す。
    // これを省くと、法線マップの傾きがわずかにねじれた方向へ乗る。
    t = normalize(t - (n * dot(n, t)));

    // 従接線は外積で作る。w がその向きの符号(Vertex.Tangent のコメント)。
    vec3 b = cross(n, t) * aTangent.w;

    vTangent = t;
    vBitangent = b;

    // 世界空間の視線(面 → カメラ)を、接空間へ持ち込む。
    //
    // **転置が逆行列になる**のが直交行列の便利なところ。
    // TBN は「接空間 → 世界」なので、逆(世界 → 接空間)は本来 inverse だが、
    // 3本が正規直交なら転置で済む。だから行ごとに内積を取るだけでよい。
    vec3 worldViewDir = uCameraPosition - worldPos.xyz;
    vTangentViewDir = vec3(dot(worldViewDir, t), dot(worldViewDir, b), dot(worldViewDir, n));
}
