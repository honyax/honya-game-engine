using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 形の種類。**判定関数を選ぶための札**。
///
/// Day 43 の <see cref="RigidBody"/> は <c>Radius</c> を直接持っていた。
/// 形が球1つしか無いうちはそれで足りていたが、
/// 箱が入った途端に「半径」の意味が無くなる。
/// </summary>
internal enum ColliderKind
{
    /// <summary>球。中心と半径。**回転を考えなくてよい形**。</summary>
    Sphere,

    /// <summary>箱(OBB)。中心と各軸の半分の長さ、そして向き。**Day 44 の主役**。</summary>
    Box,

    /// <summary>無限に広い平面。床・壁。**必ず静的**。</summary>
    Plane,

    /// <summary>
    /// カプセル。中心と半径と半分の高さ、そして向き。**今日の主役**。
    ///
    /// 札を<b>いちばん最後に足した</b>のは、
    /// <see cref="Collider.InertiaLocal"/> や HUD の表示が
    /// <c>switch</c> の <c>default</c> に落ちる形で書いてあり、
    /// 途中に挿すと既存の値の意味が変わりうるため。
    /// 列挙の値を保存(Day 32 のシーン直列化)している場合はなおさらで、
    /// **札は末尾に足す**のが定石になる。
    /// </summary>
    Capsule,
}

