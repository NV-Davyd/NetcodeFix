param(
    [string]$KeyPath = (Join-Path $PSScriptRoot "NetcodeFix_maykr.kmc"),
    [string]$Configuration = "Release",
    [string]$OutputDir = "D:\SteamLibrary\steamapps\common\CarX Drift Racing Online\kino\mods"
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "NetcodeFix.KSL.csproj"
$dllPath = Join-Path $PSScriptRoot "bin\$Configuration\net48\NetcodeFix.dll"
$maykrPath = Join-Path $PSScriptRoot "..\ksl_sdk\sdk\maykr.exe"

if (-not (Test-Path -LiteralPath $maykrPath)) {
    throw "maykr.exe was not found at: $maykrPath"
}

Write-Host "Building NetcodeFix KSL mod..."
dotnet build $projectPath -c $Configuration

if (-not (Test-Path -LiteralPath $dllPath)) {
    throw "Build completed, but mod DLL was not found at: $dllPath"
}

if (-not (Test-Path -LiteralPath $KeyPath)) {
    throw @"
KSL Maykr key is missing.

Expected:
$KeyPath

Register this as a PublicMod named NetcodeFix, generate/export a .kmc build key
from KSL developer tools, put it at that path, or run:
.\build_ksm.ps1 -KeyPath C:\path\to\NetcodeFix_maykr.kmc
"@
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "Packaging .ksm with Maykr..."
& $maykrPath $dllPath -c $KeyPath -o $OutputDir -np

Write-Host "Done. Check output folder:"
Write-Host $OutputDir
