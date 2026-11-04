using System.Text.RegularExpressions;

namespace SurfaceDeviceKit;

/// <summary>Turns raw hardware facts into a <see cref="DeviceProfile"/>. A probed PC and a simulated one take exactly this path,
/// and the Python port follows the same steps, so the two can be checked against each other with golden vectors.</summary>
public static class Classifier
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex VirtualAdapter = new(
        @"Basic (Render|Display)|Remote Display|Hyper-V|Virtual|VMware|Parallels|Citrix|Indirect Display|DisplayLink|Mirage", Opt);

    internal static double R(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    internal static string Clean(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    /// <summary>nvidia, qualcomm, intel, amd or other, judged from a device name.</summary>
    public static string VendorOf(string name)
    {
        if (Regex.IsMatch(name, "nvidia", Opt)) return "nvidia";
        if (Regex.IsMatch(name, "qualcomm|snapdragon|adreno|hexagon|microsoft sq", Opt)) return "qualcomm";
        if (Regex.IsMatch(name, "intel", Opt)) return "intel";
        if (Regex.IsMatch(name, "amd|ryzen|radeon", Opt)) return "amd";
        return "other";
    }

    /// <summary>Gigabytes a lane can give model weights. Budget = min(basis * fraction, basis - reserve).</summary>
    public static double Budget(Catalog catalog, string laneKind, double vramGb, double visibleRamGb)
    {
        var rule = catalog.Budget.Rule(laneKind);
        double basis = rule.Basis == "vram" ? vramGb : visibleRamGb;
        return R(Math.Max(0, Math.Min(basis * rule.Fraction, basis - rule.ReserveGb)));
    }

    public static string FriendlyProduct(string product, Catalog catalog)
    {
        string p = Clean(product);
        foreach (var prefix in catalog.Surface.StripPrefixes)
            if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) p = p[prefix.Length..];
        foreach (var suffix in catalog.Surface.StripSuffixes)
            if (p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) p = p[..^suffix.Length];
        return p.Trim();
    }

    public static DeviceProfile Classify(RawHardware raw, Catalog catalog, string source = "detected", string? simulatedId = null, string? label = null)
    {
        var th = catalog.Thresholds;
        var silicon = catalog.MatchSilicon(raw.Cpu);
        bool emulated = raw.ProcessArch.Length > 0 && !string.Equals(raw.OsArch, raw.ProcessArch, StringComparison.OrdinalIgnoreCase);
        double installed = raw.InstalledRamGb > 0 ? raw.InstalledRamGb : raw.VisibleRamGb;
        double visible = raw.VisibleRamGb > 0 ? raw.VisibleRamGb : raw.InstalledRamGb;
        int tier = catalog.RamTier(installed);

        string cpuName = Clean(raw.Cpu);
        var cpu = new CpuInfo(cpuName, silicon?.Vendor ?? VendorOf(cpuName), silicon?.Id ?? "unknown",
            silicon?.Label ?? cpuName, silicon?.Arch ?? raw.OsArch.ToLowerInvariant(), raw.LogicalCores, silicon?.UnifiedMemory ?? false);

        var gpus = raw.Gpus
            .Where(g => !string.IsNullOrWhiteSpace(g.Name) && !VirtualAdapter.IsMatch(g.Name))
            .Select(g => BuildGpu(g, catalog)).ToList();
        var npus = raw.Npus
            .Where(n => !string.IsNullOrWhiteSpace(n.Name))
            .Select(n => BuildNpu(n, silicon, catalog, emulated)).ToList();

        var primary = ChoosePrimaryGpu(gpus);
        var gpuLane = BuildGpuLane(primary, catalog, visible, emulated);
        var npuLane = BuildNpuLane(npus, silicon, catalog, visible);
        var cpuLane = BuildCpuLane(cpu, catalog, visible, emulated);

        bool strongNvidia = primary is { Vendor: "nvidia", Kind: "discrete" or "unified" } && gpuLane.Available;
        string deviceClass = strongNvidia ? "gpu-first"
            : npuLane.Available ? "npu-first"
            : gpuLane.Available ? "igpu-first"
            : "cpu-only";

        bool copilotSilicon = silicon is not null && silicon.CopilotPlus != "no";
        bool copilotNpu = silicon?.CopilotPlus == "listed" || npus.Any(n => n.RatedTops >= th.CopilotPlusMinTops);
        string pcClass =
            silicon?.BuilderPc == "listed" ? "builder"
            : copilotSilicon && copilotNpu && tier >= th.CopilotPlusMinRamGb ? "copilot-plus"
            : strongNvidia ? "gpu"
            : npus.Any(n => n.RatedTops is > 0) ? "npu"
            : "windows";

        var byId = new Dictionary<string, LaneInfo> { ["npu"] = npuLane, ["gpu"] = gpuLane, ["cpu"] = cpuLane };
        var order = catalog.LaneOrder[deviceClass];
        var lanes = order.Select(id => byId[id]).Concat(byId.Values.Where(l => !order.Contains(l.Id))).ToList();

        string product = FriendlyProduct(raw.Product, catalog);
        bool isSurface = Regex.IsMatch(product, catalog.Surface.ProductPattern, Opt);
        string name = label ?? (product.Length > 0 ? product : "Windows PC");
        string ram = tier > 0 ? $"{tier} GB" : $"{R(installed):0.#} GB";

        return new DeviceProfile
        {
            Source = source,
            SimulatedId = simulatedId,
            Label = name,
            Headline = label ?? $"{name} · {cpu.FamilyLabel} · {ram}", // a caller's label (the simulated devices) already names the silicon and memory
            Manufacturer = Clean(raw.Manufacturer),
            Product = product,
            Sku = Clean(raw.Sku),
            IsSurface = isSurface,
            Os = new OsInfo(raw.OsVersion, raw.OsBuild, raw.OsArch.ToLowerInvariant(),
                (raw.ProcessArch.Length > 0 ? raw.ProcessArch : raw.OsArch).ToLowerInvariant(), emulated),
            Cpu = cpu,
            Gpus = gpus,
            Npus = npus,
            Memory = new MemoryInfo(R(installed), R(visible), tier),
            Storage = new StorageInfo(raw.ModelDrive, R(raw.FreeDiskGb), R(raw.TotalDiskGb)),
            Software = new SoftwareInfo(raw.NvidiaSmi, raw.PowerPoint, raw.PowerPointVersion),
            PcClass = pcClass,
            PcClassLabel = catalog.PcClassLabels[pcClass],
            DeviceClass = deviceClass,
            DeviceClassLabel = catalog.DeviceClassLabels[deviceClass],
            Lanes = lanes,
            Raw = raw,
        };
    }

    private static GpuInfo BuildGpu(RawGpu g, Catalog catalog)
    {
        var family = catalog.MatchGpu(g.Name);
        string name = Clean(g.Name);
        return new GpuInfo(name, family?.Vendor ?? VendorOf(name), family?.Kind ?? "integrated", family?.Id ?? "unknown", R(g.VramGb), g.Driver);
    }

    private static NpuInfo BuildNpu(RawNpu n, SiliconSpec? silicon, Catalog catalog, bool emulated)
    {
        string name = Clean(n.Name);
        string vendor = VendorOf(name);
        var spec = silicon?.Npu;
        if (spec is not null && vendor == "other") vendor = spec.Vendor;
        if (spec is not null && spec.Vendor != vendor) spec = null;

        double? tops = spec?.RatedTops;
        bool runtime = spec?.Runtime ?? false;
        double min = catalog.Thresholds.NpuMinTopsForLlm;
        bool llm = tops is not null && tops >= min && runtime && catalog.Eps.Npu.ContainsKey(vendor) && !emulated;

        string note;
        if (spec is null) note = "No rating on file for this NPU.";
        else if (!string.IsNullOrEmpty(spec.Note)) note = spec.Note!;
        else if (tops is null) note = "No rated TOPS on file for this NPU.";
        else if (emulated) note = "An x64 process on an ARM64 PC cannot load the NPU runtime. Use the ARM64 build.";
        else if (tops < min) note = $"Rated {tops:0} TOPS (vendor figure): enough for light background AI, below the {min:0} TOPS this kit needs for language models.";
        else note = $"Rated {tops:0} TOPS (vendor figure).";

        return new NpuInfo(name, vendor, n.Driver, tops, tops is null ? "none" : "vendor-rated", runtime, llm, note);
    }

    private static GpuInfo? ChoosePrimaryGpu(List<GpuInfo> gpus) =>
        gpus.OrderByDescending(g => g.Vendor == "nvidia" && g.Kind != "integrated")
            .ThenByDescending(g => g.Kind != "integrated")
            .ThenByDescending(g => g.VramGb)
            .FirstOrDefault();

    private static LaneInfo BuildGpuLane(GpuInfo? gpu, Catalog catalog, double visible, bool emulated)
    {
        var caps = catalog.ModelClasses.LaneCap;
        if (gpu is null)
            return new LaneInfo("gpu", "gpu-integrated", false, "", "", [], 0, caps["gpu-integrated"], null, "Windows did not report a GPU.");

        string kind = "gpu-" + gpu.Kind;
        double budget = Budget(catalog, kind, gpu.VramGb, visible);
        var eps = catalog.Eps.Gpu.TryGetValue(gpu.Vendor, out var list) ? list : catalog.Eps.Gpu["other"];
        bool ok = !emulated && budget >= catalog.Thresholds.GpuMinBudgetGb;

        string note;
        if (emulated) note = "An x64 process on an ARM64 PC cannot load GPU runtimes. Use the ARM64 build.";
        else if (!ok) note = $"Only {budget:0.0} GB of graphics memory is usable for models.";
        else note = gpu.Kind switch
        {
            "unified" => $"Unified memory: the GPU and CPU share one pool, and the GPU reports {gpu.VramGb:0.0} GB of it.",
            "discrete" => $"Dedicated video memory: {gpu.VramGb:0.0} GB.",
            _ => "Integrated graphics use system memory, so the budget is a share of RAM.",
        };
        return new LaneInfo("gpu", kind, ok, gpu.Name, gpu.Vendor, eps, ok ? budget : 0, caps[kind], null, note);
    }

    private static LaneInfo BuildNpuLane(List<NpuInfo> npus, SiliconSpec? silicon, Catalog catalog, double visible)
    {
        var capable = npus.FirstOrDefault(n => n.LlmCapable);
        var shown = capable ?? npus.FirstOrDefault();
        string note = shown is not null ? shown.Note
            : silicon?.Npu is { Runtime: true } ? "Windows did not report an NPU for this chip. Install the NPU driver from Windows Update or the Surface driver pack."
            : "This PC has no NPU.";
        var eps = shown is not null && catalog.Eps.Npu.TryGetValue(shown.Vendor, out var list) ? list : [];
        double budget = capable is not null ? Budget(catalog, "npu", 0, visible) : 0;
        return new LaneInfo("npu", "npu", capable is not null, shown?.Name ?? "", shown?.Vendor ?? "", eps, budget,
            catalog.ModelClasses.LaneCap["npu"], shown?.RatedTops, note);
    }

    private static LaneInfo BuildCpuLane(CpuInfo cpu, Catalog catalog, double visible, bool emulated) =>
        new("cpu", "cpu", true, cpu.Name, cpu.Vendor, catalog.Eps.Cpu["any"], Budget(catalog, "cpu", 0, visible),
            catalog.ModelClasses.LaneCap["cpu"], null,
            emulated ? "Runs under x64 emulation on ARM64, which adds overhead. The ARM64 build is faster." : "Runs on any PC; the slowest lane.");
}
