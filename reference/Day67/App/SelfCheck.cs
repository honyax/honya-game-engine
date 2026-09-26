using System.Diagnostics;
using System.Numerics;

namespace GaussianSplatting;

/// <summary>
/// 自己チェック(<c>C</c> キー)。答えの分かっている入力で式を呼び、数字で確かめる。
///
/// <para>
/// 1〜8 は CPU だけ、9〜11 は GPU で描いて読み戻す。乱数の種を固定しているので、何度押しても同じ数字が出る
/// (9〜11 は GPU の丸めの差で最後の桁が揺れることがある)。
/// </para>
/// </summary>
internal static class SelfCheck
{
    public static IReadOnlyList<string> Run(SplatRenderer? renderer, out string summary)
    {
        var lines = new List<string>();
        int passed = 0;
        int total = 0;

        void Report(string title, bool ok, params string[] details)
        {
            total++;
            if (ok)
            {
                passed++;
            }

            lines.Add($"{(ok ? "OK" : "NG")}  {total}. {title}");
            foreach (string detail in details)
            {
                lines.Add("        " + detail);
            }
        }

        CheckCovariance(Report);
        CheckProjection(Report);
        CheckJacobian(Report);
        CheckConic(Report);
        CheckShOrthonormal(Report);
        CheckZonalLobe(Report);
        CheckPlyRoundTrip(Report);
        CheckSort(Report);

        if (renderer is null)
        {
            Report("GPU と CPU の突き合わせ", false, "GPU が使えないので調べられない");
        }
        else
        {
            CheckGpuMatchesCpu(Report, renderer);
            CheckUniforms(Report, renderer);
            CheckOrderMatters(Report, renderer);
        }

        summary = $"{total} 項目中 {passed} 項目 OK";
        return lines;
    }

    private delegate void Reporter(string title, bool ok, params string[] details);

    /// <summary>
    /// 1. Σ = R S Sᵀ Rᵀ。回さなければ対角に σ² が並び、回した軸は Σ の固有ベクトル(固有値 σ²)になる。
    /// </summary>
    private static void CheckCovariance(Reporter report)
    {
        Covariance3 plain = Covariance3.FromScaleRotation(new Vector3(1.0f, 2.0f, 3.0f), Quaternion.Identity);
        Covariance3 turned = Covariance3.FromScaleRotation(new Vector3(1.0f, 2.0f, 3.0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2.0f));

        // 好きな回転で、回した3本の軸が固有ベクトルになっているか。
        var random = new Random(67);
        float worst = 0.0f;
        for (int n = 0; n < 1000; n++)
        {
            var q = Quaternion.Normalize(new Quaternion(Next(random), Next(random), Next(random), Next(random)));
            var s = new Vector3(0.1f + random.NextSingle(), 0.1f + random.NextSingle(), 0.1f + random.NextSingle());
            Covariance3 c = Covariance3.FromScaleRotation(s, q);
            Matrix4x4 r = Matrix4x4.CreateFromQuaternion(q);
            Vector3[] axes = [new(r.M11, r.M12, r.M13), new(r.M21, r.M22, r.M23), new(r.M31, r.M32, r.M33)];
            float[] sigmas = [s.X, s.Y, s.Z];
            for (int k = 0; k < 3; k++)
            {
                Vector3 a = axes[k];
                var sa = new Vector3(c.Sandwich(Vector3.UnitX, a), c.Sandwich(Vector3.UnitY, a), c.Sandwich(Vector3.UnitZ, a));
                worst = MathF.Max(worst, (sa - sigmas[k] * sigmas[k] * a).Length());
            }
        }

        bool ok = Near(plain.Xx, 1) && Near(plain.Yy, 4) && Near(plain.Zz, 9) && Near(plain.Xy, 0)
            && Near(turned.Xx, 4) && Near(turned.Yy, 1) && Near(turned.Zz, 9) && worst < 1e-4f;
        report("3D の共分散 Σ = R S Sᵀ Rᵀ", ok,
            $"σ (1, 2, 3) 回さない: 対角 ({plain.Xx:F3}, {plain.Yy:F3}, {plain.Zz:F3})(期待 1, 4, 9)"
                + $" / z まわりに 90 度: 対角 ({turned.Xx:F3}, {turned.Yy:F3}, {turned.Zz:F3})(期待 4, 1, 9)",
            $"好きな回転 1000 通り: 回した軸 a が Σa = σ²a を満たす。ずれの最大 {worst:E1}");
    }

