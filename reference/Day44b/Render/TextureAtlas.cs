using System.Numerics;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace HonyaEngine;

/// <summary>
/// アトラスの中の1枚ぶん。**どのテクスチャの、どこを、どの大きさで使うか**。
///
/// <see cref="Texture"/> をそのまま渡す代わりにこれを渡すことで、
/// 「絵が違う = テクスチャが違う」という結びつきが切れる。
/// バッチにとっては同じテクスチャなので、フラッシュが起きない(要点2)。
/// </summary>
internal readonly struct AtlasRegion
{
    public AtlasRegion(Texture texture, Vector2 uvMin, Vector2 uvMax, int width, int height)
    {
        Texture = texture;
        UvMin = uvMin;
        UvMax = uvMax;
        Width = width;
        Height = height;
    }

    /// <summary>実体のテクスチャ。アトラス内の全リージョンで共有される。</summary>
    public Texture Texture { get; }

    /// <summary>切り出し範囲(テクスチャ座標)。左下が小さいほう。</summary>
    public Vector2 UvMin { get; }

    public Vector2 UvMax { get; }

    /// <summary>元画像のピクセル幅。原寸で描きたいときに使う。</summary>
    public int Width { get; }

    public int Height { get; }
}

/// <summary>
/// 複数の画像を1枚のテクスチャに詰め込んだもの。
///
/// Day 17 で見たとおり、バッチは**テクスチャが変わるたびにフラッシュ**する。
/// 絵の種類が10個あれば、どう並べ替えても最低10回のドローコールになる。
/// アトラスはこれを構造から潰す——**絵の種類が増えてもテクスチャは1枚のまま**。
///
/// 詰め方は「棚(shelf)詰め」。高い画像から順に、横一列(棚)に並べ、
/// 入らなくなったら次の段へ移る。最適な詰め方(矩形パッキング)は NP 困難だが、
/// 高さでソートしてから棚に並べるだけで実用上は十分埋まる。
/// **正解を求めるより、十分よい答えを安く出す**の典型例。
///
/// 実務ではオフラインのツール(TexturePacker 等)で焼いておき、
/// 実行時は PNG + 座標表を読むだけにするのが普通。
/// ここで実行時に組むのは、詰め方そのものを見るため。
/// </summary>
internal sealed class TextureAtlas : IDisposable
{
    private readonly Dictionary<string, AtlasRegion> _regions = new();
    private bool _disposed;

    private TextureAtlas(Texture texture, int width, int height)
    {
        Texture = texture;
        Width = width;
        Height = height;
    }

