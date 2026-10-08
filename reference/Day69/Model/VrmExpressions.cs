using System.Numerics;
using System.Text.Json;

namespace HonyaEngine;

/// <summary>
/// **VRM 1.0 の表情**(<c>VRMC_vrm.expressions</c>。Day 69)。
///
/// <para>
/// 表情は「名前の付いた、まとめて動かすつまみ」。1つの表情が3種類のものを同じ重みで動かす。
/// </para>
/// <list type="bullet">
/// <item><b>モーフターゲット</b>(<c>morphTargetBinds</c>)… どのノードの何番を、どれだけ。ほとんどの表情はこれだけ</item>
/// <item><b>マテリアルの色</b>(<c>materialColorBinds</c>)… 頬を赤らめる、目の色を変える。元の色から目標の色へ寄せる</item>
/// <item><b>UV のずらし</b>(<c>textureTransformBinds</c>)… 絵を滑らせる。瞳を絵で動かすアバターや、漫符の切り替えに使う</item>
/// </list>
///
/// <para>
/// <b>名前に約束がある</b>のが VRM の値打ち。ノードやモーフの名前はツールごとに違う(VRoid は Fcl_EYE_Close)が、
/// プリセットの <c>blink</c> はどの VRM でも「両目を閉じる」。だから自動まばたきも、Day 71 の VRMA の表情も、
/// アバターを差し替えたまま動く——Day 68 のヒューマノイドの骨の表と同じ考え方。
/// </para>
///
/// <para>
/// <b>表情どうしの約束</b>が2つある。<c>isBinary</c>(0 か 1 にしかならない)と、
/// <c>override</c>(この表情が出ている間、まばたき・視線・口の表情を抑える)。評価の順番は three-vrm の
/// VRMExpressionManager に合わせた(<see cref="Update"/>)。
/// </para>
/// </summary>
internal sealed class VrmExpressions
{
    /// <summary>ほかの表情をどう抑えるか。<c>overrideBlink</c> などの値。</summary>
    public enum Override
    {
        /// <summary>抑えない。</summary>
        None,

        /// <summary>この表情が少しでも出ていれば、相手を完全に止める。</summary>
        Block,

        /// <summary>この表情の重みのぶんだけ、相手を弱める(重み 0.3 なら相手は 0.7 倍)。</summary>
        Blend,
    }

    /// <summary>
    /// マテリアルのどの色を動かすか。<c>materialColorBinds.type</c> の6つ。MToon の欄の名前と対応している。
    /// </summary>
    public enum ColorType
    {
        /// <summary>ベースカラー(MToon の明るい側の色 = glTF の baseColorFactor)。</summary>
        Color,

        /// <summary>発光の色。</summary>
        EmissionColor,

        /// <summary>影の色(shadeColorFactor)。</summary>
        ShadeColor,

        /// <summary>マットキャップの色。</summary>
        MatcapColor,

        /// <summary>リムの色。</summary>
        RimColor,

        /// <summary>アウトラインの色。</summary>
        OutlineColor,
    }

    /// <summary>
    /// **プリセットの名前**(仕様の表)。感情5つ・口5つ・まばたき3つ・視線4つ・neutral の 18 個。
    /// </summary>
    public static readonly string[] PresetNames =
    [
        "happy", "angry", "sad", "relaxed", "surprised",
        "aa", "ih", "ou", "ee", "oh",
        "blink", "blinkLeft", "blinkRight",
        "lookUp", "lookDown", "lookLeft", "lookRight",
        "neutral",
    ];

    /// <summary><c>overrideBlink</c> が抑える相手。</summary>
    public static readonly string[] BlinkNames = ["blink", "blinkLeft", "blinkRight"];

    /// <summary><c>overrideLookAt</c> が抑える相手。</summary>
    public static readonly string[] LookAtNames = ["lookUp", "lookDown", "lookLeft", "lookRight"];

    /// <summary><c>overrideMouth</c> が抑える相手。</summary>
    public static readonly string[] MouthNames = ["aa", "ih", "ou", "ee", "oh"];

