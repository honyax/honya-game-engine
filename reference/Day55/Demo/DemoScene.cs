using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// シーンの1つを<b>当たり判定としてどう扱うか</b>(Day 51)。
/// JSON の <c>collision</c> に <c>"box"</c> / <c>"plane"</c> / <c>"none"</c> と書く。
///
/// <para>
/// <b>描画メッシュから自動で起こさない</b>のが今日の判断。
/// 60m 角の地面の板を素直に箱にすると<b>厚み 0 の箱</b>になり、
/// 街灯の細い柱は<b>腕まで含んだ大きな箱</b>になる。
/// どちらも「メッシュを見れば分かる」ことではないので、
/// 露出や太陽の方位と同じく<b>絵を作る人が書く</b>ほうへ寄せた
/// (<see cref="DemoScene"/> の説明にある「シーンはコードではなくファイルに書く」)。
/// </para>
/// </summary>
internal enum SceneCollisionKind
{
    /// <summary>当たらない。**通り抜ける小物**(倒したタイヤなど)。</summary>
    None,

    /// <summary>世界軸に沿った箱で近似する。**既定**。</summary>
    Box,

    /// <summary>無限に広い平面。**地面だけ**。</summary>
    Plane,
}

/// <summary>
/// **デモ v1 のシーン**(Day 39)。今日の主役その3。
///
/// Day 31〜38 で積んだ描画機能——HDR / glTF / 影 / 法線 / PBR / IBL / SSAO / FXAA——は、
/// どれも<b>その機能を見るための最小のセット</b>の上で動かしてきた。
/// 立方体と床、材質グリッド、モデル1体。
/// 今日はそれを本番の絵の上に載せる。
///
/// <para>
/// <b>シーンはコードではなくファイルに書く</b>。Day 24 で
/// <see cref="SceneSerializer"/> を書いたときと同じ判断だが、
/// 動機はもう少し切実になっている——
/// 「木箱をあと 30cm 右へ」を試すたびにビルドを待つのは、
/// <b>絵を作る作業として成立しない</b>。
/// <c>Ctrl+Shift+F10</c> で JSON を読み直せるようにしてあるので、
/// エディタで数字を書き換えて保存 → 押す、で反映される。
/// </para>
///
/// <para>
/// <b>Day 24 の <see cref="Scene"/> を使わないのはなぜか</b>。
/// あちらは GameObject + Component の入れ物で、**毎フレーム更新される**ことが前提にある。
/// こちらは静止した背景で、要るのは「メッシュ・マテリアル・行列」の平らな並びだけ。
/// <see cref="Model"/> が glTF のノードの木を平らにしたのとまったく同じ理屈で、
/// <b>動かないものに階層は要らない</b>。
/// キャラクターが歩き回る Day 51 では、動くものだけが <see cref="Scene"/> に載る。
/// </para>
///
/// <para>
/// <b>持ち物</b>。板のメッシュは外から借りる(<see cref="Primitives.CreateQuad"/> のもの)。
/// glTF は自分で読むので <see cref="Model"/> を所有し、
/// テクスチャは <see cref="RenderResources"/> から借りて参照カウントを返す——
/// Day 21 で決めた作法をそのまま踏襲している。
/// </para>
///
/// <para>
/// <b>Day 40 での変更</b>: JSON に<b>カメラワーク</b>(<c>camera.shots</c>)と
/// <b>色調整</b>(<c>lighting.grade</c>)が書けるようになった。
/// どちらも「この絵をどう見せたいか」の側で、露出や IBL の強さと同類。
/// <b>コードではなくシーンに置く</b>と、絵づくりがビルドを待たずに回る。
/// 書いていない JSON もそのまま読めるので、Day 39 のファイルは何も変えずに動く。
/// </para>
/// </summary>
internal sealed class DemoScene : IDisposable
{
    /// <summary>
    /// 描くもの1つぶん。**3つのパスが同じ並びを回る**。
    ///
    /// <para>
    /// Day 33 の <c>RenderShadowPass</c> には
    /// 「実際のエンジンはこれをフラグ(<c>CastShadow</c>)で持つ。
    /// ここで手書きの分岐にしてあるのは、まず『選ぶ必要がある』ことを見るためで、
    /// フラグにするのはシーン側に影を載せる Day 39 の仕事になる」と書いてあった。
    /// <see cref="CastShadow"/> がその宿題の答え。
    /// </para>
    /// </summary>
    /// <param name="Name">デバッグ表示用。</param>
    /// <param name="Mesh">板か、glTF のパーツ。</param>
    /// <param name="Material">見た目。</param>
    /// <param name="Transform">世界行列。</param>
    /// <param name="CastShadow">影を落とすか(深度パスに入れるか)。</param>
    /// <param name="BoundsMin">
    /// **世界空間の境界箱**(Day 51)。<see cref="SceneCollision"/> が
    /// 当たり判定の箱を起こすのに使う。
    ///
    /// <para>
    /// <b>世界空間で持つ</b>のは、そうしないと「どの行列を掛ければよいか」が
    /// 種類ごとに違うから。板は <see cref="Transform"/> がそのまま答えだが、
    /// glTF のパーツは <c>Model.Part.BoundsMin</c> が<b>すでに
    /// <c>Part.Transform</c> を通ったあとの値</b>なので、
    /// 掛けるべきなのは小物を置く行列(<c>placement</c>)だけになる。
    /// 使う側にこの区別を持ち出させると必ず取り違える——
    /// 実際に一度取り違えて、ゴミ箱の当たり判定が 3m の箱になった。
    /// </para>
    /// </param>
    /// <param name="BoundsMax">同上。</param>
    /// <param name="Collision">当たり判定としての扱い(Day 51)。</param>
    internal readonly record struct Item(
        string Name,
        Mesh<Vertex> Mesh,
        Material Material,
        Matrix4x4 Transform,
        bool CastShadow,
        Vector3 BoundsMin,
        Vector3 BoundsMax,
        SceneCollisionKind Collision);

