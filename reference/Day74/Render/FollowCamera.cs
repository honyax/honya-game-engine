using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **三人称の追従カメラ**(Day 51)。今日の主役その1。
///
/// <para>
/// Day 45 でキャラクターを動かし始めたとき、カメラは
/// <c>_orbit.Target = Character.Position + (0, 1, 0)</c> の1行だった。
/// 「画面外へ出ない」だけは満たしていて、遊ぶには足りない——
/// <b>キャラクターと同じ速さで注視点が飛ぶ</b>ので、段差を登った瞬間に絵が跳ね、
/// 壁際まで下がると<b>カメラが壁の中に潜って世界の裏側が見える</b>。
/// </para>
///
/// <para>
/// <b>足すのは3つだけ</b>。
/// </para>
/// <list type="number">
/// <item><b>遅れて追う</b> … 注視点を指数平滑で寄せる。跳ねが均される</item>
/// <item><b>肩越しに置く</b> … 足元ではなく胸のあたりを見る(<see cref="HeightOffset"/>)</item>
/// <item><b>壁に入ったら寄る</b> … 目の位置まで球を進めて、当たったところで止める</item>
/// </list>
///
/// <para>
/// <b>マウスは受け取らない</b>。回す・寄る操作は Day 15 の
/// <see cref="OrbitCameraController"/> がとうに持っているので、
/// 方位・仰角・距離は<b>引数でもらう</b>。
/// 今日足すのは「注視点をどう決めるか」と「目線をどこで止めるか」の2つだけで、
/// 入力の受け口をもう1つ作る理由が無い。
/// </para>
///
/// <para>
/// <b>物理を知らない</b>のがこのクラスの線引き。
/// 壁に入ったかどうかは <see cref="Update"/> に渡す
/// <c>blocked</c>(「その点に半径 r の球を置いたら何かに埋まるか」)が答える。
/// <see cref="DemoScene.Load"/> が <c>resolveAsset</c> を外からもらって
/// 「どこを探すか」を持ち込まなかったのと同じ形で、
/// <b>Render の層が Physics の層を知らないまま</b>でいられる。
/// </para>
/// </summary>
internal sealed class FollowCamera
{
    /// <summary>
    /// 注視点を足元からどれだけ上に置くか [m]。
    /// **足元を見るとキャラクターが画面の上半分に張り付く**ので、胸のあたりを狙う。
    /// </summary>
    public float HeightOffset { get; set; } = 0.55f;

    /// <summary>
    /// 追従の速さ [1/s]。**0 で即座に追う**(Day 45 の挙動)。
    ///
    /// <para>
    /// 大きいほど速く寄る。10 なら 1 フレーム(1/60秒)で残りの
    /// 15% ほどを詰める勘定になり、段差を登ったときの跳ねが2〜3フレームで均される。
    /// 小さくしすぎると、走り出したときにキャラクターが画面の端へ逃げる。
    /// </para>
    /// </summary>
    public float Smoothing { get; set; } = 10.0f;

    /// <summary>壁寄せをするか(メニューで切れる)。</summary>
    public bool CollisionEnabled { get; set; } = true;

    /// <summary>壁寄せで使う球の半径 [m]。**近クリップ面より大きく取る**。</summary>
    public float CollisionRadius { get; set; } = 0.25f;

    /// <summary>
    /// 目の位置まで何回に分けて試すか。**多いほど正確で、多いほど問い合わせが増える**。
    ///
    /// <para>
    /// 12 回なら距離 6m を 50cm 刻みで見ることになる。
    /// 刻みより細い柱はすり抜けるが、そのぶんは球の半径(25cm)が拾ってくれる。
    /// </para>
    /// </summary>
    public int CollisionSteps { get; set; } = 12;

    /// <summary>これ以上は寄らない距離 [m]。**0 まで寄せるとキャラクターの中に入る**。</summary>
    public float MinDistance { get; set; } = 0.8f;

    /// <summary>いま見ている点。**遅れて追う側**。</summary>
    public Vector3 Target { get; private set; }

    /// <summary>いまの目の位置。</summary>
    public Vector3 Eye { get; private set; }

    /// <summary>壁寄せのあとの距離 [m]。</summary>
    public float AppliedDistance { get; private set; }

    /// <summary>壁寄せが無ければこうだった距離 [m]。</summary>
    public float WantedDistance { get; private set; }

    /// <summary>いま壁に寄せられているか。**HUD の1行に出す**——絵からは読めない。</summary>
    public bool Blocked => AppliedDistance < WantedDistance - 0.01f;

    /// <summary>直前の <see cref="Update"/> で球を何回置いたか。**問い合わせの代償**。</summary>
    public int LastProbeCount { get; private set; }

    /// <summary>
    /// 遅れを捨てて、いますぐその場所へ。
    /// **瞬間移動のあとに呼ぶ**——呼ばないと、出発点へ戻した瞬間に
    /// カメラが世界を横断してゆっくり飛んでくる。
    /// </summary>
    public void Snap(Vector3 focus) =>
        Target = focus + new Vector3(0.0f, HeightOffset, 0.0f);

