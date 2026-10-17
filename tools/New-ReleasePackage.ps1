#Requires -Version 7.2
<#
.SYNOPSIS
Builds the downloadable release for this edition: the self-contained app zip, the icon pack, SHA256SUMS.txt and release notes.
.DESCRIPTION
1. Stages the edition's files (tracked plus untracked-but-not-ignored) in a clean folder.
2. Exports the icon pack (PNG 16-1024 px, .ico, .svg, NOTICE) with tools\New-AppAssets.ps1.
3. Builds the native app (Release, self-contained for win-arm64, no PDBs) where the scripts and shortcut expect it.
4. Scans the stage for hygiene issues (user paths, credentials) and, with -ForbiddenToken, brand leaks in text files and
   this app's own binaries (third-party runtime DLLs are skipped for tokens, since short tokens occur in them by chance).
5. Zips <codeName>-<version>-win-arm64.zip (one top-level folder) and <codeName>-<version>-icon-pack.zip, then writes
   SHA256SUMS.txt and release-notes.md. Everything lands in -OutputDirectory (default .runtime\release).
.EXAMPLE
pwsh -NoProfile -File tools\New-ReleasePackage.ps1 -ForbiddenToken <word from another edition>
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string[]]$ForbiddenToken = @(),
    [string]$Dotnet
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'Edition.psm1') -Force
$edition = Read-Edition -Path (Join-Path $root 'edition.json')
$code = $edition.codeName
$csproj = Join-Path $root "native\$code.csproj"
[xml]$projectXml = Get-Content -LiteralPath $csproj -Raw
$version = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$framework = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.TargetFramework } | Where-Object { $_ })[0]
if (-not $version -or -not $framework) { throw "Version and TargetFramework must be set in $csproj." }
$out = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root '.runtime\release' }
$appName = "$code-$version-win-arm64"
$iconName = "$code-$version-icon-pack"
$work = Join-Path $out 'work'
$stage = Join-Path $work $appName
$icons = Join-Path $work $iconName

function Step([string]$Text) { Write-Host "== $Text" -ForegroundColor Cyan }

function Find-Dotnet {
    $candidates = @($Dotnet, [Environment]::GetEnvironmentVariable("$($edition.envPrefix)DOTNET"),
        (Join-Path (Split-Path $root -Parent) 'asu-agentic-showcase\.tools\dotnet\dotnet.exe'))
    foreach ($candidate in $candidates) { if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate } }
    $onPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($candidate in @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'), (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'))) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "No .NET SDK found. Install the .NET 10 SDK, pass -Dotnet or set $($edition.envPrefix)DOTNET."
}

function Get-RelativeFiles([string]$Folder) {
    $prefix = $Folder.TrimEnd('\').Length + 1
    return @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($prefix) } | Sort-Object)
}

