# Day 11: Win32 P/Invoke でウィンドウとメッセージループ

Phase 2 / ロードマップ該当行: 「Win32 P/Invokeでウィンドウとメッセージループ」

## 今日のゴール

**Day 1 とまったく同じ絵が、WinForms を1行も使わずに 60fps で出る。**

見た目は Day 1 の焼き直しで、新しい絵は1つも出ない。
今日の成果は画面ではなく、**「WinForms が代わりにやっていたことを全部自分で書けた」**という事実のほうにある。

Phase 2 は「積み上げ」ではなく **「引き算」の3日間**。
Day 1〜10 で積んだソフトウェアラスタライザはいったん脇に置き、
ウィンドウの生成からOSとのやりとりまでを剥き出しにする。
ここを一度通しておくと、Phase 3 以降で使う Silk.NET が
「何を隠してくれているライブラリなのか」が具体的に分かる。

## 事前に読む資料

- [Your First Windows Program(Microsoft Learn)](https://learn.microsoft.com/en-us/windows/win32/learnwin32/your-first-windows-program) —
  今日書くコードの C++ 版そのもの。40行ほどなので必ず目を通しておく
- [Window Classes(Microsoft Learn)](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-classes) —
  「ウィンドウクラス」が何なのか(OOP のクラスとは無関係)
- [プラットフォーム呼び出し (P/Invoke)](https://learn.microsoft.com/ja-jp/dotnet/standard/native-interop/pinvoke) —
  `[DllImport]` とマーシャリングの一次情報
- [High DPI Desktop Application Development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) —
  要点6の DPI の話。長いので「DPI awareness mode」の節だけでよい
- 復習として Day01.md の要点4〜6(ゲームループと待ち方)。今日そのまま持ち込む

なおロードマップの Phase 2 の欄にある GLFW 入門PDF は Day 12(wgl コンテキスト)向けなので、
そちらはまだ読まなくてよい。

## 理論の要点

### 1. Phase 2 は引き算 — 何が消えて、何が現れるか

Day 1 の `GameWindow : Form` は 10 行足らずの設定でウィンドウを出していた。
その1行1行の裏で何が起きていたのかを並べると、今日書くものの全体像になる。

| Day 1 で書いていたもの | 実際にやっていたこと | Day 11 で自分が書くもの |
|---|---|---|
| `class GameWindow : Form` | ウィンドウクラスの登録 + ウィンドウ生成 | `RegisterClassExW` + `CreateWindowExW` |
| (Form が内部に持つ) | ウィンドウプロシージャ | `WndProc` メソッド |
| `ClientSize = new Size(w, h)` | 枠のぶんを足して外側サイズを逆算 | `AdjustWindowRect` + 検算 |
| `StartPosition = CenterScreen` | 画面中央への配置計算 | `GetSystemMetrics` + `SetWindowPos` |
| `FormBorderStyle = FixedSingle` | ウィンドウスタイルのビット操作 | `WS_OVERLAPPEDWINDOW & ~WS_THICKFRAME` |
| `SetStyle(ControlStyles.Opaque)` | 背景を塗らせない | `hbrBackground = NULL` |
| `ApplicationHighDpiMode`(csproj) | DPI 仮想化の解除 | `SetProcessDpiAwarenessContext` |
| `[STAThread]` | COM の STA 初期化 | **不要**(要点7) |
| `Application.DoEvents()` | メッセージの取り出しと配送 | `PeekMessage` + `DispatchMessage` |
| `OnKeyDown` | WM_KEYDOWN の受信 | `WndProc` の `case` |
| `Bitmap.LockBits` + `DrawImage` | ピクセル配列の画面転送 | `StretchDIBits` |

**「おまじない」だと思って書いていた行に、全部ちゃんと理由がある。**
それを1対1で確認するのが今日の主目的。

### 2. ウィンドウクラスは「型紙」、ウィンドウは「そこから作った1枚」

Win32 の「ウィンドウクラス」は OOP のクラスとは何の関係もない。
**同じ見た目・同じ挙動のウィンドウを量産するための型紙**で、
`RegisterClassEx` で OS に登録し、`CreateWindowEx` で名前を指定して1枚作る。

```
RegisterClassExW(型紙)  →  CreateWindowExW("その型紙で1枚")  →  HWND
```

ボタンもテキストボックスもスクロールバーも、
OS があらかじめ登録済みのウィンドウクラス(`"BUTTON"`、`"EDIT"`)でしかない。
**Windows では画面上のほぼ全部がウィンドウ**、というのがこのAPIの世界観。

型紙に書く項目のうち、今日効いてくるのは3つ。

- `lpfnWndProc` — ウィンドウプロシージャ。**このウィンドウの振る舞いそのもの**
- `hCursor` — 指定を忘れると、クライアント領域に入った瞬間にカーソルが
  直前のウィンドウのもの(リサイズ矢印など)のまま固まる。有名なハマりどころ
- `hbrBackground` — `NULL` にすると OS は背景を塗らない。
  Day 1 の `SetStyle(ControlStyles.Opaque, true)` と同じ意味

そして `cbSize`。**構造体の先頭に自分自身のバイト数を書く**という流儀が
Win32 には頻出する(`WNDCLASSEXW`、`BITMAPINFOHEADER`、Day 12 の `PIXELFORMATDESCRIPTOR`)。
これは構造体にフィールドを足しても古いバイナリが動き続けるようにするための仕掛けで、
サイズがそのままバージョン番号を兼ねている。**埋め忘れると必ず失敗する。**

### 3. ウィンドウプロシージャ — OS がこちらを呼び返してくる

普通のライブラリ呼び出しは「自分 → 相手」の一方向だが、
ウィンドウプロシージャは逆向き、**OS がこちらの関数を呼ぶ**(コールバック)。

```
[×]ボタンが押された → OS がキューに WM_CLOSE を積む
  → こちらの PeekMessage が取り出す
    → DispatchMessage が WndProc を呼ぶ
      → WndProc が DestroyWindow を呼ぶ
```

メッセージは `uint` の番号1つと、`wParam` / `lParam` という
**メッセージごとに意味が変わる汎用の入れ物2つ**で表現される。
`WM_KEYDOWN` なら `wParam` に仮想キーコード、`lParam` にはリピート回数や
スキャンコードがビット単位で詰まっている。型安全とは正反対だが、これが Windows の ABI。

最重要のルールが1つ。**自分で処理しないメッセージは必ず `DefWindowProcW` に渡すこと。**
ウィンドウの移動、システムメニュー、Alt+F4、フォーカス管理——
「普通のウィンドウの振る舞い」は全部あの関数の中にある。
渡し漏れると「ウィンドウが動かせない」「閉じられない」といった不可解な症状になる。

### 4. C# でコールバックを渡すときの唯一にして最大の罠

`Marshal.GetFunctionPointerForDelegate` でデリゲートを関数ポインタに変えて OS に渡す。
このとき **OS 側は生のアドレスしか持たない**。GC はそのことを知らない。

```csharp
// これは動く。しばらくは。
var proc = new Win32.WndProcDelegate(WndProc);
wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);   // proc はローカル変数
```

`proc` がスコープを抜けて GC に回収された瞬間、OS が持っているアドレスは
無効なメモリを指す。次のメッセージでアクセス違反。

**厄介なのは、すぐには落ちないこと。** GC が実際に走るまでは動き続けるので、
「起動直後は正常、数分後に突然死ぬ」「デバッグビルドでは再現しない」という
最悪の形のバグになる。対策は簡単で、**フィールドに持ち続ける**だけ。

```csharp
private readonly Win32.WndProcDelegate _wndProc;   // クラスが生きている間ずっと生きる
```

これは Win32 に限らず、**ネイティブコードにコールバックを渡すときの一般則**。
Silk.NET でも OpenGL のデバッグコールバックを登録するときに同じ話が出てくる。

### 5. PeekMessage と GetMessage — ゲームループの分かれ道

教科書の Win32 プログラムはこう書く。

```c
while (GetMessage(&msg, NULL, 0, 0)) {   // メッセージが来るまでスレッドを眠らせる
    TranslateMessage(&msg);
    DispatchMessage(&msg);
}
```

**これはゲームには使えない。** マウスもキーも動かさなければメッセージは来ず、
スレッドは眠ったまま1フレームも進まない。

ゲームが欲しいのは「あるだけ処理して、無ければすぐ戻る」動作。それが `PeekMessage`。

```csharp
while (PeekMessageW(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
{
    if (msg.message == WM_QUIT) return false;
    TranslateMessage(ref msg);
    DispatchMessageW(ref msg);
}
return true;   // キューが空になったので、フレームの続きへ
```

Day 1 の `Application.DoEvents()` は、まさにこのループの WinForms 版だった
(そして `Application.Run(form)` が `GetMessage` 版)。
Day 1 で「`Application.Run` を使わない」と書いた理由が、ここでようやく具体的になる。

**細かいが致命的な点**: 第2引数に `IntPtr.Zero`(= すべてのウィンドウ)を渡すこと。
`WM_QUIT` は特定のウィンドウ宛てではなく**スレッドのキューに直接**積まれるため、
ここに自分の `HWND` を書くと `WM_QUIT` を永久に受け取れず、終了できなくなる。

### 6. 終了は4段階 — なぜこんなに分かれているのか

[×] を押してからプロセスが終わるまで、4つのメッセージを経由する。

| 段階 | 何が起きるか | ここで何ができるか |
|---|---|---|
| `WM_CLOSE` | 閉じる要求が来た | **中止できる**。「保存しますか?」を出す場所 |
| `DestroyWindow` | ウィンドウを破棄する | — |
| `WM_DESTROY` | もう破棄された | 後片付け。**描画はもうできない** |
| `PostQuitMessage` → `WM_QUIT` | ループに終われと伝える | メッセージループが抜ける |

段階が分かれている理由は、**「閉じてよいか」と「もう閉じた」を区別する必要があるから**。
`WM_CLOSE` で `DefWindowProc` に渡すと、既定の実装が `DestroyWindow` を呼ぶ。
つまり「何もしなければ閉じる」で、止めたいときだけ自分で処理する設計。

`WM_DESTROY` で `PostQuitMessage` を呼び忘れると、
**ウィンドウが無いままメッセージループが回り続ける**(画面には何も無いのにプロセスが残る)。
Win32 初学者が必ず一度は踏む。

### 7. DPI と、消えた `[STAThread]`

`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` を
**ウィンドウを1枚も作る前に**呼ぶ。これを怠ると、125% や 150% のスケーリング環境で
Windows が勝手に画面を引き伸ばし(DPI 仮想化)、640x480 のフレームバッファがぼやける。
Day 1〜10 では csproj の `ApplicationHighDpiMode=PerMonitorV2` が裏でこれをやっていた
(あれは WinForms のソースジェネレータ用のプロパティなので、
WinForms を参照しない今日のプロジェクトでは書いても何も起きない)。

DPI を宣言すると、代わりに面倒が1つ増える。**`AdjustWindowRect` が当てにならなくなる。**
あの関数が使うのは**システムDPI(= 主モニタのDPI)**の枠の太さだが、
PerMonitorV2 のウィンドウの枠は「実際に載っているモニタのDPI」で描かれる。

本Dayの環境(システムDPI 120 = 125%)で、640x480 のクライアント領域に必要な外側サイズを測った実測:

| 想定DPI | 外側サイズ |
|---|---|
| 96 (100%) | 656 x 519 |
| 120 (125%) | 658 x 527 |
| 144 (150%) | 662 x 536 |
| 192 (200%) | 666 x 551 |

`AdjustWindowRect` は 658x527 を返した(= システムDPI の 120 と一致)。
主モニタと同じスケーリングのうちは正しいが、**スケーリングの違う副モニタに出た場合はずれる**。
96 と 192 では高さが 32 ピクセルも違い、そのぶん絵の下側が画面に出なくなる。

対策は「作ってから `GetClientRect` で実測を聞き、ずれていたら足す」。
先回りしたければ `GetDpiForWindow` + `AdjustWindowRectExForDpi` を使う手もある。

そしてもう1つ、**`[STAThread]` が消えている**。
あれは WinForms が内部で使う COM(クリップボード、ファイルダイアログ、
ドラッグ＆ドロップ)が STA を要求するために必要だったもので、
生の Win32 メッセージループには要らない。これも「おまじないに理由があった」例の1つ。

### 8. 実測 — GDI+ を1枚剥がすと転送が 27倍速くなった

Day 1 の転送は `Bitmap.LockBits` + `Graphics.DrawImage`、つまり **GDI+**。
今日使う `StretchDIBits` はその下の**素の GDI** で、できることは
「メモリ上のピクセル配列を DC へ流し込む」だけ。

同じマシン・同じウィンドウ・同じ 640x480 で、300フレーム平均を測った結果:

| 経路 | 時間/frame |
|---|---|
| A) `LockBits` + `Marshal.Copy` | 0.122 ms |
| A) `Graphics.DrawImage` | **4.486 ms** |
| A) GDI+ 合計(Day 1 の経路) | 4.608 ms |
| B) `StretchDIBits`(Day 11 の経路) | **0.170 ms** |

