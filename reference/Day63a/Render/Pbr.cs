using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **Cook-Torrance BRDF の C# 版**(Day 35)。
///
/// 同じ式が <c>shaders/textured.frag</c> にもある。**わざと2か所に書いている**。
///
/// <list type="bullet">
/// <item>
/// GLSL 側は<b>本番</b>。1フレームに数百万回走る。
/// </item>
/// <item>
/// こちらは<b>定義と検算</b>。GPU の中身は覗けないので、
/// 「D は本当に正規化されているか」「入ってきた以上の光を返していないか」を
/// 確かめる場所が CPU 側に要る(<c>RunPbrCheck</c>)。
/// 半球を数千点に刻んで積分する、という検算は GPU では書きにくい。
/// </item>
/// </list>
///
/// <para>
/// 二重管理は本来まずいが、**式が数行で、しかもこの先動かない**(物理の定義そのもの)ので、
/// ここでは検算できる価値のほうを取っている。
/// 片方を直したらもう片方も直すこと。
/// </para>
///
/// <para>
/// <b>BRDF とは何か</b>。「ある向きから来た光のうち、
/// どれだけをある向きへ返すか」を表す関数 <c>f(V, L)</c> で、単位は 1/ステラジアン。
/// 出ていく明るさは <c>Lo = f(V, L) * Li * (N・L)</c>。
/// <c>(N・L)</c> が別に掛かるのは、**面が傾くと同じ光束が広い面積に薄まる**から
/// (Day 9 のランバートで見たのと同じ理由)。
/// </para>
///
/// <para>
/// Cook-Torrance はこれを<b>微小な鏡の集まり</b>(マイクロファセット)として組み立てる。
/// <code>
///   f_spec = D * G * F / (4 * (N・V) * (N・L))
///     D … そのうち何枚が「ちょうど V へ返す向き」を向いているか(法線分布)
///     G … その鏡が隣の凸に隠れていない割合(幾何減衰)
///     F … その鏡がどれだけ反射するか(フレネル)
///     分母 … 微小面の面積と立体角を投影で数え直すときに出る係数
/// </code>
/// 3項とも「割合」なので、掛け算で組み上がるのが気持ちのよいところ。
/// </para>
/// </summary>
internal static class Pbr
{
    /// <summary>
    /// 非金属(誘電体)の垂直反射率。**だいたいの物質で 0.04 前後**。
    ///
    /// 水 0.02、プラスチック 0.04〜0.05、ガラス 0.04、ダイヤモンド 0.17。
    /// 金属だけが 0.5〜1.0 の桁で、しかも**色が付く**(金 (1.00, 0.77, 0.34) など)。
    /// 「金属か否か」を1つの数で持てるのは、この2群がきれいに離れているから。
    /// </summary>
    public const float DielectricF0 = 0.04f;

    /// <summary>
    /// 粗さの下限。0 だと D が発散する(幅 0 の無限に高いピークになる)。
    ///
    /// **完全な鏡は点光源では見えない**——大きさ 0 の光を完全な鏡に映すと、
    /// 幅 0 のハイライトになり、画素の中心に当たるかどうかが運になる。
    /// 実装では下限で潰しておくのが定石。
    /// </summary>
    public const float MinRoughness = 0.045f;

    /// <summary>
    /// 粗さ(0〜1)から、式に入れる <c>α</c> を作る。
    ///
    /// <paramref name="perceptual"/> が true(glTF の流儀)なら <c>α = roughness²</c>。
    /// **2乗するのは、そのほうがスライダーが素直に効くから**という理由しかない。
    /// α をそのまま動かすと 0〜0.2 の間でハイライトが一気に広がり、
    /// 残り 0.2〜1.0 はほとんど見た目が変わらない。
    /// 2乗を挟むと変化が全域に散る——**知覚的に線形**とはこの意味。
    ///
    /// 物理的にはどちらでもよい(α の定義域を写し替えているだけ)が、
    /// **素材が想定している流儀と食い違うと、全部の材質がつるつるに寄る**。
    /// glTF・Unreal・Unity はすべて2乗する側。
    /// </summary>
    public static float Alpha(float roughness, bool perceptual = true)
    {
        float r = Math.Clamp(roughness, MinRoughness, 1.0f);
        return perceptual ? r * r : r;
    }

