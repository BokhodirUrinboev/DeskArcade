# Builds the Ubuntu .deb from Windows: publishes linux-x64 here, then packages it inside WSL (dpkg-deb).
#   .\build-linux.ps1                       version from DeskArcade.csproj
#   .\build-linux.ps1 -Version 1.2.0        override the version
#   .\build-linux.ps1 -Distro Ubuntu-22.04  pick another WSL distro
# On Ubuntu itself, run packaging/linux/build.sh instead.
param([string]$Version, [string]$Distro = 'Ubuntu-24.04')

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content .\DeskArcade.csproj -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version must look like 1.2.3 (got '$Version')" }

$publish = Join-Path $PSScriptRoot 'dist-linux\publish'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish .\DeskArcade.csproj -c Release -r linux-x64 --self-contained "-p:Version=$Version" -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$env:WSL_UTF8 = '1'
$wslRoot = (wsl.exe -d $Distro --exec wslpath -a ($PSScriptRoot -replace '\\', '/')).Trim()
wsl.exe -d $Distro --exec bash "$wslRoot/packaging/linux/build-deb.sh" "$wslRoot/dist-linux/publish" $Version "$wslRoot/dist-linux"
if ($LASTEXITCODE -ne 0) { throw "packaging failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "Package: $PSScriptRoot\dist-linux\deskarcade_${Version}_amd64.deb"
