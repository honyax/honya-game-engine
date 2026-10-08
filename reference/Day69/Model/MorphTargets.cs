using System.Diagnostics;
using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **1つのメッシュのモーフターゲット**(Day 69)。ブレンドシェイプ・シェイプキーとも呼ぶ。
///
/// <para>
/// モーフターゲットは「形を丸ごと何通りも持つ」のではなく、<b>元の形からの差分</b>を何本も持つ。
/// 1本が「目を閉じたら各頂点がどれだけ動くか」で、重み w を掛けて足す。
/// <code>
///   位置 = 元の位置 + Σ w[k] x 差分[k]          法線も同じ形で足す
/// </code>
/// 足し算なので、半分閉じた目(w = 0.5)も、笑いながら「あ」の口(2本を同時に 1)も、同じ式で作れる。
/// 骨では作りにくい「頬が上がる」「唇がすぼまる」のような、面のゆがみそのものを動かすのに使う。
/// </para>
///
/// <para>
/// <b>重みはメッシュ単位</b>。glTF ではメッシュの全プリミティブが同じ数のターゲットを持ち、同じ重みで動く
/// (VRoid の顔は 7 枚のプリミティブ・57 本のターゲット)。だから重みの並びはこのクラスに1つだけ置く。
/// </para>
///
/// <para>
/// <b>CPU で合成して頂点バッファを書き換える</b>のが今日のやり方。GPU でやる道(差分をテクスチャに詰めて頂点シェーダで足す)は
/// 速いが、シェーダ・スキニング・影パス・速度バッファの全部に手が入る。CPU なら <b>Mesh の中身が変わるだけ</b>で、
/// 描く側(MToon のパス・影パス・速度バッファ)は1行も変えずに済む。
/// </para>
/// </summary>
internal sealed class MorphTargets
{
    /// <summary>
    /// **ターゲット1本**。差分は<b>動く頂点だけ</b>を持つ(疎な持ち方)。
    ///
    /// <para>
    /// ファイルの中の差分は頂点の数だけ並んでいて、ほとんどが 0 になっている。まばたき(Fcl_EYE_Close)が動かすのは
    /// 顔の 4,060 頂点のうち 1,066 個だけ。0 を足す計算を省くために、読むときに 0 でない頂点の番号と差分だけを残す。
    /// 57 本ぶんを全部持つと 23 万個、残すのは 4 万 5 千個(19%)になる。
    /// </para>
    /// </summary>
    internal sealed class Target
    {
        public Target(string name, int[] indices, Vector3[] positions, Vector3[] normals)
        {
            Name = name;
            Indices = indices;
            Positions = positions;
            Normals = normals;
        }

        /// <summary>名前(<c>extras.targetNames</c>)。VRoid なら Fcl_EYE_Close のような名前。</summary>
        public string Name { get; }

        /// <summary>動く頂点の番号。</summary>
        public int[] Indices { get; }

        /// <summary>位置の差分(<see cref="Indices"/> と同じ並び)。</summary>
        public Vector3[] Positions { get; }

        /// <summary>法線の差分。持たないファイルでは全部 0。</summary>
        public Vector3[] Normals { get; }

        /// <summary>
        /// ファイルの差分(頂点の数だけ並んだもの)から、0 でない頂点だけを抜き出す。
        /// </summary>
        public static Target FromDense(string name, Vector3[] positions, Vector3[]? normals)
        {
            var indices = new List<int>();
            for (int i = 0; i < positions.Length; i++)
            {
                if (positions[i] != Vector3.Zero || (normals is not null && normals[i] != Vector3.Zero))
                {
                    indices.Add(i);
                }
            }

            int[] picked = indices.ToArray();
            return new Target(
                name,
                picked,
                picked.Select(i => positions[i]).ToArray(),
                picked.Select(i => normals is not null ? normals[i] : Vector3.Zero).ToArray());
        }
    }

    /// <summary>
    /// **頂点の並び1つぶん**。同じ頂点の並び(同じ POSITION のアクセサ)を指すプリミティブは、合成を1回で済ませ、
    /// できた頂点を全員の Mesh へ送る。VRoid の顔は 7 枚のプリミティブが同じ並びを共有しているので、合成は1回・送るのは7回になる
    /// (送る回数が減らないのは、ローダーがプリミティブごとに頂点バッファを作っているから。Day 68 の「今日残した歪み」の4つ目)。
    /// </summary>
    internal sealed class Block
    {
        public Block(string key, Vertex[] baseVertices, Target[] targets)
        {
            Key = key;
            Base = baseVertices;
            Work = (Vertex[])baseVertices.Clone();
            Targets = targets;
        }

        /// <summary>見分けるための鍵。POSITION・NORMAL と全ターゲットのアクセサ番号を並べたもの。</summary>
        public string Key { get; }

        /// <summary>重みが全部 0 のときの頂点(ファイルの形)。合成のたびにここから始める。</summary>
        public Vertex[] Base { get; }

        /// <summary>合成した結果を置く場所。毎回 new しないよう使い回す。</summary>
        public Vertex[] Work { get; }

        public Target[] Targets { get; }

        /// <summary>この並びを描く Mesh(プリミティブの数だけ)。</summary>
        public List<Mesh<Vertex>> Meshes { get; } = [];
    }

