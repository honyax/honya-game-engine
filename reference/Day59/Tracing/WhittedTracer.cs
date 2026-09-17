using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 光線の数。種類ごとに数える。描画スレッドごとに1つ持ち、行を描き終えたら足し合わせる
/// (1本ごとに <c>Interlocked</c> で共有の数を増やすと、スレッドどうしがその1つの変数を奪い合って遅くなる)。
/// </summary>
internal struct RayCounters
{
    /// <summary>カメラから出た光線(1サンプルに1本)。</summary>
    public long Camera;

    /// <summary>影の光線(拡散面・ハイライトの計算で、光源1つにつき1本)。</summary>
    public long Shadow;

    /// <summary>反射の光線(金属とガラス)。</summary>
    public long Reflection;

    /// <summary>屈折の光線(ガラス)。</summary>
    public long Refraction;

    public readonly long Total => Camera + Shadow + Reflection + Refraction;

    public void Add(in RayCounters other)
    {
        Camera += other.Camera;
        Shadow += other.Shadow;
        Reflection += other.Reflection;
        Refraction += other.Refraction;
    }
}

/// <summary>
/// 1画素を追ったときの記録(右クリック)。光線の木を、深さの分だけ字下げした行で残す。
/// 普段の描画では null を渡し、<b>文字列を1つも作らない</b>(<c>log?.Add(...)</c> は log が null なら引数ごと評価されない)。
/// </summary>
internal sealed class TraceLog
{
    public List<string> Lines { get; } = new();

    public void Add(int depth, string text) => Lines.Add(new string(' ', depth * 3) + text);
}

/// <summary>面に当たった点の情報。<see cref="WhittedTracer.Trace"/> の中だけで使う。</summary>
/// <param name="Point">当たった点。</param>
/// <param name="Normal">法線。<b>光線がやって来た側を向けてある</b>(内側から当たったら裏返し済み)。</param>
/// <param name="ToViewer">当たった点から、光線の来た方へ向かう向き(<c>−Direction</c>)。</param>
/// <param name="FrontFace">外側から当たったか。ガラスで「入る」か「出る」かの区別になる。</param>
internal readonly record struct SurfaceHit(Vector3 Point, Vector3 Normal, Vector3 ToViewer, bool FrontFace, Material Material);

/// <summary>
/// <b>Whitted 法のレイトレーサ</b>(1980)。今日の主役。
///
/// <para>
/// 画素の色を、次の<b>光線の木</b>で決める(要点3)。
/// </para>
/// <code>
///   カメラの光線 ─┬─ 拡散面に当たった → 光源ごとに影の光線を1本。見えた光源の分だけ明るくする(ここで枝は終わり)
///                ├─ 金属に当たった   → 反射の光線を1本(同じことを繰り返す)
///                ├─ ガラスに当たった → 反射の光線と屈折の光線の2本。フレネルの割合で混ぜる
///                └─ 何にも当たらない → 空の色
/// </code>
/// <para>
/// 1本の光線が2本、4本と増えていくので、<b>深さの上限(<see cref="RenderSettings.MaxDepth"/>)が無いと止まらない</b>。
/// ガラス玉の中の光線は、内側で反射を繰り返すたびに2本に分かれる。
/// </para>
/// <para>
/// <b>拡散面から反射の枝を出さない</b>のが、この方法の割り切り。ざらざらした面は光をあらゆる向きへ散らすので、
/// 1本の反射の光線では表せない。だから拡散面は「光源から直接届いた光」だけで色を決め、
/// 周りの物体から跳ね返ってきた光は一定の環境光(<see cref="Scene.Ambient"/>)で済ませる。
/// <b>その散らばる向きを乱数で1本ずつ選び、何本も平均する</b>のが Day 60 のパストレーシングになる。
/// </para>
/// <para>
/// 設定と場面を作ったときに受け取り、その後は変えない。だから描画スレッドの全員で1つを共有してよい
/// (書き換わるのは呼ぶ側が渡す <see cref="RayCounters"/> だけで、それはスレッドごとに別)。
/// </para>
/// </summary>
internal sealed class WhittedTracer
{
    /// <summary>
    /// 次の光線の始点を、面から法線の向きへ浮かせる距離(m)。要点7。
    ///
    /// <para>
    /// 当たった点 <c>O + tD</c> は浮動小数で計算するので、本当の面から少しずれる。
    /// 面の<b>内側</b>にずれた点から光源へ影の光線を出すと、<b>出てすぐ自分の面に当たって</b>
    /// 「影になっている」と判定される。これが画面全体にざらざらの黒い点として出るのが影のにきび(shadow acne)。
    /// </para>
    /// <para>
    /// 1mm にしたのは、今日の場面(大きさ数 m、カメラから 5〜10m)で float の誤差(1e-6 m 程度)より十分大きく、
    /// 形どうしの隙間より十分小さいから。場面の大きさが桁で変わるなら、この値も合わせて変える必要がある。
    /// </para>
    /// </summary>
    public const float SurfaceOffset = 1e-3f;

