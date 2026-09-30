# Stamps a GitHub release into the AUR package deskarcade-bin: pkgver, pkgrel and the .debs' SHA256 digests.
#   .\packaging\aur\Update-AurPackage.ps1 -Version 1.9.0
#       downloads the release's amd64 and arm64 .debs from GitHub and hashes them
#   .\packaging\aur\Update-AurPackage.ps1 -Version 1.9.0 -AssetDir .\dist-linux
#       hashes local files instead (they must be the exact files attached to the release)
#
# Reads packaging/aur/PKGBUILD and .SRCINFO and writes the stamped copies to dist/aur (or -OutDir); the templates keep
# the release last stamped into them. Both files are edited as text, so this runs anywhere PowerShell does; on Arch,
# "makepkg --printsrcinfo" must print the stamped .SRCINFO again, which packages.yml checks before it pushes.
# After each release, .github/workflows/packages.yml runs this and pushes the result to the AUR: see docs/RELEASING.md.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$AssetDir,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')" }
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '../../dist/aur' }
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

# Arch's names for the two architectures, and the .deb each one is built from.
$debs = [ordered]@{ x86_64 = "deskarcade_${Version}_amd64.deb"; aarch64 = "deskarcade_${Version}_arm64.deb" }
$hashes = @{}
foreach ($arch in $debs.Keys) { $hashes[$arch] = Get-AssetHash $debs[$arch] }

$pkgbuild = (Get-Content -Raw (Join-Path $PSScriptRoot 'PKGBUILD')).Replace("`r`n", "`n")
$srcinfo = (Get-Content -Raw (Join-Path $PSScriptRoot '.SRCINFO')).Replace("`r`n", "`n")
$old = [regex]::Match($pkgbuild, '(?m)^pkgver=(\S+)$').Groups[1].Value
if (-not $old) { throw 'No pkgver= line in PKGBUILD' }

# A new version starts again at pkgrel 1. For the version already in the template, its pkgrel stands: to ship a fix to
# the PKGBUILD alone, raise pkgrel in both files and run the Package managers workflow with the same version.
$pkgrel = if ($old -eq $Version) { [regex]::Match($pkgbuild, '(?m)^pkgrel=(\S+)$').Groups[1].Value } else { '1' }

# PKGBUILD: the source URLs are written with ${pkgver}, so only pkgver, pkgrel and the digests change.
$pkgbuild = [regex]::Replace($pkgbuild, '(?m)^pkgver=.*$', "pkgver=$Version")
$pkgbuild = [regex]::Replace($pkgbuild, '(?m)^pkgrel=.*$', "pkgrel=$pkgrel")
# .SRCINFO has every value spelled out, the URLs included.
$srcinfo = [regex]::Replace($srcinfo, '(?m)^(\tpkgver = ).*$', "`${1}$Version")
$srcinfo = [regex]::Replace($srcinfo, '(?m)^(\tpkgrel = ).*$', "`${1}$pkgrel")
$srcinfo = $srcinfo.Replace("/download/v$old/deskarcade_${old}_", "/download/v$Version/deskarcade_${Version}_")
foreach ($arch in $debs.Keys) {
    $pkgbuild = [regex]::Replace($pkgbuild, "(?m)^sha256sums_$arch=\('[0-9a-f]{64}'\)$", "sha256sums_$arch=('$($hashes[$arch])')")
    $srcinfo = [regex]::Replace($srcinfo, "(?m)^(\tsha256sums_$arch = )[0-9a-f]{64}$", "`${1}$($hashes[$arch])")
}

foreach ($arch in $debs.Keys) {
    if (-not $pkgbuild.Contains("sha256sums_$arch=('$($hashes[$arch])')")) { throw "Could not stamp sha256sums_$arch in PKGBUILD" }
    if (-not $srcinfo.Contains("sha256sums_$arch = $($hashes[$arch])")) { throw "Could not stamp sha256sums_$arch in .SRCINFO" }
    if (-not $srcinfo.Contains("$base/$($debs[$arch])")) { throw "Could not stamp source_$arch in .SRCINFO" }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $OutDir 'PKGBUILD'), $pkgbuild, $utf8)
[IO.File]::WriteAllText((Join-Path $OutDir '.SRCINFO'), $srcinfo, $utf8)
Write-Host "Wrote $(Join-Path $OutDir 'PKGBUILD') and $(Join-Path $OutDir '.SRCINFO')"
