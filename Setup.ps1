<#
.SYNOPSIS
One-time setup on a Windows ARM64 PC with an NVIDIA GPU: Python environment, desktop shortcut and (with -Prepare) every model.
.DESCRIPTION
1. Finds an ARM64 Python 3.12 to 3.14 (3.13 preferred, or pass -Python), creates .venv and installs requirements.txt
   with the tested dependency versions in constraints.txt.
2. Installs the desktop shortcut for the native app (skip with -NoShortcut). The release zip already contains the
   self-contained app; in a git clone, build it first with .\Start-NativeShowcase.ps1 -Rebuild (needs the .NET 10 SDK).
3. -Prepare starts the service with downloads allowed on a free loopback port, waits for the GPU model, then runs every
   live demo once (headless pre-flight). That downloads the CUDA provider, the GPU media libraries and every model once,
   then shows each demo runs end to end. The first run takes a while and needs a lot of free disk.
Runs on Windows PowerShell 5.1 and PowerShell 7. Exit code 0 when every step succeeded
(with -Prepare: every live demo ran end to end on the GPU). Setup.cmd runs this with -Prepare.
.EXAMPLE
.\Setup.ps1 -Prepare
#>
[CmdletBinding()]
param(
    [switch]$Prepare,
    [switch]$NoShortcut,
    # Optional service port for the shortcut, so two editions can run side by side (the app defaults to 8790).
    [ValidateRange(1024, 65535)][int]$Port,
    [string]$Python,
    [ValidateRange(60, 86400)][int]$TimeoutSeconds = 7200
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$edition = Get-Content -LiteralPath (Join-Path $root 'edition.json') -Raw -Encoding utf8 | ConvertFrom-Json
Write-Host "$($edition.displayName) setup in $root" -ForegroundColor Cyan

function Write-Step([string]$Text) { Write-Host "== $Text" -ForegroundColor Cyan }

function Invoke-Quiet([string]$Path, [string[]]$Arguments) {
    # Windows PowerShell 5.1 turns redirected native stderr into terminating errors under 'Stop'.
    $saved = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $output = & $Path @Arguments 2>$null; return [pscustomobject]@{ Code = $LASTEXITCODE; Output = @($output) } }
    catch { return [pscustomobject]@{ Code = -1; Output = @() } }
    finally { $ErrorActionPreference = $saved }
}

function Get-PythonInfo([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $probe = Invoke-Quiet $Path @('-c', 'import platform, sys; print(platform.machine(), sys.version_info[0], sys.version_info[1])')
    if ($probe.Code -ne 0 -or -not $probe.Output) { return $null }
    $parts = "$($probe.Output[-1])".Trim() -split '\s+'
    if ($parts.Count -ne 3) { return $null }
    return [pscustomobject]@{ Path = $Path; Machine = $parts[0]; Version = [version]('{0}.{1}' -f $parts[1], $parts[2]) }
}

function Test-SupportedPython($Info) {
    # The pinned set in constraints.txt has Windows ARM64 wheels for Python 3.12 to 3.14.
    return $Info -and $Info.Machine -eq 'ARM64' -and $Info.Version -ge [version]'3.12' -and $Info.Version -lt [version]'3.15'
}

function Find-BasePython {
    if ($Python) {
        $info = Get-PythonInfo $Python
        if (-not (Test-SupportedPython $info)) { throw "-Python must point to an ARM64 Python 3.12 to 3.14: $Python" }
        return $info
    }
    $candidates = [System.Collections.Generic.List[string]]::new()
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        foreach ($line in (Invoke-Quiet $launcher.Source @('-0p')).Output) {
            if ("$line" -match '([A-Za-z]:\\.+?python\.exe)\s*$') { $candidates.Add($Matches[1]) }
        }
    }
    foreach ($pattern in @("$env:LOCALAPPDATA\Programs\Python\Python3*\python.exe", "$env:LOCALAPPDATA\Python\pythoncore-3*\python.exe",
            "$env:ProgramFiles\Python3*\python.exe")) {
        Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | ForEach-Object { $candidates.Add($_.FullName) }
    }
    $onPath = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($onPath -and $onPath.Source -notlike '*\WindowsApps\*') { $candidates.Add($onPath.Source) }
    $usable = @($candidates | Select-Object -Unique | ForEach-Object { Get-PythonInfo $_ } | Where-Object { Test-SupportedPython $_ })
    # 3.13 is the tested version; otherwise the newest supported one.
    $preferred = @($usable | Where-Object { $_.Version -eq [version]'3.13' }) + @($usable | Sort-Object Version -Descending)
    if ($preferred.Count -eq 0) {
        throw 'No ARM64 Python 3.12 to 3.14 was found. Install Python 3.13 for Windows ARM64 from python.org, or pass -Python <path to python.exe>.'
    }
    return $preferred[0]
}