**27倍。** 1フレーム 16.67ms の予算に対して **4.4ms が丸ごと浮いた**。

GDI+ が遅いというより、**やっていることが違う**。GDI+ は変形・アンチエイリアス・
補間・カラーマネジメントを備えた高機能な描画ライブラリで、
`DrawImage` は毎回「拡大縮小は要るか」「ピクセル形式の変換は要るか」を判断している。
等倍でそのまま転送するだけの用途では、その機構が丸ごと無駄になる。

これは Phase 2 全体のテーマの縮図でもある。
**層を1枚剥がすと、その層が何をしていたかが時間として見える。**
Day 13 で GPU に描かせたときにも、同じ種類の驚きが待っている。

なお、この数msは Phase 1 でもずっと払っていた。
Day 10 の実測は render 17.8ms で約41fps、つまり1フレームに約24.4ms 掛かっていた。
render を引いた残り 6.6ms の大半が転送とその周辺のコストで、
ここを 0.17ms に落とせれば 18ms/frame = **約55fps** まで届く計算になる
(改造課題2で実際に確かめられる)。

Phase 1 の実装が悪かったわけではない——Day 1 の主題は「絵が出る土台を作る」ことで、
標準ライブラリだけで組むという制約もあった。
ただ、**「ライブラリの既定の道が最速とは限らない」**という事実は測って初めて分かる。

