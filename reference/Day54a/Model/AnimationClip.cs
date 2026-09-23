using System.Numerics;

namespace HonyaEngine;

/// <summary>アニメーションが書き換えるノードの属性。</summary>
internal enum AnimationPath
{
    Translation,
    Rotation,
    Scale,

    /// <summary>モーフターゲットの重み。**今日は未対応**(読み飛ばして知らせる)。</summary>
    Weights,
}

/// <summary>キーとキーの間をどう埋めるか。</summary>
internal enum AnimationInterpolation
{
    /// <summary>直線で結ぶ。回転だけは球面線形補間(slerp)になる。</summary>
    Linear,

    /// <summary>次のキーまで前の値を保つ。**階段状**。ロボットの動きや、旗のパタパタに使う。</summary>
    Step,

    /// <summary>
    /// 三次エルミート曲線。キー1つにつき「入り接線・値・出接線」の3つ組が入る。
    /// **今日は値だけを拾って Linear として扱う**(改造課題2)。
    /// </summary>
    CubicSpline,
}

/// <summary>
/// アニメーションのクリップ1本(Day 41)。「歩く」「走る」といったひとまとまり。
///
/// <para>
/// <b>glTF のアニメーションは2階建て</b>で、そこさえ掴めば構造は単純。
/// <code>
///   channel … 「どのノードの、どの属性を」+ どの sampler を使うか
///       ↓
///   sampler … 「いつ(input)」と「どんな値(output)」の2本のアクセサ + 補間の種類
/// </code>
/// 分かれているのは、**同じ時間軸を複数のチャンネルで共有できる**ようにするため。
/// CesiumMan は 19 個の関節 × 3属性 = 57 チャンネルあるが、
/// 時間の配列は共有されているので、ファイルはそのぶん小さくなる。
/// </para>
///
/// <para>
/// <b>行列は1つも出てこない</b>のがアニメーションデータの特徴。
/// 入っているのは「時刻 t でのノードの平行移動・回転・拡大」だけで、
/// 行列に畳むのは再生する側の仕事(<see cref="AnimationPlayer"/>)。
/// この分担のおかげで、2本のクリップを混ぜる(Day 42)ことができる——
/// **TRS は混ぜられるが、行列は混ぜられない**(<see cref="NodePose"/> のコメント)。
/// </para>
/// </summary>
internal sealed class AnimationClip
{
    public AnimationClip(string name, IReadOnlyList<Channel> channels)
    {
        Name = name;
        Channels = channels;

        float duration = 0.0f;
        foreach (Channel channel in channels)
        {
            duration = MathF.Max(duration, channel.Sampler.EndTime);
        }

        // **長さ 0 のクリップを作らない**。0 で割る場所(ループの剰余)が出るのと、
        // 「再生しているのに何も起きない」を長さ 0 として片付けられるようにするため。
        Duration = MathF.Max(duration, 1e-4f);
    }

    /// <summary>クリップ名。glTF の <c>animation.name</c>。無ければ "anim0" のような仮の名。</summary>
    public string Name { get; }

    /// <summary>いちばん遅いキーの時刻(秒)。**ループの周期そのもの**。</summary>
    public float Duration { get; }

    public IReadOnlyList<Channel> Channels { get; }

    /// <summary>
    /// 時刻 <paramref name="time"/> の姿勢を <paramref name="pose"/> に書き込む。
    ///
    /// <b>触らないノードには何も書かない</b>のが肝。呼ぶ側が「まず全ノードを
    /// ファイルの姿勢に戻してから」呼ぶ約束にしてあり(<c>AnimationPlayer.Evaluate</c>)、
    /// アニメーションが持っていないノードはその値のまま残る。
    ///
    /// ここを「毎回ゼロクリアしてから書く」にすると、
    /// **回転しか持たないノードの平行移動が原点に飛ぶ**。
    /// Fox がまさにそれで、21 チャンネルのうち 20 本が回転だけを持っている。
    /// </summary>
    public void Apply(float time, Span<NodePose> pose)
    {
        foreach (Channel channel in Channels)
        {
            if ((uint)channel.Node >= (uint)pose.Length)
            {
                continue;
            }

            switch (channel.Path)
            {
                case AnimationPath.Translation:
                    pose[channel.Node].Translation = channel.Sampler.SampleVector3(time);
                    break;

                case AnimationPath.Rotation:
                    pose[channel.Node].Rotation = channel.Sampler.SampleRotation(time);
                    break;

                case AnimationPath.Scale:
                    pose[channel.Node].Scale = channel.Sampler.SampleVector3(time);
                    break;

                default:
                    // Weights(モーフ)。読み込み時に知らせてあるので、ここでは黙って飛ばす。
                    break;
            }
        }
    }

    /// <summary>「どのノードの、どの属性を、どの sampler で」。</summary>
    /// <param name="Node">対象のノード番号。</param>
    /// <param name="Path">書き換える属性。</param>
    /// <param name="Sampler">値の出どころ。</param>
    internal readonly record struct Channel(int Node, AnimationPath Path, Sampler Sampler);

