using System.Collections.Concurrent;
using System.Diagnostics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// 描画リソースの入口。**パスからハンドルを作り、寿命を管理し、非同期ロードを捌く**。
///
/// <b>Day 31 で名前と置き場所を変えた</b>。Day 30 までは <c>Core/ResourceManager.cs</c> だった。
/// だが中で持っているのは <see cref="Texture"/> と <see cref="Shader"/> の2種類だけ、つまり全部 GL のもので、
/// **<c>Core/</c> の中で <c>Silk.NET.OpenGL</c> を using している唯一のファイル**でもあった。
/// 名前は「全リソースの窓口」を約束しているのに、中身は描画専用だった、ということ。
///
/// Day 25 の設計書で「きれいな形ではない」と書いた <c>Core</c> ⇔ <c>Render</c> の相互参照は、
/// **このファイル1つが原因**だったので、<c>Render/</c> へ上げるだけで一方通行に戻る。
/// <see cref="ResourcePool{T}"/> と <see cref="Handle{T}"/> は <c>Core/</c> に残す——
/// あちらは <c>T</c> が何かを知らない総称型なので、下層に居るのが正しい。
///
/// <see cref="ResourcePool{T}"/> が「箱」だとすると、こちらは「窓口」。
/// 分けてあるのは、箱のほうが**リソースの種類を知らずに済む**から。
/// プールは添字と世代しか扱わないので、テクスチャにもシェーダにも使い回せる。
/// Day 27 の <see cref="AudioSystem"/> がプールを自前で1本持っているのが、その実例。
///
/// 窓口の仕事は3つ。
///   1. **重複排除** … 同じファイルを2回頼まれても1回しか読まない
///   2. **寿命** … 参照カウントをプールに預け、0 になったら GPU 側も破棄する
///   3. **非同期** … 読み込みでフレームを止めない(要点5・6)
/// </summary>
internal sealed class RenderResources : IDisposable
{
    /// <summary>
    /// ワーカースレッドで復号し終えた画像。**GPU へ上げる前**の状態。
    ///
    /// スレッドをまたぐので、可変の参照は持たせない。
    /// ここに <c>Texture</c>(GL のハンドル)を入れてしまうと、
    /// 「GL の呼び出しをワーカースレッドでやってしまう」設計にすぐ転ぶ。
    /// </summary>
    private readonly record struct DecodedJob(
        Handle<Texture> Handle,
        byte[] Pixels,
        int Width,
        int Height,
        bool GenerateMipmaps,
        string Path,
        Exception? Error);

    private readonly GL _gl;
    private readonly ResourcePool<Texture> _textures = new();
    private readonly ResourcePool<Shader> _shaders = new();

    /// <summary>パス(+読み込み設定)→ ハンドル。重複排除の表。</summary>
    private readonly Dictionary<string, Handle<Texture>> _textureByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>逆引き。解放するときに表からも消すために持つ。</summary>
    private readonly Dictionary<Handle<Texture>, string> _keyByTexture = [];

    private readonly Dictionary<string, Handle<Shader>> _shaderByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 復号が終わったものを描画スレッドへ渡すための箱。
    ///
    /// **スレッド間の受け渡しはここ1本に絞る**。
    /// あちこちで <c>lock</c> を取り始めると、どこが排他されているのか
    /// 誰にも分からなくなる。「入口は1つ、出口は1つ」にしておけば、
    /// 競合を考える場所がこの型の中だけで済む。
    /// </summary>
    private readonly ConcurrentQueue<DecodedJob> _decoded = new();

    /// <summary>読み込み中に代わりに映しておく絵。</summary>
    private readonly Texture _placeholder;

    private int _pending;

    public RenderResources(GL gl)
    {
        _gl = gl;
        _placeholder = CreatePlaceholder(gl);
    }

    /// <summary>
    /// 1回の <see cref="Update"/> で GPU へ上げる枚数の上限。
    ///
    /// **復号を裏に回しても、アップロードは描画スレッドに残る**(要点6)。
    /// 6枚を一度に上げると、そこで数ミリ秒止まる——せっかく裏で読んだのに
    /// 結局カクつく、という間抜けなことになる。1フレーム1枚に絞れば、
    /// 「6フレームかけて1枚ずつ現れる」という見た目になり、止まらない。
    /// </summary>
    public int MaxUploadsPerFrame { get; set; } = 1;

