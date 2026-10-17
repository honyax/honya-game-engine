using System.Numerics;
using System.Text.Json;

namespace HonyaEngine;

/// <summary>
/// **VRM アニメーション(.vrma)1本**(Day 71)。<c>VRMC_vrm_animation</c> 拡張を持つ、描くものの無い glTF。
///
/// <para>
/// 中身は3つの動き。どれも glTF の普通のアニメーションのチャンネルで書かれていて、拡張は「どのノードが何か」を言うだけ。
/// </para>
/// <list type="bullet">
/// <item><b>体</b> … ヒューマノイドの骨のノードの回転(腰だけは移動も)。骨は名前(<c>hips</c>・<c>rightUpperArm</c>)で引く</item>
/// <item><b>表情</b> … 表情ごとのノードの <c>translation.x</c> がそのまま重み(y と z は使わない)</item>
/// <item><b>視線</b> … 視線のノードの回転。+Z が正面で、yaw(Y 軸まわり)と pitch(X 軸まわり)に直して使う</item>
/// </list>
///
/// <para>
/// <b>キーはファイルの骨のローカルの回転</b>で、そのままアバターの骨へ書くと壊れる。骨の「休みの回転」(T ポーズのときの
/// ローカルの回転)がファイルごと・アバターごとに違うから。そこで一度<b>正規化された回転</b>——休みの姿勢からどれだけ回ったかを、
/// 世界にそろえた向きで書いたもの——に直し、アバターの休みの回転で戻す(<see cref="CreateClip"/>)。
/// 写した結果は Day 41 からの <see cref="AnimationClip"/> で、再生も混ぜ方も <see cref="AnimationPlayer"/> がそのまま受け持つ。
/// </para>
/// </summary>
internal sealed class VrmAnimation
{
    /// <summary>
    /// **骨1本ぶんの動き**。キーと、正規化に要るファイル側の2つの回転(どちらも T ポーズの世界の回転)。
    /// </summary>
    /// <param name="Bone">ヒューマノイドの骨の名前。</param>
    /// <param name="Path">回転か移動か(移動は hips だけ)。</param>
    /// <param name="Sampler">ファイルに書かれたままのキー。</param>
    /// <param name="ParentRestWorld">いちばん近い祖先の骨の、休みの世界の回転(祖先に骨が無ければ hips の親)。</param>
    /// <param name="RestWorld">この骨の、休みの世界の回転。</param>
    public sealed record BoneTrack(
        string Bone, AnimationPath Path, AnimationClip.Sampler Sampler, Quaternion ParentRestWorld, Quaternion RestWorld);

    private VrmAnimation(
        string name,
        string sourcePath,
        string specVersion,
        float duration,
        BoneTrack[] bones,
        Dictionary<string, AnimationClip.Sampler> expressions,
        AnimationClip.Sampler? lookAt,
        Matrix4x4 hipsParentRestWorld,
        float restHipsHeight,
        IReadOnlyDictionary<string, int> humanBones,
        int rotatedRestBones)
    {
        Name = name;
        SourcePath = sourcePath;
        SpecVersion = specVersion;
        Duration = duration;
        Bones = bones;
        Expressions = expressions;
        LookAt = lookAt;
        HipsParentRestWorld = hipsParentRestWorld;
        RestHipsHeight = restHipsHeight;
        HumanBones = humanBones;
        RotatedRestBones = rotatedRestBones;
    }

    /// <summary>ファイル名(拡張子なし)。</summary>
    public string Name { get; }

    public string SourcePath { get; }

    /// <summary><c>VRMC_vrm_animation.specVersion</c>。"1.0" のはず。</summary>
    public string SpecVersion { get; }

    /// <summary>いちばん遅いキーの時刻(秒)。体・表情・視線のどれのキーも含む。</summary>
    public float Duration { get; }

