using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **HDRI から太陽を取り出すところ**(Day 39)。今日の主役その2。
///
/// Day 36 の <see cref="SkyImage"/> には、手で空を焼く理由として
/// 「買ってきた HDRI だと<b>絵の中の太陽とシーンの平行光源が別物</b>になり、
/// 影の向きと映り込みが食い違う」と書いてあった。
/// 本物の HDRI を入れる今日、その問題に正面から当たる。
///
/// <para>
/// 答えは単純で、<b>絵のほうから太陽を測ればよい</b>。
/// 正距円筒の画像は「どの方向がどれだけ明るいか」の表そのものなので、
/// いちばん明るいかたまりを探せば、それが太陽の向きと強さになる。
/// </para>
///
/// <para>
/// <b>数式は1本だけ</b>。ある方向の集合 Ω から来る光が、
/// その方向を向いた面に与える放射照度は
/// <code>
///   E = ∫ L(ω) dω   ≒   Σ L(画素) x dω(画素)
/// </code>
/// で、正距円筒の画素1つが張る立体角は
/// <code>
///   dω = (2π/W) x (π/H) x sinθ
/// </code>
/// <b>sinθ が肝</b>。極(天頂・真下)に近い行ほど画素は横に引き伸ばされていて、
/// 実際に占める立体角は小さい。これを掛け忘れると、
/// 全部足したときに 4π(= 12.566)ではなく π^2(= 9.87)になるので、
/// **合計が 4π になるかどうかで検算できる**(<c>RunSceneCheck</c> がやっている)。
/// </para>
///
/// <para>
/// <b>取り出した太陽をどう使うか</b>。2つある。
/// </para>
///
/// <list type="number">
/// <item>
/// <b>平行光源にする</b>。向きと <see cref="Sun.Irradiance"/> をそのまま
/// <c>uLightDirection</c> / <c>uLightColor</c> に入れる。
/// 影の向きが絵の中の太陽と一致し、影の濃さも辻褄が合う。
/// </item>
/// <item>
/// <b>環境マップから抜く</b>(<see cref="RemoveSun"/>)。
/// 抜かないと<b>太陽を二重に数える</b>——IBL の中にも太陽が入っているので、
/// 平行光源と足すと 2 倍になる。
/// とくに<b>影の中が明るくなりすぎる</b>のが分かりやすい症状で、
/// 影とは「平行光源が届かない場所」なのに、IBL の中の太陽は届いてしまう。
/// </item>
/// </list>
/// </summary>
internal static class SkyAnalysis
{
    /// <summary>
    /// 輝度(相対輝度 Y)の重み。**Rec.709 / sRGB の原色に対応する係数**。
    /// FXAA(Day 38)が使ったのはガンマ後の近似だったが、
    /// こちらはリニアの値に掛けるので正規の係数を使う。
    /// </summary>
    private static readonly Vector3 LuminanceWeights = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>取り出した太陽1つぶん。</summary>
    /// <param name="Direction">
    /// **光が進む向き**(<c>Program._lightDirection</c> と同じ規約)。
    /// 空を見上げて太陽が見える方向はこの逆になる。
    /// </param>
    /// <param name="Irradiance">
    /// 太陽が担う放射照度 <c>Σ L dω</c>。
    /// **太陽のほうを向いた面が受け取る光の量**で、平行光源の色にそのまま入る。
    /// </param>
    /// <param name="SolidAngle">太陽と判定した画素が張る立体角の合計(ステラジアン)。</param>
    /// <param name="AngularRadius">
    /// 立体角を円板とみなしたときの見かけの半径(ラジアン)。
    /// **本物の太陽は 0.00465**(視直径 0.53 度)なので、
    /// これが 0.05 を大きく超えていたら「太陽を取り出せていない」と思ってよい。
    /// </param>
    /// <param name="PixelCount">太陽と判定した画素の数。</param>
    /// <param name="PeakLuminance">画像全体でいちばん明るい画素の輝度。</param>
    internal readonly record struct Sun(
        Vector3 Direction,
        Vector3 Irradiance,
        float SolidAngle,
        float AngularRadius,
        int PixelCount,
        float PeakLuminance);

