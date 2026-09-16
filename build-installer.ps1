# Publishes the app and compiles the Inno Setup installer into .\installer\Output
#   .\build-installer.ps1                   small installer, needs the .NET 10 Runtime (setup checks for it)
#   .\build-installer.ps1 -SelfContained    bundles the runtime, runs on any 64-bit Windows 10/11
#   .\build-installer.ps1 -Arch arm64       Windows on ARM installer (always bundles the runtime)
#   .\build-installer.ps1 -Version 1.2.0    override the version from DeskArcade.csproj
#   .\build-installer.ps1 -SignCertThumbprint <sha1>
#                                           sign DeskArcade.exe, Setup and the uninstaller with that
#                                           certificate from Cert:\CurrentUser\My (see docs/RELEASING.md)
#
# Output: DeskArcade-Setup-<version>.exe, ...-standalone.exe (x64 with runtime) or ...-arm64.exe.
# Releasing an update: bump <Version> in DeskArcade.csproj, run this script, ship the new setup.
# Running it over an existing install upgrades in place (same folder, settings and high scores kept).
param(
    [switch]$SelfContained,
    [string]$Version,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$SignCertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content .\DeskArcade.csproj -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version must look like 1.2.3 (got '$Version')" }
# The ARM64 installer is only shipped with the runtime bundled.
if ($Arch -eq 'arm64') { $SelfContained = [switch]$true }

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& .\build.ps1 -SelfContained:$SelfContained -Version $Version -Arch $Arch -SignCertThumbprint $SignCertThumbprint -TimestampUrl $TimestampUrl

$defines = @("/DMyAppVersion=$Version", "/DArch=$Arch")
if ($SelfContained) { $defines += '/DSelfContained' }
if ($SignCertThumbprint) {
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK)' }
    # Inno Setup runs this "deskarcade" sign tool on Setup and on the uninstaller it embeds.
    # $q is a quote and $f the file to sign; no literal quotes, so the argument survives PowerShell.
    $defines += '/DSign'
    $defines += '/Sdeskarcade=$q' + $signtool + '$q sign /sha1 ' + $SignCertThumbprint +
        ' /fd sha256 /tr ' + $TimestampUrl + ' /td sha256 /d $qDesk Arcade$q $f'
}
& $iscc /Q @defines .\installer\DeskArcade.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed ($LASTEXITCODE)" }

$suffix = if ($Arch -eq 'arm64') { '-arm64' } elseif ($SelfContained) { '-standalone' } else { '' }
Write-Host ""
Write-Host "Installer: $PSScriptRoot\installer\Output\DeskArcade-Setup-$Version$suffix.exe"
