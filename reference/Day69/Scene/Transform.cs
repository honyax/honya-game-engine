using System.Numerics;

namespace HonyaEngine;

/// <summary>
/// **位置・回転・スケール、そして親子関係**。すべての <see cref="GameObject"/> が1つ必ず持つ。
///
/// Transform だけが特別扱い(<c>AddComponent</c> ではなく最初から生えている)なのは、
/// 「そこに無いオブジェクト」というものが考えにくいから。
/// Unity も Unreal もここは同じ作りになっている。
///
/// **親子関係が Transform にある**のがこの設計の肝。
/// 砲塔を戦車に、剣を手に、UI を親パネルに乗せると、
/// 親を動かすだけで子がついてくる。
/// 「ついてくる」の実体は行列の掛け算1回で、
///
///   ワールド行列 = 自分のローカル行列 * 親のワールド行列
///
/// を根まで再帰するだけ。Day 16 でモデル行列を1個ずつ手で組んでいたものが、
/// 木構造になっただけとも言える。
///
/// 掛ける順番が「ローカル * 親」なのは <c>System.Numerics</c> が**行ベクトル規約**
/// (<c>v' = v * M</c>)だから。Day 16 で見たのと同じ約束で、
/// 「先に適用したいものを左に置く」。
/// </summary>
internal sealed class Transform
{
    private readonly List<Transform> _children = [];

    private Vector3 _localPosition;
    private Quaternion _localRotation = Quaternion.Identity;
    private Vector3 _localScale = Vector3.One;

    // 補間用に、前ステップの値を持つ(Day 19 要点3)。
    // **エンジンが Transform を握っているので、補間もエンジンが面倒を見られる**。
    // Day 21 まではスプライトの構造体に PreviousPosition を自分で足していた。
    private Vector3 _previousLocalPosition;
    private Quaternion _previousLocalRotation = Quaternion.Identity;

    private Transform? _parent;
    private Matrix4x4 _localToWorld = Matrix4x4.Identity;
    private bool _dirty = true;

    public Transform(GameObject gameObject)
    {
        GameObject = gameObject;
    }

    public GameObject GameObject { get; }

    public Vector3 LocalPosition
    {
        get => _localPosition;
        set
        {
            _localPosition = value;
            MarkDirty();
        }
    }

    public Quaternion LocalRotation
    {
        get => _localRotation;
        set
        {
            _localRotation = value;
            MarkDirty();
        }
    }

    public Vector3 LocalScale
    {
        get => _localScale;
        set
        {
            _localScale = value;
            MarkDirty();
        }
    }

    public Transform? Parent => _parent;

    public IReadOnlyList<Transform> Children => _children;

    /// <summary>
    /// ワールド行列。**必要になったときだけ計算する**(遅延評価)。
    ///
    /// 毎フレーム全員ぶん計算し直してもよいが、
    /// 動かないオブジェクトのほうが多いのが普通なので、
    /// 「変わったものだけ」に絞ったほうが効く。
    /// その代わり、値を書き換えたときに <see cref="MarkDirty"/> で
    /// **子孫まで無効にして回る**必要がある。
    /// </summary>
    public Matrix4x4 LocalToWorld
    {
        get
        {
            if (_dirty)
            {
                Matrix4x4 local =
                    Matrix4x4.CreateScale(_localScale)
                    * Matrix4x4.CreateFromQuaternion(_localRotation)
                    * Matrix4x4.CreateTranslation(_localPosition);

                _localToWorld = _parent is null ? local : local * _parent.LocalToWorld;
                _dirty = false;
            }

            return _localToWorld;
        }
    }

    public Vector3 WorldPosition => LocalToWorld.Translation;

    /// <summary>
    /// 親を付け替える。<c>null</c> を渡すと根に戻る。
    ///
    /// **ワールド位置は保たない**(付け替えるとその場で動く)。
    /// Unity の <c>SetParent(parent, worldPositionStays: true)</c> のような
    /// 「見た目を保ったまま」の付け替えは、
    /// 親のワールド行列の逆行列を掛けてローカル値を作り直すことになる。
    /// 今日は使わないので入れていない。
    /// </summary>
    public void SetParent(Transform? parent)
    {
        if (ReferenceEquals(parent, _parent))
        {
            return;
        }

        // **自分の子孫を親にすると輪ができる**。
        // 輪ができるとワールド行列の計算が無限再帰して落ちるので、ここで弾く。
        for (Transform? ancestor = parent; ancestor is not null; ancestor = ancestor._parent)
        {
            if (ReferenceEquals(ancestor, this))
            {
                throw new InvalidOperationException($"{GameObject.Name} を自分の子孫の下に置こうとしています");
            }
        }

        _parent?._children.Remove(this);
        _parent = parent;
        _parent?._children.Add(this);

        MarkDirty();
    }

