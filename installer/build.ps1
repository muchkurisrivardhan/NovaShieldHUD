# Builds installer\out\NovaShieldHUD-Setup-<arch>.exe: publishes Beacon, Dotline and Lockscreen
# self-contained (no .NET install needed) into one folder, then packs it with Inno Setup 6.
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Arch x64 -Version 0.5.0
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $Version = '0.5.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$stage = Join-Path $PSScriptRoot "out\stage-$Arch"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

foreach ($proj in 'beacon\Beacon.csproj', 'dotline\Dotline.csproj', 'lockscreen\Lockscreen.csproj') {
    dotnet publish (Join-Path $root $proj) -c Release -r "win-$Arch" --self-contained -o $stage -p:Version=$Version -nologo -v q
    if ($LASTEXITCODE) { throw "publish failed: $proj" }
}
Remove-Item (Join-Path $stage '*.pdb')

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }
& $iscc /Q "/DAppVersion=$Version" "/DArch=$Arch" "/DSource=$stage" (Join-Path $PSScriptRoot 'NovaShieldHUD.iss')
if ($LASTEXITCODE) { throw 'ISCC failed' }

$exe = Join-Path $PSScriptRoot "out\NovaShieldHUD-Setup-$Arch.exe"
"{0}  {1}" -f (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower(), (Split-Path $exe -Leaf)
