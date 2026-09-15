# Builds a single-file DeskArcade.exe into .\dist
#   .\build.ps1                    needs the .NET 10 desktop runtime to run (small exe)
#   .\build.ps1 -SelfContained     bundles the runtime (big exe, runs anywhere)
#   .\build.ps1 -Version 1.2.0     override the version from DeskArcade.csproj
param([switch]$SelfContained, [string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Use the explicit flags: "--self-contained false" gets read as plain "--self-contained" by the .NET 10 CLI.
$publishArgs = @(
    '.\DeskArcade.csproj', '-c', 'Release', '-r', 'win-x64',
    $(if ($SelfContained) { '--self-contained' } else { '--no-self-contained' }),
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', '.\dist'
)
if ($Version) { $publishArgs += "-p:Version=$Version" }

dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Write-Host ""
Write-Host "Built: $PSScriptRoot\dist\DeskArcade.exe"
