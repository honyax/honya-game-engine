using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **操作されるキャラクターの見た目**(Day 51)。今日の主役その3。
///
/// <para>
/// Day 45 のキャラクターは<b>カプセル1本</b>だった。当たり判定の形そのものを
/// 描いていたので、めり込めば絵でもめり込んで見えた——
/// <c>RenderCharacter</c> の説明に「Day 51 でモデルを載せると、この一致は失われる」
/// と書いてあったのが今日のこと。
/// </para>
///
/// <para>
/// <b>2つを結ぶ道は3本しかない</b>。
/// </para>
/// <list type="number">
/// <item><b>置く</b> … 足元 <see cref="CharacterController.Position"/> と向き <c>FacingYaw</c> から世界行列を作る</item>
/// <item><b>混ぜる</b> … <see cref="CharacterController.HorizontalSpeed"/> をブレンド木(Day 42)に渡す</item>
/// <item><b>合わせる</b> … モデルの実寸をカプセルの高さに合わせる倍率を1回だけ測る</item>
/// </list>
///
/// <para>
/// <b>逆向きの矢印は無い</b>——モデルは当たり判定に一切影響しない。
/// キツネが尻尾を振っても、前脚を伸ばしても、世界と当たるのはカプセルのまま。
/// これは手抜きではなく<b>意図した割り切り</b>で、
/// 実際のアクションゲームもほぼこの形になっている
/// (攻撃判定だけ骨に付ける、というのが次の段になる)。
/// </para>
///
/// <para>
/// <b>ブレンド木は外から受け取る</b>。どのクリップが「歩き」かを名前で探すのは
/// <c>Program.BuildLocomotion</c> がとうにやっているので、
/// 同じ判断をここでもう一度書かない——
/// 探し方が2つあると、Day 42 の見比べと今日の実プレイで
/// <b>別のクリップが選ばれる</b>という、いちばん気づきにくい食い違いが起きる。
/// </para>
/// </summary>
internal sealed class PlayableDemo : IDisposable
{
    private readonly ClipWeight[] _weights = new ClipWeight[AnimationPlayer.MaxBlendClips];

    /// <summary>メッシュ空間で「足元を原点、水平は中心」へ運ぶ平行移動。</summary>
    private Vector3 _align;

    private bool _disposed;

    public PlayableDemo(Model model, BlendTree1D? locomotion)
    {
        Model = model;
        Animation = new AnimationPlayer(model);
        Locomotion = locomotion;

        // **位相を合わせる**(Day 42)。歩きと走りを混ぜるので、
        // 同期していないと前脚が2本ぶん見えるような絵になる。
        Animation.SyncPhase = true;
    }

    public Model Model { get; }

    public AnimationPlayer Animation { get; }

    /// <summary>速度からクリップの重みを決める木(Day 42)。無ければクリップ1本で回る。</summary>
    public BlendTree1D? Locomotion { get; }

    /// <summary>メッシュ空間から世界へ持ってくる倍率。<see cref="Fit"/> が決める。</summary>
    public float Scale { get; private set; } = 1.0f;

    /// <summary>合わせた先の全高 [m]。**カプセルの高さと同じ**。</summary>
    public float Height { get; private set; } = 1.8f;

    /// <summary>合わせた先の胴の半径 [m]。**カプセルの半径に使う**。</summary>
    public float Radius { get; private set; } = 0.35f;

    /// <summary>いまの世界行列。<see cref="Advance"/> が毎フレーム作り直す。</summary>
    public Matrix4x4 Transform { get; private set; } = Matrix4x4.Identity;

    /// <summary>直前に木へ渡した速度 [m/s]。**HUD 用**。</summary>
    public float LastSpeed { get; private set; }

