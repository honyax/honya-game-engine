using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>混ぜ方。**これを切り替えるだけで火にも煙にもなる**(要点2)。</summary>
internal enum ParticleBlend
{
    /// <summary>加算。<c>src + dst</c>。重なるほど明るい。火花・爆発・魔法。</summary>
    Additive,

    /// <summary>アルファ。<c>src·a + dst·(1-a)</c>。重なっても明るくならない。煙・埃。</summary>
    Alpha,

    /// <summary>事前乗算。**1つの式で加算とアルファの両方を出せる**。</summary>
    Premultiplied,
}

/// <summary>
/// パーティクル1頂点。**24 バイト**。
///
/// <para>
/// スプライト(<see cref="SpriteVertex"/>、20 バイト)との違いは位置が 3 成分になったことだけ。
/// 色を byte 4個に詰めてある理由も同じで、
/// **毎フレーム全頂点を作り直して送る**ので 1 頂点のバイト数がそのまま転送量になる。
/// 1 万粒 = 4 万頂点で 960KB。<see cref="Vector4"/> の色にすると 1.44MB。
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ParticleVertex
{
    /// <summary>**ワールド座標**。ビルボードの向きはここに焼き込んである。</summary>
    public Vector3 Position;

    public Vector2 TexCoord;

    /// <summary>RGBA 各8bit。<see cref="SpriteVertex.PackColor"/> で詰める。</summary>
    public uint Color;

    private static readonly VertexAttribute[] AttributeList =
    [
        VertexAttribute.Float(3),      // Position
        VertexAttribute.Float(2),      // TexCoord
        VertexAttribute.UNormByte4(),  // Color
    ];

    public static ReadOnlySpan<VertexAttribute> Attributes => AttributeList;
}

