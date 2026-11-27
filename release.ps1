<#
.SYNOPSIS
  Builds, packages and (optionally) uploads a Luma release from this Windows machine. Replaces the old GitHub Actions
  release workflow.

.DESCRIPTION
  1. Checks the version and the git state.
  2. Cleans the previous publish output (artifacts/Luma-win-x64) and the Velopack output (Releases/).
  3. `vpk download github`: fetches the latest published release so vpk can build a delta package against it.
  4. Runs the test suite (skip with -SkipTests).
  5. `dotnet publish`: unpackaged WinUI 3, .NET and Windows App SDK both self-contained, win-x64.
  6. `vpk pack`: writes Setup.exe, the full and delta .nupkg and releases.win.json into Releases/.
  7. If $env:GITHUB_TOKEN is set (and -DryRun is not), `vpk upload github` creates the GitHub release and publishes it.

  Installed copies of Luma (GithubSource, stable only) pick the release up as soon as it is published.
  vpk is pinned in dotnet-tools.json; nothing else needs to be installed.

.PARAMETER Version
  Version to release, e.g. 2.0.0. A hyphen (2.1.0-beta.1) makes it a pre-release, which installed copies ignore.
  Should match <Version> in Directory.Build.props (you get a warning if it does not).

.PARAMETER DryRun
  Build and package only; never upload. Inspect Releases/ afterwards. Alias: -NoUpload.

.PARAMETER Draft
  Upload, but leave the GitHub release as a draft so you can check it before publishing it on GitHub.

.PARAMETER SkipTests
  Don't run `dotnet test` before packaging.

.EXAMPLE  ./release.ps1 -DryRun
.EXAMPLE  $env:GITHUB_TOKEN = "<token with contents:write>"; ./release.ps1
.EXAMPLE  ./release.ps1 -Version 2.0.1 -Draft
#>
[CmdletBinding()]
param(
    [string]$Version = "2.0.0",
    [Alias("NoUpload")]
    [switch]$DryRun,
    [switch]$Draft,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

# --- Project settings (taken from Luma.App.csproj and the old release workflow) --------------------------------------
$Root        = $PSScriptRoot
$Project     = Join-Path $Root "src/Luma.App/Luma.App.csproj"
$Runtime     = "win-x64"                                   # the only RID Luma supports (WinUI 3)
$PublishDir  = Join-Path $Root "artifacts/Luma-$Runtime"    # dotnet publish output = vpk --packDir
$ReleaseDir  = Join-Path $Root "Releases"                   # vpk output (git-ignored)
$RepoUrl     = "https://github.com/Younesidk/Luma"          # must match Luma:Updates:Source in appsettings.json
$Channel     = "win"
# NEVER change PackId: installed copies update against it. It must not be "Luma" either, because Velopack deletes
# %LocalAppData%\<PackId> on uninstall and Luma's user data lives in %LocalAppData%\Luma.
$PackId      = "Luma.Desktop"
$MainExe     = "Luma.exe"
$Icon        = Join-Path $Root "src/Luma.App/Assets/luma.ico"
$Tag         = "v$Version"

# --- Helpers ---------------------------------------------------------------------------------------------------------
function Write-Step([string]$Text) { Write-Host ""; Write-Host "==> $Text" -ForegroundColor Cyan }

# Runs a native command and throws a readable error when it exits with a non-zero code.
function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE). See the output above." }
}

