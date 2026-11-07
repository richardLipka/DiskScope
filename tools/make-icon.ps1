# Generates src/DiskScope/DiskScope.ico (a drive with a usage ring) in several sizes.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\DiskScope\DiskScope.ico'
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(1, $s / 16)
    $rect = New-Object System.Drawing.RectangleF ($m), ($m), ($s - 2 * $m), ($s - 2 * $m)
    $track = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(222, 230, 242))
    $g.FillEllipse($track, $rect)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(79, 140, 217))
    $g.FillPie($blue, $rect.X, $rect.Y, $rect.Width, $rect.Height, -90, 250)
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(214, 84, 66))
    $g.FillPie($red, $rect.X, $rect.Y, $rect.Width, $rect.Height, 160, 60)
    $hole = $s * 0.30
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillEllipse($white, ($s - $hole) / 2, ($s - $hole) / 2, $hole, $hole)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Output "Wrote $out"