    private readonly List<Model> _models = [];
    private readonly List<Handle<Texture>> _textures = [];
    private readonly RenderResources _resources;

    private bool _disposed;

    private DemoScene(RenderResources resources, string sourcePath)
    {
        _resources = resources;
        SourcePath = sourcePath;
    }

    /// <summary>シーンの名前。HUD に出す。</summary>
    public string Name { get; private set; } = "(名無し)";

    /// <summary>読み込み元の JSON。<c>Ctrl+Shift+F10</c> の読み直しで使う。</summary>
    public string SourcePath { get; }

    /// <summary>環境マップの元にする HDRI(<c>assets/</c> からの相対パス)。</summary>
    public string HdriPath { get; private set; } = string.Empty;

    /// <summary>このシーン向けの露出(Day 31 の <c>PostProcess.Exposure</c>)。</summary>
    public float Exposure { get; private set; } = 1.0f;

    /// <summary>IBL の強さ(Day 36)。</summary>
    public float IblIntensity { get; private set; } = 1.0f;

    /// <summary>
    /// 抽出した太陽の放射照度に掛ける倍率。
    /// **1.0 が物理的に正しい**。絵づくりで太陽を強めたいときだけ触る。
    /// </summary>
    public float SunScale { get; private set; } = 1.0f;

    /// <summary>
    /// IBL とは別に足す環境光(Day 32 の <c>uAmbientColor</c>)。
    /// **IBL があるなら 0 が正しい**——足すと二重になる。
    /// 逃げ道として残してあるだけで、既定は 0。
    /// </summary>
    public Vector3 Ambient { get; private set; } = Vector3.Zero;

    /// <summary>環境マップから太陽を抜くか(<see cref="SkyAnalysis.RemoveSun"/>)。</summary>
    public bool RemoveSunFromIbl { get; private set; } = true;

    /// <summary>太陽を取り出すしきい値(最大輝度に対する比)。</summary>
    public float SunThreshold { get; private set; } = 0.05f;

    /// <summary>
    /// **太陽に来てほしい方位**(度。<c>atan2(z, x)</c> の向き)。
    ///
    /// <para>
    /// HDRI の太陽は「その写真を撮った場所での方位」に居るので、
    /// 組んだシーンの都合とは無関係。ここに角度を書いておくと、
    /// <b>空のほうを回して太陽をその方位へ持ってくる</b>
    /// (<c>shaders/equirect.frag</c> の <c>uSkyYaw</c>)。
    /// </para>
    ///
    /// <para>
    /// <b>HDRI を差し替えても太陽の位置が動かない</b>のがこの持ち方の値打ち。
    /// <c>Ctrl+Shift+F3</c> で 3 枚を回すと、向きは固定のまま
    /// <b>光の質だけ</b>が変わるので、見比べられる。
    /// </para>
    /// </summary>
    public float SunAzimuth { get; private set; } = 25.0f;

