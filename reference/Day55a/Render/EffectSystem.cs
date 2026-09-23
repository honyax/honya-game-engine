using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 出せるエフェクトの種類(Day 50)。
///
/// <para>
/// <b>「どう撒くか」ではなく「何が起きたか」で名前を付ける</b>。
/// 呼ぶ側は「足が着いた」「当たった」としか言わず、
/// 円盤か球か・加算かアルファか・何粒かは <see cref="EffectSystem.Configure"/> が決める。
/// Day 49 は筋書きの設定を <c>Program</c> 側(使う側)に置いたが、
/// 今日のこれは<b>ゲームが何度でも呼ぶもの</b>なので、表のほうをエンジンに入れた。
/// </para>
/// </summary>
internal enum EffectKind
{
    /// <summary>足元の土埃。**一歩ごとに1回**。円盤から外へ低く広がる。</summary>
    FootDust,

    /// <summary>着地。土埃の大きい版。落ちてきた勢いに応じて呼ぶ。</summary>
    Landing,

    /// <summary>着弾。**加算で一瞬だけ**光る。面の法線に沿って跳ねる。</summary>
    Hit,

    /// <summary>爆発の閃光。**煙(<see cref="Smoke"/>)と対で出す**。</summary>
    Explosion,

    /// <summary>爆発の煙。閃光が消えたあとに残る。</summary>
    Smoke,
}

/// <summary>
/// **一定距離ごとに1回だけ発火させる目盛り**(Day 50)。足音・足跡・土埃に使う。
///
/// <para>
/// 「歩いている間ずっと土埃を出す」だと、止まっているときも出続けるし、
/// 走ると<b>フレームレートのぶんだけ濃くなる</b>。
/// 実際の足音がそうであるように、<b>距離を刻んで一歩ごとに1回</b>にする。
/// </para>
///
/// <para>
/// 端数を <see cref="_carry"/> に持ち越すのは Day 49 の <see cref="ParticleEmitter"/> と同じ理屈。
/// 切り捨てると、1フレームで進む距離が歩幅より短いときに<b>永久に一歩も出ない</b>。
/// </para>
/// </summary>
internal struct FootstepTracker
{
    /// <summary>1回の呼び出しで出す歩数の上限。**止めたあとの巨大な dt で暴発させない**。</summary>
    private const int MaxStepsPerCall = 4;

    private float _carry;

    /// <summary>歩幅(m)。人間の歩きは 0.7〜0.9m、走りはもっと広い。</summary>
    public float Stride { get; set; }

    /// <summary>
    /// <paramref name="distance"/> ぶん進めて、**何歩ぶん越えたか**を返す。
    /// 越えていなければ 0。
    /// </summary>
    public int Advance(float distance)
    {
        if (Stride <= 0.0f || distance <= 0.0f)
        {
            return 0;
        }

        _carry = MathF.Min(_carry + distance, Stride * MaxStepsPerCall);

        int steps = (int)(_carry / Stride);
        _carry -= steps * Stride;

        return steps;
    }

    /// <summary>止まったとき・瞬間移動したときに呼ぶ。**次の一歩を歩幅の頭から数え直す**。</summary>
    public void Reset() => _carry = 0.0f;
}

/// <summary>
/// **エフェクトの発生器**(Day 50)。今日の主役その2。
///
/// <para>
/// Day 49 の <see cref="ParticleEmitter"/> は「1つの放出口をずっと出しっぱなし」だった。
/// ゲームで要るのはそうではなく、<b>出来事のたびに1回だけ出て、勝手に片付くもの</b>になる——
/// 足が着いた、弾が当たった、樽が爆発した。
/// </para>
///
/// <para>
/// <b>放出口をプールして貸し出す</b>のがこのクラスの中身のほぼ全部になる。
/// 呼ばれるたびに <c>new ParticleEmitter</c> すると、
/// 256 個の構造体配列の確保が毎回走って<b>GC が刻む</b>。
/// あらかじめ何本か作っておいて、空いているものを貸す——
/// Day 20 のリソースプールと同じ判断で、
/// <b>借りたものは自分で返す(粒が尽きたら空きに戻る)</b>ところだけが違う。
/// </para>
///
/// <para>
/// <b>GL を1行も触らない</b>のは <see cref="ParticleEmitter"/> から引き継いだ線。
/// テクスチャは <see cref="Draw"/> の引数で受け取るだけで、持たない。
/// おかげで自己チェックが GL 無しで全部走る。
/// </para>
///
/// <para>
/// <b>混ぜ方でまとめて描く</b>(<see cref="Draw"/>)。
/// 加算の効果と半透明の効果が同時に何本出ていても、
/// 加算をまとめて → アルファをまとめて、の2回で済む。
/// 出た順に描くと、切り替わるたびにドローコールが増える。
/// </para>
/// </summary>
internal sealed class EffectSystem
{
    /// <summary>貸出中の1本。**放出口 + どう描くか**。</summary>
    private sealed class Slot
    {
        public Slot(int capacity, int seed)
        {
            Emitter = new ParticleEmitter(capacity, seed);
        }

