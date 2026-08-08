<#
.SYNOPSIS
    起点パスから上へ辿って一番近い .csproj を探し、dotnet run / dotnet build する。

.DESCRIPTION
    VSCode のタスクから呼ばれる想定。
    「エディタで開いているファイル」を起点にできるので、reference/DayXX でも work/ でも
    同じ1つのタスクで実行できる(Day が増えても設定を書き換えなくてよい)。

    プロジェクト直下でなくサブフォルダのファイルを開いていても、
    親を辿って .csproj を見つけるので動く。

.PARAMETER From
    起点。ファイルパスでもディレクトリパスでもよい。

.PARAMETER Root
    探索の上限。ここより上には辿らない(通常はリポジトリのルート)。

.PARAMETER Configuration
    Debug / Release。既定は Release。
    ソフトウェアラスタライザは Debug と Release で速度が大きく違うため、
    見た目やFPSを確認する用途では Release を既定にしている。

.PARAMETER BuildOnly
    実行せずビルドだけ行う。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$From,

    [string]$Root = "",

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$BuildOnly
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $From)) {
    Write-Host "起点が見つかりません: $From" -ForegroundColor Red
    Write-Host "エディタでファイルを開いた状態で実行するか、タスク「実行: reference の Day を指定」を使ってください。" -ForegroundColor Yellow
    exit 1
}

# 起点がファイルならその親ディレクトリから探し始める
$item = Get-Item -LiteralPath $From
if ($item.PSIsContainer) { $dir = $item } else { $dir = $item.Directory }

# 探索の上限を絶対パスに正規化しておく(比較のため)
$rootFull = ""
if ($Root -ne "" -and (Test-Path -LiteralPath $Root)) {
    $rootFull = (Get-Item -LiteralPath $Root).FullName
}

$project = $null
while ($null -ne $dir) {
    $found = Get-ChildItem -LiteralPath $dir.FullName -Filter *.csproj -File | Select-Object -First 1
    if ($null -ne $found) {
        $project = $found.FullName
        break
    }

    # ルートまで来たら打ち切り(リポジトリの外まで探しに行かない)
    if ($rootFull -ne "" -and $dir.FullName -eq $rootFull) { break }

    $dir = $dir.Parent
}

if ($null -eq $project) {
    Write-Host "$From から親を辿っても .csproj が見つかりませんでした。" -ForegroundColor Red
    Write-Host "プロジェクト内のファイル(例: reference/Day01/Program.cs)を開いた状態で実行してください。" -ForegroundColor Yellow
    exit 1
}

if ($BuildOnly) {
    Write-Host "> dotnet build $project -c $Configuration" -ForegroundColor Cyan
    & dotnet build $project -c $Configuration
}
else {
    Write-Host "> dotnet run --project $project -c $Configuration" -ForegroundColor Cyan
    # WinExe なのでウィンドウが閉じられるまでここで待つ。これが正常な挙動。
    & dotnet run --project $project -c $Configuration
}

exit $LASTEXITCODE
