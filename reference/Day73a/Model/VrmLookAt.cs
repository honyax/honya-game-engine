using System.Numerics;
using System.Text.Json;

namespace HonyaEngine;

/// <summary>
/// **VRM 1.0 の視線**(<c>VRMC_vrm.lookAt</c>。Day 71)。「どちらを見るか」(yaw と pitch)を、目の骨の回転に直す。
///
/// <para>
/// 視線には2つの型がある。<b>bone</b> は目の骨(<c>leftEye</c> / <c>rightEye</c>)を回し、<b>expression</b> は
/// 表情(<c>lookLeft</c> など)の重みで瞳の絵を動かす。AvatarSample_A は bone 型で、今日は bone 型だけを読む。
/// </para>
///
/// <para>
/// <b>見たい角度をそのまま目に渡さない</b>のが肝。目の玉は顔の中で 10 度ほどしか回らない(それ以上回すと白目だけになる)。
/// そこで「見たい角度 0〜90 度」を「目の角度 0〜outputScale 度」へ縮める表(rangeMap)を、上下・内外の4方向ぶん持つ。
/// 内と外が分かれているのは、左を見るとき左目は外へ、右目は内へ回るから(鼻の側には回りにくい)。
/// </para>
/// </summary>
internal sealed class VrmLookAt
{
    /// <summary>
    /// 見たい角度 → 目の角度の表。<c>inputMaxValue</c> 度までを 0〜<c>outputScale</c> 度へ比例で縮め、その先は頭打ち。
    /// </summary>
    public readonly record struct RangeMap(float InputMaxValue, float OutputScale)
    {
        public float Map(float degrees) => OutputScale * Math.Clamp(degrees / MathF.Max(InputMaxValue, 1e-4f), 0.0f, 1.0f);
    }

    /// <summary>目の骨1つ。正規化された回転を戻すのに、休みのローカルの回転と、親の休みの世界の回転が要る(<see cref="VrmAnimation.Denormalize"/>)。</summary>
    private readonly record struct Eye(int Node, Quaternion RestLocal, Quaternion ParentRestWorld);

    private readonly Eye? _left;
    private readonly Eye? _right;

    private VrmLookAt(
        string type,
        Vector3 offsetFromHeadBone,
        RangeMap horizontalInner,
        RangeMap horizontalOuter,
        RangeMap verticalDown,
        RangeMap verticalUp,
        int head,
        Quaternion headRestWorld,
        Eye? left,
        Eye? right)
    {
        Type = type;
        OffsetFromHeadBone = offsetFromHeadBone;
        HorizontalInner = horizontalInner;
        HorizontalOuter = horizontalOuter;
        VerticalDown = verticalDown;
        VerticalUp = verticalUp;
        HeadNode = head;
        HeadRestWorld = headRestWorld;
        _left = left;
        _right = right;
    }

    /// <summary>"bone" か "expression"。</summary>
    public string Type { get; }

    /// <summary>目の位置。頭の骨から見たずらし(AvatarSample_A は 6cm 上)。「カメラを見る」の向きはここから測る。</summary>
    public Vector3 OffsetFromHeadBone { get; }

    public RangeMap HorizontalInner { get; }

    public RangeMap HorizontalOuter { get; }

    public RangeMap VerticalDown { get; }

    public RangeMap VerticalUp { get; }

    public int HeadNode { get; }

    /// <summary>頭の骨の休みの世界の回転。見たい向きを「休みの頭」から見た向きに直すのに使う。</summary>
    public Quaternion HeadRestWorld { get; }

    /// <summary>目の骨を回せるか(bone 型で、目の骨が割り当てられている)。</summary>
    public bool CanRotateEyes => Type == "bone" && (_left is not null || _right is not null);

    /// <summary>表を使うか(試し)。false なら見たい角度をそのまま目の角度にする——目が白目まで回る。</summary>
    public bool UseRangeMap { get; set; } = true;

    /// <summary>最後に渡した、見たい角度(度。左と上が正)。</summary>
    public (float Yaw, float Pitch) Input { get; private set; }

    /// <summary>最後に目へ書いた角度(度)。左目・右目の yaw と、上下の pitch。HUD 用。</summary>
    public (float LeftYaw, float RightYaw, float Pitch) EyeDegrees { get; private set; }

    /// <summary>
    /// **ファイルから読む**。<paramref name="lookAt"/> は <c>VRMC_vrm.lookAt</c>(無ければ default)。
    /// 表が省略されていたら仕様の既定値(入力 90 度 → 出力 10 度)。
    /// </summary>
    public static VrmLookAt Read(JsonElement lookAt, Model model, IReadOnlyDictionary<string, int> humanBones)
    {
        bool has = lookAt.ValueKind == JsonValueKind.Object;
        string type = has && lookAt.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "bone" : "bone";

        Vector3 offset = Vector3.Zero;
        if (has && lookAt.TryGetProperty("offsetFromHeadBone", out JsonElement o) && o.GetArrayLength() >= 3)
        {
            offset = new Vector3(o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle());
        }

        RangeMap Range(string name)
        {
            if (has && lookAt.TryGetProperty(name, out JsonElement range))
            {
                float input = range.TryGetProperty("inputMaxValue", out JsonElement i) ? i.GetSingle() : 90.0f;
                float output = range.TryGetProperty("outputScale", out JsonElement s) ? s.GetSingle() : 10.0f;
                return new RangeMap(input, output);
            }

            return new RangeMap(90.0f, 10.0f);
        }

        Eye? ReadEye(string bone) =>
            humanBones.TryGetValue(bone, out int node)
                ? new Eye(node, model.Nodes[node].RestPose.Rotation, VrmAnimation.RestWorldRotation(model.Nodes, model.Nodes[node].Parent))
                : null;

        int head = humanBones.TryGetValue("head", out int h) ? h : -1;

        return new VrmLookAt(
            type,
            offset,
            Range("rangeMapHorizontalInner"),
            Range("rangeMapHorizontalOuter"),
            Range("rangeMapVerticalDown"),
            Range("rangeMapVerticalUp"),
            head,
            head >= 0 ? VrmAnimation.RestWorldRotation(model.Nodes, head) : Quaternion.Identity,
            ReadEye("leftEye"),
            ReadEye("rightEye"));
    }

