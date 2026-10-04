using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>今日の題材。シェーダの組み合わせと、入れる図形の種類がこれで決まる。</summary>
internal enum GeometryTopic
{
    /// <summary>法線を線にして見せる。三角形 → 線分4本。**この段のこんにちは世界**。</summary>
    Normals,

    /// <summary>面を押し出す。三角形 → 三角形。面法線と面ごとの番号が使える。</summary>
    Explode,

    /// <summary>点を板にする。点 → 四角形。**増やすほうの使い方**。</summary>
    Billboard,

    /// <summary>輪郭を出す。隣接付き三角形 → 線分0〜3本。**隣を見ないと決まらない**。</summary>
    Silhouette,
}

/// <summary>「GS の値段」で比べる3通り。**絵が同じもの2つと、今日の題材**。</summary>
internal enum GeometryPass
{
    /// <summary>ジオメトリシェーダを挟まない。頂点シェーダから画素シェーダへ直行する。</summary>
    None,

    /// <summary>素通しの GS を挟む。**絵は <see cref="None"/> と1成分も違わない**(自己チェックで確かめる)。</summary>
    Passthrough,

    /// <summary>いまの題材の GS。</summary>
    Topic,
}

/// <summary>題材を試す形。</summary>
internal enum GeometryShape
{
    /// <summary>UV 球(16 x 12)。継ぎ目と極に頂点の重複がある。</summary>
    Sphere,

    /// <summary>立方体。**8 隅が 24 頂点**なので、溶接しないと隣が1つも見つからない。</summary>
    Cube,

    /// <summary>細かい UV 球(128 x 96)。三角形 24,576 枚。**「GS の値段」はこれで測る**。</summary>
    DenseSphere,
}

/// <summary>
/// **ジオメトリシェーダの実験台**(Day 63a)。今日の主役の GL 側。
///
/// <para>
/// 頂点シェーダと画素シェーダの間に、もう1つ段を挟む。この段は
/// <b>「図形を丸ごと受け取って、別の図形を何個でも出す」</b>——
/// 三角形を受けて線を出す、点を受けて板を出す、隣を見て辺を選ぶ、が全部できる。
/// 昨日まで(Day 14〜58)のエンジンは、この段を1度も使っていない。
/// </para>
///
/// <para>
/// <b>置き場所は <c>Sandbox/</c></b>(Day 57 の <see cref="GpuParticles"/>、
/// Day 58 の <see cref="Raymarcher"/> と同じ)。ただし今日は
/// <b><c>Render/</c> を3か所だけ触っている</b>——段が増えるのはエンジンの形そのものの話で、
/// <see cref="Shader"/> に3段のコンストラクタ、<see cref="RenderResources"/> に窓口、
/// <see cref="Mesh{TVertex}"/> に図形の種類を渡す <c>Draw</c> が要る。
/// Day 57・58 が <c>Render/</c> を1行も触らずに済んだのは、
/// <b>どちらもパイプラインの外(コンピュート)か、既にある段の中(画素)</b>だったため。
/// </para>
/// </summary>
internal sealed class GeometryLab : IDisposable
{
    /// <summary>
    /// 形1つぶんの持ち物。**同じ頂点バッファを3通りのインデックスで読む**……
    /// と言いたいところだが、<see cref="Mesh{TVertex}"/> は VBO と EBO を組で持つ形なので、
    /// 実際には頂点が3重に置かれる(球でも 221 頂点 x 96 バイト = 21KB なので今日は目をつぶる)。
    /// 本来は VAO だけ差し替えて VBO を共有したいところ(改造課題3)。
    /// </summary>
    private sealed class Shape : IDisposable
    {
        public required string Name { get; init; }

        /// <summary>元の頂点。自己チェックと C# の鏡が読む。</summary>
        public required Vertex[] Vertices { get; init; }

        /// <summary>元のインデックス(3つで1枚)。</summary>
        public required uint[] Indices { get; init; }

        /// <summary>ふつうの三角形として描くもの。素通し・法線・押し出し・板が使う。</summary>
        public required Mesh<Vertex> Triangles { get; init; }

        /// <summary>位置で溶接してから組んだ隣接付きインデックス。</summary>
        public required uint[] WeldedIndices { get; init; }

