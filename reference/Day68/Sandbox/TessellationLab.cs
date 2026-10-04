using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>今日の題材。</summary>
internal enum TessellationTopic
{
    /// <summary>平らな格子に高さを付ける。**変位(displacement)**のいちばん素直な形。</summary>
    Terrain,

    /// <summary>立方体の6面を膨らませて球にする。**4隅だけで滑らかな曲面**。</summary>
    Sphere,
}

/// <summary>
/// 割り方。**layout 修飾子なのでコンパイル時に決まる**——uniform にできないので、
/// 3通りを見比べるには同じソースを3回コンパイルする(`tess.tese` のコメント)。
/// </summary>
internal enum TessellationSpacing
{
    /// <summary>整数へ切り上げて等分。レベルが変わると**カクッと切り替わる**。</summary>
    Equal,

    /// <summary>奇数へ丸め、端の2本を伸び縮みさせる。**1 から連続に変えられる**。LOD の定石。</summary>
    FractionalOdd,

    /// <summary>偶数へ丸める。**レベル 1 が作れない**(最低 2)。</summary>
    FractionalEven,
}

/// <summary>
/// **テッセレーションの実験台**(Day 63b)。今日の主役の GL 側。
///
/// <para>
/// 頂点シェーダの後ろに段を<b>2つ</b>挟む。間には<b>プログラムできない回路</b>(テッセレータ)があり、
/// パイプラインの形がこうなる。
/// </para>
///
/// <code>
///   頂点   → 制御(tesc) → [テッセレータ] → 評価(tese) → [ジオメトリ] → 画素
///   4回      4回            固定機能           4,225回       (今日は無し)
/// </code>
///
/// <para>
/// <b>Day 63a のジオメトリシェーダとの違い</b>は、増やすのが自分ではないこと。
/// GS は「何個出すか」を自分で決めて自分で出すが、この段は<b>6つの数を渡すだけ</b>で、
/// 割るのは回路がやる。だから GS のような「出力を溜める場所」の制約が無く、
/// <b>レベル 64 なら1パッチから 8,192 枚の三角形</b>が出てくる(GS の上限は 1024 頂点)。
/// </para>
///
/// <para>
/// 置き場所は <c>Sandbox/</c>。<c>Render/</c> は今日も2か所だけ触っている
/// (<see cref="Shader"/> の4段のコンストラクタと <see cref="RenderResources"/> の窓口)。
/// <b>画素シェーダは Day 63a の <c>gs-surface.frag</c> をそのまま使う</b>——
/// <c>Varying</c> ブロックの中身を揃えてあるので、間に何段挟まっていても同じ1本で受けられる。
/// </para>
/// </summary>
internal sealed class TessellationLab : IDisposable
{
    /// <summary>四角パッチなので制御点は4つ。</summary>
    public const int PatchVertices = 4;

    /// <summary>パッチの並び1つぶん。</summary>
    private sealed class PatchMesh : IDisposable
    {
        public required string Name { get; init; }

        public required Vertex[] Vertices { get; init; }

        /// <summary>4つで1パッチ。**順番は (0,0) (1,0) (0,1) (1,1)**。</summary>
        public required uint[] Indices { get; init; }

        public required Mesh<Vertex> Mesh { get; init; }

        public int PatchCount => Indices.Length / PatchVertices;

        public void Dispose() => Mesh.Dispose();
    }

    private readonly GL _gl;
    private readonly RenderResources _resources;

    /// <summary>割り方ごとに1本。**同じ3本のシェーダから、<c>#define</c> だけ変えて焼いたもの**。</summary>
    private readonly Handle<Shader>[] _programs = new Handle<Shader>[3];

    private readonly PatchMesh _grid;
    private readonly PatchMesh _cube;

    private readonly GpuTimer _timer;
    private readonly PrimitiveCounter _counter;

    private bool _disposed;