    /// <summary>
    /// 2. 画面の真ん中にある球(σ が全部同じ)は、画面で半径 σ × 焦点距離 / 距離 の円になる。0.3 の広げは対角に 0.3 を足す。
    /// </summary>
    private static void CheckProjection(Reporter report)
    {
        CameraFrame camera = FlyView.Default.Frame(Vector3.UnitY, 960, 540);
        const float Sigma = 0.1f;
        Vector3 center = camera.Position + camera.Forward * 5.0f;
        Covariance3 ball = Covariance3.FromScaleRotation(new Vector3(Sigma), Quaternion.Identity);

        ProjectedSplat p = SplatProjection.Project(center, ball, camera, dilate: false)!.Value;
        ProjectedSplat d = SplatProjection.Project(center, ball, camera, dilate: true)!.Value;
        float expected = camera.Fx * Sigma / 5.0f;
        bool behind = SplatProjection.Project(camera.Position - camera.Forward, ball, camera, false) is null;

        bool ok = Near(MathF.Sqrt(p.A), expected, 1e-3f) && Near(MathF.Sqrt(p.C), expected, 1e-3f) && Near(p.B, 0.0f, 1e-3f)
            && Near(d.A - p.A, 0.3f, 1e-4f) && p.Center.Length() < 1e-3f && behind;
        report("楕円体を画面へ写す(真ん中の球)", ok,
            $"距離 5m・σ 0.1m・焦点距離 {camera.Fx:F1} 画素: 画面の σ ({MathF.Sqrt(p.A):F3}, {MathF.Sqrt(p.C):F3}) 画素(期待 {expected:F3})、xy {p.B:E1}",
            $"0.3 の広げ: 対角が {d.A - p.A:F4} 増える / 半径 {p.Radius} 画素(3σ の切り上げ) / カメラの後ろは描かない: {(behind ? "はい" : "いいえ")}");
    }

    /// <summary>
    /// 3. ヤコビアンは近似。3D のガウスから点をばらまき、<b>1個ずつ正確に z で割って</b>写したとき、
    /// 近似で作った 2σ の楕円の内側に何割が入るかを数える。本物の 2D のガウスなら 1 − e⁻² = 86.5%。
    ///
    /// <para>
    /// 共分散を測って比べないのは、z で割ると、カメラに近づいた点が画面の遠くへ飛び、分散が数個の点に振り回されるから。
    /// 「内側に入る割合」なら、飛んだ点は「外」と数えられるだけで済む。
    /// </para>
    /// </summary>
    private static void CheckJacobian(Reporter report)
    {
        CameraFrame camera = FlyView.Default.Frame(Vector3.UnitY, 960, 540);
        const float Expected = 0.8647f;

        float InsideAt(float sigma, float distance, float screenX)
        {
            Vector3 center = camera.Position + camera.Right * (screenX * distance) + camera.Forward * distance;
            Covariance3 ball = Covariance3.FromScaleRotation(new Vector3(sigma), Quaternion.Identity);
            ProjectedSplat approx = SplatProjection.Project(center, ball, camera, dilate: false)!.Value;

            var random = new Random(1);
            const int Samples = 40000;
            int inside = 0;
            for (int n = 0; n < Samples; n++)
            {
                Vector3 v = camera.ToView(center + sigma * new Vector3(Gaussian(random), Gaussian(random), Gaussian(random)));
                if (v.Z <= 0.0f)
                {
                    continue;
                }

                var offset = new Vector2(camera.Fx * v.X / v.Z, camera.Fy * v.Y / v.Z) - approx.Center;
                float m2 = approx.Conic.X * offset.X * offset.X + 2.0f * approx.Conic.Y * offset.X * offset.Y + approx.Conic.Z * offset.Y * offset.Y;
                if (m2 < 4.0f)
                {
                    inside++;
                }
            }

            return (float)inside / Samples;
        }

        float smallCenter = InsideAt(0.05f, 3.0f, 0.0f);
        float smallEdge = InsideAt(0.05f, 3.0f, camera.TanHalfX);
        float largeCenter = InsideAt(0.5f, 2.0f, 0.0f);
        float largeEdge = InsideAt(0.5f, 2.0f, camera.TanHalfX);
        bool ok = MathF.Abs(smallCenter - Expected) < 0.01f && MathF.Abs(smallEdge - Expected) < 0.01f
            && MathF.Abs(largeEdge - Expected) > MathF.Abs(smallEdge - Expected);
        report("ヤコビアンの近似(写した点が、近似の 2σ の楕円に入る割合。本物のガウスなら 86.5%)", ok,
            $"σ 0.05m・距離 3m: 真ん中 {smallCenter:P1} / 画面の端 {smallEdge:P1}",
            $"σ 0.5m・距離 2m: 真ん中 {largeCenter:P1} / 画面の端 {largeEdge:P1}(カメラに対して大きい楕円体ほど外れる)");
    }

