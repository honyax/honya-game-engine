using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **HDR の空を手で焼くところ**(Day 36)。<see cref="SurfaceMaps"/>(Day 34)の環境版。
///
/// <para>
/// <b>Day 39 で本物の HDRI が入った</b>(<see cref="HdrImage"/>)。
/// それでもこのクラスは残す——下に書いた理由の 2 番目と 3 番目が
/// <b>今日いっそう効く</b>ため。
/// <c>Ctrl+Shift+F2</c> で手焼きと HDRI を切り替えられるようにしてあるので、
/// 「太陽の向きが最初から合っている空」と「絵から測った空」を見比べられる。
/// 放射照度マップの検算(<c>RunIblCheck</c>)も、答えが手元にある手焼きでしかできない。
/// </para>
///
/// IBL には環境の絵が要る。普通は Poly Haven のような配布サイトから
/// HDRI(<c>.hdr</c> / <c>.exr</c>)を持ってくるが、今日は自分で作る。理由は3つ。
///
/// <list type="number">
/// <item>
/// <b>20〜50MB のバイナリを1枚置く前に、仕組みを動かしたい</b>。
/// 本物の HDRI を入れるのは Day 39(デモの組み上げ)で、
/// そのとき <c>.hdr</c> の読み込みも書く。今日はパイプラインだけを通す。
/// </item>
/// <item>
/// <b>太陽の位置を平行光源と揃えられる</b>。これが今日いちばん効く。
/// 買ってきた HDRI だと「絵の中の太陽」と「シーンの平行光源」が別物になり、
/// 影の向きと映り込みが食い違う。同じ向きから作れば最初から辻褄が合う。
/// </item>
/// <item>
/// <b>答えを CPU で計算できる</b>。放射照度マップが正しいかは、
/// 焼いた結果を見ても分からない(ぼんやりした色にしか見えない)。
/// 元の式が手元にあれば、**CPU で積分した値と GPU が焼いた値を突き合わせられる**。
/// </item>
/// </list>
///
/// <para>
/// <b>形式は正距円筒(equirectangular)</b>。横が方位角 0〜2π、縦が仰角 -π/2〜+π/2 の
/// 横長 2:1 の絵で、HDRI がこの形で配られるため。
/// キューブへ焼き直す道筋(<see cref="EnvironmentMap"/>)を今日通しておけば、
/// Day 39 で本物の HDRI を差し込むときに**この層を差し替えるだけ**で済む。
/// </para>
///
/// <para>
/// <b>値は 1.0 を超える</b>。太陽の芯は 300 前後で、空は 1〜3、地面は 0.1 以下。
/// この開きこそが HDR で、8bit に落とすと太陽と白い雲の区別が付かなくなる——
/// そして<b>区別が付かないと IBL が効かない</b>。
/// 環境光の大半は太陽が担っているので、そこが 1.0 で切られると
/// 「全体的にぼんやり明るいだけ」の環境になる。
/// </para>
/// </summary>
internal static class SkyImage
{
    /// <summary>横幅。縦はこの半分(正距円筒は 2:1)。</summary>
    public const int Width = 1024;

    public const int Height = Width / 2;

    /// <summary>太陽の見かけの半径(ラジアン)。**本物の太陽は 0.0047**(視直径 0.53 度)。</summary>
    private const float SunAngularRadius = 0.045f;

    /// <summary>太陽の芯の明るさ。</summary>
    private const float SunIntensity = 300.0f;