    /// <summary>モーフターゲットの割り当て1つ。</summary>
    public sealed record MorphBind(MorphTargets Morph, int Index, float Weight);

    /// <summary>
    /// マテリアルの色の割り当て1つ。**元の色を覚えておく**——毎フレーム元に戻してから足すため。
    /// </summary>
    public sealed class ColorBind
    {
        public ColorBind(Material material, ColorType type, Vector4 target)
        {
            Material = material;
            Type = type;
            Target = target;
            Initial = Get(material, type);
        }

        public Material Material { get; }

        public ColorType Type { get; }

        /// <summary>重み 1 のときの色(リニア)。</summary>
        public Vector4 Target { get; }

        /// <summary>読んだときの色。表情が 0 ならこの色に戻る。</summary>
        public Vector4 Initial { get; }
    }

    /// <summary>UV のずらしの割り当て1つ。元の拡大とずらしを覚えておく。</summary>
    public sealed class UvBind
    {
        public UvBind(Material material, MToon mtoon, Vector2 scale, Vector2 offset)
        {
            Material = material;
            MToon = mtoon;
            Scale = scale;
            Offset = offset;
            InitialScale = mtoon.UvScale;
            InitialOffset = mtoon.UvOffset;
        }

        public Material Material { get; }

        public MToon MToon { get; }

        public Vector2 Scale { get; }

        public Vector2 Offset { get; }

        public Vector2 InitialScale { get; }

        public Vector2 InitialOffset { get; }
    }

    /// <summary>
    /// **表情1つ**。外から <see cref="Weight"/> を書き、<see cref="Update"/> が割り当てへ流す。
    /// </summary>
    public sealed class Expression
    {
        public Expression(string name, bool isPreset, bool isBinary, Override blink, Override lookAt, Override mouth)
        {
            Name = name;
            IsPreset = isPreset;
            FileIsBinary = isBinary;
            IsBinary = isBinary;
            FileOverrideBlink = blink;
            FileOverrideLookAt = lookAt;
            FileOverrideMouth = mouth;
            OverrideBlink = blink;
            OverrideLookAt = lookAt;
            OverrideMouth = mouth;
        }

        public string Name { get; }

        /// <summary>プリセット(名前に約束がある)か、作者が足したカスタムか。</summary>
        public bool IsPreset { get; }

        /// <summary>
        /// **0 か 1 にしかならない**。重みが 0.5 を超えたら 1、それ以外は 0。
        /// 漫符(汗・怒りマーク)のように「半分だけ出る」と変な絵になるものに付ける。
        /// </summary>
        public bool IsBinary { get; set; }

        public Override OverrideBlink { get; set; }

        public Override OverrideLookAt { get; set; }

        public Override OverrideMouth { get; set; }

        /// <summary>ファイルに書かれていた値(試しに書き換えたあと戻すため)。</summary>
        public bool FileIsBinary { get; }

        public Override FileOverrideBlink { get; }

        public Override FileOverrideLookAt { get; }

        public Override FileOverrideMouth { get; }

        public List<MorphBind> MorphBinds { get; } = [];

        public List<ColorBind> ColorBinds { get; } = [];

        public List<UvBind> UvBinds { get; } = [];

        /// <summary>**外が書く重み**(0〜1)。まばたきなら自動まばたきが、感情ならメニューが書く。</summary>
        public float Weight { get; set; }

        /// <summary>isBinary を通した重み。</summary>
        public float OutputWeight => IsBinary ? (Weight > 0.5f ? 1.0f : 0.0f) : Weight;

        /// <summary>
        /// **最後に割り当てへ流した重み**(isBinary と、ほかの表情に抑えられたぶんを通したもの)。内訳と HUD 用。
        /// </summary>
        public float AppliedWeight { get; internal set; }

