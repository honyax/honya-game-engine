using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **速度バッファ**(Day 54)。「この画素は、前のフレームで画面のどこに写っていたか」を画素ごとに持つ。
///
/// <para>
/// TAA は前のフレームの結果(履歴)を読むが、カメラも物も動くので、<b>同じ画素の場所</b>を読むと別の物の色を拾う。
/// 読むべきは「この画素に今写っている点が、前のフレームで写っていた場所」で、その差がこのバッファの中身になる。
/// 単位は UV(画面の幅・高さを 1)、向きは「前 → 今」。形式は RG16F(<see cref="RenderTargetFormat.Rg16F"/>)。
/// </para>
///
/// <para>
/// 今日描くのは<b>カメラの動きだけ</b>(全画面1枚。<c>camera-velocity.frag</c>)。
/// 深度から世界の点を戻し、前のフレームの行列で写し直す。
/// <b>止まっている物は全部これで正しい</b>——シーンの中身を何も知らなくてよいのが、この1段目の値打ちになる。
/// </para>
///
/// <para>
/// <b>自分で動いた物は、この1段では出てこない</b>。深度に入っているのは「今どこにあるか」だけで、
/// キャラクターが前のフレームにどこに居たかは書かれていない。
/// 走るキャラクターの画素は「止まっている物」として扱われ、履歴を背景の側から読むことになる——
/// TAA の疑い(<see cref="TemporalAA.Rectify"/>)が色を引き戻すので残像は出ないが、
/// 履歴が溜まらないので輪郭が階段に戻り、ずらしの揺れがそのまま見える。
/// それを直す2段目は Day 54b。
/// </para>
/// </summary>
internal sealed class MotionVectors : IDisposable
{
    /// <summary>
    /// 1フレームでカメラがこれ以上動いたら、前のフレームと<b>繋がっていない</b>(カット)とみなす [m]。
    /// 走るキャラクターを追うカメラでも 1フレーム 0.1m 程度なので、2m は瞬間移動と言ってよい。
    /// </summary>
    public const float CutDistance = 2.0f;

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _cameraShader;
    private readonly GpuTimer _timer;

    /// <summary>
    /// 速度(RG16F)。**要るまで作らない**(TAA を入れたときに初めて作る)。
    ///
    /// <para>
    /// 1段目が読む深度(<c>uDepth</c>)はシーンの深度のコピー(<see cref="PostProcess.SceneDepth"/>)を使う——
    /// 描き込み先に挿さっている深度を同時にテクスチャとして読むと、フィードバックループになる(Day 50 と同じ話)。
    /// 今日は深度テストを1回も使わないので、この入れ物は<b>カラー1枚だけ</b>。
    /// </para>
    /// </summary>
    private Framebuffer? _target;

    private Matrix4x4 _previousViewProjection;
    private Vector3 _previousCameraPosition;
    private bool _hasPrevious;
    private bool _renderedLastFrame;

    private uint _emptyVao;
    private bool _disposed;

