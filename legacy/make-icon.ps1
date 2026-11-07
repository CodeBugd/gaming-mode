# Draws GamingMode.ico (green badge + white game controller, multi-size) and a preview PNG.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'GamingMode.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-Frame([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([Drawing.Color]::Transparent)

    # rounded square with a green gradient
    $pad = [math]::Max(0.5, $s * 0.04); $r = $s * 0.24; $w = $s - 2 * $pad
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2*$r, 2*$r, 180, 90)
    $path.AddArc($pad + $w - 2*$r, $pad, 2*$r, 2*$r, 270, 90)
    $path.AddArc($pad + $w - 2*$r, $pad + $w - 2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc($pad, $pad + $w - 2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object Drawing.RectangleF($pad, $pad, $w, $w)
    $brush = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(40, 230, 120), [Drawing.Color]::FromArgb(0, 140, 70), 90.0)
    $g.FillPath($brush, $path)

    # controller glyph (Segoe Fluent Icons on Win11, MDL2 fallback)
    $fontName = if ((New-Object Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }
    $font = New-Object Drawing.Font($fontName, [single]($s * 0.60), [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $glyphRect = New-Object Drawing.RectangleF(0, ($s * 0.03), $s, $s)
    $shadow = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, 0, 40, 20))
    $g.DrawString([string][char]0xE7FC, $font, $shadow, (New-Object Drawing.RectangleF(0, ($s * 0.06), $s, $s)), $sf)
    $g.DrawString([string][char]0xE7FC, $font, [Drawing.Brushes]::White, $glyphRect, $sf)
    $g.Dispose()
    $bmp
}

# ICO file with PNG-compressed frames
$pngs = foreach ($s in $sizes) {
    $b = New-Frame $s; $ms = New-Object IO.MemoryStream
    $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $b.Save((Join-Path $env:TEMP 'GamingMode-preview.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $b.Dispose(); ,$ms.ToArray()
}
$fs = [IO.File]::Create($out); $bw = New-Object IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Wrote $out"
