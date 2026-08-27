using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 読み込み済みのモデル1体。**「何を、どこに、どう描くか」の平らな一覧**。
///
/// glTF の中身はノードの木(親子)だが、描くときに木のままである必要は無い。
/// ノードを1回歩いて**世界行列を確定させてしまえば**、
/// あとは「メッシュ + マテリアル + 行列」の並びを順に描くだけになる。
///
/// 木のまま持たないのは、静的なモデルには階層が要らないから。
/// 階層が意味を持つのは、あとから関節を動かす場合
/// (スキニング。Day 41)や、部品を付け外しする場合で、
/// **今日読むのは動かないモデル**なので平らにしてよい。
///
/// <para>
/// <b>何を所有するか</b>。<see cref="Mesh{TVertex}"/> は自分で作ったので所有する。
/// テクスチャの実体は <see cref="ResourceManager"/> のものだが、
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
    /// <param name="Name">glTF のノード名。デバッグ表示用。</param>
    internal readonly record struct Part(
        Mesh<Vertex> Mesh,
        Material Material,
        Matrix4x4 Transform,
        string Name);

    private readonly ResourceManager _resources;

    /// <summary>読み込みのときに参照カウントを増やしたテクスチャ。**同じ数だけ返す**。</summary>
    private readonly IReadOnlyList<Handle<Texture>> _textures;

    public Model(
        ResourceManager resources,
        IReadOnlyList<Part> parts,
        IReadOnlyList<Material> materials,
        IReadOnlyList<Handle<Texture>> textures,
        Vector3 boundsMin,
        Vector3 boundsMax,
        string sourcePath)
    {
        _resources = resources;
        _textures = textures;
        Parts = parts;
        Materials = materials;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
        SourcePath = sourcePath;
    }

    /// <summary>描くものの一覧。**この順に描けばよい**。</summary>
    public IReadOnlyList<Part> Parts { get; }

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
        // 誰も使わなくなった時点で ResourceManager が GPU 側を解放する。
        foreach (Handle<Texture> handle in _textures)
        {
            _resources.Release(handle);
        }
    }
}
