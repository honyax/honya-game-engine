using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// 「どう見えるか」をひとまとめにしたもの。**シェーダ + そのシェーダに渡す値**。
///
/// シェーダとマテリアルの違いが最初は分かりにくいので、対比しておく。
///   - <see cref="Shader"/>  … 処理そのもの(GPU で動くプログラム)。**重い。使い回す**
///   - <see cref="Material"/> … その処理に渡す値の組。**軽い。いくつも作る**
///
/// 同じシェーダに違う値を渡せば違う見た目になる。だから
/// 「木箱」「石壁」「金属板」はマテリアルが3つあれば足り、
/// シェーダは1本で済む。**シェーダの本数を増やさずに種類を増やす**のがこの分離の目的。
///
/// uniform は3つの階層に分かれる。**誰が、どのくらいの頻度で送るか**で分ける。
///   1. **フレームごと** … ビュー射影行列、時間  → <see cref="Camera"/> の値を Program がフレーム頭で1回送る
///   2. **オブジェクトごと** … モデル行列        → 描画のたびに呼び出し側が送る
///   3. **マテリアルごと** … 色、テクスチャ      → **このクラスの担当**
///
/// uniform は**プログラムに紐づく状態**なので、<c>glUseProgram</c> を呼び直しても値は消えない。
/// だから 1 はフレームに1回で足り、オブジェクトが100個あっても送り直す必要が無い。
/// 「どの階層の値か」を取り違えると、毎フレーム100回同じ行列を送るような無駄が生まれる。
///
/// **Day 21 での変更**: 参照ではなく <see cref="Handle{T}"/> を持つようになった。
/// マテリアルは「どのシェーダで、どのテクスチャを」を指定するだけの**設定の塊**で、
/// リソースそのものの持ち主ではない——という関係が、型に出るようになる。
/// おかげで
///   - マテリアルを丸ごとファイルに書き出せる(ハンドルは整数。Day 24 のシリアライズ)
///   - テクスチャが非同期で差し替わっても、マテリアル側は何もしなくてよい
/// が付いてくる。
/// </summary>
internal sealed class Material
{
    public Material(Handle<Shader> shader)
    {
        Shader = shader;
    }

    /// <summary>使うシェーダ。実体は <see cref="RenderResources"/> が持つ。</summary>
    public Handle<Shader> Shader { get; }

    /// <summary>名前。glTF から読んだものはファイル内の名前が入る。デバッグ表示用。</summary>
    public string Name { get; set; } = "material";

    /// <summary>色味。テクスチャの色に掛け算される。白(1,1,1,1)なら素通し。</summary>
    public Vector4 Tint { get; set; } = Vector4.One;

    /// <summary>
    /// UV の倍率。2 にすると模様が縦横2回ずつ繰り返される
    /// (テクスチャのラップモードが Repeat のとき)。
    /// </summary>
    public Vector2 UvScale { get; set; } = Vector2.One;

    /// <summary>
    /// 貼るテクスチャ。
    ///
    /// Day 15 では <c>Texture?</c> を直接持っていて、
    /// 「共有されるので破棄の責任は持たない」という但し書きが必要だった。
    /// ハンドルにすると但し書きが要らなくなる——**持てないものは壊せない**。
    /// </summary>
    public Handle<Texture> MainTexture { get; set; }

    // ===== ここから Day 32(glTF の metallic-roughness ワークフロー)=====
    //
    // glTF の材質は「拡散色 + 鏡面色」ではなく
    // **「ベースカラー + 金属か否か + 粗さ」**で表す。
    // 金属と非金属で光の返し方が根本的に違う、という物理を素直に写した形で、
    // 「拡散も鏡面も真っ白」のような物理的に有り得ない組み合わせを作りにくい。
    //
    // **Day 32 では BaseColor しか絵に使わない**。
    // 残りは読み込んで持っておくだけで、使い始めるのは
    // 法線マップが Day 34、メタリック/ラフネスが Day 35、AO が Day 37。
    // 先に器を作っておくと、その日の差分がシェーダだけで済む。

