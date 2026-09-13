using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **ライティングパス**(Day 52)。G-Buffer を読んで光を当てる。主役のもう半分。
///
/// <para>
/// <b>光の形 = 描く形</b>というのが、このクラスのいちばん大事な考え方になる。
/// </para>
///
/// <list type="table">
/// <item><term>太陽(平行光源)</term><description>
/// どこにでも当たる → <b>全画面の三角形1枚</b>。環境光と発光もここで足す
/// </description></item>
/// <item><term>点光源</term><description>
/// 半径の中にしか当たらない → <b>その半径の球</b>。球が覆った画素だけが塗られる
/// </description></item>
/// </list>
///
/// <para>
/// 点光源を球で描くので、<b>光1つの代償は「その光が画面をどれだけ覆うか」で決まる</b>。
/// 遠くの小さな光は数十画素、画面の外の光は 0 画素。
/// フォワードは「描いた画素 × 全部の光」を払うので、ここがディファードの儲けどころになる。
/// </para>
///
/// <para>
/// <b>シーンの光の設定は知らない</b>。太陽の向きも IBL も SSAO も、
/// <see cref="Render"/> に渡された関数(<c>applyLighting</c>)が送る。
/// Day 51 の <see cref="FollowCamera"/> が物理を <c>Func</c> 1本で受け取ったのと同じ形で、
/// こうしておくとフォワードの本描画と<b>同じ関数で同じ uniform を配れる</b>——
/// 2か所で組み立てると、片方だけ直したときに2つの絵が食い違う。
/// </para>
/// </summary>
internal sealed class DeferredLighting : IDisposable
{
    /// <summary>
    /// 球の分割数。**粗くてよい**——光の球は絵に出ず、どの画素を塗るかを決めるだけ。
    /// 16x12 で三角形 384 枚(うち極の 32 枚は潰れている)。
    /// 1000 個描いても 38 万枚で、いまの GPU には何でもない。
    /// </summary>
    private const int VolumeSlices = 16;

    private const int VolumeStacks = 12;

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _sunShader;
    private readonly Handle<Shader> _pointShader;

    /// <summary>
    /// 光の球。**1本だけ持ち**、頂点シェーダで位置と半径へ運ぶ。
    /// <see cref="Primitives.CreateSphere"/> は直径 1(半径 0.5)なので、
    /// <c>light-volume.vert</c> が向きだけを取り出して(正規化して)半径を掛け直す。
    /// </summary>
    private readonly Mesh<Vertex> _volume;

    /// <summary>全画面の三角形用の空 VAO(<see cref="PostProcess"/> と同じ手口)。</summary>
    private uint _emptyVao;

    private bool _disposed;

    public DeferredLighting(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        // **同じ画素シェーダを2本のプログラムで使う**。頂点シェーダだけが違う——
        // 全画面の三角形(太陽)と、球(点光源)。どちらも「G-Buffer を読んで光を当てる」
        // という中身は同じなので、画素シェーダを分けると BRDF がもう1か所に増える。
        string fragment = Path.Combine(shaderDirectory, "deferred.frag");

        _sunShader = resources.LoadShader(Path.Combine(shaderDirectory, "fullscreen.vert"), fragment);
        _pointShader = resources.LoadShader(Path.Combine(shaderDirectory, "light-volume.vert"), fragment);

        _volume = Primitives.CreateSphere(gl, VolumeSlices, VolumeStacks);
        _emptyVao = gl.GenVertexArray();

        // **球を外接させる倍率**。分割した球は頂点こそ半径の上に乗るが、
        // 面は内側へ入り込む(弦が弧より内側を通る)。そのままだと半径ぎりぎりの画素が
        // 球の外に落ち、光の縁が多角形に欠ける。いちばん深く入り込むのは
        // 面の中心で、そこまでの距離は cos(π/分割) の積になる。その逆数だけ膨らませる。
        VolumeScale = 1.0f / (MathF.Cos(MathF.PI / VolumeSlices) * MathF.Cos(MathF.PI / (2 * VolumeStacks)));
    }

    /// <summary>球を膨らませる倍率(1.03 前後)。自己チェックが見るために開けてある。</summary>
    public float VolumeScale { get; }

    /// <summary>
    /// **光の重なりを見る**(「ディファード」の F6)。球が塗った画素に一定の色を足していく。
    ///
    /// <para>
    /// 明るいところほど<b>何個の光がその画素を塗ったか</b>が多い。
    /// これがディファードの代償そのもので、半径の大きい光を重ねるほど赤く焼ける。
    /// 光が当たっていない(半径の外の)画素でも球が覆えば塗られる——
    /// 壁の裏に隠れた光の球も、壁の手前の画素を塗る。<b>払っている量をそのまま見せる</b>。
    /// </para>
    /// </summary>
    public bool OverdrawView { get; set; }

