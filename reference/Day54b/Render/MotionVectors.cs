using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace HonyaEngine;

/// <summary>
/// **速度バッファ**(Day 54)。「この画素は、前のフレームで画面のどこに写っていたか」を画素ごとに持つ。
///
/// <para>
/// TAA は前のフレームの結果(履歴)を読むが、カメラも物も動くので、<b>同じ画素の場所</b>を読むと別の物の色を拾う。
/// 読むべきは「この画素に今写っている点が、前のフレームで写っていた場所」で、その差がこのバッファの中身になる。
/// 単位は UV(画面の幅・高さを 1)、向きは「前 → 今」。形式は RG16F(<see cref="RenderTargetFormat.Rg16F"/>)。
/// </para>
///
/// <para>
/// 中身は<b>2段で描く</b>。
/// </para>
/// <list type="number">
/// <item><b>カメラ</b>(全画面1枚。<c>camera-velocity.frag</c>)… 深度から世界の点を戻し、前のフレームの行列で写し直す。
/// 止まっている物は全部これで正しい。シーンの中身を何も知らなくてよい</item>
/// <item><b>動いた物</b>(<c>velocity.vert</c>)… 前のフレームのモデル行列・関節行列で「前の位置」を出し、上から描き直す。
/// 深度テスト(LEQUAL)をシーンの深度に対して掛けるので、手前に何かあれば描かれない</item>
/// </list>
///
/// <para>
/// <b>2段目の「何が動いたか」を、Program の描画の分岐を1つも書き写さずに知る</b>のが、このクラスの工夫。
/// 本描画はどの絵でも <c>Program.Draw</c> を通るので、そこで「メッシュと行列」を記録しておき(<see cref="Record"/>)、
/// 前のフレームの記録と見比べて、<b>行列か関節が変わったものだけ</b>を2段目で描き直す。
/// 影パス・SSAO の幾何パスは描くものを手で書き写していて、Day 51 でキャラクターを足したときに3か所を直すことになった
/// (Day 52 の <c>RenderGBufferPass</c> のコメント)。同じことを4か所目で繰り返さないための形。
/// </para>
///
/// <para>
/// <b>記録の鍵は「メッシュと、そのフレームで何回目か」</b>。同じ立方体のメッシュを 5 回描けば (立方体, 0)〜(立方体, 4)。
/// 描く順番が毎フレーム同じなら、同じ鍵が同じ物を指す。順番が変わる(途中に1つ増える)と、
/// その後ろの鍵が1つずつずれて、<b>別の物の行列と比べてしまう</b>——計画書の「今日残した歪み」。
/// 本物のエンジンは物ごとに消えない番号(ID)を持っていて、前のフレームの行列もその物自身が覚えている。
/// </para>
/// </summary>
internal sealed class MotionVectors : IDisposable
{
    /// <summary>
    /// 1フレームでカメラがこれ以上動いたら、前のフレームと<b>繋がっていない</b>(カット)とみなす [m]。
    /// 走るキャラクターを追うカメラでも 1フレーム 0.1m 程度なので、2m は瞬間移動と言ってよい。
    /// </summary>
    public const float CutDistance = 2.0f;

    private readonly GL _gl;
    private readonly RenderResources _resources;
    private readonly Handle<Shader> _cameraShader;
    private readonly Handle<Shader> _objectShader;
    private readonly GpuTimer _timer;

    /// <summary>
    /// 速度(RG16F)+ 深度。**要るまで作らない**(TAA を入れたときに初めて作る)。
    ///
    /// <para>
    /// 深度を自前で持つのは、2段目の深度テストのため。シーンの深度をここへ写してから(<see cref="Framebuffer.BlitDepthTo"/>)、
    /// それと比べて描く。1段目が読む深度(<c>uDepth</c>)は別のコピー(<see cref="PostProcess.SceneDepth"/>)を使う——
    /// 描き込み先に挿さっている深度を同時にテクスチャとして読むと、フィードバックループになる(Day 50 と同じ話)。
    /// </para>
    /// </summary>
    private Framebuffer? _target;

    /// <summary>描いたもの1つぶんの記録。関節は別の並びに詰めて、ここには始まりと数だけを持つ。</summary>
    private readonly record struct Entry(Matrix4x4 Model, int JointStart, int JointCount);

    /// <summary>前のフレームから動いたもの。2段目で描き直す。</summary>
    private readonly record struct Moved(Mesh<Vertex> Mesh, Entry Current, Entry Previous);

    /// <summary>このフレームの記録。鍵は (メッシュ, そのフレームで何回目か)。</summary>
    private Dictionary<(Mesh<Vertex> Mesh, int Occurrence), Entry> _current = [];

