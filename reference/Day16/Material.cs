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
/// </summary>
internal sealed class Material
{
    public Material(Shader shader)
    {
        Shader = shader;
    }

    /// <summary>使うシェーダ。**共有される**ので Material は破棄の責任を持たない。</summary>
    public Shader Shader { get; }

    /// <summary>色味。テクスチャの色に掛け算される。白(1,1,1,1)なら素通し。</summary>
    public Vector4 Tint { get; set; } = Vector4.One;

    /// <summary>
    /// UV の倍率。2 にすると模様が縦横2回ずつ繰り返される
    /// (テクスチャのラップモードが Repeat のとき)。
    /// </summary>
    public Vector2 UvScale { get; set; } = Vector2.One;

    /// <summary>
    /// 貼るテクスチャ。**共有される**ので、こちらも破棄の責任は持たない。
    /// 誰が寿命を持つかを曖昧にするとリークか二重解放になる。
    /// ハンドルベースの正式な仕組みは Day 21 で作る。
    /// </summary>
    public Texture? MainTexture { get; set; }

    /// <summary>
    /// このマテリアルを使う状態にする。**描画の直前に呼ぶ**。
    /// </summary>
    public void Apply()
    {
        Shader.Use();

        Shader.SetVector4("uTint", Tint);
        Shader.SetVector2("uUvScale", UvScale);

        if (MainTexture is not null)
        {
            // 0番のテクスチャユニットに刺して、シェーダにも「0番を見ろ」と教える。
            //
            // **sampler は int で渡す**。テクスチャのハンドルではなくユニットの番号。
            // ここを取り違えると、たまたま動いてしまうことがあるぶん厄介
            // (ハンドルが小さい整数のときに偶然一致する)。
            MainTexture.Bind(TextureUnit.Texture0);
            Shader.SetInt("uTexture", 0);
        }
    }
}
