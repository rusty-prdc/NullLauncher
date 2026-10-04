<#
  build.ps1 — быстрая сборка NullLauncher (Debug по умолчанию).
  Использование:
    .\build.ps1                 # Debug
    .\build.ps1 -Configuration Release
    .\build.ps1 -NoRestore
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoRestore,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = if (Test-Path "C:\Program Files\dotnet\dotnet.exe") { "C:\Program Files\dotnet\dotnet.exe" } else { "dotnet" }

# Запущенный лаунчер блокирует выходной файл — снимаем с редактирования
$proc = Get-Process NullLauncher -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Останавливаю запущенный NullLauncher…" -ForegroundColor Yellow
    $proc | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

$args_ = @("build", "NullLauncher.csproj", "-c", $Configuration, "-v", "minimal", "--nologo")
if ($NoRestore) { $args_ += "--no-restore" }

Write-Host "dotnet $($args_ -join ' ')" -ForegroundColor DarkGray
& $dotnet @args_
if ($LASTEXITCODE -ne 0) {
    Write-Host "СБОЙ: сборка завершилась с кодом $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

$out = Join-Path $root "bin\$Configuration\net8.0-windows\NullLauncher.exe"
Write-Host "ГОТОВО: $out" -ForegroundColor Green

# Уровень целостности файлов сборки. Если проект собирался внутри ограниченной среды,
# файлы получают метку Low — и тогда лаунчер (процесс с низким уровнем) не может писать
# в свою папку данных в %APPDATA% (SQLite Error 14 / Access denied). Возвращаем нормальный Medium.
try {
    $binDir = Join-Path $root "bin"
    if (Test-Path $binDir) {
        & icacls $binDir /setintegritylevel '(OI)(CI)M' /T 2>&1 | Out-Null
        Write-Host "Уровень целостности файлов сборки: Medium (обычный для пользователя)" -ForegroundColor DarkGray
    }
}
catch { Write-Host "Не удалось выставить уровень целостности (не критично): $($_.Exception.Message)" -ForegroundColor Yellow }

if ($Run) {
    Write-Host "Запуск…" -ForegroundColor Cyan
    Start-Process -FilePath $out
}
