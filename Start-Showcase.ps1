[CmdletBinding()]
param(
    [int]$Port = 8790,
    [switch]$RehearsalOnly,
    [switch]$Prepare,
    [switch]$NoBrowser,
    [ValidateSet('qwen2.5-coder-14b','gpt-oss-20b')]
    [string]$Model = 'qwen2.5-coder-14b'
)
$ErrorActionPreference = 'Stop'
$existing = $null
try { $existing = Invoke-RestMethod "http://127.0.0.1:$Port/api/status" -TimeoutSec 2 }
catch { Write-Verbose "No reusable showcase on port $Port`: $($_.Exception.Message)" }
if ($existing) {
    $appId = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'edition.json') -Raw | ConvertFrom-Json).serviceAppId
    if ($existing.app_id -ne $appId) { throw "Port $Port is used by another application. Choose -Port with a free port." }
    if ($RehearsalOnly -or $Prepare) { throw 'A showcase is already running. Stop its launch terminal before changing startup mode.' }
    if ($PSBoundParameters.ContainsKey('Model') -and $existing.engine.selected_model -ne $Model) {
        $null = Invoke-RestMethod "http://127.0.0.1:$Port/api/model" -Method Post -ContentType 'application/json' -Headers @{'X-Showcase-Token'=$existing.token} -Body (@{model=$Model}|ConvertTo-Json)
    }
    Write-Host "Showcase is already running: http://127.0.0.1:$Port"
    if (-not $NoBrowser) { Start-Process "http://127.0.0.1:$Port" }
    return
}
$localPython = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
$existingPython = Join-Path (Split-Path $PSScriptRoot -Parent) 'hydrafusion-autorouting-demo\.venv\Scripts\python.exe'
$configuredPython = if ($env:HYBRID_STUDIO_PYTHON) { $env:HYBRID_STUDIO_PYTHON.Trim().Trim('"') }
if ($configuredPython) {
    if (-not (Test-Path -LiteralPath $configuredPython)) { throw "HYBRID_STUDIO_PYTHON points to a missing file: $configuredPython" }
    $python = $configuredPython
}
elseif (Test-Path -LiteralPath $localPython) { $python = $localPython }
elseif (Test-Path -LiteralPath $existingPython) { $python = $existingPython }
elseif ($RehearsalOnly) {
    $python = (Get-Command python -ErrorAction Stop).Source
} else {
    throw 'Foundry Local Python environment not found. See README.md to create .venv, or use -RehearsalOnly.'
}
$arguments = @('-u', ('"{0}"' -f (Join-Path $PSScriptRoot 'server.py')), '--port', $Port)
$arguments += @('--model', $Model)
if ($RehearsalOnly) { $arguments += '--rehearsal-only' }
if ($Prepare) { $arguments += '--prepare' }
$process = Start-Process -FilePath $python -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -NoNewWindow -PassThru
try {
    $ready = $false
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
        try {
            $null = Invoke-RestMethod "http://127.0.0.1:$Port/api/status" -TimeoutSec 2
            $ready = $true
            break
        } catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $ready -or $process.HasExited) { throw 'Showcase failed to start. Inspect the terminal error; check whether the port is already in use.' }
    Write-Host "Showcase: http://127.0.0.1:$Port — Ctrl+C to stop."
    if (-not $NoBrowser) { Start-Process "http://127.0.0.1:$Port" }
    Wait-Process -Id $process.Id
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
