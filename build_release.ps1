param(
    [string]$KeyPath = (Join-Path $PSScriptRoot "NetcodeFix_maykr.kmc"),
    [string]$Configuration = "Release",
    [string]$GameModsDir = "D:\SteamLibrary\steamapps\common\CarX Drift Racing Online\kino\mods",
    [string]$ReleaseDir = (Join-Path $PSScriptRoot "build\release")
)

$ErrorActionPreference = "Stop"

$modName = "NetcodeFix"
$modFileName = "$modName.ksm"
$modSourcePath = Join-Path $PSScriptRoot "NetcodeFixMod.cs"
$modSource = Get-Content -LiteralPath $modSourcePath -Raw

if ($modSource -notmatch 'KSLMeta\("NetcodeFix",\s*"(?<version>[^"]+)"') {
    throw "Could not read NetcodeFix version from KSLMeta in: $modSourcePath"
}

$version = $Matches.version
$buildScript = Join-Path $PSScriptRoot "build_ksm.ps1"
$builtKsm = Join-Path $GameModsDir $modFileName
$stageDir = Join-Path $ReleaseDir "stage"
$archivePath = Join-Path $ReleaseDir ("{0}_{1}_win.zip" -f $modName, $version)

Write-Host "Building installed KSL package..."
& $buildScript -KeyPath $KeyPath -Configuration $Configuration -OutputDir $GameModsDir

if (-not (Test-Path -LiteralPath $builtKsm)) {
    throw "Expected packaged mod was not found at: $builtKsm"
}

$resolvedReleaseDir = [System.IO.Path]::GetFullPath($ReleaseDir)
$resolvedStageDir = [System.IO.Path]::GetFullPath($stageDir)
$resolvedProjectDir = [System.IO.Path]::GetFullPath($PSScriptRoot)

if (-not $resolvedReleaseDir.StartsWith($resolvedProjectDir, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "ReleaseDir must stay inside the project directory: $resolvedReleaseDir"
}

if (Test-Path -LiteralPath $stageDir) {
    if (-not $resolvedStageDir.StartsWith($resolvedReleaseDir, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected staging directory: $resolvedStageDir"
    }

    Remove-Item -LiteralPath $stageDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item -LiteralPath $builtKsm -Destination (Join-Path $stageDir $modFileName) -Force

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stageDir,
    $archivePath,
    [System.IO.Compression.CompressionLevel]::NoCompression,
    $false)

Write-Host "Release archive created:"
Write-Host $archivePath
Write-Host ""
Write-Host "GitHub release tag:"
Write-Host ("v{0}" -f $version)