        public ParticleEmitter Emitter { get; }

        public EffectKind Kind { get; set; }

        public ParticleBlend Blend { get; set; }

        /// <summary>コマ送りのテクスチャを使うか。**加算 = 丸、アルファ = 煙**で1対1にしてある。</summary>
        public bool UseFlipbook { get; set; }

        /// <summary>貸出中か。粒が尽きたら false に戻る。</summary>
        public bool Active { get; set; }
    }

    private readonly Slot[] _slots;

    public EffectSystem(int slots = 12, int capacityPerSlot = 256, int seed = 5000)
    {
        _slots = new Slot[Math.Max(1, slots)];
        for (int i = 0; i < _slots.Length; i++)
        {
            // **放出口ごとに種を変える**。同じ種だと、同じフレームに出た
            // 2発の爆発が<b>一粒残らず同じ形</b>になる(鏡に映したように見える)。
            _slots[i] = new Slot(capacityPerSlot, seed + i);
        }
    }

    /// <summary>プールの本数。</summary>
    public int SlotCount => _slots.Length;

    /// <summary>貸出中の本数。</summary>
    public int ActiveCount { get; private set; }

    /// <summary>いま生きている粒の合計。</summary>
    public int ParticleCount { get; private set; }

    /// <summary>空きが無くて出せなかった回数。**0 でないならプールが足りていない**。</summary>
    public int DroppedSpawns { get; private set; }

    /// <summary>今までに出した回数(計測用)。</summary>
    public int TotalSpawns { get; private set; }

    /// <summary>
    /// エフェクトを1つ出す。<paramref name="normal"/> は<b>当たった面の向き</b>
    /// (地面なら上、壁なら横)。
    ///
    /// <para>
    /// <see cref="EffectKind.Explosion"/> だけは<b>2本使う</b>。
    /// 閃光(加算)と煙(アルファ)は混ぜ方が違うので、1つの放出口には同居できない——
    /// 「1つの出来事が、複数の放出口になる」のは実際のエンジンでもごく普通の形で、
    /// Unity なら1つのプレハブに ParticleSystem が何個もぶら下がる。
    /// </para>
    /// </summary>
    public void Spawn(EffectKind kind, Vector3 position, Vector3 normal)
    {
        SpawnOne(kind, position, normal);

        if (kind == EffectKind.Explosion)
        {
            SpawnOne(EffectKind.Smoke, position, normal);
        }
    }

    /// <summary>
    /// 全部を進めて、**粒が尽きた本を空きに戻す**。
    ///
    /// <para>
    /// 貸出中でない本は <see cref="ParticleEmitter.Update"/> すら呼ばない。
    /// 中身が空なら実質ただの早期 return だが、
    /// <b>1本も出ていないときに 12 回の空回りをしない</b>ほうが、
    /// 「エフェクトは使っていないのに毎フレーム何か走っている」という
    /// 気持ちの悪さが残らない。
    /// </para>
    /// </summary>
    public void Update(float deltaSeconds)
    {
        ActiveCount = 0;
        ParticleCount = 0;

        foreach (Slot slot in _slots)
        {
            if (!slot.Active)
            {
                continue;
            }

            slot.Emitter.Update(deltaSeconds);

            if (slot.Emitter.Count == 0)
            {
                // **一発ものは自分で片付く**。呼んだ側は消す責任を持たない——
                // 「爆発を出す」と言ったきり忘れられるのが、この形の値打ちになる。
                slot.Active = false;
                continue;
            }

            ActiveCount++;
            ParticleCount += slot.Emitter.Count;
        }
    }