    private readonly List<Block> _blocks = [];

    /// <summary>最後に送ったときの重み。同じなら送らない。</summary>
    private readonly float[] _applied;

    public MorphTargets(int nodeIndex, string name, string[] targetNames, float[] weights)
    {
        NodeIndex = nodeIndex;
        Name = name;
        TargetNames = targetNames;
        Weights = weights;

        // 頂点はファイルの形(重み 0)で作ってある。既定の重み(mesh.weights)が 0 でなければ、最初の Apply で送られる。
        _applied = new float[weights.Length];
    }

    /// <summary>このモーフを持つノード。VRM の表情はノード番号とターゲットの番号で名指ししてくる。</summary>
    public int NodeIndex { get; }

    /// <summary>ノードの名前(VRoid なら Face)。</summary>
    public string Name { get; }

    public IReadOnlyList<string> TargetNames { get; }

    /// <summary>
    /// **重み**。外(表情)が書き、<see cref="Apply"/> が読む。ターゲットの数だけ並ぶ。
    /// 0〜1 に切り詰めない——仕様が切り詰めを決めていないうえ、2つの表情が同じターゲットを足すと 1 を超えうる。
    /// </summary>
    public float[] Weights { get; }

    public int TargetCount => Weights.Length;

    public IReadOnlyList<Block> Blocks => _blocks;

    /// <summary>このモーフが書き換える Mesh の数(VRoid の顔は 7)。</summary>
    public int MeshCount => _blocks.Sum(block => block.Meshes.Count);

    /// <summary>頂点を送った回数(<see cref="Apply"/> が実際に合成した回数)。内訳用。</summary>
    public int UploadCount { get; private set; }

    /// <summary>最後に合成にかかった時間(ミリ秒)。送る時間も含む。内訳用。</summary>
    public double LastApplyMilliseconds { get; private set; }

    /// <summary>
    /// 同じ頂点の並びのブロックを探す(ローダーが2つ目のプリミティブを足すとき)。
    /// </summary>
    public Block? FindBlock(string key) => _blocks.FirstOrDefault(block => block.Key == key);

    public Block AddBlock(string key, Vertex[] baseVertices, Target[] targets)
    {
        if (targets.Length != Weights.Length)
        {
            throw new InvalidDataException(
                $"{Name}: プリミティブのターゲットの数 {targets.Length} がメッシュの重みの数 {Weights.Length} と違います");
        }

        var block = new Block(key, baseVertices, targets);
        _blocks.Add(block);
        return block;
    }

    /// <summary>
    /// **合成して GPU へ送る**。重みが前回と同じなら何もしない(false)。
    ///
    /// <para>
    /// 毎フレーム呼んでよい。表情が止まっている間(まばたきの合間がほとんど)は比べるだけで返るので、
    /// 頂点を送るのは重みが動いているフレームだけになる。
    /// </para>
    ///
    /// <para>
    /// <b>スキニングの前の形を作る</b>。glTF の決まりで、モーフはバインドポーズの空間(関節行列を掛ける前)で足す。
    /// ここで書き換えるのはその形で、関節行列は GPU がこれまでどおり後から掛ける。
    /// 順番を逆にすると(曲げた後に差分を足すと)、腕を上げたときに差分の向きが体と一緒に回らない。
    /// </para>
    /// </summary>
    public bool Apply()
    {
        if (Weights.AsSpan().SequenceEqual(_applied))
        {
            return false;
        }

        long start = Stopwatch.GetTimestamp();

        foreach (Block block in _blocks)
        {
            Blend(block.Base, block.Targets, Weights, block.Work);

            foreach (Mesh<Vertex> mesh in block.Meshes)
            {
                mesh.UpdateVertices(block.Work);
            }
        }

        Weights.CopyTo(_applied, 0);
        UploadCount++;
        LastApplyMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return true;
    }

    /// <summary>
    /// **合成の式そのもの**。元の頂点を写してから、重みが 0 でないターゲットの差分だけを足す。
    /// GL を使わないので、自己チェックが窓を開かずに呼べる。
    ///
    /// <para>
    /// 法線も同じ重みで足し、<b>長さは直さない</b>。シェーダがスキニングの後で正規化するので、ここで直しても二度手間になる。
    /// 接線は足さない(ファイルが接線の差分を持っていないうえ、MToon の法線マップは顔に貼られていない)。
    /// </para>
    /// </summary>
    public static void Blend(Vertex[] baseVertices, Target[] targets, ReadOnlySpan<float> weights, Vertex[] result)
    {
        baseVertices.AsSpan().CopyTo(result);

        for (int t = 0; t < targets.Length; t++)
        {
            float weight = weights[t];
            if (weight == 0.0f)
            {
                continue;
            }

            Target target = targets[t];
            for (int k = 0; k < target.Indices.Length; k++)
            {
                int i = target.Indices[k];
                result[i].Position += target.Positions[k] * weight;
                result[i].Normal += target.Normals[k] * weight;
            }
        }
    }

    /// <summary>名前でターゲットの番号を引く。無ければ -1。</summary>
    public int IndexOf(string targetName)
    {
        for (int i = 0; i < TargetNames.Count; i++)
        {
            if (TargetNames[i] == targetName)
            {
                return i;
            }
        }

        return -1;
    }
}