    /// <summary>骨の動き。キーを持つ骨だけ(持たない骨は休みの姿勢のまま)。</summary>
    public IReadOnlyList<BoneTrack> Bones { get; }

    /// <summary>表情の名前 → 重みのキー(<c>translation.x</c>)。</summary>
    public IReadOnlyDictionary<string, AnimationClip.Sampler> Expressions { get; }

    /// <summary>視線の回転のキー。無ければ null。</summary>
    public AnimationClip.Sampler? LookAt { get; }

    /// <summary>
    /// hips の親の、休みの世界の行列。腰の移動のキーは「親から見た位置」なので、これを掛けて世界の位置にする。
    /// 普通は単位(hips がいちばん上のノード)。
    /// </summary>
    public Matrix4x4 HipsParentRestWorld { get; }

    /// <summary>T ポーズの腰の高さ(m)。アバターの腰の高さとの比で、腰の移動を縮める。</summary>
    public float RestHipsHeight { get; }

    /// <summary>
    /// **ファイルの骨の名前 → ファイルのノード番号**(キーを持たない骨も含む)。写すときには使わない——内訳と、
    /// 自己チェックがファイルの骨の木を同じ時刻で動かして、アバターと回り方を突き合わせるのに使う。
    /// </summary>
    public IReadOnlyDictionary<string, int> HumanBones { get; }

    /// <summary>
    /// 休みの世界の回転が単位でない骨の数(内訳用)。0 なら「キーがそのまま正規化された回転」の素直なファイル。
    /// three-vrm の test.vrma(Blender で書いた)は全部の骨がこれで、UniVRM や今日の honya-wave.vrma は 0。
    /// </summary>
    public int RotatedRestBones { get; }

