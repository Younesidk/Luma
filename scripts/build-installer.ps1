<#
.SYNOPSIS
  Packages a Luma release with Velopack: installer, full + delta update packages and release metadata.

.DESCRIPTION
  1. Reads the version from Directory.Build.props (the single version source).
  2. Publishes Luma self-contained into artifacts/Luma-<runtime>.
  3. Downloads the latest *published* GitHub release into Releases/ (so Velopack can build a delta package from it).
  4. Runs `vpk pack`, which writes into Releases/:
       Luma.Desktop-win-Setup.exe        installer for new machines
       Luma.Desktop-<ver>-full.nupkg     full update package
       Luma.Desktop-<ver>-delta.nupkg    delta from the previous release (when one was found)
       releases.win.json  / RELEASES     release metadata that installed copies read
       assets.win.json                   list of files to upload (used by `vpk upload`)
       Luma.Desktop-win-Portable.zip     portable build (optional to upload)

  vpk is pinned in dotnet-tools.json and restored automatically (`dotnet tool restore`).
  Run on Windows for win-x64 and on Linux for linux-x64 (Velopack packages for the OS it runs on).

.PARAMETER ReleaseNotes
  Markdown file shown in Luma's update card. Defaults to release-notes/<version>.md when that file exists.

.PARAMETER OutputDir
  Where vpk writes its files. Defaults to Releases/. Use a separate folder (e.g. Releases-baseline) when Releases/
  already holds packages with a HIGHER version, because vpk refuses to pack a version lower than what is in the folder.

.PARAMETER SkipDelta
  Don't download the previous release; produce only the full package.

.EXAMPLE  ./scripts/build-installer.ps1
.EXAMPLE  ./scripts/build-installer.ps1 -ReleaseNotes release-notes/1.1.0.md
#>
param(
    [string]$Runtime = "win-x64",
    [string]$ReleaseNotes,
    [switch]$SkipDelta,
    [string]$OutputDir,
    [string]$RepoUrl = "https://github.com/Younesidk/Luma"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# IMPORTANT: never change PackId after the first Velopack release - it is the identity installed copies update against.
# It must also differ from "Luma": Velopack installs into %LocalAppData%\<PackId> and deletes that folder on uninstall,
# while Luma's user data (database, settings, downloads, thumbnails, logs) lives in %LocalAppData%\Luma.
$PackId = "Luma.Desktop"
if ($PackId -ieq "Luma") { throw "PackId must not be 'Luma': it would share (and on uninstall delete) the user data folder." }

$isWindowsTarget = $Runtime -like "win-*"
$channel = if ($isWindowsTarget) { "win" } else { ($Runtime -split "-")[0] }
$mainExe = if ($isWindowsTarget) { "Luma.exe" } else { "Luma" }

# --- version from Directory.Build.props ------------------------------------
$props = [xml](Get-Content (Join-Path $root "Directory.Build.props"))
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "No <Version> found in Directory.Build.props." }
Write-Host "Packaging Luma $version ($Runtime, channel '$channel')"

# --- tools ---------------------------------------------------------------------
Push-Location $root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

    # --- publish -------------------------------------------------------------------
    & (Join-Path $PSScriptRoot "publish.ps1") -Runtime $Runtime -SelfContained
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    $packDir = Join-Path $root "artifacts/Luma-$Runtime"

    $releases = if (-not $OutputDir) { Join-Path $root "Releases" }
                elseif ([IO.Path]::IsPathRooted($OutputDir)) { $OutputDir }
                else { Join-Path $root $OutputDir }
    New-Item -ItemType Directory -Force $releases | Out-Null

    # --- previous release (for delta packages) ----------------------------------
    if (-not $SkipDelta) {
        Write-Host "Fetching the latest published release from $RepoUrl for delta generation..."
        $dlArgs = @("vpk", "download", "github", "--repoUrl", $RepoUrl, "--channel", $channel, "--outputDir", $releases)
        # CI passes GITHUB_TOKEN to avoid anonymous rate limits; locally the public API works without one.
        if ($env:GITHUB_TOKEN) { $dlArgs += @("--token", $env:GITHUB_TOKEN) }
        if ($version -match "-") { $dlArgs += "--pre" }
        & dotnet @dlArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "No previous Velopack release could be downloaded (normal for the first release). Only a full package will be built."
        }
    }

    # Re-packing a version that already exists (locally or just downloaded) would be refused by vpk; drop only that version.
    Get-ChildItem $releases -Filter "$PackId-$version-*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force

    # --- release notes -------------------------------------------------------------
    if (-not $ReleaseNotes) {
        $candidate = Join-Path $root "release-notes/$version.md"
        if (Test-Path $candidate) { $ReleaseNotes = $candidate }
    }

    # --- pack -------------------------------------------------------------------------
    $packArgs = @(
        "vpk", "pack",
        "--packId", $PackId,
        "--packVersion", $version,
        "--packDir", $packDir,
        "--mainExe", $mainExe,
        "--packTitle", "Luma",
        "--packAuthors", "Luma",
        "--channel", $channel,
        "--runtime", $Runtime,
        "--outputDir", $releases
    )
    if ($isWindowsTarget) {
        $packArgs += @("--icon", (Join-Path $root "src/Luma.App/Assets/luma.ico"), "--shortcuts", "Desktop,StartMenuRoot")
    }
    if ($ReleaseNotes) {
        Write-Host "Release notes: $ReleaseNotes"
        $packArgs += @("--releaseNotes", (Resolve-Path $ReleaseNotes).Path)
    }

    & dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Release files for $version are in $releases :"
Get-ChildItem $releases | Where-Object { $_.Name -like "*$version*" -or $_.Name -like "*.json" -or $_.Name -eq "RELEASES" -or $_.Name -like "*Setup*" -or $_.Name -like "*Portable*" } |
    ForEach-Object { Write-Host ("  {0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
Write-Host ""
Write-Host "Publish:  dotnet vpk upload github --repoUrl $RepoUrl --channel $channel --outputDir `"$releases`" --tag v$version --releaseName `"Luma $version`" --publish --token <token>"
