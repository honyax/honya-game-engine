using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 反射・屈折・フレネルの式。<b>形も場面も知らない純粋な関数だけ</b>を置く。
///
/// <para>
/// ここを独立させておく理由は2つ。
/// </para>
/// <list type="number">
/// <item><b>窓を開かずに確かめられる</b>。自己チェック(C キー)の半分はこのクラスだけを相手にしている</item>
/// <item><b>Day 61 で GLSL へそのまま写せる</b>。どれも vec3 と float だけで書ける式にしてある</item>
/// </list>
///
/// <para>
/// 向きの約束: <b>d は面へ向かう入射方向</b>(光線の Direction そのもの)、
/// <b>n は入射側を向いた法線</b>(<c>d·n &lt; 0</c>)。どちらも長さ 1。
/// 内側から当たった光線では、呼ぶ側が先に n を裏返してから渡す(<see cref="WhittedTracer"/>)。
/// </para>
/// </summary>
internal static class Optics
{
    /// <summary>
    /// 鏡の反射(要点3)。<c>r = d − 2(d·n)n</c>。
    ///
    /// d を「面に沿う成分」と「面に垂直な成分 <c>(d·n)n</c>」に分けると、
    /// 反射は<b>垂直な成分だけを反転する</b>ことだと分かる。反転 = 2回引く、なので係数が 2 になる。
    /// Day 9 のスペキュラ(<c>Vec3.Reflect</c>)と同じ式。
    /// </summary>
    public static Vector3 Reflect(Vector3 d, Vector3 n) => d - n * (2.0f * Vector3.Dot(d, n));

    /// <summary>
    /// スネルの法則による屈折(要点4)。<paramref name="eta"/> は <b>入射側の屈折率 ÷ 透過側の屈折率</b>(n1 / n2)。
    ///
    /// <para>
    /// スネルの法則 <c>n1 sinθi = n2 sinθt</c> を、角度を使わずベクトルで書いたもの。
    /// 屈折した向きも「面に沿う成分」と「面に垂直な成分」に分けて作る。
    /// </para>
    /// <list type="bullet">
    /// <item>面に沿う成分 … 入射の沿う成分 <c>d + cosθi n</c> を <b>eta 倍</b>する(sin が eta 倍になる、の直訳)</item>
    /// <item>面に垂直な成分 … 長さ 1 に足りない分を、<b>面の奥へ</b> <c>−cosθt n</c> として補う</item>
    /// </list>
    /// <para>
    /// まとめると <c>t = eta d + (eta cosθi − cosθt) n</c>。
    /// <c>sin²θt = eta² (1 − cos²θi)</c> が 1 を超えたら透過の角度が存在しない——<b>全反射</b>で、false を返す。
    /// 全反射は n1 &gt; n2(ガラスから空気へ出る)ときにしか起きない。
    /// </para>
    /// </summary>
    public static bool TryRefract(Vector3 d, Vector3 n, float eta, out Vector3 refracted)
    {
        float cosI = -Vector3.Dot(d, n);
        float sin2T = eta * eta * (1.0f - cosI * cosI);
        if (sin2T > 1.0f)
        {
            refracted = Vector3.Zero;
            return false;
        }

        float cosT = MathF.Sqrt(1.0f - sin2T);
        refracted = d * eta + n * (eta * cosI - cosT);
        return true;
    }

