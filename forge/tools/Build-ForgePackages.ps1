<#
.SYNOPSIS
    Publishes the Offline PPTX Forge as one self-contained package per silicon family.

.DESCRIPTION
    Snapdragon-arm64  Surface Pro and Surface Laptop with Snapdragon X or X2, and Surface Pro 9 with 5G (Microsoft SQ3)
    Intel-x64         Surfaces with Intel or AMD processors and no NVIDIA GPU
    NVIDIA-arm64      Surface Laptop Ultra (NVIDIA RTX Spark)
    NVIDIA-x64        Surface Laptop Studio and Surface Book with an NVIDIA GPU

    The two Arm64 packages hold the same app, and so do the two x64 packages. The family stamped into each (ForgeSilicon)
    sets the package the app and its probe report, so a tester can tell at once whether a PC got the right one. Detection
    and model choice never depend on it: every package plans from the PC it runs on.

    1. Publishes a self-contained Release build per package to forge\dist\OfflinePptxForge-<package>\, so a Surface needs
       no .NET or Windows App SDK install. Publishing a package first removes its old folder and zip.
    2. Adds Test-ThisPC.cmd (the one-click check for testers), Test-Forge and Install-Shortcut (each with a .cmd), any
       -Include files (such as the shared TEST-PLAN.md), and README-FIRST.txt, whose device list comes from the published
       app's own sample devices.
    3. Start check: runs the self-test, checks that the probe names the package, and opens the window for a full page
       check through UI Automation (no model is loaded; the window never takes focus). x64 packages run under emulation
       on Arm64 Windows; Arm64 packages can't run on x64 Windows, so they are skipped there.
    4. With -Zip, writes forge\dist\OfflinePptxForge-<package>-<version>.zip as well.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File forge\tools\Build-ForgePackages.ps1 -Zip
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File forge\tools\Build-ForgePackages.ps1 -Package Intel-x64 -Include C:\plans\TEST-PLAN.md
#>
[CmdletBinding()]
param(
    [ValidateSet('Snapdragon-arm64', 'Intel-x64', 'NVIDIA-arm64', 'NVIDIA-x64')]
    [string[]]$Package = @('Snapdragon-arm64', 'Intel-x64', 'NVIDIA-arm64', 'NVIDIA-x64'),
    [string]$Out,
    [string]$Dotnet,
    [string[]]$Include,
    [switch]$Zip,
    [switch]$SkipStartCheck
)

$ErrorActionPreference = 'Stop'
trap {
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
$forge = Split-Path $PSScriptRoot -Parent
$project = Join-Path $forge 'app\OfflinePptxForge\OfflinePptxForge.csproj'
if (-not $Out) { $Out = Join-Path $forge 'dist' }
$Out = [IO.Path]::GetFullPath($Out)
$extras = @($Include | Where-Object { $_ } | ForEach-Object {
        if (-not (Test-Path $_ -PathType Leaf)) { throw "-Include file not found: $_" }
        (Resolve-Path $_).Path
    })

$packages = [ordered]@{
    'Snapdragon-arm64' = @{ Title = 'Snapdragon (Arm64)'
        For = 'Surface Pro and Surface Laptop with Snapdragon X or Snapdragon X2, and Surface Pro 9 with 5G (Microsoft SQ3).' }
    'Intel-x64'        = @{ Title = 'Intel (x64, also for AMD)'
        For = 'Surfaces with an Intel processor (Core Ultra, Core or Pentium) or an AMD processor, and no NVIDIA GPU.' }
    'NVIDIA-arm64'     = @{ Title = 'NVIDIA (Arm64, Surface Laptop Ultra)'
        For = 'Surface Laptop Ultra (NVIDIA RTX Spark).' }
    'NVIDIA-x64'       = @{ Title = 'NVIDIA (x64, Surface Laptop Studio and Surface Book)'
        For = 'Surface Laptop Studio, Surface Laptop Studio 2 and Surface Book with an NVIDIA GPU.' }
}

function Find-Dotnet {
    $candidates = @()
    if ($Dotnet) { $candidates += $Dotnet }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    if ($env:DOTNET_ROOT) { $candidates += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'), (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            $sdks = & $candidate --list-sdks 2>$null
            if ($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 }) { return $candidate }
        }
    }
    throw 'No .NET 10 SDK found. Install it from https://dot.net, or pass -Dotnet <path to dotnet.exe>.'
}

function Get-ForgeVersion {
    $match = Select-String -Path $project -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if ($match) { return $match.Matches[0].Groups[1].Value }
    return '0.0.0'
}

