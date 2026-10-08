using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 「どのクリップを、どれだけの重みで」1組(Day 42)。
///
/// <b>これがブレンドの語彙のすべて</b>。ブレンドツリーもステートマシンも、
/// 出口はこの組の並びになる。<see cref="AnimationPlayer"/> は
/// 「なぜその重みなのか」を一切知らない——
/// 速度で決めたのか、状態遷移の途中なのか、手で置いたのかは呼ぶ側の都合。
///
/// Day 40 の <c>FeatureToggles</c> が <c>Func&lt;bool&gt;</c> しか知らなかったのと同じ形で、
/// **知識を持たない側に置くほうが、あとから別の決め方を足せる**。
/// </summary>
/// <param name="Clip"><see cref="Model.Animations"/> の添字。</param>
/// <param name="Weight">効き具合。<see cref="AnimationPlayer.SetBlend"/> が合計 1 に正規化する。</param>
internal readonly record struct ClipWeight(int Clip, float Weight);

/// <summary>
/// クリップを再生して、**そのフレームの姿勢**を作る(Day 41)。
/// Day 42 で**複数のクリップを重みで混ぜられる**ようになった。
///
/// <para>
/// <b>モデルと分けてあるのが Day 41 でいちばん大きい設計判断だった</b>。
/// <see cref="Model"/> は読み込んだまま変わらない(頂点・マテリアル・骨格・クリップ)。
/// 変わるのは「今この個体がどのポーズか」だけなので、そちらをこの箱に閉じ込める。
///
/// 分けておくと、キツネを 100 匹並べるとき
/// <b>モデル1つ + プレイヤー 100 個</b>で済む。頂点バッファもテクスチャも共有され、
/// 個体ごとに増えるのは姿勢の配列(ノード数 × 40 バイトほど)だけになる。
/// </para>
///
/// <para>
/// <b>1フレームの流れ</b>は5段。Day 41 の4段に、**混ぜる段**が1つ増えた。
/// <code>
///   1. 参加するクリップごとに、ファイルの姿勢へ戻してから当てる
///   2. 重み付きで足し込む                  PoseAccumulator
///   3. 取り出して _pose を確定             回転は正規化
///   4. 木を根から降りて世界行列            _world[i] = local(i) * _world[parent]
///   5. 関節行列                            joint[j] = IBM[j] * _world[joints[j]]
/// </code>
/// 1〜3 が Day 41 では「クリップ1本を当てるだけ」だったところ。
/// **4 と 5 は1文字も変わっていない**——混ぜる場所を TRS の段に置いたので、
/// 下流から見ると「ポーズが1つ決まった」以上のことは起きていない。
/// </para>
/// </summary>
internal sealed class AnimationPlayer
{
    /// <summary>
    /// シェーダへ送れる関節の上限。**シェーダの <c>uJoints[64]</c> と必ず揃える**。
    ///
    /// 64 なのは OpenGL 3.3 が保証する頂点 uniform の量
    /// (<c>GL_MAX_VERTEX_UNIFORM_COMPONENTS</c> = 1024 float = mat4 64 個)から。
    /// 実際の GPU はもっと多く持っているが、
    /// **他の uniform も同じ枠を食う**ので余裕を見ておく。
    ///
    /// 今日のモデルは CesiumMan 19、Fox 24 なので収まる。
    /// 人型のフルリグ(指まで)は 60〜80 本になるので、
    /// **実用ではここが最初に当たる壁**。逃げ道は3つあり、どれも Phase 8 以降の話。
    ///   - UBO / SSBO に置く(uniform の枠を使わない)
    ///   - 関節行列をテクスチャに書いて頂点シェーダから読む(古くからの定番)
    ///   - メッシュを分割して、使う関節が 64 本以内になるようにする
    /// </summary>
    public const int MaxJoints = 64;

    /// <summary>
    /// 同時に混ぜられるクリップの数(Day 42)。
    ///
    /// **4 で足りる根拠**がある。ブレンドツリーが同時に使うのは隣り合う2本で、
    /// そこへクロスフェードが重なると「前の状態の2本 + 次の状態の2本」で 4 本。
    /// これ以上が要るのは、上半身と下半身を別レイヤーで動かすような場合で、
    /// そのときは**レイヤーごとにプレイヤーを分ける**ほうが素直になる。
    ///
    /// 上限を持たせているのは、姿勢の配列を毎フレーム確保しないため。
    /// 超えたぶんは**重みの小さいものから捨てる**——
    /// 黙って捨てると絵が微妙に変わるだけで気づけないので、1回だけ知らせる。
    /// </summary>
    public const int MaxBlendClips = 4;

