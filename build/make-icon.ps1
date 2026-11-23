<#
.SYNOPSIS
  Regenerates the application icon (src/LogLens.App/Assets/AppIcon.ico and AppIcon.png).

.DESCRIPTION
  Resamples the master logo (build/logo.png, square with a transparent background) into a
  multi-size ICO and a 256 px PNG. To change the logo, replace build/logo.png and rerun.

.EXAMPLE
  pwsh build/make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot 'logo.png'),
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src/LogLens.App/Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$master = [System.Drawing.Image]::FromFile((Resolve-Path $Source))

function New-IconBitmap([int]$size) {
    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.SmoothingMode = 'AntiAlias'
    $g.CompositingMode = 'SourceCopy'
    $g.DrawImage($master, 0, 0, $size, $size)
    $g.Dispose()
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
    [pscustomobject]@{ Size = $s; Bytes = (ConvertTo-PngBytes $bmp) }
    $bmp.Dispose()
}
$master.Dispose()

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

Write-Host "Wrote $icoPath ($($images.Count) sizes) and $png"
