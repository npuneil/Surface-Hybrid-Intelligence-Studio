using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Ir;

namespace Forge.Core.Spec;

/// <summary>The automated rubric scores, each already rounded to one decimal.</summary>
public sealed record GradeScores(double VisualRichness, double TextLightness, double Analogy, double BriefCoverage,
    double ClaimDiscipline, double Structure);

/// <summary>The automated half of a deck's grade: the 100-point total, its letter, the six scores and the evidence behind them
/// (<see cref="Detail"/> has the same shape as "grade.detail" in the Python Forge's metrics.json).</summary>
public sealed record GradeResult(double AutoScore, string AutoGrade, GradeScores Scores, JsonObject Detail)
{
    /// <summary>Python computes the analogy score as an int when two or more slides carry a cue (min(5, 2.5 * n) returns the int 5),
    /// so recorded metrics say 15 where every other score is written 15.0.</summary>
    internal bool AnalogyIsInt { get; init; }

    /// <summary>The "grade" object of metrics.json: auto_score, auto_grade, scores, detail.</summary>
    public JsonObject ToJson() => new()
    {
        ["auto_score"] = Py.FloatNode(AutoScore),
        ["auto_grade"] = AutoGrade,
        ["scores"] = new JsonObject
        {
            ["visual_richness"] = Py.FloatNode(Scores.VisualRichness),
            ["text_lightness"] = Py.FloatNode(Scores.TextLightness),
            ["analogy"] = AnalogyIsInt ? JsonValue.Create((int)Scores.Analogy) : Py.FloatNode(Scores.Analogy),
            ["brief_coverage"] = Py.FloatNode(Scores.BriefCoverage),
            ["claim_discipline"] = Py.FloatNode(Scores.ClaimDiscipline),
            ["structure"] = Py.FloatNode(Scores.Structure),
        },
        ["detail"] = Detail.DeepClone(),
    };
}

/// <summary>Automated 100-point deck rubric. Thresholds were fixed before any model output was graded.
/// Rubric 1.0.1: guardrail checks are negation-aware and the Copilot+ rule targets 'Copilot+ PC/certification' claims.</summary>
public static class Grade
{
    public const string RubricVersion = "1.0.1";

    /// <summary>Points available per criterion; they add up to 100.</summary>
    public static IReadOnlyDictionary<string, int> Rubric { get; } = new Dictionary<string, int>
    {
        ["visual_richness"] = 25, ["text_lightness"] = 20, ["analogy"] = 15, ["brief_coverage"] = 15,
        ["claim_discipline"] = 15, ["structure"] = 10,
    };

    /// <summary>Average words per slide at which text lightness is full marks, and at which it is zero.</summary>
    public const double WordsTarget = 30.0, WordsZero = 60.0;

    public static Regex AnalogyCues { get; } = new(@"\b(like|think of|imagine|as if|analogy|works as|acts as|is your)\b", Guardrails.Options);

    /// <summary>Layouts that draw the idea as a picture rather than as sentences (every layout but "statement").</summary>
    public static IReadOnlySet<string> Infographic { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "stack", "flow", "hub", "compare", "kpis", "analogy", "bars", "timeline", "matrix", "memory", "cta", "canvas",
    };

    /// <summary>Small whole numbers (0 to 10) that need no source: step counts, "two analogies", "3 layers".</summary>
    public static IReadOnlySet<double> OrdinalOk { get; } = Enumerable.Range(0, 11).Select(i => (double)i).ToHashSet();

    private static readonly (double Cut, string Grade)[] Cuts =
        [(97, "A+"), (93, "A"), (90, "A-"), (87, "B+"), (83, "B"), (80, "B-"), (77, "C+"), (73, "C"), (70, "C-"), (67, "D+"), (60, "D")];

    public static string Letter(double score)
    {
        foreach (var (cut, grade) in Cuts)
            if (score >= cut) return grade;
        return "F";
    }