    /// <summary>
    /// 貸出中の全部を積む。**加算をまとめて、次にアルファをまとめて**。
    ///
    /// <para>
    /// 出た順に回すと、爆発(加算)と土埃(アルファ)が交互に並んだときに
    /// <see cref="ParticleRenderer"/> が毎回フラッシュする。
    /// 2周するだけで、<b>何本出ていてもドローコールは 2 回</b>に収まる。
    /// 半透明どうしの前後関係は正しくならないが、
    /// エフェクトは「光るもの」と「濁るもの」なので、
    /// <b>光るものを先に敷いて濁るものを重ねる</b>ほうが絵としても素直になる。
    /// </para>
    /// </summary>
    public void Draw(ParticleRenderer renderer, Texture dot, Texture smoke)
    {
        DrawGroup(renderer, ParticleBlend.Additive, dot, smoke);
        DrawGroup(renderer, ParticleBlend.Alpha, dot, smoke);
    }

    /// <summary>
    /// 貸出中の本の内訳。**何がいくつ残っているか**を内訳表示に出すためのもの。
    ///
    /// <c>Slot</c> を外へ見せないのは、外から <c>Active</c> を書き換えられると
    /// プールの数え方が壊れるため。読みたいのは種類と粒数だけなので、それだけ返す。
    /// </summary>
    public IEnumerable<(EffectKind Kind, int Count)> ActiveEffects()
    {
        foreach (Slot slot in _slots)
        {
            if (slot.Active)
            {
                yield return (slot.Kind, slot.Emitter.Count);
            }
        }
    }

    public void Clear()
    {
        foreach (Slot slot in _slots)
        {
            slot.Emitter.Clear();
            slot.Active = false;
        }

        ActiveCount = 0;
        ParticleCount = 0;
        DroppedSpawns = 0;
        TotalSpawns = 0;
    }

