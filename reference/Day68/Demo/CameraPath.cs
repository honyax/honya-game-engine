using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// カメラパスの補間方式(<c>Ctrl+Alt+F4</c> で切り替える)。
///
/// <para>
/// **見比べるためだけに2つ持っている**。
/// 出来上がりの絵はどちらでも似たようなものだが、
/// <b>キーの上を通過する瞬間の速度</b>がまるで違う。
/// </para>
/// </summary>
internal enum CameraInterpolation
{
    /// <summary>
    /// 隣り合うキーを直線で結ぶ。位置は繋がる(C0)が、**速度は繋がらない**(C1 でない)——
    /// キーを通過する瞬間に向きが折れるので、パンが「カクッ」と曲がる。
    /// </summary>
    Linear,

    /// <summary>
    /// Catmull-Rom スプライン。前後1つずつを覗いて接線を決めるので、
    /// **キーの上で速度が繋がる**(C1)。カメラワークの既定はこちら。
    /// </summary>
    CatmullRom,
}

/// <summary>
/// **カメラワーク**(Day 40)。今日の主役その1。
///
/// <para>
/// Day 39 で決めの構図が1つできた(<c>Ctrl+Shift+F12</c>)。
/// デモとして成立させるには、そこから<b>動く絵</b>にしなければならない——
/// 静止画でよいなら PNG を1枚置けば済む話で、実時間で描いている意味が無い。
/// </para>
///
/// <para>
/// <b>持つのは「カメラの位置」ではなく「軌道カメラのパラメータ」</b>。
/// 注視点・距離・方位・仰角・画角の5つで、
/// これは <see cref="OrbitCameraController"/> がそのまま食える形。
/// 位置と注視点を直接持つ流儀(映像系のツールはたいていこちら)もあるが、
/// <b>手で数字を書くなら軌道のほうが圧倒的に楽</b>——
/// 「もう少し引く」が distance の1か所で済み、注視点はぶれない。
/// キャラクターを追う Day 51 では位置ベースに寄せることになる。
/// </para>
///
/// <para>
/// <b>時間の持ち方</b>。キー1つが「止まる時間(<see cref="Key.Hold"/>)」と
/// 「次のキーへ移る時間(<see cref="Key.Travel"/>)」の2つを持つ。
/// 止まる時間があると<b>ショット</b>になる——
/// 動きっぱなしの映像は、どこを見ればよいのか分からない。
/// </para>
///
/// <para>
/// <b>経路は輪になっている</b>。最後のキーの <see cref="Key.Travel"/> は
/// 「最後 → 先頭」に掛ける時間で、放っておくと永久に回り続ける。
/// デモは眺めるものなので、終わりが無いほうが都合がよい。
/// </para>
/// </summary>
internal sealed class CameraPath
{
    /// <summary>
    /// キーフレーム1つ。**ショット1つぶん**と読んでよい。
    /// </summary>
    /// <param name="Name">HUD とコンソールに出す名前。</param>
    /// <param name="Target">注視点(ワールド)。</param>
    /// <param name="Distance">注視点までの距離(メートル)。</param>
    /// <param name="Yaw">方位(ラジアン)。JSON には度で書く。</param>
    /// <param name="Pitch">仰角(ラジアン)。正で見下ろし。</param>
    /// <param name="FieldOfView">垂直画角(ラジアン)。</param>
    /// <param name="Hold">このキーで静止する秒数。</param>
    /// <param name="Travel">次のキーへ移るのに掛ける秒数。</param>
    internal readonly record struct Key(
        string Name,
        Vector3 Target,
        float Distance,
        float Yaw,
        float Pitch,
        float FieldOfView,
        float Hold,
        float Travel);

    /// <summary>ある時刻のカメラの姿勢。<see cref="Key"/> から時間を落としたもの。</summary>
    internal readonly record struct Pose(
        Vector3 Target,
        float Distance,
        float Yaw,
        float Pitch,
        float FieldOfView);

    private readonly Key[] _keys;

    /// <summary>各キーが始まる時刻。<c>_starts[i]</c> = i 未満の (Hold + Travel) の合計。</summary>
    private readonly float[] _starts;

    public CameraPath(IReadOnlyList<Key> keys)
    {
        if (keys.Count == 0)
        {
            throw new ArgumentException("カメラパスにキーが1つもありません", nameof(keys));
        }

        _keys = [.. keys];
        _starts = new float[_keys.Length];

        float time = 0.0f;
        for (int i = 0; i < _keys.Length; i++)
        {
            _starts[i] = time;
            time += MathF.Max(0.0f, _keys[i].Hold) + MathF.Max(0.0f, _keys[i].Travel);
        }

        TotalDuration = time;
    }

    public IReadOnlyList<Key> Keys => _keys;

    public int KeyCount => _keys.Length;

    /// <summary>1周の長さ(秒)。</summary>
    public float TotalDuration { get; }

