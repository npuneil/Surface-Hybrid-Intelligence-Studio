using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Ir;

namespace Forge.Core.Spec;

/// <summary>One lint finding. <see cref="Slide"/> is the 0-based slide index, -1 for a deck-level finding. <see cref="Msg"/> is the repair
/// instruction that goes back to the model, so its wording is part of the contract. The field names are the Python Forge's (slide, code, msg).</summary>
public sealed record LintIssue(int Slide, string Code, string Msg)
{
    public JsonObject ToJson() => new() { ["slide"] = Slide, ["code"] = Code, ["msg"] = Msg };

    /// <summary>Reads one entry of a metrics.json "lint_remaining" list.</summary>
    public static LintIssue FromJson(JsonNode node) =>
        new(node["slide"]!.GetValue<int>(), node["code"]!.GetValue<string>(), node["msg"]!.GetValue<string>());
}

/// <summary>How much a finding matters. A <see cref="Hard"/> finding is a wrong or missing claim, structure or required content; a
/// <see cref="Soft"/> one is polish a model can usually do (a long title or takeaway, too few icons); a <see cref="Note"/> is reported
/// but never worth a model call on its own, because a rewrite rarely fixes it (the slide is a few words over, or the renderer shrank some text).</summary>
public enum LintSeverity { Hard, Soft, Note }

/// <summary>Deterministic deck lint. Produces targeted, per-slide repair instructions. Every check is something a careful human reviewer
/// would flag; they come from the first-round findings (frontier decks and a blind judge) and from the brief itself (facts, guardrails,
/// required topics).</summary>
public static class Lint
{
    /// <summary>The words per slide the prompt asks for and the lint flags by default.</summary>
    public const int WordBudget = 32;

    private static readonly HashSet<string> SoftCodes = new(StringComparer.Ordinal) { "title_long", "title_label", "takeaway_long", "icons", "woven_analogy" };
    private static readonly HashSet<string> NoteCodes = new(StringComparer.Ordinal) { "words", "fit" };

    public static LintSeverity SeverityOf(string code) =>
        NoteCodes.Contains(code) ? LintSeverity.Note : SoftCodes.Contains(code) ? LintSeverity.Soft : LintSeverity.Hard;

    /// <summary>How much a set of findings weighs: a hard finding counts three times a soft one, so a rewrite that removes an invented number
    /// and adds a few words is still an improvement.</summary>
    public static int Cost(IEnumerable<LintIssue> issues) => issues.Sum(i => SeverityOf(i.Code) == LintSeverity.Hard ? 3 : 1);

    private static readonly HashSet<string> ContentSkip = new(StringComparer.Ordinal) { "title", "cta", "section" };
    private static readonly HashSet<string> BoxTypes = new(StringComparer.Ordinal) { "box", "pill", "circle", "hexagon", "chevron" };
    private static readonly HashSet<string> WovenLayouts = new(StringComparer.Ordinal) { "kpis", "flow", "canvas", "bars", "stack", "matrix", "compare" };

    private static readonly Regex Cue = new(@"\b(like|think of|imagine|as if)\b", Guardrails.Options);
    private static readonly Regex SingleStream = new(@"\b1 stream|single", Guardrails.Options);
    private static readonly Regex MultiStream = new(@"\b[2-9] streams|concurrent|aggregate", Guardrails.Options);

