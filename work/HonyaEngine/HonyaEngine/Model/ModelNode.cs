using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// ノード1つぶんのローカル姿勢。**平行移動・回転・拡大の3点セット**(Day 41)。
///
/// Day 32 のローダは、ノードを読んだそばから行列に畳んで捨てていた。
/// 静的なモデルならそれで足りる——**姿勢は二度と変わらない**から。
///
/// アニメーションが入ると事情が変わる。glTF のアニメーションは
/// 「このノードの translation を、この時刻にこの値へ」という形で書かれていて、
/// **行列ではなく TRS の3つを名指しで書き換える**。
/// 畳んだ行列からは平行移動・回転・拡大を取り出し直せない
/// (<see cref="Matrix4x4.Decompose"/> はあるが、せん断が入ると失敗するし、
/// 回転の符号も一意に決まらない)ので、**分解された形のまま持っておく**。
///
/// <para>
/// <b>なぜ行列ではなく TRS で補間するのか</b>。回転を行列のまま線形補間すると、
/// 途中の行列は回転行列でなくなる(直交性が崩れる)。
/// 90度回転と 0度回転を 50% で混ぜると、45度回転ではなく**縮んだ行列**になる。
/// クォータニオンなら球面補間(slerp)で「45度回転」がそのまま出る。
/// これが特論 A-4 で四元数をやった理由そのもので、
/// **アニメーションの補間はここで初めて必然になる**。
/// </para>
/// </summary>
internal struct NodePose
{
    public Vector3 Translation;
    public Quaternion Rotation;
    public Vector3 Scale;

    public NodePose(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        Translation = translation;
        Rotation = rotation;
        Scale = scale;
    }

    public static NodePose Identity => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>
    /// 行列に畳む。**S → R → T の順**(Day 32 の <c>ReadNodeTransform</c> と同じ)。
    ///
    /// System.Numerics は行ベクトル規約なので、
    /// 「先に適用したいもの」を左に書く。拡大してから回して、最後に運ぶ。
    /// </summary>
    public readonly Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateFromQuaternion(Rotation)
        * Matrix4x4.CreateTranslation(Translation);
}

/// <summary>
/// glTF のノード1つ(Day 41)。**木の形と、そこに何がぶら下がっているか**。
///
/// Day 32 の <see cref="Model"/> は「メッシュ + マテリアル + 世界行列」の平らな一覧だった。
/// 今日そこにノードの木が戻ってくるのは、次の2つがどちらも**木を歩き直す**必要があるため。
///
///   - アニメーション … ノードの TRS が毎フレーム書き換わる → 世界行列を再計算する
///   - スキニング     … 関節ノードの世界行列を集めて関節行列を作る
///
/// <para>
/// <b>親だけを持ち、子は持たない</b>。世界行列の計算は「自分のローカル × 親の世界」なので、
/// 親さえ分かれば済む。子の一覧は読み込みのときだけ要るので、
/// <see cref="Model.NodeOrder"/>(親が必ず先に来る並び)に畳んで捨ててある。
/// </para>
/// </summary>
/// <param name="Name">glTF のノード名。関節の名前("mixamorig:LeftArm" など)もここに入る。</param>
/// <param name="Parent">親のノード番号。**-1 なら根**。</param>
/// <param name="RestPose">
/// ファイルに書かれていた姿勢。**アニメーションが触らないノードはこの値のまま**になる。
///
/// 「バインドポーズ」と呼びたくなるが、厳密には別物。
/// バインドポーズはスキンの <c>inverseBindMatrices</c> が持っていて、
/// ファイルのノードがバインドポーズにあるとは限らない
/// (書き出したときのフレームの姿勢がそのまま入っていることが多い)。
/// </param>
/// <param name="MeshIndex">ぶら下がっているメッシュの番号。-1 なら無し。</param>
/// <param name="SkinIndex">使うスキンの番号。-1 ならスキン無し(= 普通のメッシュ)。</param>
internal readonly record struct ModelNode(
    string Name,
    int Parent,
    NodePose RestPose,
    int MeshIndex,
    int SkinIndex);
