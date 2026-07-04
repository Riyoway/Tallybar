#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Dev helper for Tallybar: stop any running instance (it locks the exe), build, then run.

.EXAMPLE
    ./scripts/dev.ps1            # build Debug and launch the app
    ./scripts/dev.ps1 -Probe     # build and print each provider's usage, no UI
    ./scripts/dev.ps1 -Release   # build/run the Release configuration
#>
param(
    [switch]$Probe,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'src/Tallybar.Strip/Tallybar.Strip.csproj'
$config = if ($Release) { 'Release' } else { 'Debug' }

# A running instance holds Tallybar.exe open, which fails the build's file copy.
Get-Process Tallybar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

dotnet build $proj -c $config --nologo
$exe = Join-Path $root "src/Tallybar.Strip/bin/$config/net10.0-windows/Tallybar.exe"

if ($Probe) {
    & $exe --probe
} else {
    Start-Process $exe
    "Launched $exe"
}