    private readonly Model _model;

    /// <summary>ノードごとの今の姿勢。**混ぜ終わった結果**が入る。</summary>
    private readonly NodePose[] _pose;

    /// <summary>クリップ1本ぶんの評価に使う作業場(Day 42)。**毎フレーム確保しない**ための持ち回し。</summary>
    private readonly NodePose[] _scratch;

    /// <summary>ノードごとの重み付き累積(Day 42)。</summary>
    private readonly PoseAccumulator[] _accumulator;

    /// <summary>ノードごとの世界行列。毎フレーム 4 段目で作り直す。</summary>
    private readonly Matrix4x4[] _world;

    /// <summary>スキンごとの関節行列。そのままシェーダへ送る。</summary>
    private readonly Matrix4x4[][] _jointMatrices;

    /// <summary>関節が多すぎて送れないスキン。**1回だけ知らせる**ための記録。</summary>
    private readonly bool[] _skinTooLarge;

    /// <summary>今混ぜているクリップと重み(Day 42)。</summary>
    private readonly ClipWeight[] _blend = new ClipWeight[MaxBlendClips];

    private int _blendCount;

    /// <summary>
    /// クリップごとの独立した時刻(Day 42)。**位相同期を切ったときだけ使う**。
    ///
    /// 同期しているときは <see cref="Phase"/> ひとつで全部が決まるので、こちらは動くだけで読まれない。
    /// それでも常に進めておくのは、**同期を切り替えた瞬間に時刻が飛ばない**ようにするため。
    /// </summary>
    private readonly float[] _clipTimes;

    /// <summary>
    /// 正規化した再生位置(0〜1)。Day 41 の「秒」から**割合**へ変わった(Day 42)。
    ///
    /// これが位相同期の本体。歩き 0.71 秒と走り 1.16 秒は長さが違うが、
    /// **どちらも「1周のうちどこか」で見れば同じ物差しに乗る**。
    /// 位相 0.25 なら、歩きは 0.18 秒、走りは 0.29 秒。
    /// こうして初めて「左足が前に出ている瞬間どうし」を混ぜられる。
    /// </summary>
    private float _phase;

    private bool _warnedTooManyClips;

    public AnimationPlayer(Model model)
    {
        _model = model;

        _pose = new NodePose[model.Nodes.Count];
        _scratch = new NodePose[model.Nodes.Count];
        _accumulator = new PoseAccumulator[model.Nodes.Count];
        _world = new Matrix4x4[model.Nodes.Count];
        _clipTimes = new float[model.Animations.Count];

        _jointMatrices = new Matrix4x4[model.Skins.Count][];
        _skinTooLarge = new bool[model.Skins.Count];

        for (int i = 0; i < model.Skins.Count; i++)
        {
            Skin skin = model.Skins[i];
            _skinTooLarge[i] = skin.JointCount > MaxJoints;

            if (_skinTooLarge[i])
            {
                Console.WriteLine(
                    $"[アニメ] スキン '{skin.Name}' の関節が {skin.JointCount} 本あります"
                    + $"(送れるのは {MaxJoints} 本まで)。バインドポーズで描きます");
            }

            _jointMatrices[i] = new Matrix4x4[Math.Min(skin.JointCount, MaxJoints)];
        }

        // **クリップが無くても評価する**。BoxTextured のような静的モデルでも、
        // ノードの世界行列は要る(描くときに使う)。
        SelectClip(model.Animations.Count > 0 ? 0 : -1);
    }

    /// <summary>
    /// いちばん重みの大きいクリップ。**混ぜていないときは再生中のクリップそのもの**。
    /// -1 なら「クリップ無し」= ファイルの姿勢のまま。
    ///
    /// Day 41 では素直なフィールドだったが、Day 42 で
    /// 「今どのクリップか」が一意でなくなったので**導出**に変えた。
    /// HUD と、Day 41 から続くキー操作(次のクリップへ)のためだけに残してある。
    /// </summary>
    public int ClipIndex
    {
        get
        {
            int best = -1;
            float bestWeight = 0.0f;

            for (int i = 0; i < _blendCount; i++)
            {
                if (_blend[i].Weight > bestWeight)
                {
                    bestWeight = _blend[i].Weight;
                    best = _blend[i].Clip;
                }
            }

            return best;
        }
    }