    /// <summary>
    /// 正距円筒の HDR 画像を作る。**RGB の float が横並び**(1画素 3 要素)。
    /// </summary>
    /// <param name="sunDirection">
    /// 太陽の**進む向き**(<c>Program._lightDirection</c> と同じ規約)。
    /// 空の中で太陽が見える方向はこの逆になる。
    /// </param>
    /// <remarks>
    /// **Day 39 で <c>clampToLdr</c> の引数が無くなった**。
    /// 1.0 で頭打ちにする処理(Ctrl+Alt+6)は
    /// <see cref="EnvironmentMap.BakeFromPixels"/> へ移してある——
    /// 今日から空の出どころが2つ(手焼きと HDRI)になったので、
    /// **どちらから来た画像でも同じ場所で切る**ほうが辻褄が合う。
    /// </remarks>
    public static float[] Create(Vector3 sunDirection)
    {
        var pixels = new float[Width * Height * 3];

        // 「光が来る方向」= 進む向きの逆。空を見上げて太陽が見える方向。
        Vector3 toSun = Vector3.Normalize(-sunDirection);

        for (int y = 0; y < Height; y++)
        {
            // **上下の向き**。v = 0 が画像の上端で、そこが天頂になるのが正距円筒の約束。
            // 配布されている HDRI もこの向きなので、そろえておく。
            float v = (y + 0.5f) / Height;
            float theta = v * MathF.PI;              // 0 = 天頂、π = 真下

            for (int x = 0; x < Width; x++)
            {
                float u = (x + 0.5f) / Width;

                // **0.5 を引く**のを忘れないこと。読み込み側(equirect.frag)は
                //   u = atan2(z, x) / 2π + 0.5
                // で UV を作る。atan2 が返すのは -π〜π なので、0〜1 に写すために 0.5 を足している。
                // こちらでその 0.5 を戻さないと、**空が方位角で 180 度回る**——
                // 太陽が真裏に行くので、影の向きと映り込みが食い違う。
                //
                // 上下が逆なら一目で分かるが、**方位が回っているのは絵から気づけない**。
                // 「空はそれらしいのに、なぜか影と反射が合わない」という形で出る。
                float phi = (u - 0.5f) * MathF.Tau;

                // 正距円筒 → 方向ベクトル。**この式が読み込み側と裏返しの関係**にあり、
                // equirect.frag の DirectionToUv がちょうど逆をやっている。
                var direction = new Vector3(
                    MathF.Sin(theta) * MathF.Cos(phi),
                    MathF.Cos(theta),
                    MathF.Sin(theta) * MathF.Sin(phi));

                Vector3 color = Sample(direction, toSun);

                int index = ((y * Width) + x) * 3;
                pixels[index + 0] = color.X;
                pixels[index + 1] = color.Y;
                pixels[index + 2] = color.Z;
            }
        }

        return pixels;
    }

    /// <summary>
    /// **ある方向を見たときの明るさ**。これが空の定義そのもの。
    ///
    /// 大気散乱をまじめに解く(Preetham / Hosek-Wilkie)と数百行になるので、
    /// ここは「それらしく見えて、IBL の入力として素性が良い」ことだけを狙った作り。
    /// 素性が良い、というのは
    ///   - <b>どの方向にも 0 でない値がある</b>(真っ黒だと放射照度が偏りすぎる)
    ///   - <b>上下で色が違う</b>(放射照度マップが方向で変わるのが目で分かる)
    ///   - <b>太陽が桁違いに明るい</b>(HDR であることが効く)
    /// の3つ。
    ///
    /// <para>
    /// <b>CPU 側にこの関数がある</b>ことが、あとで効いてくる。
    /// 放射照度マップの検算(<c>RunIblCheck</c>)は、
    /// この関数を半球ぶん積分した値と、GPU が焼いた値を比べている。
    /// </para>
    /// </summary>
    public static Vector3 Sample(Vector3 direction, Vector3 toSun)
    {
        direction = Vector3.Normalize(direction);

        // --- 太陽 ---
        //
        // 視線と太陽の角度が見かけの半径より内側なら芯。
        // 縁を少しだけなだらかにしてあるのは、**焼くときのちらつき対策**。
        // 完全な円板にすると、キューブへ焼くときのサンプル点が
        // 当たるか外れるかで面ごとに明るさが跳ねる。
        float cosAngle = Vector3.Dot(direction, toSun);
        float angle = MathF.Acos(Math.Clamp(cosAngle, -1.0f, 1.0f));

        float sun = 0.0f;
        if (angle < SunAngularRadius)
        {
            sun = SunIntensity * Smoothstep(SunAngularRadius, SunAngularRadius * 0.7f, angle);
        }

        // 太陽のまわりのにじみ(グレア)。**空の明るさの中で無視できない量**を持つ。
        float glare = 6.0f * MathF.Pow(MathF.Max(cosAngle, 0.0f), 220.0f);

        // --- 空と地面 ---
        //
        // 上を向くほど濃い青、地平線に近いほど白っぽく。
        // 実際の空も地平線が明るいのは、そちらのほうが大気を長く通るため。
        float up = direction.Y;

        var zenith = new Vector3(0.16f, 0.32f, 0.75f);
        var horizon = new Vector3(0.75f, 0.80f, 0.92f);
        var ground = new Vector3(0.08f, 0.07f, 0.06f);

        Vector3 color;
        if (up >= 0.0f)
        {
            // 天頂へ向かうほど青くなる。**冪で寄せる**と地平線側が広くなり、空らしくなる。
            float t = MathF.Pow(up, 0.45f);
            color = Vector3.Lerp(horizon, zenith, t) * 2.4f;
        }
        else
        {
            // 地面。**地平線をまたいで値が飛ばないようにする**のが大事なところ。
            //
            // 最初は「地面は空の 0.35 倍」と書いていたが、それだと y = 0 の前後で
            // 明るさが 7 倍跳ぶ。絵としては地平線に硬い線が入るだけだが、
            // **キューブへ焼いたときに面の継ぎ目とぶつかって、検算が通らなくなった**
            // (計画書の「検証の途中で分かったこと」)。
            //
            // 現実の地面も、地平線のすぐ近くは空とほとんど同じ明るさに見える。
            // なので空の値から始めて、下を向くほど急に暗くする。
            float t = MathF.Pow(-up, 0.45f);
            color = Vector3.Lerp(horizon * 2.4f, ground, t);
        }

        return color + new Vector3(sun + glare);
    }

