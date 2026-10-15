#version 430 core

// ============================================================
//  Day 63a: ジオメトリシェーダの手前の段(頂点シェーダ)
// ============================================================
//
// **今日の5通りの描き方が、全部この1本を使う**。
// 素通し・法線を見せる・面を押し出す・点を板にする・輪郭を出す——
// どれも「頂点をワールドへ持ち上げる」ところまでは同じで、
// 違いはその先(ジオメトリシェーダ)にしかない。
//
// <b>Day 14 からの頂点シェーダとの違いは、ワールドの位置を後ろへ渡すこと</b>。
// これまでは gl_Position(クリップ空間)だけ出せばよかったが、
// ジオメトリシェーダは<b>図形を丸ごと受け取って作り直す</b>段なので、
//   - 面の法線を外積で出す(3点のワールド座標が要る)
//   - カメラの向きに板を張る(点のワールド座標が要る)
//   - 隣の三角形が表か裏かを見る(6点のワールド座標が要る)
// のどれにもワールドの位置が要る。クリップ空間へ写すのは出す直前でよい。

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;
layout (location = 3) in vec3 aNormal;

uniform mat4 uModel;
uniform mat3 uNormalMatrix;
uniform mat4 uViewProjection;

// **インターフェースブロックで渡す**。今日いちばん地味で、いちばん効く仕掛け。
//
// 段の間の受け渡しを `out vec3 vWorldPosition;` のような素の変数で書くと、
// ジオメトリシェーダを挟んだ瞬間に困る。あの段は
//   入口 … 頂点シェーダの出力の**配列**(gl_in と同じ長さ)
//   出口 … 画素シェーダの入力
// の両方を宣言するので、同じ名前を in と out の2回書くことになり、
// **同じシェーダの中で名前がぶつかる**(再宣言はエラー)。
//
// ブロックにすると、段どうしの突き合わせは<b>ブロック名(Varying)と中身の並び</b>で決まり、
// 手前に付ける名前(OUT / IN)は**そのシェーダの中だけの呼び名**になる。
// おかげで in と out に同じ Varying を使えて、
// **画素シェーダは「ジオメトリシェーダがあるかどうか」を知らずに済む**。
// 今日の「GS の値段」(素通しの GS を挟むだけで何 ms 増えるか)は、
// 挟んだ絵と挟まない絵で画素シェーダが1文字も変わらないからこそ比べられる。
out Varying
{
    vec3 worldPosition;
    vec3 normal;
    vec4 color;
    vec2 texCoord;
} OUT;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);

    OUT.worldPosition = world.xyz;
    OUT.normal = normalize(uNormalMatrix * aNormal);
    OUT.color = aColor;
    OUT.texCoord = aTexCoord;

    // ジオメトリシェーダを挟まないときは、これがそのままラスタライザへ行く。
    // 挟んだときは、向こうから gl_in[i].gl_Position として読める
    // (**渡した値が消えるわけではない**。素通しの GS はこれをそのまま出す)。
    gl_Position = uViewProjection * world;
}
