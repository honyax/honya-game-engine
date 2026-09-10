#version 330 core

// **放射照度マップ**を焼く(Day 36)。拡散反射のための IBL。
//
// 問いはこう。
//
// > 法線が N の面に、環境から入ってくる光は合計でいくらか。
//
//   E(N) = ∫ L(l) (N・l) dω        … 半球ぶん
//
// Day 35 のランバートは、この積分を「平行光源1本」で済ませていた。
// 環境マップがあるなら、あらゆる方向から来る光を全部足せる——それがこの1行。
//
// **重いのは1回だけ**。結果は N だけで決まるので、
// 「方向を渡すと放射照度が返る」小さなキューブマップに焼いておけば、
// 本番のシェーダは 1 回引くだけで済む。
// 32x32 の6面で足りるのは、**この関数がとても滑らか**だから——
// cosθ で重み付けした半球平均なので、細かい模様は最初から消えている。

in vec3 vLocalPos;

out vec4 FragColor;

uniform samplerCube uEnvironment;

const float PI = 3.14159265359;

void main()
{
    // この画素が担当する法線。焼き先のテクセルの方向がそのまま N になる。
    vec3 n = normalize(vLocalPos);

    // N のまわりに接空間を作る。**N が真上でも壊れない選び方**にする
    // (Pbr.Basis と同じ理由。up と N が平行だと外積が 0 になる)。
    vec3 up = abs(n.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 tangent = normalize(cross(up, n));
    vec3 bitangent = cross(n, tangent);

    vec3 irradiance = vec3(0.0);
    int samples = 0;

    // **半球を素直に刻む**。重点サンプリングを使わないのは、
    // 被積分関数が cosθ という広い山1つで、格子でも十分収束するため
    // (Day 35 の鏡面はとがっていたので重点サンプリングが要った)。
    const float step = 0.025;

    for (float phi = 0.0; phi < 2.0 * PI; phi += step)
    {
        for (float theta = 0.0; theta < 0.5 * PI; theta += step)
        {
            // 接空間の方向 → 世界の方向。
            vec3 local = vec3(sin(theta) * cos(phi), sin(theta) * sin(phi), cos(theta));
            vec3 l = (tangent * local.x) + (bitangent * local.y) + (n * local.z);

            // **cos(θ) sin(θ) の2つが掛かる**のがここの肝。
            //   cos(θ) … 面が傾くと光が薄まる(ランバート)
            //   sin(θ) … 球面の格子は極ほど密なので、その補正(立体角 dω = sinθ dθ dφ)
            // sin を忘れると**真上からの光を数えすぎて**、
            // 上向きの面だけ極端に明るくなる。
            // **textureLod で段を 0 に固定する**。ここは texture() ではいけない。
            //
            // 環境マップにはミップを作ってある(事前フィルタが使う)。
            // texture() は「隣の画素と比べて UV がどれだけ動いたか」から段を自動で選ぶが、
            // ここでの l はループの中で大きく動くので、GPU は
            // **とても粗い段を選んでしまう**——結果、地平線の明るい空が
            // 地面側までにじんで、下向きの面が本来より明るく青くなる。
            //
            // 絵としては「なんとなく柔らかい環境光」にしか見えないので、
            // 自己チェックで CPU の積分と突き合わせるまで気づけなかった。
            irradiance += textureLod(uEnvironment, l, 0.0).rgb * cos(theta) * sin(theta);
            samples++;
        }
    }

    // 刻み幅の面積(step * step)を掛けるのと、
    // ランバート BRDF の 1/π を先に済ませてしまうのを、まとめて π / samples にしている。
    // samples は π² / step² 個あるので、π / samples = step² / π ——
    // **「格子の面積を掛けて、π で割る」がこの1行に畳んである**。
    //
    // **1/π を焼き込む流儀**にしたのは、本番のシェーダで
    //   diffuse = irradiance * albedo
    // と書けるようにするため。掛け忘れ・掛けすぎがいちばん起きやすい定数なので、
    // 出口を1つにしておく(textured.frag 側にはもう π が出てこない)。
    FragColor = vec4(PI * irradiance * (1.0 / float(samples)), 1.0);
}
