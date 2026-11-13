<#
.SYNOPSIS  Publishes Luma into artifacts/Luma-<runtime>.
.EXAMPLE   ./scripts/publish.ps1
.EXAMPLE   ./scripts/publish.ps1 -Runtime linux-x64 -SelfContained
#>
param(
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)

$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts/Luma-$Runtime"
$selfContained = if ($SelfContained) { "true" } else { "false" }

dotnet publish (Join-Path $root "src/Luma.App") -c Release -r $Runtime --self-contained $selfContained -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Native debug symbols (SkiaSharp, HarfBuzz) are very large and not needed to run the app.
Get-ChildItem $output -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
Write-Host "Published to $output"