    /// <summary>Numbers a slide may quote: the ones the brief states plus the small whole numbers.</summary>
    public static HashSet<double> AllowedNumbers(Brief brief)
    {
        var allowed = SpecText.BriefNumbers(brief);
        allowed.UnionWith(OrdinalOk);
        return allowed;
    }

    /// <summary>Grades a deck. <paramref name="renderSummary"/> is what drawing the deck recorded per slide (words, icons, overflows, fallbacks);
    /// <paramref name="strictJson"/> says whether the model's JSON parsed without repair.</summary>
    public static GradeResult Run(JsonObject spec, IReadOnlyList<SlideSummary> renderSummary, Brief brief, bool strictJson)
    {
        var slides = ((spec["slides"] as JsonArray) ?? []).Select(s => s as JsonObject ?? new JsonObject()).ToList();
        int n = slides.Count;
        var layouts = slides.Select(s => Py.StrGet(s, "layout", "").ToLowerInvariant()).ToList();
        var content = Enumerable.Range(0, n).Where(i => layouts[i] is not ("title" or "cta" or "section")).ToList();
        var detail = new JsonObject();

        // Visual richness (25)
        var infographic = content.Where(i => Infographic.Contains(layouts[i])).ToList();
        double share = (double)infographic.Count / Math.Max(1, content.Count);
        int variety = infographic.Select(i => layouts[i]).Distinct().Count();
        var contentSet = content.ToHashSet();
        var icons = renderSummary.Where(r => contentSet.Contains(r.Index)).Select(r => r.Icons).ToList();
        double iconAvg = (double)icons.Sum() / Math.Max(1, icons.Count);
        int canvases = slides.Count(s => Py.Str(s["layout"]).ToLowerInvariant() == "canvas" && Py.Len((s["data"] as JsonObject)?["elements"]) >= 5);
        double vr = 8 * share + 7 * Math.Min(1.0, variety / 6.0) + 5 * Math.Min(1.0, iconAvg / 4.0) + (canvases >= 2 ? 5 : canvases > 0 ? 3 : 0);
        detail["visual_richness"] = new JsonObject
        {
            ["infographic_share"] = Py.FloatNode(Py.Round(share, 2)),
            ["distinct_layouts"] = variety,
            ["icons_per_slide"] = Py.FloatNode(Py.Round(iconAvg, 1)),
            ["canvas_diagrams"] = canvases,
        };

        // Text lightness (20)
        var words = renderSummary.Select(r => r.Words).ToList();
        double avgWords = (double)words.Sum() / Math.Max(1, words.Count);
        double tl = 20 * Math.Max(0.0, Math.Min(1.0, (WordsZero - avgWords) / (WordsZero - WordsTarget)));
        detail["text_lightness"] = new JsonObject
        {
            ["avg_words_per_slide"] = Py.FloatNode(Py.Round(avgWords, 1)),
            ["max_words"] = words.Count > 0 ? words.Max() : 0,
        };

        // Analogy (15)
        int analogySlides = layouts.Count(l => l == "analogy");
        int cueSlides = 0;
        for (int i = 0; i < n; i++)
        {
            if (layouts[i] == "analogy") continue;
            string head = Py.StrOrEmpty(slides[i]["title"]) + " " + Py.StrOrEmpty(slides[i]["takeaway"])
                + " " + Py.StrOrEmpty((slides[i]["data"] as JsonObject)?["text"]);
            if (AnalogyCues.IsMatch(head)) cueSlides++;
        }
        double an = (analogySlides >= 2 ? 10 : analogySlides == 1 ? 6 : 0) + Math.Min(5, 2.5 * cueSlides);
        detail["analogy"] = new JsonObject { ["analogy_slides"] = analogySlides, ["analogy_cue_slides"] = cueSlides };

        // Brief coverage (15)
        var visible = SpecText.VisibleStrings(spec);
        var notes = SpecText.NotesText(spec);
        string visBlob = string.Join(" ", visible.Select(t => t.Text)).ToLowerInvariant();
        string noteBlob = string.Join(" ", notes).ToLowerInvariant();
        var topics = brief.RequiredTopics;
        double hit = 0;
        var missing = new JsonArray();
        foreach (var (topic, keywords) in topics)
        {
            if (keywords.Any(k => visBlob.Contains(k.ToLowerInvariant(), StringComparison.Ordinal))) hit += 1;
            else if (keywords.Any(k => noteBlob.Contains(k.ToLowerInvariant(), StringComparison.Ordinal)))
            {
                hit += 0.5;
                missing.Add(topic + " (notes only)");
            }
            else missing.Add(topic);
        }
        double bc = 15 * hit / Math.Max(1, topics.Count);
        detail["brief_coverage"] = new JsonObject
        {
            ["covered"] = Py.FloatNode(Py.Round(hit, 1)),
            ["topics"] = topics.Count,
            ["missing"] = missing,
        };

        // Claim discipline (15): every on-canvas number traceable to the brief; guardrails respected
        var allowed = AllowedNumbers(brief);
        int found = 0;
        var untraced = new List<(int Slide, double Value, string Text)>();
        foreach (var (index, _, text) in visible)
            foreach (double v in SpecText.Numbers(text))
            {
                found++;
                if (!allowed.Contains(v) && !allowed.Contains(Py.Round(v))) untraced.Add((index + 1, v, Py.Head(text, 60)));
            }
        double trace = found > 0 ? 1 - (double)untraced.Count / found : 0.6;
        var allTexts = visible.Select(t => t.Text).Concat(notes).ToList();
        var violations = new JsonArray();
        foreach (var (name, pattern) in Guardrails.Patterns)
        {
            var hits = Guardrails.AffirmativeHits(pattern, allTexts);
            if (hits.Count > 0) violations.Add(new JsonObject { ["rule"] = name, ["text"] = hits[0] });
        }
        double cd = Math.Max(0.0, 15 * trace - 4 * violations.Count);
        detail["claim_discipline"] = new JsonObject
        {
            ["numbers_on_canvas"] = found,
            ["untraceable"] = new JsonArray([.. untraced.Take(12).Select(u => (JsonNode?)new JsonObject
            {
                ["slide"] = u.Slide,
                ["value"] = Py.FloatNode(u.Value),
                ["text"] = u.Text,
            })]),
            ["guardrail_violations"] = violations,
        };

        // Structure (10)
        int lo = brief.SlideMin, hi = brief.SlideMax;
        int noted = notes.Count(t => Py.Strip(t).Length > 0);
        int fallbacks = renderSummary.Sum(r => r.Issues.Count(i => i.Kind == "fallback"));
        int overflows = renderSummary.Sum(r => r.Overflows);
        int fixedPoints = (strictJson ? 2 : 0) + (lo <= n && n <= hi ? 2 : 0)
            + (n > 0 && layouts[0] == "title" ? 1 : 0) + (n > 0 && layouts[^1] == "cta" ? 1 : 0);
        double st = fixedPoints + 2 * Math.Min(1.0, (double)noted / Math.Max(1, n) / 0.8) + (fallbacks == 0 ? 1 : 0) + (overflows == 0 ? 1 : 0);
        detail["structure"] = new JsonObject
        {
            ["strict_json"] = strictJson,
            ["slides"] = n,
            ["notes_slides"] = noted,
            ["fallbacks"] = fallbacks,
            ["overflows"] = overflows,
            ["shrunk_texts"] = renderSummary.Sum(r => r.Shrunk),
        };

        var scores = new GradeScores(Py.Round(vr, 1), Py.Round(tl, 1), Py.Round(an, 1), Py.Round(bc, 1), Py.Round(cd, 1), Py.Round(st, 1));
        double total = Py.Round(0 + scores.VisualRichness + scores.TextLightness + scores.Analogy + scores.BriefCoverage
            + scores.ClaimDiscipline + scores.Structure, 1);
        return new GradeResult(total, Letter(total), scores, detail) { AnalogyIsInt = cueSlides >= 2 };
    }
}
