using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// トレイルの節1つ。**位置と、生まれてからの秒数だけ**。
///
/// <para>
/// <see cref="Particle"/> と違って速度を持たない。
/// 節は自分では動かず、<b>置かれた場所に留まったまま薄れて消える</b>——
/// 動くのは「置く人」(剣先やキャラクター)のほうになる。
/// </para>
/// </summary>
internal struct TrailPoint
{
    public Vector3 Position;

    /// <summary>置かれてからの秒数。**幅・色・UV の全部の入力**になる。</summary>
    public float Age;
}

/// <summary>
/// **軌跡(トレイル)の節を溜めるリングバッファ**(Day 50)。今日の主役その1。
///
/// <para>
/// 剣先や弾のように「速く動くもの」は、1フレームごとの絵が飛び飛びになって
/// <b>目で追えない</b>。通った跡をリボンとして残すと、
/// 同じ動きが急に読めるようになる——残像は演出であると同時に、
/// **動きを画面に定着させる手段**でもある。
/// </para>
///
/// <para>
/// <b>粒(Day 49)と決定的に違うのは、順番が意味を持つこと</b>。
/// <see cref="ParticleEmitter"/> は死んだ粒を末尾と入れ替えて詰めていた(swap-remove)。
/// 順番が壊れても、加算合成なら足し算の順番は結果を変えないので困らなかった。
/// トレイルは<b>節を順に繋いでリボンを張る</b>ので、
/// 1つでも入れ替わると<b>リボンがねじれて自分自身を横切る</b>。
/// </para>
///
/// <para>
/// だから捨てるのは<b>必ずいちばん古い節から</b>になり、
/// データ構造は自然にリングバッファ(先頭と末尾の両方から触れる配列)に決まる。
/// 「詰め直しが O(1)」という目的は swap-remove と同じで、
/// <b>順番を保つという制約が1つ増えただけで、答えが別のものになる</b>。
/// </para>
///
/// <para>
/// <b>先端だけは特別扱い</b>する(<see cref="Move"/>)。
/// 最後の節は常に「いまの位置」に貼り付いていて、
/// <see cref="MinDistance"/> だけ離れて初めて固定され、新しい先端が生まれる。
/// こうしないと、ゆっくり動いているときに<b>リボンの先が本体から遅れる</b>——
/// 剣先とリボンの間に隙間が空くのがいちばん目に付く不具合になる。
/// </para>
/// </summary>
internal sealed class Trail
{
    private readonly TrailPoint[] _points;

    /// <summary>いちばん古い節の添字。**ここが動くことで「捨てる」を表す**。</summary>
    private int _start;

    public Trail(int capacity = 96)
    {
        // 2 未満だとリボンが1枚も張れない(節2つで四角形1枚)。
        _points = new TrailPoint[Math.Max(2, capacity)];
    }

    public int Capacity => _points.Length;

    /// <summary>いま持っている節の数。</summary>
    public int Count { get; private set; }

    /// <summary>節が消えるまでの秒数。**長くすると尾が伸びる**。</summary>
    public float Lifetime { get; set; } = 0.35f;

    /// <summary>
    /// 節を固定する間隔(m)。**細かくするほど曲線が滑らかになり、節を食う**。
    ///
    /// <para>
    /// 速さ 8m/s のものを 0.06m 間隔で置くと毎秒 133 節。
    /// 寿命 0.35 秒なら釣り合いは 47 節で、容量 96 に収まる。
    /// <b>「速さ ÷ 間隔 × 寿命」が容量を超えると尾が短くなる</b>——
    /// 粒の「放出 × 寿命」(Day 49 の要点8)と同じ形の見積もりになる。
    /// </para>
    /// </summary>
    public float MinDistance { get; set; } = 0.06f;

    /// <summary>先端の幅(m)。</summary>
    public float StartWidth { get; set; } = 0.22f;

    /// <summary>末尾の幅(m)。**細らせないと尾が板に見える**。</summary>
    public float EndWidth { get; set; } = 0.02f;

    public Vector4 StartColor { get; set; } = new(0.8f, 0.9f, 1.0f, 1.0f);

    public Vector4 EndColor { get; set; } = new(0.2f, 0.4f, 1.0f, 0.0f);

    /// <summary>容量が足りずに捨てた節の数。**0 でないなら尾が寿命より短い**。</summary>
    public int DroppedPoints { get; private set; }

    /// <summary>
    /// <paramref name="index"/> 番目の節。**0 がいちばん古い(尾の端)**、
    /// <see cref="Count"/> - 1 が先端になる。
    ///
    /// <para>
    /// リングバッファなので実体の並びは飛んでいるが、
    /// 外から見えるのは「古い順に並んだ列」だけにしてある。
    /// 描く側(<see cref="ParticleRenderer"/>)が剰余算を知る必要は無い。
    /// </para>
    /// </summary>
    public TrailPoint this[int index] => _points[(_start + index) % _points.Length];

