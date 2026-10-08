using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **MToon のつまみ一式**(Day 68)。VRM 1.0 の <c>VRMC_materials_mtoon</c> 拡張に書かれている値を、そのまま持つ。
///
/// <para>
/// <b>MToon は「トゥーンの作り方の1つ」ではなく、仕様で式まで決まったシェーダ</b>。
/// アバターの作者は UniVRM や three-vrm の上の MToon で見た目を調整して書き出しているので、
/// 式を自分の好みで変えると、同じ値を読んでも「なんか違う」絵になる。
/// だからここに持つのは<b>ファイルに書かれた値だけ</b>で、こちらの判断で足した値は1つも無い。
/// </para>
///
/// <para>
/// <b>なぜ <see cref="Material"/> に足さず、別のクラスにしたか</b>。glTF のマテリアル(PBR の値)は
/// MToon の拡張があっても<b>捨てずに残っている</b>——MToon を知らないビューアのための「代わりの見た目」だから。
/// 1つのマテリアルが2つの見た目を同時に持つので、PBR の値は <see cref="Material"/> に、
/// MToon の値はこちらに分け、<see cref="Material.MToon"/> から指す。
/// 「VRMビューア」の F3 で PBR に切り替えられるのは、両方が残っているおかげ。
/// </para>
///
/// <para>
/// <b>持たないもの</b>。UV アニメーション(<c>uvAnimation*</c>)と <c>KHR_texture_transform</c>。
/// 今日のアバターはどちらも 0 / 単位で書き出されているので、読まずに済ませる。
/// </para>
/// </summary>
internal sealed class MToon
{
    // ===== テクスチャユニットの割り当て =====
    //
    // **0〜4 と 6 は Material.Apply が毎回刺し直す番号**を使い回す(ベースカラー・法線・発光は同じ意味のまま)。
    // 使い回すのは、textured.frag で描くものに<b>前の描画の刺しっぱなしが残らない</b>ようにするため——
    // Material.Apply は 1・3・6 を描くたびに刺し直すので、ここで何を刺しても次の描画には響かない。
    // 5 は影、7 は放射照度マップ、14 はプローブ(どれもフレームに1回、外から刺す)。
    // 16・17 は誰も使っていない番号。GL が保証する「1つの段で 16 枚」は番号の上限ではなく枚数の上限なので、
    // 16 番以降も使える(番号の上限は全部の段を合わせた GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS で、最低 48)。

    /// <summary>影の色の掛け算テクスチャ(1 番)。Material の金属度・粗さの番号を借りる。</summary>
    public const int ShadeUnit = 1;

    /// <summary>マットキャップ(3 番)。Material の AO の番号を借りる。</summary>
    public const int MatcapUnit = 3;

    /// <summary>リムの掛け算テクスチャ(6 番)。Material の高さの番号を借りる。</summary>
    public const int RimUnit = 6;

    /// <summary>影の位置をずらすテクスチャ(16 番)。</summary>
    public const int ShadingShiftUnit = 16;

    /// <summary>アウトラインの太さの掛け算テクスチャ(17 番)。**頂点シェーダが読む**。</summary>
    public const int OutlineWidthUnit = 17;

    /// <summary>アウトラインの太さの単位。</summary>
    public enum OutlineMode
    {
        /// <summary>描かない。</summary>
        None,

        /// <summary><b>メートル</b>。カメラから離れると細くなる(遠くの人の線は細い)。</summary>
        World,

        /// <summary><b>画面の高さに対する割合</b>。カメラからの距離によらず同じ太さに見える。</summary>
        Screen,
    }

    // ===== 影(要点2)=====

    /// <summary>影の色(リニア)。<c>shadeColorFactor</c>。光の当たらない側はベースカラーではなくこの色になる。</summary>
    public Vector3 ShadeColorFactor { get; set; } = Vector3.Zero;

    /// <summary>影の色に掛けるテクスチャ(sRGB)。VRoid はベースカラーと同じ絵を指してくる。</summary>
    public Handle<Texture> ShadeMultiplyTexture { get; set; }

    /// <summary>
    /// **影の境目をずらす量**。<c>shadingShiftFactor</c>。N・L に足してから段を付ける。
    /// 正にすると明るい側が広がり、<b>1 なら真後ろ以外ぜんぶ明るい側</b>(VRoid の体と服がこれ)。
    /// </summary>
    public float ShadingShiftFactor { get; set; }

    /// <summary>境目のずらしを場所ごとに変えるテクスチャ(リニア、R)。</summary>
    public Handle<Texture> ShadingShiftTexture { get; set; }

    /// <summary>上のテクスチャに掛ける倍率。<c>shadingShiftTexture.scale</c>。</summary>
    public float ShadingShiftTextureScale { get; set; } = 1.0f;

