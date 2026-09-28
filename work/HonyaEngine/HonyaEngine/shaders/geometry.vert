#version 330 core

// **ビュー空間の法線と深度だけを書き出す**(Day 37)。SSAO の1パス目。
//
// depth.vert(Day 33)と並べると、この2本の性格の違いがよく分かる。
//   - depth.vert    … 光の目から見る。位置しか要らない。色は書かない
//   - geometry.vert … カメラの目から見る。**法線が要る**。色として深度と法線を書く
//
// 本描画(textured.vert)と違って、UV も頂点色も接空間も要らない。
// SSAO が問うのは「この点のまわりに、どれだけ物が詰まっているか」という
// **幾何だけの問い**で、材質は1ミリも関係しないから。
layout (location = 0) in vec3 aPosition;
layout (location = 3) in vec3 aNormal;

// Day 41。**SSAO も本描画と同じ形を見なければならない**。
// ここを足さないと、キャラクタの遮蔽だけがバインドポーズの位置に出る
// (足元の影が、歩いているキャラクタから 1 歩ぶんずれて置き去りになる)。
layout (location = 5) in vec4 aJoints;
layout (location = 6) in vec4 aWeights;

// **ビュー行列とモデル行列を掛けたもの**を CPU 側で1つにして渡す。
//
// 本描画は uModel と uViewProjection の2本に分けているが、こちらは
// 「ビュー空間の位置」がそのまま欲しいので、透視投影の手前で切る必要がある。
// 分け方が違うのは、**欲しい途中結果が違う**から。
uniform mat4 uModelView;

uniform mat4 uProjection;

// ビュー空間へ運ぶための法線行列(モデルビューの左上 3x3 の逆転置)。
// **世界空間ではなくビュー空間**へ運ぶのが本描画との違いで、
// SSAO の計算がまるごとビュー空間で行われるため。
uniform mat3 uNormalMatrix;

// textured.vert とまったく同じ約束。値も同じものを送る(Ssao.Draw)。
const int MAX_JOINTS = 64;
uniform mat4 uJoints[MAX_JOINTS];
uniform int uSkinned;

out vec3 vViewNormal;

// **カメラからの距離**(ビュー空間の -z)。正の値。
//
// gl_FragCoord.z(深度バッファの値)ではなく自分で持ち出すのは、
// あちらが 1/z に比例した非線形な値だから。SSAO は
// 「半径 0.5m の球の中を探る」という**長さの話**をするので、
// メートルのままの深度を持っておくほうが素直に書ける。
out float vViewDepth;

void main()
{
    vec4 localPos = vec4(aPosition, 1.0);
    vec3 localNormal = aNormal;

    if (uSkinned == 1)
    {
        mat4 skin = (uJoints[int(aJoints.x)] * aWeights.x)
            + (uJoints[int(aJoints.y)] * aWeights.y)
            + (uJoints[int(aJoints.z)] * aWeights.z)
            + (uJoints[int(aJoints.w)] * aWeights.w);

        localPos = skin * localPos;
        localNormal = mat3(skin) * localNormal;
    }

    vec4 viewPosition = uModelView * localPos;

    vViewNormal = uNormalMatrix * localNormal;

    // OpenGL のビュー空間は **-Z 方向が前**。符号を反転して「距離」にする。
    vViewDepth = -viewPosition.z;

    gl_Position = uProjection * viewPosition;
}
