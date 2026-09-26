using System.Diagnostics;
using System.Numerics;

namespace GaussianSplatting;

/// <summary>
/// 自前で作る場面(1〜3)。<b>学習せずに、楕円体を手で置く</b>。
///
/// <para>
/// 3DGS の楕円体は、ふつうは写真から学習して作る(今日はやらない。要点8)。
/// でも「楕円体を画面へ写して重ねる」ところは、楕円体がどうやって作られたかと関係が無いので、
/// 答えの分かっている楕円体を手で置けば、描く側だけを確かめられる。
/// </para>
/// <list type="number">
/// <item><b>楕円体3つ</b> … 大きな半透明の楕円体が3つ重なっているだけ。写し方(要点2)と重ね方(要点3)を目で見る</item>
/// <item><b>円板で覆った形</b> … 床・トーラス・球の表面に、平たい楕円体を 26 万枚敷き詰める。
/// 「面が無くても、薄い楕円体を並べれば面に見える」ことと、遠くで小さくなった楕円体(要点4)を見る</item>
/// <item><b>見る向きで色が変わる</b> … 3つの球に、見る向きで動くハイライトを球面調和(次数 3)で持たせる(要点5)</item>
/// </list>
/// </summary>
internal static class SyntheticScenes
{
    public const int Count = 3;