/// <summary>
/// 形。**剛体から形を切り離した**もの(要点2)。Day 43 で予告した歪みの片付けでもある。
///
/// Day 43 の設計書にこう書いた——
/// 「形が箱・カプセル・地形と増える → <c>Collider</c> という型が要る」。
/// 今日それをやる。切り離したことで、Day 43 に残っていた歪みが2つ同時に消える。
///
/// <list type="number">
/// <item>
/// <b><c>RigidBody.Radius</c> が消えた</b>。箱に半径は無い。
/// 形ごとの寸法は全部ここに入り、体は「形を1つ持っている」だけになる。
/// </item>
/// <item>
/// <b><c>ContactPoint.B = -1</c>(相手が平面)の分岐が消えた</b>。
/// 平面も <see cref="ColliderKind.Plane"/> という形の1つになったので、
/// 床は「逆質量 0 の剛体が平面の形を持っている」だけ——
/// <see cref="PhysicsWorld"/> の解決から <c>b?.</c> が全部消えた。
/// </item>
/// </list>
///
/// <para>
/// Day 43 で「形が1つしか無いうちに抽象化すると、たいてい間違った形の抽象になる」
/// と書いて先送りしたが、**2つ目の形が来た今が作りどき**になる。
/// 実際、球しか無かった時点で <c>Collider</c> を作っていたら、
/// おそらく「半径を持つ基底クラス」のような形になっていた。
/// </para>
///
/// <para>
/// <b>クラスではなく構造体にしてある</b>。中身は数個の数だけで、
/// 継承したい振る舞いも無い。<c>SphereCollider : Collider</c> のような
/// クラス階層にすると、判定のたびに仮想呼び出しと型チェックが入り、
/// **N² 組を回す内側のループでいちばん高いもの**を毎回踏むことになる。
/// 種類の札(<see cref="Kind"/>)で分岐するほうが素直で速い——
/// Box2D も Bullet も、形の判定は結局こういう表引きになっている。
/// </para>
///
/// <para>
/// <b>使わないフィールドが残る</b>のがこの持ち方の代償。
/// 球のときに <see cref="HalfExtents"/> は 0 のまま眠っている。
/// 32 バイトなので気にしないことにした——
/// 共用体(<c>StructLayout(LayoutKind.Explicit)</c>)で重ねることもできるが、
/// 節約できる 12 バイトに対して読みにくさが釣り合わない。
/// </para>
/// </summary>
internal readonly struct Collider
{
    /// <summary>形の種類。**判定の分岐はこれだけを見る**。</summary>
    public readonly ColliderKind Kind;

    /// <summary>球の半径 [m]。球以外では 0。</summary>
    public readonly float Radius;

    /// <summary>
    /// 箱の各軸の**半分の長さ** [m]。箱以外では 0。
    ///
    /// 「半分」で持つのは、判定の式に出てくるのがいつも半分の側だから。
    /// 中心からの距離を比べるので、全長を持っていると
    /// 判定のたびに 2 で割ることになる。
    ///
    /// <para>
    /// <b>カプセルはここの Y だけを使う</b>(<see cref="HalfHeight"/>)。
    /// 専用のフィールドを増やしてもよかったが、
    /// カプセルの寸法は「半径」と「半分の高さ」の2つしかなく、
    /// 半径は <see cref="Radius"/> に席がある。残る1つのために
    /// 構造体を 4 バイト太らせるより、**箱と席を分け合う**ほうを採った。
    /// </para>
    /// </summary>
    public readonly Vector3 HalfExtents;

    /// <summary>
    /// 平面の法線を**物体座標で**持ったもの。平面以外では 0。
    ///
    /// 世界での法線は体の向きで回した先になる(<see cref="RigidBody.ToPlane"/>)。
    /// 平面は必ず静的なので実際には回らないが、
    /// 「形は物体座標、世界の姿は体が決める」という約束を
    /// 3つの形で揃えておくと、判定側が形の置き方を気にしなくてよくなる。
    /// </summary>
    public readonly Vector3 Normal;

    private Collider(ColliderKind kind, float radius, Vector3 halfExtents, Vector3 normal)
    {
        Kind = kind;
        Radius = radius;
        HalfExtents = halfExtents;
        Normal = normal;
    }

    /// <summary>球の形。</summary>
    public static Collider Sphere(float radius) =>
        new(ColliderKind.Sphere, radius, Vector3.Zero, Vector3.Zero);

    /// <summary>箱の形。**半分の長さ**で渡す(全長ではない)。</summary>
    public static Collider Box(Vector3 halfExtents) =>
        new(ColliderKind.Box, 0.0f, halfExtents, Vector3.Zero);

    /// <summary>箱の形(立方体)。</summary>
    public static Collider Box(float halfSize) => Box(new Vector3(halfSize));

    /// <summary>平面の形。法線は正規化してから持つ。</summary>
    public static Collider Plane(Vector3 normal) =>
        new(ColliderKind.Plane, 0.0f, Vector3.Zero, Vector3.Normalize(normal));

    /// <summary>
    /// カプセルの形。**半径と「線分の半分の長さ」**で渡す(Day 45)。
    ///
    /// <b><paramref name="halfHeight"/> は全高の半分ではない</b>のが引っかかりどころ。
    /// カプセルの全高は <c>2 * (halfHeight + radius)</c> になる——
    /// 線分の両端に半球が1つずつ生えるぶん、半径2つぶん背が伸びる。
    /// 身長 1.8m・半径 0.35m のキャラクターなら
    /// <c>halfHeight = (1.8 - 2*0.35) / 2 = 0.55</c>。
    /// <see cref="FromHeight"/> はこの計算を代わりにやってくれる。
    /// </summary>
    public static Collider Capsule(float radius, float halfHeight) =>
        new(ColliderKind.Capsule, radius, new Vector3(0.0f, halfHeight, 0.0f), Vector3.Zero);

    /// <summary>
    /// 全高と半径からカプセルを作る。**人型を作るときはこちら**。
    ///
    /// 全高が半径の2倍に足りないときは、半分の高さが 0 に潰れて**球になる**。
    /// エラーにせず球へ落とすのは、
    /// 「太くて低いカプセル」を作りかけたときに落ちるより、
    /// 素直に球として動いてくれたほうがデバッグしやすいから。
    /// </summary>
    public static Collider FromHeight(float radius, float totalHeight) =>
        Capsule(radius, MathF.Max((totalHeight * 0.5f) - radius, 0.0f));

    /// <summary>
    /// おおよその大きさ [m]。**中心からいちばん遠い点までの距離**。
    ///
    /// 描画のスケールや、Day 46 のブロードフェーズで
    /// 「この体はどれだけの箱に収まるか」を粗く見積もるのに使う。
    /// 平面は無限に広いので、意味のある値が返せない(0 を返す)。
    /// </summary>
    public float BoundingRadius => Kind switch
    {
        ColliderKind.Sphere => Radius,
        ColliderKind.Box => HalfExtents.Length(),
        ColliderKind.Capsule => HalfHeight + Radius,
        _ => 0.0f,
    };

    /// <summary>
    /// カプセルの**線分の半分の長さ** [m]。カプセル以外では 0。
    ///
    /// 席は <see cref="HalfExtents"/> の Y。名前を付けておかないと、
    /// 判定のあちこちに <c>Shape.HalfExtents.Y</c> と書くことになって、
    /// **箱の寸法を触っているように読めてしまう**。
    /// </summary>
    public float HalfHeight => HalfExtents.Y;

    /// <summary>カプセルの全高 [m]。<c>2 * (HalfHeight + Radius)</c>。</summary>
    public float TotalHeight => 2.0f * (HalfHeight + Radius);

    /// <summary>
    /// この形の慣性モーメントを、**主軸まわりの3つ**で返す(要点1)。
    ///
    /// 慣性テンソルは本来 3x3 だが、対称な形の主軸を座標軸に取れば対角行列になるので、
    /// 対角の3つだけで足りる(Day 43 の要点3)。
    ///
    /// <list type="bullet">
    /// <item>
    /// <b>球</b>: どの軸も <c>2/5 m r²</c>。**等方**——回しても慣性テンソルが変わらない。
    /// </item>
    /// <item>
    /// <b>箱</b>: x 軸まわりは <c>1/12 m (h² + d²)</c>(h, d は y, z 方向の<b>全長</b>)。
    /// 半分の長さ e で書き直すと全長は 2e なので <c>1/3 m (e_y² + e_z²)</c> になる。
    /// **x 軸まわりの回しにくさに、x 方向の長さが出てこない**のが要点——
    /// 回転軸に沿った方向にいくら伸ばしても、質量は軸から離れないので回しやすさは変わらない。
    /// 長い棒を軸のまわりに回すのが簡単で、横に振るのが大変なのはこれ。
    /// </item>
    /// <item>
    /// <b>カプセル</b>(Day 45): 円柱1本 + 半球2つに分けて、
    /// <b>平行軸の定理</b>で足し合わせる。値は2種類しか出てこない——
    /// 軸まわり(<c>I_y</c>)と、それに直交する2つ(<c>I_x = I_z</c>)。
    /// <b>軸まわりだけが極端に小さい</b>のがカプセルの性格で、
    /// 転がしたカプセルが必ず「横倒しでころころ転がる」のはこのため。
    /// </item>
    /// <item>
    /// <b>平面</b>: 0(静的にしか使わないので、慣性は使われない)。
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <b>球と箱の差が、Day 44 の絵の差になった</b>。球は等方なので
    /// <see cref="RigidBody.UpdateInertiaWorld"/> が何もしないのと同じだった。
    /// 箱は3つの値が違うので、**回すたびに世界での回しにくさが変わる**。
    /// Day 43 で「使わないうちから正しい形で書いておく」と言って
    /// <c>R^T D R</c> を書いておいたのが、今日ようやく効く。
    /// </para>
    /// </summary>
    public Vector3 InertiaLocal(float mass)
    {
        switch (Kind)
        {
            case ColliderKind.Sphere:
                return new Vector3(0.4f * mass * Radius * Radius);

            case ColliderKind.Box:
                {
                    Vector3 e = HalfExtents * HalfExtents;
                    const float third = 1.0f / 3.0f;

                    return new Vector3(
                        third * mass * (e.Y + e.Z),
                        third * mass * (e.Z + e.X),
                        third * mass * (e.X + e.Y));
                }

            case ColliderKind.Capsule:
                return CapsuleInertia(mass, Radius, HalfHeight);

            default:
                return Vector3.Zero;
        }
    }

    /// <summary>
    /// カプセルの慣性モーメント。**円柱 + 半球2つを、平行軸の定理で足す**(Day 45 の要点5)。
    ///
    /// カプセルは1本の式で書ける形ではないので、2つに割って足す。
    /// 質量は<b>体積の比で分ける</b>——密度が一様な物体なので、これで正しい。
    ///
    /// <code>
    ///   L = 2h(円柱の高さ)、r = 半径
    ///   円柱の体積 V_c = π r² L      半球2つ = 球の体積 V_s = 4/3 π r³
    ///   m_c = m · V_c/(V_c+V_s)、 m_s = m · V_s/(V_c+V_s)
    ///
    ///   I_y(軸まわり)   = 1/2 m_c r²  +  2/5 m_s r²
    ///   I_x = I_z        = m_c (L²/12 + r²/4)  +  m_s (2/5 r² + L²/4 + 3/8 L r)
    /// </code>
    ///
    /// <para>
    /// <b>半球の項の <c>L²/4 + 3/8 L r</c> が平行軸の定理</b>。
    /// 半球の重心は球の中心から <c>3r/8</c> ずれているので、
    /// 「球の中心まわり → 半球の重心まわり → カプセルの中心まわり」と
    /// 2段で運び直すと、ずれの2乗の差としてこの形が残る。
    /// <b>ここを <c>L²/4</c> だけにすると、太くて短いカプセルで数 % 狂う</b>——
    /// 絵では気づけないので、自己チェックで球と一致することを確かめてある。
    /// </para>
    ///
    /// <para>
    /// <b>h = 0 にすると球の <c>2/5 m r²</c> にぴたりと戻る</b>。
    /// 円柱の体積が 0 になって <c>m_c = 0</c>、<c>m_s = m</c> になり、
    /// I_y も I_x も <c>2/5 m r²</c> に落ちる。
    /// 式を書き終えたら必ず確かめておきたい種類の性質で、
    /// **境界で既知の答えに戻るか**は導出のいちばん安い検算になる。
    /// </para>
    /// </summary>
    private static Vector3 CapsuleInertia(float mass, float radius, float halfHeight)
    {
        if (mass <= 0.0f || radius <= 0.0f)
        {
            return Vector3.Zero;
        }

        float length = halfHeight * 2.0f;
        float r2 = radius * radius;

        // 体積で質量を分ける。**π は比を取ると消える**ので書かない。
        float cylinderVolume = r2 * length;
        float sphereVolume = (4.0f / 3.0f) * r2 * radius;
        float total = cylinderVolume + sphereVolume;

        float cylinderMass = mass * (cylinderVolume / total);
        float sphereMass = mass * (sphereVolume / total);

        // 軸まわり。円柱は 1/2 m r²、球は 2/5 m r²。**どちらも中心を通る軸**なので運び直しが要らない。
        float axial = (0.5f * cylinderMass * r2) + (0.4f * sphereMass * r2);

        // 軸に直交する向き。半球の項に平行軸の定理が効く。
        float lateral =
            (cylinderMass * (((length * length) / 12.0f) + (r2 / 4.0f)))
            + (sphereMass * ((0.4f * r2) + ((length * length) / 4.0f) + (0.375f * length * radius)));

        return new Vector3(lateral, axial, lateral);
    }
}