    /// <summary>ベースカラーの倍率。テクスチャに掛かる。**リニアで持つ**(Day 31 の要点3)。</summary>
    public Vector4 BaseColorFactor { get; set; } = Vector4.One;

    /// <summary>金属度。0 = 非金属(誘電体)、1 = 金属。**中間はほぼ物理的に無い**。</summary>
    public float MetallicFactor { get; set; } = 1.0f;

    /// <summary>粗さ。0 = 鏡、1 = つや消し。</summary>
    public float RoughnessFactor { get; set; } = 1.0f;

    /// <summary>
    /// メタリックとラフネスを1枚に詰めたもの。**B が金属度、G が粗さ**。
    /// R は空き(AO を入れる流儀もある)。**色ではないのでリニアで読む**。
    /// </summary>
    public Handle<Texture> MetallicRoughnessTexture { get; set; }

    /// <summary>
    /// 接空間の法線マップ。**Day 34 でついに絵に効くようになった**。
    ///
    /// Day 32 で読み込みだけ先に作ってあったので、今日の差分は
    /// 「接線を用意してシェーダで使う」だけで済んでいる。
    /// <b>器を先に作っておくと、その日の差分が小さくなる</b>という
    /// Day 32 の狙いが当たった形。
    /// </summary>
    public Handle<Texture> NormalTexture { get; set; }

    /// <summary>
    /// 法線の強さ(Day 34)。glTF の <c>normalTexture.scale</c> に対応する。
    ///
    /// 読んだ法線の xy にだけ掛ける。1.0 で素通し、0 で凹凸なし、
    /// 1 より大きくすると凹凸が誇張される。
    /// **z には掛けない**のがミソで、xy だけ伸ばして正規化し直すと
    /// 「傾きだけを強くする」になる。
    /// </summary>
    public float NormalScale { get; set; } = 1.0f;

    /// <summary>
    /// 高さマップ(Day 34)。視差マッピングが読む。**glTF には無い**。
    ///
    /// コア仕様にも Khronos の主要な拡張にも、視差用の高さテクスチャは入っていない
    /// (<c>KHR_materials_displacement</c> は提案止まり)。
    /// だから読み込んだモデルには絶対に付いてこず、
    /// 使うなら**素材を別に用意する**か、こちらで作ることになる
    /// (<see cref="SurfaceMaps"/>)。
    ///
    /// 値の約束は「1 が表面、0 が谷底」。逆(0 が表面)の素材も世の中にはあるので、
    /// **貼ってみて凹凸が反転したら、まずここを疑う**。
    /// </summary>
    public Handle<Texture> HeightTexture { get; set; }

    /// <summary>
    /// 視差の深さ(Day 34)。**UV をずらす量の上限**を接空間の高さで表したもの。
    ///
    /// 0.05 なら「高さ 1 の凹凸が、面の大きさの 5% ぶん UV をずらす」。
    /// 大きくするほど奥行きが出るが、**輪郭の破綻も大きくなる**——
    /// 視差マッピングは形を変えていないので、深くするほど嘘がばれる。
    /// </summary>
    public float ParallaxScale { get; set; } = 0.05f;

    /// <summary>環境遮蔽。焼き込まれた「へこみの暗さ」。Day 37 の SSAO と足し合わせる。</summary>
    public Handle<Texture> OcclusionTexture { get; set; }

    /// <summary>発光。**色なので sRGB で読む**。Day 31 の HDR パイプラインとそのまま繋がる。</summary>
    public Handle<Texture> EmissiveTexture { get; set; }

    /// <summary>発光の倍率。</summary>
    public Vector3 EmissiveFactor { get; set; } = Vector3.Zero;

    /// <summary>裏面も描くか。glTF の既定は false(片面)。</summary>
    public bool DoubleSided { get; set; }

    /// <summary>OPAQUE / MASK / BLEND。今日は表示するだけで、透過の実装は Day 40。</summary>
    public string AlphaMode { get; set; } = "OPAQUE";

    /// <summary>MASK のときの切り捨てしきい値。</summary>
    public float AlphaCutoff { get; set; } = 0.5f;

