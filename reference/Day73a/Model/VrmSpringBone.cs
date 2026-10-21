using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace HonyaEngine;

/// <summary>
/// **VRM 1.0 の揺れもの**(<c>VRMC_springBone</c>。Day 70)。髪・胸・フードの紐を、体の動きに遅れてついて来させる。
///
/// <para>
/// <b>物理エンジンではない</b>。Day 43〜47 の剛体のような質量・慣性テンソル・接触の連立は持たず、
/// 関節の<b>先端の点1つ</b>を Verlet 積分で動かすだけ。点は次の4つで決まる。
/// </para>
/// <list type="number">
/// <item><b>慣性</b> … 前のステップに動いた分だけ、今のステップも動く(<c>dragForce</c> でその分を削る)</item>
/// <item><b>硬さ</b> … ファイルの姿勢の向きへ引き戻す(<c>stiffness</c>)</item>
/// <item><b>重力</b> … 決めた向きへ引く(<c>gravityPower</c> と <c>gravityDir</c>)</item>
/// <item><b>長さの拘束とコライダー</b> … 骨の長さへ引き戻し、体の球・カプセルから押し出す</item>
/// </list>
///
/// <para>
/// 決まった先端の点から<b>骨の回転</b>を逆算して <see cref="AnimationPlayer"/> の姿勢へ書き込む。
/// 頂点は今までどおり関節行列で曲がる(髪は顔と同じスキン)ので、<b>描く側は揺れものを知らない</b>。
/// 式と順番は three-vrm の <c>VRMSpringBoneJoint.update</c> に合わせた。
/// </para>
///
/// <para>
/// <b>体が動かないと揺れない</b>。じっと立っていれば、先端はファイルの姿勢の位置で止まったまま
/// (硬さが戻す先が今の位置なので。コライダーにめり込んでいる毛先だけは、押し出された所で止まる)。
/// 揺れるのは、体が動いて「戻す先」が先端から離れたときだけ。
/// </para>
/// </summary>
internal sealed class VrmSpringBone
{
    /// <summary>
    /// **1ステップの長さ**(60 Hz)。描くフレームの長さ(可変 dt)ではなく、この固定の刻みで進める。
    ///
    /// <para>
    /// 慣性を削る <c>dragForce</c> は<b>1ステップあたりの割合</b>(0.4 なら毎ステップ 40% 削る)。
    /// 可変 dt のまま回すと、144 fps の画面では 1 秒に 144 回削られ、30 fps では 30 回しか削られない——
    /// 同じアバターが画面の速さで違う揺れ方になる(計画書の要点5)。Day 19 の固定タイムステップと同じ考え方で、
    /// あちらは描くときに<b>状態</b>を補間したが、こちらは揺らすときに<b>体の動き</b>を補間して渡す(<see cref="OuterWorld"/>)。
    /// </para>
    /// </summary>
    public const float StepSeconds = 1.0f / 60.0f;

    /// <summary>1フレームで回すステップの上限。読み込みで止まったフレームの後に、溜まった分を一気に回さない。</summary>
    public const int MaxStepsPerFrame = 4;

    /// <summary>
    /// 関節1つの揺れ方(ファイルの <c>joints[]</c> の1要素)。省いた値は仕様の既定値。
    /// </summary>
    /// <param name="HitRadius">先端の点の半径。コライダーとはこの分だけ離れる。</param>
    /// <param name="Stiffness">ファイルの姿勢の向きへ戻す強さ(1 秒あたりの距離。メートル)。</param>
    /// <param name="GravityPower">重力の強さ(同じく 1 秒あたりの距離)。</param>
    /// <param name="GravityDir">重力の向き(世界の向き)。</param>
    /// <param name="DragForce">慣性を削る割合(1ステップあたり。0 で削らない、1 で慣性なし)。</param>
    internal readonly record struct JointSettings(
        float HitRadius, float Stiffness, float GravityPower, Vector3 GravityDir, float DragForce)
    {
        /// <summary>仕様の既定値(VRMC_springBone.joint.schema.json)。</summary>
        public static JointSettings Default => new(0.0f, 1.0f, 0.0f, -Vector3.UnitY, 0.5f);
    }

