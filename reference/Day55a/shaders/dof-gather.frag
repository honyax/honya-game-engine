#version 330 core

// **ボケを集める**(Day 55)。被写界深度の本体。半分の大きさで走り、**奥の層と手前の層を2枚同時に書く**(MRT)。
//
// ピントの外れた点は、画面の上で錯乱円に広がる——本来は「点がまわりへ光を配る」処理(scatter)。
// 画素シェーダは自分の色しか決められないので、裏返して「まわりの誰の光が、自分のところまで届いているか」を集める(gather)。
//
// **届くかどうかは、相手の錯乱円で決まる**。これを取り違えると2つの壊れ方をする(uScatterAsGather = 0 で見られる)。
//   - 自分の半径で集めると、ぼけた背景の画素が、ピントの合った物の色まで平均する → 物のまわりに光の縁(ハロー)
//   - 自分の半径で集めると、ピントの合った背景の画素は何も集めない → 手前のボケが物の輪郭で切り抜いたように止まる
//
// そこで2つの層に分ける。
//   奥の層 … 自分と相手の「小さいほう」の半径が届けば混ぜる。ピントの合った物(半径 0)は奥のボケに混ざらない
//   手前の層 … 相手の半径が届けば混ぜる。自分がピントの合った画素でも、手前の大きなボケが上に被さってくる
// 奥の層は「その画素の背景」なので平均でよく、手前の層は「上に被さる半透明の膜」なので、どれだけ覆うか(アルファ)も要る。

in vec2 vUv;

layout(location = 0) out vec4 FragFar;    // rgb: 奥の層の色 × 有効 / a: 有効(奥がぼけている画素なら 1。素朴なときは全部 1)
layout(location = 1) out vec4 FragNear;   // rgb: 手前の層の色 × 覆い(前掛けのアルファ) / a: 覆い(0〜1)

uniform sampler2D uPrepared;   // dof-prepare.frag の出力(半分の大きさ。a が錯乱円 [原寸の画素])
uniform sampler2D uTiles;      // 近所の升目(z がいちばん手前の錯乱円)
uniform vec2 uPixelSize;       // 1 / 原寸の画面の大きさ。錯乱円は原寸の画素で持っているので、UV へはこれで直す
uniform int uScatterAsGather;  // 1: 散らしとして集める / 0: 自分の半径で素朴に集める
uniform float uMaxRadius;      // 錯乱円の半径の上限 [画素]
uniform int uTileSize;         // 升目の大きさ [原寸の画素]

// 円盤に撒く点の数。DepthOfField.SampleCount と同じ数にしておくこと。
const int SampleCount = 48;

// 黄金角 π(3 − √5)。この角度ずつ回しながら外へ出ていくと、点が渦を巻いて円の中に満遍なく散る。
const float GoldenAngle = 2.39996323;

/// 半径 radius の円が、中心から offset だけ離れた点まで届くか。**縁の1画素でなめらかに 0 へ落とす**
/// (0 か 1 かで切ると、半径が少し変わるだけで点が出たり消えたりして、ボケの縁がちらつく)。
float Cover(float radius, float offset)
{
    return clamp(radius - offset + 0.5, 0.0, 1.0);
}

void main()
{
    vec4 center = texelFetch(uPrepared, ivec2(gl_FragCoord.xy), 0);

    // **近所でいちばん大きく手前にぼけている物の半径**(BlurField の升目)。
    // 自分がピントの合った画素でも、ここまでは探す——手前のボケが上に被さってくるかもしれないから。
    ivec2 tile = (ivec2(gl_FragCoord.xy) * 2) / uTileSize;
    float nearReach = -texelFetch(uTiles, tile, 0).z;

    float radius = uScatterAsGather == 1 ? max(abs(center.a), nearReach) : abs(center.a);
    radius = min(radius, uMaxRadius);

    // 探す範囲が 0.5 画素に満たない = 自分もぼけていないし、手前のボケも届かない。何もしない
    // (奥の層も「無効」にしておく。下の farValid と同じ扱い)。
    if (radius < 0.5)
    {
        FragFar = uScatterAsGather == 1 ? vec4(0.0) : vec4(center.rgb, 1.0);
        FragNear = vec4(0.0);
        return;
    }

    vec4 far = vec4(0.0);    // rgb: 色 × 重み / a: 重みの和
    vec4 near = vec4(0.0);

    for (int i = 0; i < SampleCount; i++)
    {
        // **Vogel の円盤**。i 番目の点を、中心から √((i + 0.5) / N) の距離、i × 黄金角の向きに置く。
        // √ を取るのは、面積が半径の2乗に比例するから——内側にも外側にも同じ密度で点が落ちる。
        float r = sqrt((float(i) + 0.5) / float(SampleCount)) * radius;
        float angle = float(i) * GoldenAngle;
        vec2 offset = vec2(cos(angle), sin(angle)) * r;

        // 双線形で読む(半分の大きさの点の間を埋める)。
        vec4 s = texture(uPrepared, vUv + (offset * uPixelSize));

        if (uScatterAsGather == 0)
        {
            // **素朴**: 自分の半径の円の中は、奥行きを問わず全部平均する。
            far += vec4(s.rgb, 1.0);
            continue;
        }

        // --- 奥の層: 自分と相手の小さいほうの半径が届くか ---
        //
        // ピントの合った相手(半径 0)は、どれだけ近くても奥のボケに混ざらない——ハローが出ない。
        // 手前の相手(負)も 0 に切るので混ざらない(手前は手前の層で扱う)。
        float farRadius = max(min(center.a, s.a), 0.0);
        far += vec4(s.rgb, 1.0) * Cover(farRadius, r);

        // --- 手前の層: 相手の半径が届くか ---
        //
        // 相手は半径 rs の円に光を配っている。円の面積は π rs² なので、1画素あたりに届くのは 1 / (π rs²)。
        // 撒いた1点は π R² / N の面積を代表しているから、1点ぶんの重みは (R / rs)² / N になる。
        // 大きくぼけた相手ほど薄く広く配る、という**光の量の保存**がこの2乗。
        // 半径 1 画素未満はピントが合っているとみなして数えない(ピントの帯の画素が手前の層に吸われて、半分の大きさでぼけるのを防ぐ)。
        float nearRadius = -s.a;
        float spread = radius / max(nearRadius, 1.0);
        float weight = Cover(nearRadius, r) * smoothstep(1.0, 2.0, nearRadius) * spread * spread;
        near += vec4(s.rgb, 1.0) * weight;
    }

    if (uScatterAsGather == 0)
    {
        FragFar = vec4(far.rgb / far.a, 1.0);
        FragNear = vec4(0.0);
        return;
    }

    // **奥の層は、奥がぼけている画素でだけ有効**。ピントの合った画素・手前の画素の奥の層は、重ねるときに使わない
    // (a = 0 にしておくと、dof-combine.frag が双線形で読んでも混ざらない)。
    bool farValid = center.a >= 0.5 && far.a > 0.0001;
    FragFar = farValid ? vec4(far.rgb / far.a, 1.0) : vec4(0.0);

    // **手前の層は前掛けのアルファで書く**(色 × 覆い)。双線形で読んだり縁でぼかしたりしても、
    // 覆いの無い画素の「意味の無い色」が混ざらない。
    float coverage = clamp(near.a / float(SampleCount), 0.0, 1.0);
    vec3 nearColor = near.a > 0.0001 ? near.rgb / near.a : vec3(0.0);
    FragNear = vec4(nearColor * coverage, coverage);
}
