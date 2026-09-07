# Draws the SemanticStart mark and packs it into a multi-resolution .ico.
#
# The mark is the one the overlay header draws in XAML: a rounded square in the brand accent with a
# white magnifier on it. It is drawn here rather than exported from a design tool so that every size
# is rendered at its own resolution instead of being resampled from a single bitmap, which is what
# keeps the 16px tray icon from turning to mush.
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\SemanticStart.App\Assets\SemanticStart.ico'))
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

# The light-theme accent the app falls back to when the user has no DWM accent colour. The icon is
# baked at build time and shown on surfaces the app does not own, so it cannot follow the live theme.
$accent = [Drawing.Color]::FromArgb(0xFF, 0x00, 0x5F, 0xB8)

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-Mark([int]$size) {
    $bmp = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Small sizes get a proportionally thicker stroke and no padding. A weight that reads correctly
    # at 256px lands on half a pixel at 16px and dissolves into the antialiasing.
    $small = $size -lt 32
    $s = [double]$size

    # Rounded square, at the 0.22 corner ratio the overlay uses (CornerRadius 4 on 18px).
    $pad = $(if ($small) { 0.0 } else { $s * 0.03 })
    $box = $s - (2 * $pad)
    $d = ($box * 0.22) * 2
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $box - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $box - $d, $pad + $box - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $box - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object Drawing.SolidBrush($accent)
    $g.FillPath($brush, $path)

    # Magnifier: ring plus handle, in white.
    $cx = $s * 0.435
    $cy = $s * 0.415
    $rad = $s * $(if ($small) { 0.215 } else { 0.20 })
    $stroke = $s * $(if ($small) { 0.10 } else { 0.085 })
    $pen = New-Object Drawing.Pen([Drawing.Color]::White, $stroke)
    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $g.DrawEllipse($pen, $cx - $rad, $cy - $rad, $rad * 2, $rad * 2)

    # Start the handle on the ring's edge at 45 degrees so it meets the circle instead of floating.
    $k = [Math]::Sqrt(0.5)
    $g.DrawLine($pen, $cx + ($rad * $k), $cy + ($rad * $k), $s * 0.78, $s * 0.76)

    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

# Entries up to 64px are stored as classic BMP (DIB) and the two large ones as PNG. All-PNG is
# tempting and half the size, but GDI+ cannot decode a PNG entry back into a bitmap, so anything
# loading the icon through System.Drawing - including the tray - fails on it. BMP is universally
# readable; PNG is only used at the sizes where the DIB would cost hundreds of KB.
function ConvertTo-IconEntry([Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    if ($size -ge 128) {
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        return ,$ms.ToArray()
    }

    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. The height is doubled because an icon DIB stores the colour bitmap and the
    # AND mask stacked in one image, even when 32bpp alpha makes the mask redundant.
    $w.Write([uint32]40)
    $w.Write([int32]$size)
    $w.Write([int32]($size * 2))
    $w.Write([uint16]1)
    $w.Write([uint16]32)
    $w.Write([uint32]0)             # BI_RGB
    $w.Write([uint32]($size * $size * 4))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)

    $rect = New-Object Drawing.Rectangle(0, 0, $size, $size)
    $data = $bmp.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($size * 4)
    try {
        # DIBs are bottom-up.
        for ($y = $size - 1; $y -ge 0; $y--) {
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + ($y * $data.Stride)), $row, 0, $row.Length)
            $w.Write($row)
        }
    }
    finally { $bmp.UnlockBits($data) }

    # AND mask: all zero, meaning "take the colour bitmap everywhere" and let alpha do the shaping.
    $maskStride = [Math]::Ceiling($size / 32.0) * 4
    $w.Write((New-Object byte[] ($maskStride * $size)))

    $w.Flush()
    # The leading comma stops PowerShell unrolling the array into the pipeline, which would hand
    # the caller a collection of individual bytes and, worse, still report the right Length.
    return ,$ms.ToArray()
}

$images = foreach ($size in $sizes) {
    $bmp = New-Mark $size
    $bytes = ConvertTo-IconEntry $bmp
    $bmp.Dispose()
    [pscustomobject]@{ Size = $size; Bytes = $bytes }
}

$fs = [IO.File]::Create($out)
$w = New-Object IO.BinaryWriter($fs)
$w.Write([uint16]0)                 # reserved
$w.Write([uint16]1)                 # type: icon
$w.Write([uint16]$images.Count)

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $dim = [byte]$(if ($img.Size -ge 256) { 0 } else { $img.Size })   # 0 means 256
    $w.Write($dim)                  # width
    $w.Write($dim)                  # height
    $w.Write([byte]0)               # palette entries
    $w.Write([byte]0)               # reserved
    $w.Write([uint16]1)             # colour planes
    $w.Write([uint16]32)            # bits per pixel
    $w.Write([uint32]$img.Bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write([byte[]]$img.Bytes) }
$w.Dispose(); $fs.Dispose()

Write-Host "wrote $out ($((Get-Item $out).Length) bytes)"
foreach ($img in $images) { Write-Host ("  {0,3}px  {1,7} bytes" -f $img.Size, $img.Bytes.Length) }
