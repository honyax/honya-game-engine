using System.Numerics;

namespace HonyaEngine;

/// <summary>場面。シェーダ側の <c>uScene</c> と同じ番号。</summary>
internal enum SdfSceneKind
{
    /// <summary>基本形と演算。滑らかな和・差・積・回るトーラス。**どれも距離を長く見積もらない**。</summary>
    Primitives,

    /// <summary>無限の繰り返し。**1本ぶんの式で、柱が地平線まで並ぶ**。</summary>
    Repetition,

    /// <summary>ねじれ。**距離の約束(傾き 1 以下)が破れる**ので、歩幅を縮めないと表面が崩れる。</summary>
    Twist,
}

/// <summary>光線を1本進めた結果。</summary>
/// <param name="Hit">面に当たったか(外れ = 遠すぎた、または歩数が尽きた)。</param>
/// <param name="Distance">近クリップ面から進んだ距離(m)。</param>
/// <param name="Steps">距離関数を呼んだ回数 = 歩数。</param>
internal readonly record struct MarchResult(bool Hit, float Distance, int Steps);

/// <summary>
/// **レイマーチングの場面を C# に写したもの**(Day 58)。<c>raymarch.frag</c> の <c>map</c> と、その周りの
/// 球面追跡・法線・影・AO を1行ずつ同じ式で持つ。
///
/// <para>
/// 絵はシェーダだけで描ける。これが要るのは<b>確かめるため</b>で、使い道は3つ。
/// </para>
/// <list type="number">
/// <item><b>距離の約束を測る</b>(<see cref="MaxGradient"/>)。傾きが 1 を超える場面では歩幅を縮めないと壊れる</item>
/// <item><b>GPU の答えと突き合わせる</b>(自己チェック)。同じ画素の光線を C# でも追って、当たった深さを比べる</item>
/// <item><b>数える</b>(<see cref="Evaluations"/>)。1画素で距離関数を何回呼んでいるか——シェーダの中では数えられない</item>
/// </list>
///
/// <para>
/// GL を1行も使わないので、<b>窓を開かずに確かめられる</b>(自己チェックの前半7項目)。
/// </para>
/// </summary>
internal sealed class SdfScene
{
    /// <summary>床の高さ。Day 31 の床(<c>Program.FloorMatrix</c>)と同じ。**ラスタと混ぜたときに揃う**。</summary>
    public const float GroundY = -0.5f;

    /// <summary>繰り返しの升目の大きさ(m)。</summary>
    public const float CellSize = 3.0f;

    /// <summary>ねじれの強さ。1m 上がるごとに何ラジアン回すか。</summary>
    public const float TwistRate = 2.5f;

    /// <summary>ねじる真ん中の柱の、横の半分の大きさ(m)。<see cref="TwistLipschitz"/> の式に入る。</summary>
    public const float TwistHalfWidth = 0.6f;

    /// <summary>
    /// **当たりとみなす距離を、進んだ距離に比例させる係数**(要点2)。
    ///
    /// <para>
    /// 960x640・画角 60° なら、1画素の幅は距離 t のところで <c>t × 2tan(30°) / 640 ≒ 0.0018t</c>。
    /// その4分の1(0.0005t)まで寄れば、画素の中ではもう区別がつかない。
    /// <b>遠い画素ほど粗くてよい</b>ので、決まった値(1mm など)にするより歩数が少なくて済む。
    /// </para>
    /// </summary>
    public const float HitEpsilon = 0.0005f;

    /// <summary>法線を取るときにずらす距離(m)。小さすぎると float の丸めが勝って法線がざらつく。</summary>
    public const float NormalOffset = 0.001f;

    /// <summary>影の光線の歩数の上限。</summary>
    public const int ShadowSteps = 64;

    /// <summary>影の光線をどこまで伸ばすか(m)。</summary>
    public const float ShadowMaxDistance = 12.0f;

    /// <summary>柔らかい影の k。**大きいほど半影が狭い**(硬い影に近づく)。</summary>
    public const float Penumbra = 8.0f;

    /// <summary>AO で法線の方向に何点聞くか。</summary>
    public const int OcclusionSamples = 5;

