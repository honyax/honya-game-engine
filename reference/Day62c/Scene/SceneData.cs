using System.Numerics;
using System.Runtime.InteropServices;

namespace HardwareRayTracer;

/// <summary>
/// 球1個ぶんの、<b>GPU に渡す形</b>(今日の要点3)。
///
/// <para>
/// C# の <c>class Sphere</c> をそのまま渡すことはできない。GPU 側には参照も継承も無いので、
/// Day 61 でやったのと同じく<b>平らな数の並び</b>に落とす。
/// </para>
/// <para>
/// 並びは GLSL の <c>std430</c> に合わせる。<c>vec4</c> は 16 バイト境界に置かれるので、
/// <c>vec4</c> を2つ並べたこの形は C# 側の 32 バイトとぴったり一致する。
/// <b>ここが1バイトずれると、絵が「なんとなくおかしい」形で壊れる</b>——
/// 色が隣の球のものになったり、半径が座標になったり。<c>vec3</c> を混ぜると
/// (16 バイト境界に揃えられて詰め物が入るので)まさにそれが起きるので、
/// <b>常に vec4 で並べて w に端数を詰める</b>のが事故の少ない書き方。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuSphere
{
    /// <summary>xyz = 中心、w = 半径。</summary>
    public Vector4 CenterRadius;

    /// <summary>xyz = 色(拡散なら反射率、金属なら F0)、w = 材質の種類(0 = 拡散、1 = 金属)。</summary>
    public Vector4 AlbedoKind;

    public static GpuSphere Diffuse(Vector3 center, float radius, Vector3 albedo)
        => new() { CenterRadius = new Vector4(center, radius), AlbedoKind = new Vector4(albedo, 0.0f) };

    public static GpuSphere Metal(Vector3 center, float radius, Vector3 f0)
        => new() { CenterRadius = new Vector4(center, radius), AlbedoKind = new Vector4(f0, 1.0f) };
}

/// <summary>
/// 今日の場面。<b>球だけ</b>を数を変えて並べる。
///
/// <para>
/// 床は無限の平面で、<b>シェーダに直接書いてある</b>(y = 0 の市松模様)。配列に入れないのは、
/// Day 60 の <c>Scene.Prepare</c> が無限の平面を BVH に入れず「囲めない側」に置いたのと同じ理由——
/// 無限に広がるものは箱で囲めない。<b>Day 62b で BLAS を建てるときにも同じ線引きが要る</b>ので、
/// 今日のうちから球と床を分けておく。
/// </para>
/// <para>
/// 球の数を 1〜4 キーで変えられる。<b>総当たりは数に比例して重くなり、
/// BLAS に入れるとほとんど変わらない</b>——それを見るのが Day 62b の目。
/// </para>
/// <para>
/// <b>Day 62c の差分は並び順だけ</b>。拡散の球を先に、金属の球を後ろにまとめて並べる。
/// 材質ごとに BLAS の<b>ジオメトリ</b>を分け、SBT で<b>材質ごとに別の hit シェーダ</b>を
/// 呼ばせるため(Day62c.md の要点2)。並べ替えても場面の見た目は変わらない。
/// </para>
/// </summary>
internal static class SceneData
{
    /// <summary>
    /// 1 / 2 / 3 / 4 キーに割り当てる球の数。
    /// <b>5000 個は Day 62b で追加</b>——総当たりでは 30ms を超えて実用にならない数を1つ入れておく。
    /// </summary>
    public static readonly int[] Presets = [8, 120, 720, 5000];

    /// <summary>起動時のカメラ。床と球がだいたい収まる高さから見下ろす。</summary>
    public static OrbitView DefaultView { get; } = new(
        Target: new Vector3(0.0f, 0.7f, 0.0f),
        Yaw: 0.35f,
        Pitch: 0.18f,
        Distance: 12.0f,
        FovYDegrees: 45.0f);

