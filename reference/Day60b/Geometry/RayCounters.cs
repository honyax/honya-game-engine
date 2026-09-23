namespace CpuRayTracer;

/// <summary>
/// 光線と交差判定の数。種類ごとに数える。描画スレッドごとに1つ持ち、行を描き終えたら足し合わせる
/// (1本ごとに <c>Interlocked</c> で共有の数を増やすと、スレッドどうしがその1つの変数を奪い合って遅くなる)。
///
/// <para>
/// <b>Day 60 で <c>Tracing/WhittedTracer.cs</c> からここへ移した。</b>
/// BVH(<see cref="Bvh"/>)が箱と形の判定の数を数えるようになり、<c>Geometry/</c> からも使うようになったため。
/// ここに置かないと <c>Geometry → Tracing → Geometry</c> の循環ができる。
/// 数えているのは「光線」と「交差判定」で、どちらも <c>Geometry/</c> の言葉——
/// カメラ・影・反射といった<b>名前</b>を付けているのが <c>Tracing/</c> 側、という切り分けにした。
/// </para>
/// </summary>
internal struct RayCounters
{
    /// <summary>カメラから出た光線(1サンプルに1本)。</summary>
    public long Camera;

    /// <summary>影の光線(拡散面から光源へ。パストレーサでは NEE の光線)。</summary>
    public long Shadow;

    /// <summary>反射の光線(金属とガラス)。</summary>
    public long Reflection;

    /// <summary>屈折の光線(ガラス)。</summary>
    public long Refraction;

    /// <summary>散乱の光線(パストレーサの拡散面。Day 60 で追加)。</summary>
    public long Scatter;

    /// <summary>AABB との判定の回数(BVH の節点を1つ見るごとに1)。<b>光線ではない</b>ので <see cref="Total"/> に入れない。</summary>
    public long BoxTests;

    /// <summary>形(球・平面)との交差判定の回数。同じく <see cref="Total"/> に入れない。</summary>
    public long ShapeTests;

    /// <summary>飛ばした光線の合計。</summary>
    public readonly long Total => Camera + Shadow + Reflection + Refraction + Scatter;

    public void Add(in RayCounters other)
    {
        Camera += other.Camera;
        Shadow += other.Shadow;
        Reflection += other.Reflection;
        Refraction += other.Refraction;
        Scatter += other.Scatter;
        BoxTests += other.BoxTests;
        ShapeTests += other.ShapeTests;
    }
}
