[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$AppProcessId,
    [string]$SelectAutomationId,
    [string]$InvokeAutomationId,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Snapshot
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$process = Get-Process -Id $AppProcessId
if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The specified app has no native window.' }
$window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$edition = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'edition.json') -Raw | ConvertFrom-Json
if ($window.Current.ProcessId -ne $AppProcessId -or -not $window.Current.Name.StartsWith($edition.displayName)) {
    throw "The specified window is not $($edition.displayName)."
}
function Find-Control([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $element) { throw "Native automation control '$Id' was not found." }
    return $element
}
if ($SelectAutomationId) {
    $control = Find-Control $SelectAutomationId
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 450
}
if ($InvokeAutomationId) {
    $control = Find-Control $InvokeAutomationId
    if (-not $control.Current.IsEnabled) { throw "Native control '$InvokeAutomationId' is disabled." }
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 450
}
$elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
$visible = foreach ($element in $elements) {
    $current = $element.Current
    if (-not $current.IsOffscreen -and ($current.Name -or $current.AutomationId)) {
        [pscustomobject]@{
            Id = $current.AutomationId
            Name = $current.Name
            Type = $current.ControlType.ProgrammaticName
            Enabled = $current.IsEnabled
            Bounds = $current.BoundingRectangle.ToString()
        }
    }
}
$result = [ordered]@{ ProcessId = $AppProcessId; Title = $window.Current.Name; Controls = @($visible) }
if ($Snapshot) {
    $directory = Join-Path (Split-Path $PSScriptRoot -Parent) '.runtime'
    $null = New-Item -ItemType Directory -Path $directory -Force
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory "$Snapshot-ui.json") -Encoding UTF8
}
$result | ConvertTo-Json -Depth 5
