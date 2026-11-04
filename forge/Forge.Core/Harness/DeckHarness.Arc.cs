using System.Text.Json.Nodes;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

public sealed partial class DeckHarness
{
    private const int ArcAttempts = 2;

    /// <summary>The whole deck in one reply: the v1.1 story in a single call, or the v1.0 prompt when <paramref name="oneShot"/> is set. A model
    /// whose reply cannot be used twice, or whose window cannot hold the prompt, falls back to writing the deck slide by slide.</summary>
    private async Task<Draft> ArcAsync(bool oneShot)
    {
        SetStep("draft", 1, 1, oneShot ? "Writing the whole deck in one reply" : "Writing the whole story in one reply");
        JsonObject? spec = null;
        string raw = "";
        bool strict = false;
        string? note = null;
        try
        {
            for (int attempt = 1; attempt <= ArcAttempts && spec is null; attempt++)
            {
                var reply = await ChatAsync(level => oneShot ? Prompts.OneShot(_brief, 8192, note) : Prompts.Arc(_brief, _story, level, 8192, note)).ConfigureAwait(false);
                raw = reply.Text;
                var parsed = SpecJson.Parse(raw);
                if (parsed.Spec is null) note = $"it was not valid JSON ({parsed.Error})";
                else
                {
                    var candidate = SpecJson.Normalize(parsed.Spec, _brief);
                    int count = SpecJson.SlideObjects(candidate).Count;
                    if (count >= Math.Max(3, _arc.Count - 2))
                    {
                        spec = candidate;
                        strict = parsed.Strict;
                    }
                    else note = $"it had {count} slides instead of {_arc.Count}";
                }
                if (spec is null) _stats.Retries++;
            }
        }
        catch (HarnessException exception) when (!_ct.IsCancellationRequested)
        {
            _warnings.Add("The model's window is too small to write the whole deck in one reply: " + exception.Message);
        }

        if (spec is null)
        {
            _warnings.Add("The model could not return the whole deck as one reply, so it was written slide by slide instead.");
            return await GuidedAsync().ConfigureAwait(false);
        }

        bool useArc = !oneShot;
        var (draftIssues, _) = Evaluate(spec, useArc);
        spec = await RepairWholeAsync(spec, draftIssues, useArc).ConfigureAwait(false);
        return new Draft(spec, raw, strict, useArc, oneShot ? HarnessMode.OneShot : HarnessMode.Arc, draftIssues);
    }

    /// <summary>Sends the lint findings and the whole spec back and applies the replacement slides the model returns. A pass is kept only when
    /// the deck ends up with fewer findings than before.</summary>
    private async Task<JsonObject> RepairWholeAsync(JsonObject spec, List<LintIssue> issues, bool useArc)
    {
        for (int pass = 1; pass <= _options.MaxRepairPasses && issues.Count > 0; pass++)
        {
            SetStep("repair", pass, _options.MaxRepairPasses, $"Pass {pass}: fixing {issues.Count} findings");
            int before = issues.Count;
            var current = spec;
            var currentIssues = issues;
            LlmReply reply;
            try { reply = await ChatAsync(level => Prompts.ArcRepair(_brief, _story, current, currentIssues, level)).ConfigureAwait(false); }
            catch (HarnessException) when (!_ct.IsCancellationRequested)
            {
                _warnings.Add("The model's window could not hold the repair request; the draft was kept as it was.");
                break;
            }

            var parsed = SpecJson.Parse(reply.Text);
            if (parsed.Spec?["fixes"] is not JsonArray { Count: > 0 } fixes)
            {
                _stats.Retries++;
                _repairs.Add(new RepairRecord(pass, before, before, false, [], Codes(issues)));
                break;
            }

            var (candidate, touched) = ApplyFixes(spec, fixes);
            var (after, _) = Evaluate(candidate, useArc);
            bool accepted = after.Count < before;
            _repairs.Add(new RepairRecord(pass, before, after.Count, accepted, touched, Codes(after)));
            if (!accepted) break;
            spec = candidate;
            issues = after;
        }
        return spec;
    }

    private static bool TryIndex(JsonNode? node, out int index)
    {
        index = -1;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<int>(out index)) return true;
        if (value.TryGetValue<double>(out double number) && number == Math.Floor(number)) { index = (int)number; return true; }
        return value.TryGetValue<string>(out string? text) && int.TryParse(text, out index);
    }

    /// <summary>Replacements first, then deletions from the last slide backwards so the other indexes stay valid.</summary>
    private static (JsonObject Spec, List<int> Touched) ApplyFixes(JsonObject spec, JsonArray fixes)
    {
        var slides = SpecJson.SlideObjects(spec);
        var deletions = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        var touched = new List<int>();
        foreach (var fix in fixes.OfType<JsonObject>())
        {
            if (!TryIndex(fix["index"], out int index) || index < 0 || index >= slides.Count || fix["slide"] is not JsonObject replacement) continue;
            if (!touched.Contains(index)) touched.Add(index);
            bool delete = replacement["delete"] is JsonValue flag && flag.TryGetValue<bool>(out bool value) && value;
            if (delete)
            {
                deletions.Add(index);
                continue;
            }
            var copy = (JsonObject)replacement.DeepClone();
            copy.Remove("delete");
            if (copy["layout"] is null && slides[index]["layout"] is { } layout) copy["layout"] = layout.DeepClone();
            slides[index] = copy;
        }
        foreach (int index in deletions) slides.RemoveAt(index);
        return (SpecJson.WithSlides(spec, slides), touched);
    }
}
