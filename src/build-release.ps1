# Puts the finished app into release\: cs2-debuffs.exe and data\debuffs.json - exactly what the
# user downloads straight from the repository. No archives.
# The script does NOT touch the app\ working folder - that holds your logs, journal and state.
# Requires .NET SDK 8; the user needs the .NET 8 Desktop Runtime (link in the README).
# Run: ./src/build-release.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src\DebuffRoulette.csproj'
$rel  = Join-Path $root 'release'
$tmp  = Join-Path $rel '.publish'

if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
New-Item -ItemType Directory -Force -Path (Join-Path $rel 'data') | Out-Null

Write-Host "Building..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $tmp | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $tmp 'cs2-debuffs.exe') (Join-Path $rel 'cs2-debuffs.exe') -Force
Copy-Item (Join-Path $root 'src\debuffs.json') (Join-Path $rel 'data\debuffs.json') -Force
Remove-Item -Recurse -Force $tmp

$kb = [math]::Round((Get-Item (Join-Path $rel 'cs2-debuffs.exe')).Length / 1KB)
Write-Host ""
Write-Host "Done: release\cs2-debuffs.exe ($kb KB) + release\data\debuffs.json" -ForegroundColor Green
Write-Host "That is the release: it lives in the repository and the user downloads it from there." -ForegroundColor Green
