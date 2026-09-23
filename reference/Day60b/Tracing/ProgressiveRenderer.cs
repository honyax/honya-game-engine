using System.Diagnostics;
using System.Numerics;

namespace CpuRayTracer;

/// <summary>描画の進み具合。描画スレッドが1パスごとに丸ごと作り直し、UI スレッドはそれを読むだけ。</summary>
/// <param name="Samples">重ね終えたサンプルの数(全画素に何回ずつ光線を打ったか)。</param>
/// <param name="PassSeconds">直前の1パスにかかった時間。</param>
/// <param name="TotalSeconds">描き始めてからの時間。</param>
/// <param name="PassRays">直前の1パスで飛ばした光線の数。</param>
internal sealed record RenderStatus(int Samples, int MaxSamples, double PassSeconds, double TotalSeconds, RayCounters PassRays)
{
    public bool Finished => Samples >= MaxSamples;
}

/// <summary>
/// <b>積み重ねて描く</b>(プログレッシブレンダリング)。別スレッドで全画素に1本ずつ光線を打つ「パス」を繰り返し、
/// 結果を足し込んでいく。画面には、そのときまでの平均を出す(要点6)。
///
/// <para>
/// こうする理由は2つ。
/// </para>
/// <list type="number">
/// <item><b>待たされない</b>。64 サンプル重ねるには Release で 1〜2 秒、Debug では 10 秒以上かかるが、1パス目で絵が出て、だんだん滑らかになる</item>
/// <item><b>1画素の中の何か所も通した平均</b>が、そのままアンチエイリアスになる。パスごとに通す位置をずらすだけでよい</item>
/// </list>
///
/// <para>
/// スレッドの分け方: <b>行ごとに <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/></b>。
/// レイトレーサは画素どうしが互いを見ない(ある画素の光線は、隣の画素の結果を読まない)ので、
/// どう分けても答えが変わらず、鍵(lock)も要らない。1つの行を書くのは1つのスレッドだけなので、
/// <c>_accumulated</c> の同じ要素を2つのスレッドが同時に書くことも無い。
/// Day 61 で GPU に移すとき、この「画素ごとに独立」がそのままコンピュートシェーダの1回の呼び出しになる。
/// </para>
/// </summary>
internal sealed class ProgressiveRenderer : IDisposable
{
    /// <summary>
    /// 下見の粗さ。描き直しを始めた直後に、4x4 画素に1本だけ打って画面を埋める。
    /// マウスでカメラを回している最中は、1パス目が終わる前に次の描き直しが来るので、
    /// これが無いと何も表示されないまま回すことになる。1パスの 1/16 の手間で済む。
    /// </summary>
    private const int PreviewBlock = 4;

    /// <summary>サンプルの色の合計(線形)。平均は <c>合計 / サンプル数</c>。</summary>
    private readonly Vector3[] _accumulated;

    /// <summary>描画スレッドが画面用の値を作る作業場。できあがったら <see cref="_display"/> へ写す。</summary>
    private readonly int[] _staging;

    /// <summary>画面に出す値(0xAARRGGBB)。UI スレッドとの受け渡しは <see cref="_displayLock"/> の中だけで行う。</summary>
    private readonly int[] _display;

    private readonly object _displayLock = new();

    private int _displayVersion;

    private RenderStatus _status;

    private CancellationTokenSource? _cancellation;

    private Task? _task;