    /// <summary>自前の場面で光を「焼き込む」向き。3DGS には光源が無いので、色を作るときに1回だけ使う。</summary>
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.4f, 1.0f, 0.3f));

    public static SplatCloud Create(int index)
    {
        var clock = Stopwatch.StartNew();
        SplatCloud cloud = index switch
        {
            0 => ThreeEllipsoids(),
            1 => CoveredSurfaces(),
            _ => ViewDependentSpheres(),
        };
        cloud.LoadMilliseconds = clock.Elapsed.TotalMilliseconds;
        return cloud;
    }

    /// <summary>場面1: 大きな楕円体が3つ。赤は横長、緑は縦長、青は平たい。</summary>
    private static SplatCloud ThreeEllipsoids()
    {
        var builder = new Builder(0);
        builder.Add(
            new Vector3(-0.6f, 0.0f, 0.0f),
            new Vector3(1.0f, 0.25f, 0.25f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f),
            0.8f,
            new Vector3(0.9f, 0.15f, 0.1f));
        builder.Add(
            new Vector3(0.5f, 0.2f, -0.4f),
            new Vector3(0.3f, 0.9f, 0.3f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.35f),
            0.8f,
            new Vector3(0.2f, 0.8f, 0.25f));
        builder.Add(
            new Vector3(0.1f, -0.3f, 0.5f),
            new Vector3(0.8f, 0.3f, 0.6f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.8f),
            0.8f,
            new Vector3(0.2f, 0.35f, 0.95f));

        SplatCloud cloud = builder.Build("楕円体3つ");
        cloud.Background = new Vector3(0.1f, 0.1f, 0.12f);
        cloud.DefaultView = new FlyView(new Vector3(0.0f, 0.0f, 4.0f), 0.0f, 0.0f, 50.0f);
        return cloud;
    }

    /// <summary>
    /// 場面2: 床(市松)・トーラス・球の表面に、平たい楕円体を敷き詰める。
    ///
    /// <para>
    /// 1枚は「面に沿った2軸は間隔の 0.6 倍、面に垂直な軸は 2mm」の円板。
    /// 学習済みのシーンでも、面の上の楕円体はたいていこういう平たい形に落ち着く(薄いほうが面の縁がくっきりするので)。
    /// 色は光を焼き込んだもの(面の向きと光の向きの内積)で、<b>描くときには光の計算を1つもしない</b>。
    /// </para>
    /// </summary>
    private static SplatCloud CoveredSurfaces()
    {
        var builder = new Builder(0);

        // 床。20m 四方を 4cm 間隔で(500 x 500 = 25 万枚)。遠くで楕円体が1画素より小さくなるのを見るため、広くしてある。
        const float FloorSpacing = 0.04f;
        const int FloorCells = 500;
        for (int iz = 0; iz < FloorCells; iz++)
        {
            for (int ix = 0; ix < FloorCells; ix++)
            {
                float x = (ix + 0.5f) * FloorSpacing - FloorCells * FloorSpacing * 0.5f;
                float z = (iz + 0.5f) * FloorSpacing - FloorCells * FloorSpacing * 0.5f;
                bool dark = ((int)MathF.Floor(x * 2.0f) + (int)MathF.Floor(z * 2.0f)) % 2 != 0;
                Vector3 albedo = dark ? new Vector3(0.25f, 0.3f, 0.35f) : new Vector3(0.8f, 0.8f, 0.75f);
                builder.AddDisk(new Vector3(x, 0.0f, z), Vector3.UnitY, FloorSpacing, Lit(albedo, Vector3.UnitY));
            }
        }

        // トーラス(立てた輪)。中心 (0, 0.8, 0)、輪の半径 0.7、管の半径 0.25。
        const float TorusSpacing = 0.02f;
        const float MajorRadius = 0.7f;
        const float MinorRadius = 0.25f;
        var torusCenter = new Vector3(0.0f, 0.8f, 0.0f);
        int majorSteps = (int)(2.0f * MathF.PI * MajorRadius / TorusSpacing);
        int minorSteps = (int)(2.0f * MathF.PI * MinorRadius / TorusSpacing);
        for (int i = 0; i < majorSteps; i++)
        {
            float u = 2.0f * MathF.PI * i / majorSteps;
            var ring = new Vector3(MathF.Cos(u), MathF.Sin(u), 0.0f);
            for (int j = 0; j < minorSteps; j++)
            {
                float v = 2.0f * MathF.PI * j / minorSteps;
                Vector3 normal = MathF.Cos(v) * ring + MathF.Sin(v) * Vector3.UnitZ;
                Vector3 position = torusCenter + ring * MajorRadius + normal * MinorRadius;
                builder.AddDisk(position, normal, TorusSpacing, Lit(new Vector3(0.95f, 0.5f, 0.15f), normal));
            }
        }

        // 球。中心 (1.6, 0.5, 0.5)、半径 0.5。
        AddSphere(builder, new Vector3(1.6f, 0.5f, 0.5f), 0.5f, 0.02f, n => Lit(new Vector3(0.15f, 0.7f, 0.65f), n));

        SplatCloud cloud = builder.Build("円板で覆った形");
        cloud.Background = new Vector3(0.55f, 0.65f, 0.8f);
        cloud.DefaultView = new FlyView(new Vector3(0.3f, 1.3f, 4.5f), 0.0f, -0.18f, 50.0f);
        return cloud;
    }

    /// <summary>
    /// 場面3: 3つの球に、<b>見る向きで動くハイライト</b>を持たせる。左から山の鋭さ(指数)が 4 / 16 / 64。
    ///
    /// <para>
    /// 楕円体 i のハイライトは「光が面で鏡のように跳ね返った向き r から見たときにいちばん明るい」。
    /// SH の向きは<b>カメラ → 楕円体</b>なので、山の軸は −r になる。
    /// 山は軸の周りに対称なので、帯調和の回転の式(<see cref="SphericalHarmonics.ZonalLobe"/>)で係数にする。
    /// </para>
    /// <para>
    /// <b>指数 64 の鋭い山は、次数 3 では表せない</b>。16 個の係数で作れるのは、なだらかな山と、その周りの波打ちだけ
    /// (完成条件で、右の球のハイライトがぼやけて大きく、周りに暗い輪が出るのを見る)。
    /// </para>
    /// </summary>
    private static SplatCloud ViewDependentSpheres()
    {
        var builder = new Builder(SphericalHarmonics.MaxDegree);

        const float FloorSpacing = 0.04f;
        const int FloorCells = 150;
        for (int iz = 0; iz < FloorCells; iz++)
        {
            for (int ix = 0; ix < FloorCells; ix++)
            {
                float x = (ix + 0.5f) * FloorSpacing - FloorCells * FloorSpacing * 0.5f;
                float z = (iz + 0.5f) * FloorSpacing - FloorCells * FloorSpacing * 0.5f;
                builder.AddDisk(new Vector3(x, 0.0f, z), Vector3.UnitY, FloorSpacing, Lit(new Vector3(0.45f, 0.45f, 0.45f), Vector3.UnitY));
            }
        }

        float[] exponents = [4.0f, 16.0f, 64.0f];
        Vector3[] albedos = [new(0.8f, 0.15f, 0.12f), new(0.15f, 0.6f, 0.2f), new(0.15f, 0.25f, 0.8f)];
        Span<float> basis = stackalloc float[SphericalHarmonics.MaxCoefficients];
        for (int s = 0; s < exponents.Length; s++)
        {
            float[] zonal = SphericalHarmonics.ZonalLobe(exponents[s]);
            Vector3 albedo = albedos[s];
            var center = new Vector3((s - 1) * 1.3f, 0.6f, 0.0f);
            int first = builder.Count;
            AddSphere(builder, center, 0.5f, 0.015f, n => Lit(albedo, n));

            for (int i = first; i < builder.Count; i++)
            {
                Vector3 n = Vector3.Normalize(builder.Positions[i] - center);
                float nl = Vector3.Dot(n, LightDirection);
                if (nl <= 0.0f)
                {
                    continue;
                }

                // 鏡の向き r = reflect(−L, n)。これを目から見る向き(楕円体 → 目)が r のとき明るい。
                Vector3 r = 2.0f * nl * n - LightDirection;
                SphericalHarmonics.Basis(-r, SphericalHarmonics.MaxDegree, basis);
                const float Strength = 0.8f;
                for (int k = 0; k < SphericalHarmonics.MaxCoefficients; k++)
                {
                    builder.Coefficients[i][k] += new Vector3(Strength * zonal[SphericalHarmonics.DegreeOf(k)] * basis[k]);
                }
            }
        }

        SplatCloud cloud = builder.Build("見る向きで色が変わる");
        cloud.Background = new Vector3(0.08f, 0.08f, 0.1f);
        cloud.DefaultView = new FlyView(new Vector3(0.0f, 1.2f, 3.6f), 0.0f, -0.2f, 50.0f);
        return cloud;
    }

    /// <summary>光を焼き込んだ色。環境光 0.25 と、面の向きと光の内積 0.75。</summary>
    private static Vector3 Lit(Vector3 albedo, Vector3 normal)
        => albedo * (0.25f + 0.75f * MathF.Max(0.0f, Vector3.Dot(normal, LightDirection)));

    /// <summary>
    /// 球の表面に、ほぼ等間隔に円板を置く(フィボナッチ球面。緯度・経度の格子だと極に集まりすぎる)。
    /// </summary>
    private static void AddSphere(Builder builder, Vector3 center, float radius, float spacing, Func<Vector3, Vector3> color)
    {
        int count = (int)(4.0f * MathF.PI * radius * radius / (spacing * spacing));
        float golden = MathF.PI * (3.0f - MathF.Sqrt(5.0f));
        for (int i = 0; i < count; i++)
        {
            float y = 1.0f - 2.0f * (i + 0.5f) / count;
            float ring = MathF.Sqrt(1.0f - y * y);
            float angle = golden * i;
            var normal = new Vector3(MathF.Cos(angle) * ring, y, MathF.Sin(angle) * ring);
            builder.AddDisk(center + normal * radius, normal, spacing, color(normal));
        }
    }

    /// <summary>楕円体を1個ずつ足していき、最後に <see cref="SplatCloud"/> にする。</summary>
    private sealed class Builder(int shDegree)
    {
        public readonly List<Vector3> Positions = [];

        public readonly List<Vector3[]> Coefficients = [];

        private readonly List<Vector3> _scales = [];

        private readonly List<Quaternion> _rotations = [];

        private readonly List<float> _opacities = [];

        public int Count => Positions.Count;

        /// <summary>
        /// 1個足す。色は「どの向きから見ても同じ色」(次数 0)で、<b>0番の係数 = (色 − 0.5) / C0</b>
        /// (<see cref="SphericalHarmonics.Evaluate"/> の逆)。
        /// </summary>
        public void Add(Vector3 position, Vector3 scale, Quaternion rotation, float opacity, Vector3 color)
        {
            var coefficients = new Vector3[SphericalHarmonics.CoefficientCount(shDegree)];
            coefficients[0] = (color - new Vector3(0.5f)) / SphericalHarmonics.C0;

            Positions.Add(position);
            Coefficients.Add(coefficients);
            _scales.Add(scale);
            _rotations.Add(rotation);
            _opacities.Add(opacity);
        }

        /// <summary>
        /// 面に沿った円板を1枚足す。ローカルの z 軸を法線に向け、z だけを薄くする。
        /// </summary>
        public void AddDisk(Vector3 position, Vector3 normal, float spacing, Vector3 color)
        {
            float sigma = spacing * 0.6f;
            Add(position, new Vector3(sigma, sigma, 0.002f), RotationFromZ(normal), 0.95f, color);
        }

        public SplatCloud Build(string name)
        {
            var cloud = new SplatCloud(name, Count, shDegree);
            for (int i = 0; i < Count; i++)
            {
                cloud.Positions[i] = Positions[i];
                cloud.Scales[i] = _scales[i];
                cloud.Rotations[i] = _rotations[i];
                cloud.Opacities[i] = _opacities[i];
                for (int k = 0; k < Coefficients[i].Length; k++)
                {
                    cloud.SetSh(i, k, Coefficients[i][k]);
                }
            }

            return cloud;
        }

        /// <summary>+Z を <paramref name="normal"/> に回す四元数。真逆のときは x 軸まわりに半回転。</summary>
        private static Quaternion RotationFromZ(Vector3 normal)
        {
            float d = Vector3.Dot(Vector3.UnitZ, normal);
            if (d < -0.9999f)
            {
                return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
            }

            Vector3 axis = Vector3.Cross(Vector3.UnitZ, normal);
            if (axis.LengthSquared() < 1e-12f)
            {
                return Quaternion.Identity;
            }

            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(d, -1.0f, 1.0f)));
        }
    }
}