function Write-Utf8([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, ($Text -replace "`r?`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))
}

Step "Staging $appName"
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
$null = New-Item -ItemType Directory -Path $stage -Force
$sourceFiles = Get-EditionFiles -Root $root
foreach ($relative in $sourceFiles) {
    $target = Join-Path $stage $relative
    $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $target
}
Write-Host "   $($sourceFiles.Count) source files"
$git = Join-Path $env:LOCALAPPDATA 'Programs\MinGit\cmd\git.exe'
if (-not (Test-Path -LiteralPath $git)) { $git = (Get-Command git.exe -ErrorAction SilentlyContinue).Source }
$commit = $null
if ($git -and (Test-Path -LiteralPath (Join-Path $root '.git'))) {
    $commit = (& $git -C $root rev-parse --short HEAD).Trim()
    if (& $git -C $root status --porcelain) { Write-Warning 'The working tree has uncommitted changes; the release notes say so.'; $commit = "$commit (with uncommitted changes)" }
}

Step 'Icon pack'
& (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $stage 'tools\New-AppAssets.ps1') -Root $stage -ExportDirectory $icons
if ($LASTEXITCODE -ne 0) { throw 'tools\New-AppAssets.ps1 failed.' }
foreach ($generated in 'native\Assets\AppIcon.ico', 'native\Assets\AppLogo.png') {
    $a = (Get-FileHash -LiteralPath (Join-Path $root $generated)).Hash; $b = (Get-FileHash -LiteralPath (Join-Path $stage $generated)).Hash
    if ($a -ne $b) { throw "$generated is out of date. Run tools\Sync-Edition.ps1 and commit before packaging." }
}
$style = Get-LogoStyle -Edition $edition
Write-Utf8 (Join-Path $icons 'NOTICE.txt') @"
$($edition.displayName) app icon ($style style)

Original prototype artwork drawn for this project and generated from edition.json by tools\New-AppAssets.ps1.
It is not the official logo, trademark or brand artwork of any company, and it implies no endorsement or affiliation.
Use it to identify this app (shortcuts, slides, the app tile).

Files: $code-<size>.png at $((Get-ChildItem -LiteralPath $icons -Filter "$code-*.png" | ForEach-Object { [int]($_.BaseName -replace '.*-', '') } | Sort-Object) -join ', ') px,
$code.ico (16-256 px, for Windows shortcuts) and $code.svg (vector source).
"@

Step 'Building the native app (Release, self-contained, win-arm64, no PDBs)'
$dotnet = Find-Dotnet
# A Release build of this self-contained project is the complete app; dotnet publish omits the app's .pri and App.xbf.
& $dotnet build (Join-Path $stage "native\$code.csproj") -c Release -nologo -v minimal `
    -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
$obj = Join-Path $stage 'native\obj'
if (Test-Path -LiteralPath $obj) { Remove-Item -LiteralPath $obj -Recurse -Force }
$appDir = Join-Path $stage "native\bin\Release\$framework\win-arm64"
$unexpected = @(Get-ChildItem -LiteralPath (Join-Path $stage 'native\bin') -Directory -Recurse | Where-Object {
        -not $appDir.StartsWith($_.FullName, [StringComparison]::OrdinalIgnoreCase) -and -not $_.FullName.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) })
if ($unexpected) { throw "Unexpected build output folders: $($unexpected.FullName -join ', ')" }
foreach ($required in "$code.exe", "$code.dll", "$code.pri", 'App.xbf', 'Assets\AppIcon.ico') {
    if (-not (Test-Path -LiteralPath (Join-Path $appDir $required))) { throw "The built app is missing $required." }
}
$pdbs = @(Get-ChildItem -LiteralPath $appDir -Recurse -Filter *.pdb)
if ($pdbs) { throw "PDB files must not ship: $($pdbs.Name -join ', ')" }
Write-Host "   $(@(Get-ChildItem -LiteralPath $appDir -Recurse -File).Count) files in native\bin\Release\$framework\win-arm64"

Step 'Scanning the stage'
$stageFiles = Get-RelativeFiles $stage
$appPrefix = "native\bin\Release\$framework\win-arm64\"
$ownFiles = @($stageFiles | Where-Object {
        -not $_.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        ($_.Substring($appPrefix.Length) -like "$code.*") -or ($_.Substring($appPrefix.Length) -like 'Assets\*') })
$hygiene = @(Find-HygieneIssues -Root $stage -Files $stageFiles) + @(Find-HygieneIssues -Root $icons -Files (Get-RelativeFiles $icons))
if ($hygiene) { $hygiene | Format-Table -AutoSize | Out-String | Write-Host; throw 'Hygiene issues in the release.' }
Write-Host "   hygiene: $($stageFiles.Count) files clean"
if ($ForbiddenToken) {
    $leaks = @(Find-EditionLeaks -Root $stage -Token $ForbiddenToken -Files $ownFiles) + @(Find-EditionLeaks -Root $icons -Token $ForbiddenToken -Files (Get-RelativeFiles $icons))
    if ($leaks) { $leaks | Format-Table -AutoSize | Out-String | Write-Host; throw "Forbidden tokens in the release: $($ForbiddenToken -join ', ')" }
    Write-Host "   forbidden tokens ($($ForbiddenToken -join ', ')): $($ownFiles.Count) own files clean"
}

Step 'Zipping'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$appZip = Join-Path $out "$appName.zip"
$iconZip = Join-Path $out "$iconName.zip"
foreach ($zip in $appZip, $iconZip) { if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force } }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $appZip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
[System.IO.Compression.ZipFile]::CreateFromDirectory($icons, $iconZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$sums = foreach ($zip in $appZip, $iconZip) { '{0}  {1}' -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $zip -Leaf) }
Write-Utf8 (Join-Path $out 'SHA256SUMS.txt') (($sums -join "`n") + "`n")
$appMb = [math]::Round((Get-Item -LiteralPath $appZip).Length / 1MB, 1)
$iconKb = [math]::Round((Get-Item -LiteralPath $iconZip).Length / 1KB)
$built = if ($commit) { "Built from commit $commit." } else { 'Built from a working tree without git history.' }
Write-Utf8 (Join-Path $out 'release-notes.md') @"
# $($edition.displayName) $version

A native WinUI 3 app for Windows ARM64 that runs local AI on the GPU of the $($edition.device) through Foundry Local (CUDA).
All demo data is synthetic. $built

## Downloads

| File | What it is |
| --- | --- |
| ``$appName.zip`` ($appMb MB) | The app, self-contained: no .NET or Windows App SDK install needed. Includes the local service, demo content and ``Setup.cmd``. |
| ``$iconName.zip`` ($iconKb KB) | The app icon: PNG from 16 to 1024 px, a multi-size .ico and the .svg source. Original artwork, not the official brand artwork of any company. |
| ``SHA256SUMS.txt`` | SHA-256 hashes of both zips. Compare with ``(Get-FileHash .\<file>).Hash``. |

## Requirements

- Windows 11 on ARM64 with an NVIDIA GPU and current drivers. Built and tested on the $($edition.device).
- Python 3.12 to 3.14 for Windows ARM64 (3.13 tested), from python.org. Setup installs the tested dependency versions in ``constraints.txt``.
- An internet connection and plenty of free disk for the first model downloads.

## Install

1. Download ``$appName.zip`` and check its hash against ``SHA256SUMS.txt``.
2. Unblock it (right-click > Properties > Unblock, or ``Unblock-File .\$appName.zip``), then extract it to a short path such as ``C:\Apps``.
3. Double-click ``Setup.cmd`` in the extracted folder. It creates the Python environment, installs the Foundry Local SDK, adds the
   desktop shortcut **$($edition.shortcutName)**, then downloads every model and runs each live demo once to check it.
   The same from a terminal: ``.\Setup.ps1 -Prepare``.
4. Open the desktop shortcut.

The app is not code-signed, so Windows may ask you to confirm the first launch.
"@

Step 'Done'
Get-ChildItem -LiteralPath $out -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Release files are in $out (staging folder: $work)." -ForegroundColor Green