## 前Dayからの差分概要

**Day 11 は Day 10 のコピーではない。** Phase が変わるので、
`reference/Day11` は空から作った新しいプロジェクトになる
(Day 10 までのソフトウェアラスタライザは Phase 2 では使わない)。
写経先も `work/SoftwareRasterizer` ではなく **`work/RawGL` を新規に作る**。

| ファイル | 行数 | 役割 |
|---|---|---|
| `Day11.csproj` | 37 | WinExe / net10.0-windows。**`UseWindowsForms` と `ApplicationHighDpiMode` が無い** |
| `Win32.cs` | 415 | P/Invoke 宣言一式。定数・構造体・`DllImport` |
| `Win32Window.cs` | 347 | ウィンドウの生成、ウィンドウプロシージャ、メッセージループ、キー状態 |
| `GdiPresenter.cs` | 104 | `StretchDIBits` による画面転送。**Day 12〜13 で消える** |
| `Framebuffer.cs` | 60 | Day 1 のものを持ち込み。Phase 2 で使わない描画メソッドは削った |
| `Program.cs` | 242 | エントリポイントとゲームループ。ループ本体は Day 1 と同じ |

行数は多いが、**実コード行(コメントと空行を除く)は 561 行**で、その内訳は

- `Win32.cs` の 168 行 … ほぼ全部が宣言。考えることは無く、写すだけ
- `Program.cs` の 138 行 … 6割は Day 1 からの持ち込み
- 残り 220 行 … 今日の本当の新規ロジック

