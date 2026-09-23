using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// 履歴の疑い方(「TAA」の F5)。<c>taa.frag</c> の <c>uRectify</c> と対応する。
/// </summary>
internal enum TaaRectify
{
    /// <summary>疑わない。**動く物の後ろに影(ゴースト)が残る**のが見える。</summary>
    None,

    /// <summary>近所 9 画素の最小〜最大の箱へ、成分ごとに押し込む(Karis 2014)。</summary>
    Clamp,

    /// <summary>平均 ± 標準偏差の箱へ、箱の中心に向かって切る(Salvi 2016 / Playdead 2016)。**既定**。</summary>
    VarianceClip,
}

/// <summary>TAA の中間の量を見る(「TAA」の F3)。</summary>
internal enum TaaDebugView
{
    /// <summary>普通に出す。</summary>
    None,

    /// <summary>**揺れの素**。履歴を混ぜず、ずらして描いた今の絵だけ。</summary>
    Current,

    /// <summary>速度バッファ。止まっているところは黒、横は赤、縦は緑。</summary>
    Velocity,

    /// <summary>履歴を疑って動かした量。**動かすと物の縁が赤く光る**。止めていても細かい模様には点が散る。</summary>
    Rectified,
}

/// <summary>
/// **TAA(Temporal Anti-Aliasing)**。Day 54 の主役。
///
/// <para>
/// FXAA(Day 38)は「出来上がった1枚の絵から段差を探して均す」手法だった。
/// 1枚しか見ないので、**1画素より細いもの**(遠くの電線、手すりの縁)は、そもそも絵に写ったり写らなかったりしていて、
/// 均しようがない——カメラが動くとちらちら点滅する。
/// </para>
///
/// <para>
/// TAA は<b>毎フレーム、画面を1画素未満ずらして描き</b>、前のフレームまでの結果と混ぜる。
/// 同じ画素が毎フレーム少しずつ違う点を標本にするので、混ぜ続けると
/// 1画素の中を何十点も取ったのと同じ値に近づく。<b>スーパーサンプリングの代金を、1フレームで払わず時間に割り振る</b>。
/// だから細いものも「写る割合」として正しく出る。
/// </para>
///
/// <para>
/// 難しいのは「前のフレームの結果」の扱いで、ここがクラスの半分以上を占める。
/// </para>
/// <list type="number">
/// <item><b>どこから読むか</b> … 物もカメラも動くので、同じ画素の場所ではなく「この画素が前に居た場所」から読む。
/// その場所は速度バッファ(<see cref="MotionVectors"/>)が教える</item>
/// <item><b>信じてよいか</b> … 前に居た場所が隠れていた(物が動いて見えるようになった)なら、履歴は別の物の色。
/// 今の絵の近所から大きく外れていたら、近所の範囲まで引き戻す(<see cref="Rectify"/>)</item>
/// <item><b>どう読むか</b> … 今日は双線形1本。読み直すたびに少しぼけるので、
/// カメラを動かしている間、絵がじわじわ眠くなる(Day 54b で Catmull-Rom を入れる)</item>
/// </list>
///
/// <para>
/// <b>代償</b>。履歴に画面と同じ大きさの RGBA16F が2枚(960x640 で 9.4MB)と、全画面1パス。
/// それに速度バッファ(<see cref="MotionVectors"/>)。FXAA は 8bit を1枚読むだけだった。
/// </para>
/// </summary>
internal sealed class TemporalAA : IDisposable
{
    /// <summary>
    /// ずらし方の周期。**Halton(2, 3) の 8 点**を繰り返す。
    ///
    /// <para>
    /// 点が多いほど細かく取れるが、一巡するまでの時間が延びる。
    /// 混ぜる割合 1 割なら、効いているのは直近 20 フレームほど(0.9^20 = 12%)なので、
    /// 16 点にしても後半の 8 点は前半が薄まってから来る。8 点は UE4 の既定と同じ。
    /// </para>
    /// </summary>
    public const int SampleCount = 8;

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _shader;
    private readonly GpuTimer _timer;

