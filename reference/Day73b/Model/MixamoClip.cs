using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **Mixamo の移動用クリップ1本**(Day 73a)。Blender で FBX から直した glb(<c>assets/mixamo/fbx_to_glb.py</c>)か、
/// 同じ形に作った代役(<c>honya-idle.glb</c> など)を読み、VRM のアバターに当てられる <see cref="VrmAnimation"/> にする。
///
/// <para>
/// <b>VRMA との違いは3つだけ</b>。そこから先(正規化して、アバターの休みの回転で戻す)は Day 71 の道をそのまま通る。
/// </para>
/// <list type="number">
/// <item><b>骨の表を自分で作る</b> … VRMA は「どのノードが hips か」を拡張に書くが、Mixamo は書かない。
/// ノードの名前(<c>mixamorig:LeftArm</c>)を <see cref="BoneTable"/> で VRM の骨の名前(<c>leftUpperArm</c>)へ引く</item>
/// <item><b>根の移動を抜く</b> … Mixamo の歩きは前へ進む。ゲームではカプセルが進める(Day 51)ので、
/// 腰の移動から「進んだ分」を引いてその場で足踏みさせる。引いた傾きがクリップの速さになる</item>
/// <item><b>時刻を 0 に揃える</b> … Blender の書き出しは 1 コマ目から始まることがある</item>
/// </list>
///
/// <para>
/// <b>単位(cm)は何もしなくてよい</b>。Mixamo の骨は cm で書かれ、親の Armature が 0.01 倍を持つ。
/// Day 71 の腰の写し方は「ファイルの腰の親の休みの世界行列」を通して世界の m に直すので、0.01 倍はそこで勝手に掛かる。
/// 回転は単位を持たないので、そもそも関係しない。
/// </para>
/// </summary>
internal sealed class MixamoClip
{
    /// <summary>
    /// **Mixamo の骨の名前 → VRM の骨の名前**。今日の表の全部(左右 52 本)。
    ///
    /// <para>
    /// 名前の前の <c>mixamorig:</c> は落としてから引く(落とし方は <see cref="BoneName"/>)。
    /// 載っていない骨は捨てる——先っぽ(<c>HeadTop_End</c>・<c>LeftToe_End</c>・指の 4)は回転を持っても見た目に効かない、
    /// 目の骨は Mixamo に無い。
    /// </para>
    ///
    /// <para>
    /// <b>背骨は 3 本ずつで対応がずれない</b>。Mixamo は Spine・Spine1・Spine2、VRM は spine・chest・upperChest。
    /// upperChest を持たないアバターなら、Spine2 の回転は捨てられる(<see cref="VrmAnimation.CreateClip"/> の「アバターに無い骨は捨てる」)。
    /// 腕の名前は1段ずれているので注意: Mixamo の <c>Arm</c> は上腕、<c>ForeArm</c> が前腕。
    /// </para>
    /// </summary>
    public static readonly (string Mixamo, string Vrm)[] BoneTable = BuildBoneTable();

    /// <summary>
    /// 「In Place で落としたらしい」とみなす速さ [m/s]。これより遅い歩き・走りは、根の移動を持っていない。
    /// </summary>
    public const float InPlaceSpeed = 0.05f;

    private MixamoClip(
        VrmAnimation animation, string kind, Vector3 drift, float seconds, bool rootMotionRemoved, int mergedAnimations,
        string[] unmapped, float armDroop)
    {
        Animation = animation;
        Kind = kind;
        Drift = drift;
        FileSpeed = seconds > 1e-4f ? new Vector2(drift.X, drift.Z).Length() / seconds : 0.0f;
        RootMotionRemoved = rootMotionRemoved;
        MergedAnimations = mergedAnimations;
        Unmapped = unmapped;
        ArmDroopDegrees = armDroop;
    }

    /// <summary>当てるときに使う形。骨の表と、根の移動を抜いた(または残した)腰のキーを持つ。</summary>
    public VrmAnimation Animation { get; }

    /// <summary>ファイル名から決めた役目(idle / walk / run)。どれでもなければ空。</summary>
    public string Kind { get; }