Push-Location $Root
try {
    # --- 1. Checks ---------------------------------------------------------------------------------------------------
    Write-Step "Checking version $Version"
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "'$Version' is not a version like 2.0.0 or 2.1.0-beta.1." }
    $isPrerelease = $Version.Contains("-")

    $props = [xml](Get-Content (Join-Path $Root "Directory.Build.props"))
    $propsVersion = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($propsVersion -ne $Version) {
        Write-Warning "Directory.Build.props says <Version>$propsVersion</Version>. The build is stamped $Version anyway; bump the file so they match."
    }

    if (git status --porcelain) { Write-Warning "You have uncommitted changes; they will be part of this build." }
    $commit = (git rev-parse HEAD).Trim()
    Write-Host "Commit: $commit  Pre-release: $isPrerelease"

    Invoke-Checked "dotnet tool restore (vpk)" { dotnet tool restore | Out-Null }

    # --- 2. Clean ----------------------------------------------------------------------------------------------------
    Write-Step "Cleaning $PublishDir and $ReleaseDir"
    foreach ($dir in $PublishDir, $ReleaseDir) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    }
    New-Item -ItemType Directory -Force $ReleaseDir | Out-Null

    # --- 3. Previous release, for delta packages ---------------------------------------------------------------------
    Write-Step "Downloading the latest published release from $RepoUrl (for the delta package)"
    $dlArgs = @("vpk", "download", "github", "--repoUrl", $RepoUrl, "--channel", $Channel, "--outputDir", $ReleaseDir)
    if ($env:GITHUB_TOKEN) { $dlArgs += @("--token", $env:GITHUB_TOKEN) }   # optional: avoids anonymous rate limits
    if ($isPrerelease)     { $dlArgs += "--pre" }
    & dotnet @dlArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not download the previous release. Only a full package will be built (installed copies still update, just with a bigger download)."
    }
    # vpk refuses to pack a version that is already in the folder (e.g. when re-releasing); drop only that version.
    Get-ChildItem $ReleaseDir -Filter "$PackId-$Version-*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force

    # --- 4. Tests ----------------------------------------------------------------------------------------------------
    if ($SkipTests) {
        Write-Step "Skipping tests (-SkipTests)"
    } else {
        Write-Step "Running tests (Release)"
        Invoke-Checked "dotnet test" { dotnet test (Join-Path $Root "Luma.sln") -c Release }
    }

    # --- 5. Publish --------------------------------------------------------------------------------------------------
    Write-Step "Publishing Luma $Version ($Runtime, self-contained, unpackaged WinUI 3)"
    # Same flags as scripts/publish.ps1. WindowsAppSDKSelfContained bundles the Windows App SDK runtime, and
    # WindowsPackageType=None makes it an unpackaged (non-MSIX) app that Velopack can install.
    Invoke-Checked "dotnet publish" {
        dotnet publish $Project -c Release -r $Runtime --self-contained true `
            -p:WindowsAppSDKSelfContained=true -p:WindowsPackageType=None -p:Version=$Version -o $PublishDir
    }
    # Debug symbols are large and not needed to run the app.
    Get-ChildItem $PublishDir -Filter *.pdb -Recurse | Remove-Item -Force

    # Catch a broken WinUI publish here rather than in an installed copy. Luma.pri holds the compiled XAML.
    $required = "Luma.exe", "Luma.dll", "Luma.pri", "Microsoft.WinUI.dll", "Microsoft.WindowsAppRuntime.dll", "Velopack.dll"
    $missing = @($required | Where-Object { -not (Test-Path (Join-Path $PublishDir $_)) })
    if ($missing) { throw "Publish output is incomplete, missing: $($missing -join ', ')" }

    # --- 6. Pack -----------------------------------------------------------------------------------------------------
    Write-Step "Packing $PackId $Version with Velopack"
    $packArgs = @(
        "vpk", "pack",
        "--packId", $PackId,
        "--packVersion", $Version,
        "--packDir", $PublishDir,
        "--mainExe", $MainExe,
        "--packTitle", "Luma",
        "--packAuthors", "Luma",
        "--channel", $Channel,
        "--runtime", $Runtime,
        "--outputDir", $ReleaseDir,
        "--icon", $Icon,
        "--shortcuts", "Desktop,StartMenuRoot"
    )
    $notes = Join-Path $Root "release-notes/$Version.md"
    if (Test-Path $notes) { $packArgs += @("--releaseNotes", $notes) }
    else { Write-Warning "release-notes/$Version.md not found; the update card will show no notes." }
    Invoke-Checked "vpk pack" { dotnet @packArgs }

    $expected = "$PackId-win-Setup.exe", "$PackId-$Version-full.nupkg", "releases.$Channel.json"
    $missing = @($expected | Where-Object { -not (Test-Path (Join-Path $ReleaseDir $_)) })
    if ($missing) { throw "vpk pack did not produce: $($missing -join ', ')" }
    if (-not (Test-Path (Join-Path $ReleaseDir "$PackId-$Version-delta.nupkg"))) {
        Write-Warning "No delta package was built; installed copies will download the full package."
    }

    Write-Host ""
    Write-Host "Release files in ${ReleaseDir}:"
    Get-ChildItem $ReleaseDir | ForEach-Object { Write-Host ("  {0,-42} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

    # --- 7. Upload ---------------------------------------------------------------------------------------------------
    if ($DryRun) {
        Write-Step "Dry run: nothing uploaded. Run again without -DryRun (with GITHUB_TOKEN set) to release."
        return
    }
    if (-not $env:GITHUB_TOKEN) {
        Write-Step "GITHUB_TOKEN is not set: nothing uploaded."
        Write-Host "Set a token with 'contents: write' on Younesidk/Luma and run again:"
        Write-Host '  $env:GITHUB_TOKEN = "<token>"; ./release.ps1'
        return
    }

    # vpk refuses to upload into an existing release, and drafts are only visible with a token, so check first.
    Write-Step "Checking GitHub for an existing $Tag release"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12   # Windows PowerShell 5.1
    $apiRepo = $RepoUrl -replace '^https://github\.com/', ''
    $headers = @{ Authorization = "Bearer $env:GITHUB_TOKEN"; "User-Agent" = "luma-release-script"; Accept = "application/vnd.github+json" }
    try {
        $existing = @(Invoke-RestMethod "https://api.github.com/repos/$apiRepo/releases?per_page=100" -Headers $headers |
                      Where-Object { $_.tag_name -eq $Tag })
    } catch {
        throw "Could not list releases on GitHub (is GITHUB_TOKEN valid for $apiRepo?): $($_.Exception.Message)"
    }
    if ($existing.Count -gt 0) {
        $kind = if ($existing[0].draft) { "a DRAFT" } else { "a PUBLISHED" }
        throw "GitHub already has $kind release for $Tag (id $($existing[0].id)). Delete it on GitHub " +
              "(or: gh release delete $Tag --repo $apiRepo --yes) and run again. The git tag can stay."
    }

    $state = if ($Draft) { "as a draft" } else { "and publishing" }
    Write-Step "Uploading $Tag to $RepoUrl $state"
    $upArgs = @(
        "vpk", "upload", "github",
        "--repoUrl", $RepoUrl,
        "--channel", $Channel,
        "--outputDir", $ReleaseDir,
        "--tag", $Tag,
        "--targetCommitish", $commit,   # only used when the tag does not exist yet on GitHub
        "--releaseName", "Luma $Version",
        "--token", $env:GITHUB_TOKEN
    )
    if (-not $Draft)   { $upArgs += "--publish" }
    if ($isPrerelease) { $upArgs += "--pre" }
    Invoke-Checked "vpk upload github (if it created a partial draft, delete it on GitHub before retrying)" { dotnet @upArgs }

    Write-Step "Done: $RepoUrl/releases/tag/$Tag"
    if ($Draft) { Write-Host "The release is a draft. Publish it on GitHub when you have checked it." }
}
catch {
    Write-Host ""
    Write-Host "RELEASE FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    Pop-Location
}
