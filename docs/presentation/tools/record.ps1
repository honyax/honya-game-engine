# ============================================================
#  発表用の動画を撮る(recording.md の方式 B)
#
#    record.ps1 day40      … scenes.json の name が day40 の1本だけ撮る
#    record.ps1 -All       … 表の全部を撮る
#    -Format h264 | vp9 | lossless … 書き出しの形式(既定 h264)。lossless は比較用の無劣化
#    -OutDir <フォルダ>     … 出力先を media/ 以外にする(比較用)
#    -SceneFile <ファイル名> … 撮影の表を替える(既定 scenes.json。静止画は stills.ps1 が stills.json で呼ぶ)
#
#  reference/DayXX には触らない。docs/presentation/.record/DayXX にコピーし、
#  そこへ Recorder.Silk.cs を足してビルド・撮影し、撮り終えたらコピーを消す。
#  出力は docs/presentation/media/(スライドが読み込む。git にコミットする)。
# ============================================================
param(
    [string]$Name,
    [switch]$All,
    [switch]$Keep,  # 撮影後も作業用コピーを残す(調べたいとき用)
    [ValidateSet('h264', 'vp9', 'lossless')][string]$Format = 'h264',
    [string]$OutDir,
    [string]$SceneFile = 'scenes.json'
)

$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$presentation = Split-Path $tools -Parent
$root = Split-Path (Split-Path $presentation -Parent) -Parent
$work = Join-Path $presentation '.record'
$media = if ($OutDir) { $OutDir } else { Join-Path $presentation 'media' }

# 形式ごとの拡張子と、ffmpeg の出力側の引数
$formats = @{
    h264     = @{ ext = '.mp4'; codec = @('-c:v', 'libx264', '-preset', 'medium', '-crf', '20', '-pix_fmt', 'yuv420p', '-movflags', '+faststart') }
    vp9      = @{ ext = '.webm'; codec = @('-c:v', 'libvpx-vp9', '-crf', '28', '-b:v', '0', '-row-mt', '1', '-deadline', 'good', '-cpu-used', '2', '-pix_fmt', 'yuv420p') }
    lossless = @{ ext = '.mkv'; codec = @('-c:v', 'libx264', '-preset', 'ultrafast', '-qp', '0', '-pix_fmt', 'yuv444p') }
}
$fmt = $formats[$Format]

$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw 'ffmpeg が PATH にありません' }
$ffprobe = Join-Path (Split-Path $ffmpeg -Parent) 'ffprobe.exe'

$scenes = Get-Content (Join-Path $tools $SceneFile) -Raw -Encoding UTF8 | ConvertFrom-Json
if ($All) { $targets = $scenes }
elseif ($Name) { $targets = @($scenes | Where-Object { $_.name -eq $Name }) }
else { throw '撮るものを指定してください(例: record.ps1 day40 / record.ps1 -All)' }
if ($targets.Count -eq 0) { throw "$SceneFile に $Name がありません" }

New-Item -ItemType Directory -Force $media | Out-Null

foreach ($scene in $targets) {
    $day = $scene.day
    $src = Join-Path $root "reference\$day"
    $dst = Join-Path $work $day
    Write-Host "=== $($scene.name)($day)==="

    # 1. コピー(前回の残りは消してから)。bin / obj は持ってこない
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Force $dst | Out-Null
    robocopy $src $dst /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "コピーに失敗しました($LASTEXITCODE)" }

    # 2. 録画の差し込みを置く(SDK 形式の csproj はフォルダ内の .cs を自動で拾う)。
    #    Day 10 までは WinForms、Day 14 以降は Silk.NET の窓なので、csproj を見て選ぶ
    $csproj = Join-Path $dst "$day.csproj"
    $kind = if (Select-String -Path $csproj -Pattern '<UseWindowsForms>true' -Quiet) { 'WinForms' } else { 'Silk' }
    Copy-Item (Join-Path $tools 'Recorder.Common.cs') (Join-Path $dst 'PresentationRecorder.Common.cs')
    Copy-Item (Join-Path $tools "Recorder.$kind.cs") (Join-Path $dst "PresentationRecorder.$kind.cs")

    # 3. Release でビルド(FPS を見せるので Debug では撮らない)
    dotnet build $csproj -c Release -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "ビルドに失敗しました: $day" }
    $exe = Join-Path $dst "bin\Release\net10.0-windows\$day.exe"
    if (-not (Test-Path $exe)) { $exe = Join-Path $dst "bin\Release\net10.0\$day.exe" }

    # 4. 撮影の表の1行を JSON にして渡す
    # 絵が細かくて重くなる場面(1 万枚のスプライトなど)は、表の crf で画質を少し落として大きさを抑える
    $codec = @($fmt.codec)
    if ($scene.crf -and $Format -ne 'lossless') { $codec[[array]::IndexOf($codec, '-crf') + 1] = [string]$scene.crf }

    $out = Join-Path $media ([IO.Path]::ChangeExtension($scene.out, $fmt.ext))
    $clip = [ordered]@{
        out     = $out
        warmup  = $scene.warmup
        seconds = $scene.seconds
        fps     = 30
        width   = 1280
        height  = 720
        ffmpeg  = $ffmpeg
        keys    = @($scene.keys)
        codec   = $codec
        crop    = $scene.crop
    }
    $clipPath = Join-Path $dst 'record.json'
    $clip | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 $clipPath

    if (Test-Path $out) { Remove-Item -Force $out }
    $env:HONYA_RECORD = $clipPath
    try {
        # アプリのコンソール出力(切り替えのたびに状態を書く)を残す。-Keep のとき、手順の確かめに使える
        $proc = Start-Process -FilePath $exe -WorkingDirectory $dst -PassThru `
            -RedirectStandardOutput (Join-Path $dst 'stdout.log') -RedirectStandardError (Join-Path $dst 'stderr.log')
        $limit = [int]($scene.warmup + $scene.seconds + 90)
        if (-not $proc.WaitForExit($limit * 1000)) {
            Stop-Process -Id $proc.Id -Force
            throw "時間内に終わりませんでした($limit 秒)"
        }
    }
    finally {
        Remove-Item Env:\HONYA_RECORD -ErrorAction SilentlyContinue
    }

    # 5. できたものを確かめる
    if (-not (Test-Path $out)) { throw "動画ができていません: $out" }
    $info = & $ffprobe -v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate:format=duration -of compact=p=0:nk=0 $out
    $mb = [math]::Round((Get-Item $out).Length / 1MB, 2)
    Write-Host "できました: $out($mb MB)"
    Write-Host "  $($info -join ' / ')"

    # 6. 作業用コピーを消して元どおりにする(reference には最初から触れていない)
    if (-not $Keep) { Remove-Item -Recurse -Force $dst }
}

if (-not $Keep -and (Test-Path $work) -and -not (Get-ChildItem $work)) { Remove-Item $work }
