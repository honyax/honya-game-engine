using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 5つの場面(1〜5 キー)。どれも「どこを見てほしいか」が1つずつある。
///
/// <para>
/// 色は<b>線形</b>の値で書く。画面に出すときに sRGB へ直す(<see cref="ProgressiveRenderer"/>)ので、
/// ここで 0.5 と書いた灰色は、画面では 0.73(186/255)くらいの明るさに見える。
/// </para>
/// <para>
/// 1〜3 は Day 59 のまま(<b>点光源</b>で照らす)。4・5 が Day 60 で足したもので、
/// どちらも<b>面光源</b>で照らす。
/// パストレーサで 1〜3 を見ると、点光源のハイライトが消え、影の中に周りの色が回り込む。
/// </para>
/// </summary>
internal static class SceneLibrary
{
    public static int Count => 5;

    public static Scene Create(int index) => index switch
    {
        1 => CreateMaterialRow(),
        2 => CreateMirrorCorridor(),
        3 => CreateCornellBox(),
        4 => CreateCaustic(),
        _ => CreateWhitted1980(),
    };

    /// <summary>
    /// 場面1「Whitted 1980」。論文の有名な1枚(赤と黄の市松の床、手前にガラス玉、奥に鏡の玉)を真似たもの。
    ///
    /// <para>
    /// 見どころ: <b>ガラス玉の中で床が逆さまに映る</b>(屈折)、<b>鏡の玉にガラス玉と空が映る</b>(反射)、
    /// <b>ガラス玉の影が真っ黒</b>(影の光線はガラスを通らない。Whitted 法の限界)。
    /// </para>
    /// </summary>
    private static Scene CreateWhitted1980()
    {
        var scene = new Scene("Whitted 1980", new OrbitView(new Vector3(-0.3f, 0.8f, 0.9f), 0.35f, 0.22f, 7.0f, 40.0f))
        {
            Ambient = new Vector3(0.06f, 0.07f, 0.09f),
            SkyHorizon = new Vector3(0.80f, 0.86f, 0.95f),
            SkyZenith = new Vector3(0.18f, 0.36f, 0.78f),
        };

        scene.Shapes.Add(new Plane(
            "床(市松)",
            Vector3.UnitY,
            0.0f,
            Material.Checker(new Vector3(0.75f, 0.08f, 0.05f), new Vector3(0.80f, 0.70f, 0.08f), 1.0f)));

        scene.Shapes.Add(new Sphere("ガラス玉", new Vector3(0.3f, 1.0f, 0.0f), 1.0f, Material.Glass(1.5f)));
        scene.Shapes.Add(new Sphere("鏡の玉", new Vector3(-1.6f, 0.8f, 2.4f), 0.8f, Material.Metal(new Vector3(0.90f))));
        scene.Shapes.Add(new Sphere(
            "青い玉",
            new Vector3(2.0f, 0.5f, 2.2f),
            0.5f,
            Material.Diffuse(new Vector3(0.10f, 0.25f, 0.75f), specular: 0.3f)));

        scene.Lights.Add(new PointLight("光1", new Vector3(-4.0f, 6.0f, -3.0f), Vector3.One, 180.0f));
        scene.Lights.Add(new PointLight("光2", new Vector3(4.0f, 4.0f, -2.0f), new Vector3(1.0f, 0.95f, 0.85f), 50.0f));
        return scene;
    }