function Assert-NotRunningFrom([string]$folder) {
    $running = Get-Process -Name OfflinePptxForge -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($folder, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
    if ($running) { throw "The Forge is running from $folder (PID $(@($running.Id) -join ', ')). Close it and run this again." }
}

function Write-Cmd([string]$path, [string[]]$lines) {
    [IO.File]::WriteAllText($path, (($lines -join "`r`n") + "`r`n"), (New-Object Text.ASCIIEncoding))
}

function Copy-ForgeFiles([string]$target) {
    foreach ($script in 'Install-Shortcut.ps1', 'Test-Forge.ps1') {
        Copy-Item (Join-Path $PSScriptRoot $script) $target -Force
        # Double-clicked (no arguments), the window stays open so the result can be read.
        Write-Cmd (Join-Path $target ([IO.Path]::ChangeExtension($script, '.cmd'))) @(
            '@echo off'
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0$script`" %*"
            'set "FORGE_EXIT=%ERRORLEVEL%"'
            'if "%~1"=="" pause'
            'exit /b %FORGE_EXIT%'
        )
    }
    Write-Cmd (Join-Path $target 'Test-ThisPC.cmd') @(
        '@echo off'
        'title Offline PPTX Forge - test this PC'
        'echo Testing the Offline PPTX Forge on this PC.'
        'echo The first run downloads the model this PC needs, so stay plugged in and on Wi-Fi.'
        'echo The Forge window opens and makes decks by itself. Leave the PC alone until this window says READY or NOT READY.'
        'echo.'
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Test-Forge.ps1" -Live -AllowDownload -Share'
        'set "FORGE_EXIT=%ERRORLEVEL%"'
        'echo.'
        'pause'
        'exit /b %FORGE_EXIT%'
    )
    foreach ($extra in $extras) { Copy-Item $extra $target -Force }
}

function Write-ReadmeFirst([string]$target, [string]$name, [string]$version, [object[]]$devices) {
    $info = $packages[$name]
    $plan = if ($extras | Where-Object { (Split-Path $_ -Leaf) -eq 'TEST-PLAN.md' }) { '  TEST-PLAN.md has the full plan and what to look for on each Surface.' } else { $null }
    $lines = @(
        "Offline PPTX Forge $version"
        "$($info.Title) package ($name)"
        'Writes a PowerPoint deck on this PC with local AI. Nothing leaves it.'
        ''
        'FOR THESE SURFACES'
        "  $($info.For)"
        '  Not sure? Open OfflinePptxForge.exe: the This PC page names the package this PC takes, and Test-ThisPC.cmd checks it.'
        ''
        'BEFORE YOU EXTRACT'
        '  Right-click the zip, select Properties, tick Unblock, then OK. The Forge is not code-signed yet, so without this'
        '  Windows asks before it runs. Extract to a local folder such as C:\Demos, not a OneDrive-synced Desktop or'
        '  Documents (syncing 200 MB slows the PC while it is tested). Nothing to install, no admin rights.'
        ''
        'TEST THIS PC'
        '  1. Plug in, join Wi-Fi, and close apps that use the GPU or NPU (the Sales Kit and other AI demos too).'
        '  2. Double-click Test-ThisPC.cmd. The first run downloads the model this PC needs from Foundry Local (several GB).'
        '     The Forge window opens and makes decks by itself. Leave the PC alone until the window says READY or NOT READY.'
        '  3. Send back the results zip it saves to the Desktop: OfflinePptxForge-results-<PC>-<time>.zip.'
    )
    if ($plan) { $lines += $plan }
    $lines += @(
        ''
        'PRESENT'
        '  Double-click Install-Shortcut.cmd once to add the Forge to Start, or open OfflinePptxForge.exe.'
        '  On Make a deck, pick a brief (or write one in plain words) and select Make the deck.'
        '  Decks are saved to Documents\Offline PPTX Forge.'
        '  Make one deck before the meeting so the model is on this PC. After that the Forge runs offline.'
    )
    $mine = @($devices | Where-Object { $_.package -eq $name -and $_.id -notlike '*-emulated' })
    if ($mine.Count -gt 0) {
        $lines += @(
            ''
            "WHAT EACH SURFACE SHOULD SHOW (the Forge's own plan for its sample devices; confirm on the exact SKU)"
        )
        foreach ($d in $mine) {
            $deck = if ($d.modelTitle) { "$($d.modelTitle) on the $("$($d.lane)".ToUpperInvariant())" } else { 'no local model fits' }
            $lines += @(
                ''
                "  $($d.label)"
                "      $($d.pcClass); decks: $deck"
                "      Sample device: $($d.id)"
            )
        }
    }
    [IO.File]::WriteAllText((Join-Path $target 'README-FIRST.txt'), (($lines -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding $true))
}

# Runs a headless command; its console output (the --json report) goes to $stdout.
function Invoke-Json([string]$exePath, [string[]]$arguments, [int]$timeoutSeconds, [string]$stdout) {
    $process = Start-Process $exePath -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError "$stdout.err"
    # Caching the handle keeps the exit code readable after WaitForExit.
    $null = $process.Handle
    if (-not $process.WaitForExit($timeoutSeconds * 1000)) { $process.Kill(); return -1 }
    $process.WaitForExit()
    return $process.ExitCode
}

function Read-Report([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    try { return Get-Content $path -Raw | ConvertFrom-Json } catch { return $null }
}

function Get-SampleDevices([string]$exePath) {
    $report = Join-Path $env:TEMP "forge-devices-$PID.json"
    $code = Invoke-Json $exePath @('--list', '--json') 60 $report
    $list = Read-Report $report
    Remove-Item $report, "$report.err" -Force -ErrorAction SilentlyContinue
    if ($code -ne 0 -or -not $list) { throw "$exePath did not list its sample devices (exit $code)." }
    return @($list)
}

function Test-StartCheck([string]$name, [string]$exePath) {
    $folder = Split-Path $exePath -Parent
    foreach ($required in 'OfflinePptxForge.pri', 'App.xbf', 'Microsoft.UI.Xaml.Controls.pri') {
        if (-not (Test-Path (Join-Path $folder $required))) { throw "The $name package is missing $required, so its window can't open." }
    }
    $work = Join-Path $env:TEMP "forge-check-$name"
    if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $work | Out-Null

    $code = Invoke-Json $exePath @('--self-test', '--json') 120 "$work\self-test.json"
    $self = Read-Report "$work\self-test.json"
    if ($code -ne 0 -or -not $self) { throw "The $name package failed its self-test (exit $code; $($self.failed) checks failed)." }

    $code = Invoke-Json $exePath @('--probe', '--json') 60 "$work\probe.json"
    $probe = Read-Report "$work\probe.json"
    if ($code -ne 0 -or -not $probe) { throw "The $name package's probe did not run (exit $code)." }
    if ($probe.package.name -ne $name) { throw "The $name package calls itself $($probe.package.name), so its silicon stamp did not reach the build." }

    # The window's own page check on this PC. Decks and data go to the work folder; models stay in the shared Foundry Local cache.
    $process = Start-Process $exePath -ArgumentList '--smoke', '--window-size', '1280x860', '--report', "`"$work\smoke.json`"",
        '--data', "`"$work\data`"", '--out', "`"$work\decks`"" -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(900000)) { $process.Kill(); throw "The $name package's window did not finish its page check in 15 minutes." }
    $process.WaitForExit()
    $smoke = Read-Report "$work\smoke.json"
    if ($process.ExitCode -ne 0 -or -not $smoke) {
        $why = if ($smoke) { (@($smoke.failures) | Select-Object -First 3) -join '; ' } else { 'no report' }
        throw "The $name package's window failed its page check (exit $($process.ExitCode)): $why"
    }
    $checks = @($smoke.checks).Count
    Remove-Item $work -Recurse -Force
    return "self-test $($self.passed) checks; probe names $($probe.package.name) (this PC takes $($probe.package.recommended)); window passed $checks page checks"
}

$dotnetExe = Find-Dotnet
$version = Get-ForgeVersion
$armWindows = (Get-CimInstance Win32_OperatingSystem).OSArchitecture -match 'ARM'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
Write-Host "Offline PPTX Forge $version  (.NET: $dotnetExe)"

$built = @()
foreach ($name in $Package) {
    $family, $arch = $name -split '-', 2
    $rid = "win-$arch"
    $platform = if ($arch -eq 'arm64') { 'ARM64' } else { 'x64' }
    $folder = Join-Path $Out "OfflinePptxForge-$name"
    Assert-NotRunningFrom $folder
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
    $zipPath = Join-Path $Out "OfflinePptxForge-$name-$version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Write-Host "Publishing $name..."
    & $dotnetExe publish $project -c Release -r $rid --self-contained true "-p:Platform=$platform" "-p:ForgeSilicon=$family" -o $folder -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name (exit $LASTEXITCODE)." }
    $exe = Join-Path $folder 'OfflinePptxForge.exe'
    if (-not (Test-Path $exe)) { throw "Publish for $name produced no OfflinePptxForge.exe." }
    Copy-ForgeFiles $folder
    $runnable = $arch -eq 'x64' -or $armWindows

    if ($SkipStartCheck) { Write-Host '  start check skipped' }
    elseif (-not $runnable) { Write-Host '  start check skipped: Arm64 packages run only on Arm64 Windows' -ForegroundColor Yellow }
    else { Write-Host "  start check: $(Test-StartCheck $name $exe)" }
    $built += [pscustomobject]@{ Name = $name; Folder = $folder; Exe = $exe; Zip = $zipPath; Runnable = $runnable }
}

# Every package lists the same sample devices, so the README-FIRST device lists come from one that runs here.
$lister = $built | Where-Object Runnable | Select-Object -First 1
$devices = if ($lister) { Get-SampleDevices $lister.Exe } else { @() }
if (-not $lister) { Write-Host 'README-FIRST.txt has no device list: no package here can run on this PC.' -ForegroundColor Yellow }
foreach ($item in $built) {
    Write-ReadmeFirst $item.Folder $item.Name $version $devices
    $sizeMb = [math]::Round(((Get-ChildItem $item.Folder -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
    Write-Host "$($item.Folder) ($sizeMb MB)"
    if ($Zip) {
        Compress-Archive -Path (Join-Path $item.Folder '*') -DestinationPath $item.Zip -CompressionLevel Optimal
        Write-Host ("  {0} ({1:N0} MB)" -f $item.Zip, ((Get-Item $item.Zip).Length / 1MB))
    }
}
Write-Host 'BUILD OK' -ForegroundColor Green
exit 0
