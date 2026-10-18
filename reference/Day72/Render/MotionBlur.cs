using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>シャッターの決め方(「被写界深度とモーションブラー」の F9)。</summary>
internal enum ShutterMode
{
    /// <summary>
    /// **シャッター角**。1フレームの時間のうち、何割シャッターを開けておくか(360 度で丸ごと)。映画の決め方。
    /// フレームレートが落ちると1フレームの時間が延びるので、<b>尾も一緒に伸びる</b>。
    /// </summary>
    Angle,

    /// <summary>
    /// **シャッター速度**。何秒開けておくかを決める。<b>フレームレートによらず尾の長さが変わらない</b>。
    /// </summary>
    Time,
}

/// <summary>
/// **モーションブラー**(Day 55)。シャッターが開いている間に動いた物は、写真の上で線に伸びる。
///
/// <para>
/// Day 54 までのカメラは<b>一瞬で写す</b>カメラだった。本物のカメラはシャッターを一定の時間開けて光を溜めるので、
/// その間に動いた物は、動いた道のりに沿って伸びて写る。24fps の映画が滑らかに見えるのはこのおかげで、
/// 1コマ1コマに「動いていた」ことが写り込んでいる。ゲームは一瞬で写すので、同じ 30fps でもカクついて見える。
/// </para>
///
/// <para>
/// <b>速度は Day 54 の速度バッファをそのまま読む</b>。「この画素は、前のフレームで画面のどこに居たか」(UV、前 → 今)は、
/// TAA が履歴を引くためのものだったが、それはそのまま「1フレームの間に画面の上をどれだけ動いたか」でもある。
/// 速度を作る側(<see cref="MotionVectors"/>)は1行も変えずに、読む側を足すだけで済んだ。
/// </para>
///
/// <para>
/// 描き方は McGuire ほか 2012 年の再構成フィルタ(<c>motion-blur.frag</c>)。尾は物の輪郭の外まで伸びるので、
/// 自分の画素の速度だけでなく、<b>近所の升目でいちばん速い速度</b>(<see cref="BlurField"/>)に沿って読む。
/// </para>
/// </summary>
internal sealed class MotionBlur : IDisposable
{
    /// <summary>尾に沿って読む点の数。<c>motion-blur.frag</c> の <c>SampleCount</c> と同じ数にしておくこと。</summary>
    public const int SampleCount = 15;

    /// <summary>
    /// 「手前か奥か」をなめらかに決める幅 [m]。これより離れていれば、はっきり手前(奥)とみなす。
    /// 同じ面の上の2画素は、深度の精度の都合で少しずれることがあるので、0 か 1 かで切るとちらつく。
    /// </summary>
    public const float SoftDepth = 0.1f;

    /// <summary>1回の <see cref="Render"/> で走る全画面パスの数。</summary>
    public const int PassCount = 1;

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _shader;
    private readonly GpuTimer _timer;

    /// <summary>ぼかした結果(原寸 RGBA16F)。**要るまで作らない**。</summary>
    private Framebuffer? _output;

    private uint _emptyVao;
    private bool _disposed;

    public MotionBlur(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        _shader = resources.LoadShader(
            Path.Combine(shaderDirectory, "fullscreen.vert"),
            Path.Combine(shaderDirectory, "motion-blur.frag"));

        _emptyVao = gl.GenVertexArray();
        _timer = new GpuTimer(gl);
    }

    /// <summary>
    /// モーションブラーを掛けるか(「被写界深度とモーションブラー」の F7)。**既定は OFF**。
    ///
    /// <para>
    /// 掛かるのは Program が<b>このフレームの速度とカメラの数字</b>を両方渡したときだけ
    /// (<see cref="PostProcess.Velocity"/> / <see cref="PostProcess.Frame"/>)。
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>シャッター角 [度](「被写界深度とモーションブラー」の F8)。180 度が映画の定番(1フレームの半分だけ開ける)。</summary>
    public float ShutterAngle { get; set; } = 180.0f;

    /// <summary>シャッターの決め方(「被写界深度とモーションブラー」の F9)。</summary>
    public ShutterMode Shutter { get; set; } = ShutterMode.Angle;

    /// <summary>
    /// シャッター速度 [秒]。<see cref="ShutterMode.Time"/> のときだけ使う。
    /// 1/120 秒は、60fps のときに 180 度と同じ長さになる値。
    /// </summary>
    public float ShutterSeconds { get; set; } = 1.0f / 120.0f;