    /// <summary>
    /// **CPU で放射照度を積分する**。自己チェック専用。
    ///
    /// ある法線 <c>n</c> に対して、半球から入ってくる光を <c>cosθ</c> で重み付けして足す:
    /// <code>
    ///   E(n) = ∫ L(l) (n・l) dω
    /// </code>
    /// GPU の <c>irradiance.frag</c> がやっているのとまったく同じ計算で、
    /// **答えを2通りの方法で出して突き合わせる**ためにここに置いてある。
    ///
    /// <para>
    /// 刻みは球面の格子。太陽が小さいので粗いと踏み外すが、
    /// <see cref="SunAngularRadius"/> が 0.045 ラジアンあるので、
    /// 0.005 ラジアン刻み(<paramref name="steps"/> = 320)なら十分に捉えられる。
    /// </para>
    /// </summary>
    public static Vector3 IntegrateIrradiance(Vector3 n, Vector3 toSun, int steps = 320)
    {
        n = Vector3.Normalize(n);

        Vector3 up = MathF.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, n));
        Vector3 bitangent = Vector3.Cross(n, tangent);

        Vector3 total = Vector3.Zero;
        double dTheta = (Math.PI / 2.0) / steps;
        double dPhi = (2.0 * Math.PI) / steps;

        for (int i = 0; i < steps; i++)
        {
            double theta = (i + 0.5) * dTheta;
            double sinTheta = Math.Sin(theta);
            double cosTheta = Math.Cos(theta);

            for (int j = 0; j < steps; j++)
            {
                double phi = (j + 0.5) * dPhi;

                var l = Vector3.Normalize(
                    (tangent * (float)(sinTheta * Math.Cos(phi)))
                    + (bitangent * (float)(sinTheta * Math.Sin(phi)))
                    + (n * (float)cosTheta));

                // cosθ(= n・l)と立体角の重み sinθ dθ dφ。
                total += Sample(l, toSun) * (float)(cosTheta * sinTheta * dTheta * dPhi);
            }
        }

        return total;
    }

    /// <summary>GLSL の smoothstep と同じ。<paramref name="edge0"/> で 0、<paramref name="edge1"/> で 1。</summary>
    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
        return t * t * (3.0f - (2.0f * t));
    }
}