    /// <summary>
    /// 履歴の2枚。**交互に使う**(ping-pong。Day 31 のぼかしと同じ形)。
    ///
    /// <para>
    /// 前のフレームの結果を読みながら、今のフレームの結果を書く。同じテクスチャを読みつつ書くことはできないので2枚要る。
    /// 書いたほうが、そのまま<b>今のフレームの出力</b>(ブルームと合成が読む)であり、<b>次のフレームの履歴</b>でもある。
    /// </para>
    ///
    /// <para>
    /// <b>RGBA16F でなければならない</b>。8bit にすると、1割ずつ混ぜる差(0.1 × 差)が 1/255 を下回ったところで
    /// 丸めに負けて、<b>履歴が今の絵に追いつかないまま止まる</b>。暗い面ほどそうなりやすく、縞が残る。
    /// </para>
    ///
    /// <para>
    /// <b>要るまで作らない</b>(Day 50 の深度のコピーと同じ)。TAA を一度も入れなければ VRAM を1バイトも使わない。
    /// </para>
    /// </summary>
    private Framebuffer? _historyA;
    private Framebuffer? _historyB;

    /// <summary>速度と「疑った量」を出すための1枚。**表示を使ったときにだけ作る**(<see cref="Resolve"/>)。</summary>
    private Framebuffer? _debugTarget;

    /// <summary>true なら A を読んで B へ書く。1回解決するごとに入れ替わる。</summary>
    private bool _readA;

    private bool _historyValid;

    /// <summary>ずらしの番号(1〜<see cref="SampleCount"/>)。0 はまだ一度も進めていない。</summary>
    private int _sampleIndex;

    private uint _emptyVao;
    private bool _disposed;

    public TemporalAA(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        _shader = resources.LoadShader(
            Path.Combine(shaderDirectory, "fullscreen.vert"),
            Path.Combine(shaderDirectory, "taa.frag"));

        _emptyVao = gl.GenVertexArray();
        _timer = new GpuTimer(gl);
    }

    /// <summary>
    /// TAA を掛けるか(「TAA」の F2)。**既定は OFF**——起動した直後の絵は Day 53 と同じ。
    ///
    /// <para>
    /// ON にしても、掛かるのは Program が<b>このフレームの速度</b>を渡したときだけ
    /// (<see cref="PostProcess.Velocity"/>)。自己チェックが後処理を単独で走らせても、TAA は割り込まない。
    /// </para>
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>今の絵をどれだけ混ぜるか(「TAA」の F4)。0.1 なら 1 割。</summary>
    public float CurrentWeight { get; set; } = 0.1f;

    /// <summary>履歴の疑い方(「TAA」の F5)。</summary>
    public TaaRectify Rectify { get; set; } = TaaRectify.VarianceClip;

    /// <summary>分散で切るときの箱の広さ(標準偏差の何倍か)。1 前後が定番。</summary>
    public float VarianceGamma { get; set; } = 1.0f;

    /// <summary>
    /// 毎フレームずらすか(「TAA」の F6)。**OFF にすると AA は効かなくなる**。
    ///
    /// <para>
    /// ずらさなければ、同じ画素は毎フレーム同じ点を標本にするので、何枚混ぜても1点ぶんの情報しか無い。
    /// 残るのは「時間方向のぼかし」だけ——TAA の AA の正体がジッターの側にあることが見える。
    /// </para>
    /// </summary>
    public bool JitterEnabled { get; set; } = true;

    /// <summary>中間の量を見る(「TAA」の F3)。</summary>
    public TaaDebugView DebugView { get; set; } = TaaDebugView.None;

    /// <summary>いまのずらし [画素]。-0.5〜0.5。HUD と内訳の表示用。</summary>
    public Vector2 CurrentJitterPixels { get; private set; }

    /// <summary>いまのずらしの番号(1〜8)。</summary>
    public int SampleIndex => _sampleIndex;

    /// <summary>履歴を持っているか。false の間は今の絵をそのまま出す。</summary>
    public bool HistoryValid => _historyValid;

    /// <summary>履歴を捨てた回数(カット・大きさの変化・切り替え)。HUD 用。</summary>
    public int ResetCount { get; private set; }

    /// <summary>解決パスの GPU の時間(ms。<see cref="GpuTimer"/>)。</summary>
    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>履歴2枚の VRAM。作る前は 0。</summary>
    public long HistoryByteSize => (_historyA?.ByteSize ?? 0) + (_historyB?.ByteSize ?? 0);

