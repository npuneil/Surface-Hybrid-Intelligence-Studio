using System.Text.Json.Serialization;

namespace SurfaceDeviceKit;

/// <summary>What the probe collected. The classifier turns this (and nothing else) into a <see cref="DeviceProfile"/>,
/// so a real PC and a simulated one go through exactly the same code.</summary>
public sealed record RawHardware
{
    public string Manufacturer { get; init; } = "";
    public string Product { get; init; } = "";
    public string Sku { get; init; } = "";
    public string Cpu { get; init; } = "";
    public int LogicalCores { get; init; }
    public string OsVersion { get; init; } = "";
    public int OsBuild { get; init; }
    /// <summary>arm64, x64 or x86: the machine, not the process.</summary>
    public string OsArch { get; init; } = "x64";
    /// <summary>The architecture this process runs as. Empty means the same as <see cref="OsArch"/>; a different value means emulation.</summary>
    public string ProcessArch { get; init; } = "";
    public List<RawGpu> Gpus { get; init; } = [];
    public List<RawNpu> Npus { get; init; } = [];
    /// <summary>Installed memory modules in GB, or 0 when Windows would not say.</summary>
    public double InstalledRamGb { get; init; }
    /// <summary>Memory Windows can hand out, in GB.</summary>
    public double VisibleRamGb { get; init; }
    public string ModelDrive { get; init; } = "";
    public double FreeDiskGb { get; init; }
    public double TotalDiskGb { get; init; }
    public bool NvidiaSmi { get; init; }
    public bool PowerPoint { get; init; }
    public string PowerPointVersion { get; init; } = "";
}

public sealed record RawGpu
{
    public string Name { get; init; } = "";
    public string Driver { get; init; } = "";
    public double VramGb { get; init; }
}

public sealed record RawNpu
{
    public string Name { get; init; } = "";
    public string Driver { get; init; } = "";
    public string Provider { get; init; } = "";
}

public sealed record OsInfo(string Version, int Build, string Arch, string ProcessArch, bool Emulated);

public sealed record CpuInfo(string Name, string Vendor, string FamilyId, string FamilyLabel, string Arch, int LogicalCores, bool UnifiedMemory);

/// <summary>Kind is discrete, unified (shares one memory pool with the CPU but reports it as dedicated) or integrated.</summary>
public sealed record GpuInfo(string Name, string Vendor, string Kind, string FamilyId, double VramGb, string Driver);

public sealed record NpuInfo(string Name, string Vendor, string Driver, double? RatedTops, string RatedSource, bool RuntimeSupported, bool LlmCapable, string Note);

public sealed record MemoryInfo(double InstalledGb, double VisibleGb, int Tier);

public sealed record StorageInfo(string Drive, double FreeGb, double TotalGb);

public sealed record SoftwareInfo(bool NvidiaSmi, bool PowerPoint, string PowerPointVersion);

/// <summary>One place a model can run: npu, gpu or cpu.</summary>
public sealed record LaneInfo(
    string Id, string Kind, bool Available, string Device, string Vendor,
    IReadOnlyList<string> Eps, double BudgetGb, string ClassCap, double? RatedTops, string Note);

/// <summary>Group is lane, workload or platform. Status is available, limited or unavailable.
/// Basis says where the claim comes from: detected (read from this PC), estimated (detected memory against catalog model sizes),
/// vendor-rated (a vendor figure nothing here measures) or catalog (a lookup).</summary>
public sealed record Capability(string Id, string Group, string Title, string Status, string Lane, string Detail, string Basis);

public sealed record DeviceProfile
{
    /// <summary>detected or simulated.</summary>
    public string Source { get; init; } = "detected";
    public string? SimulatedId { get; init; }
    public string Label { get; init; } = "";
    public string Headline { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Product { get; init; } = "";
    public string Sku { get; init; } = "";
    public bool IsSurface { get; init; }
    public OsInfo Os { get; init; } = new("", 0, "", "", false);
    public CpuInfo Cpu { get; init; } = new("", "", "", "", "", 0, false);
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<NpuInfo> Npus { get; init; } = [];
    public MemoryInfo Memory { get; init; } = new(0, 0, 0);
    public StorageInfo Storage { get; init; } = new("", 0, 0);
    public SoftwareInfo Software { get; init; } = new(false, false, "");
    public string PcClass { get; init; } = "windows";
    public string PcClassLabel { get; init; } = "";
    public string DeviceClass { get; init; } = "cpu-only";
    public string DeviceClassLabel { get; init; } = "";
    public IReadOnlyList<LaneInfo> Lanes { get; init; } = [];
    public IReadOnlyList<Capability> Capabilities { get; init; } = [];

    [JsonIgnore] public RawHardware? Raw { get; init; }

    /// <summary>Lanes that can run a model, in the order this device prefers.</summary>
    [JsonIgnore] public IEnumerable<LaneInfo> UsableLanes => Lanes.Where(l => l.Available);

    public LaneInfo? Lane(string id) => Lanes.FirstOrDefault(l => l.Id == id);
}

public sealed record ModelChoice(string Alias, string Title, string Class, double ParamsB, double SizeGb, bool SizeEstimated, double RequiredGb);

public sealed record LanePlan(string Lane, string Kind, double BudgetGb, ModelChoice? Choice, IReadOnlyList<ModelChoice> Ladder, string Reason);

public sealed record WorkloadPlan(string Workload, string Title, string Why, string? PrimaryLane, ModelChoice? Primary, IReadOnlyList<LanePlan> Lanes, string Summary);

/// <summary>The whole answer for one device: who it is, what it can do, and what to run for each workload.</summary>
public sealed record DeviceReport(DeviceProfile Profile, IReadOnlyDictionary<string, WorkloadPlan> Plans, string CatalogVersion);

public sealed record SimulatedDevice(string Id, string Group, string Label, string Note, RawHardware Raw);
