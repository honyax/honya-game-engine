using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>クラスターをどう見せるか(「Forward+」の F3)。</summary>
internal enum ClusterView
{
    /// <summary>普通に光を当てた絵。</summary>
    None,

    /// <summary>
    /// **1画素が回す光の数**(ヒートマップ)。黒 = 0、青 = 1、緑 = 4、黄 = 16、赤 = 64 以上(4倍ごとに1段)。
    /// Day 52 の「光の重なり」(ディファードの代償)と並べて見る、<b>Forward+ の代償そのもの</b>。
    /// </summary>
    LightCount,

    /// <summary>
    /// **切り口**。切り身ごとに色を変え、タイルは市松で明暗を付ける。
    /// 奥ほど色の帯が広がるのが、等比に切っている姿。
    /// </summary>
    Slices,

    /// <summary>
    /// 生の値(**自己チェック専用**。メニューでは回らない)。
    /// R・G・B にタイルの x・y と切り身、A に光の数をそのまま入れる。
    /// 半精度でも 2048 までの整数は正確に入るので、読み返して CPU の答えと突き合わせられる。
    /// </summary>
    Raw,
}

/// <summary>
/// **Forward+ の GPU 側**(Day 53)。<see cref="ClusterGrid"/> が CPU で作った一覧を
/// テクスチャバッファ3本に詰めて、<c>textured.frag</c> に刺す。
///
/// <list type="table">
/// <item><term>11: 升目(RG32UI)</term><description>クラスターごとの (始まり, 数)。960x640・64 画素・16 枚で 2400 個 = 19KB</description></item>
/// <item><term>12: 番号(R32UI)</term><description>光の番号の並び。升目がここを指す</description></item>
/// <item><term>13: 光(RGBA32F)</term><description>光1個につき2テクセル(位置 + 届く距離 / 色)。1024 個で 32KB</description></item>
/// </list>
///
/// <para>
/// <b>持つのは「光の一覧」だけ</b>で、合わせて数十KB。
/// Day 52 の G-Buffer(960x640 で 15.8MB)と比べると、Forward+ が何を払わずに済ませているかが分かる——
/// 表面を中間データに書き出さないので、帯域をほとんど食わない。
/// 代わりに払うのは<b>CPU の振り分け</b>(<see cref="AssignMilliseconds"/>)で、
/// これを GPU のコンピュートシェーダへ移すのが Day 57 になる。
/// </para>
///
/// <para>
/// <b>シーンを知らない</b>のは <see cref="DeferredLighting"/> と同じ。
/// 光の並びとカメラを受け取って詰めるだけで、どの光を効かせるかは外が決める。
/// </para>
/// </summary>
internal sealed class ClusteredLighting : IDisposable
{
    /// <summary>
    /// ユニットの番号。**0〜10 は埋まっている**(0〜4 マテリアル / 5 影 / 6 高さ / 7〜9 IBL / 10 SSAO)。
    /// OpenGL 3.3 が画素シェーダに保証するユニットは 16 個なので、ここで 14 個目まで使うことになる。
    /// </summary>
    public const int ClusterUnit = 11;

    public const int IndexUnit = 12;

    public const int LightUnit = 13;

    /// <summary>光1個が使うテクセルの数。<c>textured.frag</c> の <c>light * 2</c> と同じ約束。</summary>
    public const int TexelsPerLight = 2;

    private readonly BufferTexture _clusters;
    private readonly BufferTexture _indices;
    private readonly BufferTexture _lights;

    /// <summary>光を詰める器。毎フレーム作り直さないよう持っておく。</summary>
    private Vector4[] _lightTexels = new Vector4[TexelsPerLight * 1024];

    private bool _disposed;

    public ClusteredLighting(GL gl)
    {
        // **形式とサンプラの型は対**(BufferTexture のコメント)。
        // RG32UI / R32UI は usamplerBuffer、RGBA32F は samplerBuffer で読む。
        _clusters = new BufferTexture(gl, SizedInternalFormat.RG32ui, 8);
        _indices = new BufferTexture(gl, SizedInternalFormat.R32ui, 4);
        _lights = new BufferTexture(gl, SizedInternalFormat.Rgba32f, 16);

        MaxTexels = BufferTexture.MaxTexels(gl);
        Grid.IndexLimit = MaxTexels;
    }

    /// <summary>升目と、CPU で振り分けた結果。</summary>
    public ClusterGrid Grid { get; } = new();

