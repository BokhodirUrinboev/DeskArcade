# Imports the Authenticode code-signing certificate for release builds into Cert:\CurrentUser\My.
#   $env:WINDOWS_CERTIFICATE_PFX_BASE64   the .pfx file, base64-encoded
#   $env:WINDOWS_CERTIFICATE_PASSWORD     its password
# Prints the certificate thumbprint, and when running in GitHub Actions also writes the step outputs
# "enabled" (true/false) and "thumbprint". With either variable empty it reports that signing is
# skipped and succeeds, so unsigned builds keep working without the secrets.
# The .pfx is imported straight from memory and never written to disk.
$ErrorActionPreference = 'Stop'

function Set-StepOutput([string]$Name, [string]$Value) {
    if ($env:GITHUB_OUTPUT) { Add-Content -Path $env:GITHUB_OUTPUT -Value "$Name=$Value" -Encoding utf8 }
}

if (-not $env:WINDOWS_CERTIFICATE_PFX_BASE64 -or -not $env:WINDOWS_CERTIFICATE_PASSWORD) {
    Write-Host 'WINDOWS_CERTIFICATE_PFX_BASE64 / WINDOWS_CERTIFICATE_PASSWORD not set: building unsigned.'
    Set-StepOutput 'enabled' 'false'
    return
}

$bytes = [Convert]::FromBase64String(($env:WINDOWS_CERTIFICATE_PFX_BASE64 -replace '\s', ''))
$flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]'UserKeySet, PersistKeySet'
$certs = New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection
$certs.Import($bytes, $env:WINDOWS_CERTIFICATE_PASSWORD, $flags)

$codeSigningOid = '1.3.6.1.5.5.7.3.3'
$signer = $certs | Where-Object {
    $_.HasPrivateKey -and ($_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid -or -not $_.EnhancedKeyUsageList)
} | Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $signer) { throw 'The .pfx has no certificate with a private key that allows code signing' }
if ($signer.NotAfter -lt (Get-Date)) { throw "The code-signing certificate expired on $($signer.NotAfter)" }

$my = New-Object Security.Cryptography.X509Certificates.X509Store 'My', 'CurrentUser'
$my.Open('ReadWrite'); $my.Add($signer); $my.Close()
# Intermediate certificates from the .pfx let signtool embed the full chain.
$others = @($certs | Where-Object { $_.Thumbprint -ne $signer.Thumbprint })
if ($others.Count -gt 0) {
    $ca = New-Object Security.Cryptography.X509Certificates.X509Store 'CA', 'CurrentUser'
    $ca.Open('ReadWrite'); $others | ForEach-Object { $ca.Add($_) }; $ca.Close()
}

Write-Host "Signing with '$($signer.Subject)', thumbprint $($signer.Thumbprint), valid until $($signer.NotAfter)"
Set-StepOutput 'enabled' 'true'
Set-StepOutput 'thumbprint' $signer.Thumbprint
$signer.Thumbprint
