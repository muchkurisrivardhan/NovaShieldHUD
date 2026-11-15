# Builds the NovaShield HUD Widgets MSIX: compiles the provider, renders the icons and widget-picker
# screenshots, lays out the package, packs it with makeappx and signs it with a self-signed
# development certificate (created in your user store on first run; no admin needed here).
# Installing needs that certificate trusted once: see install.ps1.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = Split-Path (Split-Path $here)
$out = Join-Path $here 'out'
$layout = Join-Path $out 'layout'
$msix = Join-Path $out 'VantageWidgets.msix'
$subject = 'CN=Vantage Dev'   # must match Publisher in AppxManifest.xml

# makeappx / signtool from the Windows SDK BuildTools NuGet, for this PC's architecture.
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$bt = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Filter makeappx.exe |
      Where-Object { $_.Directory.Name -eq $arch } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $bt) { throw 'makeappx.exe not found: build the widgets project once so NuGet restores Microsoft.Windows.SDK.BuildTools.' }
$makeappx = $bt.FullName
$signtool = Join-Path $bt.DirectoryName 'signtool.exe'

Write-Host '1/5 Building the widget provider...'
dotnet build -c Release (Join-Path $here 'WidgetProvider.csproj') -nologo -v q | Out-Null
if ($LASTEXITCODE) { throw 'Widget provider build failed.' }
$bin = Join-Path $here 'bin\Release\net10.0-windows10.0.19041.0\win-arm64'

Write-Host '2/5 Laying out the package...'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $layout 'Assets'), (Join-Path $layout 'Public') | Out-Null
Get-ChildItem $bin -File | Where-Object { $_.Extension -notin '.pdb' -and $_.Name -ne 'selftest.txt' } | Copy-Item -Destination $layout
Copy-Item (Join-Path $here 'AppxManifest.xml') $layout
Set-Content (Join-Path $layout 'Public\README.txt') 'Public folder for the widget host (intentionally empty).'

Write-Host '3/5 Rendering icons and screenshots...'
Add-Type -AssemblyName System.Drawing
$lockExe = Get-ChildItem (Join-Path $repo 'lockscreen\bin\Release') -Recurse -Filter Lockscreen.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $lockExe) { throw 'Build lockscreen\Lockscreen.csproj first (the icons reuse its padlock emblem).' }
$tmp = Join-Path $out 'emblem'
& $lockExe.FullName --emblem $tmp --theme amber | Out-Null
$emblem = [System.Drawing.Image]::FromFile((Join-Path $tmp 'emblem-amber-1080.png'))
function Resize([int]$size, [string]$name) {
    $b = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($b); $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'
    $g.DrawImage($emblem, 0, 0, $size, $size); $g.Dispose()
    $b.Save((Join-Path $layout "Assets\$name"), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}
Resize 44 'Square44x44Logo.png'; Resize 150 'Square150x150Logo.png'; Resize 50 'StoreLogo.png'; Resize 256 'WidgetIcon.png'
$emblem.Dispose()

# Widget-picker previews: a dark card with the widget's lines, roughly as Windows draws it.
function Shot([string]$name, [object[]]$lines) {
    $w = 300; $h = 304
    $b = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath; $r = 16
    $path.AddArc(0, 0, $r*2, $r*2, 180, 90); $path.AddArc($w-$r*2-1, 0, $r*2, $r*2, 270, 90)
    $path.AddArc($w-$r*2-1, $h-$r*2-1, $r*2, $r*2, 0, 90); $path.AddArc(0, $h-$r*2-1, $r*2, $r*2, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 32, 34))), $path)
    $y = 22
    foreach ($l in $lines) {
        $style = if ($l.Bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
        $font = New-Object System.Drawing.Font('Cascadia Mono', [single]$l.Size, $style, [System.Drawing.GraphicsUnit]::Pixel)
        $g.DrawString($l.Text, $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($l.Color))), 20, $y)
        $y += [int]($l.Size * 1.45); $font.Dispose()
    }
    $g.Dispose(); $b.Save((Join-Path $layout "Assets\$name"), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}
$white = '#F2F2F2'; $dim = '#9A9A9A'; $amber = '#FFB000'; $green = '#6CCB5F'
$full = [string][char]0x2588; $empty = [string][char]0x2591; $dot = [string][char]0x00B7
function L([string]$text, [int]$size, [string]$color, [bool]$bold = $false) { [pscustomobject]@{ Text = $text; Size = $size; Color = $color; Bold = $bold } }
Shot 'Screenshot-Node.png' @(
    (L 'NODE-01' 22 $white $true), (L 'STATUS: SEALED' 13 $dim), (L ' ' 10 $dim),
    (L 'LINK' 12 $dim), (L 'ONLINE' 17 $green $true), (L 'POWER' 12 $dim), (L "98% $dot CHARGING" 17 $white $true),
    (L 'UPTIME' 12 $dim), (L '1D 15H' 17 $white $true))
Shot 'Screenshot-Progress.png' @(
    (L 'DAY / YEAR' 22 $white $true), (L ' ' 10 $dim),
    (L 'DAY   71%' 13 $dim), (L (($full * 8) + ($empty * 4)) 20 $amber), (L ' ' 6 $dim),
    (L 'YEAR  87%' 13 $dim), (L (($full * 10) + ($empty * 2)) 20 $amber))
Shot 'Screenshot-Countdown.png' @(
    (L 'TARGET' 22 $white $true), (L ' ' 8 $dim), (L '48' 64 $amber $true), (L 'DAYS TO 2027-01-01' 13 $dim))

Write-Host '4/5 Packing...'
& $makeappx pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE) { & $makeappx pack /d $layout /p $msix /o; throw 'makeappx failed (output above).' }

Write-Host '5/5 Signing...'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature -FriendlyName 'NovaShield HUD Widgets development signing' -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "   Created signing certificate $($cert.Thumbprint) in your user store."
}
Export-Certificate -Cert $cert -FilePath (Join-Path $out 'VantageWidgets.cer') | Out-Null
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix | Out-Null
if ($LASTEXITCODE) { & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /v $msix; throw 'signtool failed (output above).' }

Write-Host "Done: $msix"
Write-Host 'Next: .\install.ps1 (asks once for admin to trust the certificate).'
