using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **トゥーンの表面下散乱(SSS)**(Day 74)。MToon で描いたキャラの肌に、「影の境目のにじみ」と「逆光の透け」を足す。
///
/// <para>
/// 手法はサムザップの記事「表面下散乱(SSS)の実装案比較とトゥーン調への適用方法」の採用案
/// (Pre-Integrated 風の境目の帯 + 厚みで近似した透過)。<b>場所をまたいで光を広げない</b>——
/// 1つの画素が自分の N・L だけを見て色を足すので、追加のパスもバッファも要らない(計画書の要点1)。
/// </para>
///
/// <para>
/// <b>なぜ <see cref="MToon"/> に足さず、別のクラスにしたか</b>。MToon のクラスは「ファイルに書かれた値だけを持つ」と決めた(Day 68)。
/// SSS は VRM の仕様の外にある<b>こちらの判断で足す見た目</b>なので、同じクラスに混ぜると
/// 「どこまでが作者の指定で、どこからが自分の味付けか」が読めなくなる。既定を OFF にしてあるのも同じ理由
/// (OFF なら three-vrm と同じ絵のまま)。
/// </para>
/// </summary>
internal sealed class ToonSss
{
    /// <summary>散乱色のランプのテクスチャユニット(18 番)。MToon が 16・17 まで使っている(<see cref="MToon"/> の割り当ての表)。</summary>
    public const int RampUnit = 18;

    /// <summary>ランプの幅。横軸が「影の境目からの距離」(-1〜1)。</summary>
    public const int RampWidth = 256;

    /// <summary>
    /// ランプの高さ。<b>下の2行が夜、上の2行が昼</b>。v = 0.25 と 0.75 で引くので、
    /// 1行ずつだと双線形で隣の段が混ざる(2行あれば、引く位置がちょうど同じ段の2行の境目に来る)。
    /// </summary>
    public const int RampHeight = 4;

    /// <summary>
    /// **受光側の重み**。影の側を 1 として、明るい側の帯をこの割合に抑える(記事の <c>LitSideScatterWeight</c>)。
    /// 明るい側は直接光がもともと明るく、足した色が埋もれて「白っぽく濁る」だけになるため。
    /// <c>mtoon.frag</c> の <c>SSS_LIT_SIDE_WEIGHT</c> と同じ値(自己チェックが突き合わせる)。
    /// </summary>
    public const float LitSideWeight = 0.45f;

    // ===== 切り替え(「トゥーンの表面下散乱」のページ)=====

    /// <summary>足すか。<b>既定は OFF</b>(VRM の仕様どおりの絵。three-vrm と比べられる)。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// **落ち影の境目にも帯を乗せるか**。記事は乗せない(N・L から作った境目だけ)。
    /// 乗せると、PCF でぼけた影の縁が「境目」として扱われる——<b>帯の幅は PCF のぼけの幅を超えない</b>(計画書の設計書)。
    /// </summary>
    public bool CastShadow { get; set; }

    /// <summary>点光源にも足すか。記事はメインライトだけ。夜の裏通りは灯りのほうが効くので、こちらは既定で足す。</summary>
    public bool PointLights { get; set; } = true;

    /// <summary>
    /// **マスクの作り方**。0 = 肌のマテリアル(1枚ごとに 1 か 0)/ 1 = 肌のマテリアル x 肌らしさ(既定)/ 2 = 全部のマテリアル。
    ///
    /// <para>
    /// 0 だけでは足りない——VRoid の体のテクスチャには<b>タイツとインナーも描かれている</b>ので、逆光で脚とインナーが赤茶に透ける。
    /// 1 は画素ごとにベースカラーを見て、明るく暖かい色(肌)だけを残す(<see cref="SkinLikeness"/>)。
    /// 2 は髪や服まで赤くにじむのを見て、マスクが要る理由を確かめるためのもの。
    /// </para>
    /// </summary>
    public int Mask { get; set; } = 1;

    /// <summary>
    /// 0 = 通常 / 1 = 散乱だけ / 2 = 境目の帯だけ / 3 = 逆光の透けだけ / 4 = 影の境目からの距離 d。
    /// <c>mtoon.frag</c> の <c>uSssView</c>。
    /// </summary>
    public int View { get; set; }

    // ===== つまみ(記事のプロパティと同じ意味。値はこのアバターと照明に合わせて決めたもの)=====

