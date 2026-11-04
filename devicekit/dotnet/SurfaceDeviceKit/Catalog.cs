using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SurfaceDeviceKit;

public sealed class Thresholds
{
    public double NpuMinTopsForLlm { get; set; } = 40;
    public double CopilotPlusMinRamGb { get; set; } = 16;
    public double CopilotPlusMinTops { get; set; } = 40;
    public double GpuMinBudgetGb { get; set; } = 2.0;
    public double ModelDriveMinFreeGb { get; set; } = 12;
}

public sealed class BudgetOverhead
{
    public double Multiplier { get; set; } = 1.15;
    public double ConstantGb { get; set; } = 0.5;
}

public sealed class BudgetRule
{
    /// <summary>vram or ram.</summary>
    public string Basis { get; set; } = "ram";
    public double Fraction { get; set; } = 1.0;
    public double ReserveGb { get; set; }
}

public sealed class BudgetSection
{
    public string Note { get; set; } = "";
    public BudgetOverhead Overhead { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement> Rules { get; set; } = [];

    public BudgetRule Rule(string kind) =>
        Rules.TryGetValue(kind, out var e) ? e.Deserialize<BudgetRule>(Catalog.ReadOptions)! : new BudgetRule();
}

public sealed class ModelClassSection
{
    public List<string> Order { get; set; } = [];
    public Dictionary<string, string> Describe { get; set; } = [];
    public Dictionary<string, string> LaneCap { get; set; } = [];
    public int Rank(string cls) => Math.Max(0, Order.IndexOf(cls));
}

public sealed class LaneSpec
{
    public string Label { get; set; } = "";
    public string DeviceType { get; set; } = "";
}

public sealed class EpSection
{
    public string Note { get; set; } = "";
    public Dictionary<string, List<string>> Npu { get; set; } = [];
    public Dictionary<string, List<string>> Gpu { get; set; } = [];
    public Dictionary<string, List<string>> Cpu { get; set; } = [];
}

public sealed class SurfaceSection
{
    public string ProductPattern { get; set; } = "^Surface";
    public List<string> StripPrefixes { get; set; } = [];
    public List<string> StripSuffixes { get; set; } = [];
}

public sealed class SiliconNpu
{
    public string Vendor { get; set; } = "";
    public double? RatedTops { get; set; }
    public bool Runtime { get; set; }
    public string? Note { get; set; }
}

public sealed class SiliconSpec
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Arch { get; set; } = "";
    public string CpuMatch { get; set; } = "";
    public bool UnifiedMemory { get; set; }
    /// <summary>yes, config (depends on the SKU), listed (Microsoft lists it), or no.</summary>
    public string CopilotPlus { get; set; } = "no";
    /// <summary>Where the "listed" claim comes from, shown next to the capability.</summary>
    public string? CopilotPlusNote { get; set; }
    /// <summary>"listed" when Microsoft names this silicon in its Builder PC class. The kit does not infer the class from specs.</summary>
    public string? BuilderPc { get; set; }
    public SiliconNpu? Npu { get; set; }
    [JsonIgnore] public Regex Pattern { get; set; } = null!;
}

public sealed class GpuFamilySpec
{
    public string Id { get; set; } = "";
    public string Match { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Kind { get; set; } = "integrated";
    [JsonIgnore] public Regex Pattern { get; set; } = null!;
}

public sealed class ModelSizes
{
    public double? Gpu { get; set; }
    public double? Cpu { get; set; }
}

public sealed class ModelSpec
{
    public string Alias { get; set; } = "";
    public string Title { get; set; } = "";
    public double Params { get; set; }
    public string Class { get; set; } = "S";
    public string Task { get; set; } = "chat";
    public bool Thinks { get; set; }
    public bool Tools { get; set; }
    public ModelSizes SizeMb { get; set; } = new();