    /// <summary>
    /// 場面2「屈折率と金属」。手前にガラス玉を屈折率の順に5つ、奥に拡散・プラスチック・金属を5つ並べる。
    ///
    /// <para>
    /// 見どころ: <b>屈折率 1.00 の玉は見えないのに影だけ落ちる</b>(フレネルは 0 で素通り、影の光線は遮る)。
    /// 屈折率が上がるほど玉の中の床が小さく逆さまになり、縁の反射が明るくなる。
    /// 金属の玉は<b>縁へ行くほど白っぽくなる</b>(F キーで「なし」にすると、縁まで同じ色のまま)。
    /// </para>
    /// </summary>
    private static Scene CreateMaterialRow()
    {
        var scene = new Scene("屈折率と金属", new OrbitView(new Vector3(0.0f, 0.4f, 0.7f), 0.0f, 0.30f, 5.6f, 45.0f))
        {
            Ambient = new Vector3(0.06f, 0.06f, 0.07f),
            SkyHorizon = new Vector3(0.85f, 0.87f, 0.90f),
            SkyZenith = new Vector3(0.30f, 0.42f, 0.70f),
        };

        scene.Shapes.Add(new Plane(
            "床(市松)",
            Vector3.UnitY,
            0.0f,
            Material.Checker(new Vector3(0.70f), new Vector3(0.08f), 0.5f)));

        // カメラは −Z 側から +Z を向いているので、画面の右が −X になる(右手系で Y が上なら、+Z を向くと +X は左手側)。
        // 画面の左から順に並ぶよう、x は大きいほうから置いていく。
        ReadOnlySpan<float> iors = [1.00f, 1.10f, 1.33f, 1.50f, 2.42f];
        for (int i = 0; i < iors.Length; i++)
        {
            scene.Shapes.Add(new Sphere(
                $"ガラス玉(屈折率 {iors[i]:F2})",
                new Vector3(2.2f - 1.1f * i, 0.45f, 0.0f),
                0.45f,
                Material.Glass(iors[i])));
        }

        // 金属の F0 は、よく引用される実測値(線形)。金と銅は青の反射が弱いので黄色・赤になる。
        scene.Shapes.Add(new Sphere("白い玉(つや消し)", new Vector3(2.2f, 0.45f, 1.4f), 0.45f, Material.Diffuse(new Vector3(0.75f))));
        scene.Shapes.Add(new Sphere("赤い玉(つや)", new Vector3(1.1f, 0.45f, 1.4f), 0.45f, Material.Diffuse(new Vector3(0.70f, 0.08f, 0.06f), specular: 0.5f, shininess: 200.0f)));
        scene.Shapes.Add(new Sphere("金の玉", new Vector3(0.0f, 0.45f, 1.4f), 0.45f, Material.Metal(new Vector3(1.00f, 0.71f, 0.29f))));
        scene.Shapes.Add(new Sphere("銅の玉", new Vector3(-1.1f, 0.45f, 1.4f), 0.45f, Material.Metal(new Vector3(0.95f, 0.64f, 0.54f))));
        scene.Shapes.Add(new Sphere("アルミの玉", new Vector3(-2.2f, 0.45f, 1.4f), 0.45f, Material.Metal(new Vector3(0.91f, 0.92f, 0.92f))));

        scene.Lights.Add(new PointLight("光1", new Vector3(-3.0f, 5.0f, -4.0f), Vector3.One, 150.0f));
        scene.Lights.Add(new PointLight("光2", new Vector3(4.0f, 3.0f, -1.0f), new Vector3(1.0f, 0.9f, 0.8f), 40.0f));
        return scene;
    }

