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
/// <b>Day 41 で足したもの</b>。スキン(<c>skins</c>)、アニメーション(<c>animations</c>)、
/// 頂点の <c>JOINTS_0</c> / <c>WEIGHTS_0</c>、そしてノードの木を捨てずに持ち帰ること。
/// ノードを平らに畳んでいた Day 32 の判断が、ここで初めて足りなくなる。
/// </para>
///
/// <para>
/// <b>まだ読まないもの</b>。モーフターゲット、カメラ、ライト、
/// sparse アクセサ、拡張(KHR_*)、TRIANGLES 以外の描画モード。
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

        /// <summary>1回だけ出す知らせの記録(Day 41)。</summary>
        private readonly HashSet<string> _warned = [];

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

        /// <summary>法線をこちらで作ったパーツの数(Day 41)。**Fox が NORMAL を持たない**。</summary>
        public int GeneratedNormalParts { get; private set; }

        /// <summary>ノードの木(Day 41)。<see cref="ReadNodes"/> が埋める。</summary>
        private ModelNode[] _nodes = [];

        /// <summary>親が子より先に来るノード番号の並び(Day 41)。</summary>
        private int[] _nodeOrder = [];

        /// <summary>スキン(Day 41)。<see cref="ReadSkins"/> が埋める。</summary>
        private Skin[] _skins = [];

        /// <summary>アニメーションクリップ(Day 41)。<see cref="ReadAnimations"/> が埋める。</summary>
        private AnimationClip[] _animations = [];

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

            // **ノードとスキンを先に読む**(Day 41)。
            // プリミティブを組むときに「このメッシュはどのスキンを使うか」が要るので、
            // 木の形が確定していないと始まらない。
            ReadNodes();
            ReadSkins();

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

            // アニメーションは最後でよい。ノード番号しか参照しないので、
            // メッシュを組む前でも後でも結果は同じ。
            ReadAnimations();

            return new Model(
                _resources, parts, _materials.Values.ToArray(), _textures, min, max, _path,
                _nodes, _nodeOrder, _skins, _animations)
            {
                TriangleCount = triangles,
                VertexCount = vertices,
                FileTangentParts = FileTangentParts,
                GeneratedTangentParts = GeneratedTangentParts,
                GeneratedNormalParts = GeneratedNormalParts,
            };
        }

        // ===== ノードの木(Day 41)=====

        /// <summary>
        /// ノードを全部読んで、**親の番号と並べ替えの順**を確定させる。
        ///
        /// <para>
        /// glTF のノードは <b>子の一覧しか持たない</b>(親へのリンクは無い)。
        /// 世界行列の計算に要るのは逆向きなので、1回舐めて反転させる。
        /// </para>
        ///
        /// <para>
        /// <b>並べ替えが要る理由</b>。<c>world[i] = local(i) * world[parent]</c> を
        /// 番号順に回すと、子が親より前に書かれているファイルで
        /// **1フレーム古い親の行列**を掛けてしまう。
        /// 絵としては「腕だけ1フレーム遅れてついてくる」というたちの悪い出方をする。
        /// 根から深さ優先で降りた順に並べておけば、この問題が構造的に消える。
        /// </para>
        ///
        /// <para>
        /// <b>シーンに繋がっていないノードも読む</b>。関節がシーングラフの外に置かれた
        /// ファイルは実在するし、アニメーションはノード番号で名指ししてくるので、
        /// 「描かないノード」でも姿勢は要る。
        /// </para>
        /// </summary>
        private void ReadNodes()
        {
            JsonElement nodes = Get(_root, "nodes");
            int count = nodes.ValueKind == JsonValueKind.Array ? nodes.GetArrayLength() : 0;

            var parents = new int[count];
            Array.Fill(parents, -1);

            for (int i = 0; i < count; i++)
            {
                if (!nodes[i].TryGetProperty("children", out JsonElement children))
                {
                    continue;
                }

                foreach (JsonElement child in children.EnumerateArray())
                {
                    int index = child.GetInt32();
                    if ((uint)index < (uint)count)
                    {
                        parents[index] = i;
                    }
                }
            }

            var built = new ModelNode[count];
            for (int i = 0; i < count; i++)
            {
                JsonElement node = nodes[i];
                built[i] = new ModelNode(
                    GetString(node, "name", $"node{i}"),
                    parents[i],
                    ReadNodePose(node, i),
                    GetInt(node, "mesh", -1),
                    GetInt(node, "skin", -1));
            }

            _nodes = built;
            _nodeOrder = BuildNodeOrder(nodes, parents, count);
        }

        /// <summary>
        /// 根から深さ優先で降りて、**親が必ず子より先に来る並び**を作る。
        /// 木が forest(根が複数)であることは仕様が保証している。
        /// </summary>
        private static int[] BuildNodeOrder(JsonElement nodes, int[] parents, int count)
        {
            var order = new List<int>(count);
            var stack = new Stack<int>();

            for (int i = count - 1; i >= 0; i--)
            {
                if (parents[i] < 0)
                {
                    stack.Push(i);
                }
            }

            while (stack.Count > 0)
            {
                int node = stack.Pop();
                order.Add(node);

                if (!nodes[node].TryGetProperty("children", out JsonElement children))
                {
                    continue;
                }

                foreach (JsonElement child in children.EnumerateArray())
                {
                    int index = child.GetInt32();
                    if ((uint)index < (uint)count)
                    {
                        stack.Push(index);
                    }
                }
            }

            // **全ノードが並びに現れたことを確かめる**。現れないのは親子関係が輪になっている
            // (仕様違反の)ファイルで、そのまま進むと姿勢が更新されないノードが残る。
            if (order.Count == count)
            {
                return order.ToArray();
            }

            Console.WriteLine(
                $"[glTF] ノードの親子関係が木になっていません({order.Count}/{count})。"
                + "たどれなかったノードは根として扱います");

            var seen = new bool[count];
            foreach (int node in order)
            {
                seen[node] = true;
            }

            for (int i = 0; i < count; i++)
            {
                if (!seen[i])
                {
                    order.Add(i);
                }
            }

            return order.ToArray();
        }

        /// <summary>
        /// ノードのローカル姿勢を <see cref="NodePose"/> として読む(Day 41)。
        ///
        /// <b><see cref="ReadNodeTransform"/> と二重に見えるが、必要な二重</b>。
        /// あちらは Day 32 から使っている「行列に畳んだ形」で、
        /// <c>matrix</c> で書かれたノードの値をそのまま(せん断も含めて)保てる。
        /// こちらは分解された形で、**アニメーションが書き換えられる**代わりに
        /// <c>matrix</c> のときは分解が要る。
        ///
        /// 分解は失敗しうる(せん断が入っていると解が無い)。
        /// 失敗したら単位姿勢にして知らせる——静的な描画は
        /// <c>Part.Transform</c>(畳んだ行列)を使うので、そちらは壊れない。
        /// </summary>
        private static NodePose ReadNodePose(JsonElement node, int index)
        {
            if (node.TryGetProperty("matrix", out _))
            {
                Matrix4x4 matrix = ReadNodeTransform(node);
                if (Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
                {
                    return new NodePose(translation, rotation, scale);
                }

                Console.WriteLine($"[glTF] node {index}: matrix を TRS に分解できません(単位姿勢で代用します)");
                return NodePose.Identity;
            }

            var pose = NodePose.Identity;
            pose.Translation = ReadVector3(node, "translation", Vector3.Zero);
            pose.Scale = ReadVector3(node, "scale", Vector3.One);

            if (node.TryGetProperty("rotation", out JsonElement r) && r.GetArrayLength() == 4)
            {
                pose.Rotation = new Quaternion(
                    r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
            }

            return pose;
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

                // **このノードがスキンを使うか**(Day 41)。使うならプリミティブの
                // JOINTS_0 / WEIGHTS_0 が意味を持ち、ノードの変換のほうは無視される。
                int skinIndex = GetInt(node, "skin", -1);

                // **1つのメッシュが複数のプリミティブを持つ**ことがある。
                // 「マテリアルが違う面のかたまり」ごとに分かれていて、
                // 描画としては別々のドローコールになる。
                foreach (JsonElement primitive in Get(mesh, "primitives").EnumerateArray())
                {
                    Model.Part? part = ReadPrimitive(
                        primitive, world, name, index, skinIndex,
                        ref min, ref max, ref triangles, ref vertices);
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
            int nodeIndex,
            int skinIndex,
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

            // **今日足した2本**(Day 41)。関節の番号と、その重み。
            //
            // JOINTS_0 は整数(unsigned byte か unsigned short)、
            // WEIGHTS_0 は float(あるいは正規化された整数)で入っている。
            // 末尾の _0 は「1組目」の意味で、5本以上の関節が要るモデルは
            // JOINTS_1 / WEIGHTS_1 を足して 8 本にする。
            // **今日のモデルは全部4本以内**なので 1 組目だけ読む。
            Vector4[]? joints = attributes.TryGetProperty("JOINTS_0", out JsonElement jointRef)
                ? ReadVector4Flexible(jointRef.GetInt32(), normalizeIntegers: false)
                : null;

            Vector4[]? weights = attributes.TryGetProperty("WEIGHTS_0", out JsonElement weightRef)
                ? ReadVector4Flexible(weightRef.GetInt32(), normalizeIntegers: true)
                : null;

            // **スキン付きのメッシュはノードの変換を使わない**(Day 41)。
            //
            // glTF の仕様が「スキン付きメッシュのノードの変換は無視すること」と定めている。
            // 関節行列 IBM * world(joint) のほうが、頂点をメッシュ空間からシーン空間へ
            // 運ぶ役目を最初から持っているためで、ノードの変換をさらに掛けると
            // **モデルがノードの分だけ二重にずれる**。
            //
            // CesiumMan はノードに Z-up → Y-up の回転が入っているので、
            // ここを間違えると人が横倒しになる、という分かりやすい出方をする。
            Matrix4x4 partWorld = skinIndex >= 0 ? Matrix4x4.Identity : world;

            // パーツ単位の境界箱(Day 39)。頂点を回すついでに取る。
            var partMin = new Vector3(float.MaxValue);
            var partMax = new Vector3(float.MinValue);

            var built = new Vertex[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 position = positions[i];

                // **法線が無いモデルは実在する**(Day 41 で Fox に当たった)。
                // Day 32 は「上向きで済ませる」としていたが、それだと
                // 全面が同じ明るさになって、せっかくのスキニングが見えない。
                // 下で三角形から作り直すので、ここではいったん 0 を置く。
                Vector3 normal = normals is not null ? normals[i] : Vector3.Zero;

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

                // **関節と重み**(Day 41)。持っていなければ全部 0 のまま。
                // シェーダは uSkinned で経路を分けるので、0 でも壊れない。
                if (joints is not null && weights is not null)
                {
                    built[i].Joints = joints[i];
                    built[i].Weights = NormalizeWeights(weights[i]);
                }

                // 境界箱は**世界行列を通したあと**で取る。
                // ローカルのままだと、ノードの平行移動(街灯は 13m 上にある)が反映されない。
                // スキン付きは partWorld が単位行列なので、
                // **バインドポーズでの大きさ**がそのまま出る(上のコメント)。
                Vector3 worldPosition = Vector3.Transform(position, partWorld);
                min = Vector3.Min(min, worldPosition);
                max = Vector3.Max(max, worldPosition);

                // **パーツ単位でも取る**(Day 39)。モデル全体のぶんとは別勘定で、
                // 「このパーツだけを置きたい」ときの大きさと足元がこれで分かる。
                partMin = Vector3.Min(partMin, worldPosition);
                partMax = Vector3.Max(partMax, worldPosition);
            }

            uint[] indices = primitive.TryGetProperty("indices", out JsonElement indicesRef)
                ? ReadIndexAccessor(indicesRef.GetInt32())

                // インデックスが無いときは 0,1,2,… と並んでいるものとして扱う(仕様どおり)。
                : Enumerable.Range(0, built.Length).Select(i => (uint)i).ToArray();

            // **法線が無ければ作る**(Day 41)。接線より先にやる——
            // 接線の生成が法線を使う(グラム・シュミットで直交させる)ため。
            if (normals is null)
            {
                GenerateNormals(built, indices);
                GeneratedNormalParts++;
            }

            // **接線が無ければ作る**(Day 34)。インデックスが要るので、ここまで来てから。
            // 作ったかどうかを覚えておくのは、HUD と自己チェックで区別を出すため。
            if (tangents is null && uvs is not null)
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
            return new Model.Part(mesh, material, partWorld, name, partMin, partMax, nodeIndex, skinIndex);
        }

        /// <summary>
        /// 重みの合計を 1 に揃える(Day 41)。
        ///
        /// <para>
        /// <b>合計が 1 でないファイルは普通に来る</b>。書き出し側の丸め、
        /// 影響する関節を4本に切り詰めたときの取りこぼし、正規化忘れ——原因はいろいろある。
        /// </para>
        ///
        /// <para>
        /// 合計が 0.9 だと何が起きるか。スキニングの式は
        /// <c>p' = Σ w[i] * M[i] * p</c> なので、
        /// **その頂点だけ 10% ぶん原点へ引き寄せられる**。
        /// モデル全体が縮むのではなく、特定の頂点だけがへこむので、
        /// 「関節のあたりだけ妙にとがっている」という出方になり、
        /// スキニングの実装ミスと区別が付きにくい。
        /// </para>
        ///
        /// <para>
        /// 合計が 0 の頂点(どの関節にも属していない)は、そのまま 0 にしておく。
        /// 0 で割らないためと、**スキンを持たないメッシュと同じ扱い**にできるため。
        /// </para>
        /// </summary>
        private static Vector4 NormalizeWeights(Vector4 weights)
        {
            float sum = weights.X + weights.Y + weights.Z + weights.W;
            return sum > 1e-6f ? weights / sum : Vector4.Zero;
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

        /// <summary>
        /// **法線を三角形から作る**(Day 41)。NORMAL を持たないモデル用。
        ///
        /// <para>
        /// 面の法線は外積1本で出る。<c>cross(e1, e2)</c> を
        /// **正規化せずに**足し込むのが定石で、外積の長さは三角形の面積の2倍なので、
        /// これで自動的に「大きい面ほど強く効く」重み付き平均になる。
        /// 正規化してから足すと、細長い三角形が不相応に効いてくる。
        /// </para>
        ///
        /// <para>
        /// <b>作った法線は必ず滑らかになる</b>(頂点を共有している面の平均になる)。
        /// 立方体の角のように**本当は折れているべき縁**も丸めてしまうが、
        /// それを分けるには「同じ位置の頂点を、法線の違いで割る」処理が要る。
        /// glTF の側は最初から割った状態で書き出すのが普通なので、
        /// 「NORMAL が無いファイルは、そもそも滑らかな形」と考えてよい。
        /// 実際 Fox はそれで問題なく見える。
        /// </para>
        /// </summary>
        private static void GenerateNormals(Vertex[] vertices, uint[] indices)
        {
            var accumulated = new Vector3[vertices.Length];

            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                uint i0 = indices[i];
                uint i1 = indices[i + 1];
                uint i2 = indices[i + 2];

                Vector3 e1 = vertices[i1].Position - vertices[i0].Position;
                Vector3 e2 = vertices[i2].Position - vertices[i0].Position;

                // **正規化しない**(上のコメント)。長さが面積の重みになる。
                Vector3 faceNormal = Vector3.Cross(e1, e2);

                accumulated[i0] += faceNormal;
                accumulated[i1] += faceNormal;
                accumulated[i2] += faceNormal;
            }

            for (int i = 0; i < vertices.Length; i++)
            {
                // どの三角形にも使われていない頂点(あるいは面積 0 の三角形だけ)は
                // 長さ 0 になる。**上向きで埋める**——絵は狂うが NaN よりまし。
                vertices[i].Normal = accumulated[i].LengthSquared() > 1e-20f
                    ? Vector3.Normalize(accumulated[i])
                    : Vector3.UnitY;
            }
        }

        // ===== スキンとアニメーション(Day 41)=====

        /// <summary>
        /// <c>skins</c> を読む。**関節のノード番号と逆バインド行列の2本だけ**。
        ///
        /// <see cref="Skin"/> のコメントにあるとおり、
        /// <c>inverseBindMatrices</c> は省略できる(その場合は全部単位行列)。
        /// メッシュと関節が最初から同じ座標系にある、という意味になる——
        /// SimpleSkin はまさにそれ……ではなく、ちゃんと持っている。
        /// 省略するファイルは実際にはほとんど無いが、仕様が許すので受けておく。
        /// </summary>
        private void ReadSkins()
        {
            if (!_root.TryGetProperty("skins", out JsonElement skins)
                || skins.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var result = new List<Skin>();
            int index = 0;

            foreach (JsonElement skin in skins.EnumerateArray())
            {
                var joints = new List<int>();
                foreach (JsonElement joint in Get(skin, "joints").EnumerateArray())
                {
                    joints.Add(joint.GetInt32());
                }

                int matrixAccessor = GetInt(skin, "inverseBindMatrices", -1);
                Matrix4x4[] inverseBind;

                if (matrixAccessor >= 0)
                {
                    inverseBind = ReadMatrix4Accessor(matrixAccessor);
                }
                else
                {
                    inverseBind = new Matrix4x4[joints.Count];
                    Array.Fill(inverseBind, Matrix4x4.Identity);
                }

                // **数が合わないファイルを黙って通さない**。足りないぶんを単位行列で
                // 埋めると、その関節に属する頂点だけが原点の周りに散らばる。
                if (inverseBind.Length < joints.Count)
                {
                    Console.WriteLine(
                        $"[glTF] skin {index}: 逆バインド行列が {inverseBind.Length} 個しかありません"
                        + $"(関節は {joints.Count} 本)。足りないぶんは単位行列にします");

                    Array.Resize(ref inverseBind, joints.Count);
                    for (int i = 0; i < joints.Count; i++)
                    {
                        if (inverseBind[i] == default)
                        {
                            inverseBind[i] = Matrix4x4.Identity;
                        }
                    }
                }

                result.Add(new Skin(
                    GetString(skin, "name", $"skin{index}"),
                    joints.ToArray(),
                    inverseBind,
                    GetInt(skin, "skeleton", -1)));

                index++;
            }

            _skins = result.ToArray();
        }

        /// <summary>
        /// <c>animations</c> を読む。**channel と sampler を組み立てるだけ**。
        ///
        /// 難しいところは無く、面倒なだけの処理。難所は
        /// 「どの accessor がどの型で入っているか」の場合分けで、
        /// そこは <see cref="ReadAnimationValues"/> に押し込んである。
        /// </summary>
        private void ReadAnimations()
        {
            if (!_root.TryGetProperty("animations", out JsonElement animations)
                || animations.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var clips = new List<AnimationClip>();
            int animationIndex = 0;

            foreach (JsonElement animation in animations.EnumerateArray())
            {
                JsonElement samplers = Get(animation, "samplers");

                // **同じ sampler を2つのチャンネルが指すことがある**ので、
                // 番号で覚えて使い回す。読み直すと時間の配列が二重に確保される。
                var built = new Dictionary<int, AnimationClip.Sampler>();
                var channels = new List<AnimationClip.Channel>();

                foreach (JsonElement channel in Get(animation, "channels").EnumerateArray())
                {
                    if (!channel.TryGetProperty("target", out JsonElement target))
                    {
                        continue;
                    }

                    // **node を持たないチャンネルは仕様上ありうる**(拡張が使う)。
                    // 動かす相手がいないので飛ばす。
                    int node = GetInt(target, "node", -1);
                    if (node < 0 || node >= _nodes.Length)
                    {
                        continue;
                    }

                    string path = GetString(target, "path", string.Empty);
                    AnimationPath which = path switch
                    {
                        "translation" => AnimationPath.Translation,
                        "rotation" => AnimationPath.Rotation,
                        "scale" => AnimationPath.Scale,
                        "weights" => AnimationPath.Weights,
                        _ => AnimationPath.Weights,
                    };

                    if (which == AnimationPath.Weights)
                    {
                        WarnOnce($"path '{path}' は未対応なので飛ばします(モーフターゲットは Day 41 の範囲外)");
                        continue;
                    }

                    int samplerIndex = GetInt(channel, "sampler", -1);
                    if (samplerIndex < 0 || samplerIndex >= samplers.GetArrayLength())
                    {
                        continue;
                    }

                    if (!built.TryGetValue(samplerIndex, out AnimationClip.Sampler? sampler))
                    {
                        sampler = ReadAnimationSampler(samplers[samplerIndex]);
                        built[samplerIndex] = sampler;
                    }

                    channels.Add(new AnimationClip.Channel(node, which, sampler));
                }

                if (channels.Count > 0)
                {
                    clips.Add(new AnimationClip(
                        GetString(animation, "name", $"anim{animationIndex}"), channels));
                }

                animationIndex++;
            }

            _animations = clips.ToArray();
        }

        /// <summary>sampler 1つ(時間の配列 + 値の配列 + 補間の種類)。</summary>
        private AnimationClip.Sampler ReadAnimationSampler(JsonElement sampler)
        {
            int input = GetInt(sampler, "input", -1);
            int output = GetInt(sampler, "output", -1);

            if (input < 0 || output < 0)
            {
                throw new InvalidDataException("animation sampler に input / output がありません");
            }

            string interpolation = GetString(sampler, "interpolation", "LINEAR");
            AnimationInterpolation mode = interpolation switch
            {
                "STEP" => AnimationInterpolation.Step,
                "CUBICSPLINE" => AnimationInterpolation.CubicSpline,
                "LINEAR" => AnimationInterpolation.Linear,
                _ => AnimationInterpolation.Linear,
            };

            if (mode == AnimationInterpolation.CubicSpline)
            {
                WarnOnce("CUBICSPLINE は接線を捨てて直線で結びます(改造課題2)");
            }
            else if (interpolation is not ("LINEAR" or "STEP"))
            {
                WarnOnce($"補間 '{interpolation}' は未知なので LINEAR として扱います");
            }

            float[] times = ReadScalarAccessor(input);
            Vector4[] values = ReadAnimationValues(output);

            // **個数の検算**。CUBICSPLINE ならキー1つにつき値3つ。
            // 合わないファイルをそのまま通すと、Key() の添字が配列をはみ出す。
            int perKey = mode == AnimationInterpolation.CubicSpline ? 3 : 1;
            int needed = times.Length * perKey;

            if (values.Length < needed)
            {
                Console.WriteLine(
                    $"[glTF] animation sampler: 値が {values.Length} 個しかありません"
                    + $"(キー {times.Length} 個 × {perKey})。足りるところまでで打ち切ります");

                Array.Resize(ref times, values.Length / perKey);
            }

            return new AnimationClip.Sampler(times, values, mode);
        }

        /// <summary>
        /// アニメーションの出力値を読む。**VEC3(移動・拡大)か VEC4(回転)**。
        /// どちらも <see cref="Vector4"/> に揃えて返す(VEC3 は W = 0)。
        /// </summary>
        private Vector4[] ReadAnimationValues(int index)
        {
            JsonElement accessor = Get(_root, "accessors")[index];
            string type = GetString(accessor, "type", "?");

            if (type == "VEC4")
            {
                // 回転は正規化された整数で入っていることがある(仕様が許している)。
                return ReadVector4Flexible(index, normalizeIntegers: true);
            }

            if (type != "VEC3")
            {
                throw new InvalidDataException(
                    $"accessor {index}: アニメーションの値が VEC3 でも VEC4 でもありません({type})");
            }

            Vector3[] source = ReadVector3Accessor(index);
            var result = new Vector4[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                result[i] = new Vector4(source[i], 0.0f);
            }

            return result;
        }

        /// <summary>同じ知らせを何度も出さない。**チャンネルの数だけ出ると読めなくなる**。</summary>
        private void WarnOnce(string message)
        {
            if (_warned.Add(message))
            {
                Console.WriteLine($"[glTF] {message}");
            }
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
        /// float 1個ずつの並び(SCALAR)。**アニメーションの時間軸**用(Day 41)。
        /// </summary>
        private float[] ReadScalarAccessor(int index)
        {
            JsonElement accessor = Get(_root, "accessors")[index];
            RequireType(accessor, "SCALAR", index);
            RequireComponent(accessor, ComponentFloat, index);

            int count = GetInt(accessor, "count", 0);
            var result = new float[count];
            ReadFloats(accessor, count, 1, result.AsSpan());
            return result;
        }

        /// <summary>
        /// 4x4 行列の並び(MAT4)。**逆バインド行列**用(Day 41)。
        ///
        /// <b>転置は要らない</b>。glTF は列優先で 16 個並べ、
        /// <see cref="Matrix4x4"/> は行優先で 16 個並ぶので、
        /// バイト列をそのまま流し込むと**転置された形で入る**——
        /// そしてそれが、行ベクトル規約の C# 側で欲しい形そのもの。
        /// <c>ReadNodeTransform</c> の <c>matrix</c> と同じ話(Day 32)。
        /// </summary>
        private Matrix4x4[] ReadMatrix4Accessor(int index)
        {
            JsonElement accessor = Get(_root, "accessors")[index];
            RequireType(accessor, "MAT4", index);
            RequireComponent(accessor, ComponentFloat, index);

            int count = GetInt(accessor, "count", 0);
            var result = new Matrix4x4[count];
            ReadFloats(accessor, count, 16, result.AsSpan());
            return result;
        }

        /// <summary>
        /// VEC4 を**成分の型を問わず**読む(Day 41)。JOINTS_0 / WEIGHTS_0 / 回転の値用。
        ///
        /// <para>
        /// <see cref="ReadVector4Accessor"/> は float 決め打ちだが、こちらは
        /// byte / short / float のどれでも受ける。glTF が型を選ばせているのは
        /// **意味によって必要な精度が違う**から。
        ///   - JOINTS_0 … 関節の番号。整数。255 本を超えるなら short
        ///   - WEIGHTS_0 … 0〜1 の重み。byte で 1/255 刻みでも十分足りる
        /// </para>
        ///
        /// <para>
        /// <paramref name="normalizeIntegers"/> が **整数を 0〜1 に読み替えるかどうか**。
        /// 重みは読み替える(255 → 1.0)、関節の番号は読み替えない(3 は 3 のまま)。
        /// **ここを取り違えると、関節の番号が全部 0 になって
        /// モデルが1本の骨にぶら下がる**という派手な壊れ方をする。
        /// </para>
        /// </summary>
        private Vector4[] ReadVector4Flexible(int index, bool normalizeIntegers)
        {
            JsonElement accessor = Get(_root, "accessors")[index];
            RequireType(accessor, "VEC4", index);

            int componentType = GetInt(accessor, "componentType", 0);
            int count = GetInt(accessor, "count", 0);

            if (componentType == ComponentFloat)
            {
                var floats = new Vector4[count];
                ReadFloats(accessor, count, 4, floats.AsSpan());
                return floats;
            }

            (int size, float scale) = componentType switch
            {
                ComponentUnsignedByte => (1, 1.0f / 255.0f),
                ComponentByte => (1, 1.0f / 127.0f),
                ComponentUnsignedShort => (2, 1.0f / 65535.0f),
                ComponentShort => (2, 1.0f / 32767.0f),
                _ => throw new InvalidDataException(
                    $"accessor {index}: componentType {componentType} は VEC4 として未対応です"),
            };

            (byte[] buffer, int start, int stride) = Locate(accessor, size * 4);
            var result = new Vector4[count];

            for (int i = 0; i < count; i++)
            {
                int offset = start + (i * stride);
                result[i] = new Vector4(
                    Component(offset),
                    Component(offset + size),
                    Component(offset + (size * 2)),
                    Component(offset + (size * 3)));
            }

            return result;

            float Component(int at)
            {
                float raw = componentType switch
                {
                    ComponentUnsignedByte => buffer[at],
                    ComponentByte => (sbyte)buffer[at],
                    ComponentUnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at)),
                    _ => BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(at)),
                };

                // 符号付きの正規化は -1 で止める決まり(-128/127 が -1.0078 になるため)。
                return normalizeIntegers ? MathF.Max(raw * scale, -1.0f) : raw;
            }
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

            foreach (string name in (string[])
                ["nodes", "meshes", "skins", "animations", "materials", "textures", "images", "accessors"])
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
