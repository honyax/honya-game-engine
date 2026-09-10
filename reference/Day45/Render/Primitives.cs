using System.Numerics;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// よく使う形を作るところ。Day 15 で <c>Program</c> に置いていた <c>CreateQuad</c> の引っ越し先。
///
/// 形の定義はシーンの構成とは無関係なので、<c>Program</c> に置いておく理由が無い。
/// 「立方体が欲しい」たびに24頂点を手で並べるのも現実的ではない。
/// Day 20 以降にモデルを読み込むようになっても、
/// **動作確認用の素直な形**は要り続けるので、ここに残す。
/// </summary>
internal static class Primitives
{
    /// <summary>
    /// XY 平面に置いた 1x1 の正方形。中心が原点。
    /// 床にするときは X 軸まわりに -90 度回して寝かせる。
    /// </summary>
    public static Mesh<Vertex> CreateQuad(GL gl)
    {
        Vector4 white = Vector4.One;

        // 板は +Z を向いているので、法線は4頂点とも同じ(Day 32 で足した)。
        Vector3 normal = Vector3.UnitZ;

        // 接線(Day 34)。**U が増える向き**を入れる。
        // この板は左下 (0,0) → 右下 (1,0) で U が増えるので、+X がそのまま接線になる。
        //
        // w = +1 は「従接線 = cross(N, T) で合っている」の意味。
        // 確かめると cross((0,0,1), (1,0,0)) = (0,1,0) = +Y で、
        // 左下 → 左上 で V が増える向きと一致する。**合っているか必ず手で確かめる**——
        // 符号を間違えても絵は出て、凹凸だけが裏返る。
        var tangent = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);

