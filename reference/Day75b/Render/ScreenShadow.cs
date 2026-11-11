using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// 画面の影(Day 75b)。<see cref="DebugView"/> の値。
/// </summary>
internal enum ScreenShadowView
{
    None,

    /// <summary>R = 世界の影。シーンのライティングパスが読む。</summary>
    World,

    /// <summary>G = キャラの影。MToon が読む。</summary>
    Character,
}

/// <summary>
/// **画面の影**(Day 75b)。「この画素は太陽から見えているか」を、画面の画素ごとに1回だけ求めて RG8 の1枚に書く。
///
/// <para>
/// Day 52 までの影は、照らす人(textured.frag・deferred.frag・mtoon.frag)がそれぞれシャドウマップを引いていた。
/// 今日はシャドウマップが2枚(世界と、キャラ専用)になり、<b>読む人によって欲しい答えが違う</b>。
/// <list type="bullet">
/// <item><b>R = 世界の影</b> … 世界のシャドウマップをそのまま引いた値。Day 75a までのライティングパスと同じ答えで、シーンはこれを読む</item>
/// <item><b>G = キャラの影</b> … キャラ専用のシャドウマップ(細かい)× R。MToon はこれを読む</item>
/// </list>
/// 2つの答えを、同じ1枚の2チャンネルに並べて置く。照らす人は自分のチャンネルを1回引くだけでよい。
/// 手本(アークナイツ:エンドフィールド)の画面の影も同じ R8G8 で、R をシーンのライティング3本、G をキャラの体・顔・髪が読む。
/// </para>
///
/// <para>
/// <b>入力は G-Buffer の2枚目(法線と距離)だけ</b>。位置は距離から戻す(deferred.frag と同じ式)。
/// だから<b>キャラの形が G-Buffer に入っていないと(Day 75a の F2)、キャラの画素の答えが作れない</b>——
/// その画素には後ろの壁の距離が入っていて、壁の影の値が出てしまう。
/// </para>
///
/// <para>
/// このクラスはシャドウマップと G-Buffer しか知らない。キャラが何者か(VRM か、MToon か)は知らず、
/// 「2枚目のシャドウマップがあるか」と「その箱がどこか」を受け取るだけ。
/// </para>
/// </summary>
internal sealed class ScreenShadow : IDisposable
{
    /// <summary>
    /// 読む側に刺すテクスチャユニット。0〜14 と 16〜18 は埋まっている(マテリアル・影・IBL・SSAO・クラスター・プローブ・MToon・散乱のランプ)。
    /// </summary>
    public const int Unit = 15;

    /// <summary>キャラ専用のシャドウマップを、計算の間だけ刺すユニット。読む側には刺さない。</summary>
    private const int CharacterMapUnit = 6;

    private readonly GL _gl;
    private readonly RenderResources _resources;

    /// <summary>書き込み先。RG8・画面と同じ大きさ・深度なし(全画面の三角形を1枚描くだけ)。</summary>
    private readonly Framebuffer _target;

    private readonly Handle<Shader> _shader;
    private readonly Handle<Shader> _viewShader;

    private uint _emptyVao;
    private bool _disposed;

    /// <summary>最後に作ったときの元(<see cref="Ready"/>)。</summary>
    private GBuffer? _gbuffer;
    private ShadowMap? _world;
    private int _gbufferGeneration;
    private int _worldGeneration;

