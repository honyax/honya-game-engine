using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **法線マップと高さマップを手で作るところ**(Day 34)。
///
/// 今日の題材には、モデルから読むテクスチャでは足りないものがある。
///
/// | 要るもの | glTF から来るか |
/// |---|---|
/// | 法線マップ | **来る**(DamagedHelmet などが持っている) |
/// | 高さマップ | **来ない**。glTF のコア仕様に高さ(視差用)のテクスチャが無い |
///
/// 視差マッピングは高さが無いと始まらないので、ここで作る。
/// 買ってきた素材を置くこともできるが、**自分で作ると2つ得がある**。
///
/// <list type="number">
/// <item>
/// <b>法線マップが高さマップから出てくる</b>ことが手を動かして分かる。
/// 法線マップは「傾きの地図」で、高さマップは「高さの地図」——
/// 傾きは高さの微分なので、後者から前者は作れる(逆は積分になるので簡単ではない)。
/// 「なぜ法線マップはだいたい薄紫なのか」もここで腹に落ちる。
/// </item>
/// <item>
/// <b>3枚が必ず辻褄の合った素材になる</b>。
/// ベースカラー・法線・高さがずれていると、
/// 「実装が間違っている」のか「素材が合っていない」のか切り分けられない。
/// 同じ1つの関数から3枚を出せば、その疑いが消える。
/// </item>
/// </list>
///
/// <para>
/// 作るのはレンガ壁。目地(溝)が深いので、**視差の効きがいちばん分かりやすい**。
/// 平らな板なのに奥行きが見える、という視差マッピングの主張は、
/// 溝が深くて、斜めから見られる面でしか実感できない。
/// </para>
///
/// <para>
/// <b>並びは左下から右上</b>(<see cref="Texture.FromPixels"/> の約束)。
/// 配列の行番号 y が増える向きが、そのまま UV の V が増える向きになる。
/// ここを取り違えると法線マップの緑が上下逆になり、**凹凸が裏返る**。
/// </para>
/// </summary>
internal static class SurfaceMaps
{
    /// <summary>1辺のテクセル数。レンガの目地が潰れない程度にあればよい。</summary>
    public const int Size = 512;

    /// <summary>横に並ぶレンガの数。</summary>
    private const int Columns = 4;

    /// <summary>縦に並ぶレンガの段数。</summary>
    private const int Rows = 8;

    /// <summary>
    /// 目地の幅(レンガ1個の大きさに対する割合)。
    ///
    /// **公開してあるのは自己チェックが使うため**。
    /// 法線マップのうち「傾いている画素」の割合は、この数字だけで予言できる——
    /// レンガ1個を単位正方形と見ると、4辺から幅 <c>MortarWidth</c> の帯が傾く側なので、
    /// <c>1 - (1 - 2 * MortarWidth)^2</c> がその面積になる。
    /// **模様のパラメータから出る値と、焼いた結果が一致するか**を確かめられる。
    /// </summary>
    public const float MortarWidth = 0.06f;