    /// <summary>
    /// **ねじれの場面の傾きの上限**(要点5)。<c>√(1 + (τw)²)</c>。
    ///
    /// <para>
    /// 柱の面を向いて、軸から横に w 離れた点を y 方向へ 1m 動かすと、ねじった側の座標では
    /// 面に沿って <c>τw</c> 余分に動いたことになる。距離の変わり方は「縦の 1」と「横の τw」の合成になり、
    /// その大きさが <c>√(1 + (τw)²)</c>。面の端(|z| = w)でいちばん大きくなる。
    /// </para>
    /// <para>
    /// τ = 2.5、w = 0.6 なら 1.80。<b>歩幅をこの逆数(0.56)に縮める</b>と、表面を踏み越えなくなる。
    /// </para>
    /// </summary>
    public static readonly float TwistLipschitz =
        MathF.Sqrt(1.0f + ((TwistRate * TwistHalfWidth) * (TwistRate * TwistHalfWidth)));

    public SdfSceneKind Kind { get; set; }

    /// <summary>場面の時計(秒)。形の動きは全部この関数になっている。</summary>
    public float Time { get; set; }

    /// <summary>SDF の床を置くか。**ラスタと混ぜるときは置かない**(Day 31 の床がある)。</summary>
    public bool Ground { get; set; } = true;

    /// <summary>
    /// **距離関数を呼んだ回数**。レイマーチングの手間はほとんどこの数で決まる。
    /// GPU の中では数えられないので、C# の鏡で数える(内訳)。
    /// </summary>
    public long Evaluations { get; set; }

    /// <summary>点 p から、いちばん近い面までの距離。</summary>
    public float Map(Vector3 p) => Evaluate(p).Distance;

    /// <summary>
    /// **場面の距離関数**。距離と材質の番号を返す。シェーダの <c>map</c> と同じ。
    ///
    /// <para>
    /// <b>場面全体が1本の関数</b>になっているのが SDF の描き方の核心。
    /// 形を足すたびに <c>min</c>(和)が1つ増え、光線は1歩ごとにこれを丸ごと呼ぶ——
    /// だから<b>形の数がそのまま1歩の重さになる</b>(ラスタライズの「ドローコールが増える」に当たる)。
    /// </para>
    /// </summary>
    public (float Distance, int Material) Evaluate(Vector3 p)
    {
        Evaluations++;

        (float Distance, int Material) result = Kind switch
        {
            SdfSceneKind.Primitives => MapPrimitives(p),
            SdfSceneKind.Repetition => MapRepetition(p),
            _ => MapTwist(p),
        };

        if (Ground)
        {
            // 床はただの平面。「y がどれだけ上か」がそのまま距離になる。
            result = Union(result, (p.Y - GroundY, 0));
        }

        return result;
    }

    /// <summary>和(union)。**近いほうを取る**。材質も近いほうのものになる。</summary>
    private static (float Distance, int Material) Union(
        (float Distance, int Material) a, (float Distance, int Material) b) =>
        a.Distance < b.Distance ? a : b;

    /// <summary>基本形と演算(場面 0)。4つの形を x 方向に 3m おきに並べる。</summary>
    private (float Distance, int Material) MapPrimitives(Vector3 p)
    {
        // 1. **滑らかな和**。2つの球が近づいたり離れたりして、くっつくところで膨らむ。
        float swing = 0.25f + (0.45f * (0.5f + (0.5f * MathF.Sin(Time * 0.9f))));
        Vector3 blob = p - new Vector3(-4.5f, 0.15f, 0.0f);
        float left = Sdf.Sphere(blob - new Vector3(swing, 0.0f, 0.0f), 0.55f);
        float right = Sdf.Sphere(blob + new Vector3(swing, 0.0f, 0.0f), 0.55f);
        (float, int) result = (Sdf.SmoothMin(left, right, 0.6f), 1);

        // 2. **差**(箱 − 球)。max(a, −b) は「a の中で、かつ b の外」。回すときは点を逆に回す。
        Vector3 carved = Sdf.RotateY(p - new Vector3(-1.5f, 0.15f, 0.0f), -Time * 0.5f);
        float carvedBox = MathF.Max(
            Sdf.RoundBox(carved, new Vector3(0.55f), 0.05f),
            -Sdf.Sphere(carved, 0.72f));
        result = Union(result, (carvedBox, 2));

        // 3. **積**(箱 ∩ 球)。max(a, b) は「a の中で、かつ b の中」。
        Vector3 both = p - new Vector3(1.5f, 0.15f, 0.0f);
        float intersection = MathF.Max(
            Sdf.RoundBox(both, new Vector3(0.55f), 0.05f),
            Sdf.Sphere(both, 0.75f));
        result = Union(result, (intersection, 3));

        // 4. **立てたトーラス**。xz に寝ている輪を、成分を入れ替えて(xzy)立てる。
        Vector3 ring = Sdf.RotateY(p - new Vector3(4.5f, 0.15f, 0.0f), -Time * 0.7f);
        float torus = Sdf.Torus(new Vector3(ring.X, ring.Z, ring.Y), 0.5f, 0.18f);
        return Union(result, (torus, 4));
    }