    /// <summary>
    /// **1周で腰が進んだ距離**(ファイルの世界の m。最後のキー - 最初のキー)。歩きなら前(+Z)へ 1m ほど。
    /// 上下(Y)はループの頭と終わりで同じなので、ほぼ 0。
    /// </summary>
    public Vector3 Drift { get; }

    /// <summary>
    /// **ファイルの背丈での速さ** [m/s] = 水平に進んだ距離 / クリップの長さ。アバターでの速さは <see cref="AvatarSpeed"/>。
    /// </summary>
    public float FileSpeed { get; }

    /// <summary>根の移動を抜いたか(<see cref="Load"/> の引数のまま)。</summary>
    public bool RootMotionRemoved { get; }

    /// <summary>
    /// まとめたアニメーションの数。1 ならファイルのまま。2 以上なら、Blender の書き出しで骨ごとに割れていたものを1本にした。
    /// </summary>
    public int MergedAnimations { get; }

    /// <summary>名前が Mixamo の骨らしいのに表に無かったノード(先っぽの骨を除く)。内訳用。</summary>
    public IReadOnlyList<string> Unmapped { get; }

    /// <summary>
    /// **休みの姿勢で、左の上腕が水平から何度垂れているか**。T ポーズなら 0 付近。
    ///
    /// <para>
    /// 正規化の式は「ファイルの休みの姿勢 = アバターの休みの姿勢(VRM 1.0 は T ポーズ)」を前提にしている。
    /// 休みの姿勢が A ポーズ(腕が 45 度下がっている)のファイルを当てると、腕が全部 45 度上がって見える——
    /// 式は正しく働いていて、<b>前提のほうが崩れている</b>。それを読み込みのときに見つけるための値。
    /// </para>
    /// </summary>
    public float ArmDroopDegrees { get; }

    /// <summary>移動を持つクリップ(歩き・走り)なのに、根の移動が入っていないか。Mixamo で In Place を付けて落とすとこうなる。</summary>
    public bool InPlace => Kind is "walk" or "run" && FileSpeed < InPlaceSpeed;

    /// <summary>
    /// **このアバターに当てたときの速さ** [m/s]。ファイルの速さに<b>腰の高さの比</b>を掛ける。
    ///
    /// <para>
    /// 腰の高さが 0.88 倍のアバターは、同じ角度で脚を振ると歩幅も 0.88 倍になる。
    /// 体を 0.88 倍に縮めた絵を思い浮かべれば、同じ時間に進む距離も 0.88 倍になるのが分かる。
    /// この速さで進めたときだけ、<b>足が床の上で滑らない</b>。
    /// </para>
    /// </summary>
    public float AvatarSpeed(Model avatar, IReadOnlyDictionary<string, int> humanBones) =>
        FileSpeed * Animation.HipsScale(avatar, humanBones);

