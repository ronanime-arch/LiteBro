# Draws the LiteBro icon (a white feather on a violet-blue rounded square) into a multi-size .ico.
# Sizes up to 128 are stored as 32-bit DIBs, 256 as PNG, which is what Windows expects.
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\src\Browser\app.ico'),
    [string]$Preview
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedSquare([single]$x, [single]$size, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $x, $d, $d, 180, 90)
    $p.AddArc($x + $size - $d, $x, $d, $d, 270, 90)
    $p.AddArc($x + $size - $d, $x + $size - $d, $d, $d, 0, 90)
    $p.AddArc($x, $x + $size - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Draw-Icon([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [single][math]::Floor($S / 32)
    $box = New-RoundedSquare $inset ($S - 2 * $inset) ([single]($S * 0.22))
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        [System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($S, $S)),
        ([System.Drawing.Color]::FromArgb(0x7C, 0x3A, 0xED)), ([System.Drawing.Color]::FromArgb(0x25, 0x63, 0xEB))
    $g.FillPath($bg, $box)

    # Feather in its own frame: quill end at x=0, tip at x=1; tilted so the tip points top-right
    $small = $S -le 24
    $w = if ($small) { 1.25 } else { 1.0 }
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate($S / 2, $S / 2)
    $m.Rotate(-45)
    $m.Scale($S * 0.80, $S * 0.80)
    $m.Translate(-0.55, 0)

    # PointF, not bare numbers: PowerShell would pick the integer overload and round the curve away
    $vane = New-Object System.Drawing.Drawing2D.GraphicsPath
    $vane.AddBezier([System.Drawing.PointF]::new(1.0, 0), [System.Drawing.PointF]::new(0.75, -0.30 * $w),
        [System.Drawing.PointF]::new(0.30, -0.28 * $w), [System.Drawing.PointF]::new(0.18, 0))
    $vane.AddBezier([System.Drawing.PointF]::new(0.18, 0), [System.Drawing.PointF]::new(0.30, 0.22 * $w),
        [System.Drawing.PointF]::new(0.75, 0.24 * $w), [System.Drawing.PointF]::new(1.0, 0))
    $vane.CloseFigure()
    $vane.Transform($m)
    $g.FillPath([System.Drawing.Brushes]::White, $vane)

    $quill = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][math]::Max(1.2, $S * 0.055))
    $quill.StartCap = 'Round'; $quill.EndCap = 'Round'
    $pts = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(0.02, 0), [System.Drawing.PointF]::new(0.30, 0))
    $m.TransformPoints($pts)
    $g.DrawLine($quill, $pts[0], $pts[1])

    # Shaft and barb splits, drawn with the background brush so they read as gaps
    $cuts = @()
    if ($S -ge 40) { $cuts += , @(0.22, 0, 0.88, 0) }
    if ($S -ge 48) { $cuts += , @(0.73, -0.25, 0.60, -0.04); $cuts += , @(0.50, 0.22, 0.40, 0.04) }
    if ($cuts.Count) {
        $cut = New-Object System.Drawing.Pen $bg, ([single]($S * 0.024))
        $cut.StartCap = 'Round'; $cut.EndCap = 'Round'
        foreach ($c in $cuts) {
            $p = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new($c[0], $c[1]), [System.Drawing.PointF]::new($c[2], $c[3]))
            $m.TransformPoints($p)
            $g.DrawLine($cut, $p[0], $p[1])
        }
    }
    $g.Dispose()
    $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $S = $bmp.Width
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $S, $S), 'ReadOnly', 'Format32bppArgb')
    $px = New-Object byte[] ($data.Stride * $S)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
    $stride = $data.Stride
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    # BITMAPINFOHEADER; the height counts the colour image and the AND mask together
    $bw.Write([int]40); $bw.Write([int]$S); $bw.Write([int]($S * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $S - 1; $y -ge 0; $y--) { $bw.Write($px, $y * $stride, $S * 4) }
    # AND mask, 1 bit per pixel, rows padded to 4 bytes: set where fully transparent
    $maskStride = [int]([math]::Ceiling($S / 32.0) * 4)
    for ($y = $S - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $S; $x++) {
            if ($px[$y * $stride + $x * 4 + 3] -eq 0) { $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7)) }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    , $ms.ToArray()  # the comma keeps PowerShell from unrolling the byte array
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($S in $sizes) {
    $bmp = Draw-Icon $S
    if ($S -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        [byte[]]$bytes = $ms.ToArray()
    } else {
        [byte[]]$bytes = Get-DibBytes $bmp
    }
    [pscustomobject]@{ Size = $S; Bytes = $bytes; Bitmap = $bmp }
}

$outPath = [System.IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $outPath) | Out-Null
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { 0 } else { $i.Size }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$i.Bytes.Length); $bw.Write([int]$offset)
    $offset += $i.Bytes.Length
}
foreach ($i in $images) { $bw.Write($i.Bytes) }
$bw.Close()
"icon: $outPath ($((Get-Item $outPath).Length) bytes)"

if ($Preview) {
    # Real pixels, enlarged without smoothing, on a light and a dark strip
    $show = @(@(16, 4), @(24, 4), @(32, 3), @(48, 2), @(256, 1))
    $sheet = New-Object System.Drawing.Bitmap 1000, 640
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = 'NearestNeighbor'
    $g.PixelOffsetMode = 'Half'
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(243, 243, 243))), 0, 0, 1000, 320)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 320, 1000, 320)
    foreach ($band in 0, 320) {
        $x = 20
        foreach ($s in $show) {
            $b = ($images | Where-Object Size -eq $s[0]).Bitmap
            $z = $s[0] * $s[1]
            $g.DrawImage($b, $x, $band + 30, $z, $z)
            $x += $z + 30
        }
    }
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    "preview: $Preview"
}