    /// <summary>表層の色(リニア)。記事の <c>_SSS_Color</c>。境目のすぐそばの、浅く散った光の色。</summary>
    public Vector3 Color { get; set; } = new(1.0f, 0.45f, 0.35f);

    /// <summary>深層の色(リニア)。記事の <c>_SSS_DeepColor</c>。奥まで潜って血の色を拾った光。逆光の透けもこの色。</summary>
    public Vector3 DeepColor { get; set; } = new(0.75f, 0.12f, 0.08f);

    /// <summary>表層の帯の半幅(d の単位)。記事の <c>_SSS_Wrap</c>(0.02〜1)。</summary>
    public float Wrap { get; set; } = 0.25f;

    /// <summary>深層の帯の半幅。記事の <c>_SSS_DeepWrap</c>(0.02〜1.5)。<b>表層より狭くはしない</b>(シェーダが max を取る)。</summary>
    public float DeepWrap { get; set; } = 0.6f;

    /// <summary>深層の帯の強さ。記事の <c>_SSS_DeepIntensity</c>。</summary>
    public float DeepIntensity { get; set; } = 0.6f;

    /// <summary>逆光の透けの強さ。記事の <c>_SSS_BacklightIntensity</c>。</summary>
    public float BacklightIntensity { get; set; } = 0.8f;

    /// <summary>全体の強さ。記事の <c>_SSS_Intensity</c>。<b>足し算なので控えめに</b>(1 にすると肌が光って見える)。</summary>
    public float Intensity { get; set; } = 0.5f;

    /// <summary>散乱色のランプ。<see cref="CreateRamp"/> の絵を、ミップ無し・リニア・端で止める設定で持つ。</summary>
    public Handle<Texture> Ramp { get; private set; }

    /// <summary>ランプを作る。起動時に1回。</summary>
    public void Load(RenderResources resources)
    {
        // **sRGB にしない**(記事も sRGB 無効)。ランプは「掛ける倍率」で、色の絵ではない。
        // sRGB で読むと中央の色味が 2.2 乗で濃くなり、つまみで決めた色からずれる。
        // **ミップも付けない**。256 幅を縮めると、端の白と中央の色が混ざって境目の色味が薄まる。
        Ramp = resources.LoadTextureFromPixels(
            "toon-sss-ramp", CreateRamp(), RampWidth, RampHeight, generateMipmaps: false, srgb: false);

        // **端で止める**。Repeat のままだと d = 1(受光の中心)の画素が左端(影の奥)と混ざる。
        resources.GetTexture(Ramp).SetWrap(TextureWrap.ClampToEdge);
    }

    /// <summary>
    /// **肌かどうか**(透過マスクの代わり)。VRoid はマテリアル名の末尾に部位を付ける(<c>_SKIN</c> / <c>_HAIR</c> / <c>_CLOTH</c> …)。
    ///
    /// <para>
    /// 記事は「ベースカラーと同じ UV の R に透けの強さを描いたテクスチャ」を使う。このアバターには無いので、
    /// <b>マテリアル1枚ごとに 1 か 0</b>(顔と体だけが 1)と、画素ごとの <see cref="SkinLikeness"/> を掛けて代用する。
    /// 耳だけ強く、のような塗り分けは改造課題。
    /// </para>
    /// </summary>
    public static bool IsSkin(Material material) => material.Name.Contains("_SKIN", StringComparison.Ordinal);

    /// <summary>パーツ1つぶんのマスクの値。<c>mtoon.frag</c> の <c>uSssMask</c>。</summary>
    public float MaskOf(Material material) => Mask == 2 || IsSkin(material) ? 1.0f : 0.0f;