    public MotionVectors(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        _cameraShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "fullscreen.vert"),
            Path.Combine(shaderDirectory, "camera-velocity.frag"));

        _emptyVao = gl.GenVertexArray();
        _timer = new GpuTimer(gl);
    }

    /// <summary>
    /// **前のフレームと繋がっていない**。カメラが瞬間移動した、別の絵に切り替わった、TAA を入れた直後。
    /// Program はこれを見て TAA の履歴を捨てる(<see cref="TemporalAA.Reset"/>)。
    /// </summary>
    public bool Cut { get; private set; }

    /// <summary>速度(RG16F)。まだ一度も描いていなければ null。</summary>
    public Texture? Velocity => _target?.Color;

    /// <summary>速度の入れ物。自己チェックが読み返すために公開する。</summary>
    public Framebuffer? Target => _target;

    /// <summary>速度を描くのに掛かった GPU の時間(ms。<see cref="GpuTimer"/>)。</summary>
    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>速度の VRAM。作る前は 0。</summary>
    public long ByteSize => _target?.ByteSize ?? 0;

    /// <summary>
    /// **フレームの頭で呼ぶ**。前のフレームとの繋がりを確かめる。
    /// </summary>
    public void BeginFrame()
    {
        // **前のフレームで速度を描かなかった**(TAA を切っていた・ゲーム中だった)なら、
        // 手元の「前のフレームの行列」は何フレームも前のもの。繋がっていないとみなす。
        if (!_renderedLastFrame)
        {
            _hasPrevious = false;
        }

        _renderedLastFrame = false;
    }

    /// <summary>
    /// **速度を描く**。本描画が全部終わったあと(<c>_post.End</c> の直前)に1回。
    /// </summary>
    /// <param name="camera">このフレームのカメラ(ずらした状態のまま渡す)。</param>
    /// <param name="scene">シーンのバッファ。大きさを合わせる相手。</param>
    /// <param name="sceneDepth">シーンの深度のコピー(<see cref="PostProcess.CaptureDepth"/> 済みのもの)。1段目が読む。</param>
    public void Render(Camera camera, Framebuffer scene, Texture sceneDepth)
    {
        if (_target is null)
        {
            _target = new Framebuffer(_gl, scene.Width, scene.Height, RenderTargetFormat.Rg16F, depth: false);
        }
        else if (_target.Width != scene.Width || _target.Height != scene.Height)
        {
            _target.Resize(scene.Width, scene.Height);
            _hasPrevious = false;
        }

        Matrix4x4 current = camera.UnjitteredViewProjection;

        // **前のフレームと繋がっているか**。2つのどちらかなら繋がっていない。
        //   - 前のフレームの行列が無い(TAA を入れた直後など)
        //   - カメラが瞬間移動した
        //
        // Day 54b で3つ目(描いたものの半分以上が前のフレームに居なかった)が増える。
        bool cameraJumped = _hasPrevious
            && Vector3.Distance(camera.Position, _previousCameraPosition) > CutDistance;
        Cut = !_hasPrevious || cameraJumped;

        // 繋がっていないなら「動いていない」ことにする。どうせ履歴は捨てられるので、値は何でもよいが、
        // 意味の無い大きな速度を書いておくと、表示(速度)で画面が一瞬真っ赤になる。
        Matrix4x4 previous = Cut ? current : _previousViewProjection;

        _timer.Begin();

        _target.Bind();

        bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        bool cull = _gl.IsEnabled(EnableCap.CullFace);

        // GL_POLYGON_MODE は int を2個返す(PostProcess.EndCore と同じ用心)。
        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);

        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        RenderCameraMotion(camera, previous, sceneDepth);

        SetCap(EnableCap.DepthTest, depthTest);
        SetCap(EnableCap.Blend, blend);
        SetCap(EnableCap.CullFace, cull);
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);

        _timer.End();

        _previousViewProjection = current;
        _previousCameraPosition = camera.Position;
        _hasPrevious = true;
        _renderedLastFrame = true;
    }

    /// <summary>
    /// 前のフレームとの繋がりを切る。画面の大きさが変わったとき(縦横比が変わると、前の行列で写し直した位置が意味を失う)。
    /// </summary>
    public void Invalidate() => _hasPrevious = false;

    /// <summary>
    /// **カメラの動き**。全画面1枚。深度テストは使わない(全部の画素に書く)。
    ///
    /// <para>
    /// 2つの行列を CPU で1本にしてから渡す。
    /// <c>ずらした VP の逆</c> で今の画面の点を世界へ戻し、<c>前の VP</c> で前のフレームの画面へ写す。
    /// 逆行列は<b>ずらした</b>ほうを使う——深度はずらして描いたものなので、同じずらしで戻さないと半画素ずれた点を戻す。
    /// </para>
    /// </summary>
    private void RenderCameraMotion(Camera camera, Matrix4x4 previous, Texture sceneDepth)
    {
        _gl.Disable(EnableCap.DepthTest);

        Matrix4x4.Invert(camera.ViewProjection, out Matrix4x4 inverse);

        Shader shader = _resources.GetShader(_cameraShader);
        shader.Use();
        shader.SetMatrix4("uReprojection", inverse * previous);
        shader.SetVector2("uJitter", camera.Jitter);

        sceneDepth.Bind(TextureUnit.Texture0);
        shader.SetInt("uDepth", 0);

        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
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

        _target?.Dispose();
        _timer.Dispose();

        _gl.DeleteVertexArray(_emptyVao);
        _emptyVao = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