    public ScreenShadow(GL gl, RenderResources resources, string shaderDirectory, int width, int height)
    {
        _gl = gl;
        _resources = resources;

        // **画面と同じ大きさ**。手本は遮蔽の計算を半分・4分の1で回して最後に原寸で合わせるが、
        // ここはシャドウマップを引くだけなので原寸で1回にする(半分にすると、キャラの輪郭で隣の壁の値が混ざる)。
        _target = new Framebuffer(gl, Math.Max(1, width), Math.Max(1, height), RenderTargetFormat.Rg8, depth: false);

        string fullscreen = Path.Combine(shaderDirectory, "fullscreen.vert");
        _shader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "screen-shadow.frag"));
        _viewShader = resources.LoadShader(fullscreen, Path.Combine(shaderDirectory, "screen-shadow-view.frag"));

        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>画面の影を作るか(「キャラ専用の影」の F2)。切ると Day 75a の形(照らす人がそれぞれシャドウマップを引く)。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// <b>いまの G-Buffer とシャドウマップから作った画面の影があるか</b>。照らす人はこれを見て、画面の影を読むか自分で引くかを決める。
    ///
    /// <para>
    /// 画面の影は G-Buffer と世界のシャドウマップの「写し」なので、<b>元のどちらかが描き直されたら古い</b>。
    /// 作ったときの2つの世代の番号(<see cref="GBuffer.Generation"/> と <see cref="ShadowMap.Generation"/>)を覚えておき、今の番号と比べる。
    /// 「フレームの頭で倒す」形にしなかったのは、ライティングパスを直接呼ぶ人(Day 52 の自己チェック)がいるから——
    /// その人は自分で G-Buffer とシャドウマップを描き直すので、番号が変わって、古い影を読まずに済む。
    /// </para>
    /// </summary>
    public bool Ready =>
        Enabled
        && _gbuffer is not null && _gbuffer.Generation == _gbufferGeneration
        && _world is not null && _world.Generation == _worldGeneration;

    /// <summary>最後に作った画面の影の G に、キャラ専用のシャドウマップが入っているか。入っていなければ G は R と同じ。</summary>
    public bool HasCharacter { get; private set; }

    /// <summary>全画面に出す(「キャラ専用の影」の F5)。</summary>
    public ScreenShadowView DebugView { get; set; } = ScreenShadowView.None;

    /// <summary>書き込み先。自己チェックが読み返す。</summary>
    public Framebuffer Target => _target;

    /// <summary>読む側に刺すテクスチャ。</summary>
    public Texture Result => _target.Color;

    public int Width => _target.Width;

    public int Height => _target.Height;

    /// <summary>VRAM の推定バイト数(RG8 なので 1画素 2B)。</summary>
    public long ByteSize => _target.ByteSize;

    /// <summary>
    /// **画面の影を作る**。G-Buffer を書き終え、ライティングパスより前に呼ぶ。
    ///
    /// <para>
    /// <paramref name="character"/> は、このフレームでキャラを焼いたキャラ専用のシャドウマップ。無ければ null(G は R と同じになる)。
    /// 世界のシャドウマップの uniform は <see cref="ShadowMap.Apply"/> がいつもの名前で送る(5 番)。キャラのほうはここで別の名前で送る。
    /// </para>
    /// </summary>
    public void Render(GBuffer gbuffer, Camera camera, ShadowMap world, ShadowMap? character)
    {
        bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        bool stencil = _gl.IsEnabled(EnableCap.StencilTest);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.StencilTest);

        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        _target.Bind();

        Shader shader = _resources.GetShader(_shader);
        shader.Use();

        // 位置を戻す材料(法線と距離・逆ビュー行列・射影の2つの数・画面の大きさ)。ライティングパスと同じものを同じ名前で。
        gbuffer.Apply(shader, camera);
        shader.SetVector2("uScreenSize", new Vector2(gbuffer.Width, gbuffer.Height));

        // **光の向きは世界のシャドウマップが覚えている**(Begin で決めた向き)。キャラの箱も同じ向きで作る。
        shader.SetVector3("uLightDirection", world.Direction);
        world.Apply(shader);

        HasCharacter = character is not null && world.Enabled;
        shader.SetInt("uCharacterShadowEnabled", HasCharacter ? 1 : 0);

        if (HasCharacter)
        {
            ShadowMap map = character!;
            shader.SetMatrix4("uCharacterMatrix", map.LightSpaceMatrix);
            shader.SetInt("uCharacterPcfRadius", map.PcfRadius);
            shader.SetFloat("uCharacterBias", map.DepthBias);
            shader.SetFloat("uCharacterSlopeBias", map.SlopeBias ? map.DepthBias * 6.0f : 0.0f);
            shader.SetVector2("uCharacterTexelSize", new Vector2(1.0f / map.Resolution, 1.0f / map.Resolution));
        }

        // 無いときもプレースホルダを刺す(ShadowMap.Apply と同じ用心)。
        ((HasCharacter ? character!.Target.Depth : null) ?? _resources.Placeholder).Bind(TextureUnit.Texture0 + CharacterMapUnit);
        shader.SetInt("uCharacterShadowMap", CharacterMapUnit);

        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);
        SetCap(EnableCap.DepthTest, depthTest);
        SetCap(EnableCap.Blend, blend);
        SetCap(EnableCap.StencilTest, stencil);

        _gbuffer = gbuffer;
        _world = world;
        _gbufferGeneration = gbuffer.Generation;
        _worldGeneration = world.Generation;
    }

    /// <summary>
    /// 読む側に刺す。<c>uScreenShadowEnabled</c> は「このフレームで作ったか」。作っていなければ、読む側は自分でシャドウマップを引く。
    /// <b>作っていないときもプレースホルダを刺す</b>(サンプラが空のユニットを指すと、環境によって未定義の値が返る)。
    /// </summary>
    public void Apply(Shader shader)
    {
        shader.SetInt("uScreenShadowEnabled", Ready ? 1 : 0);
        (Ready ? Result : _resources.Placeholder).Bind(TextureUnit.Texture0 + Unit);
        shader.SetInt("uScreenShadow", Unit);
    }

    /// <summary>
    /// R か G を白黒で全画面に出す(F5)。**後処理の外**で呼ぶこと(SSAO の表示と同じ。値そのものを見たいので露出を通さない)。
    /// 白 = 光が当たっている、黒 = 影。
    /// </summary>
    public void DrawDebug(int screenWidth, int screenHeight)
    {
        if (DebugView == ScreenShadowView.None || !Ready)
        {
            return;
        }

        Framebuffer.BindDefault(_gl, screenWidth, screenHeight);

        Shader shader = _resources.GetShader(_viewShader);
        shader.Use();
        shader.SetInt("uChannel", DebugView == ScreenShadowView.World ? 0 : 1);
        Result.Bind(TextureUnit.Texture0);
        shader.SetInt("uScreenShadow", 0);

        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);

        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        SetCap(EnableCap.DepthTest, depth);
        SetCap(EnableCap.Blend, blend);
    }

    /// <summary>ウィンドウの大きさが変わったら作り直す(G-Buffer と同じ大きさを保つ)。</summary>
    public void Resize(int width, int height) => _target.Resize(Math.Max(1, width), Math.Max(1, height));

    public void ReloadShaders()
    {
        _resources.GetShader(_shader).TryReload();
        _resources.GetShader(_viewShader).TryReload();
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
        _target.Dispose();

        if (_emptyVao != 0)
        {
            _gl.DeleteVertexArray(_emptyVao);
            _emptyVao = 0;
        }
    }
}
