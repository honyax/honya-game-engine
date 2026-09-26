using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace GaussianSplatting;

/// <summary>
/// GPU の入り口。<b>見えない窓の上に OpenGL 4.3 のコンテキストを作る</b>だけのクラス。Day 61 の <c>GpuDevice</c> と同じ作り。
///
/// <para>
/// 絵は GPU に描かせるが、画面へ出すのは WinForms の窓(<see cref="ViewerWindow"/>)にする。
/// 描いた絵を読み戻すので、1フレームに数 ms 余計にかかるが、代わりに次の2つが素直になる。
/// </para>
/// <list type="bullet">
/// <item>HUD を GDI+ の文字で重ねられる(GL で文字を描く仕組みを、今日のために作らなくて済む)</item>
/// <item>描いた画素の値を CPU で読めるので、右クリックで CPU が重ね直した色と<b>数で</b>突き合わせられる(自己チェック9)</item>
/// </list>
/// <para>
/// 4.3 を求めるのは、楕円体のデータを <b>SSBO</b>(シェーダから配列として読めるバッファ)で渡すため。
/// 頂点属性で渡すと、1個に 12 + 48 個の float を属性の口に割り振ることになり、口が足りない。
/// </para>
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private readonly IWindow _window;

    private GpuDevice(IWindow window, GL gl)
    {
        _window = window;
        Gl = gl;
        Renderer = gl.GetStringS(StringName.Renderer) ?? "?";
        Version = gl.GetStringS(StringName.Version) ?? "?";
    }

    public GL Gl { get; }

    public string Renderer { get; }

    public string Version { get; }

    /// <summary>
    /// GPU を用意する。使えない環境では <c>null</c> を返し、理由を <paramref name="message"/> に入れる。
    /// Day 61 と違って CPU 版の描き方は無いので、使えなければ HUD に理由を出すだけになる。
    /// </summary>
    public static GpuDevice? TryCreate(out string message)
    {
        try
        {
            var options = WindowOptions.Default with
            {
                Size = new Silk.NET.Maths.Vector2D<int>(16, 16),

                // 窓は作るが一度も見せない(Day 61 と同じ)。
                IsVisible = false,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 3)),
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

    public void Dispose()
    {
        Gl.Dispose();
        _window.Dispose();
    }
}