    /// <summary>
    /// **読む**。ファイルの中のアニメーションは<b>最初の1本だけ</b>使う(仕様の決まり。複数あっても読まなくてよい)。
    /// </summary>
    public static VrmAnimation Load(string path)
    {
        // --- 1. 拡張を読む: どのノードが何か ---
        var boneOfNode = new Dictionary<int, string>();
        var expressionOfNode = new Dictionary<int, string>();
        int lookAtNode = -1;
        string specVersion;

        using (JsonDocument json = GltfLoader.ReadJson(path))
        {
            JsonElement root = json.RootElement;
            if (!root.TryGetProperty("extensions", out JsonElement extensions)
                || !extensions.TryGetProperty("VRMC_vrm_animation", out JsonElement vrma))
            {
                throw new InvalidDataException($"{Path.GetFileName(path)}: VRMC_vrm_animation 拡張がありません(VRM アニメーションではない glTF です)");
            }

            specVersion = vrma.TryGetProperty("specVersion", out JsonElement v) ? v.GetString() ?? "?" : "?";

            if (vrma.TryGetProperty("humanoid", out JsonElement humanoid)
                && humanoid.TryGetProperty("humanBones", out JsonElement humanBones))
            {
                foreach (JsonProperty bone in humanBones.EnumerateObject())
                {
                    boneOfNode[bone.Value.GetProperty("node").GetInt32()] = bone.Name;
                }
            }

            // 表情はプリセットとカスタムの2段。どちらも「名前 → ノード」で、アバター側の表情の名前と同じもので引く。
            if (vrma.TryGetProperty("expressions", out JsonElement expressions))
            {
                foreach (string group in (string[])["preset", "custom"])
                {
                    if (expressions.TryGetProperty(group, out JsonElement list))
                    {
                        foreach (JsonProperty expression in list.EnumerateObject())
                        {
                            expressionOfNode[expression.Value.GetProperty("node").GetInt32()] = expression.Name;
                        }
                    }
                }
            }

            if (vrma.TryGetProperty("lookAt", out JsonElement lookAt) && lookAt.TryGetProperty("node", out JsonElement node))
            {
                lookAtNode = node.GetInt32();
            }
        }

        // --- 2. 木とクリップを読む(Day 41 のローダーの、メッシュを読まない道)---
        (ModelNode[] nodes, _, AnimationClip[] clips) = GltfLoader.LoadAnimations(path);
        if (clips.Length == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: アニメーションがありません");
        }

        // --- 3. 腰の親と、腰の高さ ---
        int hips = boneOfNode.FirstOrDefault(pair => pair.Value == "hips", new KeyValuePair<int, string>(-1, "")).Key;
        if (hips < 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: hips の骨がありません");
        }

        Matrix4x4 hipsParentWorld = RestWorldMatrix(nodes, nodes[hips].Parent);
        float restHipsHeight = RestWorldMatrix(nodes, hips).Translation.Y;
        Quaternion hipsParentRotation = RestWorldRotation(nodes, nodes[hips].Parent);

        // --- 4. チャンネルを、骨・表情・視線に振り分ける ---
        var bones = new List<BoneTrack>();
        var expressionTracks = new Dictionary<string, AnimationClip.Sampler>();
        AnimationClip.Sampler? lookAtTrack = null;

        foreach (AnimationClip.Channel channel in clips[0].Channels)
        {
            if (boneOfNode.TryGetValue(channel.Node, out string? bone))
            {
                // **移動を持ってよいのは hips だけ**(仕様)。ほかの骨の移動は捨てる——背丈の違うアバターに当てると、腕が伸び縮みする。
                if (channel.Path == AnimationPath.Rotation || (channel.Path == AnimationPath.Translation && bone == "hips"))
                {
                    bones.Add(new BoneTrack(
                        bone,
                        channel.Path,
                        channel.Sampler,
                        ParentBoneRotation(nodes, channel.Node, boneOfNode, hipsParentRotation),
                        RestWorldRotation(nodes, channel.Node)));
                }
            }
            else if (expressionOfNode.TryGetValue(channel.Node, out string? expression) && channel.Path == AnimationPath.Translation)
            {
                expressionTracks[expression] = channel.Sampler;
            }
            else if (channel.Node == lookAtNode && channel.Path == AnimationPath.Rotation)
            {
                lookAtTrack = channel.Sampler;
            }
        }

        int rotatedRestBones = boneOfNode.Keys.Count(node => !IsIdentity(RestWorldRotation(nodes, node)));

        return new VrmAnimation(
            Path.GetFileNameWithoutExtension(path), path, specVersion, clips[0].Duration, bones.ToArray(), expressionTracks,
            lookAtTrack, hipsParentWorld, restHipsHeight, boneOfNode.ToDictionary(pair => pair.Value, pair => pair.Key), rotatedRestBones);
    }

