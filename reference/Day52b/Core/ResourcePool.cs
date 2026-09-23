using System.Diagnostics.CodeAnalysis;

namespace HonyaEngine;

/// <summary>
/// <see cref="Handle{T}"/> の指す先を実際に持っている入れ物。
///
/// やっていることは**配列 + 空きリスト + 世代番号**の3つだけ。
/// この3つが噛み合うと「安全に再利用できる添字」になる。
///
/// **空きリストを配列の中に通す**
/// 解放したスロットは、次に使えるように覚えておく必要がある。
/// 別に <c>Queue&lt;int&gt;</c> を持ってもいいが、
/// **空いているスロットの <c>Value</c> はどうせ null で場所が余っている**ので、
/// そこに「次の空き」の添字を書いて数珠つなぎにする。追加の確保がゼロで済む。
///
/// **参照カウント**
/// 同じテクスチャを複数のマテリアルが使うのは普通のことなので、
/// 誰か1人が要らなくなっただけで消えては困る。
/// <see cref="Retain"/> で +1、<see cref="Release"/> で -1 し、
/// **0 になった瞬間だけ**スロットを空ける。
/// これで <see cref="Material"/> の「破棄の責任を持たない」という宙ぶらりんが解消する
/// ——責任はプールにあり、利用者は要求と返却だけを申告する。
///
/// なお、参照カウントは循環参照に弱い(A が B を、B が A を持つと永遠に 0 にならない)。
/// リソースは他のリソースを持たないことが多いので今回は問題にならないが、
/// **ゲームオブジェクトの寿命管理に同じ手を使うと詰まる**。Day 22 で別の手を使う。
/// </summary>
internal sealed class ResourcePool<T>
    where T : class
{
    private struct Slot
    {
        /// <summary>中身。空きスロットでは null。</summary>
        public T? Value;

        /// <summary>
        /// このスロットが今「何代目」か。
        /// 0 は「まだ一度も使われていない」を表す(<see cref="Handle{T}.IsValid"/> 参照)。
        /// </summary>
        public uint Generation;

        public int RefCount;

        /// <summary>空きリストの次の添字。終端は -1。使用中のスロットでは意味を持たない。</summary>
        public int NextFree;
    }

    private Slot[] _slots = new Slot[8];

    /// <summary>今までに一度でも使った添字の数。配列の「有効な範囲」。</summary>
    private int _used;

    private int _freeHead = -1;

    /// <summary>今生きているリソースの数。</summary>
    public int AliveCount { get; private set; }

    /// <summary>確保済みのスロット数(解放済みの空きスロットも含む)。</summary>
    public int SlotCount => _used;

    /// <summary>
    /// 中身を登録して、ハンドルを返す。参照カウントは 1 から始まる。
    /// </summary>
    public Handle<T> Add(T value)
    {
        int index;
        if (_freeHead >= 0)
        {
            // 空きがあれば再利用する。**メモリの節約というより、添字を詰めるため**。
            // 添字が際限なく増えると 24 ビットを使い切るし、
            // あとで「全リソースを走査する」ときに空振りが増える。
            index = _freeHead;
            _freeHead = _slots[index].NextFree;
        }
        else
        {
            if (_used == _slots.Length)
            {
                Array.Resize(ref _slots, _slots.Length * 2);
            }

            index = _used++;
        }

        if (index > Handle<T>.MaxIndex)
        {
            throw new InvalidOperationException($"スロットが {Handle<T>.MaxIndex} 個を超えました");
        }

        ref Slot slot = ref _slots[index];

        // 世代は**解放時に進める**(Release 参照)ので、ここでは触らない。
        // 例外は初回だけ。0 のままだと無効ハンドルになってしまうので 1 から始める。
        if (slot.Generation == 0)
        {
            slot.Generation = 1;
        }

        slot.Value = value;
        slot.RefCount = 1;
        slot.NextFree = -1;
        AliveCount++;

        return new Handle<T>(index, slot.Generation);
    }

    /// <summary>
    /// ハンドルから中身を引く。**これが間接参照の実体**で、たった数命令。
    ///
    /// 添字の範囲外や世代違いは静かに false を返す。
    /// 「無効なハンドルを引く」は異常ではなく**普通に起きること**だから
    /// (非同期ロードの最中、解放の直後、シリアライズしたシーンの読み込み時)。
    /// </summary>
    public bool TryGet(Handle<T> handle, [NotNullWhen(true)] out T? value)
    {
        if (handle.IsValid && (uint)handle.Index < (uint)_used)
        {
            ref Slot slot = ref _slots[handle.Index];
            if (slot.Generation == handle.Generation && slot.Value is not null)
            {
                value = slot.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>中身を引く。無効なら例外。「絶対にあるはず」の場所でだけ使う。</summary>
    public T Get(Handle<T> handle)
    {
        return TryGet(handle, out T? value)
            ? value
            : throw new InvalidOperationException($"無効なハンドルです: {handle}");
    }

    public bool IsAlive(Handle<T> handle) => TryGet(handle, out _);

    /// <summary>参照カウント。デバッグ表示用。</summary>
    public int RefCountOf(Handle<T> handle) => TryGetIndex(handle, out int index) ? _slots[index].RefCount : 0;

    /// <summary>参照カウントを +1 する。「これも使うので消さないでほしい」の申告。</summary>
    public bool Retain(Handle<T> handle)
    {
        if (!TryGetIndex(handle, out int index))
        {
            return false;
        }

        _slots[index].RefCount++;
        return true;
    }

    /// <summary>
    /// 参照カウントを -1 する。0 になったらスロットを空け、外していた中身を
    /// <paramref name="removed"/> に入れて true を返す。
    ///
    /// **中身を破棄するのは呼び出し側**にしてある。
    /// プールは <c>IDisposable</c> かどうかを知らないし、
    /// GPU リソースの破棄はスレッドを選ぶ(GL の呼び出しは描画スレッド限定)。
    /// 「いつ・どこで捨てるか」の判断は上の層に残しておきたい。
    /// </summary>
    public bool Release(Handle<T> handle, out T? removed)
    {
        removed = null;
        if (!TryGetIndex(handle, out int index))
        {
            return false;
        }

        ref Slot slot = ref _slots[index];
        slot.RefCount--;
        if (slot.RefCount > 0)
        {
            return false;
        }

        removed = slot.Value;
        slot.Value = null;

        // **ここで世代を進める**。この1行で、このスロットを指していた
        // ハンドルが全部いっぺんに無効になる。
        // 「再利用のときに進める」ではなく「解放のときに進める」のが要点で、
        // そうしないと**解放後・再利用前**の隙間で古いハンドルが通ってしまう。
        slot.Generation = NextGeneration(slot.Generation);

        slot.NextFree = _freeHead;
        _freeHead = index;
        AliveCount--;
        return true;
    }

    /// <summary>
    /// ハンドルはそのままに、中身だけ差し替える。
    ///
    /// **ハンドルを使う最大の理由がこれ**。非同期ロードでは
    /// 「先に仮の絵を入れたハンドルを返し、読み終わったら本物と入れ替える」ことをする。
    /// 参照を配っていたら配った先を全部探して回る必要があるが、
    /// 間接参照なら**ここ1箇所**を書き換えれば全員が新しいほうを見る。
    /// </summary>
    public bool Replace(Handle<T> handle, T value, out T? previous)
    {
        previous = null;
        if (!TryGetIndex(handle, out int index))
        {
            return false;
        }

        previous = _slots[index].Value;
        _slots[index].Value = value;
        return true;
    }

    /// <summary>生きている中身を全部返す。終了時の後片付け用。</summary>
    public IEnumerable<T> AliveValues
    {
        get
        {
            for (int i = 0; i < _used; i++)
            {
                if (_slots[i].Value is { } value)
                {
                    yield return value;
                }
            }
        }
    }

    private bool TryGetIndex(Handle<T> handle, out int index)
    {
        index = handle.Index;
        return handle.IsValid
            && (uint)index < (uint)_used
            && _slots[index].Generation == handle.Generation
            && _slots[index].Value is not null;
    }

    /// <summary>
    /// 次の世代番号。0 は無効の予約なので飛ばす。
    ///
    /// 8 ビットなので **255 回再利用すると一周する**。
    /// その時点で「255 代前のハンドル」を握ったままの相手がいると、
    /// 世代が一致してしまい、ハンドルが蘇る。
    /// 実用上まず起きないが、起きたら発見はほぼ不可能なので、
    /// 気になるなら世代を 16 ビットにして添字を 16 ビット(65536 個)に削る。
    /// **ビット配分は「同時に持てる個数」と「安全に再利用できる回数」の綱引き**で、
    /// どちらが厳しいかはリソースの種類によって違う。
    /// </summary>
    private static uint NextGeneration(uint current)
    {
        uint next = current + 1;
        return next > Handle<T>.MaxGeneration ? 1u : next;
    }
}
