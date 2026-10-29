<#
.SYNOPSIS  Draws src\VmpLauncher.App\Assets\splash.png, the image Velopack shows while it installs or updates the launcher.
.NOTES     Keep this file pure ASCII. Windows PowerShell 5.1 reads BOM-less scripts as ANSI, so a literal
           ellipsis or dash here turns into mojibake in the image (that is how the old splash ended up showing garbage after "Loading").
#>
param([string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\VmpLauncher.App\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$W = 600; $H = 300
$bmp = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
$g.TextRenderingHint = 'AntiAliasGridFit'
function C([int]$a, [int]$r, [int]$gg, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $gg, $b) }

# Background: deep navy gradient
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0, 0, $W, $H), (C 255 0x10 0x13 0x2A), (C 255 0x0A 0x0C 0x18), ([single]70)
$g.FillRectangle($bg, 0, 0, $W, $H)

# Purple nebula behind the planet
$neb = New-Object System.Drawing.Drawing2D.GraphicsPath
$neb.AddEllipse(300, -60, 380, 380)
$nb = New-Object System.Drawing.Drawing2D.PathGradientBrush $neb
$nb.CenterColor = C 90 0xB6 0x9C 0xFF
$nb.SurroundColors = @(C 0 0xB6 0x9C 0xFF)
$g.FillPath($nb, $neb)

# Stars (fixed seed so the image is reproducible)
$rnd = New-Object System.Random 7
for ($i = 0; $i -lt 70; $i++) {
    $x = $rnd.Next(0, $W); $y = $rnd.Next(0, $H); $r = 0.6 + $rnd.NextDouble() * 1.1
    $a = 90 + $rnd.Next(0, 140)
    $col = if ($rnd.Next(0, 6) -eq 0) { C $a 0xC9 0xB5 0xFF } else { C $a 0xEC 0xEA 0xF6 }
    $sb = New-Object System.Drawing.SolidBrush $col
    $g.FillEllipse($sb, [single]($x - $r), [single]($y - $r), [single](2 * $r), [single](2 * $r))
    $sb.Dispose()
}

# Planet
$cx = 470; $cy = 150; $pr = 82
$ring = { param($front)
    $state = $g.Save()
    $g.TranslateTransform($cx, $cy); $g.RotateTransform(-18)
    $pen = New-Object System.Drawing.Pen (C 230 0xC9 0xB5 0xFF), 6
    if ($front) { $g.DrawArc($pen, -150, -24, 300, 48, 0, 180) } else { $g.DrawArc($pen, -150, -24, 300, 48, 180, 180) }
    $pen.Dispose(); $g.Restore($state)
}
& $ring $false
$planet = New-Object System.Drawing.Drawing2D.GraphicsPath
$planet.AddEllipse($cx - $pr, $cy - $pr, 2 * $pr, 2 * $pr)
$pb = New-Object System.Drawing.Drawing2D.PathGradientBrush $planet
$pb.CenterPoint = New-Object System.Drawing.PointF ($cx - 30), ($cy - 34)
$pb.CenterColor = C 255 0xE3 0xD8 0xFF
$pb.SurroundColors = @(C 255 0x4C 0x33 0xB0)
$blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
$blend.Colors = @((C 255 0x2E 0x22 0x6E), (C 255 0x8F 0x72 0xF0), (C 255 0xE3 0xD8 0xFF))
$blend.Positions = @([single]0, [single]0.55, [single]1)
$pb.InterpolationColors = $blend
$g.FillPath($pb, $planet)
& $ring $true
$moon = New-Object System.Drawing.SolidBrush (C 235 0xEC 0xEA 0xF6)
$g.FillEllipse($moon, 560, 52, 12, 12)

# Text
$title = New-Object System.Drawing.Font ('Segoe UI', [single]40, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$sub = New-Object System.Drawing.Font ('Segoe UI', [single]17, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$white = New-Object System.Drawing.SolidBrush (C 255 0xEC 0xEA 0xF6)
$grey = New-Object System.Drawing.SolidBrush (C 255 0xA9 0xA6 0xC4)
$accent = New-Object System.Drawing.SolidBrush (C 255 0xB6 0x9C 0xFF)
$g.FillRectangle($accent, 44, 106, 46, 5)
$g.DrawString('VMP Launcher', $title, $white, 38, 118)
$g.DrawString('Getting everything ready', $sub, $grey, 42, 172)

$g.Dispose()
$bmp.Save((Join-Path $OutDir 'splash.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "Wrote $(Join-Path $OutDir 'splash.png')"
