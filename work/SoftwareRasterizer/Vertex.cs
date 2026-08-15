namespace SoftwareRasterizer;

/// <summary>
/// 頂点。位置と、その位置に紐づく「属性」を持つ。
///
/// ラスタライザの仕事は、突き詰めると
/// **「3頂点が持っている属性を、三角形内部の各ピクセルへ配り直すこと」**に尽きる。
/// 今日は属性が色だけだが、この先
///   Day 8 … テクスチャ座標 (U, V)
///   Day 9 … 法線ベクトル
/// が同じように増えていく。
///
/// Day 4 では位置を int の X, Y、色を float 3つでバラバラに持っていたが、
/// Day 5 でベクトル型を導入したので Vec2 / Vec3 に置き換えた。
/// フィールドが5個から2個に減り、意味の単位でまとまった。
/// この「型を作ると、コードが短くなるだけでなく意味がはっきりする」効果が、
/// 自前の数学ライブラリを書く一番の見返りになる。
///
/// 位置が int から float になった意味は大きい。Day 6 で変換行列を通すと
/// 画面座標は必ず小数になるし、小数のまま扱えば三角形の辺をピクセルより
/// 細かい精度で置ける(サブピクセル精度)。回転の滑らかさが目に見えて変わる。
/// </summary>
internal struct Vertex
{
    /// <summary>
    /// 位置。モデル座標(変換前)または画面座標 + 深度(変換後)。
    /// </summary>
    public Vec3 Position;

    /// <summary>頂点の色。0.0〜1.0 の RGB。</summary>
    public Vec3 Color;

    /// <summary>
    /// テクスチャ座標(UV)。0〜1 が画像の端から端。
    ///
    /// 位置と違って**モデルの形とは独立**に決められる。
    /// 同じ立方体でも UV の付け方次第で、6面に同じ絵を貼ることも、
    /// 1枚の絵を6面に切り分けて貼ることもできる。
    /// この「どこに何を貼るか」を決める作業がUV展開で、
    /// モデラーが手間をかけている部分でもある。
    /// </summary>
    public Vec2 TexCoord;

    /// <summary>
    /// 法線(その点で面が向いている方向)。長さ1の単位ベクトル。
    ///
    /// **位置と違って「向き」なので、平行移動の影響を受けてはいけない**。
    /// 変換するときは W=0 の方向ベクトルとして扱う(Day 5 の要点1)。
    ///
    /// 法線を頂点ごとに持つ意味は大きい。立方体の角のように
    /// 「位置は同じでも面の向きが違う」場所では別の頂点として持つことになるし、
    /// 逆に球のように滑らかな面では隣り合う三角形で法線を共有することで、
    /// 少ない三角形でも丸く見せられる(要点4)。
    /// </summary>
    public Vec3 Normal;

    /// <summary>
    /// ワールド座標。<see cref="Position"/> が画面座標に上書きされる前の値を退避したもの。
    ///
    /// ライティングの計算に要る。光源やカメラの位置はワールド座標で持っているので、
    /// 「この点から光源へ向かう方向」を求めるにはワールド座標のままの点が必要になる。
    /// <see cref="Rasterizer.DrawTriangle"/> が投影の途中で埋める。
    /// </summary>
    public Vec3 World;

    /// <summary>
    /// クリップ座標の W の逆数。**投影を通した後だけ意味を持つ**フィールド。
    ///
    /// 透視補正補間(Day 8 の要点2)に使う。モデル座標の頂点を作る時点では
    /// 値が入っていないが、<see cref="Rasterizer.DrawTriangle"/> が投影の途中で埋める。
    /// 「変換前と変換後で意味が変わるフィールド」は本来きれいな設計ではないが、
    /// 頂点の型を2つに分けるほどの複雑さでもないので1つの型に同居させている。
    /// </summary>
    public float InvW;

    public Vertex(Vec3 position, Vec3 color)
        : this(position, color, Vec2.Zero)
    {
    }

    public Vertex(Vec3 position, Vec3 color, Vec2 texCoord)
    {
        Position = position;
        Color = color;
        TexCoord = texCoord;
        Normal = Vec3.UnitY;
        World = position;
        InvW = 1.0f;
    }

    public Vertex(float x, float y, float z, Vec3 color)
        : this(new Vec3(x, y, z), color)
    {
    }

    /// <summary>
    /// 0xAARRGGBB の色から頂点を作る便利メソッド。
    /// 色指定を今までどおり16進で書きたい場面のためのもの。
    /// </summary>
    public static Vertex FromPackedColor(float x, float y, float z, int color)
        => new(
            new Vec3(x, y, z),
            new Vec3(
                ((color >> 16) & 0xFF) / 255.0f,
                ((color >> 8) & 0xFF) / 255.0f,
                (color & 0xFF) / 255.0f));
}
