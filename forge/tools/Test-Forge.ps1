<#
.SYNOPSIS
    Checks the Offline PPTX Forge on this PC.

.DESCRIPTION
    1. Self-test: the Forge's own rules (device plans, package names, deck checks). No models needed.
    2. Probe: what the Forge reads from this PC, the model and lane it plans for decks, and whether this copy is the package
       made for this PC.
    3. Window: opens the Forge (it never takes keyboard focus), checks every page through UI Automation, previews sample
       Surfaces, and saves screenshots. No model is loaded.
    4. -Live: the same window run also makes real decks with the local model: one stopped part-way, one from a pre-staged
       brief, and one from a brief typed in plain words. It reports the model, lane, time and grade of each deck.
       The recommended model is downloaded only with -AllowDownload; without it a model already on this PC is used.
    5. -Share: writes summary.txt and zips the reports, screenshots and decks to the Desktop, ready to send back.

    Reports, screenshots and the decks the check makes go to -Out (default: %LOCALAPPDATA%\OfflinePptxForge\checks\<time>),
    so the check leaves Documents\Offline PPTX Forge alone. Models go to the usual Foundry Local cache, so the app reuses
    them afterwards. Exit 0 when every check passes.
    Test-ThisPC.cmd in each package runs: -Live -AllowDownload -Share.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Test-Forge.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File forge\tools\Test-Forge.ps1 -Live
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [string]$Out,
    [switch]$Live,
    [switch]$AllowDownload,
    [switch]$Share
)

$ErrorActionPreference = 'Stop'

function Find-ForgeExe {
    $nearby = Join-Path $PSScriptRoot 'OfflinePptxForge.exe'
    if (Test-Path $nearby) { return $nearby }
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $rid = if ($arch -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $platform = if ($arch -eq 'ARM64') { 'ARM64' } else { 'x64' }
    $forge = Split-Path $PSScriptRoot -Parent
    # A developer build first: it passes the package check on any PC, while dist holds one package per silicon family.
    $candidates = @(Join-Path $forge "app\OfflinePptxForge\bin\$platform\Release\net10.0-windows10.0.26100.0\$rid\OfflinePptxForge.exe")
    $candidates += @(Get-ChildItem (Join-Path $forge 'dist') -Directory -Filter "OfflinePptxForge-*-$($rid.Substring(4))" -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'OfflinePptxForge.exe' })
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw 'OfflinePptxForge.exe not found. Run forge\tools\Build-ForgePackages.ps1 first, or pass -Exe.'
}

# Runs the Forge and waits. With a stdout file, the console output (the --json reports) goes there.
function Invoke-Forge([string[]]$arguments, [int]$timeoutSeconds, [string]$stdout) {
    $start = @{ FilePath = $Exe; ArgumentList = $arguments; PassThru = $true; NoNewWindow = $true }
    if ($stdout) { $start.RedirectStandardOutput = $stdout; $start.RedirectStandardError = "$stdout.err" }
    $process = Start-Process @start
    # Caching the handle keeps the exit code readable after WaitForExit.
    $null = $process.Handle
    if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
        $process.Kill()
        return -1
    }
    $process.WaitForExit()
    if ($stdout -and (Test-Path "$stdout.err") -and (Get-Item "$stdout.err").Length -eq 0) { Remove-Item "$stdout.err" -Force }
    return $process.ExitCode
}

function Read-Json([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    try { return Get-Content $path -Raw | ConvertFrom-Json } catch { return $null }
}

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Check = $name; Ok = $ok; Detail = $detail })
    $label = if ($ok) { 'PASS' } else { 'FAIL' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("{0}  {1,-28} {2}" -f $label, $name, $detail) -ForegroundColor $color
}

