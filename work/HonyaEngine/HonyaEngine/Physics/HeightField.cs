using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// 高さの格子(ハイトマップ)。**地形を「高さの関数」として持つ**(要点1)。
///
/// 格子点ごとに高さを1つだけ持つ。それだけで地形が表せる、というのが要点で、
/// <b>1つの (x, z) に対して高さが1つに決まる</b>——
/// つまり洞窟もオーバーハングも表せない代わりに、
/// 三角形メッシュに比べて桁違いに安く・小さく・速くなる。
///
/// <list type="bullet">
/// <item>
/// <b>容量</b>: 頂点1つが float 1個(4バイト)。
/// 一般の三角形メッシュなら位置(12)+ 法線(12)+ 索引で 30 バイト以上かかる。
/// 257×257 の地形が 264KB で持てる。
/// </item>
/// <item>
/// <b>探索</b>: 「この箱はどの三角形に触れうるか」が<b>割り算2回</b>で分かる
/// (<see cref="Terrain3D.CellRange"/>)。
/// 一般のメッシュなら BVH を組んで木を降りることになる。
/// </item>
/// <item>
/// <b>高さの問い合わせ</b>: 「この (x, z) の地面の高さは?」が
/// 補間1回で答えられる(<see cref="Terrain3D.TryHeightAt"/>)。
/// AI の経路探索・草の配置・カメラの下限——地形を使う側がいちばん欲しい問いがこれ。
/// </item>
/// </list>
///
/// <para>
/// <b>ここには「どこに置くか」が入っていない</b>。世界のどこに、どの高さに置くかは
/// <see cref="Terrain3D"/>(世界に置いた地形)の側が持つ。
/// <see cref="Sphere3D"/> や <see cref="Box3D"/> が世界座標の形で、
/// <see cref="Collider"/> が物体座標の寸法だったのと同じ分け方——
/// <b>データは1つ、置き場所はいくつでも</b>。
/// 同じ地形を並べてタイル状の世界を作れる、という値打ちもある。
/// </para>
///
/// <para>
/// <b>クラス(参照型)にしてある</b>のは、これだけは値で持てないから。
/// <see cref="Collider"/> は「数個の数だけ」の構造体として書いてきたが、
/// 高さの配列は数万要素になる。<b>今日はじめて、形が配列を持った</b>——
/// この非対称が Day 46 の設計でいちばん引っかかるところで、
/// 設計書の「形が値でなくなった日」に書いてある。
/// </para>
/// </summary>
internal sealed class HeightField
{
    private readonly float[] _heights;

    /// <summary>
    /// 高さの配列から作る。**格子点の数**(マスの数ではない)を渡す。
    ///
    /// <paramref name="columns"/> × <paramref name="rows"/> が格子点、
    /// マスは (columns-1) × (rows-1) になる。
    /// <b>1つずれるとマスが1列消える</b>ので、名前で区別できるようにしてある
    /// (<see cref="CellsX"/> / <see cref="CellsZ"/>)。
    /// </summary>
    public HeightField(float[] heights, int columns, int rows, float cellSize)
    {
        if (columns < 2 || rows < 2)
        {
            throw new ArgumentException("格子点は各軸2つ以上要る(マスが1つも作れない)");
        }

        if (heights.Length < columns * rows)
        {
            throw new ArgumentException("高さの数が格子点の数に足りない");
        }

        _heights = heights;
        Columns = columns;
        Rows = rows;
        CellSize = MathF.Max(cellSize, 1e-3f);

        // **最小と最大はここで1回だけ**。外接箱を作るたびに全部なめると、
        // 外接箱を毎ステップ見る側(Day 46b のブロードフェーズ)が数万要素を走査することになる。
        float min = float.MaxValue;
        float max = float.MinValue;

        for (int i = 0; i < columns * rows; i++)
        {
            min = MathF.Min(min, heights[i]);
            max = MathF.Max(max, heights[i]);
        }

        MinHeight = min;
        MaxHeight = max;
    }

