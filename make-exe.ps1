# Builds a standalone NullLauncher.exe into the project root (next to the sources),
# so the launcher can be started without going into bin\Debug\net8.0-windows.
# The exe is single-file and framework-dependent (needs the installed .NET 8 Desktop Runtime).
#
# ВАЖНО: не включаем Include*ForSelfExtract — эти флаги заставляют .NET распаковывать
# содержимое exe в %TEMP%\.net при первом запуске, а на этой машине Windows отклоняет
# создание таких папок (ошибка "Failed to create directory ..."), из-за чего exe из
# корня проекта вообще не стартовал. Без этих флагов single-file запускается напрямую
# из bundle, без записи во временные папки.
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "NullLauncher.exe"
$tmp = Join-Path $root "obj\publish-exe"

# dotnet может отсутствовать в PATH (например, в новых сессиях) — ищем по стандартным путям
# и выбираем тот, где реально установлен SDK (x86-копия dotnet без SDK нам не подходит)
$candidates = @()
$cmd = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if ($cmd) { $candidates += $cmd }
$candidates += @("C:\Program Files\dotnet\dotnet.exe", "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "C:\Program Files (x86)\dotnet\dotnet.exe")
$dotnet = $null
foreach ($cand in $candidates) {
    if ((Test-Path $cand) -and (& $cand --list-sdks 2>$null | Where-Object { $_ -match '^8\.' })) { $dotnet = $cand; break }
}
if (-not $dotnet) { throw ".NET 8 SDK not found (dotnet in PATH or Program Files)" }

Write-Host "Publishing single-file exe ($Configuration) with $dotnet ..."
& $dotnet publish (Join-Path $root "NullLauncher.csproj") -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=none -p:GenerateDocumentationFile=false -o $tmp
if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $tmp "NullLauncher.exe") $exe -Force

# Native-библиотеки НЕ входят в bundle (без IncludeNativeLibrariesForSelfExtract) и
# должны лежать рядом с exe: e_sqlite3.dll — движок SQLite, WebView2Loader.dll — загрузчик
# WebView2. Без них приложение стартует, но падает с
# "The type initializer for 'Microsoft.Data.Sqlite.SqliteConnection' threw an exception".
# UI/ в корне проекта уже существует (исходники, publish копирует те же 12 файлов).
$binOut = Join-Path $root "bin\$Configuration\net8.0-windows\win-x64"
foreach ($native in @("e_sqlite3.dll", "WebView2Loader.dll")) {
    $src = @( (Join-Path $tmp $native), (Join-Path $binOut $native) ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($src) { Copy-Item $src (Join-Path $root $native) -Force }
    else { Write-Warning "Native lib not found anywhere: $native" }
}

Write-Host ("Done: " + $exe + " (" + [math]::Round((Get-Item $exe).Length / 1MB, 1) + " MB)")
