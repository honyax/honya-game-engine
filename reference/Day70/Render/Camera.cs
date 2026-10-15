using System.Numerics;

namespace HonyaEngine;

/// <summary>投影の種類。</summary>
internal enum ProjectionMode
{
    /// <summary>透視投影。遠くのものが小さくなる。人間の目に近い。</summary>
    Perspective,

    /// <summary>平行投影。距離によらず同じ大きさで描かれる。UI や設計図、見下ろし型ゲームで使う。</summary>
    Orthographic,
}

/// <summary>
/// カメラ。ビュー行列と投影行列を作るためのパラメータをまとめたもの。
///
/// Phase 1 の <c>Camera</c>(Day 6・Day 10)とほぼ同じ形にしてある。
/// **GPU に移っても、カメラの考え方は1ミリも変わらない**というのが今日の主題で、
/// 変わったのは「行列を作ったあと、頂点に掛けるのが CPU か GPU か」だけ。
///
/// 「カメラ」という実体はレンダラの中に存在しない。あるのは
///   - **世界のほうを動かして、カメラが原点に居るようにする行列**(ビュー行列)
///   - **奥行きを W に写して遠近感を作る行列**(投影行列)
/// の2つだけで、このクラスはその2つを人間に扱いやすい言葉から組み立てる。
/// </summary>
internal sealed class Camera
{
    /// <summary>カメラの位置(ワールド座標)。</summary>
    public Vector3 Position { get; set; } = new(0.0f, 0.0f, 5.0f);

    /// <summary>注視点。ここが画面の中心に来る。</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>
    /// 上方向。カメラの「頭のてっぺん」がどちらを向くか。
    /// 視線と平行にすると軸が作れず破綻する(真上を見上げたときに絵が回る問題)。
    /// この破綻を根本的に避けるにはクォータニオンが要る(特論 A-4。Phase 4 の Transform で扱う)。
    /// 今日は <see cref="OrbitCameraController"/> 側で角度を制限して回避する。
    /// </summary>
    public Vector3 Up { get; set; } = Vector3.UnitY;

    /// <summary>投影の種類。</summary>
    public ProjectionMode Mode { get; set; } = ProjectionMode.Perspective;

    /// <summary>
    /// 垂直方向の視野角(ラジアン)。透視投影のときだけ使う。
    /// 狭いほど望遠(遠近感が弱くなる)、広いほど広角(遠近感が誇張される)。
    /// </summary>
    public float FieldOfView { get; set; } = MathF.PI / 3.0f;

    /// <summary>平行投影のときに画面へ収める高さ(ワールド単位)。</summary>
    public float OrthographicHeight { get; set; } = 10.0f;

    /// <summary>画面の縦横比(幅 / 高さ)。これを間違えると絵が縦長・横長に潰れる。</summary>
    public float AspectRatio { get; set; } = 4.0f / 3.0f;

    /// <summary>
    /// 近クリップ面までの距離。**0 にはできない**うえ、小さすぎてもいけない。
    ///
    /// Phase 1 では「深度バッファの精度が食われる」という話だったが(Day 7)、
    /// GPU でも事情は同じで、しかも深度バッファは 24bit 固定なので逃げ場がない。
    /// 透視投影では深度値が 1/z に比例して分布するため、
    /// **near を10倍小さくすると遠方の精度がほぼ10分の1になる**。
    /// near はできるだけ大きく、が鉄則。
    /// </summary>
    public float NearPlane { get; set; } = 0.1f;

    /// <summary>遠クリップ面までの距離。これより奥は描かない。</summary>
    public float FarPlane { get; set; } = 100.0f;

    /// <summary>
    /// **1画素より小さなずらし**(Day 54)。NDC(-1〜1)での平行移動量で、1画素は <c>2 / 画面の幅</c>。
    ///
    /// <para>
    /// TAA は毎フレーム、画面を<b>1画素に満たない量だけ</b>ずらして描く。
    /// 同じ画素が毎フレーム少しずつ違う点を標本に取るので、それを時間の方向に混ぜると、
    /// 1画素の中を何点も取ったのと同じ絵になる——スーパーサンプリングを、1フレームで払わずに時間へ割り振る。
    /// </para>
    ///
    /// <para>
    /// <b>射影行列に入れる</b>ので、このカメラで描くパスは<b>全部が同じだけずれる</b>。
    /// 本描画も、空も、粒も、SSAO の幾何パスも、G-Buffer も。
    /// どれか1つだけずらし忘れると、そのパスの結果が半画素ずれて重なる——
    /// パスごとに足すのをやめて、全員が通るここに1か所だけ置いた。
    /// 影は光の目の行列で描くので、ずれない(ずれてはいけない)。
    /// </para>
    ///
    /// <para>
    /// 既定は 0。<b>0 のときは Day 53 までと1ビットも違わない行列</b>を返す
    /// (<see cref="ProjectionMatrix"/>)。
    /// </para>
    /// </summary>
    public Vector2 Jitter { get; set; }