    public TessellationLab(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        string vertex = Path.Combine(shaderDirectory, "tess.vert");
        string control = Path.Combine(shaderDirectory, "tess.tesc");
        string evaluation = Path.Combine(shaderDirectory, "tess.tese");

        // **Day 63a の画素シェーダを借りる**。Varying ブロックが同じなので、
        // 間にテッセレーションの2段が挟まっても1文字も変えずに繋がる。
        string fragment = Path.Combine(shaderDirectory, "gs-surface.frag");

        string[] spacings = ["equal_spacing", "fractional_odd_spacing", "fractional_even_spacing"];

        for (int i = 0; i < spacings.Length; i++)
        {
            _programs[i] = resources.LoadShader(
                vertex, control, evaluation, fragment, $"SPACING {spacings[i]}");
        }

        _grid = CreateGrid(gl, "平らな格子(8 x 8 パッチ)", 8, 6.0f);
        _cube = CreateCube(gl, "球(6 面 x 2 x 2 パッチ)", 2, 2.0f);

        _timer = new GpuTimer(gl);
        _counter = new PrimitiveCounter(gl);
    }

    public TessellationTopic Topic { get; set; } = TessellationTopic.Terrain;

    public TessellationSpacing Spacing { get; set; } = TessellationSpacing.Equal;

    /// <summary>固定のレベル(距離で決めないとき)。</summary>
    public float Level { get; set; } = 8.0f;

    /// <summary>距離でレベルを決めるか。</summary>
    public bool DistanceLod { get; set; }

    /// <summary>辺のレベルを**中点から**決めるか。切ると中心から決めるので**亀裂が出る**。</summary>
    public bool MatchEdges { get; set; } = true;

    /// <summary>変位の強さ(m)。</summary>
    public float Displacement { get; set; } = 0.6f;

    /// <summary>線で描くか(割った三角形をそのまま見る)。</summary>
    public bool Wireframe { get; set; }

    /// <summary>パッチごとに色を変えるか。<c>gl_PrimitiveID</c> を使う。</summary>
    public bool ColorByPatch { get; set; }

    /// <summary>波の時計(秒)。</summary>
    public float Time { get; set; }

    /// <summary>距離レベルの基準。この距離でちょうど <see cref="Level"/> になる。</summary>
    public float LodDistance { get; set; } = 8.0f;

    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>直前のフレームでテッセレータが作った三角形の数(GL に数えさせたもの)。</summary>
    public ulong GeneratedPrimitives => _counter.Count;

    public string TopicName => Current.Name;

    public int PatchCount => Current.PatchCount;

    public Vertex[] Vertices => Current.Vertices;

    public uint[] Indices => Current.Indices;

    /// <summary>球の半径(m)。</summary>
    public float Radius => 2.4f;

    /// <summary>
    /// **固定のレベルのとき、1パッチから出る三角形の数**。
    /// 四角パッチを縦横 n 等分すると升目が n x n、三角形はその2倍。
    /// 割り方の丸めを通した値で数える。
    /// </summary>
    public int ExpectedPrimitivesPerPatch
    {
        get
        {
            float rounded = TessellationMath.Round(Level, Spacing);
            return (int)(2.0f * rounded * rounded);
        }
    }

    /// <summary>距離で決めているときは答えられない(パッチごとに違う)。</summary>
    public int ExpectedPrimitives => DistanceLod ? -1 : ExpectedPrimitivesPerPatch * PatchCount;

    private PatchMesh Current => Topic == TessellationTopic.Sphere ? _cube : _grid;

    /// <summary>**1フレーム描く**。</summary>
    public void Draw(Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        // **裏面も描く**。変位した地形は下から覗けるし、線で描くときは裏の線も見たい。
        bool culling = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);

        if (Wireframe)
        {
            _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
        }

        _timer.Begin();
        _counter.Begin();

        DrawPatches(camera, lightDirection, lightColor, ambientColor);

        _counter.End();
        _timer.End();

        // **借りた状態は返す**(Day 18 からの作法)。
        if (Wireframe)
        {
            _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        }

