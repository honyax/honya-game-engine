namespace SoftwareRasterizer;

/// <summary>
/// 4次元ベクトル(同次座標)。
///
/// なぜ3次元の点を4つの数で表すのか——これが Day 5 の一番の山場。
///
/// 3x3 行列では**平行移動が表せない**。行列は必ず原点を原点に写すので、
/// どんな 3x3 行列を掛けても (0,0,0) は (0,0,0) のままだから。
/// そこで次元を1つ増やし、点を (x, y, z, 1) として扱う。
/// すると 4x4 行列の4行目(このコードの M41〜M43)に平行移動量を置けて、
/// 回転・拡大・平行移動を**すべて行列の掛け算1つに統一**できる。
///
/// W 成分の使い分け:
///   W = 1 … 位置(平行移動の影響を受ける)
///   W = 0 … 方向(平行移動の影響を受けない。法線や光の向きはこちら)
/// この1文字の違いが「点を動かす」と「向きを回す」を区別する。
/// 法線を W=1 で変換してしまうと、平行移動のぶんだけ向きが狂う——
/// グラフィックスで頻出のバグで、Day 9 で実際に注意することになる。
///
/// さらに W にはもう1つの役割がある。Day 6 の透視投影では、
/// 変換後の W に「カメラからの奥行き」が入り、x, y, z を W で割ると
/// 遠くのものが小さくなる。**透視除算**と呼ばれるこの一手が、
/// 3DCGが立体的に見える仕掛けのすべて。同次座標はそのための土台でもある。
/// </summary>
internal struct Vec4
{
    public float X;

    public float Y;

    public float Z;

    public float W;

    public Vec4(float x, float y, float z, float w)
    {
        X = x;
        Y = y;
        Z = z;
        W = w;
    }

    /// <summary>位置として同次座標にする(W = 1)。平行移動の影響を受ける。</summary>
    public static Vec4 Point(Vec3 v) => new(v.X, v.Y, v.Z, 1.0f);

    /// <summary>方向として同次座標にする(W = 0)。平行移動の影響を受けない。</summary>
    public static Vec4 Direction(Vec3 v) => new(v.X, v.Y, v.Z, 0.0f);

    /// <summary>W を捨てて3次元に戻す。透視除算を伴わない単純な切り捨て。</summary>
    public readonly Vec3 Xyz => new(X, Y, Z);

    public static Vec4 operator +(Vec4 a, Vec4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);

    public static Vec4 operator -(Vec4 a, Vec4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);

    public static Vec4 operator *(Vec4 a, float s) => new(a.X * s, a.Y * s, a.Z * s, a.W * s);

    public static Vec4 operator *(float s, Vec4 a) => a * s;

    public static float Dot(Vec4 a, Vec4 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    public readonly override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3}, {W:F3})";
}
