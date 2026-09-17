using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 今日の自己チェック(C キー)。10 項目。
///
/// <para>
/// どれも<b>窓も描画スレッドも使わない</b>。交差・反射・屈折・フレネルの式を、答えの分かっている入力で呼んで比べる。
/// 絵を見て「それらしい」と思えても、屈折の式の符号を1つ間違えただけで<b>それらしい別の絵</b>になるのがレイトレーサなので、
/// 式そのものを数字で確かめておく。
/// </para>
/// <para>
/// 乱数は種を固定している(<c>new Random(59)</c>)。何度押しても同じ数字が出る。
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

            if (hitShape.Intersect(new Ray(point + normal * WhittedTracer.SurfaceOffset, toLight), 0.0f, distance, out _))
            {
                acneWith++;
            }
        }

        float ratioWithout = (float)acneWithout / Math.Max(lit, 1);
        report(
            lit > 5000 && ratioWithout > 0.1f && acneWith == 0,
            "影のにきび(光の側を向いた点から、光源へ影の光線を出す)",
            $"{lit} 点のうち、浮かせないと {acneWithout} 本({ratioWithout:P1})が自分に当たる / {WhittedTracer.SurfaceOffset * 1000.0f:F0}mm 浮かせると {acneWith} 本");
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
        var tracer = new WhittedTracer(new Scene("玉だけ", default), new RenderSettings());

        // 中心から少し外した点から、ほぼ垂直に内側の面へ当てる(ガラス玉の真ん中を見た光線が、中で往復するときの形)。
        var start = new Ray(new Vector3(0.02f, 0.01f, 0.0f), Vector3.Normalize(new Vector3(0.1f, 0.05f, 1.0f)));
        const int Bounces = 30;

        float normalizedDrift = BounceInside(sphere, start, Bounces, ray => tracer.SpawnRay(ray.Origin, ray.Normal, ray.Direction), out _);
        float rawDrift = BounceInside(
            sphere,
            start,
            Bounces,
            ray => new Ray(ray.Origin + ray.Normal * WhittedTracer.SurfaceOffset, ray.Direction),
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