    /// <summary>
    /// **肌らしさ**(0〜1)。<c>mtoon.frag</c> の <c>SkinLikeness</c> と同じ式。色はリニアのベースカラー。
    ///
    /// <code>
    ///   smoothstep(0.2, 0.45, 輝度) x smoothstep(0.03, 0.12, R - B)
    /// </code>
    ///
    /// <para>
    /// しきい値はこのアバターの顔と体のテクスチャを数えて決めた(計画書の「試して分かったこと」)。
    /// 肌は輝度 0.6〜0.9・R - B が 0.2〜0.5、タイツと黒いインナーは輝度 0.1 以下、白い布は R - B がほぼ 0。
    /// 本来は画家が描くマスクのテクスチャの<b>代役</b>で、肌色に近い服を着たアバターではこの式は通用しない。
    /// </para>
    /// </summary>
    public static float SkinLikeness(Vector3 linear)
    {
        float luminance = Vector3.Dot(linear, new Vector3(0.2126f, 0.7152f, 0.0722f));
        return SmoothStep(0.2f, 0.45f, luminance) * SmoothStep(0.03f, 0.12f, linear.X - linear.Z);
    }

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0.0f, 1.0f);
        return t * t * (3.0f - (2.0f * t));
    }

    /// <summary>
    /// フレームに1回の値を送る。<paramref name="day"/> は 1 で昼の段、0 で夜の段(記事の <c>_SSS_DayNightBlend</c>)。
    /// </summary>
    public void Apply(Shader shader, RenderResources resources, float day)
    {
        shader.SetInt("uSssEnabled", Enabled ? 1 : 0);
        shader.SetInt("uSssView", View);
        shader.SetInt("uSssCastShadow", CastShadow ? 1 : 0);
        shader.SetInt("uSssPointLights", PointLights ? 1 : 0);
        shader.SetInt("uSssSkinTone", Mask == 1 ? 1 : 0);
        shader.SetVector3("uSssColor", Color);
        shader.SetVector3("uSssDeepColor", DeepColor);
        shader.SetFloat("uSssWrap", Wrap);
        shader.SetFloat("uSssDeepWrap", DeepWrap);
        shader.SetFloat("uSssDeepIntensity", DeepIntensity);
        shader.SetFloat("uSssBacklightIntensity", BacklightIntensity);
        shader.SetFloat("uSssIntensity", Intensity);
        shader.SetFloat("uSssDay", day);

        resources.GetTexture(Ramp).Bind(TextureUnit.Texture0 + RampUnit);
        shader.SetInt("uSssRamp", RampUnit);
    }

    // ===== 式(mtoon.frag と同じ。自己チェックと実験台が使う)=====

    /// <summary>
    /// **影の境目からの距離 d**(要点2)。-1 = 影の奥 / 0 = 境目 / 1 = 受光の中心。
    ///
    /// <code>
    ///   境目 b = -shift            (MToon は N・L + shift = 0 で影の度合いがちょうど半分)
    ///   d = N・L >= b ? (N・L - b) / (1 - b) : (N・L - b) / (1 + b)
    /// </code>
    ///
    /// <para>
    /// 記事の UTS は段差の位置(<c>_BaseColor_Step</c>)から境目を出すが、MToon では <b>shift の符号を変えたもの</b>が境目になる。
    /// 両側を別々に割るのは、境目が真ん中に無くても、両端をそれぞれ -1 と 1 にそろえるため。
    /// </para>
    /// <para>
    /// <b>影の側が無いマテリアル</b>(体と服。shift 1 で b = -1)は、いつも受光の中心(d = 1)にいる扱いにする。
    /// 割る数が 0 になるからではなく、<b>見えている境目が無いところに、にじませる相手は無い</b>から。
    /// 明るい側が無いとき(b &gt;= 1)は逆にいつも影の奥。
    /// </para>
    /// </summary>
    public static float BoundaryDistance(float nDotL, float shift)
    {
        float boundary = -shift;
        if (1.0f + boundary < 1e-3f)
        {
            return 1.0f;
        }

        if (1.0f - boundary < 1e-3f)
        {
            return -1.0f;
        }

        float x = nDotL - boundary;
        return x >= 0.0f ? x / (1.0f - boundary) : x / (1.0f + boundary);
    }

    /// <summary>
    /// **境目の2本の帯**(要点3)。表層と深層の重み(0〜1)と、影の側へ寄せる重みを返す。
    ///
    /// <code>
    ///   帯 = saturate(1 - |d| / 半幅)²
    ///   重み = lerp(0.45, 1, saturate(-d / 深層の半幅))
    /// </code>
    ///
    /// <para>
    /// 2乗するのは、1次のままだと帯の端で急に 0 になり、<b>帯の形が線として見える</b>から(三角形の角が出る)。
    /// </para>
    /// </summary>
    public static (float Shallow, float Deep, float Weight) Bands(float d, float wrap, float deepWrap)
    {
        float deepWidth = MathF.Max(deepWrap, wrap);
        float shallow = Math.Clamp(1.0f - (MathF.Abs(d) / wrap), 0.0f, 1.0f);
        float deep = Math.Clamp(1.0f - (MathF.Abs(d) / deepWidth), 0.0f, 1.0f);
        float shadowSide = Math.Clamp(-d / deepWidth, 0.0f, 1.0f);
        float weight = LitSideWeight + ((1.0f - LitSideWeight) * shadowSide);
        return (shallow * shallow, deep * deep, weight);
    }

    /// <summary>
    /// **逆光の透け**(要点4)。0〜1。
    ///
    /// <code>
    ///   透け = saturate(-L・V)⁴ x saturate(-N・L x 0.5 + 0.5)
    /// </code>
    ///
    /// <para>
    /// 前半は「光がカメラの向こうから来ているか」(光源を見つめる向きほど 1)。4乗で真向かいの狭い範囲に絞る。
    /// 後半は「その面が光に背を向けているか」——光の当たる側の面は透けではなく直接光で明るいので、背を向けた面ほど強くする。
    /// 厚みは見ていない(マスクが厚みの代わり。記事も同じ)。
    /// </para>
    /// </summary>
    public static float Backlight(float lDotV, float nDotL)
    {
        float back = Math.Clamp(-lDotV, 0.0f, 1.0f);
        back *= back;
        back *= back;
        return back * Math.Clamp((-nDotL * 0.5f) + 0.5f, 0.0f, 1.0f);
    }

    /// <summary>昼の段の色味(境目のいちばん濃いところ)。暖色。記事も「昼は暖色、夜は寒色」。</summary>
    public static readonly Vector3 DayTint = new(1.0f, 0.62f, 0.38f);

    /// <summary>夜の段の色味。寒色。夜は肌の赤みを抑え、月と灯りの青に寄せる。</summary>
    public static readonly Vector3 NightTint = new(0.55f, 0.65f, 1.0f);

    /// <summary>
    /// **ランプの1点**(要点3)。u は d x 0.5 + 0.5。<b>両端は白(色を変えない)、中央付近だけ色味が乗る</b>(記事のランプと同じ形)。
    /// 中央から 0.35 離れたら白。間は smoothstep でなだらかにつなぐ(段が付くと帯の中に線が出る)。
    /// </summary>
    public static Vector3 RampColor(float u, Vector3 tint)
    {
        float t = Math.Clamp(1.0f - (MathF.Abs(u - 0.5f) / 0.35f), 0.0f, 1.0f);
        t = t * t * (3.0f - (2.0f * t));
        return Vector3.Lerp(Vector3.One, tint, t);
    }

    /// <summary>
    /// **ランプの絵**(256x4 の RGBA)。下の2行が夜、上の2行が昼。
    /// 記事は手で描いたテクスチャだが、ここではリポジトリに画像を足さず、式から作る(画家が描き直す前の「たたき台」)。
    /// </summary>
    public static byte[] CreateRamp()
    {
        byte[] pixels = new byte[RampWidth * RampHeight * 4];

        for (int y = 0; y < RampHeight; y++)
        {
            // GL のテクスチャは1行目が v = 0(下)。だから y = 0, 1 が v = 0.25 で引く夜の段になる。
            Vector3 tint = y < RampHeight / 2 ? NightTint : DayTint;

            for (int x = 0; x < RampWidth; x++)
            {
                // 画素の真ん中の u(端の画素が u = 0 と 1 に来るように、幅 - 1 で割る)。
                Vector3 color = RampColor(x / (float)(RampWidth - 1), tint);
                int i = ((y * RampWidth) + x) * 4;
                pixels[i + 0] = (byte)MathF.Round(color.X * 255.0f);
                pixels[i + 1] = (byte)MathF.Round(color.Y * 255.0f);
                pixels[i + 2] = (byte)MathF.Round(color.Z * 255.0f);
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// **光1つぶんの散乱**(要点5)。<c>mtoon.frag</c> の <c>SssBoundary</c> + <c>SssTransmission</c> と同じ手順で、
    /// 光の色とマスクを掛ける前の色を返す。ランプは式で引く(テクスチャの 8bit の丸めを通らない)。
    /// </summary>
    public (Vector3 Boundary, Vector3 Transmission) Scatter(float d, float nDotL, float lDotV, float day)
    {
        float u = Math.Clamp((d * 0.5f) + 0.5f, 0.0f, 1.0f);
        Vector3 ramp = Vector3.Lerp(RampColor(u, NightTint), RampColor(u, DayTint), day);

        (float shallow, float deep, float weight) = Bands(d, Wrap, DeepWrap);
        Vector3 boundary = ((Color * ramp * shallow) + (DeepColor * ramp * deep * DeepIntensity)) * weight;
        Vector3 transmission = DeepColor * ramp * Backlight(lDotV, nDotL) * BacklightIntensity;
        return (boundary, transmission);
    }
}
