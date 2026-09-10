namespace HonyaEngine;

/// <summary>
/// リソースを指す**ハンドル**。参照の代わりに配るための、32ビットの整数1個。
///
/// Day 15 からずっと、テクスチャやシェーダは <c>Texture</c> の参照そのものを
/// 配って回していた。動きはするが、規模が大きくなると3つ困る。
///
/// **1. 誰が解放するのか決まらない**
/// <see cref="Material"/> のコメントに「共有されるので破棄の責任は持たない」と
/// 書いてあるとおり、参照を配ると寿命の持ち主が曖昧になる。
/// 曖昧なままだとリーク(誰も捨てない)か二重解放(2人が捨てる)のどちらかになる。
///
/// **2. 解放したあとの参照が生き残る**
/// C# の参照は、GC がある以上「解放済みのオブジェクトを指す」ことはない。
/// が、GPU 側のリソースは <c>Dispose</c> で消えるので、
/// **参照は生きているのに中身は死んでいる**という状態が普通に起きる。
/// 描画すると真っ黒になるか、最悪 GL のハンドル番号が再利用されて別の絵が出る。
/// この手のバグは、原因の <c>Dispose</c> と症状の描画が遠く離れるので追いにくい。
///
/// **3. 差し替えられない**
/// 非同期ロード(要点5)では「先にハンドルを返して、あとで中身を入れる」ことをしたい。
/// 参照を配ってしまうと、配った先を全部探して書き換えないと差し替えられない。
///
/// ハンドルは**間接参照を1段はさむ**ことでこの3つをまとめて解く。
/// 実体は <see cref="ResourcePool{T}"/> の配列の中にあり、ハンドルはその添字にすぎない。
/// 持ち主はプールただ1つ。差し替えはプールの中身を書き換えるだけ。
/// そして「解放済みかどうか」は**世代**で判別できる。
///
/// <code>
///   31            24 23                            0
///   +---------------+------------------------------+
///   |   世代 (8bit) |         添字 (24bit)          |
///   +---------------+------------------------------+
/// </code>
///
/// 添字だけでは足りない。スロットが再利用されると、
/// 古いハンドルが**別人を指したまま生き続ける**からで、これが 2 の再来になる。
/// そこでスロットに世代番号を持たせ、解放のたびに +1 する。
/// 古いハンドルは世代が食い違うので、その場で「無効」と分かる。
///
/// 型引数 <typeparamref name="T"/> は**実行時には何もしない**。
/// <c>Handle&lt;Texture&gt;</c> と <c>Handle&lt;Shader&gt;</c> を取り違えたら
/// コンパイルエラーにするためだけに付いている(幽霊型と呼ばれる手口)。
/// 中身は uint 1個なので、実行時のコストはゼロ。
/// </summary>
internal readonly struct Handle<T> : IEquatable<Handle<T>>
    where T : class
{
    private const int IndexBits = 24;
    private const uint IndexMask = (1u << IndexBits) - 1u;

    /// <summary>世代の最大値。これを超えたら 1 に戻る(<see cref="ResourcePool{T}"/> 参照)。</summary>
    public const uint MaxGeneration = (1u << (32 - IndexBits)) - 1u;

    /// <summary>添字の最大値。16,777,215 個。1種類のリソースとしては十分すぎる。</summary>
    public const int MaxIndex = (int)IndexMask;

    private readonly uint _bits;

    internal Handle(int index, uint generation)
    {
        _bits = ((uint)index & IndexMask) | (generation << IndexBits);
    }

    internal int Index => (int)(_bits & IndexMask);

    internal uint Generation => _bits >> IndexBits;

    /// <summary>
    /// 有効なハンドルか。
    ///
    /// **世代 0 を「無効」に予約している**のがここの肝。
    /// 添字 0 は正当なスロットなので、「添字が 0 なら無効」にはできない。
    /// 一方 <c>default(Handle&lt;T&gt;)</c> はビットが全部 0、つまり世代 0 なので、
    /// **未初期化のフィールドが自動的に無効ハンドルになる**。
    /// 構造体は必ず 0 で初期化されるので、この予約のおかげで
    /// 「初期化し忘れたハンドル」が黙って添字 0 を指す事故が起きない。
    /// </summary>
    public bool IsValid => Generation != 0;

    /// <summary>何も指していないハンドル。</summary>
    public static Handle<T> None => default;

    public bool Equals(Handle<T> other) => _bits == other._bits;

    public override bool Equals(object? obj) => obj is Handle<T> other && Equals(other);

    public override int GetHashCode() => (int)_bits;

    public static bool operator ==(Handle<T> left, Handle<T> right) => left._bits == right._bits;

    public static bool operator !=(Handle<T> left, Handle<T> right) => left._bits != right._bits;

    public override string ToString() => IsValid ? $"#{Index}.g{Generation}" : "#none";
}
