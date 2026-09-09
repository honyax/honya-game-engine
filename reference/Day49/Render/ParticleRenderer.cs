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
/// </summary>
internal sealed class ParticleRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly int _capacity;
    private readonly ParticleVertex[] _vertices;

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

    public int DrawCallCount { get; private set; }

    public int ParticleCount { get; private set; }

    /// <summary>プールに入りきらず捨てた粒の数。**0 でないなら容量が足りていない**。</summary>
    public int DroppedCount { get; private set; }

    /// <summary>並べ替えに掛かった時間(ms)。**アルファ合成の代償がここに出る**。</summary>
    public double SortMilliseconds { get; private set; }

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
        DroppedCount = 0;
        SortMilliseconds = 0.0;
        _pending = 0;
        _currentTexture = null;

        _shader.Use();
        _shader.SetMatrix4("uViewProjection", _viewProjection);
        _shader.SetInt("uTexture", 0);
        _shader.SetFloat("uIntensity", Intensity);
        _shader.SetInt("uPremultiply", Blend == ParticleBlend.Premultiplied ? 1 : 0);

        _gl.Enable(EnableCap.Blend);
        ApplyBlend();

        // **深度は読む**。不透明な床や箱の裏に回った粒は隠れてほしい。
        _gl.Enable(EnableCap.DepthTest);

        // **深度は書かない**のが既定(要点4)。
        _gl.DepthMask(DepthWrite);

        // ビルボードは常にカメラを向くので裏表が無い。カリングは切っておく。
        _gl.Disable(EnableCap.CullFace);
    }

    /// <summary>
    /// 1つの放出口を積む。テクスチャが変わったらそこでフラッシュする。
    /// </summary>
    public void Draw(ParticleEmitter emitter, Texture texture)
    {
        if (emitter.Count == 0)
        {
            return;
        }

        if (_currentTexture is not null && _currentTexture != texture)
        {
            Flush();
        }

        _currentTexture = texture;

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

    private unsafe void Flush()
    {
        if (_pending == 0 || _currentTexture is null)
        {
            return;
        }

        _shader.Use();
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

    private void ApplyBlend()
    {
        switch (Blend)
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
