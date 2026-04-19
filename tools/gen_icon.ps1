# Generates the plugin icon. 64x64 PNG with a stylized mahjong tile on a dark
# teal background, matching the MainWindow accent color.
# Run: powershell -ExecutionPolicy Bypass -File tools/gen_icon.ps1

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$size = 64
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.InterpolationMode = 'HighQualityBicubic'

# Background: dark teal circle on transparent.
$bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 12, 34, 44))
$g.FillRectangle([System.Drawing.Brushes]::Transparent, 0, 0, $size, $size)

# Rounded rect background.
$rectPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$radius = 12
$rect = New-Object System.Drawing.Rectangle 2, 2, ($size - 4), ($size - 4)
$rectPath.AddArc($rect.X, $rect.Y, $radius, $radius, 180, 90)
$rectPath.AddArc($rect.Right - $radius, $rect.Y, $radius, $radius, 270, 90)
$rectPath.AddArc($rect.Right - $radius, $rect.Bottom - $radius, $radius, $radius, 0, 90)
$rectPath.AddArc($rect.X, $rect.Bottom - $radius, $radius, $radius, 90, 90)
$rectPath.CloseAllFigures()
$g.FillPath($bg, $rectPath)

# Tile body: rounded rect inset, off-white.
$tilePath = New-Object System.Drawing.Drawing2D.GraphicsPath
$tileRect = New-Object System.Drawing.Rectangle 14, 10, 36, 44
$tileRadius = 6
$tilePath.AddArc($tileRect.X, $tileRect.Y, $tileRadius, $tileRadius, 180, 90)
$tilePath.AddArc($tileRect.Right - $tileRadius, $tileRect.Y, $tileRadius, $tileRadius, 270, 90)
$tilePath.AddArc($tileRect.Right - $tileRadius, $tileRect.Bottom - $tileRadius, $tileRadius, $tileRadius, 0, 90)
$tilePath.AddArc($tileRect.X, $tileRect.Bottom - $tileRadius, $tileRadius, $tileRadius, 90, 90)
$tilePath.CloseAllFigures()

$tileFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 245, 240, 225))
$g.FillPath($tileFill, $tilePath)

# Tile edge: subtle darker outline
$tileEdge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 180, 170, 150), 1.5)
$g.DrawPath($tileEdge, $tilePath)

# Red 1-man marker (simplified): the character "一" (one) painted in red.
# Fonts vary by system — pick a bold font with CJK glyph coverage.
$candidates = @('Yu Gothic UI', 'Microsoft YaHei', 'MS Gothic', 'SimSun', 'Arial Unicode MS')
$fontName = $null
foreach ($c in $candidates) {
  try {
    $probe = New-Object System.Drawing.Font($c, 28, [System.Drawing.FontStyle]::Bold)
    $fontName = $probe.Name
    $probe.Dispose()
    if ($fontName -eq $c) { break }
  } catch {}
}
if (-not $fontName) { $fontName = 'Arial' }

$red = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 200, 50, 45))
$teal = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 40, 130, 100))

# "A" for AI — clean, legible at 64x64.
$fontBig = New-Object System.Drawing.Font($fontName, 26, [System.Drawing.FontStyle]::Bold)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = 'Center'
$sf.LineAlignment = 'Center'
$textRect = New-Object System.Drawing.RectangleF 14, 10, 36, 28
$g.DrawString('AI', $fontBig, $red, $textRect, $sf)

# Bottom three dots (bamboo style) in teal.
$dotR = 3
$dotYs = @(44)
$dotXs = @(22, 32, 42)
foreach ($x in $dotXs) {
  $g.FillEllipse($teal, ($x - $dotR), (44 - $dotR), ($dotR * 2), ($dotR * 2))
}

$outPath = Join-Path $PSScriptRoot '..\DomanMahjongAI\images\icon.png'
$outDir = Split-Path $outPath -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()

Write-Host "wrote $outPath"
