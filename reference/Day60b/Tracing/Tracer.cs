using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 1画素を追ったときの記録(右クリック)。深さの分だけ字下げした行で残す。
/// 普段の描画では null を渡し、<b>文字列を1つも作らない</b>(<c>log?.Add(...)</c> は log が null なら引数ごと評価されない)。
/// </summary>
internal sealed class TraceLog
{
    public List<string> Lines { get; } = new();

    public void Add(int depth, string text) => Lines.Add(new string(' ', Math.Min(depth, 24) * 3) + text);
}

/// <summary>面に当たった点の情報。</summary>
/// <param name="Point">当たった点。</param>
/// <param name="Normal">法線。<b>光線がやって来た側を向けてある</b>(内側から当たったら裏返し済み)。</param>
/// <param name="ToViewer">当たった点から、光線の来た方へ向かう向き(<c>−Direction</c>)。</param>
/// <param name="FrontFace">外側から当たったか。ガラスで「入る」か「出る」かの区別になる。</param>
/// <param name="Distance">光線の始点からの距離。</param>
internal readonly record struct SurfaceHit(Vector3 Point, Vector3 Normal, Vector3 ToViewer, bool FrontFace, float Distance, Material Material);

/// <summary>
/// 光線の追い方の共通部分(<b>Day 60 で新設</b>)。
///
/// <para>
/// Day 59 の設計書で「歪み2・3」として挙げた形を直したもの。あの日は <c>WhittedTracer</c> が
/// 「光線の木の追い方」と「表示の切り替え」と「1画素の記録」を1つのクラスで抱えていたので、
/// パストレーサを足すとそこが丸ごと重複する。<b>追い方だけを差し替えられるように</b>、
/// 共通部分をここへ引き上げ、<see cref="Trace"/> 1つを抽象メソッドにした。
/// </para>
/// <list type="bullet">
/// <item><see cref="WhittedTracer"/> … 面に当たるたびに<b>枝分かれする木</b>(Day 59)</item>
/// <item><see cref="PathTracer"/> … 面に当たるたびに<b>行き先を1つ引く道</b>(Day 60)</item>
/// </list>
/// <para>
/// 場面と設定を作ったときに受け取り、その後は変えない。だから描画スレッドの全員で1つを共有してよい
/// (書き換わるのは呼ぶ側が <c>ref</c> で渡す <see cref="RayCounters"/> と <see cref="Rng"/> だけで、それはスレッドごとに別)。
/// </para>
/// </summary>
internal abstract class Tracer
{
    /// <summary>
    /// 次の光線の始点を、面から法線の向きへ浮かせる距離(m)。Day 59 の要点7。
    ///
    /// <para>
    /// 当たった点 <c>O + tD</c> は浮動小数で計算するので、本当の面から少しずれる。
    /// 面の<b>内側</b>にずれた点から光源へ影の光線を出すと、<b>出てすぐ自分の面に当たって</b>
    /// 「影になっている」と判定される。これが画面全体にざらざらの黒い点として出るのが影のにきび(shadow acne)。
    /// </para>
    /// <para>
    /// 1mm にしたのは、今日の場面(大きさ数 m、カメラから 3〜15m)で float の誤差(1e-6 m 程度)より十分大きく、
    /// 形どうしの隙間より十分小さいから。場面の大きさが桁で変わるなら、この値も合わせて変える必要がある。
    /// </para>
    /// </summary>
    public const float SurfaceOffset = 1e-3f;

    protected Tracer(Scene scene, RenderSettings settings)
    {
        Scene = scene;
        Settings = settings;
    }

    public Scene Scene { get; }

    public RenderSettings Settings { get; }

    /// <summary>設定に合う追い方を作る。<see cref="ProgressiveRenderer"/> が中身を知らずに済むように。</summary>
    public static Tracer Create(Scene scene, RenderSettings settings) => settings.Algorithm switch
    {
        TraceAlgorithm.Path => new PathTracer(scene, settings),
        _ => new WhittedTracer(scene, settings),
    };

    /// <summary>
    /// 画素の中の1点 (<paramref name="px"/>, <paramref name="py"/>) を通るカメラの光線を1本追って、その色を返す。
    /// 表示が「陰影」以外なら、その表示用の色を返す(<see cref="ViewMode"/>)。
    /// </summary>
    public Vector3 Sample(Camera camera, float px, float py, ref Rng rng, ref RayCounters counters)
    {
        Ray ray = camera.GetRay(px, py);
        long raysBefore = counters.Total;
        long testsBefore = counters.BoxTests + counters.ShapeTests;
        counters.Camera++;

        switch (Settings.View)
        {
            case ViewMode.Normal:
                return NormalColor(ray, ref counters);

            case ViewMode.RayCount:
                Trace(ray, ref rng, ref counters, null);
                return HeatColor(counters.Total - raysBefore, 6.0f);

            case ViewMode.Cost:
                Trace(ray, ref rng, ref counters, null);
                return HeatColor(counters.BoxTests + counters.ShapeTests - testsBefore, 10.0f);

            default:
                return Trace(ray, ref rng, ref counters, null);
        }
    }

    /// <summary>
    /// 1画素を追う(右クリック)。画素の真ん中を通る光線を1本だけ追い、全部を記録する。
    /// パストレーシングでは<b>毎回違う道</b>になる(押すたびに違う木が出る)——それ自体が要点2の実演になる。
    /// </summary>
    public TraceLog TracePixel(Camera camera, int x, int y, int sample, out Vector3 color, out RayCounters counters)
    {
        var log = new TraceLog();
        counters = default;
        counters.Camera++;

        var rng = Rng.Create(x, y, sample, Settings.DecorrelatePixels);
        Ray ray = camera.GetRay(x + 0.5f, y + 0.5f);
        color = Trace(ray, ref rng, ref counters, log);
        return log;
    }

