#Requires -Version 7.2
<#
.SYNOPSIS
Applies edition.json presentation fields to this tree, or checks that they are in sync.
.DESCRIPTION
Regenerates native\Brand.cs, native\Assets\AppLogo.svg/.png and AppIcon.ico, the SystemAccentColor ramp in
native\App.xaml and the --accent/--accent-deep/--focus values in style.css. Identity tokens (code name, AUMID,
service app id, ...) are not rewritten here; create a new edition with tools\New-Edition.ps1 instead.
.EXAMPLE
pwsh tools\Sync-Edition.ps1          # apply
pwsh tools\Sync-Edition.ps1 -Check   # exit 1 if anything is out of date
#>
[CmdletBinding()]
param([string]$Root = (Split-Path $PSScriptRoot -Parent), [switch]$Check)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Edition.psm1') -Force
$Root = (Resolve-Path -LiteralPath $Root).Path
$drift = @(Sync-EditionTree -Root $Root -Check:$Check)
if ($Check) {
    if ($drift.Count) {
        Write-Host "Edition drift in ${Root}:" -ForegroundColor Red
        $drift | ForEach-Object { Write-Host "  $_" }
        Write-Host 'Run tools\Sync-Edition.ps1 to regenerate.'
        exit 1
    }
    Write-Host 'Edition files are in sync with edition.json.' -ForegroundColor Green
} else {
    if ($drift.Count) { throw "Edition sync could not complete: $($drift -join '; ')" }
    $edition = Read-Edition -Path (Join-Path $Root 'edition.json')
    Write-Host "Applied edition '$($edition.id)' ($($edition.displayName)) to $Root" -ForegroundColor Green
}
