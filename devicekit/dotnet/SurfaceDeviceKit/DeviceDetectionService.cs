using System.Runtime.Versioning;

namespace SurfaceDeviceKit;

/// <summary>The one object a demo talks to. It probes this PC once, can pretend to be any simulated device for display and planning,
/// and turns either into a <see cref="DeviceReport"/>: profile, capability matrix and per-workload model plans.
/// Set SURFACE_DEVICEKIT_SIMULATE to a profile id to start in simulation.</summary>
public sealed class DeviceDetectionService
{
    public const string SimulateEnvironmentVariable = "SURFACE_DEVICEKIT_SIMULATE";

    private readonly Func<RawHardware> _probe;
    private readonly object _gate = new();
    private DeviceReport? _detected;
    private volatile DeviceReport? _simulated;
    private string? _simulatedId;
    private bool _simulatedKeepsLocalFacts = true;

    public DeviceDetectionService(Catalog? catalog = null, Func<RawHardware>? probe = null, string? modelDir = null)
    {
        Catalog = catalog ?? Catalog.Default;
        _probe = probe ?? (() => ProbeThisPc(modelDir));
        string? env = Environment.GetEnvironmentVariable(SimulateEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && SimulatedProfiles.Find(env.Trim()) is not null) Simulate(env.Trim());
    }

    public Catalog Catalog { get; }

    /// <summary>Raised after the simulated device changes or the probe is refreshed.</summary>
    public event EventHandler? Changed;

    /// <summary>What this PC really is. Probed on first use and then cached.</summary>
    public DeviceReport Detected
    {
        get
        {
            lock (_gate) return _detected ??= Analyze(_probe(), Catalog);
        }
    }

    public bool IsSimulated => _simulated is not null;

    /// <summary>The simulated device if one is set, otherwise the detected one. Use this to decide what to show and which models to plan.</summary>
    public DeviceReport Effective => _simulated ?? Detected;

    public IReadOnlyList<SimulatedDevice> SimulatedDevices => SimulatedProfiles.All;

    public DeviceReport Refresh()
    {
        DeviceReport report;
        lock (_gate)
        {
            _detected = null;
            report = Detected;
            // An active simulation takes the new local facts (disk space, PowerPoint, nvidia-smi) too.
            if (_simulatedId is not null) _simulated = BuildSimulation(_simulatedId, _simulatedKeepsLocalFacts);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return report;
    }

    /// <param name="keepLocalFacts">Simulation covers the AI hardware only. Free disk space, PowerPoint and nvidia-smi still come from this PC, because that is what will really happen.</param>
    public DeviceReport Simulate(string id, bool keepLocalFacts = true)
    {
        DeviceReport report;
        lock (_gate)
        {
            report = BuildSimulation(id, keepLocalFacts);
            _simulated = report;
            _simulatedId = id;
            _simulatedKeepsLocalFacts = keepLocalFacts;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return report;
    }

    private DeviceReport BuildSimulation(string id, bool keepLocalFacts)
    {
        var sim = SimulatedProfiles.Find(id) ?? throw new KeyNotFoundException($"No simulated device '{id}'.");
        var raw = sim.Raw;
        if (keepLocalFacts && LocalRaw() is { } local)
            raw = raw with
            {
                ModelDrive = local.ModelDrive, FreeDiskGb = local.FreeDiskGb, TotalDiskGb = local.TotalDiskGb,
                NvidiaSmi = local.NvidiaSmi, PowerPoint = local.PowerPoint, PowerPointVersion = local.PowerPointVersion,
            };
        return Analyze(raw, Catalog, "simulated", sim.Id, sim.Label);
    }

    private RawHardware? LocalRaw()
    {
        try { return Detected.Profile.Raw; }
        catch { return null; }
    }

    public void ClearSimulation()
    {
        lock (_gate)
        {
            _simulated = null;
            _simulatedId = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public WorkloadPlan Plan(string workload) => Effective.Plans[workload];

    /// <summary>Classify raw hardware, then build the capability matrix and the plan for every workload.</summary>
    public static DeviceReport Analyze(RawHardware raw, Catalog? catalog = null, string source = "detected", string? simulatedId = null, string? label = null)
    {
        catalog ??= Catalog.Default;
        var profile = Classifier.Classify(raw, catalog, source, simulatedId, label);
        var plans = Planner.PlanAll(profile, catalog);
        profile = profile with { Capabilities = CapabilityMatrix.Build(profile, plans, catalog) };
        return new DeviceReport(profile, plans, catalog.CatalogVersion);
    }

    [SupportedOSPlatform("windows")]
    private static RawHardware ProbeCore(string? modelDir) => HardwareProbe.Probe(modelDir);

    private static RawHardware ProbeThisPc(string? modelDir)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The hardware probe needs Windows. Pass a probe delegate or use a simulated device.");
        return ProbeCore(modelDir);
    }
}

/// <summary>Short entry points for callers that only want the answer.</summary>
public static class DeviceKit
{
    private static readonly Lazy<DeviceDetectionService> Shared = new(() => new DeviceDetectionService());

    /// <summary>A process-wide service, created on first use.</summary>
    public static DeviceDetectionService Service => Shared.Value;
}
