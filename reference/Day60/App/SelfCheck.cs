using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 自己チェック(C キー)。<b>18 項目</b>(1〜10 は Day 59、11〜18 が Day 60 で追加)。
///
/// <para>
/// どれも<b>窓も描画スレッドも使わない</b>。式を、答えの分かっている入力で呼んで比べる。
/// 絵を見て「それらしい」と思えても、屈折の式の符号を1つ間違えただけで<b>それらしい別の絵</b>になるのがレイトレーサなので、
/// 式そのものを数字で確かめておく。
/// </para>
/// <para>
/// <b>パストレーシングでは、この「数字で確かめる」がもっと大事になる</b>。
/// 答えがノイズに埋もれているので、係数が 2 倍ずれていても「そういう絵」に見えてしまう。
/// 11〜18 は、答えが解析的に分かっている入力(既知の積分、白い炉、総当たりとの一致)だけを選んである(要点9)。
/// </para>
/// <para>
/// 乱数は種を固定している(<c>new Random(59)</c> / <see cref="Rng.Create"/> の番号)。何度押しても同じ数字が出る。
/// 全部で 2〜3 秒かかる(11〜18 が数百万本の光線を撃つため)。
/// </para>
/// </summary>
internal static class SelfCheck
{
    public static IReadOnlyList<string> Run(out string summary)
    {
        var lines = new List<string>();
        int passed = 0;
        int total = 0;

        // 1項目を2行で出す。1行目が何を確かめたか、2行目が測った数字(窓の幅に収めるため)。
        void Report(bool ok, string heading, string detail)
        {
            total++;
            if (ok)
            {
                passed++;
            }

            lines.Add($"{(ok ? "OK" : "NG")}  {total}. {heading}");
            lines.Add($"        {detail}");
        }

        CheckSphere(Report);
        CheckPlane(Report);
        CheckReflect(Report);
        CheckSnell(Report);
        CheckTotalInternalReflection(Report);
        CheckFresnel(Report);
        CheckSchlick(Report);
        CheckShadowAcne(Report);
        CheckInvisibleGlass(Report);
        CheckDirectionDrift(Report);

        // ---- Day 60 ----
        CheckRandom(Report);
        CheckCosineSampling(Report);
        CheckMonteCarloConvergence(Report);
        CheckFurnace(Report);
        CheckDirectLightMatchesWhitted(Report);
        CheckNeeAndRoulette(Report);
        CheckAabbSlab(Report);
        CheckBvhMatchesBruteForce(Report);

        summary = $"{total} 項目中 {passed} 項目 OK";
        return lines;
    }

    private static readonly Material Gray = Material.Diffuse(new Vector3(0.5f));

    /// <summary>1. 球の正面に当てると手前の解、中から撃つと奥の解。かすめる光線・後ろの球・範囲の外は外れ。</summary>
    private static void CheckSphere(Action<bool, string, string> report)
    {
        var sphere = new Sphere("球", new Vector3(0.0f, 0.0f, 5.0f), 1.0f, Gray);
        bool front = sphere.Intersect(new Ray(Vector3.Zero, Vector3.UnitZ), 0.0f, float.PositiveInfinity, out float tFront);
        bool inside = sphere.Intersect(new Ray(sphere.Center, Vector3.UnitZ), 0.0f, float.PositiveInfinity, out float tInside);
        bool graze = sphere.Intersect(new Ray(new Vector3(1.001f, 0.0f, 0.0f), Vector3.UnitZ), 0.0f, float.PositiveInfinity, out _);
        bool behind = sphere.Intersect(new Ray(Vector3.Zero, -Vector3.UnitZ), 0.0f, float.PositiveInfinity, out _);
        bool beyond = sphere.Intersect(new Ray(Vector3.Zero, Vector3.UnitZ), 0.0f, 3.9f, out _);

        report(
            front && inside && MathF.Abs(tFront - 4.0f) < 1e-4f && MathF.Abs(tInside - 1.0f) < 1e-4f && !graze && !behind && !beyond,
            "球との交差(中心 z = 5、半径 1)",
            $"正面から t = {tFront:F5}(期待 4) / 中心から奥の解 t = {tInside:F5}(期待 1) / "
            + $"1mm 外: {HitText(graze)} / 後ろ: {HitText(behind)} / tMax 3.9 で打ち切り: {HitText(beyond)}");
    }

    /// <summary>2. 高さ 1 から 45° 下へ撃つと √2 先で床に当たる。床と平行なら当たらない。</summary>
    private static void CheckPlane(Action<bool, string, string> report)
    {
        var floor = new Plane("床", Vector3.UnitY, 0.0f, Gray);
        var origin = new Vector3(0.0f, 1.0f, 0.0f);
        bool down = floor.Intersect(new Ray(origin, Vector3.Normalize(new Vector3(1.0f, -1.0f, 0.0f))), 0.0f, float.PositiveInfinity, out float t);
        bool parallel = floor.Intersect(new Ray(origin, Vector3.UnitX), 0.0f, float.PositiveInfinity, out _);
        bool up = floor.Intersect(new Ray(origin, Vector3.UnitY), 0.0f, float.PositiveInfinity, out _);

        report(
            down && MathF.Abs(t - MathF.Sqrt(2.0f)) < 1e-5f && !parallel && !up,
            "平面に当たる距離",
            $"高さ 1 から 45 度下へ t = {t:F5}(期待 1.41421) / 床と平行: {HitText(parallel)} / 上向き: {HitText(up)}");
    }

    /// <summary>3. 反射: 長さ 1、入射角 = 反射角、入射方向・法線と同じ平面の中。</summary>
    private static void CheckReflect(Action<bool, string, string> report)
    {
        var random = new Random(59);
        float lengthError = 0.0f;
        float angleError = 0.0f;
        float planeError = 0.0f;

        for (int i = 0; i < 10000; i++)
        {
            RandomIncidence(random, out Vector3 d, out Vector3 n);
            Vector3 r = Optics.Reflect(d, n);

            lengthError = MathF.Max(lengthError, MathF.Abs(r.Length() - 1.0f));
            angleError = MathF.Max(angleError, MathF.Abs(Vector3.Dot(r, n) + Vector3.Dot(d, n)));
            planeError = MathF.Max(planeError, MathF.Abs(Vector3.Dot(Vector3.Cross(d, n), r)));
        }

        report(
            lengthError < 1e-5f && angleError < 1e-5f && planeError < 1e-5f,
            "反射(1万本)",
            $"長さのずれ {lengthError:E1} / cos(入射角) と cos(反射角) の差 {angleError:E1} / 入射面からのはみ出し {planeError:E1}");
    }

