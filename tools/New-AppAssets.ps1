#Requires -Version 7.2
[CmdletBinding()]
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    # Optional icon pack: PNGs at these sizes plus the .ico and .svg, written to this folder.
    [string]$ExportDirectory,
    [int[]]$ExportSizes = @(16, 24, 32, 48, 64, 128, 256, 512, 1024)
)
# Draws the original logo from edition.json and writes native\Assets\AppLogo.png (256 px) and AppIcon.ico (16-256 px).
# logo.style 'monogram' (default): rounded square with logo.text. 'device': laptop-and-spark glyph from
# tools\LogoGeometry.psd1 on a logo.background -> logo.backgroundEnd gradient. tools\Sync-Edition.ps1 writes AppLogo.svg
# from the same geometry and runs this script in its own process.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Import-Module (Join-Path $PSScriptRoot 'Edition.psm1') -Force
$edition = Read-Edition -Path (Join-Path $Root 'edition.json')
$style = Get-LogoStyle -Edition $edition
$text = [string]$edition.logo.text
$scale = @{ 1 = 0.586; 2 = 0.469; 3 = 0.34; 4 = 0.266 }[$text.Length]
$geometry = if ($style -eq 'device') { Get-LogoGeometry } else { @() }
$destination = Join-Path $Root 'native\Assets'
$null = New-Item -ItemType Directory -Path $destination -Force

function Get-Color([string]$Hex, [double]$Opacity = 1) {
    $color = [System.Drawing.ColorTranslator]::FromHtml($Hex)
    return [System.Drawing.Color]::FromArgb([int][math]::Round(255 * $Opacity), $color)
}

function New-RoundedRectangle([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    [single]$d = [math]::Min($Radius * 2, [math]::Min($Width, $Height))
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return ,$path
}

function Draw-Monogram($Graphics, [int]$Size) {
    [single]$margin = $Size * 0.035
    [single]$side = $Size - 2 * $margin
    $path = New-RoundedRectangle $margin $margin $side $side ($Size * 0.175)
    $fill = [System.Drawing.SolidBrush]::new((Get-Color $edition.logo.background))
    $ink = [System.Drawing.SolidBrush]::new((Get-Color $edition.logo.foreground))
    $format = [System.Drawing.StringFormat]::new()
    $font = [System.Drawing.Font]::new('Segoe UI', [single]($Size * $scale), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $Graphics.FillPath($fill, $path)
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $Graphics.DrawString($text, $font, $ink, [System.Drawing.RectangleF]::new(0, [single](-$Size * 0.015), $Size, $Size), $format)
    } finally {
        $font.Dispose(); $format.Dispose(); $ink.Dispose(); $fill.Dispose(); $path.Dispose()
    }
}

function New-Paint([string]$Kind, [int]$Size, [double]$Opacity = 1) {
    $end = Get-Field $edition.logo 'backgroundEnd'
    if ($Kind -eq 'tile' -and $end) {
        return [System.Drawing.Drawing2D.LinearGradientBrush]::new([System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($Size, $Size),
            (Get-Color $edition.logo.background $Opacity), (Get-Color $end $Opacity))
    }
    $hex = if ($Kind -eq 'tile') { $edition.logo.background } else { $edition.logo.foreground }
    return [System.Drawing.SolidBrush]::new((Get-Color $hex $Opacity))
}

function Draw-Device($Graphics, [int]$Size) {
    [single]$k = $Size / 256
    foreach ($shape in $geometry) {
        if ($shape.ContainsKey('MinSize') -and $Size -lt $shape.MinSize) { continue }
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        if ($shape.Kind -eq 'rect') {
            $path.Dispose()
            $path = New-RoundedRectangle ($shape.X * $k) ($shape.Y * $k) ($shape.W * $k) ($shape.H * $k) ($shape.R * $k)
        } elseif ($shape.Kind -eq 'spark') {
            $spark = Get-SparkSegments -X $shape.X -Y $shape.Y -R $shape.R -Waist $shape.Waist
            $from = [System.Drawing.PointF]::new($spark.Start[0] * $k, $spark.Start[1] * $k)
            foreach ($s in $spark.Segments) {
                $to = [System.Drawing.PointF]::new($s[4] * $k, $s[5] * $k)
                $path.AddBezier($from, [System.Drawing.PointF]::new($s[0] * $k, $s[1] * $k), [System.Drawing.PointF]::new($s[2] * $k, $s[3] * $k), $to)
                $from = $to
            }
            $path.CloseFigure()
        } else {
            throw "LogoGeometry.psd1: unknown shape kind '$($shape.Kind)'"
        }
        $opacity = if ($shape.ContainsKey('FillOpacity')) { $shape.FillOpacity } else { 1 }
        $brush = New-Paint $shape.Fill $Size $opacity
        try {
            $Graphics.FillPath($brush, $path)
            if ($shape.ContainsKey('Stroke')) {
                [single]$width = [math]::Max($shape.StrokeWidth * $k, $(if ($shape.ContainsKey('MinStrokePixels')) { $shape.MinStrokePixels } else { 0 }))
                $strokeBrush = New-Paint $shape.Stroke $Size
                $pen = [System.Drawing.Pen]::new($strokeBrush, $width)
                $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                try { $Graphics.DrawPath($pen, $path) } finally { $pen.Dispose(); $strokeBrush.Dispose() }
            }
        } finally {
            $brush.Dispose(); $path.Dispose()
        }
    }
}

function New-LogoPng([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        if ($style -eq 'device') { $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality }
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([System.Drawing.Color]::Transparent)
        if ($style -eq 'device') { Draw-Device $graphics $Size } else { Draw-Monogram $graphics $Size }
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

function Write-Ico([string]$Path, [object[]]$Images) {
    $file = [System.IO.File]::Create($Path)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$Images.Count)
        [uint32]$offset = 6 + 16 * $Images.Count
        foreach ($image in $Images) {
            $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length); $writer.Write($offset)
            $offset += $image.Bytes.Length
        }
        foreach ($image in $Images) { $writer.Write([byte[]]$image.Bytes) }
    } finally {
        $writer.Dispose(); $file.Dispose()
    }
}

$images = foreach ($size in 16, 24, 32, 48, 64, 128, 256) { [pscustomobject]@{ Size = $size; Bytes = (New-LogoPng $size) } }
[System.IO.File]::WriteAllBytes((Join-Path $destination 'AppLogo.png'), ($images | Where-Object Size -eq 256).Bytes)
Write-Ico (Join-Path $destination 'AppIcon.ico') $images
$label = if ($style -eq 'device') { 'laptop-and-spark' } else { "'$text' monogram" }
Write-Host "Created original $label assets in $destination"

if ($ExportDirectory) {
    $null = New-Item -ItemType Directory -Path $ExportDirectory -Force
    $name = $edition.codeName
    foreach ($size in $ExportSizes | Sort-Object -Unique) {
        [System.IO.File]::WriteAllBytes((Join-Path $ExportDirectory "$name-$size.png"), (New-LogoPng $size))
    }
    Copy-Item -LiteralPath (Join-Path $destination 'AppIcon.ico') -Destination (Join-Path $ExportDirectory "$name.ico") -Force
    Write-TextFile (Join-Path $ExportDirectory "$name.svg") (Get-LogoSvg -Edition $edition)
    Write-Host "Exported icon pack ($(@($ExportSizes).Count) PNG sizes, .ico, .svg) to $ExportDirectory"
}