    private readonly Scene _scene;
    private readonly RenderSettings _settings;

    public WhittedTracer(Scene scene, RenderSettings settings)
    {
        _scene = scene;
        _settings = settings;
    }

    /// <summary>
    /// 画素の中の1点 (<paramref name="px"/>, <paramref name="py"/>) を通るカメラの光線を1本追って、その色を返す。
    /// 表示が「陰影」以外なら、その表示用の色を返す(<see cref="ViewMode"/>)。
    /// </summary>
    public Vector3 Sample(Camera camera, float px, float py, ref RayCounters counters)
    {
        Ray ray = camera.GetRay(px, py);
        long before = counters.Total;
        counters.Camera++;

        switch (_settings.View)
        {
            case ViewMode.Normal:
                return NormalColor(ray);

            case ViewMode.RayCount:
                Trace(ray, 0, ref counters, null, "カメラ");
                return HeatColor(counters.Total - before);

            default:
                return Trace(ray, 0, ref counters, null, "カメラ");
        }
    }

    /// <summary>
    /// 1画素を追う(右クリック)。画素の真ん中を通る光線を1本だけ追い、木の全部を記録する。
    /// </summary>
    public TraceLog TracePixel(Camera camera, int x, int y, out Vector3 color, out RayCounters counters)
    {
        var log = new TraceLog();
        counters = default;
        counters.Camera++;

        Ray ray = camera.GetRay(x + 0.5f, y + 0.5f);
        color = Trace(ray, 0, ref counters, log, "カメラ");
        return log;
    }

    /// <summary>
    /// 光線を1本追って、その光線に沿ってやって来る光の色(線形)を返す。<b>反射と屈折で自分自身を呼ぶ</b>(再帰)。
    /// </summary>
    /// <param name="depth">カメラから何回跳ね返ったか。カメラの光線は 0。</param>
    /// <param name="label">1画素を追ったときに、この光線を何と呼ぶか(カメラ / 反射 / 屈折 / 全反射)。</param>
    public Vector3 Trace(in Ray ray, int depth, ref RayCounters counters, TraceLog? log, string label)
    {
        if (!_scene.Intersect(ray, out float t, out Shape? shape))
        {
            log?.Add(depth, $"{label} → 空");
            return _scene.Sky(ray.Direction);
        }

        Vector3 point = ray.At(t);
        Vector3 normal = shape.OutwardNormal(point);

        // 光線と外向きの法線が同じ向き(内積が正)なら、光線は形の内側から当たっている。
        // 法線を光線の側へ裏返しておくと、この先の計算(光源の向きとの内積、始点を浮かせる向き)が
        // 表か裏かを気にせず1通りで書ける。表か裏かは FrontFace に残しておき、ガラスだけが使う。
        bool frontFace = Vector3.Dot(ray.Direction, normal) < 0.0f;
        if (!frontFace)
        {
            normal = -normal;
        }

        log?.Add(depth, $"{label} → {shape.Name}  {t:F3}m 先{(frontFace ? "" : "(内側から)")}");

        var hit = new SurfaceHit(point, normal, -ray.Direction, frontFace, shape.Material);
        return shape.Material.Kind switch
        {
            MaterialKind.Metal => ShadeMetal(ray, hit, depth, ref counters, log),
            MaterialKind.Glass => ShadeGlass(ray, hit, depth, ref counters, log),
            _ => ShadeDiffuse(hit, depth, ref counters, log),
        };
    }

