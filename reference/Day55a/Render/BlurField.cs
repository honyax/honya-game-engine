using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **このフレームのカメラの数字**(Day 55)。被写界深度とモーションブラーが、深度を奥行きに戻し、
/// ぼけの大きさを決めるのに使う。
///
/// <para>
/// <see cref="Camera"/> そのものを渡さないのは、後処理がシーンを知らないため——
/// <see cref="TemporalAA"/> が <see cref="Camera"/> を知らないのと同じ線で、
/// 自己チェックは数字を直接並べて作り物の絵を流せる。
/// </para>
/// </summary>
/// <param name="Near">近クリップ面 [m]。</param>
/// <param name="Far">遠クリップ面 [m]。</param>
/// <param name="Perspective">透視投影か。平行投影なら深度はそのまま奥行きに比例する。</param>
/// <param name="FieldOfView">垂直画角(ラジアン)。レンズの焦点距離はここから決まる。</param>
/// <param name="FocusDistance">ピントを合わせる奥行き [m]。</param>
/// <param name="FrameSeconds">前のフレームからの時間 [秒]。速度バッファは「1フレームぶんの動き」なので、シャッターを時間で決めるときに要る。</param>
internal readonly record struct CameraFrame(
    float Near, float Far, bool Perspective, float FieldOfView, float FocusDistance, float FrameSeconds)
{
    /// <summary>カメラから数字だけを抜く。**ここから先は誰も <see cref="Camera"/> を知らない**。</summary>
    public static CameraFrame From(Camera camera, float focusDistance, float frameSeconds) => new(
        camera.NearPlane,
        camera.FarPlane,
        camera.Mode == ProjectionMode.Perspective,
        camera.FieldOfView,
        focusDistance,
        frameSeconds);
}

/// <summary>
/// **ぼけの地図**(Day 55)。被写界深度とモーションブラーが共通で読む下ごしらえ。
///
/// <para>
/// 被写界深度もモーションブラーも、本来は<b>「1つの点が、まわりへ光を配る」</b>処理(scatter)。
/// ピントの外れた点は丸く(錯乱円)、動いている点は線に(尾)広がる。
/// ところが画素シェーダは「自分の画素の色を決める」ことしかできないので、裏返して
/// <b>「まわりの誰が、自分のところまで光を配りに来るか」を集める</b>(gather)形で書く。
/// </para>
///
/// <para>
/// 集めるには「どこまで探せばよいか」が要る。自分がぼけていなくても、隣の大きくぼけた物が
/// 自分の上まで光を配りに来ることがある(手前の物のボケ、動く物の尾)。
/// 探す範囲を画面全体の最大に固定すると、ほとんどぼけていない画素まで遠くを探すことになり、
/// 決まった数の標本がまばらに散ってしまう。そこで<b>画面を 16 画素の升目に切り、
/// 升目ごとに「近所でいちばん大きく配っている物」を先に記録しておく</b>(McGuire 2012 の TileMax / NeighborMax)。
/// Day 53 で光を升目に振り分けたのと同じ発想で、今度は升目に「ぼけの届く範囲」を振り分ける。
/// </para>
///
/// <list type="number">
/// <item><b>地図</b>(原寸 RG16F)… 深度を奥行き [m] に戻し、錯乱円の半径 [画素] を出す。
/// <b>奥行きに戻す式は Day 55 ではここの1か所だけ</b>で、被写界深度もモーションブラーもこれを読む</item>
/// <item><b>升目の横</b>(横 1/16)… 横 16 画素の中でいちばん速い速度と、いちばん手前の錯乱円</item>
/// <item><b>升目の縦</b>(1/16)… それを縦 16 画素ぶんまとめて、16x16 の升目の最大にする</item>
/// <item><b>近所</b>(1/16)… 3x3 の升目の中での最大。これで「半径 16 画素以内から届くもの」は全部拾える</item>
/// </list>
///
/// <para>
/// 升目の最大を横と縦の2段に分けたのは速さのため(計画書「検証の途中で分かったこと」)。
/// 1段で 16x16 をなめると、升目が 2400 個しかないので GPU が並列に走らせる画素が足りず、手が余る。
/// </para>
/// </summary>
internal sealed class BlurField : IDisposable
{
    /// <summary>升目の大きさ [画素]。</summary>
    public const int TileSize = 16;

