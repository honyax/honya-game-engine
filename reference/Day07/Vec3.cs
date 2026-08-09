namespace SoftwareRasterizer;

/// <summary>
/// 3次元ベクトル。位置・方向・色・法線と、この先あらゆるものがこの型になる。
///
/// なぜ自作するのか(.NET には System.Numerics.Vector3 があるのに):
/// 中身が何をしているか分からないまま使うと、
/// 「なぜ正規化が要るのか」「内積の符号は何を意味するのか」が身につかないため。
/// Day 5 は自作して仕組みを理解し、Day 14 で Silk.NET へ移るときに
/// System.Numerics へ乗り換える、という段取りにしている。
/// 性能面の比較は計画書 Day05.md の要点7を参照。
///
/// struct にしているのは、頂点1つに何個も持つ小さな値だから。
/// class にすると1頂点ごとにヒープ確保とポインタ参照が発生し、
/// 毎フレーム数万個を扱うラスタライザでは致命的になる。
///
/// readonly struct にしていない理由: フィールドへの代入(v.X = 1)を許して
/// 写経中の試行錯誤をしやすくするため。実務では readonly struct + with 式が定石。
/// </summary>
internal struct Vec3
{
    public float X;

    public float Y;

    public float Z;

    public Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>全成分が同じ値のベクトル。灰色を作るときなどに便利。</summary>
    public Vec3(float value) : this(value, value, value)
    {
    }

    public static Vec3 Zero => new(0.0f, 0.0f, 0.0f);

    public static Vec3 One => new(1.0f, 1.0f, 1.0f);

    public static Vec3 UnitX => new(1.0f, 0.0f, 0.0f);

    public static Vec3 UnitY => new(0.0f, 1.0f, 0.0f);

    public static Vec3 UnitZ => new(0.0f, 0.0f, 1.0f);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>符号反転(逆向きのベクトル)。</summary>
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    public static Vec3 operator *(Vec3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vec3 operator *(float s, Vec3 a) => a * s;

    /// <summary>
    /// 成分ごとの掛け算(アダマール積)。
    /// ベクトルの掛け算としては数学的に特別な意味を持たないが、
    /// **色の掛け合わせ**では毎回これを使う(光の色 x 材質の色 = 見える色)。
    /// Day 9 のライティングで多用する。
    /// </summary>
    public static Vec3 operator *(Vec3 a, Vec3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    public static Vec3 operator /(Vec3 a, float s) => a * (1.0f / s);

    /// <summary>
    /// 内積(ドット積)。2つのベクトルが「どれだけ同じ方向を向いているか」。
    ///
    /// a・b = |a| |b| cosθ なので、両方が単位ベクトルなら結果はそのまま cosθ になる。
    ///   1 … 同じ向き / 0 … 直角 / -1 … 真逆
    /// Day 9 のランバート反射(面の向きと光の向きの内積 = 明るさ)がこの性質そのもの。
    /// Day 10 の背面カリングでも「面が自分を向いているか」の判定に使う。
    /// </summary>
    public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>
    /// 外積(クロス積)。2つのベクトルの**両方に垂直**なベクトルを返す。
    ///
    /// 長さは |a||b|sinθ で、a と b が張る平行四辺形の面積に等しい。
    /// Day 3 のエッジ関数は、この外積のZ成分だけを取り出したものだった
    /// (2次元では「垂直なベクトル」が画面から手前/奥に向く1方向しかないので、
    ///  向きは符号1つで表せる)。
    ///
    /// 主な用途は Day 10 の法線計算。三角形の2辺の外積が、その面の向きになる。
    /// 順序を入れ替えると符号が反転する(a×b = -(b×a))ので、
    /// 頂点の巻き方向が面の表裏を決める、という話に直結する。
    /// </summary>
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    /// <summary>長さの2乗。比較するだけなら平方根が要らないので、こちらで済ませられる場面は多い。</summary>
    public readonly float LengthSquared() => X * X + Y * Y + Z * Z;

    public readonly float Length() => MathF.Sqrt(LengthSquared());

    /// <summary>
    /// 正規化(長さを1にする)。方向だけが欲しいときに使う。
    ///
    /// 長さ1でないと内積が cosθ にならないので、
    /// ライティングの計算に入れる前には必ず正規化する必要がある。
    /// 長さ0のベクトルは向きが定義できないので、そのまま返している
    /// (0除算で NaN を撒き散らすと、原因の特定が非常に面倒になるため)。
    /// </summary>
    public readonly Vec3 Normalized()
    {
        float lengthSquared = LengthSquared();
        if (lengthSquared <= 0.0f)
        {
            return this;
        }

        return this * (1.0f / MathF.Sqrt(lengthSquared));
    }

    /// <summary>線形補間。t=0 で a、t=1 で b。Day 4 の属性補間と同じ発想。</summary>
    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + (b - a) * t;

    /// <summary>
    /// 反射ベクトル。法線 n の面に入射方向 v が当たって跳ね返る向き。
    /// Day 9 のスペキュラ(鏡面反射)で使う。n は正規化済みであることが前提。
    /// </summary>
    public static Vec3 Reflect(Vec3 v, Vec3 n) => v - n * (2.0f * Dot(v, n));

    public readonly override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}