    /// <summary>4. 屈折: n1 sinθi = n2 sinθt、長さ 1、入射面の中。</summary>
    private static void CheckSnell(Action<bool, string, string> report)
    {
        var random = new Random(59);
        const float N1 = 1.0f;
        const float N2 = 1.5f;
        float snellError = 0.0f;
        float lengthError = 0.0f;
        float planeError = 0.0f;
        int refracted = 0;

        for (int i = 0; i < 10000; i++)
        {
            RandomIncidence(random, out Vector3 d, out Vector3 n);
            if (!Optics.TryRefract(d, n, N1 / N2, out Vector3 t))
            {
                continue;
            }

            refracted++;
            float sinI = Vector3.Cross(d, n).Length();
            float sinT = Vector3.Cross(t, n).Length();
            snellError = MathF.Max(snellError, MathF.Abs(N1 * sinI - N2 * sinT));
            lengthError = MathF.Max(lengthError, MathF.Abs(t.Length() - 1.0f));
            planeError = MathF.Max(planeError, MathF.Abs(Vector3.Dot(Vector3.Cross(d, n), t)));
        }

        // 空気からガラスへは全反射が起きないので、1万本全部が屈折するはず。
        report(
            refracted == 10000 && snellError < 1e-5f && lengthError < 1e-5f && planeError < 1e-5f,
            $"スネルの法則(空気からガラス 1.5 へ 1万本。屈折 {refracted} 本)",
            $"n1 sin(入射角) と n2 sin(透過角) の差 {snellError:E1} / 長さのずれ {lengthError:E1} / 入射面からのはみ出し {planeError:E1}");
    }

    /// <summary>5. ガラスから空気へ: 臨界角 41.81° の手前は屈折し、先は全反射。</summary>
    private static void CheckTotalInternalReflection(Action<bool, string, string> report)
    {
        float critical = MathF.Asin(1.0f / 1.5f) * 180.0f / MathF.PI;
        bool below = Optics.TryRefract(Incidence(41.7f), Vector3.UnitY, 1.5f, out _);
        bool above = Optics.TryRefract(Incidence(41.9f), Vector3.UnitY, 1.5f, out _);
        float fresnelBelow = Optics.FresnelDielectric(MathF.Cos(41.7f * MathF.PI / 180.0f), 1.5f, 1.0f);
        float fresnelAbove = Optics.FresnelDielectric(MathF.Cos(41.9f * MathF.PI / 180.0f), 1.5f, 1.0f);

        report(
            below && !above && fresnelAbove == 1.0f && fresnelBelow < 1.0f,
            $"全反射(ガラスから空気へ。臨界角 {critical:F2} 度)",
            $"41.7 度は{(below ? "屈折する" : "屈折しない")}(反射 {fresnelBelow:F3}) / 41.9 度は{(above ? "屈折する" : "全反射")}(反射 {fresnelAbove:F3})");
    }

    /// <summary>6. 正確なフレネル: 垂直 4%、かすめると 100% に近づく、屈折率が同じなら 0、行きと帰りで同じ。</summary>
    private static void CheckFresnel(Action<bool, string, string> report)
    {
        float normalOutside = Optics.FresnelDielectric(1.0f, 1.0f, 1.5f);
        float normalInside = Optics.FresnelDielectric(1.0f, 1.5f, 1.0f);
        float grazing = Optics.FresnelDielectric(0.001f, 1.0f, 1.5f);

        float matched = 0.0f;
        float reciprocity = 0.0f;
        for (int degrees = 0; degrees < 90; degrees++)
        {
            float cosI = MathF.Cos(degrees * MathF.PI / 180.0f);
            matched = MathF.Max(matched, Optics.FresnelDielectric(cosI, 1.0f, 1.0f));

            // 空気側から角度 θi で入った光は、ガラスの中を角度 θt で進む。
            // 同じ道を逆にたどる光(ガラス側から θt)の反射率は、行きと同じになる(光の可逆性)。
            float sinT = MathF.Sqrt(1.0f - cosI * cosI) / 1.5f;
            float cosT = MathF.Sqrt(1.0f - sinT * sinT);
            reciprocity = MathF.Max(
                reciprocity,
                MathF.Abs(Optics.FresnelDielectric(cosI, 1.0f, 1.5f) - Optics.FresnelDielectric(cosT, 1.5f, 1.0f)));
        }

        report(
            MathF.Abs(normalOutside - 0.04f) < 1e-5f && MathF.Abs(normalInside - 0.04f) < 1e-5f
                && grazing > 0.99f && matched == 0.0f && reciprocity < 1e-4f,
            "正確なフレネル",
            $"垂直 外 {normalOutside:F4} / 内 {normalInside:F4}(期待 0.04) / かすめる {grazing:F4} / 屈折率が同じなら最大 {matched:F4} / 行きと帰りの差 {reciprocity:E1}");
    }

    /// <summary>
    /// 7. シュリック近似の誤差。<b>内側からは透過角の cos を使わないと、臨界角の手前で大きく外れる</b>。
    /// </summary>
    private static void CheckSchlick(Action<bool, string, string> report)
    {
        float outsideError = 0.0f;
        float insideError = 0.0f;
        float naiveError = 0.0f;

        for (float degrees = 0.0f; degrees < 90.0f; degrees += 0.1f)
        {
            float cosI = MathF.Cos(degrees * MathF.PI / 180.0f);
            outsideError = MathF.Max(outsideError, MathF.Abs(Optics.SchlickDielectric(cosI, 1.0f, 1.5f) - Optics.FresnelDielectric(cosI, 1.0f, 1.5f)));

            float exactInside = Optics.FresnelDielectric(cosI, 1.5f, 1.0f);
            if (exactInside < 1.0f)
            {
                insideError = MathF.Max(insideError, MathF.Abs(Optics.SchlickDielectric(cosI, 1.5f, 1.0f) - exactInside));
                naiveError = MathF.Max(naiveError, MathF.Abs(NaiveSchlick(cosI, 1.5f, 1.0f) - exactInside));
            }
        }

        float cos415 = MathF.Cos(41.5f * MathF.PI / 180.0f);
        report(
            outsideError < 0.05f && insideError < 0.05f && naiveError > 0.5f,
            $"シュリックと正確な式の差の最大: 外から {outsideError:F3} / 内から(透過角の cos){insideError:F3} / 内から(入射角の cos){naiveError:F3}",
            $"ガラスの内から 41.5 度: 正確 {Optics.FresnelDielectric(cos415, 1.5f, 1.0f):F3} / 透過角の cos {Optics.SchlickDielectric(cos415, 1.5f, 1.0f):F3} / 入射角の cos {NaiveSchlick(cos415, 1.5f, 1.0f):F3}");
    }