    /// <summary>今混ぜているクリップと重み(Day 42)。HUD と自己チェック用。</summary>
    public ReadOnlySpan<ClipWeight> Blend => _blend.AsSpan(0, _blendCount);

    /// <summary>正規化した再生位置(0〜1)。**混ぜているクリップ全部に共通**(Day 42)。</summary>
    public float Phase => _phase;

    /// <summary>再生位置(秒)。<see cref="Phase"/> を <see cref="Duration"/> 倍しただけの表示用。</summary>
    public float Time => _phase * Duration;

    /// <summary>再生速度の倍率。0.25 にすると 1/4 の速さ。負にすれば逆再生になる。</summary>
    public float Speed { get; set; } = 1.0f;

    /// <summary>止めているか。**止めても姿勢は保つ**(評価済みの行列をそのまま使う)。</summary>
    public bool Paused { get; set; }

    /// <summary>
    /// スキニングを効かせるか。false なら関節行列を単位行列にする = **バインドポーズ**。
    ///
    /// 見比べ用のスイッチ(Alt+F6)。骨は動いているのに頂点が動かない絵になり、
    /// **「頂点ブレンディングが何をしているか」がいちばんはっきり見える**。
    /// </summary>
    public bool SkinningEnabled { get; set; } = true;

    /// <summary>
    /// 位相を合わせて再生するか(Day 42)。**既定は ON**。
    ///
    /// <para>
    /// ON なら、混ぜている全クリップが**同じ正規化位置**を指す。
    /// 歩き(0.71 秒)と走り(1.16 秒)は長さが違うので、
    /// 秒で合わせると「歩きの左足が前、走りの右足が前」のような
    /// **食い違った瞬間どうし**を混ぜることになり、
    /// 足が地面をこすったり、歩幅が縮んだりする。
    /// </para>
    ///
    /// <para>
    /// OFF にすると、各クリップが自分の長さで勝手に回る。
    /// 2本の周期は 0.71 と 1.16 で**約分できない**ので、
    /// 位相の関係が毎周期ずれていき、
    /// **同じ歩容の瞬間なのに毎回ちがう姿勢になる**。
    /// 自己チェック(Shift+Alt+F12)がこのばらつきを数字で出す。
    /// </para>
    ///
    /// <b>切り替えても時刻は飛ばない</b>。同期を切っている間も
    /// <see cref="_phase"/> を、同期している間も <see cref="_clipTimes"/> を
    /// 両方とも進めてあるため。
    /// </summary>
    public bool SyncPhase { get; set; } = true;

    public AnimationClip? Clip
    {
        get
        {
            int index = ClipIndex;
            return index >= 0 && index < _model.Animations.Count ? _model.Animations[index] : null;
        }
    }

    public string ClipName => Clip?.Name ?? "なし";

    /// <summary>
    /// 1周の長さ(秒)。**混ぜているときは重み付きの平均**(Day 42)。
    ///
    /// 歩き 0.71 秒と走り 1.16 秒を 50:50 で混ぜると 0.935 秒。
    /// 位相はこの長さで1周するので、**混ぜる比率を動かすと歩調も連続に変わる**——
    /// 走りに寄せるほど周期が短くなる、という当たり前のことが、
    /// この1行だけで手に入る。
    /// </summary>
    public float Duration
    {
        get
        {
            float sum = 0.0f;
            float weight = 0.0f;

            for (int i = 0; i < _blendCount; i++)
            {
                sum += _model.Animations[_blend[i].Clip].Duration * _blend[i].Weight;
                weight += _blend[i].Weight;
            }

            return weight > 0.0f ? sum / weight : 0.0f;
        }
    }