    /// <summary>x 方向の格子点の数。</summary>
    public int Columns { get; }

    /// <summary>z 方向の格子点の数。</summary>
    public int Rows { get; }

    /// <summary>1マスの一辺 [m]。**正方形に決め打つ**。</summary>
    public float CellSize { get; }

    /// <summary>いちばん低い格子点の高さ [m]。</summary>
    public float MinHeight { get; }

    /// <summary>いちばん高い格子点の高さ [m]。</summary>
    public float MaxHeight { get; }

    /// <summary>x 方向のマスの数。**格子点より1つ少ない**。</summary>
    public int CellsX => Columns - 1;

    /// <summary>z 方向のマスの数。</summary>
    public int CellsZ => Rows - 1;

    /// <summary>x 方向の広がり [m]。</summary>
    public float SizeX => CellsX * CellSize;

    /// <summary>z 方向の広がり [m]。</summary>
    public float SizeZ => CellsZ * CellSize;

    /// <summary>三角形の総数。**マス1つにつき2枚**。</summary>
    public int TriangleCount => CellsX * CellsZ * 2;

    /// <summary>高さの配列そのもの。**描画用のメッシュを起こすのに使う**(読むだけ)。</summary>
    public ReadOnlySpan<float> Heights => _heights;

    /// <summary>
    /// 格子点の高さ [m]。**範囲外は端に丸める**。
    ///
    /// 丸めるのは、判定の側で「隣のマスがあるか」を毎回確かめずに済ませるため。
    /// 端のマスの外はずっと同じ高さの平地が続いているように見える——
    /// <b>地形の外へ出た物体が落ちない</b>代わりに、境界の処理が消える。
    /// (実際の判定は <see cref="Terrain3D.CellRange"/> が範囲外を弾くので、
    /// この丸めが効くのはメッシュを起こすときと高さの問い合わせだけ)
    /// </summary>
    public float HeightAt(int column, int row)
    {
        int cx = Math.Clamp(column, 0, Columns - 1);
        int cz = Math.Clamp(row, 0, Rows - 1);

        return _heights[(cz * Columns) + cx];
    }
}