    /// <summary>まだ読み込みが終わっていない件数。</summary>
    public int PendingCount => Volatile.Read(ref _pending);

    /// <summary>重複排除で読み込みを省いた回数。</summary>
    public int CacheHits { get; private set; }

    public int TextureCount => _textures.AliveCount;

    public int ShaderCount => _shaders.AliveCount;

    /// <summary>直近のフレームで GPU へのアップロードに費やした時間(ミリ秒)。</summary>
    public double LastUploadMilliseconds { get; private set; }

    /// <summary>読み込み中に表示される仮の絵。**これが出ていたら「まだ来ていない」**。</summary>
    public Texture Placeholder => _placeholder;

    // ===== テクスチャ =====

    /// <summary>
    /// 同期でテクスチャを読む。読み終わるまで戻ってこない。
    ///
    /// 起動時にどうしても必要なもの(UI のフォント、最初の画面)はこれでよい。
    /// **「読み終わるまで進めない」ことが仕様なら、同期のほうが単純で速い**。
    /// </summary>
    public Handle<Texture> LoadTexture(string path, bool generateMipmaps = true, bool srgb = true)
    {
        string key = MakeTextureKey(path, generateMipmaps, srgb);
        if (TryReuse(key, out Handle<Texture> existing))
        {
            return existing;
        }

        Texture texture = Texture.FromFile(_gl, path, generateMipmaps, srgb);
        Handle<Texture> handle = _textures.Add(texture);
        Register(key, handle);
        return handle;
    }

    /// <summary>
    /// **メモリ上の PNG / JPEG** から読む。Day 32 で足した。
    ///
    /// glb は1ファイルの中に画像も入っているので、
    /// 「パスを渡して読む」経路が使えない(<see cref="GltfLoader"/>)。
    /// かといって窓口を通さずに <see cref="Texture"/> を作ると、
    /// そこだけ寿命管理の外に出てしまう。
    ///
    /// そこで**キャッシュのキーだけ呼び出し側が決める**形にした。
    /// glTF ローダは <c>"…/DamagedHelmet.glb#image2"</c> のような文字列を渡すので、
    /// 同じモデルを2回読んでも画像は1回しか復号されない。
    /// </summary>
    /// <param name="cacheKey">
    /// このバイト列を一意に表す文字列。**同じ中身なら同じキー**にすること。
    /// ここが衝突すると、まったく別の絵が返る。
    /// </param>
    public Handle<Texture> LoadTextureFromMemory(
        string cacheKey, ReadOnlySpan<byte> encoded, bool generateMipmaps = true, bool srgb = true)
    {
        string key = MakeMemoryKey(cacheKey, generateMipmaps, srgb);
        if (TryReuse(key, out Handle<Texture> existing))
        {
            return existing;
        }

        DecodedImage image = Texture.DecodeBytes(encoded);
        Texture texture = Texture.FromPixels(_gl, image.Pixels, image.Width, image.Height, generateMipmaps, srgb);
        Handle<Texture> handle = _textures.Add(texture);
        Register(key, handle);
        return handle;
    }

    /// <summary>
    /// 非同期でテクスチャを読む。**その場でハンドルが返る**。
    ///
    /// 返ってくるハンドルは最初から有効で、指す先は仮の絵。
    /// 読み終わると <see cref="Update"/> が中身だけ本物に差し替える。
    /// 呼び出し側は「読み終わったか」を気にせず、ずっと同じハンドルを持ち続けられる
    /// ——<see cref="ResourcePool{T}.Replace"/> のコメントに書いた、間接参照の配当。
    /// </summary>
    public Handle<Texture> LoadTextureAsync(string path, bool generateMipmaps = true)
    {
        string key = MakeTextureKey(path, generateMipmaps, srgb: true);
        if (TryReuse(key, out Handle<Texture> existing))
        {
            return existing;
        }

        // **辞書へ入れるのは「要求した時点」**。完了時に入れると、
        // 読み込み中に同じパスがもう1回来たときに素通りして、2重に読んでしまう。
        // 非同期化で最初に壊れるのはたいてい重複排除のほう。
        Handle<Texture> handle = _textures.Add(_placeholder);
        Register(key, handle);
        Interlocked.Increment(ref _pending);

        string fullPath = Path.GetFullPath(path);

        _ = Task.Run(() =>
        {
            try
            {
                // ここはワーカースレッド。**GL を一切呼ばない**のが絶対条件。
                // OpenGL のコンテキストはスレッドに紐づいていて、
                // 別スレッドから触ると「カレントでない」ため何も起きないか落ちる。
                // 逆に言えば、GL を呼ばない部分(ファイル読み+PNG の展開)は
                // 好きなだけ裏に回せる。**そこが処理時間のほとんど**でもある。
                DecodedImage image = Texture.DecodeFile(fullPath);
                _decoded.Enqueue(new DecodedJob(
                    handle, image.Pixels, image.Width, image.Height, generateMipmaps, fullPath, null));
            }
            catch (Exception ex)
            {
                // **Task の中の例外は、待たないかぎり黙って消える**。
                // 握りつぶさずキューに載せて、描画スレッドで報告する。
                // 「なぜかテクスチャが仮のまま」の原因の大半はこれ。
                _decoded.Enqueue(new DecodedJob(handle, [], 0, 0, generateMipmaps, fullPath, ex));
            }
        });

        return handle;
    }

