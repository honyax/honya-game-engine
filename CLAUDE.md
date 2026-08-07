# honya-game-engine

C#で自作ゲームエンジンを写経形式で学ぶリポジトリ。全体計画は `docs/roadmap.md`(必読)。

## 構造と役割

- `docs/roadmap.md`: ロードマップ全体(Day 1〜57)。Dayの内容・順序はここが正
- `docs/plans/DayXX.md`: 各Dayの計画書(create-day スキルで生成)
- `reference/DayXX/`: Claudeが作成するリファレンスコード(答え)。各Dayは独立してビルド・実行可能
- `work/`: ユーザーが写経・改造するコード。**ユーザーの学習領域。明示的に依頼されない限り編集しないこと**(レビュー時に読むのは可)
- `assets/`: 複数Dayで共有する素材(テクスチャ、objモデル、HDRI等)

## 技術スタック

- C# / .NET 10 (LTS)、VSCode
- Phase 0〜1(Day 1〜10): 標準ライブラリのみ(WinForms + LockBits)。GPU不使用のソフトウェアラスタライザ
- Phase 2(Day 11〜13): Win32 P/Invoke + 生OpenGL(自前バインディング)
- Phase 3以降(Day 14〜): Silk.NET
- プロジェクト名・名前空間はPascalCase(`HonyaEngine` 等)

## 開発ルール

- `reference/DayXX` は前Dayの完全コピー+その日の差分。`dotnet run --project reference/DayXX` で単体実行できること
- Day作成後は必ず `dotnet build` が通ることを確認する
- コードコメントは日本語。学習用リポジトリなので「なぜそうするか」を重視して書く
- コミット・プッシュはユーザーが行う。勝手にしない
- 1Dayの差分は写経1〜3時間程度(数百行以内)に収める。超える場合は報告して分割を提案

## 進め方

- 運用: **referenceはPhase単位でまとめて先行作成**し、Phase最終Dayのマイルストーン動作を確認してから写経に入る(手戻り防止)。「Phase 1 のDayをすべて作成して」のような依頼では、Day順に作成しつつ各Dayのビルドを確認し、最後にマイルストーンの動作確認まで行う
- Dayの作成: `create-day` スキル(「Day 5 を作成して」)
- 写経は差分確認(`git diff --no-index`)ベースでユーザーが自力で行う。進めるときはreferenceに一致させる
- 写経中にreferenceの不備が見つかった場合は、該当Dayだけでなく同Phase内の後続Dayにも修正を波及させる
