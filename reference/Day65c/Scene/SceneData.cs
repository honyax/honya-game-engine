using System.Numerics;

namespace MeshletRenderer;

/// <summary>
/// 場面の組み立て。<b>同じトーラスノットを格子状に何体も並べる</b>だけ。
///
/// <para>
/// 形は1つしか作らず、置き方(行列)だけを体の数ぶん用意する——<b>インスタンシング</b>。
/// メッシュレットも1体ぶんだけ作ればよく、GPU には「このメッシュレットを、この行列で」と
/// 2つの番号の組で頼む(メッシュシェーダのワークグループの x がメッシュレット、y が体)。
/// </para>
/// <para>
/// 数を変えられるのは Day 62 の「球の数」と同じ狙いで、<b>三角形が増えたときに
/// 2つの道の値段がどう伸びるか</b>を見る。
/// </para>
/// </summary>
internal static class SceneData
{
    /// <summary>体の数(1 / 2 / 3 / 4 キー)。格子の一辺は 1 / 4 / 8 / 16。</summary>
    public static readonly int[] Presets = [1, 16, 64, 256];

    /// <summary>格子の間隔(m)。トーラスノットの差し渡しは 3m 弱なので、少しだけ隙間が空く。</summary>
    public const float Spacing = 4.0f;

    /// <summary>
    /// 起動時と <c>R</c> キーの視点。1体のときは寄り、それ以外は<b>格子の中に立って斜めに見渡す</b>。
    /// 見渡す視点では画面の外(横と後ろ)にも体が並んでいる——Day 64b で「画面の外を捨てる」が効くのはこのため。
    /// 1体のときに注視点を少し上げてあるのは、画面の上を HUD が覆うので、形を下に寄せて見せたいから。
    /// </summary>
    public static OrbitView DefaultView(int count) => count == 1
        ? new OrbitView(new Vector3(0.0f, 0.9f, 0.0f), 0.6f, 0.35f, 6.0f, 50.0f)
        : new OrbitView(Vector3.Zero, 0.6f, 0.30f, 14.0f, 50.0f);

    /// <summary>
    /// 低い視点(Day 65c で追加。<c>V</c> キー)。既定の視点から、見下ろす角度だけを 0.30 → 0.12 ラジアン(約 7 度)に下げる。
    /// 目の高さは 1.7m ほどになり、格子の奥へ向かって体が何列も重なって見える——
    /// **手前の体の管が奥の体を隠す**ので、Hi-Z で捨てられるものが一気に増える(Day 65c の検証2)。
    /// </summary>
    public static OrbitView LowView(int count) => DefaultView(count) with { Pitch = 0.12f };

    /// <summary>
    /// 体ごとの置き方(模型 → 世界の行列)を作る。<b>毎回同じ並び</b>になるよう乱数の種を固定する。
    /// </summary>
    public static Matrix4x4[] CreateInstances(int count)
    {
        int side = (int)MathF.Round(MathF.Sqrt(count));
        var random = new Random(64);
        var instances = new Matrix4x4[side * side];

        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                // 向きをばらつかせる。全部同じ向きだと、どの体も同じ面をこちらに向けて
                // 「背面のメッシュレット」がどれも同じになってしまう(Day 64b で効く)。
                float yaw = random.NextSingle() * MathF.PI * 2.0f;
                float tilt = (random.NextSingle() - 0.5f) * 0.8f;
                var position = new Vector3((x - (side - 1) * 0.5f) * Spacing, 0.0f, (z - (side - 1) * 0.5f) * Spacing);

                // 行ベクトルの流儀なので、先に掛けたものが先に効く(傾ける → 回す → 置く)。
                instances[z * side + x] = Matrix4x4.CreateRotationX(tilt)
                    * Matrix4x4.CreateRotationY(yaw)
                    * Matrix4x4.CreateTranslation(position);
            }
        }

        return instances;
    }
}