    /// <summary>Lints a deck spec.</summary>
    /// <param name="spec">The deck spec ({"slides": [...]}).</param>
    /// <param name="brief">What the deck must cover and respect.</param>
    /// <param name="renderSummary">What drawing each slide recorded (words, icons, overflows); a slide with no entry counts as 0 words and 0 icons.</param>
    /// <param name="arc">Optional storyline: the (slot, layout) each slide must have. Only applied when it has exactly one entry per slide.</param>
    /// <param name="reservedSlides">Slides the harness adds after linting (the provenance slide). They count toward the deck size and satisfy the "made_on_device" topic.</param>
    /// <param name="wordLimit">Visible words per slide above which a slide is flagged. The harness raises it, because the model aims at about 28 and lands near 40.</param>
    public static List<LintIssue> Run(JsonObject spec, Brief brief, IReadOnlyList<SlideSummary> renderSummary,
        IReadOnlyList<(string Slot, string Layout)>? arc = null, int reservedSlides = 0, int wordLimit = WordBudget)
    {
        var slides = ((spec["slides"] as JsonArray) ?? []).Select(s => s as JsonObject ?? new JsonObject()).ToList();
        var layouts = slides.Select(s => Py.StrGet(s, "layout", "").ToLowerInvariant()).ToList();
        var allowed = Grade.AllowedNumbers(brief);
        string allowedText = Script.AllowedText(brief);
        var summaries = new Dictionary<int, SlideSummary>();
        foreach (var r in renderSummary) summaries[r.Index] = r;
        var issues = new List<LintIssue>();
        void Add(int slide, string code, string msg) => issues.Add(new LintIssue(slide, code, msg));

        // Deck-level
        int lo = brief.SlideMin, hi = brief.SlideMax;
        int n = slides.Count + reservedSlides;
        if (!(lo <= n && n <= hi))
            Add(-1, "count", $"deck must have {lo - reservedSlides}-{hi - reservedSlides} slides (has {slides.Count}); "
                + "delete or merge the weakest slide by returning it with \"delete\": true");
        if (layouts.Count == 0 || layouts[0] != "title") Add(0, "first", "slide 0 must use layout 'title'");
        if (layouts.Count == 0 || layouts[^1] != "cta") Add(slides.Count - 1, "last", "last slide must use layout 'cta'");
        var analogy = Enumerable.Range(0, slides.Count).Where(i => layouts[i] == "analogy").ToList();
        if (analogy.Count < 2)
            Add(-1, "analogy_count", "deck needs exactly two 'analogy' slides; convert the best-fitting slide using the ANALOGY BANK");
        for (int k = 1; k < analogy.Count; k++)
            if (analogy[k] == analogy[k - 1] + 1)
                Add(analogy[k], "analogy_adjacent", "two analogy slides are back to back; make this one a different layout or move the analogy into a takeaway");
        var cueSlides = Enumerable.Range(0, slides.Count)
            .Where(i => layouts[i] is not ("analogy" or "title")
                && Cue.IsMatch($"{Py.StrGet(slides[i], "title", "")} {Py.StrGet(slides[i], "takeaway", "")}"))
            .ToList();
        if (cueSlides.Count < 2)
            foreach (int slide in Enumerable.Range(0, slides.Count).Where(j => WovenLayouts.Contains(layouts[j]) && !cueSlides.Contains(j)).Take(2 - cueSlides.Count))
                Add(slide, "woven_analogy", "weave a short, accurate analogy into the takeaway using 'like' (<= 12 words)");
        string visible = string.Join(" ", SpecText.VisibleStrings(spec).Select(t => t.Text)).ToLowerInvariant();
        foreach (var (topic, keywords) in brief.RequiredTopics)
        {
            if (topic == "made_on_device" && reservedSlides != 0) continue;
            if (!keywords.Any(k => visible.Contains(k.ToLowerInvariant(), StringComparison.Ordinal)))
                Add(-1, "topic", $"topic '{topic}' is not visible on any slide; work one of these words into a fitting "
                    + $"slide's label or takeaway: {string.Join(", ", keywords.Take(4))}");
        }

        // Slide-level
        for (int i = 0; i < slides.Count; i++)
        {
            var s = slides[i];
            string lay = layouts[i];
            summaries.TryGetValue(i, out var r);
            var data = s["data"] as JsonObject ?? new JsonObject();
            if (arc is not null && arc.Count > 0 && slides.Count == arc.Count && lay != arc[i].Layout)
                Add(i, "arc_layout", $"use layout '{arc[i].Layout}' for the '{arc[i].Slot}' slot");
            if (lay is "memory" or "hub" or "statement")
                Add(i, "layout_banned", $"replace the '{lay}' layout ({(lay == "memory" ? "use kpis" : "use a layout that encodes the idea")})");
            if (r is not null && r.Words > wordLimit)
                Add(i, "words", $"{r.Words} visible words; cut to <= 28 (shorter details, fewer items)");
            if (r is not null && (r.Overflows != 0 || r.Shrunk != 0))
                Add(i, "fit", "some text does not fit its shape; shorten the longest labels/details");
            int titleWords = Py.WordCount(Py.StrOrEmpty(s["title"]));
            if (titleWords > 7) Add(i, "title_long", $"title has {titleWords} words; <= 7");
            else if (lay != "title" && titleWords <= 2)
                Add(i, "title_label", "title reads as a label; rewrite as an insight claim with a verb (<= 7 words)");
            if (Py.WordCount(Py.StrOrEmpty(s["takeaway"])) > 14) Add(i, "takeaway_long", "takeaway <= 12 words");
            var strings = SpecText.SlideStrings(s);
            var bad = new List<string>();
            foreach (var (_, text) in strings)
                foreach (double v in SpecText.Numbers(text))
                    if (!allowed.Contains(v) && !allowed.Contains(Py.Round(v))) bad.Add(Py.FormatG(v));
            if (bad.Count > 0)
                Add(i, "numbers", $"numbers not in ALLOWED NUMBERS: {string.Join(", ", bad.Distinct().Order(StringComparer.Ordinal))}; "
                    + "replace with allowed numbers or words");
            string notes = Py.StrOrEmpty(s["notes"]);
            string blob = string.Join(" ", strings.Select(t => t.Text).Append(notes));
            foreach (var (name, pattern) in Guardrails.Patterns.Concat(Guardrails.ExtraBanned))
            {
                var m = Guardrails.Compile(pattern).Match(blob);
                if (m.Success) Add(i, "guardrail", $"remove '{m.Value}' ({name}) from slide and notes; state the positive version");
            }
            foreach (string text in strings.Select(t => t.Text).Append(notes))
                if (Script.Find(text, allowedText) is { Length: > 0 } foreign)
                {
                    Add(i, "script", $"remove the non-English text \"{foreign}\"; write every word in English");
                    break;
                }
            if (Py.Strip(notes).Length == 0) Add(i, "notes", "add 2-3 sentences of speaker notes (<= 45 words)");
            if (!ContentSkip.Contains(lay) && (r?.Icons ?? 0) < 3 && lay is not ("bars" or "analogy"))
                Add(i, "icons", "show at least 3 icons (add icon names to items)");
            if (lay == "bars")
            {
                var bars = (data["bars"] is JsonArray list ? list.OfType<JsonObject>() : []).ToList();
                var labels = bars.Select(b => Py.StrGet(b, "label", "")).ToList();
                if (bars.Count < 2) Add(i, "bars", "a bar chart needs 2-6 bars of the same unit");
                if (!(Py.KindOf(data["highlight"]) == Py.Kind.Str && labels.Contains(data["highlight"]!.GetValue<string>())))
                    Add(i, "bars_highlight", "'highlight' must equal one bar label exactly");
                if (labels.Any(SingleStream.IsMatch) && labels.Any(MultiStream.IsMatch))
                    Add(i, "bars_mixed", "chart mixes single-stream and aggregate throughput; keep one measurement type");
            }
            if (lay == "analogy" && Py.Len(data["pairs"]) < 4) Add(i, "analogy_pairs", "use 4 concept/analog pairs");
            if (lay == "canvas")
            {
                var elements = (data["elements"] is JsonArray list ? list.OfType<JsonObject>() : []).ToList();
                if (elements.Count < 10)
                    Add(i, "canvas_sparse", $"canvas has {elements.Count} elements; build a connected diagram with 12-20 (see EXEMPLAR)");
                var floating = FloatingBoxes(elements);
                if (floating.Count > 0)
                    Add(i, "canvas_floating", $"boxes not touched by any arrow/line: {string.Join(", ", floating.Take(5))}; connect them "
                        + "(arrow endpoints on the box edge) or remove them");
                var outside = elements.Where(e =>
                    {
                        bool stroke = IsStroke(e);
                        return Py.Float(e["x"]) + (stroke ? 0 : Py.Float(e["w"], 2)) > 12.05 || Py.Float(e["y"]) + (stroke ? 0 : Py.Float(e["h"], 1)) > 6.05;
                    })
                    .Select(e => Py.Str(Py.Truthy(e["label"]) ? e["label"] : e["type"]))
                    .ToList();
                if (outside.Count > 0) Add(i, "canvas_bounds", $"elements outside the 12 x 6 grid: {string.Join(", ", outside.Take(4))}");
            }
            if (lay == "cta")
            {
                if (Py.Len(data["steps"]) < 3) Add(i, "cta_steps", "CTA needs 3 steps");
                if (Py.Strip(Py.StrOrEmpty(data["ask"])).Length == 0)
                    Add(i, "cta_ask", "CTA needs an 'ask' sentence with the exact pilot size and duration");
            }
        }
        return issues;
    }

