using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 読み込み済みのモデル1体。**「何を、どこに、どう描くか」の平らな一覧**
/// と、**ノードの木**(Day 41 で戻ってきた)。
///
/// glTF の中身はノードの木(親子)で、Day 32 はそれを1回歩いて
/// 世界行列を確定させ、木を捨てて平らな <see cref="Part"/> の並びにしていた。
/// 静的なモデルにはそれで足りる——姿勢が二度と変わらないから。
///
/// <b>Day 41 で木が要るようになった</b>。アニメーションはノードの TRS を
/// 毎フレーム書き換えるので、そのたびに世界行列を作り直す必要がある。
/// スキニングも「関節ノードの世界行列」を集めて作るので、やはり木が要る。
///
/// 平らな一覧のほうも残してある。<see cref="Part"/> の <c>Transform</c> は
/// **ファイルを読んだ時点の姿勢での世界行列**で、アニメーションを持たないモデル
/// (Day 39 のデモシーンの小物)はこれをそのまま使い続ける。
/// 動かすときだけ <see cref="AnimationPlayer"/> が世界行列を計算し直す。
///
/// <para>
/// <b>何を所有するか</b>。<see cref="Mesh{TVertex}"/> は自分で作ったので所有する。
/// テクスチャの実体は <see cref="RenderResources"/> のものだが、
/// **読み込んだときに参照カウントを1つ増やしている**ので、
/// 捨てるときには同じ数だけ返さなければならない(Day 21 の要点3)。
///
/// ここを忘れると、モデルを切り替えるたびに 2K テクスチャが数枚ずつ残り、
/// **絵は正しいのに VRAM だけ増え続ける**。
/// 参照カウント方式でいちばん出やすい壊れ方がこれで、
/// 「借りた側が返す」を型として書けないのが弱点になっている。
/// </para>
/// </summary>
internal sealed class Model : IDisposable
{
    private bool _disposed;

    /// <summary>描くもの1つぶん。**glTF のプリミティブ1個に対応する**。</summary>
    /// <param name="Mesh">頂点とインデックス。</param>
    /// <param name="Material">見た目。複数のパーツが同じマテリアルを共有することがある。</param>
    /// <param name="Transform">
    /// **世界行列**。ノードの木を根から掛け合わせて確定させたもの。
    /// モデル全体をさらに動かすときは、これに外から掛ける。
    /// </param>
    /// <param name="Name">
    /// glTF のノード名。デバッグ表示用……<b>だったが、Day 39 で仕事が増えた</b>。
    /// 配布されているモデルには「きれいな版」と「錆びた版」が
    /// 1 ファイルに並べて入っていることがある(<c>fire_hydrant</c> / <c>metal_trash_can</c>)。
    /// デモに置くときはどちらか一方だけが欲しいので、
    /// <see cref="DemoScene"/> がこの名前でパーツを選り分ける。
    /// </param>
    /// <param name="BoundsMin">
    /// **このパーツだけ**の境界箱(世界行列を通したあと)。Day 39 で足した。
    /// モデル全体の境界箱だと、上のように 2 体入っているファイルで
    /// 「片方だけ拾ったときの大きさと足元」が分からない。
    /// </param>
    /// <param name="BoundsMax">同上。</param>
    /// <param name="NodeIndex">
    /// このパーツがぶら下がっているノードの番号(Day 41)。
    ///
    /// アニメーションで動くモデルは <c>Transform</c> が使えなくなる——
    /// あれは読み込んだ時点の姿勢での値だから。
    /// 代わりに <c>AnimationPlayer.GetNodeWorld(NodeIndex)</c> を毎フレーム引く。
    /// </param>
    /// <param name="SkinIndex">
    /// 使うスキンの番号。**-1 ならスキン無し**(Day 41)。
    ///
    /// スキン付きのパーツは <c>Transform</c> が単位行列になっている。
    /// glTF の仕様が「スキン付きメッシュのノードの変換は無視すること」と定めていて、
    /// 関節行列のほうが最初から世界へ運ぶ役目を持っているため
    /// (二重に掛けると、モデルがノードの分だけ余計にずれる)。
    /// </param>
    internal readonly record struct Part(
        Mesh<Vertex> Mesh,
        Material Material,
        Matrix4x4 Transform,
        string Name,
        Vector3 BoundsMin,
        Vector3 BoundsMax,
        int NodeIndex,
        int SkinIndex);

    private readonly RenderResources _resources;

    /// <summary>読み込みのときに参照カウントを増やしたテクスチャ。**同じ数だけ返す**。</summary>
    private readonly IReadOnlyList<Handle<Texture>> _textures;

    public Model(
        RenderResources resources,
        IReadOnlyList<Part> parts,
        IReadOnlyList<Material> materials,
        IReadOnlyList<Handle<Texture>> textures,
        Vector3 boundsMin,
        Vector3 boundsMax,
        string sourcePath,
        IReadOnlyList<ModelNode> nodes,
        IReadOnlyList<int> nodeOrder,
        IReadOnlyList<Skin> skins,
        IReadOnlyList<AnimationClip> animations)
    {
        _resources = resources;
        _textures = textures;
        Parts = parts;
        Materials = materials;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        SourcePath = sourcePath;
        Nodes = nodes;
        NodeOrder = nodeOrder;
        Skins = skins;
        Animations = animations;
    }