    /// <summary>
    /// **法線分布関数 D**(GGX / Trowbridge-Reitz)。
    ///
    /// 「微小な鏡のうち、法線が <c>H</c> を向いているものの密度」。
    /// <c>H</c>(ハーフベクトル)は <c>normalize(V + L)</c> で、
    /// **その向きの鏡だけが V へ光を返せる**——反射の法則がそう言っている。
    ///
    /// <code>
    ///   D = α² / (π * ((N・H)²(α² - 1) + 1)²)
    /// </code>
    ///
    /// GGX が Phong や Beckmann を押しのけたのは<b>裾が長い</b>から。
    /// ハイライトの芯のまわりに、ぼんやりした広がりが遠くまで残る。
    /// 実測した材質はどれもこの裾を持っていて、
    /// 「金属なのに CG っぽい」の正体はたいていここだった。
    ///
    /// <b>正規化されている</b>のが定義の要: <c>∫ D(H) (N・H) dω = 1</c>。
    /// 「鏡の総面積が、面に投影すると 1 になる」という意味で、
    /// これが成り立たないとエネルギー保存が最初から破れる。
    /// <see cref="IntegrateNdf"/> がここを数値積分で確かめる。
    /// </summary>
    public static float DistributionGgx(float nDotH, float alpha)
    {
        float a2 = alpha * alpha;
        float d = (nDotH * nDotH * (a2 - 1.0f)) + 1.0f;
        return a2 / (MathF.PI * d * d);
    }

    /// <summary>
    /// 直接光用の <c>k</c>。**知覚的な粗さ(2乗する前)から作る**。
    ///
    /// <c>(r + 1)² / 8</c> という式は Disney が
    /// 「実測と見た目が合うように」選んだ当てはめで、物理からは出てこない。
    /// IBL では <c>α / 2</c> を使う(Day 36)——**同じ G なのに係数が2通りある**
    /// のはそのため。理屈で導けるものと当てはめが混ざっているのが、
    /// このあたりのいちばん座りの悪いところ。
    /// </summary>
    public static float DirectK(float roughness)
    {
        float r = Math.Clamp(roughness, MinRoughness, 1.0f) + 1.0f;
        return r * r / 8.0f;
    }

    /// <summary>
    /// **幾何減衰 G** の片側(Schlick-GGX)。0〜1 の割合。
    ///
    /// <code>
    ///   G1(X) = (N・X) / ((N・X)(1 - k) + k)
    /// </code>
    /// </summary>
    public static float GeometrySchlickGgx(float nDotX, float k)
    {
        return nDotX / ((nDotX * (1.0f - k)) + k);
    }

    /// <summary>
    /// 入りと出の両方ぶんの幾何減衰(Smith)。
    ///
    /// 微小な鏡は互いを隠す。光が入るときに手前の凸に遮られる(shadowing)のと、
    /// 出ていくときに遮られる(masking)のと2つあり、Smith は
    /// **入りと出を独立と見て掛け合わせる**近似を取る。
    ///
    /// 本当は独立ではない(手前の凸は両方を同時に遮る)が、
    /// 相関を入れた式との差は小さく、いまでも実務の既定はこちら。
    /// </summary>
    public static float GeometrySmith(float nDotV, float nDotL, float roughness)
    {
        float k = DirectK(roughness);
        return GeometrySchlickGgx(nDotV, k) * GeometrySchlickGgx(nDotL, k);
    }

