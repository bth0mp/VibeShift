# make-icon.ps1 - draws assets\VibeShift.ico: a purple gradient rounded square with a white bolt.
# Windows PowerShell 5.1. Run from anywhere: powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\assets\VibeShift.ico'))
$sizes = 16, 24, 32, 48, 256
$from = [System.Drawing.ColorTranslator]::FromHtml('#7c3aed')
$to = [System.Drawing.ColorTranslator]::FromHtml('#c084fc')
# Bolt outline on a 256 grid
$boltPoints = @(@(148, 24), @(68, 142), @(122, 142), @(106, 232), @(188, 112), @(134, 112))

$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $d = [single]($s * 0.44)
    $w = [single]($s - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), $from, $to
    $g.FillPath($brush, $path)

    $k = $s / 256.0
    $bolt = [System.Drawing.PointF[]]($boltPoints | ForEach-Object { New-Object System.Drawing.PointF ([single]($_[0] * $k)), ([single]($_[1] * $k)) })
    $g.FillPolygon([System.Drawing.Brushes]::White, $bolt)

    $g.Dispose(); $brush.Dispose(); $path.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # Large frame as PNG (Vista+), small frames as classic 32-bit DIBs every loader understands.
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
        $data = $bmp.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $pixels = New-Object byte[] ($data.Stride * $s)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        $bmp.UnlockBits($data)
        $maskRow = [int]([math]::Floor(($s + 31) / 32) * 4)
        $mw = New-Object System.IO.BinaryWriter $ms
        $mw.Write([uint32]40); $mw.Write([int32]$s); $mw.Write([int32](2 * $s))
        $mw.Write([uint16]1); $mw.Write([uint16]32); $mw.Write([uint32]0)
        $mw.Write([uint32]($s * $s * 4 + $maskRow * $s)); $mw.Write([int32]0); $mw.Write([int32]0); $mw.Write([uint32]0); $mw.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { $mw.Write($pixels, $y * $data.Stride, $s * 4) }   # bottom-up BGRA
        $mw.Write((New-Object byte[] ($maskRow * $s)))                                          # AND mask unused with alpha
        $mw.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: ICONDIR, one ICONDIRENTRY per size, then the PNG bytes.
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $len = $pngs[$i].Length
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"wrote $out"
