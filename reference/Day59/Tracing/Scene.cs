using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 場面。形・光源・空と、<b>「この光線はどこに当たるか」に答える2つの問い合わせ</b>を持つ。
///
/// <para>
/// 今日の問い合わせは<b>全部の形に順番に聞くだけ</b>(総当たり)。形が N 個なら光線1本で N 回の交差判定になる。
/// 今日の場面は多くて 11 個なので気にならないが、Day 60 で形が数千個になると光線1本が数千回の判定になり、
/// そこで BVH(空間を箱で区切って、光線が通らない箱の中身をまとめて飛ばす)を入れる。
/// <b>呼ぶ側(<see cref="WhittedTracer"/>)は、中身が総当たりか BVH かを知らない</b>——入れ替えるのはこのクラスだけで済む。
/// </para>
/// </summary>
internal sealed class Scene
{
    public Scene(string name, OrbitView defaultView)
    {
        Name = name;
        DefaultView = defaultView;
    }

    public string Name { get; }

    /// <summary>場面を切り替えた直後(と R キー)のカメラ。</summary>
    public OrbitView DefaultView { get; }

    public List<Shape> Shapes { get; } = new();

    public List<PointLight> Lights { get; } = new();

    /// <summary>
    /// 環境光。拡散面の色に掛けて足すだけの一定値(Whitted の論文の明るさの式にも、同じ役目の項がある)。
    ///
    /// <para>
    /// 影の中が真っ黒にならないようにする<b>ごまかし</b>。本当は、影の中にも空や周りの物体から跳ね返った光が届いている。
    /// それを「どの向きからどれだけ届くか」まで数えるのが Day 60 のパストレーシングで、そうなるとこの一定値は要らなくなる。
    /// </para>
    /// </summary>
    public Vector3 Ambient { get; init; }

    /// <summary>空の色(地平線)。</summary>
    public Vector3 SkyHorizon { get; init; }

    /// <summary>空の色(天頂)。</summary>
    public Vector3 SkyZenith { get; init; }

    /// <summary>
    /// 光線が何にも当たらなかったときの色。上を向くほど <see cref="SkyZenith"/> に近づく。
    /// 何にも当たらない光線は「無限遠の空」を見ているとみなす(RTIOW と同じ青のグラデーション)。
    /// 鏡やガラスに空が映るのは、反射・屈折の光線がここに行き着くから。
    /// </summary>
    public Vector3 Sky(Vector3 direction)
    {
        float up = Math.Clamp(direction.Y, 0.0f, 1.0f);
        return Vector3.Lerp(SkyHorizon, SkyZenith, MathF.Sqrt(up));
    }

    /// <summary>
    /// <b>いちばん近く</b>で当たる形を探す(カメラ・反射・屈折の光線に使う)。
    ///
    /// <para>
    /// 当たるたびに <c>closest</c>(探す範囲の上限)を縮めていくのが要点。
    /// 先に近いものが見つかっていれば、奥の形はその距離より手前で当たらない限り答えを変えないので、
    /// 各形の <see cref="Shape.Intersect"/> は範囲の外の解をすぐ捨てられる。
    /// </para>
    /// <para>
    /// tMin は 0。自分自身に当たるのを避けるのは、始点を面から少し浮かせる側(<see cref="WhittedTracer"/>)の仕事にしてある(要点7)。
    /// </para>
    /// </summary>
    public bool Intersect(in Ray ray, out float t, [NotNullWhen(true)] out Shape? shape)
    {
        float closest = float.PositiveInfinity;
        shape = null;

        foreach (Shape candidate in Shapes)
        {
            if (candidate.Intersect(ray, 0.0f, closest, out float hitT))
            {
                closest = hitT;
                shape = candidate;
            }
        }

        t = closest;
        return shape is not null;
    }

    /// <summary>
    /// 距離 <paramref name="maxDistance"/> より手前に<b>何か1つでも</b>あれば、それを返す(影の光線に使う)。
    ///
    /// <para>
    /// 影の光線が知りたいのは「光源が見えるか」だけで、<b>どれがいちばん近いかは要らない</b>。
    /// だから最初に見つかった時点で抜けてよい。<see cref="Intersect"/> と分けてあるのはこのため
    /// (大きな場面では、影の光線がカメラの光線より何倍も速く終わる)。
    /// 戻り値を bool でなく形にしてあるのは、1画素を追ったときに「何に遮られたか」を出すため。
    /// </para>
    /// <para>
    /// ガラスも光を遮るものとして数える。<b>屈折率 1.00 の見えない玉にも影ができる</b>のはこのため(場面2)。
    /// 影の光線はまっすぐ光源へ向かうので、ガラスで曲がって届く光(集光。虫眼鏡で紙が焦げるあれ)はこの方法では描けない。
    /// </para>
    /// </summary>
    public Shape? FindOccluder(in Ray ray, float maxDistance)
    {
        foreach (Shape candidate in Shapes)
        {
            if (candidate.Intersect(ray, 0.0f, maxDistance, out _))
            {
                return candidate;
            }
        }

        return null;
    }
}
