using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace CpuRayTracer;

/// <summary>
/// GPU の入り口。<b>見えない窓の上に OpenGL 4.3 のコンテキストを作る</b>だけのクラス(今日の要点1)。
///
/// <para>
/// 今日やりたいのは「コンピュートシェーダを走らせて、結果を配列で受け取る」ことだけで、
/// <b>GPU に画面を描かせたいわけではない</b>。画面へ出す仕事は Day 1 から使っている
/// WinForms + LockBits(<see cref="ViewerWindow"/>)のままにしておきたい——そうすれば
/// CPU 版と GPU 版が<b>まったく同じ経路で同じ int[] を画面に出す</b>ので、
/// G キーで切り替えたときの差が「計算した場所」だけになる。
/// </para>
/// <para>
/// ところが OpenGL のコンテキストは<b>必ず何かの窓(に紐づく描画先)の上にしか作れない</b>。
/// GPGPU だけしたいのに窓が要る、というのは OpenGL の歴史的な都合で、
/// Vulkan や CUDA、D3D12 にはこの制約が無い(Day 62 で Vulkan を触ると、そこが素直になっているのが分かる)。
/// そこで<b>16x16 の見えない窓</b>を1つ作り、そのコンテキストだけをもらう。窓は最後まで一度も表示しない。
/// </para>
/// <para>
/// Day 11〜13 では、この「窓を作って wgl でコンテキストを作る儀式」を Win32 の P/Invoke で自分で書いた。
/// 何が起きているかは一度やってあるので、ここは Silk.NET に任せる(Phase 3 以降と同じ方針)。
/// </para>
///
/// <para>
/// <b>スレッドの決まりごと</b>: OpenGL のコンテキストは<b>作ったスレッドに貼り付く</b>。
/// 今日は UI スレッドで作り、UI スレッドからだけ触る。
/// CPU 版が別スレッドで描いていたのと対照的だが、<b>GPU の仕事は投げたら返ってくるまで CPU が空く</b>ので、
/// 1パスが数 ms の今日はこれで足りる(要点6)。
/// </para>
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private readonly IWindow _window;

    private GpuDevice(IWindow window, GL gl)
    {
        _window = window;
        Gl = gl;

        Vendor = gl.GetStringS(StringName.Vendor) ?? "?";
        Renderer = gl.GetStringS(StringName.Renderer) ?? "?";
        Version = gl.GetStringS(StringName.Version) ?? "?";

        gl.GetInteger(GLEnum.MaxComputeWorkGroupInvocations, out int invocations);
        gl.GetInteger(GLEnum.MaxComputeSharedMemorySize, out int shared);
        gl.GetInteger(GLEnum.MaxShaderStorageBufferBindings, out int storageBindings);
        MaxWorkGroupInvocations = invocations;
        MaxSharedMemoryBytes = shared;
        MaxStorageBindings = storageBindings;
    }

    public GL Gl { get; }

    public string Vendor { get; }

    public string Renderer { get; }

    public string Version { get; }

    /// <summary>1ワークグループに入れられる呼び出しの数(仕様の保証値は 1024)。</summary>
    public int MaxWorkGroupInvocations { get; }

    /// <summary>1ワークグループが使える共有メモリのバイト数(保証値は 32KB)。</summary>
    public int MaxSharedMemoryBytes { get; }

    /// <summary>SSBO を挿せる口の数(保証値は 8)。今日は 8 本ちょうど使う(<see cref="GpuScene"/>)。</summary>
    public int MaxStorageBindings { get; }

    /// <summary>
    /// GPU を用意する。使えない環境(コンピュートシェーダの無い古い GPU、リモートデスクトップなど)では
    /// <c>null</c> を返し、理由を <paramref name="message"/> に入れる。<b>例外で落とさない</b>——
    /// GPU が無くても CPU 版は動くので、そちらへ倒せばよい。
    /// </summary>
    public static GpuDevice? TryCreate(out string message)
    {
        try
        {
            var options = WindowOptions.Default with
            {
                Size = new Silk.NET.Maths.Vector2D<int>(16, 16),

                // ここが肝。窓は作るが<b>一度も見せない</b>。
                IsVisible = false,

                // コンピュートシェーダは OpenGL 4.3 から。**今日の最低条件がこの1行**。
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 3)),

                // 画面へ出さないので、フレームの入れ替えも待ち合わせも要らない。
                ShouldSwapAutomatically = false,
                IsEventDriven = false,
            };

            IWindow window = Window.Create(options);
            window.Initialize();

            GL gl = GL.GetApi(window);
            var device = new GpuDevice(window, gl);
            message = $"{device.Renderer} / OpenGL {device.Version}";
            return device;
        }
        catch (Exception e)
        {
            message = $"GPU を用意できなかった: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// 後始末。<b>見えない窓のメッセージは一度も汲んでいない</b>(<c>IWindow.DoEvents</c> を呼んでいない)。
    ///
    /// <para>
    /// 見せていない窓なので誰も触らず、汲まなくても困らない。
    /// 「汲まないと OS に応答なしと見なされて遅くなるのでは」と疑って測ってみたが、
    /// 15 秒走らせて 377 フレーム対 370 フレームで<b>差が無かった</b>ので、呼ばないことにした。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        Gl.Dispose();
        _window.Dispose();
    }
}