    /// <summary>
    /// **フレネル F**(Schlick 近似)。
    ///
    /// 「浅い角度で見るほど、何でも鏡になる」という日常の現象そのもの。
    /// 机も紙も、目の高さすれすれから見ると光沢が出る。
    ///
    /// <code>
    ///   F = F0 + (1 - F0) * (1 - cosθ)^5
    /// </code>
    ///
    /// θ は<b>ハーフベクトルと視線の角</b>で、法線と視線の角ではない。
    /// 反射しているのは微小な鏡なので、基準はその鏡の法線(= H)になる。
    /// ここを <c>N・V</c> で書くと、粗い材質の縁だけが不自然に光る。
    ///
    /// 5乗という半端な指数は Schlick が実測に当てはめて選んだもの。
    /// 正確なフレネルの式には複素屈折率が要るので、そちらは実用に向かない。
    /// </summary>
    public static Vector3 FresnelSchlick(float cosTheta, Vector3 f0)
    {
        float f = MathF.Pow(1.0f - Math.Clamp(cosTheta, 0.0f, 1.0f), 5.0f);
        return f0 + ((Vector3.One - f0) * f);
    }

    /// <summary>
    /// **F0**(垂直から見たときの反射率)を、ベースカラーと金属度から作る。
    ///
    /// これが metallic-roughness ワークフローの心臓部で、たった1行:
    /// <code>
    ///   F0 = mix(vec3(0.04), baseColor, metallic)
    /// </code>
    ///
    /// 言っているのは2つだけ。
    ///   - <b>非金属</b>: 鏡面は白っぽく弱い(0.04)。色は拡散反射が担う
    ///   - <b>金属</b>: 鏡面がベースカラーの色になる。**拡散反射はしない**
    ///
    /// 金属が拡散しないのは、中へ入った光が自由電子にすぐ吸収されて出てこないため。
    ///
    /// 「拡散色 + 鏡面色」を別々に持たせる古い流儀だと
    /// 「拡散も鏡面も真っ白」のような物理的に有り得ない材質が作れてしまう。
    /// **作れないようにしたのが metallic-roughness の値打ち**。
    /// </summary>
    public static Vector3 F0Of(Vector3 baseColor, float metallic, float dielectricF0 = DielectricF0)
    {
        var dielectric = new Vector3(dielectricF0);
        return Vector3.Lerp(dielectric, baseColor, Math.Clamp(metallic, 0.0f, 1.0f));
    }

    /// <summary>
    /// **BRDF を1回評価する**。返すのは <c>f(V, L)</c>(まだ <c>N・L</c> を掛けていない)。
    ///
    /// 呼ぶ側で <c>* lightColor * (N・L)</c> するのが約束。
    /// **BRDF に <c>N・L</c> を含めない**のは、それが BRDF ではなく
    /// レンダリング方程式の側の項だから——含めてしまうと、
    /// 半球積分でエネルギーを確かめるときに何を積分しているのか分からなくなる。
    /// </summary>
    public static Vector3 Evaluate(
        Vector3 n,
        Vector3 v,
        Vector3 l,
        Vector3 baseColor,
        float metallic,
        float roughness,
        float dielectricF0 = DielectricF0,
        bool perceptualRoughness = true)
    {
        return Evaluate(
            n, v, l, baseColor, metallic, roughness,
            out _, out _, dielectricF0, perceptualRoughness);
    }