    /// <summary>無限の繰り返し(場面 1)。**升目1つぶんの式しか書いていない**。</summary>
    private (float Distance, int Material) MapRepetition(Vector3 p)
    {
        Vector3 q = Sdf.RepeatXZ(p, CellSize);

        // 柱と、その上の玉を滑らかにつなぐ。どちらも y 軸まわりに回転対称なので、
        // 升目の境目で左右対称になり、畳んでも距離が狂わない(Sdf.RepeatXZ のコメント)。
        float pillar = Sdf.VerticalCapsule(q - new Vector3(0.0f, GroundY, 0.0f), 1.9f, 0.28f);
        float ball = Sdf.Sphere(q - new Vector3(0.0f, 1.75f, 0.0f), 0.42f);
        (float, int) result = (Sdf.SmoothMin(pillar, ball, 0.25f), 3);

        // 上下する輪。**全部の升目で同じ高さ**にしてある——升目ごとに位相を変えると
        // 隣の升目の輪のほうが近いことが起き、距離を長く見積もってしまう。
        float ringY = 0.45f + (0.5f * MathF.Sin(Time * 1.2f));
        float ring = Sdf.Torus(q - new Vector3(0.0f, ringY, 0.0f), 0.6f, 0.1f);
        return Union(result, (ring, 1));
    }

    /// <summary>ねじれ(場面 2)。真ん中に太い柱、左右に細い柱。</summary>
    private (float Distance, int Material) MapTwist(Vector3 p)
    {
        // 回してから(剛体の動き。距離は狂わない)、ねじる(距離が狂う)。
        Vector3 center = Sdf.TwistY(Sdf.RotateY(p - new Vector3(0.0f, 1.0f, 0.0f), -Time * 0.4f), TwistRate);
        (float, int) result = (
            Sdf.RoundBox(center, new Vector3(TwistHalfWidth, 1.5f, TwistHalfWidth), 0.04f), 2);

        Vector3 left = Sdf.TwistY(p - new Vector3(-2.8f, 0.5f, 0.0f), TwistRate);
        result = Union(result, (Sdf.RoundBox(left, new Vector3(0.4f, 1.0f, 0.4f), 0.03f), 1));

        Vector3 right = Sdf.TwistY(p - new Vector3(2.8f, 0.5f, 0.0f), -TwistRate);
        return Union(result, (Sdf.RoundBox(right, new Vector3(0.4f, 1.0f, 0.4f), 0.03f), 1));
    }

    // ================================================================
    //  光線を進める(シェーダの march / calcNormal / softShadow / occlusion と同じ)
    // ================================================================

    /// <summary>
    /// **球面追跡**(sphere tracing)。距離関数が返した距離だけ、光線を進める——を繰り返す(要点2)。
    ///
    /// <para>
    /// 距離 h が返ったということは、<b>半径 h の球の中には何も無い</b>ということ。
    /// だから光線を h 進めても、途中で面を踏み越えることはない。これがこの方法の安全の全部で、
    /// <b>「距離を長く見積もらない」が守られている間だけ成り立つ</b>(要点5)。
    /// </para>
    /// </summary>
    /// <param name="stepScale">歩幅に掛ける係数。距離の約束が破れている場面では 1 より小さくする。</param>
    /// <param name="log">渡すと1歩ごとの (進んだ距離, 返った距離) を書き足す(1画素を追う表示用)。</param>
    public MarchResult March(
        Vector3 origin,
        Vector3 direction,
        float stepScale,
        int maxSteps,
        float maxDistance,
        List<(float T, float H)>? log = null)
    {
        float t = 0.0f;

        for (int i = 0; i < maxSteps; i++)
        {
            float h = Map(origin + (direction * t));
            log?.Add((t, h));

            // **当たりの判定は「距離に比例した幅」**。遠くほど1画素が大きいので、粗く止めてよい。
            // 負になった(中に入った)ときもここで止まる——踏み越えた場合の壊れ方がこれ(要点5)。
            if (h < HitEpsilon * t)
            {
                return new MarchResult(true, t, i + 1);
            }

            t += h * stepScale;

            if (t > maxDistance)
            {
                return new MarchResult(false, t, i + 1);
            }
        }

        // 歩数が尽きた。**当たったとは言えない**ので外れにする(面すれすれを延々と歩いている画素)。
        return new MarchResult(false, t, maxSteps);
    }

