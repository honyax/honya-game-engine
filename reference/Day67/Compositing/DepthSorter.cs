using System.Numerics;

namespace GaussianSplatting;

/// <summary>並べ替えのやり方(<c>O</c> キー)。</summary>
internal enum SortMode
{
    /// <summary>毎フレーム、いまのカメラで奥から並べ直す(正しい)。</summary>
    EveryFrame,

    /// <summary>並べ直すのを止め、止めた瞬間の順番のまま描く。カメラを回り込ませると重なりが崩れる。</summary>
    Frozen,

    /// <summary>ファイルに入っていた順(並べない)。</summary>
    FileOrder,
}

/// <summary>
/// 楕円体を<b>カメラから遠い順</b>に並べる(要点3)。CPU で毎フレーム。
///
/// <para>
/// 半透明のものを重ねる式(<c>色 = 新しい色 × α + 前の色 × (1 − α)</c>)は、奥のものから順に重ねたときだけ正しい。
/// ポリゴンは深度バッファで「いちばん手前の1枚」を選べば済んだが、半透明の楕円体は<b>全部が少しずつ効く</b>ので、
/// 1枚を選ぶのではなく、全部を順番どおりに重ねる必要がある。だから深度バッファを使わず、並べ替える。
/// </para>
/// <para>
/// <b>計数ソート(counting sort)</b>で並べる。奥行きを 16 ビット(0〜65535)の整数に丸め、
/// 「その値の楕円体が何個あるか」を数えて、数の累積から書き込む場所を決める。比べ合いが無いので、
/// 74 万個でも <c>Array.Sort</c> の数分の1で終わる。16 ビットに丸めるので、奥行きの差がごく小さいものの順は入れ替わりうるが、
/// 画面ではまず見分けられない(同じ深さの楕円体どうしなので)。
/// </para>
/// <para>
/// GPU で並べ替える(基数ソート)のが本格的な作り方で、元論文の実装はさらに<b>画面のタイルごとに</b>並べる。
/// 今日は CPU で1回だけ並べる、いちばん簡単な形にした(要点8)。
/// </para>
/// </summary>
internal sealed class DepthSorter
{
    private const int Buckets = 65536;

    private readonly int[] _counts = new int[Buckets];

    private ushort[] _keys = [];

    private float[] _depths = [];

    private uint[] _order = [];

    /// <summary>並べた結果。<c>Order[0]</c> がいちばん奥。GPU へはこの配列を送る。</summary>
    public ReadOnlySpan<uint> Order => _order;

    /// <summary>直前の並べ替えで、カメラより前(近すぎない)にあった個数。HUD に出す。</summary>
    public int InFront { get; private set; }

    /// <summary>
    /// 並べる。<see cref="SortMode.Frozen"/> のときは何もしない(前の順番を残す)。
    /// 楕円体の数が変わったとき(場面を替えたとき)は、どのやり方でも1回は並べ直す。
    /// </summary>
    public void Sort(SplatCloud cloud, in CameraFrame camera, SortMode mode)
    {
        int n = cloud.Count;
        bool resized = _order.Length != n;
        if (resized)
        {
            _order = new uint[n];
            _keys = new ushort[n];
            _depths = new float[n];
        }

        if (mode == SortMode.Frozen && !resized)
        {
            return;
        }

        if (mode == SortMode.FileOrder)
        {
            for (int i = 0; i < n; i++)
            {
                _order[i] = (uint)i;
            }

            InFront = n;
            return;
        }

        // 1. 奥行き(カメラの前向きの距離)の最小と最大。位置は並べ替えのためだけに1回読む。
        float[] depths = _depths;
        float minDepth = float.MaxValue;
        float maxDepth = float.MinValue;
        int inFront = 0;
        for (int i = 0; i < n; i++)
        {
            float d = Vector3.Dot(cloud.Positions[i] - camera.Position, camera.Forward);
            depths[i] = d;
            minDepth = MathF.Min(minDepth, d);
            maxDepth = MathF.Max(maxDepth, d);
            if (d >= CameraFrame.Near)
            {
                inFront++;
            }
        }

        InFront = inFront;

        // 2. 16 ビットの鍵にする。<b>遠いほど小さい鍵</b>にしておくと、鍵の小さい順 = 奥から手前の順になる。
        float scale = maxDepth > minDepth ? (Buckets - 1) / (maxDepth - minDepth) : 0.0f;
        Array.Clear(_counts);
        for (int i = 0; i < n; i++)
        {
            ushort key = (ushort)((maxDepth - depths[i]) * scale);
            _keys[i] = key;
            _counts[key]++;
        }

        // 3. 数の累積 = その鍵の楕円体を書き始める場所。
        int start = 0;
        for (int k = 0; k < Buckets; k++)
        {
            int count = _counts[k];
            _counts[k] = start;
            start += count;
        }

        // 4. 前から順に置いていく。同じ鍵どうしはファイルの順のまま(安定なソート)。
        for (int i = 0; i < n; i++)
        {
            _order[_counts[_keys[i]]++] = (uint)i;
        }
    }
}
