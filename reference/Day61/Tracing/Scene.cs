using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 場面。形・光源・空と、<b>「この光線はどこに当たるか」に答える2つの問い合わせ</b>を持つ。
///
/// <para>
/// <b>Day 60 で、問い合わせの中身が総当たりから BVH に替わった</b>(要点7・8)。
/// 呼ぶ側(<see cref="Tracer"/>)の書き方は1文字も変わっていない——Day 59 の設計書で
/// 「入れ替えるのはこのクラスだけで済む」と書いたとおりになった。
/// </para>
/// <para>
/// 形を2つに分けて持つ。
/// </para>
/// <list type="bullet">
/// <item><b>囲める形</b>(球)… BVH に入れる。場面5では 499 個</item>
/// <item><b>囲めない形</b>(無限の平面)… BVH に入れられないので、毎回総当たりで聞く。今日はせいぜい6枚</item>
/// </list>
/// <para>
/// 光源も2種類ある。<b>点光源</b>(<see cref="Lights"/>)は大きさが 0 なので光線が偶然当たることは無く、
/// <b>必ず NEE でつなぐ</b>しかない。<b>面光源</b>(<see cref="AreaLights"/>。光る材質を持つ形)は
/// NEE でつなぐこともできるし、光線が偶然当たることもある(要点4)。
/// </para>
/// </summary>
internal sealed class Scene
{
    private static readonly Shape[] NoShapes = Array.Empty<Shape>();

    private Shape[] _unbounded = NoShapes;

    private Shape[] _bounded = NoShapes;

    private Shape[] _areaLights = NoShapes;

    private Bvh? _bvh;

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

    /// <summary>光る材質を持つ形。<see cref="Prepare"/> が <see cref="Shapes"/> から集める。</summary>
    public IReadOnlyList<Shape> AreaLights => _areaLights;

    /// <summary>いま使っている探し方。<see cref="Prepare"/> で決まる。</summary>
    public AccelerationMode Acceleration { get; private set; } = AccelerationMode.BruteForce;

    /// <summary>BVH(総当たりのときは null)。HUD に節点の数などを出すために公開している。</summary>
    public Bvh? Bvh => _bvh;

    /// <summary>BVH に入れられなかった形(無限の平面)の数。</summary>
    public int UnboundedCount => _unbounded.Length;

    /// <summary>
    /// 環境光。拡散面の色に掛けて足すだけの一定値(Whitted の論文の明るさの式にも、同じ役目の項がある)。
    ///
    /// <para>
    /// 影の中が真っ黒にならないようにする<b>ごまかし</b>。本当は、影の中にも空や周りの物体から跳ね返った光が届いている。
    /// <b><see cref="PathTracer"/> はこの値を読まない</b>——周りから届く光を数えるのが今日の主題で、
    /// この一定値はそれを諦めた印だから(要点1)。同じ場面を A キーで切り替えると、
    /// Whitted の影の中の「のっぺりした暗さ」と、パストレーサの影の中の「色の付いた暗さ」の差が見える。
    /// </para>
    /// </summary>
    public Vector3 Ambient { get; init; }

    /// <summary>空の色(地平線)。</summary>
    public Vector3 SkyHorizon { get; init; }

    /// <summary>空の色(天頂)。</summary>
    public Vector3 SkyZenith { get; init; }

    /// <summary>
    /// 光線が何にも当たらなかったときの色。上を向くほど <see cref="SkyZenith"/> に近づく。
    ///
    /// <para>
    /// パストレーシングでは、これが<b>無限に遠い面光源</b>として働く(要点4)。
    /// 場面5(球 500 個)に点光源も面光源も要らないのは、空だけで照らしているから。
    /// ただし NEE ではつなげない(向きを引く手立てを用意していない)ので、
    /// 空からの光は「散乱した先で偶然空に抜けた」経路だけが拾う。
    /// 閉じた場面(場面4のコーネルボックス)では、空の色を 0 にしておく——光線が外へ出ないので、どのみち読まれない。
    /// </para>
    /// </summary>
    public Vector3 Sky(Vector3 direction)
    {
        float up = Math.Clamp(direction.Y, 0.0f, 1.0f);
        return Vector3.Lerp(SkyHorizon, SkyZenith, MathF.Sqrt(up));
    }

