# Builds a SoundLibert release: self-contained publish -> Velopack package
# (Setup.exe installer + full/delta update packages) -> optional upload to
# GitHub Releases, which is where the installed app looks for updates.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.2.0
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.2.0 -Upload
#
# -Upload needs $env:GITHUB_TOKEN (a token with "contents: write" on the repo).
# Installed apps read releases anonymously, so the releases must be PUBLIC.
#
# IMPORTANT: the pack id is "SoundLibertApp", NOT "SoundLibert": Velopack
# installs into %LocalAppData%\<packId> and deletes that folder on uninstall,
# while the user's data (database, settings, downloads) lives in
# %LocalAppData%\SoundLibert. They must never be the same folder.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Upload,
    [string]$RepoUrl = "https://github.com/VasilKotik/SoundLibert",
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "SoundLibert.UI\SoundLibert.UI.csproj"
$publishDir = Join-Path $root "dist\publish-$Rid"
$releaseDir = Join-Path $root "dist\releases"
$icon = Join-Path $root "SoundLibert.UI\Assets\app.ico"
$packId = "SoundLibertApp"

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version must be SemVer (e.g. 0.2.0 or 0.2.0-beta.1), got '$Version'"
}

Push-Location $root
try {
    # Fail before anything long starts, and before a release can be half made.
    if ($Upload -and -not $env:GITHUB_TOKEN) { throw "Set `$env:GITHUB_TOKEN to upload." }

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

    Write-Host "Running the tests..."
    dotnet test (Join-Path $root "SoundLibert.Domain.Tests") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }

    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    Write-Host "Publishing SoundLibert $Version ($Rid)..."
    dotnet publish $project -c Release -r $Rid --self-contained true `
        -p:Version=$Version -p:PublishSingleFile=false -p:PublishReadyToRun=true `
        -p:DebugType=none -p:DebugSymbols=false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

    # Symbol files come along with some NuGet packages (about 100 MB of them):
    # users need none, so they stay out of the installer.
    Get-ChildItem -Path $publishDir -Filter *.pdb -Recurse | Remove-Item -Force

    # Packages of an earlier run make vpk refuse the same or a lower version.
    if (Test-Path $releaseDir) { Remove-Item -Recurse -Force $releaseDir }
    New-Item -ItemType Directory -Force $releaseDir | Out-Null

    # Delta updates are built against the previous release's full package.
    # Without it (the first release, or a network problem) users simply download
    # the full package, so this is a warning and not a failure.
    if ($Upload) {
        Write-Host "Fetching the previous release for delta packages..."
        dotnet vpk download github --repoUrl $RepoUrl --token $env:GITHUB_TOKEN -o $releaseDir
        if ($LASTEXITCODE -ne 0) { Write-Warning "No previous release fetched ($LASTEXITCODE): this release has no delta package." }
    }

    Write-Host "Packing with Velopack..."
    # Release notes (shown on the GitHub release): docs\release-notes\<version>.md
    $notes = Join-Path $root "docs\release-notes\$Version.md"
    $notesArgs = @()
    if (Test-Path $notes) { $notesArgs = @("--releaseNotes", $notes) }
    dotnet vpk pack -u $packId -v $Version -r $Rid -p $publishDir -e "SoundLibert.UI.exe" `
        --packTitle "SoundLibert" --packAuthors "SoundLibert" -i $icon -o $releaseDir @notesArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed ($LASTEXITCODE)" }

    if ($Upload) {
        Write-Host "Uploading to GitHub Releases ($RepoUrl)..."
        dotnet vpk upload github --repoUrl $RepoUrl --token $env:GITHUB_TOKEN -o $releaseDir `
            --publish --releaseName "SoundLibert $Version" --tag "v$Version"
        if ($LASTEXITCODE -ne 0) { throw "vpk upload failed ($LASTEXITCODE)" }
    }

    Write-Host "Done. Installer: $releaseDir\$packId-win-Setup.exe"
}
finally {
    Pop-Location
}
