<#
.SYNOPSIS  Publishes Luma (WinUI 3, unpackaged, self-contained) into artifacts/Luma-<runtime>.
.DESCRIPTION
  Luma is an unpackaged WinUI 3 app: both the .NET runtime and the Windows App SDK runtime are bundled
  (WindowsAppSDKSelfContained), so the output folder runs on a clean Windows 10 19041+ machine with nothing installed.
  The output folder is wiped first so files from an older build can never end up in a Velopack package.
.EXAMPLE   ./scripts/publish.ps1
#>
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    # Accepted for compatibility with older invocations; Luma is always published self-contained.
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts/Luma-$Runtime"

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish (Join-Path $root "src/Luma.App/Luma.App.csproj") -c $Configuration -r $Runtime --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Debug symbols (Windows App SDK, WinUI, CsWinRT) are large and not needed to run the app.
Get-ChildItem $output -Filter *.pdb -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

# --- sanity checks: catch a broken unpackaged WinUI publish here instead of in an installed copy ---------------------
# Luma.pri holds the compiled XAML (.xbf) of every view; without it the app fails on App.xaml at start-up.
$required = @("Luma.exe", "Luma.dll", "Luma.pri", "Microsoft.WinUI.dll", "Microsoft.WindowsAppRuntime.dll", "Velopack.dll")
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $output $_)) })
if ($missing) { throw "Publish output is incomplete, missing: $($missing -join ', ')" }

$stale = Get-ChildItem $output -Recurse -File | Where-Object { $_.Name -match '^(Avalonia|SkiaSharp|HarfBuzzSharp|libSkiaSharp|libHarfBuzzSharp)' }
if ($stale) { throw "Publish output still contains Avalonia/Skia files: $($stale.Name -join ', ')" }

$size = (Get-ChildItem $output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published to {0} ({1:N1} MB)" -f $output, $size)
exit 0