    /// <summary>
    /// **アバターのノードへ写したクリップを作る**(リターゲット)。キーを読み込みのときに1回だけ写し、再生は Day 41 の道を通す。
    ///
    /// <para>
    /// 回転のキー q は、2段で写す(<see cref="Normalize"/> → <see cref="Denormalize"/>)。
    /// <code>
    ///   正規化   N = P x q x C^-1          P, C = ファイルの「祖先の骨」と「この骨」の休みの世界の回転
    ///   戻す     r = W^-1 x N x W x R      W = アバターの親ノードの休みの世界の回転、R = アバターの骨の休みのローカルの回転
    /// </code>
    /// どちらも両側から決まった回転を掛けるだけなので、キーごとに <c>A x q x B</c> を作ればよい(<see cref="AnimationClip.Sampler.Map"/>)。
    /// </para>
    ///
    /// <para>
    /// 腰の移動は、ファイルの hips の親の座標 → 世界の位置 → <b>背丈の比で縮める</b> → アバターの hips の親の座標、と写す。
    /// 縮めないと、腰の高さ 1.0m のファイルを 0.9m のアバターに当てたとき、足が 10cm 浮く。
    /// </para>
    /// </summary>
    /// <param name="normalize">false なら写さず、キーをそのまま同じ名前の骨へ当てる(見比べ用)。</param>
    /// <param name="scaleHips">false なら腰の移動を背丈の比で縮めない(見比べ用)。</param>
    public AnimationClip CreateClip(Model avatar, IReadOnlyDictionary<string, int> humanBones, bool normalize = true, bool scaleHips = true)
    {
        var channels = new List<AnimationClip.Channel>();
        IReadOnlyList<ModelNode> nodes = avatar.Nodes;

        float scale = scaleHips ? HipsScale(avatar, humanBones) : 1.0f;

        foreach (BoneTrack track in Bones)
        {
            // **アバターに無い骨は捨てる**(任意の骨。upperChest を持たないアバターなど)。その骨の回転は失われる。
            if (!humanBones.TryGetValue(track.Bone, out int node))
            {
                continue;
            }

            int parent = nodes[node].Parent;
            AnimationClip.Sampler sampler;

            if (track.Path == AnimationPath.Rotation)
            {
                Quaternion parentWorld = RestWorldRotation(nodes, parent);
                Quaternion restLocal = nodes[node].RestPose.Rotation;

                // r = W^-1 x (P x q x C^-1) x W x R = A x q x B
                Quaternion a = Quaternion.Inverse(parentWorld) * track.ParentRestWorld;
                Quaternion b = Quaternion.Inverse(track.RestWorld) * parentWorld * restLocal;

                sampler = normalize
                    ? track.Sampler.Map(key => ToVector4(Quaternion.Normalize(a * ToQuaternion(key) * b)))
                    : track.Sampler;
            }
            else
            {
                Matrix4x4.Invert(RestWorldMatrix(nodes, parent), out Matrix4x4 toParent);
                Matrix4x4 fromFile = HipsParentRestWorld;

                sampler = normalize
                    ? track.Sampler.Map(key => new Vector4(
                        Vector3.Transform(Vector3.Transform(new Vector3(key.X, key.Y, key.Z), fromFile) * scale, toParent), 0.0f))
                    : track.Sampler.Map(key => key * scale);
            }

            channels.Add(new AnimationClip.Channel(node, track.Path, sampler));
        }

        return new AnimationClip(Name, channels);
    }

    /// <summary>アバターの腰の高さ ÷ ファイルの腰の高さ。腰の移動をこの比で縮める。</summary>
    public float HipsScale(Model avatar, IReadOnlyDictionary<string, int> humanBones) =>
        humanBones.TryGetValue("hips", out int hips) && RestHipsHeight > 1e-3f
            ? RestWorldMatrix(avatar.Nodes, hips).Translation.Y / RestHipsHeight
            : 1.0f;

    /// <summary>このファイルの骨のうち、アバターに無いもの(キーを持つ骨だけ数える)。</summary>
    public string[] MissingBones(IReadOnlyDictionary<string, int> humanBones) =>
        Bones.Select(track => track.Bone).Distinct().Where(bone => !humanBones.ContainsKey(bone)).ToArray();

    /// <summary>
    /// 時刻 <paramref name="time"/> の表情の重み。<b>0〜1 に収める</b>(仕様。範囲の外の値を受け取ったら、実装が収める)。
    /// キーを持たない表情は null(アバターの側の値を上書きしない)。
    /// </summary>
    public float? SampleExpression(string name, float time) =>
        Expressions.TryGetValue(name, out AnimationClip.Sampler? sampler)
            ? Math.Clamp(sampler.SampleVector3(time).X, 0.0f, 1.0f)
            : null;

    /// <summary>時刻 <paramref name="time"/> の視線(度)。キーが無ければ false。</summary>
    public bool TrySampleLookAt(float time, out float yaw, out float pitch)
    {
        if (LookAt is null)
        {
            yaw = 0.0f;
            pitch = 0.0f;
            return false;
        }

        (yaw, pitch) = YawPitch(LookAt.SampleRotation(time));
        return true;
    }

