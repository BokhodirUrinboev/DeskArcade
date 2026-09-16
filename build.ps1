# Builds a single-file DeskArcade.exe into .\dist
#   .\build.ps1                    needs the .NET 10 desktop runtime to run (small exe)
#   .\build.ps1 -SelfContained     bundles the runtime (big exe, runs anywhere)
#   .\build.ps1 -Arch arm64        builds for Windows on ARM (win-arm64) instead of x64
#   .\build.ps1 -Version 1.2.0     override the version from DeskArcade.csproj
#   .\build.ps1 -SignCertThumbprint <sha1>
#                                  Authenticode-sign the exe with that certificate from Cert:\CurrentUser\My
param(
    [switch]$SelfContained,
    [string]$Version,
    [ValidateSet('x64', 'arm64')][string]$Arch = 'x64',
    [string]$SignCertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Use the explicit flags: "--self-contained false" gets read as plain "--self-contained" by the .NET 10 CLI.
$publishArgs = @(
    '.\DeskArcade.csproj', '-c', 'Release', '-r', "win-$Arch",
    $(if ($SelfContained) { '--self-contained' } else { '--no-self-contained' }),
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', '.\dist'
)
if ($Version) { $publishArgs += "-p:Version=$Version" }

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if ($SignCertThumbprint) {
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if (-not $signtool) {
        # Windows SDK: newest x64 signtool.exe under Windows Kits\10\bin\<version>\x64
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK)' }
    & $signtool sign /sha1 $SignCertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 /d 'Desk Arcade' .\dist\DeskArcade.exe
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

Write-Host ""
Write-Host "Built: $PSScriptRoot\dist\DeskArcade.exe (win-$Arch)"
