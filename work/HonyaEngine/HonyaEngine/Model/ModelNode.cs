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

    /// <summary>
    /// 2つの姿勢を混ぜる(Day 42)。**平行移動と拡大は lerp、回転だけ slerp**。
    ///
    /// Day 41 の <c>Sampler</c> がキーとキーの間を埋めたのとまったく同じ演算だが、
    /// **混ぜる相手が「別のクリップ」に変わった**だけ。
    /// 「歩き」と「走り」を 30:70 で混ぜる、というのはこの1行の話になる。
    ///
    /// <b>行列にしてしまうと、この操作ができなくなる</b>。
    /// これが Day 41 で <see cref="NodePose"/> という型を作った理由そのもので、
    /// クリップの合成は**必ず TRS の段でやる**。
    /// </summary>
    public static NodePose Lerp(in NodePose a, in NodePose b, float t) =>
        new(
            Vector3.Lerp(a.Translation, b.Translation, t),
            Quaternion.Normalize(Quaternion.Slerp(a.Rotation, b.Rotation, t)),
            Vector3.Lerp(a.Scale, b.Scale, t));
}

/// <summary>
/// 3本以上の姿勢を重み付きで混ぜる入れ物(Day 42)。
///
/// <para>
/// <b>なぜ <see cref="NodePose.Lerp"/> の繰り返しで済まないのか</b>。
/// 2本なら slerp で足りるが、ブレンドツリーとクロスフェードが重なると
/// **同時に4本のクリップが効く**ことがある(走りへ移りかけたところで
/// 歩き↔走りのブレンドも走っている、という状態)。
/// slerp を入れ子にすると、**混ぜる順番によって答えが変わる**うえ、
/// 重みの意味も分かりにくくなる。
/// </para>
///
/// <para>
/// そこで実際のエンジンがやっているのと同じ手を使う。
/// <b>平行移動と拡大は重み付きの和、回転は「重み付きで足してから正規化」</b>
/// (weighted nlerp と呼ばれる)。
/// slerp とは厳密には違う——回る速さが一定にならない——が、
/// **混ぜる相手が近い姿勢のときは差が出ない**。
/// 歩きと走りのように似た歩容を混ぜるぶんには、これで十分。
/// </para>
///
/// <para>
/// <b>符号を揃えるのが肝</b>。<c>q</c> と <c>-q</c> は同じ向きを表すので、
/// 揃えずに足すと**打ち消し合って長さが 0 に近づく**。
/// そのまま正規化すると、まったく関係のない向きが出る。
/// 最初に足したものを基準にして、内積が負なら反転してから足す。
/// </para>
/// </summary>
internal struct PoseAccumulator
{
    private Vector3 _translation;
    private Vector3 _scale;

    /// <summary>正規化前のクォータニオン。**4成分をただ足していく**ので <see cref="Vector4"/> で持つ。</summary>
    private Vector4 _rotation;

    private Quaternion _reference;
    private float _weight;

    /// <summary>重み <paramref name="weight"/> で足し込む。<b>重み 0 以下は無視する</b>。</summary>
    public void Add(in NodePose pose, float weight)
    {
        if (weight <= 0.0f)
        {
            return;
        }

        Quaternion rotation = pose.Rotation;

        if (_weight <= 0.0f)
        {
            // 最初の1本が符号の基準になる。
            _reference = rotation;
        }
        else if (Quaternion.Dot(_reference, rotation) < 0.0f)
        {
            // **裏返してから足す**(上のコメント)。
            rotation = -rotation;
        }

        _translation += pose.Translation * weight;
        _scale += pose.Scale * weight;
        _rotation += new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W) * weight;
        _weight += weight;
    }

    /// <summary>
    /// 混ぜた結果を取り出す。1本も足していなければ <paramref name="fallback"/> を返す。
    ///
    /// 回転は**正規化するだけ**で重みの割り算が要らない(長さを 1 にすれば同じこと)。
    /// 平行移動と拡大のほうは重みの合計で割る——
    /// 呼ぶ側が重みを 1 に揃えていても、**丸めで 0.9999 になっている**ことがあるので、
    /// ここで割っておくとモデルがわずかに縮む事故を防げる。
    /// </summary>
    public readonly NodePose Resolve(in NodePose fallback)
    {
        if (_weight <= 1e-6f)
        {
            return fallback;
        }

        float inverse = 1.0f / _weight;
        var rotation = new Quaternion(_rotation.X, _rotation.Y, _rotation.Z, _rotation.W);

        return new NodePose(
            _translation * inverse,
            rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize(rotation) : fallback.Rotation,
            _scale * inverse);
    }
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
