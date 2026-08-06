param(
    [string]$SourceWhale = "",
    [string]$TitlePath = "",
    [string]$OutPath = "",
    [int]$Canvas = 1024,
    [int]$WhaleWidth = 860,
    [int]$TopMargin = 24,
    [int]$TextOffset = 64,
    [int]$TextSize = 58,
    [string]$TextColorHex = "",
    [string]$BackgroundColorHex = ""
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path (Split-Path $scriptDir -Parent) -Parent

if (-not $SourceWhale) { $SourceWhale = Join-Path $repoRoot 'whale_source.png' }
if (-not $TitlePath) { $TitlePath = Join-Path $scriptDir 'text.txt' }
if (-not $OutPath) { $OutPath = Join-Path $repoRoot 'assets\logo-with-title.png' }
if (-not $TextColorHex) { $TextColorHex = '#0F3B5E' }

$title = [System.IO.File]::ReadAllText($TitlePath, [System.Text.Encoding]::UTF8).Trim()
$src = New-Object System.Drawing.Bitmap($SourceWhale)

# Compute bounding box of non-transparent pixels (step 2 for speed)
$minX = $src.Width; $minY = $src.Height; $maxX = -1; $maxY = -1
for ($y = 0; $y -lt $src.Height; $y += 2) {
    for ($x = 0; $x -lt $src.Width; $x += 2) {
        $a = $src.GetPixel($x, $y).A
        if ($a -gt 8) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
$bboxW = $maxX - $minX
$bboxH = $maxY - $minY

$bmp = New-Object System.Drawing.Bitmap($Canvas, $Canvas, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
if ($BackgroundColorHex) {
    $g.Clear([System.Drawing.ColorTranslator]::FromHtml($BackgroundColorHex))
} else {
    $g.Clear([System.Drawing.Color]::Transparent)
}

$scale = $WhaleWidth / $bboxW
$whaleH = [int]($bboxH * $scale)
$wx = [int](($Canvas - $WhaleWidth) / 2)
$wy = $TopMargin
$srcRect = New-Object System.Drawing.Rectangle($minX, $minY, $bboxW, $bboxH)
$dstRect = New-Object System.Drawing.Rectangle($wx, $wy, $WhaleWidth, $whaleH)
$g.DrawImage($src, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)

$font = New-Object System.Drawing.Font('Microsoft YaHei UI', $TextSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$size = $g.MeasureString($title, $font)
$textY = $wy + $whaleH + $TextOffset
$textH = [int]$size.Height
$textRect = New-Object System.Drawing.RectangleF(0, $textY, $Canvas, $textH)
$textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml($TextColorHex))
$g.DrawString($title, $font, $textBrush, $textRect, $sf)

$dir = Split-Path $OutPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output ("saved: {0}  (whaleH={1}, textY={2})" -f $OutPath, $whaleH, $textY)

$sf.Dispose(); $font.Dispose(); $textBrush.Dispose(); $g.Dispose(); $bmp.Dispose(); $src.Dispose()