    /// <summary>
    /// **ぼけの半径の上限** [画素]。錯乱円の半径も、尾の半分の長さもここで切る。
    ///
    /// <para>
    /// 升目の大きさと同じにしてあるのが肝。3x3 の升目の近所は、自分の升目から<b>どの向きにも 16 画素以上</b>を含むので、
    /// 半径 16 画素までのぼけなら、自分のところまで届く物を必ず近所の最大に数えている。
    /// これより大きくすると、近所の外から届くものを取りこぼして、ぼけが升目の形に切れる。
    /// </para>
    /// </summary>
    public const float MaxRadius = TileSize;

    /// <summary>1回の <see cref="Build"/> で走る全画面パスの数(地図・升目の横・升目の縦・近所)。</summary>
    public const int PassCount = 4;

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _fieldShader;
    private readonly Handle<Shader> _tileShader;
    private readonly Handle<Shader> _maxShader;
    private readonly GpuTimer _timer;

    /// <summary>
    /// 奥行き [m] と錯乱円 [画素] の地図(原寸 RG16F)。**要るまで作らない**(被写界深度かモーションブラーを入れたとき)。
    ///
    /// <para>
    /// 深度バッファの値(0〜1)をそのまま 16F に入れないのは、<b>奥がつぶれる</b>ため。
    /// 透視投影の深度は 1/z に比例して並ぶ(Day 7)ので、5m 先から 100m 先までが 0.98〜1.0 に押し込まれている。
    /// 16F は 1.0 のそばで 0.0005 刻みしか持てず、そこだけで何十 m ぶんが同じ値になる。奥行きに戻してから入れれば、
    /// 10m 先で 8mm 刻みが残る。
    /// </para>
    /// </summary>
    private Framebuffer? _field;

    /// <summary>横 16 画素ごとの最大(横は 1/16、縦は原寸)。並びは <see cref="_tiles"/> と同じ。</summary>
    private Framebuffer? _strips;

    /// <summary>升目ごとの最大(xy: 速度 [画素/フレーム]、z: いちばん手前の錯乱円、w: いちばん大きい錯乱円)。</summary>
    private Framebuffer? _tiles;

    /// <summary>近所 3x3 の升目での最大。**被写界深度とモーションブラーが読むのはこちら**。</summary>
    private Framebuffer? _neighbors;

    private uint _emptyVao;
    private bool _disposed;

