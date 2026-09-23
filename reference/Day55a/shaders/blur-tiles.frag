#version 330 core

// **升目の最大・横の段**(Day 55。McGuire 2012 の TileMax を縦横に分けたもの)。
// 1画素 = 画面の横 16 画素ぶん。縦はまだ原寸のまま(縦の段は blur-max.frag)。
//
// 升目の中で2つのことを覚える。
//   - いちばん速い速度 … モーションブラーの尾が、この升目からどこまで伸びうるか
//   - いちばん手前の錯乱円 … 被写界深度の手前のボケが、この升目からどこまで広がりうるか
// どちらも「自分の画素の外まで光を配る」ものの大きさで、集める側(gather)が探す範囲を決めるのに使う。
//
// **縦横に分けるのは速さのため**。16x16 の升目を1画素で全部なめると 256 回読むが、升目は 960x640 で 2400 個しかない——
// GPU は何万もの画素を同時に走らせて待ち時間を隠す作りなので、2400 個では手が余る(計画書「検証の途中で分かったこと」)。
// 最大は「横の最大の、縦の最大」に分けても同じ答えになる(Day 31 のぼかしを縦横に分けたのと同じ理屈)。

out vec4 FragTile;   // xy: 速度 [画素/フレーム] / z: いちばん手前の錯乱円(負ほど大きい) / w: いちばん大きい |錯乱円|

uniform sampler2D uField;      // blur-field.frag の地図(y が錯乱円)
uniform sampler2D uVelocity;   // 速度(UV 単位、前 → 今)。Day 54 の MotionVectors が描いたもの
uniform int uHasVelocity;      // 0 なら速度を読まない(被写界深度だけ)
uniform int uTileSize;

void main()
{
    ivec2 size = textureSize(uField, 0);
    ivec2 origin = ivec2(int(gl_FragCoord.x) * uTileSize, int(gl_FragCoord.y));

    vec2 fastest = vec2(0.0);
    float fastestSquared = 0.0;
    float nearest = 0.0;
    float largest = 0.0;

    for (int x = 0; x < uTileSize; x++)
    {
        // 画面の端の升目ははみ出す。はみ出したぶんは端の画素で埋める。
        ivec2 pixel = min(origin + ivec2(x, 0), size - 1);

        float coc = texelFetch(uField, pixel, 0).y;
        nearest = min(nearest, coc);
        largest = max(largest, abs(coc));

        if (uHasVelocity == 1)
        {
            // UV → 画素。**長さで比べて、向きごと覚える**——尾は向きを持つので、
            // x と y を別々に最大にすると、どの物も動いていない斜めの向きができあがる。
            vec2 velocity = texelFetch(uVelocity, pixel, 0).rg * vec2(size);
            float squared = dot(velocity, velocity);

            if (squared > fastestSquared)
            {
                fastestSquared = squared;
                fastest = velocity;
            }
        }
    }

    FragTile = vec4(fastest, nearest, largest);
}
