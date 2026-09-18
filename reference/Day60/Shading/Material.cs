using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 材質の種類。<b>光線の木がどう枝分かれするか</b>を決める。
/// RTIOW の lambertian / metal / dielectric に対応するが、今日は枝の出し方が違う(<see cref="WhittedTracer"/>)。
/// </summary>
internal enum MaterialKind
{
    /// <summary>拡散面。<b>光源へ影の光線</b>を出して、直接光だけで色を決める。反射の枝は出さない。</summary>
    Diffuse,

    /// <summary>金属。<b>反射の光線を1本</b>出す。映り込みに反射色(F0)が掛かる。</summary>
    Metal,

    /// <summary>ガラス。<b>反射と屈折の2本</b>に枝分かれする。割合はフレネルで決まる。</summary>
    Glass,
}

/// <summary>
/// 材質。形(<see cref="Shape"/>)に1つずつ持たせる。不変(作ったら変えない)なので、何本のスレッドから読んでもよい。
/// </summary>
internal sealed class Material
{
    private Material(MaterialKind kind)
    {
        Kind = kind;
    }

    public MaterialKind Kind { get; }

    /// <summary>
    /// 拡散面: 素の色(反射率。0〜1 の線形値)。
    /// 金属: 垂直に見たときの反射色 F0。
    /// ガラス: 使わない(透明)。
    /// </summary>
    public Vector3 Albedo { get; private init; }

    /// <summary>市松模様のもう一方の色。<see cref="CheckerSize"/> が 0 なら使わない。</summary>
    public Vector3 CheckerAlbedo { get; private init; }

    /// <summary>市松の1マスの大きさ(m)。0 なら無地。</summary>
    public float CheckerSize { get; private init; }

    /// <summary>拡散面のハイライト(点光源の照り返し)の強さ。0 ならつや消し。</summary>
    public float Specular { get; private init; }

    /// <summary>
    /// ハイライトの鋭さ(Blinn-Phong の指数)。大きいほど小さく鋭い点になる。
    ///
    /// <para>
    /// 金属とガラスにもハイライトを付けるのは<b>ごまかし</b>。点光源は大きさが 0 なので、
    /// 鏡に映る確率も 0 で、反射の光線がちょうど光源に当たることは無い。
    /// それでは鏡の玉に光源が一切映らず不自然なので、鋭いハイライトで「光源の映り込み」を代わりに描く。
    /// Day 60 で面光源にすると、反射の光線が本当に光源に当たるようになる。
    /// </para>
    /// </summary>
    public float Shininess { get; private init; }

    /// <summary>ガラスの屈折率。空気 1.00 / 水 1.33 / ガラス 1.5 / ダイヤモンド 2.42。</summary>
    public float Ior { get; private init; }

    /// <summary>
    /// <b>自分で光っている量</b>(放射輝度。W/sr/m²。Day 60 で追加)。0 なら光らない。
    ///
    /// <para>
    /// 点光源(<see cref="PointLight"/>)と違って<b>大きさがある</b>ので、
    /// 影の縁がぼける(半影)。そして<b>光線が偶然そこに当たることがある</b>——
    /// これが点光源との決定的な違いで、パストレーシングが点光源を扱えない理由でもある(要点4)。
    /// </para>
    /// <para>
    /// 「明るさ」ではなく「放射輝度」なのが大事。放射輝度は<b>距離で薄まらない量</b>で、
    /// 面から出る値がそのまま目に届く(遠くの壁も近くの壁も、同じ塗料なら同じ明るさに見える)。
    /// 遠くの光源が暗く見えるのは、<b>見かけの大きさ(立体角)が小さくなる</b>から。
    /// 逆2乗則は、レンダリング方程式の中では立体角の式から自然に出てくる(要点4)。
    /// </para>
    /// </summary>
    public Vector3 Emission { get; private init; }

    /// <summary>光る面か。<see cref="PathTracer"/> が「当たったら光を足すか」を決めるのに使う。</summary>
    public bool IsEmissive => Emission != Vector3.Zero;

    /// <summary>
    /// 鏡面(完全な反射・屈折)か。
    ///
    /// <para>
    /// パストレーシングでは、この区別が2か所で効く(要点4)。
    /// </para>
    /// <list type="bullet">
    /// <item><b>光源へ直接つなぐ(NEE)ことができない</b>。跳ね返る向きが1つに決まっているので、
    /// 「光源のほうへ跳ね返る」確率は 0。鏡に映る光源は、跳ね返った先で偶然光源に当たることでしか出せない</item>
    /// <item>だから鏡面で跳ね返った先で光源に当たったら、<b>その光は数えなければならない</b>
    /// (拡散面の後なら NEE で数え済みなので、数えると2重になる)</item>
    /// </list>
    /// </summary>
    public bool IsSpecular => Kind is MaterialKind.Metal or MaterialKind.Glass;

    public static Material Diffuse(Vector3 albedo, float specular = 0.0f, float shininess = 64.0f) => new(MaterialKind.Diffuse)
    {
        Albedo = albedo,
        Specular = specular,
        Shininess = shininess,
    };

    public static Material Checker(Vector3 albedo, Vector3 checkerAlbedo, float size) => new(MaterialKind.Diffuse)
    {
        Albedo = albedo,
        CheckerAlbedo = checkerAlbedo,
        CheckerSize = size,
    };

    public static Material Metal(Vector3 f0, float shininess = 4000.0f) => new(MaterialKind.Metal)
    {
        Albedo = f0,
        Shininess = shininess,
    };

    public static Material Glass(float ior, float shininess = 4000.0f) => new(MaterialKind.Glass)
    {
        Ior = ior,
        Shininess = shininess,
    };

    /// <summary>
    /// 面光源(Day 60 で追加)。<b>光るだけで、何も跳ね返さない</b>拡散面として作る。
    ///
    /// <para>
    /// albedo を 0 にしてあるので、当たった光線はそこで終わる(散乱の枝が albedo 倍 = 0 になる)。
    /// 本物の電球やランプは光りつつ反射もするが、そうすると<b>光源の表面に映り込んだ像</b>のぶんノイズが増え、
    /// 「面光源とは何か」を見るには邪魔になる。
    /// </para>
    /// </summary>
    public static Material Light(Vector3 radiance) => new(MaterialKind.Diffuse)
    {
        Albedo = Vector3.Zero,
        Emission = radiance,
    };

    /// <summary>
    /// 点 <paramref name="point"/> での素の色。市松なら、x と z をマスの大きさで割った<b>整数部の和の偶奇</b>で色を選ぶ。
    ///
    /// <para>
    /// y を混ぜないのは、床(y = 0)の上の点が浮動小数の誤差で y = −0.0000001 になり、
    /// <c>floor</c> が 0 と −1 の間で揺れて模様がちらつくのを避けるため。
    /// 今日の市松は床にしか使わないので、x と z だけで足りる。
    /// </para>
    /// </summary>
    public Vector3 AlbedoAt(Vector3 point)
    {
        if (CheckerSize <= 0.0f)
        {
            return Albedo;
        }

        // int ではなく long に落とす。地平線の近くの床は x や z が 1e10 m を超えることがあり、
        // int に収まらない値の変換は環境によって int.MinValue に潰れて、模様が一色に張り付く。
        long ix = (long)MathF.Floor(point.X / CheckerSize);
        long iz = (long)MathF.Floor(point.Z / CheckerSize);
        return ((ix + iz) & 1) == 0 ? Albedo : CheckerAlbedo;
    }
}
