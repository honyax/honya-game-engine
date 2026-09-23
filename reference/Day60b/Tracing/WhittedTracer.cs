using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// <b>Whitted 法のレイトレーサ</b>(1980)。Day 59 の主役で、今日は<b>比べる相手</b>(A キー)。
///
/// <para>
/// 画素の色を、次の<b>光線の木</b>で決める(Day 59 の要点3)。
/// </para>
/// <code>
///   カメラの光線 ─┬─ 拡散面に当たった → 光源ごとに影の光線を1本。見えた光源の分だけ明るくする(ここで枝は終わり)
///                ├─ 金属に当たった   → 反射の光線を1本(同じことを繰り返す)
///                ├─ ガラスに当たった → 反射の光線と屈折の光線の2本。フレネルの割合で混ぜる
///                └─ 何にも当たらない → 空の色
/// </code>
/// <para>
/// <b>拡散面から反射の枝を出さない</b>のが、この方法の割り切り。ざらざらした面は光をあらゆる向きへ散らすので、
/// 1本の反射の光線では表せない。だから拡散面は「光源から直接届いた光」だけで色を決め、
/// 周りの物体から跳ね返ってきた光は一定の環境光(<see cref="Scene.Ambient"/>)で済ませる。
/// <b>その散らばる向きを乱数で1本ずつ選び、何本も平均する</b>のが今日の <see cref="PathTracer"/>。
/// </para>
/// <para>
/// <b>Day 60 での変更は3つだけ。</b>共通部分を <see cref="Tracer"/> へ引き上げ、
/// 場面への問い合わせに <see cref="RayCounters"/> を渡すようになり、
/// 光る面(<see cref="Material.Emission"/>)をそのまま色に足すようになった。
/// 木の追い方そのものは Day 59 のまま。
/// </para>
/// </summary>
internal sealed class WhittedTracer : Tracer
{
    public WhittedTracer(Scene scene, RenderSettings settings)
        : base(scene, settings)
    {
    }

    /// <summary>カメラの光線から木を追う。乱数は使わない(Whitted 法は決定的)。</summary>
    public override Vector3 Trace(in Ray ray, ref Rng rng, ref RayCounters counters, TraceLog? log)
        => Trace(ray, 0, ref counters, log, "カメラ");

    /// <summary>
    /// 光線を1本追って、その光線に沿ってやって来る光の色(線形)を返す。<b>反射と屈折で自分自身を呼ぶ</b>(再帰)。
    /// </summary>
    /// <param name="depth">カメラから何回跳ね返ったか。カメラの光線は 0。</param>
    /// <param name="label">1画素を追ったときに、この光線を何と呼ぶか(カメラ / 反射 / 屈折 / 全反射)。</param>
    public Vector3 Trace(in Ray ray, int depth, ref RayCounters counters, TraceLog? log, string label)
    {
        if (!TryHit(ray, ref counters, out SurfaceHit hit, out Shape shape))
        {
            log?.Add(depth, $"{label} → 空");
            return Scene.Sky(ray.Direction);
        }

        log?.Add(depth, $"{label} → {shape.Name}  {hit.Distance:F3}m 先{(hit.FrontFace ? "" : "(内側から)")}");

        return hit.Material.Kind switch
        {
            MaterialKind.Metal => ShadeMetal(ray, hit, depth, ref counters, log),
            MaterialKind.Glass => ShadeGlass(ray, hit, depth, ref counters, log),
            _ => ShadeDiffuse(hit, depth, ref counters, log),
        };
    }

    /// <summary>
    /// 拡散面(Day 59 の要点3)。<b>発光 + 環境光 + 見えた光源ごとの (ランバート + ハイライト)</b>。反射の枝は出さない。
    ///
    /// <para>
    /// 拡散の BRDF を <c>albedo / π</c> にしているのは、点光源の明るさ(W/sr)と単位を揃えるため。
    /// 半球の全部の向きへ均等に散らした光を合計すると、ちょうど albedo 倍になる割り方が 1/π になる。
    /// <b><see cref="PathTracer"/> も同じ BRDF を使う</b>ので、同じ場面の直接光の明るさが一致する(自己チェック15)。
    /// </para>
    /// </summary>
    private Vector3 ShadeDiffuse(in SurfaceHit hit, int depth, ref RayCounters counters, TraceLog? log)
    {
        Material material = hit.Material;
        Vector3 albedo = material.AlbedoAt(hit.Point);

        // 光る面は、その放射輝度をそのまま返す(Day 60 で追加)。
        // Whitted 法にとって面光源は「明るく塗った面」でしかなく、そこから周りへ光は飛ばない。
        Vector3 color = material.Emission + albedo * Scene.Ambient;

        foreach (PointLight light in Scene.Lights)
        {
            if (!TryReachLight(hit, light, depth, ref counters, log, out Vector3 toLight, out Vector3 irradiance))
            {
                continue;
            }

            Vector3 halfway = Vector3.Normalize(hit.ToViewer + toLight);
            float specular = material.Specular * BlinnPhong(hit.Normal, halfway, material.Shininess);
            color += (albedo / MathF.PI + new Vector3(specular)) * irradiance;
        }

        return color;
    }

