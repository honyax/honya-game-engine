namespace HonyaEngine;

/// <summary>
/// 固定タイムステップのゲームループ。**Phase 4(エンジンコア)の1本目**。
///
/// Day 18 までの更新は、フレーム時間をそのままシミュレーションに流し込んでいた。
///
/// <code>
/// sprite.Position += sprite.Velocity * (float)deltaSeconds;   // 可変タイムステップ
/// </code>
///
/// これは短く書けるが、3つの問題を抱えている(計画書の要点1)。
///   1. **再現しない** … 同じ入力でも fps が違えば結果が変わる
///   2. **壊れる**     … フレームが長引くと1ステップの移動量が大きくなり、
///                       壁をすり抜ける。物理を入れると発散する
///   3. **調整が効かない** … 「1秒で何回更新されるか」が環境任せなので、
///                       ゲームバランスの数値が固定できない
///
/// 対策は「**シミュレーションを固定間隔で回し、描画とは切り離す**」こと。
/// 経過時間を溜め(アキュムレータ)、固定間隔ぶん溜まるたびに1ステップ進める。
///
/// 描画は溜まり具合(<see cref="Alpha"/>)を使って、
/// 前のステップと今のステップの間を補間する。これで
/// **シミュレーションが 5Hz でも、描画は 144Hz で滑らかに見える**。
///
/// 出典は Glenn Fiedler の "Fix Your Timestep!"。
/// ゲームループの議論はほぼこの記事に集約される。
/// </summary>
internal sealed class GameLoop
{
    /// <summary>まだステップに使われていない経過時間。**アキュムレータ**。</summary>
    private double _accumulator;

    /// <summary>
    /// 1ステップぶんの時間。既定は 60Hz。
    ///
    /// **この値はゲームの仕様の一部**であって、環境で変わってはいけない。
    /// 「毎ステップ 0.5 ダメージ」と書いたら、それが1秒で 30 ダメージだと確定する。
    /// 可変タイムステップだと、この確定ができない。
    /// </summary>
    public double FixedDeltaTime { get; set; } = 1.0 / 60.0;

    /// <summary>
    /// 1フレームで回すステップ数の上限。**死のスパイラルを止める非常ブレーキ**。
    ///
    /// 上限が無いと、次のことが起きる。
    ///   フレームが重い → 溜まった時間が増える → 次フレームでステップ数が増える
    ///   → もっと重い → もっと溜まる → …
    /// これが「死のスパイラル(spiral of death)」で、一度入ると復帰できない。
    ///
    /// 上限を付けると、遅い環境では**シミュレーションが実時間より遅れる**ことになる。
    /// 遅れをどう扱うかが <see cref="DropExcess"/>。
    /// </summary>
    public int MaxStepsPerFrame { get; set; } = 8;

    /// <summary>
    /// 追いつけなかったぶんの時間を捨てるか。
    ///
    /// <list type="bullet">
    /// <item><b>true(既定)</b> … 捨てる。シミュレーションは**スローモーション**になるが、
    /// 遅れは溜まらないので動作は安定する。ほとんどのゲームはこちら</item>
    /// <item><b>false</b> … 捨てない。**遅れが永久に増え続ける**。
    /// 一度重くなると二度と追いつけず、時計が実時間からずれ続ける</item>
    /// </list>
    ///
    /// 「捨てる」を選ぶということは、**重い環境ではゲーム内時間が実時間より
    /// ゆっくり進むのを許す**という設計判断。オンライン対戦のように
    /// 実時間と同期しなければならない場合は、捨てるのではなく
    /// 描画やシミュレーションの質を落とすほうを選ぶ。
    /// </summary>
    public bool DropExcess { get; set; } = true;