    /// <summary>
    /// **法線 = 距離関数の傾き**。四面体の4つの頂点で聞く(要点6)。
    ///
    /// <para>
    /// 素直に中心差分で取ると 6 回(±x ±y ±z)呼ぶ。四面体の頂点
    /// (1,−1,−1) (−1,−1,1) (−1,1,−1) (1,1,1) に向けて聞くと 4 回で済む——
    /// 4つの向きを足すと 0 になり、それぞれの外積の和が単位行列の 4 倍になるので、
    /// <c>Σ kᵢ f(p + kᵢh) ≒ 4h ∇f</c> と傾きだけが残る。
    /// </para>
    /// </summary>
    public Vector3 Normal(Vector3 p)
    {
        var k0 = new Vector3(1.0f, -1.0f, -1.0f);
        var k1 = new Vector3(-1.0f, -1.0f, 1.0f);
        var k2 = new Vector3(-1.0f, 1.0f, -1.0f);
        var k3 = new Vector3(1.0f, 1.0f, 1.0f);

        Vector3 sum =
            (k0 * Map(p + (k0 * NormalOffset)))
            + (k1 * Map(p + (k1 * NormalOffset)))
            + (k2 * Map(p + (k2 * NormalOffset)))
            + (k3 * Map(p + (k3 * NormalOffset)));

        return Vector3.Normalize(sum);
    }

    /// <summary>
    /// **影の光線**。当たった点から光のほうへもう1本進める(要点6)。
    ///
    /// <para>
    /// 硬い影は「何かに当たったら 0」だけ。柔らかい影は、当たらなかった光線についても
    /// <b>途中でいちばん面に近づいたとき</b>の <c>k × h / t</c> を覚えておく。
    /// h は「どれだけ際どく外れたか」、t は「どれだけ遠くで外れたか」——
    /// 遠くの遮るものほど少しの外れで暗くなるので、<b>半影が遮るものから離れるほど広がる</b>。
    /// シャドウマップでは PCF を何点も取って作っていたものが、1本の光線から出てくる。
    /// </para>
    /// </summary>
    /// <param name="steps">この光線で距離関数を呼んだ回数(内訳用)。</param>
    public float Shadow(Vector3 origin, Vector3 toLight, bool hard, out int steps)
    {
        float result = 1.0f;
        float t = 0.02f;
        steps = 0;

        for (int i = 0; i < ShadowSteps; i++)
        {
            float h = Map(origin + (toLight * t));
            steps++;

            if (h < 0.001f)
            {
                return 0.0f;
            }

            if (!hard)
            {
                result = MathF.Min(result, Penumbra * h / t);
            }

            // 歩幅に下限と上限を付ける。下限が無いと面すれすれで止まって進まず、
            // 上限が無いと細いものを飛び越えて影が抜ける。
            t += Math.Clamp(h, 0.02f, 0.5f);

            if (t > ShadowMaxDistance)
            {
                break;
            }
        }

        return Math.Clamp(result, 0.0f, 1.0f);
    }

    /// <summary>
    /// **環境光の遮蔽(AO)**。法線の方向へ少しずつ離れた点で距離を聞く(要点6)。
    ///
    /// <para>
    /// 離れた高さ h のところで距離も h なら、周りに何も無い。距離が h より短ければ、
    /// そのぶん近くに何かがある。その差を足し合わせるだけ。
    /// Day 37 の SSAO が「画面に写っているもの」からしか遮蔽を拾えなかったのに対して、
    /// <b>こちらは画面の外にあるものも拾う</b>(距離関数は場面全体を知っているので)。
    /// </para>
    /// </summary>
    public float Occlusion(Vector3 p, Vector3 normal)
    {
        float occlusion = 0.0f;
        float weight = 1.0f;

        for (int i = 0; i < OcclusionSamples; i++)
        {
            float h = 0.01f + (0.12f * i / (OcclusionSamples - 1));
            float d = Map(p + (normal * h));
            occlusion += (h - d) * weight;
            weight *= 0.95f;
        }

        return Math.Clamp(1.0f - (3.0f * occlusion), 0.0f, 1.0f);
    }

    // ================================================================
    //  測る
    // ================================================================

