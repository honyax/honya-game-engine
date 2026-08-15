using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace HonyaEngine;

/// <summary>
/// エントリポイント。
///
/// Day 14 では頂点バッファの作成もドローコールもこのファイルに直書きだった。
/// 今日それを <see cref="Mesh{TVertex}"/> / <see cref="Texture"/> / <see cref="Material"/> に分け、
/// Program に残るのは**「何を、どこに、どんな見た目で描くか」だけ**になる。
///
/// 抽象化が効いているかは、描画ループの短さで測れる。
/// 今日の <see cref="OnRender"/> は、メッシュが1枚でも100枚でもほとんど変わらない形になっている。
/// </summary>
internal static class Program
{
    private const int Width = 800;
    private const int Height = 600;

    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static IInputContext _input = null!;

    private static Shader _shader = null!;
    private static Texture _texture = null!;

    /// <summary>**1枚だけ**作って、2つのマテリアルで使い回す(要点2)。</summary>
    private static Mesh<Vertex> _quad = null!;

    private static Material _leftMaterial = null!;
    private static Material _rightMaterial = null!;

    private static float _angle;
    private static bool _paused;
    private static bool _wireframe;
    private static TextureFilter _filter = TextureFilter.Linear;
    private static TextureWrap _wrap = TextureWrap.Repeat;

    private static double _fpsElapsed;
    private static int _fpsFrames;
    private static double _fps;