    /// <summary>
    /// シャドウマップが覆う半径(Day 33 の <c>ShadowMap.Radius</c>)。
    /// **太陽が低いと影が長い**ので、既定の 6 では足りない。
    /// </summary>
    public float ShadowRadius { get; private set; } = 18.0f;

    /// <summary>決めの構図(<c>Ctrl+Shift+F12</c>)。</summary>
    public Vector3 CameraTarget { get; private set; } = new(0.0f, 1.2f, 0.0f);

    public float CameraDistance { get; private set; } = 10.0f;

    /// <summary>方位(ラジアン)。JSON には度で書く。</summary>
    public float CameraYaw { get; private set; }

    public float CameraPitch { get; private set; } = 0.15f;

    /// <summary>
    /// 垂直画角(ラジアン。Day 40)。<see cref="Camera.FieldOfView"/> の既定は 60 度だが、
    /// **デモの絵には広すぎる**。60 度は「机の前で操作するゲーム」の画角で、
    /// 遠近が誇張されて壁が倒れて見える。35〜45 度あたりが落ち着く。
    /// </summary>
    public float CameraFieldOfView { get; private set; } = 40.0f * (MathF.PI / 180.0f);

    /// <summary>
    /// **プレイヤーの出発点**(Day 51)。<c>player.spawn</c>。足元の位置。
    ///
    /// <para>
    /// 決めの構図(<see cref="CameraTarget"/>)と別に持つ。
    /// あちらは「この絵をどう見せたいか」で、こちらは「どこから遊び始めるか」——
    /// 同じ数字にしたくなる場面はあるが、意味が違うものを1つの欄に詰めると
    /// <b>片方の都合でもう片方が動く</b>。
    /// </para>
    /// </summary>
    public Vector3 PlayerSpawn { get; private set; } = Vector3.Zero;

    /// <summary>出発点で向いている方角(ラジアン。JSON には度で書く。Day 51)。</summary>
    public float PlayerYaw { get; private set; }

    /// <summary>
    /// カメラワーク(Day 40)。<c>camera.shots</c> が無ければ <c>null</c> で、
    /// そのときは決めの構図の1枚だけになる(Day 39 の挙動)。
    /// </summary>
    public CameraPath? Shots { get; private set; }

    /// <summary>
    /// カラーグレーディングの設定(Day 40)。
    /// **絵づくりの最後の一枚**なので、シーンと一緒に書けるようにした。
    /// </summary>
    public GradeSettings Grade { get; private set; } = new(false, 0.0f, 0.0f, 1.0f, 1.0f);

    /// <summary>グレーディングのつまみ(Day 38 の <see cref="ColorGrade"/> に流し込む)。</summary>
    internal readonly record struct GradeSettings(
        bool Enabled,
        float Temperature,
        float Tint,
        float Contrast,
        float Saturation);

    /// <summary>描くもの。**この順に描けばよい**。</summary>
    public IReadOnlyList<Item> Items => _items;

    private readonly List<Item> _items = [];

    /// <summary>三角形の総数。</summary>
    public int TriangleCount { get; private set; }

    /// <summary>読み込みにかかった時間(ミリ秒)。</summary>
    public double LoadMilliseconds { get; private set; }

    /// <summary>読み込んだ glTF の数。</summary>
    public int ModelCount => _models.Count;

    /// <summary>このシーンが握っているテクスチャの枚数。</summary>
    public int TextureCount => _textures.Count + _models.Sum(model => model.TextureCount);

    /// <summary>影を落とす設定になっているものの数。</summary>
    public int ShadowCasterCount => _items.Count(item => item.CastShadow);

