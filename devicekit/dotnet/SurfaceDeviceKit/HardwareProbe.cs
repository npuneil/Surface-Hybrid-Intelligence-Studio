using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SurfaceDeviceKit;

/// <summary>Reads the hardware facts from Windows: registry, SetupAPI (present devices only) and a few Win32 calls.
/// No WMI, no admin rights, no network. The only process it may start is nvidia-smi, and only when the registry has no video memory figure.</summary>
[SupportedOSPlatform("windows")]
public static class HardwareProbe
{
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly Guid SystemClass = new("4d36e97d-e325-11ce-bfc1-08002be10318");
    private static readonly Guid ComputeAcceleratorClass = new("f01a9d53-3ff6-48d2-9f97-c8a7004be10c");
    // "Neural" is not used: Intel's GNA (Gaussian & Neural Accelerator) is a low-power audio block, not an NPU.
    private static readonly Regex NpuName = new(@"\bNPU\b|AI Boost|Hexagon|\bIPU\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StaleAdapter = new(@"Basic (Render|Display)|Remote Display|Hyper-V|Virtual|Indirect Display", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const double GiB = 1024.0 * 1024 * 1024;

    /// <param name="modelDir">Where models will be stored. Only its drive matters (for the free-space check).</param>
    public static RawHardware Probe(string? modelDir = null)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        using var bios = hklm.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var cpuKey = hklm.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

        var gpus = new List<RawGpu>();
        var npus = new List<RawNpu>();
        foreach (var d in PresentDevices(DisplayClass, hklm))
        {
            if (NpuName.IsMatch(d.Name)) npus.Add(new RawNpu { Name = d.Name, Driver = d.Driver, Provider = d.Provider });
            else if (!StaleAdapter.IsMatch(d.Name)) gpus.Add(new RawGpu { Name = d.Name, Driver = d.Driver, VramGb = Math.Round(d.VramBytes / GiB, 2) });
        }
        foreach (var d in PresentDevices(ComputeAcceleratorClass, hklm))
            if (!npus.Any(n => n.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)))
                npus.Add(new RawNpu { Name = d.Name, Driver = d.Driver, Provider = d.Provider });
        // Before Windows 11 24H2 an NPU is listed under System devices instead.
        foreach (var d in PresentDevices(SystemClass, hklm))
            if (NpuName.IsMatch(d.Name) && !npus.Any(n => n.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)))
                npus.Add(new RawNpu { Name = d.Name, Driver = d.Driver, Provider = d.Provider });

        string? smi = FindNvidiaSmi();
        if (smi is not null && gpus.Any(NeedsVramFromSmi))
        {
            // nvidia-smi lists NVIDIA GPUs only, so the n-th NVIDIA adapter here is its n-th line.
            var totals = NvidiaSmiTotalsGb(smi);
            int nth = 0;
            for (int i = 0; i < gpus.Count; i++)
            {
                if (!IsNvidia(gpus[i])) continue;
                if (gpus[i].VramGb <= 0 && nth < totals.Count && totals[nth] is { } gb) gpus[i] = gpus[i] with { VramGb = gb };
                nth++;
            }
        }

        modelDir ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var (free, total, drive) = Disk(modelDir);
        var (ppt, pptVersion) = PowerPoint(hklm);
        var (osVersion, osBuild) = Windows(hklm);

        return new RawHardware
        {
            Manufacturer = Classifier.Clean(Str(bios, "SystemManufacturer")),
            Product = Classifier.Clean(Str(bios, "SystemProductName")),
            Sku = Classifier.Clean(Str(bios, "SystemSKU")),
            Cpu = Classifier.Clean(Str(cpuKey, "ProcessorNameString")),
            LogicalCores = Environment.ProcessorCount,
            OsVersion = osVersion,
            OsBuild = osBuild,
            OsArch = ArchName(RuntimeInformation.OSArchitecture),
            ProcessArch = ArchName(RuntimeInformation.ProcessArchitecture),
            Gpus = gpus,
            Npus = npus,
            InstalledRamGb = InstalledRamGb(),
            VisibleRamGb = VisibleRamGb(),
            ModelDrive = drive,
            FreeDiskGb = free,
            TotalDiskGb = total,
            NvidiaSmi = smi is not null,
            PowerPoint = ppt,
            PowerPointVersion = pptVersion,
        };
    }

    private static string ArchName(Architecture a) => a switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        _ => a.ToString().ToLowerInvariant(),
    };

    private static string Str(RegistryKey? key, string name) => key?.GetValue(name)?.ToString() ?? "";

    private static (string Version, int Build) Windows(RegistryKey hklm)
    {
        using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        _ = int.TryParse(Str(key, "CurrentBuildNumber"), out int build);
        string display = Str(key, "DisplayVersion");
        string ubr = Str(key, "UBR");
        // ProductName still says "Windows 10" on Windows 11, so take only the edition from it.
        string edition = Regex.Replace(Str(key, "ProductName"), @"^Windows\s+1[01]\s*", "").Trim();
        string name = build >= 22000 ? "Windows 11" : "Windows 10";
        string text = $"{name} {edition} {display}".Replace("  ", " ").Trim();
        return ($"{text} (build {build}{(ubr.Length > 0 ? "." + ubr : "")})", build);
    }

    private static (double FreeGb, double TotalGb, string Drive) Disk(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            var info = new DriveInfo(root);
            return (Math.Round(info.AvailableFreeSpace / GiB, 2), Math.Round(info.TotalSize / GiB, 2), root.TrimEnd('\\'));
        }
        catch { return (0, 0, ""); }
    }

    private static (bool Installed, string Version) PowerPoint(RegistryKey hklm)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var key = classes.OpenSubKey(@"PowerPoint.Application\CurVer");
            if (key is null) continue;
            using var c2r = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            string version = Str(c2r, "VersionToReport");
            if (version.Length == 0) version = Regex.Match(Str(key, ""), @"\d+$").Value;
            return (true, version);
        }
        return (false, "");
    }

    private static string? FindNvidiaSmi()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim().Trim('"'), "nvidia-smi.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* a malformed PATH entry is not an error */ }
        }
        foreach (string candidate in new[] { Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static bool IsNvidia(RawGpu g) => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsVramFromSmi(RawGpu g) => g.VramGb <= 0 && IsNvidia(g);

    /// <summary>Video memory in GB for each NVIDIA GPU, in the order nvidia-smi prints them (null for a line that is not a number).</summary>
    private static List<double?> NvidiaSmiTotalsGb(string smi)
    {
        var totals = new List<double?>();
        try
        {
            using var p = Process.Start(new ProcessStartInfo(smi, "--query-gpu=memory.total --format=csv,noheader,nounits")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (p is null) return totals;
            // Read in the background so that a hung nvidia-smi is still cut off after four seconds.
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(4000)) { p.Kill(); return totals; }
            foreach (string line in output.GetAwaiter().GetResult().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                totals.Add(double.TryParse(line, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mib)
                    ? Math.Round(mib / 1024.0, 2) : null);
        }
        catch { totals.Clear(); }
        return totals;
    }

    private sealed record PnpEntry(string Name, string Driver, string Provider, long VramBytes);

    /// <summary>Devices of one class that are present right now, so adapters left behind in the registry by old drivers are ignored.</summary>
    private static List<PnpEntry> PresentDevices(Guid classGuid, RegistryKey hklm)
    {
        var result = new List<PnpEntry>();
        IntPtr set = SetupDiGetClassDevsW(ref classGuid, null, IntPtr.Zero, DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE) return result;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                string driverKey = Property(set, ref data, SPDRP_DRIVER);
                string description = Property(set, ref data, SPDRP_DEVICEDESC);
                string name = description, driver = "", provider = "";
                long vram = 0;

                if (driverKey.Length > 0)
                {
                    using var key = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
                    if (key is not null)
                    {
                        string desc = Str(key, "DriverDesc");
                        if (desc.Length > 0) name = desc;
                        driver = Str(key, "DriverVersion");
                        provider = Str(key, "ProviderName");
                        vram = key.GetValue("HardwareInformation.qwMemorySize") switch
                        {
                            long q => q,
                            int d => (uint)d,
                            byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                            byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                            _ => 0,
                        };
                        if (vram == 0 && key.GetValue("HardwareInformation.MemorySize") is { } legacy)
                            vram = legacy switch { int d => (uint)d, long q => q, byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), _ => 0 };
                    }
                }
                name = Classifier.Clean(name);
                if (name.Length > 0) result.Add(new PnpEntry(name, driver, provider, vram));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static string Property(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out uint needed);
        if (needed == 0 || needed > 4096) return "";
        var buffer = new byte[needed];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, needed, out _)) return "";
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static double InstalledRamGb()
    {
        try { return GetPhysicallyInstalledSystemMemory(out ulong kb) ? Math.Round(kb / 1024.0 / 1024.0, 2) : 0; }
        catch { return 0; }
    }

    private static double VisibleRamGb()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? Math.Round(status.ullTotalPhys / GiB, 2) : 0;
    }

    private const uint DIGCF_PRESENT = 0x2;
    private const uint SPDRP_DEVICEDESC = 0x0;
    private const uint SPDRP_DRIVER = 0x9;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property,
        out uint propertyRegDataType, byte[]? propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
