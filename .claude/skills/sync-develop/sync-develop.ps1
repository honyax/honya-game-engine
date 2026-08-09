<#
.SYNOPSIS
    マージ済みの作業ブランチから develop へ、作業ツリーを一切変えずに切り替える。

.DESCRIPTION
    従来の「switch してから pull」だと、切り替え直後の一瞬だけファイルが古い状態
    (新規ファイルが消える・編集が巻き戻る)になる。
    このスクリプトは先にローカル develop の ref だけを進めてから switch するので、
    その中間状態が発生しない。

    判断はすべてこのスクリプト内で行い、結果を終了コードで返す。
    呼び出し側(スキル)は終了コードだけで分岐すればよい。

    終了コード:
      0  正常終了
      1  現在のブランチが develop / detached HEAD
      2  未プッシュのコミットがある、または fetch 失敗
      3  ローカル develop を fast-forward できない
      4  develop の内容が現在のブランチと一致しない
      5  switch 失敗
      6  ブランチ削除失敗
     10  未コミットの変更があるため確認が必要(-Force を付けて再実行すれば続行)

.PARAMETER Force
    未コミットの変更・新規ファイルがあっても続行する。
    switch 時にそれらは develop 側へそのまま持ち越される。

.PARAMETER Base
    切り替え先のブランチ。既定は develop。
#>
[CmdletBinding()]
param(
    [switch]$Force,

    [string]$Base = 'develop'
)

$ErrorActionPreference = 'Stop'
# PS 5.1 は既定でコンソールコードページに従うため、UTF-8 を明示しないと
# 日本語メッセージがリダイレクト先で文字化けする
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Write-Fail {
    param([int]$Code, [string]$Message)
    Write-Output "[ERROR] $Message"
    exit $Code
}

# --- 0. git リポジトリか ---
$null = & git rev-parse --git-dir
if ($LASTEXITCODE -ne 0) { Write-Fail 1 "git リポジトリではありません。" }

# --- 1. 現在のブランチをチェック ---
$branch = (& git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -eq 'HEAD') {
    Write-Fail 1 "detached HEAD 状態です。先にブランチをチェックアウトしてください。"
}
if ($branch -eq $Base) {
    Write-Fail 1 "現在のブランチが $Base です。作業ブランチに居るときに実行してください。"
}
Write-Output "[INFO] 現在のブランチ: $branch"

# --- 2. リモートの最新状態を取得(作業ツリーには触れない) ---
#     未プッシュ判定を正しく行うため、判定より先に fetch する
& git fetch --prune origin
if ($LASTEXITCODE -ne 0) { Write-Fail 2 "git fetch --prune origin に失敗しました。" }

# --- 3. 未プッシュのコミットがないか ---
$remoteSha = & git rev-parse --verify --quiet "refs/remotes/origin/$branch"
if ($remoteSha) {
    $ahead = (& git rev-list --count "origin/$branch..HEAD").Trim()
    if ([int]$ahead -gt 0) {
        Write-Fail 2 "未プッシュのコミットが $ahead 件あります。先に push してください。"
    }
    Write-Output "[INFO] origin/$branch と同期済み"
}
else {
    # PR マージ時に GitHub 側でブランチが削除されたケース。
    # HEAD が origin/$Base に含まれていれば、内容はリモートに存在する
    & git merge-base --is-ancestor HEAD "origin/$Base"
    if ($LASTEXITCODE -ne 0) {
        Write-Fail 2 "origin/$branch が存在せず、HEAD は origin/$Base にも含まれていません。未プッシュのコミットがあります。"
    }
    Write-Output "[INFO] origin/$branch は削除済み(マージ済みのため問題なし)"
}

# --- 4. 未コミットの変更・新規ファイルの確認 ---
$dirty = & git status --porcelain
if ($dirty -and -not $Force) {
    Write-Output "[CONFIRM] 未コミットの変更または新規ファイルがあります:"
    $dirty | ForEach-Object { Write-Output "  $_" }
    exit 10
}
if ($dirty) {
    Write-Output "[INFO] 未コミットの変更を持ち越して続行します(-Force)"
}

# --- 5. ローカル $Base を最新化 ---
#     チェックアウトしていないブランチの ref を直接書き換えるだけなので作業ツリーは無傷。
#     refspec に + を付けない = fast-forward 限定(非FFならここで失敗する)
$beforeSha = & git rev-parse --verify --quiet --short $Base
& git fetch origin "${Base}:${Base}"
if ($LASTEXITCODE -ne 0) {
    Write-Fail 3 "ローカル $Base を fast-forward できませんでした。$Base に直接コミットしている可能性があります。"
}
$afterSha = (& git rev-parse --short $Base).Trim()

# --- 6. $Base の内容が現在のブランチと同一か ---
#     tree が一致していれば switch してもファイルは1バイトも変わらない
$headTree = (& git rev-parse "HEAD^{tree}").Trim()
$baseTree = (& git rev-parse "$Base^{tree}").Trim()
if ($headTree -ne $baseTree) {
    Write-Output "[ERROR] $Base の内容が現在のブランチと一致しません。PR がまだマージされていない可能性があります。"
    Write-Output "[ERROR] 差分:"
    & git diff --stat HEAD $Base
    exit 4
}
Write-Output "[INFO] $Base と作業ツリーの内容が一致(tree $headTree)"

# --- 7. $Base に switch ---
& git switch $Base
if ($LASTEXITCODE -ne 0) { Write-Fail 5 "git switch $Base に失敗しました。" }

# --- 8. 切り替え前のブランチを削除 ---
& git branch -d $branch
if ($LASTEXITCODE -ne 0) {
    # squash merge の場合、内容は同じでもコミットの祖先関係がないため -d は拒否される。
    # 手順6で tree の一致を確認済みなので、失われる内容はない
    & git branch -D $branch
    if ($LASTEXITCODE -ne 0) { Write-Fail 6 "ブランチ $branch の削除に失敗しました。" }
    Write-Output "[INFO] -d が拒否されたため -D で削除しました(内容は $Base と一致済み)"
}

if ($beforeSha) { $from = $beforeSha } else { $from = '(なし)' }
Write-Output "[OK] $Base に切り替え、$branch を削除しました。"
Write-Output "[RESULT] $Base $from -> $afterSha / deleted=$branch"
exit 0
