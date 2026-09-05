using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **glTF 2.0 の読み込み**。Day 32 の主役。
///
/// Day 10 で OBJ ローダを書いた。あれは「テキストを行ごとに読んで v / vt / f を拾う」だけで、
/// 100 行に満たなかった。glTF はそれよりずっと大きいが、**大きい理由がはっきりしている**。
///
/// | | OBJ(1992) | glTF 2.0(2017) |
/// |---|---|---|
/// | 形式 | テキスト | JSON + バイナリ |
/// | 読み込み | 数値を1個ずつ parse | **バイト列をそのまま GPU へ** |
/// | マテリアル | 別ファイル(.mtl)、実装依存 | 仕様に組み込み。**PBR で定義** |
/// | 階層 | 無い | ノードの木 |
/// | テクスチャ | パスだけ | 埋め込みも可。サンプラの設定も持つ |
///
/// glTF が "the JPEG of 3D" と呼ばれるのは、**実行時にそのまま使える形**で入っているから。
/// OBJ は「頂点の位置の配列」と「面の定義」が別々なので、
/// 読み込んだあとに GPU 向けの頂点配列へ組み直す必要があった(Day 10 の要点2)。
/// glTF は組み直したあとの姿——**頂点バッファとインデックスバッファそのもの**——が入っている。
///
/// <para>
/// <b>4段の間接参照</b>が glTF の骨格で、ここさえ掴めば残りは細部になる。
/// <code>
///   accessor  … 「float の VEC3 が 14556 個」= 意味と個数
///       ↓
///   bufferView… 「buffer の 1024 バイト目から 174672 バイト」= 場所
///       ↓
///   buffer    … バイト列そのもの(glb なら同じファイルの中、gltf なら別ファイル)
/// </code>
/// 面倒に見えるが、この分け方のおかげで
/// **1本のバイト列を複数の意味で切り出せる**(位置と法線が同じ buffer に同居できる)。
/// </para>
///
/// <para>
/// <b>今日読まないもの</b>。仕様は広いので、静的メッシュに要らないものは落としてある。
/// アニメーション、スキン、モーフターゲット、カメラ、ライト、
/// sparse アクセサ、拡張(KHR_*)、TRIANGLES 以外の描画モード。
/// アニメーションとスキンは Day 41 で戻ってくる。
/// </para>
/// </summary>
internal static class GltfLoader
{
    /// <summary>glb の先頭 4 バイト。ASCII で "glTF"。</summary>
    private const uint GlbMagic = 0x46546C67;

    private const uint ChunkJson = 0x4E4F534A;   // "JSON"
    private const uint ChunkBin = 0x004E4942;    // "BIN\0"

    // accessor.componentType。GL の定数と同じ値なのは偶然ではなく、
    // **そのまま glDrawElements に渡せるように**そう決められている。
    private const int ComponentByte = 5120;
    private const int ComponentUnsignedByte = 5121;
    private const int ComponentShort = 5122;
    private const int ComponentUnsignedShort = 5123;
    private const int ComponentUnsignedInt = 5125;
    private const int ComponentFloat = 5126;

    /// <summary>primitive.mode。4 = TRIANGLES。</summary>
    private const int ModeTriangles = 4;

    /// <summary>
    /// ファイルを読んで <see cref="Model"/> にする。<c>.glb</c> と <c>.gltf</c> の両方を受ける。
    /// </summary>
    /// <param name="shader">できたマテリアルに割り当てるシェーダ。</param>
    /// <param name="forceGenerateTangents">
    /// <b>ファイルの TANGENT を無視して、こちらで作り直す</b>(Day 34)。
    ///
    /// 見比べるためだけの引数で、実用では常に false でよい——
    /// ファイルの接線は**法線マップを焼いたときと同じもの**なので、
    /// あるならそちらを使うのが正しい。
    /// 生成した接線とどれだけ違うかを目で見るために開けてある(Alt+7)。
    /// </param>
    public static Model Load(
        GL gl, RenderResources resources, string path, Handle<Shader> shader,
        bool forceGenerateTangents = false)
    {
        byte[] bytes = File.ReadAllBytes(path);

        // **拡張子ではなく中身で判別する**。先頭が "glTF" なら glb。
        // 拡張子は人が付け替えられるが、マジックナンバーは中身そのものなので嘘をつかない。
        bool isBinary = bytes.Length >= 4
            && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic;

        JsonDocument json;
        byte[]? embeddedBuffer = null;

        if (isBinary)
        {
            (json, embeddedBuffer) = ReadGlb(bytes, path);
        }
        else
        {
            json = JsonDocument.Parse(bytes);
        }

        using (json)
        {
            var context = new LoadContext(
                gl, resources, path, json.RootElement, embeddedBuffer, shader, forceGenerateTangents);
            return context.Build();
        }
    }