    /// <summary>
    /// 誘電体(ガラス・水)の<b>正確な</b>フレネル反射率(要点5)。偏光していない光として、S 波と P 波の平均を取る。
    ///
    /// <para>
    /// 光は面に当たると、一部が跳ね返り、残りが中へ入る。跳ね返る割合 F は<b>角度で変わる</b>。
    /// ガラスを真正面から見ると 4% しか反射しないが、かすめる角度では 100% に近づく
    /// (水面を遠くまで見渡すと空が映るのはこれ)。
    /// </para>
    /// <para>
    /// 全反射の角度では 1 を返す。<b>屈折率が同じ(n1 = n2)ならどの角度でも 0</b> になり、
    /// 境目は無いのと同じになる(場面2の「屈折率 1.00」の玉が見えないのはこのため。自己チェック6・9)。
    /// </para>
    /// </summary>
    /// <param name="cosI">入射角の cos(法線と、入射方向を逆にした向きの内積)。0〜1。</param>
    public static float FresnelDielectric(float cosI, float n1, float n2)
    {
        // 屈折率が同じなら境目は無い。下の式どおりに計算しても 1e-15 ほどの値になる(平方根の丸め誤差)が、
        // ちょうど 0 を返しておくと、呼ぶ側が「反射の割合が 0 より大きいときだけ反射の光線を出す」で枝をまるごと省ける。
        if (n1 == n2)
        {
            return 0.0f;
        }

        cosI = Math.Clamp(cosI, 0.0f, 1.0f);
        float sinT = n1 / n2 * MathF.Sqrt(1.0f - cosI * cosI);
        if (sinT >= 1.0f)
        {
            return 1.0f;
        }

        float cosT = MathF.Sqrt(1.0f - sinT * sinT);

        // S 波(振動が入射面に垂直)と P 波(入射面の中)で、跳ね返る振幅の比が違う。
        // 反射率は振幅の2乗。自然光は両方を半分ずつ含むので平均する。
        float rs = (n1 * cosI - n2 * cosT) / (n1 * cosI + n2 * cosT);
        float rp = (n2 * cosI - n1 * cosT) / (n2 * cosI + n1 * cosT);
        return 0.5f * (rs * rs + rp * rp);
    }

    /// <summary>
    /// 誘電体のフレネル反射率の<b>シュリック近似</b>(要点5)。<c>F = F0 + (1 − F0)(1 − cosθ)⁵</c>。
    ///
    /// <para>
    /// <b>cosθ には、屈折率の低い側(空気側)の角度を使う</b>。空気からガラスへ入るなら入射角、
    /// ガラスから空気へ出るなら<b>透過角</b>。RTIOW は内側からでも入射角を使っていて、
    /// 臨界角の手前で反射率を大きく見誤る(臨界角 41.8° の手前 41.5° で、正確には 0.54 のところを 0.04 と出す。自己チェック7)。
    /// 反射率は光の行きと帰りで同じ(入射角 θi で入る光と、透過角 θt で出てくる光は同じ割合で跳ね返る)なので、
    /// 空気側の角度で聞けば、外からでも内からでも同じ近似が使える。こう使うと正確な式との差は、外からでも内からでも最大 0.036 に収まる。
    /// </para>
    /// </summary>
    public static float SchlickDielectric(float cosI, float n1, float n2)
    {
        float r0 = (n1 - n2) / (n1 + n2);
        r0 *= r0;

        float cos = Math.Clamp(cosI, 0.0f, 1.0f);
        if (n1 > n2)
        {
            float sin2T = n1 / n2 * (n1 / n2) * (1.0f - cos * cos);
            if (sin2T >= 1.0f)
            {
                return 1.0f;
            }

            cos = MathF.Sqrt(1.0f - sin2T);
        }

        float m = 1.0f - cos;
        return r0 + (1.0f - r0) * (m * m * m * m * m);
    }

    /// <summary>
    /// 垂直に見たときの誘電体の反射率 <c>F0 = ((n1 − n2) / (n1 + n2))²</c>。
    /// フレネルを切ったとき(F キーで「なし」)は、角度によらずこの値を使う。
    /// </summary>
    public static float NormalIncidenceReflectance(float n1, float n2)
    {
        float r0 = (n1 - n2) / (n1 + n2);
        return r0 * r0;
    }

    /// <summary>
    /// 金属のフレネル(シュリック近似)。<paramref name="f0"/> は<b>垂直に見たときの反射色</b>。
    ///
    /// <para>
    /// 金属は光を中へ通さない(入った光はすぐ吸収される)ので、屈折の枝が無い。
    /// 代わりに<b>反射の色</b>を持つ——金が黄色く、銅が赤いのは、色ごとに跳ね返る割合が違うから。
    /// かすめる角度ではどの金属も白に近づく(<c>(1 − cos)⁵</c> の項で F0 が 1 に引っ張られる)。
    /// 金属の正確なフレネルには複素屈折率が要るので、今日はシュリックだけにする。
    /// </para>
    /// </summary>
    public static Vector3 SchlickConductor(float cosI, Vector3 f0)
    {
        float m = 1.0f - Math.Clamp(cosI, 0.0f, 1.0f);
        float m5 = m * m * m * m * m;
        return f0 + (Vector3.One - f0) * m5;
    }
}
