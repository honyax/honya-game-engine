using Silk.NET.Input;

namespace HonyaEngine;

/// <summary>
/// **デバッグ操作の割り当て表**(Day 48)。今日の主役。
///
/// <para>
/// Day 31 から Day 47 まで、その日のスイッチは <c>Program.OnKeyDown</c> の
/// switch 文へ<b>修飾キーの組み合わせ</b>で足してきた。
/// Ctrl+F の段、Shift+F の段、Ctrl+Shift+F の段……と埋めていって、
/// 昨日の時点で <b>2424 行・case 225 個</b>。修飾キー3つの組み合わせは
/// とうに使い切っていて、Day 45 からは文字キー(X / G / F)に逃げていた。
/// </para>
///
/// <para>
/// <b>キーが足りないことより重かった問題</b>がある。
/// C# の switch は<b>上から順に</b>照合するので、
/// <c>case Key.F1 when ctrl:</c> は <b>Ctrl+Shift+Alt+F1 でも成立する</b>。
/// だから新しい段はいつも既存の段より<b>上</b>に置かねばならず、
/// 各 case のガードには <c>!shift &amp;&amp; !alt</c> という否定が並んでいた。
/// **switch の並び順が仕様の一部**になっていた——
/// これは動くうちは気づけず、順番を間違えた日に「今日のキーが1つも効かない」形で出る。
/// </para>
///
/// <para>
/// <b>表にすると、その危うさが構造ごと消える</b>。
/// 引くときは「今のページの、このキー」で<b>完全一致</b>なので、
/// 並び順はどこにも効かない。組み合わせを探す必要も無くなる——
/// ページを1枚足せば <see cref="SlotCount"/> 個の席が丸ごと手に入る。
/// </para>
///
/// <para>
/// <b>値を持たない</b>のは <see cref="FeatureToggles"/> と同じ判断(Day 40)。
/// 本当の状態は <see cref="Ssao.Enabled"/> や <c>Physics.FrictionEnabled</c> の
/// ほうにあり、ここが持つのは<b>そこへの読み書きの手順</b>だけ。
/// 二重に持つと、キー以外の経路で触られた瞬間に食い違う。
/// </para>
///
/// <para>
/// <b>キーは席の順番で決まる</b>(<see cref="Slots"/>)。
/// 登録した順に F2, F3, … F12, 1, 2, … 9, 0 が割り当たる。
/// <c>Add</c> にキーを書かせないのは、**書かせると必ず重複する**から——
/// 昨日までの switch で起きていたことがそのまま起きる。
/// 順番で決まるなら重複はあり得ないし、割り当て表を読めば今のキーが分かる。
/// </para>
/// </summary>
internal sealed class DebugMenu
{
    /// <summary>
    /// 席の並び。**F1 は入っていない**——ページ送りに使うので、常にメニュー自身のもの。
    ///
    /// <para>
    /// 数字キーを後ろに回してあるのは、F の段のほうが「デバッグのつまみ」として
    /// 手が憶えているため。1ページ 21 席あるので、いちばん多いページ(12 項目)でも余る。
    /// </para>
    /// </summary>
    public static readonly Key[] Slots =
    [
        Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7,
        Key.F8, Key.F9, Key.F10, Key.F11, Key.F12,
        Key.Number1, Key.Number2, Key.Number3, Key.Number4, Key.Number5,
        Key.Number6, Key.Number7, Key.Number8, Key.Number9, Key.Number0,
    ];

    /// <summary>1ページに置ける項目の数。</summary>
    public static int SlotCount => Slots.Length;

    /// <summary>割り当て1つぶん。</summary>
    internal sealed class Entry
    {
        public Entry(Key key, string name, string effect, Action action)
        {
            Key = key;
            Name = name;
            Effect = effect;
            Action = action;
        }

        /// <summary>押すキー。席の順番で決まる。</summary>
        public Key Key { get; }

        /// <summary>一覧に出す短い名前。</summary>
        public string Name { get; }

        /// <summary>**何が起きるか**。一覧に出す1行。</summary>
        public string Effect { get; }

        public Action Action { get; }
    }

    /// <summary>
    /// ページ1枚。**1つのテーマにつき1枚**にしてある。
    ///
    /// <para>
    /// 昨日までの「Ctrl+F の段」「Shift+F の段」がそのままページになった——
    /// 修飾キーの組み合わせが、たまたま Day ごとのテーマと1対1だったため。
    /// つまり<b>分類は元からあって、キーの組み合わせで表現していただけ</b>だった。
    /// </para>
    /// </summary>
    internal sealed class Page
    {
        private readonly List<Entry> _entries = [];

        public Page(string name, string summary)
        {
            Name = name;
            Summary = summary;
        }

        /// <summary>ページの名前。HUD にも出る。</summary>
        public string Name { get; }

        /// <summary>このページが何のためのものか。一覧の見出しに出す。</summary>
        public string Summary { get; }

        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>
        /// 項目を1つ足す。**キーは書かない**——登録順に席が決まる。
        ///
        /// <para>
        /// 席が足りなくなったら黙って落とすのではなく例外にする。
        /// 「押しても何も起きないキー」は原因を探すのがいちばん面倒な壊れ方で、
        /// しかも起動した瞬間に分かる種類の間違いだから。
        /// </para>
        /// </summary>
        public Page Add(string name, string effect, Action action)
        {
            if (_entries.Count >= SlotCount)
            {
                throw new InvalidOperationException(
                    $"ページ「{Name}」の席({SlotCount})が足りない。ページを分けること");
            }

            _entries.Add(new Entry(Slots[_entries.Count], name, effect, action));
            return this;
        }
    }

