using System.Text.Json.Nodes;
using Forge.Core.Harness;
using Forge.Engine;
using SurfaceDeviceKit.FoundryLocal;

namespace Forge.Cli;

/// <summary>Plain-text and JSON views of what the engine decided and what a run did.</summary>
internal static class Show
{
    public const int TypicalSlides = ForgeText.TypicalSlides;

    public static string Gb(double gb) => ForgeText.Gb(gb);

    public static string Settings(RunSettings s) => ForgeText.Settings(s);

    public static string Speed(SpeedLog log, string variant, SpeedEntry? entry, int slides) => ForgeText.Speed(log, variant, entry, slides);

    /// <summary>Every model this PC can run for the Forge. The settings and the wait are shown for the model that would start and for any
    /// that differ from it or have been measured here, so the list stays readable.</summary>
    public static string Options(ForgeSession session, SessionPlan plan, RunRequest request, int slides)
    {
        var lines = new List<string>();
        if (plan.Models.Options.Count == 0) lines.Add("  nothing can run here");
        string? baseline = null;
        foreach (var option in plan.Models.Options)
        {
            bool first = option == plan.Default;
            lines.Add($"  {(first ? ">" : " ")} {option.LaneLabel,-3} {option.Title,-30} {option.EpLabel,-17} {Gb(option.FileGb),8}  needs {option.RequiredGb,5:0.0} GB  {(option.Cached ? "on this PC" : "download")}");
            var speed = session.Speed.Entry(option.VariantId);
            string settings = Settings(RunPolicy.Decide(ModelFacts.From(option), speed, request));
            if (first) baseline = settings;
            if (first || settings != baseline) lines.Add("      " + settings);
            if (first || speed is not null) lines.Add("      " + Speed(session.Speed, option.VariantId, speed, slides));
        }
        foreach (string note in plan.Models.Notes) lines.Add("  note: " + note);
        return string.Join("\n", lines);
    }

    public static JsonObject SettingsJson(RunSettings s) => new()
    {
        ["mode"] = s.Mode.ToString(),
        ["repairPasses"] = s.RepairPasses,
        ["repairsPerPass"] = s.RepairsPerPass,
        ["constrainedDecoding"] = s.UseGuidance,
        ["why"] = new JsonArray([.. s.Why.Select(w => (JsonNode?)JsonValue.Create(w))]),
    };

    public static JsonObject SpeedJson(SpeedEntry? e) => e is null ? new JsonObject() : new JsonObject
    {
        ["decodeTokensPerSecond"] = Math.Round(e.DecodeTokensPerSecond, 1),
        ["prefillTokensPerSecond"] = Math.Round(e.PrefillTokensPerSecond, 1),
        ["firstTokenSeconds"] = Math.Round(e.FirstTokenSeconds, 2),
        ["slideTokens"] = e.SlideTokens,
        ["constrainedDecoding"] = e.Guidance.ToString().ToLowerInvariant(),
        ["measuredAt"] = e.MeasuredAt.ToString("O"),
        ["samples"] = e.Samples,
    };

    public static string Run(ForgeRun run, PreparedModel model)
    {
        var r = run.Result;
        var f = run.Files;
        var lines = new List<string>
        {
            $"Deck       {f.Pptx}",
            $"            {run.Deck.Slides.Count} slides, {new FileInfo(f.Pptx).Length / 1024:N0} KB{(f.Renamed ? ", saved under a numbered name because the usual one is open in PowerPoint" : "")}",
        };
        if (f.ContactSheet is not null) lines.Add($"Previews    {f.PreviewFolder}  (and {Path.GetFileName(f.ContactSheet)})");
        lines.Add($"Model       {model.Facts.Title} on the {model.Model.Device} ({model.Model.Ep}), {(r.Stats.DecodeTokensPerSecond > 0 ? $"{r.Stats.DecodeTokensPerSecond:0.0} tok/s decode" : "decode speed not measured")}");
        lines.Add($"Mode        {Settings(run.Settings)}");
        lines.Add($"Time        drafted in {Provenance.Duration(r.DraftSeconds)} ({r.Stats.LlmCalls} model calls, {Provenance.Count(r.Stats.CompletionTokens)} tokens{(r.Stats.TokensMeasured ? "" : ", estimated")}); built and saved in {run.BuildSeconds:0.0} s");
        string grade = r.Grade is null ? "not graded" : $"grade {r.Grade.AutoGrade} (auto score {r.Grade.AutoScore:0.0})";
        lines.Add($"Quality     {grade}; {r.LintRemaining.Count} lint issue{(r.LintRemaining.Count == 1 ? "" : "s")} left, {r.DraftIssues.Count} in the draft, {r.Repairs.Count} repair pass{(r.Repairs.Count == 1 ? "" : "es")} run");
        foreach (var issue in r.LintRemaining) lines.Add($"            slide {issue.Slide + 1}: {issue.Code}: {issue.Msg}");
        lines.Add(r.Energy.Measured ? $"Energy      {Provenance.Wh(r.Energy.Wh)} measured ({r.Energy.Basis})" : $"Energy      not measured ({r.Energy.Basis})");
        foreach (string warning in r.Warnings) lines.Add("Warning     " + warning);
        return string.Join("\n", lines);
    }

    public static JsonObject RunJson(ForgeRun run, PreparedModel model)
    {
        var r = run.Result;
        var f = run.Files;
        return new JsonObject
        {
            ["pptx"] = f.Pptx,
            ["folder"] = f.Folder,
            ["previewFolder"] = f.ContactSheet is null ? null : f.PreviewFolder,
            ["contactSheet"] = f.ContactSheet,
            ["slides"] = run.Deck.Slides.Count,
            ["model"] = model.Model.Describe(),
            ["settings"] = SettingsJson(run.Settings),
            ["mode"] = r.Mode.ToString(),
            ["constrainedDecoding"] = r.GuidedDecoding,
            ["draftSeconds"] = Math.Round(r.DraftSeconds, 1),
            ["buildSeconds"] = Math.Round(run.BuildSeconds, 2),
            ["modelCalls"] = r.Stats.LlmCalls,
            ["completionTokens"] = r.Stats.CompletionTokens,
            ["decodeTokensPerSecond"] = Math.Round(r.Stats.DecodeTokensPerSecond, 1),
            ["grade"] = r.Grade?.AutoGrade.ToString(),
            ["autoScore"] = r.Grade is null ? null : Math.Round(r.Grade.AutoScore, 1),
            ["lintLeft"] = new JsonArray([.. r.LintRemaining.Select(i => (JsonNode?)i.ToJson())]),
            ["lintInDraft"] = r.DraftIssues.Count,
            ["repairPasses"] = r.Repairs.Count,
            ["energy"] = new JsonObject { ["measured"] = r.Energy.Measured, ["wh"] = r.Energy.Measured ? Math.Round(r.Energy.Wh, 3) : null, ["basis"] = r.Energy.Basis },
            ["warnings"] = new JsonArray([.. r.Warnings.Select(w => (JsonNode?)JsonValue.Create(w))]),
            ["metrics"] = f.Metrics?.DeepClone(),
        };
    }
}
