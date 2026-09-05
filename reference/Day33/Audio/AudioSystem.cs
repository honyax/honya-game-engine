using Silk.NET.OpenAL;

namespace HonyaEngine;

/// <summary>
/// 「今このボイスを鳴らしている人」を指す札。**<see cref="Handle{T}"/> と同じ発想**。
///
/// ボイスの枠は使い回される。添字だけを配ると、
/// <b>自分の音がとっくに終わって枠が別の音に取られたあと、その別人を止めてしまう</b>。
/// Day 21 でテクスチャのハンドルに世代を持たせたのとまったく同じ問題なので、
/// 同じ手(添字 + 世代)で解く。
///
/// 「鳴らしっぱなしで気にしない」音には <see cref="None"/> を返して構わない。
/// 実際、効果音の 9 割は札を受け取る必要がない。
/// 札が要るのは**あとから止めたり位置を追わせたりするもの**——
/// BGM、ループするエンジン音、詠唱中の音——だけになる。
/// </summary>
internal readonly struct VoiceId : IEquatable<VoiceId>
{
    private readonly int _index;
    private readonly int _generation;

    internal VoiceId(int index, int generation)
    {
        _index = index;
        _generation = generation;
    }

    public static VoiceId None => new(-1, 0);

    public bool IsValid => _index >= 0;

    internal int Index => _index;

    internal int Generation => _generation;

    public bool Equals(VoiceId other) => _index == other._index && _generation == other._generation;

    public override bool Equals(object? obj) => obj is VoiceId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_index, _generation);

    public override string ToString() => IsValid ? $"voice#{_index}.g{_generation}" : "voice#none";
}

/// <summary>
/// 音を鳴らす層。**デバイスとボイスの管理**が仕事。
///
/// OpenAL の登場人物は3つしかない。
///
/// <code>
///   Device   … サウンドカード。1つ開く
///   Context  … 描画でいう GL コンテキスト。カレントにして使う
///   Source   … 「鳴らす人」。バッファを1つくわえて再生する
/// </code>
///
/// 設計で決めたことが4つある。
///
/// <b>1. <see cref="RenderResources"/> に相乗りしない</b>
/// テクスチャと同じように扱えるのだから同居させたくなるが、そうしない。
///
/// Day 27 に書いた理由はこうだった——窓口は当時 <c>Core/ResourceManager</c> という名前で
/// <c>Core/</c> に居て <c>GL</c> を握っていた。そこへ OpenAL を足すと
/// 「<c>Core</c> がグラフィックスと音の両方を知る」ことになり、
/// Day 25 の設計書で書いた <c>Core</c> ⇔ <c>Render</c> の相互参照がもう一段悪くなる、と。
///
/// <b>その理由は Day 31 で消えた</b>。窓口を <c>Render/</c> へ上げたので、もう <c>Core</c> の話ではない。
/// それでも相乗りしないのは、単に層が違うから——音は <c>Audio/</c> の持ち物で、
/// 描画の窓口に間借りする理由が無い。
/// **理由のほうが先に消えても、結論は動かなかった**。Day 27 の判断はそれだけ筋が良かった、ということになる。
///
/// 代わりに <see cref="ResourcePool{T}"/>(これは何も知らない総称型)だけを借りて、
/// 音のリソースはこのクラスが自分で持つ。
///
/// <b>2. デバイスが無くても動く</b>
/// 音が出ない環境(リモートデスクトップ、サウンドを持たないサーバ、
/// ドライバが死んでいるとき)はいくらでもある。
/// そこで落ちるゲームは論外なので、<see cref="IsAvailable"/> が <c>false</c> のときは
/// **全部の操作が黙って何もしない**ようにしてある。
/// 呼び出し側に <c>if (audio != null)</c> を書かせない、という方針。
///
/// <b>3. ボイスは前もって作って使い回す</b>
/// ソースの生成は安くないうえ、**同時に作れる数には上限がある**
/// (実装依存。OpenAL Soft の既定は 256)。
/// 敵が 200 体死ぬたびに <c>GenSource</c> を呼んでいたら、いつか枯れる。
/// 起動時に固定数だけ作り、空きが無ければ**奪う**(要点4)。
///
/// <b>4. 1ステップあたりの発音数を絞る</b>
/// これが無いと、Day 26 で 2 万体を動かせるようにした結果として
/// **1ステップに数百回の再生要求**が飛んでくる。
/// 音として意味が無いだけでなく、同じ波形が位相をそろえて重なるので
/// 音量が線形に足されて割れる(要点5)。
/// </summary>
internal sealed unsafe class AudioSystem : IDisposable
{
    private readonly ALContext? _alc;
    private readonly AL? _al;
    private readonly Device* _device;
    private readonly Context* _context;

