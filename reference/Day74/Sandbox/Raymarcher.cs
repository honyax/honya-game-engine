using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>何を画面に出すか。シェーダ側の <c>uView</c> と同じ番号。</summary>
internal enum SdfView
{
    /// <summary>陰影。光・影・AO・霧。**今日の到達点**。</summary>
    Shaded,

    /// <summary>歩数。**形の縁と地平線が赤くなる**(かすめる光線ほど歩く)。</summary>
    Steps,

    /// <summary>距離の断面。水平な板で場面を切り、距離を等高線で塗る。**距離場そのものが見える**。</summary>
    Slice,

    /// <summary>法線。距離関数の傾きから出したもの。</summary>
    Normal,
}

/// <summary>影の出し方。シェーダ側の <c>uShadow</c> と同じ番号。</summary>
internal enum SdfShadow
{
    /// <summary>柔らかい影。光線がいちばん際どく外れたときの k × h / t(要点6)。</summary>
    Soft,

    /// <summary>硬い影。当たったら 0、当たらなければ 1。</summary>
    Hard,

    /// <summary>影の光線を飛ばさない。**いちばん高い仕事を外す**とどれだけ軽くなるかを見る。</summary>
    None,
}

/// <summary>
/// **画面を覆う三角形1枚で、距離関数の場面を描く**(Day 58)。今日の主役の GL 側。
///
/// <para>
/// やっていることは <c>raymarch.frag</c> に uniform を送って三角形を1枚描くだけ——
/// <b>頂点バッファも索引も無い</b>。Day 31 の後処理と同じ <c>fullscreen.vert</c> を使うので、
/// エンジンの側から見ると「全画面のパスが1本増えた」以上のものではない。
/// </para>
///
/// <para>
/// 後処理と違うのは<b>深度を書く</b>こと。当たった点をビュー射影で写して <c>gl_FragDepth</c> に入れるので、
/// あとから描く粒も、TAA の速度も、被写界深度も、SDF の形をラスタの形と区別せずに扱える(要点7)。
/// <see cref="Composite"/> を入れると、外れた画素を捨てて Day 31 の床と箱の中に形を混ぜる。
/// </para>
///
/// <para>
/// <b>置き場所は <c>Sandbox/</c></b>(Day 57 の <see cref="GpuParticles"/> と同じ)。
/// <c>Render/</c> の誰もこのクラスを知らず、借りているのは Day 57 と同じ4つ
/// (<c>Shader</c> / <c>RenderResources</c> / <c>Camera</c> / <c>GpuTimer</c>)。
/// </para>
/// </summary>
internal sealed class Raymarcher : IDisposable
{
    /// <summary>シェーダの <c>MAX_STEPS</c>。<see cref="MaxSteps"/> はこれを超えられない。</summary>
    public const int MaxStepsLimit = 256;

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _shader;
    private readonly GpuTimer _timer;

    /// <summary>
    /// 中身の無い頂点配列。**コアプロファイルでは VAO を挿さないと描けない**ので、
    /// 属性を1つも持たないものを挿す(<see cref="PostProcess"/> と同じ)。
    /// </summary>
    private uint _emptyVao;

    private bool _disposed;

    public Raymarcher(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        _shader = resources.LoadShader(
            Path.Combine(shaderDirectory, "fullscreen.vert"),
            Path.Combine(shaderDirectory, "raymarch.frag"));

        _emptyVao = gl.GenVertexArray();
        _timer = new GpuTimer(gl);
    }

    public SdfSceneKind Scene { get; set; }

    public SdfView View { get; set; }

    public SdfShadow Shadow { get; set; }

    /// <summary>
    /// **ラスタと混ぜる**。外れた画素を捨て、SDF の床を外す(Day 31 の床と箱が見える)。
    /// 切ると Shadertoy と同じく、画面の全画素をこのシェーダが塗る。
    /// </summary>
    public bool Composite { get; set; }

    /// <summary>場面の時計(秒)。形の動きは全部この関数。</summary>
    public float Time { get; set; }

    /// <summary>歩幅の係数。**ねじれの場面では 1 / <see cref="SdfScene.TwistLipschitz"/> にすると表面が崩れない**。</summary>
    public float StepScale { get; set; } = 1.0f;

    public int MaxSteps { get; set; } = 128;

