#Requires -Version 7.2
[CmdletBinding()]
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
# Draws the original rounded-square monogram from edition.json (logo.text/background/foreground) and writes
# native\Assets\AppLogo.png (256 px) and AppIcon.ico (16-256 px). tools\Sync-Edition.ps1 also writes AppLogo.svg.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$edition = Get-Content -LiteralPath (Join-Path $Root 'edition.json') -Raw -Encoding utf8 | ConvertFrom-Json
$text = [string]$edition.logo.text
if ($text -notmatch '^[A-Za-z0-9]{1,4}$') { throw 'edition.json logo.text must be 1-4 letters or digits.' }
$scale = @{ 1 = 0.586; 2 = 0.469; 3 = 0.34; 4 = 0.266 }[$text.Length]
$destination = Join-Path $Root 'native\Assets'
$null = New-Item -ItemType Directory -Path $destination -Force

function New-LogoImage([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $fill = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($edition.logo.background))
    $ink = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($edition.logo.foreground))
    $format = [System.Drawing.StringFormat]::new()
    $font = $null
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.Clear([System.Drawing.Color]::Transparent)
        [single]$margin = $Size * 0.035
        [single]$diameter = $Size * 0.35
        [single]$edge = $Size - $margin - $diameter
        $path.AddArc($margin, $margin, $diameter, $diameter, 180, 90)
        $path.AddArc($edge, $margin, $diameter, $diameter, 270, 90)
        $path.AddArc($edge, $edge, $diameter, $diameter, 0, 90)
        $path.AddArc($margin, $edge, $diameter, $diameter, 90, 90)
        $path.CloseFigure()
        $graphics.FillPath($fill, $path)
        $font = [System.Drawing.Font]::new('Segoe UI', [single]($Size * $scale),
            [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rectangle = [System.Drawing.RectangleF]::new(0, [single](-$Size * 0.015), $Size, $Size)
        $graphics.DrawString($text, $font, $ink, $rectangle, $format)
        return ,$bitmap
    } catch {
        $bitmap.Dispose()
        throw
    } finally {
        if ($font) { $font.Dispose() }
        $format.Dispose()
        $ink.Dispose()
        $fill.Dispose()
        $path.Dispose()
        $graphics.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes) {
    $bitmap = New-LogoImage $size
    $stream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($size -eq 256) { [System.IO.File]::WriteAllBytes((Join-Path $destination 'AppLogo.png'), $stream.ToArray()) }
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    } finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

$file = [System.IO.File]::Create((Join-Path $destination 'AppIcon.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$images.Count)
    [uint32]$offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Bytes.Length)
        $writer.Write($offset)
        $offset += $image.Bytes.Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
} finally {
    $writer.Dispose()
    $file.Dispose()
}
Write-Host "Created original '$text' monogram assets in $destination"