    /// <summary>
    /// 8. 影のにきび。床と球の上の点から光源へ影の光線を出し、<b>自分自身に当たった割合</b>を数える。
    /// 始点を浮かせないと、浮動小数の誤差で当たった点の半分近くが面の内側に落ち、自分の影に入る。
    /// </summary>
    private static void CheckShadowAcne(Action<bool, string, string> report)
    {
        var floor = new Plane("床", Vector3.UnitY, 0.0f, Gray);
        var sphere = new Sphere("球", new Vector3(0.0f, 1.0f, 0.0f), 1.0f, Gray);
        Shape[] shapes = [floor, sphere];
        var lightPosition = new Vector3(3.0f, 6.0f, -4.0f);
        var eye = new Vector3(0.3f, 2.5f, -7.0f);

        var random = new Random(59);
        int lit = 0;
        int acneWithout = 0;
        int acneWith = 0;

        for (int i = 0; i < 20000; i++)
        {
            // 床の 6m 四方か、球のどこかを狙う。
            Vector3 target = i % 2 == 0
                ? new Vector3(random.NextSingle() * 6.0f - 3.0f, 0.0f, random.NextSingle() * 6.0f - 3.0f)
                : sphere.Center + RandomUnitVector(random);
            var ray = new Ray(eye, Vector3.Normalize(target - eye));

            Shape? hitShape = null;
            float closest = float.PositiveInfinity;
            foreach (Shape shape in shapes)
            {
                if (shape.Intersect(ray, 0.0f, closest, out float t))
                {
                    closest = t;
                    hitShape = shape;
                }
            }

            if (hitShape is null)
            {
                continue;
            }

            Vector3 point = ray.At(closest);
            Vector3 normal = hitShape.OutwardNormal(point);
            if (Vector3.Dot(ray.Direction, normal) > 0.0f)
            {
                normal = -normal;
            }

            Vector3 toLight = Vector3.Normalize(lightPosition - point);
            if (Vector3.Dot(normal, toLight) <= 0.0f)
            {
                continue;
            }

            // 光源の側を向いた点だけを数える。そこから光源へ向かう光線が自分に当たるのは、誤差のせいでしかない。
            lit++;
            float distance = Vector3.Distance(lightPosition, point);
            if (hitShape.Intersect(new Ray(point, toLight), 0.0f, distance, out _))
            {
                acneWithout++;
            }

            if (hitShape.Intersect(new Ray(point + normal * Tracer.SurfaceOffset, toLight), 0.0f, distance, out _))
            {
                acneWith++;
            }
        }

        float ratioWithout = (float)acneWithout / Math.Max(lit, 1);
        report(
            lit > 5000 && ratioWithout > 0.1f && acneWith == 0,
            "影のにきび(光の側を向いた点から、光源へ影の光線を出す)",
            $"{lit} 点のうち、浮かせないと {acneWithout} 本({ratioWithout:P1})が自分に当たる / {Tracer.SurfaceOffset * 1000.0f:F0}mm 浮かせると {acneWith} 本");
    }

    /// <summary>
    /// 9. 屈折率 1.00 のガラス玉は、正確なフレネルなら<b>空の色を1つも変えない</b>(反射の枝も出ない)。
    /// シュリックだと縁で反射が出て、見えてしまう。
    /// </summary>
    private static void CheckInvisibleGlass(Action<bool, string, string> report)
    {
        var scene = new Scene("見えない玉", default)
        {
            SkyHorizon = new Vector3(0.9f, 0.9f, 0.9f),
            SkyZenith = new Vector3(0.1f, 0.3f, 0.9f),
        };
        // 空の色が地平線の近くで急に変わる(Sky の平方根)ので、玉は見上げる位置に置いて、比べる向きを地平線から離しておく。
        var sphere = new Sphere("屈折率 1.00 の玉", new Vector3(0.0f, 2.0f, 4.0f), 1.0f, Material.Glass(1.0f));
        scene.Shapes.Add(sphere);

        // Day 60 から、場面は使う前に Prepare で形を仕分ける必要がある(総当たりでも同じ)。
        scene.Prepare(AccelerationMode.BruteForce);

        var exact = new WhittedTracer(scene, new RenderSettings { Fresnel = FresnelMode.Exact });
        var schlick = new WhittedTracer(scene, new RenderSettings { Fresnel = FresnelMode.Schlick });
        var random = new Random(59);

        float exactError = 0.0f;
        float schlickError = 0.0f;
        var exactRays = new RayCounters();
        var schlickRays = new RayCounters();

        for (int i = 0; i < 5000; i++)
        {
            // 玉の面の上の点に向けて撃つ。どれも輪郭の内側を通り、縁ぎりぎりのかすめる光線も入る。
            Vector3 target = sphere.Center + RandomUnitVector(random);
            var ray = new Ray(Vector3.Zero, Vector3.Normalize(target));
            Vector3 sky = scene.Sky(ray.Direction);

            exactError = MathF.Max(exactError, MaxAbs(exact.Trace(ray, 0, ref exactRays, null, "カメラ") - sky));
            schlickError = MathF.Max(schlickError, MaxAbs(schlick.Trace(ray, 0, ref schlickRays, null, "カメラ") - sky));
        }

        report(
            exactError < 1e-4f && exactRays.Reflection == 0 && exactRays.Refraction > 0 && schlickError > 1e-3f,
            "屈折率 1.00 の玉は見えない(玉を通した 5000 本の色を、空の色と比べる)",
            $"ずれ 正確 {exactError:E1}(反射の枝 {exactRays.Reflection} 本・屈折 {exactRays.Refraction} 本) / シュリック {schlickError:F3}(反射の枝 {schlickRays.Reflection} 本)");
    }

    /// <summary>
    /// 10. ガラス玉の中で反射を繰り返しても、<b>向きの長さが 1 のまま</b>。
    /// 正規化し直さないと、ずれが1回ごとに十数倍になって発散する(<see cref="WhittedTracer.SpawnRay"/>)。
    /// </summary>
    private static void CheckDirectionDrift(Action<bool, string, string> report)
    {
        var sphere = new Sphere("玉", Vector3.Zero, 0.35f, Material.Glass(1.5f));
        var emptyScene = new Scene("玉だけ", default);
        emptyScene.Prepare(AccelerationMode.BruteForce);
        var tracer = new WhittedTracer(emptyScene, new RenderSettings());

        // 中心から少し外した点から、ほぼ垂直に内側の面へ当てる(ガラス玉の真ん中を見た光線が、中で往復するときの形)。
        var start = new Ray(new Vector3(0.02f, 0.01f, 0.0f), Vector3.Normalize(new Vector3(0.1f, 0.05f, 1.0f)));
        const int Bounces = 30;

        float normalizedDrift = BounceInside(sphere, start, Bounces, ray => tracer.SpawnRay(ray.Origin, ray.Normal, ray.Direction), out _);
        float rawDrift = BounceInside(
            sphere,
            start,
            Bounces,
            ray => new Ray(ray.Origin + ray.Normal * Tracer.SurfaceOffset, ray.Direction),
            out int rawBrokenAt);

        report(
            normalizedDrift < 1e-5f && rawBrokenAt > 0,
            $"向きの長さ(半径 0.35 のガラス玉の中で、ほぼ垂直に {Bounces} 回反射させる)",
            $"作るたびに正規化: 長さのずれ最大 {normalizedDrift:E1} / 正規化しない: "
            + (rawBrokenAt > 0 ? $"{rawBrokenAt} 回目で 1% を超えた(30 回の中の最大 {rawDrift:E1})" : $"最大 {rawDrift:E1}"));
    }