    /// <summary>
    /// **境目の硬さ**。<c>shadingToonyFactor</c>。0 なら N・L が -1〜1 をかけて滑らかに変わり、
    /// 1 に近づくほど幅が 0 に縮んで<b>明るい側と影の側の2色</b>になる。
    /// </summary>
    public float ShadingToonyFactor { get; set; } = 0.9f;

    /// <summary>
    /// **環境光の均し**。<c>giEqualizationFactor</c>。0 なら法線の向きの環境光をそのまま、
    /// 1 なら上と下の平均(向きによらない一様な環境光)。トゥーンでは環境光の陰影が邪魔になるので、1 寄りが多い。
    /// </summary>
    public float GiEqualizationFactor { get; set; } = 0.9f;

    // ===== リムとマットキャップ(要点3)=====

    /// <summary>マットキャップの色(リニア)。<c>matcapFactor</c>。</summary>
    public Vector3 MatcapFactor { get; set; } = Vector3.One;

    /// <summary>
    /// **マットキャップ**(sRGB)。球を1枚撮った絵で、ビュー空間の法線の xy で引く。
    /// 光源と関係なく「こちらを向いた面ほど何色」を決められるので、髪の天使の輪や金属の照り返しに使う。
    /// </summary>
    public Handle<Texture> MatcapTexture { get; set; }

    /// <summary>リムの色(リニア)。<c>parametricRimColorFactor</c>。</summary>
    public Vector3 ParametricRimColorFactor { get; set; } = Vector3.Zero;

    /// <summary>リムとマットキャップに掛けるテクスチャ(sRGB)。</summary>
    public Handle<Texture> RimMultiplyTexture { get; set; }

    /// <summary>
    /// **リムをどれだけ光で照らすか**。<c>rimLightingMixFactor</c>。0 なら光と関係なく光る(暗闇でも縁が光る)、
    /// 1 なら届いている光(直接光の色 + 環境光)を掛ける。
    /// </summary>
    public float RimLightingMixFactor { get; set; } = 1.0f;

    /// <summary>縁の鋭さ。<c>parametricRimFresnelPowerFactor</c>。大きいほど縁の細い帯に縮む。</summary>
    public float ParametricRimFresnelPowerFactor { get; set; } = 5.0f;

    /// <summary>縁の持ち上げ。<c>parametricRimLiftFactor</c>。正にすると正面寄りの面にもリムが乗る。</summary>
    public float ParametricRimLiftFactor { get; set; }

    // ===== アウトライン(要点4)=====

    /// <summary>太さの単位。<c>outlineWidthMode</c>。</summary>
    public OutlineMode OutlineWidthMode { get; set; } = OutlineMode.None;

    /// <summary>太さ。<c>outlineWidthFactor</c>。単位は <see cref="OutlineWidthMode"/> で決まる。</summary>
    public float OutlineWidthFactor { get; set; }

    /// <summary>太さを場所ごとに変えるテクスチャ(リニア、**G**)。目の周りや口の中で線を消すのに使う。</summary>
    public Handle<Texture> OutlineWidthMultiplyTexture { get; set; }

    /// <summary>線の色(リニア)。<c>outlineColorFactor</c>。</summary>
    public Vector3 OutlineColorFactor { get; set; } = Vector3.Zero;

    /// <summary>線の色をどれだけ光で照らすか。<c>outlineLightingMixFactor</c>。0 なら線の色のまま。</summary>
    public float OutlineLightingMixFactor { get; set; } = 1.0f;

    // ===== 描く順番 =====

    /// <summary>半透明でも深度を書くか。<c>transparentWithZWrite</c>。</summary>
    public bool TransparentWithZWrite { get; set; }

    /// <summary>
    /// **半透明どうしの描く順**。<c>renderQueueOffsetNumber</c>(-9〜0)。小さいほど先に描く。
    /// 白目の上に瞳、瞳の上にハイライト、のように重なる順を作者が決めている。
    /// </summary>
    public int RenderQueueOffsetNumber { get; set; }

    // ===== UV のずらし(Day 69)=====
    //
    // ファイルからは読まない(KHR_texture_transform は今日も読まない。今日のアバターは全部「ずらし 0・拡大 1」)。
    // VRM の表情の textureTransformBinds が、毎フレームここを書き換える。

    /// <summary>UV の拡大。glTF の UV の向き(左上が原点)で持つ。</summary>
    public Vector2 UvScale { get; set; } = Vector2.One;

    /// <summary>UV のずらし。glTF の UV の向きで持つ(<see cref="Apply"/> が GL の向きへ直して送る)。</summary>
    public Vector2 UvOffset { get; set; } = Vector2.Zero;

