# Генерация UI/assets/icon.ico — PNG-кадры всех размеров, без растровых артефактов.
# Дизайн: тёмный скруглённый квадрат #17191D + изумрудный «N» (#3ECF8E).
param([string]$Out = "$PSScriptRoot\..\UI\assets\icon.ico")

Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [int]($size * 0.06)
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))
    $r = [Math]::Max(2, [int]($size * 0.20))

    # фон
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
    $bg.CenterColor = [System.Drawing.Color]::FromArgb(255, 0x1D, 0x20, 0x25)
    $bg.SurroundColors = @([System.Drawing.Color]::FromArgb(255, 0x14, 0x16, 0x19))
    $g.FillPath($bg, $path)

    # буква N тремя штрихами
    $em = [System.Drawing.Color]::FromArgb(255, 0x3E, 0xCF, 0x8E)
    $penW = [Math]::Max(2, [int]($size * 0.115))
    $pen = New-Object System.Drawing.Pen($em, $penW)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $x0 = [int]($size * 0.30); $x1 = [int]($size * 0.70)
    $y0 = [int]($size * 0.30); $y1 = [int]($size * 0.70)
    $g.DrawLine($pen, $x0, $y1, $x0, $y0)
    $g.DrawLine($pen, $x1, $y0, $x1, $y1)
    $g.DrawLine($pen, $x0, $y0, $x1, $y1)

    # акцентная точка
    $dotR = [Math]::Max(1, [int]($size * 0.055))
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x7C, 0xF0, 0xB8))
    $g.FillEllipse($brush, ($x1 - $dotR), ($y0 - $dotR), (2 * $dotR), (2 * $dotR))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    ,($ms.ToArray())
}

$frames = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @{}
foreach ($f in $frames) { $pngs[$f] = New-IconPng $f }

# сборка ICO
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type = icon
$bw.Write([uint16]$frames.Count)  # count
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $data = $pngs[$f]
    $dim = if ($f -ge 256) { [byte]0 } else { [byte]$f }
    $bw.Write($dim)       # width
    $bw.Write($dim)       # height
    $bw.Write([byte]0)    # colors
    $bw.Write([byte]0)    # reserved
    $bw.Write([uint16]1)  # planes
    $bw.Write([uint16]32) # bpp
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write($pngs[$f]) }
$bw.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $ms.ToArray())
$bw.Dispose()
Write-Host "ICO записан: $([IO.Path]::GetFullPath($Out)) ($($ms.Length) байт)"
