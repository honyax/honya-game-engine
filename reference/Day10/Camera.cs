namespace SoftwareRasterizer;

/// <summary>
/// カメラ。ビュー行列と投影行列を作るためのパラメータをまとめただけのもの。
///
/// 「カメラ」という実体はレンダラの中に存在しない。あるのは
/// **世界のほうを動かして、カメラが原点に居るようにする行列**(ビュー行列)と、
/// **奥行きを W に写して遠近感を作る行列**(投影行列)の2つだけ。
/// このクラスはその2つを、人間に扱いやすい言葉(位置・注視点・視野角)から作る。
/// </summary>
internal sealed class Camera
{
    /// <summary>カメラの位置(ワールド座標)。</summary>
    public Vec3 Position { get; set; } = new(0.0f, 0.0f, 5.0f);

    /// <summary>注視点。ここを画面の中心に捉える。</summary>
    public Vec3 Target { get; set; } = Vec3.Zero;

    /// <summary>
    /// 上方向。カメラの「頭のてっぺん」がどちらを向くか。
    /// 視線と平行にすると軸が作れず破綻する(真上を見上げると絵が回ってしまう問題)。
    /// 本格的な実装ではここをクォータニオンで扱う(特論 A-4 / Day 41)。
    /// </summary>
    public Vec3 Up { get; set; } = Vec3.UnitY;

    /// <summary>
    /// 垂直方向の視野角(ラジアン)。
    /// 狭いほど望遠(遠くが大きく写り、遠近感が弱くなる)、
    /// 広いほど広角(遠近感が誇張され、端が引き伸ばされる)。
    /// ゲームでは 60〜90度あたりが一般的。
    /// </summary>
    public float FieldOfView { get; set; } = MathF.PI / 3.0f;

    /// <summary>画面の縦横比。これを間違えると絵が縦長・横長に潰れる。</summary>
    public float AspectRatio { get; set; } = 4.0f / 3.0f;

    /// <summary>
    /// 近クリップ面までの距離。これより手前は描かない。
    ///
    /// **0 にはできない。** 投影行列の M43 に near/(near-far) が入るので
    /// 0 だと遠近感そのものが消えるうえ、Day 7 の深度の精度が壊滅する。
    /// 近すぎる値(0.001 等)にすると深度値のほとんどが手前の狭い範囲に食われ、
    /// 遠くのものの前後関係が判定できなくなる(Zファイティング)。
    /// **near はできるだけ大きく**が鉄則。
    /// </summary>
    public float NearPlane { get; set; } = 0.1f;

    /// <summary>遠クリップ面までの距離。これより奥は描かない。</summary>
    public float FarPlane { get; set; } = 100.0f;

    /// <summary>ビュー行列。ワールド座標 → カメラ座標。</summary>
    public Mat4 ViewMatrix => Mat4.LookAt(Position, Target, Up);

    /// <summary>投影行列。カメラ座標 → クリップ座標。</summary>
    public Mat4 ProjectionMatrix => Mat4.Perspective(FieldOfView, AspectRatio, NearPlane, FarPlane);

    /// <summary>
    /// ビュー行列と投影行列を合成したもの。
    ///
    /// モデルごとに変わらないので、フレームに1回作ってモデル行列だけ掛ければよい。
    /// 頂点が10万個あっても行列の合成は数回で済む、というのが
    /// 「変換を行列に統一した」ことの実利(Day 5 の要点1)。
    /// </summary>
    public Mat4 ViewProjection => ViewMatrix * ProjectionMatrix;
}
