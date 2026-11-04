using System.Text.Json;

namespace HybridIntelligenceStudio;

/// <summary>One processing lane (npu, gpu or cpu) of the PC the studio describes. The vendor (nvidia, qualcomm, intel, amd or other) is the device kit's.</summary>
internal sealed record TargetLane(string Id, bool Available, string? Device, string? Vendor = null);

/// <summary>The PC the studio describes, from /api/status.target: this PC, or a Surface previewed from the catalog.
/// Lane is the processing lane the chat model runs on, once the service has chosen one.</summary>
internal sealed record TargetInfo(bool Simulated, string Label, string Headline, string? DeviceClass, string? DeviceClassLabel,
    string? PcClassLabel, string? Where, IReadOnlyList<TargetLane> Lanes, string? Error, string? Lane = null)
{
    internal bool HasLane(string id) => Lanes.Any(l => l.Id == id && l.Available);

    /// <summary>Which platform guide card describes this PC: the vendor of the lane that runs the chat model. Null when that vendor has no card
    /// (AMD and others) or nothing has been chosen yet.</summary>
    internal string? Platform => Lanes.FirstOrDefault(l => l.Id == Lane && l.Available)?.Vendor switch
    {
        "nvidia" => "rtx", "qualcomm" => "snapdragon", "intel" => "intel", _ => null,
    };

    /// <summary>Short title-bar wording: the device class, marked as a preview while another device is shown.</summary>
    internal string ClassPillText => (Simulated ? "Preview \u00B7 " : "") + (DeviceClassLabel ?? "Device not detected");

    internal static TargetInfo? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.Object) return null;
        var lanes = new List<TargetLane>();
        if (t.TryGetProperty("lanes", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var l in list.EnumerateArray())
                if (l.ValueKind == JsonValueKind.Object && StatusParser.Str(l, "id") is string id)
                    lanes.Add(new TargetLane(id, StatusParser.Bool(l, "available"), StatusParser.Str(l, "device"), StatusParser.Str(l, "vendor")));
        return new TargetInfo(StatusParser.Bool(t, "simulated"), StatusParser.Str(t, "label") ?? "This PC",
            StatusParser.Str(t, "headline") ?? StatusParser.Str(t, "label") ?? "This PC", StatusParser.Str(t, "deviceClass"),
            StatusParser.Str(t, "deviceClassLabel"), StatusParser.Str(t, "pcClassLabel"), StatusParser.Str(t, "where"), lanes,
            StatusParser.Str(t, "error"), StatusParser.Str(t, "lane"));
    }
}

/// <summary>Where this PC runs its models, in the words the pages use. Neutral until the service reports the device.</summary>
internal sealed record TargetWords(string Where, string Provider)
{
    internal static readonly TargetWords Neutral = new("this PC", "Foundry Local");

    /// <summary>The loaded chat model is the best witness; before one loads, the service's description of the PC stands in. A previewed
    /// Surface is not what the engine runs, so it is always described by its own words.</summary>
    internal static TargetWords From(TargetInfo? target, ActiveModel? model)
    {
        bool preview = target?.Simulated == true;
        string? where = preview ? target!.Where : !string.IsNullOrWhiteSpace(model?.Where) ? model!.Where : target?.Where;
        string provider = !preview && !string.IsNullOrWhiteSpace(model?.Ep) ? $"the {model!.Ep} execution provider" : Neutral.Provider;
        return new TargetWords(string.IsNullOrWhiteSpace(where) ? Neutral.Where : where, provider);
    }

    /// <summary>False until the service has said where the models run.</summary>
    internal bool Known => Where != Neutral.Where;

    /// <summary>"the NPU" at the start of a sentence.</summary>
    internal string WhereCap => char.ToUpperInvariant(Where[0]) + Where[1..];
}

internal sealed record DeviceLane(string Id, string Kind, bool Available, string? Device, string? Vendor, IReadOnlyList<string> Eps,
    double? BudgetGb, string? ClassCap, double? RatedTops, string? Note);

internal sealed record DeviceCapability(string Id, string Group, string Title, string Status, string? Lane, string Detail, string Basis);

/// <summary>What this PC runs for one slot of the studio's lineup (chat, reasoner, speech, vision, embeddings).</summary>
internal sealed record LineupRow(string Slot, string Title, string Reference, bool Available, string? Label, string? Where, string? Lane,
    string? Device, string? Ep, double? SizeGb, bool Cached, bool Estimated, string? Better, string? Blocker)
{
    /// <summary>A short account of the row for the This PC page: model, lane, runtime and size, or why nothing runs.</summary>
    internal string Summary
    {
        get
        {
            if (!Available) return Blocker ?? "No model fits this PC.";
            var parts = new List<string> { $"{Label} on {Where}" };
            if (!string.IsNullOrWhiteSpace(Ep)) parts.Add(Ep!);
            if (SizeGb is double gb) parts.Add($"{gb:0.0} GB" + (Estimated ? " (estimated)" : ""));
            parts.Add(Cached ? "downloaded" : "not downloaded");
            return string.Join(" \u00B7 ", parts);
        }
    }
}