        public required MeshAdjacency.Report WeldedReport { get; init; }

        public required Mesh<Vertex> WeldedAdjacency { get; init; }

        /// <summary>**溶接せずに**組んだ隣接付きインデックス。比べるために持っている。</summary>
        public required uint[] RawIndices { get; init; }

        public required MeshAdjacency.Report RawReport { get; init; }

        public required Mesh<Vertex> RawAdjacency { get; init; }

        public void Dispose()
        {
            Triangles.Dispose();
            WeldedAdjacency.Dispose();
            RawAdjacency.Dispose();
        }
    }

    private readonly GL _gl;
    private readonly RenderResources _resources;

    private readonly Handle<Shader> _passthrough;
    private readonly Handle<Shader> _normals;
    private readonly Handle<Shader> _explode;
    private readonly Handle<Shader> _billboard;
    private readonly Handle<Shader> _silhouette;

    /// <summary>ジオメトリシェーダを1本も挟まない、比べるためのプログラム。</summary>
    private readonly Handle<Shader> _noGeometry;

    private readonly Shape _sphere;
    private readonly Shape _cube;
    private readonly Shape _denseSphere;

    private readonly GpuTimer _timer;
    private readonly PrimitiveCounter _counter;

    /// <summary>線を描くときの太さ(この GL が許す範囲に収めたもの)。</summary>
    private readonly float _lineWidth;

    private bool _disposed;

    public GeometryLab(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        string vertex = Path.Combine(shaderDirectory, "gs.vert");
        string surface = Path.Combine(shaderDirectory, "gs-surface.frag");
        string line = Path.Combine(shaderDirectory, "gs-line.frag");
        string sprite = Path.Combine(shaderDirectory, "gs-sprite.frag");

        _passthrough = resources.LoadShader(vertex, Path.Combine(shaderDirectory, "gs-passthrough.geom"), surface);
        _normals = resources.LoadShader(vertex, Path.Combine(shaderDirectory, "gs-normals.geom"), line);
        _explode = resources.LoadShader(vertex, Path.Combine(shaderDirectory, "gs-explode.geom"), surface);
        _billboard = resources.LoadShader(vertex, Path.Combine(shaderDirectory, "gs-billboard.geom"), sprite);
        _silhouette = resources.LoadShader(vertex, Path.Combine(shaderDirectory, "gs-silhouette.geom"), line);

        // **同じ .vert と同じ .frag を、GS 抜きでリンクする**。
        // インターフェースブロックのおかげでこれが通る(gs.vert のコメント)。
        // 絵は素通しの GS とまったく同じになるので、差は段のぶんだけになる。
        _noGeometry = resources.LoadShader(vertex, surface);

        _sphere = CreateShape(gl, "球(16 x 12)", Primitives.CreateSphere(gl, 16, 12));
        _cube = CreateShape(gl, "立方体", Primitives.CreateCube(gl));

        // **値段を測るための細かい球**。三角形が 12 枚や 384 枚では、
        // 描画1回ぶんの固定費に埋もれて段の値段が読めない(MeasureGeometryCost のコメント)。
        _denseSphere = CreateShape(gl, "細かい球(128 x 96)", Primitives.CreateSphere(gl, 128, 96));

        // **線の太さの上限を聞く**。コアプロファイルでは 1.0 しか保証されておらず、
        // 上限を超えた値を渡すと GL_INVALID_VALUE になる(絵は出るので気付きにくい)。
        Span<float> lineWidthRange = stackalloc float[2];
        gl.GetFloat(GLEnum.AliasedLineWidthRange, lineWidthRange);
        _lineWidth = Math.Clamp(2.5f, 1.0f, MathF.Max(1.0f, lineWidthRange[1]));

        _timer = new GpuTimer(gl);
        _counter = new PrimitiveCounter(gl);
    }

    public GeometryTopic Topic { get; set; } = GeometryTopic.Normals;

    public GeometryShape ShapeKind { get; set; } = GeometryShape.Sphere;

    /// <summary>隣接を組む前に位置で頂点をまとめるか。**輪郭の題材だけに効く**。</summary>
    public bool Weld { get; set; } = true;

    /// <summary>元の形を素通しの GS で薄く描くか。線だけだと形が分からないので既定は ON。</summary>
    public bool ShowBase { get; set; } = true;