    /// <summary>
    /// 混ぜるクリップと重みを差し替える(Day 42)。**重みは合計 1 に正規化する**。
    ///
    /// 呼ぶ側(ブレンドツリー / ステートマシン)が正規化済みの値を渡してくるとしても、
    /// ここで揃え直す。合計が 0.99 のまま <see cref="PoseAccumulator"/> へ流すと
    /// **モデル全体がわずかに縮む**(重み合計が 1 でないときの症状は Day 41 の要点1と同じ)。
    ///
    /// **時刻は動かさない**のがここの約束。重みだけを差し替えるので、
    /// 毎フレーム呼んでも再生位置は連続したまま。
    /// </summary>
    public void SetBlend(ReadOnlySpan<ClipWeight> clips)
    {
        _blendCount = 0;
        float total = 0.0f;

        foreach (ClipWeight entry in clips)
        {
            if (entry.Weight <= 1e-4f || (uint)entry.Clip >= (uint)_model.Animations.Count)
            {
                continue;
            }

            if (_blendCount >= MaxBlendClips)
            {
                if (!_warnedTooManyClips)
                {
                    _warnedTooManyClips = true;
                    Console.WriteLine(
                        $"[アニメ] 同時に混ぜられるクリップは {MaxBlendClips} 本までです。"
                        + "重みの小さいものから捨てます");
                }

                // 今持っているいちばん弱いものと比べて、強いほうを残す。
                int weakest = 0;
                for (int i = 1; i < _blendCount; i++)
                {
                    if (_blend[i].Weight < _blend[weakest].Weight)
                    {
                        weakest = i;
                    }
                }

                if (entry.Weight <= _blend[weakest].Weight)
                {
                    continue;
                }

                total -= _blend[weakest].Weight;
                _blend[weakest] = entry;
                total += entry.Weight;
                continue;
            }

            _blend[_blendCount++] = entry;
            total += entry.Weight;
        }

        if (total > 0.0f)
        {
            for (int i = 0; i < _blendCount; i++)
            {
                _blend[i] = _blend[i] with { Weight = _blend[i].Weight / total };
            }
        }

        Evaluate();
    }

    /// <summary>
    /// 再生位置だけを先頭へ戻す(Day 42)。**混ぜているクリップと重みはそのまま**。
    ///
    /// Day 41 では「先頭へ戻す」= <c>SelectClip(ClipIndex)</c> で足りていたが、
    /// 混ぜられるようになると**それではブレンドが1本に潰れる**。
    /// 「時刻を戻す」と「クリップを選び直す」は別の操作、と分けておく。
    /// </summary>
    public void Rewind()
    {
        _phase = 0.0f;
        Array.Clear(_clipTimes);
        Evaluate();
    }

    /// <summary>クリップを1本だけ選ぶ。範囲外なら「クリップ無し」になる。</summary>
    public void SelectClip(int index)
    {
        // **時刻を 0 に戻す**。クリップごとに長さが違う(Fox は 3.4 秒 / 0.7 秒 / 1.2 秒)ので、
        // 前のクリップの時刻をそのまま持ち込むと、短いほうへ移った瞬間に
        // ループの途中から始まって不自然に見える。
        // 途中から繋ぐのは <see cref="AnimationStateMachine"/> の仕事(クロスフェード)。
        _phase = 0.0f;
        Array.Clear(_clipTimes);

        if (index >= 0 && index < _model.Animations.Count)
        {
            SetBlend([new ClipWeight(index, 1.0f)]);
        }
        else
        {
            SetBlend([]);
        }
    }

    /// <summary>次のクリップへ。最後まで行ったら先頭に戻る。</summary>
    public void NextClip()
    {
        if (_model.Animations.Count > 0)
        {
            SelectClip((ClipIndex + 1) % _model.Animations.Count);
        }
    }

    /// <summary>1フレームぶん進める。<paramref name="deltaSeconds"/> は可変 dt でよい。</summary>
    public void Update(float deltaSeconds)
    {
        if (!Paused)
        {
            Advance(deltaSeconds * Speed);
        }
    }

    /// <summary>
    /// 時刻を進めて評価し直す。一時停止中のコマ送り(Alt+F8)からも呼ぶ。
    ///
    /// <b>位相と各クリップの時刻を両方進める</b>のが Day 42 の形。
    /// 使うのは片方だけだが、両方進めておかないと
    /// <see cref="SyncPhase"/> を切り替えた瞬間に姿勢が飛ぶ。
    ///
    /// **剰余で折り返す**のがループの全部。C# の <c>%</c> は負の数に対して
    /// 負を返すので、逆再生のために長さを足してからもう一度取る。
    /// </summary>
    public void Advance(float seconds)
    {
        float duration = Duration;
        if (duration > 0.0f)
        {
            _phase = Wrap(_phase + (seconds / duration), 1.0f);
        }

        for (int i = 0; i < _clipTimes.Length; i++)
        {
            _clipTimes[i] = Wrap(_clipTimes[i] + seconds, _model.Animations[i].Duration);
        }

        Evaluate();
    }