    /// <summary>表示用の1枚の VRAM。表示(速度・疑った量)を一度も使っていなければ 0。</summary>
    public long DebugByteSize => _debugTarget?.ByteSize ?? 0;

    /// <summary>TAA が抱えている VRAM の合計(後処理の HUD に足す)。</summary>
    public long ByteSize => HistoryByteSize + DebugByteSize;

    /// <summary>履歴の大きさ(作る前は 0)。</summary>
    public int Width => _historyA?.Width ?? 0;

    public int Height => _historyA?.Height ?? 0;

    /// <summary>
    /// **van der Corput の数列**。整数 <paramref name="index"/> を <paramref name="radix"/> 進法で書き、
    /// 桁を小数点の右へ鏡に映す。
    ///
    /// <para>
    /// 2 進法なら 1, 2, 3, 4 … が 0.5, 0.25, 0.75, 0.125 … になる。
    /// <b>どこで打ち切っても、それまでの点が 0〜1 に満遍なく散っている</b>のが性質で、
    /// 乱数のように固まったり、等間隔の格子のように縞(モアレ)を作ったりしない。
    /// </para>
    /// </summary>
    public static float RadicalInverse(int index, int radix)
    {
        float result = 0.0f;
        float fraction = 1.0f / radix;

        while (index > 0)
        {
            result += (index % radix) * fraction;
            index /= radix;
            fraction /= radix;
        }

        return result;
    }

    /// <summary>
    /// **Halton 列**。x を 2 進、y を 3 進の van der Corput にしたもの。
    ///
    /// <para>
    /// 2 と 3 のように<b>互いに素な基数</b>を組むのが肝で、同じ基数を x と y に使うと
    /// 点が対角線の上に並んでしまう。番号 0 は (0, 0) なので、1 から使う。
    /// </para>
    /// </summary>
    public static Vector2 Halton(int index) => new(RadicalInverse(index, 2), RadicalInverse(index, 3));

    /// <summary>
    /// 番号 <paramref name="index"/>(1〜)のずらしを画素で。**画素の中心を 0 にして -0.5〜0.5**。
    /// </summary>
    public static Vector2 JitterPixels(int index) => Halton(index) - new Vector2(0.5f);

    /// <summary>
    /// **このフレームのずらしを決める**。Program が毎フレーム1回呼び、戻り値を <see cref="Camera.Jitter"/> に入れる。
    ///
    /// <para>
    /// 戻り値は NDC(-1〜1)。1画素は <c>2 / 幅</c> なので、画素のずらしに <c>2 / 幅</c> を掛ける。
    /// 画面の大きさで割るのは、ずらしたいのが「画素の中の位置」だから——同じ NDC のずらしでも、
    /// 画面が大きければ何画素ぶんにもなってしまう。
    /// </para>
    /// </summary>
    public Vector2 NextJitter(int width, int height)
    {
        if (!JitterEnabled)
        {
            CurrentJitterPixels = Vector2.Zero;
            return Vector2.Zero;
        }

        _sampleIndex = (_sampleIndex % SampleCount) + 1;
        CurrentJitterPixels = JitterPixels(_sampleIndex);

        return new Vector2(
            CurrentJitterPixels.X * 2.0f / Math.Max(1, width),
            CurrentJitterPixels.Y * 2.0f / Math.Max(1, height));
    }

    /// <summary>
    /// **履歴を捨てる**。次の1フレームは今の絵をそのまま出し、そこから溜め直す。
    ///
    /// <para>
    /// 捨てるべきときは「前のフレームと絵が繋がっていない」とき——カメラが瞬間移動した(カット)、
    /// 別のシーンに切り替わった、画面の大きさが変わった。
    /// 疑い(<see cref="Rectify"/>)があるので捨てなくても数フレームで消えるが、
    /// その数フレームは前の絵が薄く重なる。実際のエンジンもカメラのカットを知らせる口を持っている。
    /// </para>
    /// </summary>
    public void Reset()
    {
        if (_historyValid)
        {
            ResetCount++;
        }

        _historyValid = false;
    }