    /// <summary>描くものの一覧。**この順に描けばよい**。</summary>
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>ノードの木(Day 41)。番号は glTF の <c>nodes</c> の添字そのまま。</summary>
    public IReadOnlyList<ModelNode> Nodes { get; }

    /// <summary>
    /// **親が必ず子より先に来るノード番号の並び**(Day 41)。
    ///
    /// 世界行列は「自分のローカル × 親の世界」で作るので、
    /// 親を先に計算しておかないと 1 フレーム遅れた親の行列を掛けてしまう。
    /// glTF の <c>nodes</c> の並びに順序の保証は無い——
    /// 子が親より前に書かれているファイルは実在する。
    ///
    /// 毎フレーム木を再帰で降りてもよいが、
    /// **順番は読み込みの時点で決まっている**ので、1回並べ替えて配列にしておく。
    /// 再帰が消えて、キャッシュに乗る素直なループになる。
    /// </summary>
    public IReadOnlyList<int> NodeOrder { get; }

    /// <summary>スキンの一覧(Day 41)。空ならスキン無しのモデル。</summary>
    public IReadOnlyList<Skin> Skins { get; }

    /// <summary>アニメーションクリップの一覧(Day 41)。Fox だけが3本持っている。</summary>
    public IReadOnlyList<AnimationClip> Animations { get; }

    /// <summary>スキン付きのパーツの数(Day 41)。HUD と自己チェック用。</summary>
    public int SkinnedParts
    {
        get
        {
            int count = 0;
            foreach (Part part in Parts)
            {
                if (part.SkinIndex >= 0)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>関節の総数(Day 41)。<c>AnimationPlayer.MaxJoints</c> と見比べるために出す。</summary>
    public int JointCount
    {
        get
        {
            int count = 0;
            foreach (Skin skin in Skins)
            {
                count += skin.JointCount;
            }

            return count;
        }
    }

    /// <summary>マテリアルの一覧。パーツから共有されている。デバッグ表示と数え上げ用。</summary>
    public IReadOnlyList<Material> Materials { get; }

    /// <summary>
    /// 全パーツを世界行列で変換したあとの境界箱。
    ///
    /// **モデルの大きさは読むまで分からない**のが今日の実感で、
    /// glTF には単位の決まりがある(1.0 = 1メートル)とはいえ、
    /// 実際には 0.06m の水筒から 40m の街灯まで来る。
    /// カメラを自動で合わせるために持っておく(<c>Program.FrameModel</c>)。
    /// </summary>
    public Vector3 BoundsMin { get; }

    public Vector3 BoundsMax { get; }

    public Vector3 BoundsCenter => (BoundsMin + BoundsMax) * 0.5f;

    /// <summary>境界箱の対角線の長さ。「どのくらい引けば全体が入るか」の目安。</summary>
    public float BoundsRadius => (BoundsMax - BoundsMin).Length() * 0.5f;

    /// <summary>どのファイルから読んだか。</summary>
    public string SourcePath { get; }

    public int TriangleCount { get; init; }

    public int VertexCount { get; init; }

    /// <summary>
    /// ファイルに TANGENT が入っていたパーツの数(Day 34)。
    ///
    /// **持っているモデルのほうが少ない**のが実感で、今日の4体では
    /// WaterBottle と Lantern だけが持っている。
    /// 「エクスポータが出してくれるはず」と決め打ちすると、
    /// DamagedHelmet のような有名モデルでいきなり法線マップが効かなくなる。
    /// </summary>
    public int FileTangentParts { get; init; }

    /// <summary>接線をローダ側で作ったパーツの数(Day 34)。</summary>
    public int GeneratedTangentParts { get; init; }

    /// <summary>
    /// 法線をローダ側で作ったパーツの数(Day 41)。
    ///
    /// **Fox が NORMAL を持っていない**。接線と同じで、
    /// 「有名なサンプルなら全部揃っているはず」は成り立たない。
    /// </summary>
    public int GeneratedNormalParts { get; init; }

    /// <summary>このモデルが握っているテクスチャの枚数。</summary>
    public int TextureCount => _textures.Count;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // **同じメッシュが複数のパーツに現れることは無い**(プリミティブ1個 = メッシュ1個)ので、
        // 重複を気にせず畳んでよい。
        foreach (Part part in Parts)
        {
            part.Mesh.Dispose();
        }

        // テクスチャは**返す**。捨てるのではない——
        // 他のモデルが同じ絵を使っていれば、そちらの参照が残るので消えない。
        // 誰も使わなくなった時点で RenderResources が GPU 側を解放する。
        foreach (Handle<Texture> handle in _textures)
        {
            _resources.Release(handle);
        }
    }
}
