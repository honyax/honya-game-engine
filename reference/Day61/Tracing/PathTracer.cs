using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// <b>パストレーサ</b>。今日の主役(要点1〜5)。
///
/// <para>
/// Whitted 法との違いは1つだけ。<b>面に当たったとき、行き先を「全部」ではなく「1つ」引く</b>。
/// 拡散面は無数の向きへ散るが、そのうちの1本だけを乱数で選び、選ばれる確率で割る。
/// 1本ずつでは当たり外れが激しい(ノイズ)が、<b>何千本も撃って平均すると、
/// 散らばる向きを全部足したのと同じ値に近づく</b>(モンテカルロ積分。要点2)。
/// </para>
/// <code>
///   Whitted(木)            パストレーシング(道)
///   カメラ ─┬─ 反射         カメラ ── 散乱 ── 散乱 ── 散乱 ── 空
///          ├─ 屈折         (枝分かれしないので、深さに対して光線の数が線形)
///          └─ 影×光源の数
/// </code>
/// <para>
/// これで Whitted 法の3つのごまかしが全部消える。
/// </para>
/// <list type="bullet">
/// <item><b>環境光の一定値</b> → 周りから跳ね返って来る光を本当に数える(色移り。場面4)</item>
/// <item><b>点光源のハイライト</b> → 面光源が本当に鏡に映り、影の縁が本当にぼける(場面4・6)</item>
/// <item><b>ガラスの真っ黒な影</b> → ガラスを通った光が床に集まる(集光。場面6)</item>
/// </list>
///
/// <para>
/// <b>再帰ではなくループ</b>で書いてある。枝分かれしないので再帰にする必要が無く、
/// ループなら深さの上限を何千にしてもスタックが溢れない。Day 61 で GLSL に写すときも、
/// GLSL には再帰が無いのでこの形でないと移せない。
/// </para>
/// </summary>
internal sealed class PathTracer : Tracer
{
    /// <summary>
    /// ロシアンルーレットを始める深さ(要点5)。
    ///
    /// <para>
    /// 最初の数回まで打ち切ると、明るい場所でもノイズが出る(道が短いうちは、まだ誰の色も決まっていない)。
    /// 3 回跳ね返った先の光は、たいてい元の1割以下しか画素に効かないので、そこから間引く。
    /// </para>
    /// </summary>
    private const int RouletteStartDepth = 3;

    /// <summary>
    /// ロシアンルーレットで続ける確率の下限。
    /// 0 まで許すと「打ち切られなかったときの重み(1/確率)」が跳ね上がり、
    /// <b>たまに1画素だけ極端に明るい点</b>(firefly)が出る。
    /// </summary>
    private const float MinSurvivalProbability = 0.05f;

    public PathTracer(Scene scene, RenderSettings settings)
        : base(scene, settings)
    {
    }

