<#
.SYNOPSIS
Renders the live demos' synthetic media (speech WAVs and scanned-document PNGs) from a JSON spec.

.DESCRIPTION
Runs under Windows PowerShell 5.1, which provides System.Speech (Windows text-to-speech) and System.Drawing.
All text comes from the spec written by demos.py, so the speech script is also the accuracy reference.
Output goes to the requested folder (the service uses .runtime\demo-media); nothing here is committed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Spec,
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
Add-Type -AssemblyName System.Drawing

$data = Get-Content -LiteralPath $Spec -Raw -Encoding UTF8 | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-FontFamilyName([string[]]$Preferred) {
    $installed = (New-Object System.Drawing.Text.InstalledFontCollection).Families | ForEach-Object Name
    foreach ($name in $Preferred) { if ($installed -contains $name) { return $name } }
    return 'Arial'
}

function Save-Speech($item, [string]$path) {
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $voices = @($synth.GetInstalledVoices() | Where-Object Enabled | ForEach-Object VoiceInfo |
            Sort-Object @{ Expression = { $_.Culture.Name -ne 'en-US' } }, Name)
        if (-not $voices) { throw 'No Windows text-to-speech voice is installed.' }
        $byGender = @{
            female = ($voices | Where-Object Gender -eq 'Female' | Select-Object -First 1)
            male   = ($voices | Where-Object Gender -eq 'Male' | Select-Object -First 1)
        }
        $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo([int]$item.rate,
            [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
        $builder = New-Object System.Speech.Synthesis.PromptBuilder
        $used = New-Object System.Collections.Generic.List[string]
        foreach ($segment in $item.segments) {
            $voice = $byGender[[string]$segment.voice]
            if (-not $voice) { $voice = $voices[0] }
            if (-not $used.Contains($voice.Name)) { $used.Add($voice.Name) }
            $builder.StartVoice($voice)
            $builder.AppendText([string]$segment.text)
            $builder.EndVoice()
            $builder.AppendBreak([TimeSpan]::FromMilliseconds([int]$segment.pause_ms))
        }
        $synth.SetOutputToWaveFile($path, $format)
        $synth.Speak($builder)
        $synth.SetOutputToNull()
        return , $used.ToArray()
    } finally { $synth.Dispose() }
}

function New-Canvas([int]$width, [int]$height) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::FromArgb(250, 248, 241))
    return $bitmap, $g
}

function Save-Rotated($bitmap, [double]$angle, [string]$path) {
    $out = New-Object System.Drawing.Bitmap $bitmap.Width, $bitmap.Height
    $g = [System.Drawing.Graphics]::FromImage($out)
    try {
        $g.Clear([System.Drawing.Color]::FromArgb(236, 236, 232))
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.TranslateTransform($bitmap.Width / 2, $bitmap.Height / 2)
        $g.RotateTransform($angle)
        $g.TranslateTransform(-$bitmap.Width / 2, -$bitmap.Height / 2)
        $g.DrawImage($bitmap, 0, 0, $bitmap.Width, $bitmap.Height)
        $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $g.Dispose(); $out.Dispose() }
}