    /// <summary>補間方式(<c>Ctrl+Alt+F4</c>)。</summary>
    public CameraInterpolation Interpolation { get; set; } = CameraInterpolation.CatmullRom;

    /// <summary>
    /// 区間の入口と出口で速度を 0 に寄せるか(<c>Ctrl+Alt+F5</c>)。
    ///
    /// <para>
    /// <b>補間方式とは別の軸</b>だというのが、ここでいちばん取り違えやすいところ。
    /// 補間方式は「キーとキーの間をどんな形の線で結ぶか」を決め、
    /// イージングは「その線の上をどんな速さで進むか」を決める。
    /// 前者を変えると軌跡が変わり、後者を変えても<b>軌跡は1ミリも動かない</b>。
    /// </para>
    ///
    /// <para>
    /// 静止(<see cref="Key.Hold"/>)を挟むデモではイージングが要る。
    /// 止まっていたカメラがいきなり最高速で動き出すと、
    /// **その瞬間だけ切り貼りしたように見える**。
    /// </para>
    /// </summary>
    public bool Ease { get; set; } = true;

    /// <summary>時刻 <paramref name="time"/> が何番目のショットに属するか。</summary>
    public int IndexAt(float time)
    {
        float t = Wrap(time);

        int index = 0;
        while (index + 1 < _keys.Length && t >= _starts[index + 1])
        {
            index++;
        }

        return index;
    }

    /// <summary>そのショットが始まる時刻。</summary>
    public float StartTimeOf(int index) => _starts[Math.Clamp(index, 0, _keys.Length - 1)];

    /// <summary>時刻を 0〜<see cref="TotalDuration"/> に畳む。</summary>
    public float Wrap(float time)
    {
        if (TotalDuration <= 0.0f)
        {
            return 0.0f;
        }

        float t = time % TotalDuration;
        return t < 0.0f ? t + TotalDuration : t;
    }

    /// <summary>
    /// 時刻 <paramref name="time"/> のカメラの姿勢を求める。**このクラスの入口**。
    ///
    /// <para>
    /// 3段になっている。
    /// </para>
    /// <list type="number">
    /// <item>時刻から区間(何番目のキーの、静止中か移動中か)を決める</item>
    /// <item>移動中なら区間内の進み <c>u</c>(0〜1)を出し、イージングを掛ける</item>
    /// <item>その <c>u</c> でキーを混ぜる(2つ、または前後を覗いて4つ)</item>
    /// </list>
    ///
    /// <para>
    /// <b>2 と 3 を分けておくのが肝</b>。イージングは 2 にしか触らないので、
    /// 軌跡(3 が作るもの)には影響しない。
    /// </para>
    /// </summary>
    public Pose Sample(float time)
    {
        if (_keys.Length == 1)
        {
            return PoseOf(0);
        }

        float t = Wrap(time);
        int index = IndexAt(t);
        float local = t - _starts[index];

        Key key = _keys[index];

        if (local <= key.Hold || key.Travel <= 0.0f)
        {
            return PoseOf(index);
        }

        float u = Math.Clamp((local - key.Hold) / key.Travel, 0.0f, 1.0f);

        if (Ease)
        {
            // smoothstep。u=0 と u=1 で微分が 0 になるので、静止と滑らかに繋がる。
            u = u * u * (3.0f - (2.0f * u));
        }

        return Interpolate(index, u);
    }

    /// <summary>
    /// そのときの**カメラの動く速さ**(m/s と 度/s)。
    ///
    /// <para>
    /// <b>数字で出さないと分からない</b>から作った関数。
    /// 線形補間と Catmull-Rom の差はキーを通過する 0.1 秒に出るもので、
    /// 眺めていて気づけるものではない。
    /// HUD に出しておくと、<c>Ctrl+Alt+F4</c> で線形に落とした瞬間に
    /// **通過点で数字が飛ぶ**のがそのまま見える。
    /// </para>
    ///
    /// <para>
    /// 解析的な微分ではなく前進差分にしてあるのは、イージングも巻き取りも
    /// 全部混ざった<b>実際に画面が動く速さ</b>を測りたいから。
    /// </para>
    /// </summary>
    public (float Metres, float Degrees) SpeedAt(float time, float step = 1.0f / 120.0f)
    {
        Pose a = Sample(time);
        Pose b = Sample(time + step);

        Vector3 eyeA = OrbitCameraController.EyePosition(a.Target, a.Distance, a.Yaw, a.Pitch);
        Vector3 eyeB = OrbitCameraController.EyePosition(b.Target, b.Distance, b.Yaw, b.Pitch);

        return (
            Vector3.Distance(eyeA, eyeB) / step,
            MathF.Abs(WrapAngle(b.Yaw - a.Yaw)) * (180.0f / MathF.PI) / step);
    }

    /// <summary>キーそのものの姿勢(静止中に返すもの)。</summary>
    public Pose PoseOf(int index)
    {
        Key key = _keys[Math.Clamp(index, 0, _keys.Length - 1)];
        return new Pose(key.Target, key.Distance, key.Yaw, key.Pitch, key.FieldOfView);
    }

