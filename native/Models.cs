using System.Text.Json;

namespace HybridIntelligenceStudio;

internal sealed record ModelChoice(string Alias, string Id, string Label, bool Reasoning, bool Cached, double? SizeMb, string? Provider);

internal sealed record ActiveModel(string Name, string Alias, string Id, string Provider, bool Reasoning, string? License);

internal sealed record EngineState(string State, string Detail, string? SelectedModel, ActiveModel? Model,
    IReadOnlyList<ModelChoice> Models, string? Error)
{
    internal bool IsReady => State == "ready" && Model is not null;
    internal bool IsBusy => State is "starting" or "loading" or "downloading" or "warming";
}

internal sealed record GpuBudget(string Name, long ReportedBudgetMiB);

internal sealed record DeviceFacts(
    string Model, string? Manufacturer, string? Cpu, int? Cores, IReadOnlyList<string> Accelerators,
    long? InstalledBytes, long? OsVisibleBytes, IReadOnlyList<GpuBudget> GpuBudgets,
    string? MemorySource, string? ObservedAt, string? Os, string? OsVersion, string? Error)
{
    internal long? PrimaryGpuBudgetMiB => GpuBudgets.Count > 0 ? GpuBudgets[0].ReportedBudgetMiB : null;
    internal double? InstalledGiB => InstalledBytes / Sizing.BytesPerGiB;
    internal double? OsVisibleGiB => OsVisibleBytes / Sizing.BytesPerGiB;

    /// <summary>Model name without a repeated vendor prefix ("Microsoft Corporation Microsoft Surface ...").</summary>
    internal string DisplayName
    {
        get
        {
            string? vendor = Manufacturer?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(Manufacturer) || (vendor is not null && Model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
                ? Model : $"{Manufacturer} {Model}";
        }
    }
}

internal sealed record StatusSnapshot(string AppId, EngineState Engine, DeviceFacts Device,
    IReadOnlyDictionary<string, double> Assumptions);

internal sealed record Scenario(string Key, string Title, string Label, string Prompt, string Source);

internal static class StatusParser
{
    internal const string ExpectedAppId = Brand.ServiceAppId;

    internal static StatusSnapshot Parse(JsonElement root)
    {
        string appId = Str(root, "app_id") ?? "";
        var engine = root.TryGetProperty("engine", out var e) && e.ValueKind == JsonValueKind.Object ? ParseEngine(e)
            : new EngineState("unknown", "Engine status unavailable", null, null, [], null);
        var device = root.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.Object ? ParseDevice(d)
            : new DeviceFacts("Device not verified", null, null, null, [], null, null, [], null, null, null, null, "No device inventory returned.");
        var assumptions = new Dictionary<string, double>();
        if (root.TryGetProperty("assumptions", out var a) && a.ValueKind == JsonValueKind.Object)
            foreach (var p in a.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) assumptions[p.Name] = p.Value.GetDouble();
        return new StatusSnapshot(appId, engine, device, assumptions);
    }

    private static EngineState ParseEngine(JsonElement e)
    {
        ActiveModel? model = null;
        if (e.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object)
            model = new ActiveModel(Str(m, "name") ?? "Unknown model", Str(m, "alias") ?? "", Str(m, "id") ?? "",
                Str(m, "provider") ?? "", Bool(m, "reasoning"), Str(m, "license"));
        var models = new List<ModelChoice>();
        if (e.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var c in list.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && Str(c, "alias") is string alias)
                    models.Add(new ModelChoice(alias, Str(c, "id") ?? "", Str(c, "label") ?? alias, Bool(c, "reasoning"),
                        Bool(c, "cached"), Num(c, "size_mb"), Str(c, "provider")));
        return new EngineState(Str(e, "state") ?? "unknown", Str(e, "detail") ?? "", Str(e, "selected_model"), model, models, Str(e, "error"));
    }

    private static DeviceFacts ParseDevice(JsonElement d)
    {
        var accelerators = new List<string>();
        if (d.TryGetProperty("accelerators", out var acc))
        {
            if (acc.ValueKind == JsonValueKind.Array)
                foreach (var a in acc.EnumerateArray()) { if (a.ValueKind == JsonValueKind.String) accelerators.Add(a.GetString()!); }
            else if (acc.ValueKind == JsonValueKind.String) accelerators.Add(acc.GetString()!);
        }
        var budgets = new List<GpuBudget>();
        if (d.TryGetProperty("gpu_memory", out var gm) && gm.ValueKind == JsonValueKind.Array)
            foreach (var g in gm.EnumerateArray())
                if (g.ValueKind == JsonValueKind.Object && Num(g, "reported_budget_mib") is double mib && mib > 0)
                    budgets.Add(new GpuBudget(Str(g, "name") ?? "GPU", (long)mib));
        double? cores = Num(d, "cores");
        double? installed = Num(d, "installed_ram_bytes");
        double? visible = Num(d, "os_visible_ram_bytes");
        return new DeviceFacts(Str(d, "model") ?? "Device not verified", Str(d, "manufacturer"), Str(d, "cpu"),
            cores is null ? null : (int)cores, accelerators, installed is null ? null : (long)installed,
            visible is null ? null : (long)visible, budgets, Str(d, "memory_source"), Str(d, "observed_at"),
            Str(d, "os"), Str(d, "os_version"), Str(d, "error"));
    }

    internal static Dictionary<string, Scenario> ParseScenarios(JsonElement root)
    {
        var result = new Dictionary<string, Scenario>();
        if (root.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object) continue;
            string? prompt = Str(p.Value, "prompt");
            if (string.IsNullOrWhiteSpace(prompt)) continue;
            result[p.Name] = new Scenario(p.Name, Str(p.Value, "title") ?? p.Name, Str(p.Value, "label") ?? p.Name,
                prompt, Str(p.Value, "source") ?? "");
        }
        return result;
    }

    internal static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double x) && double.IsFinite(x) ? x : null;

    internal static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
