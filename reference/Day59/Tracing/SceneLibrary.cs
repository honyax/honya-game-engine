using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 今日の3つの場面(1 / 2 / 3 キー)。どれも「どこを見てほしいか」が1つずつある。
///
/// <para>
/// 色は<b>線形</b>の値で書く。画面に出すときに sRGB へ直す(<see cref="ProgressiveRenderer"/>)ので、
/// ここで 0.5 と書いた灰色は、画面では 0.73(186/255)くらいの明るさに見える。
/// </para>
/// </summary>
internal static class SceneLibrary
{
    public static int Count => 3;

    public static Scene Create(int index) => index switch
    {
        1 => CreateMaterialRow(),
        2 => CreateMirrorCorridor(),
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
}