    /// <summary>タイル1枚の大きさ [画素](「Forward+」の F5)。次の <see cref="Build"/> から効く。</summary>
    public int TileSize { get; set; } = ClusterGrid.DefaultTileSize;

    /// <summary>奥行きの切り身の数(「Forward+」の F4)。**1 でタイルだけ**。次の <see cref="Build"/> から効く。</summary>
    public int SliceCount { get; set; } = ClusterGrid.DefaultSliceCount;

    /// <summary>クラスターをどう見せるか。</summary>
    public ClusterView View { get; set; } = ClusterView.None;

    /// <summary>
    /// **振り分けを止める**(「Forward+」の F6)。止めたままカメラを回すと、
    /// 光が<b>タイルの形に四角く切れる</b>——升目がカメラに貼り付いていることが目に見える。
    /// </summary>
    public bool Frozen { get; set; }

    /// <summary>このドライバのテクスチャバッファの上限(テクセル)。</summary>
    public int MaxTexels { get; }

    /// <summary>CPU の振り分けにかかった時間(最後の1回)。</summary>
    public double AssignMilliseconds { get; private set; }

    /// <summary>テクスチャバッファへ詰めるのにかかった時間(CPU が命令を積んだ時間)。</summary>
    public double UploadMilliseconds { get; private set; }

    public BufferTexture ClusterBuffer => _clusters;

    public BufferTexture IndexBuffer => _indices;

    public BufferTexture LightBuffer => _lights;

    /// <summary>3本が使っているバイト数。</summary>
    public long ByteSize => _clusters.ByteSize + _indices.ByteSize + _lights.ByteSize;

    /// <summary>
    /// **振り分けて、詰める**。本描画の前に1フレーム1回(カメラが決まったあと)。
    /// </summary>
    public void Build(Camera camera, int width, int height, ReadOnlySpan<PointLight> lights)
    {
        if (Frozen)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        Grid.Configure(width, height, TileSize, SliceCount, camera);
        Grid.Assign(camera.ViewMatrix, lights);

        AssignMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        stopwatch.Restart();

        // --- 光そのもの ---
        //
        // **全部の光を送る**(振り分けで1つも選ばれなかった光も)。番号の並びが光の番号で指すので、
        // 詰め直すと番号を振り直すことになり、2本の整合を取る仕事が増える。1024 個で 32KB なので気にしない。
        int texels = lights.Length * TexelsPerLight;
        if (_lightTexels.Length < texels)
        {
            Array.Resize(ref _lightTexels, Math.Max(texels, _lightTexels.Length * 2));
        }

        for (int i = 0; i < lights.Length; i++)
        {
            _lightTexels[i * TexelsPerLight] = lights[i].PositionAndRadius;
            _lightTexels[(i * TexelsPerLight) + 1] = new Vector4(lights[i].Color, 0.0f);
        }

        _lights.Upload<Vector4>(_lightTexels.AsSpan(0, texels));
        _clusters.Upload(Grid.Grid);
        _indices.Upload(Grid.Indices);

        UploadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// **3本を刺して、升目の決め方を送る**。
    ///
    /// <para>
    /// <b>Forward+ で描かないときも毎回呼ぶこと</b>。サンプラの uniform は送らなければ 0 番を指し、
    /// 0 番の <c>uTexture</c>(sampler2D)と<b>型の違うサンプラが同じユニットを指す</b>ことになる——
    /// GL はそのドローコールをエラーで捨てる(Day 52 の DeferredLighting のコメントと同じ罠)。
    /// シェーダを F5 で読み直すと uniform は全部 0 に戻るので、1回送れば済むものでもない。
    /// </para>
    /// </summary>
    public void Apply(Shader shader)
    {
        _clusters.Bind(TextureUnit.Texture0 + ClusterUnit);
        shader.SetInt("uClusters", ClusterUnit);

        _indices.Bind(TextureUnit.Texture0 + IndexUnit);
        shader.SetInt("uLightIndices", IndexUnit);

        _lights.Bind(TextureUnit.Texture0 + LightUnit);
        shader.SetInt("uLightData", LightUnit);

        shader.SetInt3("uClusterCount", Grid.TilesX, Grid.TilesY, Grid.SliceCount);
        shader.SetFloat("uClusterTileSize", Grid.TileSize);
        shader.SetFloat("uClusterNear", Grid.Near);
        shader.SetFloat("uClusterSliceScale", Grid.SliceScale);
        shader.SetInt("uClusterView", (int)View);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clusters.Dispose();
        _indices.Dispose();
        _lights.Dispose();
    }
}
