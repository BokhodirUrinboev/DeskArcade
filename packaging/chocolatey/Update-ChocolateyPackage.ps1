# Stamps a GitHub release into the Chocolatey package: version, installer URLs and SHA256 digests, then packs it.
#   .\packaging\chocolatey\Update-ChocolateyPackage.ps1 -Version 1.9.0
#       downloads the release's x64 standalone and ARM64 installers from GitHub and hashes them
#   .\packaging\chocolatey\Update-ChocolateyPackage.ps1 -Version 1.9.0 -InstallerDir .\installer\Output
#       hashes local installers instead (they must be the exact files attached to the release)
#
# Copies packaging\chocolatey to dist\chocolatey (or -OutDir), stamps the copy, and runs "choco pack" there when
# Chocolatey is installed, leaving deskarcade.<version>.nupkg next to it. The template keeps the release last stamped
# into it. After each release, .github/workflows/packages.yml runs this and pushes the package: see docs/RELEASING.md.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$InstallerDir,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')" }
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '../../dist/chocolatey' }
$base = "https://github.com/BokhodirUrinboev/DeskArcade/releases/download/v$Version"

function Get-AssetHash([string]$name) {
    if ($InstallerDir) {
        $path = Join-Path $InstallerDir $name
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
New-Item -ItemType Directory -Force (Join-Path $OutDir 'tools') | Out-Null

# The nuspec: its version and the release notes link.
$nuspec = Get-Content -Raw (Join-Path $PSScriptRoot 'deskarcade.nuspec')
$old = [regex]::Match($nuspec, '<version>([^<]+)</version>').Groups[1].Value
if (-not $old) { throw 'No <version> in deskarcade.nuspec' }
$nuspec = $nuspec.Replace("<version>$old</version>", "<version>$Version</version>")
$nuspec = $nuspec.Replace("/releases/tag/v$old<", "/releases/tag/v$Version<")
[IO.File]::WriteAllText((Join-Path $OutDir 'deskarcade.nuspec'), $nuspec, $utf8)

# The install script: both installer URLs and their digests. The uninstall script has nothing to stamp.
$install = Get-Content -Raw (Join-Path $PSScriptRoot 'tools/chocolateyinstall.ps1')
$install = $install.Replace("/download/v$old/DeskArcade-Setup-$old-", "/download/v$Version/DeskArcade-Setup-$Version-")
foreach ($pair in @(@('checksum64', 'standalone'), @('checksumArm64', 'arm64'))) {
    $hash = Get-AssetHash "DeskArcade-Setup-$Version-$($pair[1]).exe"
    $install = [regex]::Replace($install, "(?m)^(\`$$($pair[0])\s*=\s*')[0-9a-f]{64}'", "`${1}$hash'")
    if (-not $install.Contains("'$hash'")) { throw "Could not stamp `$$($pair[0]) in chocolateyinstall.ps1" }
}
if (-not $install.Contains("$base/DeskArcade-Setup-$Version-standalone.exe")) { throw 'Could not stamp the installer URLs' }
[IO.File]::WriteAllText((Join-Path $OutDir 'tools/chocolateyinstall.ps1'), $install, $utf8)
Copy-Item (Join-Path $PSScriptRoot 'tools/chocolateyuninstall.ps1') (Join-Path $OutDir 'tools') -Force

$OutDir = (Resolve-Path $OutDir).Path
Write-Host "Package source: $OutDir"

if (Get-Command choco -ErrorAction SilentlyContinue) {
    choco pack (Join-Path $OutDir 'deskarcade.nuspec') --outputdirectory $OutDir --limit-output
    if ($LASTEXITCODE -ne 0) { throw "choco pack failed ($LASTEXITCODE)" }
    Write-Host "Package: $(Join-Path $OutDir "deskarcade.$Version.nupkg")"
}
