using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **6面のテクスチャを1枚として扱うもの**(Day 36)。立方体の内側に絵を貼った箱。
///
/// 普通のテクスチャは UV(2次元)で引くが、キューブマップは<b>方向ベクトル</b>で引く。
/// <code>
///   vec3 color = texture(uEnvironment, direction).rgb;
/// </code>
/// 「その向きを見たら何が見えるか」を返す関数、と思うのがいちばん近い。
///
/// <para>
/// GPU が方向から面と UV を求めてくれる。手順は
/// <b>いちばん絶対値の大きい成分でどの面かが決まり、残りの2成分を割って UV にする</b>だけ。
/// 自分で書いても 10 行だが、ハードウェアがやるほうが速く、
/// しかも<b>面の継ぎ目をまたいだ補間</b>(シームレス)まで面倒を見てくれる。
/// </para>
///
/// <para>
/// <b>なぜ球ではなく立方体なのか</b>。方向を平面の絵に写す方法はいくらでもあるが、
///   - 正距円筒(equirectangular)… 極が極端に引き伸ばされ、テクセルが無駄になる
///   - キューブ … 6面とも歪みが同程度。しかも**普通の2Dテクスチャ6枚**として置ける
/// 3Dの描画ハードウェアが立方体を選んだのは、後者が実装として素直だったから。
/// 素材は正距円筒で配られることが多い(HDRI は横長の1枚)ので、
/// <b>読み込んだあとキューブへ焼き直す</b>のが定番の入口になる(<see cref="EnvironmentMap"/>)。
/// </para>
///
/// <para>
/// <b>面の並びは GL が決めている</b>。+X, -X, +Y, -Y, +Z, -Z の順で、
/// <c>TextureTarget.TextureCubeMapPositiveX</c> から連番。
/// この順番は RenderMan(1980 年代)由来で、
/// <b>Y の向きが上下反転している</b>という有名な落とし穴が付いてくる(<see cref="FaceViews"/>)。
/// </para>
/// </summary>
internal sealed class CubeMap : IDisposable
{
    private readonly GL _gl;
    private bool _disposed;

    private CubeMap(GL gl, uint handle, int size, int mipLevels)
    {
        _gl = gl;
        Handle = handle;
        Size = size;
        MipLevels = mipLevels;
    }

    public uint Handle { get; }

    /// <summary>1面の1辺のテクセル数。**6面とも正方形で同じ大きさ**(GL の要求)。</summary>
    public int Size { get; }

    /// <summary>ミップレベルの数。1 なら原寸のみ。</summary>
    public int MipLevels { get; }

    /// <summary>VRAM の推定バイト数。RGB16F は 1 テクセル 6 バイト。</summary>
    public long ByteSize
    {
        get
        {
            long total = 0;
            for (int mip = 0; mip < MipLevels; mip++)
            {
                long side = Math.Max(1, Size >> mip);
                total += side * side * 6 * 6;
            }

            return total;
        }
    }

    /// <summary>
    /// 空のキューブマップを作る。**中身は GPU が描いて埋める**(<see cref="EnvironmentMap"/>)。
    ///
    /// 形式は RGB16F 固定。環境マップに入るのは色ではなく<b>明るさ</b>で、
    /// 太陽は 100 を超える値になるので、8bit では入らない(Day 31 の要点そのもの)。
    /// RGBA ではなく RGB なのは、環境に透明度が無いため——1テクセル 8 バイトが 6 バイトになる。
    ///
    /// <para>
    /// <b>ClampToEdge は必ず要る</b>。Repeat にすると、面の端を読んだときに
    /// 同じ面の反対側へ回り込んで、**継ぎ目に十字の線が出る**。
    /// </para>
    ///
    /// <para>
    /// <b>そのうえで <c>TextureCubeMapSeamless</c> を有効にする</b>(<see cref="EnableSeamless"/>)。
    /// ClampToEdge は「面の中で止める」だけなので、面をまたいだ補間はしてくれない。
    /// 継ぎ目のテクセルは隣の面の色と混ざるべきで、それを GPU にやらせる指定。
    /// 事前フィルタ(粗い = 大きくぼかす)では特に効き、
    /// 無いと**立方体の稜線がうっすら見える**。
    /// </para>
    /// </summary>
    public static unsafe CubeMap Create(GL gl, int size, int mipLevels = 1)
    {
        mipLevels = Math.Clamp(mipLevels, 1, FullMipCount(size));

        uint handle = gl.GenTexture();

        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.TextureCubeMap, handle);

