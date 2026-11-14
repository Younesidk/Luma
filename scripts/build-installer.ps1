<#
.SYNOPSIS  Builds artifacts/installer/Luma-Setup-<version>.exe (self-contained, no .NET install needed).
.DESCRIPTION
  1. Publishes Luma self-contained for win-x64 into artifacts/Luma-win-x64.
  2. Compiles installer/Luma.iss with Inno Setup (ISCC.exe).
  Requires Inno Setup 6:  winget install JRSoftware.InnoSetup
.EXAMPLE   ./scripts/build-installer.ps1
#>
param(
    [string]$Iscc   # optional explicit path to ISCC.exe
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# --- locate Inno Setup ------------------------------------------------------
if (-not $Iscc) {
    $candidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
    $Iscc = $candidates | Select-Object -First 1
}
if (-not $Iscc) {
    throw "Inno Setup 6 was not found. Install it with:  winget install JRSoftware.InnoSetup  (then re-run this script)."
}

# --- version from Directory.Build.props --------------------------------------
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = "1.0.0" }

# --- publish ------------------------------------------------------------------
& (Join-Path $PSScriptRoot "publish.ps1") -Runtime win-x64 -SelfContained
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# --- compile installer --------------------------------------------------------
$source = Join-Path $root "artifacts/Luma-win-x64"
$out    = Join-Path $root "artifacts/installer"
& $Iscc "/DAppVersion=$version" "/DSourceDir=$source" "/DOutputDir=$out" (Join-Path $root "installer/Luma.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$exe = Join-Path $out "Luma-Setup-$version.exe"
Write-Host ""
Write-Host "Installer: $exe ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)"