function Save-Form($item, [string]$path) {
    $sans = Get-FontFamilyName @('Segoe UI', 'Arial')
    $hand = Get-FontFamilyName @('Segoe Print', 'Ink Free', 'Segoe Script', 'Segoe UI')
    $bitmap, $g = New-Canvas ([int]$item.width) ([int]$item.height)
    try {
        $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(30, 34, 40))
        $muted = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(96, 102, 110))
        $pen = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(24, 52, 120))
        $rule = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 154, 160)), 1.5
        $titleFont = New-Object System.Drawing.Font $sans, 26, ([System.Drawing.FontStyle]::Bold)
        $subFont = New-Object System.Drawing.Font $sans, 12
        $labelFont = New-Object System.Drawing.Font $sans, 10, ([System.Drawing.FontStyle]::Bold)
        $typedFont = New-Object System.Drawing.Font $sans, 16
        $handFont = New-Object System.Drawing.Font $hand, 17
        $margin = 60
        $g.DrawString([string]$item.title, $titleFont, $ink, $margin, 50)
        $g.DrawString([string]$item.subtitle, $subFont, $muted, $margin + 2, 98)
        $g.DrawLine($rule, $margin, 132, $item.width - $margin, 132)
        $y = 160
        $columnWidth = ($item.width - 2 * $margin - 20) / 2
        $index = 0
        foreach ($row in $item.rows) {
            $wide = [bool]$row.wide
            if ($wide -and ($index % 2) -eq 1) { $index++; $y += 96 }
            $x = if ($wide -or ($index % 2) -eq 0) { $margin } else { $margin + $columnWidth + 20 }
            $width = if ($wide) { $item.width - 2 * $margin } else { $columnWidth }
            $height = if ($wide) { 150 } else { 80 }
            $g.DrawRectangle($rule, [float]$x, [float]$y, [float]$width, [float]$height)
            $g.DrawString(([string]$row.label).ToUpperInvariant(), $labelFont, $muted, $x + 10, $y + 8)
            $font = if ($row.hand) { $handFont } else { $typedFont }
            $brush = if ($row.hand) { $pen } else { $ink }
            $box = New-Object System.Drawing.RectangleF ([float]($x + 12)), ([float]($y + 30)), ([float]($width - 24)), ([float]($height - 34))
            $g.DrawString([string]$row.value, $font, $brush, $box)
            if ($wide) { $index = 0; $y += $height + 16 }
            elseif (($index % 2) -eq 1) { $index++; $y += $height + 16 }
            else { $index++ }
        }
        if (($index % 2) -eq 1) { $y += 96 }
        $g.DrawString([string]$item.footer, $subFont, $muted, $margin, $item.height - 60)
        Save-Rotated $bitmap ([double]$item.rotate) $path
    } finally { $g.Dispose(); $bitmap.Dispose() }
}

function Save-Receipt($item, [string]$path) {
    $mono = Get-FontFamilyName @('Consolas', 'Cascadia Mono', 'Courier New')
    $bitmap, $g = New-Canvas ([int]$item.width) ([int]$item.height)
    try {
        $g.Clear([System.Drawing.Color]::FromArgb(253, 253, 250))
        $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(36, 36, 36))
        $bold = New-Object System.Drawing.Font $mono, 20, ([System.Drawing.FontStyle]::Bold)
        $font = New-Object System.Drawing.Font $mono, 14
        $center = New-Object System.Drawing.StringFormat
        $center.Alignment = 'Center'
        $right = New-Object System.Drawing.StringFormat
        $right.Alignment = 'Far'
        $y = 40
        $g.DrawString([string]$item.merchant, $bold, $ink, (New-Object System.Drawing.RectangleF 0, $y, $item.width, 40), $center)
        $y += 46
        foreach ($line in $item.header) {
            $g.DrawString([string]$line, $font, $ink, (New-Object System.Drawing.RectangleF 0, $y, $item.width, 30), $center)
            $y += 28
        }
        $y += 18
        foreach ($line in $item.lines) {
            if ([string]$line.left -eq '---') {
                $g.DrawString(('-' * 40), $font, $ink, (New-Object System.Drawing.RectangleF 0, $y, $item.width, 30), $center)
            } else {
                $lineFont = if ($line.bold) { $bold } else { $font }
                $g.DrawString([string]$line.left, $lineFont, $ink, 40, $y)
                $g.DrawString([string]$line.right, $lineFont, $ink, (New-Object System.Drawing.RectangleF 0, $y, ($item.width - 40), 34), $right)
            }
            $y += if ($line.bold) { 36 } else { 28 }
        }
        $y += 24
        foreach ($line in $item.footer) {
            $g.DrawString([string]$line, $font, $ink, (New-Object System.Drawing.RectangleF 0, $y, $item.width, 30), $center)
            $y += 28
        }
        Save-Rotated $bitmap ([double]$item.rotate) $path
    } finally { $g.Dispose(); $bitmap.Dispose() }
}

$results = @()
foreach ($item in $data.files) {
    if ($item.name -notmatch '^[a-z0-9-]+\.(wav|png)$') { throw "Unsafe media name: $($item.name)" }
    $final = Join-Path $OutDir $item.name
    $temp = "$final.tmp"
    $voicesUsed = @()
    switch ($item.kind) {
        'speech' { $voicesUsed = Save-Speech $item $temp }
        'form' { Save-Form $item $temp }
        'receipt' { Save-Receipt $item $temp }
        default { throw "Unknown media kind: $($item.kind)" }
    }
    Move-Item -LiteralPath $temp -Destination $final -Force
    $results += @{ name = $item.name; bytes = (Get-Item -LiteralPath $final).Length; voices = @($voicesUsed) }
}
@{ files = $results } | ConvertTo-Json -Compress -Depth 4