    /// <summary>
    /// 復号済みのものを GPU へ上げる。**毎フレーム1回、描画スレッドから呼ぶ**。
    /// </summary>
    public void Update()
    {
        LastUploadMilliseconds = 0.0;

        for (int uploaded = 0; uploaded < MaxUploadsPerFrame; uploaded++)
        {
            if (!_decoded.TryDequeue(out DecodedJob job))
            {
                return;
            }

            Interlocked.Decrement(ref _pending);

            if (job.Error is not null)
            {
                Console.WriteLine($"[resource] 読み込み失敗 {Path.GetFileName(job.Path)}: {job.Error.Message}");
                continue;
            }

            // **世代チェックの出番**。読んでいる間に誰かが解放していたら、
            // このハンドルはもう別人(あるいは空)を指している。
            // 参照を渡していたら、ここで解放済みのオブジェクトに書き込んでいた。
            if (!_textures.IsAlive(job.Handle))
            {
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            Texture texture = Texture.FromPixels(_gl, job.Pixels, job.Width, job.Height, job.GenerateMipmaps);
            LastUploadMilliseconds += stopwatch.Elapsed.TotalMilliseconds;

            if (_textures.Replace(job.Handle, texture, out Texture? previous))
            {
                DisposeIfNotPlaceholder(previous);
            }
        }
    }

    public bool TryGetTexture(Handle<Texture> handle, out Texture? texture) => _textures.TryGet(handle, out texture);

    /// <summary>
    /// ハンドルからテクスチャを引く。無効なハンドルでも**仮の絵が返る**。
    ///
    /// null を返さないのは、描画側に <c>if (texture is null)</c> を撒かないため。
    /// 絵が出ないより、**紫の市松模様が出るほうが原因に気づける**。
    /// </summary>
    public Texture GetTexture(Handle<Texture> handle) =>
        _textures.TryGet(handle, out Texture? texture) ? texture : _placeholder;

    /// <summary>本物が入っているか(仮の絵のままでないか)。</summary>
    public bool IsReady(Handle<Texture> handle) =>
        _textures.TryGet(handle, out Texture? texture) && !ReferenceEquals(texture, _placeholder);

    public bool Retain(Handle<Texture> handle) => _textures.Retain(handle);

    /// <summary>
    /// 参照カウントを1つ返す。0 になったら GPU 側も破棄する。
    /// </summary>
    public bool Release(Handle<Texture> handle)
    {
        if (!_textures.Release(handle, out Texture? removed))
        {
            return false;
        }

        DisposeIfNotPlaceholder(removed);

        // 表からも消す。放っておいても TryReuse の生存確認が弾いてくれる
        // (世代が違うので IsAlive が false になり、素直に読み直される)が、
        // 死んだ項目が残り続けるのは気持ちが悪い。
        if (_keyByTexture.Remove(handle, out string? key))
        {
            _textureByKey.Remove(key);
        }

        return true;
    }

    public int RefCountOf(Handle<Texture> handle) => _textures.RefCountOf(handle);

    // ===== シェーダ =====

    /// <summary>
    /// シェーダを読む。**同期のみ**。
    ///
    /// テクスチャと違って裏に回す旨みが薄い。
    /// 重いのはファイルの読み込みではなく <c>glCompileShader</c> と
    /// <c>glLinkProgram</c>——つまり GL の呼び出しなので、
    /// どのみち描画スレッドでやるしかない。
    /// (本気でやるならコンパイル済みバイナリを事前に用意して、
    ///  実行時は読み込むだけにする。エンジンが大きくなると必ず通る道)
    /// </summary>
    public Handle<Shader> LoadShader(string vertexPath, string fragmentPath)
    {
        string key = $"{Path.GetFullPath(vertexPath)}|{Path.GetFullPath(fragmentPath)}";
        if (_shaderByKey.TryGetValue(key, out Handle<Shader> existing) && _shaders.IsAlive(existing))
        {
            _shaders.Retain(existing);
            CacheHits++;
            return existing;
        }

        var shader = new Shader(_gl, vertexPath, fragmentPath);
        Handle<Shader> handle = _shaders.Add(shader);
        _shaderByKey[key] = handle;
        return handle;
    }

    public Shader GetShader(Handle<Shader> handle) => _shaders.Get(handle);

    // ===== 後片付け =====

    public void Dispose()
    {
        // 飛んでいる復号を待たずに落とすと、キューに残ったピクセルが
        // そのまま GC 行きになるだけなので実害は無い。
        // ただし Task 自体は動き続けるので、本物のエンジンでは
        // CancellationToken を渡して止められるようにする。
        foreach (Texture texture in _textures.AliveValues)
        {
            DisposeIfNotPlaceholder(texture);
        }

        foreach (Shader shader in _shaders.AliveValues)
        {
            shader.Dispose();
        }

        _placeholder.Dispose();
    }

    /// <summary>
    /// 「読み込み中」を表す 8x8 の市松模様。
    ///
    /// **黒や白ではなく紫**にするのは業界の慣習で、
    /// 自然界にほぼ無い色だから。絵として成立してしまう色を使うと、
    /// 「読み込みに失敗している」ことに最後まで気づかない。
    /// </summary>
    private static Texture CreatePlaceholder(GL gl)
    {
        const int size = 8;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool dark = ((x / 2) + (y / 2)) % 2 == 0;
                int i = ((y * size) + x) * 4;
                pixels[i + 0] = dark ? (byte)40 : (byte)230;
                pixels[i + 1] = dark ? (byte)10 : (byte)40;
                pixels[i + 2] = dark ? (byte)50 : (byte)230;
                pixels[i + 3] = 255;
            }
        }