    public ProgressiveRenderer(int width, int height)
    {
        Width = width;
        Height = height;
        _accumulated = new Vector3[width * height];
        _staging = new int[width * height];
        _display = new int[width * height];
        _status = new RenderStatus(0, 1, 0.0, 0.0, default);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// いまの進み具合。参照の読み書きは不可分なので、描画スレッドが差し替えている最中に読んでも、
    /// 古いか新しいかのどちらか一方が丸ごと返る(半分だけ新しい値が混ざることは無い)。
    /// </summary>
    public RenderStatus Status => Volatile.Read(ref _status);


    /// <summary>
    /// 描き直しを始める。走っている描画があれば、止まるのを待ってから始める。
    /// 場面・カメラ・設定はどれも不変なので、描画スレッドにそのまま渡してよい。
    ///
    /// <para>
    /// <b>Day 60 で、追い方(<see cref="Tracer"/>)を引数で受け取るようになった。</b>
    /// Day 59 はここで <c>new WhittedTracer(...)</c> していたので、
    /// パストレーサを足すとこのクラスが「どちらで描くか」を知ることになる(Day 59 の設計書の歪み3)。
    /// いまは <see cref="Tracer.Create"/> が選び、ここは受け取った1つを回すだけ。
    /// </para>
    /// </summary>
    public void Start(Tracer tracer, Camera camera, RenderSettings settings)
    {
        Stop();

        Array.Clear(_accumulated);
        Volatile.Write(ref _status, new RenderStatus(0, settings.MaxSamples, 0.0, 0.0, default));

        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        _task = Task.Run(() => RenderLoop(tracer, camera, settings, token));
    }

    /// <summary>
    /// 描画を止める。取り消しは<b>行の切れ目</b>でしか効かないので、最大で1行ぶん待つことになる
    /// (普段は 1ms 足らず。場面2で深さの上限を 32 にすると1行が重くなり、0.3 秒ほど待つ)。
    /// </summary>
    public void Stop()
    {
        if (_task is null)
        {
            return;
        }

        _cancellation!.Cancel();
        _task.Wait();
        _cancellation.Dispose();
        _cancellation = null;
        _task = null;
    }

    /// <summary>
    /// 画面用の値が <paramref name="version"/> から更新されていれば <paramref name="destination"/> へ写す。
    /// 1パスごとにしか変わらないので、毎フレーム 50 万画素を写し直さずに済む。
    /// </summary>
    public bool CopyDisplay(int[] destination, ref int version)
    {
        lock (_displayLock)
        {
            if (_displayVersion == version)
            {
                return false;
            }

            Array.Copy(_display, destination, _display.Length);
            version = _displayVersion;
            return true;
        }
    }

    public void Dispose() => Stop();

    private void RenderLoop(Tracer tracer, Camera camera, RenderSettings settings, CancellationToken token)
    {
        // 1つだけ空けておくのは UI スレッドのため。全部を描画に回すと、マウスで回したときの反応が鈍る。
        var options = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };

        bool encodeSrgb = settings.View == ViewMode.Shaded;
        try
        {
            RenderPreview(tracer, camera, encodeSrgb, options);

            var total = Stopwatch.StartNew();
            for (int sample = 0; sample < settings.MaxSamples; sample++)
            {
                Vector2 offset = settings.Jitter ? SampleOffset(sample) : new Vector2(0.5f);

                var pass = Stopwatch.StartNew();
                var passRays = new RayCounters();
                object raysLock = new();

                Parallel.For(0, Height, options, y =>
                {
                    // 数えるのは行ごとに手元の変数で。足し合わせる(鍵を取る)のは行の終わりに1回だけ。
                    var rays = new RayCounters();
                    int row = y * Width;
                    for (int x = 0; x < Width; x++)
                    {
                        // 乱数は<b>画素とサンプル番号から作り直す</b>(要点6)。状態を持ち回さないので、
                        // どのスレッドがどの行を受け持っても絵が変わらない(Parallel.For の割り当ては毎回違う)。
                        var rng = Rng.Create(x, y, sample, settings.DecorrelatePixels);
                        _accumulated[row + x] += tracer.Sample(camera, x + offset.X, y + offset.Y, ref rng, ref rays);
                    }

                    lock (raysLock)
                    {
                        passRays.Add(rays);
                    }
                });

                double passSeconds = pass.Elapsed.TotalSeconds;
                Publish(sample + 1, encodeSrgb, options);
                Volatile.Write(ref _status, new RenderStatus(
                    sample + 1, settings.MaxSamples, passSeconds, total.Elapsed.TotalSeconds, passRays));
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() で取り消された。積み上げ途中の値は、次の Start() で消すのでそのままでよい。
        }
    }

    /// <summary>下見。<see cref="PreviewBlock"/> 四方に1本ずつ打ち、その色でブロックを塗る。積み重ねには入れない。</summary>
    private void RenderPreview(Tracer tracer, Camera camera, bool encodeSrgb, ParallelOptions options)
    {
        int blockRows = (Height + PreviewBlock - 1) / PreviewBlock;

        Parallel.For(0, blockRows, options, blockY =>
        {
            var rays = new RayCounters();
            int top = blockY * PreviewBlock;
            int bottom = Math.Min(top + PreviewBlock, Height);

            for (int left = 0; left < Width; left += PreviewBlock)
            {
                int right = Math.Min(left + PreviewBlock, Width);
                var rng = Rng.Create(left, top, 0, true);
                Vector3 color = tracer.Sample(camera, (left + right) * 0.5f, (top + bottom) * 0.5f, ref rng, ref rays);
                int pixel = ToPixel(color, encodeSrgb);

                for (int y = top; y < bottom; y++)
                {
                    Array.Fill(_staging, pixel, y * Width + left, right - left);
                }
            }
        });

        CommitStaging();
    }

    /// <summary>積み上げた合計をサンプル数で割って平均にし、画面用の値にする。</summary>
    private void Publish(int samples, bool encodeSrgb, ParallelOptions options)
    {
        float scale = 1.0f / samples;

        Parallel.For(0, Height, options, y =>
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                _staging[row + x] = ToPixel(_accumulated[row + x] * scale, encodeSrgb);
            }
        });