        /// <summary>この表情が相手をどれだけ抑えるか(0〜1)。three-vrm の overrideBlinkAmount と同じ。</summary>
        public float OverrideAmount(Override mode) => mode switch
        {
            Override.Block => OutputWeight > 0.0f ? 1.0f : 0.0f,
            Override.Blend => OutputWeight,
            _ => 0.0f,
        };
    }

    private readonly List<Expression> _expressions = [];

    public IReadOnlyList<Expression> All => _expressions;

    /// <summary>最後の <see cref="Update"/> で、まばたき・視線・口の表情に掛けた倍率(1 なら抑えていない)。</summary>
    public (float Blink, float LookAt, float Mouth) Multipliers { get; private set; } = (1.0f, 1.0f, 1.0f);

    /// <summary>名前で引く。無ければ null(プリセットでも、作者が作っていなければ無い)。</summary>
    public Expression? Find(string name) => _expressions.FirstOrDefault(expression => expression.Name == name);

    /// <summary>重みを書く。表情が無ければ何もしない(どのアバターでも同じ呼び方ができるように)。</summary>
    public void SetWeight(string name, float weight)
    {
        Expression? expression = Find(name);
        if (expression is not null)
        {
            expression.Weight = weight;
        }
    }

    public float GetWeight(string name) => Find(name)?.Weight ?? 0.0f;

    /// <summary>表情を足す。ファイルから読んだものも、コードで作ったもの(今日の「照れ」「目そらし」)もここを通る。</summary>
    public void Add(Expression expression)
    {
        if (Find(expression.Name) is not null)
        {
            throw new InvalidOperationException($"表情「{expression.Name}」はもうある");
        }

        _expressions.Add(expression);
    }

    /// <summary>
    /// **重みを割り当てへ流す**。3段でできている(three-vrm の VRMExpressionManager.update と同じ順)。
    ///
    /// <code>
    ///   1. 抑える量を決める   まばたきの倍率 = max(0, 1 - Σ 各表情の overrideBlink の量)   視線・口も同じ
    ///   2. 元に戻す           割り当てのあるモーフの重みを 0 に、色と UV を読んだときの値に
    ///   3. 足す               各表情の重み(isBinary を通し、抑える倍率を掛けたもの)x 割り当ての重み を足し込む
    /// </code>
    ///
    /// <para>
    /// <b>1 を先に全部済ませる</b>のが肝。表情を1つずつ「抑えて足す」と、並び順で結果が変わる
    /// (happy の後に blink が来れば抑えられ、前なら抑えられない)。
    /// <b>2 で戻してから足す</b>のは、2つの表情が同じモーフや色を動かすことがあるから。前のフレームの値に足すと、毎フレーム積み上がる。
    /// </para>
    /// </summary>
    public void Update()
    {
        // --- 1. 抑える量 ---
        float blink = 1.0f;
        float lookAt = 1.0f;
        float mouth = 1.0f;

        foreach (Expression expression in _expressions)
        {
            blink -= expression.OverrideAmount(expression.OverrideBlink);
            lookAt -= expression.OverrideAmount(expression.OverrideLookAt);
            mouth -= expression.OverrideAmount(expression.OverrideMouth);
        }

        Multipliers = (MathF.Max(blink, 0.0f), MathF.Max(lookAt, 0.0f), MathF.Max(mouth, 0.0f));

        // --- 2. 元に戻す ---
        foreach (Expression expression in _expressions)
        {
            foreach (MorphBind bind in expression.MorphBinds)
            {
                bind.Morph.Weights[bind.Index] = 0.0f;
            }

            foreach (ColorBind bind in expression.ColorBinds)
            {
                Set(bind.Material, bind.Type, bind.Initial);
            }

            foreach (UvBind bind in expression.UvBinds)
            {
                bind.MToon.UvScale = bind.InitialScale;
                bind.MToon.UvOffset = bind.InitialOffset;
            }
        }

        // --- 3. 足す ---
        foreach (Expression expression in _expressions)
        {
            float weight = expression.OutputWeight * Multiplier(expression.Name);

            // **抑えられた isBinary は 0**。0.6 倍に抑えられたら 0.6 で出すのではなく、出さない(0 か 1 の約束を守る)。
            if (expression.IsBinary && weight < 1.0f)
            {
                weight = 0.0f;
            }

            expression.AppliedWeight = weight;
            if (weight == 0.0f)
            {
                continue;
            }

            foreach (MorphBind bind in expression.MorphBinds)
            {
                bind.Morph.Weights[bind.Index] += bind.Weight * weight;
            }

            // 色と UV は「目標 - 元」の差を重みで足す。2つの表情が同じ色を動かしても、差が足し合わさるだけで済む。
            foreach (ColorBind bind in expression.ColorBinds)
            {
                Vector4 current = Get(bind.Material, bind.Type);
                Set(bind.Material, bind.Type, current + ((bind.Target - bind.Initial) * weight));
            }

            foreach (UvBind bind in expression.UvBinds)
            {
                bind.MToon.UvScale += (bind.Scale - bind.InitialScale) * weight;
                bind.MToon.UvOffset += (bind.Offset - bind.InitialOffset) * weight;
            }
        }
    }

