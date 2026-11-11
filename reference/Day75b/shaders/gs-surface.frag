#version 430 core

// ============================================================
//  Day 63a: 面を塗る(今日の4通りに共通の画素シェーダ)
// ============================================================
//
// **ここはジオメトリシェーダの有無を知らない**。
// 入口が Varying ブロックなので、頂点シェーダから直に来ても、
// 素通しの GS を通っても、押し出しの GS を通っても、同じ1本で受けられる。
// 「GS の値段」を測るときに**画素の仕事が1文字も変わらない**のはこのおかげ(gs.vert のコメント)。
//
// 陰影はランバート + 環境光だけ。今日の主題は段のほうなので、
// Day 35 の PBR は通さない(通すと材質の設定が主役を食う)。

in Varying
{
    vec3 worldPosition;
    vec3 normal;
    vec4 color;
    vec2 texCoord;
} IN;

uniform vec3 uLightDirection;
uniform vec3 uLightColor;
uniform vec3 uAmbientColor;
uniform vec3 uEyePosition;

out vec4 FragColor;

void main()
{
    vec3 normal = normalize(IN.normal);

    // **両面を塗る**。押し出すと三角形の裏が見えるようになるので、
    // 裏を向いている画素では法線をひっくり返す。
    // gl_FrontFacing は「ラスタライザから見て表だったか」で、巻き順から決まる。
    if (!gl_FrontFacing)
    {
        normal = -normal;
    }

    vec3 toLight = normalize(-uLightDirection);
    float diffuse = max(dot(normal, toLight), 0.0);

    vec3 toEye = normalize(uEyePosition - IN.worldPosition);
    vec3 halfVector = normalize(toLight + toEye);
    float specular = pow(max(dot(normal, halfVector), 0.0), 48.0);

    vec3 base = IN.color.rgb;
    vec3 color = base * ((uLightColor * diffuse) + uAmbientColor);
    color += uLightColor * specular * 0.25;

    FragColor = vec4(color, IN.color.a);
}
