using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// クリップを1本再生して、**そのフレームの姿勢**を作る(Day 41)。
///
/// <para>
/// <b>モデルと分けてあるのが今日いちばん大きい設計判断</b>。
/// <see cref="Model"/> は読み込んだまま変わらない(頂点・マテリアル・骨格・クリップ)。
/// 変わるのは「今この個体がどのポーズか」だけなので、そちらをこの箱に閉じ込める。
///
/// 分けておくと、キツネを 100 匹並べるとき
/// <b>モデル1つ + プレイヤー 100 個</b>で済む。頂点バッファもテクスチャも共有され、
/// 個体ごとに増えるのは姿勢の配列(ノード数 × 40 バイトほど)だけになる。
/// Day 50 のプレイアブルデモで効いてくる形で、
/// 逆に <see cref="Model"/> に <c>Time</c> を持たせてしまうと、
/// **同じモデルの2体目が作れない**。
/// </para>
///
/// <para>
/// <b>1フレームの流れ</b>は3段。
/// <code>
///   1. ファイルの姿勢へ戻す        … _pose ← model.Nodes[i].RestPose
///   2. クリップを当てる            … clip.Apply(time, _pose)     ノードの TRS が変わる
///   3. 木を根から降りて世界行列    … _world[i] = local(i) * _world[parent]
///   4. 関節行列を作る              … joint[j] = IBM[j] * _world[joints[j]]
/// </code>
/// 2 と 3 が分かれているのが要点で、**アニメーションはローカル姿勢しか触らない**。
/// 世界行列は毎回まとめて作り直す。「動いたノードだけ更新する」最適化もあるが、
/// 親が動けば子孫が全部動くので、木が小さいうちは得にならない。
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

    private readonly Model _model;

    /// <summary>ノードごとの今の姿勢。**アニメーションが書き換えるのはここだけ**。</summary>
    private readonly NodePose[] _pose;

    /// <summary>ノードごとの世界行列。毎フレーム 3 段目で作り直す。</summary>
    private readonly Matrix4x4[] _world;

    /// <summary>スキンごとの関節行列。そのままシェーダへ送る。</summary>
    private readonly Matrix4x4[][] _jointMatrices;

    /// <summary>関節が多すぎて送れないスキン。**1回だけ知らせる**ための記録。</summary>
    private readonly bool[] _skinTooLarge;

    private float _time;

    public AnimationPlayer(Model model)
    {
        _model = model;

        _pose = new NodePose[model.Nodes.Count];
        _world = new Matrix4x4[model.Nodes.Count];

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
        ClipIndex = model.Animations.Count > 0 ? 0 : -1;
        Evaluate();
    }

    /// <summary>再生中のクリップ。-1 なら「クリップ無し」= ファイルの姿勢のまま。</summary>
    public int ClipIndex { get; private set; }

    /// <summary>再生位置(秒)。</summary>
    public float Time => _time;

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

    public AnimationClip? Clip =>
        ClipIndex >= 0 && ClipIndex < _model.Animations.Count ? _model.Animations[ClipIndex] : null;

    public string ClipName => Clip?.Name ?? "なし";

    public float Duration => Clip?.Duration ?? 0.0f;

    /// <summary>クリップを選び直す。範囲外なら「クリップ無し」になる。</summary>
    public void SelectClip(int index)
    {
        ClipIndex = index >= 0 && index < _model.Animations.Count ? index : -1;

        // **時刻を 0 に戻す**。クリップごとに長さが違う(Fox は 3.4 秒 / 0.7 秒 / 1.2 秒)ので、
        // 前のクリップの時刻をそのまま持ち込むと、短いほうへ移った瞬間に
        // ループの途中から始まって不自然に見える。
        // 途中から繋ぐのは Day 42(位相を合わせてブレンドする)の仕事。
        _time = 0.0f;
        Evaluate();
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
    /// <b>剰余で折り返す</b>のがループの全部。C# の <c>%</c> は負の数に対して
    /// 負を返すので、逆再生のために長さを足してからもう一度取る。
    /// </summary>
    public void Advance(float seconds)
    {
        float duration = Duration;
        if (duration > 0.0f)
        {
            _time = ((_time + seconds) % duration + duration) % duration;
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

    /// <summary>
    /// 今の時刻の姿勢から、世界行列と関節行列を作り直す。
    /// </summary>
    private void Evaluate()
    {
        // --- 1. ファイルの姿勢へ戻す ---
        //
        // **毎回戻すのが大事**。クリップが触らないノードは前フレームの値が残るべきだが、
        // 「前フレームの値」ではなく「ファイルの値」でなければならない。
        // 戻さずに済ませると、クリップを切り替えたときに
        // **前のクリップが最後に書いた値が残る**(Fox で Walk → Survey にしたときに
        // 腰の高さだけ前のクリップのまま、といった壊れ方になる)。
        for (int i = 0; i < _pose.Length; i++)
        {
            _pose[i] = _model.Nodes[i].RestPose;
        }

        // --- 2. クリップを当てる ---
        Clip?.Apply(_time, _pose);

        // --- 3. 木を降りて世界行列 ---
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

        // --- 4. 関節行列 ---
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