    /// <summary>
    /// 球を作る。<b>乱数の種を固定してある</b>ので、同じ数を指定すれば必ず同じ並びになる。
    /// 「同じ場面を BLAS に入れ替えたら絵が変わらないか」を確かめるとき、
    /// 場面が毎回変わっていては比べられない。
    ///
    /// <para>
    /// <b>返り値は拡散が先、金属が後ろ</b>に並んでいる(Day 62c)。
    /// <see cref="SceneSpheres.DiffuseCount"/> がその境目。
    /// </para>
    /// </summary>
    public static SceneSpheres Create(int count)
    {
        var spheres = new List<GpuSphere>(count)
        {
            // 目印になる大きい球3つ。左から 金属 / 拡散(赤) / 金属(金)。
            // 金属を2つ置いてあるのは、映り込みの中に小さい球が並ぶのを見たいから
            // ——総当たりの費用は**反射した光線でも同じだけかかる**ことが目で分かる。
            GpuSphere.Metal(new Vector3(-2.6f, 1.0f, 0.0f), 1.0f, new Vector3(0.95f, 0.95f, 0.97f)),
            GpuSphere.Diffuse(new Vector3(0.0f, 1.0f, 0.0f), 1.0f, new Vector3(0.75f, 0.25f, 0.20f)),
            GpuSphere.Metal(new Vector3(2.6f, 1.0f, 0.0f), 1.0f, new Vector3(0.90f, 0.70f, 0.30f)),
        };

        // 残りは小さい球をばらまく。大きい球と重ならない位置だけ採る。
        //
        // ばらまく範囲は 720 個までは ±10m で固定(**Day 62a とまったく同じ場面**にして、
        // 絵を1画素まで比べられるようにしておく)。5000 個だけは ±26m に広げる——
        // ±10m に 5000 個入れると球どうしが貫通して、何を見ているのか分からなくなるため。
        //
        // 広げると画面の外に出る球が増えるが、それがむしろ都合がよい。
        // **総当たりは、見えていない球にも満額の費用を払う**のが目で分かる。
        float extent = count <= 720 ? 10.0f : 26.0f;
        var rng = new Random(20260919);
        while (spheres.Count < count)
        {
            float x = (float)(rng.NextDouble() * 2.0 - 1.0) * extent;
            float z = (float)(rng.NextDouble() * 2.0 - 1.0) * extent;
            float radius = (float)(rng.NextDouble() * 0.12 + 0.12);
            var center = new Vector3(x, radius, z);

            bool overlaps = false;
            for (int i = 0; i < 3; i++)
            {
                Vector4 cr = spheres[i].CenterRadius;
                if (Vector3.Distance(center, new Vector3(cr.X, cr.Y, cr.Z)) < cr.W + radius + 0.15f)
                {
                    overlaps = true;
                    break;
                }
            }

            if (overlaps)
            {
                continue;
            }

            // 4個に1個を金属にする。反射が増えると光線の本数も増えるので、
            // 「絵の見た目」と「重さ」が結びついていることが分かりやすい。
            if (rng.Next(4) == 0)
            {
                spheres.Add(GpuSphere.Metal(center, radius, new Vector3(0.85f, 0.87f, 0.90f)));
            }
            else
            {
                // 一様な乱数をそのまま色にすると、3成分が似た値になりやすくて**全部が灰色がかる**。
                // 2つの乱数を掛けると小さい値に寄り、成分ごとの差が開いて色が付く(RTIOW と同じ手)。
                var albedo = new Vector3(
                    (float)(rng.NextDouble() * rng.NextDouble()),
                    (float)(rng.NextDouble() * rng.NextDouble()),
                    (float)(rng.NextDouble() * rng.NextDouble())) * 1.6f + new Vector3(0.05f);
                spheres.Add(GpuSphere.Diffuse(center, radius, Vector3.Min(albedo, new Vector3(0.9f))));
            }
        }

        // **材質で並べ替える**(Day 62c)。拡散が先、金属が後ろ。
        //
        // OrderBy は安定なので、同じ材質どうしの順番は変わらない。
        // 並べ替えても絵は変わらないが、総当たりの探索順は変わるので、
        // 重なった球がちょうど同じ距離で並ぶような画素では色が入れ替わりうる。
        GpuSphere[] sorted = [.. spheres.OrderBy(s => s.AlbedoKind.W)];
        int diffuseCount = sorted.Count(s => s.AlbedoKind.W == 0.0f);
        return new SceneSpheres(sorted, diffuseCount);
    }
}

/// <summary>
/// 場面の球と、<b>拡散と金属の境目</b>(Day 62c で追加)。
///
/// <para>
/// BLAS のジオメトリを2つに割り、0 番に拡散の球、1 番に金属の球を入れる。
/// シェーダ側は「何番目のジオメトリの、何番目の図形か」しか分からないので、
/// <b>この境目を渡して番号を足し戻す</b>ことになる。
/// </para>
/// <code>
///   球の番号 = (ジオメトリ番号 == 0) ? 図形の番号 : DiffuseCount + 図形の番号
/// </code>
/// <para>
/// 材質ごとにジオメトリを分けるのは、<b>SBT が「どの hit シェーダを呼ぶか」を
/// ジオメトリ単位でしか切り替えられない</b>から(要点3)。1つのジオメトリの中の
/// 図形ごとに変えることはできない。
/// </para>
/// </summary>
/// <param name="Spheres">拡散が先、金属が後ろに並んだ球。</param>
/// <param name="DiffuseCount">拡散の球の数(= 金属の球が始まる位置)。</param>
internal readonly record struct SceneSpheres(GpuSphere[] Spheres, int DiffuseCount)
{
    public int Count => Spheres.Length;

    public int MetalCount => Spheres.Length - DiffuseCount;
}