    /// <summary>
    /// **エフェクトの表**。種類ごとの設定を1箇所に集めてある。
    ///
    /// <para>
    /// <c>static</c> にしてあるのは、自己チェックが
    /// <b>プールを通さずに1本だけ設定して確かめられる</b>ようにするため。
    /// Day 48 の <see cref="DebugMenu"/> と同じで、
    /// <b>表そのものをデータとして触れる</b>ようにしておくと、
    /// 「この効果の混ぜ方は何か」を絵を見ずに答えられる。
    /// </para>
    /// </summary>
    /// <returns>撒く数(バースト)。</returns>
    public static int Configure(
        ParticleEmitter emitter,
        EffectKind kind,
        Vector3 position,
        Vector3 normal,
        out ParticleBlend blend,
        out bool useFlipbook)
    {
        Vector3 up = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;

        emitter.Position = position;
        emitter.Direction = up;

        // **一発もの**。撒き続けないので、放出は止めたまま Burst だけで出す。
        emitter.EmitRate = 0.0f;
        emitter.Emitting = false;
        emitter.SpinRange = 1.2f;
        emitter.SizeJitter = 0.35f;

        switch (kind)
        {
            case EffectKind.Landing:
            case EffectKind.FootDust:
                {
                    bool landing = kind == EffectKind.Landing;

                    // 円盤から外へ。**足が着いた面に沿って広がる**のが土埃らしさになる。
                    emitter.Shape = ParticleShape.Disc;
                    emitter.Radius = landing ? 0.34f : 0.16f;
                    emitter.SpeedMin = landing ? 0.9f : 0.4f;
                    emitter.SpeedMax = landing ? 2.1f : 1.0f;
                    emitter.LifetimeMin = landing ? 0.5f : 0.35f;
                    emitter.LifetimeMax = landing ? 1.0f : 0.7f;

                    // **重力はほとんど効かせない**。土埃は舞うもので、落ちるものではない。
                    emitter.Gravity = new Vector3(0.0f, -0.6f, 0.0f);
                    emitter.Drag = 3.2f;

                    emitter.StartSize = landing ? 0.18f : 0.10f;
                    emitter.EndSize = landing ? 0.70f : 0.38f;

                    // 乾いた土の色。**アルファは低め**——濃い煙にすると足元が汚れて見える。
                    emitter.StartColor = new Vector4(0.72f, 0.66f, 0.55f, landing ? 0.75f : 0.45f);
                    emitter.EndColor = new Vector4(0.62f, 0.58f, 0.50f, 0.0f);

                    emitter.FlipbookColumns = 4;
                    emitter.FlipbookRows = 4;

                    blend = ParticleBlend.Alpha;
                    useFlipbook = true;
                    return landing ? 30 : 12;
                }

            case EffectKind.Hit:
                {
                    // 当たった面から**跳ね返る向き**へ。円錐の軸が法線そのものになる。
                    emitter.Shape = ParticleShape.Cone;
                    emitter.ConeAngleDegrees = 62.0f;
                    emitter.Radius = 0.04f;
                    emitter.SpeedMin = 2.2f;
                    emitter.SpeedMax = 5.5f;
                    emitter.LifetimeMin = 0.12f;
                    emitter.LifetimeMax = 0.32f;
                    emitter.Gravity = new Vector3(0.0f, -9.81f, 0.0f);
                    emitter.Drag = 5.0f;
                    emitter.StartSize = 0.10f;
                    emitter.EndSize = 0.01f;
                    emitter.StartColor = new Vector4(1.0f, 0.88f, 0.55f, 1.0f);
                    emitter.EndColor = new Vector4(1.0f, 0.32f, 0.08f, 0.0f);
                    emitter.FlipbookColumns = 1;
                    emitter.FlipbookRows = 1;

                    blend = ParticleBlend.Additive;
                    useFlipbook = false;
                    return 26;
                }

            case EffectKind.Explosion:
                {
                    // 球状に一気に。**抵抗で止める**と「弾けた」形が残る(Day 49 の筋書きと同じ)。
                    emitter.Shape = ParticleShape.Sphere;
                    emitter.Radius = 0.22f;
                    emitter.SpeedMin = 3.0f;
                    emitter.SpeedMax = 9.0f;
                    emitter.LifetimeMin = 0.30f;
                    emitter.LifetimeMax = 0.75f;
                    emitter.Gravity = new Vector3(0.0f, -3.0f, 0.0f);
                    emitter.Drag = 3.0f;
                    emitter.StartSize = 0.36f;
                    emitter.EndSize = 0.04f;
                    emitter.StartColor = new Vector4(1.0f, 0.78f, 0.42f, 1.0f);
                    emitter.EndColor = new Vector4(0.9f, 0.18f, 0.04f, 0.0f);
                    emitter.FlipbookColumns = 1;
                    emitter.FlipbookRows = 1;

                    blend = ParticleBlend.Additive;
                    useFlipbook = false;
                    return 140;
                }

            default:
                {
                    // 爆発のあとに残る煙。**閃光より寿命が長く、上へ昇る**。
                    emitter.Shape = ParticleShape.Sphere;
                    emitter.Radius = 0.3f;
                    emitter.SpeedMin = 0.4f;
                    emitter.SpeedMax = 1.8f;
                    emitter.LifetimeMin = 0.9f;
                    emitter.LifetimeMax = 1.8f;

                    // **正の重力**。熱い煙は浮くので、浮力の代わりに符号を反転させる。
                    emitter.Gravity = new Vector3(0.0f, 0.7f, 0.0f);
                    emitter.Drag = 1.4f;
                    emitter.StartSize = 0.30f;
                    emitter.EndSize = 1.10f;
                    emitter.StartColor = new Vector4(0.26f, 0.24f, 0.22f, 0.55f);
                    emitter.EndColor = new Vector4(0.34f, 0.33f, 0.32f, 0.0f);
                    emitter.FlipbookColumns = 4;
                    emitter.FlipbookRows = 4;

                    blend = ParticleBlend.Alpha;
                    useFlipbook = true;
                    return 40;
                }
        }
    }

    private void SpawnOne(EffectKind kind, Vector3 position, Vector3 normal)
    {
        Slot? slot = null;

        foreach (Slot candidate in _slots)
        {
            if (!candidate.Active)
            {
                slot = candidate;
                break;
            }
        }

        if (slot is null)
        {
            // **黙って古いものを潰さない**。潰すと、いちばん見てほしい爆発が
            // 足音に横取りされて消える。出せなかったことを数えて HUD に出す。
            DroppedSpawns++;
            return;
        }

        slot.Emitter.Clear();

        int burst = Configure(
            slot.Emitter, kind, position, normal, out ParticleBlend blend, out bool flipbook);

        slot.Kind = kind;
        slot.Blend = blend;
        slot.UseFlipbook = flipbook;
        slot.Active = true;

        slot.Emitter.Burst(burst);

        ActiveCount++;
        TotalSpawns++;
    }

    private void DrawGroup(ParticleRenderer renderer, ParticleBlend blend, Texture dot, Texture smoke)
    {
        foreach (Slot slot in _slots)
        {
            if (!slot.Active || slot.Blend != blend)
            {
                continue;
            }

            renderer.Draw(slot.Emitter, slot.UseFlipbook ? smoke : dot, blend);
        }
    }
}
