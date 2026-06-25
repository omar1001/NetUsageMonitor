# Generates a multi-resolution .ico for NetUsage Monitor (blue->teal rounded square
# with three ascending white usage bars). Re-run to regenerate Assets/app.ico.
param(
    [string]$OutPath = (Join-Path $PSScriptRoot "..\src\NetUsageMonitor\Assets\app.ico")
)

Add-Type -AssemblyName System.Drawing

function Render-Logo([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1, [int]($s * 0.06))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
    $radius = [Math]::Max(2, [int]($s * 0.22))
    $d = $radius * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $c1 = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
    $c2 = [System.Drawing.Color]::FromArgb(255, 16, 185, 129)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 45)
    $g.FillPath($brush, $path)

    $wb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    $barW = [Math]::Max(1, [int]($s * 0.13))
    $gap = [Math]::Max(1, [int]($s * 0.07))
    $baseY = [int]($s * 0.72)
    $x = [int]($s * 0.26)
    foreach ($h in @(0.18, 0.30, 0.44)) {
        $bh = [Math]::Max(1, [int]($s * $h))
        $r = New-Object System.Drawing.Rectangle($x, ($baseY - $bh), $barW, $bh)
        $g.FillRectangle($wb, $r)
        $x += $barW + $gap
    }

    $brush.Dispose(); $wb.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = @(16, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = Render-Logo $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , ($ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$dir = Split-Path -Parent $OutPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$fs = New-Object System.IO.FileStream($OutPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $pngs[$i]
    $wh = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$wh); $bw.Write([byte]$wh); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngs) { $bw.Write($data) }
$bw.Flush(); $fs.Close()
Write-Host "Wrote icon: $OutPath ($((Get-Item $OutPath).Length) bytes)"
