# Writes src\MozaHud.ico: the app logo (orange rounded square with a white graph line, as drawn by
# TrayApp.MakeAppIcon) at 16-256 px, so the exe and shortcuts to it show the logo. build.ps1 embeds it.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$out = Join-Path $root 'src\MozaHud.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Draw([int]$n) {
    $bmp = New-Object Drawing.Bitmap $n, $n, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $k = $n / 32.0
    $w = $n - 1; $d = 14 * $k
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc(0, 0, $d, $d, 180, 90); $p.AddArc($w - $d, 0, $d, $d, 270, 90)
    $p.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $p.AddArc(0, $w - $d, $d, $d, 90, 90); $p.CloseFigure()
    $b = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 122, 26))
    $g.FillPath($b, $p)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([Math]::Max(1.6, 2.6 * $k))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $pts = @(@(5, 22), @(11, 14), @(16, 19), @(21, 9), @(27, 15)) | ForEach-Object { New-Object Drawing.PointF ($_[0] * $k), ($_[1] * $k) }
    $g.DrawLines($pen, [Drawing.PointF[]]$pts)
    $g.Dispose(); $b.Dispose(); $pen.Dispose(); $p.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

# ICO file: header, one directory entry per size, then the PNG images
$images = foreach ($s in $sizes) { , (Draw $s) }
$fs = [IO.File]::Create($out)
$bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Wrote $out"
