# Builds installer\out\NovaShieldHUD-Setup-<arch>.exe: publishes Beacon and Dotline
# self-contained (no .NET install needed) into one folder, Filee (the converter, an Avalonia
# app with its own dependencies) into its filee\ subfolder, then packs it with Inno Setup 6.
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Arch x64 -Version 0.5.0
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [string] $Version = '0.5.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$stage = Join-Path $PSScriptRoot "out\stage-$Arch"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

foreach ($proj in 'beacon\Beacon.csproj', 'dotline\Dotline.csproj') {
    dotnet publish (Join-Path $root $proj) -c Release -r "win-$Arch" --self-contained -o $stage -p:Version=$Version -nologo -v q
    if ($LASTEXITCODE) { throw "publish failed: $proj" }
}

# Filee: its own folder, since its libraries (Avalonia, SkiaSharp, ...) are not Beacon's. Not a
# FileeRelease build: Beacon starts it, so it must not register itself at sign-in or update
# itself from upstream (which doesn't carry the NovaShield look).
$filee = Join-Path $stage 'filee'
$fileeRoot = Join-Path $root 'filee'
$native = @()
if ($Arch -eq 'arm64') {
    # Unhwp's package has no win-arm64 library; Filee's build fetches the pinned one.
    $natives = Join-Path $fileeRoot 'build\.cache\win-arm64-native'
    & (Join-Path $fileeRoot 'build\fetch-engines.ps1') -Runtime "win-$Arch" -Only unhwp -Destination $natives
    $native = @("-p:UnhwpNativeLibrary=$(Join-Path $natives 'unhwp\unhwp.dll')")
}
# The Noto fonts of Filee's UI aren't in git (36 MB); pinned and SHA-256 checked.
& (Join-Path $fileeRoot 'build\fetch-fonts.ps1')
dotnet publish (Join-Path $fileeRoot 'src\Filee.App\Filee.App.csproj') -c Release -r "win-$Arch" --self-contained `
    -p:PublishReadyToRun=true @native -o $filee -nologo -v q
if ($LASTEXITCODE) { throw 'publish failed: Filee.App' }
# Only the small rhwp and 7-Zip engines are bundled; Filee downloads the large ones when needed.
$runtimeTo = @{}
if ($Arch -eq 'x64') { $runtimeTo = @{ RuntimeTo = $filee } }
& (Join-Path $fileeRoot 'build\fetch-engines.ps1') -Runtime "win-$Arch" -Only rhwp, 7zip -Destination (Join-Path $filee 'engines') @runtimeTo
Get-ChildItem $filee -Recurse -Filter '*.pdb' | Remove-Item
Copy-Item (Join-Path $fileeRoot 'LICENSE') (Join-Path $filee 'LICENSE.txt')
Copy-Item (Join-Path $fileeRoot 'THIRD-PARTY-NOTICES.md') $filee

Remove-Item (Join-Path $stage '*.pdb')

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }
& $iscc /Q "/DAppVersion=$Version" "/DArch=$Arch" "/DSource=$stage" (Join-Path $PSScriptRoot 'NovaShieldHUD.iss')
if ($LASTEXITCODE) { throw 'ISCC failed' }

$exe = Join-Path $PSScriptRoot "out\NovaShieldHUD-Setup-$Arch.exe"
"{0}  {1}" -f (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower(), (Split-Path $exe -Leaf)
