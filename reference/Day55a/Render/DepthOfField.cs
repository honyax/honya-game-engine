using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>ボケの集め方(「被写界深度」の F5)。<c>dof-gather.frag</c> の <c>uScatterAsGather</c> と対応する。</summary>
internal enum DofGather
{
    /// <summary>
    /// **散らしとして集める**(既定)。奥の層は「自分と相手の小さいほうの半径」、手前の層は「相手の半径」で、
    /// 相手の光が自分まで届くかを決める。
    /// </summary>
    ScatterAsGather,

    /// <summary>
    /// 素朴に集める。**自分の**半径の円の中を、奥も手前も区別せずに全部平均する。
    /// ピントの合った物が奥のボケににじみ出し、手前のボケは輪郭で切り抜いたように止まる。
    /// </summary>
    Naive,
}

/// <summary>
/// **被写界深度**(Day 55)。レンズの口径が有限なので、ピントの奥行きから外れた点は画面の上で円(錯乱円)に広がる。
///
/// <para>
/// Day 54 までのカメラは<b>ピンホール</b>だった。穴が点なので、どの奥行きの点も画面の1点に写る——全部にピントが合っている。
/// 本物のレンズは光を集めるために口径を持ち、ピントの奥行きの点だけが1点に集まる。
/// それより手前や奥の点は、口径の形(ここでは円)に広がって写る。これが「ボケ」で、
/// 画面の中で「どこを見てほしいか」を決める、映画の絵づくりの主役になる。
/// </para>
///
/// <para>
/// 錯乱円の大きさは薄レンズの式から出る(計画書の要点2)。
/// <code>
///   錯乱円の半径 [画素] = L × (1/S − 1/z)      S: ピントの奥行き、z: その画素の奥行き
///   L = 口径 × 画面の高さ[画素] / (4 tan(画角/2))
/// </code>
/// <b>「ボケの大きさ = 口径 ÷ ピント面での画面の高さ」</b>と読める。センサーの大きさも焦点距離も式に残らない。
/// </para>
///
/// <para>
/// 描き方は3パス。<b>ぼけたものは縮めても分からない</b>(Day 31 のブルームと同じ)ので、集める仕事は半分の大きさでやる。
/// </para>
/// <list type="number">
/// <item><b>下ごしらえ</b>(半分)… 2x2 画素を平均した色と、そのうちいちばん手前の錯乱円</item>
/// <item><b>集める</b>(半分、2枚同時に書く)… 円盤に 48 点を撒いて、<b>奥の層</b>と<b>手前の層</b>を別々に集める</item>
/// <item><b>重ねる</b>(原寸)… ピントの合ったところは原寸の絵、奥がぼけたところは奥の層、その上に手前の層</item>
/// </list>
/// </summary>
internal sealed class DepthOfField : IDisposable
{
    /// <summary>
    /// センサーの高さ [m]。**フルサイズ(35mm 判)の 24mm**。画角から焦点距離を決めるのに使う。
    /// Unity の Physical Camera の既定も同じ大きさで、画角 40 度なら焦点距離 33mm のレンズになる。
    /// </summary>
    public const float SensorHeight = 0.024f;

    /// <summary>集める円盤の点の数。<c>dof-gather.frag</c> の <c>SampleCount</c> と同じ数にしておくこと。</summary>
    public const int SampleCount = 48;

    /// <summary>1回の <see cref="Render"/> で走る全画面パスの数(下ごしらえ・集める・重ねる)。</summary>
    public const int PassCount = 3;

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _prepareShader;
    private readonly Handle<Shader> _gatherShader;
    private readonly Handle<Shader> _combineShader;
    private readonly GpuTimer _timer;

    /// <summary>半分の大きさの色と錯乱円(RGBA16F。a が錯乱円)。**要るまで作らない**。</summary>
    private Framebuffer? _prepared;

    /// <summary>
    /// 半分の大きさの2層。0 番が奥の層、1 番が手前の層(前掛けのアルファ付き)。
    /// **1回の描画で2枚へ書く**(MRT。Day 52 の G-Buffer と同じ口)。
    /// </summary>
    private Framebuffer? _layers;

    /// <summary>重ねた結果(原寸 RGBA16F)。後ろの段(ブルーム・合成)はこれを読む。</summary>
    private Framebuffer? _output;

    private uint _emptyVao;
    private bool _disposed;