/// <summary>
/// 世界に置いた地形。**<see cref="HeightField"/> に位置を与えたもの**。
///
/// <see cref="Sphere3D"/> や <see cref="Box3D"/> と同じ役どころで、
/// <b>世界座標で答える</b>のがこの型の仕事。
/// <see cref="RigidBody.ToTerrain"/> が体の位置と組み合わせて作る。
///
/// <para>
/// <see cref="Origin"/> は<b>格子の (0, 0) 隅</b>の世界座標。
/// 中心ではなく隅にしてあるのは、マスの番号が
/// <c>(x - Origin.X) / CellSize</c> の切り捨てで出るようにするため——
/// 中心を基準にすると、この式のあちこちに「半分」が挟まる。
/// </para>
///
/// <para>
/// <b>回らない</b>。地形は必ず軸に沿って置く。
/// 傾けたければ地形ではなく箱(<see cref="Box3D"/>)を使う、という割り切りで、
/// 実際のゲームエンジンでも Heightmap コライダは回転を持たない。
/// 高さが「1つの (x, z) に1つ」であることに全部が寄りかかっているので、
/// 回した瞬間にその前提が崩れる。
/// </para>
/// </summary>
internal readonly struct Terrain3D
{
    /// <summary>高さのデータ。</summary>
    public readonly HeightField Field;

    /// <summary>格子の (0, 0) 隅の世界座標。**高さの基準でもある**。</summary>
    public readonly Vector3 Origin;

    public Terrain3D(HeightField field, Vector3 origin)
    {
        Field = field;
        Origin = origin;
    }

    /// <summary>
    /// 地面より下へ、どこまでを「地形の受け持ち」とするか [m](要点3)。
    ///
    /// <b>地形には厚みが無い</b>。三角形は面なので、
    /// 1ステップで自分の厚みより深く潜った物体はそのまま抜けていく——
    /// 60Hz で 13m/s なら1ステップ 22cm 進むので、
    /// 直径 32cm のカプセルが山の上から落ちてくると<b>実際に抜ける</b>
    /// (200 体を落として1個だけ抜けた。自己チェックで見つかった)。
    ///
    /// <para>
    /// <b>外接箱を下へ厚くしておくと、抜けた物を拾い直せる</b>。
    /// <see cref="Collision3D.SphereTriangle"/> は地面の裏側にいる物体を
    /// 「真上から見て三角形の中なら押し上げる」と書いてあるが、
    /// 外接箱が地面にぴったり張り付いていると<b>そもそも組にならない</b>ので、
    /// この枝は一度も走らないまま物が落ちていく。
    /// 2m ぶん厚くしておけば、1ステップで抜けたものは必ず次のステップで戻る。
    /// </para>
    ///
    /// <para>
    /// <b>本当の直し方は掃引(スイープ)</b>——動く前と後を結んだ形で判定する。
    /// そちらは判定そのものを書き直すことになるので今日は入れず、
    /// <b>外接箱を厚くするという安いほうで手当てしてある</b>。
    /// 「抜けさせない」ではなく「抜けても戻す」という割り切りで、
    /// 地形が高さの関数だからこそ書ける手でもある。
    /// </para>
    /// </summary>
    public const float RecoveryDepth = 2.0f;

    /// <summary>
    /// 地形全体を包む AABB。**上は最大の高さ、下は最小の高さからさらに厚みぶん**。
    ///
    /// これが有限なので、地形は平面と違って
    /// <b>理屈のうえではブロードフェーズの格子に入れられる</b>。
    /// ただし1枚で世界じゅうを覆うので、入れると全部のマスに登録されて
    /// 格子の意味が無くなる——Day 46b の格子(<c>SpatialGrid3D</c>)が
    /// 「大きすぎるものは別扱い」で弾くのはそのため(Day 46b の要点2)。
    ///
    /// <para>
    /// <b>下だけ <see cref="RecoveryDepth"/> ぶん広い</b>のがこの箱の非対称なところ。
    /// 上に伸ばす理由は無い(地面の上に浮いている物は当たらない)が、
    /// 下は「抜けた物を拾い直す」ために要る。
    /// </para>
    /// </summary>
    public Aabb3D Bounds => new(
        Origin + new Vector3(0.0f, Field.MinHeight - RecoveryDepth, 0.0f),
        Origin + new Vector3(Field.SizeX, Field.MaxHeight, Field.SizeZ));

    /// <summary>格子点1つの世界座標。</summary>
    public Vector3 Vertex(int column, int row) => Origin + new Vector3(
        column * Field.CellSize,
        Field.HeightAt(column, row),
        row * Field.CellSize);

    /// <summary>
    /// マス1つを2枚に割った、その片方(要点1)。**割り方を1箇所に決めておく**。
    ///
    /// 4つの格子点は平面に乗っているとは限らないので、**必ず2枚の三角形に割る**。
    /// どちらの対角線で割るかは自由だが、<b>割り方を変えると地形の形が変わる</b>——
    /// 4隅の高さが同じでも、割り方しだいで尾根になったり谷になったりする。
    ///
    /// <code>
    ///        (0,1) --- (1,1)          割り方: (0,0)-(1,1) の対角線
    ///          |  \      |            0 枚目: (0,0) (0,1) (1,1)
    ///          |    \    |            1 枚目: (0,0) (1,1) (1,0)
    ///        (0,0) --- (1,0)
    /// </code>
    ///
    /// <para>
    /// <b>巻き順は上向きに揃えてある</b>。<see cref="Triangle3D.Normal"/> が
    /// 必ず +Y 側を向くので、「地面の表はどちら」を判定のたびに考えずに済む。
    /// 描画のメッシュ(<c>Primitives.CreateHeightField</c>)も同じ割り方・
    /// 同じ巻き順で作ってあり、<b>絵と当たり判定が1枚もずれない</b>。
    /// ここがずれると、見えている坂と登れる坂が食い違うという
    /// 最悪に分かりにくいバグになる。
    /// </para>
    /// </summary>
    /// <param name="cellX">マスの x 番号(0 〜 CellsX-1)。</param>
    /// <param name="cellZ">マスの z 番号(0 〜 CellsZ-1)。</param>
    /// <param name="which">0 か 1。マスを割った2枚のどちらか。</param>
    public Triangle3D Triangle(int cellX, int cellZ, int which)
    {
        Vector3 v00 = Vertex(cellX, cellZ);
        Vector3 v11 = Vertex(cellX + 1, cellZ + 1);

        return which == 0
            ? new Triangle3D(v00, Vertex(cellX, cellZ + 1), v11)
            : new Triangle3D(v00, v11, Vertex(cellX + 1, cellZ));
    }

    /// <summary>
    /// この AABB が触れうるマスの範囲。**割り算2回で決まる**(要点1)。
    ///
    /// 高さの格子がいちばん効くのがここ。一般の三角形メッシュなら
    /// 「どの三角形に触れうるか」を木構造で絞ることになるが、
    /// 格子なら<b>箱の x と z をマスの大きさで割るだけ</b>で範囲が出る。
    ///
    /// <para>
    /// <b>y は見ない</b>。地形は上下に薄いとは限らない(崖がある)ので、
    /// 高さで絞ろうとすると結局マスごとの高さを見ることになる。
    /// 絞るのは水平だけにして、高さは三角形との判定に任せるほうが素直。
    /// </para>
    ///
    /// <para>
    /// <c>(int)</c> の切り捨ては 0 方向へ働くので、負の値では1つずれる。
    /// ここでは <see cref="MathF.Floor"/> を通してから丸めている——
    /// 地形の原点より手前にいる物体は普通にいるので、
    /// Day 26 の 2D 格子(端に丸めるので実害が無かった)とは事情が違う。
    /// </para>
    /// </summary>
    /// <returns>1マスでも重なっていれば true。</returns>
    public bool CellRange(in Aabb3D box, out int x0, out int z0, out int x1, out int z1)
    {
        float inverse = 1.0f / Field.CellSize;

        x0 = (int)MathF.Floor((box.Min.X - Origin.X) * inverse);
        z0 = (int)MathF.Floor((box.Min.Z - Origin.Z) * inverse);
        x1 = (int)MathF.Floor((box.Max.X - Origin.X) * inverse);
        z1 = (int)MathF.Floor((box.Max.Z - Origin.Z) * inverse);

        // **完全に外に居るなら、そう答える**。
        // 切り詰めてから空かどうかを見ると、
        // 「端のマスに1枚だけ当たっている」と区別が付かない。
        if (x1 < 0 || z1 < 0 || x0 >= Field.CellsX || z0 >= Field.CellsZ)
        {
            return false;
        }

        x0 = Math.Max(x0, 0);
        z0 = Math.Max(z0, 0);
        x1 = Math.Min(x1, Field.CellsX - 1);
        z1 = Math.Min(z1, Field.CellsZ - 1);

        return true;
    }

    /// <summary>
    /// この (x, z) の地面の高さ。**三角形の割り方と完全に一致させる**(要点1)。
    ///
    /// 4隅を「双一次補間」する実装をよく見るが、<b>それは間違い</b>——
    /// 双一次補間の面は曲面(双曲放物面)で、
    /// 実際に判定に使っている2枚の平らな三角形とは一致しない。
    /// 対角線から離れるほどずれ、1マス 1m・高低差 1m の地形で最大 25cm ずれる。
    ///
    /// <para>
    /// <b>どちらの三角形の上にいるか</b>を先に決めてから、
    /// その平面の上で補間するのが正しい。マス内の位置 (u, w) が
    /// <c>w &gt;= u</c> なら 0 枚目、そうでなければ 1 枚目——
    /// <see cref="Triangle"/> の割り方(対角線 (0,0)-(1,1))から決まる。
    /// </para>
    ///
    /// <para>
    /// 使い道は判定ではなく<b>問い合わせ</b>。
    /// 「キャラクターの足元の地面の高さは?」「ここに木を植えたい」
    /// 「カメラが地面に潜らないようにしたい」——
    /// どれも判定を1回も走らせずに答えが出る。
    /// <b>地形を高さの格子で持つ最大の配当がこれ</b>で、
    /// 三角形メッシュの地形ではこの問いに答えるだけでレイキャストが要る。
    /// </para>
    /// </summary>
    /// <returns>地形の範囲内なら true。</returns>
    public bool TryHeightAt(float x, float z, out float height)
    {
        float inverse = 1.0f / Field.CellSize;
        float gx = (x - Origin.X) * inverse;
        float gz = (z - Origin.Z) * inverse;

        int cellX = (int)MathF.Floor(gx);
        int cellZ = (int)MathF.Floor(gz);

        if (cellX < 0 || cellZ < 0 || cellX >= Field.CellsX || cellZ >= Field.CellsZ)
        {
            height = 0.0f;
            return false;
        }

        // マスの中の位置(0〜1)。
        float u = gx - cellX;
        float w = gz - cellZ;

        float h00 = Field.HeightAt(cellX, cellZ);
        float h11 = Field.HeightAt(cellX + 1, cellZ + 1);

        // **対角線 (0,0)-(1,1) のどちら側か**で三角形が決まる。
        // どちらの式も (0,0) と (1,1) では同じ値を返すので、
        // 対角線をまたいでも高さが飛ばない。
        if (w >= u)
        {
            float h01 = Field.HeightAt(cellX, cellZ + 1);
            height = h00 + ((h01 - h00) * w) + ((h11 - h01) * u);
        }
        else
        {
            float h10 = Field.HeightAt(cellX + 1, cellZ);
            height = h00 + ((h10 - h00) * u) + ((h11 - h10) * w);
        }

        height += Origin.Y;
        return true;
    }

    /// <summary>
    /// この (x, z) の地面の向き。**その点が乗っている三角形の法線そのもの**。
    ///
    /// 格子点ごとの法線を補間して滑らかにする手もある(描画ではそうする)が、
    /// <b>判定に使う法線は面の法線でなければいけない</b>——
    /// 滑らかにした法線で押し戻すと、平らな面の上でも横向きの成分が混ざり、
    /// 物が勝手に滑り出す。
    ///
    /// <para>
    /// <see cref="CharacterController"/> の坂の上限(Day 45b の要点2)は
    /// この法線の Y 成分だけを見る。
    /// つまり<b>「登れる坂か」は三角形1枚ごとに決まる</b>ことになる。
    /// </para>
    /// </summary>
    /// <returns>地形の範囲内なら true。</returns>
    public bool TryNormalAt(float x, float z, out Vector3 normal)
    {
        float inverse = 1.0f / Field.CellSize;
        int cellX = (int)MathF.Floor((x - Origin.X) * inverse);
        int cellZ = (int)MathF.Floor((z - Origin.Z) * inverse);

        if (cellX < 0 || cellZ < 0 || cellX >= Field.CellsX || cellZ >= Field.CellsZ)
        {
            normal = Vector3.UnitY;
            return false;
        }

        float u = ((x - Origin.X) * inverse) - cellX;
        float w = ((z - Origin.Z) * inverse) - cellZ;

        normal = Triangle(cellX, cellZ, w >= u ? 0 : 1).Normal;
        return true;
    }
}