    /// <summary>
    /// 1本の道を最後まで歩いて、カメラに届く光(線形)を返す。
    ///
    /// <para>
    /// 持ち回る値は2つだけ。
    /// </para>
    /// <list type="bullet">
    /// <item><c>radiance</c> … ここまでに拾った光の合計。<b>答え</b></item>
    /// <item><c>throughput</c> … 「この先で拾った光が、画素にどれだけ効くか」の倍率。
    /// 面で跳ね返るたびに <c>BRDF × cosθ / pdf</c> を掛けていく。最初は 1</item>
    /// </list>
    /// <para>
    /// レンダリング方程式の入れ子(「ここへ来る光」の中にまた「ここへ来る光」がある)を、
    /// <b>掛け算を溜めながら前へ進む形</b>にほどいたもの。再帰で書けば
    /// <c>L = Le + ∫ f × L' × cos dω</c> の中で L' を呼ぶことになるが、
    /// 枝が1本しか無いなら、掛かる係数を先に掛けておけば同じことになる(要点1)。
    /// </para>
    /// </summary>
    public override Vector3 Trace(in Ray ray, ref Rng rng, ref RayCounters counters, TraceLog? log)
    {
        Ray current = ray;
        Vector3 radiance = Vector3.Zero;
        Vector3 throughput = Vector3.One;

        // 直前の跳ね返りが鏡面だったか。カメラの光線は「鏡面の後」と同じ扱いにする
        // (そうしないと、光源を直接見ているのに光らない)。
        bool specularBounce = true;
        string label = "カメラ";

        for (int depth = 0; ; depth++)
        {
            if (!TryHit(current, ref counters, out SurfaceHit hit, out Shape shape))
            {
                // 何にも当たらなかった = 無限に遠い空を見ている。
                // 空は NEE でつないでいないので、ここは条件なしでいつも足す。
                Vector3 sky = Scene.Sky(current.Direction);
                radiance += throughput * sky;
                log?.Add(depth, $"{label} → 空 ({sky.X:F2}, {sky.Y:F2}, {sky.Z:F2})");
                break;
            }

            Material material = hit.Material;
            log?.Add(depth, $"{label} → {shape.Name}  {hit.Distance:F3}m 先{(hit.FrontFace ? "" : "(内側から)")}");

            if (material.IsEmissive)
            {
                // 光る面に当たった。NEE で同じ光をもう数えているなら、ここで足すと<b>2重になる</b>(要点4)。
                // 数え済みなのは「直前が拡散面だった」とき。鏡面の後は NEE が使えていないので、ここで足すしかない。
                bool alreadyCounted = Settings.NextEventEstimation && !specularBounce;
                if (!alreadyCounted)
                {
                    radiance += throughput * material.Emission;
                }

                log?.Add(depth + 1, alreadyCounted
                    ? "発光: NEE で数え済みなので足さない(2重に数えないため)"
                    : $"発光 ({material.Emission.X:F1}, {material.Emission.Y:F1}, {material.Emission.Z:F1}) を足す");

                // 今日の面光源は albedo 0(光るだけで跳ね返さない)。ここで道は終わる。
                break;
            }

            // --- 拡散面: NEE(直接光)を足してから、次の向きを1つ引く ---
            if (material.Kind == MaterialKind.Diffuse)
            {
                Vector3 albedo = material.AlbedoAt(hit.Point);

                if (Settings.NextEventEstimation)
                {
                    radiance += throughput * SampleDirectLight(hit, albedo, depth, ref rng, ref counters, log);
                }

                if (depth >= Settings.MaxDepth)
                {
                    log?.Add(depth + 1, "散乱: 深さの上限なので追わない(この先の光は届かない)");
                    break;
                }

                // コサイン重点サンプリング(要点3)。
                // 重み = BRDF × cosθ / pdf = (albedo/π) × cosθ / (cosθ/π) = albedo。
                // <b>π も cos も約分で消える</b>のがこの選び方の値打ちで、
                // 一様に引くと重み = albedo × 2cosθ になって、斜めの向きほど暗いばらつきが残る。
                Vector3 scattered = Sampler.CosineHemisphere(hit.Normal, ref rng);
                throughput *= albedo;
                specularBounce = false;
                label = "散乱";
                counters.Scatter++;
                current = SpawnRay(hit.Point, hit.Normal, scattered);

                log?.Add(depth + 1, $"散乱(拡散) 重み ({albedo.X:F2}, {albedo.Y:F2}, {albedo.Z:F2}) → 累積 ({throughput.X:F3}, {throughput.Y:F3}, {throughput.Z:F3})");
            }
            else
            {
                if (depth >= Settings.MaxDepth)
                {
                    log?.Add(depth + 1, "反射・屈折: 深さの上限なので追わない");
                    break;
                }

                current = ScatterSpecular(current, hit, depth, ref rng, ref counters, log, ref throughput, out label);
                specularBounce = true;
            }

            // --- ロシアンルーレット(要点5)---
            if (Settings.RussianRoulette && depth >= RouletteStartDepth)
            {
                // 続ける確率を「この先どれだけ効くか」= throughput の最大成分にする。
                // 暗くなった道ほど切られやすく、切られなかった道は 1/確率 倍に重くなる。
                float survival = Math.Clamp(
                    MathF.Max(throughput.X, MathF.Max(throughput.Y, throughput.Z)),
                    MinSurvivalProbability,
                    1.0f);

                if (rng.NextFloat() >= survival)
                {
                    log?.Add(depth + 1, $"ロシアンルーレット: 続ける確率 {survival:F3} → 打ち切り");
                    break;
                }

                throughput /= survival;
                log?.Add(depth + 1, $"ロシアンルーレット: 続ける確率 {survival:F3} → 続ける(重みを {1.0f / survival:F2} 倍)");
            }
        }

        return radiance;
    }

