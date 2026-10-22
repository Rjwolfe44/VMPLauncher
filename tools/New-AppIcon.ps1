<#
.SYNOPSIS  Generates assets\VmpLauncher.ico (16..256 px, PNG-compressed entries) and assets\splash.png.
           Dark space disc, purple accent (#B69CFF) orbit ring and a small rocket-ish marker.
#>
param([string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\VmpLauncher.App\Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = [math]::Max(1, $size * 0.04)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($size - 2*$pad), ($size - 2*$pad)
    # disc: deep space gradient
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath; $path.AddEllipse($rect)
    $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
    $pgb.CenterColor = [System.Drawing.Color]::FromArgb(255, 0x1A, 0x1E, 0x38)
    $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 0x0B, 0x0D, 0x17))
    $pgb.CenterPoint = New-Object System.Drawing.PointF ($size*0.38), ($size*0.35)
    $g.FillEllipse($pgb, $rect)
    # subtle rim
    $rim = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 0x2A, 0x2F, 0x52)), ([math]::Max(1, $size*0.025))
    $g.DrawEllipse($rim, $rect)
    # stars
    $rand = New-Object System.Random 7
    $starBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(180, 0xEC, 0xEA, 0xF6))
    for ($i = 0; $i -lt [math]::Max(6, $size/6); $i++) {
        $a = $rand.NextDouble() * 2 * [math]::PI; $r = $rand.NextDouble() * ($size*0.40)
        $x = $size/2 + [math]::Cos($a)*$r; $y = $size/2 + [math]::Sin($a)*$r
        $d = [math]::Max(0.6, $size * (0.006 + $rand.NextDouble()*0.012))
        $g.FillEllipse($starBrush, [float]($x-$d/2), [float]($y-$d/2), [float]$d, [float]$d)
    }
    # orbit ring (tilted ellipse) in accent
    $accent = [System.Drawing.Color]::FromArgb(255, 0xB6, 0x9C, 0xFF)
    $ringPen = New-Object System.Drawing.Pen $accent, ([math]::Max(1.2, $size*0.055))
    $ringPen.StartCap = 'Round'; $ringPen.EndCap = 'Round'
    $state = $g.Save()
    $g.TranslateTransform($size/2, $size/2); $g.RotateTransform(-28)
    $rw = $size*0.70; $rh = $size*0.30
    $g.DrawArc($ringPen, [float](-$rw/2), [float](-$rh/2), [float]$rw, [float]$rh, 20, 300)
    $g.Restore($state)
    # planet core
    $core = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ($size*0.35),($size*0.35)), (New-Object System.Drawing.PointF ($size*0.65),($size*0.65)), ([System.Drawing.Color]::FromArgb(255,0xC8,0xB3,0xFF)), ([System.Drawing.Color]::FromArgb(255,0x7C,0x5C,0xD6))
    $cd = $size*0.30
    $g.FillEllipse($core, [float]($size/2-$cd/2), [float]($size/2-$cd/2), [float]$cd, [float]$cd)
    # rocket marker: small bright dot on the ring
    $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xFF, 0xFF, 0xFF))
    $dd = [math]::Max(1.5, $size*0.09)
    $g.FillEllipse($dotBrush, [float]($size*0.72-$dd/2), [float]($size*0.30-$dd/2), [float]$dd, [float]$dd)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    ,@{ Size = $s; Bytes = $ms.ToArray() }
}
# ICO container with PNG entries
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$p.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'VmpLauncher.ico'), $ico.ToArray())

# splash 600x300
$splash = New-Object System.Drawing.Bitmap 600, 300
$g = [System.Drawing.Graphics]::FromImage($splash); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'ClearTypeGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(255, 0x0B, 0x0D, 0x17))
$logo = Draw-Icon 160; $g.DrawImage($logo, 40, 70, 160, 160); $logo.Dispose()
$font = New-Object System.Drawing.Font ('Segoe UI', [single]30, [System.Drawing.FontStyle]::Bold)
$g.DrawString('VMP Launcher', $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,0xEC,0xEA,0xF6))), 230, 105)
$font2 = New-Object System.Drawing.Font ('Segoe UI', [single]13)
$g.DrawString('Loading…', $font2, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,0xA9,0xA6,0xC4))), 234, 160)
$g.Dispose()
$splash.Save((Join-Path $OutDir 'splash.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$splash.Dispose()
# 256 png for README / gallery
$big = Draw-Icon 256; $big.Save((Join-Path $OutDir 'VmpLauncher-256.png'), [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()
Write-Host "Wrote icon + splash to $OutDir"
