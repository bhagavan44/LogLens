<#
.SYNOPSIS
  Regenerates the application icon (src/LogLens.App/Assets/AppIcon.ico and AppIcon.png).

.DESCRIPTION
  Draws the icon with GDI+ so there is no binary design source to keep in sync: a dark tile with
  log rows (level pill + message bar) and a magnifier showing the error row. Small sizes get a
  simplified drawing (fewer rows, thicker shapes, no lens) so they stay legible in the taskbar.

.EXAMPLE
  pwsh build/make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/LogLens.App/Assets'),
    # Optional: write a contact sheet of every size (light and dark backgrounds) to this path.
    [string]$PreviewPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

$Navy1 = C '#2E4268'; $Navy2 = C '#111A2C'
$Info = C '#4DA3FF'; $Warn = C '#FFB020'; $Err = C '#FF5A5F'; $Debug = C '#8FA3BF'
$Text = C '#DCE6F5'; $LensFace = C '#1A2740'; $RingColor = C '#F2F7FF'; $HandleColor = C '#9DB4D6'

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Fill-Rounded($g, $color, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-RoundedPath $x $y $w $h $r
    $brush = New-Object System.Drawing.SolidBrush $color
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()
}

# One log row in 256-unit space: level pill on the left, message bar on the right.
function Draw-Row($g, $level, [single]$x, [single]$cy, [single]$pillW, [single]$pillH, [single]$barW, [single]$barH, [single]$gap) {
    Fill-Rounded $g $level $x ($cy - $pillH / 2) $pillW $pillH ($pillH / 2)
    Fill-Rounded $g $Text ($x + $pillW + $gap) ($cy - $barH / 2) $barW $barH ($barH / 2)
}

function New-IconBitmap([int]$size) {
    $ss = 4                                   # supersampling factor
    $big = $size * $ss
    $bmp = New-Object System.Drawing.Bitmap $big, $big, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($big / 256.0, $big / 256.0)   # draw in a 256 x 256 design space

    # Tile with a vertical gradient and a soft inner top edge.
    $tile = New-RoundedPath 10 10 236 236 56
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 10)), ([System.Drawing.PointF]::new(0, 246)), $Navy1, $Navy2
    $g.FillPath($grad, $tile)
    $grad.Dispose()
    if ($size -ge 32) {
        $edge = New-Object System.Drawing.Pen (C '#4A6290'), 3
        $g.DrawPath($edge, $tile)
        $edge.Dispose()
    }
    $tile.Dispose()

    if ($size -le 24) {
        # Tiny: three fat rows, no lens.
        Draw-Row $g $Info 44 76 52 36 70 22 12
        Draw-Row $g $Warn 44 128 52 36 46 22 12
        Draw-Row $g $Err 44 180 52 36 82 22 12
    }
    elseif ($size -le 40) {
        # Small: four rows, no lens.
        Draw-Row $g $Debug 40 66 44 26 82 16 12
        Draw-Row $g $Info 40 108 44 26 62 16 12
        Draw-Row $g $Warn 40 150 44 26 88 16 12
        Draw-Row $g $Err 40 192 44 26 70 16 12
    }
    else {
        # Full: four rows plus a magnifier over the error row.
        Draw-Row $g $Debug 38 62 36 20 76 12 12
        Draw-Row $g $Info 38 98 36 20 100 12 12
        Draw-Row $g $Warn 38 134 36 20 62 12 12
        Draw-Row $g $Info 38 170 36 20 90 12 12

        # Handle first so the lens sits on top of it.
        $pen = New-Object System.Drawing.Pen $HandleColor, 22
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, 178, 178, 222, 222)
        $pen.Dispose()

        # Lens: opaque dark face with the error row enlarged inside, clipped to the circle.
        $cx = 154; $cy = 150; $r = 56
        $lensPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $lensPath.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r)
        $lensBrush = New-Object System.Drawing.SolidBrush $LensFace
        $g.FillPath($lensBrush, $lensPath)
        $lensBrush.Dispose()

        $state = $g.Save()
        $g.SetClip($lensPath)
        Draw-Row $g $Warn ($cx - 36) ($cy - 32) 26 14 36 9 8
        Draw-Row $g $Err ($cx - 46) ($cy + 2) 46 26 36 16 9
        Draw-Row $g $Info ($cx - 34) ($cy + 35) 26 14 28 9 8
        $g.Restore($state)

        $ring = New-Object System.Drawing.Pen $RingColor, 12
        $g.DrawEllipse($ring, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
        $ring.Dispose(); $lensPath.Dispose()
    }

    $g.Dispose()

    # Downsample to the final size.
    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $og = [System.Drawing.Graphics]::FromImage($out)
    $og.InterpolationMode = 'HighQualityBicubic'
    $og.PixelOffsetMode = 'HighQuality'
    $og.CompositingMode = 'SourceCopy'
    $og.DrawImage($bmp, 0, 0, $size, $size)
    $og.Dispose(); $bmp.Dispose()
    $out
}

function ConvertTo-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    [pscustomobject]@{ Size = $s; Bytes = (ConvertTo-PngBytes $bmp); Bitmap = $bmp }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null

# ICO container with PNG-compressed entries (supported since Windows Vista).
$icoPath = Join-Path $OutDir 'AppIcon.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$i.Bytes.Length); $bw.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $bw.Write($i.Bytes) }
$bw.Dispose(); $fs.Dispose()

# 256 px PNG for in-app use (title bar).
$png = Join-Path $OutDir 'AppIcon.png'
[System.IO.File]::WriteAllBytes($png, ($images | Where-Object Size -eq 256).Bytes)

if ($PreviewPath) {
    $sheet = New-Object System.Drawing.Bitmap 760, 300
    $sg = [System.Drawing.Graphics]::FromImage($sheet)
    $sg.Clear((C '#F3F3F3'))
    $x = 10
    foreach ($i in ($images | Where-Object Size -lt 256)) { $sg.DrawImage($i.Bitmap, $x, 10, $i.Size, $i.Size); $x += $i.Size + 12 }
    $sg.FillRectangle((New-Object System.Drawing.SolidBrush (C '#202020')), 0, 140, 760, 160)
    $sg.DrawImage(($images | Where-Object Size -eq 128).Bitmap, 10, 150, 128, 128)
    $x = 160
    foreach ($i in ($images | Where-Object Size -lt 256)) { $sg.DrawImage($i.Bitmap, $x, 160, $i.Size, $i.Size); $x += $i.Size + 12 }
    $sg.Dispose(); $sheet.Save($PreviewPath); $sheet.Dispose()
}
foreach ($i in $images) { $i.Bitmap.Dispose() }

Write-Host "Wrote $icoPath ($($images.Count) sizes) and $png"