        if (culling)
        {
            _gl.Enable(EnableCap.CullFace);
        }
    }

    /// <summary>
    /// **待って測る**(「値段」と自己チェック用)。Day 63a の <c>GeometryLab.MeasurePass</c> と同じ形。
    /// </summary>
    public ulong MeasurePass(
        Camera camera,
        Vector3 lightDirection,
        Vector3 lightColor,
        Vector3 ambientColor,
        int repeats,
        out double milliseconds)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        uint timeQuery = _gl.GenQuery();

        // 時間のクエリを内側にするのは Day 63a と同じ理由
        // (図形の数を待つあいだ GPU が手を空けるので、外側だとその空きまで数えられる)。
        ulong primitives = _counter.Measure(() =>
        {
            _gl.BeginQuery(QueryTarget.TimeElapsed, timeQuery);

            for (int i = 0; i < repeats; i++)
            {
                DrawPatches(camera, lightDirection, lightColor, ambientColor);
            }

            _gl.EndQuery(QueryTarget.TimeElapsed);
        });

        _gl.GetQueryObject(timeQuery, QueryObjectParameterName.Result, out ulong nanoseconds);
        _gl.DeleteQuery(timeQuery);

        int divisor = Math.Max(1, repeats);
        milliseconds = nanoseconds / 1_000_000.0 / divisor;
        return primitives / (ulong)divisor;
    }

    /// <summary>
    /// **C# の鏡: 隣り合うパッチが共有する辺に、同じレベルを出しているか**。
    ///
    /// <para>
    /// <c>tess.tesc</c> とまったく同じ式で、格子の内部の辺を全部調べる。
    /// <b>「亀裂が出ない」は絵では言えない</b>。カメラを動かせばいつか出るかもしれない、を
    /// 否定するには、すべての辺で両側の値が一致することを数で示すしかない。
    /// </para>
    /// </summary>
    /// <param name="internalEdges">内部の辺(2枚のパッチが共有している辺)の数。</param>
    /// <param name="mismatched">両側の値が食い違った辺の数。</param>
    /// <returns>食い違いのうち、いちばん大きかった差(レベルの値そのもの)。</returns>
    public float CheckEdgeAgreement(
        Vector3 eye, bool matchEdges, out int internalEdges, out int mismatched)
    {
        PatchMesh mesh = Current;
        var levels = new Dictionary<(uint Low, uint High), float>();

        internalEdges = 0;
        mismatched = 0;
        float worst = 0.0f;

        for (int patch = 0; patch < mesh.PatchCount; patch++)
        {
            int at = patch * PatchVertices;
            uint i00 = mesh.Indices[at + 0];
            uint i10 = mesh.Indices[at + 1];
            uint i01 = mesh.Indices[at + 2];
            uint i11 = mesh.Indices[at + 3];

            Vector3 p00 = mesh.Vertices[(int)i00].Position;
            Vector3 p10 = mesh.Vertices[(int)i10].Position;
            Vector3 p01 = mesh.Vertices[(int)i01].Position;
            Vector3 p11 = mesh.Vertices[(int)i11].Position;

            Vector3 center = (p00 + p10 + p01 + p11) * 0.25f;

            // tess.tesc の Outer[0..3] と同じ対応(u=0 / v=0 / u=1 / v=1)。
            (uint A, uint B, Vector3 P, Vector3 Q)[] edges =
            [
                (i00, i01, p00, p01),
                (i00, i10, p00, p10),
                (i10, i11, p10, p11),
                (i01, i11, p01, p11),
            ];

            foreach ((uint a, uint b, Vector3 p, Vector3 q) in edges)
            {
                Vector3 from = matchEdges ? (p + q) * 0.5f : center;
                float level = TessellationMath.LevelAt(from, eye, Level, LodDistance);
                float rounded = TessellationMath.Round(level, Spacing);

                (uint Low, uint High) key = a < b ? (a, b) : (b, a);

                if (!levels.TryGetValue(key, out float other))
                {
                    levels[key] = rounded;
                    continue;
                }

                internalEdges++;

                if (rounded != other)
                {
                    mismatched++;
                    worst = MathF.Max(worst, MathF.Abs(rounded - other));
                }
            }
        }

        return worst;
    }

    /// <summary>
    /// **C# の鏡: 距離で決めたとき、いちばん細かいパッチのレベル**。
    ///
    /// <para>
    /// 「距離で決めると三角形が減る」を確かめるのに要る。減るかどうかは<b>何と比べるか</b>で決まり、
    /// 固定レベルのほうが低ければ当然増える(近いパッチはそれより細かくなるので)。
    /// <b>いちばん細かいパッチと同じレベルを全体に配った場合</b>と比べて初めて、
    /// 「同じ細かさを配るより、距離で配るほうが少なくて済む」と言える。
    /// </para>
    /// </summary>
    public float MaxRoundedLevel(Vector3 eye)
    {
        PatchMesh mesh = Current;
        float worst = 1.0f;

        for (int patch = 0; patch < mesh.PatchCount; patch++)
        {
            int at = patch * PatchVertices;

            Vector3 p00 = mesh.Vertices[(int)mesh.Indices[at + 0]].Position;
            Vector3 p10 = mesh.Vertices[(int)mesh.Indices[at + 1]].Position;
            Vector3 p01 = mesh.Vertices[(int)mesh.Indices[at + 2]].Position;
            Vector3 p11 = mesh.Vertices[(int)mesh.Indices[at + 3]].Position;

            Vector3[] midpoints =
            [
                (p00 + p01) * 0.5f,
                (p00 + p10) * 0.5f,
                (p10 + p11) * 0.5f,
                (p01 + p11) * 0.5f,
            ];

            foreach (Vector3 midpoint in midpoints)
            {
                float level = TessellationMath.LevelAt(midpoint, eye, Level, LodDistance);
                worst = MathF.Max(worst, TessellationMath.Round(level, Spacing));
            }
        }

        return worst;
    }

    /// <summary>この GL がテッセレーションに許している上限。</summary>
    public (int GenLevel, int PatchVertices, int ControlComponents, int EvaluationComponents) Limits()
    {
        _gl.GetInteger(GLEnum.MaxTessGenLevel, out int genLevel);
        _gl.GetInteger(GLEnum.MaxPatchVertices, out int patchVertices);
        _gl.GetInteger(GLEnum.MaxTessControlOutputComponents, out int control);
        _gl.GetInteger(GLEnum.MaxTessEvaluationOutputComponents, out int evaluation);
        return (genLevel, patchVertices, control, evaluation);
    }

    private void DrawPatches(
        Camera camera, Vector3 lightDirection, Vector3 lightColor, Vector3 ambientColor)
    {
        Shader shader = _resources.GetShader(_programs[(int)Spacing]);
        shader.Use();

        shader.SetMatrix4("uModel", Matrix4x4.Identity);
        shader.SetMatrix4("uViewProjection", camera.ViewProjection);
        shader.SetVector3("uEyePosition", camera.Position);
        shader.SetVector3("uLightDirection", lightDirection);
        shader.SetVector3("uLightColor", lightColor);
        shader.SetVector3("uAmbientColor", ambientColor);

        shader.SetFloat("uLevel", Level);
        shader.SetInt("uDistanceLod", DistanceLod ? 1 : 0);
        shader.SetInt("uMatchEdges", MatchEdges ? 1 : 0);
        shader.SetFloat("uLodDistance", LodDistance);
        shader.SetFloat("uMaxLevel", TessellationMath.MaxLevel);

        shader.SetInt("uTopic", Topic == TessellationTopic.Sphere ? 1 : 0);
        shader.SetFloat("uDisplacement", Displacement);
        shader.SetFloat("uTime", Time);
        shader.SetFloat("uRadius", Radius);
        shader.SetFloat("uNormalStep", TessellationMath.NormalStep);
        shader.SetInt("uColorByPatch", ColorByPatch ? 1 : 0);

        // **1パッチが何頂点かは GL 側の状態**(シェーダの layout ではない)。
        // tesc の `layout (vertices = 4) out` は<b>出す</b>制御点の数で、こちらは<b>入れる</b>数。
        // 食い違うと GL_INVALID_OPERATION になるが、**絵が出ないだけで理由は言ってくれない**。
        _gl.PatchParameter(GLEnum.PatchVertices, PatchVertices);

        Current.Mesh.Draw(PrimitiveType.Patches);
    }

    /// <summary>
    /// 平らな格子。XZ 平面に <paramref name="cells"/> x <paramref name="cells"/> のパッチを並べる。
    ///
    /// <para>
    /// **頂点は隣のパッチと共有する**((cells+1)^2 個)。共有していないと、
    /// 溶接の話(Day 63a)がここでも要ることになる——
    /// 今日の亀裂は<b>頂点が共有されていても起きる</b>ので、そこと混ぜないために揃えてある。
    /// </para>
    /// </summary>
    private static PatchMesh CreateGrid(GL gl, string name, int cells, float size)
    {
        var vertices = new Vertex[(cells + 1) * (cells + 1)];
        float half = size * 0.5f;

        for (int z = 0; z <= cells; z++)
        {
            for (int x = 0; x <= cells; x++)
            {
                float u = x / (float)cells;
                float v = z / (float)cells;

                var position = new Vector3(-half + (u * size), 0.0f, -half + (v * size));

                // 市松に色を付けておくと、変位したときに「どこが動いたか」が読める。
                float shade = ((x + z) % 2 == 0) ? 0.85f : 0.65f;
                var color = new Vector4(shade * 0.55f, shade * 0.75f, shade, 1.0f);

                vertices[(z * (cells + 1)) + x] = new Vertex(position, new Vector2(u, v), color, Vector3.UnitY);
            }
        }

        var indices = new uint[cells * cells * PatchVertices];
        int write = 0;

        for (int z = 0; z < cells; z++)
        {
            for (int x = 0; x < cells; x++)
            {
                uint at = (uint)((z * (cells + 1)) + x);

                // **(0,0) (1,0) (0,1) (1,1) の順**。tess.tese の双一次補間がこの順を前提にしている。
                indices[write + 0] = at;
                indices[write + 1] = at + 1;
                indices[write + 2] = at + (uint)(cells + 1);
                indices[write + 3] = at + (uint)(cells + 1) + 1;
                write += PatchVertices;
            }
        }

        return new PatchMesh
        {
            Name = name,
            Vertices = vertices,
            Indices = indices,
            Mesh = new Mesh<Vertex>(gl, vertices, indices, Vertex.Attributes),
        };
    }

    /// <summary>
    /// 立方体の6面を <paramref name="cells"/> x <paramref name="cells"/> のパッチに割ったもの。
    /// 評価シェーダが正規化して球にする。
    /// </summary>
    private static PatchMesh CreateCube(GL gl, string name, int cells, float size)
    {
        var vertices = new List<Vertex>(6 * (cells + 1) * (cells + 1));
        var indices = new List<uint>(6 * cells * cells * PatchVertices);

        // 面ごとの (原点, u 方向, v 方向)。原点は面の左下の隅。
        float h = size * 0.5f;
        (Vector3 Origin, Vector3 U, Vector3 V, Vector4 Color)[] faces =
        [
            (new Vector3(-h, -h, h), Vector3.UnitX, Vector3.UnitY, new Vector4(1.00f, 0.55f, 0.55f, 1.0f)),
            (new Vector3(h, -h, -h), -Vector3.UnitX, Vector3.UnitY, new Vector4(0.55f, 1.00f, 0.65f, 1.0f)),
            (new Vector3(h, -h, h), -Vector3.UnitZ, Vector3.UnitY, new Vector4(0.60f, 0.70f, 1.00f, 1.0f)),
            (new Vector3(-h, -h, -h), Vector3.UnitZ, Vector3.UnitY, new Vector4(1.00f, 0.95f, 0.55f, 1.0f)),
            (new Vector3(-h, h, h), Vector3.UnitX, -Vector3.UnitZ, new Vector4(1.00f, 1.00f, 1.00f, 1.0f)),
            (new Vector3(-h, -h, -h), Vector3.UnitX, Vector3.UnitZ, new Vector4(0.70f, 0.70f, 0.75f, 1.0f)),
        ];

        foreach ((Vector3 origin, Vector3 uAxis, Vector3 vAxis, Vector4 color) in faces)
        {
            uint first = (uint)vertices.Count;

            for (int y = 0; y <= cells; y++)
            {
                for (int x = 0; x <= cells; x++)
                {
                    float u = x / (float)cells;
                    float v = y / (float)cells;
                    Vector3 position = origin + (uAxis * (u * size)) + (vAxis * (v * size));

                    vertices.Add(new Vertex(
                        position, new Vector2(u, v), color, Vector3.Normalize(position)));
                }
            }

            for (int y = 0; y < cells; y++)
            {
                for (int x = 0; x < cells; x++)
                {
                    uint at = first + (uint)((y * (cells + 1)) + x);
                    indices.Add(at);
                    indices.Add(at + 1);
                    indices.Add(at + (uint)(cells + 1));
                    indices.Add(at + (uint)(cells + 1) + 1);
                }
            }
        }

        Vertex[] vertexArray = vertices.ToArray();
        uint[] indexArray = indices.ToArray();

        return new PatchMesh
        {
            Name = name,
            Vertices = vertexArray,
            Indices = indexArray,
            Mesh = new Mesh<Vertex>(gl, vertexArray, indexArray, Vertex.Attributes),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _grid.Dispose();
        _cube.Dispose();
        _timer.Dispose();
        _counter.Dispose();

        // シェーダは RenderResources が持っているので、ここでは返さない。
    }
}