$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
if ($arch -ne 'ARM64') { Write-Warning "This build targets Windows ARM64; this PC reports $arch." }

Write-Step 'Python environment'
$venvPython = Join-Path $root '.venv\Scripts\python.exe'
$venvInfo = Get-PythonInfo $venvPython
if ($venvInfo -and -not (Test-SupportedPython $venvInfo)) {
    throw "$venvPython is $($venvInfo.Machine) Python $($venvInfo.Version); ARM64 Python 3.12 to 3.14 is needed. Delete .venv and run setup again."
}
if (-not $venvInfo) {
    $base = Find-BasePython
    Write-Host "   Creating .venv with Python $($base.Version) ($($base.Path))"
    & $base.Path -m venv (Join-Path $root '.venv')
    if ($LASTEXITCODE -ne 0) { throw 'python -m venv failed.' }
}
Write-Host '   Installing requirements.txt with the tested versions in constraints.txt ...'
& $venvPython -m pip install --disable-pip-version-check --quiet -r (Join-Path $root 'requirements.txt') -c (Join-Path $root 'constraints.txt')
if ($LASTEXITCODE -ne 0) {
    throw 'pip install failed (see the error above). Check the network connection and that your package index (pip config list) serves the versions in constraints.txt, then run setup again.'
}
if ((Invoke-Quiet $venvPython @('-c', 'import foundry_local_sdk')).Code -ne 0) { throw 'The Foundry Local SDK does not import from .venv.' }
Write-Host '   .venv is ready' -ForegroundColor Green

$exe = Join-Path $root "native\bin\Release\net10.0-windows10.0.26100.0\win-arm64\$($edition.codeName).exe"
$openHint = "open '$($edition.shortcutName)' on the desktop"
if (-not $NoShortcut) {
    Write-Step 'Desktop shortcut'
    if (Test-Path -LiteralPath $exe) {
        $shortcutArgs = @{}
        if ($Port) { $shortcutArgs.Port = $Port }
        & (Join-Path $root 'tools\Install-NativeShortcut.ps1') @shortcutArgs
    } else {
        Write-Warning 'The native app is not built yet. Run .\Start-NativeShowcase.ps1 -Rebuild (needs the .NET 10 SDK), then tools\Install-NativeShortcut.ps1.'
        $openHint = 'start the app with .\Start-NativeShowcase.ps1'
    }
} else {
    $openHint = 'start the app with .\Start-NativeShowcase.ps1'
}

if (-not $Prepare) {
    Write-Host "Setup complete. Run .\Setup.ps1 -Prepare once to download and check every model, then $openHint." -ForegroundColor Green
    exit 0
}

Write-Step 'Download and check every model (headless pre-flight; the first run takes a while)'
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start(); $servicePort = $listener.LocalEndpoint.Port; $listener.Stop()
$runtime = Join-Path $root '.runtime'
$null = New-Item -ItemType Directory -Path $runtime -Force
$log = Join-Path $runtime 'setup-prepare.log'
$server = Start-Process -FilePath $venvPython -WorkingDirectory $root -WindowStyle Hidden -PassThru `
    -ArgumentList @('-u', ('"{0}"' -f (Join-Path $root 'server.py')), '--port', "$servicePort", '--prepare') `
    -RedirectStandardError $log -RedirectStandardOutput (Join-Path $runtime 'setup-prepare.out.log')