internal sealed record SimulatedDevice(string Id, string Group, string Label, string? Note);

internal sealed record DemoFitStep(string Title, string Role, bool Available, string? Label, string? Where, string? Lane, string? Blocker);

/// <summary>How well one demo fits the PC shown: ready, limited (some models are missing) or unavailable.</summary>
internal sealed record DemoFit(string State, IReadOnlyList<string> Lanes, string? Where, string Summary, string? Note, int Rank,
    IReadOnlyList<DemoFitStep> Steps)
{
    internal bool Unavailable => State == "unavailable";

    /// <summary>The model labels with where each runs, one per distinct model, in step order.</summary>
    internal string ModelLine => string.Join(" \u2192 ", Steps.Where(s => s.Available)
        .Select(s => $"{s.Label} on {s.Where}").Distinct());

    internal string? FirstBlocker => Steps.FirstOrDefault(s => !s.Available)?.Blocker;

    internal static DemoFit? Parse(JsonElement demo) =>
        demo.TryGetProperty("fit", out var f) && f.ValueKind == JsonValueKind.Object ? ParseFields(f) : null;

    /// <summary>Reads the fit fields from the object that holds them: a demo's "fit" in /api/demos, or a demo row of /api/device.</summary>
    internal static DemoFit ParseFields(JsonElement f)
    {
        var steps = new List<DemoFitStep>();
        if (f.TryGetProperty("steps", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var s in list.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object)
                    steps.Add(new DemoFitStep(StatusParser.Str(s, "title") ?? "Step", StatusParser.Str(s, "role") ?? "",
                        StatusParser.Bool(s, "available"), StatusParser.Str(s, "label"), StatusParser.Str(s, "where"),
                        StatusParser.Str(s, "lane"), StatusParser.Str(s, "blocker")));
        var lanes = new List<string>();
        if (f.TryGetProperty("lanes", out var ll) && ll.ValueKind == JsonValueKind.Array)
            foreach (var l in ll.EnumerateArray())
                if (l.ValueKind == JsonValueKind.String) lanes.Add(l.GetString()!);
        return new DemoFit(StatusParser.Str(f, "state") ?? "ready", lanes, StatusParser.Str(f, "where"), StatusParser.Str(f, "summary") ?? "",
            StatusParser.Str(f, "note"), (int)(StatusParser.Num(f, "rank") ?? 0), steps);
    }
}

internal sealed record DeviceDemoRow(string Id, DemoFit Fit);

/// <summary>The device kit's report for the PC shown (/api/device): inventory, lanes, capabilities, the model lineup and the demo fits.</summary>
internal sealed record DeviceReport(
    bool Simulated, string Label, string Headline, string DetectedLabel, string? PcClassLabel, string? DeviceClass, string? DeviceClassLabel,
    string Product, string Cpu, int? CpuCores, IReadOnlyList<string> Gpus, IReadOnlyList<string> Npus, double? InstalledGb, double? VisibleGb,
    string Os, IReadOnlyList<DeviceLane> Lanes, IReadOnlyList<DeviceCapability> Capabilities, IReadOnlyList<LineupRow> Lineup,
    string Placement, string? SimulatedId, IReadOnlyList<SimulatedDevice> Devices, IReadOnlyList<DeviceDemoRow> Demos,
    string? GpuName = null, string? GpuKind = null, double? GpuMemoryGb = null)
{
    internal DeviceLane? Lane(string id) => Lanes.FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// This report in the shape of a PC's own memory facts, so the sizing pages budget a previewed Surface the same way they budget this PC.
    /// A GPU budget is only given for a GPU with memory of its own or a unified pool: an integrated GPU's "video memory" is a share of
    /// system memory, not a separate budget.
    /// </summary>
    internal DeviceFacts ToFacts()
    {
        bool gpuBudget = Lane("gpu") is { Available: true } && GpuKind is "discrete" or "unified" && GpuMemoryGb is > 0;
        return new DeviceFacts(Label, null, Cpu, CpuCores, [.. Gpus, .. Npus],
            InstalledGb is double installed ? (long)Math.Round(installed * Sizing.BytesPerGiB) : null,
            VisibleGb is double visible ? (long)Math.Round(visible * Sizing.BytesPerGiB) : null,
            gpuBudget ? [new GpuBudget(GpuName ?? "GPU", (long)Math.Round(GpuMemoryGb!.Value * 1024))] : [],
            "Device kit profile (estimate)", null, null, null, null);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>A stronger option that first needs a download, as a sentence; null when the lineup row names none.</summary>
    private static string? StrongerOption(JsonElement row) =>
        Obj(row, "better") is { } b && StatusParser.Str(b, "label") is string label
            ? $"{label} is a stronger option that first needs a download" + (StatusParser.Num(b, "size_mb") is double mb ? $" ({mb / 1024:0.0} GB)." : ".")
            : null;

    /// <summary>The named child object, or an empty object, so a field that is missing reads as absent instead of throwing.</summary>
    private static JsonElement Obj(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : EmptyObject;

    internal static DeviceReport Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Device report was not a JSON object.");
        if (!Obj(root, "report").TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object)
            throw new FormatException("Device report has no profile.");
        var detected = Obj(root, "detected");

        static IReadOnlyList<JsonElement> Items(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)] : [];

        static IReadOnlyList<string> Names(JsonElement parent, string name, Func<JsonElement, string?> label) =>
            [.. Items(parent, name).Select(label).OfType<string>()];

        var lanes = Items(profile, "lanes").Select(l => new DeviceLane(StatusParser.Str(l, "id") ?? "", StatusParser.Str(l, "kind") ?? "",
            StatusParser.Bool(l, "available"), StatusParser.Str(l, "device"), StatusParser.Str(l, "vendor"),
            l.TryGetProperty("eps", out var eps) && eps.ValueKind == JsonValueKind.Array
                ? [.. eps.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)] : [],
            StatusParser.Num(l, "budgetGb"), StatusParser.Str(l, "classCap"), StatusParser.Num(l, "ratedTops"), StatusParser.Str(l, "note"))).ToList();

        var capabilities = Items(profile, "capabilities").Select(c => new DeviceCapability(StatusParser.Str(c, "id") ?? "",
            StatusParser.Str(c, "group") ?? "", StatusParser.Str(c, "title") ?? "", StatusParser.Str(c, "status") ?? "unavailable",
            StatusParser.Str(c, "lane"), StatusParser.Str(c, "detail") ?? "", StatusParser.Str(c, "basis") ?? "")).ToList();

        var lineup = Items(root, "models").Select(m => new LineupRow(StatusParser.Str(m, "slot") ?? "", StatusParser.Str(m, "title") ?? "",
            StatusParser.Str(m, "reference") ?? "", StatusParser.Bool(m, "available"), StatusParser.Str(m, "label"), StatusParser.Str(m, "where"),
            StatusParser.Str(m, "lane"), StatusParser.Str(m, "device"), StatusParser.Str(m, "ep"), StatusParser.Num(m, "sizeGb"),
            StatusParser.Bool(m, "cached"), StatusParser.Bool(m, "estimated"), StrongerOption(m), StatusParser.Str(m, "blocker"))).ToList();

        var simulation = Obj(root, "simulation");
        var devices = Items(simulation, "devices").Select(x => new SimulatedDevice(StatusParser.Str(x, "id") ?? "", StatusParser.Str(x, "group") ?? "",
            StatusParser.Str(x, "label") ?? "", StatusParser.Str(x, "note"))).Where(x => x.Id.Length > 0).ToList();

        var demos = Items(root, "demos").Select(x => new DeviceDemoRow(StatusParser.Str(x, "id") ?? "", DemoFit.ParseFields(x)))
            .Where(x => x.Id.Length > 0).ToList();

        var memory = Obj(profile, "memory");
        var cpu = Obj(profile, "cpu");
        var os = Obj(profile, "os");
        string osText = StatusParser.Str(os, "version") is string v
            ? $"{v}, {StatusParser.Str(os, "arch") ?? "unknown arch"}" + (StatusParser.Bool(os, "emulated") ? " (this app is emulated)" : "")
            : "Operating system not reported";
        string label = StatusParser.Str(profile, "label") ?? "This PC";
        double? cores = StatusParser.Num(cpu, "logicalCores");
        var gpuItems = Items(profile, "gpus");
        string? laneGpu = lanes.FirstOrDefault(l => l.Id == "gpu")?.Device;
        JsonElement? primaryGpu = gpuItems.Count == 0 ? null : gpuItems.FirstOrDefault(g => StatusParser.Str(g, "name") == laneGpu, gpuItems[0]);
        string? gpuName = primaryGpu is { } pg ? StatusParser.Str(pg, "name") : null;
        string? gpuKind = primaryGpu is { } pk ? StatusParser.Str(pk, "kind") : null;
        double? gpuMemory = primaryGpu is { } pm ? StatusParser.Num(pm, "vramGb") : null;

        return new DeviceReport(StatusParser.Bool(root, "simulated"), label, StatusParser.Str(profile, "headline") ?? label,
            StatusParser.Str(detected, "label") ?? label, StatusParser.Str(profile, "pcClassLabel"), StatusParser.Str(profile, "deviceClass"),
            StatusParser.Str(profile, "deviceClassLabel"), StatusParser.Str(profile, "product") ?? "", StatusParser.Str(cpu, "name") ?? "Processor not reported",
            cores is null ? null : (int)cores, Names(profile, "gpus", g => StatusParser.Str(g, "name")),
            Names(profile, "npus", n => StatusParser.Str(n, "name")), StatusParser.Num(memory, "installedGb"), StatusParser.Num(memory, "visibleGb"),
            osText, lanes, capabilities, lineup, StatusParser.Str(root, "placement") ?? "", StatusParser.Str(simulation, "id"), devices, demos,
            gpuName, gpuKind, gpuMemory);
    }
}
