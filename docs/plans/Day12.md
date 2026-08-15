# Day 12: wglコンテキスト生成、OpenGL関数の自前ロード

Phase 2 / ロードマップ該当行: 「wglコンテキスト生成、OpenGL関数の自前ロード」

## 今日のゴール

**自前の P/Invoke だけで OpenGL 3.3 コアプロファイルのコンテキストを作り、GPU が塗った色で画面が埋まる。**

Day 1 から11日間、画面に出るピクセルは全部 CPU が書いていた。
今日から**1ピクセルも自分で書かなくなる**。
やることは「この色で塗れ」と GPU に伝えるだけで、307,200ピクセルの塗りつぶしは GPU の中で並列に走る。

絵としては単色が明滅するだけで、見た目の派手さは Day 11 から後退する。
今日の成果は画面ではなく、**GLFW や Silk.NET が1行で済ませている「儀式」を全部自分で書けた**という事実のほう。

## 事前に読む資料

- [GLFWによるOpenGL入門(PDF)](https://tokoik.github.io/GLFWdraft.pdf) — ロードマップ指定。
  GLFW を使う側の視点だが、「ウィンドウとコンテキストの関係」の説明が丁寧
- [Creating an OpenGL Context (WGL) — OpenGL Wiki](https://www.khronos.org/opengl/wiki/Creating_an_OpenGL_Context_(WGL)) —
  **今日書くコードそのもの**。ダミーウィンドウの件も書いてある
- [Load OpenGL Functions — OpenGL Wiki](https://www.khronos.org/opengl/wiki/Load_OpenGL_Functions) —
  要点5の一次情報。`wglGetProcAddress` の失敗値の話もここ
- [OpenGL Context — OpenGL Wiki](https://www.khronos.org/opengl/wiki/OpenGL_Context) — プロファイルとは何か
- 復習として Day11.md の要点2〜4(ウィンドウクラス、ウィンドウプロシージャ、コールバックのGC)

## 理論の要点

### 1. OpenGL の仕様に「ウィンドウ」は出てこない

意外に思うかもしれないが、**OpenGL は「どこに描くか」を定義していない**。
仕様が決めているのは「コンテキストという状態の入れ物があり、そこに対して命令を出すと絵ができる」までで、
そのコンテキストをどう作り、どの画面に結び付けるかは**プラットフォーム側の別API**の仕事になる。

| プラットフォーム | 担当API |
|---|---|
| Windows | **WGL**(今日やる) |
| Linux / X11 | GLX |
| macOS | CGL(現在は非推奨) |
| モバイル / 組み込み | EGL |

GLFW・SDL・Silk.NET の Windowing が吸収しているのは、まさにこの分岐。
「クロスプラットフォームのウィンドウライブラリ」が何を抽象化しているのかが、ここで具体的に分かる。

**コンテキスト**は、GPU 側に置かれた状態の集合(バインド中のバッファ、有効なシェーダ、
深度テストの ON/OFF……)。`gl*` 関数がコンテキストを引数に取らないのは、
**「カレントコンテキスト」がスレッドごとに1つ決まっている**という暗黙の前提があるため。
Vulkan や D3D12 が毎回デバイスやコマンドリストを明示的に渡すのと対照的な、1992年当時の設計。

### 2. ピクセルフォーマット — 描き先の仕様書

コンテキストを作る前に、**そのウィンドウがどんなバッファを持つか**を決める必要がある。

- 色は何ビットか(32bit RGBA)
- ダブルバッファか
- 深度バッファは何ビットか(24bit)
- ステンシルは(8bit)
- ハードウェアアクセラレーションを使うか

これを `PIXELFORMATDESCRIPTOR` に書いて `ChoosePixelFormat` に渡すと、
ドライバが「一番近いもの」の**番号**を返す。それを `SetPixelFormat` で DC に設定する。

Phase 1 で自分で `new int[width * height]` と `new float[width * height]` を確保していたものが、
ここでは「ドライバに注文する」形になる。深度バッファ(Day 7)も、
もう自分で持たずにピクセルフォーマットの属性として要求するだけになった。

### 3. 鶏と卵 — なぜダミーウィンドウを作って捨てるのか

**今日いちばん奇妙で、いちばん重要な部分。**

`PIXELFORMATDESCRIPTOR` は OpenGL 1.0 時代の様式で、MSAA や sRGB といった
後発の要求を表現できない。またコンテキストのバージョンやプロファイルも指定できない。
そのために拡張関数が2つ用意されている。

- `wglChoosePixelFormatARB` — 属性リスト形式でピクセルフォーマットを選ぶ
- `wglCreateContextAttribsARB` — バージョンとプロファイルを指定してコンテキストを作る

ところがこの2つを手に入れるには `wglGetProcAddress` を呼ぶ必要があり、
**`wglGetProcAddress` はカレントコンテキストが無いと動かない**。
コンテキストを作るための関数を、コンテキストが無いと取れない。

「じゃあ古い方法でいったんコンテキストを作って、拡張関数を取ってから、
同じウィンドウで作り直せばいい」——ここで2つ目の壁にぶつかる。

> **`SetPixelFormat` は1つのウィンドウにつき生涯1回しか呼べない。**
> 一度設定したピクセルフォーマットは変更できない(MSDN に明記されている)。

つまり本番のウィンドウを「練習台」に使ってしまうと、そのウィンドウはもう
古いピクセルフォーマットで固定されてしまう。だから**使い捨てのウィンドウ**が要る。

```
1. ダミーウィンドウを作る(表示しない、1x1)
2. 古い様式でピクセルフォーマットを設定      ← このウィンドウはこれで使い物にならなくなる
3. 古い様式でコンテキストを作り、カレントにする
4. wglGetProcAddress で拡張関数を3つ回収     ← これが目的
5. コンテキスト・DC・ウィンドウ・クラスを全部破棄
--- ここまでが準備 ---
6. 本番ウィンドウに wglChoosePixelFormatARB でピクセルフォーマットを設定
7. wglCreateContextAttribsARB で 3.3 コアプロファイルのコンテキストを作る
8. カレントにして、OpenGL の関数をロードする
```

**GLFW も内部でまったく同じことをしている**(`_glfwInitWGL` がダミーウィンドウを立てる)。
「ライブラリを使えば1行」の裏にあるのがこれ。

### 4. コアプロファイル — 「使えなくなる」ことが価値

OpenGL 3.2 以降、コンテキストには2つのプロファイルがある。

| プロファイル | 中身 |
|---|---|
| **コア** | 現代的な API のみ。`glBegin`/`glVertex` 等の固定機能は**削除されている** |
| 互換 | 1.0 からの全部入り。古いコードが動き続ける |

今日はコアを選ぶ。機能が減るのに選ぶ理由は、**うっかり古い API を呼んだらエラーにしてほしい**から。
Phase 1 で自分で書いた頂点変換とラスタライズを、Day 13 からはシェーダとして
GPU 側に書き直すことになる。そのとき固定機能という逃げ道があると、
「なんとなく動いてしまって何も学ばない」経路ができてしまう。

バージョンに 3.3 を選んだのは、**シェーダを書くうえで必要なものがひととおり揃った最初の版**だから
(VAO、`layout` 修飾子、インスタンシング)。4.x 固有の機能は Phase 6 以降まで使わない。
LearnOpenGL が 3.3 を土台にしているのも同じ理由。

### 5. 関数ロードの二重構造 — Silk.NET が代行している作業の正体

`opengl32.dll` が実際にエクスポートしているのは **OpenGL 1.1 までの関数だけ**で、
これは1996年(Windows NT 4.0)に凍結されている。それ以降に追加された数千の関数は
**GPU ドライバの DLL の中**にあり、名前も存在もコンパイル時には分からない。

結果、関数の取り方が2通りに分かれる。**本Dayの環境で実測した結果がこれ**。

| 関数 | 追加バージョン | `wglGetProcAddress` | `GetProcAddress(opengl32.dll)` |
|---|---|---|---|
| `glClear` | 1.0 | **NULL** | 取得 |
| `glClearColor` | 1.0 | **NULL** | 取得 |
| `glGetString` | 1.0 | **NULL** | 取得 |
| `glViewport` | 1.0 | **NULL** | 取得 |
| `glGetStringi` | 3.0 | 取得 | **NULL** |
| `glCreateShader` | 2.0 | 取得 | **NULL** |
| `glGenVertexArrays` | 3.0 | 取得 | **NULL** |

**きれいに排他**になっている。片方だけでは `glClear` すら呼べない。
だからローダは必ず2段構えになる。

```csharp
IntPtr address = Wgl.wglGetProcAddress(name);
if (失敗) { address = Win32.GetProcAddress(_opengl32, name); }
```

そして「失敗」の判定にもう1つ罠がある。**仕様上は NULL だが、実際のドライバは
1 / 2 / 3 / -1 を返すことがある**。GLAD も GLEW も同じ5つを並べて判定している。
歴史的事故がそのまま定番コードとして固まった例。

取れたアドレスは `Marshal.GetDelegateForFunctionPointer<T>` でデリゲートに変える。
**Silk.NET が数千関数ぶん自動生成しているのは、この作業。**
ロードマップが「学びの密度が低い」と評した領域そのものなので、
一度手で書いたら以降は堂々とライブラリに任せてよい。

### 6. コアプロファイルでは拡張の一覧の取り方が変わる

`glGetString(GL_EXTENSIONS)` は、拡張名を空白区切りで全部繋げた**巨大な1本の文字列**を返す API だった。
拡張が数百個になると数万文字になり、固定長バッファに入れて壊す事故が絶えなかったため、
コアプロファイルでは**削除された**。

```csharp
glGetIntegerv(GL_NUM_EXTENSIONS, out int count);
for (int i = 0; i < count; i++) { glGetStringi(GL_EXTENSIONS, i); }   // 1つずつ取る
```

本Dayの環境では **403個**。旧APIを呼ぶと `GL_INVALID_ENUM` になって NULL が返ることも実測で確認した。
リファレンスコードでは、これを「わざと呼んで NULL を確認する」形で残してある
(コアプロファイルが効いている証拠になるので)。

ここで OpenGL のエラー処理の流儀にも触れておく。**OpenGL は例外を投げず、
エラーをフラグに積んで黙って続行する。** `glGetError()` を呼ぶまで気付けず、
しかも呼ぶとフラグは消える。要所に `CheckError` を挟むのが基本で、
もっと楽をしたければデバッグコンテキスト(改造課題2)を使う。

### 7. SwapBuffers と VSync — 自前のフレームレート制限が要らなくなる

Day 11 までの `Present` は「1.2MB のピクセル配列を毎フレーム転送する」ことだった。
`SwapBuffers` は**表示するバッファを差し替えるだけ**なので、解像度が上がってもコストがほぼ変わらない。

そして VSync。`wglSwapIntervalEXT(1)` を呼ぶと、`SwapBuffers` が
次の垂直帰線まで待つようになる。**Day 1 から書いてきた `WaitUntil` が不要になる**ので、
今日で削除した。

**実測**(本Dayの環境、640x480 の画面クリアのみ):

| 設定 | fps | 1フレーム |
|---|---|---|
| `wglSwapIntervalEXT(1)` | **60.0** | 16.671 ms |
| `wglSwapIntervalEXT(0)` | **5,743** | 0.174 ms |

60fps はモニタが決めていて、こちらの処理能力とは無関係だったことが数字で見える。
VSync を切ると 5,700fps ——つまり**1フレームの実質的な仕事は 0.17ms しかない**。

Day 11 と並べるとこうなる。

| | Day 11(CPU) | Day 12(GPU) |
|---|---|---|
| 画面を塗る | 0.9〜1.3 ms(グラデーション) | 計測不能なほど小さい |
| 画面に出す | 0.170 ms(`StretchDIBits`) | — |
| フレーム全体 | 約 1.2 ms | **0.174 ms** |

厳密な比較ではない(Day 11 はグラデーション、今日は単色クリア)が、
**CPU が全ピクセルを触る仕事が丸ごと消えた**ことは読み取れる。
Phase 1 の Day 10 で「17.8ms、これがCPUの限界」と書いた話の続きが、ようやくここから始まる。

## 前Dayからの差分概要

| ファイル | 変更 | 内容 |
|---|---|---|
| `Wgl.cs` | **新規(206行)** | WGL の宣言。`PIXELFORMATDESCRIPTOR`、ピクセルフォーマットとコンテキストのAPI、ARB拡張の属性とシグネチャ |
| `GLContext.cs` | **新規(306行)** | **今日の本体**。ダミーウィンドウによる拡張ロード → 本番コンテキスト生成 → VSync |
| `GL.cs` | **新規(209行)** | OpenGL 関数のローダと、今日使う7関数 |
| `Program.cs` | 差し替え | フレームバッファ描画 → GPU クリア。`WaitUntil` を削除、コンソール出力を追加 |
| `Win32.cs` | -25 / +33行 | DIB 転送まわりを削除、`LoadLibraryW` / `GetProcAddress` / `AllocConsole` を追加 |
| `Win32Window.cs` | **変更なし** | Day 11 のまま。ウィンドウの作り方はコンテキストと無関係 |
| `Framebuffer.cs` | **削除** | CPU 側のピクセルバッファはもう要らない |
| `GdiPresenter.cs` | **削除** | 転送は `SwapBuffers` が担当する |

実コード行(コメント・空行を除く)は新規3ファイルで **384行**。
ただし `Wgl.cs` の89行はほぼ全部が宣言で、考えることは無い。
**手を動かして考えるのは `GLContext.cs` の185行**で、そこが今日の山。

### 写経する順番

1. `Win32.cs` の差分 — `LoadLibraryW` / `GetProcAddress` / `AllocConsole` の追加と、DIB まわりの削除
2. `Wgl.cs` — 単調。`PIXELFORMATDESCRIPTOR` のフィールド順序だけ注意(順序が狂うと実行時に壊れる)
3. `GL.cs` — ローダの2段構え(要点5)が要点。関数そのものは7個だけ
4. `GLContext.cs` — **今日の本体**。
   `EnsureExtensionsLoaded`(ダミー)→ `SetupPixelFormat` → コンストラクタ、の順に読む
5. `Program.cs` — ループから `WaitUntil` が消え、描画が3行になる

差分の確認:

```
git diff --no-index reference/Day11/Program.cs reference/Day12/Program.cs
git diff --no-index reference/Day11/Win32.cs   reference/Day12/Win32.cs
```

## 完成条件

```
dotnet run --project reference/Day12 -c Release
```

1. **コンソールウィンドウが1枚立ち**、GL の情報が出る。`GL_RENDERER` に自分の GPU 名が出ていること
2. **`GL_VERSION` が 3.3 で始まる**。要求したバージョンが通った証拠
3. `glGetString(GL_EXTENSIONS)` の行が **`NULL(コアプロファイルなので正常)`** になっている(要点6)
4. ゲームウィンドウが **虹色に滑らかに変化する**。カクつきや点滅が無い
5. タイトルバーが
   `Day12 - OpenGL 3.3 Core  60.0 fps | VSync:ON | V:VSync Space:停止 Esc:終了`
6. **V キーで VSync を切ると fps が数千に跳ね上がる**(本Dayの環境では約5,000)。
   もう一度押すと 60.0 に戻る。**今日いちばん分かりやすい実験**
7. **Space** で色の変化が止まり、もう一度押すと止まった色から再開する
8. **Esc** / **[×]** / **Alt+F4** で終了し、プロセスが残らない

参考として、本Dayの環境でのコンソール出力:

```
GL_VENDOR                   : NVIDIA Corporation
GL_RENDERER                 : NVIDIA GeForce RTX 3070/PCIe/SSE2
GL_VERSION                  : 3.3.0 NVIDIA 596.49
GL_SHADING_LANGUAGE_VERSION : 3.30 NVIDIA via Cg compiler
拡張の数                    : 403
```

うまくいかないときの確認ポイント:

- **`GL_RENDERER` が `GDI Generic`** → ハードウェアアクセラレーションが効いていない。
  `WGL_ACCELERATION_ARB, WGL_FULL_ACCELERATION_ARB` を属性に入れているか。
  この状態だと OpenGL 1.1 相当しか動かず、Day 13 のシェーダは全滅する
- **`必須の WGL 拡張が見つからない: wglCreateContextAttribsARB`** →
  ダミーコンテキストがカレントになっていない(要点3の手順3を飛ばしている)。
  あるいは本当に古いドライバ
- **`SetPixelFormat に失敗した`** → **要点3のいちばんの罠**。
  本番ウィンドウに対して2回設定しようとしている。
  ダミーと本番でウィンドウを分けているか確認する
- **`OpenGL 関数が見つからない: glClear`** → ローダのフォールバックが無い(要点5)。
  `wglGetProcAddress` だけでは 1.1 の関数は取れない
- **起動直後に落ちる / `AccessViolationException`** →
  ダミーの `WndProc` デリゲートを static フィールドで保持しているか(Day 11 の要点4)
- **画面が真っ黒のまま** → `SwapBuffers` を呼んでいない。
  OpenGL はバックバッファに描くので、入れ替えないと何も見えない
- **色が変化せず1色で止まる** → `glClearColor` は色を**設定するだけ**。
  `glClear` を毎フレーム呼んでいるか
- **コンソールに日本語が化ける** → `Console.OutputEncoding` の設定。
  `AllocConsole` の**後**に設定する必要がある
- **VSync を切っても fps が変わらない** → ドライバ側の設定で
  「垂直同期:オン」が強制されている(NVIDIA コントロールパネル等)。
  アプリ側の指定より優先される

## 改造課題

### 課題1(易): 要求するコンテキストを変えてみる

`GLContext` に渡す値を変えて、ドライバの反応を観察する。

1. **4.6 を要求する。** 通るか。通ったら `GL_VERSION` は何になるか。
   通らないなら自分のドライバの上限はどこか
2. **`WGL_CONTEXT_CORE_PROFILE_BIT_ARB` を
   `WGL_CONTEXT_COMPATIBILITY_PROFILE_BIT_ARB` に変える。**
   `glGetString(GL_EXTENSIONS)` が NULL でなくなるはず。
   返ってきた文字列の長さを測ってみると、要点6で「削除された」理由が実感できる
3. **存在しない 5.0 を要求する。** どんな失敗の仕方をするか。
   エラーコードは何か(`Marshal.GetLastWin32Error`)
4. `WGL_DEPTH_BITS_ARB` を 0 にして、`DescribePixelFormat` の結果がどう変わるか見る

### 課題2(中): デバッグコンテキストと `glDebugMessageCallback`

要点6で触れた「エラーが黙って積まれる」問題を、根本的に楽にする。

1. コンテキスト属性に `WGL_CONTEXT_FLAGS_ARB, WGL_CONTEXT_DEBUG_BIT_ARB` を足す
2. `glDebugMessageCallback`(OpenGL 4.3 / `GL_KHR_debug`)をロードする。
   シグネチャは
   `void glDebugMessageCallback(DEBUGPROC callback, const void *userParam)`
3. `glEnable(GL_DEBUG_OUTPUT)` と `GL_DEBUG_OUTPUT_SYNCHRONOUS` を有効にする。
   後者を付けると**エラーを起こしたその場でコールバックが呼ばれる**ので、
   スタックトレースが意味を持つようになる
4. わざと `glGetString(GL_EXTENSIONS)` を呼んで、通知が飛んでくることを確認する
5. **ここで Day 11 の要点4が再登場する。** コールバックのデリゲートを
   フィールドで保持しないと、数分後にアクセス違反で落ちる。今度は自分で気付けるか

Day 13 以降、シェーダのコンパイルエラー以外の不具合(バインドし忘れ、
型の不一致)はほぼ全部これで見つかるようになる。**入れておくと後が楽。**

### 課題3(難): 関数ロードを自動化する

`GL.Load()` は今のところ7行だが、Day 13 で15個ほどに増え、Phase 3 に進めば数十個になる。
名前を2回(フィールド名と文字列)書くのは間違いの元でもある。

1. まず**フォールバックを外して壊してみる**。`wglGetProcAddress` だけにすると
   どの関数から落ちるか。要点5の表を自分の環境で再現する
2. `GL` クラスの public static フィールドをリフレクションで列挙し、
   **フィールド名からロードする**ように書き換える
   (`glClearColor` フィールド → `"glClearColor"` を引く)。
   デリゲート型は `FieldInfo.FieldType` から取れるので、
   `Marshal.GetDelegateForFunctionPointer(address, fieldType)` で作れる
3. 取れなかった関数は例外にせず**null のまま残し**、
   起動時に「ロードできなかった関数」の一覧を出すようにする。
   全関数が必須とは限らない(拡張は環境によって有無がある)ため
4. リフレクションの起動時コストを測る。数千関数になったとき許容できるか。
   **できないなら、Silk.NET がなぜコード生成という手段を選んだのかが分かる**

## 動作確認済み環境

- .NET SDK 10.0.102 / Windows 11 / NVIDIA GeForce RTX 3070(ドライバ 596.49)
- `GL_VERSION` = `3.3.0 NVIDIA 596.49` / GLSL `3.30` / 拡張 403個
- VSync ON で 60.0 fps(16.671 ms/frame)、OFF で 5,743 fps(0.174 ms/frame)
- `wglGetProcAddress` と `GetProcAddress(opengl32.dll)` が
  GL 1.1 と 2.0 以降できれいに排他になることを7関数で実測(要点5の表)
- コアプロファイルで `glGetString(GL_EXTENSIONS)` が
  `GL_INVALID_ENUM`(0x0500)と NULL を返すことを確認
- Esc / WM_CLOSE のどちらでも終了コード 0 で正常終了
- `dotnet build` 警告 0 / `dotnet format whitespace --verify-no-changes` 差分 0