$base = "http://127.0.0.1:$servicePort"
$results = [System.Collections.Generic.List[object]]::new()
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $status = $null; $lastDetail = ''
    while ((Get-Date) -lt $deadline) {
        if ($server.HasExited) { throw "The service exited with code $($server.ExitCode). See $log" }
        try { $status = Invoke-RestMethod "$base/api/status" -TimeoutSec 5 -UseBasicParsing } catch { $status = $null }
        if ($status) {
            if ($status.app_id -ne $edition.serviceAppId) { throw "Port $servicePort answered as '$($status.app_id)'." }
            if ($status.engine.state -eq 'ready') { break }
            if ($status.engine.state -eq 'error') { throw "GPU model preparation failed: $($status.engine.detail)" }
            if ($status.engine.detail -and $status.engine.detail -ne $lastDetail) { $lastDetail = $status.engine.detail; Write-Host "   $lastDetail" }
        }
        Start-Sleep -Seconds 2
    }
    if (-not $status -or $status.engine.state -ne 'ready') { throw "The GPU model was not ready within $TimeoutSeconds s. See $log" }
    Write-Host "   $($status.engine.model.name) is ready on $($status.engine.model.provider)" -ForegroundColor Green
    $headers = @{ 'X-Showcase-Token' = $status.token }
    $catalog = Invoke-RestMethod "$base/api/demos" -TimeoutSec 30 -UseBasicParsing
    foreach ($demo in $catalog.demos) {
        Write-Host ('   {0} ...' -f $demo.title) -NoNewline
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $done = $null; $errors = [System.Collections.Generic.List[string]]::new()
        try {
            $body = @{ demo = $demo.id; mode = 'live' } | ConvertTo-Json -Compress
            $response = Invoke-WebRequest "$base/api/demo/run" -Method Post -Headers $headers -ContentType 'application/json' `
                -Body $body -TimeoutSec $TimeoutSeconds -UseBasicParsing
            $content = if ($response.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
            foreach ($line in ($content -split "`n")) {
                if (-not $line.Trim()) { continue }
                $item = $line | ConvertFrom-Json
                if ($item.type -eq 'done') { $done = $item }
                elseif ($item.type -eq 'error') { $errors.Add([string]$item.error) }
                elseif ($item.type -eq 'step' -and $item.state -eq 'failed') { $errors.Add([string]$item.error) }
            }
            if (-not $done -and $errors.Count -eq 0) { $errors.Add('The run ended without a result.') }
        } catch {
            $errors.Add($_.Exception.Message)
        }
        $ran = [bool]($done -and $done.steps_failed -eq 0 -and $errors.Count -eq 0)
        $passed = if ($done) { [int]$done.checks_passed } else { 0 }
        $total = if ($done) { [int]$done.checks_total } else { 0 }
        $seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
        $results.Add([pscustomobject]@{ demo = $demo.id; title = $demo.title; ran_end_to_end = $ran; checks_passed = $passed
            checks_total = $total; seconds = $seconds; errors = $errors.ToArray() })
        if ($ran) {
            $color = if ($passed -lt $total) { 'Yellow' } else { 'Green' }
            Write-Host (' {0} of {1} checks passed, {2} s' -f $passed, $total, $seconds) -ForegroundColor $color
        } else {
            Write-Host (' did not run end to end: {0}' -f ($errors -join ' | ')) -ForegroundColor Red
        }
    }
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}
$endToEnd = @($results | Where-Object { $_.ran_end_to_end }).Count
$allChecks = @($results | Where-Object { $_.ran_end_to_end -and $_.checks_passed -eq $_.checks_total }).Count
$report = [ordered]@{ schema = "$($edition.schemaPrefix)-setup-prepare/1"; finished_at = (Get-Date).ToString('o'); demos = $results.ToArray() }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runtime 'setup-prepare.json') -Encoding utf8
$summary = "$endToEnd of $($results.Count) live demos ran end to end on the GPU; $allChecks passed every check."
if ($results.Count -eq 0 -or $endToEnd -ne $results.Count) {
    Write-Host "$summary Fix the failures above and run .\Setup.ps1 -Prepare again (service log: $log)." -ForegroundColor Red
    exit 1
}
Write-Host "$summary Next, $openHint to present." -ForegroundColor Green
exit 0