        ReadOnlySpan<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0.0f), new Vector2(0.0f, 0.0f), white, normal, tangent),   // 左下
            new(new Vector3(0.5f, -0.5f, 0.0f), new Vector2(1.0f, 0.0f), white, normal, tangent),    // 右下
            new(new Vector3(0.5f, 0.5f, 0.0f), new Vector2(1.0f, 1.0f), white, normal, tangent),     // 右上
            new(new Vector3(-0.5f, 0.5f, 0.0f), new Vector2(0.0f, 1.0f), white, normal, tangent),    // 左上
        ];

        ReadOnlySpan<uint> indices = [0, 1, 2, 2, 3, 0];

        return new Mesh<Vertex>(gl, vertices, indices, Vertex.Attributes);
    }

    /// <summary>
    /// 1辺 1 の立方体。中心が原点。
    ///
    /// **頂点は 8 個ではなく 24 個**になる。
    /// 立方体の角は3つの面が共有しているが、面ごとに UV が違うので
    /// 「1つの頂点が持てる UV は1組」という制約に引っかかる。
    /// Day 10 の OBJ ローダで「位置/UV/法線 の組み合わせごとに頂点を作る」と
    /// 書いたのとまったく同じ事情で、頂点は**位置ではなく属性の組で数える**。
    /// (法線を持つようになる Day 32 以降は、面ごとに向きが違うのでなおさら分けられない)
    ///
    /// 面の向きは**外から見て反時計回り(CCW)**にそろえてある。
    /// OpenGL の既定では CCW が表面なので、これで背面カリングが正しく効く。
    /// 1面でも順序を間違えると、その面だけ**内側から覗いたときに見える**という
    /// 分かりやすい壊れ方をするので、C キーでカリングを切って確かめられる。
    /// </summary>
    public static Mesh<Vertex> CreateCube(GL gl)
    {
        var vertices = new List<Vertex>(24);
        var indices = new List<uint>(36);

        // 面ごとに色味を変えておく。テクスチャに掛け算されるので、
        // 立方体の面の切れ目が見分けやすくなる。
        Vector4 front = new(1.00f, 0.55f, 0.55f, 1.0f);   // +Z 赤
        Vector4 back = new(0.55f, 1.00f, 0.65f, 1.0f);    // -Z 緑
        Vector4 right = new(0.60f, 0.70f, 1.00f, 1.0f);   // +X 青
        Vector4 left = new(1.00f, 0.95f, 0.55f, 1.0f);    // -X 黄
        Vector4 top = new(1.00f, 1.00f, 1.00f, 1.0f);     // +Y 白
        Vector4 bottom = new(0.70f, 0.70f, 0.75f, 1.0f);  // -Y 灰

        const float h = 0.5f;

        // 各面、外から見て「左下 → 右下 → 右上 → 左上」の順に渡す。
        AddFace(vertices, indices,
            new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h), front);
        AddFace(vertices, indices,
            new Vector3(h, -h, -h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(h, h, -h), back);
        AddFace(vertices, indices,
            new Vector3(h, -h, h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(h, h, h), right);
        AddFace(vertices, indices,
            new Vector3(-h, -h, -h), new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(-h, h, -h), left);
        AddFace(vertices, indices,
            new Vector3(-h, h, h), new Vector3(h, h, h), new Vector3(h, h, -h), new Vector3(-h, h, -h), top);
        AddFace(vertices, indices,
            new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, -h, h), new Vector3(-h, -h, h), bottom);

        return new Mesh<Vertex>(gl, vertices.ToArray(), indices.ToArray(), Vertex.Attributes);
    }

    /// <summary>
    /// 半径 0.5 の球(直径 1)。中心が原点。**UV 球**(緯度と経度で刻む素直な作り)。
    ///
    /// Day 35 で足した。理由は<b>材質を見比べるには球しかない</b>から。
    /// 立方体は面ごとに法線が一定なので、1つの面の中でハイライトが動かない——
    /// 粗さを変えてもハイライトの広がりが見えず、金属度の効きも読み取れない。
    /// 球は<b>法線があらゆる向きを1個の中で通る</b>ので、
    /// フレネル(縁ほど明るい)も、粗さ(ハイライトの広がり)も、
    /// 1個の絵に全部出る。PBR の解説がどれも球を並べるのはこのため。
    ///
    /// <para>
    /// UV 球の弱点は極が詰まること(三角形が細く潰れる)。
    /// 均等に割りたければ正二十面体を分割する測地球にするが、
    /// <b>UV が素直に取れない</b>ので今日はこちらを取った——
    /// 法線マップを貼れる形にしておきたい(Day 34)。
    /// </para>
    ///
    /// <para>
    /// <b>接線は導関数から出す</b>。Day 34 の板や立方体は
    /// 「左下 → 右下 が U」と手で決められたが、曲面ではそうはいかない。
    /// U が増える向き = <c>∂P/∂u</c> をそのまま計算するのが本来の定義で、
    /// 球なら経度方向の接ベクトルになる。
    /// </para>
    ///
    /// <para>
    /// <b>この球は w = -1 になる</b>。手で決めた板・立方体は +1 だったので、
    /// **Day 34 で作った w の仕組みが初めて実際に効く**形になっている。
    /// 理由は下のコメントに書いた。
    /// </para>
    /// </summary>
    /// <param name="slices">経度方向の分割数(横)。</param>
    /// <param name="stacks">緯度方向の分割数(縦)。</param>
    public static Mesh<Vertex> CreateSphere(GL gl, int slices = 48, int stacks = 32)
    {
        var vertices = new List<Vertex>((slices + 1) * (stacks + 1));
        var indices = new List<uint>(slices * stacks * 6);

        Vector4 white = Vector4.One;

        // **経度は 0〜slices(slices+1 本)**。最後の1本は最初と同じ位置だが、
        // U が 0 と 1 で違うので別の頂点にする必要がある
        // (立方体の頂点が 8 個ではなく 24 個だったのと同じ事情)。
        for (int y = 0; y <= stacks; y++)
        {
            // v = 0 を下、v = 1 を上にする。θ は上から測るので (1 - v)。
            float v = (float)y / stacks;
            float theta = (1.0f - v) * MathF.PI;
            float sinTheta = MathF.Sin(theta);
            float cosTheta = MathF.Cos(theta);

            for (int x = 0; x <= slices; x++)
            {
                float u = (float)x / slices;
                float phi = u * MathF.Tau;
                float sinPhi = MathF.Sin(phi);
                float cosPhi = MathF.Cos(phi);

                // 単位球の上の点。**位置がそのまま法線**になるのが球の便利なところ。
                var normal = new Vector3(sinTheta * cosPhi, cosTheta, sinTheta * sinPhi);

                // ∂P/∂u。φ で微分すると (-sinφ, 0, cosφ) に比例する。
                // **極でも長さが 0 にならない**ので、正規化して安全に使える
                // (∂P/∂v のほうは極で潰れるので、こちらを接線に選ぶのが正しい)。
                var tangent = new Vector3(-sinPhi, 0.0f, cosPhi);

                // **w = -1 になる**。確かめ方は Day 34 と同じで、
                // cross(N, T) が「V の増える向き」と合うかを見る。
                //   赤道の φ=0 で N = (1,0,0)、T = (0,0,1)
                //   cross(N, T) = (0,-1,0) = 下向き
                // V は上へ増えるので、符号を反転しないと合わない。
                //
                // 板と立方体は +1 だったので、**今日はじめて -1 が出る**。
                // ここを +1 のままにすると、球に法線マップを貼ったときだけ
                // 凹凸が上下反転する——絵が出てしまうぶん気づきにくい壊れ方をする。
                var tangent4 = new Vector4(tangent, -1.0f);

                vertices.Add(new Vertex(normal * 0.5f, new Vector2(u, v), white, normal, tangent4));
            }
        }

        int stride = slices + 1;

        for (int y = 0; y < stacks; y++)
        {
            for (int x = 0; x < slices; x++)
            {
                uint bottomLeft = (uint)((y * stride) + x);
                uint bottomRight = bottomLeft + 1;
                uint topLeft = bottomLeft + (uint)stride;
                uint topRight = topLeft + 1;

                // 外から見て反時計回り(CCW)。立方体と同じ約束にそろえてある。
                //
                // **並びが「左下 → 左上 → 右上」になる**のは、
                // U が増える向き(経度)と V が増える向き(緯度)の外積が
                // 球の内側を向くため。手で決めずに、
                // cross(BR - BL, TR - BL) が外を向くかを実際に計算して決めた——
                // 逆にするとカリングで全部消える(C キーで切れば見えるので、
                // 「消えた = 巻き順」と切り分けられる)。
                indices.Add(bottomLeft);
                indices.Add(topLeft);
                indices.Add(topRight);
                indices.Add(topRight);
                indices.Add(bottomRight);
                indices.Add(bottomLeft);
            }
        }

        return new Mesh<Vertex>(gl, vertices.ToArray(), indices.ToArray(), Vertex.Attributes);
    }

    /// <summary>
    /// Y 軸に沿った円柱。半径 0.5、高さ 1、中心が原点(Day 45)。
    ///
    /// <b>カプセルを描くために足した</b>。カプセルは「球を線分に沿って滑らせたもの」なので、
    /// 絵のほうも<b>球2つ + 円柱1本</b>で描ける——
    /// <see cref="Collision3D.CapsuleBox"/> が判定を球に落として解いているのと
    /// まったく同じ分け方になっている。
    ///
    /// <para>
    /// <b>カプセル用のメッシュを作らないのはなぜか</b>。カプセルは
    /// 半径と半分の高さの2つで形が決まるので、寸法ごとにメッシュが要る。
    /// <b>非一様な拡大では正しく描けない</b>のが理由で、
    /// Y だけ引き伸ばすと半球が楕円に潰れてしまう。
    /// 一方この分け方なら、球は一様に拡大(半径)、
    /// 円柱は<b>XZ に半径・Y に高さ</b>という素直な拡大で済み、
    /// **どんな寸法のカプセルでもメッシュ2本で描ける**。
    /// ドローコールは1体あたり3回になるが、
    /// カプセルは画面に数本しか出ないので釣り合う。
    /// </para>
    ///
    /// <para>
    /// <b>フタも張る</b>。カプセルとして使うぶんには球で隠れて見えないが、
    /// 円柱だけを単体で置いたときに中が透けると
    /// 「メッシュが壊れている」と見分けが付かない。
    /// 側面と同じ頂点を使い回さないのは、<b>法線が違う</b>から——
    /// 側面の法線は横を向き、フタの法線は上下を向く。
    /// 立方体が 8 頂点ではなく 24 頂点だったのと同じ事情(Day 12)。
    /// </para>
    /// </summary>
    /// <param name="slices">円周方向の分割数。</param>
    public static Mesh<Vertex> CreateCylinder(GL gl, int slices = 32)
    {
        var vertices = new List<Vertex>((slices + 1) * 2 + (slices + 2) * 2);
        var indices = new List<uint>(slices * 12);

        Vector4 white = Vector4.One;

        // --- 側面 ---
        //
        // 経度は 0〜slices(slices+1 本)。**最後の1本は最初と同じ位置**だが、
        // U が 0 と 1 で違うので別の頂点にする(球と同じ事情)。
        for (int y = 0; y < 2; y++)
        {
            float v = y;
            float height = (v - 0.5f);   // -0.5 か +0.5

            for (int x = 0; x <= slices; x++)
            {
                float u = (float)x / slices;
                float phi = u * MathF.Tau;
                float sinPhi = MathF.Sin(phi);
                float cosPhi = MathF.Cos(phi);

                // 側面の法線は**真横**。上下の成分を持たない。
                var normal = new Vector3(sinPhi, 0.0f, cosPhi);
                var position = new Vector3(sinPhi * 0.5f, height, cosPhi * 0.5f);

                // ∂P/∂u。φ で微分すると (cosφ, 0, -sinφ)。
                // 球(<see cref="CreateSphere"/>)と同じ理由で w = -1 になる。
                var tangent = new Vector4(cosPhi, 0.0f, -sinPhi, -1.0f);

                vertices.Add(new Vertex(position, new Vector2(u, v), white, normal, tangent));
            }
        }

        int stride = slices + 1;

        for (int x = 0; x < slices; x++)
        {
            uint bottomLeft = (uint)x;
            uint bottomRight = bottomLeft + 1;
            uint topLeft = bottomLeft + (uint)stride;
            uint topRight = topLeft + 1;

            // 外から見て反時計回り(CCW)。球と同じ巻き順に揃えてある。
            indices.Add(bottomLeft);
            indices.Add(topLeft);
            indices.Add(topRight);
            indices.Add(topRight);
            indices.Add(bottomRight);
            indices.Add(bottomLeft);
        }

        // --- フタ2枚 ---
        //
        // 中心の1点から扇状に張る(トライアングルファン相当を
        // インデックスで組む)。
        for (int cap = 0; cap < 2; cap++)
        {
            bool top = cap == 1;
            float height = top ? 0.5f : -0.5f;
            Vector3 normal = top ? Vector3.UnitY : -Vector3.UnitY;
            var tangent = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);

            uint center = (uint)vertices.Count;
            vertices.Add(new Vertex(
                new Vector3(0.0f, height, 0.0f), new Vector2(0.5f, 0.5f), white, normal, tangent));

            for (int x = 0; x <= slices; x++)
            {
                float phi = (float)x / slices * MathF.Tau;
                float sinPhi = MathF.Sin(phi);
                float cosPhi = MathF.Cos(phi);

                vertices.Add(new Vertex(
                    new Vector3(sinPhi * 0.5f, height, cosPhi * 0.5f),
                    new Vector2((sinPhi + 1.0f) * 0.5f, (cosPhi + 1.0f) * 0.5f),
                    white,
                    normal,
                    tangent));
            }

            for (int x = 0; x < slices; x++)
            {
                uint a = center + 1 + (uint)x;
                uint b = a + 1;

                // **上下で巻き順が逆**。どちらも「外から見て CCW」にしたいので、
                // 上フタと下フタでは頂点の並べる向きが入れ替わる。
                if (top)
                {
                    indices.Add(center);
                    indices.Add(b);
                    indices.Add(a);
                }
                else
                {
                    indices.Add(center);
                    indices.Add(a);
                    indices.Add(b);
                }
            }
        }

        return new Mesh<Vertex>(gl, vertices.ToArray(), indices.ToArray(), Vertex.Attributes);
    }
    /// <summary>四角形1面ぶんの頂点4つとインデックス6つを足す。</summary>
    private static void AddFace(
        List<Vertex> vertices,
        List<uint> indices,
        Vector3 bottomLeft,
        Vector3 bottomRight,
        Vector3 topRight,
        Vector3 topLeft,
        Vector4 color)
    {
        uint baseIndex = (uint)vertices.Count;

        // **法線は面から計算する**(Day 32)。
        // 立方体の頂点が 8 個ではなく 24 個なのは面ごとに UV が違うからだったが、
        // 法線も面ごとに違うので、いずれにせよ分けるしかなかった
        // (角を共有させると、その頂点の法線は3面の平均になり、
        //  立方体の角が丸く陰影付けされてしまう)。
        //
        // 外積の順は「反時計回りに並んだ3点」から外向きが出るように取る。
        // 渡される4点は外から見て CCW という約束なので、
        // (右下 - 左下) x (左上 - 左下) が外を向く。
        Vector3 normal = Vector3.Normalize(
            Vector3.Cross(bottomRight - bottomLeft, topLeft - bottomLeft));

        // **接線も面から出る**(Day 34)。渡される4点は「左下 → 右下 → 右上 → 左上」で、
        // UV も同じ順に (0,0) → (1,0) → (1,1) → (0,1) を割り当てているので、
        // **左下 → 右下 が U の増える向き**そのものになる。
        //
        // w = +1 でよいことは、板(CreateQuad)と同じ確かめ方でどの面でも成り立つ——
        // cross(N, T) が「左下 → 左上」を向く。UV の割り当てを1面でも変えると崩れるので、
        // 面ごとに UV を変えたくなったときはここも見直すこと。
        Vector3 tangent = Vector3.Normalize(bottomRight - bottomLeft);
        var tangent4 = new Vector4(tangent, 1.0f);

        vertices.Add(new Vertex(bottomLeft, new Vector2(0.0f, 0.0f), color, normal, tangent4));
        vertices.Add(new Vertex(bottomRight, new Vector2(1.0f, 0.0f), color, normal, tangent4));
        vertices.Add(new Vertex(topRight, new Vector2(1.0f, 1.0f), color, normal, tangent4));
        vertices.Add(new Vertex(topLeft, new Vector2(0.0f, 1.0f), color, normal, tangent4));

        // 四角形を三角形2枚に割る。渡された順が CCW なら、この並びも CCW になる。
        indices.Add(baseIndex + 0);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 2);
        indices.Add(baseIndex + 3);
        indices.Add(baseIndex + 0);
    }
}
