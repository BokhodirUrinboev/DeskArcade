# Installs Desk Arcade with the release's own Setup .exe (Inno Setup, per-user, .NET runtime bundled), silently.
# packaging/chocolatey/Update-ChocolateyPackage.ps1 stamps the URLs and SHA256 digests below for each release.
$ErrorActionPreference = 'Stop'

$url64         = 'https://github.com/BokhodirUrinboev/DeskArcade/releases/download/v1.8.5/DeskArcade-Setup-1.8.5-standalone.exe'
$checksum64    = '32c7e56659525e1cce6afcff9995a8bd58526c96880e7296dbbfac182b8af204'
$urlArm64      = 'https://github.com/BokhodirUrinboev/DeskArcade/releases/download/v1.8.5/DeskArcade-Setup-1.8.5-arm64.exe'
$checksumArm64 = 'c56ca0e098db6cb52a3185d605f02dcc1e7b426fd5b96abe903593743fefc8d6'

# Chocolatey only tells 32 from 64 bits, so Windows on ARM is picked out here to get the native ARM64 build. The
# machine-wide environment in the registry names the real processor, even inside an x64-emulated process.
$machineArch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
if ($machineArch -eq 'ARM64') {
  $url64 = $urlArm64
  $checksum64 = $checksumArm64
}

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  fileType       = 'exe'
  softwareName   = 'Desk Arcade'
  url64bit       = $url64
  checksum64     = $checksum64
  checksumType64 = 'sha256'
  # Inno Setup: no wizard or message boxes, no restart, and a log beside Chocolatey's. Setup closes a running game,
  # upgrades the folder in place, keeps settings and high scores, and refuses to downgrade.
  silentArgs     = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=`"$($env:TEMP)\$($env:ChocolateyPackageName).$($env:ChocolateyPackageVersion).Install.log`""
  validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