    /// <summary>
    /// 次のステップまでどのくらい進んでいるか。0.0〜1.0。
    ///
    /// **描画時の補間係数**として使う。
    /// <c>Lerp(前のステップの状態, 今のステップの状態, Alpha)</c> で
    /// 「ステップとステップの間」の絵が作れる。
    ///
    /// これが無いと、シミュレーションのレートでしか絵が更新されない。
    /// 60Hz なら気付きにくいが、20Hz や 5Hz にすると一目でカクつく。
    /// </summary>
    public double Alpha { get; private set; }

    /// <summary>直前の <see cref="Advance"/> で回したステップ数。</summary>
    public int StepsLastFrame { get; private set; }

    /// <summary>起動からのステップ数の合計。**同じ入力なら必ず同じ値になる**のが固定ステップの利点。</summary>
    public long TotalSteps { get; private set; }

    /// <summary>シミュレーション内の経過時間(秒)。実時間とは一致しないことがある。</summary>
    public double SimulationTime { get; private set; }

    /// <summary>今たまっている未処理の時間(秒)。**遅れの指標**。</summary>
    public double Lag => _accumulator;

    /// <summary>追いつけずに捨てた時間の合計(秒)。増え続けるなら処理落ちしている。</summary>
    public double DroppedSeconds { get; private set; }

    /// <summary>
    /// フレーム時間を渡して、必要な回数だけ <paramref name="fixedUpdate"/> を呼ぶ。
    /// </summary>
    /// <param name="frameSeconds">前回の呼び出しからの実時間(秒)。</param>
    /// <param name="fixedUpdate">
    /// 1ステップぶんの更新。引数は必ず <see cref="FixedDeltaTime"/>。
    ///
    /// **毎回同じ値が渡る**のがこの仕組みの本質で、
    /// だから同じ入力に対して同じ結果になる(決定性)。
    /// </param>
    public void Advance(double frameSeconds, Action<float> fixedUpdate)
    {
        // 負の時間や NaN が来ると累積が壊れるので弾いておく。
        // ウィンドウをドラッグしたあとなどに巨大な値が来ることがあるが、
        // それは MaxStepsPerFrame と DropExcess が受け止める。
        if (double.IsNaN(frameSeconds) || frameSeconds < 0.0)
        {
            frameSeconds = 0.0;
        }

        _accumulator += frameSeconds;
        StepsLastFrame = 0;

        // **溜まっているぶんだけ、固定間隔で進める**。
        // 1フレームで0回のことも、複数回のこともある。
        // 描画レートが高ければ0回が続き、低ければまとめて回る。
        while (_accumulator >= FixedDeltaTime && StepsLastFrame < MaxStepsPerFrame)
        {
            fixedUpdate((float)FixedDeltaTime);

            _accumulator -= FixedDeltaTime;
            SimulationTime += FixedDeltaTime;
            TotalSteps++;
            StepsLastFrame++;
        }

        if (_accumulator >= FixedDeltaTime)
        {
            // 上限に達したのにまだ残っている = このフレームでは追いつけなかった。
            if (DropExcess)
            {
                // 1ステップ未満の端数だけ残して、あとは捨てる。
                // 端数を残すのは、**Alpha を連続に保つため**。
                // ここでゼロにすると補間が毎回リセットされ、絵がカクつく。
                double keep = _accumulator % FixedDeltaTime;
                DroppedSeconds += _accumulator - keep;
                _accumulator = keep;
            }
        }

        // 捨てない設定だと _accumulator が FixedDeltaTime を超えたままになる。
        // その場合 Alpha は 1 を超えるが、補間ではなく**外挿**になってしまうので、
        // ここで 1 に丸めておく(絵は最新のステップのまま止まる)。
        Alpha = Math.Clamp(_accumulator / FixedDeltaTime, 0.0, 1.0);
    }

    /// <summary>溜まっている時間と統計を捨てる。設定を変えた直後などに呼ぶ。</summary>
    public void Reset()
    {
        _accumulator = 0.0;
        Alpha = 0.0;
        StepsLastFrame = 0;
        DroppedSeconds = 0.0;
    }
}