    /// <summary>
    /// 金属(Day 59 の要点3)。<b>反射の光線を1本</b>出し、返ってきた色に反射色(フレネル)を掛ける。
    /// </summary>
    private Vector3 ShadeMetal(in Ray ray, in SurfaceHit hit, int depth, ref RayCounters counters, TraceLog? log)
    {
        Vector3 color = SpecularHighlights(hit, depth, ref counters, log);

        if (depth >= Settings.MaxDepth)
        {
            log?.Add(depth + 1, "反射: 深さの上限なので追わない(黒)");
            return color;
        }

        float cosI = Vector3.Dot(hit.Normal, hit.ToViewer);
        Vector3 reflectance = MetalFresnel(cosI, hit.Material.Albedo);

        counters.Reflection++;
        Ray reflected = SpawnRay(hit.Point, hit.Normal, Optics.Reflect(ray.Direction, hit.Normal));
        log?.Add(depth + 1, $"反射の割合 ({reflectance.X:F2}, {reflectance.Y:F2}, {reflectance.Z:F2})");
        color += reflectance * Trace(reflected, depth + 1, ref counters, log, "反射");
        return color;
    }

    /// <summary>
    /// ガラス(Day 59 の要点4・5)。<b>反射と屈折の2本</b>に枝分かれし、フレネルの割合 F と 1 − F で混ぜる。
    ///
    /// <para>
    /// 光は面で消えも増えもしないので、跳ね返った分と中へ入った分を足すと必ず元の量になる(F + (1 − F) = 1)。
    /// 全反射のときは屈折の枝が無く、F = 1 で全部が反射に回る。
    /// <b><see cref="PathTracer"/> は、この2本を「確率 F で反射、1−F で屈折」と1本に潰す</b>(要点5)。
    /// </para>
    /// </summary>
    private Vector3 ShadeGlass(in Ray ray, in SurfaceHit hit, int depth, ref RayCounters counters, TraceLog? log)
    {
        // 外側から当たったなら空気(1.0)からガラスへ、内側からならガラスから空気へ。
        float n1 = hit.FrontFace ? 1.0f : hit.Material.Ior;
        float n2 = hit.FrontFace ? hit.Material.Ior : 1.0f;
        float cosI = Vector3.Dot(hit.Normal, hit.ToViewer);

        bool canRefract = Optics.TryRefract(ray.Direction, hit.Normal, n1 / n2, out Vector3 refracted);
        float reflectance = canRefract ? DielectricFresnel(cosI, n1, n2) : 1.0f;

        if (log is not null)
        {
            float angle = MathF.Acos(Math.Clamp(cosI, 0.0f, 1.0f)) * 180.0f / MathF.PI;
            log.Add(depth + 1, canRefract
                ? $"屈折率 {n1:F2} → {n2:F2}  入射角 {angle:F1}度  反射 {reflectance:F3} / 透過 {1.0f - reflectance:F3}"
                : $"屈折率 {n1:F2} → {n2:F2}  入射角 {angle:F1}度  全反射(透過の角度が無い)");
        }

        // ハイライトは外から見た面にだけ付ける。内側の面から見ると光源は面の裏にあり、どのみち届かない。
        Vector3 color = hit.FrontFace ? SpecularHighlights(hit, depth, ref counters, log) : Vector3.Zero;

        if (depth >= Settings.MaxDepth)
        {
            log?.Add(depth + 1, "反射・屈折: 深さの上限なので追わない(黒)");
            return color;
        }

        // 反射の割合が 0 の枝は追っても何も足されないので、光線を出さない。
        // 屈折率 1.00 の玉(正確なフレネルなら F = 0)では、ここで反射の枝がまるごと消える。
        if (reflectance > 0.0f)
        {
            counters.Reflection++;
            Ray reflectedRay = SpawnRay(hit.Point, hit.Normal, Optics.Reflect(ray.Direction, hit.Normal));
            color += reflectance * Trace(reflectedRay, depth + 1, ref counters, log, canRefract ? "反射" : "全反射");
        }

        if (canRefract && reflectance < 1.0f)
        {
            // 屈折の光線は面の<b>向こう側</b>へ進むので、始点も向こう側(法線の逆)へ浮かせる。
            // 手前側へ浮かせると、出てすぐ同じ面に当たり、ガラスに入れないまま跳ね返り続ける。
            counters.Refraction++;
            Ray refractedRay = SpawnRay(hit.Point, -hit.Normal, refracted);
            color += (1.0f - reflectance) * Trace(refractedRay, depth + 1, ref counters, log, "屈折");
        }

        return color;
    }