    /// <summary>
    /// **読む**。<paramref name="removeRootMotion"/> が false なら、根の移動を残す(見比べ用。アバターが前へ歩いて行き、ループの頭で戻る)。
    /// </summary>
    public static MixamoClip Load(string path, bool removeRootMotion = true)
    {
        (ModelNode[] nodes, _, AnimationClip[] clips) = GltfLoader.LoadAnimations(path);
        if (clips.Length == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: アニメーションがありません");
        }

        // --- 1. 骨の表を作る(名前で引く)---
        var table = BoneTable.ToDictionary(pair => pair.Mixamo, pair => pair.Vrm, StringComparer.OrdinalIgnoreCase);
        var boneOfNode = new Dictionary<int, string>();
        var unmapped = new List<string>();

        for (int i = 0; i < nodes.Length; i++)
        {
            string name = BoneName(nodes[i].Name);

            if (table.TryGetValue(name, out string? bone))
            {
                boneOfNode[i] = bone;
            }
            else if (nodes[i].Name.Contains(':') && !name.EndsWith("_End", StringComparison.OrdinalIgnoreCase) && !name.EndsWith('4'))
            {
                unmapped.Add(nodes[i].Name);
            }
        }

        int hips = boneOfNode.FirstOrDefault(pair => pair.Value == "hips", new KeyValuePair<int, string>(-1, "")).Key;
        if (hips < 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)}: mixamorig:Hips の骨がありません(Mixamo のクリップではない glb です)");
        }

        // --- 2. 割れたクリップを1本にまとめる ---
        (AnimationClip clip, int merged) = Merge(clips);

        // --- 3. 時刻を 0 に揃える ---
        float start = clip.Channels.Min(channel => channel.Sampler.StartTime);
        float end = clip.Channels.Max(channel => channel.Sampler.EndTime);

        // --- 4. 根の移動を測り、抜く ---
        //
        // 腰の移動のキーは「腰の親(Armature)の座標」で書かれている。Armature は 0.01 倍で、Blender の向きの直しも持ちうるので、
        // **世界に直してから**水平の成分を測る(前がどちらの軸かを決め打ちしない)。
        Matrix4x4 toWorld = VrmAnimation.RestWorldMatrix(nodes, nodes[hips].Parent);
        Matrix4x4.Invert(toWorld, out Matrix4x4 toParent);

        AnimationClip.Channel? hipsTrack = clip.Channels
            .Where(channel => channel.Node == hips && channel.Path == AnimationPath.Translation)
            .Select(channel => (AnimationClip.Channel?)channel)
            .FirstOrDefault();

        Vector3 drift = Vector3.Zero;
        if (hipsTrack is { } track)
        {
            drift = Vector3.Transform(track.Sampler.SampleVector3(end), toWorld)
                - Vector3.Transform(track.Sampler.SampleVector3(start), toWorld);
        }

        // **水平だけを抜く**。上下(1歩ごとに沈む)と左右の揺れは残す——それが歩きの絵の一部。
        // 抜くのは「時刻に比例して進んだ分」= 傾き一定の直線。歩きのループは最初と最後の姿勢が同じで、腰だけが 1 周ぶん前にいるので、
        // 直線を引くと最後のキーが最初のキーに重なり、継ぎ目が消える。
        var horizontal = new Vector3(drift.X, 0.0f, drift.Z);
        float seconds = MathF.Max(end - start, 1e-4f);

        var channels = new List<AnimationClip.Channel>();
        foreach (AnimationClip.Channel channel in clip.Channels)
        {
            bool isHipsTranslation = channel.Node == hips && channel.Path == AnimationPath.Translation;

            AnimationClip.Sampler sampler = isHipsTranslation && removeRootMotion
                ? channel.Sampler.MapTimed(
                    (time, key) =>
                    {
                        Vector3 world = Vector3.Transform(new Vector3(key.X, key.Y, key.Z), toWorld);
                        world -= horizontal * ((time - start) / seconds);
                        return new Vector4(Vector3.Transform(world, toParent), 0.0f);
                    },
                    start)
                : channel.Sampler.MapTimed((_, key) => key, start);

            channels.Add(channel with { Sampler = sampler });
        }

        var shifted = new AnimationClip(clip.Name, channels);

        // --- 5. 休みの姿勢が T ポーズかを確かめる ---
        float armDroop = 0.0f;
        int upperArm = boneOfNode.FirstOrDefault(pair => pair.Value == "leftUpperArm", new KeyValuePair<int, string>(-1, "")).Key;
        int lowerArm = boneOfNode.FirstOrDefault(pair => pair.Value == "leftLowerArm", new KeyValuePair<int, string>(-1, "")).Key;
        if (upperArm >= 0 && lowerArm >= 0)
        {
            Vector3 arm = VrmAnimation.RestWorldMatrix(nodes, lowerArm).Translation - VrmAnimation.RestWorldMatrix(nodes, upperArm).Translation;
            armDroop = MathF.Asin(Math.Clamp(-arm.Y / MathF.Max(arm.Length(), 1e-6f), -1.0f, 1.0f)) * (180.0f / MathF.PI);
        }

        // --- 6. ここから先は VRMA と同じ ---
        VrmAnimation animation = VrmAnimation.FromHumanoid(path, "mixamo", nodes, shifted, boneOfNode, [], -1);

        return new MixamoClip(
            animation, KindOf(path), drift, seconds, removeRootMotion, merged, unmapped.ToArray(), armDroop);
    }

    /// <summary>
    /// **名前から <c>mixamorig:</c> を落とす**。最後のコロンより後ろを取る。
    /// Mixamo は同じ場面に2体目を読ませると <c>mixamorig1:</c> のように番号を振るので、決まった前置きを消すのでは足りない。
    /// </summary>
    public static string BoneName(string nodeName)
    {
        int colon = nodeName.LastIndexOf(':');
        return colon >= 0 ? nodeName[(colon + 1)..] : nodeName;
    }

    /// <summary>ファイル名から役目を決める。Mixamo の名前は「Breathing Idle」「Walking」「Running」「Jogging」のように揺れる。</summary>
    public static string KindOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return name.Contains("idle") ? "idle"
            : name.Contains("run") || name.Contains("jog") || name.Contains("sprint") ? "run"
            : name.Contains("walk") ? "walk"
            : string.Empty;
    }

    /// <summary>
    /// **割れたクリップを1本にまとめる**。Blender の書き出しは、FBX の作り方によって骨1本ずつ別のアニメーションに割れることがある。
    /// <b>狙うノードが重ならない</b>ときだけまとめる——重なるなら本当に別のクリップ(With Skin で数本入れた glb など)なので、最初の1本を使う。
    /// </summary>
    internal static (AnimationClip Clip, int Merged) Merge(AnimationClip[] clips)
    {
        if (clips.Length == 1)
        {
            return (clips[0], 1);
        }

        var targets = new HashSet<(int, AnimationPath)>();
        foreach (AnimationClip.Channel channel in clips.SelectMany(clip => clip.Channels))
        {
            if (!targets.Add((channel.Node, channel.Path)))
            {
                return (clips[0], 1);
            }
        }

        return (new AnimationClip(clips[0].Name, clips.SelectMany(clip => clip.Channels).ToArray()), clips.Length);
    }

    private static (string, string)[] BuildBoneTable()
    {
        var table = new List<(string, string)>
        {
            ("Hips", "hips"),
            ("Spine", "spine"),
            ("Spine1", "chest"),
            ("Spine2", "upperChest"),
            ("Neck", "neck"),
            ("Head", "head"),
        };

        foreach ((string mixamo, string vrm) in (ReadOnlySpan<(string, string)>)[("Left", "left"), ("Right", "right")])
        {
            table.Add(($"{mixamo}Shoulder", $"{vrm}Shoulder"));
            table.Add(($"{mixamo}Arm", $"{vrm}UpperArm"));
            table.Add(($"{mixamo}ForeArm", $"{vrm}LowerArm"));
            table.Add(($"{mixamo}Hand", $"{vrm}Hand"));
            table.Add(($"{mixamo}UpLeg", $"{vrm}UpperLeg"));
            table.Add(($"{mixamo}Leg", $"{vrm}LowerLeg"));
            table.Add(($"{mixamo}Foot", $"{vrm}Foot"));
            table.Add(($"{mixamo}ToeBase", $"{vrm}Toes"));

            // **親指だけ名前がずれる**。VRM 1.0 の親指は Metacarpal・Proximal・Distal(手のひらの中の骨から数える)、
            // ほかの指は Proximal・Intermediate・Distal。Mixamo はどの指も 1・2・3。小指は Pinky → Little。
            table.Add(($"{mixamo}HandThumb1", $"{vrm}ThumbMetacarpal"));
            table.Add(($"{mixamo}HandThumb2", $"{vrm}ThumbProximal"));
            table.Add(($"{mixamo}HandThumb3", $"{vrm}ThumbDistal"));

            foreach ((string finger, string vrmFinger) in (ReadOnlySpan<(string, string)>)
                [("Index", "Index"), ("Middle", "Middle"), ("Ring", "Ring"), ("Pinky", "Little")])
            {
                table.Add(($"{mixamo}Hand{finger}1", $"{vrm}{vrmFinger}Proximal"));
                table.Add(($"{mixamo}Hand{finger}2", $"{vrm}{vrmFinger}Intermediate"));
                table.Add(($"{mixamo}Hand{finger}3", $"{vrm}{vrmFinger}Distal"));
            }
        }

        return table.ToArray();
    }
}