    /// <summary>
    /// 高さマップを作る。**返すのは 0〜1 の高さが R に入った RGBA**。
    ///
    /// RGBA で持つのは <see cref="Texture.FromPixels"/> がそれしか受け取らないため。
    /// 1チャンネルで済む(<see cref="Texture.CreateR8"/> がある)が、
    /// **視差の途中結果を目で見たいときに RGB へ同じ値を入れておくほうが楽**なので、
    /// ここでは灰色として持っておく。
    ///
    /// 高さの決め方は3段。
    ///   1. レンガの面は 1.0、目地は 0.0
    ///   2. 境目は急に落とさず、少しなだらかにする(**面取り**)
    ///   3. 全体に細かいざらつきを足す
    ///
    /// 2 が要るのは、真上から真下へ垂直に落ちる壁があると
    /// **レイマーチが刻み幅の粗さをそのまま拾って階段になる**から。
    /// 現実のレンガにも角の丸みがあるので、絵としても自然になる。
    /// </summary>
    public static byte[] CreateHeight()
    {
        var pixels = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float height = HeightAt(x, y);
                byte value = ToByte(height);

                int i = ((y * Size) + x) * 4;
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// **高さマップから法線マップを作る**。今日の要点3そのもの。
    ///
    /// やることは高さの傾きを測るだけ。隣どうしの高さの差が傾きで、
    /// 傾きが分かれば面の向き(法線)が決まる。
    ///
    /// <code>
    ///   dx = h(x+1, y) - h(x-1, y)     // 横方向の傾き
    ///   dy = h(x, y+1) - h(x, y-1)     // 縦方向の傾き
    ///   N  = normalize(-dx, -dy, 1)    // 接空間での法線
    /// </code>
    ///
    /// <b>符号がマイナスなのはなぜか</b>。右へ行くほど高くなる面
    /// (dx が正)は、法線が左へ傾く——坂を登るとき、地面は自分のほうを向く。
    /// ここを間違えると凹凸が裏返るので、**山が山に見えるかで確かめる**。
    ///
    /// <b>Z が 1 固定なのが「だいたい薄紫」の正体</b>。
    /// 平らなところは (0, 0, 1) で、これを 0〜1 に写すと (0.5, 0.5, 1.0) = 薄い青紫。
    /// 法線マップが一面その色に見えるのは、**ほとんどの画素が平らだから**であって、
    /// 「そういう色の画像」なのではない。
    ///
    /// <paramref name="strength"/> は傾きの倍率。大きいほど凹凸が強く見えるが、
    /// **高さマップは変わらない**——だから視差(高さを使う)と法線(傾きを使う)が
    /// 食い違うことがありうる。素材として配られる法線マップと高さマップが
    /// 微妙に噛み合わないのは、たいていこれが原因になる。
    /// </summary>
    public static byte[] CreateNormal(float strength = 8.0f)
    {
        var pixels = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                // 端は反対側へ回り込ませる。テクスチャは Repeat で貼るので、
                // **端で傾きが途切れると継ぎ目に線が出る**。
                float left = HeightAt(x - 1, y);
                float right = HeightAt(x + 1, y);
                float down = HeightAt(x, y - 1);
                float up = HeightAt(x, y + 1);

                var normal = Vector3.Normalize(new Vector3(
                    -(right - left) * strength,
                    -(up - down) * strength,
                    1.0f));

                int i = ((y * Size) + x) * 4;
                pixels[i] = ToByte((normal.X * 0.5f) + 0.5f);
                pixels[i + 1] = ToByte((normal.Y * 0.5f) + 0.5f);
                pixels[i + 2] = ToByte((normal.Z * 0.5f) + 0.5f);
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// ベースカラー。レンガは赤茶、目地は灰色。
    ///
    /// **色は sRGB で作る**(<see cref="Texture.FromPixels"/> に <c>srgb: true</c> で渡す)。
    /// 法線と高さは数値なので <c>srgb: false</c>——
    /// **同じ関数から出た3枚が、読み方だけ違う**のが Day 32 の要点5の実例になっている。
    ///
    /// レンガごとに明るさを少しずつ変えてある。全部同じ色だと、
    /// 視差でずれたときに「ずれた」ことが分からない
    /// (模様が動いて初めて奥行きに見える)。
    /// </summary>
    public static byte[] CreateBaseColor()
    {
        var pixels = new byte[Size * Size * 4];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float height = HeightAt(x, y);

                // 目地(高さが低い)は灰色、レンガ(高い)は赤茶。
                var mortar = new Vector3(0.62f, 0.60f, 0.56f);
                var brick = BrickColor(x, y);

                // 高さ 0.35 あたりを境に混ぜる。面取りのところが自然に繋がる。
                float t = Smoothstep(0.15f, 0.55f, height);
                Vector3 color = Vector3.Lerp(mortar, brick, t);

                // ざらつきを明るさに少しだけ乗せる。
                color *= 0.92f + (Noise(x, y, 128) * 0.16f);

                int i = ((y * Size) + x) * 4;
                pixels[i] = ToByte(color.X);
                pixels[i + 1] = ToByte(color.Y);
                pixels[i + 2] = ToByte(color.Z);
                pixels[i + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>
    /// 座標 (x, y) の高さ。**3枚すべてがこの1つの関数から出る**。
    ///
    /// 範囲外は反対側へ回り込ませる(Repeat で貼るテクスチャなので、
    /// 端がつながっていないと継ぎ目が出る)。
    /// </summary>
    private static float HeightAt(int x, int y)
    {
        x = ((x % Size) + Size) % Size;
        y = ((y % Size) + Size) % Size;

        (float u, float v) = ((float)x / Size, (float)y / Size);

        // 段ごとに半個ぶんずらす。**レンガらしさはこの互い違いで出る**。
        float row = v * Rows;
        int rowIndex = (int)MathF.Floor(row);
        float offset = (rowIndex % 2 == 0) ? 0.0f : 0.5f;

        float column = (u * Columns) + offset;

        // レンガ1個の中での位置(0〜1)。
        float fx = column - MathF.Floor(column);
        float fy = row - rowIndex;

        // 目地までの距離。**縁からの距離のうち小さいほう**が、その画素の「深さ」を決める。
        float edge = MathF.Min(
            MathF.Min(fx, 1.0f - fx),
            MathF.Min(fy, 1.0f - fy));

        // 面取り。目地の幅ぶんかけて 0 → 1 へなだらかに上がる。
        float height = Smoothstep(0.0f, MortarWidth, edge);

        // レンガの表面のざらつき。目地には乗せない(乗せると溝が浅く見える)。
        height *= 0.88f + (Noise(x, y, 64) * 0.12f);

        return Math.Clamp(height, 0.0f, 1.0f);
    }

    /// <summary>レンガ1個ぶんの色。個体差を出すために格子の番号から決める。</summary>
    private static Vector3 BrickColor(int x, int y)
    {
        float v = (float)y / Size;
        float row = v * Rows;
        int rowIndex = (int)MathF.Floor(row);
        float offset = (rowIndex % 2 == 0) ? 0.0f : 0.5f;
        int columnIndex = (int)MathF.Floor(((float)x / Size * Columns) + offset);

        // **番号を折り返す**。半個ずらした段では columnIndex が Columns に届くので、
        // 折り返さないと右端のレンガだけ左端と違う色になり、
        // タイルを2枚並べたときに**縦の継ぎ目**として見える(Alt+6 で 2 以上にすると出る)。
        columnIndex = Wrap(columnIndex, Columns);
        rowIndex = Wrap(rowIndex, Rows);

        // 番号から擬似乱数を作る。**格子の番号だけで決まる**ので、
        // 同じレンガの中では色が変わらない。
        float r = Hash((columnIndex * 73) + (rowIndex * 151));

        return new Vector3(
            0.42f + (r * 0.16f),
            0.16f + (r * 0.08f),
            0.12f + (r * 0.05f));
    }

    /// <summary>
    /// ざらつき。**格子の値を補間しただけの、いちばん素朴なノイズ**。
    ///
    /// パーリンノイズのような滑らかさは無いが、
    /// 表面の細かい荒れを出すには足りる。
    /// 今日の主題は視差なので、ノイズは主役ではない。
    ///
    /// <para>
    /// <b>格子の番号を折り返すのが、タイルとして貼るための条件</b>。
    /// <paramref name="cells"/> 個ぶんで1周するようにしておくと、
    /// 右端の次が左端に繋がる。折り返しを忘れると、
    /// **テクスチャを2枚並べた継ぎ目に縦線が出る**——
    /// 模様そのものは繋がっているのに、ざらつきだけが途切れるので原因が分かりにくい。
    /// </para>
    /// </summary>
    /// <param name="cells">テクスチャ1枚に並ぶ格子の数。**Size を割り切る必要は無い**。</param>
    private static float Noise(int x, int y, int cells)
    {
        float px = (float)x / Size * cells;
        float py = (float)y / Size * cells;

        int gx = (int)MathF.Floor(px);
        int gy = (int)MathF.Floor(py);
        float fx = px - gx;
        float fy = py - gy;

        float a = CornerHash(gx, gy, cells);
        float b = CornerHash(gx + 1, gy, cells);
        float c = CornerHash(gx, gy + 1, cells);
        float d = CornerHash(gx + 1, gy + 1, cells);

        float sx = Smoothstep(0.0f, 1.0f, fx);
        float sy = Smoothstep(0.0f, 1.0f, fy);

        return ((a * (1 - sx)) + (b * sx)) * (1 - sy)
            + (((c * (1 - sx)) + (d * sx)) * sy);
    }

    /// <summary>格子の隅の値。**番号を折り返してから引く**ので、端と端が同じ値になる。</summary>
    private static float CornerHash(int gx, int gy, int cells) =>
        Hash((Wrap(gx, cells) * 374761393) + (Wrap(gy, cells) * 668265263));

    /// <summary>0 以上 <paramref name="period"/> 未満へ折り返す。負の数でも正しく回る。</summary>
    private static int Wrap(int value, int period) => ((value % period) + period) % period;

    /// <summary>整数から 0〜1 の擬似乱数。**決定的**なので、毎回同じ絵になる。</summary>
    private static float Hash(int value)
    {
        uint x = (uint)value;
        x ^= x >> 16;
        x *= 0x7feb352dU;
        x ^= x >> 15;
        x *= 0x846ca68bU;
        x ^= x >> 16;
        return (x & 0xFFFFFF) / (float)0xFFFFFF;
    }

    /// <summary>GLSL の <c>smoothstep</c> と同じもの。両端で傾きが 0 になる補間。</summary>
    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / MathF.Max(edge1 - edge0, 1e-6f), 0.0f, 1.0f);
        return t * t * (3.0f - (2.0f * t));
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
}