    /// <summary>
    /// 4. 逆行列と半径と濃さ。σ (2.9, 1) 画素の楕円なら、半径は 3σ = 8.7 の切り上げで 9 画素、長い軸の 3σ で濃さは exp(−4.5)。
    /// (σ をちょうど 3 にしないのは、3 × 3 = 9 が浮動小数の誤差で 9.0000005 になり、切り上げで 10 になるから。)
    /// </summary>
    private static void CheckConic(Reporter report)
    {
        CameraFrame camera = FlyView.Default.Frame(Vector3.UnitY, 960, 540);
        float z = 4.0f;
        Vector3 center = camera.Position + camera.Forward * z;

        // 画面で σ が (2.9, 1) 画素になるように、3D の σ を焦点距離から逆算する。
        var scale = new Vector3(2.9f * z / camera.Fx, 1.0f * z / camera.Fy, 0.01f);
        ProjectedSplat p = SplatProjection.Project(center, Covariance3.FromScaleRotation(scale, Quaternion.Identity), camera, dilate: false)!.Value;

        // Σ' と Σ'⁻¹ を掛けて単位行列になるか。
        float i00 = p.A * p.Conic.X + p.B * p.Conic.Y;
        float i01 = p.A * p.Conic.Y + p.B * p.Conic.Z;
        float i11 = p.B * p.Conic.Y + p.C * p.Conic.Z;
        float at3Sigma = p.AlphaAt(new Vector2(3.0f * 2.9f, 0.0f), 1.0f);
        float atCenter = p.AlphaAt(Vector2.Zero, 1.0f);

        bool ok = Near(i00, 1, 1e-4f) && Near(i01, 0, 1e-4f) && Near(i11, 1, 1e-4f) && p.Radius == 9.0f
            && Near(at3Sigma, MathF.Exp(-4.5f), 1e-3f) && Near(atCenter, 0.99f, 1e-6f);
        report("逆行列・半径・濃さ(画面で σ (2.9, 1) 画素の楕円)", ok,
            $"Σ' Σ'⁻¹ = ({i00:F4}, {i01:F4}; {i01:F4}, {i11:F4}) / 半径 {p.Radius} 画素(期待 9)",
            $"濃さ(不透明度 1): 中心 {atCenter:F3}(0.99 で頭打ち) / 長い軸の 3σ {at3Sigma:F4}(期待 exp(-4.5) = {MathF.Exp(-4.5f):F4})");
    }