try {
    if (-not $Exe) { $Exe = Find-ForgeExe }
    $Exe = (Resolve-Path $Exe).Path
    if (-not $Out) { $Out = Join-Path $env:LOCALAPPDATA ("OfflinePptxForge\checks\" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    $Out = (Resolve-Path $Out).Path
    Write-Host "Forge: $Exe"
    Write-Host "Reports: $Out`n"

    $report = Join-Path $Out 'self-test.json'
    $code = Invoke-Forge @('--self-test', '--json') 120 $report
    $self = Read-Json $report
    $detail = if ($self) { "$($self.passed) of $($self.passed + $self.failed) checks" } else { "exit $code" }
    Add-Result 'Self-test' ($code -eq 0 -and $self) $detail
    if ($self) { $self.checks | Where-Object { -not $_.passed } | ForEach-Object { Write-Host "      $($_.name): $($_.detail)" -ForegroundColor Red } }

    $null = Invoke-Forge @('--probe') 60 (Join-Path $Out 'probe.txt')
    $report = Join-Path $Out 'probe.json'
    $code = Invoke-Forge @('--probe', '--json') 60 $report
    $probe = Read-Json $report
    $detail = "exit $code"
    if ($probe) {
        $plan = $probe.plans.'structured-json'
        $deck = if ($plan.primary) { "$($plan.primary.title) on the $("$($plan.primaryLane)".ToUpperInvariant())" } else { 'no local model fits' }
        $detail = "$($probe.profile.product) -> $($probe.profile.pcClassLabel); decks: $deck"
    }
    Add-Result 'This PC' ($code -eq 0 -and $probe) $detail
    if ($probe -and $probe.package) {
        $info = $probe.package
        $copy = if ($info.silicon -eq 'any') { "developer build ($($info.name))" } else { $info.name }
        if ($info.note) { Add-Result 'Package' $false "this copy is $copy, but this PC takes $($info.recommended): use that package" }
        else { Add-Result 'Package' $true "$copy; this PC takes $($info.recommended)" }
    }

    # One window run: the page checks, and with -Live the decks as well.
    $report = Join-Path $Out 'window.json'
    $arguments = @(if ($Live) { '--smoke-live' } else { '--smoke' })
    $arguments += @('--window-size', '1280x860', '--report', "`"$report`"", '--capture', "`"$(Join-Path $Out 'screenshots')`"",
        '--data', "`"$(Join-Path $Out 'data')`"", '--out', "`"$(Join-Path $Out 'decks')`"")
    if ($Live -and $AllowDownload) { $arguments += '--allow-download' }
    # A first live run can spend most of an hour downloading the model on slow Wi-Fi.
    $minutes = if (-not $Live) { 15 } elseif ($AllowDownload) { 120 } else { 45 }
    if ($Live) { Write-Host "`nMaking decks in the Forge window (up to $minutes minutes; leave the PC alone)..." }
    $code = Invoke-Forge $arguments ($minutes * 60)
    $window = Read-Json $report
    if ($code -eq -1) { Add-Result 'Window' $false "timed out after $minutes minutes" }
    elseif (-not $window) { Add-Result 'Window' $false "exit $code, no report" }
    else {
        $checks = @($window.checks)
        $page = @($checks | Where-Object { $_.name -notlike 'live*' })
        $pageFailed = @($page | Where-Object { -not $_.passed })
        Add-Result 'Window' ($pageFailed.Count -eq 0) "$($page.Count - $pageFailed.Count) of $($page.Count) page checks, $(@($window.screenshots).Count) screenshots"
        $pageFailed | Select-Object -First 8 | ForEach-Object { Write-Host "      $($_.name): $($_.detail)" -ForegroundColor Red }
        if ($Live) {
            $liveChecks = @($checks | Where-Object { $_.name -like 'live*' })
            $liveFailed = @($liveChecks | Where-Object { -not $_.passed })
            $recommended = $liveChecks | Where-Object { $_.name -eq 'live: the first deck uses the recommended model' } | Select-Object -First 1
            if ($recommended) { Add-Result 'Model' ([bool]$recommended.passed) $recommended.detail }
            foreach ($timing in @($liveChecks | Where-Object { $_.name -match '^live \((.+)\) timing$' })) {
                $label = $timing.name -replace '^live \((.+)\) timing$', '$1'
                $deckChecks = @($liveChecks | Where-Object { $_.name.StartsWith("live ($label)") })
                $model = $deckChecks | Where-Object { $_.name -eq "live ($label) model" } | Select-Object -First 1
                $text = $timing.detail + $(if ($model) { "; $($model.detail)" })
                Add-Result "Deck: $label" (@($deckChecks | Where-Object { -not $_.passed }).Count -eq 0) $text
            }
            $decks = @(Get-ChildItem (Join-Path $Out 'decks') -Recurse -Filter *.pptx -ErrorAction SilentlyContinue).Count
            Add-Result 'Live decks' ($liveChecks.Count -gt 0 -and $liveFailed.Count -eq 0) "$($liveChecks.Count - $liveFailed.Count) of $($liveChecks.Count) live checks, $decks decks saved"
            $liveFailed | Select-Object -First 8 | ForEach-Object { Write-Host "      $($_.name): $($_.detail)" -ForegroundColor Red }
        }
        if ($code -ne 0 -and @($results | Where-Object { -not $_.Ok }).Count -eq 0) { Add-Result 'Window exit' $false "exit $code; see window.json" }
    }

    $failed = @($results | Where-Object { -not $_.Ok }).Count
    $verdict = if ($failed -eq 0) { "READY: all $($results.Count) checks passed." } else { "NOT READY: $failed of $($results.Count) checks failed." }
    if ($Share) {
        $model = if ($probe -and $probe.profile.product) { "$($probe.profile.product)" } else { 'unknown PC' }
        $lines = @(
            "Offline PPTX Forge check, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
            "PC:       $(if ($probe) { $probe.profile.headline } else { $model })"
            "Class:    $(if ($probe) { "$($probe.profile.pcClassLabel) ($($probe.profile.deviceClassLabel))" })"
            "Package:  $(if ($probe) { "$($probe.package.name) (this PC takes $($probe.package.recommended))" })"
            "Forge:    $Exe"
            "Windows:  $([Environment]::OSVersion.VersionString), $env:PROCESSOR_ARCHITECTURE process"
            ''
        ) + @($results | ForEach-Object { "{0}  {1,-28} {2}" -f $(if ($_.Ok) { 'PASS' } else { 'FAIL' }), $_.Check, $_.Detail }) + @('', $verdict)
        [IO.File]::WriteAllLines((Join-Path $Out 'summary.txt'), [string[]]$lines)
        # The app's own logs from the last day, next to the ones the check wrote under data.
        $appData = Join-Path $env:LOCALAPPDATA 'OfflinePptxForge'
        $logs = @(Get-ChildItem $appData -File -Filter *.log -ErrorAction SilentlyContinue) + @(Get-ChildItem (Join-Path $appData 'logs') -File -ErrorAction SilentlyContinue)
        $recent = @($logs | Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-1) })
        if ($recent.Count -gt 0) {
            New-Item -ItemType Directory -Force -Path (Join-Path $Out 'logs') | Out-Null
            $recent | Copy-Item -Destination (Join-Path $Out 'logs') -Force
        }
        $safe = ($model -replace '[^A-Za-z0-9]+', '-').Trim('-')
        $zip = Join-Path ([Environment]::GetFolderPath('Desktop')) ("OfflinePptxForge-results-$safe-" + (Get-Date -Format 'yyyyMMdd-HHmm') + '.zip')
        Compress-Archive -Path (Join-Path $Out '*') -DestinationPath $zip -Force
        Write-Host "`nResults saved to $zip (send this file back)."
    }
    Write-Host ''
    if ($failed -eq 0) { Write-Host $verdict -ForegroundColor Green; exit 0 }
    Write-Host "$verdict Reports: $Out" -ForegroundColor Red
    exit 1
}
catch {
    Write-Host "Check error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