なので、写経の実質的な重さは 200 行強。1〜2時間で収まるはず。

### 名前空間とプロジェクト名

Phase 2 の名前空間は `RawGL`(Day 11〜13 で固定)。
`Framebuffer`(Day 1)、`SoftwareRasterizer`(Day 2〜10)に続く3つ目。

### 写経する順番

1. `Day11.csproj` — **何が消えたか**を Day10.csproj と見比べる。ここが Phase 2 の宣言
2. `Framebuffer.cs` — Day 1 のものを削っただけ。ウォームアップ
3. `Win32.cs` — 長いが単調。定数 → 構造体 → デリゲート → `DllImport` の順に写す。
   `WNDCLASSEXW` と `BITMAPINFOHEADER` のフィールドの順序だけは間違えないこと
   (順序が狂うとコンパイルは通り、実行時に不可解な失敗をする)
4. `Win32Window.cs` — **今日の本体**。
   `RegisterWindowClass` → `CreateWindow` → `ProcessMessages` → `WndProc` の順
5. `GdiPresenter.cs` — 短い。`biHeight` を負にする理由(要点8の脚注)を押さえる
6. `Program.cs` — ループは Day 1 と同じなので、差分は先頭の DPI 宣言と入力処理だけ

Day 1 との比較:

```
git diff --no-index reference/Day01/Program.cs reference/Day11/Program.cs
git diff --no-index reference/Day01/Framebuffer.cs reference/Day11/Framebuffer.cs
```

