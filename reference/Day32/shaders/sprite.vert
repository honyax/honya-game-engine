#version 330 core

// スクリーン座標(ピクセル)。左上が (0,0)。
layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec2 aTexCoord;
layout (location = 2) in vec4 aColor;

// スクリーン座標 → クリップ座標。平行投影で、Y の向きもここで反転する。
// **モデル行列が無い**のが 3D 側との一番の違い。
// スプライトの位置・回転・大きさは CPU 側で頂点に焼き込んであるので、
// シェーダに渡すのはフレームごとに1本の行列だけで済む。
// これが「1万枚を1回のドローコールで」を成立させている理屈で、
// オブジェクトごとの uniform が1つでも残っていたら、
// その時点で1万回 glUniform を呼ぶことになる。
uniform mat4 uProjection;

out vec2 vTexCoord;
out vec4 vColor;

void main()
{
    gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
    vTexCoord = aTexCoord;
    vColor = aColor;
}