    /// <summary>
    /// 場面3「合わせ鏡」。床と天井と、向かい合う2枚の鏡(どれも無限の平面)で廊下を作り、間に玉を置く。
    ///
    /// <para>
    /// 見どころ: <b>深さの上限([ / ])を上げるほど、鏡の奥へ映り込みが続く</b>。上限に着いた枝は黒になるので、
    /// 奥の何枚目から黒くなるかで上限が数えられる。鏡の反射色をわずかに緑にしてあるので、奥ほど暗く緑に沈む
    /// (本物の合わせ鏡でも、鏡のガラスが緑の光をわずかに多く返すので同じことが起きる)。
    /// 光線の数の表示(V)にすると、<b>鏡を見ている画素だけが深さの上限まで光線を使い切っている</b>のが分かる。
    /// </para>
    /// <para>
    /// 天井があるのは、無い場合に<b>上を向いた光線が2枚の鏡の間を往復し続けて、空に抜けられない</b>から。
    /// 鏡が上へ無限に続くので、どれだけ往復しても鏡の外に出られず、深さの上限で必ず黒になり、空が真っ黒に見える。
    /// </para>
    /// </summary>
    private static Scene CreateMirrorCorridor()
    {
        var scene = new Scene("合わせ鏡", new OrbitView(new Vector3(0.4f, 0.7f, 0.6f), -0.9f, 0.12f, 2.2f, 60.0f))
        {
            Ambient = new Vector3(0.05f, 0.05f, 0.06f),
            SkyHorizon = new Vector3(0.80f, 0.85f, 0.92f),
            SkyZenith = new Vector3(0.25f, 0.40f, 0.75f),
        };

        scene.Shapes.Add(new Plane(
            "床(市松)",
            Vector3.UnitY,
            0.0f,
            Material.Checker(new Vector3(0.72f), new Vector3(0.06f), 0.5f)));
        scene.Shapes.Add(new Plane("天井", Vector3.UnitY, 2.4f, Material.Diffuse(new Vector3(0.60f, 0.58f, 0.55f))));

        // x = -1.6 と x = +1.6 の2枚。平面の式は N·P = d なので、法線を内向きにすると d はどちらも -1.6 になる。
        Material mirror = Material.Metal(new Vector3(0.86f, 0.92f, 0.88f));
        scene.Shapes.Add(new Plane("鏡(x=-1.6)", Vector3.UnitX, -1.6f, mirror));
        scene.Shapes.Add(new Plane("鏡(x=+1.6)", -Vector3.UnitX, -1.6f, mirror));

        scene.Shapes.Add(new Sphere("赤い玉", new Vector3(0.0f, 0.5f, 0.6f), 0.5f, Material.Diffuse(new Vector3(0.75f, 0.10f, 0.08f), specular: 0.3f)));
        scene.Shapes.Add(new Sphere("ガラス玉", new Vector3(0.7f, 0.35f, -0.3f), 0.35f, Material.Glass(1.5f)));
        scene.Shapes.Add(new Sphere("金の玉", new Vector3(-0.7f, 0.3f, 1.6f), 0.3f, Material.Metal(new Vector3(1.00f, 0.71f, 0.29f))));

        scene.Lights.Add(new PointLight("光1", new Vector3(0.0f, 1.9f, 0.4f), Vector3.One, 15.0f));
        return scene;
    }

    /// <summary>
    /// 場面4「コーネルボックス」。<b>今日の起動時の場面</b>(要点1・4)。
    ///
    /// <para>
    /// コーネル大学が 1984 年に「計算した絵と、実際に作った箱を写真に撮ったもの」を比べるために作った場面。
    /// 以来、大域照明の効き目を見る標準の題材になっている。
    /// </para>
    /// <para>
    /// 見どころは3つ。どれも <b>Day 59 の Whitted 法では原理的に出せない</b>(A キーで見比べる)。
    /// </para>
    /// <list type="number">
    /// <item><b>色移り</b>(color bleeding)… 赤い壁の近くの白い玉の側面が赤みを帯びる。
    /// 壁で跳ね返った赤い光が玉に届いている</item>
    /// <item><b>柔らかい影</b>… 天井の光る球には大きさがあるので、影の縁に半影ができる</item>
    /// <item><b>鏡に映る光源</b>… 鏡の玉に、光る球そのものが映る(点光源では映らない。要点4)</item>
    /// </list>
    /// <para>
    /// 箱は<b>無限の平面6枚</b>で作ってある(場面3の合わせ鏡と同じ手)。カメラは箱の中にいるので、
    /// 光線は外へ出られない——だから空の色は 0 でよく、<b>光源は天井の球だけ</b>になる。
    /// 上へ回しすぎる(左ドラッグ)とカメラが天井を突き抜け、箱の外から天井の裏を見ることになる。
    /// </para>
    /// <para>
    /// 環境光を 0.05 だけ入れてあるのは Whitted 法のため。0 にすると Whitted では真っ黒な絵になり、
    /// 「壊れている」のか「そういうものなのか」が分からなくなる。パストレーサはこの値を読まない。
    /// </para>
    /// </summary>
    private static Scene CreateCornellBox()
    {
        var scene = new Scene("コーネルボックス", new OrbitView(new Vector3(0.0f, 1.2f, 0.35f), 0.0f, 0.05f, 3.6f, 45.0f))
        {
            Ambient = new Vector3(0.05f),

            // 閉じた箱なので、光線が空に抜けることは無い。0 にしておくと、
            // 万一どこかに隙間があったとき(壁を1枚書き忘れたとき)に真っ黒な帯としてすぐ分かる。
            SkyHorizon = Vector3.Zero,
            SkyZenith = Vector3.Zero,
        };

        // コーネルボックスの実測値に近い反射率(線形)。
        Material white = Material.Diffuse(new Vector3(0.73f));
        Material red = Material.Diffuse(new Vector3(0.63f, 0.065f, 0.05f));
        Material green = Material.Diffuse(new Vector3(0.14f, 0.45f, 0.091f));

        // カメラは −Z 側から +Z を向いているので、+X が画面の左。本家と同じ「左が赤、右が緑」にする。
        scene.Shapes.Add(new Plane("床", Vector3.UnitY, 0.0f, white));
        scene.Shapes.Add(new Plane("天井", Vector3.UnitY, 2.6f, white));
        scene.Shapes.Add(new Plane("左の壁(赤)", Vector3.UnitX, 1.3f, red));
        scene.Shapes.Add(new Plane("右の壁(緑)", Vector3.UnitX, -1.3f, green));
        scene.Shapes.Add(new Plane("奥の壁", Vector3.UnitZ, 2.6f, white));
        scene.Shapes.Add(new Plane("手前の壁", Vector3.UnitZ, -4.2f, white));

        // 光る球。天井に触れる高さに置く(埋め込むと、球の上半分へ向けた影の光線が天井に遮られて無駄になる)。
        scene.Shapes.Add(new Sphere(
            "光る球",
            new Vector3(0.0f, 2.2f, 0.3f),
            0.35f,
            Material.Light(new Vector3(22.0f, 21.0f, 19.0f))));

        scene.Shapes.Add(new Sphere("鏡の玉", new Vector3(-0.62f, 0.45f, 0.5f), 0.45f, Material.Metal(new Vector3(0.92f))));
        scene.Shapes.Add(new Sphere("白い玉", new Vector3(0.70f, 0.5f, 0.95f), 0.5f, Material.Diffuse(new Vector3(0.73f))));
        return scene;
    }