    /// <summary>
    /// コライダー1つ。ノードに付いた**球**か**カプセル**。<paramref name="Tail"/> が null なら球。
    /// </summary>
    /// <param name="Node">付いているノード。ノードと一緒に動く。</param>
    /// <param name="Offset">ノードのローカルでの中心(カプセルなら片方の端)。</param>
    /// <param name="Radius">半径。</param>
    /// <param name="Tail">カプセルのもう片方の端(ノードのローカル)。球なら null。</param>
    internal sealed record Collider(int Node, Vector3 Offset, float Radius, Vector3? Tail)
    {
        public bool IsCapsule => Tail is not null;
    }

    /// <summary>
    /// コライダーのまとまり。鎖は**まとまりの番号**で「どれに当たるか」を選ぶ。
    /// 中身は試しで差し替えられる(<see cref="Colliders"/>)。ファイルの中身は <see cref="FileColliders"/> に残る。
    /// </summary>
    internal sealed class ColliderGroup(string name, IReadOnlyList<Collider> colliders)
    {
        public string Name { get; } = name;

        public IReadOnlyList<Collider> FileColliders { get; } = colliders;

        public IReadOnlyList<Collider> Colliders { get; set; } = colliders;
    }

    /// <summary>
    /// **関節1つ**。骨 <see cref="Node"/> を、その子 <see cref="ChildNode"/> の向きを先端として揺らす。
    /// 鎖の最後の関節は先端の印で、揺れない(<see cref="Spring.Joints"/> に入れない)。
    /// </summary>
    internal sealed class Joint
    {
        public Joint(Model model, int node, int childNode, JointSettings settings)
        {
            Node = node;
            ChildNode = childNode;
            Parent = model.Nodes[node].Parent;
            RestRotation = model.Nodes[node].RestPose.Rotation;
            ChildLocal = model.Nodes[childNode].RestPose.Translation;
            BoneAxis = ChildLocal.LengthSquared() > 1e-12f ? Vector3.Normalize(ChildLocal) : Vector3.UnitY;
            FileSettings = settings;
            Settings = settings;
            Rotation = RestRotation;
        }

        public int Node { get; }

        public int ChildNode { get; }

        public int Parent { get; }

        /// <summary>ファイルの姿勢の回転。揺れの回転は、毎回これに「曲げた分」を掛けて作る(前のステップの回転に積まない)。</summary>
        public Quaternion RestRotation { get; }

        /// <summary>子のノードの位置(この骨のローカル)。骨の長さと向きはここから決まる。</summary>
        public Vector3 ChildLocal { get; }

        /// <summary>骨の向き(ローカル)。<see cref="ChildLocal"/> を長さ 1 にしたもの。</summary>
        public Vector3 BoneAxis { get; }

        public JointSettings FileSettings { get; }

        /// <summary>いまの揺れ方。試し(硬さ・抵抗)はここを書き換える。</summary>
        public JointSettings Settings { get; set; }

        /// <summary>
        /// **先端の点**(center の座標)。Verlet 積分は速度を持たず、今と1つ前の位置の差を速度の代わりにする。
        /// </summary>
        public Vector3 CurrentTail { get; set; }

        public Vector3 PreviousTail { get; set; }

        /// <summary>最後に決めたローカルの回転。ステップを回さないフレームも、これを姿勢へ書き戻す。</summary>
        public Quaternion Rotation { get; set; }

        /// <summary>ファイルの姿勢から何度曲がっているか(HUD と自己チェック用)。</summary>
        public float BendDegrees { get; set; }

        /// <summary>
        /// 先端の速さ(m/s。center の座標で)。<b>揺れているかどうかはこちらで見る</b>——曲がりは、コライダーに押されて
        /// 止まっているだけでも大きく出る。center ごと運ばれた動きは、ここに入らない。
        /// </summary>
        public float TailSpeed { get; set; }
    }