## 完成条件

```
dotnet run --project reference/Day11 -c Release
```

1. **画面中央に 640x480 のウィンドウが出る**。タイトルは「Day11 - Win32 P/Invoke」
2. **Day 1 とまったく同じ絵**——横に赤・縦に緑のグラデーションで、青が明滅し、
   白い四角が画面内を跳ね回っている
3. タイトルバーが
   `Day11 - Win32 P/Invoke  60.0 fps | render 1.03 ms | present 0.20 ms | Space:一時停止 矢印:移動 Esc:終了`
   のように 0.5 秒ごとに更新される
4. **present が 0.2ms 前後**。Day 1 の GDI+ 経路(約4.6ms)から一桁以上速くなっている
5. **Space** でアニメーションが止まる(タイトルに「一時停止中」が出る)。
   もう一度押すと**止まった位置から**再開する
6. **矢印キーを押しっぱなし**にすると四角が動く。離すと止まる
   (= `WM_KEYDOWN` / `WM_KEYUP` が両方効いている証拠)
7. **Esc** でも **[×]** でも **Alt+F4** でも終了する。
   終了後にプロセスが残らない(タスクマネージャで確認)
8. ウィンドウの**サイズ変更ができない**。最大化ボタンが無い
9. クライアント領域の**カーソルが普通の矢印**

うまくいかないときの確認ポイント:

- **`Win32Exception: RegisterClassExW に失敗した`** → `cbSize` の埋め忘れ(要点2)。
  あるいは同じクラス名で二重に登録している
- **ウィンドウは出るが何も描かれない** → `GdiPresenter` の `biSize` / `biPlanes` / `biBitCount`。
  `StretchDIBits` はエラーを投げず、黙って 0 を返して何もしない
- **絵が上下逆さま** → `biHeight` が正のまま。負にするとトップダウン(要点8の脚注)
- **絵の色がおかしい(赤と青が入れ替わる)** → `biBitCount` が 24 になっている、
  あるいは `Framebuffer.Rgb` のシフト量。0xAARRGGBB でなければならない
- **数分動かしていると突然落ちる / `AccessViolationException`** →
  **ウィンドウプロシージャのデリゲートが GC された**(要点4)。
  フィールドで保持しているか確認する。今日いちばん出会う可能性の高いバグ
- **[×] を押しても終わらない、プロセスが残る** → `WM_DESTROY` で `PostQuitMessage` を
  呼んでいない、または `PeekMessage` の第2引数に `HWND` を渡している(要点5・6)
- **ウィンドウが動かせない・カーソルが変** → `DefWindowProcW` への渡し漏れ、
  または `hCursor` の指定漏れ(要点2・3)
- **絵の右端か下端が切れる** → クライアントサイズの検算漏れ(要点7)。
  スケーリング設定の違うモニタで起きやすい
- **絵がぼやける** → `SetProcessDpiAwarenessContext` を呼んでいない、
  または**ウィンドウを作った後に**呼んでいる(要点7)
- **ウィンドウをドラッグしている間アニメーションが止まる** → **正常**。
  ドラッグ中は OS 側がモーダルループに入り、こちらのループに制御が戻らない
  (改造課題1で扱う)

