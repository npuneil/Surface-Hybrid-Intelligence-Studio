using System.Text.Json.Nodes;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

/// <summary>The run record written next to every deck: what model and lane wrote it, how long and how many tokens it took, what it cost,
/// the grade, and what lint still found. Field names follow the Python Forge's metrics file where they overlap.</summary>
public static class Metrics
{
    private static double R(double value, int digits = 2) => Math.Round(value, digits);

    public static JsonObject Build(HarnessResult result, DeviceStory story, string templateKey, double renderSeconds, string? pptxPath, string? deviceLabel = null)
    {
        var stats = result.Stats;
        double meanFirstToken = stats.LlmCalls > 0 ? stats.FirstTokenSeconds / stats.LlmCalls : 0;

        var power = result.Energy.Measured
            ? new JsonObject { ["measured"] = true, ["wh"] = R(result.Energy.Wh, 4), ["basis"] = result.Energy.Basis }
            : new JsonObject { ["measured"] = false, ["reason"] = result.Energy.Basis };

        var economics = Economics.Local(result.Energy, stats.PromptTokens, stats.CompletionTokens);
        var comparison = new JsonArray();
        foreach (string model in Economics.CloudPrices.Keys)
        {
            var row = Economics.Cloud(model, stats.PromptTokens, stats.CompletionTokens);
            row["estimate"] = true;
            comparison.Add(row);
        }
        economics["cloud_comparison"] = comparison;
        economics["assumptions"] = Economics.Assumptions();

        var history = new JsonArray
        {
            new JsonObject { ["pass"] = "draft", ["issues"] = result.DraftIssues.Count, ["codes"] = Strings(result.DraftIssues.Select(i => i.Code).Distinct().Order(StringComparer.Ordinal)) },
        };
        foreach (var repair in result.Repairs)
            history.Add(new JsonObject
            {
                ["pass"] = $"repair{repair.Pass}",
                ["issues"] = repair.IssuesAfter,
                ["accepted"] = repair.Accepted,
                ["fixed_slides"] = repair.FixedSlides.Count,
                ["codes"] = Strings(repair.Codes),
            });

        var summary = new JsonArray();
        foreach (var s in result.RenderSummary)
            summary.Add(new JsonObject
            {
                ["index"] = s.Index, ["layout"] = s.Layout, ["words"] = s.Words, ["overflows"] = s.Overflows, ["shrunk"] = s.Shrunk, ["icons"] = s.Icons,
                ["issues"] = Strings(s.Issues.Select(i => $"{i.Kind}: {i.Detail}")),
            });

        var metrics = new JsonObject
        {
            ["harness"] = result.Mode switch { HarnessMode.Arc => "arc", HarnessMode.OneShot => "one-shot", _ => "guided" },
            ["brief"] = result.Brief.Id,
            ["template"] = templateKey,
            ["device"] = deviceLabel ?? story.NameWithSilicon,
            ["lane"] = result.Model.Lane,
            ["model"] = result.Model.Model,
            ["runtime"] = result.Model.Runtime,
            ["context_tokens"] = result.Model.ContextTokens,
            ["context_level"] = result.ContextLevel,
            ["guided_decoding"] = result.GuidedDecoding,
            ["llm_calls"] = stats.LlmCalls,
            ["retries"] = stats.Retries,
            ["prompt_tokens"] = stats.PromptTokens,
            ["completion_tokens"] = stats.CompletionTokens,
            ["tokens_measured"] = stats.TokensMeasured,
            ["decode_tps"] = R(stats.DecodeTokensPerSecond, 1),
            ["ttft_s"] = R(meanFirstToken, 3),
            ["generate_s"] = R(stats.GenerateSeconds),
            ["render_s"] = R(renderSeconds),
            ["total_s"] = R(result.DraftSeconds + renderSeconds),
            ["power"] = power,
            ["economics"] = economics,
            ["grade"] = result.Grade?.ToJson(),
            ["pptx"] = pptxPath,
            ["render_summary"] = summary,
            ["lint_history"] = history,
            ["lint_remaining"] = new JsonArray([.. result.LintRemaining.Select(i => (JsonNode?)i.ToJson())]),
            ["measured_facts"] = result.MeasuredFact is null ? new JsonArray() : new JsonArray(result.MeasuredFact),
            ["warnings"] = Strings(result.Warnings),
        };
        return metrics;
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]);
}