    /// <summary>解析の結果ひとまとめ。</summary>
    /// <param name="Sun">取り出した太陽。</param>
    /// <param name="TotalIrradiance">全天から来る放射照度(太陽も含む)。</param>
    /// <param name="SkyAverage">**太陽を除いた**平均放射輝度。太陽を抜いた穴の埋め草に使う。</param>
    /// <param name="SunShare">全体の輝度のうち太陽が占める割合(0〜1)。</param>
    /// <param name="SolidAngleSum">立体角の合計。**4π(12.566)になるはず**という検算用。</param>
    /// <param name="Cutoff">
    /// 太陽と判定した輝度の下限(<c>ピーク x しきい値</c>)。
    /// <see cref="RemoveSun"/> が**同じ基準で抜く**ために持ち回る。
    /// </param>
    /// <param name="HalfFloatOverflow">
    /// 65504(半精度 float の上限)を超える成分を持つ画素の数。
    /// GPU へ上げるとき <c>RGB16F</c> で頭打ちになるぶん。
    /// </param>
    internal readonly record struct Result(
        Sun Sun,
        Vector3 TotalIrradiance,
        Vector3 SkyAverage,
        float SunShare,
        double SolidAngleSum,
        float Cutoff,
        int HalfFloatOverflow);

    /// <summary>
    /// 正距円筒の画素の中心が向いている方向。
    /// **<see cref="SkyImage.Create"/> と同じ式でなければならない**——
    /// 片方だけ直すと、手で焼いた空と読み込んだ HDRI で太陽が別の場所に出る。
    /// </summary>
    public static Vector3 DirectionAt(int x, int y, int width, int height)
    {
        // v = 0 が上端(天頂)。配布されている HDRI もこの向き。
        float theta = ((y + 0.5f) / height) * MathF.PI;

        // 0.5 を引くのは、読み込み側(equirect.frag)が
        // u = atan2(z, x) / 2π + 0.5 で UV を作っているぶんを戻すため。
        float phi = (((x + 0.5f) / width) - 0.5f) * MathF.Tau;

        float sinTheta = MathF.Sin(theta);

        return new Vector3(
            sinTheta * MathF.Cos(phi),
            MathF.Cos(theta),
            sinTheta * MathF.Sin(phi));
    }

