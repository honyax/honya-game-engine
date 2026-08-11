# honya-game-engine

C#による自作ゲームエンジン学習プロジェクト。ソフトウェアラスタライザから始めて、生OpenGL、Silk.NETベースのエンジン本体、AAA品質を目指すグラフィックスデモまでをDay制の写経形式で進める。

全体計画と進捗: [docs/roadmap.md](docs/roadmap.md)

## 必要なもの

| 項目 | 内容 |
|---|---|
| OS | Windows。Phase 2(Day 11〜13)のWin32 P/Invokeが前提。他のDayの大半はmacOS/Linuxでも可 |
| SDK | .NET 10 (LTS)。`global.json` でバージョンを固定している |
| IDE | VSCode + C# Dev Kit 拡張(デバッグ・ソリューション管理はこれで十分。Visual Studio / Riderでも可) |
| 拡張 | GLSLシンタックス系拡張(Phase 2以降のシェーダー編集用) |

主要NuGet: Silk.NET(Phase 3以降)、Silk.NET.OpenAL(Day 27)、StbImageSharp(画像読み込み)。
Phase 0〜1(Day 1〜10)は標準ライブラリのみ(WinForms + LockBits)で、GPUを使わない。

## 構成

```text
honya-game-engine/
├── README.md                    # このファイル(前提環境・構成・実行方法)
├── .gitignore                   # `dotnet new gitignore` で生成
├── global.json                  # .NET SDKバージョン固定
├── docs/
│   ├── roadmap.md               # 全体計画(Day一覧・進捗・参考資料)
│   └── plans/
│       ├── Day01.md             # 各Dayの計画書(ゴール/事前に読む資料/実装手順/改造課題)
│       └── ... Day66.md
├── assets/                      # 複数Dayで共有する素材(テクスチャ、objモデル、フォント等)
├── reference/                   # リファレンスコード(AIが作成する「答え」)。Dayごとの完動スナップショット
│   ├── Day01/
│   │   ├── Day01.csproj
│   │   └── Program.cs
│   └── ... Day66/
└── work/                        # 写経用(自分の手で書くコード)。Day分割せず、プロジェクトを継続成長させる
    ├── Framebuffer/             # Day 1: ピクセルバッファと60fpsループ(Phase 0)
    ├── SoftwareRasterizer/      # Day 2〜10: ソフトウェアラスタライザ(ここで完結)
    ├── RawGL/                   # Day 11〜13: 生OpenGLバインディング体験(使い捨て)
    ├── HonyaEngine/             # Day 14〜: 本番エンジン。Day 29頃にエンジン(classlib)とゲーム(exe)に分割
    │   ├── HonyaEngine/         #   エンジン本体(クラスライブラリ)
    │   └── Sandbox/             #   動作確認用ゲーム(卒業制作もここ)
    └── Labs/                    # 教養編の独立実験(エンジンに組み込まないもの)
        ├── CpuRayTracer/        #   Day 58〜60
        ├── VulkanRT/            #   Day 61・63(メッシュシェーダ含む)
        └── GaussianSplatting/   #   Day 66
```

- `reference/DayXX` は各Dayが独立プロジェクトで、単体でビルド・実行できる。後のDayは前のDayの完全コピー+その日の差分。
  コード重複は意図的で、任意の時点の完動品が常に残る(「30日でできる!OS自作入門」と同じ方式)
- `work/` はDayでフォルダを分けず、上記のプロジェクトを継続的に成長させる。区切りはgitのタグ/コミット(`day05` 等)で残す
- 写経で詰まったら該当Dayの `reference/DayXX` を見る
- .slnは無くても動く。IDEで横断的に見たい場合のみ置く

## 実行方法(VSCode)

`.vscode/` に設定済み。**エディタで動かしたいプロジェクトのファイルを1つ開いた状態で:**

| 操作 | 動作 |
|---|---|
| **Ctrl+Shift+B** | 開いているファイルのプロジェクトを Release でビルドして起動 |
| **F5** | 同じプロジェクトを Debug でデバッグ実行(ブレークポイントが効く) |

開いているファイルから親フォルダを辿って一番近い `.csproj` を探すので、
`reference/DayXX` でも `work/` でも同じ操作で動く。Dayが増えても設定変更は不要。

ファイルを開いていないときは Ctrl+Shift+P → `Tasks: Run Task` から:

- `実行: reference の Day を指定 (Release)` — Day番号を入力して起動
- `ビルド: reference の全Day (Release)` — 全Dayが壊れていないか一括確認

FPSを評価するときは必ず Release で。Debug はソフトウェアラスタライザだと目に見えて遅い。

## 使い方(コマンドライン)

```
dotnet run --project reference/Day05                  # リファレンスの実行
dotnet run --project reference/Day05 -c Release       # FPSを見るときはRelease
git diff --no-index reference/Day04 reference/Day05   # 前Dayとの差分
```

Claude Codeで「Day 5 を作成して」「Phase 1 のDayをすべて作成して」(create-day)。
