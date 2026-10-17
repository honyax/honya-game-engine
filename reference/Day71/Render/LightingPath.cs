namespace HonyaEngine;

/// <summary>
/// **光の当て方**(Day 53)。Day 52 まではフォワードかディファードかの2つで、<c>bool</c> で足りていた。
/// 3つ目(Forward+)が来たので選択肢にする。
///
/// <para>
/// 3つとも<b>同じ絵を描く</b>(同じ BRDF・同じ減衰・同じ影と IBL)。違うのは
/// 「どの画素に、どの光を、何回回すか」の段取りだけで、それが代償の形を決める。
/// </para>
///
/// <list type="table">
/// <listheader><term>描き方</term><description>
/// 画素ごとに回す光 / 光の数の上限 / ジオメトリを描く回数 / 半透明 / 余分に持つもの
/// </description></listheader>
/// <item><term><see cref="Forward"/></term><description>
/// <b>全部</b> / 64 個(uniform の枠)/ 3 回 / 描ける / なし
/// </description></item>
/// <item><term><see cref="Deferred"/></term><description>
/// 球が覆った光だけ / なし / <b>2 回</b> / <b>描けない</b> / G-Buffer 15.8MB
/// </description></item>
/// <item><term><see cref="ForwardPlus"/></term><description>
/// <b>その升目にかかる光だけ</b> / なし / 3 回 / 描ける / 光の一覧 数十KB + CPU の振り分け
/// </description></item>
/// </list>
///
/// <para>
/// <b>Forward+ は「フォワードのまま、ディファードの儲けどころだけを取る」</b>。
/// ディファードが速いのは「光が届く画素だけを塗る」からで、G-Buffer はそのための手段にすぎない。
/// 画面を升目に切って「この升目に届く光はこれだけ」と先に選り分けておけば、
/// 同じことがフォワードの画素シェーダの中でできる——表面を中間データに書き出さずに。
/// </para>
/// </summary>
internal enum LightingPath
{
    /// <summary>Day 32〜51 の描き方。点光源は全部を uniform で渡し、画素ごとに全部回す(Day 52)。</summary>
    Forward,

    /// <summary>Day 52。表面を G-Buffer に書き、光は球を描いて当てる。</summary>
    Deferred,

    /// <summary>
    /// Day 53。<b>フォワードの画素シェーダ</b>が、自分の升目(クラスター)にかかる光だけを回す。
    /// 升目への振り分けは CPU が毎フレームやる(<see cref="ClusterGrid"/>)。
    /// </summary>
    ForwardPlus,
}