    /// <summary>内側で反射を <paramref name="bounces"/> 回繰り返し、向きの長さのずれの最大を返す。</summary>
    private static float BounceInside(
        Sphere sphere,
        Ray ray,
        int bounces,
        Func<(Vector3 Origin, Vector3 Normal, Vector3 Direction), Ray> spawn,
        out int brokenAt)
    {
        float maxDrift = 0.0f;
        brokenAt = 0;

        for (int i = 1; i <= bounces; i++)
        {
            if (!sphere.Intersect(ray, 0.0f, float.PositiveInfinity, out float t))
            {
                // 始点が玉の外へ出てしまった(ずれが大きくなりすぎた)。
                brokenAt = brokenAt == 0 ? i : brokenAt;
                return float.PositiveInfinity;
            }

            Vector3 point = ray.At(t);
            Vector3 inward = -sphere.OutwardNormal(point);
            ray = spawn((point, inward, Optics.Reflect(ray.Direction, inward)));

            float drift = MathF.Abs(ray.Direction.Length() - 1.0f);
            if (!(drift <= maxDrift))
            {
                maxDrift = drift;
            }

            if (brokenAt == 0 && !(drift < 0.01f))
            {
                brokenAt = i;
            }
        }

        return maxDrift;
    }

    /// <summary>
    /// 11. 乱数(PCG32)。<b>一様か</b>と、<b>隣の画素と無関係か</b>(要点6)。
    ///
    /// <para>
    /// 2つめが今日の本題。画素の番号を種に混ぜないと、全部の画素が同じ数列を使う——
    /// 相関が 1.0000 になり、ノイズが画面をまたいだ模様になる。
    /// </para>
    /// </summary>
    private static void CheckRandom(Action<bool, string, string> report)
    {
        const int Count = 1_000_000;
        var rng = Rng.Create(0, 0, 0);
        double sum = 0.0;
        var bins = new int[16];
        bool inRange = true;

        for (int i = 0; i < Count; i++)
        {
            float u = rng.NextFloat();
            if (!(u >= 0.0f && u < 1.0f))
            {
                inRange = false;
            }

            sum += u;
            bins[Math.Min(15, (int)(u * 16.0f))]++;
        }

        double mean = sum / Count;
        double expectedPerBin = Count / 16.0;
        double maxBinError = 0.0;
        foreach (int count in bins)
        {
            maxBinError = Math.Max(maxBinError, Math.Abs(count - expectedPerBin) / expectedPerBin);
        }

        double decorrelated = NeighborCorrelation(true);
        double shared = NeighborCorrelation(false);

        report(
            inRange && Math.Abs(mean - 0.5) < 0.002 && maxBinError < 0.02
                && Math.Abs(decorrelated) < 0.02 && shared > 0.99,
            $"乱数(PCG32)。100万個の平均 {mean:F5}(期待 0.5)/ 16 分割のずれ {maxBinError:P2} / 範囲外 {(inRange ? "なし" : "あり")}",
            $"隣り合う画素の相関: 画素ごとに種を変える {decorrelated:F4} / 変えないと {shared:F4}(全部の画素が同じ数列になる)");
    }

    /// <summary>隣り合う画素の「最初の1個目の乱数」どうしの相関係数。</summary>
    private static double NeighborCorrelation(bool decorrelate)
    {
        const int Count = 100_000;
        double sa = 0.0, sb = 0.0, saa = 0.0, sbb = 0.0, sab = 0.0;

        for (int i = 0; i < Count; i++)
        {
            var left = Rng.Create(i % 320, i / 320, 0, decorrelate);
            var right = Rng.Create(i % 320 + 1, i / 320, 0, decorrelate);
            double a = left.NextFloat();
            double b = right.NextFloat();
            sa += a;
            sb += b;
            saa += a * a;
            sbb += b * b;
            sab += a * b;
        }

        double ma = sa / Count;
        double mb = sb / Count;
        double da = Math.Sqrt(Math.Max(1e-18, saa / Count - ma * ma));
        double db = Math.Sqrt(Math.Max(1e-18, sbb / Count - mb * mb));
        return (sab / Count - ma * mb) / (da * db);
    }

    /// <summary>
    /// 12. コサイン重点サンプリング(要点3)。<b>作った向きの分布が、名乗っている pdf と合っているか</b>。
    ///
    /// <para>
    /// pdf が <c>cosθ/π</c> なら、<c>cosθ</c> が c 未満になる確率は <c>c²</c>(立体角で積分すると出る)。
    /// これがずれていると、割り算で打ち消すはずの重みが打ち消されず、<b>絵が一様に明るく(暗く)なる</b>。
    /// </para>
    /// </summary>
    private static void CheckCosineSampling(Action<bool, string, string> report)
    {
        const int Count = 1_000_000;
        Vector3 normal = Vector3.Normalize(new Vector3(0.3f, 0.8f, -0.5f));
        var rng = Rng.Create(12, 0, 0);

        double cosSum = 0.0;
        float lengthError = 0.0f;
        bool belowSurface = false;
        var bins = new int[20];

        for (int i = 0; i < Count; i++)
        {
            Vector3 direction = Sampler.CosineHemisphere(normal, ref rng);
            float cos = Vector3.Dot(direction, normal);
            if (cos < 0.0f)
            {
                belowSurface = true;
            }

            cosSum += cos;
            lengthError = MathF.Max(lengthError, MathF.Abs(direction.Length() - 1.0f));
            bins[Math.Clamp((int)(cos * 20.0f), 0, 19)]++;
        }

        double maxCdfError = 0.0;
        long running = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            running += bins[i];
            double c = (i + 1) / 20.0;
            maxCdfError = Math.Max(maxCdfError, Math.Abs((double)running / Count - c * c));
        }