    /// <summary>Findings per slide index (-1 for deck-level), used to see which slides a repair round fixed.</summary>
    public static Dictionary<int, int> PerSlideCounts(IEnumerable<LintIssue> issues)
    {
        var counts = new Dictionary<int, int>();
        foreach (var issue in issues) counts[issue.Slide] = counts.GetValueOrDefault(issue.Slide) + 1;
        return counts;
    }

    private static bool IsStroke(JsonObject e) => Py.StrGet(e, "type", "").ToLowerInvariant() is "arrow" or "line";

    /// <summary>Boxes that no arrow/line endpoint touches (within 0.35 grid units of the box edge).</summary>
    private static List<string> FloatingBoxes(List<JsonObject> elements)
    {
        var ends = new List<(double X, double Y)>();
        foreach (var e in elements.Where(IsStroke))
        {
            double x = Py.Float(e["x"]), y = Py.Float(e["y"]);
            ends.Add((x, y));
            ends.Add((Py.Float(e["x2"], x + 2), Py.Float(e["y2"], y)));
        }
        var output = new List<string>();
        foreach (var e in elements)
        {
            if (!BoxTypes.Contains(Py.StrGet(e, "type", "box").ToLowerInvariant())) continue;
            double x = Py.Float(e["x"]), y = Py.Float(e["y"]), w = Py.Float(e["w"], 2), h = Py.Float(e["h"], 1);
            const double margin = 0.35;
            if (!ends.Any(p => x - margin <= p.X && p.X <= x + w + margin && y - margin <= p.Y && p.Y <= y + h + margin))
                output.Add(Py.Truthy(e["label"]) ? Py.Str(e["label"]) : "?");
        }
        return output;
    }
}
