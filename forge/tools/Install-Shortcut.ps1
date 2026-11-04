<#
.SYNOPSIS
    Adds a Start menu shortcut (and with -Desktop, a desktop shortcut) for the Offline PPTX Forge.

.DESCRIPTION
    Finds OfflinePptxForge.exe next to this script (a published package folder), else in forge\dist for this PC's
    processor, else in the Release build. The shortcut carries the app's identity, so the running Forge groups under it
    on the taskbar.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Install-Shortcut.ps1 -Desktop
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [switch]$Desktop,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$name = 'Offline PPTX Forge'
$appId = 'SurfaceHybridIntelligence.OfflinePptxForge'

function Find-ForgeExe {
    $nearby = Join-Path $PSScriptRoot 'OfflinePptxForge.exe'
    if (Test-Path $nearby) { return $nearby }
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $rid = if ($arch -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    $platform = if ($arch -eq 'ARM64') { 'ARM64' } else { 'x64' }
    $forge = Split-Path $PSScriptRoot -Parent
    $candidates = @(Get-ChildItem (Join-Path $forge 'dist') -Directory -Filter "OfflinePptxForge-*-$($rid.Substring(4))" -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'OfflinePptxForge.exe' })
    $candidates += Join-Path $forge "app\OfflinePptxForge\bin\$platform\Release\net10.0-windows10.0.26100.0\$rid\OfflinePptxForge.exe"
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw 'OfflinePptxForge.exe not found. Run forge\tools\Build-ForgePackages.ps1 first, or pass -Exe.'
}

try {
    if (-not $Exe) { $Exe = Find-ForgeExe }
    $Exe = (Resolve-Path $Exe).Path
    if (-not ('ForgeShortcut' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ForgeShortcut {
    [StructLayout(LayoutKind.Sequential, Pack=4)]
    struct PropertyKey { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size=24)]
    struct PropVariant {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink {}
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
    public static void SetAppId(string path, string appId) {
        object link = new ShellLink();
        try {
            var file = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
            file.Load(path, 2);
            var store = (IPropertyStore)link;
            var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            var value = new PropVariant { vt = 31, pointer = Marshal.StringToCoTaskMemUni(appId) };
            try { store.SetValue(ref key, ref value); store.Commit(); file.Save(path, true); }
            finally { Marshal.FreeCoTaskMem(value.pointer); }
        } finally { Marshal.FinalReleaseComObject(link); }
    }
}
'@
    }
    $folders = @(Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs')
    if ($Desktop) { $folders += [Environment]::GetFolderPath('Desktop') }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in $folders) {
        $path = Join-Path $folder "$name.lnk"
        $shortcut = $shell.CreateShortcut($path)
        $shortcut.TargetPath = $Exe
        $shortcut.WorkingDirectory = Split-Path $Exe -Parent
        $shortcut.IconLocation = "$Exe,0"
        $shortcut.Description = 'Offline PPTX Forge: writes a PowerPoint deck on this PC with local AI'
        $shortcut.Save()
        [ForgeShortcut]::SetAppId($path, $appId)
        Write-Host "Shortcut: $path"
    }
    Write-Host "App: $Exe"
    if ($Launch) { Start-Process $Exe | Out-Null; Write-Host 'Started the Forge.' }
    exit 0
}
catch {
    Write-Host "Shortcut setup failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