    /// <summary>
    /// <b>NEE</b>(next event estimation。要点4)。拡散面から<b>光源へ直接つないだ</b>ときに届く光を返す。
    /// 返す値には BRDF(<c>albedo/π</c>)まで掛かっていて、呼ぶ側は throughput を掛けるだけでよい。
    ///
    /// <para>
    /// 「散乱の光線が偶然光源に当たるのを待つ」のに比べて、<b>必ず光源につなぐ</b>ので当たり外れが小さい。
    /// 光源が小さいほど差が大きく、場面4(天井の球)では同じサンプル数でのノイズが 1/5 以下になる(自己チェック16)。
    /// </para>
    /// <para>
    /// <b>光源が複数あるときは1つだけ選ぶ</b>。全部につなぐと光源の数だけ影の光線が要るが、
    /// 1つを 1/N の確率で選んで N 倍すれば、平均は同じで光線は1本で済む。
    /// これも「全部足す代わりに1つ引いて確率で割る」というモンテカルロの同じ手口(要点2)。
    /// </para>
    /// <para>
    /// 選び方を明るさや距離で重み付けすると、もっとノイズが減る(光源が何十個もある場面では必須)。
    /// 今日は場面の光源が1〜3個なので、一様に選んでいる。
    /// </para>
    /// </summary>
    private Vector3 SampleDirectLight(in SurfaceHit hit, Vector3 albedo, int depth, ref Rng rng, ref RayCounters counters, TraceLog? log)
    {
        int pointCount = Scene.Lights.Count;
        int total = pointCount + Scene.AreaLights.Count;
        if (total == 0)
        {
            return Vector3.Zero;
        }

        Vector3 brdf = albedo / MathF.PI;
        int pick = rng.NextInt(total);

        // --- 点光源。大きさが 0 なので「向きを引く」余地は無く、必ずその1点へつなぐ ---
        if (pick < pointCount)
        {
            PointLight light = Scene.Lights[pick];
            Vector3 offset = light.Position - hit.Point;
            float distance = offset.Length();
            Vector3 toLight = offset / distance;

            float cosTheta = Vector3.Dot(hit.Normal, toLight);
            if (cosTheta <= 0.0f)
            {
                log?.Add(depth + 1, $"NEE → {light.Name}: 面の裏側。光線は出さない");
                return Vector3.Zero;
            }

            counters.Shadow++;
            var shadowRay = new Ray(OffsetAlong(hit.Point, hit.Normal), toLight);
            if (Scene.FindOccluder(shadowRay, distance, ref counters) is Shape occluder)
            {
                log?.Add(depth + 1, $"NEE → {light.Name}: {occluder.Name} に遮られた");
                return Vector3.Zero;
            }

            // 逆2乗則と cosθ は Day 59 の点光源とまったく同じ式。
            // 最後の × total は「N 個から1個選んだ」ぶんの割り戻し。
            Vector3 irradiance = light.Color * (light.Intensity / (distance * distance) * cosTheta);
            Vector3 contribution = brdf * irradiance * total;
            log?.Add(depth + 1, $"NEE → {light.Name}: 届いた  寄与 ({contribution.X:F3}, {contribution.Y:F3}, {contribution.Z:F3})");
            return contribution;
        }

        // --- 面光源。光る形の上へ向かう向きを引く ---
        Shape lightShape = Scene.AreaLights[pick - pointCount];
        if (!lightShape.TrySampleDirection(hit.Point, ref rng, out Vector3 direction, out float lightDistance, out float pdf) || pdf <= 0.0f)
        {
            return Vector3.Zero;
        }

        float cosSurface = Vector3.Dot(hit.Normal, direction);
        if (cosSurface <= 0.0f)
        {
            log?.Add(depth + 1, $"NEE → {lightShape.Name}: 面の裏側");
            return Vector3.Zero;
        }

        counters.Shadow++;
        var ray = new Ray(OffsetAlong(hit.Point, hit.Normal), direction);

        // 光源自身に当たって「遮られた」と判定されないよう、わずかに手前で打ち切る。
        float maxDistance = MathF.Max(lightDistance - 2.0f * SurfaceOffset, 0.0f);
        if (Scene.FindOccluder(ray, maxDistance, ref counters) is Shape blocker)
        {
            log?.Add(depth + 1, $"NEE → {lightShape.Name}: {blocker.Name} に遮られた");
            return Vector3.Zero;
        }

        // 寄与 = BRDF × 光源の放射輝度 × cosθ / pdf。
        // pdf は立体角あたりなので、逆2乗則はここに<b>書かれていない</b>——
        // 遠い光源ほど覆う円錐が細くなって pdf が大きくなり、割った結果が自動的に小さくなる(要点4)。
        Vector3 areaContribution = brdf * lightShape.Material.Emission * (cosSurface / pdf) * total;
        log?.Add(depth + 1, $"NEE → {lightShape.Name}: 届いた  pdf {pdf:F3}  寄与 ({areaContribution.X:F3}, {areaContribution.Y:F3}, {areaContribution.Z:F3})");
        return areaContribution;
    }

