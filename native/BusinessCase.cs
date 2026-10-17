using System.Text.Json;
using System.Text.Json.Nodes;

namespace HybridIntelligenceStudio;

internal static class BusinessCase
{
    internal static readonly (string Key, string Label, double Min, double Max, string Help)[] Fields =
    [
        ("users", "Users in scope", 1, 100000, "People who would use local AI"),
        ("tasks", "Tasks per user per day", 0, 100, "Repeatable drafting or summarising tasks"),
        ("minutes", "Minutes saved per task", 0, 120, "Hypothesis to validate in a pilot"),
        ("days", "Working days per year", 1, 366, ""),
        ("adoption", "Adoption (%)", 0, 100, "Share of eligible tasks actually using it"),
        ("hourly", "Loaded hourly cost (USD)", 0, 1000, "For illustrative capacity value only"),
        ("premium", "Device premium per user (USD)", 0, 20000, "Incremental investment assumption"),
    ];

    internal static readonly IReadOnlyDictionary<string, double> Defaults = new Dictionary<string, double>
    {
        ["users"] = 100, ["tasks"] = 4, ["minutes"] = 3, ["days"] = 220, ["adoption"] = 60, ["hourly"] = 60, ["premium"] = 600
    };

    internal static string? Validate(IReadOnlyDictionary<string, double> values)
    {
        if (values.Count != Fields.Length || Fields.Any(f => !values.ContainsKey(f.Key)))
            return "Provide exactly the seven calculator assumptions.";
        foreach (var (key, label, min, max, _) in Fields)
        {
            double value = values[key];
            if (!double.IsFinite(value)) return $"{label} must be a number.";
            if (value < min || value > max) return $"{label} must be between {min:N0} and {max:N0}.";
        }
        return null;
    }

    /// <summary>Used only to cross-check the authoritative service response in tests.</summary>
    internal static double ExpectedHours(IReadOnlyDictionary<string, double> v) =>
        Math.Round(v["users"] * v["tasks"] * v["minutes"] / 60 * v["days"] * v["adoption"] / 100, 2);
}

internal sealed record RunRecord(
    string ScenarioKey, string ScenarioTitle, string ScenarioLabel, string Prompt, string Source, string Mode,
    string Output, RunMetrics? Metrics, long ReasoningCharactersWithheld, DateTimeOffset StartedAt, string? Error);

