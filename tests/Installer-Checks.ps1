[CmdletBinding()]
param([string]$ArchiveDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$checks = 0
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Read-Script([string]$Relative) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root $Relative), [ref]$tokens, [ref]$errors)
    if ($errors) { throw "$Relative parser errors: $errors" }
    return $ast
}
function Assignment($Ast, [string]$Name) {
    $node = $Ast.Find({ param($n)
        $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq "`$$Name"
    }.GetNewClosure(), $true)
    if (-not $node) { throw "Missing assignment: $Name" }
    return $node.Extent.Text
}
$setupAst = Read-Script 'Setup.ps1'
foreach ($name in 'Invoke-Quiet', 'Get-PythonInfo', 'Test-SupportedPython', 'Get-DemoPreparationDecision') {
    $node = $setupAst.Find({ param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name
    }.GetNewClosure(), $true)
    Invoke-Expression $node.Extent.Text
}
function Invoke-Quiet([string]$Path, [string[]]$Arguments) {
    Assert ($Arguments[1] -like '*sysconfig.get_platform()*') 'Python probe must inspect interpreter architecture, not host machine'
    return [pscustomobject]@{ Code = 0; Output = @("$script:probePlatform 3 13") }
}
foreach ($probePlatform in 'win-arm64', 'win-amd64', 'win32') {
    $info = Get-PythonInfo (Join-Path $root 'Setup.ps1')
    $expectedMachine = switch ($probePlatform) { 'win-arm64' { 'ARM64' }; 'win-amd64' { 'AMD64' }; default { 'win32' } }
    Assert ($info.Machine -eq $expectedMachine) 'Python interpreter build architecture mapping failed'
}
foreach ($pythonMachine in 'ARM64', 'AMD64') {
    Assert (Test-SupportedPython ([pscustomobject]@{ Machine = $pythonMachine; Version = [version]'3.13' })) 'Native Python rejected'
    $other = if ($pythonMachine -eq 'ARM64') { 'AMD64' } else { 'ARM64' }
    Assert (-not (Test-SupportedPython ([pscustomobject]@{ Machine = $other; Version = [version]'3.13' }))) 'Emulated Python accepted'
    foreach ($version in '3.11', '3.15') {
        Assert (-not (Test-SupportedPython ([pscustomobject]@{ Machine = $pythonMachine; Version = [version]$version }))) 'Unsupported Python accepted'
    }
}
$available = [pscustomobject]@{ available = $true; blocker = $null }
$blocked = [pscustomobject]@{ available = $false; blocker = 'Model exceeds this PC memory budget.' }
Assert (Get-DemoPreparationDecision ([pscustomobject]@{ fit = @{ state = 'ready'; steps = @($available) } })).Eligible 'Ready demo was skipped'
foreach ($state in 'limited', 'unavailable', 'ready') {
    $decision = Get-DemoPreparationDecision ([pscustomobject]@{ fit = @{ state = $state; steps = @($available, $blocked) } })
    Assert (-not $decision.Eligible) "$state demo with a blocked step was accepted"
    Assert ($decision.Reason -like '*memory budget*') 'Blocker not reported'
}
Assert (-not (Get-DemoPreparationDecision ([pscustomobject]@{ id = 'fallback' })).Eligible) 'Unqualified fallback demo accepted'