    /// <summary>
    /// 場面5「ガラスの集光」。Day 59 の改造課題1で予告した絵(要点4・7)。
    ///
    /// <para>
    /// Day 59 では、ガラス玉の影は<b>真っ黒</b>だった。影の光線はまっすぐ光源へ向かうので、
    /// 「ガラスで曲がって届く光」を聞く手立てが無かったから。
    /// パストレーシングでは、床から散乱した光線がたまたまガラス玉を通って光源に当たることがあり、
    /// そこで<b>集光</b>(caustics)が拾える——虫眼鏡で紙を焦がすあの明るい点。
    /// </para>
    /// <para>
    /// ただし<b>ノイズがとても多い</b>。その明るい点に効く経路は「拡散 → ガラス → ガラス → 光源」で、
    /// 拡散面から引いた向きがちょうどガラス玉を通って光源に届く確率は小さい。
    /// NEE はここでは役に立たない(光源への直線がガラスに遮られる)。
    /// <b>集光を速く収束させるのは、いまも研究が続いている題材</b>(双方向パストレーシング、
    /// フォトンマッピング、SDS 経路の扱い)。今日はその難しさを目で見るのが目的。
    /// </para>
    /// <para>
    /// 光源とガラス玉の距離・大きさは、<b>焦点がちょうど床に来る</b>ように選んである
    /// (球レンズの焦点距離は <c>f = n r / (2(n−1))</c>。屈折率 1.5、半径 0.55 で 0.825m)。
    /// </para>
    /// </summary>
    private static Scene CreateCaustic()
    {
        var scene = new Scene("ガラスの集光", new OrbitView(new Vector3(0.0f, 0.45f, 0.55f), 0.10f, 0.34f, 5.0f, 40.0f))
        {
            Ambient = new Vector3(0.03f),
            SkyHorizon = new Vector3(0.05f, 0.06f, 0.09f),
            SkyZenith = new Vector3(0.02f, 0.03f, 0.07f),
        };

        scene.Shapes.Add(new Plane("床", Vector3.UnitY, 0.0f, Material.Diffuse(new Vector3(0.70f))));
        scene.Shapes.Add(new Sphere("ガラス玉", new Vector3(0.0f, 1.0f, 0.0f), 0.55f, Material.Glass(1.5f)));
        scene.Shapes.Add(new Sphere("白い玉", new Vector3(1.7f, 0.5f, 0.3f), 0.5f, Material.Diffuse(new Vector3(0.72f))));
        scene.Shapes.Add(new Sphere(
            "光る球",
            new Vector3(0.0f, 4.2f, -1.2f),
            0.5f,
            Material.Light(new Vector3(45.0f, 44.0f, 40.0f))));
        return scene;
    }
}