    /// <summary>
    /// **シェーダへ送る形**(拡大 xy・ずらし zw)。頂点の UV はローダーが v' = 1 - v に裏返してある(Day 32)ので、上下を直す。
    /// ファイルの向きの v_new = v x s + o を、裏返した向きで書き直すと v'_new = v' x s + (1 - s - o) になる。
    /// ファイルの向きで o を増やすと、裏返した向きでは定数項が減る——符号を取り違えると、絵が上下逆の向きへ動く。
    /// </summary>
    public Vector4 GlUvTransform => new(UvScale.X, UvScale.Y, UvOffset.X, 1.0f - UvScale.Y - UvOffset.Y);

    /// <summary>アウトラインを描くか。</summary>
    public bool HasOutline => OutlineWidthMode != OutlineMode.None && OutlineWidthFactor > 0.0f;

    /// <summary>
    /// **キャラに渡す点光源の上限**(Day 68 の要点5)。<c>mtoon.frag</c> の <c>MAX_MTOON_LIGHTS</c> と同じ数。
    ///
    /// <para>
    /// MToon はディファードの共通ライティングに乗らない(G-Buffer に「影の色」も「境目の硬さ」も書く欄が無い)ので、
    /// キャラは別のフォワードパスで描く。フォワードは光を uniform の配列で渡すしかなく、
    /// シーンの全部(群れを出せば 1000 個)は渡せない。<b>キャラは小さい</b>ので、届く光だけを CPU で選べば 8 個で足りる。
    /// Unity のフォワード(1つの物に効く光の数に上限がある)と同じ考え方。
    /// </para>
    /// </summary>
    public const int MaxLights = 8;

    /// <summary>
    /// **キャラに届く点光源を選ぶ**(Day 68 の要点5)。キャラを包む球(中心と半径)に、光の届く球が重なるものだけを候補にし、
    /// 「キャラのいちばん近い点で、どれだけ明るいか」の大きい順に <paramref name="destination"/> の長さまで詰める。
    /// </summary>
    /// <returns>選んだ数。</returns>
    public static int SelectLights(
        ReadOnlySpan<PointLight> lights, Vector3 center, float radius, Span<PointLight> destination)
    {
        Span<float> scores = stackalloc float[destination.Length];
        int count = 0;

        foreach (PointLight light in lights)
        {
            float distance = Vector3.Distance(light.Position, center);

            // 届く球とキャラの球が重ならなければ、キャラのどこにも効かない(減衰の窓が 0)。
            if (distance >= light.Radius + radius)
            {
                continue;
            }

            // **いちばん近い点での明るさ**で比べる。中心での明るさで比べると、
            // 大きな光が中心にはわずかに届かないだけで落ちる(肩には当たっているのに)。
            float nearest = MathF.Max(distance - radius, 0.0f);
            float score = PointLight.Attenuation(nearest, light.Radius)
                * Vector3.Dot(light.Color, new Vector3(0.2126f, 0.7152f, 0.0722f));

            // 挿入ソート。8 個しか持たないので、全部並べ替えるより速い。
            int at = count;
            while (at > 0 && scores[at - 1] < score)
            {
                at--;
            }

            if (at >= destination.Length)
            {
                continue;
            }

            int last = Math.Min(count, destination.Length - 1);
            for (int i = last; i > at; i--)
            {
                scores[i] = scores[i - 1];
                destination[i] = destination[i - 1];
            }

            scores[at] = score;
            destination[at] = light;
            count = Math.Min(count + 1, destination.Length);
        }

        return count;
    }

    /// <summary>
    /// **影の度合い**(Day 68 の要点2)。<c>mtoon.frag</c> の <c>Shading</c> と同じ式で、自己チェックと実験台が使う。
    ///
    /// <code>
    ///   shading = linearstep(-1 + toony, 1 - toony, N・L + shift)
    /// </code>
    ///
    /// <para>
    /// 1 なら明るい側(ベースカラー)、0 なら影の側(影の色)。その間は2色を混ぜる。
    /// <b>N・L に足してから段を付ける</b>のが順番の肝で、段を付けてから足すと 0〜1 をはみ出す。
    /// </para>
    /// </summary>
    public static float Shading(float nDotL, float shift, float toony)
    {
        return LinearStep(-1.0f + toony, 1.0f - toony, nDotL + shift);
    }

    /// <summary>
    /// <c>smoothstep</c> の曲がっていない版。<b>a == b のときは段差</b>(x &gt;= a で 1)になる。
    /// toony = 1 で幅が 0 になるので、0 で割らない形にしてある(<c>mtoon.frag</c> と同じ)。
    /// </summary>
    public static float LinearStep(float a, float b, float x)
    {
        float width = b - a;
        if (width <= 1e-6f)
        {
            return x >= a ? 1.0f : 0.0f;
        }

        return Math.Clamp((x - a) / width, 0.0f, 1.0f);
    }