$fixture = Join-Path $root ('.runtime\release\installer-checks-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture -Force
try {
    $sourceRoot = $root
    $edition = [pscustomobject]@{ codeName = 'HybridIntelligenceStudio' }
    foreach ($rid in 'win-arm64', 'win-x64') {
        $path = Join-Path $fixture "native\bin\Release\net10.0-windows10.0.26100.0\$rid\HybridIntelligenceStudio.exe"
        $null = New-Item -ItemType Directory -Path (Split-Path $path) -Force
        $null = New-Item -ItemType File -Path $path
    }
    foreach ($relative in 'Setup.ps1', 'Start-NativeShowcase.ps1', 'tools\Install-NativeShortcut.ps1') {
        $ast = Read-Script $relative
        $ridAssignment = Assignment $ast 'rid'
        $platformAssignment = Assignment $ast 'platform'
        $exeAssignment = Assignment $ast $(if ($relative -eq 'Start-NativeShowcase.ps1') { 'candidates' } else { 'exe' })
        foreach ($nativeArchitecture in 'ARM64', 'AMD64', 'x86') {
            $root = $fixture
            $threw = $false
            try { Invoke-Expression $ridAssignment } catch { $threw = $true }
            if ($nativeArchitecture -eq 'x86') { Assert $threw "$relative accepted unsupported architecture"; continue }
            Assert (-not $threw) "$relative rejected native architecture"
            Invoke-Expression $platformAssignment
            Invoke-Expression $exeAssignment
            $selected = if ($relative -eq 'Start-NativeShowcase.ps1') { @($candidates | Where-Object { Test-Path -LiteralPath $_ })[0] } else { $exe }
            $expected = if ($nativeArchitecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
            Assert ($selected -like "*\$expected\HybridIntelligenceStudio.exe") "$relative selected an emulated executable"
        }
        $root = $sourceRoot
    }
} finally {
    $root = $sourceRoot
    if ((Resolve-Path -LiteralPath $fixture).Path -ne [System.IO.Path]::GetFullPath($fixture)) { throw 'Unsafe fixture cleanup' }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}

if ($ArchiveDirectory) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [xml]$project = Get-Content -LiteralPath (Join-Path $root 'native\HybridIntelligenceStudio.csproj') -Raw
    $version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    foreach ($family in 'Snapdragon-arm64', 'Intel-x64', 'NVIDIA-arm64', 'NVIDIA-x64') {
        $name = "HybridIntelligenceStudio-$version-$family"
        $rid = if ($family -like '*-arm64') { 'win-arm64' } else { 'win-x64' }
        $zip = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $ArchiveDirectory "$name.zip"))
        try {
            $entries = @($zip.Entries.FullName | ForEach-Object { $_.Replace('/', '\') })
            Assert (@($entries | Where-Object { $_ -like "$name\native\bin\Release\*\$rid\HybridIntelligenceStudio.exe" }).Count -eq 1) "$family missing native executable"
            $other = if ($rid -eq 'win-arm64') { 'win-x64' } else { 'win-arm64' }
            Assert (@($entries | Where-Object { $_ -like "*\native\bin\*\$other\*" }).Count -eq 0) "$family includes wrong-architecture output"
            Assert ($entries -contains "$name\README-FIRST.txt") "$family missing first-use instructions"
            Assert ($entries -contains "$name\installer-pack.json") "$family missing pack metadata"
            Assert (@($entries | Where-Object { $_ -match '\.(onnx|gguf|safetensors|pdb|pyc)$' -or $_ -match '\\(\.venv|obj|__pycache__)\\' }).Count -eq 0) "$family includes weights, cache or intermediates"
            $entry = $zip.Entries | Where-Object { $_.FullName.Replace('/', '\') -like "*\native\bin\Release\*\$rid\HybridIntelligenceStudio.exe" }
            $stream = $entry.Open()
            $memory = [System.IO.MemoryStream]::new()
            try { $stream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $stream.Dispose(); $memory.Dispose() }
            $machine = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3c) + 4)
            Assert ($machine -eq $(if ($rid -eq 'win-arm64') { 0xaa64 } else { 0x8664 })) "$family wrong PE machine"
        } finally { $zip.Dispose() }
    }
    foreach ($line in (Get-Content -LiteralPath (Join-Path $ArchiveDirectory 'SHA256SUMS.txt'))) {
        $hash, $filename = $line -split '\s+', 2
        Assert ((Get-FileHash -LiteralPath (Join-Path $ArchiveDirectory $filename) -Algorithm SHA256).Hash -eq $hash) "Hash mismatch: $filename"
    }
}
Write-Host "$checks installer/package checks passed."