        Texture texture = Texture.FromPixels(gl, pixels, size, size, generateMipmaps: false);

        // 市松をぼかさない。仮の絵だと一目で分かるほうがよい。
        texture.SetFilter(TextureFilter.Nearest);
        return texture;
    }

    /// <summary>
    /// キャッシュの表に**読み込み設定まで含める**。
    ///
    /// 同じ PNG でも「ミップマップ有り」と「無し」は別物なので、
    /// パスだけをキーにすると先に読まれたほうが返ってしまう。
    /// Day 18 で見たとおり、ミップマップの有無を取り違えたテクスチャは
    /// 黙って真っ黒になる。**キャッシュのキーは「同じ結果になる条件」全部**。
    /// </summary>
    private static string MakeTextureKey(string path, bool generateMipmaps, bool srgb) =>
        MakeMemoryKey(Path.GetFullPath(path), generateMipmaps, srgb);

    /// <summary>
    /// キーに**読み込み設定を全部混ぜる**。
    /// Day 32 で sRGB が加わった——同じ PNG でも
    /// 「色として読む」か「データとして読む」かで別物になるため。
    /// </summary>
    private static string MakeMemoryKey(string baseKey, bool generateMipmaps, bool srgb) =>
        baseKey + (generateMipmaps ? string.Empty : "|nomip") + (srgb ? string.Empty : "|linear");

    private void Register(string key, Handle<Texture> handle)
    {
        _textureByKey[key] = handle;
        _keyByTexture[handle] = key;
    }

    private bool TryReuse(string key, out Handle<Texture> handle)
    {
        if (_textureByKey.TryGetValue(key, out handle) && _textures.IsAlive(handle))
        {
            _textures.Retain(handle);
            CacheHits++;
            return true;
        }

        handle = Handle<Texture>.None;
        return false;
    }

    private void DisposeIfNotPlaceholder(Texture? texture)
    {
        // 仮の絵は全員で共有しているので、差し替えのたびに捨てては困る。
        if (texture is not null && !ReferenceEquals(texture, _placeholder))
        {
            texture.Dispose();
        }
    }
}