    /// <summary>
    /// ビュー行列。ワールド座標 → ビュー座標。
    ///
    /// Phase 1 では <c>Mat4.LookAt</c> を自作したが、
    /// <see cref="Matrix4x4.CreateLookAt"/> は**まったく同じもの**を作る。
    /// 右手系・カメラは -Z 方向を見る・行ベクトル規約、と3つとも一致しているので
    /// そのまま使える。自作した経験があるから「そのまま使える」と判断できる、
    /// というのが Phase 1 を通した意味。
    /// </summary>
    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Target, Up);

    /// <summary>
    /// 投影行列。ビュー座標 → クリップ座標。**Day 54 からジッターが入る**(<see cref="Jitter"/>)。
    ///
    /// <para>
    /// <b>ずらしは投影の「後ろ」に掛ける</b>。行ベクトル規約なので右から平行移動を掛けると、
    /// クリップ座標の x に <c>jx × w</c> が足される。透視除算で w で割ると <c>jx</c> だけが残り、
    /// <b>手前でも奥でも同じ画素数だけずれる</b>——画面の上での平行移動になる。
    /// 投影の前(ビュー空間)でずらすと、奥ほどずれが小さくなって画素単位の話にならない。
    /// </para>
    ///
    /// <para>
    /// 透視投影なら M31・M32 が、平行投影なら M41・M42 が 0 でなくなる。
    /// <b>「対角の2つの数だけで深度から位置が戻る」</b>(Day 37 の ssao.frag・Day 52 の deferred.frag)は
    /// この非対角を見ていないので、ジッターぶん(半画素以内)ずれた位置を戻す。計画書の「今日残した歪み」。
    /// </para>
    /// </summary>
    public Matrix4x4 ProjectionMatrix => Jitter == Vector2.Zero
        ? UnjitteredProjectionMatrix
        : UnjitteredProjectionMatrix * Matrix4x4.CreateTranslation(Jitter.X, Jitter.Y, 0.0f);

    /// <summary>
    /// **ずらす前の投影行列**(Day 54)。Day 53 までの <see cref="ProjectionMatrix"/> そのもの。
    ///
    /// <para>
    /// 「前のフレームのどこに写っていたか」を計算するときはこちらを使う(速度バッファ)。
    /// ずらしたままの行列で比べると、ずらしの差(毎フレーム変わる)が<b>動きに化ける</b>——
    /// カメラも物も止まっているのに、画面全体が半画素ずつ震えているように計算されてしまう。
    /// </para>
    /// </summary>
    public Matrix4x4 UnjitteredProjectionMatrix => Mode == ProjectionMode.Perspective
        ? CreatePerspective(FieldOfView, AspectRatio, NearPlane, FarPlane)
        : CreateOrthographic(OrthographicHeight * AspectRatio, OrthographicHeight, NearPlane, FarPlane);

    /// <summary>
    /// ビュー行列と投影行列を合成したもの。
    ///
    /// **オブジェクトごとに変わらない**ので、フレームに1回作って uniform に送る。
    /// 掛ける順が「ビュー → 投影」なのは行ベクトル規約だから(Day 14 の要点4)。
    /// </summary>
    public Matrix4x4 ViewProjection => ViewMatrix * ProjectionMatrix;

    /// <summary>ずらす前のビュー射影行列(Day 54)。速度バッファが「前のフレーム」と比べるのに使う。</summary>
    public Matrix4x4 UnjitteredViewProjection => ViewMatrix * UnjitteredProjectionMatrix;

    /// <summary>カメラから注視点までの距離。</summary>
    public float DistanceToTarget => Vector3.Distance(Position, Target);

    /// <summary>
    /// 透視投影行列を作る。**OpenGL の深度規約(NDC の z が -1〜1)**で作る。
    ///
    /// ここが今日いちばん引っかかりやすいところ。
    /// <see cref="Matrix4x4.CreatePerspectiveFieldOfView"/> は名前も引数もぴったりだが、
    /// **深度を 0〜1 に写す DirectX の規約**で作られている。
    /// Phase 1 の <c>Mat4.Perspective</c> もそちらに合わせてあった
    /// (自前の Z バッファがその範囲を前提にしていたため)。
    ///
    /// OpenGL に 0〜1 の行列を渡すと、絵は一応出る。壊れるのは深度のほうで、
    /// **近クリップ面が実効的に near/2 まで手前にずれ**、
    /// 本来クリップされるはずの near/2 〜 near の隙間に**深度バッファの半分(0〜0.5)が食われる**。
    /// near = 0.1 なら、深度 0.5 に来るのが far ではなく near、というずれ方をする。
    /// 絵は出るので気づきにくく、遠くで Z ファイティングが出て初めて疑うことになる。
    ///
    /// 行列そのものは Phase 1 と1か所しか違わない。3行目・4行目の Z に関わる係数だけ:
    ///   DirectX  M33 = f/(n-f)      M43 = nf/(n-f)
    ///   OpenGL   M33 = (f+n)/(n-f)  M43 = 2fn/(n-f)
    /// 遠近感を作る仕掛け(M34 = -1 で Z を W にコピーし、透視除算で割る)は共通で、
    /// **違うのは「割ったあとの z をどの範囲に収めるか」だけ**。
    /// </summary>
    public static Matrix4x4 CreatePerspective(float fieldOfViewY, float aspectRatio, float near, float far)
    {
        float yScale = 1.0f / MathF.Tan(fieldOfViewY * 0.5f);
        float xScale = yScale / aspectRatio;

        return new Matrix4x4(
            xScale, 0.0f, 0.0f, 0.0f,
            0.0f, yScale, 0.0f, 0.0f,
            0.0f, 0.0f, (far + near) / (near - far), -1.0f,     // M34 = -1 が「Z を W へコピー」
            0.0f, 0.0f, 2.0f * far * near / (near - far), 0.0f);  // M44 = 0 が透視投影の目印
    }

    /// <summary>
    /// **スクリーン座標をそのまま使える**平行投影行列を作る。2D 描画用。
    ///
    /// 3D 用の <see cref="CreateOrthographic"/> は原点が画面中心だったが、
    /// 2D では「左上が (0,0)、右下が (幅, 高さ)」のほうが圧倒的に扱いやすい。
    /// スプライトの位置をピクセルで指定できるようになるので、
    /// UI もタイルマップも、素直に書ける。
    ///
    /// 呼び方は <c>CreateScreen(0, width, height, 0, -1, 1)</c>。
    /// **bottom に height、top に 0 を渡す**のがポイントで、
    /// これで Y 軸が下向きになる(画面の下ほど y が大きい)。
    /// 上下の入れ替えは M22 の符号1つで済むので、専用の反転行列は要らない。
    ///
    /// z の範囲を -1〜1 にしてあるのは、2D では奥行きを使わないから。
    /// z = 0 のスプライトがちょうど範囲の真ん中に収まる。
    /// </summary>
    public static Matrix4x4 CreateScreen(
        float left, float right, float bottom, float top, float near, float far)
    {
        return new Matrix4x4(
            2.0f / (right - left), 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / (top - bottom), 0.0f, 0.0f,
            0.0f, 0.0f, 2.0f / (near - far), 0.0f,
            -(right + left) / (right - left),
            -(top + bottom) / (top - bottom),
            (far + near) / (near - far),
            1.0f);
    }

    /// <summary>
    /// 平行投影行列を作る。こちらも OpenGL の深度規約に合わせる。
    ///
    /// 透視投影との違いは**W をいじらない**こと。M44 = 1 のままなので透視除算が
    /// 何もしない(1 で割るだけ)。遠近感が出ないのはそのため。
    /// つまり平行投影は「箱を -1〜1 の立方体に押し込むだけの拡大縮小+平行移動」で、
    /// 透視投影の特別な場合というより**別物**だと思ったほうがよい。
    /// </summary>
    public static Matrix4x4 CreateOrthographic(float width, float height, float near, float far)
    {
        return new Matrix4x4(
            2.0f / width, 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / height, 0.0f, 0.0f,
            0.0f, 0.0f, 2.0f / (near - far), 0.0f,
            0.0f, 0.0f, (far + near) / (near - far), 1.0f);
    }
}
