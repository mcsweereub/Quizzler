# Generates src/Quizzler.App/Assets/quizzler.ico: an indigo rounded square with a white "Q".
# Multi-size, PNG-compressed ICO (supported since Windows Vista). Re-run after tweaking the design.
#   powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\Quizzler.App\Assets\quizzler.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$sizes = 16, 20, 24, 32, 48, 64, 256

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square background with a diagonal indigo -> violet gradient.
    $r = [Math]::Max(2, [int]($size * 0.22))
    $rect = New-Object System.Drawing.Rectangle 0, 0, ($size - 1), ($size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r * 2, $r * 2, 180, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Y, $r * 2, $r * 2, 270, 90)
    $path.AddArc($rect.Right - $r * 2, $rect.Bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, `
        ([System.Drawing.Color]::FromArgb(255, 79, 70, 229)), ([System.Drawing.Color]::FromArgb(255, 124, 58, 237)), 45
    $g.FillPath($brush, $path)

    # White "Q", centred.
    $font = New-Object System.Drawing.Font 'Segoe UI', ([float]($size * 0.62)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = 'Center'
    $format.LineAlignment = 'Center'
    $textRect = New-Object System.Drawing.RectangleF 0, ([float]($size * 0.02)), $size, $size
    $g.DrawString('Q', $font, [System.Drawing.Brushes]::White, $textRect, $format)

    $g.Dispose()
    $bytes = if ($size -ge 256) { ConvertTo-Png $bmp } else { ConvertTo-Dib $bmp }
    $bmp.Dispose()
    return , $bytes
}

function ConvertTo-Png($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# Classic ICO frame: BITMAPINFOHEADER + bottom-up 32bpp BGRA pixels + 1bpp AND mask (all zero; alpha does the work).
function ConvertTo-Dib($bmp) {
    $s = $bmp.Width
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $s, $s), 'ReadOnly', 'Format32bppArgb')
    $pixels = New-Object byte[] ($s * $s * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))   # height covers XOR + AND
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($pixels.Length + $maskStride * $s))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $s * 4, $s * 4) }
    $w.Write((New-Object byte[] ($maskStride * $s)))
    $w.Flush()
    return , $ms.ToArray()
}

$images = $sizes | ForEach-Object { , (New-IconPng $_) }

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)   # ICONDIR
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {                                   # ICONDIRENTRY x N
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))                  # 256 is stored as 0
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
Write-Host "Wrote $out"
