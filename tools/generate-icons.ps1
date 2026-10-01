<#
.SYNOPSIS
  Regenerates assets\DevOverlay.ico (16/24/32/48/64/128/256, transparent, PNG-compressed entries) from assets\DevOverlay-logo.png.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$source = Join-Path $root 'assets\DevOverlay-logo.png'
$target = Join-Path $root 'assets\DevOverlay.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256

$original = [System.Drawing.Image]::FromFile($source)
# Premultiplied alpha while resampling avoids dark/light fringes around the soft glow.
$pre = New-Object System.Drawing.Bitmap $original.Width, $original.Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
$g = [System.Drawing.Graphics]::FromImage($pre); $g.Clear([System.Drawing.Color]::Transparent)
$g.DrawImage($original, 0, 0, $original.Width, $original.Height); $g.Dispose()

$images = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $gr = [System.Drawing.Graphics]::FromImage($bmp)
    $gr.Clear([System.Drawing.Color]::Transparent)
    $gr.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gr.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $gr.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gr.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $gr.DrawImage($pre, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $gr.Dispose()
    $out = $bmp.Clone((New-Object System.Drawing.Rectangle 0, 0, $size, $size), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bmp.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $out.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $out.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
}
$pre.Dispose(); $original.Dispose()

$fs = [System.IO.File]::Create($target)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$i.Bytes.Length); $w.Write([uint32]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $w.Write($i.Bytes) }
$w.Dispose(); $fs.Dispose()
"{0}: {1} sizes, {2:N0} bytes" -f $target, $images.Count, (Get-Item $target).Length