    /// <summary>押し出しで三角形ごとに色を変えるか。<c>gl_PrimitiveIDIn</c> を使う。</summary>
    public bool ColorByPrimitive { get; set; } = true;

    /// <summary>場面の時計(秒)。形の回転と押し出しの息づかいがこれで決まる。</summary>
    public float Time { get; set; }

    /// <summary>題材ごとのツマミ(0〜1)。法線の長さ・押し出しの量・板の大きさを兼ねる。</summary>
    public float Amount { get; set; } = 0.5f;

    /// <summary>直前に測れた GPU の時間(ms)。</summary>
    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>直前のフレームでジオメトリシェーダが出した図形の数(GL に数えさせたもの)。</summary>
    public ulong EmittedPrimitives => _counter.Count;

    public string ShapeName => Current.Name;

    public Vertex[] Vertices => Current.Vertices;

    public uint[] Indices => Current.Indices;

    /// <summary>いまの溶接の設定で使っている隣接付きインデックス。</summary>
    public uint[] AdjacencyIndices => Weld ? Current.WeldedIndices : Current.RawIndices;

    public MeshAdjacency.Report Report => Weld ? Current.WeldedReport : Current.RawReport;

    public MeshAdjacency.Report WeldedReport => Current.WeldedReport;

    public MeshAdjacency.Report RawReport => Current.RawReport;

    public int TriangleCount => Current.Indices.Length / 3;

    public int VertexCount => Current.Vertices.Length;

    /// <summary>
    /// **1枚の描画で GS が出す図形の数**(C# 側の見積もり)。
    /// 自己チェックで <see cref="PrimitiveCounter"/> の実測と突き合わせる。
    /// 輪郭だけはカメラの位置で変わるので、ここでは答えられない(-1 を返す)。
    /// </summary>
    public int ExpectedPrimitives => Topic switch
    {
        // 頂点法線3本 + 面法線1本。潰れた三角形は面法線を出さないが、
        // 球も立方体も元のインデックスには潰れた三角形が無いので、ちょうど4倍になる。
        GeometryTopic.Normals => TriangleCount * 4,

        // 三角形1枚につき三角形1枚。
        GeometryTopic.Explode => TriangleCount,

        // 点1つにつき四角形1枚 = 三角形2枚(triangle_strip で4頂点 → 2枚)。
        // 点の数は**頂点の数**(索引を使わずに描くので。Mesh.DrawVertices)。
        GeometryTopic.Billboard => VertexCount * 2,

        _ => -1,
    };

    private Shape Current => ShapeKind switch
    {
        GeometryShape.Cube => _cube,
        GeometryShape.DenseSphere => _denseSphere,
        _ => _sphere,
    };

    /// <summary>
    /// いまの形の姿勢。**ゆっくり回す**——輪郭はカメラとの関係で決まるので、
    /// 止めていると「見る側で決まる」ことが絵から読めない。
    /// </summary>
    public Matrix4x4 ModelMatrix =>
        Matrix4x4.CreateScale(ShapeKind == GeometryShape.Cube ? 2.2f : 3.0f)
        * Matrix4x4.CreateRotationX(0.22f)
        * Matrix4x4.CreateRotationY(Time * 0.35f);

    /// <summary>法線の線の長さ(m)。</summary>
    public float NormalLength => 0.15f + (Amount * 0.85f);

    /// <summary>押し出す距離(m)。**息をするように出入りさせる**。</summary>
    public float ExplodeDistance => Amount * 1.6f * ((0.5f * MathF.Sin(Time * 0.7f)) + 0.5f);

    /// <summary>板の1辺(m)。</summary>
    public float BillboardSize => 0.08f + (Amount * 0.42f);

    /// <summary>
    /// **1フレーム描く**。いま挿さっているフレームバッファ(ふつうは <c>_post.Scene</c>)へ。
    /// </summary>
    public void Draw(Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        Matrix4x4 model = ModelMatrix;

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        // **裏面も描く**。押し出すと三角形の裏が見えるし、
        // 輪郭の題材では「裏の面がどこにあるか」がそのまま答えなので、
        // カリングで消してしまうと確かめようがない。
        bool culling = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);

        _timer.Begin();

