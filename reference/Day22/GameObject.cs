namespace HonyaEngine;

/// <summary>
/// シーンに置かれるもの。**名前と <see cref="Transform"/> と、コンポーネントの入れ物**。
///
/// 中身がほとんど無いのがこのクラスの特徴で、それでよい。
/// 「敵」も「弾」も「カメラ」も「UI パネル」も、
/// **型としては全部これ1つ**。違いは何を <c>AddComponent</c> したか、でしかない。
///
/// この「型が1つしか無い」という性質が、
///   - シーンをファイルに書き出せる(Day 24)
///   - エディタで中身を並べて編集できる
/// といった仕組みを一気に簡単にする。
/// 型が100種類あったら、100種類ぶんの読み書きを書くことになる。
/// </summary>
internal sealed class GameObject
{
    private readonly List<Component> _components = [];
    private bool _activeSelf = true;

    internal GameObject(Scene scene, string name)
    {
        Scene = scene;
        Name = name;
        Transform = new Transform(this);
    }

    public Scene Scene { get; }

    public string Name { get; set; }

    /// <summary>
    /// 位置・回転・スケール。**<c>AddComponent</c> しなくても最初から有る**。
    /// 「そこに無いオブジェクト」は考えにくいので、特別扱いしている。
    /// </summary>
    public Transform Transform { get; }

    /// <summary>このオブジェクト自身が有効か。</summary>
    public bool ActiveSelf => _activeSelf;

    /// <summary>
    /// 親までたどって、実際に有効か。
    ///
    /// **親を切ったら子も止まる**のが直感に合う。
    /// 「敵の集団」をまとめて消したいときに、親を1つ切れば済む。
    /// 毎回たどるのは無駄に見えるが、階層はたいてい数段しかない。
    /// </summary>
    public bool ActiveInHierarchy
    {
        get
        {
            if (!_activeSelf)
            {
                return false;
            }

            for (Transform? t = Transform.Parent; t is not null; t = t.Parent)
            {
                if (!t.GameObject._activeSelf)
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>破棄が予約されているか。<see cref="Scene.Destroy"/> がここを立てる。</summary>
    public bool IsDestroyed { get; internal set; }

    public IReadOnlyList<Component> Components => _components;

    /// <summary>
    /// コンポーネントを足す。
    ///
    /// <c>new()</c> 制約を付けて**引数なしで作れる型だけ**を受け付けている。
    /// 設定はプロパティで後から入れる作りにしておくと、
    /// Day 24 でシーンをファイルから復元するときに
    /// 「作ってからプロパティを埋める」だけで済む。
    /// コンストラクタ引数があると、その復元手順を型ごとに書くことになる。
    /// </summary>
    public T AddComponent<T>()
        where T : Component, new()
    {
        var component = new T { GameObject = this };
        _components.Add(component);
        Scene.RegisterComponent(component);
        return component;
    }

    /// <summary>
    /// 指定した型のコンポーネントを1つ探す。無ければ <c>null</c>。
    ///
    /// **中身は線形探索**。「<c>GetComponent</c> は遅い」とよく言われるが、
    /// 実測すると1回 1.0ns(2個目でも 2.0ns)で、フィールドを読むのと大差ない。
    /// 型の判定はせいぜい数個ぶんで、しかも同じ場所を続けて触るからキャッシュに乗る。
    ///
    /// 効いてくるのは**別々のオブジェクトを順に引くとき**で、
    /// 2万個を1周すると 0.04ms が 0.20ms になる(計画書の要点6)。
    /// 探索そのものより、オブジェクトを渡り歩くこと自体が高い。
    ///
    /// いずれにせよ定石は「<see cref="Component.Start"/> で1回引いてフィールドに持つ」。
    /// 探索を速くするのではなく、**回数を減らす**ほうが素直。
    /// </summary>
    public T? GetComponent<T>()
        where T : class
    {
        // foreach ではなく添字で回している。List<T> の列挙子は構造体なので
        // 割り当ては起きないが、境界チェックの都合でこちらのほうが素直に速い。
        for (int i = 0; i < _components.Count; i++)
        {
            if (_components[i] is T match)
            {
                return match;
            }
        }

        return null;
    }

    public bool TryGetComponent<T>(out T? component)
        where T : class
    {
        component = GetComponent<T>();
        return component is not null;
    }

    /// <summary>
    /// 有効・無効を切り替える。
    ///
    /// 敵を「消す」のではなく「切る」ようにしておくと、
    /// 使い回し(オブジェクトプール)がそのまま実現できる。
    /// 生成と破棄はコストが高いので、弾や敵のように大量に出入りするものは
    /// **切って隠して、また点ける**のが定石。
    /// </summary>
    public void SetActive(bool active)
    {
        if (_activeSelf == active)
        {
            return;
        }

        bool wasActive = ActiveInHierarchy;
        _activeSelf = active;
        bool isActive = ActiveInHierarchy;

        if (wasActive == isActive)
        {
            return;
        }

        NotifyActiveChanged(isActive);
    }

    /// <summary>自分と子孫に <c>OnEnable</c> / <c>OnDisable</c> を配る。</summary>
    private void NotifyActiveChanged(bool active)
    {
        for (int i = 0; i < _components.Count; i++)
        {
            Component component = _components[i];
            if (!component.Enabled)
            {
                continue;
            }

            if (active)
            {
                component.OnEnable();
            }
            else
            {
                component.OnDisable();
            }
        }

        // 子は自分の ActiveSelf を変えていないが、
        // **実効の有効・無効は変わる**ので通知する必要がある。
        foreach (Transform child in Transform.Children)
        {
            if (child.GameObject._activeSelf)
            {
                child.GameObject.NotifyActiveChanged(active);
            }
        }
    }

    public override string ToString() => Name;
}