    /// <summary>
    /// 区間 <paramref name="index"/> の中を <paramref name="u"/>(0〜1)で混ぜる。
    ///
    /// <para>
    /// <b>方位だけ扱いが違う</b>。角度は 359 度と 1 度が隣にあるので、
    /// 数字をそのまま混ぜると <b>358 度ぶん逆回りする</b>。
    /// 基準のキーからの差を毎回 -180〜180 度に畳んでから足し直すと
    /// **常に近いほうを回る**——これを「巻き取り」と呼ぶ。
    /// </para>
    ///
    /// <para>
    /// <b>仰角は畳まない</b>。<see cref="OrbitCameraController.Apply"/> が
    /// ±86 度で止めているので一周することがなく、
    /// 畳むと「89 度 → -89 度」を近道と判断して**カメラがひっくり返る**。
    /// 「角度だから畳む」ではなく、<b>一周し得る角度だけ畳む</b>のが正しい。
    /// </para>
    /// </summary>
    private Pose Interpolate(int index, float u)
    {
        int i1 = index;
        int i2 = Next(i1);

        Key k1 = _keys[i1];
        Key k2 = _keys[i2];

        // 方位を巻き取る。k1 を基準に、k0〜k3 が1本の数直線に乗るようにする。
        float y1 = k1.Yaw;
        float y2 = y1 + WrapAngle(k2.Yaw - y1);

        if (Interpolation == CameraInterpolation.Linear)
        {
            return new Pose(
                Vector3.Lerp(k1.Target, k2.Target, u),
                Lerp(k1.Distance, k2.Distance, u),
                Lerp(y1, y2, u),
                Lerp(k1.Pitch, k2.Pitch, u),
                Lerp(k1.FieldOfView, k2.FieldOfView, u));
        }

        int i0 = Previous(i1);
        int i3 = Next(i2);

        Key k0 = _keys[i0];
        Key k3 = _keys[i3];

        float y0 = y1 - WrapAngle(y1 - k0.Yaw);
        float y3 = y2 + WrapAngle(k3.Yaw - y2);

        return new Pose(
            CatmullRom(k0.Target, k1.Target, k2.Target, k3.Target, u),
            CatmullRom(k0.Distance, k1.Distance, k2.Distance, k3.Distance, u),
            CatmullRom(y0, y1, y2, y3, u),
            CatmullRom(k0.Pitch, k1.Pitch, k2.Pitch, k3.Pitch, u),
            CatmullRom(k0.FieldOfView, k1.FieldOfView, k2.FieldOfView, k3.FieldOfView, u));
    }

    private int Next(int index) => (index + 1) % _keys.Length;

    private int Previous(int index) => (index + _keys.Length - 1) % _keys.Length;

    private static float Lerp(float a, float b, float u) => a + ((b - a) * u);

    /// <summary>
    /// Catmull-Rom スプライン。**<paramref name="p1"/> から <paramref name="p2"/> までを描く**。
    ///
    /// <para>
    /// <c>p0</c> と <c>p3</c> は形を決めるために覗くだけの点で、線は通らない。
    /// 逆に <c>p1</c> と <c>p2</c> は必ず通る——
    /// <b>制御点を通る</b>のがベジエ曲線との決定的な違いで、
    /// カメラワークやアニメーションで好まれるのはこれが理由。
    /// 「ここを通ってほしい」と書いた点を通らない曲線では、絵の位置が決められない。
    /// </para>
    ///
    /// <para>
    /// 中身は「<c>p1</c> での接線を <c>(p2-p0)/2</c>、<c>p2</c> での接線を
    /// <c>(p3-p1)/2</c> と決めた3次エルミート補間」。
    /// <b>隣の区間と接線の式が同じ</b>なので、キーの上で速度が一致する(C1)。
    /// 展開すると下の形になる。
    /// </para>
    /// </summary>
    private static float CatmullRom(float p0, float p1, float p2, float p3, float u)
    {
        float u2 = u * u;
        float u3 = u2 * u;

        return 0.5f * (
            (2.0f * p1)
            + ((-p0 + p2) * u)
            + (((2.0f * p0) - (5.0f * p1) + (4.0f * p2) - p3) * u2)
            + ((-p0 + (3.0f * p1) - (3.0f * p2) + p3) * u3));
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u) =>
        new(
            CatmullRom(p0.X, p1.X, p2.X, p3.X, u),
            CatmullRom(p0.Y, p1.Y, p2.Y, p3.Y, u),
            CatmullRom(p0.Z, p1.Z, p2.Z, p3.Z, u));

    /// <summary>角度を -π〜π に畳む。</summary>
    public static float WrapAngle(float radians)
    {
        float wrapped = radians % MathF.Tau;

        if (wrapped > MathF.PI)
        {
            wrapped -= MathF.Tau;
        }
        else if (wrapped < -MathF.PI)
        {
            wrapped += MathF.Tau;
        }

        return wrapped;
    }
}
