using System.Numerics;

namespace CpuRayTracer;

/// <summary>
/// 点光源。<b>大きさの無い1点</b>から、全方向へ同じ強さで光る。
///
/// <para>
/// 大きさが無いので、影の光線は1本で「見える / 見えない」が決まり、<b>影の縁はくっきり</b>になる(硬い影)。
/// 現実の光源には大きさがあり、縁は半影でぼける。それを出すには光源の上の何点もへ影の光線を出して平均する必要があり、
/// その「平均」が Day 60 のモンテカルロ積分の入口になる。
/// </para>
/// </summary>
/// <param name="Name">1画素を追ったときの表示名。</param>
/// <param name="Position">位置(m)。</param>
/// <param name="Color">光の色(線形、0〜1 程度)。</param>
/// <param name="Intensity">
/// 強さ(光度。W/sr にあたる)。距離 d の点に届く明るさは <c>Color × Intensity / d²</c>
/// (逆2乗則。光が球面に広がり、球の面積が d² に比例するため)。
/// </param>
internal readonly record struct PointLight(string Name, Vector3 Position, Vector3 Color, float Intensity);
