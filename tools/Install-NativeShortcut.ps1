[CmdletBinding()]
param()
# Installs a desktop shortcut for the built native app with this edition's icon and taskbar identity (edition.json).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$edition = Get-Content -LiteralPath (Join-Path $root 'edition.json') -Raw -Encoding utf8 | ConvertFrom-Json
$exe = Join-Path $root "native\bin\Release\net10.0-windows10.0.26100.0\win-arm64\$($edition.codeName).exe"
$icon = Join-Path $root 'native\Assets\AppIcon.ico'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $icon)) {
    throw 'Build the native app and generate its assets (tools\Sync-Edition.ps1) before installing the shortcut.'
}
$desktop = [Environment]::GetFolderPath('Desktop')
$path = Join-Path $desktop "$($edition.shortcutName).lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($path)
$link.TargetPath = $exe
$link.Arguments = '--root "{0}"' -f $root
$link.WorkingDirectory = Split-Path $exe -Parent
$link.IconLocation = "$icon,0"
$link.Description = "$($edition.displayName) - native WinUI 3 local AI showcase"
$link.Save()
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StudioShortcutIdentity {
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Text;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path, IntPtr binding, uint flags, ref Guid iid, out IPropertyStore store);
    public static void SetAppId(string path, string appId) {
        Guid iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 2, ref iid, out store));
        var key = new PropertyKey {
            Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5
        };
        var value = new PropVariant { Type = 31, Text = Marshal.StringToCoTaskMemUni(appId) };
        try {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value));
            Marshal.ThrowExceptionForHR(store.Commit());
        } finally {
            Marshal.FreeCoTaskMem(value.Text);
            Marshal.ReleaseComObject(store);
        }
    }
}
'@
[StudioShortcutIdentity]::SetAppId($path, $edition.appUserModelId)
$verify = $shell.CreateShortcut($path)
if ($verify.TargetPath -ne $exe -or $verify.IconLocation -notlike '*AppIcon.ico*') {
    throw 'Native shortcut verification failed.'
}
$folder = (New-Object -ComObject Shell.Application).NameSpace($desktop)
$identity = $folder.ParseName((Split-Path $path -Leaf)).ExtendedProperty('System.AppUserModel.ID')
if ($identity -ne $edition.appUserModelId) { throw 'Shortcut taskbar identity was not persisted.' }
Write-Host "Native desktop shortcut installed: $path"