    public DepthOfField(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        string fullscreen = Path.Combine(shaderDirectory, "fullscreen.vert");
        _prepareShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "dof-prepare.frag"));
        _gatherShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "dof-gather.frag"));
        _combineShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "dof-combine.frag"));

        _emptyVao = gl.GenVertexArray();
        _timer = new GpuTimer(gl);
    }

    /// <summary>
    /// 被写界深度を掛けるか(「被写界深度」の F2)。**既定は OFF**——起動した直後の絵は Day 54 と同じ。
    ///
    /// <para>
    /// ON にしても、掛かるのは Program が<b>このフレームのカメラの数字</b>を渡したときだけ
    /// (<see cref="PostProcess.Frame"/>)。自己チェックが後処理を単独で走らせても、被写界深度は割り込まない。
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// **F 値**(「被写界深度」の F3)。焦点距離 ÷ 口径。**小さいほど口径が大きく、よくボケる**。
    /// 1.4 は明るい単焦点レンズの開放。
    /// </summary>
    public float FNumber { get; set; } = 1.4f;

    /// <summary>ボケの集め方(「被写界深度」の F5)。</summary>
    public DofGather Gather { get; set; } = DofGather.ScatterAsGather;

    /// <summary>錯乱円を色で見る(「被写界深度」の F6)。手前は青、奥は橙。</summary>
    public bool ShowCircleOfConfusion { get; set; }

    /// <summary>半分の大きさのバッファの幅(作る前は 0)。</summary>
    public int HalfWidth => _prepared?.Width ?? 0;

    /// <summary>半分の大きさのバッファの高さ(作る前は 0)。</summary>
    public int HalfHeight => _prepared?.Height ?? 0;

    /// <summary>3パスぶんの GPU の時間(ms。<see cref="GpuTimer"/>)。</summary>
    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>被写界深度が抱えている VRAM(半分の大きさ3枚 + 原寸1枚)。作る前は 0。</summary>
    public long ByteSize => (_prepared?.ByteSize ?? 0) + (_layers?.ByteSize ?? 0) + (_output?.ByteSize ?? 0);

    /// <summary>
    /// **焦点距離** f [m]。フルサイズのセンサーで、この画角になるレンズ。
    /// 画角 40 度なら 33mm、20 度なら 68mm——<b>画角を狭めるほど長いレンズ</b>(望遠)になる。
    /// </summary>
    public static float FocalLength(float fieldOfView) => SensorHeight * 0.5f / MathF.Tan(fieldOfView * 0.5f);

    /// <summary>**口径**(入射瞳の直径)A [m] = 焦点距離 ÷ F 値。光が通る穴の大きさ。</summary>
    public static float Aperture(float fieldOfView, float fNumber) => FocalLength(fieldOfView) / fNumber;

    /// <summary>
    /// **レンズの係数** L [画素・m]。錯乱円の半径 = L × (1/S − 1/z)。
    ///
    /// <para>
    /// 薄レンズの式でピントの奥行き S が焦点距離 f よりずっと大きいとみなすと、
    /// 錯乱円の直径(画面の高さに対する割合)は <c>口径 ÷ (2 S tan(画角/2)) × |1 − S/z|</c> になる。
    /// 分母の <c>2 S tan(画角/2)</c> は<b>ピントの奥行きで画面に写る範囲の高さ</b>。半径の画素に直すと
    /// <c>口径 × 画面の高さ[画素] / (4 tan(画角/2)) × |1/S − 1/z|</c> で、前半がこの L。
    /// </para>
    ///
    /// <para>
    /// F 値を決めて画角を狭めると、口径が焦点距離に比例して大きくなり(1/tan)、ピント面の高さは小さくなる(tan)。
    /// <b>ボケは 1/tan² で効く</b>——同じ F 値でも望遠のほうがはるかにボケる。
    /// </para>
    /// </summary>
    public static float LensScale(float fieldOfView, float fNumber, int screenHeight) =>
        Aperture(fieldOfView, fNumber) * screenHeight / (4.0f * MathF.Tan(fieldOfView * 0.5f));

    /// <summary>
    /// このフレームのレンズの係数。**平行投影では 0**(遠近の無いカメラにピントの奥行きは無い)。
    /// </summary>
    public float LensScale(CameraFrame camera, int screenHeight) =>
        camera.Perspective ? LensScale(camera.FieldOfView, FNumber, screenHeight) : 0.0f;

    /// <summary>
    /// **錯乱円の半径** [画素]。ピントより手前が負、奥が正。<c>blur-field.frag</c> と同じ式(上限では切らない)。
    ///
    /// <para>
    /// z = S で 0、z → ∞ で L / S に近づき、z = S / 2 で −L / S になる。
    /// <b>ピントの半分の奥行きの物は、無限遠と同じ大きさにぼける</b>——そして手前は上限なく大きくなる。
    /// </para>
    /// </summary>
    public static float CircleOfConfusion(float distance, float focusDistance, float lensScale) =>
        lensScale * ((1.0f / focusDistance) - (1.0f / distance));

    /// <summary>
    /// **薄レンズの式そのもの**(近似しない)。自己チェックが <see cref="CircleOfConfusion"/> と比べる。
    ///
    /// <para>
    /// センサーの上の錯乱円の直径は <c>c = A f (z − S) / (z (S − f))</c>。これをセンサーの高さで割って画面の画素に直し、
    /// 半分にして半径にする。<see cref="LensScale(float, float, int)"/> との違いは分母の <c>S − f</c> だけで、
    /// 近似の誤差は <c>f / S</c>(焦点距離 38mm・ピント 3.2m なら 1.2%)。
    /// </para>
    /// </summary>
    public static float ThinLensCircleOfConfusion(
        float distance, float focusDistance, float fieldOfView, float fNumber, int screenHeight)
    {
        float focal = FocalLength(fieldOfView);
        float aperture = focal / fNumber;
        float diameter = aperture * focal * (distance - focusDistance) / (distance * (focusDistance - focal));

        return 0.5f * diameter / SensorHeight * screenHeight;
    }

    /// <summary>
    /// **ぼかす**。<paramref name="field"/> は同じフレームで <see cref="BlurField.Build"/> 済みであること。
    /// 深度テストとブレンドが切れている前提で呼ぶ(<see cref="PostProcess"/> の <c>EndCore</c> が畳んでいる)。
    /// </summary>
    /// <param name="scene">ぼかす前の絵(TAA を掛けたフレームなら TAA の結果)。</param>
    /// <param name="field">奥行きと錯乱円の地図と、近所の升目。</param>
    /// <returns>ぼかした絵(原寸 RGBA16F)。</returns>
    public Texture Render(Texture scene, BlurField field)
    {
        int width = scene.Width;
        int height = scene.Height;
        int halfWidth = Math.Max(1, (width + 1) / 2);
        int halfHeight = Math.Max(1, (height + 1) / 2);

        _prepared = Ensure(_prepared, halfWidth, halfHeight);
        _output = Ensure(_output, width, height);

        if (_layers is null)
        {
            _layers = new Framebuffer(
                _gl, halfWidth, halfHeight, [RenderTargetFormat.Rgba16F, RenderTargetFormat.Rgba16F], depth: false);
        }
        else
        {
            _layers.Resize(halfWidth, halfHeight);
        }

        _timer.Begin();

        // --- 1. 下ごしらえ(半分の大きさ)---
        _prepared.Bind();
        Shader prepare = _resources.GetShader(_prepareShader);
        prepare.Use();
        BindTexture(prepare, "uScene", scene, 0);
        BindTexture(prepare, "uField", field.Field!, 1);
        DrawFullscreen();

        // --- 2. 奥の層と手前の層を集める(半分の大きさ、2枚同時)---
        _layers.Bind();
        Shader gather = _resources.GetShader(_gatherShader);
        gather.Use();
        BindTexture(gather, "uPrepared", _prepared.Color, 0);
        BindTexture(gather, "uTiles", field.Tiles!, 1);

        // 錯乱円は**原寸の画素**で持っている。半分の大きさの絵を読むときも、ずらす量は原寸の画素 → UV で直す。
        gather.SetVector2("uPixelSize", new Vector2(1.0f / width, 1.0f / height));
        gather.SetInt("uScatterAsGather", Gather == DofGather.ScatterAsGather ? 1 : 0);
        gather.SetFloat("uMaxRadius", BlurField.MaxRadius);
        gather.SetInt("uTileSize", BlurField.TileSize);
        DrawFullscreen();

        // --- 3. 原寸で重ねる ---
        _output.Bind();
        Shader combine = _resources.GetShader(_combineShader);
        combine.Use();
        BindTexture(combine, "uScene", scene, 0);
        BindTexture(combine, "uField", field.Field!, 1);
        BindTexture(combine, "uFar", _layers.Colors[0], 2);
        BindTexture(combine, "uNear", _layers.Colors[1], 3);
        combine.SetVector2("uHalfTexel", new Vector2(1.0f / halfWidth, 1.0f / halfHeight));
        combine.SetInt("uScatterAsGather", Gather == DofGather.ScatterAsGather ? 1 : 0);
        combine.SetFloat("uMaxRadius", BlurField.MaxRadius);
        combine.SetInt("uDebug", ShowCircleOfConfusion ? 1 : 0);
        DrawFullscreen();

        _timer.End();

        return _output.Color;
    }

    /// <summary>シェーダを読み直す(F5)。</summary>
    public void ReloadShaders()
    {
        _resources.GetShader(_prepareShader).TryReload();
        _resources.GetShader(_gatherShader).TryReload();
        _resources.GetShader(_combineShader).TryReload();
    }

    private Framebuffer Ensure(Framebuffer? target, int width, int height)
    {
        if (target is null)
        {
            return new Framebuffer(_gl, width, height, RenderTargetFormat.Rgba16F, depth: false);
        }

        target.Resize(width, height);
        return target;
    }

    private void DrawFullscreen()
    {
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

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

        _prepared?.Dispose();
        _layers?.Dispose();
        _output?.Dispose();
        _timer.Dispose();

        _gl.DeleteVertexArray(_emptyVao);
        _emptyVao = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