    /// <summary>
    /// 正距円筒の HDR 画像から太陽を取り出す。
    ///
    /// <para>
    /// やり方は<b>しきい値ひとつ</b>。いちばん明るい画素の輝度を求め、
    /// その <paramref name="threshold"/> 倍を超える画素を「太陽」とする。
    /// 連結成分を追ったり、円板を当てはめたりはしない——
    /// **太陽は圧倒的に明るいので、それで足りる**ことが多いため。
    /// </para>
    ///
    /// <para>
    /// <b>足りない場合がある</b>のも今日の見どころ。
    /// 撮影時に太陽が飽和している HDRI(露出段数の足りないもの)だと、
    /// 太陽の芯も空も同じくらいの値になっていて、
    /// しきい値をどこに置いても<b>空の半分が「太陽」になる</b>。
    /// そうなったかどうかは <see cref="Sun.AngularRadius"/> を見れば分かる
    /// (本物の太陽は 0.005 ラジアン)。
    /// </para>
    /// </summary>
    /// <param name="pixels">RGB の float が横並び。長さ <c>width * height * 3</c>。</param>
    /// <param name="threshold">
    /// 最大輝度に対する比。既定の 0.05 は
    /// 「太陽の縁までは拾い、まわりのにじみ(グレア)は拾わない」あたり。
    /// </param>
    public static Result Analyze(float[] pixels, int width, int height, float threshold = 0.05f)
    {
        // --- 1. 最大輝度を探す ---
        float peak = 0.0f;
        int overflow = 0;

        for (int i = 0; i < width * height; i++)
        {
            var color = new Vector3(pixels[(i * 3) + 0], pixels[(i * 3) + 1], pixels[(i * 3) + 2]);
            peak = MathF.Max(peak, Vector3.Dot(color, LuminanceWeights));

            // **半精度 float の上限**。GPU 側は RGB16F なので、ここを超えると頭打ちになる。
            // 太陽の芯は 10 万を超えることがあるので、実際にぶつかる。
            if (color.X > 65504.0f || color.Y > 65504.0f || color.Z > 65504.0f)
            {
                overflow++;
            }
        }

        float cutoff = peak * threshold;

        // --- 2. 太陽と空を分けて積分する ---
        //
        // **足し込みは倍精度で持つ**。ここは 200 万回の加算になるので、
        // float のままだと**誤差が積もって 4π の検算が通らない**
        // (実測で 12.5637。正しくは 12.5664 で、相対 2e-4 のずれ)。
        // 1回ぶんの立体角(1e-5 程度)を 12 くらいまで育った合計に足すと、
        // 下位ビットが毎回こぼれる——**大きい数に小さい数を足し続ける**という、
        // 浮動小数点でいちばん素直に精度が落ちる形になっている。
        //
        // 絵には出ないので、数字で検算していなければ気づけない類の誤差。
        double sunRed = 0.0, sunGreen = 0.0, sunBlue = 0.0;
        double skyRed = 0.0, skyGreen = 0.0, skyBlue = 0.0;
        double directionX = 0.0, directionY = 0.0, directionZ = 0.0;

        double sunSolidAngle = 0.0;
        double skySolidAngle = 0.0;
        double sunLuminance = 0.0;
        double totalLuminance = 0.0;
        int sunPixels = 0;

        // 立体角の定数部分。**sinθ だけが行によって変わる**。
        double solidAngleBase = (Math.Tau / width) * (Math.PI / height);

        for (int y = 0; y < height; y++)
        {
            double theta = ((y + 0.5) / height) * Math.PI;
            double solidAngle = solidAngleBase * Math.Sin(theta);

            for (int x = 0; x < width; x++)
            {
                int index = ((y * width) + x) * 3;
                var color = new Vector3(pixels[index + 0], pixels[index + 1], pixels[index + 2]);

                float luminance = Vector3.Dot(color, LuminanceWeights);
                totalLuminance += luminance * solidAngle;

                if (luminance >= cutoff && peak > 0.0f)
                {
                    sunRed += color.X * solidAngle;
                    sunGreen += color.Y * solidAngle;
                    sunBlue += color.Z * solidAngle;
                    sunSolidAngle += solidAngle;
                    sunLuminance += luminance * solidAngle;
                    sunPixels++;

                    // **向きは輝度で重み付けして平均する**。単純平均だと、
                    // しきい値ぎりぎりで拾った縁の画素が芯と同じ重みになり、
                    // 太陽の中心がにじんだ側へ引っぱられる。
                    Vector3 direction = DirectionAt(x, y, width, height);
                    double weight = luminance * solidAngle;
                    directionX += direction.X * weight;
                    directionY += direction.Y * weight;
                    directionZ += direction.Z * weight;
                }
                else
                {
                    skyRed += color.X * solidAngle;
                    skyGreen += color.Y * solidAngle;
                    skyBlue += color.Z * solidAngle;
                    skySolidAngle += solidAngle;
                }
            }
        }

        var sunIrradiance = new Vector3((float)sunRed, (float)sunGreen, (float)sunBlue);
        var skyIrradiance = new Vector3((float)skyRed, (float)skyGreen, (float)skyBlue);
        var sunDirectionSum = new Vector3((float)directionX, (float)directionY, (float)directionZ);

        // --- 3. まとめる ---
        //
        // 太陽が1画素も見つからないことは(peak > 0 なら)無いが、
        // 真っ黒な画像を渡されたときに 0 除算しないようにしておく。
        Vector3 toSun = sunDirectionSum.LengthSquared() > 0.0f
            ? Vector3.Normalize(sunDirectionSum)
            : Vector3.UnitY;

        // 立体角 Ω の円板の見かけの半径。Ω = 2π(1 - cos r) を r について解いたもの。
        float angularRadius = (float)Math.Acos(Math.Clamp(1.0 - (sunSolidAngle / Math.Tau), -1.0, 1.0));

        var sun = new Sun(
            // **符号を反転する**。求めたのは「太陽が見える方向」で、
            // 平行光源が欲しいのは「光が進む向き」。ここを間違えると
            // 影が太陽の側に伸びるという、見れば分かる壊れ方をする。
            Direction: -toSun,
            Irradiance: sunIrradiance,
            SolidAngle: (float)sunSolidAngle,
            AngularRadius: angularRadius,
            PixelCount: sunPixels,
            PeakLuminance: peak);

        return new Result(
            Sun: sun,
            TotalIrradiance: sunIrradiance + skyIrradiance,
            SkyAverage: skySolidAngle > 0.0 ? skyIrradiance / (float)skySolidAngle : Vector3.Zero,
            SunShare: totalLuminance > 0.0 ? (float)(sunLuminance / totalLuminance) : 0.0f,
            SolidAngleSum: sunSolidAngle + skySolidAngle,
            Cutoff: cutoff,
            HalfFloatOverflow: overflow);
    }