    /// <summary>
    /// 時間 → 値の対応表。**キーフレームの本体**。
    ///
    /// <para>
    /// <b>値をすべて <see cref="Vector4"/> で持つ</b>。平行移動と拡大は3成分しか使わない
    /// (W は 0)が、回転(4成分)と型を揃えておくと、
    /// キーを取り出す添字の計算を1か所で書ける。
    /// 1キーあたり 4 バイト無駄になるが、CesiumMan で 57 チャンネル × 数十キーなので、
    /// 全部合わせても数十 KB。**読みやすさのほうが高くつく規模**。
    /// </para>
    /// </summary>
    internal sealed class Sampler
    {
        private readonly float[] _times;
        private readonly Vector4[] _values;

        /// <summary>CUBICSPLINE は 1 キーにつき値が3つ(入り接線・値・出接線)並ぶ。</summary>
        private readonly int _stride;

        /// <summary>3つ組のうち「値そのもの」の位置。CUBICSPLINE なら 1、それ以外は 0。</summary>
        private readonly int _offset;

        public Sampler(float[] times, Vector4[] values, AnimationInterpolation interpolation)
        {
            _times = times;
            _values = values;
            Interpolation = interpolation;

            _stride = interpolation == AnimationInterpolation.CubicSpline ? 3 : 1;
            _offset = interpolation == AnimationInterpolation.CubicSpline ? 1 : 0;
        }

        public AnimationInterpolation Interpolation { get; }

        public int KeyCount => _times.Length;

        /// <summary>いちばん遅いキーの時刻。クリップの長さを決めるのに使う。</summary>
        public float EndTime => _times.Length == 0 ? 0.0f : _times[^1];

        public Vector3 SampleVector3(float time)
        {
            if (_times.Length == 0)
            {
                return Vector3.Zero;
            }

            (int index0, int index1, float t) = Locate(time);

            Vector4 a = Key(index0);
            var v0 = new Vector3(a.X, a.Y, a.Z);

            if (Interpolation == AnimationInterpolation.Step)
            {
                return v0;
            }

            Vector4 b = Key(index1);
            return Vector3.Lerp(v0, new Vector3(b.X, b.Y, b.Z), t);
        }

        /// <summary>
        /// 回転を取り出す。**ここだけ lerp ではなく slerp**。
        ///
        /// 成分ごとに線形補間すると、途中のクォータニオンの長さが 1 でなくなる。
        /// 正規化し直せば向きとしては使えるが、**回る速さが一定にならない**
        /// (中間で速く、両端で遅くなる)。90 度以上離れたキーで目に見えて出る。
        ///
        /// <see cref="Quaternion.Slerp"/> は内積が負なら片方を反転してから補間するので、
        /// **遠回り(360 度近く回る)になる問題もこちらで面倒を見てくれる**。
        /// q と -q は同じ向きを表すのに、素直に補間すると長いほうを通ってしまう——
        /// 自作するときにいちばん踏むのがこれ。
        /// </summary>
        public Quaternion SampleRotation(float time)
        {
            if (_times.Length == 0)
            {
                return Quaternion.Identity;
            }

            (int index0, int index1, float t) = Locate(time);

            Vector4 a = Key(index0);
            var q0 = new Quaternion(a.X, a.Y, a.Z, a.W);

            if (Interpolation == AnimationInterpolation.Step)
            {
                return Quaternion.Normalize(q0);
            }

            Vector4 b = Key(index1);
            var q1 = new Quaternion(b.X, b.Y, b.Z, b.W);

            return Quaternion.Normalize(Quaternion.Slerp(q0, q1, t));
        }

        /// <summary>キー <paramref name="index"/> の「値そのもの」。CUBICSPLINE の接線は飛ばす。</summary>
        private Vector4 Key(int index) => _values[(index * _stride) + _offset];

        /// <summary>
        /// 時刻を挟む2つのキーと、その間の位置(0〜1)を返す。
        ///
        /// <para>
        /// <b>両端は伸ばす(clamp)</b>。仕様どおりで、最初のキーより前は最初の値、
        /// 最後のキーより後は最後の値になる。ループは呼ぶ側(<c>AnimationPlayer</c>)が
        /// 時刻を折り返して作るので、ここでは折り返さない。
        /// </para>
        ///
        /// <para>
        /// <b>二分探索にしてある</b>。CesiumMan のキーは 1 チャンネルあたり数十個なので
        /// 線形に舐めても大差ないが、キーが数百個ある長いクリップだと
        /// **チャンネル数 × キー数**が毎フレーム乗る。
        /// 実際のエンジンは「前フレームの位置から1つ進むだけ」で済ませることが多く、
        /// そのほうがさらに速いが、逆再生やシークで壊れやすい。
        /// </para>
        /// </summary>
        private (int Index0, int Index1, float T) Locate(float time)
        {
            if (_times.Length <= 1 || time <= _times[0])
            {
                return (0, 0, 0.0f);
            }

            int last = _times.Length - 1;
            if (time >= _times[last])
            {
                return (last, last, 0.0f);
            }

            // _times[low] <= time < _times[high] を保ったまま幅を詰める。
            int low = 0;
            int high = last;
            while (low + 1 < high)
            {
                int middle = (low + high) / 2;
                if (_times[middle] <= time)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            float span = _times[high] - _times[low];

            // 同じ時刻のキーが2つ並んでいることがある(切り替えを鋭くするための書き方)。
            // 0 で割らないようにしておく。
            return (low, high, span > 1e-9f ? (time - _times[low]) / span : 0.0f);
        }
    }
}
