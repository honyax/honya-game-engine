using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **テクスチャバッファ**(Buffer Texture / TBO。Day 53)。GPU 上のただのバイト列を、
/// 画素シェーダから<b>番号で引ける1次元の表</b>として見せる。
///
/// <para>
/// Forward+ は光の一覧を画素シェーダに渡す必要がある。Day 52 のフォワードは uniform の配列で渡していたが、
/// その枠は float 1024 個しかなく、64 個が限度だった(<see cref="PointLight.MaxForward"/>)。
/// OpenGL 3.3 の枠の中で「大きな配列を画素シェーダから読む」手段を並べると、残るのはこれだけになる。
/// </para>
///
/// <list type="table">
/// <item><term>uniform の配列</term><description>float 1024 個まで(保証値)。<b>光 64 個で埋まる</b>(Day 52)</description></item>
/// <item><term>uniform ブロック(UBO)</term><description>16KB まで(保証値)。光 1024 個 × 32 バイト = 32KB が<b>入らない</b></description></item>
/// <item><term>シェーダストレージ(SSBO)</term><description>OpenGL 4.3 から。<b>このエンジンは 3.3 で作っている</b>(Day 57 のコンピュートで使う)</description></item>
/// <item><term>テクスチャバッファ(TBO)</term><description>3.1 から。<b>6万5千テクセル以上</b>(保証値。実機はたいてい 1 億を超える)</description></item>
/// </list>
///
/// <para>
/// 使い方は「バッファにバイト列を詰める → テクスチャとして刺す → シェーダが <c>texelFetch</c> で引く」。
/// 普通のテクスチャと違って<b>フィルタもミップも無く、UV ではなく整数の番号で引く</b>。
/// 「テクスチャ」という名前だが、中身は配列そのもので、テクスチャの仕組み(ユニットとサンプラ)を
/// 借りて画素シェーダまで届けているだけ、と思えばよい。
/// </para>
///
/// <para>
/// <b>毎フレーム中身を入れ替える前提</b>で作ってある(光は動くし、カメラが動けば振り分けも変わる)。
/// Day 18 の <see cref="SpriteBatch"/> と同じく、書く前に<b>いったん捨てる</b>(オーファニング)——
/// 前のフレームの中身をまだ GPU が読んでいるかもしれないので、同じ場所に上書きすると待たされる。
/// </para>
/// </summary>
internal sealed class BufferTexture : IDisposable
{
    /// <summary>最初に確保しておくバイト数。足りなくなったら倍々で広げる。</summary>
    private const int InitialBytes = 4096;

    private readonly GL _gl;

    /// <summary>中身のバイト列(バッファオブジェクト)。</summary>
    private readonly uint _buffer;

    /// <summary>それを「テクスチャとして見る」ための口。**画素は持たない**——持っているのは <see cref="_buffer"/> への参照だけ。</summary>
    private readonly uint _texture;

    private bool _disposed;

