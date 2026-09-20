namespace HonyaEngine;

/// <summary>記録・再生の状態。</summary>
internal enum RecorderMode
{
    Off,
    Recording,
    Replaying,
}

/// <summary>
/// 入力を1ステップぶんずつ記録し、あとで同じ順に流し直す。**リプレイ**。
///
/// 大がかりな機能に見えるが、Day 19 の固定タイムステップと
/// 今日の <see cref="InputSnapshot"/> がそろっていれば**ほとんど何もしていない**。
/// 溜めて、順に返すだけ。
///
///   ゲームの状態(t+1) = f(ゲームの状態(t), 入力(t))
///
/// f が決定的(Day 19 要点7)で、入力が全部記録されているなら、
/// 初期状態から同じ入力を流せば必ず同じ結果になる。
/// だから**画面を録画する必要が無い**——入力列だけあれば再現できる。
/// 1時間のプレイでも、60Hz × 3600秒 × 20バイト = 4MB ほど。
///
/// この性質はリプレイだけでなく、
///   - ロールバック方式のネットワーク対戦(過去に戻って入力を差し替え、やり直す)
///   - タイムアタックの記録検証(サーバで再生して不正を検出する)
///   - バグの再現(「この入力列で落ちる」を添付できる)
/// の土台になる。**エンジンの設計が決定的であることの配当**がここで返ってくる。
/// </summary>
internal sealed class InputRecorder
{
    private readonly List<InputSnapshot> _frames = [];
    private int _playHead;

    public RecorderMode Mode { get; private set; } = RecorderMode.Off;

    /// <summary>記録されているステップ数。</summary>
    public int Count => _frames.Count;

    /// <summary>再生中に、今何ステップ目を流しているか。</summary>
    public int PlayHead => _playHead;

    /// <summary>
    /// 記録時のステップ間隔。
    ///
    /// **再生時にこれが違うと結果が一致しない**。入力列が同じでも
    /// 1ステップの重みが変われば別のシミュレーションになる。
    /// 記録データにはこの手の「前提条件」を必ず一緒に入れておく
    /// (実際のリプレイ形式にはさらにゲームのバージョンや乱数の種が入る)。
    /// </summary>
    public double FixedDeltaTime { get; private set; }

    public void StartRecording(double fixedDeltaTime)
    {
        _frames.Clear();
        _playHead = 0;
        FixedDeltaTime = fixedDeltaTime;
        Mode = RecorderMode.Recording;
    }

    public void StopRecording() => Mode = RecorderMode.Off;

    /// <summary>再生を始める。記録が無ければ何もしない。</summary>
    public bool StartReplaying()
    {
        if (_frames.Count == 0)
        {
            return false;
        }

        _playHead = 0;
        Mode = RecorderMode.Replaying;
        return true;
    }

    public void Stop()
    {
        Mode = RecorderMode.Off;
        _playHead = 0;
    }

    /// <summary>記録中なら1ステップぶん溜める。</summary>
    public void Record(in InputSnapshot snapshot)
    {
        if (Mode == RecorderMode.Recording)
        {
            _frames.Add(snapshot);
        }
    }

    /// <summary>
    /// 再生中の次の1ステップを取り出す。末尾まで来たら false を返す。
    /// </summary>
    public bool TryReplay(out InputSnapshot snapshot)
    {
        if (Mode != RecorderMode.Replaying || _playHead >= _frames.Count)
        {
            snapshot = InputSnapshot.Empty;
            return false;
        }

        snapshot = _frames[_playHead];
        _playHead++;
        return true;
    }
}
