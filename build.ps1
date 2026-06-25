# Builds NetUsage Monitor into a single self-contained .exe in dist\NetUsageMonitor.exe
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "==> Generating icon..."
& (Join-Path $root "tools\generate-icon.ps1")

Write-Host "==> Publishing ($Configuration, single-file, self-contained)..."
dotnet publish (Join-Path $root "src\NetUsageMonitor\NetUsageMonitor.csproj") -c $Configuration

$pub = Join-Path $root "src\NetUsageMonitor\bin\$Configuration\net8.0-windows\win-x64\publish\NetUsageMonitor.exe"
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $pub (Join-Path $dist "NetUsageMonitor.exe") -Force

$mb = [math]::Round((Get-Item (Join-Path $dist "NetUsageMonitor.exe")).Length / 1MB, 1)
Write-Host "==> Done: dist\NetUsageMonitor.exe ($mb MB)"
Write-Host "    Right-click -> Run as administrator (needed to read per-app network usage)."
