using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Ir;
using Forge.Core.Spec;

namespace Forge.Tests;

/// <summary>Lint and grade parity with the original Python Forge. Owned by the lint/grade port.
/// 1. The six recorded decks: Grade must reproduce the "grade" Python stored in metrics.json (text for text) and Lint its "lint_remaining".
/// 2. fixtures\oracle\lint-grade.json: what the original forge.lint and forge.grade returned for the same decks after targeted mutations
///    (a long title, a banned claim, a sparse canvas, ...). The mutations are replayed below and the mutated inputs are checked against a
///    hash Python recorded, so the oracle needs no Python at run time.
/// 3. Python's own answers for the primitives the port reimplements (round, %g, repr, float(), whitespace, negation matching), and unit checks.</summary>
public static class GradeParity
{
    public static int Run()
    {
        var check = new Check("grade");
        var oracle = JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures.Root, "oracle", "lint-grade.json")))!.AsObject();
        var arcs = oracle["arcs"]!.AsObject().ToDictionary(p => p.Key,
            p => p.Value!.AsArray().Select(a => ((string)a![0]!, (string)a[1]!)).ToList());
        var decks = Fixtures.Decks().Select(Deck.Load).ToDictionary(d => d.Name);

        RecordedDecks(check, decks.Values, arcs);
        OracleReplay(check, oracle, decks, arcs);
        PythonPrimitives(check, oracle["py"]!.AsObject());
        PatternSources(check, oracle["py"]!["patterns"]!.AsObject());
        UnitChecks(check);
        return check.Finish();
    }

    // ------------------------------------------------------------------------------------------------ recorded decks

    private static void RecordedDecks(Check check, IEnumerable<Deck> decks, Dictionary<string, List<(string, string)>> arcs)
    {
        foreach (var d in decks)
        {
            var result = Grade.Run(d.Spec, d.Summary.Select(ToSummary).ToList(), Brief.FromJson(d.GradeBrief), d.Strict);
            var got = result.ToJson();
            var want = d.Metrics["grade"]!.AsObject();
            check.That(Math.Abs(result.AutoScore - Num(want["auto_score"])) <= 0.05, $"{d.Name}: auto_score",
                $"{result.AutoScore} vs recorded {want["auto_score"]}");
            check.That(result.AutoGrade == (string)want["auto_grade"]!, $"{d.Name}: auto_grade", $"{result.AutoGrade} vs recorded {want["auto_grade"]}");
            foreach (var (name, recorded) in want["scores"]!.AsObject())
                check.That(Math.Abs(Num(got["scores"]![name]) - Num(recorded)) <= 0.05, $"{d.Name}: score {name}",
                    $"{got["scores"]![name]} vs recorded {recorded}");
            foreach (var (name, recorded) in want["detail"]!.AsObject())
            {
                string? diff = FirstDiff(recorded, got["detail"]![name], name);
                check.That(diff is null, $"{d.Name}: detail {name}", diff ?? "");
            }
            check.That(want.ToJsonString() == got.ToJsonString(), $"{d.Name}: grade JSON is the same text as metrics.json",
                Clip(got.ToJsonString()) + " vs " + Clip(want.ToJsonString()));

            if (d.LintRemaining is null)
            {
                Console.WriteLine($"  note: {d.Name} predates the repair loop (no lint_remaining); its lint is covered by the oracle baseline runs");
                continue;
            }
            var lint = Lint.Run(d.LintSpec, Brief.FromJson(d.Brief), d.LintSummary.Select(ToSummary).ToList(), arcs[d.Name], d.Reserved);
            var gotLint = new JsonArray([.. lint.Select(i => (JsonNode?)i.ToJson())]);
            string? lintDiff = FirstDiff(d.LintRemaining, gotLint, "lint_remaining");
            check.That(lintDiff is null && d.LintRemaining.ToJsonString() == gotLint.ToJsonString(), $"{d.Name}: lint_remaining ({d.LintRemaining.Count} issues)",
                lintDiff ?? "same content, different text");
        }
    }

    // ------------------------------------------------------------------------------------------------ oracle replay

    private static void OracleReplay(Check check, JsonObject oracle, Dictionary<string, Deck> decks, Dictionary<string, List<(string, string)>> arcs)
    {
        var msgs = oracle["msgs"]!.AsArray().Select(m => (string)m!).ToList();
        var mutations = Mutations();
        var seen = new HashSet<string>();
        check.That(oracle["rubric"]!.GetValue<string>() == Grade.RubricVersion, "oracle was recorded against the same rubric version");
        foreach (var run in oracle["runs"]!.AsArray())
        {
            string deck = (string)run!["f"]!, name = (string)run["m"]!, label = $"{deck} / {name}";
            seen.Add(name);
            if (!mutations.TryGetValue(name, out var apply))
            {
                check.That(false, $"{label}: no C# replay for this mutation");
                continue;
            }
            var c = new Ctx(decks[deck]);
            try { apply(c); }
            catch (NotApplicable)
            {
                check.That(false, $"{label}: mutation is not applicable in C# although Python applied it");
                continue;
            }
            bool sameInputs = c.Arc == (bool)run["arc"]! && c.Reserved == (int)run["reserved"]! && c.Strict == (bool)run["strict"]! && c.Hash() == (string)run["h"]!;
            check.That(sameInputs, $"{label}: the replayed mutation produces the inputs Python saw");
            if (!sameInputs) continue;

            var summaries = c.Summary.Select(ToSummary).ToList();
            var brief = Brief.FromJson(c.Brief);
            var want = run["lint"]!.AsArray().Select(t => new LintIssue((int)t![0]!, (string)t[1]!, msgs[(int)t[2]!])).ToList();
            var got = Lint.Run(c.Spec, brief, summaries, c.Arc ? arcs[deck] : null, c.Reserved);
            check.That(want.SequenceEqual(got), $"{label}: lint issues", LintDiff(want, got));

            var gotGrade = Grade.Run(c.Spec, summaries, brief, c.Strict).ToJson();
            string? diff = FirstDiff(run["grade"], gotGrade, "grade");
            check.That(diff is null && run["grade"]!.ToJsonString() == gotGrade.ToJsonString(), $"{label}: grade", diff ?? "same content, different text");
        }
        var unused = mutations.Keys.Where(k => !seen.Contains(k)).ToList();
        check.That(unused.Count == 0, "every C# mutation is exercised by the oracle", string.Join(", ", unused));
        check.That(seen.Count >= 25, $"oracle has at least 25 distinct mutations (has {seen.Count})");
        Console.WriteLine($"  oracle: {oracle["runs"]!.AsArray().Count} runs of {seen.Count} mutations, {msgs.Count} distinct lint messages");
    }

    private static string LintDiff(List<LintIssue> want, List<LintIssue> got)
    {
        if (want.SequenceEqual(got)) return "";
        int i = 0;
        while (i < want.Count && i < got.Count && want[i] == got[i]) i++;
        string Show(List<LintIssue> list) => i < list.Count ? $"[{list[i].Slide}] {list[i].Code}: {list[i].Msg}" : "(none)";
        return Clip($"{want.Count} expected vs {got.Count} actual issues; first difference at #{i}: expected {Show(want)} / actual {Show(got)}");
    }

    /// <summary>The first place two JSON values differ (numbers compare by their text, so 15 and 15.0 differ), or null.</summary>
    private static string? FirstDiff(JsonNode? want, JsonNode? got, string path)
    {
        switch (want)
        {
            case JsonObject wo:
                if (got is not JsonObject go) return $"{path}: expected an object, got {Clip(got?.ToJsonString() ?? "null")}";
                foreach (var (key, value) in wo)
                {
                    if (!go.TryGetPropertyValue(key, out var other)) return $"{path}.{key}: missing";
                    string? diff = FirstDiff(value, other, $"{path}.{key}");
                    if (diff is not null) return diff;
                }
                string? extra = go.Select(p => p.Key).FirstOrDefault(k => !wo.ContainsKey(k));
                return extra is null ? null : $"{path}.{extra}: unexpected";
            case JsonArray wa:
                if (got is not JsonArray ga) return $"{path}: expected a list, got {Clip(got?.ToJsonString() ?? "null")}";
                for (int i = 0; i < Math.Min(wa.Count, ga.Count); i++)
                {
                    string? diff = FirstDiff(wa[i], ga[i], $"{path}[{i}]");
                    if (diff is not null) return diff;
                }
                return wa.Count == ga.Count ? null : $"{path}: expected {wa.Count} items, got {ga.Count}";
            default:
                string a = want?.ToJsonString() ?? "null", b = got?.ToJsonString() ?? "null";
                return a == b ? null : $"{path}: expected {Clip(a)}, got {Clip(b)}";
        }
    }

    private static string Clip(string text) => text.Length <= 400 ? text : text[..400] + "...";

    private static double Num(JsonNode? node) => ParseDouble(node!.ToJsonString());

    private static SlideSummary ToSummary(JsonObject r) => new((int)r["index"]!, (string?)r["layout"] ?? "", (int)r["words"]!, (int)r["overflows"]!,
        (int)r["shrunk"]!, (int)r["icons"]!, [.. ((JsonArray)r["issues"]!).Select(i => new Issue((string)i!["kind"]!, (string?)i["detail"] ?? ""))]);

    private static int Idx(JsonObject summary) => (int)summary["index"]!;

    /// <summary>A recorded deck as lint and grade saw it: grade reads the finished deck and the brief plus the facts measured on the device;
    /// lint ran before the provenance slide (the second to last, on "made_on_device" briefs) was added, against the original brief.</summary>
    private sealed class Deck
    {
        public required string Name { get; init; }
        public required JsonObject Brief { get; init; }
        public required JsonObject GradeBrief { get; init; }
        public required JsonObject Spec { get; init; }
        public required List<JsonObject> Summary { get; init; }
        public required JsonObject Metrics { get; init; }
        public required bool Strict { get; init; }
        public required JsonObject LintSpec { get; init; }
        public required List<JsonObject> LintSummary { get; init; }
        public required int Reserved { get; init; }
        public JsonArray? LintRemaining { get; init; }

        public static Deck Load(DeckFixture f)
        {
            var brief = f.Brief ?? throw new InvalidOperationException($"{f.Name} has no brief");
            var gradeBrief = (JsonObject)brief.DeepClone();
            var facts = new JsonArray();
            foreach (var fact in (brief["facts"] as JsonArray ?? []).Concat(f.Metrics["measured_facts"] as JsonArray ?? []))
                facts.Add(fact?.DeepClone());
            gradeBrief["facts"] = facts;

            var summary = ((JsonArray)f.Metrics["render_summary"]!).Select(r => (JsonObject)r!.DeepClone()).ToList();
            var lintSpec = (JsonObject)f.Spec.DeepClone();
            var lintSummary = summary.Select(r => (JsonObject)r.DeepClone()).ToList();
            var lintRemaining = f.Metrics["lint_remaining"] as JsonArray;
            bool provenance = lintRemaining is not null && (brief["required_topics"] as JsonObject)?.ContainsKey("made_on_device") == true;
            if (provenance)
            {
                var slides = (JsonArray)lintSpec["slides"]!;
                int p = Math.Max(1, slides.Count - 2);
                slides.RemoveAt(p);
                lintSummary = lintSummary.Where(r => Idx(r) != p).Select(r => { if (Idx(r) > p) r["index"] = Idx(r) - 1; return r; }).ToList();
            }
            return new Deck
            {
                Name = f.Name, Brief = brief, GradeBrief = gradeBrief, Spec = f.Spec, Summary = summary, Metrics = f.Metrics,
                Strict = f.Metrics["grade"]!["detail"]!["structure"]!["strict_json"]!.GetValue<bool>(),
                LintSpec = lintSpec, LintSummary = lintSummary, Reserved = provenance ? 1 : 0, LintRemaining = lintRemaining,
            };
        }
    }

    // ------------------------------------------------------------------------------------------------ mutations

    private sealed class NotApplicable : Exception { }

    /// <summary>One run's inputs: the lint view of a deck (JSON, like the Python dicts) that a mutation edits before lint and grade read it.</summary>
    private sealed class Ctx
    {
        public JsonObject Spec { get; set; }
        public List<JsonObject> Summary { get; set; }
        public JsonObject Brief { get; }
        public bool Strict { get; set; }
        public bool Arc { get; set; } = true;
        public int Reserved { get; set; }
        public JsonArray Slides => (JsonArray)Spec["slides"]!;

        public Ctx(Deck d)
        {
            Spec = (JsonObject)d.LintSpec.DeepClone();
            Summary = d.LintSummary.Select(r => (JsonObject)r.DeepClone()).ToList();
            Brief = (JsonObject)d.GradeBrief.DeepClone();
            Strict = d.Strict;
            Reserved = d.Reserved;
        }

        /// <summary>First 16 hex digits of the SHA-256 of the inputs as json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=True) prints them.</summary>
        public string Hash()
        {
            var sb = new StringBuilder("{\"arc\":").Append(Arc ? "true" : "false").Append(",\"brief\":");
            Canon(Brief, sb);
            sb.Append(",\"reserved\":").Append(Reserved).Append(",\"spec\":");
            Canon(Spec, sb);
            sb.Append(",\"strict\":").Append(Strict ? "true" : "false").Append(",\"summary\":[");
            for (int i = 0; i < Summary.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Canon(Summary[i], sb);
            }
            sb.Append("]}");
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
        }
    }

    private static void Canon(JsonNode? node, StringBuilder sb)
    {
        switch (Py.KindOf(node))
        {
            case Py.Kind.None: sb.Append("null"); break;
            case Py.Kind.Bool or Py.Kind.Int: sb.Append(node!.ToJsonString()); break;
            case Py.Kind.Float: sb.Append(Py.FloatRepr(double.Parse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture))); break;
            case Py.Kind.Str: Quote(sb, node!.GetValue<string>()); break;
            case Py.Kind.List:
                sb.Append('[');
                var list = (JsonArray)node!;
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Canon(list[i], sb);
                }
                sb.Append(']');
                break;
            default:
                sb.Append('{');
                bool first = true;
                foreach (var (key, value) in ((JsonObject)node!).OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Quote(sb, key);
                    sb.Append(':');
                    Canon(value, sb);
                }
                sb.Append('}');
                break;
        }
    }

    private static void Quote(StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (char ch in text)
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < ' ' || ch > '~') sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        sb.Append('"');
    }

    private static JsonNode? Json(string text) => JsonNode.Parse(text);
    private static JsonObject Sl(Ctx c, int i) => (JsonObject)c.Slides[i]!;
    private static string Lay(JsonObject slide) => Py.StrGet(slide, "layout", "").ToLowerInvariant();
    private static List<int> Content(Ctx c) => Enumerable.Range(0, c.Slides.Count).Where(i => Lay(Sl(c, i)) is not ("title" or "cta" or "section")).ToList();
    private static int Ci(Ctx c, int k) { var content = Content(c); return k < content.Count ? content[k] : throw new NotApplicable(); }
    private static JsonObject S(Ctx c, int k) => Sl(c, Ci(c, k));
    private static List<int> FindAll(Ctx c, string layout) => Enumerable.Range(0, c.Slides.Count).Where(i => Lay(Sl(c, i)) == layout).ToList();
    private static int SummIdx(Ctx c, int slide) { int k = c.Summary.FindIndex(r => Idx(r) == slide); return k >= 0 ? k : throw new NotApplicable(); }
    private static void SetSumm(Ctx c, int slide, params (string Key, JsonNode? Value)[] values) { var r = c.Summary[SummIdx(c, slide)]; foreach (var (k, v) in values) r[k] = v; }
    private static (int Lo, int Hi) Range(Ctx c) => c.Brief["slide_range"] is JsonArray { Count: >= 2 } r ? ((int)r[0]!, (int)r[1]!) : (8, 12);
    private static JsonObject Topics(Ctx c) => (JsonObject)c.Brief["required_topics"]!;
    private static bool IsStroke(JsonNode? element) => element is JsonObject e && Py.StrGet(e, "type", "").ToLowerInvariant() is "arrow" or "line";

    private static JsonNode? Pop(JsonObject o, string key, JsonNode? absent = null)
    {
        if (!o.TryGetPropertyValue(key, out var value)) return absent;
        o.Remove(key);
        return value?.DeepClone();
    }

    private static void DeleteSlide(Ctx c, int i)
    {
        c.Slides.RemoveAt(i);
        c.Summary = c.Summary.Where(r => Idx(r) != i).Select(r => { if (Idx(r) > i) r["index"] = Idx(r) - 1; return r; }).ToList();
    }

    private static void DupSlide(Ctx c, int i)
    {
        c.Slides.Insert(i + 1, Sl(c, i).DeepClone());
        var result = new List<JsonObject>();
        foreach (var r in c.Summary)
        {
            if (Idx(r) > i) { r["index"] = Idx(r) + 1; result.Add(r); continue; }
            result.Add(r);
            if (Idx(r) == i) { var copy = (JsonObject)r.DeepClone(); copy["index"] = i + 1; result.Add(copy); }
        }
        c.Summary = result;
    }

    private static void Grow(Ctx c, int count) { while (c.Slides.Count < count) DupSlide(c, Content(c)[^1]); }
    private static void Shrink(Ctx c, int count) { while (c.Slides.Count > count) DeleteSlide(c, Content(c)[^1]); }

    private static void Put(Ctx c, int slide, string where, string text)
    {
        var s = Sl(c, slide);
        if (where != "data") { s[where] = text; return; }
        if (s["data"] is not JsonObject) s["data"] = new JsonObject();
        ((JsonObject)s["data"]!)["zz_note"] = text;
    }

    private static JsonObject Data(Ctx c, int k)
    {
        var s = S(c, k);
        if (s["data"] is not JsonObject) s["data"] = new JsonObject();
        return (JsonObject)s["data"]!;
    }

    private static void TieWords(Ctx c, int first)
    {
        Grow(c, 12);
        foreach (var r in c.Summary) r["words"] = 31;
        c.Summary[0]["words"] = first;
    }

    private static readonly Regex CueRx = new(@"\b(like|think of|imagine|as if)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] Woven = ["kpis", "flow", "canvas", "bars", "stack", "matrix", "compare"];

    private static void StripCues(Ctx c)
    {
        foreach (var node in c.Slides)
        {
            var s = (JsonObject)node!;
            foreach (string key in new[] { "title", "takeaway" })
                if (s[key] is JsonValue v && v.TryGetValue<string>(out string? text)) s[key] = CueRx.Replace(text, "as");
        }
    }

    private static JsonNode? StripDigits(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (string key in o.Select(p => p.Key).ToList())
                {
                    var v = o[key];
                    if (key is "value" or "capacity" or "size" && Py.IsNumber(v)) o[key] = "n";
                    else if (v is JsonValue s && s.TryGetValue<string>(out string? text)) o[key] = Regex.Replace(text, @"\d", "n");
                    else StripDigits(v);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                    if (a[i] is JsonValue s && s.TryGetValue<string>(out string? text)) a[i] = Regex.Replace(text, @"\d", "n");
                    else StripDigits(a[i]);
                break;
        }
        return node;
    }

    private static JsonObject Canvas(Ctx c, int nth = 0)
    {
        var idx = FindAll(c, "canvas");
        if (nth >= idx.Count) throw new NotApplicable();
        var s = Sl(c, idx[nth]);
        if (s["data"] is not JsonObject d || d["elements"] is not JsonArray) throw new NotApplicable();
        return s;
    }

    private static JsonArray Els(JsonObject canvasSlide) => (JsonArray)((JsonObject)canvasSlide["data"]!)["elements"]!;
    private static JsonObject El(JsonArray elements, int k) => (JsonObject)elements[k]!;

    private static void MakeBars(Ctx c, int k, JsonNode?[] labels, JsonNode? highlight)
    {
        var s = S(c, k);
        s["layout"] = "bars";
        var bars = new JsonArray();
        for (int j = 0; j < labels.Length; j++) bars.Add(new JsonObject { ["label"] = labels[j], ["value"] = 10 * (j + 1) });
        s["data"] = new JsonObject { ["unit"] = "tok/s", ["highlight"] = highlight, ["bars"] = bars };
    }

    private static JsonObject Cta(Ctx c)
    {
        var idx = FindAll(c, "cta");
        if (idx.Count == 0) throw new NotApplicable();
        var s = Sl(c, idx[^1]);
        if (s["data"] is not JsonObject) s["data"] = new JsonObject();
        return (JsonObject)s["data"]!;
    }

    private static void SetTakeaways(Ctx c, params string[] lines)
    {
        for (int k = 0; k < lines.Length; k++) S(c, k)["takeaway"] = lines[k];
    }

    /// <summary>The same mutations, by name, that fixtures\oracle\lint-grade.json was generated with (each edits the lint view of one deck).</summary>
    private static Dictionary<string, Action<Ctx>> Mutations() => new()
    {
        // baselines and deck level
        ["baseline"] = c => { },
        ["baseline_noarc"] = c => c.Arc = false,
        ["strict_false"] = c => c.Strict = false,
        ["empty_deck"] = c => { c.Spec["slides"] = new JsonArray(); c.Summary.Clear(); },
        ["slides_missing"] = c => { c.Spec.Remove("slides"); c.Summary.Clear(); },
        ["deck_short"] = c => Shrink(c, Range(c).Lo - 1),
        ["deck_long"] = c => Grow(c, Range(c).Hi + 1),
        ["deck_at_hi_reserved"] = c => { Grow(c, Range(c).Hi); c.Reserved = 1; },
        ["reserved_topic_skip"] = c => { Topics(c)["made_on_device"] = Json("""["zzz-nothing"]"""); c.Reserved = 1; },
        ["topic_made_on_device_not_reserved"] = c => { Topics(c)["made_on_device"] = Json("""["zzz-nothing"]"""); c.Reserved = 0; },
        ["summary_drop_entry"] = c => { int i = Ci(c, 1); c.Summary.RemoveAll(r => Idx(r) == i); },
        ["summary_duplicate_entry"] = c =>
        {
            var r = (JsonObject)c.Summary[SummIdx(c, Ci(c, 0))].DeepClone();
            r["words"] = 40;
            r["icons"] = 0;
            c.Summary.Add(r);
        },
        ["summary_empty"] = c => c.Summary.Clear(),
        ["summary_extra_entry"] = c => c.Summary.Add((JsonObject)Json("""
            {"index": 99, "layout": "x", "words": 50, "overflows": 1, "shrunk": 1, "icons": 7, "issues": [{"kind": "fallback", "detail": "z"}]}
            """)!),
        ["words_tie_down"] = c => TieWords(c, 34),
        ["words_tie_up"] = c => TieWords(c, 40),

        // titles, takeaways, notes
        ["title_word_counts"] = c =>
        {
            string[] titles = ["Local AI keeps client data on the laptop", "Local AI keeps client data on device", "Local AI", "Local AI wins", "Overview", ""];
            for (int k = 0; k < titles.Length; k++) S(c, k)["title"] = titles[k];
        },
        ["title_types"] = c =>
        {
            S(c, 0).Remove("title");
            S(c, 1)["title"] = null;
            S(c, 2)["title"] = 0;
            S(c, 3)["title"] = Json("""["Local AI", "stays on the laptop for everyone today"]""");
            S(c, 4)["title"] = 3.5;
            S(c, 5)["title"] = true;
            S(c, 6)["title"] = Json("""{"a": "b c d e f g h i"}""");
        },
        ["title_ends"] = c => { Sl(c, 0)["title"] = "AI"; Sl(c, c.Slides.Count - 1)["title"] = "Go"; },
        ["takeaway_word_counts"] = c =>
        {
            string[] words = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen"];
            S(c, 0)["takeaway"] = string.Join(" ", words.Take(14));
            S(c, 1)["takeaway"] = string.Join(" ", words.Take(15));
            S(c, 2).Remove("takeaway");
            S(c, 3)["takeaway"] = null;
            S(c, 4)["takeaway"] = true;
            S(c, 5)["takeaway"] = 12;
            S(c, 6)["takeaway"] = Json("""["like", "x y z"]""");
            S(c, 7)["takeaway"] = 0;
        },
        ["notes_variants"] = c =>
        {
            S(c, 0).Remove("notes");
            S(c, 1)["notes"] = "   \t\n ";
            S(c, 2)["notes"] = null;
            var s3 = S(c, 3);
            s3["speaker_notes"] = Pop(s3, "notes", "");
            S(c, 4)["notes"] = 42;
            S(c, 5)["notes"] = Json("""["a", "b"]""");
            S(c, 6)["notes"] = Json("""{"k": "v"}""");
            S(c, 7)["notes"] = "\u00a0\u2003\u001f";
        },
        ["notes_all_gone"] = c => { foreach (var s in c.Slides) ((JsonObject)s!).Remove("notes"); },
        ["notes_every_other"] = c => { for (int i = 1; i < c.Slides.Count; i += 2) Sl(c, i).Remove("notes"); },

        // numbers
        ["num_formats"] = c => S(c, 0)["takeaway"] =
            "Totals 1,234,567 and 0.00001234 and 3.14159265 and 1,234,565 and 100000 and 0.0001 and 999999.5 and 12.50 and 123456.5",
        ["num_rounding_to_allowed"] = c =>
        {
            var allowed = SpecText.BriefNumbers(Brief.FromJson(c.Brief)).Where(v => v >= 11 && v == Math.Truncate(v)).Order().ToList();
            if (allowed.Count == 0) throw new NotApplicable();
            long a = (long)allowed[0];
            S(c, 1)["takeaway"] = $"{a}.4 {a}.5 {a + 1}.5 {a + 2}.4 {Py.FloatRepr(a + 0.49)}";
        },
        ["num_numeric_values"] = c =>
        {
            Data(c, 0)["value"] = 4821;
            Data(c, 1)["value"] = 12.5;
            Data(c, 2)["value"] = 1e22;
            Data(c, 3)["value"] = 0.00001;
            Data(c, 4)["value"] = -5;
            Data(c, 5)["value"] = true;
            var d6 = Data(c, 6);
            d6["capacity"] = 99;
            d6["size"] = 777;
            Data(c, 7)["value"] = "88";
        },
        ["num_unicode_digits"] = c => S(c, 0)["takeaway"] = "Reach \u0663\u0664\u0665 users and \uff11\uff12\uff13\uff14 more, 7.\u0665 too",
        ["num_many_untraced"] = c => Put(c, Ci(c, 0), "data",
            string.Join(" ", Enumerable.Range(0, 15).Select(i => (9000 + i * 7).ToString(CultureInfo.InvariantCulture))) + " 9007 9007 9014.5"),
        ["num_glued_and_commas"] = c => S(c, 0)["takeaway"] = "A8 F2 v3.1 x9 1.2.3 1,2,3 5, 1,000.5 ,5 7-8B 64,000 MiB $5 20% 3x 1e3 0.5 007 1_000",
        ["num_none_anywhere"] = c => StripDigits(c.Spec),

        // guardrails
        ["gr_each_pattern_a"] = c => SetTakeaways(c, "Fully air-gapped for every client.", "Up to 45 TOPS on the NPU.",
            "Copilot+ PC ready and Copilot Plus PC too.", "Pay $ 5 or $20 per seat.", "Strong ROI in a month."),
        ["gr_each_pattern_b"] = c => SetTakeaways(c, "This replaces Microsoft 365 Copilot and replaces the subscription.",
            "128GB of VRAM and 192 GB and dedicated VRAM.", "Certified offline and fully offline guaranteed.",
            "Works offline; runs fully offline; no internet needed.", "Copilot plus a local lane, no PC claim."),
        ["gr_negation"] = c => SetTakeaways(c, "This is not an air-gap claim.", "Never overclaim. Fully air-gapped.", "Air-gap, not required",
            "Nothing air-gapped here, and I know air-gap too.", "Never claim this; air-gapped for all.", "noair-gap",
            "We avoid air-gap claims and so don't say air-gapped.", "Without TOPS claims, TOPS numbers are not shown. TOPS!"),
        ["gr_where"] = c =>
        {
            S(c, 0)["notes"] = "We are air-gapped.";
            Put(c, Ci(c, 1), "data", "Fully air-gapped.");
            S(c, 2)["icon"] = "air-gapped";
            S(c, 3)["tone"] = "air-gapped";
            var s4 = S(c, 4);
            if (!s4.ContainsKey("data")) s4["data"] = new JsonObject();
            (s4["data"] as JsonObject ?? throw new NotApplicable())["highlight"] = "air-gapped";
            var s5 = S(c, 5);
            s5.Remove("notes");
            s5["speaker_notes"] = "air-gapped";
            S(c, 6)["title"] = "Air gap";
            S(c, 7)["takeaway"] = "AIRGAP";
        },
        ["gr_truncation"] = c =>
        {
            string emoji = "\U0001F600";
            S(c, 0)["takeaway"] = new string('x', 100) + " air-gapped";
            S(c, 1)["takeaway"] = string.Concat(Enumerable.Repeat(emoji, 100)) + " TOPS";
            S(c, 2)["takeaway"] = string.Concat(Enumerable.Repeat(emoji, 70)) + " 9999";
        },

        // required topics
        ["topic_variants"] = c =>
        {
            var topics = Topics(c);
            topics[topics.First().Key] = Json("""["zzz-unfindable"]""");
            topics["empty_kw"] = new JsonArray();
            topics["many_kw"] = Json("""["k1x", "k2x", "k3x", "k4x", "k5x", "k6x"]""");
            topics["notes_only"] = Json("""["okapi-sighting"]""");
            topics["visible_added"] = Json("""["ZEBRA-CROSSING"]""");
            topics["unicode_kw"] = Json("""["CAF\u00c9"]""");
            S(c, 1)["notes"] = "An okapi-sighting was recorded.";
            S(c, 2)["title"] = "Zebra-Crossing rules";
            S(c, 3)["takeaway"] = "caf\u00e9 stays open";
        },
        ["topic_none"] = c => c.Brief["required_topics"] = new JsonObject(),

        // layouts and analogies
        ["layout_ends"] = c => { Sl(c, 0)["layout"] = "stack"; Sl(c, c.Slides.Count - 1)["layout"] = "kpis"; },
        ["layout_case_and_odd"] = c =>
        {
            Sl(c, 0)["layout"] = "Title";
            Sl(c, c.Slides.Count - 1)["layout"] = "CTA";
            S(c, 0)["layout"] = " stack";
            S(c, 1)["layout"] = "Stack";
            S(c, 2)["layout"] = "STATEMENT";
            S(c, 3)["layout"] = null;
            S(c, 4).Remove("layout");
        },
        ["layout_banned"] = c => { string[] names = ["memory", "hub", "statement", "MEMORY"]; for (int k = 0; k < names.Length; k++) S(c, k)["layout"] = names[k]; },
        ["arc_swap"] = c =>
        {
            JsonObject a = S(c, 0), b = S(c, 1);
            var la = a["layout"]?.DeepClone();
            var lb = b["layout"]?.DeepClone();
            a["layout"] = lb;
            b["layout"] = la;
        },
        ["analogy_adjacent"] = c => { S(c, 0)["layout"] = "analogy"; S(c, 1)["layout"] = "analogy"; },
        ["analogy_none"] = c => { foreach (var n in c.Slides) if (Lay((JsonObject)n!) == "analogy") ((JsonObject)n!)["layout"] = "stack"; },
        ["analogy_one"] = c =>
        {
            var idx = FindAll(c, "analogy");
            foreach (int i in idx.Skip(1)) Sl(c, i)["layout"] = "stack";
            if (idx.Count < 2) throw new NotApplicable();
        },
        ["analogy_pair_forms"] = c =>
        {
            var idx = FindAll(c, "analogy");
            if (idx.Count == 0) throw new NotApplicable();
            for (int j = 0; j < idx.Count; j++)
            {
                var s = Sl(c, idx[j]);
                if (s["data"] is not JsonObject) s["data"] = new JsonObject();
                var d = (JsonObject)s["data"]!;
                switch (j % 4)
                {
                    case 0: d["pairs"] = Json("""[["a", "b"], ["c", "d"], ["e", "f"]]"""); break;
                    case 1: d.Remove("pairs"); break;
                    case 2: d["pairs"] = "abcd"; break;
                    default: d["pairs"] = Json("""{"a": 1, "b": 2, "c": 3, "d": 4}"""); break;
                }
            }
        },
        ["cue_removed"] = StripCues,
        ["cue_exactly_one"] = c =>
        {
            StripCues(c);
            var woven = Enumerable.Range(0, c.Slides.Count).Where(i => Woven.Contains(Lay(Sl(c, i)))).ToList();
            if (woven.Count == 0) throw new NotApplicable();
            Sl(c, woven[^1])["takeaway"] = "A map, like a guide";
        },
        ["cue_exactly_two"] = c =>
        {
            StripCues(c);
            var woven = Enumerable.Range(0, c.Slides.Count).Where(i => Woven.Contains(Lay(Sl(c, i)))).ToList();
            if (woven.Count < 2) throw new NotApplicable();
            Sl(c, woven[0])["takeaway"] = "Imagine the map";
            Sl(c, woven[^1])["takeaway"] = "As if a guide";
        },
        ["cue_variants"] = c =>
        {
            StripCues(c);
            S(c, 0)["takeaway"] = "Works as a hub";
            S(c, 1)["title"] = "Your analogy here";
            var s2 = S(c, 2);
            if (!s2.ContainsKey("data")) s2["data"] = new JsonObject();
            (s2["data"] as JsonObject ?? throw new NotApplicable())["text"] = "think of it as a map";
            Sl(c, 0)["title"] = "Like a boss";
            S(c, 3)["takeaway"] = "is your helper";
            S(c, 4)["takeaway"] = "Unlike others";
            S(c, 5)["takeaway"] = "LIKE caps";
        },

        // canvas
        ["canvas_trim"] = c =>
        {
            var first = Els(Canvas(c, 0));
            while (first.Count > 8) first.RemoveAt(first.Count - 1);
            try
            {
                var second = Els(Canvas(c, 1));
                while (second.Count > 4) second.RemoveAt(second.Count - 1);
            }
            catch (NotApplicable) { }
        },
        ["canvas_no_arrows"] = c =>
        {
            var els = Els(Canvas(c));
            for (int i = els.Count - 1; i >= 0; i--)
                if (IsStroke(els[i])) els.RemoveAt(i);
        },
        ["canvas_geometry"] = c =>
        {
            var els = Els(Canvas(c));
            (string Key, JsonNode? Value)[] variants =
            [
                ("x", 11.5), ("y", 5.5), ("x", "3.5"), ("x", " 4 "), ("x", "1_0"), ("x", "abc"), ("x", "\u0661\u0662"), ("x", "1e1"),
                ("x", "inf"), ("x", null), ("x", true), ("x", Json("[1]")), ("x", "nan"), ("y", "-"), ("w", "0x10"),
            ];
            for (int k = 0; k < variants.Length && k < els.Count; k++) El(els, k)[variants[k].Key] = variants[k].Value;
        },
        ["canvas_types_labels"] = c =>
        {
            var els = Els(Canvas(c));
            if (els.Count < 11) throw new NotApplicable();
            El(els, 0)["type"] = null;
            El(els, 1)["type"] = "Arrow ";
            El(els, 2)["type"] = "ARROW";
            El(els, 3)["type"] = "Box";
            El(els, 4).Remove("type");
            El(els, 5)["type"] = 5;
            El(els, 6)["label"] = null;
            El(els, 7)["label"] = "";
            El(els, 8)["label"] = 0;
            El(els, 9)["label"] = 12;
            El(els, 10)["label"] = Json("""["a", "b"]""");
        },
        ["canvas_missing_geometry"] = c =>
        {
            foreach (var n in Els(Canvas(c)))
            {
                var e = (JsonObject)n!;
                foreach (string key in IsStroke(e) ? new[] { "x2", "y2" } : new[] { "w", "h" }) e.Remove(key);
            }
        },
        ["canvas_elements_forms"] = c =>
        {
            ((JsonObject)Canvas(c, 0)["data"]!)["elements"] = "abcdefghij";
            try { ((JsonObject)Canvas(c, 1)["data"]!)["elements"] = Json("""{"a": 1, "b": 2, "c": 3, "d": 4, "e": 5}"""); }
            catch (NotApplicable) { }
        },
        ["canvas_to_stack"] = c => { foreach (int i in FindAll(c, "canvas")) Sl(c, i)["layout"] = "stack"; },
        ["canvas_duplicated"] = c => { Canvas(c); DupSlide(c, FindAll(c, "canvas")[0]); },
        ["canvas_data_none"] = c => Canvas(c)["data"] = null,

        // bars
        ["bars_forms"] = c =>
        {
            MakeBars(c, 0, ["1 stream decode", "4 streams aggregate", "Peak"], "Peak");
            MakeBars(c, 1, ["Only"], "Missing");
            MakeBars(c, 2, [], null);
            var s3 = S(c, 3);
            s3["layout"] = "bars";
            s3["data"] = Json("""{"bars": {"a": {"label": "x"}}, "highlight": "a"}""");
            MakeBars(c, 4, [5, 6.5, null], "5");
            MakeBars(c, 5, [5, 6.5, null], 5);
            MakeBars(c, 6, ["single stream", "concurrent jobs"], "Single Stream");
            MakeBars(c, 7, ["2 streams", "3 streams", "agg"], "agg");
        },
        ["bars_highlight_wrong"] = c =>
        {
            var idx = FindAll(c, "bars");
            if (idx.Count == 0) throw new NotApplicable();
            foreach (int i in idx) (Sl(c, i)["data"] as JsonObject ?? throw new NotApplicable())["highlight"] = "nonexistent";
        },

        // cta
        ["cta_steps_short"] = c =>
        {
            var d = Cta(c);
            d["steps"] = d["steps"] is JsonArray steps ? new JsonArray([.. steps.Take(2).Select(x => x?.DeepClone())]) : Json("""["a", "b"]""");
        },
        ["cta_steps_and_ask_missing"] = c => { var d = Cta(c); d.Remove("steps"); d.Remove("ask"); },
        ["cta_ask_blank"] = c => Cta(c)["ask"] = "  \t",
        ["cta_weird_forms"] = c => { var d = Cta(c); d["steps"] = "abc"; d["ask"] = 0; },
        ["cta_dict_steps_number_ask"] = c => { var d = Cta(c); d["steps"] = Json("""{"a": 1, "b": 2}"""); d["ask"] = 5; },

        // render summary
        ["summary_icons"] = c =>
        {
            SetSumm(c, Ci(c, 0), ("icons", 2));
            SetSumm(c, Ci(c, 1), ("icons", 3));
            SetSumm(c, Ci(c, 2), ("icons", 0));
            SetSumm(c, 0, ("icons", 0));
        },
        ["summary_icons_high"] = c => { foreach (var r in c.Summary) r["icons"] = 9; },
        ["summary_icons_zero"] = c => { foreach (var r in c.Summary) r["icons"] = 0; },
        ["summary_words"] = c =>
        {
            SetSumm(c, Ci(c, 0), ("words", 33));
            SetSumm(c, Ci(c, 1), ("words", 32));
            SetSumm(c, Ci(c, 2), ("words", 28));
            SetSumm(c, Ci(c, 3), ("words", 100));
        },
        ["summary_words_high"] = c => { foreach (var r in c.Summary) r["words"] = 80; },
        ["summary_words_low"] = c => { foreach (var r in c.Summary) r["words"] = 10; },
        ["summary_flags"] = c =>
        {
            SetSumm(c, Ci(c, 0), ("overflows", 1));
            SetSumm(c, Ci(c, 1), ("shrunk", 2));
            SetSumm(c, Ci(c, 2), ("overflows", 3), ("shrunk", 1));
            SetSumm(c, Ci(c, 3), ("issues", Json("""[{"kind": "fallback", "detail": "boom"}]""")));
            SetSumm(c, Ci(c, 4), ("issues", Json("""[{"kind": "memory_overcommit", "detail": "x"}, {"kind": "fallback", "detail": "y"}]""")));
        },

        // type confusion and exotic text
        ["tc_data_forms"] = c =>
        {
            JsonNode?[] forms = [Json("[1, 2]"), "hello world", 5];
            int done = 0;
            foreach (int i in Content(c))
            {
                var s = Sl(c, i);
                if (Lay(s) is "canvas" or "bars" or "analogy" or "cta") continue;
                s["data"] = forms[done++];
                if (done == 3) return;
            }
            throw new NotApplicable();
        },
        ["ws_word_counts"] = c =>
        {
            S(c, 0)["title"] = "Local\u00a0AI\u2003keeps\u3000data\u001fon\u001claptop here now";
            S(c, 1)["title"] = "Local\u200bAI\u200bkeeps\u200bdata";
            S(c, 2)["takeaway"] = string.Join("\u001f", Enumerable.Range(0, 15).Select(i => "w" + i));
        },
        ["brief_variants"] = c =>
        {
            if (c.Brief["facts"] is not JsonArray facts) c.Brief["facts"] = facts = new JsonArray();
            foreach (string fact in new[] { "Customers number 7777 in 31 regions", "Line one\n4242 users and\t5151 units", "Cafe 300 \u20ac", "Ctl \u00015 end" })
                facts.Add(fact);
            if (c.Brief["guardrails"] is not JsonArray guardrails) c.Brief["guardrails"] = guardrails = new JsonArray();
            guardrails.Add("Never claim 9000 TOPS");
            c.Brief["audience"] = Py.StrGet(c.Brief, "audience", "") + " 3000 staff";
            S(c, 0)["takeaway"] = "7777 31 4242 5151 300 9000 3000 15 16.5";
        },
        ["brief_range_tight"] = c => c.Brief["slide_range"] = Json("[10, 10]"),
        ["brief_range_wide"] = c => c.Brief["slide_range"] = Json("[3, 30]"),
        ["layouts_uniform"] = c => { foreach (int i in Content(c)) Sl(c, i)["layout"] = "stack"; },
        ["layouts_statement"] = c => { foreach (int i in Content(c)) Sl(c, i)["layout"] = "statement"; },
        ["layouts_variety"] = c =>
        {
            string[] names = ["stack", "flow", "compare", "kpis", "bars", "timeline", "matrix", "hub"];
            var content = Content(c);
            for (int k = 0; k < content.Count; k++) Sl(c, content[k])["layout"] = names[k % names.Length];
        },

        // averages whose binary value sits just off a decimal tie, where Python's round() and a decimal half-even disagree
        ["round_ties_words"] = c =>
        {
            Grow(c, 20);
            foreach (var r in c.Summary) r["words"] = 30;
            c.Summary[0]["words"] = 30 + 603 - 30 * c.Summary.Count;
        },
        ["round_ties_share_icons"] = c =>
        {
            Grow(c, 42);
            var content = Content(c);
            for (int k = 0; k < content.Count; k++) Sl(c, content[k])["layout"] = k == 0 ? "stack" : "statement";
            foreach (var r in c.Summary) r["icons"] = 0;
            foreach (int i in content.Take(14)) SetSumm(c, i, ("icons", 1));
        },
    };

    // ------------------------------------------------------------------------------------------------ Python primitives

    private static double ParseDouble(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string? Expect(string actual, string expected, string what) => actual == expected ? null : $"{what}: expected {expected}, got {actual}";

    /// <summary>One check per table: every row is run and the first few failures are reported.</summary>
    private static void Table(Check check, string name, JsonNode? rows, Func<JsonNode, string?> test)
    {
        var list = rows!.AsArray();
        var failures = list.Select(r => test(r!)).Where(f => f is not null).ToList();
        check.That(failures.Count == 0, $"{name} ({list.Count} cases)", Clip(string.Join(" | ", failures.Take(4))));
    }

    private static void PythonPrimitives(Check check, JsonObject py)
    {
        Table(check, "repr(float) and round(x, 1|2)", py["round"], r =>
        {
            string text = (string)r[0]!;
            double x = ParseDouble(text);
            return Expect(Py.FloatRepr(x), text, "repr") ?? Expect(Py.FloatRepr(Py.Round(x, 1)), (string)r[1]!, $"round({text}, 1)")
                ?? Expect(Py.FloatRepr(Py.Round(x, 2)), (string)r[2]!, $"round({text}, 2)");
        });
        Table(check, "format(x, 'g')", py["g"], r => Expect(Py.FormatG(ParseDouble((string)r[0]!)), (string)r[1]!, $"%g of {r[0]}"));
        Table(check, "repr(float) of random bit patterns", py["repr"], r => Expect(Py.FloatRepr(ParseDouble((string)r!)), (string)r!, "repr"));
        Table(check, "float(str)", py["float"], r =>
        {
            string text = (string)r[0]!, shown = JsonValue.Create(text)!.ToJsonString();
            bool ok = Py.TryParseFloat(text, out double v);
            if (r[1] is null) return ok ? $"float({shown}) should be rejected, got {Py.FloatRepr(v)}" : null;
            return ok ? Expect(Py.FloatRepr(v), (string)r[1]!, $"float({shown})") : $"float({shown}) was rejected";
        });
        var pythonSpace = py["space"]!.AsArray().Select(n => (int)n!).ToHashSet();
        var ourSpace = Enumerable.Range(0, 0x10000).Where(cp => cp is < 0xD800 or > 0xDFFF).Where(cp => Py.IsSpace((char)cp)).ToHashSet();
        check.That(pythonSpace.SetEquals(ourSpace), $"str.split() whitespace over the BMP ({pythonSpace.Count} characters)",
            $"only Python: {string.Join(",", pythonSpace.Except(ourSpace).Take(5))}; only C#: {string.Join(",", ourSpace.Except(pythonSpace).Take(5))}");
        Table(check, "str(json value) and repr(json value)", py["str"], r =>
        {
            var node = JsonNode.Parse((string)r[0]!);
            return Expect(Py.Str(node), (string)r[1]!, $"str({r[0]})") ?? Expect(Py.Repr(node), (string)r[2]!, $"repr({r[0]})");
        });
        Table(check, "len(s) and s[:n] by code points", py["slice"], r =>
        {
            string text = (string)r[0]!;
            return Expect(Py.Len(text).ToString(CultureInfo.InvariantCulture), r[1]!.ToJsonString(), "len") ?? Expect(Py.Head(text, 3), (string)r[2]!, "[:3]")
                ?? Expect(Py.Head(text, 5), (string)r[3]!, "[:5]");
        });
        Table(check, "letter(score)", py["letter"], r => Expect(Grade.Letter(ParseDouble((string)r[0]!)), (string)r[1]!, $"letter({r[0]})"));
        Table(check, "numbers(text)", py["numbers"], r =>
        {
            string got = string.Join(",", SpecText.Numbers((string)r[0]!).Select(Py.FloatRepr));
            return Expect(got, string.Join(",", r[1]!.AsArray().Select(n => (string)n!)), $"numbers({r[0]})");
        });
        Table(check, "affirmative_hits(pattern, [text])", py["hits"], r =>
        {
            string pattern = Guardrails.Patterns.First(p => p.Name == (string)r[0]!).Pattern;
            var got = Guardrails.AffirmativeHits(pattern, [(string)r[1]!]);
            return Expect(string.Join("|", got), string.Join("|", r[2]!.AsArray().Select(n => (string)n!)), $"{r[0]} in {Json(JsonValue.Create((string)r[1]!)!.ToJsonString())}");
        });
    }

    // ------------------------------------------------------------------------------------------------ regex sources

    /// <summary>The patterns were ported verbatim; this pins their source text and case option to the Python modules', including alternatives no mutation reaches.</summary>
    private static void PatternSources(Check check, JsonObject patterns)
    {
        var failures = new List<string>();
        void Same(string what, Regex actual, JsonNode? expected, bool ignoreCase = true)
        {
            if (actual.ToString() != (string)expected!) failures.Add($"{what}: C# has {actual}, Python has {expected}");
            else if (actual.Options.HasFlag(RegexOptions.IgnoreCase) != ignoreCase) failures.Add($"{what}: ignore-case option differs");
        }
        static Regex Private(Type type, string name) => (Regex)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        List<(string, string)> Rules(string key) => [.. patterns[key]!.AsArray().Select(r => ((string)r![0]!, (string)r[1]!))];

        if (!Rules("guardrails").SequenceEqual(Guardrails.Patterns)) failures.Add("rubric guardrail rules differ from GUARDRAIL_PATTERNS");
        if (!Rules("extra_banned").SequenceEqual(Guardrails.ExtraBanned)) failures.Add("lint-only rules differ from EXTRA_BANNED");
        Same("NEGATION", Guardrails.Negation, patterns["negation"]);
        Same("ANALOGY_CUES", Grade.AnalogyCues, patterns["analogy_cues"]);
        Same("lint CUE", Private(typeof(Lint), "Cue"), patterns["lint_cue"]);
        Same("single-stream bars", Private(typeof(Lint), "SingleStream"), patterns["single_stream"]);
        Same("multi-stream bars", Private(typeof(Lint), "MultiStream"), patterns["multi_stream"]);
        Same("number scanner", Private(typeof(SpecText), "Number"), patterns["number"], ignoreCase: false);
        Same("test helper that strips cues", CueRx, patterns["lint_cue"]);
        check.That(failures.Count == 0, "regex sources and case options match the Python modules", Clip(string.Join(" | ", failures)));
    }

    // ------------------------------------------------------------------------------------------------ unit checks

    private static void UnitChecks(Check check)
    {
        string airGap = Guardrails.Patterns.First(p => p.Name == "air-gap claim").Pattern;
        int Hits(string text) => Guardrails.AffirmativeHits(airGap, [text]).Count;
        check.That(Hits("no air-gap claim") == 0, "negated 'no air-gap claim' does not fire");
        check.That(Hits("This is not an air-gap claim.") == 0, "'not an air-gap claim' does not fire");
        check.That(Hits("fully air-gapped") == 1, "'fully air-gapped' fires");
        check.That(Hits("air-gapped, not required") == 1, "a negation after the match does not excuse it");
        check.That(Hits("We know air-gap and say nothing air-gapped") == 2, "'know' and 'nothing' are not negations");
        check.That(Hits("Not this; air-gapped though") == 1, "';' starts a new sentence, so the negation does not carry over");
        check.That(Hits("Never claim it. Air-gapped!") == 1, "the negation stops at the sentence end");
        check.That(Guardrails.AffirmativeHits(airGap, [new string('x', 100) + " air-gapped"])[0].Length == 90, "a hit is clipped to 90 characters");
        check.That(Guardrails.AffirmativeHits(airGap, ["  padded air-gapped  "])[0] == "padded air-gapped", "a hit is trimmed");
        check.That(Guardrails.Patterns.Count == 8 && Guardrails.ExtraBanned.Single().Name == "unhedged offline claim", "guardrail rules: 8 rubric rules plus the lint-only 'unhedged offline claim'");

        (double Score, string Grade)[] letters =
        [
            (100, "A+"), (97, "A+"), (96.9, "A"), (93, "A"), (92.9, "A-"), (90, "A-"), (89.9, "B+"), (87, "B+"), (86.9, "B"), (83, "B"), (82.9, "B-"),
            (80, "B-"), (79.9, "C+"), (77, "C+"), (76.9, "C"), (73, "C"), (72.9, "C-"), (70, "C-"), (69.9, "D+"), (67, "D+"), (66.9, "D"), (60, "D"),
            (59.9, "F"), (0, "F"),
        ];
        foreach (var (score, expected) in letters)
            check.That(Grade.Letter(score) == expected, $"letter({score}) is {expected}", Grade.Letter(score));

        check.That(Grade.Rubric.Values.Sum() == 100 && Grade.RubricVersion == "1.0.1", "rubric 1.0.1 adds up to 100 points");
        check.That(Lint.WordBudget == 32, "lint word budget is 32");
        var perSlide = Lint.PerSlideCounts([new LintIssue(-1, "count", "m"), new LintIssue(2, "words", "m"), new LintIssue(2, "fit", "m")]);
        check.That(perSlide.Count == 2 && perSlide[-1] == 1 && perSlide[2] == 2, "lint findings are counted per slide, -1 for the deck");
        check.That(Grade.OrdinalOk.SetEquals(Enumerable.Range(0, 11).Select(i => (double)i)), "ordinal numbers 0..10 need no source");
        check.That(SpecText.Numbers("A8 F2 v3.1 1,200 3.5 64,000 MiB").SequenceEqual([1200.0, 3.5, 64000.0]), "numbers glued to letters (A8, F2, v3.1) are not numbers");
        check.That(Py.Round(0.35, 1) == 0.3 && Py.Round(2.675, 2) == 2.67 && Py.Round(0.125, 2) == 0.12 && Py.Round(2.5) == 2, "round() is half-to-even on the exact binary value");
        check.That(Py.WordCount("Local\u00a0AI\u2003keeps\u3000data  on\tlaptop\n") == 6, "word count splits on Unicode whitespace like str.split()");
        check.That(Py.FormatG(1234565) == "1.23456e+06" && Py.FormatG(0.00001234) == "1.234e-05" && Py.FormatG(45) == "45", "%g formatting");
    }
}
