# Uninstalls Desk Arcade with the uninstaller its Setup left (unins000.exe), silently. The uninstaller closes a
# running game and removes the "arcade" command from PATH; its question about deleting settings and high scores
# takes the default answer, No, so they stay in %APPDATA%\DeskArcade.
$ErrorActionPreference = 'Stop'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  softwareName   = 'Desk Arcade'
  fileType       = 'exe'
  silentArgs     = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
  validExitCodes = @(0)
}

# Setup installs per user, so the entry is under HKCU; Get-UninstallRegistryKey looks there as well as in HKLM.
[array]$key = Get-UninstallRegistryKey -SoftwareName $packageArgs.softwareName

if ($key.Count -eq 1) {
  # "C:\Users\...\Desk Arcade\unins000.exe", quoted in the registry
  $packageArgs.file = $key[0].UninstallString.Trim('"')
  Uninstall-ChocolateyPackage @packageArgs
}
elseif ($key.Count -eq 0) {
  Write-Warning "$($packageArgs.packageName) has already been uninstalled by other means."
}
else {
  Write-Warning "$($key.Count) matches found for '$($packageArgs.softwareName)'; to avoid removing the wrong program, none was uninstalled."
  $key | ForEach-Object { Write-Warning "- $($_.DisplayName)" }
}
