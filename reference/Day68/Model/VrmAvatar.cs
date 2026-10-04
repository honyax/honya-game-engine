using System.Numerics;
using System.Text.Json;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **VRM 1.0 のアバター1体**(Day 68)。中身は glTF の <see cref="Model"/> に、
/// <c>VRMC_vrm</c> 拡張の2つ——<b>メタ情報</b>(誰のアバターで、何に使ってよいか)と
/// <b>ヒューマノイド</b>(どのノードが腰で、どれが左手か)——を足したもの。
///
/// <para>
/// <b>VRM は glTF そのもの</b>。拡張子が .vrm なだけで、中身は glb で、拡張を知らないローダーでも
/// メッシュ・マテリアル(PBR の値)・スキンまでは読める。VRM が足しているのは「人型であること」の約束で、
/// ヒューマノイドのボーンの表があるから、Day 71 で<b>別のアバター用に作ったアニメーションを当てられる</b>
/// (ボーン名で書かれた動きを、このアバターのノード番号へ引き直すだけで済む)。
/// </para>
///
/// <para>
/// <b>VRM 0.x は読まない</b>。0.x は拡張の名前(<c>VRM</c>)も、座標の向き(-Z 向き)も、
/// マテリアルの持ち方(MToon の値を文字列のキーで持つ)も 1.0 と違う。VRoid Hub に上がっているファイルの多くは 0.x なので、
/// 読めないときは何で書き出し直せばよいかを知らせて止める。
/// </para>
///
/// <para>
/// 今日はまだ動かさないので、姿勢はファイルのまま(VRM 1.0 は T ポーズで書き出す決まり)。
/// それでも <see cref="AnimationPlayer"/> を持っておくのは、関節行列とノードの世界行列を作るのが彼の仕事だから。
/// Day 69 で表情、Day 70 で揺れもの、Day 71 でアニメーションがここに増えていく。
/// </para>
/// </summary>
internal sealed class VrmAvatar : IDisposable
{
    /// <summary>
    /// **VRM 1.0 で必ず割り当てる 15 本**。仕様の humanoid.md の表で「Required」とあるもの。
    /// 胸(chest)や首(neck)は任意で、無いアバターもありうる——アニメーションを当てる側は、無い骨を親へ寄せて扱う。
    /// </summary>
    public static readonly string[] RequiredBones =
    [
        "hips", "spine", "head",
        "leftUpperLeg", "leftLowerLeg", "leftFoot",
        "rightUpperLeg", "rightLowerLeg", "rightFoot",
        "leftUpperArm", "leftLowerArm", "leftHand",
        "rightUpperArm", "rightLowerArm", "rightHand",
    ];

    /// <summary>
    /// **骨の親子の約束**(子 → 親)。必須の骨どうしだけ。ノードの木の上で、子の骨の祖先に親の骨が居なければならない
    /// (間に任意の骨や VRM の外のノードが挟まってもよい)。自己チェックが木を登って確かめる。
    /// </summary>
    public static readonly (string Child, string Parent)[] RequiredHierarchy =
    [
        ("spine", "hips"), ("head", "spine"),
        ("leftUpperLeg", "hips"), ("leftLowerLeg", "leftUpperLeg"), ("leftFoot", "leftLowerLeg"),
        ("rightUpperLeg", "hips"), ("rightLowerLeg", "rightUpperLeg"), ("rightFoot", "rightLowerLeg"),
        ("leftUpperArm", "spine"), ("leftLowerArm", "leftUpperArm"), ("leftHand", "leftLowerArm"),
        ("rightUpperArm", "spine"), ("rightLowerArm", "rightUpperArm"), ("rightHand", "rightLowerArm"),
    ];

