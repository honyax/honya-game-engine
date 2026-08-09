---
name: sync-develop
description: マージ済みの作業ブランチから develop へ、ローカルのファイルを一切変えずに切り替えてブランチを削除する。「developに同期して」「マージしたのでdevelopに戻して」等で起動。
---

# develop 同期スキル

判定と実行はすべて `sync-develop.ps1` が行う。
**このスキルでは git コマンドを個別に実行したり、出力を読んで独自に判断したりしない。**
スクリプトの終了コードだけで分岐する。

## 手順

1. リポジトリのルートで実行する:

   ```
   powershell -NoProfile -File .claude/skills/sync-develop/sync-develop.ps1
   ```

2. 終了コードで分岐する

   - **0**: `[OK]` と `[RESULT]` の行をそのまま報告して終了
   - **10**: 未コミットの変更がある。`[CONFIRM]` に続くファイル一覧を示し、
     AskUserQuestion で「続行 / 中止」を確認する
     - 続行 → 同じコマンドに `-Force` を付けて再実行し、その終了コードで再度この分岐に従う
       (`-Force` 時に 10 は返らない)
     - 中止 → 何もせず終了
   - **それ以外(1〜6)**: `[ERROR]` の行をそのまま報告して終了。
     **リカバリを自分で試みない**(push / commit / reset / 強制切り替え等は一切行わない)

## 終了コードの意味

| コード | 状況 | 対処(ユーザーに伝える内容) |
| --- | --- | --- |
| 0 | 正常終了 | develop に切り替え、作業ブランチを削除済み |
| 1 | develop に居る / detached HEAD / gitリポジトリでない | 作業ブランチに切り替えてから実行する |
| 2 | 未プッシュのコミットがある、または fetch 失敗 | 先に push する |
| 3 | ローカル develop を fast-forward できない | develop に直接コミットしている可能性。ユーザーの判断が必要 |
| 4 | develop の内容が作業ブランチと一致しない | PRが未マージ、または別のPRで develop が先に進んでいる |
| 5 | switch 失敗 | 出力をそのまま報告する |
| 6 | ブランチ削除失敗 | 出力をそのまま報告する |

コード 4 で終了した場合、ローカル develop の ref は最新化済みだが、
作業ツリーは元のブランチのまま変わっていない(何も壊れていない)。

## スクリプトが行う処理

1. 現在のブランチを確認(develop / detached HEAD なら終了)
2. `git fetch --prune origin`(作業ツリーには触れない)
3. 未プッシュのコミットがないか確認
   - `origin/<branch>` があれば `origin/<branch>..HEAD` の件数で判定
   - PRマージ時に GitHub 側でブランチが削除済みの場合は、HEAD が `origin/develop` に
     含まれているかで判定
4. 未コミットの変更・新規ファイルの有無を確認(あれば 10 で終了、`-Force` で続行)
5. `git fetch origin develop:develop` でローカル develop を最新化
   (fast-forward 限定。作業ツリーは無傷)
6. `HEAD^{tree}` と `develop^{tree}` を比較し、switch してもファイルが変わらないことを保証
7. `git switch develop`
8. 切り替え前のブランチを削除(`-d`。squash merge で拒否された場合のみ `-D`。
   手順6で内容の一致を確認済みなので失われるものはない)

## パラメータ

- `-Force`: 未コミットの変更があっても続行する(変更は develop 側へ持ち越される)
- `-Base <name>`: 切り替え先ブランチ。既定は `develop`
