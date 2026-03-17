param(
  [switch]$KillApp = $true
)

$ErrorActionPreference = "Stop"

function Remove-IfExists($path) {
  if (Test-Path $path) {
    Write-Host "Removing $path"
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
  }
}

if ($KillApp) {
  Get-Process HazelInvoice -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping HazelInvoice PID $($_.Id)"
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
  }
}

# Fix common StaticWebAssets dev-manifest duplication issues.
Remove-IfExists ".\\bin"
Remove-IfExists ".\\obj"
Remove-IfExists ".\\wwwroot\\Identity"
Remove-IfExists ".\\wwwroot\\HazelInvoice.styles.css"

Write-Host "Done."