    /// <summary>
    /// 拡散と鏡面を**分けて受け取る**版。
    ///
    /// 自己チェックが要る形で、たとえば「金属は拡散しない」は
    /// 足し合わせたあとの値からは確かめようがない。
    /// GLSL 側の <c>CookTorrance</c> が out 引数で2つ返しているのと同じ理由。
    /// </summary>
    public static Vector3 Evaluate(
        Vector3 n,
        Vector3 v,
        Vector3 l,
        Vector3 baseColor,
        float metallic,
        float roughness,
        out Vector3 diffuse,
        out Vector3 specular,
        float dielectricF0 = DielectricF0,
        bool perceptualRoughness = true)
    {
        diffuse = Vector3.Zero;
        specular = Vector3.Zero;

        float nDotL = Vector3.Dot(n, l);
        if (nDotL <= 0.0f)
        {
            return Vector3.Zero;
        }

        float nDotV = MathF.Max(Vector3.Dot(n, v), 1e-4f);

        Vector3 h = Vector3.Normalize(v + l);
        float nDotH = MathF.Max(Vector3.Dot(n, h), 0.0f);
        float vDotH = MathF.Max(Vector3.Dot(v, h), 0.0f);

        float alpha = Alpha(roughness, perceptualRoughness);

        float d = DistributionGgx(nDotH, alpha);
        float g = GeometrySmith(nDotV, nDotL, roughness);
        Vector3 f = FresnelSchlick(vDotH, F0Of(baseColor, metallic, dielectricF0));

        specular = d * g * f / (4.0f * nDotV * nDotL);

        // **反射しなかったぶんだけが中に入る**。これがエネルギー保存の要で、
        // 拡散に回せるのは (1 - F)。さらに金属は拡散しないので (1 - metallic)。
        Vector3 kd = (Vector3.One - f) * (1.0f - Math.Clamp(metallic, 0.0f, 1.0f));

        // ランバートの 1/π。**半球に均等にばらまくと積分が π になる**ので、
        // 1 に戻すために割る。Day 9 ではこの π を省いていた
        // (光の強さのほうで吸収していた)が、鏡面と足し合わせる以上もう省けない。
        diffuse = kd * baseColor / MathF.PI;

        return diffuse + specular;
    }

    /// <summary>
    /// **D の正規化を数値積分で確かめる**。返り値が 1 に近ければ合格。
    ///
    /// <c>∫ D(H) (N・H) dω</c> を、半球を刻んで足すだけ。
    /// <c>dω = sinθ dθ dφ</c> なので、<c>sinθ</c> を掛け忘れると
    /// **極のあたりを数えすぎて値が跳ねる**——ここが定番の踏み外し。
    ///
    /// 等方性なので φ には依存しない。1周ぶん(2π)を最後に掛ければ足りる。
    /// α が小さいほど D はとがるので、刻みが粗いと取りこぼす。
    /// </summary>
    public static double IntegrateNdf(float alpha, int steps = 200000)
    {
        double total = 0.0;
        double dTheta = (Math.PI / 2.0) / steps;

        for (int i = 0; i < steps; i++)
        {
            // 区画の中心で代表する(中点則)。端で取ると誤差が片側に寄る。
            double theta = (i + 0.5) * dTheta;
            double nDotH = Math.Cos(theta);

            total += DistributionGgx((float)nDotH, alpha) * nDotH * Math.Sin(theta) * dTheta;
        }

        return total * 2.0 * Math.PI;
    }

    /// <summary>
    /// **Hammersley 点列**の i 番目。0〜1 の2次元の点を、偏りなくばらまく。
    ///
    /// 乱数ではなく<b>低食い違い列</b>(quasi-random)を使う。
    /// 乱数だと点が固まったり空いたりして、同じ計算を2回やると答えが変わる——
    /// **自己チェックが毎回違う数字を出すのは困る**。
    /// Hammersley は決定的で、しかも一様乱数より速く収束する。
    ///
    /// y 座標は van der Corput 列(2進数を左右反転した小数)。
    /// ビットをひっくり返すと 0, 0.5, 0.25, 0.75, 0.125, ... と
    /// **隙間を埋める順**に並ぶ、というのが仕掛け。
    ///
    /// Day 36 の IBL でもそのまま使う(環境マップを事前積分するのに要る)。
    /// </summary>
    public static Vector2 Hammersley(uint i, uint count)
    {
        uint bits = i;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);

