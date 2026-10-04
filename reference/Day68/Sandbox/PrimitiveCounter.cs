using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **GPU に「図形をいくつ出したか」を数えさせる**(Day 63a)。
/// <c>GL_PRIMITIVES_GENERATED</c> クエリの薄い皮。
///
/// <para>
/// ジオメトリシェーダの壊れ方でいちばん厄介なのは、<b>絵が出てしまう</b>種類。
/// <c>max_vertices</c> を小さく書くと、超えたぶんは<b>エラーも警告も無く捨てられる</b>し、
/// <c>EndPrimitive</c> を忘れても「なんとなく線が繋がった絵」が出る。
/// どちらも「思ったより少ない / 多い」だけなので、絵を睨んでも気付けない。
/// </para>
///
/// <para>
/// このクエリは<b>ラスタライザへ渡る直前の図形の数</b>を数える。
/// ジオメトリシェーダがある場合は<b>その段が出した数</b>なので、
/// 「三角形 384 枚を入れたら線分が 1536 本出た」を機械で確かめられる。
/// カリングや深度テストで消えるのはこの後なので、**画面に見えている数ではない**
/// (見えている数を数えたいなら <c>GL_SAMPLES_PASSED</c> のほうで、あれは画素を数える)。
/// </para>
///
/// <para>
/// 使い分けを2つ持たせてある。
/// <list type="bullet">
/// <item><see cref="Begin"/> / <see cref="End"/> … 毎フレーム用。<b>待たない</b>。
///       <see cref="GpuTimer"/> と同じく輪にして、届いているものだけ拾う</item>
/// <item><see cref="Measure"/> … 自己チェック用。<b>待つ</b>。
///       その場で正確な数が要るので、届くまで CPU を止める</item>
/// </list>
/// </para>
/// </summary>
internal sealed class PrimitiveCounter : IDisposable
{
    private const int RingSize = 4;

    private readonly GL _gl;
    private readonly uint[] _queries = new uint[RingSize];
    private readonly bool[] _pending = new bool[RingSize];

    /// <summary>待ってでも答えが要るとき用の、輪に入れない1本。</summary>
    private readonly uint _immediate;

    private int _next;
    private bool _running;
    private bool _disposed;

    public PrimitiveCounter(GL gl)
    {
        _gl = gl;

        for (int i = 0; i < RingSize; i++)
        {
            _queries[i] = gl.GenQuery();
        }

        _immediate = gl.GenQuery();
    }

    /// <summary>直前に届いた「出した図形の数」。まだ1回も届いていなければ 0。</summary>
    public ulong Count { get; private set; }

    /// <summary>数え始める。**この番のクエリがまだ結果待ちなら、今回は数えない**。</summary>
    public void Begin()
    {
        Collect();

        if (_pending[_next])
        {
            _running = false;
            return;
        }

        _gl.BeginQuery(QueryTarget.PrimitivesGenerated, _queries[_next]);
        _running = true;
    }

    public void End()
    {
        if (!_running)
        {
            return;
        }

        _gl.EndQuery(QueryTarget.PrimitivesGenerated);
        _pending[_next] = true;
        _next = (_next + 1) % RingSize;
        _running = false;
    }

    /// <summary>
    /// **1回の描画で出た図形の数を、待って受け取る**(自己チェック用)。
    ///
    /// <para>
    /// <b>同じ種類のクエリは入れ子にできない</b>ので、輪のほうが走っている最中には使えない。
    /// 自己チェックはメニューから呼ぶ(描画の外)ので、その心配は無い。
    /// </para>
    /// </summary>
    public ulong Measure(Action draw)
    {
        _gl.BeginQuery(QueryTarget.PrimitivesGenerated, _immediate);
        draw();
        _gl.EndQuery(QueryTarget.PrimitivesGenerated);

        // **ここは待つ**。GL_QUERY_RESULT を聞くと、届くまで CPU が止まる。
        // 毎フレームやると GPU と CPU の足並みが崩れるが、1回きりなら問題にならない。
        _gl.GetQueryObject(_immediate, QueryObjectParameterName.Result, out ulong result);
        return result;
    }

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

            _gl.GetQueryObject(_queries[i], QueryObjectParameterName.Result, out ulong count);
            _pending[i] = false;
            Count = count;
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

        _gl.DeleteQuery(_immediate);
    }
}
