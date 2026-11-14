# Removes the Vantage Widgets package and the development certificate install.ps1 trusted,
# leaving the PC as it was. Removing the trusted certificate needs one admin prompt.
$ErrorActionPreference = 'Stop'
$subject = 'CN=Vantage Dev'

$pkg = Get-AppxPackage Vantage.LockscreenWidgets
if ($pkg) { Remove-AppxPackage -Package $pkg.PackageFullName; Write-Host 'Package removed.' }
else { Write-Host 'Package was not installed.' }

$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq $subject
if ($trusted) {
    Write-Host 'Removing the trusted certificate (one admin prompt)...'
    $cmd = "Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Subject -eq '$subject' | Remove-Item"
    try { Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $cmd }
    catch { Write-Warning 'The admin prompt was declined; the certificate is still trusted. Run this again to remove it.' }
}

Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $subject | Remove-Item
Write-Host 'Signing certificate removed from your user store.'