    /// <summary>詰め込み先のテクスチャ。**これ1枚だけ**。</summary>
    public Texture Texture { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyDictionary<string, AtlasRegion> Regions => _regions;

    public AtlasRegion this[string name] => _regions[name];

    /// <summary>
    /// 画像ファイルをまとめて1枚に詰める。
    /// </summary>
    /// <param name="paths">詰める画像。キーはファイル名(拡張子なし)になる。</param>
    /// <param name="padding">
    /// リージョン同士のすき間(ピクセル)。
    ///
    /// **0 にしてはいけない**。バイリニア補間は指定した点の周囲2x2テクセルを読むので、
    /// 端のピクセルを描くと隣のリージョンの色が混ざる(ブリーディング)。
    /// 縮小して描くほど広い範囲から読まれるので、余裕を持って空けておく。
    /// </param>
    public static TextureAtlas FromFiles(GL gl, IEnumerable<string> paths, int padding = 4)
    {
        // Day 15 以降と同じ約束で読み込む。**先頭行が画像の下端**になる。
        StbImage.stbi_set_flip_vertically_on_load(1);

        var sources = new List<(string Name, ImageResult Image)>();
        foreach (string path in paths)
        {
            ImageResult image = ImageResult.FromMemory(
                File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
            sources.Add((Path.GetFileNameWithoutExtension(path), image));
        }

        // **高い順に並べる**。これが棚詰めの肝で、
        // ばらばらの順に置くと棚の高さが最初の1枚で決まってしまい、無駄が増える。
        sources.Sort((a, b) => b.Image.Height.CompareTo(a.Image.Height));

        int atlasSize = ChooseAtlasSize(sources, padding);
        byte[] pixels = new byte[atlasSize * atlasSize * 4];   // 全部ゼロ = 透明

        var placements = new List<(string Name, int X, int Y, int W, int H)>();

        int shelfY = padding;        // 今の棚の下端
        int shelfHeight = 0;         // 今の棚の高さ(その棚で一番高い画像)
        int cursorX = padding;       // 今の棚の書き込み位置

        foreach ((string name, ImageResult image) in sources)
        {
            if (cursorX + image.Width + padding > atlasSize)
            {
                // 横に入らないので次の棚へ。棚の高さぶん下げる。
                shelfY += shelfHeight + padding;
                shelfHeight = 0;
                cursorX = padding;
            }

            if (shelfY + image.Height + padding > atlasSize)
            {
                throw new InvalidOperationException(
                    $"アトラス({atlasSize}x{atlasSize})に収まりません: {name}");
            }

            Blit(pixels, atlasSize, image, cursorX, shelfY);
            placements.Add((name, cursorX, shelfY, image.Width, image.Height));

            cursorX += image.Width + padding;
            shelfHeight = Math.Max(shelfHeight, image.Height);
        }

        // **ミップマップを作らない**。
        //
        // アトラスとミップマップは相性が悪い。縮小版を作る過程で隣のリージョンの
        // 色が混ざり込み、レベルが上がるほど広範囲から混ざる。padding を
        // どれだけ空けても、いずれ全部が1テクセルに潰れるので原理的に防げない。
        //
        // 対策は3つ。(1) ミップマップを使わない (2) リージョンごとに
        // ミップマップを自前で作って詰め直す (3) テクスチャ配列
        // (GL_TEXTURE_2D_ARRAY)を使う。現代的な答えは (3) で、
        // 「同じ大きさの絵を層に積む」ので混ざりようがない。
        // 今日は (1)。スプライトの縮小率がそこまで大きくないので実用上は足りる。
        var texture = Texture.FromPixels(gl, pixels, atlasSize, atlasSize, generateMipmaps: false);
        texture.SetWrap(TextureWrap.ClampToEdge);

        var atlas = new TextureAtlas(texture, atlasSize, atlasSize);

        foreach ((string name, int x, int y, int w, int h) in placements)
        {
            // **半テクセル内側に寄せる**。
            // UV がちょうど境界にあると、丸め次第で隣のテクセルを踏むことがある。
            // テクセルの中心を指すようにしておくと、その事故が起きない。
            float half = 0.5f;
            var uvMin = new Vector2((x + half) / atlasSize, (y + half) / atlasSize);
            var uvMax = new Vector2((x + w - half) / atlasSize, (y + h - half) / atlasSize);

            atlas._regions[name] = new AtlasRegion(texture, uvMin, uvMax, w, h);
        }

        return atlas;
    }

    /// <summary>
    /// 詰め込み先の一辺を決める。
    ///
    /// 面積の合計から下限を出し、2の累乗に切り上げる。
    /// 2の累乗にこだわるのは古い GPU の制約の名残だが、
    /// ミップマップやテクスチャ圧縮との相性が今でもよいので慣習として残っている。
    /// </summary>
    private static int ChooseAtlasSize(List<(string Name, ImageResult Image)> sources, int padding)
    {
        long area = 0;
        int widest = 1;
        int tallest = 1;

        foreach ((_, ImageResult image) in sources)
        {
            area += (long)(image.Width + padding) * (image.Height + padding);
            widest = Math.Max(widest, image.Width + padding * 2);
            tallest = Math.Max(tallest, image.Height + padding * 2);
        }

        // 棚詰めは隙間ができるので、面積の下限の 1.4 倍くらいを狙う。
        int size = (int)Math.Ceiling(Math.Sqrt(area * 1.4));
        size = Math.Max(size, Math.Max(widest, tallest));

        int pow2 = 1;
        while (pow2 < size)
        {
            pow2 *= 2;
        }

        return pow2;
    }

    /// <summary>画像を、アトラスのピクセル配列の指定位置へコピーする。</summary>
    private static void Blit(byte[] destination, int destinationSize, ImageResult source, int x, int y)
    {
        for (int row = 0; row < source.Height; row++)
        {
            int sourceOffset = row * source.Width * 4;
            int destinationOffset = ((y + row) * destinationSize + x) * 4;
            Array.Copy(source.Data, sourceOffset, destination, destinationOffset, source.Width * 4);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Texture.Dispose();
    }
}
