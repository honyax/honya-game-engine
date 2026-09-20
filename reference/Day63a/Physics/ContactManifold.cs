using System.Numerics;
using System.Runtime.CompilerServices;

namespace HonyaEngine;

/// <summary>
/// この接触が**どの軸から出たか**。数字では見えないものを HUD に出すために持つ。
///
/// 分離軸定理は 15 本の軸を試して「いちばん重なりが浅い1本」を選ぶ(要点2)。
/// どれが選ばれたかで接触点の作り方がまるで変わるので、
/// **絵がおかしいときに最初に見る値**になる。
/// </summary>
internal enum ManifoldSource
{
    /// <summary>当たっていない。</summary>
    None,

    /// <summary>球が絡む接触。**点は必ず1つ**(球は1点でしか触れない)。</summary>
    Point,

    /// <summary>A の面が基準面。A の面に B がめり込んでいる。</summary>
    FaceA,

    /// <summary>B の面が基準面。</summary>
    FaceB,

    /// <summary>辺と辺。**点は1つ**。箱の角どうしが擦れ違うときに出る。</summary>
    EdgeEdge,
}

/// <summary>
/// マニフォールドの点1つ。**位置とめり込みの深さだけ**。
///
/// 法線はマニフォールド全体で共有する(<see cref="ContactManifold.Normal"/>)。
/// 面と面が触れているとき、4つの点は<b>同じ向きに</b>押し戻すべきで、
/// 点ごとに法線を持たせると、床に置いた箱が場所によって違う向きに押されて震える。
/// </summary>
internal readonly struct ManifoldPoint
{
    /// <summary>接触点(世界座標)。</summary>
    public readonly Vector3 Point;

    /// <summary>めり込みの深さ [m]。**正の値**。</summary>
    public readonly float Depth;

    public ManifoldPoint(Vector3 point, float depth)
    {
        Point = point;
        Depth = depth;
    }
}

/// <summary>
/// 接触点を4つ並べるためだけの入れ物。
///
/// <c>[InlineArray]</c> は「同じ型のフィールドを N 個並べた構造体」を
/// 配列のように添字で読み書きできるようにする C# の仕掛け。
/// **ヒープに何も置かない**のが値打ちで、
/// <c>ManifoldPoint[4]</c> と書くと接触1つごとに配列が1本作られ、
/// 数百組を毎ステップ回す物理ではそれがそのままゴミになる。
///
/// <para>
/// 構造体の中に配列を持ちたい、というだけの話なので、
/// 意味を考えるところではない。<c>fixed</c> バッファの安全版だと思っておけばよい。
/// </para>
/// </summary>
[InlineArray(ContactManifold.MaxPoints)]
internal struct ManifoldPointArray
{
    private ManifoldPoint _element0;
}

/// <summary>
/// 接触マニフォールド。**1組の物体が触れている「面」を、数点で代表したもの**(要点4)。
///
/// Day 43 の <see cref="Contact3D"/> は点が1つだった。球はどこで触れても1点なので、
/// それで足りていた。**箱では足りない**。
///
/// <para>
/// 床に置いた箱を1点で支えると、その1点まわりのトルクが立って箱が傾く。
/// 次のステップでは傾いた先の角が最深点になり、そこを1点で押し戻すのでまた傾く——
/// <b>箱が永久にガタガタと揺れ続ける</b>。
/// 面で触れているものは面で支えないと落ち着かない、というのが
/// マニフォールドが要る理由のすべて。
/// </para>
///
/// <para>
/// <b>点は4つまで</b>にしてある。凸多面体どうしの接触面は凸多角形なので、
/// 理屈のうえでは頂点がいくつでも出うる(箱どうしなら最大8つ)。
/// ただし<b>凸多角形を支えるのに要るのは4点</b>——
/// 面の重なりの外周から4つ取れば、残りはその内側に入る。
/// Box2D も Bullet も同じ上限で、実用上これで足りることが知られている。
/// </para>
///
/// <para>
/// <b>法線は1本だけ</b>持つ。面どうしの接触では、4点とも同じ向きに押し戻すのが正しい。
/// </para>
/// </summary>
internal struct ContactManifold
{
    /// <summary>接触点の上限。**4点**。</summary>
    public const int MaxPoints = 4;

    /// <summary>
    /// 押し戻す向き。**A を B から引き離す向き**で長さ1。
    ///
    /// Day 43 の <see cref="Contact3D.Normal"/> と同じ約束。
    /// ここを取り違えると物がめり込む方向へ飛ぶ。
    /// </summary>
    public Vector3 Normal;