    /// <summary>
    /// **距離関数の傾きの最大**(リプシッツ定数の見積もり)。箱の中からばらばらに点を取り、傾きを測る。
    ///
    /// <para>
    /// 本当の距離なら、どこで測っても傾きは <b>ちょうど 1</b>(1m 動けば距離は最大でも 1m しか変わらない)。
    /// 1 より小さいのは「短めに見積もっている」で安全、<b>1 より大きいのは「長めに見積もっている」で危ない</b>——
    /// 返った距離だけ進むと、面を踏み越えることがある(要点5)。
    /// </para>
    ///
    /// <para>
    /// <b>測り方は2段</b>。まず中心差分で傾きの向きを出し、次に<b>その向きへ実際に動かして</b>距離の変わり方を割る。
    /// 中心差分の長さをそのまま使うと、<c>min</c> / <c>max</c> の折れ目のそばで
    /// x は片方の形、y はもう片方の形……と<b>別々の枝の傾きを寄せ集めてしまい</b>、1 を超えて出る
    /// (本当の距離でも 1.01 程度になる。Day 58 の検証の途中で分かったこと)。
    /// 1本の向きに沿った変わり方なら、折れ目をまたいでも本当の傾きを超えない。
    /// </para>
    /// </summary>
    public float MaxGradient(Vector3 min, Vector3 max, int samples, int seed)
    {
        var random = new Random(seed);
        const float H = 0.001f;
        float worst = 0.0f;

        for (int i = 0; i < samples; i++)
        {
            var p = new Vector3(
                min.X + ((max.X - min.X) * random.NextSingle()),
                min.Y + ((max.Y - min.Y) * random.NextSingle()),
                min.Z + ((max.Z - min.Z) * random.NextSingle()));

            var gradient = new Vector3(
                Map(p + new Vector3(H, 0.0f, 0.0f)) - Map(p - new Vector3(H, 0.0f, 0.0f)),
                Map(p + new Vector3(0.0f, H, 0.0f)) - Map(p - new Vector3(0.0f, H, 0.0f)),
                Map(p + new Vector3(0.0f, 0.0f, H)) - Map(p - new Vector3(0.0f, 0.0f, H)));

            float length = gradient.Length();
            if (length <= 0.0f)
            {
                continue;
            }

            Vector3 along = gradient / length;
            float slope = MathF.Abs(Map(p + (along * H)) - Map(p - (along * H))) / (2.0f * H);
            worst = MathF.Max(worst, slope);
        }

        return worst;
    }

    // ================================================================
    //  画素と光線(シェーダの main の先頭と同じ)
    // ================================================================

    /// <summary>
    /// **画素の中心から光線を作る**。ビュー射影の逆行列で、画面の点を近クリップ面と遠クリップ面へ戻す(要点3)。
    ///
    /// <para>
    /// 始点は<b>カメラの位置ではなく近クリップ面の上の点</b>にしてある。ラスタライズは近クリップ面より手前を切り捨てるので、
    /// 同じ約束にしておくと、混ぜたときに手前の形の出方が揃う。平行投影(P キー)でもこの式がそのまま使える。
    /// </para>
    /// </summary>
    /// <param name="pixelCenter">画素の中心(左下が原点。<c>gl_FragCoord.xy</c> と同じ)。</param>
    public static (Vector3 Origin, Vector3 Direction) PixelRay(
        in Matrix4x4 inverseViewProjection, Vector2 pixelCenter, Vector2 resolution)
    {
        Vector2 ndc = (pixelCenter / resolution * 2.0f) - Vector2.One;

        Vector4 near = Vector4.Transform(new Vector4(ndc, -1.0f, 1.0f), inverseViewProjection);
        Vector4 far = Vector4.Transform(new Vector4(ndc, 1.0f, 1.0f), inverseViewProjection);

        var nearPoint = new Vector3(near.X, near.Y, near.Z) / near.W;
        var farPoint = new Vector3(far.X, far.Y, far.Z) / far.W;

        return (nearPoint, Vector3.Normalize(farPoint - nearPoint));
    }

    /// <summary>
    /// **世界の点を深度バッファの値(0〜1)にする**。ラスタライズが頂点に対してやっていることと同じ(要点7)。
    /// </summary>
    public static float WindowDepth(in Matrix4x4 viewProjection, Vector3 point)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1.0f), viewProjection);
        return (clip.Z / clip.W * 0.5f) + 0.5f;
    }
}
