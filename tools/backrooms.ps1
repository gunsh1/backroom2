# Backrooms プロジェクトのビルド／キャプチャ実行スクリプト
#
#   .\tools\backrooms.ps1 setup      HDRP + DXR の設定をやり直す
#   .\tools\backrooms.ps1 materials  マテリアルを作り直す
#   .\tools\backrooms.ps1 scene      サンドボックスシーンを作り直す
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

function Invoke-Unity {
    param([string]$Method, [string]$LogName, [switch]$Gui)

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
    return $p.ExitCode
}

switch ($Task) {
    "setup"     { Invoke-Unity "Backrooms.EditorTools.BackroomsSetup.ConfigureAll" "unity_setup.log" | Out-Null }
    "materials" { Invoke-Unity "Backrooms.EditorTools.MaterialBuilder.BuildAll"    "unity_mat.log"   | Out-Null }
    "scene"     { Invoke-Unity "Backrooms.EditorTools.SceneBuilder.Build"          "unity_scene.log" | Out-Null }
    "capture"   { Invoke-Unity "Backrooms.EditorTools.CaptureTool.PlayAndCapture"  "unity_capture.log" -Gui | Out-Null }
    "open" {
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
    default { throw "unknown task: $Task  (open|setup|materials|scene|capture|all)" }
}

foreach ($r in @("setup_report.txt", "material_report.txt", "scene_report.txt", "capture_report.txt")) {
    $p = Join-Path $Artifacts $r
    if (Test-Path $p) { Write-Host "report: $p" -ForegroundColor DarkGray }
}
Write-Host "done." -ForegroundColor Green