    /// <summary>
    /// glb コンテナをほどく。**中身は「ヘッダ + チャンクの並び」だけ**。
    ///
    /// <code>
    ///   [magic "glTF"][version 2][全体の長さ]       12 バイト
    ///   [チャンク長][型 "JSON"][JSON 本体]
    ///   [チャンク長][型 "BIN\0"][バイナリ本体]      ← 無いこともある
    /// </code>
    ///
    /// 「なぜ zip ではないのか」は、**展開せずにそのまま使えるようにするため**。
    /// バイナリチャンクは頂点バッファの並びそのものなので、
    /// ファイルから読んだメモリの一部を、コピーも変換もせず GPU へ渡せる。
    /// 圧縮すると必ず展開の1手間が入る。
    /// </summary>
    private static (JsonDocument Json, byte[]? Binary) ReadGlb(byte[] bytes, string path)
    {
        if (bytes.Length < 12)
        {
            throw new InvalidDataException($"glb が短すぎます: {path}");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        if (version != 2)
        {
            throw new InvalidDataException($"glb のバージョンが 2 ではありません: {version} ({path})");
        }

        JsonDocument? json = null;
        byte[]? binary = null;

        // ヘッダの次からチャンクを順に読む。
        // **未知の型のチャンクは読み飛ばす**のが仕様の要求で、
        // そうしておくと将来チャンクが増えても古いローダが壊れない。
        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            offset += 8;

            if (offset + length > bytes.Length)
            {
                throw new InvalidDataException($"glb のチャンクがファイル末尾をはみ出しています: {path}");
            }

            if (type == ChunkJson)
            {
                json = JsonDocument.Parse(bytes.AsMemory(offset, length));
            }
            else if (type == ChunkBin)
            {
                binary = bytes.AsSpan(offset, length).ToArray();
            }

            // チャンクは4バイト境界にそろえる決まり。
            // 長さが4の倍数でないときは詰め物が入っているので、そのぶん進める。
            offset += (length + 3) & ~3;
        }

        return (json ?? throw new InvalidDataException($"glb に JSON チャンクがありません: {path}"), binary);
    }

    /// <summary>
    /// 1回の読み込みで持ち回る状態。
    ///
    /// 静的メソッドに全部の引数を渡し回すと引数が7個を超えるので、
    /// **読み込み1回ぶんの寿命を持つ入れ物**にまとめてある。
    /// </summary>
    private sealed class LoadContext
    {
        private readonly GL _gl;
        private readonly RenderResources _resources;
        private readonly string _path;
        private readonly string _directory;
        private readonly JsonElement _root;
        private readonly Handle<Shader> _shader;

        /// <summary>buffer 番号 → バイト列。glb の埋め込みぶんと、外部ファイルぶんが混ざる。</summary>
        private readonly Dictionary<int, byte[]> _buffers = [];

        private readonly byte[]? _embedded;

        /// <summary>ファイルの TANGENT を捨てて作り直すか(Day 34。見比べ用)。</summary>
        private readonly bool _forceGenerateTangents;

        /// <summary>material 番号 → できあがったマテリアル。**同じものを何度も作らない**。</summary>
        private readonly Dictionary<int, Material> _materials = [];

        /// <summary>
        /// 参照カウントを増やしたテクスチャ。<see cref="Model.Dispose"/> が同じ数だけ返す。
        ///
        /// **同じハンドルが複数回入ることを許す**。
        /// 1枚の絵を2つのマテリアルが指していれば、RenderResources 側のカウントは 2 になっているので、
        /// こちらも 2 回返さないと釣り合わない。
        /// </summary>
        private readonly List<Handle<Texture>> _textures = [];

        /// <summary>ファイルに TANGENT が入っていたパーツの数(Day 34)。HUD と自己チェック用。</summary>
        public int FileTangentParts { get; private set; }

        /// <summary>接線をこちらで作ったパーツの数(Day 34)。</summary>
        public int GeneratedTangentParts { get; private set; }

        public LoadContext(
            GL gl,
            RenderResources resources,
            string path,
            JsonElement root,
            byte[]? embedded,
            Handle<Shader> shader,
            bool forceGenerateTangents)
        {
            _gl = gl;
            _resources = resources;
            _path = path;
            _directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            _root = root;
            _embedded = embedded;
            _shader = shader;
            _forceGenerateTangents = forceGenerateTangents;
        }

        public Model Build()
        {
            var parts = new List<Model.Part>();
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            int triangles = 0;
            int vertices = 0;

            // シーンは複数あることがあるが、既定のものだけ描く。
            // scene が無いファイルもあるので、そのときは 0 番。
            int sceneIndex = GetInt(_root, "scene", 0);
            JsonElement scenes = Get(_root, "scenes");
            JsonElement nodes = Get(_root, "nodes");

            if (scenes.ValueKind != JsonValueKind.Array || sceneIndex >= scenes.GetArrayLength())
            {
                throw new InvalidDataException($"シーンがありません: {_path}");
            }

            foreach (JsonElement rootNode in Get(scenes[sceneIndex], "nodes").EnumerateArray())
            {
                Visit(nodes, rootNode.GetInt32(), Matrix4x4.Identity, parts, ref min, ref max, ref triangles, ref vertices);
            }

            if (parts.Count == 0)
            {
                throw new InvalidDataException($"描けるメッシュが1つもありません: {_path}");
            }

            return new Model(_resources, parts, _materials.Values.ToArray(), _textures, min, max, _path)
            {
                TriangleCount = triangles,
                VertexCount = vertices,
                FileTangentParts = FileTangentParts,
                GeneratedTangentParts = GeneratedTangentParts,
            };
        }