    /// <summary>
    /// 形を仕分けし、必要なら BVH を作る。<b>描画を始める前に1回だけ</b>呼ぶ(Day 60 で追加)。
    ///
    /// <para>
    /// 場面は描画中「変わらないもの」として全スレッドで共有するので、ここで書き換えるのは
    /// <b>描画スレッドが1本も走っていないとき</b>だけ、という約束にしてある
    /// (<see cref="ViewerWindow"/> は <c>Stop()</c> してから呼ぶ)。
    /// </para>
    /// </summary>
    public void Prepare(AccelerationMode mode)
    {
        Acceleration = mode;

        _areaLights = Shapes.Where(s => s.Material.IsEmissive).ToArray();

        if (mode == AccelerationMode.BruteForce)
        {
            // Day 59 と同じ総当たり。平面と球を分ける意味も無いので、全部を「囲めない側」に置く。
            _unbounded = Shapes.ToArray();
            _bounded = NoShapes;
            _bvh = null;
            return;
        }

        _unbounded = Shapes.Where(s => !s.TryGetBounds(out _)).ToArray();
        _bounded = Shapes.Where(s => s.TryGetBounds(out _)).ToArray();
        _bvh = Bvh.Build(_bounded, mode);
    }

    /// <summary>
    /// <b>いちばん近く</b>で当たる形を探す(カメラ・反射・屈折・散乱の光線に使う)。
    ///
    /// <para>
    /// 当たるたびに <c>closest</c>(探す範囲の上限)を縮めていくのが要点。
    /// 先に近いものが見つかっていれば、奥の形はその距離より手前で当たらない限り答えを変えないので、
    /// 各形の <see cref="Shape.Intersect"/> は範囲の外の解をすぐ捨てられる。
    /// BVH も同じ <c>closest</c> を使って、遠い箱をまるごと捨てる。
    /// </para>
    /// <para>
    /// tMin は 0。自分自身に当たるのを避けるのは、始点を面から少し浮かせる側(<see cref="Tracer.SpawnRay"/>)の仕事にしてある(Day 59 の要点7)。
    /// </para>
    /// </summary>
    public bool Intersect(in Ray ray, ref RayCounters counters, out float t, [NotNullWhen(true)] out Shape? shape)
    {
        float closest = float.PositiveInfinity;
        shape = null;

        counters.ShapeTests += _unbounded.Length;
        foreach (Shape candidate in _unbounded)
        {
            if (candidate.Intersect(ray, 0.0f, closest, out float hitT))
            {
                closest = hitT;
                shape = candidate;
            }
        }

        _bvh?.Intersect(ray, ref counters, ref closest, ref shape);

        t = closest;
        return shape is not null;
    }

    /// <summary>
    /// 距離 <paramref name="maxDistance"/> より手前に<b>何か1つでも</b>あれば、それを返す(影の光線に使う)。
    ///
    /// <para>
    /// 影の光線が知りたいのは「光源が見えるか」だけで、<b>どれがいちばん近いかは要らない</b>。
    /// だから最初に見つかった時点で抜けてよい。<see cref="Intersect"/> と分けてあるのはこのため
    /// (場面5では、影の光線がカメラの光線より 2〜3 割速く終わる)。
    /// 戻り値を bool でなく形にしてあるのは、1画素を追ったときに「何に遮られたか」を出すため。
    /// </para>
    /// <para>
    /// ガラスも光を遮るものとして数える。<b>屈折率 1.00 の見えない玉にも影ができる</b>のはこのため(場面2)。
    /// 影の光線はまっすぐ光源へ向かうので、ガラスで曲がって届く光(集光。虫眼鏡で紙が焦げるあれ)はこの方法では描けない。
    /// パストレーサでは、その光は<b>NEE ではなく散乱の経路のほうが拾う</b>(場面6)。
    /// </para>
    /// </summary>
    public Shape? FindOccluder(in Ray ray, float maxDistance, ref RayCounters counters)
    {
        counters.ShapeTests += _unbounded.Length;
        foreach (Shape candidate in _unbounded)
        {
            if (candidate.Intersect(ray, 0.0f, maxDistance, out _))
            {
                return candidate;
            }
        }

        return _bvh?.FindOccluder(ray, maxDistance, ref counters);
    }
}