    /// <summary>
    /// **正規化**: ファイルの骨のローカルの回転 → 休みの姿勢からの回り方(世界にそろえた向きで)。
    /// 休みの姿勢のキー(q = P^-1 x C。親が祖先の骨のとき)は単位になる。
    /// </summary>
    public static Quaternion Normalize(Quaternion key, Quaternion parentRestWorld, Quaternion restWorld) =>
        parentRestWorld * key * Quaternion.Inverse(restWorld);

    /// <summary>
    /// **戻す**: 正規化された回転 → アバターの骨のローカルの回転。three-vrm の VRMHumanoidRig と同じ式。
    /// 正規化された回転 N を「親の休みの向き」で見直し(W^-1 x N x W)、骨の休みの回転 R の上に掛ける。
    /// </summary>
    public static Quaternion Denormalize(Quaternion normalized, Quaternion parentRestWorld, Quaternion restLocal) =>
        Quaternion.Inverse(parentRestWorld) * normalized * parentRestWorld * restLocal;

    /// <summary>
    /// 回転 → 視線の yaw と pitch(度)。+Z をこの回転で回した向きから求める。
    /// yaw は左(+X)が正、pitch は上が正。Y → X の順のオイラー角(仕様の「Extrinsic ZXY」)と同じで、Z まわりは捨てる。
    /// </summary>
    public static (float Yaw, float Pitch) YawPitch(Quaternion rotation)
    {
        Vector3 forward = Vector3.Transform(Vector3.UnitZ, rotation);
        float yaw = MathF.Atan2(forward.X, forward.Z);
        float pitch = MathF.Asin(Math.Clamp(forward.Y, -1.0f, 1.0f));
        return (yaw * (180.0f / MathF.PI), pitch * (180.0f / MathF.PI));
    }

    /// <summary>ノードの休みの世界の回転(根から自分までのローカルの回転を掛けたもの)。-1 なら単位。拡大は見ない。</summary>
    public static Quaternion RestWorldRotation(IReadOnlyList<ModelNode> nodes, int node)
    {
        Quaternion world = Quaternion.Identity;
        for (int current = node; current >= 0; current = nodes[current].Parent)
        {
            world = nodes[current].RestPose.Rotation * world;
        }

        return Quaternion.Normalize(world);
    }

    /// <summary>ノードの休みの世界の行列。-1 なら単位。</summary>
    public static Matrix4x4 RestWorldMatrix(IReadOnlyList<ModelNode> nodes, int node)
    {
        Matrix4x4 world = Matrix4x4.Identity;
        for (int current = node; current >= 0; current = nodes[current].Parent)
        {
            world *= nodes[current].RestPose.ToMatrix();
        }

        return world;
    }

    /// <summary>
    /// いちばん近い祖先の骨の、休みの世界の回転。正規化された骨の木は<b>骨だけでできている</b>ので、
    /// 間に骨でないノードが挟まっていても、祖先の骨まで登る。どこまで登っても無ければ hips の親(<paramref name="fallback"/>)。
    /// </summary>
    private static Quaternion ParentBoneRotation(
        IReadOnlyList<ModelNode> nodes, int node, Dictionary<int, string> boneOfNode, Quaternion fallback)
    {
        for (int current = nodes[node].Parent; current >= 0; current = nodes[current].Parent)
        {
            if (boneOfNode.ContainsKey(current))
            {
                return RestWorldRotation(nodes, current);
            }
        }

        return fallback;
    }

    private static bool IsIdentity(Quaternion rotation) => MathF.Abs(MathF.Abs(rotation.W) - 1.0f) < 1e-4f;

    private static Quaternion ToQuaternion(Vector4 key) => new(key.X, key.Y, key.Z, key.W);

    private static Vector4 ToVector4(Quaternion rotation) => new(rotation.X, rotation.Y, rotation.Z, rotation.W);
}