        /// <summary>
        /// ノードを1つ処理して、子へ降りる。**行列を掛けながら降りるのが階層の全部**。
        ///
        /// 親の世界行列に自分のローカル行列を掛けたものが自分の世界行列で、
        /// それを子に渡す。Day 22 の <see cref="Transform"/> と同じ話が、
        /// ファイルの側にもそのまま出てくる。
        /// </summary>
        private void Visit(
            JsonElement nodes,
            int index,
            Matrix4x4 parent,
            List<Model.Part> parts,
            ref Vector3 min,
            ref Vector3 max,
            ref int triangles,
            ref int vertices)
        {
            JsonElement node = nodes[index];
            Matrix4x4 world = ReadNodeTransform(node) * parent;

            if (node.TryGetProperty("mesh", out JsonElement meshRef))
            {
                string name = GetString(node, "name", $"node{index}");
                JsonElement mesh = Get(_root, "meshes")[meshRef.GetInt32()];

                // **1つのメッシュが複数のプリミティブを持つ**ことがある。
                // 「マテリアルが違う面のかたまり」ごとに分かれていて、
                // 描画としては別々のドローコールになる。
                foreach (JsonElement primitive in Get(mesh, "primitives").EnumerateArray())
                {
                    Model.Part? part = ReadPrimitive(primitive, world, name, ref min, ref max, ref triangles, ref vertices);
                    if (part is not null)
                    {
                        parts.Add(part.Value);
                    }
                }
            }

            if (node.TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    Visit(nodes, child.GetInt32(), world, parts, ref min, ref max, ref triangles, ref vertices);
                }
            }
        }