    /// <summary>今入っている点の数。0 なら当たっていない。</summary>
    public int Count;

    /// <summary>どの軸から出た接触か。**HUD と自己チェック用**。</summary>
    public ManifoldSource Source;

    /// <summary>接触点(最大4つ)。</summary>
    public ManifoldPointArray Points;

    public readonly bool Hit => Count > 0;

    /// <summary>いちばん深い点の深さ [m]。**沈み具合の目安**。</summary>
    public readonly float MaxDepth
    {
        get
        {
            float deepest = 0.0f;
            for (int i = 0; i < Count; i++)
            {
                deepest = MathF.Max(deepest, Points[i].Depth);
            }

            return deepest;
        }
    }

    /// <summary>点が1つだけのマニフォールド。**球が絡む判定はこれで返る**。</summary>
    public static ContactManifold Single(in Contact3D contact, ManifoldSource source)
    {
        var manifold = new ContactManifold { Normal = contact.Normal, Source = source };
        manifold.Add(contact.Point, contact.Depth);
        return manifold;
    }

    /// <summary>
    /// 点を1つ足す。**満杯なら、いちばん浅い点と入れ替える**。
    ///
    /// 面と面をクリップすると点が最大8つ出る(要点5)。
    /// そこから4つ選ぶとき、いちばん素朴なのが「深い順に4つ」。
    ///
    /// <para>
    /// <b>これは最良の選び方ではない</b>。深さだけで選ぶと、
    /// 面のごく狭い一角に4点が固まることがある——
    /// そうなると面で支えているつもりが実質1点になり、また箱が揺れる。
    /// Box2D は「最深点1つ + そこからいちばん遠い点 + …」と
    /// <b>広がりを見て</b>選んでいる。
    /// 今日それを入れなかったのは、箱どうしの面接触で実際に出る点が
    /// たいてい4つ以下で、選び方が問題になる場面が少ないため
    /// (改造課題2でここを差し替える)。
    /// </para>
    /// </summary>
    public void Add(Vector3 point, float depth)
    {
        if (Count < MaxPoints)
        {
            Points[Count] = new ManifoldPoint(point, depth);
            Count++;
            return;
        }

        // 満杯。いちばん浅い点を探して、新しい点のほうが深ければ置き換える。
        int shallowest = 0;
        for (int i = 1; i < MaxPoints; i++)
        {
            if (Points[i].Depth < Points[shallowest].Depth)
            {
                shallowest = i;
            }
        }

        if (depth > Points[shallowest].Depth)
        {
            Points[shallowest] = new ManifoldPoint(point, depth);
        }
    }

    /// <summary>
    /// 点をいちばん深いものだけに絞る。**デモで「1点だとどうなるか」を見せるため**。
    ///
    /// <see cref="PhysicsWorld.MaxContactsPerPair"/> が 1 のときに呼ばれる。
    /// 実装の都合ではなく<b>実験のための道具</b>で、
    /// これを 1 にすると床の箱が落ち着かなくなるのが今日の見どころ。
    /// </summary>
    public void Reduce(int limit)
    {
        if (Count <= limit)
        {
            return;
        }

        // 深い順に前へ寄せる(選択ソート。高々4要素なので素朴でよい)。
        for (int i = 0; i < limit; i++)
        {
            int deepest = i;
            for (int j = i + 1; j < Count; j++)
            {
                if (Points[j].Depth > Points[deepest].Depth)
                {
                    deepest = j;
                }
            }

            (Points[i], Points[deepest]) = (Points[deepest], Points[i]);
        }

        Count = limit;
    }

    /// <summary>
    /// A と B を入れ替えた版。**法線を裏返して、種類の札も入れ替える**。
    ///
    /// 判定関数は「球と箱」「箱と平面」の順でしか書いていないので、
    /// 呼ぶ側が逆順で持っているときにこれで揃える(<see cref="Collision3D.Collide"/>)。
    /// <see cref="ManifoldSource.FaceA"/> と <see cref="ManifoldSource.FaceB"/> も
    /// 入れ替えないと、HUD の表示だけが嘘になる。
    /// </summary>
    public readonly ContactManifold Flipped()
    {
        ContactManifold flipped = this;
        flipped.Normal = -Normal;
        flipped.Source = Source switch
        {
            ManifoldSource.FaceA => ManifoldSource.FaceB,
            ManifoldSource.FaceB => ManifoldSource.FaceA,
            _ => Source,
        };

        return flipped;
    }
}