    private readonly List<Page> _pages = [];

    /// <summary>
    /// いま開いているページ。**-1 は「閉じている」**。
    ///
    /// <para>
    /// 閉じている状態をわざわざ持っているのは、
    /// <b>素のキーを取り上げないため</b>。F5 のシェーダ再読込も、
    /// 数字キーのスプライト数も、Day 14 から手が憶えている。
    /// メニューを開いている間だけ席として借りて、閉じれば元に戻る。
    /// </para>
    /// </summary>
    public int Index { get; private set; } = -1;

    public IReadOnlyList<Page> Pages => _pages;

    /// <summary>開いていれば今のページ、閉じていれば <c>null</c>。</summary>
    public Page? Current => Index >= 0 && Index < _pages.Count ? _pages[Index] : null;

    public Page Add(string name, string summary)
    {
        var page = new Page(name, summary);
        _pages.Add(page);
        return page;
    }

    /// <summary>
    /// 次のページへ(<c>F1</c>)。**最後まで行くと閉じる**。
    ///
    /// <para>
    /// 輪にして閉じる状態を挟むのは、「押し続ければいつか元に戻る」ようにするため。
    /// 閉じ方を別のキーにすると、そのキーを憶えていないと戻れなくなる。
    /// </para>
    /// </summary>
    public void Next() => Index = Index + 1 >= _pages.Count ? -1 : Index + 1;

    /// <summary>前のページへ(<c>Shift+F1</c>)。閉じているときは最後のページへ。</summary>
    public void Previous() => Index = Index - 1 < -1 ? _pages.Count - 1 : Index - 1;

    /// <summary>
    /// 今のページから引いて、あれば実行する。**完全一致**なので並び順は効かない。
    ///
    /// <para>
    /// 閉じているときは常に <c>false</c> を返し、呼び出し側の switch に流す。
    /// ここが昨日までとの決定的な違いで、
    /// <b>「どちらが先か」を考えなくてよくなった</b>のはこの1行のおかげ。
    /// </para>
    /// </summary>
    public bool TryInvoke(Key key)
    {
        Page? page = Current;

        if (page is null)
        {
            return false;
        }

        foreach (Entry entry in page.Entries)
        {
            if (entry.Key == key)
            {
                entry.Action();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 今のページの割り当てをコンソールへ。**ページを切り替えるたびに出す**。
    ///
    /// <para>
    /// 昨日までは、その日の「到達点」の case が
    /// <c>"  Shift+F:摩擦 ON/OFF  Alt+F:蓄積インパルス  …"</c> のような行を
    /// <b>手で書いて</b>出していた。項目を足すたびに書き足す必要があり、
    /// 書き忘れても動くので<b>誰も気づかない</b>。表から起こせば、そこが揃う。
    /// </para>
    /// </summary>
    public void PrintCurrent()
    {
        Page? page = Current;

        Console.WriteLine();

        if (page is null)
        {
            Console.WriteLine("メニュー: 閉じた(F2〜F12・数字キーは素の割り当てに戻る)");
            Console.WriteLine("  F1:次へ  Shift+F1:前へ  Ctrl+F1:目次  Alt+F1:自己チェック");
            return;
        }

        Console.WriteLine($"メニュー [{Index + 1}/{_pages.Count}] **{page.Name}** — {page.Summary}");

        foreach (Entry entry in page.Entries)
        {
            Console.WriteLine($"  {KeyLabel(entry.Key),-4} {entry.Name,-16} {entry.Effect}");
        }
    }

    /// <summary>ページの目次(<c>Ctrl+F1</c>)。16 枚もあると順送りだけでは探せない。</summary>
    public void PrintIndex()
    {
        Console.WriteLine();
        Console.WriteLine($"メニューのページ({_pages.Count} 枚。F1 で送る / Shift+F1 で戻る)");

        for (int i = 0; i < _pages.Count; i++)
        {
            Console.WriteLine(
                $"  {(i == Index ? "▸" : " ")} {i + 1,2}. {_pages[i].Name,-22} {_pages[i].Summary}");
        }
    }

    /// <summary>HUD の1行。**開いているかどうかが絵から読めない**ので常に出す。</summary>
    public string HudLine()
    {
        Page? page = Current;

        return page is null
            ? $"メニュー:閉  F1で開く({_pages.Count}ページ)"
            : $"メニュー[{Index + 1}/{_pages.Count}]:{page.Name}  {page.Entries.Count}項目";
    }

    /// <summary>キーを表示用の短い名前に。<c>Number1</c> のままでは一覧が読めない。</summary>
    public static string KeyLabel(Key key)
    {
        if (key == Key.Number0)
        {
            return "0";
        }

        if (key >= Key.Number1 && key <= Key.Number9)
        {
            return ((int)(key - Key.Number1) + 1).ToString();
        }

        return key.ToString();
    }
}
