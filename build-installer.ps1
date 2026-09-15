# Publishes the app and compiles the Inno Setup installer into .\installer\Output
#   .\build-installer.ps1                   small installer, needs the .NET 10 Runtime (setup checks for it)
#   .\build-installer.ps1 -SelfContained    bundles the runtime, runs on any 64-bit Windows 10/11
#   .\build-installer.ps1 -Version 1.2.0    override the version from DeskArcade.csproj
#
# Releasing an update: bump <Version> in DeskArcade.csproj, run this script, ship the new setup.
# Running it over an existing install upgrades in place (same folder, settings and high scores kept).
param([switch]$SelfContained, [string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content .\DeskArcade.csproj -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version must look like 1.2.3 (got '$Version')" }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& .\build.ps1 -SelfContained:$SelfContained -Version $Version

$defines = @("/DMyAppVersion=$Version")
if ($SelfContained) { $defines += '/DSelfContained' }
& $iscc /Q @defines .\installer\DeskArcade.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed ($LASTEXITCODE)" }

$suffix = if ($SelfContained) { '-standalone' } else { '' }
Write-Host ""
Write-Host "Installer: $PSScriptRoot\installer\Output\DeskArcade-Setup-$Version$suffix.exe"