    /// <summary>Z 軸まわりの回転を設定する。2D では回転といえばこれ。</summary>
    public void SetLocalRotationZ(float radians) =>
        LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians);

    /// <summary>
    /// 現在値を「前ステップの値」として控える。**ステップの頭で <see cref="Scene"/> が呼ぶ**。
    ///
    /// これを各コンポーネントの責任にすると、必ずどれかが忘れて
    /// そのオブジェクトだけカクつく。**忘れようのない場所に置く**のが設計の要点。
    /// </summary>
    internal void Snapshot()
    {
        _previousLocalPosition = _localPosition;
        _previousLocalRotation = _localRotation;
    }

    /// <summary>前ステップと現ステップの間を <paramref name="alpha"/> で混ぜたワールド座標。</summary>
    public Vector3 GetInterpolatedWorldPosition(float alpha)
    {
        Vector3 local = Vector3.Lerp(_previousLocalPosition, _localPosition, alpha);

        // 親がいなければ、ローカル座標がそのままワールド座標。
        // **スプライトの大半はこの経路を通る**ので、ここで分岐する価値がある。
        return _parent is null
            ? local
            : Vector3.Transform(local, _parent.GetInterpolatedLocalToWorld(alpha));
    }

    /// <summary>
    /// 補間したワールドの Z 回転(ラジアン)。
    ///
    /// **鎖の全員が Z 軸まわりに回っている場合だけ正しい**。
    /// Z 回転どうしの合成は角度の足し算になるので、こう書ける
    /// (実際に確かめた: 0.7 と 0.4 を合成すると 1.1 になる)。
    /// 一般の 3D では成り立たないので、そのときは
    /// <see cref="GetInterpolatedLocalToWorld"/> の行列から取り出すことになる。
    /// </summary>
    public float GetInterpolatedWorldRotationZ(float alpha)
    {
        // Slerp ではなく Lerp を使っている。
        // 1ステップぶんの回転差は小さく、その範囲では両者の差は見えない。
        // Slerp は acos と sin を呼ぶので、2万個ぶん回すと効いてくる。
        Quaternion rotation = Quaternion.Lerp(_previousLocalRotation, _localRotation, alpha);

        // クォータニオンから Z 回転角を取り出す。Z 軸まわりだけなら
        // q = (0, 0, sin(θ/2), cos(θ/2)) なので θ = 2*atan2(z, w)。
        float angle = 2.0f * MathF.Atan2(rotation.Z, rotation.W);

        return _parent is null ? angle : angle + _parent.GetInterpolatedWorldRotationZ(alpha);
    }

    /// <summary>補間したワールド行列。親をたどって再帰する。</summary>
    public Matrix4x4 GetInterpolatedLocalToWorld(float alpha)
    {
        Matrix4x4 local =
            Matrix4x4.CreateScale(_localScale)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Lerp(_previousLocalRotation, _localRotation, alpha))
            * Matrix4x4.CreateTranslation(Vector3.Lerp(_previousLocalPosition, _localPosition, alpha));

        return _parent is null ? local : local * _parent.GetInterpolatedLocalToWorld(alpha);
    }

    /// <summary>
    /// 親から順に回転を合成する場合の注意。
    ///
    /// <c>Quaternion.Concatenate(local, parent)</c> は「local を適用してから parent」で、
    /// 行列の <c>M(local) * M(parent)</c> と一致する。
    /// ところが**演算子 <c>*</c> は順序が逆**で、同じものは <c>parent * local</c> と書く。
    /// 行列の <c>*</c> と並べて書くと必ず間違えるので、
    /// クォータニオンを合成するときは <c>Concatenate</c> のほうを使うとよい
    /// (実際に両方を行列と突き合わせて確認した)。
    /// </summary>
    public static Quaternion Compose(Quaternion local, Quaternion parent) =>
        Quaternion.Concatenate(local, parent);

    /// <summary>
    /// 自分と子孫のワールド行列を「作り直しが要る」印にする。
    ///
    /// **すでに印が付いていたら子をたどらない**。
    /// たどってしまうと、深い木で1フレームに何度も動かしたときに
    /// 同じ枝を何度も歩くことになる。
    /// </summary>
    private void MarkDirty()
    {
        if (_dirty)
        {
            return;
        }

        _dirty = true;

        foreach (Transform child in _children)
        {
            child.MarkDirty();
        }
    }
}
