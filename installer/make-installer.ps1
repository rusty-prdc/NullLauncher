<#
  make-installer.ps1 — сборка дистрибутива NullLauncher.
  1) publish.ps1 → publish\NullLauncher (self-contained win-x64)
  2) ZIP-архив → dist\NullLauncher-<ver>-win-x64.zip (всегда)
  3) Inno Setup → dist\NullLauncher-<ver>-Setup.exe (если установлен iscc)

  Использование:
    .\installer\make-installer.ps1
    .\installer\make-installer.ps1 -SkipPublish      # использовать готовый publish\
    .\installer\make-installer.ps1 -NoZip            # только exe-инсталлятор
#>
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$ver = ([xml](Get-Content NullLauncher.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $ver) { $ver = "1.0.0" }

$pub = Join-Path $root "publish\NullLauncher"
$exe = Join-Path $pub "NullLauncher.exe"

if (-not $SkipPublish) {
    & (Join-Path $root "publish.ps1") -Clean
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path $exe)) {
    Write-Host "СБОЙ: $exe не найден. Запустите .\publish.ps1" -ForegroundColor Red
    exit 1
}

$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

if (-not $NoZip) {
    $zip = Join-Path $dist "NullLauncher-$ver-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$pub\*" -DestinationPath $zip -CompressionLevel Optimal
    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "ZIP: $zip ($mb МБ)" -ForegroundColor Green
}

$iscc = Get-Command iscc -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($iscc) {
    $script = Join-Path $root "installer\NullLauncher.iss"
    Write-Host "Inno Setup: $script" -ForegroundColor DarkGray
    & $iscc $script
    if ($LASTEXITCODE -ne 0) { Write-Host "СБОЙ: ISCC код $LASTEXITCODE" -ForegroundColor Red; exit $LASTEXITCODE }
    Write-Host "Инсталлятор: dist\NullLauncher-$ver-Setup.exe" -ForegroundColor Green
} else {
    Write-Host "Inno Setup не найден — собран только ZIP. Установите Inno Setup 6 для создания Setup.exe." -ForegroundColor Yellow
}

Write-Host "Готово. Дистрибутивы в $dist" -ForegroundColor Cyan