    /// <summary>前のフレームの記録。<see cref="BeginFrame"/> で <see cref="_current"/> と入れ替える。</summary>
    private Dictionary<(Mesh<Vertex> Mesh, int Occurrence), Entry> _previous = [];

    /// <summary>
    /// 関節行列を詰めた並び(このフレーム・前のフレーム)。**毎フレーム作り直さない**——
    /// <c>Clear</c> は中身を空にするだけで器は残るので、2フレーム目からは確保が1回も起きない。
    /// </summary>
    private List<Matrix4x4> _currentJoints = [];
    private List<Matrix4x4> _previousJoints = [];

    private readonly Dictionary<Mesh<Vertex>, int> _occurrences = [];
    private readonly List<Moved> _moved = [];

    private Matrix4x4 _previousViewProjection;
    private Vector3 _previousCameraPosition;
    private bool _hasPrevious;
    private bool _renderedLastFrame;

    private uint _emptyVao;
    private bool _disposed;

    public MotionVectors(GL gl, RenderResources resources, string shaderDirectory)
    {
        _gl = gl;
        _resources = resources;

        _cameraShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "fullscreen.vert"),
            Path.Combine(shaderDirectory, "camera-velocity.frag"));

        _objectShader = resources.LoadShader(
            Path.Combine(shaderDirectory, "velocity.vert"),
            Path.Combine(shaderDirectory, "velocity.frag"));

        _emptyVao = gl.GenVertexArray();
        _timer = new GpuTimer(gl);
    }

    /// <summary>
    /// 自分で動いた物の速度を描くか(「TAA」の F7)。**OFF にするとカメラの動きだけ**(1段目だけ)。
    ///
    /// <para>
    /// OFF にして走るキャラクターを追うと、キャラクターの画素は「止まっている物」として扱われ、
    /// 履歴を背景の側から読む。疑い(<see cref="TemporalAA.Rectify"/>)が色を引き戻すので残像は出ないが、
    /// 履歴が溜まらないので、キャラクターの輪郭が階段に戻り、ずらしの揺れがそのまま見える。
    /// </para>
    /// </summary>
    public bool ObjectMotion { get; set; } = true;

    /// <summary>いま記録しているか。<see cref="BeginFrame"/> で始まり、<see cref="Render"/> で終わる。</summary>
    public bool Recording { get; private set; }

    /// <summary>このフレームに記録した数(本描画で描いた数)。</summary>
    public int RecordedCount { get; private set; }

    /// <summary>このフレームに記録したうち、前のフレームに居なかったもの。</summary>
    public int NewCount { get; private set; }

    /// <summary>前のフレームから動いたもの(2段目の候補)。</summary>
    public int MovedCount => _moved.Count;

    /// <summary>直前の <see cref="Render"/> で2段目に描き直した数。</summary>
    public int DrawnCount { get; private set; }

    /// <summary>
    /// **前のフレームと繋がっていない**。カメラが瞬間移動した、別の絵に切り替わった、TAA を入れた直後。
    /// Program はこれを見て TAA の履歴を捨てる(<see cref="TemporalAA.Reset"/>)。
    /// </summary>
    public bool Cut { get; private set; }

    /// <summary>速度(RG16F)。まだ一度も描いていなければ null。</summary>
    public Texture? Velocity => _target?.Color;

    /// <summary>速度 + 深度の入れ物。自己チェックが読み返すために公開する。</summary>
    public Framebuffer? Target => _target;

    /// <summary>2段ぶんの GPU の時間(ms。<see cref="GpuTimer"/>)。</summary>
    public double GpuMilliseconds => _timer.Milliseconds;

    /// <summary>速度 + 深度の VRAM。作る前は 0。</summary>
    public long ByteSize => _target?.ByteSize ?? 0;

    /// <summary>
    /// **フレームの頭で呼ぶ**。前のフレームの記録を「前」へ回し、このフレームの記録を始める。
    /// </summary>
    /// <param name="record">
    /// 記録するか。TAA を切っているときは false——記録は辞書への書き込みなので、
    /// 使わないのに毎フレーム払う理由が無い。
    /// </param>
    public void BeginFrame(bool record)
    {
        // **前のフレームで速度を描かなかった**(TAA を切っていた・ゲーム中だった)なら、
        // 手元の「前のフレームの行列」は何フレームも前のもの。繋がっていないとみなす。
        if (!_renderedLastFrame)
        {
            _hasPrevious = false;
        }

        _renderedLastFrame = false;

        (_current, _previous) = (_previous, _current);
        (_currentJoints, _previousJoints) = (_previousJoints, _currentJoints);

        _current.Clear();
        _currentJoints.Clear();
        _occurrences.Clear();
        _moved.Clear();

        RecordedCount = 0;
        NewCount = 0;
        Recording = record;
    }

    /// <summary>
    /// **本描画で描いたものを1つ覚える**。<c>Program.Draw</c> が毎回呼ぶ。記録中でなければ何もしない。
    ///
    /// <para>
    /// 前のフレームに同じ鍵の記録があって、行列か関節が<b>1ビットでも</b>違えば「動いた」。
    /// 止まっている物は毎フレーム同じ計算で同じ行列が出るので、ぴったり一致する。
    /// 前のフレームに記録が無ければ「新しく出てきた」——前の位置が分からないので、1段目(カメラの動き)に任せる。
    /// </para>
    /// </summary>
    public void Record(Mesh<Vertex> mesh, Matrix4x4 model, ReadOnlySpan<Matrix4x4> joints)
    {
        if (!Recording)
        {
            return;
        }

        int occurrence = _occurrences.GetValueOrDefault(mesh);
        _occurrences[mesh] = occurrence + 1;

        var entry = new Entry(model, _currentJoints.Count, joints.Length);
        _currentJoints.AddRange(joints);

        (Mesh<Vertex>, int) key = (mesh, occurrence);
        _current[key] = entry;
        RecordedCount++;

        if (!_previous.TryGetValue(key, out Entry previous) || previous.JointCount != joints.Length)
        {
            NewCount++;
            return;
        }

        bool moved = previous.Model != model || !JointsOf(_previousJoints, previous).SequenceEqual(joints);

        if (moved)
        {
            _moved.Add(new Moved(mesh, entry, previous));
        }
    }

    /// <summary>
    /// **速度を描く**。本描画が全部終わったあと(<c>_post.End</c> の直前)に1回。
    /// </summary>
    /// <param name="camera">このフレームのカメラ(ずらした状態のまま渡す)。</param>
    /// <param name="scene">シーンのバッファ。深度を写す元。</param>
    /// <param name="sceneDepth">シーンの深度のコピー(<see cref="PostProcess.CaptureDepth"/> 済みのもの)。1段目が読む。</param>
    public void Render(Camera camera, Framebuffer scene, Texture sceneDepth)
    {
        Recording = false;

        if (_target is null)
        {
            _target = new Framebuffer(_gl, scene.Width, scene.Height, RenderTargetFormat.Rg16F, depth: true);
        }
        else if (_target.Width != scene.Width || _target.Height != scene.Height)
        {
            _target.Resize(scene.Width, scene.Height);
            _hasPrevious = false;
        }

        Matrix4x4 current = camera.UnjitteredViewProjection;

        // **前のフレームと繋がっているか**。3つのどれかなら繋がっていない。
        //   - 前のフレームの行列が無い(TAA を入れた直後など)
        //   - カメラが瞬間移動した
        //   - 描いたものの半分以上が、前のフレームに居なかった(別の絵に切り替わった)
        bool cameraJumped = _hasPrevious
            && Vector3.Distance(camera.Position, _previousCameraPosition) > CutDistance;
        bool sceneChanged = RecordedCount > 0 && NewCount * 2 > RecordedCount;
        Cut = !_hasPrevious || cameraJumped || sceneChanged;

        // 繋がっていないなら「動いていない」ことにする。どうせ履歴は捨てられるので、値は何でもよいが、
        // 意味の無い大きな速度を書いておくと、表示(速度)で画面が一瞬真っ赤になる。
        Matrix4x4 previous = Cut ? current : _previousViewProjection;

        _timer.Begin();

        // 2段目の深度テストの相手。シーンの深度をそのまま写す。
        scene.BlitDepthTo(_target);
        _target.Bind();

        bool depthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        bool cull = _gl.IsEnabled(EnableCap.CullFace);
        bool offset = _gl.IsEnabled(EnableCap.PolygonOffsetFill);
        _gl.GetInteger(GetPName.DepthFunc, out int depthFunc);
        _gl.GetBoolean(GetPName.DepthWritemask, out bool depthMask);

        // GL_POLYGON_MODE は int を2個返す(PostProcess.EndCore と同じ用心)。
        Span<int> polygonModes = stackalloc int[2];
        _gl.GetInteger(GetPName.PolygonMode, polygonModes);

        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        RenderCameraMotion(camera, previous, sceneDepth);

        DrawnCount = 0;

        if (ObjectMotion && !Cut && _moved.Count > 0)
        {
            RenderObjectMotion(camera, current, previous);
        }

        SetCap(EnableCap.DepthTest, depthTest);
        SetCap(EnableCap.Blend, blend);
        SetCap(EnableCap.CullFace, cull);
        SetCap(EnableCap.PolygonOffsetFill, offset);
        _gl.DepthFunc((DepthFunction)depthFunc);
        _gl.DepthMask(depthMask);
        _gl.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)polygonModes[0]);

        _timer.End();

        _previousViewProjection = current;
        _previousCameraPosition = camera.Position;
        _hasPrevious = true;
        _renderedLastFrame = true;
    }

    /// <summary>
    /// 前のフレームとの繋がりを切る。画面の大きさが変わったとき(縦横比が変わると、前の行列で写し直した位置が意味を失う)。
    /// </summary>
    public void Invalidate() => _hasPrevious = false;

    /// <summary>
    /// **1段目: カメラの動き**。全画面1枚。深度テストは使わない(全部の画素に書く)。
    ///
    /// <para>
    /// 2つの行列を CPU で1本にしてから渡す。
    /// <c>ずらした VP の逆</c> で今の画面の点を世界へ戻し、<c>前の VP</c> で前のフレームの画面へ写す。
    /// 逆行列は<b>ずらした</b>ほうを使う——深度はずらして描いたものなので、同じずらしで戻さないと半画素ずれた点を戻す。
    /// </para>
    /// </summary>
    private void RenderCameraMotion(Camera camera, Matrix4x4 previous, Texture sceneDepth)
    {
        _gl.Disable(EnableCap.DepthTest);

        Matrix4x4.Invert(camera.ViewProjection, out Matrix4x4 inverse);

        Shader shader = _resources.GetShader(_cameraShader);
        shader.Use();
        shader.SetMatrix4("uReprojection", inverse * previous);
        shader.SetVector2("uJitter", camera.Jitter);

        sceneDepth.Bind(TextureUnit.Texture0);
        shader.SetInt("uDepth", 0);

        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// **2段目: 動いた物**。前のフレームから行列か関節が変わったものだけを描き直す。
    ///
    /// <para>
    /// 深度テストは <b>LEQUAL</b>、深度は書かない。シーンの深度と「同じ深さ」の画素だけが通る——
    /// つまり、その物が実際に見えている画素だけに速度を書く。手前に柱があれば柱の画素は1段目のまま。
    /// </para>
    ///
    /// <para>
    /// <b>ポリゴンオフセットで少しだけ手前に寄せる</b>。本描画(textured.vert)とこちら(velocity.vert)は別のシェーダなので、
    /// 同じ式を書いても深度が最後の1ビットまで一致する保証は無い(GLSL の <c>invariant</c> を両方に付ければ保証される)。
    /// 1ビット奥に出れば LEQUAL で落ちて、動く物に<b>点々と穴が開く</b>。寄せておけば、その心配が無い。
    /// </para>
    ///
    /// <para>
    /// 裏面は捨てない。材質ごとの両面の設定(<see cref="Material.DoubleSided"/>)を知らずに済むし、
    /// 閉じた物の裏面は表面より奥なので、深度テストで勝手に落ちる。
    /// </para>
    /// </summary>
    private void RenderObjectMotion(Camera camera, Matrix4x4 current, Matrix4x4 previous)
    {
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1.0f, -1.0f);

        Shader shader = _resources.GetShader(_objectShader);
        shader.Use();
        shader.SetMatrix4("uViewProjection", camera.ViewProjection);
        shader.SetMatrix4("uCurrentViewProjection", current);
        shader.SetMatrix4("uPreviousViewProjection", previous);

        foreach (Moved moved in _moved)
        {
            shader.SetMatrix4("uModel", moved.Current.Model);
            shader.SetMatrix4("uPreviousModel", moved.Previous.Model);

            bool skinned = moved.Current.JointCount > 0;
            shader.SetInt("uSkinned", skinned ? 1 : 0);

            if (skinned)
            {
                shader.SetMatrix4Array("uJoints", JointsOf(_currentJoints, moved.Current));
                shader.SetMatrix4Array("uPreviousJoints", JointsOf(_previousJoints, moved.Previous));
            }

            moved.Mesh.Draw();
            DrawnCount++;
        }
    }

    private static ReadOnlySpan<Matrix4x4> JointsOf(List<Matrix4x4> joints, Entry entry) =>
        CollectionsMarshal.AsSpan(joints).Slice(entry.JointStart, entry.JointCount);

    private void SetCap(EnableCap cap, bool enabled)
    {
        if (enabled)
        {
            _gl.Enable(cap);
        }
        else
        {
            _gl.Disable(cap);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _target?.Dispose();
        _timer.Dispose();

        _gl.DeleteVertexArray(_emptyVao);
        _emptyVao = 0;

        // シェーダは RenderResources が持っているので、ここでは捨てない。
    }
}