    /// <summary>
    /// 光線を1本追って、その光線に沿ってやって来る光の色(線形)を返す。<b>追い方ごとに中身が違う</b>。
    /// </summary>
    public abstract Vector3 Trace(in Ray ray, ref Rng rng, ref RayCounters counters, TraceLog? log);

    /// <summary>
    /// 光線を場面に当て、<b>法線を光線の側へ向けた</b>当たりの情報を作る。追い方によらず同じ処理なのでここに置く。
    /// </summary>
    protected bool TryHit(in Ray ray, ref RayCounters counters, out SurfaceHit hit, out Shape shape)
    {
        if (!Scene.Intersect(ray, ref counters, out float t, out Shape? candidate))
        {
            hit = default;
            shape = null!;
            return false;
        }

        Vector3 point = ray.At(t);
        Vector3 normal = candidate.OutwardNormal(point);

        // 光線と外向きの法線が同じ向き(内積が正)なら、光線は形の内側から当たっている。
        // 法線を光線の側へ裏返しておくと、この先の計算(光源の向きとの内積、始点を浮かせる向き)が
        // 表か裏かを気にせず1通りで書ける。表か裏かは FrontFace に残しておき、ガラスだけが使う。
        bool frontFace = Vector3.Dot(ray.Direction, normal) < 0.0f;
        if (!frontFace)
        {
            normal = -normal;
        }

        hit = new SurfaceHit(point, normal, -ray.Direction, frontFace, t, candidate.Material);
        shape = candidate;
        return true;
    }

    /// <summary>
    /// 次の光線を作る。始点を <paramref name="side"/> の向きへ浮かせ、<b>向きを長さ 1 に直してから</b>渡す。
    ///
    /// <para>
    /// 反射も屈折も散乱も、式の上では長さ 1 の向きから長さ 1 の向きを作る。それでも正規化し直すのは、
    /// <b>浮動小数の誤差が跳ね返るたびに増幅する</b>から(Day 59 の要点7)。向きの長さが 1 から e だけずれると、
    /// 球の交差(<see cref="Sphere.Intersect"/>)は長さ 1 を前提に解くので当たる点が面から外れ、
    /// その点から求めた法線の長さもずれ、その法線で反射した向きはさらにずれる。
    /// ガラス玉の中でほぼ垂直に反射を繰り返すと、<b>ずれは1回ごとに約 17 倍</b>になった(自己チェック10)。
    /// </para>
    /// </summary>
    public Ray SpawnRay(Vector3 point, Vector3 side, Vector3 direction)
        => new(OffsetAlong(point, side), Vector3.Normalize(direction));

    /// <summary>
    /// 次の光線の始点。設定で浮かせるのを切ったら(E キー)、当たった点をそのまま使う(Day 59 の要点7)。
    /// </summary>
    protected Vector3 OffsetAlong(Vector3 point, Vector3 direction)
        => Settings.OffsetOrigin ? point + direction * SurfaceOffset : point;

    protected float DielectricFresnel(float cosI, float n1, float n2) => Settings.Fresnel switch
    {
        FresnelMode.Schlick => Optics.SchlickDielectric(cosI, n1, n2),
        FresnelMode.Off => Optics.NormalIncidenceReflectance(n1, n2),
        _ => Optics.FresnelDielectric(cosI, n1, n2),
    };

    /// <summary>金属は「正確」でもシュリックを使う(<see cref="Optics.SchlickConductor"/> の説明を参照)。</summary>
    protected Vector3 MetalFresnel(float cosI, Vector3 f0)
        => Settings.Fresnel == FresnelMode.Off ? f0 : Optics.SchlickConductor(cosI, f0);

    /// <summary>法線の表示。何にも当たらなければ黒。</summary>
    private Vector3 NormalColor(in Ray ray, ref RayCounters counters)
    {
        if (!TryHit(ray, ref counters, out SurfaceHit hit, out _))
        {
            return Vector3.Zero;
        }

        return hit.Normal * 0.5f + new Vector3(0.5f);
    }

    /// <summary>
    /// 数を色にする。<b>対数で7段</b>(紺 → 青 → 水色 → 緑 → 黄 → 赤 → 白)。
    ///
    /// <para>
    /// <paramref name="maxPower"/> は「白になる数の log2」。光線の数なら 6(64 本で白)、
    /// 交差判定の数なら 10(1024 回で白)。段の境目は2倍ずつではなくなるが、
    /// <b>総当たりと BVH の差が 100 倍あるので、そのくらい広く取らないと画面が真っ白になる</b>。
    /// </para>
    /// </summary>
    public static Vector3 HeatColor(long count, float maxPower)
    {
        ReadOnlySpan<Vector3> stops =
        [
            new(0.00f, 0.00f, 0.30f),
            new(0.00f, 0.30f, 1.00f),
            new(0.00f, 0.90f, 0.90f),
            new(0.20f, 0.90f, 0.10f),
            new(1.00f, 0.90f, 0.00f),
            new(1.00f, 0.20f, 0.00f),
            new(1.00f, 1.00f, 1.00f),
        ];

        float scale = (stops.Length - 1) / maxPower;
        float level = Math.Clamp(MathF.Log2(MathF.Max(count, 1)) * scale, 0.0f, stops.Length - 1);
        int index = Math.Min((int)level, stops.Length - 2);
        return Vector3.Lerp(stops[index], stops[index + 1], level - index);
    }
}