    /// <summary>光線をどこまで伸ばすか(m)。カメラの遠クリップ面(100m)より手前にしておく。</summary>
    public float MaxDistance { get; set; } = 80.0f;

    /// <summary>距離の断面の高さ(m)。</summary>
    public float SliceHeight { get; set; }

    public Vector3 LightDirection { get; set; } = Vector3.Normalize(new Vector3(-0.53f, -0.72f, 0.45f));

    public Vector3 LightColor { get; set; } = Vector3.One;

    public Vector3 AmbientColor { get; set; } = new(0.13f, 0.16f, 0.22f);

    /// <summary>直前に測れた GPU の時間(ms)。**画素の数 × 歩数**でほぼ決まる。</summary>
    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>
    /// **1枚描く**。いま挿さっているフレームバッファ(ふつうは <c>_post.Scene</c>)の、色と深度へ書く。
    /// </summary>
    /// <param name="width">描き込み先の幅(画素)。<c>gl_FragCoord</c> を 0〜1 に直すのに使う。</param>
    /// <param name="height">同じく高さ。</param>
    public void Draw(Camera camera, int width, int height)
    {
        // **ずらしの入った行列をそのまま逆にする**(要点3)。
        // TAA を入れているフレームでは、光線も画素の中で毎フレーム動き、SDF の縁も均される。
        Matrix4x4 viewProjection = camera.ViewProjection;
        Matrix4x4.Invert(viewProjection, out Matrix4x4 inverseViewProjection);

        Shader shader = _resources.GetShader(_shader);
        shader.Use();

        shader.SetMatrix4("uInverseViewProjection", inverseViewProjection);
        shader.SetMatrix4("uViewProjection", viewProjection);
        shader.SetVector2("uResolution", new Vector2(width, height));

        shader.SetInt("uScene", (int)Scene);
        shader.SetInt("uView", (int)View);
        shader.SetInt("uShadow", (int)Shadow);
        shader.SetInt("uGround", Composite ? 0 : 1);
        shader.SetInt("uComposite", Composite ? 1 : 0);
        shader.SetFloat("uTime", Time);
        shader.SetFloat("uSliceHeight", SliceHeight);

        shader.SetFloat("uStepScale", StepScale);
        shader.SetInt("uMaxSteps", Math.Clamp(MaxSteps, 1, MaxStepsLimit));
        shader.SetFloat("uMaxDistance", MaxDistance);
        shader.SetFloat("uHitEpsilon", SdfScene.HitEpsilon);

        shader.SetVector3("uLightDirection", LightDirection);
        shader.SetVector3("uLightColor", LightColor);
        shader.SetVector3("uAmbientColor", AmbientColor);

        // **深度は読んで書く**。後処理の全画面パスは深度テストを切るが、こちらは入れる。
        //
        // LEQUAL にしてあるのは、外れた画素が深度 1.0 を書くから。Clear した値も 1.0 なので、
        // LESS だと「何も無いところ」にも描けなくなる(歩数・断面の表示で空の画素が抜ける)。
        //
        // <b>gl_FragDepth を書くと、早期深度テストが効かなくなる</b>——
        // GPU は画素シェーダを走らせる前に深度で弾く最適化を持っているが、
        // 深度をシェーダが決めるならその前には判定できない。この三角形は全画素を覆うので、
        // ラスタの床の裏に隠れる画素も、全部の歩数を払ってから捨てられる(要点8)。
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        // 三角形1枚なので裏向きで消えると何も出ない。カリングは借りて返す。
        bool culling = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Disable(EnableCap.CullFace);

        _timer.Begin();
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
        _timer.End();

        // **借りた状態は返す**(Day 18 からの作法)。深度の比べ方はエンジン全体が LESS の前提。
        _gl.DepthFunc(DepthFunction.Less);

        if (culling)
        {
            _gl.Enable(EnableCap.CullFace);
        }
    }

    /// <summary>C# の鏡に、いまの場面と時刻を写す(自己チェック・内訳・1画素を追う表示用)。</summary>
    public SdfScene CreateMirror() => new()
    {
        Kind = Scene,
        Time = Time,
        Ground = !Composite,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _timer.Dispose();

        if (_emptyVao != 0)
        {
            _gl.DeleteVertexArray(_emptyVao);
            _emptyVao = 0;
        }

        // シェーダは RenderResources が持っているので、ここでは返さない
        // (Day 31 の「窓口が寿命を持つ」に合わせる)。
    }
}
