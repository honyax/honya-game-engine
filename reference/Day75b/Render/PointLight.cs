using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **点光源1つ**(Day 52)。位置・届く距離・色の3つだけを持つ。
///
/// <para>
/// Day 32 から光は<b>太陽1つ</b>だった。平行光源は向きしか持たず、
/// どこに居ても同じ強さで当たるので、「どの画素に効くか」を考える必要が無かった。
/// 点光源は違う——<b>近いものだけを照らす</b>。
/// この「近いものだけ」がディファードの話の出発点になる。
/// 光が何百個あっても、1画素に効くのはそのうち数個しかない。
/// </para>
///
/// <para>
/// <b>GL を知らない</b>のは <see cref="Pbr"/> と同じ判断。
/// シェーダに同じ式(<c>PointAttenuation</c>)があり、こちらは<b>検算用</b>。
/// 片方だけ直す壊し方を防ぐため、自己チェックが両方のシェーダの文字列を突き合わせる。
/// </para>
/// </summary>
/// <param name="Position">世界での位置 [m]。</param>
/// <param name="Radius">
/// **届く距離** [m]。ここで光がちょうど 0 になる(<see cref="Attenuation"/>)。
/// 物理の点光源はどこまでも届く(1/d² は 0 にならない)ので、これは<b>嘘</b>——
/// ただしディファードの球(ライトボリューム)を有限の大きさにするための、必要な嘘。
/// </param>
/// <param name="Color">
/// 色と強さをまとめたもの。**1.0 を超えてよい**(HDR。Day 31)。
/// 太陽の <c>uLightColor</c> と同じ単位で、距離 0 のときにこの値が来る。
/// </param>
internal readonly record struct PointLight(Vector3 Position, float Radius, Vector3 Color)
{
    /// <summary>
    /// **フォワードで一度に送れる数**。<c>textured.frag</c> の <c>MAX_FORWARD_LIGHTS</c> と同じ値。
    ///
    /// <para>
    /// 位置と色で vec4 を2本ずつ使うので、64 個で 128 本 = float 512 個。
    /// OpenGL 3.3 が画素シェーダに保証する uniform の枠は float 1024 個で、
    /// textured.frag の他の uniform(材質・影・IBL……)がすでに百個近く食っている。
    /// <b>保証の範囲で置けるのはこのあたりが限度</b>で、
    /// これがフォワードが多光源に弱い理由の、いちばん目に見える形になる。
    /// </para>
    /// </summary>
    public const int MaxForward = 64;

    /// <summary>
    /// **減衰**。<c>textured.frag</c> と <c>deferred.frag</c> の <c>PointAttenuation</c> と同じ式。
    ///
    /// <code>
    ///   窓   = saturate(1 - (d / r)^4)^2
    ///   減衰 = 窓 / (d^2 + 1)
    /// </code>
    ///
    /// <para>
    /// <b>1/d² が物理</b>。点から出た光は球面に広がるので、面積 4πd² で薄まる。
    /// 分母の <c>+ 1</c> は d → 0 で無限大に飛ぶのを止めるためのもの。
    /// </para>
    ///
    /// <para>
    /// <b>窓が今日の本題</b>。1/d² は 0 にならないので、そのままだと
    /// 「どの画素に効くか」が全画面になり、光の球を描く意味が無くなる。
    /// 窓を掛けて<b>半径ちょうどで 0 に落とす</b>と、球の外は1画素も塗らなくてよくなる。
    /// </para>
    ///
    /// <para>
    /// <b>4乗してから2乗する</b>のは、近くでは窓がほぼ 1 のまま(1/d² の形を崩さない)で、
    /// 端でだけ滑らかに 0 へ落ちるようにするため。
    /// 素朴に <c>1 - d/r</c> を掛けると、近くまで一様に暗くなって光が「小さく」見える。
    /// Brian Karis, "Real Shading in Unreal Engine 4"(SIGGRAPH 2013)の式で、
    /// 以後ほとんどのエンジンがこの形を使っている。
    /// </para>
    /// </summary>
    public static float Attenuation(float distance, float radius)
    {
        if (radius <= 0.0f || distance >= radius)
        {
            return 0.0f;
        }

        float ratio = distance / radius;
        float ratio4 = ratio * ratio * ratio * ratio;
        float window = Math.Clamp(1.0f - ratio4, 0.0f, 1.0f);

        return window * window / ((distance * distance) + 1.0f);
    }

    /// <summary>シェーダへ送る形。xyz = 位置、w = 届く距離。</summary>
    public Vector4 PositionAndRadius => new(Position, Radius);
}