    /// <summary>
    /// 1フレームぶん進める。**可変 dt で呼ぶ**(見せ方だけの処理なので Day 19 の線引きどおり)。
    ///
    /// <para>
    /// <b>寄せ方に <c>1 - exp(-k·dt)</c> を使う</b>のが今日の細かい要点。
    /// よくある <c>Lerp(now, wanted, 0.1f)</c> は<b>フレームレートで速さが変わる</b>——
    /// 144fps では 60fps の 2.4 倍の回数だけ寄るので、同じ 0.1 でも追従が別物になる。
    /// 指数の形にしておくと、<b>「1秒でどれだけ残るか」が dt によらず一定</b>になる。
    /// </para>
    ///
    /// <para>
    /// <b>寄せるのは注視点だけ</b>で、方位・仰角・距離は生のまま使う。
    /// マウスで回している最中に角度まで遅らせると、
    /// <b>手の動きにカメラが付いてこない</b>という、いちばん嫌な種類の遅れになる。
    /// 遅らせてよいのは「勝手に動くもの」だけ、というのがここの線引き。
    /// </para>
    /// </summary>
    /// <param name="focus">追う点(キャラクターの足元)。</param>
    /// <param name="yaw">方位 [rad]。<see cref="OrbitCameraController.Yaw"/> をそのまま。</param>
    /// <param name="pitch">仰角 [rad]。</param>
    /// <param name="distance">寄せる前の距離 [m]。</param>
    /// <param name="blocked">
    /// 「その点に半径 r の球を置いたら何かに埋まるか」。
    /// <c>null</c> なら壁寄せをしない。**このクラスが物理を知らないための1本**。
    /// </param>
    /// <param name="deltaSeconds">可変 dt。</param>
    public void Update(
        Vector3 focus,
        float yaw,
        float pitch,
        float distance,
        Func<Vector3, float, bool>? blocked,
        float deltaSeconds)
    {
        Vector3 wanted = focus + new Vector3(0.0f, HeightOffset, 0.0f);

        if (Smoothing <= 0.0f || deltaSeconds <= 0.0f)
        {
            Target = wanted;
        }
        else
        {
            // **フレームレートに依らない寄せ方**(このメソッドの説明)。
            // t は「このフレームで詰める割合」。dt が2倍なら、残りが2乗ぶん減る。
            float t = 1.0f - MathF.Exp(-Smoothing * deltaSeconds);
            Target = Vector3.Lerp(Target, wanted, t);
        }

        WantedDistance = distance;
        LastProbeCount = 0;

        AppliedDistance = CollisionEnabled && blocked is not null
            ? Sweep(yaw, pitch, distance, blocked)
            : distance;

        Eye = OrbitCameraController.EyePosition(Target, AppliedDistance, yaw, pitch);
    }

    /// <summary>
    /// 決まった注視点と目の位置を <see cref="Camera"/> に書き込む。
    ///
    /// <para>
    /// <b>平行投影の高さも書く</b>のは <see cref="OrbitCameraController.Apply"/> と同じ。
    /// 書かないと、透視 ⇔ 平行を切り替えたときに見かけの大きさが揃わない。
    /// </para>
    /// </summary>
    public void Apply(Camera camera)
    {
        camera.Target = Target;
        camera.Position = Eye;
        camera.OrthographicHeight =
            2.0f * AppliedDistance * MathF.Tan(camera.FieldOfView * 0.5f);
    }

    /// <summary>
    /// 注視点から目の位置へ球を進めて、**最初にぶつかった手前**で止める。
    ///
    /// <para>
    /// 本物のエンジンはここでスフィアキャスト(球の掃引)を使う。
    /// うちの <see cref="PhysicsWorld"/> は「その場所で埋まっているか」しか答えられないので、
    /// <b>刻んで何度も置く</b>ことで代用する——
    /// Day 46 のブロードフェーズが効いているので、1回の問い合わせは足元のマスだけで済む。
    /// </para>
    ///
    /// <para>
    /// <b>手前から順に見る</b>のが大事。奥から見て「当たっていない最初の点」を探すと、
    /// 壁の向こう側の空間を「空いている」と答えてしまう
    /// (薄い壁を挟んだ部屋の中にカメラが置かれる)。
    /// </para>
    ///
    /// <para>
    /// <b>注視点そのものが埋まっていたら、いちばん寄せる</b>。
    /// キャラクターが箱にめり込んだフレームでは全部の点が埋まるので、
    /// 「1つも空いていない = <see cref="MinDistance"/>」という答えを用意しておかないと、
    /// カメラが元の距離に留まって壁の中に残る。
    /// </para>
    /// </summary>
    private float Sweep(float yaw, float pitch, float distance, Func<Vector3, float, bool> blocked)
    {
        int steps = Math.Max(CollisionSteps, 1);
        float free = MinDistance;

        for (int i = 1; i <= steps; i++)
        {
            float sample = MinDistance + ((distance - MinDistance) * i / steps);
            Vector3 point = OrbitCameraController.EyePosition(Target, sample, yaw, pitch);

            LastProbeCount++;

            if (blocked(point, CollisionRadius))
            {
                // **1つ手前まで**。当たった点そのものに置くと、球の半径ぶんめり込む。
                break;
            }

            free = sample;
        }

        return free;
    }
}