    /// <summary>
    /// **環境マップから太陽を抜いた画像を作る**。元の配列は書き換えない。
    ///
    /// <para>
    /// 抜く理由は二重計上を避けるため(クラスの説明を参照)。
    /// 抜き方は「太陽と判定した画素を、そのすぐ外側の輪の平均色で埋める」。
    /// 空の平均色で埋めると、太陽のまわりが明るい絵では
    /// <b>そこだけ暗い染みになって空に穴が開く</b>ので、近所の色を使う。
    /// </para>
    ///
    /// <para>
    /// <b>プロの現場ではもっと丁寧にやる</b>。太陽を抜いた環境マップと、
    /// 抜いた太陽を面光源として別に持ち、
    /// 鏡面反射では面光源のほうを解析的に評価する(Karis 2013 の representative point)。
    /// ここではそこまでやらず、<b>拡散の二重計上を消す</b>ところまでで止めてある——
    /// 抜いた結果、粗い金属に太陽が映らなくなるのが今日の限界で、
    /// それが分かるように <c>Ctrl+Shift+F5</c> で切り替えられるようにしてある。
    /// </para>
    /// </summary>
    public static float[] RemoveSun(float[] pixels, int width, int height, in Result analysis)
    {
        var result = (float[])pixels.Clone();

        if (analysis.Sun.PixelCount == 0)
        {
            return result;
        }

        Vector3 toSun = -analysis.Sun.Direction;
        float sunRadius = analysis.Sun.AngularRadius;

        // 輪の内側と外側。**画素1つぶんの角度**を下限にしておかないと、
        // 太陽が数画素しかないときに輪の中が空になる。
        float pixelAngle = MathF.PI / height;
        float inner = MathF.Max(sunRadius * 2.0f, pixelAngle * 3.0f);
        float outer = MathF.Max(sunRadius * 6.0f, pixelAngle * 9.0f);

        // --- 1. 輪の平均色を求める ---
        Vector3 ringSum = Vector3.Zero;
        float ringWeight = 0.0f;

        float solidAngleBase = (MathF.Tau / width) * (MathF.PI / height);

        // **太陽の近くだけ見ればよい**ので、走査する行を絞る。
        // 全画素を2回舐めると 2k の HDRI で 200 万画素 x 2 になる。
        float sunTheta = MathF.Acos(Math.Clamp(toSun.Y, -1.0f, 1.0f));
        int rowFrom = Math.Max(0, (int)MathF.Floor(((sunTheta - outer) / MathF.PI) * height) - 1);
        int rowTo = Math.Min(height - 1, (int)MathF.Ceiling(((sunTheta + outer) / MathF.PI) * height) + 1);

        for (int y = rowFrom; y <= rowTo; y++)
        {
            float theta = ((y + 0.5f) / height) * MathF.PI;
            float solidAngle = solidAngleBase * MathF.Sin(theta);

            for (int x = 0; x < width; x++)
            {
                float angle = AngleTo(x, y, width, height, toSun);
                if (angle <= inner || angle > outer)
                {
                    continue;
                }

                int index = ((y * width) + x) * 3;
                ringSum += new Vector3(pixels[index + 0], pixels[index + 1], pixels[index + 2]) * solidAngle;
                ringWeight += solidAngle;
            }
        }

        Vector3 fill = ringWeight > 0.0f ? ringSum / ringWeight : analysis.SkyAverage;

        // --- 2. 太陽を埋める ---
        //
        // **判定は Analyze とまったく同じ**にする(<see cref="Result.Cutoff"/> を持ち回っている)。
        // 「明るさで選んだものを、明るさで抜く」ので、
        // <b>平行光源に入れたぶんと、環境マップから消したぶんが必ず一致する</b>。
        // 見かけの半径で切ると、選んだ画素と抜いた画素がずれて、
        // 二重計上が少し残ったり、逆に引きすぎたりする。
        //
        // ただし<b>太陽の近所に限る</b>。同じしきい値を全画面に当てると、
        // 遠くにある別の強い光(街灯、窓の反射)まで消えてしまう。
        for (int y = rowFrom; y <= rowTo; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (AngleTo(x, y, width, height, toSun) > outer)
                {
                    continue;
                }

                int index = ((y * width) + x) * 3;
                var color = new Vector3(pixels[index + 0], pixels[index + 1], pixels[index + 2]);

                if (Vector3.Dot(color, LuminanceWeights) < analysis.Cutoff)
                {
                    continue;
                }

                result[index + 0] = fill.X;
                result[index + 1] = fill.Y;
                result[index + 2] = fill.Z;
            }
        }

        return result;
    }

    /// <summary>画素の向きと、与えた方向との間の角度(ラジアン)。</summary>
    private static float AngleTo(int x, int y, int width, int height, Vector3 direction)
    {
        float cosAngle = Vector3.Dot(DirectionAt(x, y, width, height), direction);
        return MathF.Acos(Math.Clamp(cosAngle, -1.0f, 1.0f));
    }
}