    /// <summary>
    /// 先端を <paramref name="position"/> へ動かす。**毎フレーム呼ぶ**。
    ///
    /// <para>
    /// <see cref="MinDistance"/> 進むまでは<b>先端の節を書き換えるだけ</b>で、
    /// 節は増えない。離れて初めて新しい節を足す——
    /// つまり<b>節を置くのは距離が決めるのであって、フレームレートではない</b>。
    /// 144fps でも 60fps でも同じ形のリボンになる
    /// (Day 49 の「端数を持ち越す」と目的が同じで、手段が距離になっただけ)。
    /// </para>
    /// </summary>
    public void Move(Vector3 position)
    {
        if (Count < 2)
        {
            // 節が足りないときは、同じ位置に2つ置いて長さ 0 のリボンから始める。
            // **1つだけだと次のフレームで「直前に固めた節」が無い**。
            while (Count < 2)
            {
                Push(position);
            }

            return;
        }

        // 直前に固めた節(先端の1つ手前)から測る。
        // **先端から測ってはいけない**——先端は毎フレーム動くので、
        // どれだけ進んでも距離が 0 のままになり、節が永久に増えない。
        Vector3 committed = this[Count - 2].Position;

        if (Vector3.DistanceSquared(position, committed) >= MinDistance * MinDistance)
        {
            Push(position);
            return;
        }

        ref TrailPoint tip = ref _points[(_start + Count - 1) % _points.Length];
        tip.Position = position;
        tip.Age = 0.0f;
    }

    /// <summary>
    /// 節を老けさせて、寿命を過ぎたものを<b>古いほうから</b>捨てる。
    ///
    /// <para>
    /// <c>while</c> 1本で済むのがリングバッファの効き目になる。
    /// 節は置かれた順に並んでいるので、
    /// <b>いちばん古いものが寿命を過ぎていないなら、その先も過ぎていない</b>——
    /// 全部を舐める必要が無い。
    /// </para>
    /// </summary>
    public void Update(float deltaSeconds)
    {
        if (deltaSeconds <= 0.0f)
        {
            return;
        }

        for (int i = 0; i < Count; i++)
        {
            _points[(_start + i) % _points.Length].Age += deltaSeconds;
        }

        while (Count > 0 && _points[_start].Age >= Lifetime)
        {
            _start = (_start + 1) % _points.Length;
            Count--;
        }
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
        DroppedPoints = 0;
    }

    /// <summary>寿命の中での位置(0 = 先端、1 = 消える寸前)。</summary>
    public float NormalizedAge(int index) =>
        Lifetime <= 0.0f ? 1.0f : Math.Clamp(this[index].Age / Lifetime, 0.0f, 1.0f);

    public float WidthAt(float normalized) => float.Lerp(StartWidth, EndWidth, normalized);

    public Vector4 ColorAt(float normalized) => Vector4.Lerp(StartColor, EndColor, normalized);

    /// <summary>
    /// リボンを張る「横方向」。**進行方向と視線の両方に垂直**な向き。
    ///
    /// <para>
    /// ビルボード(Day 49 の要点1)は<b>カメラの右と上</b>で四角形を張っていた。
    /// トレイルはそうできない——リボンは<b>進行方向に沿っていなければならない</b>ので、
    /// 縦は自由に選べない。選べるのは横だけで、
    /// 「進行方向に垂直」かつ「カメラから見て最も広く見える」向き、
    /// つまり<b>進行方向 × カメラへの向き</b>になる。
    /// </para>
    ///
    /// <para>
    /// <b>真正面から見たときが縮退点</b>。剣先がカメラへ真っ直ぐ向かってくると
    /// 2本が平行になり、外積が 0 に潰れる。
    /// 正規化すると 0/0 で NaN が出て、<b>リボンが画面から消える</b>
    /// (NaN の頂点はクリップ段で落ちる)。
    /// この1点のためだけに逃げ道が要る、というのがビルボードとの実装上の差になる。
    /// </para>
    /// </summary>
    public static Vector3 Side(Vector3 direction, Vector3 toCamera)
    {
        Vector3 forward = Normalize(direction, Vector3.UnitX);
        Vector3 view = Normalize(toCamera, Vector3.UnitZ);

        Vector3 side = Vector3.Cross(forward, view);
        float lengthSquared = side.LengthSquared();

        // |a × b| = sin θ なので、これは「なす角が 1e-6 rad 未満」の判定になる。
        if (lengthSquared < 1e-12f)
        {
            // 進行方向が視線と重なっている。**どちらへ張っても同じ**なので、
            // 視線に垂直な適当な1本を返す(Day 49 の RandomInCone と同じ逃げ方)。
            Vector3 helper = MathF.Abs(view.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            side = Vector3.Cross(view, helper);
            lengthSquared = side.LengthSquared();

            if (lengthSquared < 1e-12f)
            {
                return Vector3.UnitX;
            }
        }

        return side / MathF.Sqrt(lengthSquared);
    }

    private static Vector3 Normalize(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : fallback;

    /// <summary>
    /// 節を1つ足す。**いっぱいならいちばん古いものを捨てる**。
    ///
    /// <para>
    /// ここが swap-remove(Day 49)との分かれ道になる。
    /// 末尾と入れ替えれば同じ O(1) で済むが、順番が壊れてリボンがねじれる。
    /// リングバッファは<b>添字を1つ進めるだけ</b>で、順番を保ったまま O(1) を買える。
    /// </para>
    /// </summary>
    private void Push(Vector3 position)
    {
        if (Count == _points.Length)
        {
            _start = (_start + 1) % _points.Length;
            Count--;
            DroppedPoints++;
        }

        _points[(_start + Count) % _points.Length] = new TrailPoint
        {
            Position = position,
            Age = 0.0f,
        };

        Count++;
    }
}
