namespace SoftwareRasterizer;

/// <summary>
/// 頂点。位置と、その位置に紐づく「属性」を持つ。
///
/// ラスタライザの仕事は、突き詰めると
/// **「3頂点が持っている属性を、三角形内部の各ピクセルへ配り直すこと」**に尽きる。
/// 今日は属性が色だけだが、この先
///   Day 8 … テクスチャ座標 (U, V)
///   Day 9 … 法線ベクトル
/// が同じように増えていく。そして増えても補間のコードは同じ形のまま
/// (重み x 頂点の値の足し算)なので、この構造を今日のうちに作っておく。
///
/// 色を byte ではなく float 0.0〜1.0 で持つ理由:
///   - 補間の途中で丸めが入らない(byte だと 0〜255 の刻みで誤差が出る)
///   - Day 9 のライティングで「明るさを掛ける」計算が自然に書ける
///     (0.5倍の明るさ = 0.5を掛けるだけ。byte だと桁あふれと丸めの扱いが要る)
///   - 1.0 を超える値(まぶしい光)を一時的に持てる。最後に画面へ出すときだけ丸める
/// GPUのシェーダーが色を float で扱うのも同じ理由。
///
/// R, G, B をバラの float で持っているのは Day 5 までのつなぎ。
/// Day 5 で Vec3 を自作したら、位置も色もそれに置き換わる。
/// </summary>
internal struct Vertex
{
    /// <summary>画面座標 X。Day 6 で変換行列を通すようになると実数になる。</summary>
    public int X;

    /// <summary>画面座標 Y。</summary>
    public int Y;

    public float R;

    public float G;

    public float B;

    public Vertex(int x, int y, float r, float g, float b)
    {
        X = x;
        Y = y;
        R = r;
        G = g;
        B = b;
    }

    /// <summary>
    /// 0xAARRGGBB の色から頂点を作る便利メソッド。
    /// 色指定を今までどおり16進で書きたい場面のためのもの。
    /// </summary>
    public static Vertex FromPackedColor(int x, int y, int color)
        => new(
            x,
            y,
            ((color >> 16) & 0xFF) / 255.0f,
            ((color >> 8) & 0xFF) / 255.0f,
            (color & 0xFF) / 255.0f);
}
