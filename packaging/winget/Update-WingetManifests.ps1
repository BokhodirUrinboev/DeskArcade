# Stamps a GitHub release into the winget manifests: version, installer URLs and SHA256 digests.
#   .\packaging\winget\Update-WingetManifests.ps1 -Version 1.3.0
#       downloads the release's installers from GitHub and hashes them
#   .\packaging\winget\Update-WingetManifests.ps1 -Version 1.3.0 -InstallerDir .\installer\Output
#       hashes local installers instead (they must be the exact files attached to the release)
#
# Reads the templates in packaging\winget\manifests and writes the stamped copies to
# dist\winget\<version> (or -OutDir), then runs "winget validate" on them when winget is available.
# The installer file names come from the template URLs, with the template version replaced.
# Submitting to winget-pkgs is a separate, manual step: see docs/RELEASING.md.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$InstallerDir,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')" }

$templates = Join-Path $PSScriptRoot 'manifests'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot "..\..\dist\winget\$Version" }

$installerTemplate = Join-Path $templates 'ImperiumGames.DeskArcade.installer.yaml'
$oldVersion = (Select-String -Path $installerTemplate -Pattern '^PackageVersion:\s*(\S+)').Matches[0].Groups[1].Value

# Architecture -> release URL for this version, from the installer template.
$urls = [ordered]@{}
$arch = $null
foreach ($line in Get-Content $installerTemplate) {
    if ($line -match '^\s*-\s*Architecture:\s*(\S+)') { $arch = $Matches[1] }
    elseif ($line -match '^\s*InstallerUrl:\s*(\S+)') { $urls[$arch] = $Matches[1].Replace($oldVersion, $Version) }
}
if ($urls.Count -eq 0) { throw "No installers found in $installerTemplate" }

$hashes = @{}
foreach ($arch in $urls.Keys) {
    $name = $urls[$arch].Split('/')[-1]
    if ($InstallerDir) {
        $path = Join-Path $InstallerDir $name
        if (-not (Test-Path $path)) { throw "$path not found" }
    }
    else {
        $path = Join-Path ([IO.Path]::GetTempPath()) $name
        Write-Host "Downloading $($urls[$arch])"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $urls[$arch] -OutFile $path -UseBasicParsing
    }
    $hashes[$arch] = (Get-FileHash $path -Algorithm SHA256).Hash
    Write-Host "$arch  $($hashes[$arch])  $name"
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false
foreach ($template in Get-ChildItem $templates -Filter '*.yaml') {
    $arch = $null
    $lines = foreach ($line in Get-Content $template.FullName) {
        if ($line -match '^\s*-\s*Architecture:\s*(\S+)') { $arch = $Matches[1] }
        if ($line -match '^# Template:' -or $line -match '^# packaging/winget/') { continue }
        switch -Regex ($line) {
            '^PackageVersion:' { "PackageVersion: $Version"; break }
            '^(\s*)InstallerUrl:' { "$($Matches[1])InstallerUrl: $($urls[$arch])"; break }
            '^(\s*)InstallerSha256:' { "$($Matches[1])InstallerSha256: $($hashes[$arch])"; break }
            '^ReleaseNotesUrl:' { $line.Replace($oldVersion, $Version); break }
            default { $line }
        }
    }
    [IO.File]::WriteAllText((Join-Path $OutDir $template.Name), (($lines -join "`n") + "`n"), $utf8)
}
$OutDir = (Resolve-Path $OutDir).Path
Write-Host "Manifests: $OutDir"

if (Get-Command winget -ErrorAction SilentlyContinue) {
    winget validate --manifest $OutDir
    if ($LASTEXITCODE -ne 0) { throw "winget validate failed ($LASTEXITCODE)" }
}
