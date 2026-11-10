#Requires -Version 7.2
<#
.SYNOPSIS
Builds device-aware prototype installer packs without bundled model weights.
.DESCRIPTION
Stages source under .runtime\release\work and builds self-contained win-arm64 and win-x64 apps there.
Creates Snapdragon-arm64, Intel-x64, NVIDIA-arm64 and NVIDIA-x64 packs, an icon pack, hashes and release notes.
Pack names guide downloads only: the service's actual device detection and live-catalog resolver decide placement.
-BuildOnly validates both builds without creating downloadable archives. No app, setup or shortcut is launched.
-FinalizeStaged reuses verified native outputs after checking native source equality, refreshing the remaining source.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string[]]$ForbiddenToken = @(),
    [string]$Dotnet,
    [switch]$BuildOnly,
    [switch]$FinalizeStaged
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
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $root '.runtime\release'))
$out = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { $releaseRoot }
if ($out -ne $releaseRoot -and -not $out.StartsWith("$releaseRoot\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be inside this project''s .runtime\release folder.'
}
$work = Join-Path $out 'work'
$stage = Join-Path $work 'source'
$iconName = "$code-$version-icon-pack"
$icons = Join-Path $work $iconName
$packs = @(
    @{ Name = 'Snapdragon-arm64'; Rid = 'win-arm64' },
    @{ Name = 'Intel-x64'; Rid = 'win-x64' },
    @{ Name = 'NVIDIA-arm64'; Rid = 'win-arm64' },
    @{ Name = 'NVIDIA-x64'; Rid = 'win-x64' }
)

function Step([string]$Text) { Write-Host "== $Text" -ForegroundColor Cyan }

function Find-Dotnet {
    $candidates = @($Dotnet, [Environment]::GetEnvironmentVariable("$($edition.envPrefix)DOTNET"),
        (Join-Path (Split-Path $root -Parent) 'asu-agentic-showcase\.tools\dotnet\dotnet.exe'))
    foreach ($candidate in $candidates) { if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate } }
    $onPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "No .NET 10 SDK found. Pass -Dotnet or set $($edition.envPrefix)DOTNET."
}