        CommitStaging();
    }

    private void CommitStaging()
    {
        lock (_displayLock)
        {
            Array.Copy(_staging, _display, _staging.Length);
            _displayVersion++;
        }
    }

    /// <summary>
    /// サンプル番号 <paramref name="sample"/> で、画素の中のどこを通すか(0〜1)。
    ///
    /// <para>
    /// 0 番は画素の真ん中。1 番からは <b>Halton(2, 3) 列</b>で、Day 54 の TAA のずらしと同じ点列を使う。
    /// 乱数で散らすより「すでに打った点から遠いところ」を順に埋めていくので、少ないサンプルで平均が落ち着く。
    /// TAA は<b>時間をまたいで</b>(毎フレーム少しずつ動く絵に)ずらしを重ねたが、今日は<b>止まった絵</b>に重ねるので、
    /// 前のフレームを動きに合わせて引き寄せる手間(再投影)が要らない。
    /// </para>
    /// <para>
    /// 全部の画素が同じずらしを使う。画素ごとに違う点を使う必要は無い——平均したいのは「1画素の中の位置」だけだから。
    /// </para>
    /// </summary>
    public static Vector2 SampleOffset(int sample)
        => sample == 0 ? new Vector2(0.5f) : new Vector2(RadicalInverse(sample, 2), RadicalInverse(sample, 3));

    /// <summary>
    /// van der Corput 列。番号を <paramref name="radix"/> 進で書き、桁を小数点の右へ鏡に映す(Day 54 と同じ)。
    /// 1, 2, 3, 4 … が 2 進なら 0.5, 0.25, 0.75, 0.125 … になり、区間を半分ずつ埋めていく。
    /// </summary>
    private static float RadicalInverse(int index, int radix)
    {
        float result = 0.0f;
        float fraction = 1.0f / radix;
        while (index > 0)
        {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }

        return result;
    }

    /// <summary>
    /// 線形の色を画面の1画素(0xAARRGGBB)にする。<paramref name="encodeSrgb"/> なら sRGB の曲線を掛ける。
    ///
    /// <para>
    /// 光の計算(足し算・平均)は<b>線形のまま</b>やり、画面に出す直前にだけ曲線を掛ける。
    /// 先に曲線を掛けてから平均すると、明るい画素と暗い画素の境目(アンチエイリアスした縁)が暗く沈む。
    /// 法線や光線の数の表示は「色」ではなく値そのものを見たいので、曲線を掛けない。
    /// </para>
    /// <para>
    /// 1.0 を超えた明るさは切り詰めるだけにしてある(トーンマッピングは Day 31 でやった話なので、今日は入れない)。
    /// </para>
    /// </summary>
    public static int ToPixel(Vector3 color, bool encodeSrgb)
    {
        if (encodeSrgb)
        {
            color = new Vector3(LinearToSrgb(color.X), LinearToSrgb(color.Y), LinearToSrgb(color.Z));
        }

        return unchecked((int)(0xFF000000u | (ToByte(color.X) << 16) | (ToByte(color.Y) << 8) | ToByte(color.Z)));
    }

    /// <summary>sRGB の伝達曲線。暗いところは直線、それ以外は 1/2.4 乗。</summary>
    private static float LinearToSrgb(float value)
    {
        if (!(value > 0.0f))
        {
            // 0 以下と NaN をまとめて 0 にする(NaN はどの比較も false になるので、否定で拾える)。
            return 0.0f;
        }

        if (value >= 1.0f)
        {
            return 1.0f;
        }

        return value <= 0.0031308f ? value * 12.92f : 1.055f * MathF.Pow(value, 1.0f / 2.4f) - 0.055f;
    }

    private static uint ToByte(float value)
    {
        if (!(value > 0.0f))
        {
            return 0;
        }

        return value >= 1.0f ? 255u : (uint)(value * 255.0f + 0.5f);
    }
}
