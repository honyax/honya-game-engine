namespace SoftwareRasterizer;

/// <summary>
/// 4x4 行列。回転・拡大縮小・平行移動、そして Day 6 では投影までを担う。
///
/// ==== 規約(ここを間違えると一日溶ける)====
///
/// 本リポジトリは **行ベクトル規約** を採用する。つまり
///
///     変換後の点 = 点 * 行列          (v' = v * M)
///
/// と書き、点は「横に寝た1x4の行」として扱う。平行移動量は4行目(M41〜M43)に入る。
/// これは System.Numerics.Matrix4x4 や XNA / MonoGame / DirectX と同じ規約で、
/// Day 14 で Silk.NET(System.Numerics を使う)へ移るときにそのまま繋がる。
///
/// 一方 OpenGL / GLSL と多くの教科書(ゲームグラフィックス特論も)は
/// **列ベクトル規約**を使う。あちらは v' = M * v と書き、平行移動量は4列目に入る。
///
/// 2つの規約の関係は「互いに転置」。同じ変換を表す行列同士が転置の関係にあり、
/// 掛ける順序も逆になる。
///
///     行ベクトル: v * M_model * M_view * M_proj     (左から順に適用)
///     列ベクトル: M_proj * M_view * M_model * v     (右から順に適用)
///
/// **どちらが正しいということはなく、単なる書き方の約束**。
/// ただし混ぜると壊れる。教科書のコードを写すときは、まずどちらの規約かを確認すること。
/// Phase 2 で生 OpenGL に行列を渡すときは、転置するか
/// glUniformMatrix の transpose 引数を true にする必要がある(そこで改めて扱う)。
///
/// ==== メモリ配置 ====
///
/// M11 M12 M13 M14      1行目
/// M21 M22 M23 M24      2行目
/// M31 M32 M33 M34      3行目
/// M41 M42 M43 M44      4行目 ← 平行移動量はここ
///
/// float[16] の配列ではなく16個のフィールドにしているのは、
/// 配列だと毎回ヒープ確保と境界チェックが入るため。struct のフィールドなら
/// レジスタに乗り、インデックス計算も要らない。
/// </summary>
internal struct Mat4
{
    public float M11, M12, M13, M14;
    public float M21, M22, M23, M24;
    public float M31, M32, M33, M34;
    public float M41, M42, M43, M44;

    /// <summary>
    /// 単位行列。掛けても何も変わらない行列で、数の 1 に相当する。
    /// 変換を積み上げていくときの出発点になる。
    /// </summary>
    public static Mat4 Identity => new()
    {
        M11 = 1.0f, M22 = 1.0f, M33 = 1.0f, M44 = 1.0f,
    };

    /// <summary>
    /// 平行移動。行ベクトル規約なので、移動量は4行目に置く。
    ///
    /// 点 (x,y,z,1) を掛けると、4行目が W=1 倍されて足し込まれる。
    /// 方向 (x,y,z,0) を掛けると W=0 倍なので何も足されない——
    /// これが「W=0 なら平行移動の影響を受けない」の仕組み。
    /// </summary>
    public static Mat4 Translation(Vec3 t)
    {
        Mat4 m = Identity;
        m.M41 = t.X;
        m.M42 = t.Y;
        m.M43 = t.Z;
        return m;
    }

    /// <summary>拡大縮小。対角に倍率を置くだけ。</summary>
    public static Mat4 Scale(Vec3 s)
    {
        Mat4 m = Identity;
        m.M11 = s.X;
        m.M22 = s.Y;
        m.M33 = s.Z;
        return m;
    }

    public static Mat4 Scale(float s) => Scale(new Vec3(s));

    /// <summary>
    /// X軸まわりの回転。YZ平面の中で回るので、X成分は変わらない。
    ///
    /// 2次元の回転行列 [cos -sin; sin cos] を、回転しない軸を避けて埋め込んでいるだけ。
    /// 符号の並びは規約(行ベクトル・右手系)から決まる。
    /// 回転の向きが逆に見えたら、まず sin の符号2箇所を疑うとよい。
    /// </summary>
    public static Mat4 RotationX(float radians)
    {
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        Mat4 m = Identity;
        m.M22 = c;
        m.M23 = s;
        m.M32 = -s;
        m.M33 = c;
        return m;
    }