        // --- 下地(素通しの GS。薄く塗る)---
        //
        // **図形の数のクエリはここを囲まない**。素通しの GS が三角形の数をそのまま出すのは自明で、
        // 混ぜると「今日の題材が何個出したか」が読めなくなる(下地を切るたびに数が変わる)。
        // 時間のほうは囲む——こちらは「この実験台がフレームに乗せている重さ」なので、下地も込み。
        if (ShowBase)
        {
            Shader baseShader = _resources.GetShader(_passthrough);
            baseShader.Use();
            PrepareCommon(baseShader, model, camera);
            PrepareLighting(baseShader, camera, lightDirection, lightColor, ambientColor);
            baseShader.SetVector4("uColorScale", new Vector4(0.28f, 0.30f, 0.36f, 1.0f));
            Current.Triangles.Draw();
        }

        // --- 今日の題材 ---
        _counter.Begin();
        DrawTopicOnly(camera, lightDirection, lightColor, ambientColor);
        _counter.End();

        _timer.End();

        // **借りた状態は返す**(Day 18 からの作法)。
        if (culling)
        {
            _gl.Enable(EnableCap.CullFace);
        }
    }

    /// <summary>
    /// **同じ絵を、段の組み合わせを変えて測る**(「ジオメトリシェーダ」の F10)。
    ///
    /// <para>
    /// 返すのは<b>出した図形の数</b>で、時間のほうは <paramref name="milliseconds"/> に入れる。
    /// どちらも<b>待って受け取る</b>ので、毎フレーム呼ぶものではない
    /// (<see cref="PrimitiveCounter.Measure"/> のコメント)。
    /// </para>
    ///
    /// <para>
    /// <b>1回では測れない</b>。GPU はクロックを上下させるので、
    /// 1回きりの値はばらつく。ここでは <paramref name="repeats"/> 回まとめて描いて
    /// 全体を1つのクエリで挟み、1回ぶんに割る。
    /// </para>
    /// </summary>
    public ulong MeasurePass(
        Camera camera,
        Vector3 lightDirection,
        Vector3 lightColor,
        Vector3 ambientColor,
        GeometryPass pass,
        int repeats,
        out double milliseconds)
    {
        Matrix4x4 model = ModelMatrix;

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        void DrawOnce()
        {
            switch (pass)
            {
                case GeometryPass.None:
                    // **GS 無し**。同じ .vert と同じ .frag を、段を挟まずにリンクしたもの。
                    Shader plain = _resources.GetShader(_noGeometry);
                    plain.Use();
                    PrepareCommon(plain, model, camera);
                    PrepareLighting(plain, camera, lightDirection, lightColor, ambientColor);
                    Current.Triangles.Draw();
                    break;

                case GeometryPass.Passthrough:
                    Shader passthrough = _resources.GetShader(_passthrough);
                    passthrough.Use();
                    PrepareCommon(passthrough, model, camera);
                    PrepareLighting(passthrough, camera, lightDirection, lightColor, ambientColor);
                    passthrough.SetVector4("uColorScale", Vector4.One);
                    Current.Triangles.Draw();
                    break;

                default:
                    DrawTopicOnly(camera, lightDirection, lightColor, ambientColor);
                    break;
            }
        }

        uint timeQuery = _gl.GenQuery();

        // **2種類のクエリを重ねる**。入れ子にできないのは「同じ種類どうし」だけなので、
        // 時間(TIME_ELAPSED)と図形の数(PRIMITIVES_GENERATED)は同時に走らせられる。
        //
        // 時間のクエリを内側にしてあるのは、<see cref="PrimitiveCounter.Measure"/> が
        // 結果を待つ(CPU が止まる)から。待っている間 GPU は手が空くので、
        // 外側に置くとその空き時間まで GPU の時間として数えられてしまう。
        ulong primitives = _counter.Measure(() =>
        {
            _gl.BeginQuery(QueryTarget.TimeElapsed, timeQuery);

            for (int i = 0; i < repeats; i++)
            {
                DrawOnce();
            }

            _gl.EndQuery(QueryTarget.TimeElapsed);
        });

        _gl.GetQueryObject(timeQuery, QueryObjectParameterName.Result, out ulong nanoseconds);
        _gl.DeleteQuery(timeQuery);

        int divisor = Math.Max(1, repeats);
        milliseconds = nanoseconds / 1_000_000.0 / divisor;
        return primitives / (ulong)divisor;
    }

    /// <summary>いまの題材を1枚だけ描く(自己チェックで図形の数を数えるときに使う)。</summary>
    public void DrawTopicOnly(
        Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        Matrix4x4 model = ModelMatrix;

        switch (Topic)
        {
            case GeometryTopic.Normals:
                DrawNormals(model, camera, lightDirection, lightColor, ambientColor);
                break;

            case GeometryTopic.Explode:
                DrawExplode(model, camera, lightDirection, lightColor, ambientColor);
                break;

            case GeometryTopic.Billboard:
                DrawBillboard(model, camera, lightDirection, lightColor, ambientColor);
                break;

            default:
                DrawSilhouette(model, camera, lightDirection, lightColor, ambientColor);
                break;
        }
    }

    /// <summary>この GL 実装が許す、ジオメトリシェーダ1回ぶんの出力の上限を聞く。</summary>
    public (int Vertices, int Components, int TotalComponents, int Invocations) Limits()
    {
        _gl.GetInteger(GLEnum.MaxGeometryOutputVertices, out int vertices);
        _gl.GetInteger(GLEnum.MaxGeometryOutputComponents, out int components);
        _gl.GetInteger(GLEnum.MaxGeometryTotalOutputComponents, out int total);
        _gl.GetInteger(GLEnum.MaxGeometryShaderInvocations, out int invocations);
        return (vertices, components, total, invocations);
    }

    private static Shape CreateShape(GL gl, string name, Mesh<Vertex> triangles)
    {
        // **GPU から読み返して組む**(Day 34 で足した窓口)。
        // 起動時に1回だけなので、同期の代償は払ってよい。
        // Primitives が配列を返さない形なので、ここが素直な取り出し口になっている。
        Vertex[] vertices = triangles.ReadVertices();
        uint[] indices = triangles.ReadIndices();

        uint[] welded = MeshAdjacency.Build(vertices, indices, weld: true, out MeshAdjacency.Report weldedReport);
        uint[] raw = MeshAdjacency.Build(vertices, indices, weld: false, out MeshAdjacency.Report rawReport);

        return new Shape
        {
            Name = name,
            Vertices = vertices,
            Indices = indices,
            Triangles = triangles,
            WeldedIndices = welded,
            WeldedReport = weldedReport,
            WeldedAdjacency = new Mesh<Vertex>(gl, vertices, welded, Vertex.Attributes),
            RawIndices = raw,
            RawReport = rawReport,
            RawAdjacency = new Mesh<Vertex>(gl, vertices, raw, Vertex.Attributes),
        };
    }

    private void DrawNormals(
        in Matrix4x4 model, Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        Shader shader = _resources.GetShader(_normals);
        shader.Use();

        // 光は使わない(線に陰影は付けない)。法線行列は gs.vert が線の向きに使うので要る。
        PrepareCommon(shader, model, camera);
        shader.SetFloat("uLength", NormalLength);
        shader.SetFloat("uDepthBias", DepthBias);
        shader.SetInt("uShowFaceNormal", 1);

        _gl.LineWidth(_lineWidth);
        Current.Triangles.Draw();
        _gl.LineWidth(1.0f);
    }

    private void DrawExplode(
        in Matrix4x4 model, Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        Shader shader = _resources.GetShader(_explode);
        shader.Use();

        // **法線行列を送らない**。この題材は頂点法線を1度も使わず、
        // 陰影も押し出す向きも gs-explode.geom が作る面法線で決まるので、
        // gs.vert の法線の計算はまるごと消される(uNormalMatrix も一緒に消える)。
        // 「頂点ごとの法線ではなく、面ごとの法線で塗る」がそのまま uniform の数に出ている。
        PrepareCommon(shader, model, camera, normalMatrix: false);
        PrepareLighting(shader, camera, lightDirection, lightColor, ambientColor);
        shader.SetFloat("uExplode", ExplodeDistance);
        shader.SetFloat("uShrink", 0.12f);
        shader.SetInt("uColorByPrimitive", ColorByPrimitive ? 1 : 0);
        shader.SetVector4("uColorScale", Vector4.One);
        Current.Triangles.Draw();
    }

    private void DrawBillboard(
        in Matrix4x4 model, Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        Shader shader = _resources.GetShader(_billboard);
        shader.Use();

        // 板の色は法線から作る(gs-billboard.geom)ので法線行列は要るが、光は使わない。
        PrepareCommon(shader, model, camera);
        shader.SetMatrix4("uView", camera.ViewMatrix);
        shader.SetFloat("uSize", BillboardSize);

        // **同じメッシュを点として描く**。索引は使わず、頂点バッファを頭から順に。
        // 三角形の索引のまま点を描くと、何枚もの三角形に使われている頂点が
        // その回数だけ点になり(球なら 221 頂点に対して点が 1152 個)、
        // 「板1枚 = 頂点1つ」の対応が崩れて数えられなくなる。
        Current.Triangles.DrawVertices(PrimitiveType.Points);
    }

    private void DrawSilhouette(
        in Matrix4x4 model, Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        Shader shader = _resources.GetShader(_silhouette);
        shader.Use();

        // **法線をどこでも使わない**プログラム。法線行列は送らない(送っても消されている)。
        PrepareCommon(shader, model, camera, normalMatrix: false);
        shader.SetVector3("uEyePosition", camera.Position);
        shader.SetFloat("uDepthBias", DepthBias);
        shader.SetVector4("uLineColor", new Vector4(1.8f, 1.1f, 0.25f, 1.0f));

        // **ここだけ隣接付きのインデックスで描く**。頂点バッファは同じ。
        Mesh<Vertex> mesh = Weld ? Current.WeldedAdjacency : Current.RawAdjacency;

        _gl.LineWidth(_lineWidth);
        mesh.Draw(PrimitiveType.TrianglesAdjacency);
        _gl.LineWidth(1.0f);
    }

    /// <summary>線を面から少しだけ手前へ引く量(クリップ空間の z を w の割合で)。</summary>
    private const float DepthBias = 0.0012f;

    /// <summary>
    /// どのプログラムにも要る3つ。
    ///
    /// <para>
    /// <b>プログラムごとに送るものを分けてある</b>のは、<see cref="Shader"/> が
    /// 「その名前の uniform が見つからない」と1回だけ知らせるため(Day 14)。
    /// 使われていない uniform は<b>リンクのときに消される</b>ので、
    /// 全部まとめて送ると起動のたびに嘘の警告が並ぶ。
    /// たとえば輪郭のプログラムは <c>gs-line.frag</c> で色をそのまま出すだけなので、
    /// 法線を使う経路が丸ごと消え、<c>uNormalMatrix</c> も一緒に消える。
    /// </para>
    /// </summary>
    /// <param name="normalMatrix">法線行列を送るか。輪郭のプログラムは法線を使わない。</param>
    private void PrepareCommon(Shader shader, in Matrix4x4 model, Camera camera, bool normalMatrix = true)
    {
        shader.SetMatrix4("uModel", model);
        shader.SetMatrix4("uViewProjection", camera.ViewProjection);

        if (normalMatrix)
        {
            shader.SetMatrix3("uNormalMatrix", NormalMatrix(model));
        }
    }

    /// <summary>面を塗るプログラム(<c>gs-surface.frag</c>)だけに要るもの。</summary>
    private void PrepareLighting(
        Shader shader, Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        shader.SetVector3("uEyePosition", camera.Position);
        shader.SetVector3("uLightDirection", lightDirection);
        shader.SetVector3("uLightColor", lightColor);
        shader.SetVector3("uAmbientColor", ambientColor);
    }

    /// <summary>法線行列(Program.NormalMatrix と同じもの。Sandbox から呼べないので写してある)。</summary>
    private static Matrix4x4 NormalMatrix(Matrix4x4 model)
    {
        model.M41 = 0.0f;
        model.M42 = 0.0f;
        model.M43 = 0.0f;

        return Matrix4x4.Invert(model, out Matrix4x4 inverse) ? Matrix4x4.Transpose(inverse) : model;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _sphere.Dispose();
        _cube.Dispose();
        _denseSphere.Dispose();
        _timer.Dispose();
        _counter.Dispose();

        // シェーダは RenderResources が持っているので、ここでは返さない
        // (Day 31 の「窓口が寿命を持つ」に合わせる)。
    }
}
