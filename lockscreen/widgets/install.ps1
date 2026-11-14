# Installs the Vantage Widgets package built by build-package.ps1.
#
# Windows only installs a package whose signature it trusts. The package is signed with your
# own development certificate, so this trusts that one certificate on this PC by adding it to
# Local Machine > Trusted People. That single step needs admin: Windows shows one UAC prompt.
# Everything else runs as you. uninstall.ps1 removes both the package and the certificate.
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'out'
$msix = Join-Path $out 'VantageWidgets.msix'
$cer = Join-Path $out 'VantageWidgets.cer'
if (-not (Test-Path $msix) -or -not (Test-Path $cer)) { throw 'Run .\build-package.ps1 first.' }

$thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) {
    Write-Host 'Trusting the signing certificate (one admin prompt)...'
    $cmd = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    try { Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $cmd }
    catch { throw 'The admin prompt was declined, so the certificate was not trusted and nothing was installed.' }
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumb")) { throw 'The certificate was not trusted, so nothing was installed.' }
}

Write-Host 'Installing the package...'
Add-AppxPackage -Path $msix -ForceUpdateFromAnyVersion
Get-AppxPackage Vantage.LockscreenWidgets | Select-Object Name, Version, InstallLocation | Format-List
Write-Host 'Installed. Add the widgets: Win+W > + (Add widgets) > Vantage.'
Write-Host 'Lock screen: Settings > Personalization > Lock screen > widgets (if Windows offers them there).'