    /// <summary>
    /// **メタ情報**。VRM 1.0 の <c>meta</c>。名前・作者と、<b>利用条件</b>(アバターとして使ってよい人・商用・再配布・改変・クレジット)。
    ///
    /// <para>
    /// 利用条件がファイルの中に入っているのが VRM の特徴で、アプリはこれを読んで表示する(あるいは使わせない)責任を負う。
    /// 今日のアバターは VRoid Studio で書き出したとき、ここが既定の「作者のみ・再配布禁止」のままだった
    /// (書き出し直して VRoid Hub の利用条件に揃えた。計画書の素材の節)。
    /// </para>
    /// </summary>
    internal sealed record Meta(
        string Name,
        string[] Authors,
        string LicenseUrl,
        string AvatarPermission,
        string CommercialUsage,
        string CreditNotation,
        bool AllowRedistribution,
        string Modification);

    private VrmAvatar(Model model, string specVersion, Meta meta, IReadOnlyDictionary<string, int> humanBones)
    {
        Model = model;
        SpecVersion = specVersion;
        Info = meta;
        HumanBones = humanBones;
        Animation = new AnimationPlayer(model);
    }

    /// <summary>メッシュ・マテリアル・スキン。glTF として読んだぶん。</summary>
    public Model Model { get; }

    /// <summary>関節行列とノードの世界行列。今日はファイルの姿勢のまま評価するだけ。</summary>
    public AnimationPlayer Animation { get; }

    /// <summary><c>VRMC_vrm.specVersion</c>。"1.0" のはず。</summary>
    public string SpecVersion { get; }

    /// <summary>メタ情報(名前・作者・利用条件)。</summary>
    public Meta Info { get; }

    /// <summary>
    /// **ヒューマノイドの骨 → ノード番号**。<c>humanoid.humanBones</c> をそのまま写したもの。
    /// 名前は仕様の表の名前(<c>hips</c>・<c>leftUpperArm</c> など)で、ノードの名前(<c>J_Bip_C_Hips</c>)とは別物。
    /// <b>ノードの名前はツールごとに違う</b>ので、人型として扱うときは必ずこの表を通す。
    /// </summary>
    public IReadOnlyDictionary<string, int> HumanBones { get; }

    /// <summary>MToon で描くマテリアルの数(HUD 用)。</summary>
    public int MToonMaterialCount => Model.Materials.Count(material => material.MToon is not null);

    /// <summary>
    /// **読む**。glTF として <see cref="GltfLoader.Load"/> で読み、JSON をもう一度開いて <c>VRMC_vrm</c> を読む。
    /// VRM 1.0 でなければ例外にする(何で書き出し直せばよいかを添える)。
    /// </summary>
    public static VrmAvatar Load(GL gl, RenderResources resources, string path, Handle<Shader> shader)
    {
        // **先に拡張を確かめる**。0.x のファイルを Model として読んでから断ると、18MB 分のテクスチャを無駄に上げることになる。
        string specVersion;
        Meta meta;
        Dictionary<string, int> bones;

        using (JsonDocument json = GltfLoader.ReadJson(path))
        {
            JsonElement root = json.RootElement;
            JsonElement extensions = root.TryGetProperty("extensions", out JsonElement e) ? e : default;

            if (extensions.ValueKind != JsonValueKind.Object
                || !extensions.TryGetProperty("VRMC_vrm", out JsonElement vrm))
            {
                string hint = extensions.ValueKind == JsonValueKind.Object && extensions.TryGetProperty("VRM", out _)
                    ? "VRM 0.x のファイルです。VRoid Studio(v1.20 以降)で開き、VRM 1.0 で書き出し直してください"
                    : "VRMC_vrm 拡張がありません(VRM ではない glTF です)";
                throw new InvalidDataException($"{Path.GetFileName(path)}: {hint}");
            }

            specVersion = Text(vrm, "specVersion", "?");
            meta = ReadMeta(vrm.TryGetProperty("meta", out JsonElement m) ? m : default);
            bones = ReadHumanBones(vrm);
        }

        Model model = GltfLoader.Load(gl, resources, path, shader);
        return new VrmAvatar(model, specVersion, meta, bones);
    }

