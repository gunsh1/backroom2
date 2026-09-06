# Backrooms プロジェクトのビルド／キャプチャ実行スクリプト
#
#   .\tools\backrooms.ps1 setup      HDRP + DXR の設定をやり直す
#   .\tools\backrooms.ps1 materials  マテリアルを作り直す
#   .\tools\backrooms.ps1 scene      乱数生成のシーンを作り直す
#   .\tools\backrooms.ps1 replica    参照画像と同じ構造の複製シーンを作る
#   .\tools\backrooms.ps1 infinite   無限に歩けるシーンを作る（VHS 付き）
#   .\tools\backrooms.ps1 capture    再生してスクリーンショットと fps を採取（GUI モード）
#   .\tools\backrooms.ps1 all        materials -> scene -> capture
#
# DXR はグラフィックスデバイスを要するため、capture だけは -batchmode を使わない。

param([Parameter(Position = 0)][string]$Task = "all")

$ErrorActionPreference = "Stop"
$Root     = Split-Path -Parent $PSScriptRoot
$Unity    = "C:\Program Files\Unity\Hub\Editor\6000.5.8f1\Editor\Unity.exe"
$Project  = Join-Path $Root "BackroomsRT"
$Artifacts= Join-Path $Root "artifacts"

if (-not (Test-Path $Unity))   { throw "Unity が見つからない: $Unity" }
if (-not (Test-Path $Project)) { throw "プロジェクトが見つからない: $Project" }
New-Item -ItemType Directory -Force $Artifacts | Out-Null

# エディタが開いたままだとバッチ実行はロックで落ちるが、後続のタスクは
# そのまま走ってしまい「変更が反映されていない古いキャプチャ」が返る。
# 一度これで誤った結論を出したので、先頭で必ず止める。
function Assert-NoEditor {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue |
                 Where-Object { $_.CommandLine -match [regex]::Escape($Project) -and $_.CommandLine -notmatch '-adb2' })
    if ($running.Count -gt 0) {
        $ids = ($running | ForEach-Object { $_.ProcessId }) -join ', '
        throw "Unity エディタがこのプロジェクトを開いています (PID $ids)。閉じてから実行してください。"
    }
}

function Invoke-Unity {
    param([string]$Method, [string]$LogName, [switch]$Gui)

    Assert-NoEditor
    $log = Join-Path $Artifacts $LogName
    $args = @("-projectPath", $Project)
    if (-not $Gui) { $args += @("-batchmode", "-nographics", "-quit") }
    $args += @("-executeMethod", $Method, "-logFile", $log)

    Write-Host "-> $Method" -ForegroundColor Cyan
    $p = Start-Process -FilePath $Unity -ArgumentList $args -Wait -PassThru -NoNewWindow
    $errors = @(Select-String -Path $log -Pattern "error CS" -ErrorAction SilentlyContinue)
    if ($errors.Count -gt 0) {
        Write-Host "コンパイルエラー $($errors.Count) 件:" -ForegroundColor Red
        $errors | Select-Object -First 15 | ForEach-Object { Write-Host "   $($_.Line)" }
        throw "compile failed"
    }
    Write-Host "   exit=$($p.ExitCode)  log=$log" -ForegroundColor DarkGray
    if ($p.ExitCode -ne 0) { throw "$Method が exit=$($p.ExitCode) で終了した。$log を確認すること。" }
    return $p.ExitCode
}

switch ($Task) {
    "setup"     { Invoke-Unity "Backrooms.EditorTools.BackroomsSetup.ConfigureAll" "unity_setup.log" | Out-Null }
    "materials" { Invoke-Unity "Backrooms.EditorTools.MaterialBuilder.BuildAll"    "unity_mat.log"   | Out-Null }
    "scene"     { Invoke-Unity "Backrooms.EditorTools.SceneBuilder.Build"          "unity_scene.log" | Out-Null }
    "replica"   { Invoke-Unity "Backrooms.EditorTools.SceneBuilder.BuildReplicaScene" "unity_replica.log" | Out-Null }
    "infinite"  { Invoke-Unity "Backrooms.EditorTools.SceneBuilder.BuildInfiniteScene" "unity_infinite.log" | Out-Null }
    "capture"   { Invoke-Unity "Backrooms.EditorTools.CaptureTool.PlayAndCapture"  "unity_capture.log" -Gui | Out-Null }
    "open" {
        Assert-NoEditor
        Write-Host "エディタを開く: $Project" -ForegroundColor Cyan
        Start-Process -FilePath $Unity -ArgumentList @("-projectPath", $Project)
        Write-Host "起動しました。" -ForegroundColor Green
        return
    }
    "all" {
        Invoke-Unity "Backrooms.EditorTools.MaterialBuilder.BuildAll"   "unity_mat.log"     | Out-Null
        Invoke-Unity "Backrooms.EditorTools.SceneBuilder.Build"         "unity_scene.log"   | Out-Null
        Invoke-Unity "Backrooms.EditorTools.CaptureTool.PlayAndCapture" "unity_capture.log" -Gui | Out-Null
    }
    default { throw "unknown task: $Task  (open|setup|materials|scene|replica|infinite|capture|all)" }
}

foreach ($r in @("setup_report.txt", "material_report.txt", "scene_report.txt", "capture_report.txt")) {
    $p = Join-Path $Artifacts $r
    if (Test-Path $p) { Write-Host "report: $p" -ForegroundColor DarkGray }
}
Write-Host "done." -ForegroundColor Green
