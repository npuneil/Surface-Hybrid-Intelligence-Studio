using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HybridIntelligenceStudio;

internal sealed record DemoOption(string Id, string Label, string Prompt, string? Media, bool Rehearsal);

internal sealed record DemoStepInfo(string Title, string Role);

internal sealed record DemoInfo(
    string Id, string Title, string Category, string Glyph, string Workload, string Pitch, string Proof,
    string InputKind, string InputLabel, string? InputMedia, string Source, string? OptionLabel,
    IReadOnlyList<DemoOption> Options, string? DefaultOption, string Prompt, string PromptLabel,
    bool Editable, bool Rehearsal, IReadOnlyList<DemoStepInfo> Steps)
{
    internal DemoOption? Option(string? id) => Options.FirstOrDefault(o => o.Id == id);
    internal string PromptFor(string? option) => Option(option)?.Prompt ?? Prompt;
    internal string? MediaFor(string? option) => Option(option) is { } o ? o.Media : InputMedia;
    internal bool RehearsalFor(string? option) => Option(option) is { } o ? o.Rehearsal : Rehearsal;
    internal IEnumerable<string> Roles => Steps.Select(s => s.Role);
}

internal sealed record MediaModelStatus(string Role, string Label, string Id, bool Available, bool Cached, bool Loaded, double? SizeMb);

internal sealed record DemoCatalog(
    IReadOnlyList<DemoInfo> Demos, string MediaState, string MediaDetail, string RuntimeState, string RuntimeDetail,
    IReadOnlyList<MediaModelStatus> MediaModels)
{
    /// <summary>Demo ids the native app links to without a service round trip; a Python test keeps this in sync with demos.ORDER.</summary>
    internal static readonly string[] KnownIds =
        ["meeting", "voice-memo", "vision", "ask-docs", "translate", "brief", "redact", "triage", "code", "reasoning", "faceoff", "team"];

    internal static readonly string[] MediaRoles = ["asr", "vision", "embed"];

    internal static readonly DemoCatalog Empty = new([], "pending", "Demo list not loaded yet.", "unknown", "", []);

    internal DemoInfo? Find(string? id) => Demos.FirstOrDefault(d => d.Id == id);

    internal bool RuntimeReady => RuntimeState is "active" or "not_needed";

    /// <summary>Why a live run cannot start yet, or null when it can. Rehearsal never needs models or media.</summary>
    internal string? LiveBlocker(DemoInfo demo, EngineState? engine)
    {
        if (engine is null) return "Waiting for the local GPU service.";
        if (!engine.IsReady) return "Waiting for the chat model to finish loading on the GPU.";
        var roles = demo.Roles.ToHashSet(StringComparer.Ordinal);
        bool needsMedia = roles.Overlaps(["asr", "vision"]) || demo.InputKind is "audio" or "image";
        if (roles.Overlaps(MediaRoles) && !RuntimeReady)
            return string.IsNullOrWhiteSpace(RuntimeDetail) ? "The GPU speech and vision libraries are not prepared." : RuntimeDetail;
        foreach (string role in roles.Where(MediaRoles.Contains))
        {
            var model = MediaModels.FirstOrDefault(m => m.Role == role);
            if (model is null || !model.Available) return $"The {role} model is not in this device's catalog.";
            if (!model.Cached) return $"{model.Label} is not downloaded yet. Run Start-Showcase.ps1 -Prepare once.";
        }
        foreach (string role in roles)
        {
            var choice = engine.Models.FirstOrDefault(m => m.Alias == role);
            if (choice is not null && !choice.Cached) return $"{choice.Label} is not downloaded yet. Run Start-Showcase.ps1 -Prepare once.";
        }
        if (needsMedia && MediaState != "ready")
            return MediaState == "error" ? "Demo media could not be rendered: " + MediaDetail : "Rendering the synthetic demo media...";
        return null;
    }
}