    /// <summary>Y軸まわりの回転。モデルをその場で回すときに一番よく使う。</summary>
    public static Mat4 RotationY(float radians)
    {
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        Mat4 m = Identity;
        m.M11 = c;
        m.M13 = -s;
        m.M31 = s;
        m.M33 = c;
        return m;
    }

    /// <summary>Z軸まわりの回転。2Dの回転はこれ(画面内でぐるぐる回る)。</summary>
    public static Mat4 RotationZ(float radians)
    {
        float c = MathF.Cos(radians);
        float s = MathF.Sin(radians);
        Mat4 m = Identity;
        m.M11 = c;
        m.M12 = s;
        m.M21 = -s;
        m.M22 = c;
        return m;
    }

    /// <summary>
    /// 行列の積。**掛ける順序が変換の順序**になる(行ベクトル規約では左から順に適用)。
    ///
    ///     Scale * RotationZ * Translation
    ///
    /// と書けば「まず拡大し、次に回し、最後に移動する」。
    /// 順序を入れ替えると結果が変わる(行列の積は交換法則が成り立たない)。
    /// 「原点で回してから移動」と「移動してから原点で回す」が別物なのは直感どおりで、
    /// 後者は遠くの点を軸にぐるっと公転することになる。
    ///
    /// 中身は「左の行 x 右の列」の内積を16回。展開して書いているのは、
    /// 三重ループにするとインデックス計算とループのオーバーヘッドが乗るため。
    /// </summary>
    public static Mat4 operator *(Mat4 a, Mat4 b) => new()
    {
        M11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41,
        M12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42,
        M13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43,
        M14 = a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44,

        M21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41,
        M22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42,
        M23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43,
        M24 = a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44,

        M31 = a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41,
        M32 = a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42,
        M33 = a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43,
        M34 = a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44,

        M41 = a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41,
        M42 = a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42,
        M43 = a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43,
        M44 = a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44,
    };

    /// <summary>
    /// 同次座標のベクトルを変換する(v * M)。
    /// Day 6 で透視投影行列を掛けると、ここで W に奥行きが入ってくる。
    /// </summary>
    public static Vec4 Transform(Vec4 v, Mat4 m) => new(
        v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + v.W * m.M41,
        v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + v.W * m.M42,
        v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + v.W * m.M43,
        v.X * m.M14 + v.Y * m.M24 + v.Z * m.M34 + v.W * m.M44);

    /// <summary>
    /// 点として変換する(W = 1)。平行移動が効く。
    /// W が 1 のままである保証があるとき(平行移動・回転・拡大のみ)に使う簡易版。
    /// 透視投影を含む行列に使ってはいけない(W が 1 でなくなるため)。
    /// </summary>
    public static Vec3 TransformPoint(Vec3 v, Mat4 m) => new(
        v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41,
        v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42,
        v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43);

    /// <summary>
    /// 方向として変換する(W = 0)。平行移動を無視する。
    /// 法線や光の向きはこちらで変換しないと、物体を動かしたときに向きが狂う。
    /// </summary>
    public static Vec3 TransformDirection(Vec3 v, Mat4 m) => new(
        v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31,
        v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32,
        v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33);

    /// <summary>
    /// 転置(行と列を入れ替える)。
    /// 列ベクトル規約(OpenGL / GLSL)との相互変換に使う。
    /// また、回転だけでできた行列は転置がそのまま逆行列になる、という便利な性質もある。
    /// </summary>
    public readonly Mat4 Transposed() => new()
    {
        M11 = M11, M12 = M21, M13 = M31, M14 = M41,
        M21 = M12, M22 = M22, M23 = M32, M24 = M42,
        M31 = M13, M32 = M23, M33 = M33, M34 = M43,
        M41 = M14, M42 = M24, M43 = M34, M44 = M44,
    };

    public readonly override string ToString()
        => $"[{M11,7:F3} {M12,7:F3} {M13,7:F3} {M14,7:F3}]\n"
         + $"[{M21,7:F3} {M22,7:F3} {M23,7:F3} {M24,7:F3}]\n"
         + $"[{M31,7:F3} {M32,7:F3} {M33,7:F3} {M34,7:F3}]\n"
         + $"[{M41,7:F3} {M42,7:F3} {M43,7:F3} {M44,7:F3}]";
}