        // **6面を1つずつ確保する**。glTexImage2D を 6 回呼ぶ——
        // キューブマップ専用の確保 API は無く、面ごとに 2D テクスチャとして扱う。
        for (int face = 0; face < 6; face++)
        {
            for (int mip = 0; mip < mipLevels; mip++)
            {
                gl.TexImage2D(
                    TextureTarget.TextureCubeMapPositiveX + face,
                    mip,
                    InternalFormat.Rgb16f,
                    (uint)Math.Max(1, size >> mip),
                    (uint)Math.Max(1, size >> mip),
                    0,
                    PixelFormat.Rgb,
                    PixelType.Float,
                    null);
            }
        }

        var cube = new CubeMap(gl, handle, size, mipLevels);
        cube.SetFilter();
        gl.BindTexture(TextureTarget.TextureCubeMap, 0);

        return cube;
    }

    /// <summary>1辺が 1 になるまで何回半分にできるか。256 なら 9 段。</summary>
    public static int FullMipCount(int size) => (int)Math.Floor(Math.Log2(size)) + 1;

    /// <summary>
    /// 面をまたいだ補間を有効にする。**コンテキスト全体の設定**なので1回でよい。
    ///
    /// テクスチャごとではなく GL の状態なのが少し気持ち悪いが、
    /// OpenGL 3.2 でコアに入ったときからこの形になっている。
    /// </summary>
    public static void EnableSeamless(GL gl)
    {
        gl.Enable(EnableCap.TextureCubeMapSeamless);
    }

    /// <summary>
    /// **6面ぶんのビュー行列**。焼くときに使う。
    ///
    /// 立方体の中心から6方向を向いたカメラを作るだけ……なのだが、
    /// <b>ここが今日いちばん踏みやすい落とし穴</b>。
    ///
    /// <para>
    /// キューブマップの面の向きは RenderMan 由来の<b>左手系</b>で定義されていて、
    /// OpenGL の右手系とは <c>Y</c> が上下逆になっている。
    /// 素直に「上は +Y」で6面のカメラを組むと、
    /// <b>焼いた環境が上下反転する</b>——しかも空が上にあるうちは気づきにくく、
    /// 「なぜか地面が天井から照らしてくる」という形で後から出てくる。
    /// </para>
    ///
    /// <para>
    /// 対処は 2 通りある。
    ///   1. ここで上方向を反転させておく(**この実装**)
    ///   2. シェーダで方向ベクトルの Y を反転する
    /// 1 のほうが**間違えるのが1箇所で済む**。読む側(skybox / textured / prefilter)は
    /// 全部そのまま書けるので、規約が散らばらない。
    /// </para>
    /// </summary>
    public static Matrix4x4[] FaceViews(Vector3 eye)
    {
        // 面の順は GL の連番と同じ: +X, -X, +Y, -Y, +Z, -Z。
        //
        // Up がどれも「下向き(-Y)」なのが 1 の対処そのもの。
        // 上下の面(+Y / -Y)だけは Up に Y を使えないので、Z を当てる。
        (Vector3 Forward, Vector3 Up)[] faces =
        [
            (Vector3.UnitX, -Vector3.UnitY),
            (-Vector3.UnitX, -Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitZ),
            (-Vector3.UnitY, -Vector3.UnitZ),
            (Vector3.UnitZ, -Vector3.UnitY),
            (-Vector3.UnitZ, -Vector3.UnitY),
        ];

        var views = new Matrix4x4[6];
        for (int i = 0; i < 6; i++)
        {
            views[i] = Matrix4x4.CreateLookAt(eye, eye + faces[i].Forward, faces[i].Up);
        }

        return views;
    }

    /// <summary>
    /// 焼くときに使う射影行列。**画角 90 度**で、近クリップと遠クリップは適当でよい。
    ///
    /// 90 度なのは、立方体の1面がちょうど 90 度を覆うから。
    /// ここを 89 度や 91 度にすると、面の継ぎ目に隙間や重なりができる。
    /// </summary>
    public static Matrix4x4 FaceProjection() =>
        Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2.0f, 1.0f, 0.1f, 10.0f);

    /// <summary>指定したテクスチャユニットに結び付ける。</summary>
    public void Bind(TextureUnit unit = TextureUnit.Texture0)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.TextureCubeMap, Handle);
    }

    /// <summary>
    /// ミップマップを生成する。**事前フィルタでは呼んではいけない**——
    /// あちらは各ミップに「その粗さでぼかした結果」を自分で描き込むので、
    /// 上書きされてしまう。環境マップ本体(焼いた直後)だけで使う。
    /// </summary>
    public void GenerateMipmaps()
    {
        Bind();
        _gl.GenerateMipmap(TextureTarget.TextureCubeMap);
    }

    /// <summary>
    /// **1面を読み返す**。自己チェック用。
    ///
    /// キューブマップは絵として並べて見せにくい(6面を展開する UI が要る)ので、
    /// 数字で確かめられる窓口を用意しておく。
    /// 「太陽の側の面がいちばん明るいか」「放射照度マップが元より暗いか」は、
    /// 平均を取れば1行で判定できる。
    ///
    /// <see cref="Mesh{TVertex}.ReadVertices"/> と同じで**遅い**(GPU の完了を待つ)。
    /// </summary>
    public unsafe float[] ReadFace(int face, int mip = 0)
    {
        int side = Math.Max(1, Size >> mip);
        var pixels = new float[side * side * 3];

        Bind();

        fixed (float* data = pixels)
        {
            _gl.GetTexImage(
                TextureTarget.TextureCubeMapPositiveX + face,
                mip,
                PixelFormat.Rgb,
                PixelType.Float,
                data);
        }

        return pixels;
    }

    /// <summary>ミップの有無に応じて縮小フィルタを選ぶ(<see cref="Texture.SetFilter"/> と同じ話)。</summary>
    private void SetFilter()
    {
        _gl.BindTexture(TextureTarget.TextureCubeMap, Handle);

        _gl.TexParameter(
            TextureTarget.TextureCubeMap,
            TextureParameterName.TextureMinFilter,
            MipLevels > 1 ? (int)TextureMinFilter.LinearMipmapLinear : (int)TextureMinFilter.Linear);

        // **確保した段までしか使わせない**。
        // 事前フィルタは 128x128 に 5 段だけ焼く(全部で 8 段ぶん置ける)ので、
        // 上限を教えておかないと GPU は 6〜7 段目を読みに行き、
        // **中身が未定義のまま**なので環境によって黒くなったりごみが出たりする。
        // 「不完全なテクスチャは黙って壊れる」の一例(Texture.HasMipmaps のコメント)。
        _gl.TexParameter(
            TextureTarget.TextureCubeMap, TextureParameterName.TextureBaseLevel, 0);
        _gl.TexParameter(
            TextureTarget.TextureCubeMap, TextureParameterName.TextureMaxLevel, MipLevels - 1);

        _gl.TexParameter(
            TextureTarget.TextureCubeMap,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);

        // R が3本目の軸(立方体の奥行き)。2D の S/T に1本足した形。
        _gl.TexParameter(
            TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(
            TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteTexture(Handle);
    }
}