    /// <summary>
    /// 金属とガラス。<b>跳ね返る向きが決まっている</b>(鏡面)ので、乱数で引くのは
    /// ガラスの「反射と屈折のどちらへ行くか」だけ(要点5)。
    ///
    /// <para>
    /// Whitted 法はガラスで木を2本に分けたが、パストレーシングは<b>確率 F で反射、1−F で屈折</b>と
    /// 1本に潰す。反射を選んだときの寄与は <c>F × L / F = L</c>、屈折なら <c>(1−F) × L / (1−F) = L</c> で、
    /// どちらも重みが 1 のまま。<b>平均すれば木と同じ</b>になり、光線の数は深さに対して線形で済む。
    /// </para>
    /// </summary>
    private Ray ScatterSpecular(
        in Ray ray,
        in SurfaceHit hit,
        int depth,
        ref Rng rng,
        ref RayCounters counters,
        TraceLog? log,
        ref Vector3 throughput,
        out string label)
    {
        Material material = hit.Material;
        float cosI = Vector3.Dot(hit.Normal, hit.ToViewer);

        if (material.Kind == MaterialKind.Metal)
        {
            Vector3 reflectance = MetalFresnel(cosI, material.Albedo);
            throughput *= reflectance;
            label = "反射";
            counters.Reflection++;
            log?.Add(depth + 1, $"反射(金属) 重み ({reflectance.X:F2}, {reflectance.Y:F2}, {reflectance.Z:F2}) → 累積 ({throughput.X:F3}, {throughput.Y:F3}, {throughput.Z:F3})");
            return SpawnRay(hit.Point, hit.Normal, Optics.Reflect(ray.Direction, hit.Normal));
        }

        // ガラス。外側から当たったなら空気からガラスへ、内側からならガラスから空気へ。
        float n1 = hit.FrontFace ? 1.0f : material.Ior;
        float n2 = hit.FrontFace ? material.Ior : 1.0f;
        bool canRefract = Optics.TryRefract(ray.Direction, hit.Normal, n1 / n2, out Vector3 refracted);
        float reflectance2 = canRefract ? DielectricFresnel(cosI, n1, n2) : 1.0f;

        if (rng.NextFloat() < reflectance2)
        {
            label = canRefract ? "反射" : "全反射";
            counters.Reflection++;
            log?.Add(depth + 1, $"屈折率 {n1:F2} → {n2:F2}  反射の確率 {reflectance2:F3} → {label}を選んだ(重みは 1 のまま)");
            return SpawnRay(hit.Point, hit.Normal, Optics.Reflect(ray.Direction, hit.Normal));
        }

        // 屈折の光線は面の向こう側へ進むので、始点も向こう側(法線の逆)へ浮かせる。
        label = "屈折";
        counters.Refraction++;
        log?.Add(depth + 1, $"屈折率 {n1:F2} → {n2:F2}  反射の確率 {reflectance2:F3} → 屈折を選んだ(重みは 1 のまま)");
        return SpawnRay(hit.Point, -hit.Normal, refracted);
    }
}