    /// <summary>
    /// JSON からシーンを組み立てる。
    ///
    /// <para>
    /// <b>失敗は例外にする</b>。素材が1つ足りないだけで真っ黒な画面が出るより、
    /// どのファイルが無いかを言って止まるほうがよい。
    /// 呼び出し側(<c>Program.LoadDemoScene</c>)が受けて、デモを off に戻す。
    /// </para>
    /// </summary>
    /// <param name="resolveAsset">
    /// <c>assets/</c> からの相対パスを実際のパスに直す関数。
    /// **どこを探すかの規則をこのクラスに持ち込まない**ため、外から渡してもらう。
    /// </param>
    public static DemoScene Load(
        GL gl,
        RenderResources resources,
        string jsonPath,
        Handle<Shader> shader,
        Mesh<Vertex> quad,
        Func<string, string> resolveAsset)
    {
        var stopwatch = Stopwatch.StartNew();

        var scene = new DemoScene(resources, jsonPath);

        using var document = JsonDocument.Parse(
            File.ReadAllText(jsonPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        JsonElement root = document.RootElement;

        scene.Name = GetString(root, "name", "(名無し)");
        scene.HdriPath = GetString(root, "hdri", string.Empty);

        if (root.TryGetProperty("lighting", out JsonElement lighting))
        {
            scene.Exposure = GetFloat(lighting, "exposure", 1.0f);
            scene.IblIntensity = GetFloat(lighting, "iblIntensity", 1.0f);
            scene.SunScale = GetFloat(lighting, "sunScale", 1.0f);
            scene.Ambient = GetVector3(lighting, "ambient", Vector3.Zero);
            scene.RemoveSunFromIbl = GetBool(lighting, "removeSunFromIbl", true);
            scene.SunThreshold = GetFloat(lighting, "sunThreshold", 0.05f);
            scene.SunAzimuth = GetFloat(lighting, "sunAzimuth", 25.0f);
            scene.ShadowRadius = GetFloat(lighting, "shadowRadius", 18.0f);

            if (lighting.TryGetProperty("grade", out JsonElement grade))
            {
                scene.Grade = new GradeSettings(
                    Enabled: GetBool(grade, "enabled", true),
                    Temperature: GetFloat(grade, "temperature", 0.0f),
                    Tint: GetFloat(grade, "tint", 0.0f),
                    Contrast: GetFloat(grade, "contrast", 1.0f),
                    Saturation: GetFloat(grade, "saturation", 1.0f));
            }
        }

        if (root.TryGetProperty("camera", out JsonElement camera))
        {
            scene.CameraTarget = GetVector3(camera, "target", scene.CameraTarget);
            scene.CameraDistance = GetFloat(camera, "distance", scene.CameraDistance);

            // **JSON には度で書く**。手で編集するファイルなので、
            // ラジアンで書かせるのは親切ではない。
            scene.CameraYaw = GetFloat(camera, "yaw", 0.0f) * (MathF.PI / 180.0f);
            scene.CameraPitch = GetFloat(camera, "pitch", 10.0f) * (MathF.PI / 180.0f);
            scene.CameraFieldOfView =
                GetFloat(camera, "fov", 40.0f) * (MathF.PI / 180.0f);

            scene.Shots = ReadCameraPath(camera, scene);
        }

        // --- プレイヤーの出発点(Day 51)---
        //
        // **書いていなくても読める**。Day 39〜50 の JSON はこの節を持たないので、
        // 既定(原点・北向き)のまま通る。逆に今日足した節を古い Day が読んでも、
        // 知らないキーは黙って飛ばされる(System.Text.Json の既定)——
        // <b>assets/ は全部の Day で共有している</b>ので、
        // どちらの向きにも壊れないことをここで担保しておく。
        if (root.TryGetProperty("player", out JsonElement player))
        {
            scene.PlayerSpawn = GetVector3(player, "spawn", Vector3.Zero);
            scene.PlayerYaw = GetFloat(player, "yaw", 0.0f) * (MathF.PI / 180.0f);
        }

        // --- 1. マテリアル(板に貼るぶん)---
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);

        if (root.TryGetProperty("materials", out JsonElement materialList))
        {
            foreach (JsonElement entry in materialList.EnumerateArray())
            {
                string name = GetString(entry, "name", $"material{materials.Count}");
                materials[name] = scene.BuildMaterial(entry, shader, resolveAsset);
            }
        }

        // --- 2. 板(地面・壁)---
        if (root.TryGetProperty("surfaces", out JsonElement surfaces))
        {
            foreach (JsonElement entry in surfaces.EnumerateArray())
            {
                string materialName = GetString(entry, "material", string.Empty);
                if (!materials.TryGetValue(materialName, out Material? material))
                {
                    throw new InvalidDataException(
                        $"{Path.GetFileName(jsonPath)}: マテリアル \"{materialName}\" が materials に見つかりません");
                }

                Matrix4x4 transform = ReadTransform(entry);

                // 板は XY 平面の 1x1(<see cref="Primitives.CreateQuad"/>)。
                // **厚みは 0** なので、箱にするときは最低の厚みを与えることになる
                // (<see cref="SceneCollision.MinThickness"/>)。
                (Vector3 boardMin, Vector3 boardMax) = TransformBounds(
                    new Vector3(-0.5f, -0.5f, 0.0f), new Vector3(0.5f, 0.5f, 0.0f), transform);

                scene._items.Add(new Item(
                    Name: GetString(entry, "name", "板"),
                    Mesh: quad,
                    Material: material,
                    Transform: transform,
                    CastShadow: GetBool(entry, "castShadow", false),
                    BoundsMin: boardMin,
                    BoundsMax: boardMax,
                    Collision: ReadCollision(entry)));

                // 板は三角形2枚。glTF のぶんは AddProp が数える。
                scene.TriangleCount += 2;
            }
        }

        // --- 3. 小物(glTF)---
        if (root.TryGetProperty("props", out JsonElement props))
        {
            foreach (JsonElement entry in props.EnumerateArray())
            {
                scene.AddProp(gl, resources, entry, shader, resolveAsset);
            }
        }

        scene.LoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return scene;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (Model model in _models)
        {
            model.Dispose();
        }

        // 板に貼ったテクスチャは**借りている**ので返す(Day 21 の要点3)。
        // 板そのもののメッシュは Program のものなので触らない。
        foreach (Handle<Texture> handle in _textures)
        {
            _resources.Release(handle);
        }
    }

    /// <summary>
    /// 板1枚ぶんのマテリアルを作る。
    ///
    /// <para>
    /// <b>sRGB の指定に注目</b>(Day 32 の要点5)。**色は true、数値は false**。
    /// ベースカラーだけが「人が見る色」で、法線・粗さ・金属度・高さは数値。
    /// ここを取り違えると、粗さが 2.2 乗されて全体がつるつるになる——
    /// 絵は出るので気づきにくい。
    /// </para>
    ///
    /// <para>
    /// <b>ORM の 1 枚を 2 か所に刺す</b>のがここのミソ。
    /// Poly Haven の <c>_arm</c> は **R が AO、G が粗さ、B が金属度**という詰め方で、
    /// これは glTF が想定している並び(<c>occlusionTexture</c> の R、
    /// <c>metallicRoughnessTexture</c> の G と B)とちょうど同じ。
    /// つまり<b>同じ 1 枚を occlusion と metallicRoughness の両方に割り当てればよい</b>。
    /// 2 回 bind することになるが、テクスチャの実体は 1 つなので VRAM は増えない。
    /// </para>
    /// </summary>
    private Material BuildMaterial(JsonElement entry, Handle<Shader> shader, Func<string, string> resolveAsset)
    {
        var material = new Material(shader)
        {
            Name = GetString(entry, "name", "material"),
            UvScale = GetVector2(entry, "uvScale", Vector2.One),
            MetallicFactor = GetFloat(entry, "metallic", 1.0f),
            RoughnessFactor = GetFloat(entry, "roughness", 1.0f),
            NormalScale = GetFloat(entry, "normalScale", 1.0f),
            ParallaxScale = GetFloat(entry, "parallaxScale", 0.0f),
            BaseColorFactor = new Vector4(GetVector3(entry, "baseColorFactor", Vector3.One), 1.0f),
        };

        if (TryGetString(entry, "baseColor", out string? baseColor))
        {
            material.MainTexture = Track(_resources.LoadTexture(resolveAsset(baseColor), srgb: true));
        }

        if (TryGetString(entry, "normal", out string? normal))
        {
            material.NormalTexture = Track(_resources.LoadTexture(resolveAsset(normal), srgb: false));
        }

        if (TryGetString(entry, "orm", out string? orm))
        {
            Handle<Texture> handle = Track(_resources.LoadTexture(resolveAsset(orm), srgb: false));
            material.MetallicRoughnessTexture = handle;

            // **同じ 1 枚を AO にも使う**。R が AO なのは ORM の約束(このメソッドの説明)。
            material.OcclusionTexture = handle;
        }

        if (TryGetString(entry, "height", out string? height))
        {
            material.HeightTexture = Track(_resources.LoadTexture(resolveAsset(height), srgb: false));
        }

        return material;
    }

    /// <summary>
    /// glTF を1体読んで置く。
    ///
    /// <para>
    /// <b>2つの後始末</b>が入る。どちらも「配布されているモデルをそのまま使う」と必ず当たるもの。
    /// </para>
    ///
    /// <list type="number">
    /// <item>
    /// <b>要らないノードを落とす</b>(<c>skipNodes</c>)。
    /// Poly Haven の消火栓とゴミ箱には「きれいな版」と「錆びた版」が並べて入っている。
    /// そのまま置くと 2 体並ぶので、名前で選り分ける。
    /// </item>
    /// <item>
    /// <b>足元を原点に合わせる</b>(<c>align</c>)。
    /// 上のように 2 体入っているファイルは、片方が原点から 0.5m ずれた場所にある。
    /// 残ったパーツの境界箱から
    /// 「水平は中心、垂直は底面」を原点へ持ってくれば、
    /// JSON の <c>position</c> が素直に「床のどこに立たせるか」になる。
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <b>AO を足す</b>のもここ。Poly Haven の glTF は
    /// <c>metallicRoughnessTexture</c> に ARM の 1 枚を指しているが、
    /// <c>occlusionTexture</c> は書いていない。R に AO が入っているので、
    /// <see cref="BuildMaterial"/> と同じ理屈で同じ 1 枚を刺してやる。
    /// これをやらないと、モデルの凹んだところに焼き込まれた暗がりが出ない。
    /// </para>
    /// </summary>
    private void AddProp(
        GL gl,
        RenderResources resources,
        JsonElement entry,
        Handle<Shader> shader,
        Func<string, string> resolveAsset)
    {
        string relativePath = GetString(entry, "model", string.Empty);
        string name = GetString(entry, "name", Path.GetFileNameWithoutExtension(relativePath));

        Model model = GltfLoader.Load(gl, resources, resolveAsset(relativePath), shader);
        _models.Add(model);

        // ARM の 1 枚を AO にも回す。マテリアルは複数のパーツで共有されているので、
        // パーツではなくマテリアルの一覧を回す(同じものを2回触らないため)。
        foreach (Material material in model.Materials)
        {
            if (!material.OcclusionTexture.IsValid && material.MetallicRoughnessTexture.IsValid)
            {
                material.OcclusionTexture = material.MetallicRoughnessTexture;
            }
        }

        string[] skip = ReadStringArray(entry, "skipNodes");

        var parts = model.Parts
            .Where(part => !skip.Any(pattern => part.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (parts.Count == 0)
        {
            throw new InvalidDataException(
                $"{name}: skipNodes で全部のパーツが消えました(ノード名を確認してください)");
        }

        // --- 残ったパーツの境界箱 ---
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (Model.Part part in parts)
        {
            min = Vector3.Min(min, part.BoundsMin);
            max = Vector3.Max(max, part.BoundsMax);
        }

        // --- 世界へ運ぶ行列 ---
        //
        // 順番は **拡大 → 回転 → 足元合わせ → 平行移動**。
        //
        // <b>足元合わせを回転より後に置く</b>のが今日の学び。
        // 先に合わせてから回すと、タイヤを倒した瞬間に半分が床へ沈む——
        // 「合わせた足元」は回転で別の場所へ行ってしまうから。
        // 回してから境界箱を取り直せば、どんな向きでも必ず床に乗る。
        float scale = GetFloat(entry, "scale", 1.0f);
        Vector3 rotation = GetVector3(entry, "rotation", Vector3.Zero) * (MathF.PI / 180.0f);

        Matrix4x4 orientation =
            Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateRotationX(rotation.X)
            * Matrix4x4.CreateRotationY(rotation.Y)
            * Matrix4x4.CreateRotationZ(rotation.Z);

        Vector3 align = GetBool(entry, "align", true)
            ? AlignToGround(min, max, orientation)
            : Vector3.Zero;

        Matrix4x4 placement =
            orientation
            * Matrix4x4.CreateTranslation(align)
            * Matrix4x4.CreateTranslation(GetVector3(entry, "position", Vector3.Zero));

        bool castShadow = GetBool(entry, "castShadow", true);
        SceneCollisionKind collision = ReadCollision(entry);

        foreach (Model.Part part in parts)
        {
            // **パーツ単位の境界箱**(Day 51)。1体が何個かのパーツに割れているので、
            // 当たり判定を作る側が名前で束ね直す(<see cref="SceneCollision"/>)。
            //
            // <b>掛けるのは <c>placement</c> だけ</b>。<c>Part.BoundsMin</c> は
            // <c>Part.Transform</c> を通ったあとのモデル空間の値なので、
            // <c>Transform</c>(= <c>Part.Transform * placement</c>)を掛けると二重になる。
            (Vector3 partMin, Vector3 partMax) =
                TransformBounds(part.BoundsMin, part.BoundsMax, placement);

            _items.Add(new Item(
                Name: $"{name}/{part.Name}",
                Mesh: part.Mesh,
                Material: part.Material,
                Transform: part.Transform * placement,
                CastShadow: castShadow,
                BoundsMin: partMin,
                BoundsMax: partMax,
                Collision: collision));
        }

        TriangleCount += parts.Sum(part => part.Mesh.IndexCount / 3);
    }

    /// <summary>
    /// 回したあとの境界箱から「水平は中心、垂直は底面」を原点へ運ぶ平行移動を作る。
    ///
    /// <para>
    /// <b>境界箱を回すには 8 隅を全部通す</b>。中心と大きさだけを回しても
    /// 正しい箱にならない——斜めに回した箱は、元より必ず大きくなる。
    /// 8 隅を変換してから改めて最小・最大を取るのがいちばん短くて確実。
    /// </para>
    /// </summary>
    private static Vector3 AlignToGround(Vector3 min, Vector3 max, Matrix4x4 orientation)
    {
        (Vector3 rotatedMin, Vector3 rotatedMax) = TransformBounds(min, max, orientation);

        return new Vector3(
            -(rotatedMin.X + rotatedMax.X) * 0.5f,
            -rotatedMin.Y,
            -(rotatedMin.Z + rotatedMax.Z) * 0.5f);
    }

    /// <summary>
    /// 境界箱を行列で運んで、包み直す(Day 51 で <see cref="AlignToGround"/> から切り出した)。
    ///
    /// <para>
    /// <b>8 隅を全部通す</b>。中心と大きさだけを回しても正しい箱にならない——
    /// <b>斜めに回した箱は、元より必ず大きくなる</b>。
    /// この「必ず大きくなる」が、Day 51 で当たり判定が実物より太る理由そのものになる。
    /// </para>
    /// </summary>
    internal static (Vector3 Min, Vector3 Max) TransformBounds(
        Vector3 min, Vector3 max, in Matrix4x4 matrix)
    {
        var resultMin = new Vector3(float.MaxValue);
        var resultMax = new Vector3(float.MinValue);

        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);

            Vector3 transformed = Vector3.Transform(point, matrix);
            resultMin = Vector3.Min(resultMin, transformed);
            resultMax = Vector3.Max(resultMax, transformed);
        }

        return (resultMin, resultMax);
    }

    /// <summary>
    /// <c>collision</c> を読む(Day 51)。**書いていなければ箱**。
    ///
    /// <para>
    /// 既定を <see cref="SceneCollisionKind.Box"/> にしてあるのは、
    /// <b>書き忘れたときに通り抜けるより、書き忘れたときにぶつかるほうが気づける</b>から。
    /// 通り抜けは「そういう演出かもしれない」と思ってしまう。
    /// </para>
    /// </summary>
    private static SceneCollisionKind ReadCollision(JsonElement entry) =>
        GetString(entry, "collision", "box").ToLowerInvariant() switch
        {
            "none" => SceneCollisionKind.None,
            "plane" => SceneCollisionKind.Plane,
            _ => SceneCollisionKind.Box,
        };

    /// <summary>
    /// 板1枚の世界行列。**板は +Z を向いている**(<see cref="Primitives.CreateQuad"/>)ので、
    /// 床にするには X 軸まわりに -90 度回す。
    ///
    /// <para>
    /// 回転は **X → Y → Z** の順で掛ける。この順番は JSON を書く人との約束で、
    /// どれか1つでも変えると同じ数字が別の向きになる。
    /// </para>
    /// </summary>
    private static Matrix4x4 ReadTransform(JsonElement entry)
    {
        Vector2 size = GetVector2(entry, "size", Vector2.One);
        Vector3 rotation = GetVector3(entry, "rotation", Vector3.Zero) * (MathF.PI / 180.0f);
        Vector3 position = GetVector3(entry, "position", Vector3.Zero);

        return Matrix4x4.CreateScale(size.X, size.Y, 1.0f)
            * Matrix4x4.CreateRotationX(rotation.X)
            * Matrix4x4.CreateRotationY(rotation.Y)
            * Matrix4x4.CreateRotationZ(rotation.Z)
            * Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// <c>camera.shots</c> を読んでカメラワークを作る(Day 40)。
    ///
    /// <para>
    /// <b>書かなかった項目は前のショットを引き継ぐ</b>。
    /// 1つ目は決めの構図(<c>camera</c> 直下の値)を土台にする。
    /// カメラワークは「注視点だけずらす」「距離だけ寄る」のように
    /// <b>1つの数字しか変わらないショットが大半</b>なので、
    /// 5 項目を毎回全部書かせると、変えた場所が読み取れないファイルになる。
    /// </para>
    ///
    /// <para>
    /// <b>ショットが1つしか無ければ <c>null</c> を返す</b>。
    /// キーが1つのパスは補間するものが無く、決めの構図と同じものになる。
    /// 「動かないカメラワーク」を持っていると、
    /// HUD にも自己チェックにも意味の無い枝が増える。
    /// </para>
    /// </summary>
    private static CameraPath? ReadCameraPath(JsonElement camera, DemoScene scene)
    {
        if (!camera.TryGetProperty("shots", out JsonElement shots)
            || shots.ValueKind != JsonValueKind.Array
            || shots.GetArrayLength() < 2)
        {
            return null;
        }

        var keys = new List<CameraPath.Key>();

        Vector3 target = scene.CameraTarget;
        float distance = scene.CameraDistance;
        float yaw = scene.CameraYaw;
        float pitch = scene.CameraPitch;
        float fov = scene.CameraFieldOfView;

        foreach (JsonElement shot in shots.EnumerateArray())
        {
            target = GetVector3(shot, "target", target);
            distance = GetFloat(shot, "distance", distance);
            yaw = GetFloat(shot, "yaw", yaw * (180.0f / MathF.PI)) * (MathF.PI / 180.0f);
            pitch = GetFloat(shot, "pitch", pitch * (180.0f / MathF.PI)) * (MathF.PI / 180.0f);
            fov = GetFloat(shot, "fov", fov * (180.0f / MathF.PI)) * (MathF.PI / 180.0f);

            keys.Add(new CameraPath.Key(
                Name: GetString(shot, "name", $"ショット{keys.Count + 1}"),
                Target: target,
                Distance: distance,
                Yaw: yaw,
                Pitch: pitch,
                FieldOfView: fov,
                Hold: MathF.Max(0.0f, GetFloat(shot, "hold", 2.0f)),
                Travel: MathF.Max(0.0f, GetFloat(shot, "travel", 6.0f))));
        }

        return new CameraPath(keys);
    }

    /// <summary>借りたテクスチャを覚えておく。<see cref="Dispose"/> で同じ数だけ返す。</summary>
    private Handle<Texture> Track(Handle<Texture> handle)
    {
        _textures.Add(handle);
        return handle;
    }

    // --- JSON の読み出し(Day 24 の SceneSerializer と同じ書きぶり)---

    private static string GetString(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    /// <summary>
    /// 文字列の項目を読む。**<c>NotNullWhen</c> を付けておく**——
    /// これが無いと、呼び出し側で <c>value</c> が null かもしれない扱いになり、
    /// <c>if (TryGetString(...))</c> の中でも警告が出る。
    /// </summary>
    private static bool TryGetString(
        JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        if (element.TryGetProperty(name, out JsonElement found) && found.ValueKind == JsonValueKind.String)
        {
            value = found.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static float GetFloat(JsonElement element, string name, float fallback) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : fallback;

    private static bool GetBool(JsonElement element, string name, bool fallback) =>
        element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static Vector2 GetVector2(JsonElement element, string name, Vector2 fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        // **1つだけ書いてあったら縦横に同じ値**。uvScale で楽をするための甘やかし。
        if (value.GetArrayLength() == 1)
        {
            float single = value[0].GetSingle();
            return new Vector2(single, single);
        }

        return new Vector2(value[0].GetSingle(), value[1].GetSingle());
    }

    private static Vector3 GetVector3(JsonElement element, string name, Vector3 fallback) =>
        element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() >= 3
            ? new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle())
            : fallback;

    private static string[] ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }
}
