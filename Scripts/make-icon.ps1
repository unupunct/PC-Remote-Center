# Generates the application icon (monitor glyph on a cyan->purple gradient) as a multi-size .ico (PNG-compressed frames).
param([string]$Out = (Join-Path $PSScriptRoot '..\WindowsServer\RemoteControlServer\Assets\app.ico'))
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $r = [int]($size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2; $rect = New-Object System.Drawing.Rectangle 0, 0, ($size - 1), ($size - 1)
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90); $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90); $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90); $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 14, 165, 233)), ([System.Drawing.Color]::FromArgb(255, 124, 58, 237)), 45
    $g.FillPath($grad, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.5, $size * 0.06))
    $mx = $size * 0.2; $my = $size * 0.24; $mw = $size * 0.6; $mh = $size * 0.42
    $g.DrawRectangle($pen, $mx, $my, $mw, $mh)
    $g.DrawLine($pen, $size * 0.5, $my + $mh, $size * 0.5, $size * 0.76)
    $g.DrawLine($pen, $size * 0.34, $size * 0.76, $size * 0.66, $size * 0.76)
    $g.DrawLine($pen, $mx + $mw * 0.18, $my + $mh * 0.3, $mx + $mw * 0.36, $my + $mh * 0.5)
    $g.DrawLine($pen, $mx + $mw * 0.36, $my + $mh * 0.5, $mx + $mw * 0.18, $my + $mh * 0.7)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { , (New-Frame $_) }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close(); $fs.Close()
Write-Host "Wrote $Out"
