# Day 14: Silk.NETへ移行、シェーダー管理クラス

Phase 3 / ロードマップ該当行: 「Silk.NETへ移行、シェーダー管理クラス」

## 今日のゴール

**Day 13 とまったく同じ絵が、Silk.NET の上で出る。そしてシェーダを実行したまま差し替えられる。**

画面に出るものは Day 13 の焼き直し(回転する三角形)。
違うのはその下で、Phase 2 で書いた**818行の「儀式」がパッケージ参照3行に置き換わる**。

そして今日から `HonyaEngine` が始まる。Day 15 以降は、この名前空間の中身を
**捨てずに育て続ける**。Phase 2 までは「一度書いて捨てるコード」だったが、
ここからは最終的な AAA デモまで生き残るコードになる。

## 事前に読む資料

- [ゲームグラフィックス特論 A-2 GPU](https://tokoik.github.io/gg/) — ロードマップ指定。
  Day 13 と同じ回。今度は「ライブラリの上から」読み直すと、
  自分が書いたどの部分がどう畳まれたかが見える
- [Silk.NET 公式](https://dotnet.github.io/Silk.NET/) — トップページと
  [Windowing のドキュメント](https://dotnet.github.io/Silk.NET/docs/)
- [Silk.NET のチュートリアル(公式リポジトリ)](https://github.com/dotnet/Silk.NET/tree/main/examples/CSharp/OpenGL%20Tutorials) —
  今日書くコードにいちばん近い実例
- [Matrix4x4 構造体(Microsoft Learn)](https://learn.microsoft.com/ja-jp/dotnet/api/system.numerics.matrix4x4) —
  要点4で扱う行ベクトル規約の確認用
- 復習として **Day13.md の要点5(列優先)**。今日その続きをやる

## 理論の要点

### 1. Silk.NET が引き受けたもの、引き受けなかったもの

Phase 2 で書いたコードが、そのまま消えるわけではない。**消えるものと残るものがある。**

| Phase 2 で書いたもの | 実コード行 | Day 14 では |
|---|---|---|
| `Win32Window.cs`(ウィンドウとメッセージループ) | 172 | **消滅**。`Window.Create(options)` |
| `Wgl.cs`(ピクセルフォーマットとコンテキストの宣言) | 89 | **消滅** |
| `GLContext.cs`(ダミーウィンドウの儀式、VSync) | 185 | **消滅**。`options.API` と `options.VSync` |
| `Win32.cs`(P/Invoke 一式) | 143 | **消滅** |
| `GL.cs`(関数のロードと宣言) | 229 | **消滅**。`GL.GetApi(window)` |
| `Program.cs`(頂点バッファ、ドローコール) | 181 | **残る**(179行) |
| `Shader.cs`(コンパイル、リンク、uniform) | 104 | **残って育つ**(154行) |

合計 **1,103行 → 333行**。消えた770行のほぼ全部が「儀式」で、
**GPU の使い方そのもの(頂点バッファ、シェーダ、ドローコール)は1行も減っていない。**

これがロードマップの言う「Silk.NETが担う部分は学びの密度が低い」の具体的な中身。
`glGenBuffers` を `gl.GenBuffer()` と書くようになっただけで、
やることも順序も知識も何ひとつ変わらない。

対応も素直に1対1になる。

| 自作 | Silk.NET |
|---|---|
| `GL.glGenBuffers(1, out uint id)` | `uint id = gl.GenBuffer()` |
| `GL.glBindBuffer(GL.GL_ARRAY_BUFFER, id)` | `gl.BindBuffer(BufferTargetARB.ArrayBuffer, id)` |
| `GL.GL_TRIANGLES` | `PrimitiveType.Triangles` |
| `GL.glGetString(GL.GL_RENDERER)` を UTF-8 で変換 | `gl.GetStringS(StringName.Renderer)` |

**定数が enum になった**のが一番効く差で、`glBindBuffer` に
うっかり `GL_TRIANGLES` を渡す事故がコンパイル時に止まるようになった。

### 2. イベント駆動のループ — 自分で回さなくなる

Day 11 で書いた `while (window.ProcessMessages())` のループは、`window.Run()` の中に入った。
こちらは「このタイミングで呼んでくれ」と登録する側に回る。

```csharp
_window.Load += OnLoad;       // コンテキストが出来た直後に1回
_window.Update += OnUpdate;   // 更新
_window.Render += OnRender;   // 描画
_window.Closing += OnClosing; // 後片付け
_window.Run();
```

**`Update` と `Render` が分かれている**のがポイント。今日はどちらも毎フレーム
同じ回数呼ばれるので違いは無いが、これは **Day 19 の固定タイムステップ**への布石になっている
(更新は秒60回に固定し、描画はモニタの都合で可変にする)。

もう1つ大事な制約。**`Load` より前に GL の関数は使えない。**
コンテキストがまだ無いからで、Day 12 で「wglMakeCurrent の後でないと
wglGetProcAddress が動かない」と書いたのとまったく同じ理由。
ライブラリを使っても、GPU の都合は変わらない。

### 3. シェーダをファイルへ出す — 「管理クラス」の第一歩

Day 13 のシェーダは C# の `const string` だった。ファイルに出す理由は3つ。

- **エディタが GLSL として扱える**(色分け、補完)
- **git の差分が読める**。C# の文字列の中の変更は差分として読みにくい
- **実行中に書き換えられる**(次の要点)

読み込み先は `reference/Day14/shaders/`。**`assets/` ではなく Day のフォルダに置く**のは、
`reference/DayXX` が「その時点の完動スナップショット」でなければならないから。
共有 `assets/` に置くと、Day 15 でシェーダを変更した瞬間に Day 14 が壊れる。

パスは実行ディレクトリから上へ辿って `shaders` フォルダを探す。
`bin/` にコピーしたものではなく**ソースツリー側を直接読む**のが肝で、
そうしないと編集のたびにビルドが要り、ホットリロードの意味が消える。
(まともなリソース管理は Day 21 で扱う。それまでは Phase 1 の `ObjLoader` と同じこの手で済ませる)

### 4. System.Numerics の行列は、なぜ転置せずに渡してよいのか

Day 13 では `float[16]` を列優先で手詰めしていた。今日から
`System.Numerics.Matrix4x4` を使う。ここで**2つの食い違いが同時に起きる**。

| | System.Numerics | OpenGL / GLSL |
|---|---|---|
| メモリの並び | **行優先**(M11,M12,M13,M14,M21,…) | **列優先**として読む |
| 掛け算の規約 | **行ベクトル**(v * M) | **列ベクトル**(M * v) |

一見すると2箇所直さないといけなさそうだが、**この2つはちょうど打ち消し合う。**

行優先のメモリを列優先として読むと、それは**転置**になる。
そして転置は、行ベクトル規約を列ベクトル規約へ変換する操作そのもの。
だから `transpose: false` のまま素直に渡せば正しく動く。

```csharp
_gl.UniformMatrix4(location, 1, false, (float*)pointer);   // transpose は false でよい
```

**実測で確認した。** Day 13(手詰めの列優先)と Day 14(System.Numerics)で
同じ絵になるか、`glReadPixels` で5点を読み比べたところ**全点が完全一致**した(完成条件を参照)。

もう1つ、掛ける順序が Phase 1 と逆になる点に注意。
行ベクトル規約では `A * B` が「A を適用してから B」を意味する。

```csharp
// 回転してから、アスペクト比の補正で横を縮める
Matrix4x4 transform = Matrix4x4.CreateRotationZ(_angle)
                    * Matrix4x4.CreateScale(1.0f / aspect, 1.0f, 1.0f);
```

Phase 1 の自作 `Mat4`(列ベクトル規約)なら `Scale * Rotate` と書く場面。
**どちらが正しいということはなく、規約が2つあるだけ**——Day 10 で
OBJ の V 座標について書いたのと同じ構図で、どこかで必ず吸収する必要がある。

### 5. ホットリロード — 落ちないことが仕様

**今日いちばん実用的な部品。** シェーダを書き換えるたびに再起動していると、
1日に何十回も待つことになる。F5 で作り直せるようにする。

作りで大事なのは「成功したときの動き」ではなく、**失敗したときの動き**。

```csharp
public bool TryReload()
{
    if (!TryCreateProgram(out uint newProgram, out string error))
    {
        Console.WriteLine($"[リロード失敗] 古いシェーダを使い続けます:\n{error}");
        return false;   // ← 例外を投げない
    }

    _gl.DeleteProgram(_program);   // 新しいものが出来てから古いものを消す
    _program = newProgram;
    _uniformLocations.Clear();     // 場所は変わりうるのでキャッシュを捨てる
    return true;
}
```

- **例外を投げない。** 落ちたら結局再起動と同じで、仕組みの意味が半分消える
- **新しいプログラムが出来てから古いものを消す。** 逆にすると失敗時に描くものが無くなる
- **uniform の場所のキャッシュを捨てる。** リンクし直すと場所は変わりうる

**実測**: シェーダをわざと壊して F5 すると、こう出て**アプリは 60fps のまま動き続けた**。

```
[リロード失敗] 古いシェーダを使い続けます:
basic.frag のコンパイルに失敗しました:
0(21) : error C0000: syntax error, unexpected reserved word "this" at token "this"
```

### 6. uniform の場所をキャッシュする理由と、-1 の意味

`glGetUniformLocation` は名前(文字列)で引くので、毎フレーム呼ぶものではない。
`Dictionary<string, int>` に覚えておく。

**-1 が返ってもエラーとは限らない**のは Day 13 と同じだが、
今日は実際に踏むようになっている。フラグメントシェーダの
`FragColor` を上書きするように書き換えると、`uTime` は結果に影響しなくなり、
**最適化で削除されて -1 になる**。

```
[警告] uniform 'uTime' が見つかりません(未使用で削除された可能性)
```

これは実際にホットリロードの実験で出た。バグではないので、
毎フレーム警告を出さないよう「一度出した名前は覚えておく」ようにしてある。

### 7. プロジェクト設定の変更点

Phase 2 から csproj が3つ変わった。

| 設定 | Phase 2 | Day 14 | 理由 |
|---|---|---|---|
| `OutputType` | `WinExe` | **`Exe`** | Day 12 の `AllocConsole` の小細工が不要になる。標準出力がそのまま使える |
| `RootNamespace` | `RawGL` | **`HonyaEngine`** | Phase 3 以降で固定 |
| `AllowUnsafeBlocks` | なし | **`true`** | Silk.NET の一部 API がポインタを取るため |

`TargetFramework` は `net10.0-windows` のままにした。Silk.NET 自体は
クロスプラットフォームなので `net10.0` にできるが、
`.vscode/launch.json` が `bin/Debug/net10.0-windows/` を決め打ちしているのと、
このリポジトリが実際には Windows 専用(PowerShell スクリプト、Phase 0〜1 の WinForms)
であることから、**変えない**判断にした。
変えるなら launch.json も同時に直す必要がある(1行)。

`unsafe` について。Silk.NET は `void*` を取る API を持っていて、
`VertexAttribPointer` のオフセットや `BufferData` のデータ先頭がそれに当たる。
グラフィックス周りでは普通のことで、避けようとするほうが不自然になる。

## 前Dayからの差分概要

**Day 14 は Day 13 のコピーではない。** Phase が変わるので、
`reference/Day14` は空から作った新しいプロジェクトになる。
写経先も `work/RawGL` ではなく **`work/HonyaEngine` を新規に作る**。

| ファイル | 行数 | 役割 |
|---|---|---|
| `Day14.csproj` | 15 | Silk.NET のパッケージ参照3つ。**Phase 2 の37行から半分以下に** |
| `Shader.cs` | 261 | **今日の中心**。ファイル読み込み、ホットリロード、uniform |
| `Program.cs` | 305 | ウィンドウ設定、三角形、ループ |
| `shaders/basic.vert` | 21 | Day 13 の頂点シェーダをファイルに出したもの |
| `shaders/basic.frag` | 19 | 同上 + `uTime` による脈動 |

実コード行は **333行**(Day 13 は 1,103行)。
うち `Shader.cs` の154行が今日の新しい内容で、`Program.cs` の179行は
Day 13 からの移し替えが大半。**1〜2時間で収まる。**

### NuGet パッケージ

```
Silk.NET.Windowing 2.23.0   ウィンドウ生成(内部で GLFW を使う)
Silk.NET.OpenGL    2.23.0   OpenGL バインディング
Silk.NET.Input     2.23.0   キーボード・マウス
```

`dotnet restore` で自動的に入る。ネイティブの GLFW も
`Silk.NET.Windowing.Glfw` 経由で一緒に入るので、別途用意する必要はない。

### 写経する順番

1. `Day14.csproj` — **Phase 2 の csproj と見比べる**。何が消えたかが Phase 3 の宣言
2. `shaders/basic.vert` / `.frag` — Day 13 の文字列をファイルに移すだけ
3. `Shader.cs` — **今日の中心**。
   `TryCreateProgram` → `TryCompile` → `TryReload` → uniform、の順に読む。
   **失敗時に何をしないか**(要点5)に注目する
4. `Program.cs` の `Main` と `OnLoad` — Day 12 の300行がここに畳まれている
5. `Program.cs` の `CreateTriangle` — Day 13 と1対1で見比べる
6. `Program.cs` の `OnRender` — 行列の掛ける順序(要点4)

Day 13 との比較:

```
git diff --no-index reference/Day13/Program.cs reference/Day14/Program.cs
git diff --no-index reference/Day13/Shader.cs  reference/Day14/Shader.cs
```

## 完成条件

```
dotnet run --project reference/Day14 -c Release
```

1. 起動時にターミナルへ GL の情報が出る。**`AllocConsole` が無くなった**ので、
   `dotnet run` した端末にそのまま出る
2. シェーダの読み込み元が **`reference/Day14/shaders`**(`bin/...` ではない)と表示される
3. **Day 13 とまったく同じ三角形**が回転している。上が赤、左下が緑、右下が青で、
   中心は混色。回転しても歪まない
4. 明るさがゆっくり脈打つ(`uTime` による)
5. タイトルバーが
   `Day14 - Silk.NET へ移行  60.0 fps | VSync:ON | 塗り | F5:シェーダ再読込 W:ワイヤー V:VSync Space:停止 Esc:終了`
6. **F5 でホットリロードが効く。** `shaders/basic.frag` の最終行のコメントを外して保存し、
   F5 を押すと**アプリを止めずに色が反転する**。コンソールに `[リロード成功]`
7. **わざと壊しても落ちない。** `.frag` に適当な文字列を書いて F5 すると
   `[リロード失敗] 古いシェーダを使い続けます` が出て、**絵はそのまま動き続ける**
8. **W** でワイヤーフレーム、**V** で VSync、**Space** で回転停止、**Esc** で終了

Day 13 と同じ絵であることは目視ではなく実測で確認してある。
`glReadPixels` で5点を読み、Day 13 の値と突き合わせた結果:

| 位置 | Day 14 | Day 13 | |
|---|---|---|---|
| 左下すみ (5,5) | 25,28,33 | 25,28,33 | 一致 |
| 中央 (320,240) | 144,106,107 | 144,106,107 | 一致 |
| 上頂点付近 (320,370) | 245,56,57 | 245,56,57 | 一致 |
| 左下頂点付近 (200,130) | 59,234,64 | 59,234,64 | 一致 |
| 右下頂点付近 (440,130) | 59,64,234 | 59,64,234 | 一致 |

**全点が完全一致**。要点4の「転置しなくてよい」がこれで裏付けられている。

うまくいかないときの確認ポイント:

- **`DirectoryNotFoundException: shaders`** → 実行ディレクトリから上に
  `shaders` フォルダが見つからない。`dotnet run --project` で起動しているか
- **三角形が出ない / 画面外へ飛ぶ** → 行列の掛ける順序(要点4)。
  `Scale * Rotate` にすると回転と一緒にアスペクト補正まで回ってしまう
- **`transpose` を true にしたら壊れた** → 正しい。要点4のとおり false が正解
- **`Shader` があいまいだとコンパイルエラー** → `Silk.NET.OpenGL` にも
  `Shader` 型がある。`namespace HonyaEngine;` の中なら自分のほうが優先されるので
  問題にならないが、トップレベルステートメントで書くとぶつかる
- **F5 を押しても何も起きない** → ウィンドウにフォーカスがあるか。
  また、エディタが**保存していない**可能性(VSCode の自動保存を確認)
- **F5 でリロードしたら文字化けした GLSL エラーが出る** →
  シェーダファイルの文字コード。**UTF-8(BOM なし)**で保存する。
  PowerShell の `Set-Content` は既定で BOM を付けるので使わないこと
- **`uniform 'uTime' が見つかりません` の警告** → 要点6。
  `FragColor` を上書きするよう書き換えると実際に出る。バグではない
- **fps が 60 に張り付かない** → `options.VSync` と、
  ドライバ側の垂直同期設定(Day 12 と同じ)

## 改造課題

### 課題1(易): 行列の規約を自分で壊してみる

要点4を頭でなく手で確認する。**壊れ方を知っておくと、後で必ず助かる。**

1. `SetMatrix4` の `transpose` を `true` にする。どう壊れるか。
   なぜその壊れ方になるのかを、要点4の表で説明できるか
2. 掛ける順序を `CreateScale(...) * CreateRotationZ(...)` に変える。
   何が起きるか。**アスペクト補正が回転してしまう**のを目で確認する
3. `Matrix4x4.CreateTranslation(0.3f, 0.0f, 0.0f)` を足して、
   三角形を右に寄せる。回転の前に掛けるか後に掛けるかで結果がどう変わるか
4. `Matrix4x4` の中身を `Console.WriteLine` して、`M11`〜`M44` の
   どこに平行移動成分が入るか確かめる(Phase 1 の自作 `Mat4` と比べる)

### 課題2(中): シェーダを保存した瞬間に反映する

F5 を押すのすら惜しくなってくる。`FileSystemWatcher` で自動化する。

1. `shaders` フォルダを監視し、`.vert` / `.frag` の変更でリロードする
2. **変更通知は1回の保存で複数回飛ぶ。** そのまま繋ぐと1回の保存で
   何度もコンパイルが走る。最後の通知から数百ms待ってから実行する(デバウンス)
3. **通知はワーカースレッドで来る。** そこから GL を呼んではいけない
   (コンテキストはスレッドに紐付いている。Day 12 の要点1)。
   フラグを立てて、次の `OnRender` で実際のリロードを行う
4. **保存途中の不完全なファイルを読むことがある。** 要点5の `TryCreateProgram` が
   `IOException` を握り潰しているのはこのためで、実際に踏むか確かめる

3 が特に重要。**GL の呼び出しは必ず描画スレッドから**という制約は、
Phase 4 以降で非同期リソース読み込み(Day 21)をやるときに本格的に効いてくる。

### 課題3(難): `Shader` を「マテリアル」へ育てる前段を作る

Day 15 でマテリアルを抽象化するので、その下ごしらえ。

1. 現在の `SetFloat` / `SetVector3` / `SetMatrix4` は、呼ぶ側が
   uniform の名前と型を知っている前提。**シェーダに聞けるようにする**。
   `glGetProgramiv(GL_ACTIVE_UNIFORMS)` と `glGetActiveUniform` で、
   リンク済みプログラムから uniform の名前・型・場所を全部列挙できる
2. 列挙結果を辞書に持ち、`Dictionary<string, object>` で値を設定できるようにする。
   型が合わなければ実行時に警告する
3. **なぜこれが要るのか**を考える。マテリアルは
   「このシェーダに、この値の組を設定したもの」なので、
   値の組をデータとして持てないとファイルから読めない(Day 24 のシリアライズ)
4. 発展: uniform ブロック(UBO)を調べる。
   ビュー行列や投影行列のように**全マテリアルで共通の値**を、
   マテリアルごとに設定し直すのは無駄で、Phase 3 の後半で効いてくる

## 動作確認済み環境

- .NET SDK 10.0.102 / Windows 11 / NVIDIA GeForce RTX 3070(ドライバ 596.49)
- Silk.NET 2.23.0(Windowing / OpenGL / Input)
- OpenGL 3.3 コアプロファイル / GLSL 3.30 / VSync ON で 60.0 fps
- `glReadPixels` による5点比較で、**Day 13 と完全一致**することを確認(完成条件の表)
- ホットリロードの成功(`[リロード成功]`)と失敗(`[リロード失敗]` +
  アプリは 60fps のまま継続)の両方を実測で確認
- 実コード行 1,103行(Day 13)→ **333行**(Day 14)
- `dotnet build` 警告 0 / `dotnet format whitespace --verify-no-changes` 差分 0
