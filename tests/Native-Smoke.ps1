[CmdletBinding()]
param(
    [ValidateRange(1, 65535)][int]$Port = 8790,
    [switch]$Live,
    [string]$WindowSize,
    [switch]$Rebuild,
    [ValidateRange(60, 3600)][int]$TimeoutSeconds = 900
)
# Native UX checks for Surface Hybrid Intelligence Studio:
#  1. --self-test: deterministic sizing/platform/parser/privacy/export checks (no UI, no network).
#  2. --smoke: drives the real WinUI window (navigation, sizing controls, business case via the
#     service, rehearsal for every scenario, exports, screenshots). -Live also streams every
#     scenario on the RTX GPU and checks CUDA provenance. Results: .runtime\native\*.json, screens\*.png.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root '.runtime\native'
$exe = Join-Path $root 'native\bin\Release\net10.0-windows10.0.26100.0\win-arm64\HybridIntelligenceStudio.exe'

if ($Rebuild -or -not (Test-Path -LiteralPath $exe)) {
    $dotnet = @($env:HYBRID_STUDIO_DOTNET, (Join-Path (Split-Path $root -Parent) 'asu-agentic-showcase\.tools\dotnet\dotnet.exe')) |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $dotnet) { $dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source }
    if (-not $dotnet) {
        $dotnet = @((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'), (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')) |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $dotnet) { throw 'No .NET SDK found. Install the .NET 10 SDK or set HYBRID_STUDIO_DOTNET to dotnet.exe.' }
    & $dotnet build (Join-Path $root 'native\HybridIntelligenceStudio.csproj') -c Release -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
}

function Invoke-App([string[]]$Arguments, [int]$Timeout) {
    $p = Start-Process -FilePath $exe -ArgumentList $Arguments -WorkingDirectory (Split-Path $exe) -PassThru
    if (-not $p.WaitForExit($Timeout * 1000)) {
        Stop-Process -Id $p.Id
        throw "HybridIntelligenceStudio timed out: $($Arguments -join ' ')"
    }
    return $p.ExitCode
}

$failed = $false
$selfCode = Invoke-App @('--self-test', '--root', "`"$root`"") 120
$self = Get-Content (Join-Path $runtime 'self-test.json') -Raw | ConvertFrom-Json
Write-Host ("Self-test: {0}/{1} passed (exit {2})" -f ($self.total - $self.failed), $self.total, $selfCode)
$self.results | Where-Object { -not $_.passed } | ForEach-Object { Write-Host "  FAIL $($_.name): $($_.detail)" -ForegroundColor Red }
if ($selfCode -ne 0) { $failed = $true }

$smokeArgs = @('--smoke', '--exit-after-smoke', '--port', $Port, '--root', "`"$root`"")
if ($Live) { $smokeArgs += '--smoke-live' }
if ($WindowSize) { $smokeArgs += @('--window-size', $WindowSize) }
$smokeCode = Invoke-App $smokeArgs $TimeoutSeconds
$smoke = Get-Content (Join-Path $runtime 'native-smoke.json') -Raw | ConvertFrom-Json
$passedChecks = @($smoke.checks | Where-Object passed).Count
Write-Host ("UI smoke: {0}/{1} checks passed, live={2} (exit {3})" -f $passedChecks, @($smoke.checks).Count, $smoke.live_inference_checked, $smokeCode)
$smoke.failures | ForEach-Object { Write-Host "  FAIL $_" -ForegroundColor Red }
foreach ($run in $smoke.live_runs) {
    Write-Host ("  live {0}: {1} on {2}, TTFT {3} ms, {4} tokens in {5} ms" -f $run.scenario, $run.model, $run.provider, $run.ttft_ms, $run.output_tokens, $run.total_ms)
}
Write-Host "Screenshots: $(Join-Path $runtime 'screens')"
if ($smokeCode -ne 0 -or -not $smoke.passed) { $failed = $true }
if ($failed) { exit 1 }