## 改造課題

### 課題1(易): `GetMessage` に変えて、ゲームループの意味を体感する

要点5を手で確かめる。

1. `PeekMessageW` を `GetMessageW` に差し替える
   (`BOOL GetMessageW(out MSG, HWND, UINT, UINT)`。戻り値は 0 で終了、-1 でエラー)
2. 実行する。**マウスを動かしている間だけアニメーションが進む**はず。
   なぜそうなるかを、要点5の言葉で説明できるか
3. 元に戻したうえで、今度は `WaitUntil` の呼び出しを消してみる。
   fps はどうなるか、タスクマネージャで CPU 使用率はどうなるか
4. ついでにウィンドウのドラッグ中にアニメーションが止まることを確認する。
   これはモーダルループ(OS がドラッグ処理のために自前のメッセージループを回す)によるもの。
   **`WM_PAINT` を自分で処理して現在のフレームバッファを転送する**と、
   ドラッグ中も絵が保たれるようになる。実装して確かめる

### 課題2(中): Phase 1 の転送を `StretchDIBits` に差し替える

要点8で計算した「Day 10 が 45fps → 56fps になるはず」を実測で確かめる。

1. `work/SoftwareRasterizer`(または `reference/Day10` のコピー)の `Present()` を、
   `GdiPresenter` と同じ `StretchDIBits` 経由に差し替える。
   フレームバッファの形式は同じなので、`Bitmap` は丸ごと不要になる
2. Day 10 と同じシーンで fps を測り、差を記録する
3. **予想と合ったか。** 合わなかった場合、どこで食われているかを考える
   (ヒント: 転送が速くなると、今度は別のものがボトルネックになる)
4. 発展: GDI+ の `DrawImage` が具体的に何に時間を使っているかを調べる。
   転送先の矩形サイズを変えて拡大縮小を発生させると、時間はどう変わるか

### 課題3(難): ウィンドウを2枚出せるようにする

今の実装は、ウィンドウプロシージャがインスタンスメソッドのデリゲートである
(= `this` を暗黙に捕まえている)ことに依存しているので、ウィンドウが1枚しか作れない。
実用的なフレームワークが必ず採っている方法に書き換える。

1. ウィンドウプロシージャを `static` にする。当然そのままではインスタンスに触れなくなる
2. `CreateWindowExW` の最後の引数 `lpParam` に、`GCHandle` で固定した
   `Win32Window` インスタンスを渡す
3. `WM_NCCREATE` を処理する。`lParam` は `CREATESTRUCT*` で、その中に `lpCreateParams` として
   さっきの値が入っている。それを `SetWindowLongPtr(hwnd, GWLP_USERDATA, ...)` で
   ウィンドウ自身に保存する
4. 以降のメッセージでは `GetWindowLongPtr(hwnd, GWLP_USERDATA)` から
   インスタンスを取り出して転送する
5. `WM_NCDESTROY` で `GCHandle` を解放する。**忘れるとリークする**
6. ウィンドウを2枚出し、片方を閉じてももう片方が生き残ることを確認する
   (`PostQuitMessage` を呼ぶ条件を「最後の1枚が閉じたとき」に変える必要がある)

なぜ `WM_CREATE` ではなく `WM_NCCREATE` なのか、
そして**なぜウィンドウプロシージャは `CreateWindowEx` が戻る前に呼ばれるのか**を
説明できるようになれば、Win32 のウィンドウ生成は理解できたと言ってよい。

## 動作確認済み環境

- .NET SDK 10.0.102 / Windows 11 / システムDPI 120(125% スケーリング)
- Release ビルドで 58.9〜60.7 fps(render 0.89〜1.28 ms、present 0.16〜0.22 ms)
- クライアント領域が実測でちょうど 640x480 物理ピクセル
  (ウィンドウ全体は 658x527、`GetDpiForWindow` = 120)
- 転送経路のベンチマーク(300フレーム平均、同一マシン・同一ウィンドウ):
  GDI+ 合計 4.608 ms/frame に対し `StretchDIBits` 0.170 ms/frame = **27.2倍**
- `dotnet build` 警告 0 / `dotnet format whitespace --verify-no-changes` 差分 0
