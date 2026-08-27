using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>スプライトを実際に描く順番の決め方。</summary>
internal enum SpriteSortMode
{
    /// <summary>
    /// 積まれた順にそのまま描く。状態が変わるたびにフラッシュする(Day 17 の挙動)。
    /// 並べ替えないので**呼び出した順が保証される**が、
    /// 呼び出し側がテクスチャをまとめて渡さないとドローコールが爆発する。
    /// </summary>
    Immediate,

    /// <summary>
    /// テクスチャごとにまとめてから描く。**ドローコールが最小になる**。
    /// 並べ替わるので描画順は保証されない。不透明なものや、
    /// 重ならないものにはこれでよい。
    /// </summary>
    Texture,

    /// <summary>
    /// レイヤーの奥から手前へ描く。同じレイヤーの中ではテクスチャでまとめる。
    /// **半透明の前後関係を正しくしたいときはこれしかない**(要点3)。
    /// </summary>
    BackToFront,
}

/// <summary>
/// 大量の四角形を、まとめて少ないドローコールで描くための箱。
///
/// Day 17 で作った素朴な版に、Phase 3 の締めとして3つ足した。
///   1. <see cref="AtlasRegion"/> を受け取れるようにした
///      → 絵の種類が増えてもテクスチャは1枚のまま(要点2)
///   2. <see cref="SpriteSortMode"/> による並べ替え
///      → 呼び出し側が順番を気にしなくてよくなった(要点3)
///   3. 頂点を 32 → 20 バイトに(<see cref="SpriteVertex"/>)
///
/// 1 と 2 が合わさると、**どんな順に Draw を呼んでも、
/// 1フレーム1回の転送 + テクスチャの種類ぶんのドローコール**に収束する。
/// アトラスを使えばテクスチャの種類は1なので、**ドローコールは1回**。
/// </summary>
internal sealed class SpriteBatch : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;

    /// <summary>1回のフラッシュで溜められるスプライトの最大数。</summary>
    private readonly int _capacity;

    /// <summary>積まれた順の頂点。</summary>
    private readonly SpriteVertex[] _vertices;

    /// <summary>
    /// 並べ替えたあとの頂点。GPU へ送るのはこちら。
    ///
    /// 元の配列を直接並べ替えないのは、**ソートキーで動かすのは4頂点ひとかたまり**で、
    /// その場で入れ替えると別のスプライトを上書きしてしまうため。
    /// 20バイト × 4頂点 × capacity ぶん余分にメモリを食うが、
    /// capacity 10000 でも 800KB なので気にしなくてよい。
    /// </summary>
    private readonly SpriteVertex[] _sorted;

    /// <summary>ソートキー。<see cref="_order"/> と対で <c>Array.Sort</c> に渡す。</summary>
    private readonly long[] _keys;

    /// <summary>ソート後のスプライト番号。</summary>
    private readonly int[] _order;

    /// <summary>スプライトごとのテクスチャ。ドローコールの切れ目を決めるのに使う。</summary>
    private readonly Texture[] _quadTextures;

    private readonly uint _vertexArray;
    private readonly uint _vertexBuffer;
    private readonly uint _indexBuffer;

    /// <summary>今たまっているスプライトの数(フラッシュするとゼロに戻る)。</summary>
    private int _pending;

    /// <summary><see cref="SpriteSortMode.Immediate"/> のときだけ使う、今のテクスチャ。</summary>
    private Texture? _currentTexture;

    private bool _began;
    private bool _savedDepthTest;
    private bool _savedCullFace;
    private bool _savedBlend;
    private bool _disposed;

    /// <summary>このフレームで発行したドローコールの回数。**今日いちばん見るべき数字**。</summary>
    public int DrawCallCount { get; private set; }

    /// <summary>このフレームで受け付けたスプライトの枚数。</summary>
    public int SpriteCount { get; private set; }

    /// <summary>描く順番の決め方。<see cref="Begin"/> で指定する。</summary>
    public SpriteSortMode SortMode { get; private set; } = SpriteSortMode.Texture;

    /// <summary>
    /// false にすると1枚ごとにフラッシュする。**バッチの効果を測るためのスイッチ**で、
    /// 実用の設定ではない。B キーで切り替えて fps を比べる。
    /// </summary>
    public bool BatchingEnabled { get; set; } = true;

    /// <summary>
    /// バッファオーファニングを使うか(Day 17 の要点3)。
    /// 測ったらこの負荷では効かなかったので既定は false。切り替えは残してある。
    /// </summary>
    public bool UseOrphaning { get; set; }

    public unsafe SpriteBatch(GL gl, Shader shader, int capacity = 10000)
    {
        _gl = gl;
        _shader = shader;
        _capacity = capacity;
        _vertices = new SpriteVertex[capacity * 4];
        _sorted = new SpriteVertex[capacity * 4];
        _keys = new long[capacity];
        _order = new int[capacity];
        _quadTextures = new Texture[capacity];

        _vertexArray = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArray);

        // --- 頂点バッファ: 中身は空のまま、場所だけ確保する ---
        _vertexBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);
        _gl.BufferData(
            BufferTargetARB.ArrayBuffer,
            (nuint)(_vertices.Length * Unsafe.SizeOf<SpriteVertex>()),
            null,
            BufferUsageARB.StreamDraw);

        // --- インデックスバッファ: 最初に1回作れば終わり ---
        //
        // 四角形 i の頂点は必ず 4i, 4i+1, 4i+2, 4i+3 に並ぶので、
        // 中身はスプライトの内容に一切依存しない。
        //
        // **並べ替えてもここは変わらない**のが今日の設計の要。
        // 頂点のほうを並べ替えて詰め直すので、インデックスは常に
        // 「先頭から順に四角形を作る」形のままでよい。
        uint[] indices = new uint[capacity * 6];
        for (int i = 0; i < capacity; i++)
        {
            uint v = (uint)(i * 4);
            indices[i * 6 + 0] = v + 0;
            indices[i * 6 + 1] = v + 1;
            indices[i * 6 + 2] = v + 2;
            indices[i * 6 + 3] = v + 2;
            indices[i * 6 + 4] = v + 3;
            indices[i * 6 + 5] = v + 0;
        }

        _indexBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _indexBuffer);
        fixed (uint* data = indices)
        {
            _gl.BufferData(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(indices.Length * sizeof(uint)),
                data,
                BufferUsageARB.StaticDraw);
        }

        // --- 頂点属性 ---
        int stride = Unsafe.SizeOf<SpriteVertex>();
        int offset = 0;
        ReadOnlySpan<VertexAttribute> attributes = SpriteVertex.Attributes;
        for (int i = 0; i < attributes.Length; i++)
        {
            VertexAttribute attribute = attributes[i];

            _gl.VertexAttribPointer(
                (uint)i,
                attribute.ComponentCount,
                attribute.Type,
                attribute.Normalized,
                (uint)stride,
                (void*)offset);

            _gl.EnableVertexAttribArray((uint)i);

            offset += attribute.ByteSize;
        }

        if (offset != stride)
        {
            throw new InvalidOperationException(
                $"頂点属性の合計 {offset} バイトが {nameof(SpriteVertex)} のサイズ {stride} バイトと一致しません");
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
    }

    /// <summary>
    /// スプライトの受け付けを始める。
    ///
    /// ここで 2D を描くための状態に切り替える(Day 17 の要点5)。
    /// 借りたものは <see cref="End"/> で返す。
    /// </summary>
    public void Begin(Matrix4x4 projection, SpriteSortMode sortMode = SpriteSortMode.Texture)
    {
        if (_began)
        {
            throw new InvalidOperationException("Begin が二重に呼ばれています。End を先に呼んでください");
        }

        _began = true;
        SortMode = sortMode;
        DrawCallCount = 0;
        SpriteCount = 0;
        _currentTexture = null;

        _savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _savedCullFace = _gl.IsEnabled(EnableCap.CullFace);
        _savedBlend = _gl.IsEnabled(EnableCap.Blend);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shader.Use();
        _shader.SetMatrix4("uProjection", projection);
        _shader.SetInt("uTexture", 0);
    }

    /// <summary>テクスチャ全体を1枚のスプライトとして積む。</summary>
    public void Draw(Texture texture, Vector2 center, Vector2 size, float rotation, Vector4 color, float layer = 0.0f)
        => Draw(
            new AtlasRegion(texture, Vector2.Zero, Vector2.One, texture.Width, texture.Height),
            center, size, rotation, color, layer);

    /// <summary>
    /// アトラスの一部を1枚のスプライトとして積む。
    /// </summary>
    /// <param name="layer">
    /// 奥行き。0 が奥、1 が手前。
    /// <see cref="SpriteSortMode.BackToFront"/> のときだけ意味を持つ。
    ///
    /// **深度バッファではない**ことに注意。2D では深度テストを切っているので、
    /// これは「描く順を決めるためだけの数字」。
    /// 深度バッファを使わない理由は、半透明が深度テストと両立しないため(要点3)。
    /// </param>
    public void Draw(
        in AtlasRegion region,
        Vector2 center,
        Vector2 size,
        float rotation,
        Vector4 color,
        float layer = 0.0f)
    {
        if (!_began)
        {
            throw new InvalidOperationException("Begin を先に呼んでください");
        }

        // 満杯になったら、そこまでのぶんを吐き出す。
        //
        // **並べ替えモードでは、これが起きるとソートが分断される**。
        // 前半だけで並べ替え → 描画 → 後半だけで並べ替え、になるので、
        // 前半の手前のスプライトが後半の奥のスプライトに隠される。
        // だから容量は「1フレームで積む最大枚数」以上にしておくのが望ましい。
        if (_pending >= _capacity)
        {
            FlushAll();
        }

        // Immediate だけは、テクスチャが変わった時点で吐き出す(Day 17 と同じ)。
        // 並べ替えモードでは、テクスチャが変わっても溜め続ける。
        // **どうせあとでまとめ直すので、ここで切る意味が無い**。
        if (SortMode == SpriteSortMode.Immediate
            && _currentTexture is not null
            && _currentTexture != region.Texture)
        {
            FlushAll();
        }

        _currentTexture = region.Texture;

        // --- 回転を効かせた4隅を求める ---
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        Vector2 right = new(cos * size.X * 0.5f, sin * size.X * 0.5f);
        Vector2 down = new(-sin * size.Y * 0.5f, cos * size.Y * 0.5f);

        Vector2 topLeft = center - right - down;
        Vector2 topRight = center + right - down;
        Vector2 bottomRight = center + right + down;
        Vector2 bottomLeft = center - right + down;

        // 色は**1枚につき1回だけ**詰める。4頂点それぞれで詰め直すのは無駄。
        uint packed = SpriteVertex.PackColor(color);

        // 画面の上辺には V が大きいほうを割り当てる(Day 17 の要点6)。
        Vector2 uvMin = region.UvMin;
        Vector2 uvMax = region.UvMax;

        int v = _pending * 4;
        _vertices[v + 0] = new SpriteVertex(topLeft, new Vector2(uvMin.X, uvMax.Y), packed);
        _vertices[v + 1] = new SpriteVertex(topRight, new Vector2(uvMax.X, uvMax.Y), packed);
        _vertices[v + 2] = new SpriteVertex(bottomRight, new Vector2(uvMax.X, uvMin.Y), packed);
        _vertices[v + 3] = new SpriteVertex(bottomLeft, new Vector2(uvMin.X, uvMin.Y), packed);

        _quadTextures[_pending] = region.Texture;
        _keys[_pending] = MakeSortKey(region.Texture, layer);
        _order[_pending] = _pending;

        _pending++;
        SpriteCount++;

        if (!BatchingEnabled)
        {
            FlushAll();
        }
    }

    /// <summary>
    /// ソートキーを作る。**1本の long に押し込む**のがこの手の定番。
    ///
    /// 比較関数を書いて <c>Comparison&lt;T&gt;</c> を渡すより、
    /// 数値1つに畳んでおくほうがずっと速い(比較のたびにデリゲート呼び出しが起きない)。
    /// 優先度の高い条件を上位ビットに置くだけで、多段ソートが1回の比較で済む。
    ///
    ///   上位32bit … レイヤー(奥ほど小さい)
    ///   下位32bit … テクスチャのハンドル
    ///
    /// レイヤーを 16bit に量子化しているのは、float をそのままビット比較できないから
    /// (負数の扱いが素直でない)。0.0〜1.0 を 0〜65535 に写せば単純な整数比較になる。
    /// 65536 段あれば 2D の重ね順には十分すぎる。
    /// </summary>
    private long MakeSortKey(Texture texture, float layer)
    {
        if (SortMode != SpriteSortMode.BackToFront)
        {
            // テクスチャだけでまとめる。ハンドルの値そのものに意味は無く、
            // **同じものが隣り合えばよい**だけなのでこれで足りる。
            return texture.Handle;
        }

        uint quantized = (uint)(Math.Clamp(layer, 0.0f, 1.0f) * 65535.0f);
        return ((long)quantized << 32) | texture.Handle;
    }

    /// <summary>溜まっているぶんを描いて、状態を元に戻す。</summary>
    public void End()
    {
        if (!_began)
        {
            throw new InvalidOperationException("Begin が呼ばれていません");
        }

        FlushAll();
        _began = false;

        SetCap(EnableCap.DepthTest, _savedDepthTest);
        SetCap(EnableCap.CullFace, _savedCullFace);
        SetCap(EnableCap.Blend, _savedBlend);
    }

    /// <summary>
    /// 溜まっているぶんを GPU へ送って描く。
    ///
    /// **転送は1回、ドローコールはテクスチャの切れ目の数だけ**。
    /// インデックスバッファは「先頭から順に四角形」の形で固定なので、
    /// 頂点を並べ替えて詰め直せば、あとは <c>glDrawElements</c> の
    /// オフセットをずらすだけで任意の区間を描ける。
    /// </summary>
    private unsafe void FlushAll()
    {
        if (_pending == 0)
        {
            return;
        }

        // --- 並べ替え ---
        // Immediate では並べ替えも詰め直しもしないので、積んだ配列をそのまま送る。
        // Day 17 の挙動と同じコストになるようにしてあり、比較の基準として使える。
        SpriteVertex[] source = _vertices;

        if (SortMode != SpriteSortMode.Immediate)
        {
            // キーの配列と番号の配列を対で渡すと、キーで並べ替えつつ番号も同じ順に動く。
            // 自前で (key, index) のペアを作るより割り当てが少ない。
            Array.Sort(_keys, _order, 0, _pending);

            // 並べ替えた順に頂点を詰め直す。
            for (int i = 0; i < _pending; i++)
            {
                Array.Copy(_vertices, _order[i] * 4, _sorted, i * 4, 4);
            }

            source = _sorted;
        }

        int stride = Unsafe.SizeOf<SpriteVertex>();

        _gl.BindVertexArray(_vertexArray);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);

        if (UseOrphaning)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(_vertices.Length * stride),
                null,
                BufferUsageARB.StreamDraw);
        }

        // **1フレームに1回の転送**。ここが Day 17 との一番の違い。
        // Day 17 はテクスチャが変わるたびに転送していた。
        fixed (SpriteVertex* data = source)
        {
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                (nuint)(_pending * 4 * stride),
                data);
        }

        // --- テクスチャの切れ目でドローコールを分ける ---
        int runStart = 0;
        for (int i = 1; i <= _pending; i++)
        {
            bool boundary = i == _pending
                || _quadTextures[_order[i]] != _quadTextures[_order[runStart]];

            if (!boundary)
            {
                continue;
            }

            _quadTextures[_order[runStart]].Bind(TextureUnit.Texture0);

            _gl.DrawElements(
                PrimitiveType.Triangles,
                (uint)((i - runStart) * 6),
                DrawElementsType.UnsignedInt,
                // インデックスバッファ内のバイトオフセット。
                // 四角形 runStart 個ぶん先から読む。
                (void*)(runStart * 6 * sizeof(uint)));

            DrawCallCount++;
            runStart = i;
        }

        _pending = 0;
        _currentTexture = null;
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

        _gl.DeleteBuffer(_vertexBuffer);
        _gl.DeleteBuffer(_indexBuffer);
        _gl.DeleteVertexArray(_vertexArray);
    }
}