    /// <summary>
    /// 骨のノード番号。割り当てられていなければ -1(任意の骨は無いことがある)。
    /// </summary>
    public int BoneNode(string bone) => HumanBones.TryGetValue(bone, out int node) ? node : -1;

    /// <summary>
    /// **骨の世界の位置**。<paramref name="placement"/> はアバターを置く行列(足元をどこに立たせるか)。
    /// 「顔に寄る」カメラが <c>head</c> を引くのに使う——ノードの名前ではなく骨の名前で引けるのがヒューマノイドの値打ち。
    /// </summary>
    public Vector3 BonePosition(string bone, Matrix4x4 placement)
    {
        int node = BoneNode(bone);
        if (node < 0)
        {
            return placement.Translation;
        }

        return (Animation.GetNodeWorld(node) * placement).Translation;
    }

    /// <summary>
    /// **ノード <paramref name="node"/> の祖先に <paramref name="ancestor"/> が居るか**。
    /// 骨の親子の約束(<see cref="RequiredHierarchy"/>)を確かめるのに使う。
    /// </summary>
    public bool IsAncestor(int ancestor, int node)
    {
        for (int current = Model.Nodes[node].Parent; current >= 0; current = Model.Nodes[current].Parent)
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// パーツに送る関節行列。スキンを持たないパーツは空(受け取った側は uSkinned = 0)。
    /// </summary>
    public ReadOnlySpan<Matrix4x4> PartJoints(in Model.Part part) =>
        part.SkinIndex >= 0 ? Animation.GetJointMatrices(part.SkinIndex) : default;

    /// <summary>
    /// パーツを置く行列。スキン付きは単位行列(位置は関節行列が持つ)なので、置き場所の行列だけになる。
    /// VRoid のアバターは3つのメッシュが全部スキン付き。
    /// </summary>
    public static Matrix4x4 PartMatrix(in Model.Part part, Matrix4x4 placement) =>
        part.Transform * placement;

    public void Dispose()
    {
        // プレイヤーは GPU のものを握っていないので、モデルだけ畳めばよい(Program.SetModel のコメント)。
        Model.Dispose();
    }

    private static Meta ReadMeta(JsonElement meta)
    {
        string[] authors = meta.ValueKind == JsonValueKind.Object
            && meta.TryGetProperty("authors", out JsonElement list)
            && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(author => author.GetString() ?? "?").ToArray()
                : [];

        // **省略されたときの値は、仕様が決めた「いちばん厳しい側」**。書いていない許可は、許可していないものとして読む。
        return new Meta(
            Text(meta, "name", "?"),
            authors,
            Text(meta, "licenseUrl", "?"),
            Text(meta, "avatarPermission", "onlyAuthor"),
            Text(meta, "commercialUsage", "personalNonProfit"),
            Text(meta, "creditNotation", "required"),
            meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("allowRedistribution", out JsonElement r)
                && r.ValueKind == JsonValueKind.True,
            Text(meta, "modification", "prohibited"));
    }

    private static Dictionary<string, int> ReadHumanBones(JsonElement vrm)
    {
        var bones = new Dictionary<string, int>();

        if (vrm.TryGetProperty("humanoid", out JsonElement humanoid)
            && humanoid.TryGetProperty("humanBones", out JsonElement humanBones)
            && humanBones.ValueKind == JsonValueKind.Object)
        {
            // **1.0 は「骨の名前 → {node}」の辞書**。0.x は配列で、要素の中に bone と node を持っていた。
            foreach (JsonProperty bone in humanBones.EnumerateObject())
            {
                if (bone.Value.TryGetProperty("node", out JsonElement node) && node.ValueKind == JsonValueKind.Number)
                {
                    bones[bone.Name] = node.GetInt32();
                }
            }
        }

        return bones;
    }

    private static string Text(JsonElement owner, string name, string fallback) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}