internal enum DemoEventKind { Plan, Step, Text, Activity, Check, Done, Error, Ignored }

internal sealed record DemoMetric(string Label, string Value);

internal sealed record DemoCheck(string Label, string Value, bool Pass, string Detail);

internal sealed record DemoPlanStep(string Title, string Model);

internal sealed record DemoSummary(string Mode, double? TotalMs, int ChecksPassed, int ChecksTotal, int StepsFailed, string? Note)
{
    internal bool AllPassed => StepsFailed == 0 && ChecksPassed == ChecksTotal;
}

/// <summary>One NDJSON event from /api/demo/run. Hidden reasoning is only ever a character count.</summary>
internal sealed record DemoEvent(DemoEventKind Kind, int Index = -1, string? State = null, string? Model = null,
    string? Detail = null, string? Text = null, string? Error = null, IReadOnlyList<DemoMetric>? Metrics = null,
    DemoCheck? Check = null, IReadOnlyList<DemoPlanStep>? Plan = null, long ReasoningCharacters = 0, DemoSummary? Summary = null);

/// <summary>Accumulated state of one step during and after a run.</summary>
internal sealed class DemoStepRecord(string title, string model)
{
    internal string Title { get; } = title;
    internal string Model { get; set; } = model;
    internal string State { get; set; } = "waiting";
    internal string? Detail { get; set; }
    internal string? Error { get; set; }
    internal StringBuilder Text { get; } = new();
    internal List<DemoMetric> Metrics { get; } = [];
    internal long ReasoningCharacters { get; set; }
}

internal sealed record DemoRunRecord(
    DemoInfo Demo, string Mode, string? Option, string Prompt, bool Edited, DateTimeOffset StartedAt,
    IReadOnlyList<DemoStepRecord> Steps, IReadOnlyList<DemoCheck> Checks, DemoSummary? Summary, string? Error)
{
    internal bool Live => Mode == "live";
    internal bool Completed => Error is null && Summary is not null;
    internal bool Passed => Completed && Summary!.AllPassed;
    /// <summary>Every step ran; checks may still have graded an answer wrong (shown amber, not as a failure).</summary>
    internal bool RanEndToEnd => Completed && Summary!.StepsFailed == 0;

    internal string Outcome => Error is not null ? "Did not complete"
        : !Live ? "Rehearsal shown"
        : Summary!.StepsFailed > 0 ? $"{Summary.StepsFailed} step{(Summary.StepsFailed == 1 ? "" : "s")} failed"
        : Summary.ChecksTotal == 0 ? "Completed (no checks scored)"
        : $"{Summary.ChecksPassed} of {Summary.ChecksTotal} checks passed";

    internal string AllText() => string.Join("\n\n", Steps.Where(s => s.Text.Length > 0).Select(s => $"## {s.Title} ({s.Model})\n{s.Text.ToString().Trim()}"));
}

internal static class DemoParser
{
    private static string S(JsonElement e, string name, string fallback = "") => StatusParser.Str(e, name) ?? fallback;