    /// <summary>
    /// **縁の明るさ**(Day 68 の要点3)。<c>mtoon.frag</c> と同じ式。色を掛ける前の 0〜1。
    /// <c>N・V</c> は面がこちらを向いているほど 1、縁ほど 0。
    /// </summary>
    public static float Rim(float nDotV, float lift, float power)
    {
        float x = Math.Clamp(1.0f - nDotV + lift, 0.0f, 1.0f);
        return MathF.Pow(x, power);
    }

    /// <summary>
    /// マテリアル1つぶんの値を <c>mtoon</c> のシェーダへ送る。
    ///
    /// <para>
    /// <b><see cref="Material.Apply"/> は呼ばない</b>。あちらはマテリアル自身のシェーダ(<c>textured</c>)を選んで送るので、
    /// MToon のシェーダには届かない。ベースカラー・法線・発光・アルファは glTF の値をそのまま使う約束なので、
    /// <paramref name="material"/> から読んで、ここで同じ番号に刺す。
    /// </para>
    /// </summary>
    public void Apply(Shader shader, RenderResources resources, Material material)
    {
        // --- glTF の値(MToon も同じものを使う)---
        shader.SetVector4("uBaseColorFactor", material.BaseColorFactor);
        shader.SetVector3("uEmissiveFactor", material.EmissiveFactor);
        shader.SetFloat("uNormalScale", material.NormalScale);
        shader.SetInt("uAlphaMode", material.AlphaMode switch { "MASK" => 1, "BLEND" => 2, _ => 0 });
        shader.SetFloat("uAlphaCutoff", material.AlphaCutoff);

        Bind(resources, shader, material.MainTexture, 0, "uTexture");
        Bind(resources, shader, material.NormalTexture, 2, "uNormalMap");
        Bind(resources, shader, material.EmissiveTexture, 4, "uEmissiveMap");
        shader.SetInt("uHasNormalMap", material.NormalTexture.IsValid ? 1 : 0);

        // 発光のテクスチャが無いときは足さない(仮の絵の白に発光の色を掛けると、全身が光る)。
        shader.SetInt("uHasEmissiveMap", material.EmissiveTexture.IsValid ? 1 : 0);

        // --- MToon の値 ---
        shader.SetVector3("uShadeColorFactor", ShadeColorFactor);
        shader.SetFloat("uShadingShiftFactor", ShadingShiftFactor);
        shader.SetFloat("uShadingShiftTextureScale", ShadingShiftTextureScale);
        shader.SetFloat("uShadingToonyFactor", ShadingToonyFactor);
        shader.SetFloat("uGiEqualizationFactor", GiEqualizationFactor);
        shader.SetVector3("uMatcapFactor", MatcapFactor);
        shader.SetVector3("uParametricRimColorFactor", ParametricRimColorFactor);
        shader.SetFloat("uRimLightingMixFactor", RimLightingMixFactor);
        shader.SetFloat("uParametricRimFresnelPowerFactor", ParametricRimFresnelPowerFactor);
        shader.SetFloat("uParametricRimLiftFactor", ParametricRimLiftFactor);
        shader.SetInt("uOutlineWidthMode", (int)OutlineWidthMode);
        shader.SetFloat("uOutlineWidthFactor", OutlineWidthFactor);
        shader.SetVector3("uOutlineColorFactor", OutlineColorFactor);
        shader.SetFloat("uOutlineLightingMixFactor", OutlineLightingMixFactor);

        shader.SetVector4("uUvTransform", GlUvTransform);

        // **無いテクスチャは「掛けても変わらない値」で埋める**。掛け算のテクスチャは白(1)、
        // 足し算のずらしは黒(0)。RenderResources の仮の絵は白なので、ずらしだけは有無の旗も送る。
        Bind(resources, shader, ShadeMultiplyTexture, ShadeUnit, "uShadeMultiplyTexture");
        Bind(resources, shader, MatcapTexture, MatcapUnit, "uMatcapTexture");
        Bind(resources, shader, RimMultiplyTexture, RimUnit, "uRimMultiplyTexture");
        Bind(resources, shader, ShadingShiftTexture, ShadingShiftUnit, "uShadingShiftTexture");
        Bind(resources, shader, OutlineWidthMultiplyTexture, OutlineWidthUnit, "uOutlineWidthMultiplyTexture");

        shader.SetInt("uHasShadingShiftTexture", ShadingShiftTexture.IsValid ? 1 : 0);

        // **マットキャップが無いときは足さない**。仮の絵(白)のまま足すと、全身が白く飛ぶ。
        shader.SetInt("uHasMatcapTexture", MatcapTexture.IsValid ? 1 : 0);
    }

    private static void Bind(RenderResources resources, Shader shader, Handle<Texture> handle, int unit, string name)
    {
        resources.GetTexture(handle).Bind(TextureUnit.Texture0 + unit);
        shader.SetInt(name, unit);
    }
}