internal static class Evidence
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    internal const string MetricCaveat =
        "Metrics describe this single local run on this device and are not a benchmark, a comparison with other devices or a throughput guarantee. " +
        "Time to first token is measured to the first visible answer text.";

    internal const string PrivacyNote =
        "Private model reasoning is never displayed, exported or saved; only its character count is recorded.";

    internal static JsonObject Device(DeviceFacts? d) => d is null ? new JsonObject { ["error"] = "Device inventory unavailable." } : new JsonObject
    {
        ["model"] = d.Model,
        ["manufacturer"] = d.Manufacturer,
        ["cpu"] = d.Cpu,
        ["cores"] = d.Cores,
        ["accelerators"] = new JsonArray(d.Accelerators.Select(a => (JsonNode?)a).ToArray()),
        ["installed_physical_memory_bytes"] = d.InstalledBytes,
        ["windows_visible_memory_bytes"] = d.OsVisibleBytes,
        ["gpu_reported_memory_budget_mib"] = d.PrimaryGpuBudgetMiB,
        ["memory_views_note"] = "Installed, Windows-visible and GPU-budget figures are distinct reported views; they are not additive. The GPU figure is a driver-reported budget, not dedicated VRAM.",
        ["memory_source"] = d.MemorySource,
        ["observed_at"] = d.ObservedAt,
        ["os"] = d.Os,
        ["os_version"] = d.OsVersion,
    };

    internal static JsonObject Run(RunRecord run, DeviceFacts? device, string lifecycle)
    {
        bool live = run.Mode == "live";
        var m = run.Metrics;
        return new JsonObject
        {
            ["schema"] = Brand.SchemaPrefix + ".run-evidence/1",
            ["exported_at"] = DateTimeOffset.Now.ToString("O"),
            ["exported_by"] = Brand.DisplayName + " (native WinUI 3) - explicit user export",
            ["mode"] = run.Mode,
            ["mode_label"] = live ? "LIVE local inference on this device's RTX GPU" : "AUTHORED REHEARSAL - not model output; no performance metrics",
            ["started_at"] = run.StartedAt.ToString("O"),
            ["scenario"] = new JsonObject
            {
                ["key"] = run.ScenarioKey, ["title"] = run.ScenarioTitle, ["label"] = run.ScenarioLabel,
                ["data"] = "Synthetic, fictional source notes"
            },
            ["prompt"] = run.Prompt,
            ["source"] = run.Source,
            ["output_text"] = run.Output,
            ["output_note"] = "Rendered as text. Generated code is never executed by this app.",
            ["error"] = run.Error,
            ["metrics"] = live && m is not null ? new JsonObject
            {
                ["model_name"] = m.ModelName, ["model_id"] = m.ModelId, ["execution_provider"] = m.Provider,
                ["ttft_ms"] = m.TtftMs, ["total_ms"] = m.TotalMs, ["characters"] = m.Characters,
                ["output_tokens"] = m.OutputTokens, ["tokens_per_second"] = m.TokensPerSecond, ["finish_reason"] = m.FinishReason,
            } : null,
            ["private_reasoning_characters_withheld"] = live ? run.ReasoningCharactersWithheld : 0,
            ["metric_caveat"] = MetricCaveat,
            ["privacy"] = PrivacyNote,
            ["device"] = Device(device),
            ["service_lifecycle"] = lifecycle,
        };
    }

    internal static JsonObject BusinessCaseExport(JsonElement response) => new()
    {
        ["schema"] = Brand.SchemaPrefix + ".business-case/1",
        ["exported_at"] = DateTimeOffset.Now.ToString("O"),
        ["calculated_by"] = "Local showcase service /api/calculate (deterministic arithmetic)",
        ["result"] = JsonNode.Parse(response.GetRawText()),
        ["formula"] = "hours = users x tasks x minutes / 60 x days x adoption / 100; capacity value = hours x hourly cost; investment = users x device premium",
        ["note"] = "Illustrative capacity value, not cash savings, an ROI forecast or a software-subscription replacement.",
    };

    internal static JsonObject SizingExport(SizingInput input, SizingResult result, TShirtSize? size, BudgetSource source) => new()
    {
        ["schema"] = Brand.SchemaPrefix + ".sizing-estimate/1",
        ["exported_at"] = DateTimeOffset.Now.ToString("O"),
        ["band"] = size?.ToString(),
        ["inputs"] = new JsonObject
        {
            ["parameter_billions"] = input.ParameterBillions, ["weight_bits"] = input.WeightBits,
            ["context_tokens"] = input.ContextTokens, ["kv_gib_per_1k_tokens"] = input.KvGiBPer1kTokens,
            ["runtime_overhead_gib"] = input.RuntimeOverheadGiB, ["budget_view"] = source.ToString(),
            ["budget_gib"] = input.BudgetGiB, ["reserve_gib"] = input.ReserveGiB, ["headroom_gib"] = input.HeadroomGiB,
        },
        ["result"] = result.IsValid ? new JsonObject
        {
            ["verdict"] = result.Verdict.ToString(), ["weight_gib"] = Math.Round(result.WeightGiB, 3),
            ["kv_gib"] = Math.Round(result.KvGiB, 3), ["working_set_gib"] = Math.Round(result.WorkingSetGiB, 3),
            ["usable_gib"] = Math.Round(result.UsableGiB, 3), ["required_gib"] = Math.Round(result.RequiredGiB, 3),
            ["margin_gib"] = Math.Round(result.MarginGiB, 3),
        } : new JsonObject { ["verdict"] = "Invalid", ["error"] = result.Error },
        ["formula"] = Sizing.Formula,
        ["caveat"] = Sizing.Caveat,
    };

    internal static string Save(JsonObject json, string directory, string prefix)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, json.ToJsonString(Options));
        return path;
    }
}