        // pdf を半球で積分すると 1 になるはず。一様に引いた向きで pdf の平均を取り、半球の立体角 2π を掛ける。
        var uniformRng = Rng.Create(12, 1, 0);
        double pdfIntegral = 0.0;
        for (int i = 0; i < Count; i++)
        {
            Vector3 direction = Sampler.UniformHemisphere(normal, ref uniformRng);
            pdfIntegral += Sampler.CosineHemispherePdf(Vector3.Dot(direction, normal));
        }

        pdfIntegral = pdfIntegral / Count * (2.0 * Math.PI);
        double meanCos = cosSum / Count;

        report(
            !belowSurface && lengthError < 1e-5f && Math.Abs(meanCos - 2.0 / 3.0) < 0.002
                && maxCdfError < 0.003 && Math.Abs(pdfIntegral - 1.0) < 0.005,
            $"コサイン重点サンプリング(100万本)。面の裏へ出た向き {(belowSurface ? "あり" : "なし")} / 長さのずれ {lengthError:E1}",
            $"cos の平均 {meanCos:F5}(期待 0.66667)/ 分布と c² のずれ {maxCdfError:F4} / pdf の半球積分 {pdfIntegral:F5}(期待 1)");
    }

    /// <summary>
    /// 13. モンテカルロ積分の収束(要点2・3)。答えの分かっている積分 <c>∫cos²θ dω = 2π/3</c> を推定する。
    ///
    /// <para>
    /// 確かめることは2つ。<b>サンプルを 64 倍にすると誤差が 1/8 になる</b>(1/√N)ことと、
    /// <b>重点サンプリングのほうが同じサンプル数で誤差が小さい</b>こと。
    /// 1/√N は「サンプルを2倍にしても、ノイズは 3 割しか減らない」ということでもある——
    /// パストレーシングの絵がなかなか綺麗にならない理由がこれ(要点9)。
    /// </para>
    /// </summary>
    private static void CheckMonteCarloConvergence(Action<bool, string, string> report)
    {
        const double Exact = 2.0 * Math.PI / 3.0;
        const int Trials = 16;
        ReadOnlySpan<int> counts = [64, 4096, 262144];
        Span<double> uniformRms = stackalloc double[3];
        Span<double> cosineRms = stackalloc double[3];
        Vector3 normal = Vector3.UnitY;

        for (int level = 0; level < counts.Length; level++)
        {
            double uniformSquares = 0.0;
            double cosineSquares = 0.0;

            for (int trial = 0; trial < Trials; trial++)
            {
                var uniformRng = Rng.Create(level, trial, 13);
                var cosineRng = Rng.Create(level, trial, 14);
                double uniform = 0.0;
                double cosine = 0.0;

                for (int i = 0; i < counts[level]; i++)
                {
                    // 一様: 値 / pdf = cos² / (1/2π)
                    float cu = Vector3.Dot(Sampler.UniformHemisphere(normal, ref uniformRng), normal);
                    uniform += cu * cu / Sampler.UniformHemispherePdf;

                    // 重点: 値 / pdf = cos² / (cos/π) = π cos
                    float cc = Vector3.Dot(Sampler.CosineHemisphere(normal, ref cosineRng), normal);
                    cosine += cc * cc / Sampler.CosineHemispherePdf(cc);
                }

                uniform /= counts[level];
                cosine /= counts[level];
                uniformSquares += (uniform - Exact) * (uniform - Exact);
                cosineSquares += (cosine - Exact) * (cosine - Exact);
            }

            uniformRms[level] = Math.Sqrt(uniformSquares / Trials);
            cosineRms[level] = Math.Sqrt(cosineSquares / Trials);
        }

        double ratio1 = uniformRms[0] / uniformRms[1];
        double ratio2 = uniformRms[1] / uniformRms[2];
        double improvement = cosineRms[2] / uniformRms[2];

        report(
            ratio1 > 4.0 && ratio1 < 16.0 && ratio2 > 4.0 && ratio2 < 16.0 && improvement < 0.8,
            $"モンテカルロの収束(∫cos²θ dω = {Exact:F4} を {Trials} 回ずつ推定。N を 64 倍で誤差 1/8 が理論値)",
            $"一様 N=64 {uniformRms[0]:F4} → 4096 {uniformRms[1]:F4}(1/{ratio1:F1})→ 262144 {uniformRms[2]:F4}(1/{ratio2:F1})"
            + $"/ 同じ N で重点は {cosineRms[2]:F4} = 一様の {improvement:F2} 倍");
    }

    /// <summary>
    /// 14. <b>白い炉のテスト</b>(furnace test)。今日いちばん鋭いチェック(要点9)。
    ///
    /// <para>
    /// <b>反射率 1(何も吸わない)の拡散面だけを、一様な明るさ L の空の中に置く</b>。
    /// 入った光を全部返すのだから、どこをどう見ても<b>ちょうど L</b> が返るはず——面が「消える」。
    /// どれだけ跳ね返っても重みは 1 のままなので、<b>1本ごとの値まで厳密に L</b> になり、
    /// モンテカルロのばらつきすら出ない。
    /// </para>
    /// <para>
    /// <c>BRDF × cosθ / pdf</c> の打ち消しがどこか1か所でもずれていれば、この値が L からずれる。
    /// π を落とした・cos を2回掛けた・pdf を間違えた、のどれでも一発で捕まる。
    /// </para>
    /// </summary>
    private static void CheckFurnace(Action<bool, string, string> report)
    {
        const float SkyLevel = 0.6f;
        var scene = new Scene("白い炉", default)
        {
            SkyHorizon = new Vector3(SkyLevel),
            SkyZenith = new Vector3(SkyLevel),
        };

        // 球を床から浮かせてある。接していると、接点の近くで何百回も往復する光線が出て、
        // 深さの上限に着いたぶんだけ暗くなってしまう(それはそれで正しい振る舞いだが、ここで見たいものではない)。
        scene.Shapes.Add(new Plane("床", Vector3.UnitY, 0.0f, Material.Diffuse(Vector3.One)));
        scene.Shapes.Add(new Sphere("球", new Vector3(0.0f, 1.2f, 0.0f), 0.6f, Material.Diffuse(Vector3.One)));
        scene.Prepare(AccelerationMode.BruteForce);

        var tracer = new PathTracer(scene, new RenderSettings { MaxDepth = 128, RussianRoulette = false });
        var counters = new RayCounters();
        var eye = new Vector3(0.0f, 1.3f, -4.0f);
        float maxError = 0.0f;
        int tested = 0;
        int trapped = 0;
        const int Count = 20000;

        for (int i = 0; i < Count; i++)
        {
            var rng = Rng.Create(i, 14, 0);

            // 半分は床、半分は球を狙う(空へ抜けるだけの光線は当たり前なので数えない)。
            Vector3 target = i % 2 == 0
                ? new Vector3(rng.NextFloat() * 6.0f - 3.0f, 0.0f, rng.NextFloat() * 6.0f - 1.0f)
                : new Vector3(0.0f, 1.2f, 0.0f) + 0.6f * Sampler.UniformSphere(ref rng);

            var ray = new Ray(eye, Vector3.Normalize(target - eye));
            Vector3 color = tracer.Trace(ray, ref rng, ref counters, null);

            // <b>真っ黒(0)で返ってきた道は、深さの上限に着いた道</b>。
            // 球の輪郭をかすめる光線は判別式がほぼ 0 になり、丸め誤差で「球の内側から当たった」と
            // 判定されることがある。そうなると散乱の向きが球の中を向き、反射率 1 の閉じた空洞に
            // 閉じ込められて二度と出られない(128 回跳ね返って黒になる)。
            // 光線の追い方としては正しい振る舞いで、炉のテストで見たいものではないので数から外し、
            // 代わりに<b>その本数が全体のごく一部であること</b>を条件にする(要点9)。
            if (color == Vector3.Zero)
            {
                trapped++;
                continue;
            }

            tested++;
            maxError = MathF.Max(maxError, MaxAbs(color - new Vector3(SkyLevel)));
        }

        double bounces = (double)counters.Scatter / Math.Max(1, tested);
        report(
            maxError < 1e-6f && bounces > 1.0 && tested > Count / 2 && trapped < Count / 1000,
            $"白い炉(反射率 1 の床と球を、一様な明るさ {SkyLevel} の空に置く。{tested} 本)",
            $"空の色とのずれの最大 {maxError:E1}(期待 0。1本ごとに厳密)/ 跳ね返り {bounces:F2} 回 / "
            + $"球の中に閉じ込められた道 {trapped} 本は除いた");
    }

    /// <summary>
    /// 15. <b>パストレーサの直接光が、Whitted 法とぴったり一致する</b>(要点1)。
    ///
    /// <para>
    /// 拡散面だけ・点光源1つ・環境光 0・深さの上限 0 にすると、両方とも
    /// 「当たった面の直接光だけ」を計算する。<b>同じ式を通っているなら、1本ごとに同じ値</b>になるはず
    /// (点光源が1つなら NEE の光源選びに乱数が要らないので、パストレーサ側も決定的になる)。
    /// </para>
    /// <para>
    /// パストレーシングは「Whitted 法の置き換え」ではなく「Whitted 法を含む一般化」だということが、
    /// この一致で確かめられる。ずれるなら、NEE の pdf か BRDF の係数が違っている。
    /// </para>
    /// </summary>
    private static void CheckDirectLightMatchesWhitted(Action<bool, string, string> report)
    {
        var scene = new Scene("直接光だけ", default)
        {
            Ambient = Vector3.Zero,
            SkyHorizon = new Vector3(0.20f, 0.25f, 0.40f),
            SkyZenith = new Vector3(0.05f, 0.10f, 0.30f),
        };

        scene.Shapes.Add(new Plane("床", Vector3.UnitY, 0.0f, Material.Checker(new Vector3(0.70f, 0.20f, 0.15f), new Vector3(0.20f), 1.0f)));
        scene.Shapes.Add(new Sphere("玉A", new Vector3(-0.8f, 0.6f, 0.0f), 0.6f, Material.Diffuse(new Vector3(0.6f, 0.7f, 0.3f))));
        scene.Shapes.Add(new Sphere("玉B", new Vector3(0.9f, 0.4f, 1.2f), 0.4f, Material.Diffuse(new Vector3(0.2f, 0.3f, 0.8f))));
        scene.Lights.Add(new PointLight("光", new Vector3(-3.0f, 5.0f, -2.5f), Vector3.One, 120.0f));
        scene.Prepare(AccelerationMode.BruteForce);

        var settings = new RenderSettings { MaxDepth = 0, RussianRoulette = false };
        var whitted = new WhittedTracer(scene, settings);
        var path = new PathTracer(scene, settings);

        var eye = new Vector3(0.0f, 1.6f, -5.0f);
        var counters = new RayCounters();
        float maxDifference = 0.0f;
        int hits = 0;
        int shadowed = 0;
        const int Count = 5000;

        for (int i = 0; i < Count; i++)
        {
            var rng = Rng.Create(i, 15, 0);
            var direction = Vector3.Normalize(new Vector3(
                (rng.NextFloat() - 0.5f) * 2.4f,
                (rng.NextFloat() - 0.5f) * 1.2f,
                1.0f));
            var ray = new Ray(eye, direction);

            if (scene.Intersect(ray, ref counters, out _, out _))
            {
                hits++;
            }

            var rngA = Rng.Create(i, 15, 1);
            var rngB = Rng.Create(i, 15, 1);
            Vector3 a = whitted.Trace(ray, ref rngA, ref counters, null);
            Vector3 b = path.Trace(ray, ref rngB, ref counters, null);
            if (a.X + a.Y + a.Z < 1e-4f)
            {
                shadowed++;
            }

            maxDifference = MathF.Max(maxDifference, MaxAbs(a - b));
        }

        report(
            maxDifference < 1e-6f && hits > Count / 2 && shadowed > 0,
            $"パストレーサの直接光 = Whitted(拡散だけ・点光源1つ・深さ 0。{Count} 本)",
            $"色の差の最大 {maxDifference:E1}(期待 0) / 何かに当たった {hits} 本 / 影の中だった {shadowed} 本");
    }

    /// <summary>
    /// 16. <b>NEE とロシアンルーレットは、絵を変えずにノイズと光線の数だけを変える</b>(要点4・5)。
    ///
    /// <para>
    /// コーネルボックスを小さく3通り描いて比べる。3つの平均が一致していれば、どちらも<b>不偏</b>
    /// (答えを歪めていない)。NEE はノイズを大きく減らし、ロシアンルーレットは光線を減らす。
    /// </para>
    /// <para>
    /// ノイズは<b>画素ごとの平均の標準誤差</b>(σ/√N)を全画素で平均したもの。
    /// サンプル数が違う設定どうしを比べられるよう、√N で割ってある。
    /// </para>
    /// </summary>
    private static void CheckNeeAndRoulette(Action<bool, string, string> report)
    {
        const int Width = 40;
        const int Height = 24;
        var baseSettings = new RenderSettings { MaxDepth = 6, NextEventEstimation = false, RussianRoulette = false };

        // NEE なしの推定は<b>収束がとても遅い</b>(たまに光源に当たった道だけが極端に明るい)ので、
        // 平均を突き合わせる相手として使えるだけのサンプル数が要る。ここだけ 4096 本にしてある。
        RenderSmall(CornellBox(), baseSettings, Width, Height, 4096, out double meanPlain, out double noisePlain, out RayCounters plainRays);
        RenderSmall(CornellBox(), baseSettings with { NextEventEstimation = true }, Width, Height, 512, out double meanNee, out double noiseNee, out RayCounters neeRays);
        RenderSmall(CornellBox(), baseSettings with { NextEventEstimation = true, RussianRoulette = true }, Width, Height, 512, out double meanRoulette, out double noiseRoulette, out RayCounters rouletteRays);

        double neeError = Math.Abs(meanNee - meanPlain) / meanPlain;
        double rouletteError = Math.Abs(meanRoulette - meanNee) / meanNee;
        double noiseRatio = noiseNee / noisePlain;
        double rayRatio = (double)rouletteRays.Total / neeRays.Total;

        report(
            neeError < 0.03 && rouletteError < 0.03 && noiseRatio < 0.5 && rayRatio < 0.9,
            $"NEE とロシアンルーレット(コーネルボックス {Width}x{Height}。平均が一致すれば不偏)",
            $"平均 なし {meanPlain:F4} / NEE {meanNee:F4}(差 {neeError:P1})/ +RR {meanRoulette:F4}(差 {rouletteError:P1})"
            + $"/ ノイズは NEE で {noiseRatio:F2} 倍 / 光線は RR で {rayRatio:F2} 倍");
    }

    private static Scene CornellBox()
    {
        Scene scene = SceneLibrary.Create(3);
        scene.Prepare(AccelerationMode.Sah);
        return scene;
    }

    /// <summary>
    /// 小さく描いて、<b>明るさの平均</b>と<b>ノイズ</b>(画素ごとの平均の標準誤差)を測る。
    /// ここだけ <see cref="Parallel.For"/> を使う(3通り描くので、1本で回すと数秒かかる)。
    /// 乱数は画素とサンプル番号から作るので、スレッドの割り当てが変わっても答えは変わらない。
    /// </summary>
    private static void RenderSmall(
        Scene scene,
        RenderSettings settings,
        int width,
        int height,
        int samples,
        out double mean,
        out double noise,
        out RayCounters counters)
    {
        var camera = new Camera(scene.DefaultView, width, height);
        Tracer tracer = Tracer.Create(scene, settings);

        var totals = new double[height];
        var noises = new double[height];
        var shared = new RayCounters();
        object gate = new();

        Parallel.For(0, height, y =>
        {
            var rays = new RayCounters();
            double rowSum = 0.0;
            double rowNoise = 0.0;

            for (int x = 0; x < width; x++)
            {
                double sum = 0.0;
                double sumSquares = 0.0;

                for (int i = 0; i < samples; i++)
                {
                    var rng = Rng.Create(x, y, i, settings.DecorrelatePixels);
                    Vector2 offset = ProgressiveRenderer.SampleOffset(i);
                    Vector3 color = tracer.Sample(camera, x + offset.X, y + offset.Y, ref rng, ref rays);
                    double luminance = 0.2126 * color.X + 0.7152 * color.Y + 0.0722 * color.Z;
                    sum += luminance;
                    sumSquares += luminance * luminance;
                }

                double pixelMean = sum / samples;
                double variance = Math.Max(0.0, sumSquares / samples - pixelMean * pixelMean);
                rowSum += pixelMean;

                // 1サンプルあたりに揃えた標準偏差。サンプル数の違う設定どうしを比べられるようにする。
                rowNoise += Math.Sqrt(variance);
            }

            totals[y] = rowSum;
            noises[y] = rowNoise;
            lock (gate)
            {
                shared.Add(rays);
            }
        });

        mean = totals.Sum() / (width * height);
        noise = noises.Sum() / (width * height);
        counters = shared;
    }

    /// <summary>
    /// 17. AABB のスラブ法(要点7)。範囲の内外に加えて、<b>板と平行で、しかも板の上から出る光線</b>を確かめる。
    ///
    /// <para>
    /// その光線では <c>0 × ∞ = NaN</c> が出る。<c>MathF.Min</c> / <c>MathF.Max</c> で書くと NaN が伝わって
    /// 「外れ」になり、<b>箱の縁に沿った光線だけがすり抜ける</b>。比較で書けば NaN は無視されて正しくなる。
    /// </para>
    /// </summary>
    private static void CheckAabbSlab(Action<bool, string, string> report)
    {
        var box = new Aabb(new Vector3(-1.0f), new Vector3(1.0f));

        bool Hit(Vector3 origin, Vector3 direction, float tMax = float.PositiveInfinity)
            => box.Intersect(origin, Aabb.Reciprocal(Vector3.Normalize(direction)), 0.0f, tMax);

        bool front = Hit(new Vector3(0.0f, 0.0f, -5.0f), Vector3.UnitZ);
        bool inside = Hit(Vector3.Zero, Vector3.UnitZ);
        bool behind = Hit(new Vector3(0.0f, 0.0f, -5.0f), -Vector3.UnitZ);
        bool graze = Hit(new Vector3(0.999f, 0.0f, -5.0f), Vector3.UnitZ);
        bool outside = Hit(new Vector3(1.001f, 0.0f, -5.0f), Vector3.UnitZ);
        bool beyond = Hit(new Vector3(0.0f, 0.0f, -5.0f), Vector3.UnitZ, 3.9f);

        // 板(x = -1)の真上を、その板と平行に進む光線。
        bool onSlab = Hit(new Vector3(-1.0f, 0.0f, -5.0f), Vector3.UnitZ);
        bool parallelOutside = Hit(new Vector3(2.0f, 0.0f, -5.0f), Vector3.UnitZ);
        bool naive = NaiveSlab(box, new Vector3(-1.0f, 0.0f, -5.0f), Vector3.UnitZ);

        report(
            front && inside && !behind && graze && !outside && !beyond && onSlab && !parallelOutside && !naive,
            $"AABB のスラブ法(一辺 2 の箱)。正面 {HitText(front)} / 中から {HitText(inside)} / 後ろ {HitText(behind)} / "
            + $"1mm 内 {HitText(graze)} / 1mm 外 {HitText(outside)} / tMax 3.9 {HitText(beyond)}",
            $"板の上を板と平行に進む光線(0 かける無限大 = NaN): {HitText(onSlab)}"
            + $"。Min/Max で書くと {HitText(naive)} / 箱の外を平行に: {HitText(parallelOutside)}");
    }

    /// <summary>
    /// <c>MathF.Min</c> / <c>MathF.Max</c> で書いたスラブ法(17 の比べる相手)。
    /// これらは「どちらかが NaN なら NaN を返す」ので、NaN が最後まで伝わって比較が false になる。
    /// </summary>
    private static bool NaiveSlab(in Aabb box, Vector3 origin, Vector3 direction)
    {
        Vector3 inv = Aabb.Reciprocal(Vector3.Normalize(direction));
        float t0x = (box.Min.X - origin.X) * inv.X;
        float t1x = (box.Max.X - origin.X) * inv.X;
        float t0y = (box.Min.Y - origin.Y) * inv.Y;
        float t1y = (box.Max.Y - origin.Y) * inv.Y;
        float t0z = (box.Min.Z - origin.Z) * inv.Z;
        float t1z = (box.Max.Z - origin.Z) * inv.Z;

        float enter = MathF.Max(0.0f, MathF.Max(MathF.Min(t0x, t1x), MathF.Max(MathF.Min(t0y, t1y), MathF.Min(t0z, t1z))));
        float exit = MathF.Min(MathF.Max(t0x, t1x), MathF.Min(MathF.Max(t0y, t1y), MathF.Max(t0z, t1z)));
        return enter <= exit;
    }

    /// <summary>
    /// 18. <b>BVH は総当たりと同じ答えを返す</b>(要点7・8)。速くするための仕掛けが答えを変えていないことを、
    /// 2万本の光線で1本ずつ突き合わせる。
    ///
    /// <para>
    /// BVH の間違いは<b>絵に出にくい</b>のが厄介なところ。箱を1つ取りこぼしても、たいていは
    /// 「遠くの玉が数画素だけ欠ける」程度にしか見えず、ノイズに紛れる。だから絵ではなく数で確かめる。
    /// </para>
    /// <para>
    /// ついでに、1本あたりの交差判定の回数を3通りで測る——これが BVH を入れる理由そのもの。
    /// </para>
    /// </summary>
    private static void CheckBvhMatchesBruteForce(Action<bool, string, string> report)
    {
        Scene Build(AccelerationMode mode)
        {
            Scene scene = SceneLibrary.Create(4);
            scene.Prepare(mode);
            return scene;
        }

        Scene brute = Build(AccelerationMode.BruteForce);
        Scene median = Build(AccelerationMode.Median);
        Scene sah = Build(AccelerationMode.Sah);

        var camera = new Camera(brute.DefaultView, 960, 540);
        var rng = Rng.Create(18, 0, 0);
        var bruteRays = new RayCounters();
        var medianRays = new RayCounters();
        var sahRays = new RayCounters();
        int mismatches = 0;
        int hits = 0;
        const int Count = 20000;

        for (int i = 0; i < Count; i++)
        {
            Ray ray = camera.GetRay(rng.NextFloat() * 960.0f, rng.NextFloat() * 540.0f);
            bool hitBrute = brute.Intersect(ray, ref bruteRays, out float tBrute, out Shape? shapeBrute);
            bool hitMedian = median.Intersect(ray, ref medianRays, out float tMedian, out Shape? shapeMedian);
            bool hitSah = sah.Intersect(ray, ref sahRays, out float tSah, out Shape? shapeSah);

            if (hitBrute != hitMedian || hitBrute != hitSah)
            {
                mismatches++;
                continue;
            }

            if (!hitBrute)
            {
                continue;
            }

            hits++;

            // 場面は3つ別に作っているので、形は参照ではなく名前で突き合わせる(名前は場面の中で一意)。
            if (shapeBrute!.Name != shapeMedian!.Name || shapeBrute.Name != shapeSah!.Name
                || MathF.Abs(tBrute - tMedian) > 1e-5f || MathF.Abs(tBrute - tSah) > 1e-5f)
            {
                mismatches++;
            }
        }

        double bruteTests = (double)(bruteRays.BoxTests + bruteRays.ShapeTests) / Count;
        double medianTests = (double)(medianRays.BoxTests + medianRays.ShapeTests) / Count;
        double sahTests = (double)(sahRays.BoxTests + sahRays.ShapeTests) / Count;
        Bvh sahBvh = sah.Bvh!;
        Bvh medianBvh = median.Bvh!;

        report(
            mismatches == 0 && hits > Count / 2 && sahTests < bruteTests * 0.2,
            $"BVH と総当たりが一致(形 {brute.Shapes.Count} 個の場面へ {Count} 本。当たり {hits} 本、食い違い {mismatches} 件)",
            $"1本あたりの交差判定: 総当たり {bruteTests:F1} → 中央分割 {medianTests:F1}({bruteTests / medianTests:F1} 分の1)"
            + $"→ SAH {sahTests:F1}({bruteTests / sahTests:F1} 分の1)/ 節点 中央分割 {medianBvh.NodeCount} 深さ {medianBvh.MaxDepth}"
            + $" / SAH {sahBvh.NodeCount} 深さ {sahBvh.MaxDepth}");
    }

    /// <summary>RTIOW の書き方。内側からでも入射角の cos をそのまま使う(7 の比べる相手)。</summary>
    private static float NaiveSchlick(float cosI, float n1, float n2)
    {
        float r0 = Optics.NormalIncidenceReflectance(n1, n2);
        float m = 1.0f - cosI;
        return r0 + (1.0f - r0) * m * m * m * m * m;
    }

    /// <summary>法線 +Y の面に、入射角 <paramref name="degrees"/> 度で上から当たる向き。</summary>
    private static Vector3 Incidence(float degrees)
    {
        float radians = degrees * MathF.PI / 180.0f;
        return new Vector3(MathF.Sin(radians), -MathF.Cos(radians), 0.0f);
    }

    /// <summary>向きがばらばらの法線と、それに向かって当たる入射方向の組を1つ作る。</summary>
    private static void RandomIncidence(Random random, out Vector3 d, out Vector3 n)
    {
        n = RandomUnitVector(random);
        d = RandomUnitVector(random);
        if (Vector3.Dot(d, n) > 0.0f)
        {
            d = -d;
        }
    }

    /// <summary>球面の上に一様な向き。立方体の中の点を選び、球の外なら選び直す(RTIOW と同じ棄却法)。</summary>
    private static Vector3 RandomUnitVector(Random random)
    {
        while (true)
        {
            var v = new Vector3(random.NextSingle() * 2.0f - 1.0f, random.NextSingle() * 2.0f - 1.0f, random.NextSingle() * 2.0f - 1.0f);
            float lengthSquared = v.LengthSquared();
            if (lengthSquared > 1e-6f && lengthSquared <= 1.0f)
            {
                return v / MathF.Sqrt(lengthSquared);
            }
        }
    }

    private static float MaxAbs(Vector3 v) => MathF.Max(MathF.Abs(v.X), MathF.Max(MathF.Abs(v.Y), MathF.Abs(v.Z)));

    private static string HitText(bool hit) => hit ? "当たり" : "外れ";
}
