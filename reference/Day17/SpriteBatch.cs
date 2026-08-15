using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// 大量の四角形を、まとめて少ないドローコールで描くための箱。
///
/// ここまでの <see cref="Mesh{TVertex}"/> は「1つの形 = 1回の <c>glDrawElements</c>」だった。
/// スプライトを1万枚描こうとすると、それは**1万回のドローコール**になる。
/// ドローコール1回のCPU側コストは数マイクロ秒あるので、
/// それだけで1フレームの予算(60fps なら 16.6ms)を軽く超える。
///
/// 解決策は「頂点を GPU に送る回数」ではなく「**描けと命じる回数**」を減らすこと。
/// 1万枚ぶんの頂点(4万頂点)を1本のバッファに詰めて、1回で描かせる。
/// GPU にとっては4万頂点の三角形リストでしかなく、それが1万個の別々の絵だとは知らない。
///
/// <see cref="Mesh{TVertex}"/> を使い回せない理由は、あちらが
/// <c>StaticDraw</c> で1回きりのアップロードを前提にしているから。
/// スプライトの頂点は**毎フレーム全部作り直す**ので、バッファの扱いが根本的に違う(要点3)。
///
/// 使い方:
/// <code>
/// batch.Begin(projection);
/// batch.Draw(texture, center, size, rotation, color);   // 何回でも
/// batch.End();
/// </code>
/// </summary>
internal sealed class SpriteBatch : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;

    /// <summary>1回のフラッシュで溜められるスプライトの最大数。</summary>
    private readonly int _capacity;

    /// <summary>
    /// CPU 側の作業用配列。**毎フレームここに書き込んで、まとめて GPU へ送る**。
    /// 使い回すので確保は最初の1回だけ。毎フレーム <c>new</c> すると GC を叩き続けることになる。
    /// </summary>
    private readonly SpriteVertex[] _vertices;

    private readonly uint _vertexArray;
    private readonly uint _vertexBuffer;
    private readonly uint _indexBuffer;

    /// <summary>今たまっているスプライトの数(フラッシュするとゼロに戻る)。</summary>
    private int _pending;

    /// <summary>
    /// 今たまっているぶんが使っているテクスチャ。
    /// **違うテクスチャで描こうとしたらフラッシュするしかない**(要点4)。
    /// </summary>
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

    /// <summary>
    /// false にすると1枚ごとにフラッシュする。**バッチの効果を測るためのスイッチ**で、
    /// 実用の設定ではない。B キーで切り替えて fps を比べる。
    /// </summary>
    public bool BatchingEnabled { get; set; } = true;

    /// <summary>
    /// バッファオーファニングを使うか(要点3)。
    /// **測ったらこの負荷では効かなかったので既定は false**。切り替えは残してある。
    /// </summary>
    public bool UseOrphaning { get; set; }

    public unsafe SpriteBatch(GL gl, Shader shader, int capacity = 4000)
    {
        _gl = gl;
        _shader = shader;
        _capacity = capacity;
        _vertices = new SpriteVertex[capacity * 4];

        _vertexArray = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArray);

        // --- 頂点バッファ: 中身は空のまま、**場所だけ**確保する ---
        //
        // 第3引数を null にすると「このサイズで確保はするが、内容は未定義」の意味になる。
        // 毎フレーム内容が変わるので、ここで詰めるものは無い。
        //
        // StreamDraw は「毎フレーム書き換えて、数回描いて捨てる」という使い方の申告。
        // StaticDraw / DynamicDraw / StreamDraw の違いは**ドライバへのヒントでしかなく**、
        // 間違えても動く。ただしドライバはこれを見てメモリの置き場所を決めるので、
        // 嘘をつくと遅くなることがある。
        _vertexBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);
        _gl.BufferData(
            BufferTargetARB.ArrayBuffer,
            (nuint)(_vertices.Length * Unsafe.SizeOf<SpriteVertex>()),
            null,
            BufferUsageARB.StreamDraw);

        // --- インデックスバッファ: **こちらは最初に1回作れば終わり** ---
        //
        // 四角形 i の頂点は必ず 4i, 4i+1, 4i+2, 4i+3 に並ぶので、
        // インデックスの中身はスプライトの内容に一切依存しない。
        // つまり毎フレーム送り直す必要が無い。**動的なのは頂点だけ**というのが要点。
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

        // --- 頂点属性 --- (Mesh とまったく同じ組み立て方)
        int stride = Unsafe.SizeOf<SpriteVertex>();
        int offset = 0;
        ReadOnlySpan<int> attributeSizes = SpriteVertex.AttributeSizes;
        for (int i = 0; i < attributeSizes.Length; i++)
        {
            _gl.VertexAttribPointer(
                (uint)i, attributeSizes[i], VertexAttribPointerType.Float, false, (uint)stride, (void*)offset);
            _gl.EnableVertexAttribArray((uint)i);
            offset += attributeSizes[i] * sizeof(float);
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
    /// ここで**2D を描くための状態**に切り替える(要点5)。3D 用の設定のままでは、
    ///   - 深度テストが効いていると、あとから描いた手前のスプライトが弾かれる
    ///   - 背面カリングが効いていると、Y 下向きの座標系で巻きが裏返って全部消える
    ///   - ブレンドが無効だと、アルファが 0 の部分も背景色で塗り潰される
    /// の3つが起きる。**借りたものは <see cref="End"/> で返す**。
    /// </summary>
    /// <param name="projection">スクリーン座標 → クリップ座標の行列。</param>
    public void Begin(Matrix4x4 projection)
    {
        if (_began)
        {
            throw new InvalidOperationException("Begin が二重に呼ばれています。End を先に呼んでください");
        }

        _began = true;
        DrawCallCount = 0;
        SpriteCount = 0;

        _savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _savedCullFace = _gl.IsEnabled(EnableCap.CullFace);
        _savedBlend = _gl.IsEnabled(EnableCap.Blend);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);

        // いわゆる「通常のアルファブレンド」。
        //   結果 = 描く色 * α + すでにある色 * (1 - α)
        // α が 0 の部分は「すでにある色」がそのまま残るので、切り抜きになる。
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shader.Use();
        _shader.SetMatrix4("uProjection", projection);
        _shader.SetInt("uTexture", 0);
    }

    /// <summary>スプライトを1枚積む。テクスチャ全体を使う。</summary>
    public void Draw(Texture texture, Vector2 center, Vector2 size, float rotation, Vector4 color)
        => Draw(texture, center, size, rotation, color, Vector2.Zero, Vector2.One);

    /// <summary>
    /// スプライトを1枚積む。テクスチャの一部だけを切り出す版。
    ///
    /// <paramref name="uvMin"/> / <paramref name="uvMax"/> はテクスチャ座標なので、
    /// **原点は左下**(Day 15 の要点4で上下反転して読み込んでいるため)。
    /// スプライトシートを扱いやすいピクセル指定にするのは Day 18 の仕事。
    /// </summary>
    public void Draw(
        Texture texture,
        Vector2 center,
        Vector2 size,
        float rotation,
        Vector4 color,
        Vector2 uvMin,
        Vector2 uvMax)
    {
        if (!_began)
        {
            throw new InvalidOperationException("Begin を先に呼んでください");
        }

        // **フラッシュが必要になる2つの条件**。ここが今日の肝(要点4)。
        //   1. テクスチャが変わる … シェーダに刺さっているテクスチャは1つだけなので、
        //      違う絵を混ぜられない
        //   2. バッファが満杯   … これ以上は物理的に入らない
        if (_currentTexture != texture || _pending >= _capacity)
        {
            Flush();
            _currentTexture = texture;
        }

        // --- 回転を効かせた4隅を求める ---
        //
        // 中心から「右半分」「下半分」へのベクトルを回転させて足し引きする。
        // 4隅それぞれに sin/cos を掛けるより、こちらのほうが計算が少なくて済む。
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);
        Vector2 right = new(cos * size.X * 0.5f, sin * size.X * 0.5f);
        Vector2 down = new(-sin * size.Y * 0.5f, cos * size.Y * 0.5f);

        Vector2 topLeft = center - right - down;
        Vector2 topRight = center + right - down;
        Vector2 bottomRight = center + right + down;
        Vector2 bottomLeft = center - right + down;

        // --- UV の割り当て ---
        //
        // **画面の上辺には V が大きいほうを割り当てる**。V 座標の反転がここで3回目。
        //   Day 10 … OBJ の V
        //   Day 15 … 画像読み込み時の上下反転
        //   Day 17 … スクリーン座標が Y 下向き ⇔ テクスチャ座標が V 上向き
        // 3回とも「原点の取り方が違う2つの世界をつなぐ」という同じ問題で、
        // 直し方も毎回1か所。どこで1回だけ反転させるかを決めておくのが大事。
        int v = _pending * 4;
        _vertices[v + 0] = new SpriteVertex(topLeft, new Vector2(uvMin.X, uvMax.Y), color);
        _vertices[v + 1] = new SpriteVertex(topRight, new Vector2(uvMax.X, uvMax.Y), color);
        _vertices[v + 2] = new SpriteVertex(bottomRight, new Vector2(uvMax.X, uvMin.Y), color);
        _vertices[v + 3] = new SpriteVertex(bottomLeft, new Vector2(uvMin.X, uvMin.Y), color);

        _pending++;
        SpriteCount++;

        if (!BatchingEnabled)
        {
            Flush();
        }
    }

    /// <summary>溜まっているぶんを描いて、状態を元に戻す。</summary>
    public void End()
    {
        if (!_began)
        {
            throw new InvalidOperationException("Begin が呼ばれていません");
        }

        Flush();
        _began = false;

        // 借りた状態を返す。
        // BlendFunc は元に戻していない。**「何を保存して何を保存しないか」を
        // クラスごとに決め打ちするのは長期的には破綻する**ので、
        // 本来はレンダーステートをまとめて持つ仕組みが要る。
        // ここでは Begin が必ず BlendFunc を設定し直すことで辻褄を合わせている。
        SetCap(EnableCap.DepthTest, _savedDepthTest);
        SetCap(EnableCap.CullFace, _savedCullFace);
        SetCap(EnableCap.Blend, _savedBlend);
    }

    /// <summary>
    /// 溜まっているぶんを GPU へ送って、1回だけ描く。
    /// </summary>
    private unsafe void Flush()
    {
        if (_pending == 0 || _currentTexture is null)
        {
            return;
        }

        _gl.BindVertexArray(_vertexArray);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);

        int vertexCount = _pending * 4;
        int stride = Unsafe.SizeOf<SpriteVertex>();

        if (UseOrphaning)
        {
            // **バッファオーファニング**。動的バッファの定番テクニックとされているもの。
            //
            // 同じバッファに BufferSubData で上書きするとき、GPU がまだ前フレームの
            // 描画でそのバッファを読んでいると、ドライバは読み終わるのを待つ
            // (= CPU が止まる)。ここで同じサイズ・同じ用途で BufferData を
            // 呼び直すと「前の中身はもう要らない」という宣言になり、ドライバは
            // 古い領域を GPU に使わせたまま、**新しい領域をこちらに渡してくれる**。
            // 古い領域は描き終わったら勝手に回収される(= 孤児 orphan にする)。
            //
            // ただし**このデモでは効かなかった**。
            // 20000枚(6ドローコール、1フレーム約1.05ms)で ON/OFF の差は 0.3% 以内、
            // 1000枚では ON のほうがむしろ遅い。理由は単純で、**待つほど GPU が
            // 遅れていない**から。オーファニングは「待ちを消す」技であって、
            // 待ちが無いところでは BufferData の呼び出しぶんだけ損をする。
            //
            // 効くのは GPU 律速のとき。既定を false にしてあるのはそのためで、
            // O キーで切り替えて自分の環境で測れるようにしてある。
            // **定番と呼ばれる手法でも、自分の負荷で測るまでは分からない**
            // ——Day 2・Day 9 で繰り返し踏んだのと同じ話。
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(_vertices.Length * stride),
                null,
                BufferUsageARB.StreamDraw);
        }

        fixed (SpriteVertex* data = _vertices)
        {
            // 使ったぶんだけ送る。満杯でないときに全域を送ると、
            // 使っていない領域まで毎フレーム転送することになる。
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                (nuint)(vertexCount * stride),
                data);
        }

        _currentTexture.Bind(TextureUnit.Texture0);

        _gl.DrawElements(
            PrimitiveType.Triangles,
            (uint)(_pending * 6),
            DrawElementsType.UnsignedInt,
            (void*)0);

        DrawCallCount++;
        _pending = 0;
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
