namespace HonyaEngine;

/// <summary>
/// **パーティクルの絵をコードで作る**(Day 49)。
///
/// <para>
/// 素材を増やさないための判断でもあるが、それ以上に
/// <b>パーティクルの絵は「形」ではなく「減衰の仕方」だ</b>ということが、
/// 作ってみるといちばんよく分かる。
/// 中心が明るくて縁へ滑らかに落ちる——それだけで火にも煙にも埃にも見える。
/// </para>
///
/// <para>
/// <b>縁を切り立たせない</b>のが唯一にして最大のコツになる。
/// 縁でアルファが急に 0 になると、粒が<b>円板</b>に見えて重なりが目立つ。
/// ここでは <c>(1 - r)²</c> で落としてあり、これだけで「もや」になる。
/// </para>
/// </summary>
internal static class ParticleTextures
{
    /// <summary>
    /// 中心が明るい丸(1コマ)。火花・炎・魔法。
    ///
    /// <para>
    /// RGB は白で固定して、色は<b>頂点色で付ける</b>(<see cref="ParticleEmitter.StartColor"/>)。
    /// テクスチャに色を焼くと、色違いのたびにテクスチャが要る——
    /// Day 17 のスプライトで「色は頂点で」と決めたのと同じ理屈になる。
    /// </para>
    /// </summary>
    public static byte[] CreateSoftDot(int size)
    {
        byte[] pixels = new byte[size * size * 4];
        float center = (size - 1) * 0.5f;
        float scale = 1.0f / (size * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - center) * scale;
                float dy = (y - center) * scale;
                float radius = MathF.Sqrt((dx * dx) + (dy * dy));

                // **2乗で落とす**。線形だと縁が見えて円板になる。
                float falloff = MathF.Max(0.0f, 1.0f - radius);
                float alpha = falloff * falloff;

                int i = ((y * size) + x) * 4;
                pixels[i + 0] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = (byte)(Math.Clamp(alpha, 0.0f, 1.0f) * 255.0f);
            }
        }

        return pixels;
    }

    /// <summary>
    /// **トレイルの断面**(Day 50)。中心の帯が濃く、上下の縁へ落ちる。
    ///
    /// <para>
    /// <b>横方向(U)には何も持たない</b>。トレイルの長さ方向の濃淡は
    /// <see cref="Trail.ColorAt"/>(頂点色)が付けるので、
    /// テクスチャが持つのは<b>幅方向の断面だけ</b>でよい。
    /// こうしておくと、尾を伸ばしても模様が引き伸ばされない——
    /// 「長さは頂点、断面はテクスチャ」と役割を分けた結果になる。
    /// </para>
    ///
    /// <para>
    /// 縁を <c>(1 - d)³</c> で落としてあるのは <see cref="CreateSoftDot"/> より1乗ぶん強い。
    /// リボンは面積が広いので、2乗だと<b>帯の縁がはっきり出て板に見える</b>。
    /// </para>
    /// </summary>
    public static byte[] CreateTrailStrip(int size)
    {
        byte[] pixels = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            // 中心が 0、上下の縁が 1。
            float distance = MathF.Abs((((y + 0.5f) / size) * 2.0f) - 1.0f);
            float falloff = MathF.Max(0.0f, 1.0f - distance);
            float alpha = falloff * falloff * falloff;

            var value = (byte)(Math.Clamp(alpha, 0.0f, 1.0f) * 255.0f);

            for (int x = 0; x < size; x++)
            {
                int i = ((y * size) + x) * 4;
                pixels[i + 0] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
                pixels[i + 3] = value;
            }
        }

        return pixels;
    }

    /// <summary>
    /// **広がりながら薄れる煙のコマ送り**。<paramref name="columns"/> × <paramref name="rows"/> の升に並べる。
    ///
    /// <para>
    /// コマは<b>左上から右へ、次の行へ</b>と並べる。
    /// OpenGL のテクスチャは配列の先頭行が <c>v = 0</c> なので、
    /// メモリ上の並び順がそのまま <c>v</c> の順になる——
    /// <see cref="ParticleRenderer"/> の UV 計算はこの前提で書いてある。
    /// </para>
    ///
    /// <para>
    /// 1コマぶんの絵は「半径が伸びて、縁がほどけて、全体が薄くなる」の3つだけ。
    /// ほどけ具合は<b>角度と半径から作った擬似ノイズ</b>で出している。
    /// 乱数表を持たないのは、**コマ間で連続していないと煙がちらつく**ため——
    /// 角度の関数にしておけば、隣のコマでも同じ向きが同じようにほどける。
    /// </para>
    /// </summary>
    public static byte[] CreateSmokeFlipbook(int cell, int columns, int rows)
    {
        int width = cell * columns;
        int height = cell * rows;
        byte[] pixels = new byte[width * height * 4];

        int frames = columns * rows;
        float center = (cell - 1) * 0.5f;
        float scale = 1.0f / (cell * 0.5f);

        for (int frame = 0; frame < frames; frame++)
        {
            int originX = (frame % columns) * cell;
            int originY = (frame / columns) * cell;

            // コマの進み具合(0→1)。
            float t = frames <= 1 ? 0.0f : frame / (float)(frames - 1);

            // 半径は 0.35 から 1.0 へ。**最初から大きいと「湧いた」感じが出ない**。
            float radius = 0.35f + (0.65f * t);

            // 全体の濃さ。**薄くはするが 0 にはしない**。
            //
            // ここで 0 まで落とすと、<see cref="ParticleEmitter.EndColor"/> の
            // アルファ 0 と<b>二重に掛かって</b>、煙が最初から最後までほとんど見えなくなる。
            // 実際、最初は `1 - t` にしていて「煙が出ない」と悩んだ(計画書の要点3)。
            //
            // **コマ送りが持つのは形、消え方は色のカーブが持つ**、と分けておく。
            // どちらか一方に寄せないと、片方を調整したときにもう片方が効いて訳が分からなくなる。
            float fade = 1.0f - (0.55f * t);

            // ほどけ具合。時間が経つほど縁が荒れる。
            float ripple = 0.08f + (0.22f * t);

            for (int y = 0; y < cell; y++)
            {
                for (int x = 0; x < cell; x++)
                {
                    float dx = (x - center) * scale;
                    float dy = (y - center) * scale;
                    float r = MathF.Sqrt((dx * dx) + (dy * dy));
                    float angle = MathF.Atan2(dy, dx);

                    // 角度で縁を波打たせる。3本と7本の波を重ねると、
                    // 規則正しさが消えて煙らしくなる(**周波数を互いに素にする**)。
                    float wobble =
                        (MathF.Sin((angle * 3.0f) + (t * 2.0f)) * 0.6f)
                        + (MathF.Sin((angle * 7.0f) - (t * 3.0f)) * 0.4f);

                    float edge = radius * (1.0f + (wobble * ripple));

                    float falloff = MathF.Max(0.0f, 1.0f - (r / MathF.Max(0.01f, edge)));
                    float alpha = falloff * falloff * fade;

                    int i = (((originY + y) * width) + originX + x) * 4;
                    pixels[i + 0] = 255;
                    pixels[i + 1] = 255;
                    pixels[i + 2] = 255;
                    pixels[i + 3] = (byte)(Math.Clamp(alpha, 0.0f, 1.0f) * 255.0f);
                }
            }
        }

        return pixels;
    }
}