    /// <summary>
    /// **モデルの実寸をカプセルに合わせる**。読み込んだ直後に1回だけ呼ぶ。
    ///
    /// <para>
    /// <paramref name="min"/> と <paramref name="max"/> には
    /// <b>今のポーズで測った箱</b>を渡す(<c>Program.MeasurePosedBounds</c>)。
    /// バインドポーズの箱を使ってはいけない理由は Day 41 の <c>FrameModel</c> と同じで、
    /// スキン付きのパーツはメッシュ空間の座標がそのまま入っており、
    /// 立たせる回転を関節行列の側が持っていることがある。
    /// </para>
    ///
    /// <para>
    /// <b>半径は細いほうの辺から取る</b>。キツネは鼻から尻尾まで 1.1m あるが、
    /// 幅は 25cm しかない。長いほうを採ると<b>半径 55cm の樽</b>になり、
    /// 狭い場所へ入れなくなる。カプセルは「体の芯」を包むものなので、
    /// 細いほうに合わせて、上下は <see cref="Height"/> が受け持つ。
    /// </para>
    ///
    /// <para>
    /// <b>倍率は高さで決める</b>。glTF の単位は約束が無く、
    /// Fox は cm(全高 70)で作られている。
    /// 「目標の高さ ÷ 実寸の高さ」なら、どの単位で作られたモデルでも同じ大きさに揃う。
    /// </para>
    /// </summary>
    /// <param name="min">今のポーズでの境界箱(メッシュ空間)。</param>
    /// <param name="max">同上。</param>
    /// <param name="targetHeight">世界での全高 [m]。</param>
    public void Fit(Vector3 min, Vector3 max, float targetHeight)
    {
        Vector3 size = Vector3.Max(max - min, new Vector3(1e-4f));

        Scale = targetHeight / size.Y;
        Height = targetHeight;

        // 細いほうの水平の辺の半分。極端な値にならないよう挟んでおく。
        Radius = Math.Clamp(MathF.Min(size.X, size.Z) * 0.5f * Scale, 0.12f, targetHeight * 0.35f);

        // **足元を原点へ、水平は中心へ**。<c>DemoScene.AlignToGround</c> と同じ考え方で、
        // これをやっておくと <see cref="CharacterController.Position"/>(足元)を
        // そのまま平行移動に使える。
        _align = new Vector3(
            -(min.X + max.X) * 0.5f,
            -min.Y,
            -(min.Z + max.Z) * 0.5f);
    }

    /// <summary>
    /// 1フレームぶん進める。**可変 dt で呼ぶ**。
    ///
    /// <para>
    /// <b>重みを先に決めてから時刻を進める</b>(Day 42 と同じ順)。
    /// 逆にすると、速度が変わったフレームだけ1つ古い重みで描かれる。
    /// </para>
    ///
    /// <para>
    /// <b>並べる順は 足元合わせ → 倍率 → 向き → 位置</b>。
    /// 向きより先に位置を掛けると、キャラクターが世界の原点のまわりを公転する。
    /// 倍率より先に位置を掛けると、歩いた距離まで倍率が掛かる——
    /// どちらも「絵は出るが動きが変」という形で出るので、順番はここに固定しておく。
    /// </para>
    /// </summary>
    /// <param name="character">位置・向き・速さの出どころ。</param>
    /// <param name="deltaSeconds">可変 dt。</param>
    /// <param name="blend">
    /// 速度でクリップを混ぜるか。**切ると立ち止まりのクリップに固定**され、
    /// 走っていても足が止まる——ブレンドが絵のどこを支えているかが分かる。
    /// </param>
    public void Advance(CharacterController character, float deltaSeconds, bool blend)
    {
        LastSpeed = character.HorizontalSpeed;

        if (Locomotion is not null)
        {
            // 混ぜないときは木の左端(速度 0 = 立ち止まり)を引く。
            int count = Locomotion.Evaluate(blend ? LastSpeed : 0.0f, _weights);

            if (count > 0)
            {
                Animation.SetBlend(_weights.AsSpan(0, count));
            }
        }

        Animation.Update(deltaSeconds);

        Transform =
            Matrix4x4.CreateTranslation(_align)
            * Matrix4x4.CreateScale(Scale)
            * Matrix4x4.CreateRotationY(character.FacingYaw)
            * Matrix4x4.CreateTranslation(character.Position);
    }

    /// <summary>
    /// パーツを置く行列。**<c>Program.PartMatrix</c> と同じ3つの場合分け**(Day 41)。
    ///
    /// <para>
    /// スキン付きのパーツは単位行列で、位置は関節行列が持っている。
    /// スキンを持たないパーツ(Fox には無いが、モデルを差し替えると出てくる)は
    /// プレイヤーが計算したノードの世界行列を使う。
    /// </para>
    /// </summary>
    public Matrix4x4 PartMatrix(in Model.Part part)
    {
        Matrix4x4 local = part.SkinIndex >= 0
            ? Matrix4x4.Identity
            : Model.Animations.Count > 0
                ? Animation.GetNodeWorld(part.NodeIndex)
                : part.Transform;

        return local * Transform;
    }

    /// <summary>パーツに送る関節行列。スキン無しなら空(受け取った側が <c>uSkinned = 0</c> にする)。</summary>
    public ReadOnlySpan<Matrix4x4> PartJoints(in Model.Part part) =>
        part.SkinIndex >= 0 ? Animation.GetJointMatrices(part.SkinIndex) : default;

    /// <summary>いま混ざっているクリップを「歩き0.42+走り0.58」の形に(HUD 用)。</summary>
    public string BlendLabel()
    {
        ReadOnlySpan<ClipWeight> blend = Animation.Blend;

        if (blend.Length == 0)
        {
            return "なし";
        }

        var text = new System.Text.StringBuilder();

        for (int i = 0; i < blend.Length; i++)
        {
            if (i > 0)
            {
                text.Append('+');
            }

            text.Append($"{Model.Animations[blend[i].Clip].Name}{blend[i].Weight:F2}");
        }

        return text.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Model.Dispose();
    }
}