    /// <summary>
    /// このマテリアルを使う状態にする。**描画の直前に呼ぶ**。
    ///
    /// ハンドルを解く相手が要るので、<see cref="RenderResources"/> を受け取る形になった。
    /// マテリアル自身に管理者への参照を持たせてもよいが、
    /// **どの管理者に属するかが暗黙になる**ので引数で渡している。
    /// </summary>
    public void Apply(RenderResources resources)
    {
        Shader shader = resources.GetShader(Shader);
        shader.Use();

        shader.SetVector4("uTint", Tint);
        shader.SetVector2("uUvScale", UvScale);

        // 0番のテクスチャユニットに刺して、シェーダにも「0番を見ろ」と教える。
        //
        // **sampler は int で渡す**。テクスチャのハンドルではなくユニットの番号。
        // ここを取り違えると、たまたま動いてしまうことがあるぶん厄介
        // (ハンドルが小さい整数のときに偶然一致する)。
        //
        // ハンドルが未設定でも <see cref="RenderResources.GetTexture"/> は
        // 仮の絵を返すので、**必ず何かを bind する**。
        // 「テクスチャが無いときは bind しない」にすると、
        // 直前に描いたものの絵がそのまま出てしまい、原因が分かりにくい。
        resources.GetTexture(MainTexture).Bind(TextureUnit.Texture0);
        shader.SetInt("uTexture", 0);

        // --- Day 32: PBR の値とマップ ---
        //
        // **ユニット番号は固定で割り当てる**。
        // 「何番に何が刺さっているか」をマテリアルごとに変えると、
        // シェーダ側が知りようがなくなる。番号は two-way の約束なので、
        // ここと textured.frag の両方に同じ表を書いておく。
        //   0 ベースカラー / 1 メタリック・ラフネス / 2 法線 / 3 AO / 4 発光
        //   5 シャドウマップ(ShadowMap が刺す)/ 6 高さ(Day 34)
        shader.SetVector4("uBaseColorFactor", BaseColorFactor);
        shader.SetFloat("uMetallicFactor", MetallicFactor);
        shader.SetFloat("uRoughnessFactor", RoughnessFactor);
        shader.SetVector3("uEmissiveFactor", EmissiveFactor);

        BindMap(resources, shader, MetallicRoughnessTexture, 1, "uMetallicRoughnessMap", "uHasMetallicRoughnessMap");
        BindMap(resources, shader, NormalTexture, 2, "uNormalMap", "uHasNormalMap");
        BindMap(resources, shader, OcclusionTexture, 3, "uOcclusionMap", "uHasOcclusionMap");
        BindMap(resources, shader, EmissiveTexture, 4, "uEmissiveMap", "uHasEmissiveMap");

        // --- Day 34: 接空間 ---
        //
        // **5番は飛ばす**。そこは Day 33 のシャドウマップが使っていて、
        // マテリアルではなく ShadowMap がフレームに1回刺す
        // (ユニットの割り当ては1枚の表で管理しないと必ず衝突する)。
        shader.SetFloat("uNormalScale", NormalScale);
        shader.SetFloat("uParallaxScale", ParallaxScale);
        BindMap(resources, shader, HeightTexture, 6, "uHeightMap", "uHasHeightMap");
    }

    /// <summary>
    /// 補助マップを1枚割り当てる。**無いときも必ず何かを bind する**。
    ///
    /// 「無いなら bind しない」にすると、そのユニットには
    /// **直前のマテリアルの絵が刺さったまま**になる。
    /// シェーダ側は <c>uHas…</c> を見て使わないので絵には出ないが、
    /// 「使わないはずのものが読まれている」状態を残さないほうが後で楽になる。
    /// </summary>
    private static void BindMap(
        RenderResources resources,
        Shader shader,
        Handle<Texture> handle,
        int unit,
        string samplerName,
        string presenceName)
    {
        resources.GetTexture(handle).Bind(TextureUnit.Texture0 + unit);
        shader.SetInt(samplerName, unit);
        shader.SetInt(presenceName, handle.IsValid ? 1 : 0);
    }
}