    /// <summary>
    /// **ファイルを開き直して読む**。表示中のアバターの試しの値(<see cref="UseRangeMap"/>)に触らずに、まっさらな視線が欲しいとき
    /// (自己チェック)に使う。Model は読み込み済みのものを借りる(Day 70 の <see cref="VrmSpringBone.ReadFile"/> と同じ形)。
    /// </summary>
    public static VrmLookAt ReadFile(string path, Model model, IReadOnlyDictionary<string, int> humanBones)
    {
        using JsonDocument json = GltfLoader.ReadJson(path);
        JsonElement source = json.RootElement.TryGetProperty("extensions", out JsonElement extensions)
            && extensions.TryGetProperty("VRMC_vrm", out JsonElement vrm)
            && vrm.TryGetProperty("lookAt", out JsonElement lookAt)
                ? lookAt
                : default;
        return Read(source, model, humanBones);
    }

    /// <summary>
    /// **目の角度を決める**(表を通す)。左目は左を見るとき外へ、右目は内へ回る。上下は両目同じ。
    /// </summary>
    public (float LeftYaw, float RightYaw, float Pitch) MapAngles(float yaw, float pitch)
    {
        if (!UseRangeMap)
        {
            return (yaw, yaw, pitch);
        }

        float left = yaw >= 0.0f ? HorizontalOuter.Map(yaw) : -HorizontalInner.Map(-yaw);
        float right = yaw >= 0.0f ? HorizontalInner.Map(yaw) : -HorizontalOuter.Map(-yaw);
        float vertical = pitch >= 0.0f ? VerticalUp.Map(pitch) : -VerticalDown.Map(-pitch);
        return (left, right, vertical);
    }

    /// <summary>
    /// **目の骨を回す**。<paramref name="yaw"/> と <paramref name="pitch"/> は見たい角度(度。左と上が正)。
    /// 書くのはローカルの姿勢だけ——世界行列と関節行列は、呼ぶ側が <see cref="AnimationPlayer.UpdateWorldAndJoints"/> で作り直す。
    /// </summary>
    public void Apply(AnimationPlayer pose, float yaw, float pitch)
    {
        Input = (yaw, pitch);
        EyeDegrees = MapAngles(yaw, pitch);

        if (!CanRotateEyes)
        {
            return;
        }

        Write(_left, EyeDegrees.LeftYaw);
        Write(_right, EyeDegrees.RightYaw);

        void Write(Eye? eye, float eyeYaw)
        {
            if (eye is not Eye e)
            {
                return;
            }

            // **正規化された目の回転**: 頭の休みの向き(+Z が正面)で、Y まわりに yaw、X まわりに pitch。
            // X まわりの正の回転は +Z を下へ向けるので、上を正にした pitch は符号を返して渡す。
            Quaternion normalized = Quaternion.CreateFromYawPitchRoll(
                eyeYaw * (MathF.PI / 180.0f), -EyeDegrees.Pitch * (MathF.PI / 180.0f), 0.0f);

            // 目の骨も、体の骨と同じ式でアバターの回転へ戻す。AvatarSample_A の目は休みの回転が 23 度ほど傾いている。
            NodePose local = pose.GetLocalPose(e.Node);
            local.Rotation = Quaternion.Normalize(VrmAnimation.Denormalize(normalized, e.ParentRestWorld, e.RestLocal));
            pose.SetLocalPose(e.Node, local);
        }
    }

    /// <summary>
    /// **目の位置から <paramref name="target"/> を見る角度**(度)。頭が回っていても、頭の休みの向きから測る
    /// (首を振りながらカメラを見ると、目は首の動きを打ち消すように回る)。
    /// </summary>
    public (float Yaw, float Pitch) AnglesTo(AnimationPlayer pose, Matrix4x4 placement, Vector3 target)
    {
        if (HeadNode < 0)
        {
            return (0.0f, 0.0f);
        }

        Matrix4x4 head = pose.GetNodeWorld(HeadNode) * placement;
        Vector3 eye = Vector3.Transform(OffsetFromHeadBone, head);

        // 世界の向き → 今の頭から見た向き → 休みの頭の向きへ戻す(three-vrm の VRMLookAt.lookAt と同じ)。
        Quaternion current = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(head));
        Vector3 direction = Vector3.Transform(target - eye, HeadRestWorld * Quaternion.Inverse(current));
        if (direction.LengthSquared() < 1e-10f)
        {
            return (0.0f, 0.0f);
        }

        direction = Vector3.Normalize(direction);
        float yaw = MathF.Atan2(direction.X, direction.Z) * (180.0f / MathF.PI);
        float pitch = MathF.Asin(Math.Clamp(direction.Y, -1.0f, 1.0f)) * (180.0f / MathF.PI);
        return (yaw, pitch);
    }
}