    private static void Main()
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(Width, Height),
            Title = "Day15 - メッシュ/テクスチャ/マテリアル",
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.Default,
                new APIVersion(3, 3)),
            VSync = true,
            WindowBorder = WindowBorder.Fixed,
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Run();
        _window.Dispose();
    }

    private static void OnLoad()
    {
        _gl = GL.GetApi(_window);

        _input = _window.CreateInput();
        foreach (IKeyboard keyboard in _input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
        }

        Console.WriteLine($"GL_RENDERER : {_gl.GetStringS(StringName.Renderer)}");
        Console.WriteLine($"GL_VERSION  : {_gl.GetStringS(StringName.Version)}");
        Console.WriteLine();

        // --- シェーダ ---
        string shaderDirectory = ResolveDirectory("shaders");
        _shader = new Shader(
            _gl,
            Path.Combine(shaderDirectory, "textured.vert"),
            Path.Combine(shaderDirectory, "textured.frag"));

        // --- テクスチャ ---
        string texturePath = ResolveAssetPath("textures/uv-test.png");
        _texture = Texture.FromFile(_gl, texturePath);
        Console.WriteLine($"テクスチャ: {Path.GetFileName(texturePath)} ({_texture.Width}x{_texture.Height})");

        // --- メッシュ ---
        _quad = CreateQuad(_gl);

        // --- マテリアル ---
        // **同じシェーダ・同じテクスチャ・同じメッシュ**から、
        // 値を変えるだけで2種類の見た目を作る。これがマテリアルの存在意義。
        _leftMaterial = new Material(_shader)
        {
            MainTexture = _texture,
            Tint = Vector4.One,               // 素通し
            UvScale = Vector2.One,            // 等倍
        };

        _rightMaterial = new Material(_shader)
        {
            MainTexture = _texture,
            Tint = new Vector4(1.0f, 0.75f, 0.55f, 1.0f),   // 暖色に寄せる
            UvScale = new Vector2(2.0f, 2.0f),              // 縦横2回ずつ繰り返す
        };

        Console.WriteLine();
        Console.WriteLine("F:フィルタ R:ラップ W:ワイヤー V:VSync Space:停止 F5:シェーダ再読込 Esc:終了");
        Console.WriteLine();
    }

    /// <summary>
    /// 正方形を1枚作る。**4頂点 + 6インデックス**。
    ///
    /// インデックスを使わないと三角形2枚で6頂点必要になる。
    /// 共有される2頂点を1つにまとめられるのがインデックス描画の利点で、
    /// Day 9 で索引付きメッシュにしたときの理屈がそのまま GPU 側に来ている。
    /// </summary>
    private static Mesh<Vertex> CreateQuad(GL gl)
    {
        Vector4 white = Vector4.One;

        // UV は左下を (0,0)、右上を (1,1) にする。
        // テクスチャ側で上下反転して読み込んである(Texture.FromFile 参照)ので、
        // これで**画像ファイルで見たとおりの向き**に表示される。
        ReadOnlySpan<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0.0f), new Vector2(0.0f, 0.0f), white),   // 左下
            new(new Vector3(0.5f, -0.5f, 0.0f), new Vector2(1.0f, 0.0f), white),    // 右下
            new(new Vector3(0.5f, 0.5f, 0.0f), new Vector2(1.0f, 1.0f), white),     // 右上
            new(new Vector3(-0.5f, 0.5f, 0.0f), new Vector2(0.0f, 1.0f), white),    // 左上
        ];

        // 反時計回り(CCW)にそろえる。OpenGL の既定では CCW が表面。
        ReadOnlySpan<uint> indices = [0, 1, 2, 2, 3, 0];

        return new Mesh<Vertex>(gl, vertices, indices, Vertex.AttributeSizes);
    }

    private static void OnUpdate(double deltaSeconds)
    {
        if (!_paused)
        {
            _angle += (float)deltaSeconds * 0.5f;
        }

        _fpsFrames++;
        _fpsElapsed += deltaSeconds;
        if (_fpsElapsed >= 0.5)
        {
            _fps = _fpsFrames / _fpsElapsed;
            _fpsFrames = 0;
            _fpsElapsed = 0.0;

            _window.Title =
                $"Day15 - メッシュ/テクスチャ/マテリアル  {_fps:F1} fps | "
                + $"フィルタ:{_filter} | ラップ:{_wrap} | {(_wireframe ? "ワイヤー" : "塗り")}"
                + " | F:フィルタ R:ラップ W:ワイヤー V:VSync Space:停止 Esc:終了";
        }
    }

    private static void OnRender(double deltaSeconds)
    {
        _gl.ClearColor(0.10f, 0.11f, 0.13f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        // 画面のアスペクト比を打ち消す。**フレームごとに1回だけ決まる値**なので、
        // 本来はマテリアルではなくカメラの担当(Day 16 で分離する)。
        float aspect = (float)Width / Height;
        Matrix4x4 screen = Matrix4x4.CreateScale(1.0f / aspect, 1.0f, 1.0f);

        // 左: 素通し・等倍
        DrawQuad(_leftMaterial, offsetX: -0.55f, rotation: _angle, screen);

        // 右: 暖色・UV 2倍(ラップモードの違いが出る)
        DrawQuad(_rightMaterial, offsetX: 0.55f, rotation: -_angle, screen);
    }

    /// <summary>
    /// 1枚描く。**マテリアルを適用 → オブジェクトの行列を設定 → メッシュを描く**、の3手。
    /// メッシュが増えてもこの形は変わらない。
    /// </summary>
    private static void DrawQuad(Material material, float offsetX, float rotation, Matrix4x4 screen)
    {
        material.Apply();

        // 行ベクトル規約なので「回転 → 拡大 → 平行移動 → 画面補正」の順に左から掛ける
        // (Day 14 の要点4)。
        Matrix4x4 transform =
            Matrix4x4.CreateRotationZ(rotation)
            * Matrix4x4.CreateScale(0.9f)
            * Matrix4x4.CreateTranslation(offsetX, 0.0f, 0.0f)
            * screen;

        // モデル行列は**オブジェクトごと**の値なので、マテリアルではなくここで設定する。
        material.Shader.SetMatrix4("uTransform", transform);

        _quad.Draw();
    }

    private static void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        switch (key)
        {
            case Key.Escape:
                _window.Close();
                break;

            case Key.Space:
                _paused = !_paused;
                break;

            case Key.V:
                _window.VSync = !_window.VSync;
                break;

            case Key.W:
                _wireframe = !_wireframe;
                _gl.PolygonMode(
                    TriangleFace.FrontAndBack,
                    _wireframe ? PolygonMode.Line : PolygonMode.Fill);
                break;

            case Key.F:
                // Day 8 で自作したニアレスト/バイリニアの切り替え。
                // GPU では設定1つで済むが、**やっていることは同じ**。
                _filter = _filter == TextureFilter.Linear ? TextureFilter.Nearest : TextureFilter.Linear;
                _texture.SetFilter(_filter);
                break;

            case Key.R:
                // 右の四角(UV 2倍)で違いが出る。Repeat は模様が4回、
                // ClampToEdge は端の色が引き伸ばされる。
                _wrap = _wrap == TextureWrap.Repeat ? TextureWrap.ClampToEdge : TextureWrap.Repeat;
                _texture.SetWrap(_wrap);
                break;

            case Key.F5:
                _shader.TryReload();
                break;
        }
    }

    private static void OnClosing()
    {
        // GPU リソースの解放。**共有されているものは1回だけ**捨てる。
        // マテリアルはシェーダもテクスチャも所有していないので、
        // ここで両方を明示的に破棄する(要点2)。
        _quad.Dispose();
        _texture.Dispose();
        _shader.Dispose();
        _input.Dispose();
    }

    /// <summary>実行ディレクトリから上へ辿ってフォルダを探す(Day 14 と同じ)。</summary>
    private static string ResolveDirectory(string name)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, name);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException($"フォルダが見つかりません: {name}");
    }

    /// <summary>
    /// リポジトリ共有の素材(<c>assets/</c>)を探す。Phase 1 の ObjLoader と同じ手。
    ///
    /// シェーダは Day ごとに変わるので各 Day のフォルダに置くが、
    /// テクスチャは Day をまたいで同じものを使うので <c>assets/</c> に置く。
    /// </summary>
    private static string ResolveAssetPath(string relativePath)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);

            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "assets", relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"素材が見つかりません: assets/{relativePath}");
    }
}
