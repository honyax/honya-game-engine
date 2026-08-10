namespace SoftwareRasterizer;

/// <summary>
/// テクスチャのサンプリング方法。
/// </summary>
internal enum TextureFilter
{
    /// <summary>最も近いテクセルを1つ読む。拡大するとブロック状になる。</summary>
    Nearest,

    /// <summary>周囲4テクセルを距離で重み付けして混ぜる。拡大してもなめらか。</summary>
    Bilinear,
}

/// <summary>
/// テクスチャ(画像)。
///
/// 中身は <see cref="Framebuffer"/> と同じで「int の1次元配列 + 幅と高さ」でしかない。
/// 違うのは**読み方**で、フレームバッファが整数のピクセル座標で書き込むのに対し、
/// テクスチャは 0.0〜1.0 の実数座標(UV)で読む。
///
/// UV が 0〜1 に正規化されているのは、**テクスチャの解像度と切り離すため**。
/// モデルの側は「この頂点は画像の右上」とだけ言えばよく、
/// その画像が 64x64 でも 4096x4096 でも同じデータが使える。
/// 解像度を差し替えられる(LODやモバイル向けの縮小版)のはこの設計のおかげ。
/// </summary>
internal sealed class Texture
{
    public int Width { get; }

    public int Height { get; }

    /// <summary>テクセルの配列。0xAARRGGBB。フレームバッファと同じ形式。</summary>
    public int[] Texels { get; }

    /// <summary>サンプリング方法。実行中に切り替えて比べられるようにしている。</summary>
    public TextureFilter Filter { get; set; } = TextureFilter.Bilinear;

    public Texture(int width, int height)
    {
        Width = width;
        Height = height;
        Texels = new int[width * height];
    }

    /// <summary>
    /// UV から色を読む。<see cref="Filter"/> に応じて方式を切り替える。
    ///
    /// 戻り値を Vec3(0〜1の実数)にしているのは、この後シェーディングで
    /// 掛け算するため(Day 9)。int のまま返すと掛け算のたびに変換が要る。
    /// </summary>
    public Vec3 Sample(float u, float v)
        => Filter == TextureFilter.Nearest ? SampleNearest(u, v) : SampleBilinear(u, v);

    /// <summary>
    /// ニアレストネイバー。最も近いテクセルを1つ読むだけ。
    ///
    /// 速いが、拡大すると1テクセルが四角いブロックとして見える(ドット絵の拡大と同じ)。
    /// レトロな見た目をわざと出したいとき以外は、拡大時には使いにくい。
    /// </summary>
    public Vec3 SampleNearest(float u, float v)
    {
        // UV をテクセル座標に直す。0.0 がテクスチャの左端、1.0 が右端。
        int x = WrapIndex((int)MathF.Floor(u * Width), Width);
        int y = WrapIndex((int)MathF.Floor(v * Height), Height);
        return ToVec3(Texels[y * Width + x]);
    }

    /// <summary>
    /// バイリニア補間。周囲4テクセルを距離で重み付けして混ぜる。
    ///
    /// 要点は **-0.5 のずらし**。テクセルの「色が定義されている場所」は
    /// テクセルの左上の角ではなく**中心**なので、
    /// テクセル座標に直したあと 0.5 引いて中心基準にそろえる必要がある。
    /// これを忘れると絵が半テクセルずれる(拡大率が大きいほど目立つ)。
    ///
    /// 混ぜ方は Day 4 の属性補間と同じ線形補間を、横方向と縦方向に2回やるだけ。
    /// 「バイ(bi = 2つの)リニア(linear = 線形)」の名前どおり。
    /// </summary>
    public Vec3 SampleBilinear(float u, float v)
    {
        float x = u * Width - 0.5f;
        float y = v * Height - 0.5f;

        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);

        // 小数部分がそのまま混ぜる比率になる。
        float fx = x - x0;
        float fy = y - y0;

        int x0w = WrapIndex(x0, Width);
        int y0w = WrapIndex(y0, Height);
        int x1w = WrapIndex(x0 + 1, Width);
        int y1w = WrapIndex(y0 + 1, Height);

        Vec3 c00 = ToVec3(Texels[y0w * Width + x0w]);
        Vec3 c10 = ToVec3(Texels[y0w * Width + x1w]);
        Vec3 c01 = ToVec3(Texels[y1w * Width + x0w]);
        Vec3 c11 = ToVec3(Texels[y1w * Width + x1w]);

        // まず横方向に2回、その結果を縦方向に1回混ぜる。
        Vec3 top = Vec3.Lerp(c00, c10, fx);
        Vec3 bottom = Vec3.Lerp(c01, c11, fx);
        return Vec3.Lerp(top, bottom, fy);
    }

    /// <summary>
    /// 範囲外のテクセル座標を繰り返し(リピート)で折り返す。
    ///
    /// C# の % は負の数に対して負を返すので、そのままだと
    /// UV が 0 未満のときに配列外アクセスになる。2回目の加算と % で正に直している。
    /// 他の折り返し方(クランプ、ミラー)もあり、実機ではテクスチャごとに設定できる。
    /// </summary>
    private static int WrapIndex(int value, int size) => ((value % size) + size) % size;

    private static Vec3 ToVec3(int packed) => new(
        ((packed >> 16) & 0xFF) / 255.0f,
        ((packed >> 8) & 0xFF) / 255.0f,
        (packed & 0xFF) / 255.0f);

    /// <summary>
    /// 市松模様 + 円 + 目盛りのテストパターンを作る。
    ///
    /// 画像ファイルを読み込まず手続きで作っているのは、
    /// Phase 0〜1 を標準ライブラリだけで完結させるため(と、素材の用意が要らないため)。
    /// Day 10 で obj モデルを扱うときに、必要なら画像読み込みを足す。
    ///
    /// 模様の選び方には意図がある。
    ///   - 市松    … 直線が多いので、透視補正の有無が一目で分かる
    ///   - 円      … 曲線なので、ニアレストのジャギーが目立つ
    ///   - 細い線  … 縮小したときのちらつき(エイリアシング)が分かる
    /// </summary>
    public static Texture CreateTestPattern(int size, int checkerCells)
    {
        var texture = new Texture(size, size);
        int cellSize = Math.Max(size / checkerCells, 1);
        float center = size / 2.0f;
        float radius = size * 0.34f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool darkCell = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                Vec3 color = darkCell
                    ? new Vec3(0.16f, 0.20f, 0.32f)
                    : new Vec3(0.88f, 0.86f, 0.78f);

                // 中心の円。境界を1テクセルで切るので、ニアレストだと階段が出る。
                float dx = x + 0.5f - center;
                float dy = y + 0.5f - center;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance < radius)
                {
                    color = darkCell
                        ? new Vec3(0.90f, 0.45f, 0.15f)
                        : new Vec3(0.95f, 0.70f, 0.30f);
                }

                // 外周1テクセルの枠。面の境目がどこかを見るための目印。
                if (x == 0 || y == 0 || x == size - 1 || y == size - 1)
                {
                    color = new Vec3(0.10f, 0.85f, 0.75f);
                }

                texture.Texels[y * size + x] = Framebuffer.Rgb(color.X, color.Y, color.Z);
            }
        }

        return texture;
    }
}