    private readonly ResourcePool<AudioClip> _clips = new();
    private readonly Dictionary<string, Handle<AudioClip>> _clipByPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly Voice[] _voices;
    private readonly Dictionary<Handle<AudioClip>, int> _startsThisStep = [];
    private readonly Random _random = new(27);

    /// <summary>再生を始めた順番。**奪う相手を選ぶ**ときに、古いものから潰すために使う。</summary>
    private long _startCounter;

    private int _startedThisStep;
    private int _culledThisStep;
    private int _stolenThisStep;

    private struct Voice
    {
        public uint Source;
        public int Generation;
        public bool Active;
        public bool Looping;
        public int Priority;
        public long StartedAt;
        public Handle<AudioClip> Clip;
    }

    public AudioSystem(int voiceCount = 32)
    {
        VoiceCount = voiceCount;
        _voices = new Voice[voiceCount];

        try
        {
            _alc = ALContext.GetApi(soft: true);
            _al = AL.GetApi(soft: true);

            // 空文字列は「既定のデバイス」。名前を指定すれば特定の出力先を開ける
            // (ゲームの設定画面で出力先を選ばせるならここ)。
            _device = _alc.OpenDevice(string.Empty);

            if (_device is null)
            {
                Dispose();
                return;
            }

            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);

            // **描画の GL と同じで、コンテキストは「カレント」の概念を持つ**。
            // これを忘れると、以降の呼び出しが全部無効になる。
            DeviceName = _al.GetStateProperty(StateString.Renderer) ?? "(不明)";
            Version = _al.GetStateProperty(StateString.Version) ?? "(不明)";

            for (int i = 0; i < voiceCount; i++)
            {
                _voices[i].Source = _al.GenSource();
                _voices[i].Generation = 1;

                // **すべてのソースをリスナー基準にする**。
                // 見下ろし型の 2D ゲームでは、ワールド座標をそのまま使うより
                // 「聞いている人から見た向き」で置くほうが素直
                // (カメラが動いても音の左右が勝手に変わらない)。
                _al.SetSourceProperty(_voices[i].Source, SourceBoolean.SourceRelative, true);
            }

            IsAvailable = CheckError("初期化");
        }
        catch (Exception exception)
        {
            // OpenAL のネイティブライブラリが見つからない場合はここに来る。
            // **音が出ないだけで、ゲームは続く**。
            Console.WriteLine($"[audio] 初期化に失敗しました: {exception.Message}");
            IsAvailable = false;
        }
    }

    /// <summary>音が出せるか。<c>false</c> なら全部の操作が黙って何もしない。</summary>
    public bool IsAvailable { get; private set; }

    public string DeviceName { get; private set; } = "(なし)";

    public string Version { get; private set; } = "(なし)";

    /// <summary>用意したボイスの数。**同時に鳴らせる上限**。</summary>
    public int VoiceCount { get; }

    /// <summary>いま鳴っているボイスの数。</summary>
    public int ActiveVoices { get; private set; }

    public int ClipCount => _clips.AliveCount;

    /// <summary>
    /// 全体の音量。<b>リスナーの利得</b>として効かせるので、
    /// すでに鳴っている音にも即座にかかる。
    /// ソースごとに掛け直す実装だと、鳴っている最中のものが変わらない。
    /// </summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0.0f, 1.0f);
            _al?.SetListenerProperty(ListenerFloat.Gain, _masterVolume);
        }
    }

    private float _masterVolume = 0.6f;

    /// <summary>ピッチをわずかに揺らすか(要点5)。</summary>
    public bool PitchVariation { get; set; } = true;

    /// <summary>揺らす幅。±6% くらいが「同じ音だが機械的でない」の境目。</summary>
    public float PitchJitter { get; set; } = 0.06f;

    /// <summary>
    /// 1ステップに、同じクリップを何回まで鳴らし始めてよいか。0 で無制限。
    /// **4 くらいから上は、増やしても音として区別できない**。
    /// </summary>
    public int MaxStartsPerClipPerStep { get; set; } = 4;

    public int StartedLastStep { get; private set; }

    public int CulledLastStep { get; private set; }

    public int StolenLastStep { get; private set; }

    public int RequestedLastStep => StartedLastStep + CulledLastStep;

    /// <summary>
    /// WAV を読み込む。**同じパスは使い回す**(Day 21 のキャッシュと同じ)。
    /// </summary>
    public Handle<AudioClip> Load(string path)
    {
        if (!IsAvailable || _al is null)
        {
            return Handle<AudioClip>.None;
        }

        if (_clipByPath.TryGetValue(path, out Handle<AudioClip> cached) && _clips.IsAlive(cached))
        {
            return cached;
        }

        WavData wav = WavFile.Load(path);
        AudioClip clip = AudioClip.FromWav(_al, wav, Path.GetFileNameWithoutExtension(path));
        Handle<AudioClip> handle = _clips.Add(clip);
        _clipByPath[path] = handle;
        return handle;
    }

    public AudioClip? TryGet(Handle<AudioClip> handle) =>
        _clips.TryGet(handle, out AudioClip? clip) ? clip : null;

    /// <summary>
    /// 1ステップの頭で呼ぶ。**終わったボイスを回収し、発音の予算を戻す**。
    ///
    /// 「終わったかどうか」は OpenAL に聞くしかない
    /// (再生時間から計算すると、ピッチを変えたぶんずれる)。
    /// ボイスの数だけ問い合わせるが、32 回程度なら実測 0.01ms 未満で収まる。
    /// </summary>
    public void Update()
    {
        StartedLastStep = _startedThisStep;
        CulledLastStep = _culledThisStep;
        StolenLastStep = _stolenThisStep;

        _startedThisStep = 0;
        _culledThisStep = 0;
        _stolenThisStep = 0;
        _startsThisStep.Clear();

        if (!IsAvailable || _al is null)
        {
            return;
        }

        int active = 0;
        for (int i = 0; i < _voices.Length; i++)
        {
            if (!_voices[i].Active)
            {
                continue;
            }

            _al.GetSourceProperty(_voices[i].Source, GetSourceInteger.SourceState, out int state);

            if ((SourceState)state == SourceState.Stopped)
            {
                ReleaseVoice(i);
            }
            else
            {
                active++;
            }
        }

        ActiveVoices = active;
    }

    /// <summary>
    /// 鳴らす。<b>鳴らせなかったときは <see cref="VoiceId.None"/> を返すだけで、例外は投げない</b>。
    ///
    /// 音が鳴らないのはゲームを止める理由にならない。
    /// ここで例外を投げると、「音が多すぎたときだけ落ちる」という
    /// 再現しにくいバグを自分で仕込むことになる。
    /// </summary>
    /// <param name="pan">-1(左)〜 +1(右)。<b>モノラルのクリップにしか効かない</b>。</param>
    /// <param name="priority">大きいほど優先。ボイスが足りないときに残る側。</param>
    public VoiceId Play(
        Handle<AudioClip> clip,
        float volume = 1.0f,
        float pitch = 1.0f,
        float pan = 0.0f,
        int priority = 0,
        bool looping = false)
    {
        if (!IsAvailable || _al is null)
        {
            return VoiceId.None;
        }

        if (!_clips.TryGet(clip, out AudioClip? data))
        {
            return VoiceId.None;
        }

        // --- 1ステップあたりの発音数で絞る(要点5)---
        //
        // ループするものは対象外。BGM が予算切れで鳴らないと困る。
        if (!looping && MaxStartsPerClipPerStep > 0)
        {
            _startsThisStep.TryGetValue(clip, out int started);
            if (started >= MaxStartsPerClipPerStep)
            {
                _culledThisStep++;
                return VoiceId.None;
            }

            _startsThisStep[clip] = started + 1;
        }

        int index = AcquireVoice(priority, looping);
        if (index < 0)
        {
            _culledThisStep++;
            return VoiceId.None;
        }

        ref Voice voice = ref _voices[index];
        uint source = voice.Source;

        _al.SetSourceProperty(source, SourceInteger.Buffer, data.Buffer);
        _al.SetSourceProperty(source, SourceFloat.Gain, Math.Clamp(volume, 0.0f, 1.0f));
        _al.SetSourceProperty(source, SourceBoolean.Looping, looping);

        // **ピッチを揺らす**。同じ波形がぴったり重なると、
        // 音が大きくなるだけで「たくさん鳴っている」感じにならない(要点5)。
        float finalPitch = pitch;
        if (PitchVariation)
        {
            finalPitch *= 1.0f + (((float)_random.NextDouble() - 0.5f) * 2.0f * PitchJitter);
        }

        // 0 以下や極端な値を渡すと OpenAL がエラーを返す。
        _al.SetSourceProperty(source, SourceFloat.Pitch, Math.Clamp(finalPitch, 0.25f, 4.0f));

        // **リスナーからの距離を 1 に保ったまま左右へ振る**。
        // 単に x を動かすと、端へ行くほど距離が伸びて小さくなる。
        // 円周上に置けば、距離減衰の影響を受けずに定位だけが変わる。
        float x = Math.Clamp(pan, -1.0f, 1.0f);
        float z = -MathF.Sqrt(MathF.Max(0.0f, 1.0f - (x * x)));
        _al.SetSourceProperty(source, SourceVector3.Position, x, 0.0f, z);

        _al.SourcePlay(source);

        voice.Active = true;
        voice.Looping = looping;
        voice.Priority = priority;
        voice.StartedAt = ++_startCounter;
        voice.Clip = clip;

        _startedThisStep++;
        ActiveVoices++;

        return new VoiceId(index, voice.Generation);
    }

    public VoiceId PlayLoop(Handle<AudioClip> clip, float volume = 1.0f) =>

        // ループは奪われては困るので、優先度を上げておく。
        Play(clip, volume, 1.0f, 0.0f, priority: 100, looping: true);

    public bool IsPlaying(VoiceId voice)
    {
        if (!TryResolve(voice, out int index) || _al is null)
        {
            return false;
        }

        _al.GetSourceProperty(_voices[index].Source, GetSourceInteger.SourceState, out int state);
        return (SourceState)state == SourceState.Playing;
    }

    public void Stop(VoiceId voice)
    {
        if (!TryResolve(voice, out int index))
        {
            return;
        }

        StopVoice(index);
        ReleaseVoice(index);
        ActiveVoices = Math.Max(0, ActiveVoices - 1);
    }

    public void StopAll()
    {
        for (int i = 0; i < _voices.Length; i++)
        {
            if (_voices[i].Active)
            {
                StopVoice(i);
                ReleaseVoice(i);
            }
        }

        ActiveVoices = 0;
    }

    /// <summary>
    /// 空いているボイスを取る。無ければ**奪う**。
    ///
    /// 奪う相手の選び方が、そのままゲームの手触りになる。ここでは
    ///   1. ループしているものは奪わない(BGM を消さない)
    ///   2. 優先度がいちばん低いもの
    ///   3. 同じ優先度なら、いちばん古くから鳴っているもの
    /// の順に選ぶ。3 が効くのは、**古い音ほど「もう聞こえた」から**。
    ///
    /// 実務ではもう1つ「今どれくらい小さく鳴っているか」を見ることが多い。
    /// 遠くの小さな音を先に消したほうが、消えたことに気づかれにくい。
    /// </summary>
    private int AcquireVoice(int priority, bool looping)
    {
        for (int i = 0; i < _voices.Length; i++)
        {
            if (!_voices[i].Active)
            {
                return i;
            }
        }

        int victim = -1;
        for (int i = 0; i < _voices.Length; i++)
        {
            if (_voices[i].Looping)
            {
                continue;
            }

            if (victim < 0
                || _voices[i].Priority < _voices[victim].Priority
                || (_voices[i].Priority == _voices[victim].Priority
                    && _voices[i].StartedAt < _voices[victim].StartedAt))
            {
                victim = i;
            }
        }

        // 全部が自分より偉い(あるいは全部ループ)なら、**新しいほうを諦める**。
        // ここで無理に奪うと、鳴り始めた瞬間に別の音に消される音が量産される。
        if (victim < 0 || (_voices[victim].Priority > priority && !looping))
        {
            return -1;
        }

        StopVoice(victim);
        ReleaseVoice(victim);
        _stolenThisStep++;
        ActiveVoices = Math.Max(0, ActiveVoices - 1);
        return victim;
    }

    private void StopVoice(int index)
    {
        if (_al is null)
        {
            return;
        }

        _al.SourceStop(_voices[index].Source);

        // **バッファを外してから次に使う**。
        // 付けたままだと、次に別のバッファを差すときに
        // 「再生中のソースには設定できない」と怒られることがある。
        _al.SetSourceProperty(_voices[index].Source, SourceInteger.Buffer, 0);
    }

    /// <summary>
    /// ボイスの枠を空きに戻す。**世代を1つ進める**ので、
    /// 前の持ち主が握っている <see cref="VoiceId"/> はここで無効になる。
    /// </summary>
    private void ReleaseVoice(int index)
    {
        _voices[index].Active = false;
        _voices[index].Looping = false;
        _voices[index].Clip = Handle<AudioClip>.None;
        _voices[index].Generation++;
    }

    private bool TryResolve(VoiceId voice, out int index)
    {
        index = voice.Index;

        return IsAvailable
            && voice.IsValid
            && index < _voices.Length
            && _voices[index].Active
            && _voices[index].Generation == voice.Generation;
    }

    /// <summary>
    /// 直前の呼び出しでエラーが出ていないか。**OpenGL と同じ「べっとりしたフラグ」方式**で、
    /// 読み出すまで残り続け、読むと消える。だから
    /// 「どこで起きたか」を知るには要所で読むしかない。
    /// </summary>
    private bool CheckError(string where)
    {
        if (_al is null)
        {
            return false;
        }

        AudioError error = _al.GetError();
        if (error == AudioError.NoError)
        {
            return true;
        }

        Console.WriteLine($"[audio] {where}: {error}");
        return false;
    }

    public void Dispose()
    {
        if (_al is not null)
        {
            StopAll();

            for (int i = 0; i < _voices.Length; i++)
            {
                if (_voices[i].Source != 0)
                {
                    _al.DeleteSource(_voices[i].Source);
                    _voices[i].Source = 0;
                }
            }

            // **ソースを消してからバッファを消す**。
            // 逆にすると、再生中のバッファを消すことになる。
            foreach (AudioClip clip in _clips.AliveValues)
            {
                clip.Dispose();
            }
        }

        if (_alc is not null)
        {
            _alc.MakeContextCurrent(null);

            if (_context is not null)
            {
                _alc.DestroyContext(_context);
            }

            if (_device is not null)
            {
                _alc.CloseDevice(_device);
            }
        }

        IsAvailable = false;
    }
}