        return new Vector2((float)i / count, bits * 2.3283064365386963e-10f);
    }

    /// <summary>
    /// **GGX の重点サンプリング**。<c>D</c> の形に沿ってハーフベクトル H を引く。
    ///
    /// 半球を等間隔に刻んで足す素朴な積分は、<b>粗さが小さいと使い物にならない</b>。
    /// α = 0.0025(粗さ 0.05)のとき D のピークの幅は 0.0025 ラジアン——
    /// 刻みがそれより粗いと、山を丸ごと踏み外すか、
    /// 逆に踏んだ1点に広いマス目の面積を掛けて大きく数えすぎるかのどちらかになる。
    /// 実際、素朴な積分では「返る光 120%」という有り得ない値が出た。
    ///
    /// 解は<b>山のあるところを重点的に引く</b>こと。
    /// D に比例した確率で H を引き、そのぶん重み(確率密度)で割り戻す。
    /// 逆関数法で cosθ を閉じた式で出せるのが GGX の便利なところ:
    /// <code>
    ///   cosθ = sqrt((1 - ξ) / (1 + (α² - 1)ξ))
    /// </code>
    ///
    /// Day 36 の IBL は「環境マップを粗さごとにぼかす」ためにこれをそのまま使う。
    /// </summary>
    public static Vector3 ImportanceSampleGgx(Vector2 xi, Vector3 n, float alpha)
    {
        float a2 = alpha * alpha;

        float phi = MathF.Tau * xi.X;
        float cosTheta = MathF.Sqrt((1.0f - xi.Y) / (1.0f + ((a2 - 1.0f) * xi.Y)));
        float sinTheta = MathF.Sqrt(MathF.Max(1.0f - (cosTheta * cosTheta), 0.0f));

        var local = new Vector3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);

        (Vector3 tangent, Vector3 bitangent) = Basis(n);
        return Vector3.Normalize((tangent * local.X) + (bitangent * local.Y) + (n * local.Z));
    }

    /// <summary>
    /// **方向アルベド**(directional albedo)。
    /// ある視線 <c>V</c> について <c>∫ f(V, L) (N・L) dω_L</c> を積分したもの。
    ///
    /// 意味は「あらゆる方向から明るさ 1 の光が来たとき、V の向きへどれだけ返すか」。
    /// **1 を超えたら光を作り出している**ので、物理として破綻している。
    ///
    /// 逆に 1 に届かないぶんは「失われた光」で、これは破綻ではなく<b>近似の限界</b>。
    /// Cook-Torrance は微小な鏡で1回だけ跳ねる前提なので、
    /// 凹凸の中で2回3回と跳ね返る光をまるごと落としている。
    /// **粗いほど損が大きくなる**のがその証拠で、
    /// ざらざらの金属が実物より暗くなるという形で絵に出る
    /// (多重散乱の補正は Day 36 の発展課題)。
    ///
    /// <para>
    /// <b>拡散と鏡面で引き方を変える</b>。同じ半球の積分でも、
    /// 被積分関数の山がある場所が違うため。
    ///   - 拡散 … cos に比例して引く(山が広い)
    ///   - 鏡面 … D に比例して引く(山がとがっている)
    /// 引き方と割り戻す確率密度が対応していれば、どちらも同じ値に収束する。
    /// </para>
    /// </summary>
    public static Vector3 DirectionalAlbedo(
        Vector3 n,
        Vector3 v,
        Vector3 baseColor,
        float metallic,
        float roughness,
        int samples = 8192)
    {
        float nDotV = MathF.Max(Vector3.Dot(n, v), 1e-4f);
        float alpha = Alpha(roughness);
        Vector3 f0 = F0Of(baseColor, metallic);

        (Vector3 tangent, Vector3 bitangent) = Basis(n);

        Vector3 diffuse = Vector3.Zero;
        Vector3 specular = Vector3.Zero;

        for (uint i = 0; i < samples; i++)
        {
            Vector2 xi = Hammersley(i, (uint)samples);

            // --- 拡散: cos に比例して引く ---
            //
            // pdf = cosθ/π なので、推定値は f_diff * cosθ / pdf = f_diff * π。
            // f_diff = kD * albedo / π だから、**π が打ち消えて kD * albedo が残る**。
            {
                float cosTheta = MathF.Sqrt(1.0f - xi.Y);
                float sinTheta = MathF.Sqrt(xi.Y);
                float phi = MathF.Tau * xi.X;

                Vector3 l = Vector3.Normalize(
                    (tangent * (sinTheta * MathF.Cos(phi)))
                    + (bitangent * (sinTheta * MathF.Sin(phi)))
                    + (n * cosTheta));

                Vector3 h = Vector3.Normalize(v + l);
                float vDotH = MathF.Max(Vector3.Dot(v, h), 0.0f);

                Vector3 f = FresnelSchlick(vDotH, f0);
                Vector3 kd = (Vector3.One - f) * (1.0f - Math.Clamp(metallic, 0.0f, 1.0f));

                diffuse += kd * baseColor;
            }

            // --- 鏡面: D に比例して引く ---
            //
            // pdf(L) = D(N・H)(N・H) / (4 (V・H)) なので、
            // f_spec * (N・L) / pdf を約分すると **G * F * (V・H) / ((N・V)(N・H))** になる。
            // D がきれいに消えるのが重点サンプリングの気持ちよさで、
            // **とがった山を刻む必要が無くなる**。
            {
                Vector3 h = ImportanceSampleGgx(xi, n, alpha);

                // 反射させて L を作る。H が「その鏡の法線」なので、V を H で折り返す。
                Vector3 l = Vector3.Normalize((2.0f * Vector3.Dot(v, h) * h) - v);

                float nDotL = Vector3.Dot(n, l);
                if (nDotL <= 0.0f)
                {
                    continue;
                }

                float nDotH = MathF.Max(Vector3.Dot(n, h), 1e-4f);
                float vDotH = MathF.Max(Vector3.Dot(v, h), 1e-4f);

                float g = GeometrySmith(nDotV, nDotL, roughness);
                Vector3 f = FresnelSchlick(vDotH, f0);

                specular += f * (g * vDotH / (nDotV * nDotH));
            }
        }

        return (diffuse + specular) / samples;
    }

    /// <summary>
    /// **IBL 用の <c>k</c>**(Day 36)。<see cref="DirectK"/> と<b>別の式</b>になる。
    ///
    /// <code>
    ///   直接光: k = (roughness + 1)² / 8
    ///   IBL   : k = roughness² / 2 = α / 2
    /// </code>
    ///
    /// 同じ幾何減衰なのに係数が2通りある、というのは Day 35 の要点5 で触れたとおり。
    /// 直接光のほうは Disney の当てはめで、こちらは
    /// 「あらゆる方向から来る光を積分する」場合に合うよう Karis が選んだもの。
    ///
    /// **取り違えると金属の縁が暗くなりすぎる**(直接光用の k は IBL では大きすぎる)。
    /// 症状が地味なので、間違えたまま気づかないことが多い。
    /// </summary>
    public static float IblK(float roughness) => Alpha(roughness) / 2.0f;

    /// <summary>IBL 用の幾何減衰。<see cref="GeometrySmith"/> と k だけが違う。</summary>
    public static float GeometrySmithIbl(float nDotV, float nDotL, float roughness)
    {
        float k = IblK(roughness);
        return GeometrySchlickGgx(nDotV, k) * GeometrySchlickGgx(nDotL, k);
    }

    /// <summary>
    /// **粗さを考えたフレネル**(Day 36)。環境光の鏡面の割合を出すのに使う。
    ///
    /// <code>
    ///   F = F0 + (max(1 - roughness, F0) - F0) * (1 - cosθ)^5
    /// </code>
    ///
    /// 素の <see cref="FresnelSchlick"/> は、浅い角度で必ず 1 に飛ぶ。
    /// 直接光ならそれでよい(1本の光線の話なので)が、
    /// 環境光に使うと**粗い材質の縁が真っ白に光る**——
    /// ざらざらの面では、浅く当たった光もあちこちへ散るので、
    /// 実際にはそこまで強く返らない。
    ///
    /// そこで上限を <c>1 - roughness</c> で抑える。
    /// 物理から導いた式ではなく、Sébastien Lagarde が
    /// 「見た目が破綻しない」ように置いた当てはめ。
    /// </summary>
    public static Vector3 FresnelSchlickRoughness(float cosTheta, Vector3 f0, float roughness)
    {
        float f = MathF.Pow(1.0f - Math.Clamp(cosTheta, 0.0f, 1.0f), 5.0f);
        var ceiling = new Vector3(
            MathF.Max(1.0f - roughness, f0.X),
            MathF.Max(1.0f - roughness, f0.Y),
            MathF.Max(1.0f - roughness, f0.Z));

        return f0 + ((ceiling - f0) * f);
    }

    /// <summary>
    /// **分割和の右半分**(Day 36)。BRDF を環境から切り離して事前に表にする。
    ///
    /// Day 35 の <see cref="DirectionalAlbedo"/> と積分の中身はほとんど同じだが、
    /// <b>フレネルを2つに割る</b>ところが違う。Schlick の式
    /// <code>
    ///   F = F0 + (1 - F0) * (1 - V・H)^5
    /// </code>
    /// は <c>F0</c> について1次なので、積分の外へ出せる:
    /// <code>
    ///   ∫ f (N・L) dω = F0 * A + B
    ///     A = ∫ (1 - (1-V・H)^5) * G_vis dω
    ///     B = ∫      (1-V・H)^5  * G_vis dω
    /// </code>
    /// **A と B は F0 に依存しない**——つまり材質の色に依存しない。
    /// 残った変数は <c>N・V</c> と <c>roughness</c> の2つだけなので、
    /// <b>2次元の表1枚</b>で全材質ぶんをまかなえる。これが split-sum の値打ち。
    ///
    /// <para>
    /// 返すのは <c>(A, B)</c>。使う側は
    /// <code>
    ///   specular = prefiltered * (F0 * A + B)
    /// </code>
    /// と書く。表はテクスチャ(RG16F)に焼いて GPU へ渡す。
    /// </para>
    ///
    /// <para>
    /// <b>CPU で焼いている</b>のは、Day 35 で書いた重点サンプリングがそのまま使えるから。
    /// GPU でやるなら5本目のシェーダが要るところを、既にある関数の組み替えで済ませた——
    /// Day 35 の計画書で「Day 36 の LUT がまさにそれ」と書いた回収になる。
    /// </para>
    /// </summary>
    public static Vector2 IntegrateBrdf(float nDotV, float roughness, int samples = 1024)
    {
        nDotV = Math.Clamp(nDotV, 1e-3f, 1.0f);

        // **N を +Z に固定して考える**。等方性なので方位は自由に取ってよく、
        // V を XZ 平面に寝かせておけば N・V だけで系が決まる。
        var n = Vector3.UnitZ;
        var v = new Vector3(MathF.Sqrt(1.0f - (nDotV * nDotV)), 0.0f, nDotV);

        float alpha = Alpha(roughness);

        float a = 0.0f;
        float b = 0.0f;

        for (uint i = 0; i < samples; i++)
        {
            Vector2 xi = Hammersley(i, (uint)samples);
            Vector3 h = ImportanceSampleGgx(xi, n, alpha);
            Vector3 l = Vector3.Normalize((2.0f * Vector3.Dot(v, h) * h) - v);

            float nDotL = l.Z;
            if (nDotL <= 0.0f)
            {
                continue;
            }

            float nDotH = MathF.Max(h.Z, 0.0f);
            float vDotH = MathF.Max(Vector3.Dot(v, h), 0.0f);

            // pdf で割ったあとに残るぶん。D が約分で消えるのは Day 35 と同じ。
            float g = GeometrySmithIbl(nDotV, nDotL, roughness);
            float visibility = (g * vDotH) / (nDotH * nDotV);

            float fc = MathF.Pow(1.0f - vDotH, 5.0f);

            a += (1.0f - fc) * visibility;
            b += fc * visibility;
        }

        return new Vector2(a / samples, b / samples);
    }

    /// <summary>
    /// <paramref name="n"/> に垂直な2本を作る。**n が ±Y でも壊れない選び方**にしてある。
    /// </summary>
    private static (Vector3 Tangent, Vector3 Bitangent) Basis(Vector3 n)
    {
        Vector3 up = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, n));
        return (tangent, Vector3.Cross(n, tangent));
    }
}
