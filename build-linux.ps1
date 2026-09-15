# Builds the Ubuntu .deb (and optionally the AppImage) from Windows: publishes here, then packages inside WSL.
#   .\build-linux.ps1                       version from DeskArcade.csproj, amd64
#   .\build-linux.ps1 -Version 1.2.0        override the version
#   .\build-linux.ps1 -Arch arm64           cross-compile for 64-bit ARM (linux-arm64)
#   .\build-linux.ps1 -AppImage             also build DeskArcade-<version>-<x86_64|aarch64>.AppImage
#   .\build-linux.ps1 -Distro Ubuntu-22.04  pick another WSL distro
# On Ubuntu itself, run packaging/linux/build.sh (and packaging/linux/build-appimage.sh) instead.
param(
    [string]$Version,
    [ValidateSet('amd64', 'arm64')][string]$Arch = 'amd64',
    [switch]$AppImage,
    [string]$Distro = 'Ubuntu-24.04'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content .\DeskArcade.csproj -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version must look like 1.2.3 (got '$Version')" }

$rid = @{ amd64 = 'linux-x64'; arm64 = 'linux-arm64' }[$Arch]
$appImageArch = @{ amd64 = 'x86_64'; arm64 = 'aarch64' }[$Arch]
$publishName = if ($Arch -eq 'amd64') { 'publish' } else { "publish-$Arch" }

$publish = Join-Path $PSScriptRoot "dist-linux\$publishName"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish .\DeskArcade.csproj -c Release -r $rid --self-contained "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$env:WSL_UTF8 = '1'
$wslRoot = (wsl.exe -d $Distro --exec wslpath -a ($PSScriptRoot -replace '\\', '/')).Trim()
wsl.exe -d $Distro --exec bash "$wslRoot/packaging/linux/build-deb.sh" "$wslRoot/dist-linux/$publishName" $Version "$wslRoot/dist-linux" $Arch
if ($LASTEXITCODE -ne 0) { throw "packaging failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "Package: $PSScriptRoot\dist-linux\deskarcade_${Version}_$Arch.deb"

if ($AppImage) {
    wsl.exe -d $Distro --exec bash "$wslRoot/packaging/linux/build-appimage.sh" "$wslRoot/dist-linux/$publishName" $Version $appImageArch "$wslRoot/dist-linux"
    if ($LASTEXITCODE -ne 0) { throw "AppImage build failed ($LASTEXITCODE)" }
    Write-Host "AppImage: $PSScriptRoot\dist-linux\DeskArcade-$Version-$appImageArch.AppImage"
}
