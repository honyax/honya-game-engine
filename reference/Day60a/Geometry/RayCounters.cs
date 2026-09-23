namespace CpuRayTracer;

/// <summary>
/// 光線の数。種類ごとに数える。描画スレッドごとに1つ持ち、行を描き終えたら足し合わせる
/// (1本ごとに <c>Interlocked</c> で共有の数を増やすと、スレッドどうしがその1つの変数を奪い合って遅くなる)。
///
/// <para>
/// <b>Day 60 で <c>Tracing/WhittedTracer.cs</c> からここへ移した。</b>
/// 追い方が2つ(<see cref="WhittedTracer"/> と <see cref="PathTracer"/>)になり、片方のファイルに置いておく理由が無くなったため。
/// 数えているのは「光線」で、<c>Geometry/</c> の言葉(<see cref="Ray"/> の隣)——
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

    /// <summary>飛ばした光線の合計。</summary>
    public readonly long Total => Camera + Shadow + Reflection + Refraction + Scatter;

    public void Add(in RayCounters other)
    {
        Camera += other.Camera;
        Shadow += other.Shadow;
        Reflection += other.Reflection;
        Refraction += other.Refraction;
        Scatter += other.Scatter;
    }
}
