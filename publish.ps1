<#
  publish.ps1 — самодостаточная (self-contained) сборка win-x64 → publish\NullLauncher.
  Использование:
    .\publish.ps1                      # Release self-contained (по умолчанию)
    .\publish.ps1 -SingleFile          # в один exe (стартует чуть дольше)
    .\publish.ps1 -FrameworkDependent  # меньше размер, требует .NET 8 на ПК
    .\publish.ps1 -Clean               # очистить каталог перед сборкой
#>
[CmdletBinding()]
param(
    [switch]$SingleFile,
    [switch]$FrameworkDependent,
    [switch]$Clean,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }

$proc = Get-Process NullLauncher -ErrorAction SilentlyContinue
if ($proc) { $proc | Stop-Process -Force; Start-Sleep -Milliseconds 400 }

$outDir = Join-Path $root "publish\NullLauncher"
if ($Clean -and (Test-Path $outDir)) {
    Write-Host "Очищаю $outDir…" -ForegroundColor Yellow
    Remove-Item $outDir -Recurse -Force
}

$selfContained = if ($FrameworkDependent) { "false" } else { "true" }
$publishSingle  = if ($SingleFile) { "true" } else { "false" }

$args_ = @(
    "publish", "NullLauncher.csproj",
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", $selfContained,
    "-p:PublishSingleFile=$publishSingle",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=none",
    "-o", $outDir,
    "-v", "minimal", "--nologo"
)

Write-Host "dotnet $($args_ -join ' ')" -ForegroundColor DarkGray
& $dotnet @args_
if ($LASTEXITCODE -ne 0) {
    Write-Host "СБОЙ: publish завершился с кодом $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

$exe = Join-Path $outDir "NullLauncher.exe"
if (-not (Test-Path $exe)) {
    Write-Host "СБОЙ: $exe не найден" -ForegroundColor Red
    exit 1
}

$sizeMb = [math]::Round((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "ГОТОВО: $exe ($sizeMb МБ)" -ForegroundColor Green
Write-Host "Папка распространения: $outDir" -ForegroundColor Cyan
