#Requires -Version 7.2
<#
.SYNOPSIS
Generates a re-branded edition of this studio from editions\<id>.json.
.DESCRIPTION
1. Copies the tracked tree (git ls-files; generated brand files, editions\ and the handout PDF are excluded).
2. Applies the target's literal prose rules, then maps every source identity token (edition.json) to the
   target in one longest-match pass, in file contents and in paths (for example the .csproj name).
3. Writes the target edition.json (without rules/forbiddenTokens) and regenerates Brand.cs, accents and assets.
4. Fails if any forbidden token or source identity token remains in a path or file (binaries included),
   or if a user-profile path or credential pattern is found.
Run tools\Test-Edition.ps1 -Path <Destination> afterwards for the full build/test conformance gate.
.EXAMPLE
pwsh tools\New-Edition.ps1 -Edition surface -Destination ..\Surface-Hybrid-Intelligence-Studio
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{1,30}$')][string]$Edition,
    [Parameter(Mandatory)][string]$Destination,
    [string]$Source = (Split-Path $PSScriptRoot -Parent),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Edition.psm1') -Force
$started = Get-Date

$Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
$Destination = [System.IO.Path]::GetFullPath($Destination, (Get-Location).Path).TrimEnd('\')
if ($Destination -eq $Source -or $Destination.StartsWith("$Source\", [StringComparison]::OrdinalIgnoreCase) -or
    $Source.StartsWith("$Destination\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Destination must be outside the source tree (and must not contain it).'
}
$from = Read-Edition -Path (Join-Path $Source 'edition.json')
$to = Read-Edition -Path (Join-Path $Source "editions\$Edition.json") -Target
if ($to.id -ne $Edition) { throw "editions\$Edition.json has id '$($to.id)'; the file name and id must match." }

if (Test-Path -LiteralPath $Destination) {
    $existing = @(Get-ChildItem -LiteralPath $Destination -Force | Where-Object Name -ne '.git')
    if ($existing.Count -and -not $Force) { throw "Destination is not empty: $Destination. Use -Force to regenerate it (its .git folder is kept)." }
    $existing | Remove-Item -Recurse -Force
} else {
    $null = New-Item -ItemType Directory -Path $Destination
}

$map = Get-EditionIdentityMap -From $from -To $to
$excluded = @('edition.json', 'native\Brand.cs', 'native\Assets\AppIcon.ico', 'native\Assets\AppLogo.png', 'native\Assets\AppLogo.svg', 'Executive-Handout.pdf')
$files = @(Get-EditionFiles -Root $Source | Where-Object { $_ -notin $excluded -and $_ -notlike 'editions\*' })
$ruleHits = [hashtable]::new([StringComparer]::Ordinal)
$changed = 0
$renamed = [System.Collections.Generic.List[string]]::new()
foreach ($relative in $files) {
    $target = Convert-EditionText -Text $relative -Pairs $map
    if ($target -cne $relative) { $renamed.Add("$relative -> $target") }
    $outPath = Join-Path $Destination $target
    $null = New-Item -ItemType Directory -Path (Split-Path $outPath -Parent) -Force
    $inPath = Join-Path $Source $relative
    if (-not (Test-EditionTextFile $relative)) {
        Copy-Item -LiteralPath $inPath -Destination $outPath
        continue
    }
    $original = [System.IO.File]::ReadAllText($inPath)
    $text = Convert-EditionText -Text $original -Pairs (Get-EditionRules -Edition $to -RelativePath $relative) -Counts $ruleHits
    $text = Convert-EditionText -Text $text -Pairs $map
    if ($text -cne $original) { $changed++ }
    Write-TextFile $outPath $text
}

$manifest = ConvertTo-TreeManifest -Edition $to
Write-TextFile (Join-Path $Destination 'edition.json') (($manifest | ConvertTo-Json -Depth 6).ReplaceLineEndings("`r`n") + "`r`n")
$drift = @(Sync-EditionTree -Root $Destination)
if ($drift.Count) { throw "Generated edition is inconsistent: $($drift -join '; ')" }

$tokens = [System.Collections.Generic.List[string]]::new()
foreach ($token in @(Get-Field $to 'forbiddenTokens')) { if ($token) { $tokens.Add($token) } }
foreach ($pair in $map) { $tokens.Add($pair.From) }
$tokens = @($tokens | Sort-Object -Unique)
$all = Get-EditionFiles -Root $Destination
$leaks = @(Find-EditionLeaks -Root $Destination -Token $tokens -Files $all)
$hygiene = @(Find-HygieneIssues -Root $Destination -Files $all)
$unused = @(@(Get-Field $to 'rules') | Where-Object { $_ -and -not $ruleHits.ContainsKey($_.from) } | ForEach-Object { $_.from })

$report = [ordered]@{
    schema = 'studio-edition-generation/1'
    generated_at = (Get-Date).ToString('o')
    source_edition = $from.id
    target_edition = $to.id
    files = $all.Count
    text_files_changed = $changed
    renamed = $renamed
    identity_map = @($map | ForEach-Object { [ordered]@{ field = $_.Field; from = $_.From; to = $_.To } })
    rules_applied = $ruleHits.Count
    rules_unused = $unused
    leak_tokens = $tokens
    leaks = $leaks
    hygiene = $hygiene
    passed = ($leaks.Count -eq 0 -and $hygiene.Count -eq 0)
}
$runtime = Join-Path $Destination '.runtime'
$null = New-Item -ItemType Directory -Path $runtime -Force
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runtime 'edition-generation.json') -Encoding utf8

Write-Host ("Edition '{0}' -> '{1}': {2} files, {3} text files rewritten, {4} paths renamed, {5}/{6} rules applied ({7:n1}s)" -f
    $from.id, $to.id, $all.Count, $changed, $renamed.Count, $ruleHits.Count, @(Get-Field $to 'rules').Count, ((Get-Date) - $started).TotalSeconds)
$renamed | ForEach-Object { Write-Host "  renamed $_" }
$unused | ForEach-Object { Write-Warning "Rule matched nothing (source text changed?): $_" }
if ($leaks.Count -or $hygiene.Count) {
    $leaks | ForEach-Object { Write-Host "  LEAK '$($_.Token)' in $($_.File) ($($_.Where))" -ForegroundColor Red }
    $hygiene | ForEach-Object { Write-Host "  HYGIENE $($_.Issue) in $($_.File) ($($_.Where))" -ForegroundColor Red }
    Write-Host "Add or adjust rules in editions\$Edition.json and regenerate with -Force." -ForegroundColor Red
    exit 1
}
Write-Host "No source-brand tokens remain ($($tokens.Count) checked). Next (builds, tests and prints Executive-Handout.pdf): pwsh tools\Test-Edition.ps1 -Path `"$Destination`" -Browser" -ForegroundColor Green
