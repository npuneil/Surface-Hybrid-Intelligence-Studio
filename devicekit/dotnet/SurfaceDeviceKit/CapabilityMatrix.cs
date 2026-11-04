namespace SurfaceDeviceKit;

/// <summary>What this device can do, in plain words. Every entry says where it comes from (see <see cref="Capability"/>),
/// so a demo never has to claim more than was detected, estimated or vendor-rated.</summary>
public static class CapabilityMatrix
{
    public const string Available = "available";
    public const string Limited = "limited";
    public const string Unavailable = "unavailable";

    public static IReadOnlyList<Capability> Build(DeviceProfile p, IReadOnlyDictionary<string, WorkloadPlan> plans, Catalog catalog)
    {
        var list = new List<Capability>();
        var npu = p.Lane("npu")!;
        var gpu = p.Lane("gpu")!;
        var cpu = p.Lane("cpu")!;

        list.Add(NpuCapability(p, npu, catalog));
        list.Add(GpuCapability(p, gpu));
        list.Add(new Capability("cpu-inference", "lane", "CPU language models", Available, "cpu",
            $"{cpu.Device}: {cpu.BudgetGb:0.0} GB of RAM available for model weights. {cpu.Note}", "estimated"));

        foreach (var (id, workload) in catalog.Workloads)
            list.Add(WorkloadCapability(plans[id], workload));
        list.Add(LargeModels(plans["chat"], p, catalog));

        list.Add(CopilotPlus(p, catalog));
        list.Add(Storage(p, catalog));
        list.Add(NativeBuild(p));
        list.Add(PowerPoint(p));
        return list;
    }

    private static Capability NpuCapability(DeviceProfile p, LaneInfo npu, Catalog catalog)
    {
        var shown = p.Npus.FirstOrDefault(n => n.LlmCapable) ?? p.Npus.FirstOrDefault();
        if (npu.Available && shown is not null)
            return new Capability("npu-inference", "lane", "NPU language models", Available, "npu",
                $"{shown.Name}. {shown.Note} Runtime: {string.Join(" or ", npu.Eps).ToUpperInvariant()} through Foundry Local.", "vendor-rated");
        if (shown is null)
            return new Capability("npu-inference", "lane", "NPU language models", Unavailable, "", npu.Note, "detected");
        string status = shown.RuntimeSupported && shown.RatedTops is not null ? Limited : Unavailable;
        return new Capability("npu-inference", "lane", "NPU language models", status, "", $"{shown.Name}. {shown.Note}",
            shown.RatedTops is null ? "detected" : "vendor-rated");
    }

    private static Capability GpuCapability(DeviceProfile p, LaneInfo gpu)
    {
        if (!gpu.Available)
            return new Capability("gpu-inference", "lane", "GPU language models", Unavailable, "", gpu.Note, "detected");
        bool strong = gpu.Kind != "gpu-integrated";
        return new Capability("gpu-inference", "lane", "GPU language models", strong ? Available : Limited, "gpu",
            $"{gpu.Device}: {gpu.BudgetGb:0.0} GB for model weights, up to {gpu.ClassCap}-class models. {gpu.Note}", strong ? "detected" : "estimated");
    }

    private static Capability WorkloadCapability(WorkloadPlan plan, WorkloadSpec workload)
    {
        if (plan.Primary is not { } pick || plan.PrimaryLane is null)
            return new Capability(workload.Id, "workload", workload.Title, Unavailable, "", plan.Summary, "estimated");
        int have = workload.Candidates.IndexOf(pick.Alias);
        int need = workload.Candidates.IndexOf(workload.AvailableFrom);
        string status = need < 0 || have <= need ? Available : Limited;
        return new Capability(workload.Id, "workload", workload.Title, status, plan.PrimaryLane, plan.Summary, "estimated");
    }