    /// <summary>
    /// 金属とガラスに付ける、点光源のハイライト(<see cref="Material.Shininess"/> の説明にある「ごまかし」)。
    /// 強さにはフレネルを掛ける。<b>屈折率 1.00 のガラスは、ハイライトまで含めて本当に見えなくなる</b>。
    ///
    /// <para>
    /// <see cref="PathTracer"/> にはこれが無い。鏡面は光源のほうへ跳ね返る確率が 0 なので、
    /// <b>点光源は鏡に映らない</b>のが正しい振る舞いで、パストレーサはそのとおりに描く(要点4)。
    /// A キーで切り替えると、鏡の玉から白い点が消えるのが分かる。
    /// </para>
    /// </summary>
    private Vector3 SpecularHighlights(in SurfaceHit hit, int depth, ref RayCounters counters, TraceLog? log)
    {
        Material material = hit.Material;
        Vector3 sum = Vector3.Zero;

        foreach (PointLight light in Scene.Lights)
        {
            if (!TryReachLight(hit, light, depth, ref counters, log, out Vector3 toLight, out Vector3 irradiance))
            {
                continue;
            }

            Vector3 halfway = Vector3.Normalize(hit.ToViewer + toLight);
            float cosH = Vector3.Dot(hit.ToViewer, halfway);
            Vector3 fresnel = material.Kind == MaterialKind.Metal
                ? MetalFresnel(cosH, material.Albedo)
                : new Vector3(DielectricFresnel(cosH, 1.0f, material.Ior));

            sum += fresnel * BlinnPhong(hit.Normal, halfway, material.Shininess) * irradiance;
        }

        return sum;
    }

    /// <summary>
    /// 光源が見えるか調べ、見えたら<b>面が受け取る光の強さ</b>(<c>光の色 × 強さ / 距離² × cosθ</c>)を返す。
    ///
    /// <para>
    /// 影の光線を出すのは、<b>面が光源の方を向いているときだけ</b>。裏を向いていれば、遮るものが無くても光は届かないので、
    /// 光線を1本節約できる(<c>cosθ ≤ 0</c> の判定が影の光線より先にあるのはこのため)。
    /// </para>
    /// </summary>
    private bool TryReachLight(
        in SurfaceHit hit,
        in PointLight light,
        int depth,
        ref RayCounters counters,
        TraceLog? log,
        out Vector3 toLight,
        out Vector3 irradiance)
    {
        Vector3 offset = light.Position - hit.Point;
        float distance = offset.Length();
        toLight = offset / distance;
        irradiance = Vector3.Zero;

        float cosTheta = Vector3.Dot(hit.Normal, toLight);
        if (cosTheta <= 0.0f)
        {
            log?.Add(depth + 1, $"影: {light.Name} は面の裏側。光線は出さない");
            return false;
        }

        if (Settings.Shadows)
        {
            counters.Shadow++;
            var shadowRay = new Ray(OffsetAlong(hit.Point, hit.Normal), toLight);
            Shape? occluder = Scene.FindOccluder(shadowRay, distance, ref counters);
            if (occluder is not null)
            {
                log?.Add(depth + 1, $"影 → {light.Name}: {occluder.Name} に遮られた");
                return false;
            }

            log?.Add(depth + 1, $"影 → {light.Name}: 届いた");
        }

        irradiance = light.Color * (light.Intensity / (distance * distance) * cosTheta);
        return true;
    }

    /// <summary>
    /// 正規化した Blinn-Phong。<c>(s + 8) / 8π × cos^s</c>。
    ///
    /// <para>
    /// 係数 <c>(s + 8) / 8π</c> は、鋭さ s を変えても<b>跳ね返す光の総量がほぼ変わらない</b>ようにするためのもの。
    /// 係数が無いと、s を大きくしたときにハイライトが小さくなるだけでなく暗くもなる
    /// (Day 9 のフォンはこの係数を持っていなかった)。
    /// </para>
    /// </summary>
    private static float BlinnPhong(Vector3 normal, Vector3 halfway, float shininess)
    {
        float cosH = MathF.Max(Vector3.Dot(normal, halfway), 0.0f);
        return (shininess + 8.0f) / (8.0f * MathF.PI) * MathF.Pow(cosH, shininess);
    }
}
