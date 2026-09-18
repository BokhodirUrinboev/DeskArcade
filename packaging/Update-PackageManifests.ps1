# Stamps a GitHub release into the Homebrew cask and the Scoop manifest: version, URLs and SHA256 digests.
#   .\packaging\Update-PackageManifests.ps1 -Version 1.4.0
#       downloads the release's macOS zips and Windows installers from GitHub and hashes them
#   .\packaging\Update-PackageManifests.ps1 -Version 1.4.0 -AssetDir .\dist
#       hashes local files instead (they must be the exact files attached to the release)
#
# Reads packaging\homebrew\deskarcade.rb and packaging\scoop\deskarcade.json and writes the stamped copies to
# dist\homebrew and dist\scoop (or -OutDir). The templates keep their own version and placeholder hashes.
# Publishing them (a tap or homebrew/cask; a Scoop bucket) is a manual step: see docs/RELEASING.md.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$AssetDir,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')" }
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '..\dist' }
$base = "https://github.com/BokhodirUrinboev/DeskArcade/releases/download/v$Version"

function Get-AssetHash([string]$name) {
    if ($AssetDir) {
        $path = Join-Path $AssetDir $name
        if (-not (Test-Path $path)) { throw "$path not found" }
    }
    else {
        $path = Join-Path ([IO.Path]::GetTempPath()) $name
        Write-Host "Downloading $base/$name"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri "$base/$name" -OutFile $path -UseBasicParsing
    }
    $hash = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "$hash  $name"
    $hash
}

$utf8 = New-Object System.Text.UTF8Encoding $false

# Homebrew: one zip per architecture.
$cask = Get-Content -Raw (Join-Path $PSScriptRoot 'homebrew\deskarcade.rb')
$old = [regex]::Match($cask, 'version "([^"]+)"').Groups[1].Value
$cask = $cask.Replace("version `"$old`"", "version `"$Version`"")
# only the 64-hex digests: "arch arm: "arm64", intel: "x64"" uses the same keys
$cask = [regex]::Replace($cask, 'arm:\s+"[0-9a-f]{64}"', "arm:   `"$(Get-AssetHash "DeskArcade-$Version-macos-arm64.zip")`"")
$cask = [regex]::Replace($cask, 'intel: "[0-9a-f]{64}"', "intel: `"$(Get-AssetHash "DeskArcade-$Version-macos-x64.zip")`"")
New-Item -ItemType Directory -Force (Join-Path $OutDir 'homebrew') | Out-Null
[IO.File]::WriteAllText((Join-Path $OutDir 'homebrew\deskarcade.rb'), $cask, $utf8)

# Scoop: the Inno Setup installers, which Scoop unpacks without running them.
# Edited as text, not through ConvertTo-Json, which would reflow the whole file in Windows PowerShell.
$scoop = Get-Content -Raw (Join-Path $PSScriptRoot 'scoop\deskarcade.json')
$old = [regex]::Match($scoop, '"version": "([^"]+)"').Groups[1].Value
$scoop = $scoop.Replace("`"version`": `"$old`"", "`"version`": `"$Version`"")
$scoop = $scoop.Replace("download/v$old/DeskArcade-Setup-$old-", "download/v$Version/DeskArcade-Setup-$Version-")
foreach ($arch in @(@('64bit', 'standalone'), @('arm64', 'arm64'))) {
    $hash = Get-AssetHash "DeskArcade-Setup-$Version-$($arch[1]).exe"
    $scoop = [regex]::Replace($scoop, "(`"$($arch[0])`": \{\s*`"url`": `"[^`"]+`",\s*`"hash`": `")[0-9a-f]{64}", "`${1}$hash")
}
if ($scoop -notmatch "`"version`": `"$([regex]::Escape($Version))`"") { throw 'Could not stamp the Scoop manifest' }
New-Item -ItemType Directory -Force (Join-Path $OutDir 'scoop') | Out-Null
[IO.File]::WriteAllText((Join-Path $OutDir 'scoop\deskarcade.json'), $scoop, $utf8)

Write-Host "Wrote $(Join-Path $OutDir 'homebrew\deskarcade.rb') and $(Join-Path $OutDir 'scoop\deskarcade.json')"