    /// <summary>Weight file size for a lane. NPU packages are not in the static catalog, so the GPU size stands in and the result is flagged as an estimate.</summary>
    public (double Gb, bool Estimated)? SizeFor(string laneId)
    {
        double? mb = laneId == "cpu" ? SizeMb.Cpu ?? SizeMb.Gpu : SizeMb.Gpu ?? SizeMb.Cpu;
        return mb is null ? null : (mb.Value / 1024.0, laneId == "npu");
    }
}

public sealed class WorkloadSpec
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Task { get; set; } = "chat";
    public string Why { get; set; } = "";
    /// <summary>Weakest candidate that still counts as full capability. Anything after it in the list is reported as limited.</summary>
    public string AvailableFrom { get; set; } = "";
    public List<string> Candidates { get; set; } = [];
}

/// <summary>The data-only knowledge file (catalog/device-catalog.json): silicon families, lane budgets, EP preferences and model preferences per workload.</summary>
public sealed class Catalog
{
    internal static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
    private const string ResourceName = "SurfaceDeviceKit.device-catalog.json";

    public int Schema { get; set; }
    public string CatalogVersion { get; set; } = "";
    public string About { get; set; } = "";
    public List<int> RamTiers { get; set; } = [];
    public double TierTolerance { get; set; } = 0.08;
    public Thresholds Thresholds { get; set; } = new();
    public BudgetSection Budget { get; set; } = new();
    public ModelClassSection ModelClasses { get; set; } = new();
    public Dictionary<string, LaneSpec> Lanes { get; set; } = [];
    public Dictionary<string, List<string>> LaneOrder { get; set; } = [];
    public Dictionary<string, string> DeviceClassLabels { get; set; } = [];
    public Dictionary<string, string> PcClassLabels { get; set; } = [];
    public EpSection Eps { get; set; } = new();
    public SurfaceSection Surface { get; set; } = new();
    public List<SiliconSpec> Silicon { get; set; } = [];
    public List<GpuFamilySpec> GpuFamilies { get; set; } = [];
    public Dictionary<string, ModelSpec> Models { get; set; } = [];
    public Dictionary<string, WorkloadSpec> Workloads { get; set; } = [];

    private static Catalog? _default;

    /// <summary>The catalog embedded in the library, or the file named by SURFACE_DEVICEKIT_CATALOG.</summary>
    public static Catalog Default => _default ??= LoadDefault();

    private static Catalog LoadDefault()
    {
        string? path = Environment.GetEnvironmentVariable("SURFACE_DEVICEKIT_CATALOG");
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"SURFACE_DEVICEKIT_CATALOG points at a file that does not exist: {path}", path);
            return Parse(File.ReadAllText(path));
        }
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static Catalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(json, ReadOptions) ?? throw new InvalidDataException("The device catalog is empty.");
        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        foreach (var s in catalog.Silicon) s.Pattern = new Regex(s.CpuMatch, Options);
        foreach (var g in catalog.GpuFamilies) g.Pattern = new Regex(g.Match, Options);
        foreach (var (alias, model) in catalog.Models) model.Alias = alias;
        foreach (var (id, workload) in catalog.Workloads) workload.Id = id;
        return catalog;
    }

    internal static string ReadEmbedded(string name)
    {
        using var stream = typeof(Catalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public SiliconSpec? MatchSilicon(string cpuName) => Silicon.FirstOrDefault(s => s.Pattern.IsMatch(cpuName));

    public GpuFamilySpec? MatchGpu(string name) => GpuFamilies.FirstOrDefault(g => g.Pattern.IsMatch(name));

    /// <summary>Largest standard RAM size that the installed amount reaches (within the rounding tolerance).</summary>
    public int RamTier(double gb)
    {
        int tier = 0;
        foreach (int t in RamTiers.Order())
            if (t <= gb * (1 + TierTolerance)) tier = t;
        return tier;
    }

    public double LoadCost(double sizeGb) => sizeGb * Budget.Overhead.Multiplier + Budget.Overhead.ConstantGb;
}