        /// <summary>
        /// ノードのローカル行列。**2通りの書き方がある**。
        ///
        ///   1. <c>matrix</c> … 16 個の float。列優先で並んでいる
        ///   2. <c>translation</c> / <c>rotation</c> / <c>scale</c> … 分解された形
        ///
        /// どちらか一方しか現れない(仕様で排他)。分解された形のほうが多いが、
        /// Blender の書き出しなどは <c>matrix</c> を使うことがある(BoxTextured がそれ)。
        ///
        /// <b>掛ける順は S → R → T</b>。拡大してから回して、最後に運ぶ。
        /// 逆にすると、回転したあとの軸に沿って拡大されて形が歪む。
        /// </summary>
        private static Matrix4x4 ReadNodeTransform(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out JsonElement matrix))
            {
                Span<float> m = stackalloc float[16];
                int i = 0;
                foreach (JsonElement value in matrix.EnumerateArray())
                {
                    m[i++] = value.GetSingle();
                }

                // glTF は列優先(column-major)で並べる。
                // System.Numerics.Matrix4x4 は行優先(M11 が先頭、行ベクトル規約)なので、
                // **並べ替えではなく転置して受け取る**ことになる。
                // Day 14 の要点4で見た「同じ変換を、規約の違う2つの書き方で表す」がここにも出る。
                return new Matrix4x4(
                    m[0], m[1], m[2], m[3],
                    m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11],
                    m[12], m[13], m[14], m[15]);
            }

            Vector3 translation = ReadVector3(node, "translation", Vector3.Zero);
            Vector3 scale = ReadVector3(node, "scale", Vector3.One);

            // 回転は**クォータニオン (x, y, z, w)**。Day 5 で四元数を触っておいたのが効く。
            // オイラー角ではないのは、補間したときに素直に回るのと、
            // ジンバルロックが無いため(特論 A-4)。
            var rotation = Quaternion.Identity;
            if (node.TryGetProperty("rotation", out JsonElement r) && r.GetArrayLength() == 4)
            {
                rotation = new Quaternion(
                    r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
            }

            return Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation);
        }

        /// <summary>プリミティブ1個を <see cref="Mesh{TVertex}"/> に組み立てる。</summary>
        private Model.Part? ReadPrimitive(
            JsonElement primitive,
            Matrix4x4 world,
            string name,
            ref Vector3 min,
            ref Vector3 max,
            ref int triangles,
            ref int vertices)
        {
            // TRIANGLES 以外(点・線・ストリップ)は今日は捨てる。
            // 黙って捨てると「一部だけ出ない」で悩むので知らせておく。
            int mode = GetInt(primitive, "mode", ModeTriangles);
            if (mode != ModeTriangles)
            {
                Console.WriteLine($"[glTF] {name}: mode {mode} は未対応なので飛ばします(4=TRIANGLES のみ)");
                return null;
            }

            JsonElement attributes = Get(primitive, "attributes");

            // **POSITION だけが必須**。仕様でそう決まっている。
            if (!attributes.TryGetProperty("POSITION", out JsonElement positionRef))
            {
                return null;
            }

            Vector3[] positions = ReadVector3Accessor(positionRef.GetInt32());

            Vector3[]? normals = attributes.TryGetProperty("NORMAL", out JsonElement normalRef)
                ? ReadVector3Accessor(normalRef.GetInt32())
                : null;

            Vector2[]? uvs = attributes.TryGetProperty("TEXCOORD_0", out JsonElement uvRef)
                ? ReadVector2Accessor(uvRef.GetInt32())
                : null;

            // **TANGENT は Day 34 で読むようになった**。VEC4 で、xyz が接線、w が従接線の符号。
            //
            // 持っていないモデルのほうが多い(4体のうち WaterBottle と Lantern だけ)。
            // 無ければ位置と UV から作る——それが <see cref="GenerateTangents"/>。
            Vector4[]? tangents = !_forceGenerateTangents
                && attributes.TryGetProperty("TANGENT", out JsonElement tangentRef)
                ? ReadVector4Accessor(tangentRef.GetInt32())
                : null;

            var built = new Vertex[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 position = positions[i];

                // 法線が無いモデルもある。仕様上は「面法線を使え」となっているが、
                // その場で計算すると頂点の共有をやめる必要がある。
                // 今日読むモデルは全部持っているので、無いときは上向きで済ませる。
                Vector3 normal = normals is not null ? normals[i] : Vector3.UnitY;

                // **V を反転する**。Day 10 の OBJ で踏んだのとまったく同じ話。
                // glTF の UV は「左上が (0,0)」で、OpenGL のテクスチャは「左下が (0,0)」。
                // Texture 側で画像を上下反転して読んでいる(Day 15)ので、
                // ここでも合わせて反転すると辻褄が合う。
                Vector2 uv = uvs is not null
                    ? new Vector2(uvs[i].X, 1.0f - uvs[i].Y)
                    : Vector2.Zero;

                // **接線の w はここでは触らない**(Day 34 の落とし穴)。
                //
                // 上で V を反転しているので、「V が増える向き」は
                // ファイルの言う従接線と**逆**になる。それなら w も反転すべきに見えるが、逆。
                //
                // 法線マップの緑成分は「ファイルの従接線に沿ってどれだけ傾いているか」を表す。
                // そして UV の反転と画像の上下反転は打ち消し合うので、
                // シェーダが読むテクセルは**ファイルの想定どおりの場所**になる。
                // つまり緑の意味も**ファイルの従接線のまま**なので、w もそのまま使うのが正しい。
                //
                // ここを「反転しているのだから w も」と直すと、
                // **凹凸が全部へこみになる**(でこぼこの向きだけが裏返る)。
                // 絵は出るので気づきにくい類の間違いで、Alt+8 でわざと再現できるようにしてある。
                Vector4 tangent = tangents is not null
                    ? tangents[i]
                    : new Vector4(1.0f, 0.0f, 0.0f, 1.0f);

                built[i] = new Vertex(position, uv, Vector4.One, normal, tangent);

                // 境界箱は**世界行列を通したあと**で取る。
                // ローカルのままだと、ノードの平行移動(街灯は 13m 上にある)が反映されない。
                Vector3 worldPosition = Vector3.Transform(position, world);
                min = Vector3.Min(min, worldPosition);
                max = Vector3.Max(max, worldPosition);
            }

            uint[] indices = primitive.TryGetProperty("indices", out JsonElement indicesRef)
                ? ReadIndexAccessor(indicesRef.GetInt32())

                // インデックスが無いときは 0,1,2,… と並んでいるものとして扱う(仕様どおり)。
                : Enumerable.Range(0, built.Length).Select(i => (uint)i).ToArray();

            // **接線が無ければ作る**(Day 34)。インデックスが要るので、ここまで来てから。
            // 作ったかどうかを覚えておくのは、HUD と自己チェックで区別を出すため。
            if (tangents is null && uvs is not null && normals is not null)
            {
                GenerateTangents(built, indices, uvs);
                GeneratedTangentParts++;
            }
            else if (tangents is not null)
            {
                FileTangentParts++;
            }

            triangles += indices.Length / 3;
            vertices += built.Length;

            int materialIndex = GetInt(primitive, "material", -1);
            Material material = GetOrCreateMaterial(materialIndex);

            var mesh = new Mesh<Vertex>(_gl, built, indices, Vertex.Attributes);
            return new Model.Part(mesh, material, world, name);
        }

        /// <summary>
        /// **接線を位置と UV から作る**(Day 34)。TANGENT を持たないモデル用。
        ///
        /// 求めたいのは「UV の U が増える向きは、3D 空間ではどちらか」。
        /// 三角形1枚には頂点が3つあり、それぞれ位置と UV を持っているので、
        /// **連立方程式を1つ解けば出る**。
        ///
        /// 三角形の2辺を、位置の差と UV の差で書くと
        /// <code>
        ///   E1 = T * du1 + B * dv1
        ///   E2 = T * du2 + B * dv2
        /// </code>
        /// 未知が T と B の2本、式も2本なので解ける。答えが下のコードで、
        /// <c>r = 1 / (du1*dv2 - du2*dv1)</c> は 2x2 行列の逆行列の分母
        /// (= UV の三角形の面積の2倍)にあたる。
        ///
        /// <para>
        /// <b>頂点は複数の三角形に共有される</b>ので、面ごとに出した T を足し込んでいく。
        /// 法線を頂点で共有するときに面法線を平均するのと同じ理屈で、
        /// これで隣り合う面の間で接線が滑らかに繋がる。
        /// </para>
        ///
        /// <para>
        /// <b>UV が退化した三角形に注意</b>。UV の三角形が潰れている
        /// (3頂点が同じ UV を持つ、など)と分母が 0 になって NaN が出る。
        /// NaN は足し込みで**周りの頂点まで巻き込んで伝染する**ので、
        /// 面積が 0 に近い三角形はそこで捨てる。
        /// モデルの一部が真っ黒になるときの原因として、かなり上位に来る。
        /// </para>
        ///
        /// <para>
        /// <b>UV は反転前のものを使う</b>のがここの肝。<paramref name="sourceUvs"/> は
        /// ファイルから読んだそのままで、頂点に入れた「V を反転したもの」ではない。
        /// 反転した UV で作ると従接線の向きが逆になり、
        /// **ファイルが TANGENT を持っているモデルと符号の約束が食い違う**。
        /// 同じシェーダで両方を扱うので、規約はファイル側に合わせる。
        /// </para>
        ///
        /// なお実用では <c>MikkTSpace</c>(Blender などが使う標準実装)に合わせるのが定石で、
        /// **焼いた法線マップとまったく同じ接線でないと、細かい継ぎ目が出る**。
        /// ここで作るのは素直な平均版なので、法線マップを焼いたツールとは厳密には一致しない。
        /// </summary>
        private static void GenerateTangents(Vertex[] vertices, uint[] indices, Vector2[] sourceUvs)
        {
            var accumulatedTangent = new Vector3[vertices.Length];
            var accumulatedBitangent = new Vector3[vertices.Length];

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                uint i0 = indices[i];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];

                Vector3 e1 = vertices[i1].Position - vertices[i0].Position;
                Vector3 e2 = vertices[i2].Position - vertices[i0].Position;

                Vector2 duv1 = sourceUvs[i1] - sourceUvs[i0];
                Vector2 duv2 = sourceUvs[i2] - sourceUvs[i0];

                float determinant = (duv1.X * duv2.Y) - (duv2.X * duv1.Y);

                // 退化した UV。ここで捨てないと NaN が伝染する(上のコメント)。
                if (MathF.Abs(determinant) < 1e-12f)
                {
                    continue;
                }

                float r = 1.0f / determinant;

                Vector3 tangent = ((e1 * duv2.Y) - (e2 * duv1.Y)) * r;
                Vector3 bitangent = ((e2 * duv1.X) - (e1 * duv2.X)) * r;

                foreach (uint index in (ReadOnlySpan<uint>)[i0, i1, i2])
                {
                    accumulatedTangent[index] += tangent;
                    accumulatedBitangent[index] += bitangent;
                }
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 normal = vertices[i].Normal;
                Vector3 tangent = accumulatedTangent[i];

                // **グラム・シュミットで法線に直交させる**。
                // 足し込んだ接線は、法線とぴったり直角にはなっていない
                // (共有する面ごとに少しずつ傾いているため)。
                // 法線の向きの成分を引くと、面に乗った成分だけが残る。
                tangent -= normal * Vector3.Dot(normal, tangent);

                // 直交させた結果が 0 になることがある(接線が法線と平行だった)。
                // そのときは適当な直交ベクトルで埋める——**絵は狂うが NaN よりまし**。
                tangent = tangent.LengthSquared() > 1e-12f
                    ? Vector3.Normalize(tangent)
                    : Orthogonal(normal);

                // **w は cross(N, T) が従接線と同じ向きかどうか**。
                // 逆を向いていれば -1。UV が鏡像になっている面がこれになる。
                float handedness =
                    Vector3.Dot(Vector3.Cross(normal, tangent), accumulatedBitangent[i]) < 0.0f
                        ? -1.0f
                        : 1.0f;

                vertices[i].Tangent = new Vector4(tangent, handedness);
            }

            // 法線に直交する適当な1本。**どの軸と組んでも平行にならない**ように選ぶ。
            static Vector3 Orthogonal(Vector3 normal) =>
                Vector3.Normalize(Vector3.Cross(
                    normal,
                    MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
        }

        // ===== アクセサ =====
        //
        // ここが glTF のいちばん機械的なところ。
        // 「型(VEC3)」「成分の型(float)」「個数」「どこから」を組み合わせて切り出す。

        private Vector3[] ReadVector3Accessor(int index)
        {
            JsonElement accessor = Get(_root, "accessors")[index];
            RequireType(accessor, "VEC3", index);
            RequireComponent(accessor, ComponentFloat, index);

            int count = GetInt(accessor, "count", 0);
            var result = new Vector3[count];
            ReadFloats(accessor, count, 3, result.AsSpan());
            return result;
        }

        private Vector4[] ReadVector4Accessor(int index)
        {
            JsonElement accessor = Get(_root, "accessors")[index];
            RequireType(accessor, "VEC4", index);
            RequireComponent(accessor, ComponentFloat, index);

            int count = GetInt(accessor, "count", 0);
            var result = new Vector4[count];
            ReadFloats(accessor, count, 4, result.AsSpan());
            return result;
        }

        private Vector2[] ReadVector2Accessor(int index)
        {
            JsonElement accessor = Get(_root, "accessors")[index];
            RequireType(accessor, "VEC2", index);
            RequireComponent(accessor, ComponentFloat, index);

            int count = GetInt(accessor, "count", 0);
            var result = new Vector2[count];
            ReadFloats(accessor, count, 2, result.AsSpan());
            return result;
        }

        /// <summary>
        /// float の並びを構造体の配列へ流し込む。
        ///
        /// **byteStride(飛び飛びの並び)に対応するのがここの肝**。
        /// glTF は「位置・法線・UV を1頂点ずつ交互に並べる(interleaved)」書き方も許していて、
        /// その場合 bufferView に <c>byteStride</c> が入る。
        /// stride が 0(または未指定)なら詰めて並んでいる。
        ///
        /// これを無視して「詰めて並んでいる」と決め打ちすると、
        /// **interleaved なモデルだけ頂点がぐちゃぐちゃになる**。
        /// 今日の3体は全部 stride 無しなので、対応を落としても動いてしまう——
        /// つまり「動いたから正しい」が言えないところ。
        /// </summary>
        private unsafe void ReadFloats<T>(JsonElement accessor, int count, int components, Span<T> destination)
            where T : unmanaged
        {
            (byte[] buffer, int start, int stride) = Locate(accessor, components * sizeof(float));

            fixed (T* target = destination)
            {
                var floats = new Span<float>(target, count * components);
                for (int i = 0; i < count; i++)
                {
                    int offset = start + (i * stride);
                    for (int c = 0; c < components; c++)
                    {
                        floats[(i * components) + c] =
                            BitConverter.ToSingle(buffer, offset + (c * sizeof(float)));
                    }
                }
            }
        }

        /// <summary>
        /// インデックスを読む。**成分の型が3通りある**。
        ///
        /// 頂点が 65536 個未満なら u16 で足りるので、多くのモデルは u16 を使う
        /// (今日の3体とも u16)。u32 が要るのは大きなモデルだけで、
        /// u8 は小さすぎてほぼ見ないが、仕様には載っている。
        ///
        /// **こちらは常に uint に広げてしまう**。GPU 側は u16 のほうが帯域が半分で済むが、
        /// <see cref="Mesh{TVertex}"/> が uint 固定なので今日はそこまで踏み込まない。
        /// </summary>
        private uint[] ReadIndexAccessor(int index)
        {
            JsonElement accessor = Get(_root, "accessors")[index];
            RequireType(accessor, "SCALAR", index);

            int componentType = GetInt(accessor, "componentType", 0);
            int size = componentType switch
            {
                ComponentUnsignedByte or ComponentByte => 1,
                ComponentUnsignedShort or ComponentShort => 2,
                ComponentUnsignedInt => 4,
                _ => throw new InvalidDataException(
                    $"accessor {index}: インデックスの型 {componentType} は未対応です"),
            };

            int count = GetInt(accessor, "count", 0);
            (byte[] buffer, int start, int stride) = Locate(accessor, size);

            var result = new uint[count];
            for (int i = 0; i < count; i++)
            {
                int offset = start + (i * stride);
                result[i] = size switch
                {
                    1 => buffer[offset],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset)),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset)),
                };
            }

            return result;
        }

        /// <summary>
        /// アクセサが指す「どのバイト列の、どこから、何バイトおきか」を解決する。
        /// **accessor → bufferView → buffer の3段を1回で降りる**。
        /// </summary>
        /// <param name="elementSize">1要素のバイト数。stride が無いときの既定値になる。</param>
        private (byte[] Buffer, int Start, int Stride) Locate(JsonElement accessor, int elementSize)
        {
            if (accessor.TryGetProperty("sparse", out _))
            {
                // sparse は「大部分が同じ値で、一部だけ違う」データを縮めて持つ仕組み。
                // モーフターゲットで使われるので、Day 41 で必要になったら書く。
                throw new NotSupportedException("sparse アクセサは未対応です");
            }

            int viewIndex = GetInt(accessor, "bufferView", -1);
            if (viewIndex < 0)
            {
                throw new InvalidDataException("bufferView を持たないアクセサは未対応です");
            }

            JsonElement view = Get(_root, "bufferViews")[viewIndex];
            byte[] buffer = GetBuffer(GetInt(view, "buffer", 0));

            // **オフセットが2段ある**。bufferView の中での位置と、
            // その中でのアクセサの位置。足し合わせて初めて実際の場所になる。
            int start = GetInt(view, "byteOffset", 0) + GetInt(accessor, "byteOffset", 0);
            int stride = GetInt(view, "byteStride", 0);

            return (buffer, start, stride > 0 ? stride : elementSize);
        }

        /// <summary>
        /// buffer の実体を取り出す。**3通りの出どころがある**。
        ///
        ///   1. glb の BIN チャンク … <c>uri</c> が無い
        ///   2. 外部ファイル … <c>uri</c> が相対パス(BoxTextured0.bin)
        ///   3. 埋め込み … <c>uri</c> が <c>data:application/octet-stream;base64,…</c>
        ///
        /// 3 は .gltf 1ファイルで完結させたいときに使われる。
        /// base64 はバイト数が 4/3 に増えるので、大きなモデルには向かない。
        /// </summary>
        private byte[] GetBuffer(int index)
        {
            if (_buffers.TryGetValue(index, out byte[]? cached))
            {
                return cached;
            }

            JsonElement buffer = Get(_root, "buffers")[index];
            byte[] bytes;

            if (!buffer.TryGetProperty("uri", out JsonElement uri))
            {
                bytes = _embedded
                    ?? throw new InvalidDataException($"buffer {index}: uri が無いのに BIN チャンクがありません");
            }
            else
            {
                bytes = ReadUri(uri.GetString() ?? string.Empty);
            }

            _buffers[index] = bytes;
            return bytes;
        }

        /// <summary>
        /// uri を解決してバイト列にする。data URI と相対パスの両方。
        /// </summary>
        private byte[] ReadUri(string uri)
        {
            const string dataPrefix = "data:";
            if (uri.StartsWith(dataPrefix, StringComparison.Ordinal))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("base64 以外の data URI は未対応です");
                }

                return Convert.FromBase64String(uri[(comma + 1)..]);
            }

            // **URI なのでパーセントエンコードされている**。
            // 「My Model/tex 01.png」のような名前は "My%20Model/tex%2001.png" と書かれる。
            // そのまま File.ReadAllBytes に渡すと「ファイルが無い」になる。
            string relative = Uri.UnescapeDataString(uri);
            return File.ReadAllBytes(Path.Combine(_directory, relative));
        }

        // ===== マテリアル =====

        /// <summary>
        /// マテリアルを作る(同じ番号なら使い回す)。
        ///
        /// glTF の材質は **metallic-roughness ワークフロー**で定義されている。
        /// 「拡散色 + 鏡面色」ではなく「ベースカラー + 金属か否か + 粗さ」で表すやり方で、
        /// 物理的に破綻した組み合わせを作りにくいのが利点(Day 35 で本番)。
        ///
        /// <b>今日はベースカラーしか絵に使わない</b>が、
        /// **読むところまでは全部やる**。法線マップは Day 34、
        /// メタリック/ラフネスは Day 35、AO は Day 37 で使い始める——
        /// そのとき「読み込みは済んでいる」状態にしておくと、
        /// その日の差分がシェーダだけになる。
        /// </summary>
        private Material GetOrCreateMaterial(int index)
        {
            if (_materials.TryGetValue(index, out Material? cached))
            {
                return cached;
            }

            var material = new Material(_shader);

            if (index >= 0
                && _root.TryGetProperty("materials", out JsonElement materials)
                && index < materials.GetArrayLength())
            {
                JsonElement source = materials[index];
                material.Name = GetString(source, "name", $"material{index}");

                if (source.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr))
                {
                    material.BaseColorFactor = ReadVector4(pbr, "baseColorFactor", Vector4.One);
                    material.MetallicFactor = GetFloat(pbr, "metallicFactor", 1.0f);
                    material.RoughnessFactor = GetFloat(pbr, "roughnessFactor", 1.0f);

                    // **ベースカラーだけが色**。ここだけ sRGB で読む。
                    material.MainTexture = ReadTexture(pbr, "baseColorTexture", srgb: true);

                    // メタリック/ラフネスは1枚に詰められている(B=金属度, G=粗さ)。
                    // 数値なので**リニアで読む**。
                    material.MetallicRoughnessTexture = ReadTexture(pbr, "metallicRoughnessTexture", srgb: false);
                }

                material.NormalTexture = ReadTexture(source, "normalTexture", srgb: false);

                // **法線の強さは normalTexture の中に入っている**(Day 34)。
                // baseColorFactor のようにマテリアル直下ではなく、
                // テクスチャ参照の側に付くのが glTF の書き方。
                // 既定は 1.0 なので、指定が無ければ素通しになる。
                if (source.TryGetProperty("normalTexture", out JsonElement normalRef))
                {
                    material.NormalScale = GetFloat(normalRef, "scale", 1.0f);
                }

                material.OcclusionTexture = ReadTexture(source, "occlusionTexture", srgb: false);

                // 発光は色なので sRGB。
                material.EmissiveTexture = ReadTexture(source, "emissiveTexture", srgb: true);
                material.EmissiveFactor = ReadVector3(source, "emissiveFactor", Vector3.Zero);

                material.DoubleSided = source.TryGetProperty("doubleSided", out JsonElement ds) && ds.GetBoolean();
                material.AlphaMode = GetString(source, "alphaMode", "OPAQUE");
                material.AlphaCutoff = GetFloat(source, "alphaCutoff", 0.5f);
            }

            // シェーダは色を掛け算するので、Tint は素通し(白)にしておく。
            // 色味は BaseColorFactor のほうが持つ。
            material.Tint = Vector4.One;

            _materials[index] = material;
            return material;
        }

        /// <summary>
        /// <c>{"index": 3, "texCoord": 0}</c> の形からテクスチャを解決する。
        ///
        /// texture → image の1段があるのは、**同じ画像を違うサンプラ設定で使い回せる**ようにするため
        /// (片方は Repeat、もう片方は ClampToEdge、など)。
        /// </summary>
        private Handle<Texture> ReadTexture(JsonElement owner, string property, bool srgb)
        {
            if (!owner.TryGetProperty(property, out JsonElement reference))
            {
                return Handle<Texture>.None;
            }

            int textureIndex = GetInt(reference, "index", -1);
            if (textureIndex < 0 || !_root.TryGetProperty("textures", out JsonElement textures))
            {
                return Handle<Texture>.None;
            }

            // TEXCOORD_1 以降を指すマテリアルは、UV を2組持つ必要がある。
            // Vertex が1組しか持っていないので、今日は知らせて0番で代用する。
            int texCoord = GetInt(reference, "texCoord", 0);
            if (texCoord != 0)
            {
                Console.WriteLine($"[glTF] {property}: TEXCOORD_{texCoord} は未対応なので 0 番で代用します");
            }

            JsonElement texture = textures[textureIndex];
            int imageIndex = GetInt(texture, "source", -1);
            if (imageIndex < 0)
            {
                return Handle<Texture>.None;
            }

            JsonElement image = Get(_root, "images")[imageIndex];
            Handle<Texture> handle;

            if (image.TryGetProperty("uri", out JsonElement uri))
            {
                string value = uri.GetString() ?? string.Empty;
                if (value.StartsWith("data:", StringComparison.Ordinal))
                {
                    handle = _resources.LoadTextureFromMemory(
                        $"{_path}#image{imageIndex}", ReadUri(value), srgb: srgb);
                }
                else
                {
                    // **外部ファイルはパスで読む**。同じ絵を別のモデルが使っていれば、
                    // RenderResources の重複排除がそのまま効く。
                    handle = _resources.LoadTexture(
                        Path.Combine(_directory, Uri.UnescapeDataString(value)), srgb: srgb);
                }
            }
            else
            {
                // 埋め込み。bufferView が指す範囲がそのまま PNG / JPEG のバイト列。
                int viewIndex = GetInt(image, "bufferView", -1);
                if (viewIndex < 0)
                {
                    return Handle<Texture>.None;
                }

                JsonElement view = Get(_root, "bufferViews")[viewIndex];
                byte[] buffer = GetBuffer(GetInt(view, "buffer", 0));
                int start = GetInt(view, "byteOffset", 0);
                int length = GetInt(view, "byteLength", 0);

                handle = _resources.LoadTextureFromMemory(
                    $"{_path}#image{imageIndex}", buffer.AsSpan(start, length), srgb: srgb);
            }

            ApplySampler(texture, handle);

            if (handle.IsValid)
            {
                _textures.Add(handle);
            }

            return handle;
        }

        /// <summary>
        /// サンプラの設定(拡大縮小の補間、繰り返し方)をテクスチャへ反映する。
        ///
        /// glTF は GL の定数をそのまま数値で持っている(9729 = GL_LINEAR など)。
        /// 仕様が OpenGL ES 2.0 を土台にしているためで、
        /// **数字を見て意味が分かるのは GL を触ったことがある人だけ**という珍しい設計になっている。
        ///
        /// サンプラが無いときは「実装の好きにしてよい」なので、こちらの既定
        /// (Linear + Repeat)のままにする。
        /// </summary>
        private void ApplySampler(JsonElement texture, Handle<Texture> handle)
        {
            if (!handle.IsValid
                || !texture.TryGetProperty("sampler", out JsonElement samplerRef)
                || !_root.TryGetProperty("samplers", out JsonElement samplers))
            {
                return;
            }

            JsonElement sampler = samplers[samplerRef.GetInt32()];
            Texture target = _resources.GetTexture(handle);

            const int nearest = 9728;
            const int repeat = 10497;

            if (GetInt(sampler, "magFilter", 0) == nearest)
            {
                target.SetFilter(TextureFilter.Nearest);
            }

            // S(横)と T(縦)は別々に設定できるが、
            // Texture 側が1つにまとめているので、片方でも ClampToEdge ならそちらにする。
            int wrapS = GetInt(sampler, "wrapS", repeat);
            int wrapT = GetInt(sampler, "wrapT", repeat);
            if (wrapS != repeat || wrapT != repeat)
            {
                target.SetWrap(TextureWrap.ClampToEdge);
            }
        }

        // ===== JSON の小道具 =====
        //
        // System.Text.Json は「無ければ例外」なので、
        // **省略可能なプロパティだらけの glTF**とは相性が悪い。
        // 「無ければ既定値」を1行で書けるようにしておく。

        private static JsonElement Get(JsonElement owner, string name) =>
            owner.TryGetProperty(name, out JsonElement value) ? value : default;

        private static int GetInt(JsonElement owner, string name, int fallback) =>
            owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : fallback;

        private static float GetFloat(JsonElement owner, string name, float fallback) =>
            owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetSingle()
                : fallback;

        private static string GetString(JsonElement owner, string name, string fallback) =>
            owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

        private static Vector3 ReadVector3(JsonElement owner, string name, Vector3 fallback) =>
            owner.TryGetProperty(name, out JsonElement value) && value.GetArrayLength() >= 3
                ? new Vector3(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle())
                : fallback;

        private static Vector4 ReadVector4(JsonElement owner, string name, Vector4 fallback) =>
            owner.TryGetProperty(name, out JsonElement value) && value.GetArrayLength() >= 4
                ? new Vector4(
                    value[0].GetSingle(), value[1].GetSingle(),
                    value[2].GetSingle(), value[3].GetSingle())
                : fallback;

        private static void RequireType(JsonElement accessor, string expected, int index)
        {
            string actual = GetString(accessor, "type", "?");
            if (actual != expected)
            {
                throw new InvalidDataException($"accessor {index}: {expected} のはずが {actual} でした");
            }
        }

        private static void RequireComponent(JsonElement accessor, int expected, int index)
        {
            int actual = GetInt(accessor, "componentType", 0);
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"accessor {index}: componentType {expected} のはずが {actual} でした");
            }
        }
    }

    /// <summary>
    /// ファイルの中身を1行にまとめる(自己チェックとコンソール表示用)。
    /// **読み込まずに構成だけ見たい**ときのために切り出してある。
    /// </summary>
    public static string Describe(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool isBinary = bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == GlbMagic;

        JsonDocument json = isBinary ? ReadGlb(bytes, path).Json : JsonDocument.Parse(bytes);
        using (json)
        {
            JsonElement root = json.RootElement;
            var text = new StringBuilder();
            text.Append(isBinary ? "glb" : "gltf");
            text.Append($"  {bytes.Length / 1024.0:F0}KB");

            foreach (string name in (string[])["nodes", "meshes", "materials", "textures", "images", "accessors"])
            {
                int count = root.TryGetProperty(name, out JsonElement array) ? array.GetArrayLength() : 0;
                text.Append($"  {name}:{count}");
            }

            if (root.TryGetProperty("asset", out JsonElement asset))
            {
                text.Append($"  generator:{GetGenerator(asset)}");
            }

            return text.ToString();

            static string GetGenerator(JsonElement asset) =>
                asset.TryGetProperty("generator", out JsonElement value) ? value.GetString() ?? "?" : "?";
        }
    }
}
