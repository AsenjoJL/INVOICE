param(
  [string]$Runtime = "win-x64",
  [switch]$SelfContained = $true,
  [switch]$SingleFile = $true,
  [switch]$IncludeNativeLibrariesForSelfExtract = $true,
  [string]$NsisPath = "C:\\Program Files (x86)\\NSIS\\makensis.exe",
  [string]$NsisScript = ".\\Installer.nsi"
)

$ErrorActionPreference = "Stop"

Write-Host "Cleaning dev artifacts..."
& "$PSScriptRoot\\dev-clean.ps1" -KillApp:$true

Write-Host "Publishing..."
$publishArgs = @(
  "publish",
  "-c", "Release",
  "-r", $Runtime,
  "--self-contained", ($SelfContained.ToString().ToLowerInvariant()),
  "/p:PublishSingleFile=$($SingleFile.ToString().ToLowerInvariant())",
  "/p:IncludeNativeLibrariesForSelfExtract=$($IncludeNativeLibrariesForSelfExtract.ToString().ToLowerInvariant())"
)
dotnet @publishArgs

if (!(Test-Path $NsisPath)) {
  throw "NSIS not found at: $NsisPath"
}
if (!(Test-Path $NsisScript)) {
  throw "NSIS script not found at: $NsisScript"
}

Write-Host "Building installer with NSIS..."
& $NsisPath $NsisScript

Write-Host "Done."

