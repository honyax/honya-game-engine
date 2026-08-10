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
    /// <summary>画面座標(ピクセル単位、小数可)。</summary>
    public Vec2 Position;

    /// <summary>頂点の色。0.0〜1.0 の RGB。</summary>
    public Vec3 Color;

    public Vertex(Vec2 position, Vec3 color)
    {
        Position = position;
        Color = color;
    }

    public Vertex(float x, float y, float r, float g, float b)
        : this(new Vec2(x, y), new Vec3(r, g, b))
    {
    }

    /// <summary>
    /// 0xAARRGGBB の色から頂点を作る便利メソッド。
    /// 色指定を今までどおり16進で書きたい場面のためのもの。
    /// </summary>
    public static Vertex FromPackedColor(float x, float y, int color)
        => new(
            new Vec2(x, y),
            new Vec3(
                ((color >> 16) & 0xFF) / 255.0f,
                ((color >> 8) & 0xFF) / 255.0f,
                (color & 0xFF) / 255.0f));
}