    /// <param name="format">
    /// 1テクセルの形式。<b>シェーダ側のサンプラの型と揃える</b>こと——
    /// <c>Rgba32f</c> なら <c>samplerBuffer</c>、<c>R32ui</c> / <c>RG32ui</c> なら <c>usamplerBuffer</c>。
    /// 食い違うと <c>texelFetch</c> は黙って 0 を返す(エラーは出ない)。
    /// </param>
    /// <param name="bytesPerTexel">1テクセルのバイト数(<c>Rgba32f</c> なら 16)。</param>
    public unsafe BufferTexture(GL gl, SizedInternalFormat format, int bytesPerTexel)
    {
        _gl = gl;
        Format = format;
        BytesPerTexel = bytesPerTexel;

        _buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.TextureBuffer, _buffer);
        gl.BufferData(BufferTargetARB.TextureBuffer, InitialBytes, null, BufferUsageARB.StreamDraw);
        gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);
        CapacityBytes = InitialBytes;

        _texture = gl.GenTexture();
        AttachBuffer();
    }

    public SizedInternalFormat Format { get; }

    public int BytesPerTexel { get; }

    /// <summary>最後に詰めたテクセルの数。シェーダが引いてよいのはこの手前まで。</summary>
    public int TexelCount { get; private set; }

    /// <summary>いま確保しているバイト数(詰めた量以上)。</summary>
    public long CapacityBytes { get; private set; }

    /// <summary>最後に詰めた量のバイト数。</summary>
    public long ByteSize => (long)TexelCount * BytesPerTexel;

    /// <summary>
    /// **このドライバが許すテクセルの数の上限**(<c>GL_MAX_TEXTURE_BUFFER_SIZE</c>)。
    /// 仕様の保証は 65536 で、光の番号の並びはタイルを細かくするとこれを超えうる。
    /// </summary>
    public static int MaxTexels(GL gl) => gl.GetInteger(GLEnum.MaxTextureBufferSize);

    /// <summary>
    /// **中身を詰め替える**。前の中身は捨てる(オーファニング)。足りなければ倍々で広げる。
    /// </summary>
    public unsafe void Upload<T>(ReadOnlySpan<T> data)
        where T : unmanaged
    {
        long bytes = (long)data.Length * Unsafe.SizeOf<T>();
        TexelCount = (int)(bytes / BytesPerTexel);

        _gl.BindBuffer(BufferTargetARB.TextureBuffer, _buffer);

        bool grew = bytes > CapacityBytes;
        if (grew)
        {
            // **倍々で広げる**(List<T> と同じ)。ぴったりにすると、光が1個増えるたびに確保し直す。
            CapacityBytes = Math.Max(bytes, CapacityBytes * 2);
        }

        // **いったん捨ててから書く**。同じ大きさで null を渡すと、ドライバは新しい領域を用意し、
        // 古い領域は前のフレームの描画が終わった時点で片付ける——CPU は GPU を待たずに済む。
        _gl.BufferData(BufferTargetARB.TextureBuffer, (nuint)CapacityBytes, null, BufferUsageARB.StreamDraw);

        if (bytes > 0)
        {
            fixed (T* pointer = data)
            {
                _gl.BufferSubData(BufferTargetARB.TextureBuffer, 0, (nuint)bytes, pointer);
            }
        }

        _gl.BindBuffer(BufferTargetARB.TextureBuffer, 0);

        // 広げたときはテクスチャ側にも結び付け直す。仕様上は同じバッファを指したままで足りるが、
        // 大きさの控えを持っているドライバもあるので、変わったときだけ念のため言い直しておく。
        if (grew)
        {
            AttachBuffer();
        }
    }

    /// <summary>
    /// ユニットに刺す。**結び付け先は <c>TEXTURE_BUFFER</c>**——同じユニットの <c>TEXTURE_2D</c> とは別の席なので、
    /// 2D のテクスチャを追い出さない。
    /// </summary>
    public void Bind(TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.TextureBuffer, _texture);
    }

    /// <summary>
    /// **GPU から読み返す**(自己チェック用)。<see cref="Mesh{TVertex}.ReadIndices"/> と同じく、
    /// どの VAO にも属さない <c>CopyReadBuffer</c> に結び付けて読む(Day 52 の検証の途中で分かったこと 1)。
    /// </summary>
    public unsafe T[] Read<T>(int count)
        where T : unmanaged
    {
        var result = new T[count];

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _buffer);

        fixed (T* pointer = result)
        {
            _gl.GetBufferSubData(BufferTargetARB.CopyReadBuffer, 0, (nuint)(count * Unsafe.SizeOf<T>()), pointer);
        }

        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);

        return result;
    }

    /// <summary>GPU 側で確保されているバイト数(<c>GL_BUFFER_SIZE</c>)。自己チェック用。</summary>
    public long QueryBufferSize()
    {
        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _buffer);
        _gl.GetBufferParameter(BufferTargetARB.CopyReadBuffer, BufferPNameARB.Size, out int size);
        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);

        return size;
    }

    /// <summary>
    /// テクスチャに「このバッファを、この形式で見よ」と言う(<c>glTexBuffer</c>)。
    /// テクスチャはユニット 0 の <c>TEXTURE_BUFFER</c> の席を借りて設定し、すぐ返す。
    /// </summary>
    private void AttachBuffer()
    {
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.TextureBuffer, _texture);
        _gl.TexBuffer(TextureTarget.TextureBuffer, Format, _buffer);
        _gl.BindTexture(TextureTarget.TextureBuffer, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gl.DeleteTexture(_texture);
        _gl.DeleteBuffer(_buffer);
    }
}