    private static Capability LargeModels(WorkloadPlan chat, DeviceProfile p, Catalog catalog)
    {
        int large = catalog.ModelClasses.Rank("L");
        var fit = chat.Lanes.Where(l => l.Choice is not null)
            .Select(l => (Lane: l, Pick: l.Ladder.FirstOrDefault(m => catalog.ModelClasses.Rank(m.Class) >= large)))
            .FirstOrDefault(x => x.Pick is not null);
        if (fit.Pick is { } pick)
            return new Capability("large-models", "workload", "Large models (12B and up)", Available, fit.Lane.Lane,
                $"{pick.Title} fits on the {catalog.Lanes[fit.Lane.Lane].Label}: it needs about {pick.RequiredGb:0.0} GB and the lane has {fit.Lane.BudgetGb:0.0} GB.", "estimated");

        double need = catalog.Workloads["chat"].Candidates
            .Where(catalog.Models.ContainsKey).Select(a => catalog.Models[a]).Where(m => catalog.ModelClasses.Rank(m.Class) >= large)
            .Select(m => m.SizeFor("gpu")).Where(s => s is not null).Select(s => catalog.LoadCost(s!.Value.Gb)).DefaultIfEmpty(0).Min();
        double best = p.Lanes.Where(l => l.Available).Select(l => l.BudgetGb).DefaultIfEmpty(0).Max();
        return new Capability("large-models", "workload", "Large models (12B and up)", Unavailable, "",
            $"A 12B-class model needs about {need:0.0} GB on a GPU. The best lane here has {best:0.0} GB, so the plan stays with smaller models.", "estimated");
    }

    private static Capability CopilotPlus(DeviceProfile p, Catalog catalog)
    {
        var th = catalog.Thresholds;
        var silicon = catalog.Silicon.FirstOrDefault(s => s.Id == p.Cpu.FamilyId);
        double topsMin = th.CopilotPlusMinTops;
        double best = p.Npus.Select(n => n.RatedTops ?? 0).DefaultIfEmpty(0).Max();

        if (silicon is null || silicon.CopilotPlus == "no")
            return new Capability("copilot-plus", "platform", "Copilot+ PC class", Unavailable, "",
                $"{p.Cpu.FamilyLabel} is not a Copilot+ PC class chip.", "catalog");
        if (p.Memory.Tier < th.CopilotPlusMinRamGb)
            return new Capability("copilot-plus", "platform", "Copilot+ PC class", Unavailable, "",
                $"Copilot+ PCs need at least {th.CopilotPlusMinRamGb:0} GB of RAM; this one has {p.Memory.Tier} GB.", "detected");
        if (silicon.CopilotPlus == "listed")
            return new Capability("copilot-plus", "platform", "Copilot+ PC class", Available, "",
                $"{silicon.CopilotPlusNote ?? $"Microsoft lists {p.Cpu.FamilyLabel} devices as Copilot+ PCs."} Confirm on the exact SKU.", "catalog");
        if (best < topsMin)
            return new Capability("copilot-plus", "platform", "Copilot+ PC class", Unavailable, "",
                $"Copilot+ PCs need an NPU rated at {topsMin:0} TOPS or more; none was found here.", "vendor-rated");
        return new Capability("copilot-plus", "platform", "Copilot+ PC class", Available, "",
            $"{p.Cpu.FamilyLabel} with an NPU rated {best:0} TOPS and {p.Memory.Tier} GB of RAM. Confirm on the exact SKU.", "vendor-rated");
    }

    private static Capability Storage(DeviceProfile p, Catalog catalog)
    {
        double need = catalog.Thresholds.ModelDriveMinFreeGb;
        double free = p.Storage.FreeGb;
        string status = free >= need ? Available : free >= 4 ? Limited : Unavailable;
        string drive = string.IsNullOrEmpty(p.Storage.Drive) ? "the model drive" : p.Storage.Drive;
        return new Capability("storage", "platform", "Room for models", status, "",
            $"{free:0} GB free on {drive}. This kit wants {need:0} GB free for model downloads.", "detected");
    }

    private static Capability NativeBuild(DeviceProfile p) =>
        p.Os.Emulated
            ? new Capability("native-build", "platform", "Native build", Limited, "",
                $"This {p.Os.ProcessArch} build is running under emulation on {p.Os.Arch} Windows, so NPU and GPU runtimes cannot load. Use the {p.Os.Arch} build.", "detected")
            : new Capability("native-build", "platform", "Native build", Available, "",
                $"{p.Os.ProcessArch} build on {p.Os.Arch} Windows.", "detected");

    private static Capability PowerPoint(DeviceProfile p)
    {
        string name = p.Software.PowerPointVersion.Length > 0 ? $"PowerPoint {p.Software.PowerPointVersion}" : "PowerPoint";
        return p.Software.PowerPoint
            ? new Capability("powerpoint", "platform", "PowerPoint on this PC", Available, "",
                $"{name} is installed, so decks can be opened and exported as images.", "detected")
            : new Capability("powerpoint", "platform", "PowerPoint on this PC", Limited, "",
                "PowerPoint was not found. Previews use the built-in renderer and decks open in any PPTX viewer.", "detected");
    }
}