/// <summary>
/// **パーティクルを1回のドローコールで描く**(Day 49)。今日の主役その2。
///
/// <para>
/// <b>ビルボード</b>——粒はただの点だが、画面には四角い絵として出したい。
/// そこで<b>カメラの右と上</b>で四角形を張る。こうすると、
/// カメラがどこから見ても四角形は必ず正面を向く。
/// </para>
///
/// <para>
/// カメラの右と上は<b>ビュー行列から取れる</b>。ビュー行列はワールドからビューへの
/// 回転(と平行移動)で、回転部分は正規直交なので<b>転置が逆行列</b>になる。
/// つまりビュー空間の <c>(1,0,0)</c> に対応するワールドのベクトルは、
/// ビュー行列の回転部分の<b>1列目</b>——<c>(M11, M21, M31)</c> がそれになる。
/// </para>
///
/// <para>
/// <b>四隅を CPU で作る</b>のは Day 18 のスプライトバッチと同じ判断。
/// 頂点シェーダで展開する手(ジオメトリシェーダやインスタンシング)もあるが、
/// そちらは「粒ごとの uniform」か「追加の頂点属性」が要る。
/// CPU で焼き込めば<b>uniform はフレームに1本</b>で済み、
/// 1万粒でもドローコールは1回のままになる。
/// </para>
///
/// <para>
/// <b>深度は読むが書かない</b>のがもう1つの要点(要点4)。
/// 半透明なものが深度を書くと、**後ろに描かれる粒が深度テストで落ちる**——
/// 手前の粒が「壁」になって、その裏の粒が消える。
/// </para>
///
/// <para>
/// <b>Day 50 で3つ増えた</b>。
/// </para>
/// <list type="number">
/// <item>
/// <b>ソフトパーティクル</b>(<see cref="SetSceneDepth"/>)…
/// シーンの深度を読んで、床や壁に近い画素を薄くする。
/// 四角形が地面を貫いてできる<b>まっすぐな切り口</b>が消える(要点1)。
/// </item>
/// <item>
/// <b>混ぜ方を積む単位ごとに切り替えられる</b>(<see cref="Draw(ParticleEmitter, Texture, ParticleBlend)"/>)…
/// 加算の閃光と半透明の煙が1フレームに同居する。
/// テクスチャの切り替えと同じ扱いで、<b>変わったところでフラッシュする</b>。
/// </item>
/// <item>
/// <b>トレイル</b>(<see cref="Draw(Trail, Texture, ParticleBlend)"/>)…
/// リボンも「ワールド座標の四角形の列」なので、
/// <b>粒とまったく同じ頂点・同じシェーダ・同じバッファ</b>に載る。
/// </item>
/// </list>
/// </summary>
internal sealed class ParticleRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly int _capacity;
    private readonly ParticleVertex[] _vertices;

    /// <summary>トレイルの節ごとの「横方向」。**継ぎ目で辻褄を合わせるため先に全部出す**。</summary>
    private Vector3[] _jointSide = new Vector3[64];

    /// <summary>並べ替え用の作業領域。**(奥行き, 粒の添字)** を詰めて `Array.Sort` に掛ける。</summary>
    private float[] _depths;
    private int[] _order;

    private readonly uint _vertexArray;
    private readonly uint _vertexBuffer;
    private readonly uint _indexBuffer;

    private Matrix4x4 _viewProjection;
    private Vector3 _cameraRight;
    private Vector3 _cameraUp;
    private Vector3 _cameraPosition;

    private Texture? _currentTexture;

    /// <summary>いま GL に入っている混ぜ方。**これが変わる境目がドローコールの境目**(Day 50)。</summary>
    private ParticleBlend _currentBlend;

    /// <summary>シーンの深度のコピー。null ならソフトパーティクルは効かない(Day 50)。</summary>
    private Texture? _sceneDepth;

    private float _near = 0.1f;
    private float _far = 100.0f;
    private Vector2 _screenSize = new(1.0f, 1.0f);

    private int _pending;
    private bool _disposed;

    public unsafe ParticleRenderer(GL gl, Shader shader, int capacity = 8192)
    {
        _gl = gl;
        _shader = shader;
        _capacity = capacity;
        _vertices = new ParticleVertex[capacity * 4];
        _depths = new float[capacity];
        _order = new int[capacity];

        _vertexArray = _gl.GenVertexArray();
        _gl.BindVertexArray(_vertexArray);

        _vertexBuffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);
        _gl.BufferData(
            BufferTargetARB.ArrayBuffer,
            (nuint)(_vertices.Length * Unsafe.SizeOf<ParticleVertex>()),
            null,
            BufferUsageARB.StreamDraw);

        // 四角形 i の頂点は必ず 4i〜4i+3 に並ぶので、インデックスは中身に依存しない。
        // **並べ替えても頂点のほうを詰め直す**ので、ここは最初に1回作れば終わり。
        uint[] indices = new uint[capacity * 6];
        for (int i = 0; i < capacity; i++)
        {
            uint v = (uint)(i * 4);
            indices[(i * 6) + 0] = v + 0;
            indices[(i * 6) + 1] = v + 1;
            indices[(i * 6) + 2] = v + 2;
            indices[(i * 6) + 3] = v + 2;
            indices[(i * 6) + 4] = v + 3;
            indices[(i * 6) + 5] = v + 0;
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

        int stride = Unsafe.SizeOf<ParticleVertex>();
        int offset = 0;
        ReadOnlySpan<VertexAttribute> attributes = ParticleVertex.Attributes;
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
                $"頂点属性の合計 {offset} バイトが {nameof(ParticleVertex)} のサイズ {stride} バイトと一致しません");
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, 0);
    }

    public ParticleBlend Blend { get; set; } = ParticleBlend.Additive;

    /// <summary>
    /// 奥から手前へ並べ替えるか。**加算合成では要らない**(足し算の順番は結果を変えない)。
    /// アルファ合成では切ると粒が四角く抜ける(要点3)。
    /// </summary>
    public bool SortByDepth { get; set; }

    /// <summary>
    /// 深度を書くか。**普段は false**。true にすると、
    /// 手前の粒が壁になって後ろの粒が消えるのが見える(要点4)。
    /// </summary>
    public bool DepthWrite { get; set; }

    /// <summary>明るさの倍率。1 を超えるとブルームのしきい値を越えて**にじむ**。</summary>
    public float Intensity { get; set; } = 2.0f;

    /// <summary>
    /// **ソフトパーティクル**を効かせるか(Day 50)。切ると Day 49 の絵に戻る——
    /// 粒の四角形が床を貫いた<b>まっすぐな切り口</b>が出る。
    /// </summary>
    public bool SoftParticles { get; set; } = true;

    /// <summary>
    /// 何メートル手前から薄れ始めるか(Day 50)。
    ///
    /// <para>
    /// 小さすぎると切り口が消えず、大きすぎると<b>粒全体が薄くなって存在感が無くなる</b>。
    /// 粒の大きさと同じくらい(土埃なら 0.3〜0.5m)が目安になる。
    /// </para>
    /// </summary>
    public float SoftFadeDistance { get; set; } = 0.35f;

    public int DrawCallCount { get; private set; }

    public int ParticleCount { get; private set; }

    /// <summary>積んだトレイルの帯の枚数(Day 50)。粒とは別に数える。</summary>
    public int TrailSegmentCount { get; private set; }

    /// <summary>プールに入りきらず捨てた粒の数。**0 でないなら容量が足りていない**。</summary>
    public int DroppedCount { get; private set; }

    /// <summary>並べ替えに掛かった時間(ms)。**アルファ合成の代償がここに出る**。</summary>
    public double SortMilliseconds { get; private set; }

    /// <summary>
    /// **シーンの深度を渡す**(Day 50)。ソフトパーティクルの入力になる。
    ///
    /// <para>
    /// <b>いま描き込んでいるフレームバッファの深度そのものを渡してはいけない</b>。
    /// 「書き込み先として挿さっているテクスチャを、同じ描画で読む」のは
    /// OpenGL では<b>フィードバックループ</b>で、結果は未定義になる。
    /// 呼ぶ側で<b>コピーしてから</b>渡すこと(<see cref="PostProcess.CaptureDepth"/>)。
    /// </para>
    ///
    /// <para>
    /// <paramref name="near"/> と <paramref name="far"/> が要るのは、
    /// 深度バッファの値が<b>線形ではない</b>ため(要点2)。
    /// 引き算する前に、両方を「カメラからの距離(m)」へ戻す必要がある。
    /// </para>
    /// </summary>
    public void SetSceneDepth(Texture? depth, float near, float far, int width, int height)
    {
        _sceneDepth = depth;
        _near = MathF.Max(1e-4f, near);
        _far = MathF.Max(_near + 1e-4f, far);
        _screenSize = new Vector2(Math.Max(1, width), Math.Max(1, height));
    }

    public void Begin(Camera camera)
    {
        Matrix4x4 view = camera.ViewMatrix;

        // **ビュー行列の回転部分の列がカメラの基底**(クラスの説明を参照)。
        _cameraRight = new Vector3(view.M11, view.M21, view.M31);
        _cameraUp = new Vector3(view.M12, view.M22, view.M32);
        _cameraPosition = camera.Position;
        _viewProjection = camera.ViewProjection;

        DrawCallCount = 0;
        ParticleCount = 0;
        TrailSegmentCount = 0;
        DroppedCount = 0;
        SortMilliseconds = 0.0;
        _pending = 0;
        _currentTexture = null;

        _shader.Use();
        _shader.SetMatrix4("uViewProjection", _viewProjection);
        _shader.SetInt("uTexture", 0);

        // --- ソフトパーティクル(Day 50)---
        //
        // **深度が無ければ切る**。シェーダ側は uSoftFade が 0 なら
        // テクスチャを1回も引かないので、深度を渡さないまま呼んでも安全。
        bool soft = SoftParticles && _sceneDepth is not null;

        _shader.SetInt("uSceneDepth", 1);
        _shader.SetFloat("uSoftFade", soft ? MathF.Max(0.0f, SoftFadeDistance) : 0.0f);
        _shader.SetVector2("uNearFar", new Vector2(_near, _far));
        _shader.SetVector2("uScreenSize", _screenSize);

        _gl.Enable(EnableCap.Blend);

        _currentBlend = Blend;
        ApplyBlend(_currentBlend);
        _shader.SetFloat("uIntensity", IntensityFor(_currentBlend));

        // **深度は読む**。不透明な床や箱の裏に回った粒は隠れてほしい。
        _gl.Enable(EnableCap.DepthTest);

        // **深度は書かない**のが既定(要点4)。
        _gl.DepthMask(DepthWrite);

        // ビルボードは常にカメラを向くので裏表が無い。カリングは切っておく。
        _gl.Disable(EnableCap.CullFace);
    }

    /// <summary>1つの放出口を、レンダラ既定の混ぜ方(<see cref="Blend"/>)で積む。</summary>
    public void Draw(ParticleEmitter emitter, Texture texture) => Draw(emitter, texture, Blend);

    /// <summary>
    /// 1つの放出口を積む。**テクスチャか混ぜ方が変わったらそこでフラッシュする**(Day 50)。
    ///
    /// <para>
    /// Day 49 の混ぜ方は <see cref="Begin"/> で1回決めるだけだった。
    /// エフェクトを扱い始めると<b>加算の閃光と半透明の煙が同じフレームに出る</b>ので、
    /// 積む単位ごとに切り替えられないと、どちらかを諦めることになる。
    /// </para>
    ///
    /// <para>
    /// <b>混ぜ方は GL の状態</b>なので、途中で変えるには
    /// それまでに積んだぶんを描き切ってからでないといけない——
    /// テクスチャの切り替えとまったく同じ理屈で、
    /// <b>切り替えの回数がそのままドローコールの数</b>になる。
    /// だから <see cref="EffectSystem.Draw"/> は混ぜ方でまとめて回している。
    /// </para>
    /// </summary>
    public void Draw(ParticleEmitter emitter, Texture texture, ParticleBlend blend)
    {
        if (emitter.Count == 0)
        {
            return;
        }

        SetState(texture, blend);

        ReadOnlySpan<Particle> alive = emitter.Alive;

        if (SortByDepth)
        {
            AppendSorted(emitter, alive);
        }
        else
        {
            for (int i = 0; i < alive.Length; i++)
            {
                Append(emitter, alive[i]);
            }
        }
    }

    /// <summary>
    /// **トレイルを1本積む**(Day 50)。節の列から帯を張る。
    ///
    /// <para>
    /// <b>粒とまったく同じ入れ物に載る</b>のがこの実装の要点になる。
    /// 頂点は <see cref="ParticleVertex"/>、シェーダは <c>particle.vert/frag</c>、
    /// インデックスは <see cref="_indexBuffer"/> の使い回し——
    /// 帯1枚が四角形1枚なので、粒と同じ「4頂点・6インデックス」で収まる。
    /// トレイル専用の VAO もシェーダも1本も増えていない。
    /// </para>
    ///
    /// <para>
    /// <b>横方向は節ごとに1回だけ出す</b>(<see cref="_jointSide"/>)。
    /// 帯ごとに計算すると、隣り合う2枚が共有するはずの辺が
    /// <b>わずかに違う場所</b>に来て、継ぎ目に隙間が開く。
    /// 節で1つに決めておけば、両側の帯が同じ2点を使うので必ず閉じる。
    /// </para>
    ///
    /// <para>
    /// 節の向きは<b>前後の節を結んだ向き</b>にする(端は片側だけ)。
    /// 曲がり角で「入ってくる向き」と「出ていく向き」の中間を取ることになり、
    /// これが無いと<b>急カーブの外側で帯が折れて尖る</b>。
    /// </para>
    /// </summary>
    public void Draw(Trail trail, Texture texture, ParticleBlend blend)
    {
        if (trail.Count < 2)
        {
            return;
        }

        SetState(texture, blend);

        if (_jointSide.Length < trail.Count)
        {
            _jointSide = new Vector3[trail.Count];
        }

        for (int i = 0; i < trail.Count; i++)
        {
            Vector3 previous = trail[Math.Max(0, i - 1)].Position;
            Vector3 next = trail[Math.Min(trail.Count - 1, i + 1)].Position;

            _jointSide[i] = Trail.Side(next - previous, _cameraPosition - trail[i].Position);
        }

        for (int i = 0; i + 1 < trail.Count; i++)
        {
            if (_pending >= _capacity)
            {
                Flush();

                if (_pending >= _capacity)
                {
                    DroppedCount++;
                    return;
                }
            }

            // 節0がいちばん古い(尾)なので、i 側が尾、i+1 側が先端に近い。
            float tailAge = trail.NormalizedAge(i);
            float headAge = trail.NormalizedAge(i + 1);

            Vector3 tailOffset = _jointSide[i] * (trail.WidthAt(tailAge) * 0.5f);
            Vector3 headOffset = _jointSide[i + 1] * (trail.WidthAt(headAge) * 0.5f);

            Vector3 tail = trail[i].Position;
            Vector3 head = trail[i + 1].Position;

            uint tailColor = SpriteVertex.PackColor(trail.ColorAt(tailAge));
            uint headColor = SpriteVertex.PackColor(trail.ColorAt(headAge));

            // **U が帯の長さ方向、V が幅方向**。
            // 絵(<see cref="ParticleTextures.CreateTrailStrip"/>)は V にだけ濃淡を持つので、
            // U をどう取っても見た目は同じ——それでも寿命に沿わせておくと、
            // 模様のあるテクスチャに差し替えたときにそのまま流れる。
            int baseIndex = _pending * 4;

            _vertices[baseIndex + 0] = new ParticleVertex
            {
                Position = tail + tailOffset,
                TexCoord = new Vector2(tailAge, 0.0f),
                Color = tailColor,
            };
            _vertices[baseIndex + 1] = new ParticleVertex
            {
                Position = head + headOffset,
                TexCoord = new Vector2(headAge, 0.0f),
                Color = headColor,
            };
            _vertices[baseIndex + 2] = new ParticleVertex
            {
                Position = head - headOffset,
                TexCoord = new Vector2(headAge, 1.0f),
                Color = headColor,
            };
            _vertices[baseIndex + 3] = new ParticleVertex
            {
                Position = tail - tailOffset,
                TexCoord = new Vector2(tailAge, 1.0f),
                Color = tailColor,
            };

            _pending++;
            TrailSegmentCount++;
        }
    }

    public void End()
    {
        Flush();

        // **借りた状態は必ず返す**(Day 18 から続く作法)。
        // 深度書き込みを戻し忘れると、次のフレームの不透明な描画が深度を書かなくなり、
        // **前後関係がめちゃくちゃになる**——しかも原因はここではなく向こうに出る。
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>
    /// 奥から手前へ並べ替えて積む。
    ///
    /// <para>
    /// <b>キーはカメラからの距離の2乗</b>。平方根を取らないのは、
    /// 大小の比較にしか使わないため(平方根は単調増加なので、取っても順序は変わらない)。
    /// </para>
    /// </summary>
    private void AppendSorted(ParticleEmitter emitter, ReadOnlySpan<Particle> alive)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_depths.Length < alive.Length)
        {
            _depths = new float[alive.Length];
            _order = new int[alive.Length];
        }

        for (int i = 0; i < alive.Length; i++)
        {
            // **負の距離**にして昇順に並べると、そのまま「遠い順」になる。
            _depths[i] = -Vector3.DistanceSquared(alive[i].Position, _cameraPosition);
            _order[i] = i;
        }

        Array.Sort(_depths, _order, 0, alive.Length);

        SortMilliseconds +=
            (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;

        for (int i = 0; i < alive.Length; i++)
        {
            Append(emitter, alive[_order[i]]);
        }
    }

    private void Append(ParticleEmitter emitter, in Particle particle)
    {
        if (_pending >= _capacity)
        {
            // **フラッシュして続ける**。捨てるより描くほうが筋がよい——
            // 容量はドローコールの回数の話であって、絵の内容の話ではない。
            Flush();

            if (_pending >= _capacity)
            {
                DroppedCount++;
                return;
            }
        }

        float normalized = particle.Normalized;
        float half = emitter.SizeAt(normalized, particle.Seed) * 0.5f;

        if (half <= 0.0f)
        {
            return;
        }

        // **カメラの右と上を、粒の回転だけ回す**。
        // 回転を頂点の位置に焼き込んでしまうので、シェーダは何も知らなくてよい。
        float cos = MathF.Cos(particle.Rotation);
        float sin = MathF.Sin(particle.Rotation);

        Vector3 axisX = ((_cameraRight * cos) + (_cameraUp * sin)) * half;
        Vector3 axisY = ((_cameraUp * cos) - (_cameraRight * sin)) * half;

        Vector3 center = particle.Position;

        // --- フリップブックの UV ---
        //
        // コマ番号 → 列と行 → その升の左上と右下。
        // **アトラス(Day 18)と同じ話**だが、升が等分なので割り算で出せる。
        int columns = Math.Max(1, emitter.FlipbookColumns);
        int rows = Math.Max(1, emitter.FlipbookRows);
        int frame = emitter.FrameAt(normalized);

        float du = 1.0f / columns;
        float dv = 1.0f / rows;
        float u0 = (frame % columns) * du;
        float v0 = (frame / columns) * dv;
        float u1 = u0 + du;
        float v1 = v0 + dv;

        uint color = SpriteVertex.PackColor(emitter.ColorAt(normalized));

        int baseIndex = _pending * 4;

        _vertices[baseIndex + 0] = new ParticleVertex
        {
            Position = center - axisX + axisY,
            TexCoord = new Vector2(u0, v0),
            Color = color,
        };
        _vertices[baseIndex + 1] = new ParticleVertex
        {
            Position = center + axisX + axisY,
            TexCoord = new Vector2(u1, v0),
            Color = color,
        };
        _vertices[baseIndex + 2] = new ParticleVertex
        {
            Position = center + axisX - axisY,
            TexCoord = new Vector2(u1, v1),
            Color = color,
        };
        _vertices[baseIndex + 3] = new ParticleVertex
        {
            Position = center - axisX - axisY,
            TexCoord = new Vector2(u0, v1),
            Color = color,
        };

        _pending++;
        ParticleCount++;
    }

    /// <summary>
    /// テクスチャと混ぜ方を今から積むものに合わせる(Day 50)。
    /// **どちらが変わってもフラッシュ**する。
    /// </summary>
    private void SetState(Texture texture, ParticleBlend blend)
    {
        // **先に描き切る**。順番を逆にすると、すでに積んである粒が
        // これから設定する混ぜ方で描かれてしまう。
        if ((_currentTexture is not null && _currentTexture != texture) || blend != _currentBlend)
        {
            Flush();
        }

        _currentTexture = texture;

        if (blend != _currentBlend)
        {
            _currentBlend = blend;
            ApplyBlend(blend);

            _shader.Use();
            _shader.SetInt("uPremultiply", blend == ParticleBlend.Premultiplied ? 1 : 0);
            _shader.SetFloat("uIntensity", IntensityFor(blend));
        }
    }

    /// <summary>
    /// その混ぜ方での明るさの倍率(Day 50)。**アルファのときは必ず 1 倍**。
    ///
    /// <para>
    /// <see cref="Intensity"/> は<b>加算のための倍率</b>だった(Day 49 の要点2)。
    /// 加算では RGB を持ち上げたぶんが素直に明るさになり、
    /// 1.0 を超えればブルームが拾ってにじむ。
    /// </para>
    ///
    /// <para>
    /// ところが<b>アルファ合成の粒に同じ倍率を掛けると、色が白く飛ぶ</b>。
    /// 土埃の色 (0.62, 0.55, 0.44) を 2 倍すると (1.24, 1.10, 0.88) で、
    /// 3成分とも 1.0 付近に張り付いて<b>土の色が消える</b>——
    /// おまけにブルームのしきい値も越えるので、足元だけが光る。
    /// Day 49 は加算の粒しか同時に出さなかったので気づけなかった箇所で、
    /// <b>加算とアルファが同居した今日、初めて症状が出た</b>。
    /// </para>
    ///
    /// <para>
    /// アルファの粒を明るくしたいなら
    /// <see cref="ParticleEmitter.StartColor"/> のほうを上げる。
    /// 「明るさ」と「色」を混ぜないための線引きになる。
    /// </para>
    /// </summary>
    private float IntensityFor(ParticleBlend blend) =>
        blend == ParticleBlend.Alpha ? 1.0f : Intensity;

    private unsafe void Flush()
    {
        if (_pending == 0 || _currentTexture is null)
        {
            return;
        }

        _shader.Use();

        // **深度は必ず何かを挿しておく**(Day 50)。
        // ソフトパーティクルを切っていてもシェーダには sampler2D が宣言されており、
        // 何も挿さっていないユニットを持つプログラムは、
        // ドライバによっては「不完全」として描画ごと落とす。
        // 引かれない(uSoftFade が 0)ので、粒のテクスチャを挿しておけば足りる。
        (_sceneDepth ?? _currentTexture).Bind(TextureUnit.Texture1);

        _currentTexture.Bind(TextureUnit.Texture0);

        _gl.BindVertexArray(_vertexArray);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBuffer);

        fixed (ParticleVertex* data = _vertices)
        {
            _gl.BufferSubData(
                BufferTargetARB.ArrayBuffer,
                0,
                (nuint)(_pending * 4 * Unsafe.SizeOf<ParticleVertex>()),
                data);
        }

        _gl.DrawElements(
            PrimitiveType.Triangles,
            (uint)(_pending * 6),
            DrawElementsType.UnsignedInt,
            null);

        DrawCallCount++;
        _pending = 0;
    }

    /// <summary>
    /// **深度バッファの値を「カメラからの距離(m)」へ戻す**(Day 50)。
    ///
    /// <para>
    /// 深度バッファに入っているのは <c>z</c> ではなく <c>1/z</c> に近いもので、
    /// 手前に精度が偏っている(要点2)。そのまま引き算すると、
    /// 近くでは効きすぎ、遠くではまったく効かないフェードになる。
    /// </para>
    ///
    /// <para>
    /// <c>particle.frag</c> の同名の関数と<b>1文字ずつ同じ式</b>にしてある。
    /// 自己チェックがこちらを検算することで、シェーダ側の式も一緒に確かめられる——
    /// GPU の中身は読めないので、<b>片方を C# に持ってきて確かめる</b>のが唯一の手になる。
    /// </para>
    /// </summary>
    public static float LinearizeDepth(float depth01, float near, float far)
    {
        float ndc = (depth01 * 2.0f) - 1.0f;
        return 2.0f * near * far / (far + near - (ndc * (far - near)));
    }

    /// <summary>
    /// ソフトパーティクルの薄め方(Day 50)。**1 でそのまま、0 で消える**。
    ///
    /// <para>
    /// 差が負(粒のほうが奥)になるのは、深度テストを通り抜けた画素では起きない。
    /// それでも <c>Clamp</c> しておくのは、深度の精度の都合で
    /// <b>ちょうど同じ面のときに符号が揺れる</b>ため——
    /// クランプしないと床と同じ高さの粒がちらつく。
    /// </para>
    /// </summary>
    public static float SoftFade(float sceneDistance, float particleDistance, float fadeDistance)
    {
        if (fadeDistance <= 0.0f)
        {
            return 1.0f;
        }

        return Math.Clamp((sceneDistance - particleDistance) / fadeDistance, 0.0f, 1.0f);
    }

    private void ApplyBlend(ParticleBlend blend)
    {
        switch (blend)
        {
            case ParticleBlend.Alpha:
                // 普通の半透明。**重ねても明るくならない**ので煙・埃向き。
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                break;

            case ParticleBlend.Premultiplied:
                // RGB に A を掛けた色を渡す前提。**式は1つでよい**——
                // A=1 ならアルファ合成、A=0 なら加算とまったく同じ結果になる。
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                break;

            default:
                // 加算。**背景を消さずに足す**ので、暗いところほど効果が出る。
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gl.DeleteVertexArray(_vertexArray);
        _gl.DeleteBuffer(_vertexBuffer);
        _gl.DeleteBuffer(_indexBuffer);
        _disposed = true;
    }
}
