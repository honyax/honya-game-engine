using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **GPU に測らせるストップウォッチ**(Day 54)。タイマークエリ(<c>GL_TIME_ELAPSED</c>)の薄い皮。
///
/// <para>
/// Day 31 から HUD に出してきた ms は、ほとんどが <c>Stopwatch</c>——<b>CPU が命令を積み終えるまで</b>の時間だった。
/// GL の呼び出しは積むだけで返るので、GPU がそれを実行した時間ではない
/// (Day 50 の <c>CaptureDepth</c> や Day 52 の <c>DescribeDeferred</c> のコメントがその断り書き)。
/// 本当の時間は「計測」で <c>glFinish</c> を挟んで測っていたが、あれは GPU を<b>連続で全開にする</b>ので、
/// Day 53 の計測で開発機の電源が落ちた。
/// </para>
///
/// <para>
/// <b>GPU の時間は GPU に測らせる</b>。<c>glBeginQuery(GL_TIME_ELAPSED)</c> と <c>glEndQuery</c> で挟むと、
/// GPU がその間の命令を実行し終えたときに「何ナノ秒掛かったか」を書いてくれる。
/// 普段のフレーム(VSync で休みながら)の中で測れるので、負荷は1つも増えない。
/// </para>
///
/// <para>
/// <b>結果は数フレーム遅れて届く</b>。GPU は CPU より 1〜2 フレーム後ろを走っているので、
/// 積んだ直後に結果を聞くと、<b>GPU が追いつくまで CPU が待たされる</b>(それでは glFinish と同じ)。
/// そこでクエリを数本の輪にして、「前に積んだぶんで、もう届いているもの」だけを拾う。
/// </para>
///
/// <para>
/// 制約が1つある。<b>同じ種類のクエリは同時に1本しか走らせられない</b>(入れ子にできない)。
/// 「TAA 全体」と「その中の速度パス」を同時に測ることはできないので、測る区間は並べて置く。
/// </para>
/// </summary>
internal sealed class GpuTimer : IDisposable
{
    /// <summary>
    /// 輪の長さ。GPU の遅れ(1〜2 フレーム)より長ければよい。
    /// 足りないと、結果がまだ届いていないクエリの番が回ってきて、そのフレームは測れない(<see cref="Begin"/>)。
    /// </summary>
    private const int RingSize = 4;

    private readonly GL _gl;
    private readonly uint[] _queries = new uint[RingSize];

    /// <summary>積んだが、まだ結果を拾っていないクエリ。</summary>
    private readonly bool[] _pending = new bool[RingSize];

    private int _next;
    private bool _running;
    private bool _disposed;

    public GpuTimer(GL gl)
    {
        _gl = gl;

        for (int i = 0; i < RingSize; i++)
        {
            _queries[i] = gl.GenQuery();
        }
    }

    /// <summary>GPU で掛かった時間(移動平均、ms)。まだ1回も届いていなければ 0。</summary>
    public double Milliseconds { get; private set; }

    /// <summary>届いた結果の数。0 なら <see cref="Milliseconds"/> はまだ当てにならない。</summary>
    public int Samples { get; private set; }

    /// <summary>
    /// 測り始める。**前に積んだぶんで、届いているものを先に拾う**。
    ///
    /// <para>
    /// この番のクエリがまだ結果待ちなら、今回は測らない。待てば測れるが、
    /// 待つこと自体が「CPU が GPU に追いつかれるまで止まる」ことなので、測る意味が無くなる。
    /// </para>
    /// </summary>
    public void Begin()
    {
        Collect();

        if (_pending[_next])
        {
            _running = false;
            return;
        }

        _gl.BeginQuery(QueryTarget.TimeElapsed, _queries[_next]);
        _running = true;
    }

    /// <summary>測り終える。結果はあとの <see cref="Begin"/> で拾う。</summary>
    public void End()
    {
        if (!_running)
        {
            return;
        }

        _gl.EndQuery(QueryTarget.TimeElapsed);
        _pending[_next] = true;
        _next = (_next + 1) % RingSize;
        _running = false;
    }

    /// <summary>
    /// 届いている結果を拾う。**待たない**(<c>GL_QUERY_RESULT_AVAILABLE</c> を先に聞く)。
    ///
    /// <para>
    /// いきなり <c>GL_QUERY_RESULT</c> を聞くと、届いていなければ<b>届くまで CPU が止まる</b>。
    /// 「届いたか」を聞くのは待たずに返るので、届いていないものは次の機会に回す。
    /// </para>
    /// </summary>
    private void Collect()
    {
        for (int i = 0; i < RingSize; i++)
        {
            if (!_pending[i])
            {
                continue;
            }

            _gl.GetQueryObject(_queries[i], QueryObjectParameterName.ResultAvailable, out int available);
            if (available == 0)
            {
                continue;
            }

            // ナノ秒で届く。64bit なのは、32bit だと 4.3 秒で一周してしまうから
            // (GL 3.3 で ARB_timer_query が本体に入ったときに 64bit の取り出し口も一緒に入った)。
            _gl.GetQueryObject(_queries[i], QueryObjectParameterName.Result, out ulong nanoseconds);
            _pending[i] = false;

            double milliseconds = nanoseconds / 1_000_000.0;
            Milliseconds = Samples == 0 ? milliseconds : (Milliseconds * 0.9) + (milliseconds * 0.1);
            Samples++;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (uint query in _queries)
        {
            _gl.DeleteQuery(query);
        }
    }
}
