# ============================================================
#  発表用の静止画を撮る(機能の ON / OFF を並べて見せるため)
#
#    stills.ps1 ssao       … stills.json の name が ssao の1つだけ撮る
#    stills.ps1 -All       … 表の全部を撮る
#
#  record.ps1 で短い動画を無劣化で撮り、表の shots に書いた時刻の1コマを JPG に切り出す。
#  同じ動画の中で機能を切り替えるので、OFF と ON の構図はぴったり揃う。
#  出力は docs/presentation/media/stills/(スライドが読み込む。git にコミットする)。
# ============================================================
param(
    [string]$Name,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$presentation = Split-Path $tools -Parent
$tmp = Join-Path $presentation '.record\stills'
$outDir = Join-Path $presentation 'media\stills'

$table = Get-Content (Join-Path $tools 'stills.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($All) { $targets = $table }
elseif ($Name) { $targets = @($table | Where-Object { $_.name -eq $Name }) }
else { throw '撮るものを指定してください(例: stills.ps1 ssao / stills.ps1 -All)' }
if ($targets.Count -eq 0) { throw "stills.json に $Name がありません" }

New-Item -ItemType Directory -Force $tmp, $outDir | Out-Null

foreach ($entry in $targets) {
    # 1. 無劣化で撮る(record.ps1 が reference のコピーに録画を差し込む)
    & (Join-Path $tools 'record.ps1') $entry.name -SceneFile 'stills.json' -Format lossless -OutDir $tmp
    $video = Join-Path $tmp ([IO.Path]::ChangeExtension($entry.out, '.mkv'))

    # 2. 時刻ごとに1コマ切り出す。crop で一部を拡大するときは、既定では画素が見えるよう最近傍で広げる
    foreach ($shot in $entry.shots) {
        $out = Join-Path $outDir $shot.out
        $width = if ($shot.width) { $shot.width } else { 960 }
        # 拡大のしかたは flags で変えられる(既定: 切り出すときは neighbor、全体なら lanczos)
        $flags = if ($shot.flags) { $shot.flags } elseif ($shot.crop) { 'neighbor' } else { 'lanczos' }
        $filter = if ($shot.crop) { "crop=$($shot.crop),scale=${width}:-2:flags=$flags" } else { "scale=${width}:-2:flags=$flags" }
        ffmpeg -hide_banner -loglevel error -y -ss $shot.at -i $video -frames:v 1 -vf $filter -q:v 3 $out
        if ($LASTEXITCODE -ne 0) { throw "切り出しに失敗しました: $out" }
        $kb = [math]::Round((Get-Item $out).Length / 1KB)
        Write-Host "  $($shot.out)($kb KB)"
    }

    Remove-Item -Force $video
}

# 作業用の置き場を片付ける(.record ごと空なら消す)
Remove-Item -Recurse -Force $tmp
$work = Split-Path $tmp -Parent
if ((Test-Path $work) -and -not (Get-ChildItem $work)) { Remove-Item $work }