    /// <summary>
    /// 近所の升目の速度に沿って読むか(「被写界深度とモーションブラー」の F10)。
    /// **OFF にすると自分の速度だけ**——尾が物の輪郭の中に閉じ込められ、背景の上へ伸びない。
    /// </summary>
    public bool UseTiles { get; set; } = true;

    /// <summary>升目ごとの速度を色で見る(「被写界深度とモーションブラー」の F11)。</summary>
    public bool ShowTiles { get; set; }

    /// <summary>直前のフレームでシャッターが開いていた割合(1フレームに対して)。HUD と内訳の表示用。</summary>
    public float LastShutterOpen { get; private set; }

    /// <summary>1パスぶんの GPU の時間(ms。<see cref="GpuTimer"/>)。</summary>
    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>ぼかした結果の VRAM。作る前は 0。</summary>
    public long ByteSize => _output?.ByteSize ?? 0;

    /// <summary>
    /// **1フレームのうちシャッターが開いている割合**。速度バッファは「1フレームぶんの動き」なので、これを掛けると尾の長さになる。
    ///
    /// <para>
    /// 角度で決めると、割合は 180 / 360 = 0.5 のまま——1フレームの時間が倍になれば、1フレームぶんの動きも倍になり、<b>尾が倍に伸びる</b>。
    /// 秒で決めると、割合が <c>シャッター速度 ÷ 1フレームの時間</c> になり、1フレームの動きが倍になったぶん割合が半分になって、<b>尾は同じ長さ</b>。
    /// </para>
    /// </summary>
    public static float OpenFraction(ShutterMode shutter, float shutterAngle, float shutterSeconds, float frameSeconds) =>
        shutter == ShutterMode.Angle
            ? shutterAngle / 360.0f
            : shutterSeconds / MathF.Max(frameSeconds, 1.0e-4f);

    /// <summary>いまの設定で、1フレームの時間が <paramref name="frameSeconds"/> のときの割合。</summary>
    public float OpenFraction(float frameSeconds) =>
        OpenFraction(Shutter, ShutterAngle, ShutterSeconds, frameSeconds);

    /// <summary>
    /// **ぼかす**。<paramref name="field"/> は同じフレームで <see cref="BlurField.Build"/> 済みであること(速度を渡して)。
    /// </summary>
    /// <param name="scene">ぼかす前の絵(被写界深度を掛けたフレームなら、その結果)。</param>
    /// <param name="velocity">速度バッファ(UV、前 → 今)。</param>
    /// <param name="field">奥行きの地図と、近所の升目。</param>
    /// <param name="camera">このフレームのカメラの数字(1フレームの時間を使う)。</param>
    public Texture Render(Texture scene, Texture velocity, BlurField field, CameraFrame camera)
    {
        if (_output is null)
        {
            _output = new Framebuffer(_gl, scene.Width, scene.Height, RenderTargetFormat.Rgba16F, depth: false);
        }
        else
        {
            _output.Resize(scene.Width, scene.Height);
        }

        LastShutterOpen = OpenFraction(camera.FrameSeconds);

        _timer.Begin();

        _output.Bind();
        Shader shader = _resources.GetShader(_shader);
        shader.Use();

        BindTexture(shader, "uScene", scene, 0);
        BindTexture(shader, "uField", field.Field!, 1);
        BindTexture(shader, "uVelocity", velocity, 2);
        BindTexture(shader, "uTiles", field.Tiles!, 3);

        shader.SetVector2("uScreenSize", new Vector2(scene.Width, scene.Height));
        shader.SetFloat("uShutterOpen", LastShutterOpen);
        shader.SetFloat("uMaxRadius", BlurField.MaxRadius);
        shader.SetInt("uTileSize", BlurField.TileSize);
        shader.SetInt("uUseTiles", UseTiles ? 1 : 0);
        shader.SetFloat("uSoftDepth", SoftDepth);
        shader.SetInt("uDebug", ShowTiles ? 1 : 0);

        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _timer.End();

        return _output.Color;
    }

    /// <summary>シェーダを読み直す(F5)。</summary>
    public void ReloadShader() => _resources.GetShader(_shader).TryReload();

    private static void BindTexture(Shader shader, string name, Texture texture, int unit)
    {
        texture.Bind(TextureUnit.Texture0 + unit);
        shader.SetInt(name, unit);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _output?.Dispose();
        _timer.Dispose();

        _gl.DeleteVertexArray(_emptyVao);
        _emptyVao = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
