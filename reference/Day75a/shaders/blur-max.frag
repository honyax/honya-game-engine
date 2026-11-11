#version 330 core

// **窓の中の最大**(Day 55)。ぼけの地図の2段目と3段目で使い回す。
//
//   升目の縦の段 … 横の段(blur-tiles.frag)の結果を、縦に 16 画素ぶんまとめる → 16x16 の升目の最大(TileMax)
//   近所の段     … 升目の結果を、自分とまわり8升(3x3)でまとめる → 近所の最大(McGuire 2012 の NeighborMax)
//
// 近所まで見るのは、升目の端の画素には**隣の升目の物**の尾やボケが届くことがあるから。
// ぼけの半径の上限を升目の大きさ(16 画素)にしてあるので、3x3 の近所まで見れば、
// 自分の升目のどの画素に届くものも必ず数えている(BlurField.MaxRadius)。

out vec4 FragTile;   // blur-tiles.frag と同じ並び(xy: 速度 / z: いちばん手前の錯乱円 / w: いちばん大きい |錯乱円|)

uniform sampler2D uSource;

// 出力の1画素ぶんで、入力を何画素進むか。縦の段は (1, 16)、近所の段は (1, 1)。
uniform ivec2 uStride;

// 窓の始まり(出力の位置 × uStride からの相対)。縦の段は (0, 0)、近所の段は (-1, -1)。
uniform ivec2 uOffset;

// 窓の大きさ。縦の段は (1, 16)、近所の段は (3, 3)。
uniform ivec2 uCount;

void main()
{
    ivec2 origin = (ivec2(gl_FragCoord.xy) * uStride) + uOffset;
    ivec2 last = textureSize(uSource, 0) - 1;

    vec4 result = vec4(0.0);
    float fastestSquared = 0.0;

    for (int y = 0; y < uCount.y; y++)
    {
        for (int x = 0; x < uCount.x; x++)
        {
            vec4 texel = texelFetch(uSource, clamp(origin + ivec2(x, y), ivec2(0), last), 0);

            // 速度は長さで比べて向きごと(blur-tiles.frag と同じ理由)。
            float squared = dot(texel.xy, texel.xy);
            if (squared > fastestSquared)
            {
                fastestSquared = squared;
                result.xy = texel.xy;
            }

            result.z = min(result.z, texel.z);
            result.w = max(result.w, texel.w);
        }
    }

    FragTile = result;
}
