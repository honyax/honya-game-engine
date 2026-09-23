using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **ライティングパス**(Day 52)。G-Buffer を読んで光を当てる。主役のもう半分。
///
/// <para>
/// <b>光の形 = 描く形</b>というのが、このクラスのいちばん大事な考え方になる。
/// 太陽(平行光源)はどこにでも当たるので<b>全画面の三角形1枚</b>——環境光と発光もここで足す。
/// Day 52b で足す点光源は半径の中にしか当たらないので、<b>その半径の球</b>を描くことになる。
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
    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _sunShader;

    /// <summary>全画面の三角形用の空 VAO(<see cref="PostProcess"/> と同じ手口)。</summary>
    private uint _emptyVao;

    private bool _disposed;

    public DeferredLighting(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        // **画素シェーダは deferred.frag 1本**。Day 52b で点光源の球(light-volume.vert)と
        // 組む2本目のプログラムができるが、画素シェーダは同じものを使い回す——
        // どちらも「G-Buffer を読んで光を当てる」という中身は同じで、
        // 分けると BRDF がもう1か所に増える。
        string fragment = Path.Combine(shaderDirectory, "deferred.frag");

        _sunShader = resources.LoadShader(Path.Combine(shaderDirectory, "fullscreen.vert"), fragment);
        _emptyVao = gl.GenVertexArray();
    }

    /// <summary>
    /// **光を当てる**。<see cref="PostProcess.Begin"/> のあと、シーンのバッファに描く。
    ///
    /// <para>
    /// 呼ぶ前に<b>G-Buffer の深度をシーンのバッファへ写しておく</b>こと
    /// (<see cref="Framebuffer.BlitDepthTo"/>)。あとから描く粒(Day 49)が壁の向こうへ透けないため、
    /// そして Day 52b の点光源の球がその深度と比べて塗る画素を選ぶため。
    /// </para>
    /// </summary>
    /// <param name="applyLighting">
    /// 太陽・IBL・影・SSAO の uniform を送る関数。**フォワードの本描画と同じものを渡す**。
    /// </param>
    public void Render(Camera camera, GBuffer gbuffer, Action<Shader> applyLighting)
    {
        // シーンが残していった GL の状態を覚えておく(Ssao.Compute と同じ作法)。
        bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        bool cull = _gl.IsEnabled(EnableCap.CullFace);

        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        // --- 太陽・環境光・発光(全画面1枚)---
        //
        // **ブレンドしない**。空の画素はシェーダが捨てる(discard)ので、
        // 先に描いてある空(Day 36 の DrawSkybox)はそのまま残る。
        Shader sun = _resources.GetShader(_sunShader);
        sun.Use();
        applyLighting(sun);
        gbuffer.Apply(sun, camera);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        // **借りた状態は返す**。深度の比べ方と書き込みは、このエンジンの既定(Less / 書く)へ。
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Less);
        _gl.CullFace(TriangleFace.Back);
        SetCap(EnableCap.DepthTest, depthTest);
        SetCap(EnableCap.Blend, blend);
        SetCap(EnableCap.CullFace, cull);
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);
    }

    public void ReloadShaders()
    {
        _resources.GetShader(_sunShader).TryReload();
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

        if (_emptyVao != 0)
        {
            _gl.DeleteVertexArray(_emptyVao);
            _emptyVao = 0;
        }

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