    /// <summary>
    /// **解決する**。今の絵と履歴を混ぜて書き、その結果(= 次の履歴)を返す。
    ///
    /// <para>
    /// 深度テストとブレンドが切れている前提で呼ぶ(<see cref="PostProcess"/> の <c>EndCore</c> が畳んでいる)。
    /// 履歴の大きさは<b>今の絵に合わせる</b>——違えば作り直して溜め直す。
    /// </para>
    /// </summary>
    /// <param name="current">今のフレーム(ずらして描いたシーンバッファ)。</param>
    /// <param name="depth">今のフレームの深度(近所でいちばん手前を探す)。</param>
    /// <param name="velocity">速度バッファ(<see cref="MotionVectors.Velocity"/>)。</param>
    public Texture Resolve(Texture current, Texture depth, Texture velocity)
    {
        EnsureHistory(current.Width, current.Height);

        Framebuffer read = _readA ? _historyA! : _historyB!;
        Framebuffer write = _readA ? _historyB! : _historyA!;

        _timer.Begin();

        Shader shader = _resources.GetShader(_shader);
        shader.Use();

        BindTexture(shader, "uCurrent", current, 0);
        BindTexture(shader, "uHistory", read.Color, 1);
        BindTexture(shader, "uVelocity", velocity, 2);
        BindTexture(shader, "uDepth", depth, 3);

        shader.SetVector2("uTexelSize", new Vector2(1.0f / current.Width, 1.0f / current.Height));
        shader.SetFloat("uCurrentWeight", CurrentWeight);
        shader.SetInt("uHistoryValid", _historyValid ? 1 : 0);
        shader.SetInt("uRectify", (int)Rectify);
        shader.SetFloat("uVarianceGamma", VarianceGamma);

        // **本物の解決**。書いたものが次のフレームの履歴になる。
        // 揺れの素(Current)だけはここで出す——書くのは今の絵そのものなので、履歴として使っても壊れない。
        write.Bind();
        shader.SetInt("uDebug", DebugView == TaaDebugView.Current ? 1 : 0);
        DrawFullscreen();

        _timer.End();

        Texture output = write.Color;
        PassCount = 1;

        // **速度と「疑った量」の表示は、別の1枚に描く**。
        //
        // 履歴そのものに描くと、次のフレームは「速度の色」を履歴として読むことになる。
        // 疑いを切っていると、表示を通常に戻したあとも速度の色が 1 割ずつしか抜けず、数十フレーム残る。
        // 表示用の1枚は、表示を使ったときにだけ作る。
        if (DebugView is TaaDebugView.Velocity or TaaDebugView.Rectified)
        {
            _debugTarget ??= new Framebuffer(_gl, current.Width, current.Height, RenderTargetFormat.Rgba16F, depth: false);
            _debugTarget.Resize(current.Width, current.Height);
            _debugTarget.Bind();

            shader.SetInt("uDebug", (int)DebugView);
            DrawFullscreen();

            output = _debugTarget.Color;
            PassCount = 2;
        }

        _readA = !_readA;
        _historyValid = true;

        return output;
    }

    /// <summary>直前の <see cref="Resolve"/> で走らせた全画面パスの数(表示を出すと 2)。</summary>
    public int PassCount { get; private set; }

    private void DrawFullscreen()
    {
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

    /// <summary>画面の大きさが変わったら履歴を作り直す(作ってあれば)。**中身は捨てる**。</summary>
    public void Resize(int width, int height)
    {
        if (_historyA is null)
        {
            return;
        }

        _historyA.Resize(width, height);
        _historyB!.Resize(width, height);
        Reset();
    }

    /// <summary>シェーダを読み直す(F5)。</summary>
    public void ReloadShader() => _resources.GetShader(_shader).TryReload();

    private void EnsureHistory(int width, int height)
    {
        if (_historyA is null)
        {
            _historyA = new Framebuffer(_gl, width, height, RenderTargetFormat.Rgba16F, depth: false);
            _historyB = new Framebuffer(_gl, width, height, RenderTargetFormat.Rgba16F, depth: false);
            _historyValid = false;
            return;
        }

        if (_historyA.Width != width || _historyA.Height != height)
        {
            Resize(width, height);
        }
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

        _historyA?.Dispose();
        _historyB?.Dispose();
        _debugTarget?.Dispose();
        _timer.Dispose();

        _gl.DeleteVertexArray(_emptyVao);
        _emptyVao = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