    /// <summary>
    /// 拡散面(要点3)。<b>環境光 + 見えた光源ごとの (ランバート + ハイライト)</b>。反射の枝は出さない。
    ///
    /// <para>
    /// 拡散の BRDF を <c>albedo / π</c> にしているのは、点光源の明るさ(W/sr)と単位を揃えるため。
    /// 半球の全部の向きへ均等に散らした光を合計すると、ちょうど albedo 倍になる割り方が 1/π になる。
    /// Day 60 のパストレーシングも同じ BRDF を使うので、<b>同じ場面の直接光の明るさが Day 59 と揃う</b>。
    /// </para>
    /// </summary>
    private Vector3 ShadeDiffuse(in SurfaceHit hit, int depth, ref RayCounters counters, TraceLog? log)
    {
        Material material = hit.Material;
        Vector3 albedo = material.AlbedoAt(hit.Point);
        Vector3 color = albedo * _scene.Ambient;

        foreach (PointLight light in _scene.Lights)
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
    /// 金属(要点3)。<b>反射の光線を1本</b>出し、返ってきた色に反射色(フレネル)を掛ける。
    /// </summary>
    private Vector3 ShadeMetal(in Ray ray, in SurfaceHit hit, int depth, ref RayCounters counters, TraceLog? log)
    {
        Vector3 color = SpecularHighlights(hit, depth, ref counters, log);

        if (depth >= _settings.MaxDepth)
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
    /// ガラス(要点4・5)。<b>反射と屈折の2本</b>に枝分かれし、フレネルの割合 F と 1 − F で混ぜる。
    ///
    /// <para>
    /// 光は面で消えも増えもしないので、跳ね返った分と中へ入った分を足すと必ず元の量になる(F + (1 − F) = 1)。
    /// 全反射のときは屈折の枝が無く、F = 1 で全部が反射に回る。
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

        if (depth >= _settings.MaxDepth)
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
    /// </summary>
    private Vector3 SpecularHighlights(in SurfaceHit hit, int depth, ref RayCounters counters, TraceLog? log)
    {
        Material material = hit.Material;
        Vector3 sum = Vector3.Zero;

        foreach (PointLight light in _scene.Lights)
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

        if (_settings.Shadows)
        {
            counters.Shadow++;
            var shadowRay = new Ray(OffsetAlong(hit.Point, hit.Normal), toLight);
            Shape? occluder = _scene.FindOccluder(shadowRay, distance);
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
    /// 反射・屈折の光線を作る。始点を <paramref name="side"/> の向きへ浮かせ、<b>向きを長さ 1 に直してから</b>渡す。
    ///
    /// <para>
    /// 反射も屈折も、式の上では長さ 1 の向きから長さ 1 の向きを作る。それでも正規化し直すのは、
    /// <b>浮動小数の誤差が再帰で増幅する</b>から(要点7)。向きの長さが 1 から e だけずれると、
    /// 球の交差(<see cref="Sphere.Intersect"/>)は長さ 1 を前提に解くので当たる点が面から外れ、
    /// その点から求めた法線の長さもずれ、その法線で反射した向きはさらにずれる。
    /// ガラス玉の中でほぼ垂直に反射を繰り返すと、<b>ずれは1回ごとに約 17 倍</b>になり、
    /// 深さ 10 で色が 1e16 を超えて画素が真っ白に飛んだ(自己チェック10)。
    /// </para>
    /// <para>
    /// 公開しているのは自己チェック10から同じ作り方を呼ぶため。
    /// </para>
    /// </summary>
    public Ray SpawnRay(Vector3 point, Vector3 side, Vector3 direction)
        => new(OffsetAlong(point, side), Vector3.Normalize(direction));

    /// <summary>
    /// 次の光線の始点。設定で浮かせるのを切ったら(E キー)、当たった点をそのまま使う(要点7)。
    /// </summary>
    private Vector3 OffsetAlong(Vector3 point, Vector3 direction)
        => _settings.OffsetOrigin ? point + direction * SurfaceOffset : point;

    private float DielectricFresnel(float cosI, float n1, float n2) => _settings.Fresnel switch
    {
        FresnelMode.Schlick => Optics.SchlickDielectric(cosI, n1, n2),
        FresnelMode.Off => Optics.NormalIncidenceReflectance(n1, n2),
        _ => Optics.FresnelDielectric(cosI, n1, n2),
    };

    /// <summary>金属は「正確」でもシュリックを使う(<see cref="Optics.SchlickConductor"/> の説明を参照)。</summary>
    private Vector3 MetalFresnel(float cosI, Vector3 f0)
        => _settings.Fresnel == FresnelMode.Off ? f0 : Optics.SchlickConductor(cosI, f0);

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

    /// <summary>法線の表示。何にも当たらなければ黒。</summary>
    private Vector3 NormalColor(in Ray ray)
    {
        if (!_scene.Intersect(ray, out float t, out Shape? shape))
        {
            return Vector3.Zero;
        }

        Vector3 normal = shape.OutwardNormal(ray.At(t));
        if (Vector3.Dot(ray.Direction, normal) > 0.0f)
        {
            normal = -normal;
        }

        return normal * 0.5f + new Vector3(0.5f);
    }

    /// <summary>
    /// 光線の数を色にする。<b>2倍ごとに1段</b>(対数)で、1本 紺 / 2 青 / 4 水色 / 8 緑 / 16 黄 / 32 赤 / 64 以上 白。
    /// 床は3本(カメラ1 + 影2)で青と水色の間、ガラス玉は何十本にもなる。
    /// </summary>
    public static Vector3 HeatColor(long rayCount)
    {
        ReadOnlySpan<Vector3> stops =
        [
            new(0.00f, 0.00f, 0.30f),
            new(0.00f, 0.30f, 1.00f),
            new(0.00f, 0.90f, 0.90f),
            new(0.20f, 0.90f, 0.10f),
            new(1.00f, 0.90f, 0.00f),
            new(1.00f, 0.20f, 0.00f),
            new(1.00f, 1.00f, 1.00f),
        ];

        float level = Math.Clamp(MathF.Log2(MathF.Max(rayCount, 1)), 0.0f, stops.Length - 1);
        int index = Math.Min((int)level, stops.Length - 2);
        return Vector3.Lerp(stops[index], stops[index + 1], level - index);
    }
}