function Get-RelativeFiles([string]$Folder) {
    $prefix = $Folder.TrimEnd('\').Length + 1
    return @(Get-ChildItem -LiteralPath $Folder -Recurse -File -Force |
        ForEach-Object { $_.FullName.Substring($prefix) } | Sort-Object)
}

function Write-Utf8([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, ($Text -replace "`r?`n", "`r`n"), [System.Text.UTF8Encoding]::new($false))
}

function Copy-RelativeFiles([string]$Source, [string]$Destination, [string[]]$Files) {
    foreach ($relative in $Files) {
        $target = Join-Path $Destination $relative
        $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
        Copy-Item -LiteralPath (Join-Path $Source $relative) -Destination $target
    }
}

Step 'Staging source (never building in the running app''s output)'
# Delete only our dedicated work directory, never a caller-selected broad directory or a junction.
if (-not $FinalizeStaged -and (Test-Path -LiteralPath $work)) {
    $resolved = (Resolve-Path -LiteralPath $work).Path
    if ($resolved -ne [System.IO.Path]::GetFullPath($work) -or
        (Get-Item -LiteralPath $work).Attributes -band [System.IO.FileAttributes]::ReparsePoint -or
        -not $resolved.StartsWith("$releaseRoot\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe release work path: $work"
    }
    Remove-Item -LiteralPath $work -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $stage -Force
$sourceFiles = @(Get-EditionFiles -Root $root | Where-Object {
    "\$_" -notmatch '\\(\.git|\.runtime|\.venv|__pycache__|bin|obj)(\\|$)' -and
    $_ -notmatch '\.(onnx|gguf|safetensors|pdb|pyc)$'
})
if ($FinalizeStaged) {
    foreach ($relative in @($sourceFiles | Where-Object { $_ -like 'native\*' })) {
        $stagedFile = Join-Path $stage $relative
        if (-not (Test-Path -LiteralPath $stagedFile) -or
            (Get-FileHash -LiteralPath (Join-Path $root $relative)).Hash -ne (Get-FileHash -LiteralPath $stagedFile).Hash) {
            throw "Native source changed since build: $relative. Re-run without -FinalizeStaged."
        }
    }
}
Copy-RelativeFiles $root $stage $sourceFiles
Write-Host "   $($sourceFiles.Count) source files; model weights and local caches excluded"
$git = Join-Path $env:LOCALAPPDATA 'Programs\MinGit\cmd\git.exe'
$commit = 'working tree without git history'
if ((Test-Path -LiteralPath $git) -and (Test-Path -LiteralPath (Join-Path $root '.git'))) {
    $commit = (& $git -C $root rev-parse --short HEAD).Trim()
    if (& $git -C $root status --porcelain) { $commit += ' (with uncommitted changes)' }
}

Step 'Icon pack'
& (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $stage 'tools\New-AppAssets.ps1') -Root $stage -ExportDirectory $icons
if ($LASTEXITCODE -ne 0) { throw 'tools\New-AppAssets.ps1 failed.' }
foreach ($generated in 'native\Assets\AppIcon.ico', 'native\Assets\AppLogo.png') {
    if ((Get-FileHash -LiteralPath (Join-Path $root $generated)).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $stage $generated)).Hash) {
        throw "$generated is out of date. Run tools\Sync-Edition.ps1 before packaging."
    }
}
Write-Utf8 (Join-Path $icons 'NOTICE.txt') @"
$($edition.displayName) prototype app icon
Original artwork generated from edition.json by tools\New-AppAssets.ps1.
Not official company artwork; no endorsement or affiliation is implied.
Includes PNG sizes 16-1024, multi-size ICO and SVG.
"@

$dotnet = Find-Dotnet
foreach ($rid in 'win-arm64', 'win-x64') {
    Step "Building self-contained $rid (build validation, not a device runtime test)"
    # Build, not publish: WinUI needs the .pri and App.xbf produced by the build output.
    if (-not $FinalizeStaged) {
        & $dotnet build (Join-Path $stage "native\$code.csproj") -c Release -r $rid -nologo -v minimal `
            -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $rid." }
    }
    $appDir = Join-Path $stage "native\bin\Release\$framework\$rid"
    foreach ($required in "$code.exe", "$code.dll", "$code.pri", 'App.xbf', 'Assets\AppIcon.ico', 'coreclr.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $appDir $required))) { throw "$rid output is missing $required." }
    }
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $appDir "$code.exe"))
    $machine = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3c) + 4)
    $expectedMachine = if ($rid -eq 'win-arm64') { 0xaa64 } else { 0x8664 }
    if ($machine -ne $expectedMachine) { throw "$rid executable has the wrong PE architecture: $machine" }
    if (Get-ChildItem -LiteralPath $appDir -Recurse -Filter *.pdb) { throw "PDB files must not ship ($rid)." }
}
if ($BuildOnly) {
    Write-Host "Both architectures built and PE-checked in $stage. No runtime tests or installer actions performed." -ForegroundColor Green
    return
}

Step 'Scanning staged source and build output'
$stageFiles = @(Get-RelativeFiles $stage | Where-Object {
    $_ -notlike 'native\obj\*' -and "\$_" -notmatch '\\(\.git|\.runtime|\.venv|__pycache__)(\\|$)'
})
$ownFiles = @($stageFiles | Where-Object { $_ -notlike 'native\bin\*' -or
    $_ -like "native\bin\Release\$framework\*\$code.*" -or $_ -like "native\bin\Release\$framework\*\Assets\*" })
$hygiene = @(Find-HygieneIssues -Root $stage -Files $stageFiles) +
    @(Find-HygieneIssues -Root $icons -Files (Get-RelativeFiles $icons))
if ($hygiene) { $hygiene | Format-Table -AutoSize | Out-String | Write-Host; throw 'Hygiene issues in the release.' }
if ($ForbiddenToken) {
    $leaks = @(Find-EditionLeaks -Root $stage -Token $ForbiddenToken -Files $ownFiles) +
        @(Find-EditionLeaks -Root $icons -Token $ForbiddenToken -Files (Get-RelativeFiles $icons))
    if ($leaks) { $leaks | Format-Table -AutoSize | Out-String | Write-Host; throw 'Forbidden tokens in the release.' }
}

Step 'Creating four device-family download packs'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archives = [System.Collections.Generic.List[string]]::new()
foreach ($pack in $packs) {
    $appName = "$code-$version-$($pack.Name)"
    $destination = Join-Path $work $appName
    $nativePrefix = "native\bin\Release\$framework\$($pack.Rid)\"
    $files = @($stageFiles | Where-Object { $_ -notlike 'native\bin\*' -or $_.StartsWith($nativePrefix, [StringComparison]::OrdinalIgnoreCase) })
    Copy-RelativeFiles $stage $destination $files
    Write-Utf8 (Join-Path $destination 'README-FIRST.txt') @"
$($edition.displayName) $version - $($pack.Name)
EXTERNAL DEMONSTRATION PROTOTYPE - NOT A SUPPORTED PRODUCT

This pack contains the self-contained $($pack.Rid) WinUI app and local service source.
No .NET or Windows App SDK installation is required to launch the included app.
Built from $commit. Both native architectures are build-validated, not runtime-certified on every device.

REQUIREMENTS
- Windows 11 (build 22621 or later), native $($pack.Rid) architecture.
- Native-architecture Python 3.12-3.14 from python.org (3.13 preferred).
- Internet access and sufficient free disk space for first-use dependencies and selected models.
- Current device drivers. Model/provider availability depends on the live Foundry Local catalog.
  An NPU or GPU being present does not guarantee a supported provider or model.

FIRST USE
1. Extract the entire folder to a writable local location. Unblock the downloaded ZIP if Windows requires it.
2. Run Setup.cmd. It creates this folder's .venv, installs requirements.txt constrained by constraints.txt,
   creates a desktop shortcut, and starts the service with model downloads allowed.
   Terminal alternative: .\Setup.ps1 -Prepare; add -NoShortcut to omit the shortcut.
3. Setup uses the existing device/silicon detection and live-catalog memory-fit resolver on THIS PC.
   Only demos with fully available model steps are prepared and checked; limited/unavailable demos are
   skipped with their reasons in .runtime\setup-prepare.json. A zero-eligible-demo result fails preparation.
4. Open the desktop shortcut, or run .\Start-NativeShowcase.ps1.

MODEL DOWNLOADS AND LIMITS
No model weights are bundled. First-use downloads can be large and slow; review each model's license.
Packs of the same architecture contain the same app; the family name is download guidance, not a forced
GPU/NPU mode. Actual hardware, driver/provider support, live catalog and memory fit are authoritative.
No cloud endpoint is required for local inference after preparation; downloads need network access.
Demo data is synthetic. Results and benchmarks are illustrative, not product or performance guarantees.
The prototype is unsigned. Your organization's execution/security policy may block it.
"@
    $metadata = [ordered]@{ schema = 'studio-installer-pack/1'; prototype = $true; family = $pack.Name
        runtime = $pack.Rid; model_weights_bundled = $false; placement = 'detected-device-live-catalog'
        validation = 'cross-architecture build and PE checks; not device runtime certification'; source = $commit }
    Write-Utf8 (Join-Path $destination 'installer-pack.json') ($metadata | ConvertTo-Json)
    $packFiles = Get-RelativeFiles $destination
    $issues = @(Find-HygieneIssues -Root $destination -Files @('README-FIRST.txt', 'installer-pack.json'))
    if ($issues) { throw "Generated pack documentation failed hygiene checks: $($pack.Name)" }
    if (@($packFiles | Where-Object { $_ -match '\.(onnx|gguf|safetensors|pdb|pyc)$' -or $_ -like 'native\obj\*' })) {
        throw "Unexpected weights or build intermediates in $appName."
    }
    $zip = Join-Path $out "$appName.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($destination, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $archives.Add($zip)
}
$iconZip = Join-Path $out "$iconName.zip"
if (Test-Path -LiteralPath $iconZip) { Remove-Item -LiteralPath $iconZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($icons, $iconZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$archives.Add($iconZip)
$sums = foreach ($zip in $archives) { '{0}  {1}' -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $zip -Leaf) }
Write-Utf8 (Join-Path $out 'SHA256SUMS.txt') (($sums -join "`n") + "`n")
$downloadRows = foreach ($zip in $archives) {
    "| ``$(Split-Path $zip -Leaf)`` | $([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MB |"
}
Write-Utf8 (Join-Path $out 'release-notes.md') @"
# $($edition.displayName) $version - device-aware prototype packs

External demonstration prototype, not a supported product. Synthetic demo data. Built from $commit.

| Download | Size |
| --- | --- |
$($downloadRows -join "`n")
| ``SHA256SUMS.txt`` | SHA-256 hashes for all five archives |

Choose the native ARM64 or x64 pack for your PC; family names are guidance, not forced acceleration.
Both architectures are self-contained build-validated outputs; this is not runtime testing on unavailable devices.
No model weights ship. Read README-FIRST.txt inside each pack for requirements and first-use downloads.

Windows 11 build 22621+, native Python 3.12-3.14 (3.13 preferred), current drivers, internet and disk space are required.
Setup installs requirements.txt using the pinned constraints.txt, detects the actual PC through the existing device kit,
resolves live-catalog models that fit its memory budget, then downloads and checks only fully available demos.
Limited/unavailable demos are skipped and reported. Provider support, not hardware marketing or the ZIP name, determines
whether CPU, GPU or NPU models run. No blanket GPU/NPU compatibility or performance guarantee is made.

Extract the whole archive, run Setup.cmd (or .\Setup.ps1 -Prepare -NoShortcut), then launch the shortcut or
.\Start-NativeShowcase.ps1. The unsigned prototype may be blocked by Windows or organizational security policy.
"@
Step 'Done'
Get-ChildItem -LiteralPath $out -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Format-Table -AutoSize | Out-String | Write-Host
Write-Host "Release files: $out. Staging: $work. No installer or shortcut executed." -ForegroundColor Green