    internal static DemoCatalog ParseCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Demo list was not a JSON object.");
        var demos = new List<DemoInfo>();
        if (root.TryGetProperty("demos", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var d in list.EnumerateArray())
                if (d.ValueKind == JsonValueKind.Object && ParseDemo(d) is { } demo) demos.Add(demo);
        string mediaState = "pending", mediaDetail = "";
        if (root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
        {
            mediaState = S(media, "state", "pending");
            mediaDetail = S(media, "detail");
        }
        string runtimeState = "unknown", runtimeDetail = "";
        var models = new List<MediaModelStatus>();
        if (root.TryGetProperty("engine", out var engine) && engine.ValueKind == JsonValueKind.Object
            && engine.TryGetProperty("media", out var em) && em.ValueKind == JsonValueKind.Object)
        {
            if (em.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Object)
            {
                runtimeState = S(rt, "state", "unknown");
                runtimeDetail = S(rt, "detail");
            }
            if (em.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    if (m.ValueKind == JsonValueKind.Object && StatusParser.Str(m, "role") is string role)
                        models.Add(new MediaModelStatus(role, S(m, "label", role), S(m, "id"), StatusParser.Bool(m, "available"),
                            StatusParser.Bool(m, "cached"), StatusParser.Bool(m, "loaded"), StatusParser.Num(m, "size_mb")));
        }
        return new DemoCatalog(demos, mediaState, mediaDetail, runtimeState, runtimeDetail, models);
    }

    private static DemoInfo? ParseDemo(JsonElement d)
    {
        if (StatusParser.Str(d, "id") is not string id || StatusParser.Str(d, "title") is not string title) return null;
        string kind = "text", label = "", inputMedia = "";
        if (d.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            kind = S(input, "kind", "text");
            label = S(input, "label");
            inputMedia = S(input, "media");
        }
        var options = new List<DemoOption>();
        if (d.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            foreach (var o in opts.EnumerateArray())
                if (o.ValueKind == JsonValueKind.Object && StatusParser.Str(o, "id") is string oid)
                    options.Add(new DemoOption(oid, S(o, "label", oid), S(o, "prompt"), StatusParser.Str(o, "media"), StatusParser.Bool(o, "rehearsal")));
        var steps = new List<DemoStepInfo>();
        if (d.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array)
            foreach (var s in st.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object)
                    steps.Add(new DemoStepInfo(S(s, "title", "Step"), S(s, "role")));
        return new DemoInfo(id, title, S(d, "category"), S(d, "glyph", "\uE768"), S(d, "workload"), S(d, "pitch"), S(d, "proof"),
            kind, label, string.IsNullOrEmpty(inputMedia) ? null : inputMedia, S(d, "source"), StatusParser.Str(d, "option_label"),
            options, StatusParser.Str(d, "default_option"), S(d, "prompt"), S(d, "prompt_label", "Instruction"),
            StatusParser.Bool(d, "editable"), StatusParser.Bool(d, "rehearsal"), steps);
    }

    internal static DemoEvent ParseEvent(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Stream event was not a JSON object.");
        int index = StatusParser.Num(root, "index") is double i ? (int)i : -1;
        switch (StatusParser.Str(root, "type"))
        {
            case "plan":
                var plan = new List<DemoPlanStep>();
                if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                    foreach (var s in steps.EnumerateArray())
                        if (s.ValueKind == JsonValueKind.Object) plan.Add(new DemoPlanStep(S(s, "title", "Step"), S(s, "model")));
                return new DemoEvent(DemoEventKind.Plan, Plan: plan);
            case "step":
                var metrics = new List<DemoMetric>();
                if (root.TryGetProperty("metrics", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    foreach (var m in ms.EnumerateArray())
                        if (m.ValueKind == JsonValueKind.Object && StatusParser.Str(m, "label") is string ml)
                            metrics.Add(new DemoMetric(ml, S(m, "value")));
                return new DemoEvent(DemoEventKind.Step, index, S(root, "state", "running"), StatusParser.Str(root, "model"),
                    StatusParser.Str(root, "detail"), Error: StatusParser.Str(root, "error"), Metrics: metrics);
            case "text":
                return new DemoEvent(DemoEventKind.Text, index, Text: S(root, "text"));
            case "activity":
                return new DemoEvent(DemoEventKind.Activity, index, ReasoningCharacters: (long)(StatusParser.Num(root, "reasoning_characters") ?? 0));
            case "check":
                return new DemoEvent(DemoEventKind.Check, index, Check: new DemoCheck(S(root, "label", "Check"), S(root, "value"),
                    StatusParser.Bool(root, "pass"), S(root, "detail")));
            case "done":
                return new DemoEvent(DemoEventKind.Done, Summary: new DemoSummary(S(root, "mode", "unknown"), StatusParser.Num(root, "total_ms"),
                    (int)(StatusParser.Num(root, "checks_passed") ?? 0), (int)(StatusParser.Num(root, "checks_total") ?? 0),
                    (int)(StatusParser.Num(root, "steps_failed") ?? 0), StatusParser.Str(root, "note")));
            case "error":
                return new DemoEvent(DemoEventKind.Error, Error: S(root, "error", "The demo failed."));
            default:
                return new DemoEvent(DemoEventKind.Ignored);
        }
    }

    /// <summary>Duration of a PCM WAV file from its header, or null when the header is not understood.</summary>
    internal static double? WavSeconds(byte[] wav)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
        int byteRate = 0;
        for (int pos = 12; pos + 8 <= wav.Length;)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            if (size < 0) return null;
            if (id == "fmt " && pos + 20 <= wav.Length) byteRate = BitConverter.ToInt32(wav, pos + 16);
            if (id == "data") return byteRate > 0 ? Math.Min(size, wav.Length - pos - 8) / (double)byteRate : null;
            pos += 8 + size + (size & 1);
        }
        return null;
    }
}

internal static class DemoEvidence
{
    internal static JsonObject Run(DemoRunRecord run, DeviceFacts? device, string lifecycle) => new()
    {
        ["schema"] = Brand.SchemaPrefix + ".demo-evidence/1",
        ["exported_at"] = DateTimeOffset.Now.ToString("O"),
        ["exported_by"] = Brand.DisplayName + " (native WinUI 3) - explicit user export",
        ["demo"] = new JsonObject { ["id"] = run.Demo.Id, ["title"] = run.Demo.Title, ["category"] = run.Demo.Category, ["data"] = "Synthetic, fictional inputs" },
        ["mode"] = run.Mode,
        ["mode_label"] = run.Live ? "LIVE local inference on this device's RTX GPU" : "AUTHORED REHEARSAL - not model output; no checks scored",
        ["option"] = run.Option,
        ["prompt"] = run.Prompt,
        ["prompt_edited"] = run.Edited,
        ["started_at"] = run.StartedAt.ToString("O"),
        ["outcome"] = run.Outcome,
        ["error"] = run.Error,
        ["steps"] = new JsonArray(run.Steps.Select(s => (JsonNode?)new JsonObject
        {
            ["title"] = s.Title, ["model"] = s.Model, ["state"] = s.State, ["error"] = s.Error,
            ["output_text"] = s.Text.ToString(),
            ["metrics"] = new JsonArray(s.Metrics.Select(m => (JsonNode?)new JsonObject { ["label"] = m.Label, ["value"] = m.Value }).ToArray()),
            ["private_reasoning_characters_withheld"] = s.ReasoningCharacters,
        }).ToArray()),
        ["checks"] = new JsonArray(run.Checks.Select(c => (JsonNode?)new JsonObject
        {
            ["label"] = c.Label, ["value"] = c.Value, ["passed"] = c.Pass, ["detail"] = c.Detail,
        }).ToArray()),
        ["summary"] = run.Summary is { } s2 ? new JsonObject
        {
            ["total_ms"] = s2.TotalMs, ["checks_passed"] = s2.ChecksPassed, ["checks_total"] = s2.ChecksTotal,
            ["steps_failed"] = s2.StepsFailed, ["note"] = s2.Note,
        } : null,
        ["output_note"] = "Rendered as text. Generated code is never executed by this app.",
        ["check_note"] = CheckNote,
        ["metric_caveat"] = Evidence.MetricCaveat,
        ["privacy"] = Evidence.PrivacyNote,
        ["device"] = Evidence.Device(device),
        ["service_lifecycle"] = lifecycle,
    };

    internal const string CheckNote =
        "Checks compare live output with ground truth written into the synthetic inputs. They show this run's result, not a general accuracy rate.";
}
