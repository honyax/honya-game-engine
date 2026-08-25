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

    /// <summary>使うシェーダ。実体は <see cref="ResourceManager"/> が持つ。</summary>
    public Handle<Shader> Shader { get; }

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

    /// <summary>
    /// このマテリアルを使う状態にする。**描画の直前に呼ぶ**。
    ///
    /// ハンドルを解く相手が要るので、<see cref="ResourceManager"/> を受け取る形になった。
    /// マテリアル自身に管理者への参照を持たせてもよいが、
    /// **どの管理者に属するかが暗黙になる**ので引数で渡している。
    /// </summary>
    public void Apply(ResourceManager resources)
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
        // ハンドルが未設定でも <see cref="ResourceManager.GetTexture"/> は
        // 仮の絵を返すので、**必ず何かを bind する**。
        // 「テクスチャが無いときは bind しない」にすると、
        // 直前に描いたものの絵がそのまま出てしまい、原因が分かりにくい。
        resources.GetTexture(MainTexture).Bind(TextureUnit.Texture0);
        shader.SetInt("uTexture", 0);
    }
}