    /// <summary>
    /// **鎖1本**(ファイルの <c>springs[]</c> の1要素)。根から先へ並んだ関節と、当たるコライダーのまとまり、center。
    /// </summary>
    internal sealed class Spring(string name, Joint[] joints, ColliderGroup[] groups, int center)
    {
        public string Name { get; } = name;

        /// <summary>揺れる関節。根から先の順(この順に決めないと、子が古い親を見る)。</summary>
        public Joint[] Joints { get; } = joints;

        public ColliderGroup[] Groups { get; } = groups;

        /// <summary>ファイルの center。-1 なら無し(世界の座標で揺らす)。</summary>
        public int FileCenter { get; } = center;

        /// <summary>
        /// **いまの center**。試しで -1 にできる。先端の点はこの座標で覚えているので、変わったら置き直す。
        /// </summary>
        public int Center { get; set; } = center;

        /// <summary>先端の点を、どの center の座標で覚えているか。<see cref="Center"/> と違えば置き直す。</summary>
        internal int StateCenter { get; set; } = int.MinValue;
    }

    private float _accumulator;

    /// <summary>
    /// **前のフレームの、鎖の外の世界行列**(鎖の根の親・center・コライダーのノード。置いた先)。
    /// 1フレームに何ステップか回すとき、ステップの時刻の体を前のフレームとの間で補間して作る(<see cref="OuterWorld"/>)。
    /// キーの -1 は「親が無い」= 置き場所の行列。
    /// </summary>
    private Dictionary<int, Matrix4x4> _previousWorld = [];

    private Dictionary<int, Matrix4x4> _currentWorld = [];

    public VrmSpringBone(IReadOnlyList<Spring> springs, IReadOnlyList<ColliderGroup> groups)
    {
        Springs = springs;
        Groups = groups;
    }

    public IReadOnlyList<Spring> Springs { get; }

    public IReadOnlyList<ColliderGroup> Groups { get; }

    /// <summary>揺らすか。切るとファイル(アニメーション)の姿勢のまま。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>コライダーで押し出すか。</summary>
    public bool CollidersEnabled { get; set; } = true;

    /// <summary>揺れる関節の数(鎖の先端の印を除く)。</summary>
    public int JointCount => Springs.Sum(spring => spring.Joints.Length);

    /// <summary>最後のフレームで回したステップの数(60 fps なら 1、30 fps なら 2、144 fps ならたいてい 0)。</summary>
    public int LastSteps { get; private set; }

    /// <summary>最後のフレームでコライダーに押し出された回数。</summary>
    public int LastHits { get; private set; }

    /// <summary>最後のフレームにかかった時間(ms)。</summary>
    public double LastMilliseconds { get; private set; }

    /// <summary>いちばん曲がっている関節の角度(度)。</summary>
    public float MaxBendDegrees => Springs.SelectMany(spring => spring.Joints).Select(joint => joint.BendDegrees).DefaultIfEmpty().Max();

    /// <summary>いちばん速く動いている先端の速さ(m/s。center の座標で)。じっとしていれば 0。</summary>
    public float MaxTailSpeed => Springs.SelectMany(spring => spring.Joints).Select(joint => joint.TailSpeed).DefaultIfEmpty().Max();

    /// <summary>次のフレームで、先端の点を全部ファイルの姿勢の位置へ置き直す。</summary>
    public void Reset()
    {
        foreach (Spring spring in Springs)
        {
            spring.StateCenter = int.MinValue;
        }

        _accumulator = 0.0f;
    }