    /// <summary>
    /// 5. 16 個の基底が「互いに直交し、長さ 1」か。定数や符号を1つ写し間違えると、ここで落ちる。
    /// </summary>
    private static void CheckShOrthonormal(Reporter report)
    {
        const int Theta = 200;
        const int Phi = 400;
        var gram = new double[16, 16];
        Span<float> basis = stackalloc float[16];
        for (int i = 0; i < Theta; i++)
        {
            double theta = Math.PI * (i + 0.5) / Theta;
            double weight = Math.Sin(theta) * (Math.PI / Theta) * (2.0 * Math.PI / Phi);
            for (int j = 0; j < Phi; j++)
            {
                double phi = 2.0 * Math.PI * (j + 0.5) / Phi;
                var d = new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)), (float)(Math.Sin(theta) * Math.Sin(phi)), (float)Math.Cos(theta));
                SphericalHarmonics.Basis(d, 3, basis);
                for (int a = 0; a < 16; a++)
                {
                    for (int b = 0; b < 16; b++)
                    {
                        gram[a, b] += basis[a] * basis[b] * weight;
                    }
                }
            }
        }

        double worstDiagonal = 0.0, worstOff = 0.0;
        for (int a = 0; a < 16; a++)
        {
            for (int b = 0; b < 16; b++)
            {
                if (a == b)
                {
                    worstDiagonal = Math.Max(worstDiagonal, Math.Abs(gram[a, b] - 1.0));
                }
                else
                {
                    worstOff = Math.Max(worstOff, Math.Abs(gram[a, b]));
                }
            }
        }

        report("球面調和の基底(16 個)が正規直交か(球面で数値積分)", worstDiagonal < 1e-3 && worstOff < 1e-3,
            $"∫ Y_k² のずれの最大 {worstDiagonal:E1}(期待 0) / ∫ Y_j Y_k(j ≠ k)の最大 {worstOff:E1}(期待 0)");
    }

    /// <summary>
    /// 6. 軸の周りに対称な山を次数 3 で近似する。なだらかな山はほぼ表せるが、鋭い山ほど頂が低く潰れ、周りが負に波打つ。
    /// </summary>
    private static void CheckZonalLobe(Reporter report)
    {
        var details = new List<string>();
        var rms = new List<float>();
        var peaks = new List<float>();
        var axis = Vector3.Normalize(new Vector3(0.3f, 0.5f, -0.8f));
        Span<float> basis = stackalloc float[16];
        Span<float> coefficients = stackalloc float[48];
        foreach (float exponent in new[] { 4.0f, 16.0f, 64.0f })
        {
            // 山の係数(RGB とも同じ)。0.5 を引いた形にしておくと、Evaluate の +0.5 と打ち消し合う。
            float[] zonal = SphericalHarmonics.ZonalLobe(exponent);
            SphericalHarmonics.Basis(axis, 3, basis);
            for (int k = 0; k < 16; k++)
            {
                float c = zonal[SphericalHarmonics.DegreeOf(k)] * basis[k] - (k == 0 ? 0.5f / SphericalHarmonics.C0 : 0.0f);
                coefficients[k * 3] = coefficients[k * 3 + 1] = coefficients[k * 3 + 2] = c;
            }

            // 球面の上で、元の山との差の2乗平均と、いちばん低くなったところ(負の波打ち)。
            double sum = 0.0, area = 0.0;
            float lowest = float.MaxValue;
            for (int i = 0; i < 100; i++)
            {
                double theta = Math.PI * (i + 0.5) / 100;
                for (int j = 0; j < 200; j++)
                {
                    double phi = 2.0 * Math.PI * (j + 0.5) / 200;
                    var d = new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)), (float)(Math.Sin(theta) * Math.Sin(phi)), (float)Math.Cos(theta));
                    float t = Vector3.Dot(d, axis);
                    float truth = t > 0.0f ? MathF.Pow(t, exponent) : 0.0f;
                    float approx = Unclamped(coefficients, d);
                    double w = Math.Sin(theta);
                    sum += (approx - truth) * (approx - truth) * w;
                    area += w;
                    lowest = MathF.Min(lowest, approx);
                }
            }

            float peak = Unclamped(coefficients, axis);
            peaks.Add(peak);
            rms.Add((float)Math.Sqrt(sum / area));
            details.Add($"指数 {exponent,2}: 山の頂 {peak:F3}(期待 1) / 差の2乗平均 {rms[^1]:F4} / いちばん低いところ {lowest:F3}");
        }

        report("次数 3 で山を近似する(帯調和の回転の式)", peaks[0] > peaks[1] && peaks[1] > peaks[2] && rms[0] < 0.05f && peaks[0] > 0.8f, details.ToArray());

        static float Unclamped(ReadOnlySpan<float> coefficients, Vector3 d)
        {
            Span<float> b = stackalloc float[16];
            SphericalHarmonics.Basis(d, 3, b);
            float v = 0.5f;
            for (int k = 0; k < 16; k++)
            {
                v += b[k] * coefficients[k * 3];
            }

            return v;
        }
    }

    /// <summary>
    /// 7. .ply に書いて読み直すと元に戻るか。書くときに log・ロジットを取り、読むときに exp・シグモイドで戻す。
    /// </summary>
    private static void CheckPlyRoundTrip(Reporter report)
    {
        SplatCloud original = SyntheticScenes.Create(2);
        string path = Path.Combine(Path.GetTempPath(), $"day67-selfcheck-{Environment.ProcessId}.ply");
        try
        {
            using (var stream = File.Create(path))
            {
                PlyFormat.Write(stream, original);
            }

            long bytes = new FileInfo(path).Length;
            SplatCloud loaded = PlyFormat.Read(path);

            float position = 0, scale = 0, rotation = 0, opacity = 0, sh = 0;
            for (int i = 0; i < original.Count; i++)
            {
                position = MathF.Max(position, (loaded.Positions[i] - original.Positions[i]).Length());
                scale = MathF.Max(scale, ((loaded.Scales[i] - original.Scales[i]) / original.Scales[i]).Length());

                // q と −q は同じ回転なので、内積の絶対値で比べる。
                rotation = MathF.Max(rotation, 1.0f - MathF.Abs(Quaternion.Dot(loaded.Rotations[i], original.Rotations[i])));
                opacity = MathF.Max(opacity, MathF.Abs(loaded.Opacities[i] - original.Opacities[i]));
            }

            for (int j = 0; j < original.Sh.Length; j++)
            {
                sh = MathF.Max(sh, MathF.Abs(loaded.Sh[j] - original.Sh[j]));
            }

            bool ok = loaded.Count == original.Count && loaded.ShDegree == original.ShDegree
                && position == 0 && scale < 1e-5f && rotation < 1e-6f && opacity < 1e-5f && sh == 0;
            report(".ply に書いて読み直す(場面3、次数 3)", ok,
                $"{loaded.Count:N0} 個・次数 {loaded.ShDegree}・{bytes / (1024.0 * 1024.0):F1} MB(1個あたり {(double)bytes / loaded.Count:F1} バイト)",
                $"ずれの最大: 位置 {position:E1} / 大きさ(比) {scale:E1} / 回転 {rotation:E1} / 不透明度 {opacity:E1} / 係数 {sh:E1}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 8. 計数ソート。並んだ順が全部を1回ずつ含み、奥行きが(16 ビットの丸めの幅を除いて)減っていくか。<c>Array.Sort</c> と速さも比べる。
    /// </summary>
    private static void CheckSort(Reporter report)
    {
        SplatCloud cloud = SyntheticScenes.Create(1);
        CameraFrame camera = cloud.DefaultView.Frame(cloud.WorldUp, 960, 540);
        var sorter = new DepthSorter();

        sorter.Sort(cloud, camera, SortMode.EveryFrame);
        var clock = Stopwatch.StartNew();
        sorter.Sort(cloud, camera, SortMode.EveryFrame);
        double countingMs = clock.Elapsed.TotalMilliseconds;

        ReadOnlySpan<uint> order = sorter.Order;
        var seen = new bool[cloud.Count];
        float min = float.MaxValue, max = float.MinValue;
        var depths = new float[cloud.Count];
        for (int i = 0; i < cloud.Count; i++)
        {
            depths[i] = Vector3.Dot(cloud.Positions[i] - camera.Position, camera.Forward);
            min = MathF.Min(min, depths[i]);
            max = MathF.Max(max, depths[i]);
        }

        float bucket = (max - min) / 65535.0f;
        int reversed = 0, duplicates = 0;
        for (int k = 0; k < order.Length; k++)
        {
            if (seen[order[k]])
            {
                duplicates++;
            }

            seen[order[k]] = true;
            if (k > 0 && depths[order[k]] > depths[order[k - 1]] + bucket)
            {
                reversed++;
            }
        }

        var keys = (float[])depths.Clone();
        var indices = Enumerable.Range(0, cloud.Count).ToArray();
        clock.Restart();
        Array.Sort(keys, indices);
        double arraySortMs = clock.Elapsed.TotalMilliseconds;

        report("奥から並べる(計数ソート、場面2)", duplicates == 0 && reversed == 0 && seen.All(s => s),
            $"{cloud.Count:N0} 個: 重複 {duplicates} / 丸めの幅({bucket * 1000.0f:F2} mm)を超えて逆に並んだ所 {reversed}",
            $"計数ソート {countingMs:F1} ms / Array.Sort {arraySortMs:F1} ms(同じ奥行きの配列を並べた場合)");
    }

    /// <summary>
    /// 9. GPU の絵と、CPU で1画素ずつ重ね直した色が同じか。頂点シェーダと画素シェーダの式が C# と揃っているかの答え合わせ。
    /// </summary>
    private static void CheckGpuMatchesCpu(Reporter report, SplatRenderer renderer)
    {
        var details = new List<string>();
        float worst = 0.0f;
        int probes = 0;
        foreach (int sceneIndex in new[] { 0, 2 })
        {
            SplatCloud cloud = SyntheticScenes.Create(sceneIndex);
            CameraFrame camera = cloud.DefaultView.Frame(cloud.WorldUp, renderer.Width, renderer.Height);
            RenderOptions options = new();
            RenderScene(renderer, cloud, camera, options);

            float sceneWorst = 0.0f;
            for (int gy = 1; gy <= 5; gy++)
            {
                for (int gx = 1; gx <= 7; gx++)
                {
                    int x = renderer.Width * gx / 8;
                    int y = renderer.Height * gy / 6;
                    ProbeResult probe = PixelProbe.Probe(cloud, camera, x, y, options.ShDegree, options.Dilate, options.ScaleMultiplier);
                    Vector3 gpu = renderer.PixelColor(x, y, cloud.Background);
                    Vector3 diff = Vector3.Abs(gpu - probe.Color);
                    sceneWorst = MathF.Max(sceneWorst, MathF.Max(diff.X, MathF.Max(diff.Y, diff.Z)));
                    probes++;
                }
            }

            worst = MathF.Max(worst, sceneWorst);
            details.Add($"場面{sceneIndex + 1}「{cloud.Name}」: 35 画素の色の差の最大 {sceneWorst:E1}");
        }

        report("GPU の絵と、CPU で重ね直した色(35 画素 x 2 場面)", worst < 2e-3f, details.ToArray());
    }

    /// <summary>10. シェーダに渡した uniform が全部見つかったか(名前の打ち間違いは GL が黙って無視する)。</summary>
    private static void CheckUniforms(Reporter report, SplatRenderer renderer)
    {
        IReadOnlyList<string> missing = renderer.MissingUniforms;
        report("シェーダの uniform", missing.Count == 0,
            missing.Count == 0 ? "12 個とも見つかった" : $"見つからない: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// 11. 並べる順が絵を変えるか。正しい順(奥から)と、逆(手前から)と、ファイルの順で描いて、画素を数える。
    /// </summary>
    private static void CheckOrderMatters(Reporter report, SplatRenderer renderer)
    {
        SplatCloud cloud = SyntheticScenes.Create(0);
        CameraFrame camera = cloud.DefaultView.Frame(cloud.WorldUp, renderer.Width, renderer.Height);
        var sorter = new DepthSorter();
        sorter.Sort(cloud, camera, SortMode.EveryFrame);
        uint[] correct = sorter.Order.ToArray();
        uint[] reversed = correct.Reverse().ToArray();

        int[] Draw(uint[] order)
        {
            var pixels = new int[renderer.Width * renderer.Height];
            renderer.Upload(cloud);
            renderer.Render(camera, order, new RenderOptions(), pixels);
            return pixels;
        }

        int[] a = Draw(correct);
        int[] b = Draw(reversed);
        int changed = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (ChannelDifference(a[i], b[i]) > 2)
            {
                changed++;
            }
        }

        report("並べる向きで絵が変わる(場面1、奥から / 手前から)", changed > 1000,
            $"3 目盛りより大きく色が変わった画素 {changed:N0}({(double)changed / a.Length:P1})",
            "手前から「over」で重ねると、奥の楕円体が手前の上に乗る");
    }

    private static void RenderScene(SplatRenderer renderer, SplatCloud cloud, in CameraFrame camera, RenderOptions options)
    {
        var sorter = new DepthSorter();
        sorter.Sort(cloud, camera, SortMode.EveryFrame);
        renderer.Upload(cloud);
        renderer.Render(camera, sorter.Order, options, new int[renderer.Width * renderer.Height]);
    }

    private static int ChannelDifference(int a, int b)
    {
        int d = 0;
        for (int shift = 0; shift < 24; shift += 8)
        {
            d = Math.Max(d, Math.Abs(((a >> shift) & 0xff) - ((b >> shift) & 0xff)));
        }

        return d;
    }

    private static bool Near(float value, float expected, float tolerance = 1e-4f) => MathF.Abs(value - expected) <= tolerance;

    private static float Next(Random random) => random.NextSingle() * 2.0f - 1.0f;

    /// <summary>標準正規分布の乱数(Box-Muller)。</summary>
    private static float Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }
}