    /// <summary>この表情に掛ける倍率。プリセットのまばたき・視線・口だけが抑えられる(カスタムは抑えられない)。</summary>
    private float Multiplier(string name) =>
        (BlinkNames.Contains(name) ? Multipliers.Blink : 1.0f)
        * (LookAtNames.Contains(name) ? Multipliers.LookAt : 1.0f)
        * (MouthNames.Contains(name) ? Multipliers.Mouth : 1.0f);

    /// <summary>
    /// **ファイルから読む**。<paramref name="expressions"/> は <c>VRMC_vrm.expressions</c>(無ければ default)。
    /// ノード番号はモデルのモーフへ、マテリアルの番号は <see cref="Model.MaterialsByIndex"/> で引き直す。
    /// 引けない割り当ては、知らせて飛ばす(表情1つが壊れていても、ほかの表情は使えるように)。
    /// </summary>
    public static VrmExpressions Read(JsonElement expressions, Model model)
    {
        var result = new VrmExpressions();
        if (expressions.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (string group in (string[])["preset", "custom"])
        {
            if (!expressions.TryGetProperty(group, out JsonElement list) || list.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty entry in list.EnumerateObject())
            {
                result.Add(ReadExpression(entry.Name, entry.Value, group == "preset", model));
            }
        }

        return result;
    }

    private static Expression ReadExpression(string name, JsonElement source, bool isPreset, Model model)
    {
        var expression = new Expression(
            name,
            isPreset,
            source.TryGetProperty("isBinary", out JsonElement binary) && binary.ValueKind == JsonValueKind.True,
            ReadOverride(source, "overrideBlink"),
            ReadOverride(source, "overrideLookAt"),
            ReadOverride(source, "overrideMouth"));

        foreach (JsonElement bind in Array(source, "morphTargetBinds"))
        {
            int node = Int(bind, "node");
            int index = Int(bind, "index");
            MorphTargets? morph = model.FindMorph(node);

            if (morph is null || index < 0 || index >= morph.TargetCount)
            {
                Console.WriteLine($"[VRM] 表情 {name}: ノード {node} のモーフ {index} が無いので飛ばす");
                continue;
            }

            float weight = bind.TryGetProperty("weight", out JsonElement w) ? w.GetSingle() : 0.0f;
            expression.MorphBinds.Add(new MorphBind(morph, index, weight));
        }

        foreach (JsonElement bind in Array(source, "materialColorBinds"))
        {
            ColorType? type = ReadColorType(bind);
            if (!model.MaterialsByIndex.TryGetValue(Int(bind, "material"), out Material? material) || type is null)
            {
                Console.WriteLine($"[VRM] 表情 {name}: マテリアルの色の割り当てを読めないので飛ばす");
                continue;
            }

            expression.ColorBinds.Add(new ColorBind(material, type.Value, Vector(bind, "targetValue", 4, Vector4.One)));
        }

        foreach (JsonElement bind in Array(source, "textureTransformBinds"))
        {
            if (!model.MaterialsByIndex.TryGetValue(Int(bind, "material"), out Material? material) || material.MToon is null)
            {
                Console.WriteLine($"[VRM] 表情 {name}: UV の割り当ては MToon のマテリアルにしか付けられないので飛ばす");
                continue;
            }

            Vector4 scale = Vector(bind, "scale", 2, Vector4.One);
            Vector4 offset = Vector(bind, "offset", 2, Vector4.Zero);
            expression.UvBinds.Add(new UvBind(
                material, material.MToon, new Vector2(scale.X, scale.Y), new Vector2(offset.X, offset.Y)));
        }

        return expression;
    }

    /// <summary>
    /// **マテリアルの色を読む**。6つの種類をマテリアルの欄へ対応させる。MToon を持たないマテリアルの MToon の欄は 0。
    /// </summary>
    public static Vector4 Get(Material material, ColorType type) => type switch
    {
        ColorType.Color => material.BaseColorFactor,
        ColorType.EmissionColor => new Vector4(material.EmissiveFactor, 1.0f),
        ColorType.ShadeColor => new Vector4(material.MToon?.ShadeColorFactor ?? Vector3.Zero, 1.0f),
        ColorType.MatcapColor => new Vector4(material.MToon?.MatcapFactor ?? Vector3.Zero, 1.0f),
        ColorType.RimColor => new Vector4(material.MToon?.ParametricRimColorFactor ?? Vector3.Zero, 1.0f),
        ColorType.OutlineColor => new Vector4(material.MToon?.OutlineColorFactor ?? Vector3.Zero, 1.0f),
        _ => Vector4.Zero,
    };

    /// <summary>マテリアルの色を書く。アルファを持つのはベースカラーだけ(ほかは rgb だけ使う)。</summary>
    public static void Set(Material material, ColorType type, Vector4 value)
    {
        var rgb = new Vector3(value.X, value.Y, value.Z);
        switch (type)
        {
            case ColorType.Color:
                material.BaseColorFactor = value;
                break;
            case ColorType.EmissionColor:
                material.EmissiveFactor = rgb;
                break;
            case ColorType.ShadeColor when material.MToon is not null:
                material.MToon.ShadeColorFactor = rgb;
                break;
            case ColorType.MatcapColor when material.MToon is not null:
                material.MToon.MatcapFactor = rgb;
                break;
            case ColorType.RimColor when material.MToon is not null:
                material.MToon.ParametricRimColorFactor = rgb;
                break;
            case ColorType.OutlineColor when material.MToon is not null:
                material.MToon.OutlineColorFactor = rgb;
                break;
        }
    }

    private static Override ReadOverride(JsonElement source, string name) =>
        source.TryGetProperty(name, out JsonElement value) ? value.GetString() switch
        {
            "block" => Override.Block,
            "blend" => Override.Blend,
            _ => Override.None,
        }
        : Override.None;

    private static ColorType? ReadColorType(JsonElement bind) =>
        bind.TryGetProperty("type", out JsonElement type) ? type.GetString() switch
        {
            "color" => ColorType.Color,
            "emissionColor" => ColorType.EmissionColor,
            "shadeColor" => ColorType.ShadeColor,
            "matcapColor" => ColorType.MatcapColor,
            "rimColor" => ColorType.RimColor,
            "outlineColor" => ColorType.OutlineColor,
            _ => null,
        }
        : null;

    private static IEnumerable<JsonElement> Array(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : [];

    private static int Int(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : -1;

    private static Vector4 Vector(JsonElement owner, string name, int count, Vector4 fallback)
    {
        if (!owner.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        Span<float> values = [fallback.X, fallback.Y, fallback.Z, fallback.W];
        int i = 0;
        foreach (JsonElement value in list.EnumerateArray())
        {
            if (i < count)
            {
                values[i++] = value.GetSingle();
            }
        }

        return new Vector4(values[0], values[1], values[2], values[3]);
    }
}