    /// <summary>
    /// **1フレームぶん揺らす**。<paramref name="pose"/> はアニメーションを当て終えた姿勢。
    /// 髪の回転を書き込み、最後に世界行列と関節行列を作り直す。
    ///
    /// <para>
    /// <paramref name="placement"/> はアバターを置く行列。<b>揺れは置いた先の世界で計算する</b>——
    /// ノードの世界行列(<see cref="AnimationPlayer.GetNodeWorld"/>)は置く前の値なので、そのままでは
    /// 「置き場所ごと回した」動きが揺れに入らない。入れるかどうかを決めるのは center(要点4)。
    /// </para>
    /// </summary>
    public void Update(AnimationPlayer pose, Matrix4x4 placement, float deltaSeconds)
    {
        if (!Enabled)
        {
            // 姿勢には何も書かない(アニメーションの値のまま)。次に入れたとき、先端をその場から始め直す。
            Reset();
            _previousWorld.Clear();
            LastSteps = 0;
            LastHits = 0;
            foreach (Joint joint in Springs.SelectMany(spring => spring.Joints))
            {
                joint.BendDegrees = 0.0f;
                joint.TailSpeed = 0.0f;
            }

            return;
        }

        long start = Stopwatch.GetTimestamp();

        // **溜めた時間を固定の刻みで払う**(StepSeconds のコメント)。払いきれない端数は次のフレームへ持ち越す。
        // 数えるときは 10 マイクロ秒だけ甘くする。1/144 を 288 回足すと 2 秒にわずかに届かず、120 回目を取りこぼすので。
        float dt = MathF.Max(deltaSeconds, 0.0f);
        _accumulator = MathF.Min(_accumulator + dt, StepSeconds * MaxStepsPerFrame);
        int steps = (int)((_accumulator + 1e-5f) / StepSeconds);
        _accumulator = MathF.Max(_accumulator - (steps * StepSeconds), 0.0f);

        LastSteps = steps;
        LastHits = 0;

        // **鎖の外の世界行列を、このフレームのぶん集めておく**(鎖の根の親・center・コライダーのノード)。
        // 鎖の中はここから掛け進めるので、揺れものが読む体の動きはこれで全部になる。
        _currentWorld.Clear();
        _currentWorld[-1] = placement;
        foreach (Spring spring in Springs)
        {
            if (spring.Joints.Length > 0)
            {
                Capture(pose, placement, spring.Joints[0].Parent);
            }

            Capture(pose, placement, spring.Center);
            foreach (ColliderGroup group in spring.Groups)
            {
                foreach (Collider collider in group.Colliders)
                {
                    Capture(pose, placement, collider.Node);
                }
            }
        }

        foreach (Spring spring in Springs)
        {
            if (spring.Joints.Length == 0)
            {
                continue;
            }

            if (spring.StateCenter != spring.Center)
            {
                ResetSpring(pose, spring);
            }

            for (int step = 0; step < steps; step++)
            {
                // **ステップの時刻へ体を戻す**。このフレームの終わりから「持ち越した端数 + 後ろに控えたステップの分」だけ前。
                // 30 fps なら1フレームに2回回すが、1回目は前のフレームとの真ん中の体を見る。両方とも今の体で回すと、
                // 体の動き2回分が1回目に一度に入り、揺れ方が 60 fps と大きく変わる(計画書の要点5)。
                float behind = _accumulator + ((steps - 1 - step) * StepSeconds);
                float alpha = dt > 0.0f ? Math.Clamp(1.0f - (behind / dt), 0.0f, 1.0f) : 1.0f;
                StepSpring(pose, spring, alpha);
            }

            // **ステップを回さないフレームも、決めた回転を書き戻す**。pose はフレームの頭でクリップ(無ければファイル)の値に
            // 戻っているので、書かないと髪がそのフレームだけファイルの姿勢へ跳ね戻る。
            foreach (Joint joint in spring.Joints)
            {
                NodePose local = pose.GetLocalPose(joint.Node);
                local.Rotation = joint.Rotation;
                pose.SetLocalPose(joint.Node, local);
            }
        }

        pose.UpdateWorldAndJoints();
        (_previousWorld, _currentWorld) = (_currentWorld, _previousWorld);
        LastMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>ノードの世界行列(置いた先)を、このフレームの値として覚える。-1 は「親が無い」= 置き場所そのもの。</summary>
    private void Capture(AnimationPlayer pose, Matrix4x4 placement, int node)
    {
        if (node >= 0)
        {
            _currentWorld[node] = pose.GetNodeWorld(node) * placement;
        }
    }

    /// <summary>
    /// **鎖の外のノードの世界行列を、ステップの時刻で引く**。前のフレームの値と今の値を <paramref name="alpha"/> で混ぜる。
    /// 行列の線形補間は回転を少し縮めるが、1フレームの間の動き(数度)なら目に見えない。
    /// </summary>
    private Matrix4x4 OuterWorld(int node, float alpha)
    {
        Matrix4x4 current = _currentWorld[node];
        return alpha < 1.0f && _previousWorld.TryGetValue(node, out Matrix4x4 previous)
            ? Matrix4x4.Lerp(previous, current, alpha)
            : current;
    }

    /// <summary>
    /// **center の世界行列**。先端の点はこの座標で覚える。center が無ければ世界そのもの(単位行列)。
    /// center のノードが動いた分は、先端の点も一緒に運ばれる——つまり<b>揺れに入らない</b>(要点4)。
    /// </summary>
    private Matrix4x4 CenterWorld(Spring spring, float alpha) =>
        spring.Center >= 0 ? OuterWorld(spring.Center, alpha) : Matrix4x4.Identity;

    /// <summary>
    /// **先端の点を、いまの姿勢のままの位置へ置く**(速度 0)。読み込んだとき・center を変えたとき・揺れを切ったあと。
    /// </summary>
    private void ResetSpring(AnimationPlayer pose, Spring spring)
    {
        Matrix4x4.Invert(CenterWorld(spring, 1.0f), out Matrix4x4 worldToCenter);
        Matrix4x4 parentWorld = OuterWorld(spring.Joints[0].Parent, 1.0f);

        foreach (Joint joint in spring.Joints)
        {
            NodePose local = pose.GetLocalPose(joint.Node);
            local.Rotation = joint.RestRotation;
            Matrix4x4 restWorld = local.ToMatrix() * parentWorld;

            Vector3 tail = Vector3.Transform(Vector3.Transform(joint.ChildLocal, restWorld), worldToCenter);
            joint.CurrentTail = tail;
            joint.PreviousTail = tail;
            joint.Rotation = joint.RestRotation;
            joint.BendDegrees = 0.0f;
            joint.TailSpeed = 0.0f;
            parentWorld = restWorld;
        }

        spring.StateCenter = spring.Center;
    }

    /// <summary>
    /// **鎖1本を1ステップ進める**。根から先へ。鎖の世界行列は<b>鎖の中で掛け進める</b>——根の親(鎖の外。補間した値)から始め、
    /// 関節を1つ決めるたびに、決めた回転で1段降りる。次の関節は、いま決めた関節を親として読む。
    /// </summary>
    private void StepSpring(AnimationPlayer pose, Spring spring, float alpha)
    {
        Matrix4x4 centerWorld = CenterWorld(spring, alpha);
        Matrix4x4.Invert(centerWorld, out Matrix4x4 worldToCenter);
        Matrix4x4 parentWorld = OuterWorld(spring.Joints[0].Parent, alpha);

        foreach (Joint joint in spring.Joints)
        {
            JointSettings settings = joint.Settings;

            // --- 1. ファイルの回転のままの骨(戻す先)---
            //
            // 親は今の値(体・鎖の1つ前の関節の結果)。自分の回転だけファイルの値にする。移動と拡大はアニメーションの値のまま。
            NodePose local = pose.GetLocalPose(joint.Node);
            local.Rotation = joint.RestRotation;
            Matrix4x4 restWorld = local.ToMatrix() * parentWorld;
            Vector3 root = restWorld.Translation;
            Vector3 restTail = Vector3.Transform(joint.ChildLocal, restWorld);
            float length = Vector3.Distance(root, restTail);

            // --- 2. 先端を動かす(center の座標で)---
            Vector3 restDirection = Normalize(Vector3.TransformNormal(restTail - root, worldToCenter));
            Vector3 gravity = Normalize(Vector3.TransformNormal(settings.GravityDir, worldToCenter));
            Vector3 next = Integrate(
                joint.CurrentTail, joint.PreviousTail, restDirection, gravity, settings, StepSeconds);

            // --- 3. 世界へ戻して、長さとコライダー ---
            Vector3 nextWorld = ConstrainLength(Vector3.Transform(next, centerWorld), root, length);

            if (CollidersEnabled)
            {
                LastHits += Collide(spring, root, length, settings.HitRadius, alpha, ref nextWorld);
            }

            joint.PreviousTail = joint.CurrentTail;
            joint.CurrentTail = Vector3.Transform(nextWorld, worldToCenter);
            joint.TailSpeed = Vector3.Distance(joint.CurrentTail, joint.PreviousTail) / StepSeconds;

            // --- 4. 先端の向きから回転を逆算する ---
            //
            // 先端をファイルの回転の骨のローカルへ持ち込み、「骨の向き → 先端の向き」の回転を掛ける。
            // **いつもファイルの回転から作る**ので、前のステップの誤差が回転に積もらない。
            Matrix4x4.Invert(restWorld, out Matrix4x4 restWorldInverse);
            Vector3 to = Normalize(Vector3.Transform(nextWorld, restWorldInverse));
            joint.Rotation = Quaternion.Normalize(joint.RestRotation * FromTo(joint.BoneAxis, to));
            joint.BendDegrees = MathF.Acos(Math.Clamp(Vector3.Dot(joint.BoneAxis, to), -1.0f, 1.0f)) * (180.0f / MathF.PI);

            local.Rotation = joint.Rotation;
            parentWorld = local.ToMatrix() * parentWorld;
        }
    }

    /// <summary>
    /// **コライダーから押し出す**。めり込んでいたら外へ出し、長さの拘束をかけ直す。押し出した回数を返す。
    /// 1つ押し出したあとの点で次のコライダーを見るので、並び順で結果がわずかに変わる(three-vrm と同じ)。
    /// </summary>
    private int Collide(Spring spring, Vector3 root, float length, float hitRadius, float alpha, ref Vector3 tail)
    {
        int hits = 0;

        foreach (ColliderGroup group in spring.Groups)
        {
            foreach (Collider collider in group.Colliders)
            {
                Matrix4x4 world = OuterWorld(collider.Node, alpha);
                Vector3 offset = Vector3.Transform(collider.Offset, world);

                float distance = collider.Tail is Vector3 colliderTail
                    ? CapsuleDistance(offset, Vector3.Transform(colliderTail, world), collider.Radius, tail, hitRadius, out Vector3 normal)
                    : SphereDistance(offset, collider.Radius, tail, hitRadius, out normal);

                if (distance < 0.0f)
                {
                    tail = ConstrainLength(tail - (normal * distance), root, length);
                    hits++;
                }
            }
        }

        return hits;
    }

    // ============================================================
    //  式(状態を持たない。自己チェックがそのまま呼ぶ)
    // ============================================================

    /// <summary>
    /// **Verlet 積分の1ステップ**。three-vrm と同じ形。
    /// <code>
    ///   次 = 今 + (今 - 前) x (1 - dragForce)          慣性
    ///        + 戻す向き x stiffness x dt               硬さ
    ///        + 重力の向き x gravityPower x dt           重力
    /// </code>
    /// <para>
    /// 硬さと重力は<b>力ではなく移動量</b>(dt を1回掛けるだけで、dt² ではない)。名前は力でも、
    /// 「1 秒あたりどれだけ寄せるか」の速さとして効く。仕様もこの形で値を決めている。
    /// </para>
    /// </summary>
    public static Vector3 Integrate(
        Vector3 current, Vector3 previous, Vector3 restDirection, Vector3 gravityDirection, in JointSettings settings, float dt) =>
        current
        + ((current - previous) * (1.0f - settings.DragForce))
        + (restDirection * (settings.Stiffness * dt))
        + (gravityDirection * (settings.GravityPower * dt));

    /// <summary>**長さの拘束**。先端を根からの向きのまま、骨の長さの所へ戻す。根に重なったときは動かさない。</summary>
    public static Vector3 ConstrainLength(Vector3 tail, Vector3 root, float length)
    {
        Vector3 offset = tail - root;
        float current = offset.Length();
        return current > 1e-9f ? root + (offset * (length / current)) : tail;
    }

    /// <summary>
    /// **球との距離**(表面どうし)。負ならめり込んでいて、<paramref name="normal"/> が押し出す向き(球の中心から外向き)。
    /// </summary>
    public static float SphereDistance(Vector3 center, float radius, Vector3 point, float pointRadius, out Vector3 normal)
    {
        Vector3 offset = point - center;
        float length = offset.Length();
        normal = length > 1e-9f ? offset / length : Vector3.UnitY;
        return length - radius - pointRadius;
    }

    /// <summary>
    /// **カプセルとの距離**。線分 head–tail のうち点にいちばん近い所を中心にした球と見なす。
    /// 近い所は「線分への射影」を両端で切ったもの(Day 45 の線分と点の距離と同じ)。
    /// </summary>
    public static float CapsuleDistance(
        Vector3 head, Vector3 tail, float radius, Vector3 point, float pointRadius, out Vector3 normal)
    {
        Vector3 segment = tail - head;
        float lengthSquared = segment.LengthSquared();
        float t = lengthSquared > 1e-12f ? Math.Clamp(Vector3.Dot(point - head, segment) / lengthSquared, 0.0f, 1.0f) : 0.0f;
        return SphereDistance(head + (segment * t), radius, point, pointRadius, out normal);
    }

    /// <summary>
    /// 単位ベクトル <paramref name="from"/> を <paramref name="to"/> へ回す最小の回転(three.js の setFromUnitVectors)。
    /// 真逆のときは、from に垂直な軸のどれかで 180 度回す。
    /// </summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float dot = Vector3.Dot(from, to);
        if (dot < -0.999999f)
        {
            Vector3 axis = MathF.Abs(from.X) > MathF.Abs(from.Z)
                ? new Vector3(-from.Y, from.X, 0.0f)
                : new Vector3(0.0f, -from.Z, from.Y);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        Vector3 cross = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross, 1.0f + dot));
    }

    private static Vector3 Normalize(Vector3 value) =>
        value.LengthSquared() > 1e-18f ? Vector3.Normalize(value) : Vector3.Zero;

    // ============================================================
    //  ファイルから読む
    // ============================================================

    /// <summary>
    /// **<c>VRMC_springBone</c> を読む**。無ければ鎖 0 本(揺れないアバター)。ノードを番号で指しているので、
    /// <see cref="Model"/> を読み終えてから呼ぶ(表情と同じ。<see cref="VrmAvatar.Load"/>)。
    /// </summary>
    public static VrmSpringBone Read(JsonElement source, Model model)
    {
        if (source.ValueKind != JsonValueKind.Object)
        {
            return new VrmSpringBone([], []);
        }

        var colliders = new List<Collider>();
        foreach (JsonElement entry in Array(source, "colliders"))
        {
            colliders.Add(ReadCollider(entry));
        }

        var groups = new List<ColliderGroup>();
        foreach (JsonElement entry in Array(source, "colliderGroups"))
        {
            Collider[] members = Array(entry, "colliders")
                .Select(index => index.GetInt32())
                .Where(index => (uint)index < (uint)colliders.Count)
                .Select(index => colliders[index])
                .ToArray();
            groups.Add(new ColliderGroup(Text(entry, "name", $"group{groups.Count}"), members));
        }

        var springs = new List<Spring>();
        foreach (JsonElement entry in Array(source, "springs"))
        {
            JsonElement[] joints = Array(entry, "joints").ToArray();
            string name = Text(entry, "name", $"spring{springs.Count}");

            // **最後の関節は先端の印**。揺らすのは「自分と次」の組がある関節だけ(three-vrm と同じ読み方)。
            var built = new List<Joint>();
            for (int i = 0; i + 1 < joints.Length; i++)
            {
                int node = Int(joints[i], "node");
                int child = Int(joints[i + 1], "node");

                if ((uint)node >= (uint)model.Nodes.Count || (uint)child >= (uint)model.Nodes.Count)
                {
                    Console.WriteLine($"[VRM] 揺れもの {name}: ノード {node} / {child} が無いので飛ばす");
                    continue;
                }

                // **鎖は親子が続いている前提**(子の親 = 1つ前の関節)。間に別のノードが挟まると、
                // 1つずつ世界行列を作り直す今のやり方では、挟まったノードが古いままになる(計画書の「今日残した歪み」)。
                if (model.Nodes[child].Parent != node)
                {
                    Console.WriteLine($"[VRM] 揺れもの {name}: ノード {child} の親が {node} ではない(揺れ方が少しずれる)");
                }

                built.Add(new Joint(model, node, child, ReadSettings(joints[i])));
            }

            ColliderGroup[] used = Array(entry, "colliderGroups")
                .Select(index => index.GetInt32())
                .Where(index => (uint)index < (uint)groups.Count)
                .Select(index => groups[index])
                .ToArray();

            int center = entry.TryGetProperty("center", out JsonElement c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : -1;

            springs.Add(new Spring(name, built.ToArray(), used, center));
        }

        // **根が木の上の方にある鎖から先に回す**。スカートの下にもう1本ぶら下がっているような鎖では、
        // 下の鎖が上の鎖の決めた回転を親として読む必要がある(親が先に来る並び = Model.NodeOrder)。
        var order = new Dictionary<int, int>();
        for (int i = 0; i < model.NodeOrder.Count; i++)
        {
            order[model.NodeOrder[i]] = i;
        }

        return new VrmSpringBone(
            springs.OrderBy(spring => spring.Joints.Length > 0 ? order.GetValueOrDefault(spring.Joints[0].Node) : 0).ToArray(),
            groups);
    }

    /// <summary>
    /// **ファイルを開き直して読む**。表示中のアバターの状態(先端の点・試しの値)に触らずに、まっさらな揺れものが欲しいとき
    /// (自己チェック)に使う。Model は読み込み済みのものを借りる。
    /// </summary>
    public static VrmSpringBone ReadFile(string path, Model model)
    {
        using JsonDocument json = GltfLoader.ReadJson(path);
        JsonElement root = json.RootElement;
        JsonElement source = root.TryGetProperty("extensions", out JsonElement extensions)
            && extensions.TryGetProperty("VRMC_springBone", out JsonElement springBone)
                ? springBone
                : default;
        return Read(source, model);
    }

    private static Collider ReadCollider(JsonElement entry)
    {
        int node = Int(entry, "node");
        JsonElement shape = entry.TryGetProperty("shape", out JsonElement s) ? s : default;

        if (shape.ValueKind == JsonValueKind.Object && shape.TryGetProperty("capsule", out JsonElement capsule))
        {
            return new Collider(node, Vector(capsule, "offset", Vector3.Zero), Float(capsule, "radius", 0.0f), Vector(capsule, "tail", Vector3.Zero));
        }

        JsonElement sphere = shape.ValueKind == JsonValueKind.Object && shape.TryGetProperty("sphere", out JsonElement sp) ? sp : default;
        return new Collider(node, Vector(sphere, "offset", Vector3.Zero), Float(sphere, "radius", 0.0f), null);
    }

    private static JointSettings ReadSettings(JsonElement joint)
    {
        JointSettings fallback = JointSettings.Default;
        Vector3 gravityDir = Vector(joint, "gravityDir", fallback.GravityDir);

        return new JointSettings(
            Float(joint, "hitRadius", fallback.HitRadius),
            Float(joint, "stiffness", fallback.Stiffness),
            Float(joint, "gravityPower", fallback.GravityPower),
            gravityDir.LengthSquared() > 1e-12f ? Vector3.Normalize(gravityDir) : fallback.GravityDir,
            Float(joint, "dragForce", fallback.DragForce));
    }

    private static IEnumerable<JsonElement> Array(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out JsonElement list)
        && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : [];

    private static int Int(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : -1;

    private static float Float(JsonElement owner, string name, float fallback) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : fallback;

    private static Vector3 Vector(JsonElement owner, string name, Vector3 fallback)
    {
        if (owner.ValueKind != JsonValueKind.Object
            || !owner.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array
            || value.GetArrayLength() < 3)
        {
            return fallback;
        }

        return new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
    }

    private static string Text(JsonElement owner, string name, string fallback) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}