    /// <summary>ノードの世界行列。**スキンを持たないパーツを描くのに使う**(BoxAnimated)。</summary>
    public Matrix4x4 GetNodeWorld(int node) =>
        (uint)node < (uint)_world.Length ? _world[node] : Matrix4x4.Identity;

    /// <summary>
    /// スキンの関節行列。**そのままシェーダの <c>uJoints</c> へ送る**。
    /// 範囲外なら空を返し、呼ぶ側は「スキン無し」として描く。
    /// </summary>
    public ReadOnlySpan<Matrix4x4> GetJointMatrices(int skin) =>
        (uint)skin < (uint)_jointMatrices.Length ? _jointMatrices[skin] : default;

    /// <summary>クリップ <paramref name="clip"/> を今どの時刻で読むか(Day 42)。</summary>
    private float TimeOf(int clip)
    {
        float duration = _model.Animations[clip].Duration;
        return SyncPhase ? _phase * duration : _clipTimes[clip];
    }

    /// <summary>0 以上 <paramref name="period"/> 未満に折り返す。負の数も正しく回る。</summary>
    private static float Wrap(float value, float period) =>
        period > 0.0f ? (((value % period) + period) % period) : 0.0f;

    /// <summary>
    /// 今の時刻と重みから、世界行列と関節行列を作り直す。
    /// </summary>
    private void Evaluate()
    {
        // --- 1〜2. 参加するクリップを順に評価して足し込む ---
        Array.Clear(_accumulator);

        for (int b = 0; b < _blendCount; b++)
        {
            ClipWeight entry = _blend[b];

            // **毎回ファイルの姿勢へ戻す**。クリップが触らないノードは
            // 「前フレームの値」ではなく「ファイルの値」でなければならない。
            // 戻さずに済ませると、クリップを切り替えたときに
            // **前のクリップが最後に書いた値が残る**(Fox で Walk → Survey にしたときに
            // 腰の高さだけ前のクリップのまま、といった壊れ方になる)。
            for (int i = 0; i < _scratch.Length; i++)
            {
                _scratch[i] = _model.Nodes[i].RestPose;
            }

            _model.Animations[entry.Clip].Apply(TimeOf(entry.Clip), _scratch);

            for (int i = 0; i < _scratch.Length; i++)
            {
                _accumulator[i].Add(_scratch[i], entry.Weight);
            }
        }

        // --- 3. 混ぜた結果を取り出す ---
        //
        // **1本も混ぜていないノードはファイルの姿勢**。
        // クリップ無しのときも、この経路でファイルの姿勢がそのまま出る。
        for (int i = 0; i < _pose.Length; i++)
        {
            _pose[i] = _accumulator[i].Resolve(_model.Nodes[i].RestPose);
        }

        // --- 4. 木を降りて世界行列 ---
        //
        // **NodeOrder は親が必ず子より先に来る並び**(Model.NodeOrder)。
        // だから1回舐めるだけで、親の世界行列が確定済みであることが保証される。
        // glTF のノード番号順には何の保証も無いので、この並びが要る。
        foreach (int node in _model.NodeOrder)
        {
            Matrix4x4 local = _pose[node].ToMatrix();
            int parent = _model.Nodes[node].Parent;

            _world[node] = parent < 0 ? local : local * _world[parent];
        }

        // --- 5. 関節行列 ---
        for (int s = 0; s < _jointMatrices.Length; s++)
        {
            Skin skin = _model.Skins[s];
            Matrix4x4[] matrices = _jointMatrices[s];

            for (int j = 0; j < matrices.Length; j++)
            {
                // **スキニングを切ったとき、あるいは関節が多すぎるときは単位行列**。
                // 頂点は Σw * I * p = p になり、バインドポーズがそのまま出る
                // (重みの合計が 1 に正規化してあるから成り立つ)。
                if (!SkinningEnabled || _skinTooLarge[s])
                {
                    matrices[j] = Matrix4x4.Identity;
                    continue;
                }

                // **逆バインド行列が先、関節の世界行列が後**。
                // 行ベクトル規約(v * M)なので、先に適用するものを左に書く。
                //   p を関節のローカルへ持っていく → 今の関節の位置へ運び直す
                matrices[j] = skin.InverseBindMatrices[j] * _world[skin.Joints[j]];
            }
        }
    }
}
