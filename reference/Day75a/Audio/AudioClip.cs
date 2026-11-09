using Silk.NET.OpenAL;

namespace HonyaEngine;

/// <summary>
/// 鳴らせる音1つぶん。**OpenAL のバッファ**を包んだもの。
///
/// ここがこの層でいちばん大事な線引きになる。
///
/// <code>
///   AudioClip (バッファ) … 音のデータ。1つだけ持つ
///   ボイス    (ソース)   … 「今それを鳴らしている人」。同時にいくつも作れる
/// </code>
///
/// <see cref="Texture"/> と <see cref="Material"/> の関係とまったく同じ形で、
/// **データと、それを使う人を分ける**。
/// 敵が 30 体同時に死んでも、爆発音のデータは1つしかメモリに載らない。
///
/// この分け方をしないと「同じ音を重ねて鳴らす」ができない。
/// 1つの再生位置しか持てないので、2発目が1発目を頭から巻き戻すことになる。
///
/// バッファの寿命はソースより長くなければいけない。
/// **再生中のバッファを消すと OpenAL は無効な状態になる**ので、
/// <see cref="AudioSystem"/> 側で「止めてから消す」順番を守っている。
/// </summary>
internal sealed class AudioClip : IDisposable
{
    private readonly AL _al;
    private uint _buffer;

    private AudioClip(AL al, uint buffer, WavData wav, string name)
    {
        _al = al;
        _buffer = buffer;
        Name = name;
        SampleRate = wav.SampleRate;
        Channels = wav.Channels;
        BitsPerSample = wav.BitsPerSample;
        Duration = wav.Duration;
        ByteSize = wav.Data.Length;
    }

    public string Name { get; }

    public uint Buffer => _buffer;

    public int SampleRate { get; }

    public int Channels { get; }

    public int BitsPerSample { get; }

    /// <summary>秒。ボイスの寿命を見積もるのに使う。</summary>
    public float Duration { get; }

    public int ByteSize { get; }

    /// <summary>
    /// <b>モノラルかどうか</b>。定位(パン)が効くかどうかを分ける。
    ///
    /// OpenAL は**ステレオのバッファには 3D 定位を適用しない**。
    /// 左右がすでに決まっているものを勝手に動かせないので、当然といえば当然だが、
    /// 「位置を設定したのに真ん中から聞こえる」という形で出るので原因が分かりにくい。
    /// **効果音はモノラルで作る**のが実務上の答えになる。
    /// </summary>
    public bool IsMono => Channels == 1;

    public static AudioClip FromWav(AL al, in WavData wav, string name)
    {
        uint buffer = al.GenBuffer();

        // OpenAL の基本セットは 8bit/16bit の モノ/ステレオ の 4 通りだけ。
        // WavFile 側でここに落ちない形式を弾いてある。
        BufferFormat format = (wav.Channels, wav.BitsPerSample) switch
        {
            (1, 8) => BufferFormat.Mono8,
            (1, 16) => BufferFormat.Mono16,
            (2, 8) => BufferFormat.Stereo8,
            _ => BufferFormat.Stereo16,
        };

        // **ここで GPU ならぬサウンドデバイス側へコピーされる**。
        // 渡した配列はこの後に解放してよい(テクスチャのアップロードと同じ)。
        al.BufferData(buffer, format, wav.Data, wav.SampleRate);

        return new AudioClip(al, buffer, wav, name);
    }

    public void Dispose()
    {
        if (_buffer != 0)
        {
            _al.DeleteBuffer(_buffer);
            _buffer = 0;
        }
    }
}