    public BlurField(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        string fullscreen = Path.Combine(shaderDirectory, "fullscreen.vert");
        _fieldShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "blur-field.frag"));
        _tileShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "blur-tiles.frag"));
        _maxShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "blur-max.frag"));

        _emptyVao = gl.GenVertexArray();
        _timer = new GpuTimer(gl);
    }

    /// <summary>奥行きと錯乱円の地図(x: 奥行き [m]、y: 錯乱円の半径 [画素]。手前が負、奥が正)。まだ作っていなければ null。</summary>
    public Texture? Field => _field?.Color;

    /// <summary>近所の升目の最大。まだ作っていなければ null。</summary>
    public Texture? Tiles => _neighbors?.Color;

    /// <summary>升目の数(横)。</summary>
    public int TilesX => _tiles?.Width ?? 0;

    /// <summary>升目の数(縦)。</summary>
    public int TilesY => _tiles?.Height ?? 0;

    /// <summary>4パスぶんの GPU の時間(ms。<see cref="GpuTimer"/>)。</summary>
    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>地図と升目3枚の VRAM。作る前は 0。</summary>
    public long ByteSize =>
        (_field?.ByteSize ?? 0) + (_strips?.ByteSize ?? 0) + (_tiles?.ByteSize ?? 0) + (_neighbors?.ByteSize ?? 0);

    /// <summary>
    /// **地図と升目を作る**。後処理が被写界深度・モーションブラーの前に1回呼ぶ。
    /// </summary>
    /// <param name="depth">シーンの深度のコピー(<see cref="PostProcess.SceneDepth"/>)。</param>
    /// <param name="velocity">速度バッファ(<see cref="MotionVectors.Velocity"/>)。モーションブラーを掛けないなら null。</param>
    /// <param name="camera">このフレームのカメラの数字。</param>
    /// <param name="lensScale">レンズの係数 L [画素・m](<see cref="DepthOfField.LensScale(CameraFrame, int)"/>)。被写界深度を掛けないなら 0。</param>
    public void Build(Texture depth, Texture? velocity, CameraFrame camera, float lensScale)
    {
        int width = depth.Width;
        int height = depth.Height;

        // 端の升目は画面からはみ出す(960 / 16 = 60 はちょうどだが、画面の大きさはいつも 16 の倍数とは限らない)。
        int tilesX = (width + TileSize - 1) / TileSize;
        int tilesY = (height + TileSize - 1) / TileSize;

        _field = Ensure(_field, width, height, RenderTargetFormat.Rg16F);
        _strips = Ensure(_strips, tilesX, height, RenderTargetFormat.Rgba16F);
        _tiles = Ensure(_tiles, tilesX, tilesY, RenderTargetFormat.Rgba16F);
        _neighbors = Ensure(_neighbors, tilesX, tilesY, RenderTargetFormat.Rgba16F);

        _timer.Begin();

        // --- 1. 地図: 深度 → 奥行き → 錯乱円 ---
        _field.Bind();
        Shader field = _resources.GetShader(_fieldShader);
        field.Use();
        BindTexture(field, "uDepth", depth, 0);
        field.SetVector2("uNearFar", new Vector2(camera.Near, camera.Far));
        field.SetInt("uPerspective", camera.Perspective ? 1 : 0);
        field.SetFloat("uLensScale", lensScale);
        field.SetFloat("uFocusDistance", MathF.Max(camera.FocusDistance, camera.Near));
        field.SetFloat("uMaxRadius", MaxRadius);
        DrawFullscreen();

        // --- 2. 升目の横: 横 16 画素の最大 ---
        _strips.Bind();
        Shader tiles = _resources.GetShader(_tileShader);
        tiles.Use();
        BindTexture(tiles, "uField", _field.Color, 0);

        // 速度が無い(被写界深度だけ)なら読まない。見ない引数に何かを挿しておく必要は無いが、
        // 「見ない」を uniform で言っておかないと、前のパスが 1 番に残したテクスチャを速度として読む。
        if (velocity is not null)
        {
            BindTexture(tiles, "uVelocity", velocity, 1);
        }

        tiles.SetInt("uHasVelocity", velocity is null ? 0 : 1);
        tiles.SetInt("uTileSize", TileSize);
        DrawFullscreen();

        // --- 3. 升目の縦: 横の段を縦 16 画素ぶんまとめる ---
        Shader max = _resources.GetShader(_maxShader);
        max.Use();
        DrawMax(max, _strips.Color, _tiles, new(1, TileSize), new(0, 0), new(1, TileSize));

        // --- 4. 近所: 3x3 の升目の最大 ---
        DrawMax(max, _tiles.Color, _neighbors, new(1, 1), new(-1, -1), new(3, 3));

        _timer.End();
    }

    /// <summary>シェーダを読み直す(F5)。</summary>
    public void ReloadShaders()
    {
        _resources.GetShader(_fieldShader).TryReload();
        _resources.GetShader(_tileShader).TryReload();
        _resources.GetShader(_maxShader).TryReload();
    }

    /// <summary>
    /// <c>blur-max.frag</c> で「窓の中の最大」を1回取る。**升目の縦と近所の2回で、同じシェーダを使い回す**。
    /// </summary>
    /// <param name="stride">出力の1画素ぶんで、入力を何画素進むか。</param>
    /// <param name="offset">窓の始まり(出力の位置 × stride からの相対)。</param>
    /// <param name="count">窓の大きさ。</param>
    private void DrawMax(Shader shader, Texture source, Framebuffer target, Int2 stride, Int2 offset, Int2 count)
    {
        target.Bind();
        BindTexture(shader, "uSource", source, 0);
        shader.SetInt2("uStride", stride.X, stride.Y);
        shader.SetInt2("uOffset", offset.X, offset.Y);
        shader.SetInt2("uCount", count.X, count.Y);
        DrawFullscreen();
    }

    /// <summary>整数の2つ組(GLSL の ivec2 に送る)。</summary>
    private readonly record struct Int2(int X, int Y);

    /// <summary>
    /// 要るときに作り、大きさが変わっていれば作り直す。**画面の大きさの変化を外から知らせなくてよい**
    /// (<see cref="MotionVectors"/> の速度バッファと同じ置き方)。
    /// </summary>
    private Framebuffer Ensure(Framebuffer? target, int width, int height, RenderTargetFormat format)
    {
        if (target is null)
        {
            return new Framebuffer(_gl, width, height, format, depth: false);
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

        _field?.Dispose();
        _strips?.Dispose();
        _tiles?.Dispose();
        _neighbors?.Dispose();
        _timer.Dispose();

        _gl.DeleteVertexArray(_emptyVao);
        _emptyVao = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