    /// <summary>
    /// 画面の外の光を CPU で落とすか(「ディファード」の F7)。**絵は変わらない**。
    ///
    /// <para>
    /// 落とさなくても GPU が球をクリップして 0 画素になるので、絵は1画素も変わらない。
    /// 変わるのは<b>ドローコールの数</b>だけで、1000 個の光のうち画面に入るのは数百個。
    /// 光を1個ずつ判定するのは粗い粒度のカリングで、
    /// これを画面のタイルごとにやるのが Day 53(Forward+ / クラスタード)になる。
    /// </para>
    /// </summary>
    public bool CullLights { get; set; } = true;

    /// <summary>このフレームに描いた光の数(= 点光源のドローコール)。</summary>
    public int LightsDrawn { get; private set; }

    /// <summary>このフレームに画面の外として落とした光の数。</summary>
    public int LightsCulled { get; private set; }

    /// <summary>光の球1個の三角形の数。</summary>
    public int VolumeTriangles => _volume.IndexCount / 3;

    /// <summary>
    /// **光を当てる**。<see cref="PostProcess.Begin"/> のあと、シーンのバッファに描く。
    ///
    /// <para>
    /// 呼ぶ前に<b>G-Buffer の深度をシーンのバッファへ写しておく</b>こと
    /// (<see cref="Framebuffer.BlitDepthTo"/>)。点光源の球はその深度と比べて塗る画素を選ぶ。
    /// 写さないとシーンのバッファの深度はクリアしたまま(いちばん奥)で、球が1つも通らない。
    /// </para>
    /// </summary>
    /// <param name="applyLighting">
    /// 太陽・IBL・影・SSAO の uniform を送る関数。**フォワードの本描画と同じものを渡す**。
    /// </param>
    public void Render(
        Camera camera, GBuffer gbuffer, ReadOnlySpan<PointLight> lights, Action<Shader> applyLighting)
    {
        LightsDrawn = 0;
        LightsCulled = 0;

        // シーンが残していった GL の状態を覚えておく(Ssao.Compute と同じ作法)。
        bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        bool cull = _gl.IsEnabled(EnableCap.CullFace);

        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        // --- 1. 太陽・環境光・発光(全画面1枚)---
        //
        // **ブレンドしない**。空の画素はシェーダが捨てる(discard)ので、
        // 先に描いてある空(Day 36 の DrawSkybox)はそのまま残る。
        Shader sun = _resources.GetShader(_sunShader);
        sun.Use();
        applyLighting(sun);
        gbuffer.Apply(sun, camera);
        sun.SetInt("uPointLightPass", 0);
        sun.SetInt("uOverdrawView", OverdrawView ? 1 : 0);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        // --- 2. 点光源(1個ずつ球を描いて足す)---
        if (!lights.IsEmpty)
        {
            DrawPointLights(camera, gbuffer, lights, applyLighting);
        }

        // **借りた状態は返す**。深度の比べ方と書き込みは、このエンジンの既定(Less / 書く)へ。
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.CullFace(TriangleFace.Back);
        SetCap(EnableCap.DepthTest, depthTest);
        SetCap(EnableCap.Blend, blend);
        SetCap(EnableCap.CullFace, cull);
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);
    }

    /// <summary>
    /// **球が視錐台にかかっているか**。かかっていなければ描かなくてよい。
    ///
    /// <para>
    /// ビュー射影行列の列を足し引きすると、視錐台の6枚の面がそのまま出てくる
    /// (Gribb と Hartmann の方法)。クリップ座標で「-w ≤ x ≤ w」が画面の内側なので、
    /// <c>x + w ≥ 0</c> が左の面、<c>w - x ≥ 0</c> が右の面——
    /// 行列の1列目と4列目を足せば左の面の式になる、という仕掛け。
    /// System.Numerics は行ベクトル(<c>v * M</c>)なので、式に使うのは<b>列</b>になる。
    /// </para>
    ///
    /// <para>
    /// どれか1枚の面より、半径ぶん以上外側にあれば見えない。
    /// 角の近くでは「見えないのに見える」と答えることがあるが、<b>逆は無い</b>——
    /// 描かなくてよいものを描くのは無駄なだけ、描くべきものを落とすと光が消える。
    /// </para>
    /// </summary>
    public static bool IsVisible(in Matrix4x4 viewProjection, Vector3 center, float radius)
    {
        var column1 = new Vector4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
        var column2 = new Vector4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
        var column3 = new Vector4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
        var column4 = new Vector4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);

        return Inside(column4 + column1, center, radius)   // 左
            && Inside(column4 - column1, center, radius)   // 右
            && Inside(column4 + column2, center, radius)   // 下
            && Inside(column4 - column2, center, radius)   // 上
            && Inside(column4 + column3, center, radius)   // 近(OpenGL の z は -1〜1)
            && Inside(column4 - column3, center, radius);  // 遠
    }

    /// <summary>面の式 (a, b, c, d) の表側から半径ぶん以上はみ出していなければ true。</summary>
    private static bool Inside(Vector4 plane, Vector3 center, float radius)
    {
        var normal = new Vector3(plane.X, plane.Y, plane.Z);
        float length = normal.Length();

        // 式は正規化されていないので、長さで割ってからメートルとして比べる。
        return ((Vector3.Dot(normal, center) + plane.W) / length) >= -radius;
    }

    /// <summary>
    /// **点光源の球を並べる**。今日いちばん GL の状態が込み入るところ。
    ///
    /// <para>
    /// <b>1. 加算で重ねる</b>(<c>One, One</c>)。太陽が描いた色の上に、光の数だけ足していく。
    /// 光の寄与は足し算で重なる(Day 35 の要点「光源が増えれば L ごとに足す」)ので、
    /// 描く順番を気にしなくてよい。
    /// </para>
    ///
    /// <para>
    /// <b>2. 球の裏面を描く</b>(表面をカリング)。表面を描くと、
    /// <b>カメラが球の中に入った瞬間に光が消える</b>——表面がすべてカメラの後ろへ回るため。
    /// 裏面なら、中に居ても外に居ても必ず画面を覆う。
    /// 街灯の真下を歩くと必ず踏む落とし穴で、自己チェックに1項目入れてある。
    /// </para>
    ///
    /// <para>
    /// <b>3. 深度は「奥にあれば通す」(<c>GEQUAL</c>)で、書かない</b>。
    /// 裏面が G-Buffer の面より奥にあるなら、その面は球の中か手前にある。
    /// 裏面のほうが手前なら、面は球よりずっと奥にある——光は届かないので塗らない。
    /// 深度を書かないのは、球どうしが隠し合うと後から描いた光が消えるため。
    /// </para>
    /// </summary>
    private void DrawPointLights(
        Camera camera, GBuffer gbuffer, ReadOnlySpan<PointLight> lights, Action<Shader> applyLighting)
    {
        Shader point = _resources.GetShader(_pointShader);
        point.Use();

        // **点光源のプログラムにも全部送る**。こちらは影も IBL も使わないが、
        // サンプラの uniform を放っておくと全部 0 番を指す——
        // 型の違うサンプラ(2D とキューブ)が同じ番号を指したまま描くと、
        // GL はエラーを出してドローコールごと捨てる。黙って光が消えるので、送っておくのが安い。
        applyLighting(point);
        gbuffer.Apply(point, camera);
        point.SetInt("uPointLightPass", 1);
        point.SetInt("uOverdrawView", OverdrawView ? 1 : 0);

        Matrix4x4 viewProjection = camera.ViewProjection;
        point.SetMatrix4("uViewProjection", viewProjection);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Gequal);
        _gl.DepthMask(false);

        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Front);

        foreach (PointLight light in lights)
        {
            if (CullLights && !IsVisible(viewProjection, light.Position, light.Radius))
            {
                LightsCulled++;
                continue;
            }

            // **光1個 = uniform 3つ + ドローコール1回**。
            // 1000 個ならドローコールも 1000 回で、CPU 側はここがいちばん重くなる。
            // 本番のエンジンはインスタンシング(位置と色を頂点バッファに並べて1回で描く)にする。
            point.SetVector4("uPointPosition", light.PositionAndRadius);
            point.SetVector3("uPointColor", light.Color);
            point.SetFloat("uVolumeRadius", light.Radius * VolumeScale);

            _volume.Draw();
            LightsDrawn++;
        }
    }

    /// <summary>
    /// **分割した球の面が、中心にいちばん近づく距離**(届く距離を 1 としたとき)。自己チェック用。
    ///
    /// <para>
    /// 1 以上なら、どの面も届く距離の外にある——光が 0 になるところまで球が覆っている。
    /// 三角形ごとに「面を含む平面までの距離」を測って、いちばん小さいものを返す。
    /// 平面までの距離は三角形そのものまでの距離以下なので、<b>控えめな(安全側の)答え</b>になる。
    /// GPU から頂点を読み戻す(<see cref="Mesh{TVertex}.ReadVertices"/>)ので遅く、検算専用。
    /// </para>
    /// </summary>
    public float MeasureInnerRadius()
    {
        Vertex[] vertices = _volume.ReadVertices();
        uint[] indices = _volume.ReadIndices();

        float nearest = float.MaxValue;

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            // light-volume.vert と同じく、向きだけを取り出す(直径 1 の球なので)。
            Vector3 a = Vector3.Normalize(vertices[indices[i]].Position);
            Vector3 b = Vector3.Normalize(vertices[indices[i + 1]].Position);
            Vector3 c = Vector3.Normalize(vertices[indices[i + 2]].Position);

            Vector3 normal = Vector3.Cross(b - a, c - a);

            // 極で潰れた三角形(面積 0)は平面が決まらないので飛ばす。
            if (normal.LengthSquared() < 1e-12f)
            {
                continue;
            }

            float distance = MathF.Abs(Vector3.Dot(Vector3.Normalize(normal), a));
            nearest = MathF.Min(nearest, distance);
        }

        return nearest * VolumeScale;
    }

    public void ReloadShaders()
    {
        _resources.GetShader(_sunShader).TryReload();
        _resources.GetShader(_pointShader).TryReload();
    }

    private void SetCap(EnableCap cap, bool enabled)
    {
        if (enabled)
        {
            _gl.Enable(cap);
        }
        else
        {
            _gl.Disable(cap);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _volume.Dispose();

        if (_emptyVao != 0)
        {
            _gl.DeleteVertexArray(_emptyVao);
            _emptyVao = 0;
        }

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
