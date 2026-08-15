namespace SoftwareRasterizer;

/// <summary>
/// 陰影の付け方(シェーディングモデル)。
///
/// 3つとも**光の計算式は同じ**で、違うのは「どこで計算するか」だけ。
/// この違いだけで見た目と負荷が大きく変わるのが Day 9 の見どころ。
/// </summary>
internal enum ShadingMode
{
    /// <summary>面ごとに1回だけ計算する。面が平らに塗られ、ポリゴンの境目が見える。</summary>
    Flat,

    /// <summary>頂点で計算して内部は色を補間する。安いが、ハイライトが頂点に引っ張られる。</summary>
    Gouraud,

    /// <summary>ピクセルごとに法線を補間して計算する。最も正しいが最も重い。</summary>
    Phong,
}

/// <summary>
/// 点光源。位置と色と強さを持つだけ。
/// </summary>
internal struct Light
{
    public Vec3 Position;

    public Vec3 Color;

    /// <summary>環境光。どこからともなく当たっている光の近似。</summary>
    public Vec3 Ambient;

    /// <summary>ハイライトの鋭さ。大きいほど小さく鋭い(つるつるした材質)。</summary>
    public float Shininess;

    /// <summary>鏡面反射の強さ。0 にすると完全につや消しになる。</summary>
    public float SpecularStrength;

    public static Light Default => new()
    {
        Position = new Vec3(3.0f, 4.0f, 3.0f),
        Color = new Vec3(1.0f, 0.97f, 0.90f),
        Ambient = new Vec3(0.12f, 0.13f, 0.18f),
        Shininess = 32.0f,
        SpecularStrength = 0.6f,
    };

    /// <summary>
    /// ある点の明るさを計算する。**今日の中心となる式**。
    ///
    /// 3つの成分を足し合わせる。
    ///
    /// 1. **環境光(ambient)**
    ///    どこからともなく当たっている光。本来は他の物体からの照り返しだが、
    ///    それを真面目に計算するのが大域照明(Day 36〜37)なので、
    ///    ここでは定数で近似する。これが無いと影の部分が真っ黒になって何も見えない。
    ///
    /// 2. **拡散反射(diffuse / ランバート反射)**
    ///    ざらざらした面が光をあらゆる方向へ均等に散らす成分。
    ///    明るさは **面の向き N と光の向き L の内積**だけで決まり、
    ///    見る方向にはよらない。「正面から当たれば明るく、斜めなら暗い」
    ///    という当たり前の現象が、内積1つで表せるのが気持ちいいところ。
    ///    内積が負(光が裏から当たっている)なら 0 にする。
    ///
    /// 3. **鏡面反射(specular / フォン反射)**
    ///    つるつるした面が光を鏡のように跳ね返す成分。こちらは**見る方向による**。
    ///    光が面で反射した方向 R と、視線の方向 V が近いほど強く光る。
    ///    R と V の内積を <see cref="Shininess"/> 乗すると、
    ///    近いときだけ急激に大きくなり、ハイライトが小さく鋭くなる。
    ///
    /// 物理的に正しい式ではない(エネルギー保存を満たしていない)。
    /// 「それらしく見える」ことを目的に1970年代に作られた経験的なモデルで、
    /// 物理ベースの正しい式は Day 35 の PBR で扱う。
    /// それでも今なお基礎として教えられるのは、この式の各項が
    /// 「何がどう効いているか」を切り分けて理解しやすいから。
    /// </summary>
    public readonly Vec3 Shade(Vec3 worldPosition, Vec3 normal, Vec3 albedo, Vec3 cameraPosition)
    {
        // 補間された法線は長さが1でないので、必ず正規化する。
        // 単位ベクトル同士を補間すると弦の中点になるため、必ず1より短くなる。
        Vec3 n = normal.Normalized();

        // この点から光源へ向かう方向。
        Vec3 toLight = (Position - worldPosition).Normalized();

        // --- 拡散反射(ランバート)---
        // 内積がそのまま「面がどれだけ光の方に向いているか」になる。
        float diffuse = MathF.Max(Vec3.Dot(n, toLight), 0.0f);

        // --- 鏡面反射(フォン)---
        float specular = 0.0f;
        if (diffuse > 0.0f)
        {
            // 光が裏から当たっている面にハイライトを出さないよう、
            // 拡散が 0 のときは計算しない(見た目の破綻を防ぐ実用上の処置)。
            Vec3 toCamera = (cameraPosition - worldPosition).Normalized();

            // 入射方向を面で反射させた向き。Vec3.Reflect は Day 5 で用意したもの。
            Vec3 reflected = Vec3.Reflect(-toLight, n);

            float alignment = MathF.Max(Vec3.Dot(reflected, toCamera), 0.0f);
            specular = MathF.Pow(alignment, Shininess) * SpecularStrength;
        }

        // 拡散反射は物の色(アルベド)に染まるが、鏡面反射は染まらない。
        // 赤いプラスチックのハイライトが白いのはこのため。
        return albedo * (Ambient + Color * diffuse) + Color * specular;
    }
}
