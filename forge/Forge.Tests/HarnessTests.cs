using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Layouts;
using Forge.Core.Pptx;
using Forge.Core.Spec;

namespace Forge.Tests;

/// <summary>The prompt harness against a scripted model: no runtime, every path headless. The script plays a model that follows the prompt
/// (so the plumbing from prompt to slide to lint is exercised) and, where a test needs it, one that slips in a particular way.</summary>
public static class HarnessTests
{
    private static readonly DeviceStory Story = new()
    {
        Name = "Surface Laptop 7", Silicon = "Snapdragon X Plus", Engine = "Snapdragon NPU", Lane = "npu",
        Runtime = "Foundry Local with the QNN execution provider", Model = "Qwen2.5 7B", UnifiedMemory = true,
        Facts =
        [
            "Device: Surface Laptop 7 with Snapdragon X Plus and 16 GB installed memory.",
            "The deck is drafted by Qwen2.5 7B on the Snapdragon NPU.",
        ],
        Keywords = ["snapdragon", "npu"], Noun = "Laptop",
    };

    private sealed class ListProgress : IProgress<HarnessProgress>
    {
        public List<HarnessProgress> Items { get; } = [];
        public void Report(HarnessProgress value) => Items.Add(value);
    }

    private sealed class FixedMeter(EnergyReading reading, bool failOnStop = false) : IEnergyMeter
    {
        public bool Started { get; private set; }
        public void Start() => Started = true;
        public EnergyReading Stop() => failOnStop ? throw new InvalidOperationException("sensor gone") : reading;
    }

    public static int Run()
    {
        var check = new Check("harness");
        var tests = new (string Name, Action<Check> Test)[]
        {
            ("tidy", Tidy), ("skeletons", Skeletons), ("arcs", ArcRanges), ("bundled", Bundled), ("drafter", Drafter),
            ("provenance", ProvenanceText), ("guided", GuidedHappyPath), ("repair-accept", RepairAccepted), ("repair-reject", RepairRejected),
            ("repair-weighted", RepairWeighted), ("repair-once", RepairNotRepeated), ("word-limit", WordLimits), ("foreign-script", ForeignScript),
            ("retry", BadJsonThenGood), ("placeholder", PlaceholderOnFailure), ("topic-repair", TopicRepair),
            ("no-guidance", GuidanceRejected), ("options", OptionsRespected), ("windows", Windows), ("failures", Failures),
            ("arc", ArcWhole), ("arc-fallback", ArcFallsBack), ("arc-fixes", ArcFixes), ("energy", Energy), ("metrics", MetricsShape),
        };
        foreach (var (name, test) in tests)
        {
            try { test(check); }
            catch (Exception ex) { check.That(false, name + " crashed", ex.ToString()); }
        }
        return check.Finish();
    }

    // ---- helpers ----

    private static (Brief Brief, IReadOnlyList<ArcSlot> Arc) Setup(string id)
    {
        var brief = BundledBriefs.Find(id) ?? throw new InvalidOperationException("no bundled brief " + id);
        var adapted = BriefAdapter.Adapt(brief, Story);
        return (brief, Arcs.For(adapted, Arcs.Reserved(adapted)));
    }

    private static ScriptedLlm Clean(IReadOnlyList<ArcSlot> arc, int context = 32768, Func<LlmRequest, int, LlmReply?>? special = null, bool guidance = true) =>
        new(ScriptedLlm.Model(context: context, guidance: guidance), (request, n) =>
            special?.Invoke(request, n) ?? ScriptedLlm.Say(DeckScript.Reply(request, arc), guided: request.Schema is not null));

    private static HarnessResult Go(ILlm llm, Brief brief, HarnessOptions? options = null, IEnergyMeter? meter = null, IProgress<HarnessProgress>? progress = null) =>
        DeckHarness.RunAsync(llm, Story, brief, options, meter, progress).GetAwaiter().GetResult();

    private static string Layout(JsonNode? slide) => slide?["layout"]?.GetValue<string>() ?? "";
    private static string TitleOf(JsonNode? slide) => slide?["title"]?.GetValue<string>() ?? "";
    private static List<JsonObject> Slides(HarnessResult result) => SpecJson.SlideObjects(result.Spec);
    private static string Issues(IEnumerable<LintIssue> issues) => string.Join("; ", issues.Select(i => $"{i.Slide}:{i.Code}"));

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Breaks a good slide the way a small model does: a title that reads as a label, and/or no speaker notes. Neither adds words,
    /// so the slide stays inside its word budget and the planted findings are the only ones.</summary>
    private static JsonObject WithBreakage(string slideJson, bool labelTitle, bool noNotes)
    {
        var slide = Parse(slideJson);
        if (labelTitle) slide["title"] = "Big Idea";
        if (noNotes) slide["notes"] = "";
        return slide;
    }

    // ---- SlideTidy ----

    private static bool Build(JsonObject reply, string layout, out JsonObject slide, out JsonObject? description, out string error) =>
        SlideTidy.TryBuild(reply, new ArcSlot("test", layout, ""), Story, out slide, out description, out error);

    private static void Tidy(Check c)
    {
        bool ok = Build(Parse("""{"title":"Hello There","hero_icons":["laptop","not_an_icon","laptop"]}"""), "title", out var slide, out _, out _);
        var icons = slide["hero_icons"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        c.That(ok && Layout(slide) == "title", "title slide builds");
        c.That(icons.Count == 4 && icons[0] == "laptop" && icons.Distinct().Count() == 4 && icons.All(Icons.Known), "hero icons are validated, de-duplicated and padded to four", string.Join(",", icons));

        ok = Build(Parse("""{"slide":{"layout":"bars","title":"Wrapped","takeaway":"t","data":{"layers":[{"label":"a","detail":"b","icon":"chip"}]}}}"""), "stack", out slide, out _, out _);
        c.That(ok && Layout(slide) == "stack" && TitleOf(slide) == "Wrapped", "a wrapped slide is unwrapped and the slot's layout is forced");

        ok = Build(Parse("""{"slides":[{"title":"In a list","takeaway":"t","steps":[{"label":"a","detail":"b","icon":"chip"}]}]}"""), "flow", out slide, out _, out _);
        c.That(ok && slide["data"]?["steps"] is JsonArray { Count: 1 }, "a slide with its collection at the top level is moved under data");

        ok = Build(Parse("""{"title":"Speeds","data":{"highlight":"model b","bars":[{"label":"Model A","value":"41 tokens/s"},{"label":"Model B","value":"86"}]}}"""), "bars", out slide, out _, out _);
        var bars = slide["data"]!["bars"]!.AsArray();
        c.That(ok && bars[0]!["value"]!.GetValue<double>() == 41 && bars[1]!["value"]!.GetValue<double>() == 86, "bar values become numbers");
        c.That(slide["data"]!["highlight"]!.GetValue<string>() == "Model B", "a highlight that differs only by case becomes the exact bar label");

        Build(Parse("""{"title":"Speeds","data":{"bars":[{"label":"Model A","value":4},{"label":"Model B","value":9}]}}"""), "bars", out slide, out _, out _);
        c.That(slide["data"]!["highlight"]!.GetValue<string>() == "Model B", "a missing highlight becomes the tallest bar");

        Build(Parse("""{"title":"Quadrants","data":{"highlight":"7","quadrants":[{"label":"a"},{"label":"b"},{"label":"c"},{"label":"d"}]}}"""), "matrix", out slide, out _, out _);
        c.That(slide["data"]!["highlight"]!.GetValue<int>() == 3, "a matrix highlight is clamped to 0-3");

        Build(Parse("""{"title":"Numbers","data":{"items":[{"value":3,"label":"steps"},{"value":"2","label":"lanes"}]}}"""), "kpis", out slide, out _, out _);
        c.That(slide["data"]!["items"]![0]!["value"] is JsonValue v && v.TryGetValue<string>(out string? text) && text == "3", "a numeric KPI value is written out as text");

        Build(Parse("""{"title":"Speeds","data":{"items":[{"value":"28 tokens/s","label":"a"},{"value":"4 tokens per second","label":"b"},{"value":"12 GB","label":"c"}]}}"""), "kpis", out slide, out _, out _);
        var values = slide["data"]!["items"]!.AsArray().Select(i => i!["value"]!.GetValue<string>()).ToList();
        c.That(values.SequenceEqual(["28 tok/s", "4 tok/s", "12 GB"]), "a long rate is shortened so the tile's number stays on one line", string.Join(" | ", values));

        c.That(SlideTidy.PrettySlot("moe_analogy") == "MoE analogy" && SlideTidy.PrettySlot("codegen_rag") == "Codegen RAG" && SlideTidy.PrettySlot("cta") == "CTA"
            && SlideTidy.PrettySlot("big_idea") == "Big idea" && SlideTidy.PrettySlot("") == "", "slot names read as words, with the acronyms in capitals",
            $"{SlideTidy.PrettySlot("moe_analogy")} | {SlideTidy.PrettySlot("codegen_rag")} | {SlideTidy.PrettySlot("cta")}");

        c.That(!Build(Parse("""{"takeaway":"no title","data":{"items":[{"value":"1"}]}}"""), "kpis", out _, out _, out string error) && error.Contains("title"), "a reply with no title is rejected", error);
        c.That(!Build(Parse("""{"title":"No data"}"""), "stack", out _, out _, out error) && error.Contains("data"), "a reply with no data is rejected", error);
        c.That(!Build(Parse("""{"title":"Empty","data":{"layers":[]}}"""), "stack", out _, out _, out error) && error.Contains("layers"), "an empty collection is rejected", error);
        c.That(!Build(Parse("""{"title":"Half","data":{"left":{"title":"a"}}}"""), "compare", out _, out _, out error) && error.Contains("left"), "a compare slide needs both sides", error);

        ok = Build(Parse("""{"title":"Diagram","takeaway":"t","inside":[{"label":"Notes","icon":"document"},"Model","Draft","Extra"],"outside":["Cloud"],"notes":"n"}"""), "canvas", out slide, out var description, out _);
        c.That(ok && description is not null && description["inside"]!.AsArray().Count == 3, "a canvas description keeps at most three inside items");
        var elements = slide["data"]?["elements"] as JsonArray;
        c.That(Layout(slide) == "canvas" && elements is { Count: >= 10 }, "a canvas description is drawn as a diagram", $"{elements?.Count} elements");
        c.That(!Build(Parse("""{"title":"Diagram","takeaway":"t"}"""), "canvas", out _, out _, out error) && error.Contains("inside"), "a canvas without inside items is rejected", error);
    }

    private static void Skeletons(Check c)
    {
        foreach (string layout in new[] { "title", "kpis", "stack", "flow", "compare", "analogy", "bars", "timeline", "matrix", "canvas", "cta" })
        {
            var skeleton = SlideTidy.Skeleton(new ArcSlot("pain", layout, ""), Story);
            var spec = new JsonObject { ["title"] = "T", ["client"] = "C", ["slides"] = new JsonArray(skeleton) };
            var deck = DeckBuilder.Build(spec, Theme.Surface);
            c.That(deck.Slides.Count == 1, $"a {layout} placeholder still draws a slide");
        }
    }

    // ---- Arcs and briefs ----

    private static void ArcRanges(Check c)
    {
        foreach (string id in new[] { "contoso_sales", "zava_technical" })
        {
            var brief = Brief.FromJson(BundledBriefs.Find(id)!.ToJson());
            var full = Arcs.For(brief);
            brief.SlideMax = 9;
            var trimmed = Arcs.For(brief, reserved: 1);
            c.That(full.Count >= 10, $"{id}: the full story has at least ten slots", $"{full.Count}");
            c.That(trimmed.Count + 1 <= 9, $"{id}: the story is trimmed to leave room for the measured slide", $"{trimmed.Count}");
            c.That(trimmed[0].Slot == "title" && trimmed[^1].Slot == "cta", $"{id}: the title and the call to action survive trimming");
            c.That(trimmed.Count(s => s.Layout == "analogy") == 2, $"{id}: both analogy slots survive trimming");
        }
        var plain = Arcs.For(BundledBriefs.Find("contoso_sales")!);
        c.That(Arcs.Reserved(BundledBriefs.Find("contoso_sales")!) == 1 && Arcs.Reserved(new Brief()) == 0, "only a brief that asks for the measured slide reserves a place for it");
        c.That(plain.Count(s => s.Layout == "bars") == 0, "a sales story has no speed chart");

        var technical = Brief.FromJson(BundledBriefs.Find("zava_technical")!.ToJson());
        c.That(Arcs.For(technical).Single(s => s.Slot == "throughput").Layout == "kpis", "without two measured speeds in the facts the speed chart becomes KPIs");
        technical.Facts.Add("Model A decodes about 40 tokens/s single stream.");
        technical.Facts.Add("Model B decodes about 80 tokens/s single stream.");
        c.That(Arcs.For(technical).Single(s => s.Slot == "throughput").Layout == "bars", "with two measured speeds the speed chart stays");
    }

    private static readonly string[] DeviceSpecific =
        ["Surface Laptop Ultra", "RTX Spark", "Blackwell", "Local-AI-Bench", "128 GB", "86 tokens", "72 tokens", "llama.cpp", "CUDA", "28 open models"];

    private static void Bundled(Check c)
    {
        c.That(BundledBriefs.All.Count == 5, "five briefs ship with the app", $"{BundledBriefs.All.Count}");
        c.That(BundledBriefs.All.Take(3).All(b => !b.IsTechnical) && BundledBriefs.All.Skip(3).All(b => b.IsTechnical), "sales briefs are listed before technical ones");
        var token = new Regex(@"\{[a-z_]+\}", RegexOptions.CultureInvariant);
        foreach (var brief in BundledBriefs.All)
        {
            string raw = brief.ToJson().ToJsonString();
            c.That(!DeviceSpecific.Any(t => raw.Contains(t, StringComparison.OrdinalIgnoreCase)), $"{brief.Id}: names no particular device", DeviceSpecific.FirstOrDefault(t => raw.Contains(t, StringComparison.OrdinalIgnoreCase)) ?? "");
            c.That(brief.RequiredTopics.ContainsKey(BriefAdapter.ProvenanceTopic), $"{brief.Id}: asks for the measured slide");
            c.That(BriefAdapter.Warnings(brief, Story).Count == 0, $"{brief.Id}: no device warning on any PC");
            var adapted = BriefAdapter.Adapt(brief, Story);
            string text = adapted.ToJson().ToJsonString();
            c.That(!token.IsMatch(text) && text.Contains(Story.Name), $"{brief.Id}: device tokens resolve to this PC", token.Match(text).Value);
            c.That(Story.Facts.All(f => adapted.Facts.Contains(f)), $"{brief.Id}: this PC's facts are added");
            var deviceWords = adapted.RequiredTopics.Where(t => t.Key is "device" or "hardware").SelectMany(t => t.Value).ToList();
            c.That(deviceWords.Contains("snapdragon") && deviceWords.Contains("npu"), $"{brief.Id}: the device topic knows this PC's words", string.Join(",", deviceWords));
        }
        var named = new Brief { Objective = "A pilot of Surface Laptop Ultra with NVIDIA RTX Spark.", Facts = ["Device: Surface Laptop Ultra."] };
        c.That(BriefAdapter.Warnings(named, Story).Count >= 1, "a brief naming another device is warned about");
        c.That(BriefAdapter.Resolve("{device_full} runs {model} on {engine}, {lane} lane, {runtime}.", Story).Contains("Surface Laptop 7 with Snapdragon X Plus runs Qwen2.5 7B on Snapdragon NPU, npu lane,"), "device tokens are all filled in");
    }

    private static void Drafter(Check c)
    {
        string draft = """
        {"client":"Fabrikam","pitch_type":"sales","audience":"Fabrikam sales leaders.","objective":"Approve a 3-week pilot for 12 reps.",
         "context":"Reps write 40 reports a week. They lose 55% of the day to drafting.",
         "facts":["Fabrikam has 12 reps in the pilot.","Reps save 55% of drafting time."],
         "required_topics":[{"topic":"Pilot Ask","keywords":["Pilot","12 reps"]},{"topic":"device","keywords":["laptop"]}],
         "guardrails":["Never promise 99% accuracy.","Do not claim savings."]}
        """;
        var llm = new ScriptedLlm(ScriptedLlm.Model(), (_, _) => ScriptedLlm.Say(draft));
        var result = BriefDrafter.DraftAsync(llm, "Pitch Fabrikam on a 3-week pilot for 12 reps who write 40 reports a week.", new DraftOptions(Provenance: true)).GetAwaiter().GetResult();
        var brief = result.Brief;
        c.That(brief.Id == "fabrikam_sales" && brief.Client == "Fabrikam" && brief.Source == "prompt", "the drafted brief is named from the client and pitch");
        c.That(brief.Facts.SequenceEqual(["Fabrikam has 12 reps in the pilot."]), "a fact with a number the request never gave is dropped", string.Join("|", brief.Facts));
        c.That(!brief.Context.Contains("55") && brief.Context.Contains("40"), "a context sentence with an invented number is dropped", brief.Context);
        c.That(brief.Dropped.Count(d => d.StartsWith("fact:", StringComparison.Ordinal)) == 1 && brief.Dropped.Any(d => d.StartsWith("guardrail:", StringComparison.Ordinal)), "what was dropped is reported");
        c.That(!brief.Guardrails.Any(g => g.Contains("99")) && brief.Guardrails.Contains("Do not claim savings.") && BriefAdapter.DefaultGuardrails.All(brief.Guardrails.Contains), "guardrails keep the safe ones and gain the defaults");
        c.That(brief.RequiredTopics.ContainsKey("pilot_ask") && !brief.RequiredTopics.ContainsKey("device") && brief.RequiredTopics.ContainsKey(BriefAdapter.ProvenanceTopic), "topics are normalized and the measured-slide topic is added on request");
        c.That(llm.Requests.Count == 1 && llm.Requests[0].Schema is not null, "the brief is drafted in one constrained call");
        bool threw = false;
        try { BriefDrafter.DraftAsync(llm, "   ").GetAwaiter().GetResult(); } catch (ArgumentException) { threw = true; }
        c.That(threw, "an empty request is refused before any model call");
        var bad = new ScriptedLlm(ScriptedLlm.Model(), (_, _) => ScriptedLlm.Say("not json at all"));
        threw = false;
        try { BriefDrafter.DraftAsync(bad, "Pitch Fabrikam.").GetAwaiter().GetResult(); } catch (HarnessException) { threw = true; }
        c.That(threw, "a reply that is not a brief is reported");
    }

    private static void ProvenanceText(Check c)
    {
        c.That(Provenance.Duration(47) == "47 s" && Provenance.Duration(161) == "2 min 41 s", "durations read as seconds or minutes", $"{Provenance.Duration(47)} / {Provenance.Duration(161)}");
        c.That(Provenance.Short(47) == "47 s" && Provenance.Short(161) == "2m 41s", "a tile's time is the short form", $"{Provenance.Short(47)} / {Provenance.Short(161)}");
        c.That(Provenance.Count(1234567) == "1,234,567" && Provenance.Wh(12.34) == "12 Wh" && Provenance.Wh(0.456) == "0.46 Wh", "counts and energy are formatted");
        var stats = new HarnessStats { LlmCalls = 3, CompletionTokens = 1500, DecodeTokens = 1497, DecodeSeconds = 30 };
        string noMeter = Provenance.MeasuredFact(Story, stats, 61, EnergyReading.NotMeasured("none"));
        c.That(noMeter.Contains("Qwen2.5 7B") && noMeter.Contains("Snapdragon NPU") && noMeter.Contains("1 min 1 s") && noMeter.Contains("1,500 tokens") && noMeter.Contains("0 cloud calls"), "the measured fact states what was measured", noMeter);
        c.That(noMeter.Contains("tokens/s") && !noMeter.Contains(" Wh"), "without a power meter the fact gives decode speed, never energy", noMeter);
        string metered = Provenance.MeasuredFact(Story, stats, 61, new EnergyReading(true, 1.25, "board power"));
        c.That(metered.Contains("1.25 Wh") && !metered.Contains("tokens/s decode"), "with a power meter the fact gives the measured energy", metered);
        var slide = Provenance.Slide(Story, stats, 61, EnergyReading.NotMeasured("none"));
        var shown = Items(slide);
        c.That(Layout(slide) == "kpis" && TitleOf(slide) == "This Laptop Built This Deck" && shown.Count == 4, "the measured slide is KPIs under an honest title", string.Join(" | ", shown));
        c.That(shown.SequenceEqual(["0 Cloud calls", "1m 1s Drafting time", "1,500 Tokens generated", "50 Tokens per second"]), "without a meter the fourth KPI is the decode speed", string.Join(" | ", shown));
        var bare = Provenance.Slide(Story, new HarnessStats { CompletionTokens = 10 }, 5, EnergyReading.NotMeasured("none"));
        c.That(Items(bare).Count == 3, "with neither a speed nor a meter the slide shows only the three counted facts");
        var powered = Provenance.Slide(Story, stats, 61, new EnergyReading(true, 1.25, "GPU board power plus 10 W platform overhead"));
        c.That(Items(powered).Count == 4 && Items(powered)[3] == "1.25 Wh Energy used" && powered["notes"]!.GetValue<string>().Contains("GPU board power"), "a meter adds the energy and says how it was measured", string.Join(" | ", Items(powered)));
    }

    private static List<string> Items(JsonNode slide) =>
        [.. slide["data"]!["items"]!.AsArray().Select(i => i!["value"]!.GetValue<string>() + " " + i["label"]!.GetValue<string>())];

    // ---- guided mode ----

    private static void GuidedHappyPath(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var llm = Clean(arc);
        var progress = new ListProgress();
        var result = Go(llm, brief, progress: progress);
        var slides = Slides(result);

        c.That(slides.Count == arc.Count + 1, "one slide per slot plus the measured slide", $"{slides.Count} vs {arc.Count}");
        c.That(Layout(slides[0]) == "title" && Layout(slides[^1]) == "cta", "the deck opens on the title and ends on the call to action");
        c.That(Layout(slides[^2]) == "kpis" && TitleOf(slides[^2]) == "This Laptop Built This Deck", "the measured slide sits just before the call to action");
        c.That(Enumerable.Range(0, arc.Count).All(i => i >= arc.Count - 1 || Layout(slides[i]) == arc[i].Layout), "every slide uses its slot's layout");
        c.That(llm.Requests.Count == arc.Count && result.Stats.LlmCalls == arc.Count && result.Stats.Retries == 0, "a clean deck takes one call per slot", $"{llm.Requests.Count} calls");
        c.That(llm.Requests.All(r => r.Schema is not null && r.MaxTokens <= 700), "every guided call carries a schema and a small token budget");
        c.That(result.Mode == HarnessMode.Guided && result.GuidedDecoding && result.StrictJson, "guided mode, constrained decoding, strict JSON");
        c.That(result.LintRemaining.Count == 0 && result.DraftIssues.Count == 0, "a model that follows the prompt leaves nothing for lint", Issues(result.LintRemaining));
        c.That(result.Repairs.Count == 0, "no repair pass runs on a clean deck");
        c.That(result.MeasuredFact is not null && result.Brief.Facts[^1] == result.MeasuredFact && result.MeasuredFact.Contains("0 cloud calls"), "the measured fact is part of the graded brief");
        c.That(result.Stats.PromptTokens == 900 * arc.Count && result.Stats.CompletionTokens == 120 * arc.Count && Math.Abs(result.Stats.DecodeTokensPerSecond - 119 / 3.0) < 0.1, "token counts and decode speed come from the replies", $"{result.Stats.DecodeTokensPerSecond}");
        c.That(result.Grade is not null && result.Grade.AutoScore > 0, "the deck is graded", result.Grade?.AutoGrade ?? "");
        c.That(Slides(result).Count == result.RenderSummary.Count, "the render summary covers every slide");

        var steps = progress.Items.Where(p => p.Phase == "draft").ToList();
        c.That(steps.Count > 0 && steps.Select(p => p.Step).Max() == arc.Count && steps.All(p => p.Total == arc.Count), "progress counts the slides as they are written");

        var bytes = PptxWriter.ToBytes(DeckBuilder.Build(result.Spec, Theme.Surface));
        var problems = PptxPackage.Validate(bytes);
        c.That(problems.Count == 0, "the finished deck is a valid PPTX", string.Join("; ", problems.Take(3)));

        // The same story on a technical brief.
        var (tbrief, tarc) = Setup("zava_technical");
        var technical = Go(Clean(tarc), tbrief);
        c.That(Slides(technical).Count == tarc.Count + 1 && technical.LintRemaining.Count == 0, "the technical story is clean too", $"{Slides(technical).Count} slides; {Issues(technical.LintRemaining)}");
    }

    private static void RepairAccepted(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var llm = Clean(arc, special: (r, n) =>
            DeckScript.SlotOf(r) is { Index: 2 } && !DeckScript.IsRepair(r) ? ScriptedLlm.Say(WithBreakage(DeckScript.Reply(r, arc), true, true).ToJsonString()) : null);
        var result = Go(llm, brief);
        c.That(result.DraftIssues.Count == 2 && result.DraftIssues.All(i => i.Slide == 2), "the draft has the two planted findings", Issues(result.DraftIssues));
        c.That(result.Repairs.Count == 1 && result.Repairs[0].Accepted && result.Repairs[0].IssuesBefore == 2 && result.Repairs[0].IssuesAfter == 0, "the repair is accepted when the findings drop", string.Join(",", result.Repairs.Select(r => $"{r.IssuesBefore}->{r.IssuesAfter}")));
        c.That(result.Repairs[0].FixedSlides.SequenceEqual([2]), "the repair reports which slide it fixed");
        c.That(result.LintRemaining.Count == 0 && TitleOf(Slides(result)[2]) == "A Second Lane Beside The Cloud", "the fixed slide replaced the broken one");
        c.That(llm.Requests.Count == arc.Count + 1, "one extra call repaired it", $"{llm.Requests.Count}");
        var repair = llm.Requests.Last();
        c.That(repair.User.Contains("YOUR PREVIOUS ATTEMPT") && repair.User.Contains("FIX THESE ISSUES") && repair.User.Contains("title reads as a label"), "the repair shows the model its attempt and what was wrong");
        c.That(repair.Temperature < llm.Requests[0].Temperature, "repairs run cooler than the first draft");
    }

    private static void RepairRejected(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var llm = Clean(arc, special: (r, n) =>
        {
            if (DeckScript.SlotOf(r) is not { Index: 3 }) return null;
            return ScriptedLlm.Say(WithBreakage(DeckScript.Reply(r, arc), true, DeckScript.IsRepair(r)).ToJsonString());
        });
        var result = Go(llm, brief);
        c.That(result.DraftIssues.Count == 1, "the draft has one finding", Issues(result.DraftIssues));
        c.That(result.Repairs.Count == 1 && !result.Repairs[0].Accepted && result.Repairs[0].FixedSlides.Count == 0, "a repair that makes the slide worse is not accepted", Issues(result.LintRemaining));
        c.That(result.LintRemaining.Count == 1 && !string.IsNullOrEmpty(Slides(result)[3]["notes"]?.GetValue<string>()), "the deck keeps the draft slide, never a worse one");
        c.That(llm.Requests.Count == arc.Count + 1, "a rejected pass is the last pass", $"{llm.Requests.Count}");
    }

    /// <summary>A repair that trades a missing thing for a small one (here missing notes for a title that reads as a label) is an improvement
    /// even though the number of findings stays the same.</summary>
    private static void RepairWeighted(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var llm = Clean(arc, special: (r, n) =>
        {
            if (DeckScript.SlotOf(r) is not { Index: 2 }) return null;
            string reply = DeckScript.Reply(r, arc);
            if (!DeckScript.IsRepair(r)) return ScriptedLlm.Say(WithBreakage(reply, false, true).ToJsonString());
            return r.User.Contains("add 2-3 sentences of speaker notes") ? ScriptedLlm.Say(WithBreakage(reply, true, false).ToJsonString()) : null;
        });
        var result = Go(llm, brief);
        c.That(result.DraftIssues.Count == 1 && result.DraftIssues[0].Code == "notes", "the draft is missing the notes on one slide", Issues(result.DraftIssues));
        string passes = string.Join(",", result.Repairs.Select(r => $"{r.IssuesBefore}->{r.IssuesAfter} {(r.Accepted ? "kept" : "dropped")}"));
        c.That(result.Repairs.Count == 2 && result.Repairs[0] is { Accepted: true, IssuesBefore: 1, IssuesAfter: 1 }, "a missing thing traded for a small one is kept", passes);
        c.That(result.Repairs.Count == 2 && result.Repairs[1].Accepted && result.LintRemaining.Count == 0, "and the next pass finishes the job", $"{passes}; {Issues(result.LintRemaining)}");
    }

    /// <summary>A slide rewritten for some feedback that did not improve is not asked again for the same feedback: the model answers the same way.</summary>
    private static void RepairNotRepeated(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        // Slide 2 learns from its repair; slide 3 never does.
        var llm = Clean(arc, special: (r, n) =>
            DeckScript.SlotOf(r) is { Index: 2 or 3 } slot && !(slot.Index == 2 && DeckScript.IsRepair(r))
                ? ScriptedLlm.Say(WithBreakage(DeckScript.Reply(r, arc), true, false).ToJsonString()) : null);
        var result = Go(llm, brief);
        c.That(result.DraftIssues.Select(i => i.Slide).Order().SequenceEqual([2, 3]), "two slides have a title that reads as a label", Issues(result.DraftIssues));
        c.That(result.Repairs.Count == 1 && result.Repairs[0].FixedSlides.SequenceEqual([2]), "one of them is fixed", string.Join(",", result.Repairs.Select(r => $"{r.IssuesBefore}->{r.IssuesAfter} {string.Join("/", r.FixedSlides)}")));
        c.That(llm.Requests.Count == arc.Count + 2, "the other is tried once and not asked again with the same feedback", $"{llm.Requests.Count} calls");
        c.That(result.LintRemaining.Count == 1 && result.LintRemaining[0].Slide == 3, "its finding is still reported", Issues(result.LintRemaining));
    }

    private static readonly string[] Filler = ["with", "extra", "care", "for", "each", "step", "of", "the", "work"];

    /// <summary>Lengthens the details of a slide by about <paramref name="extra"/> words in all, leaving the title and takeaway (which have limits of their own) alone.</summary>
    private static JsonObject Pad(string slideJson, int extra)
    {
        var slide = Parse(slideJson);
        var details = new List<JsonObject>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["detail"] is JsonValue) details.Add(o);
                    foreach (var child in o.Select(p => p.Value).ToList()) Walk(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) Walk(child);
                    break;
            }
        }
        Walk(slide["data"]);
        for (int i = 0; i < details.Count; i++)
        {
            int share = extra / details.Count + (i < extra % details.Count ? 1 : 0);
            details[i]["detail"] = details[i]["detail"]!.GetValue<string>() + " " + string.Join(' ', Enumerable.Range(0, share).Select(k => Filler[k % Filler.Length]));
        }
        return slide;
    }

    /// <summary>A small model aims at 28 words a slide and lands near 40, so the harness flags a slide only past 48; and a slide that is merely
    /// wordy is reported but never rewritten, because a rewrite rarely makes it shorter.</summary>
    private static void WordLimits(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        int at = arc.ToList().FindIndex(s => s.Slot == "device");
        int baseline = Go(Clean(arc), brief).RenderSummary.Single(s => s.Index == at).Words;
        ScriptedLlm Wordy() => Clean(arc, special: (r, n) =>
            DeckScript.SlotOf(r) is { } slot && slot.Index == at ? ScriptedLlm.Say(Pad(DeckScript.Reply(r, arc), 40 - baseline).ToJsonString()) : null);

        var relaxed = Go(Wordy(), brief);
        int words = relaxed.RenderSummary.Single(s => s.Index == at).Words;
        c.That(words is > Lint.WordBudget and <= 48, "the slide is over the lint's default budget and inside the harness's", $"{words} words from {baseline}");
        c.That(!relaxed.DraftIssues.Any(i => i.Code == "words") && relaxed.Repairs.Count == 0, "so the harness lets it be", Issues(relaxed.DraftIssues));

        var model = Wordy();
        var strict = Go(model, brief, new HarnessOptions { WordLimit = Lint.WordBudget });
        c.That(strict.DraftIssues.Any(i => i.Slide == at && i.Code == "words"), "a stricter limit flags it", Issues(strict.DraftIssues));
        c.That(strict.Repairs.Count == 0 && model.Requests.Count == arc.Count && strict.LintRemaining.Any(i => i.Code == "words"), "but a slide that is only wordy costs no model call and is still reported", $"{model.Requests.Count} calls");
    }

    private static IEnumerable<string> Texts(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (string text in o.SelectMany(p => Texts(p.Value))) yield return text;
                break;
            case JsonArray a:
                foreach (string text in a.SelectMany(Texts)) yield return text;
                break;
            case JsonValue v when v.TryGetValue<string>(out string? s):
                yield return s;
                break;
        }
    }

    /// <summary>A slide as a small model sometimes writes it: a Chinese phrase run onto the end of the takeaway.</summary>
    private static JsonObject Leak(string slideJson)
    {
        var slide = Parse(slideJson);
        slide["takeaway"] = slide["takeaway"]!.GetValue<string>() + "分工明确";
        return slide;
    }

    /// <summary>Constrained decoding keeps a reply valid JSON, not English, and a small model can slip into Chinese mid-sentence. The slip is a
    /// hard finding the repair is asked to fix; one that survives the repair is removed before the deck is drawn, never shipped.</summary>
    private static void ForeignScript(Check c)
    {
        string leaky = "NPU, GPU, CPU分工明确, 高效协同";
        c.That(Script.Runs(leaky).SequenceEqual(["分工明确", "高效协同"]) && Script.Find(leaky) == "分工明确", "runs of foreign text are found", string.Join("|", Script.Runs(leaky)));
        c.That(Script.Strip(leaky) == "NPU, GPU, CPU", "and removed, with the comma they leave behind", Script.Strip(leaky));
        c.That(Script.Strip("Fast 本地 inference (本地) stays local") == "Fast inference stays local", "an emptied bracket goes with them", Script.Strip("Fast 本地 inference (本地) stays local"));
        c.That(Script.Find("Привет мир") == "Привет мир" && Script.Find("한국어") == "한국어" && Script.Find("𠀀") == "𠀀" && Script.Find("5 µs, 30 Ω, π, café and naïve") == "",
            "Cyrillic, Hangul and the supplementary Han planes are a slip; Greek letters and accents are deck text");
        c.That(Script.Find("٣٤٥ and １２ and a\u3000space") == "", "digits and spaces in any script are not a slip (the numbers check reads digits)");
        c.That(Script.Find("株式会社 Contoso", "Client: 株式会社 Contoso") == "" && Script.Strip("株式会社 Contoso 分工明确", "Client: 株式会社 Contoso") == "株式会社 Contoso",
            "text the brief itself contains is the client's own and is left alone");
        var tree = Parse("""{"title":"NPU分工明确","notes":"ok","data":{"items":[{"label":"高效协同","detail":"Fast"},{"label":"Local","value":3}]}}""");
        c.That(Script.Scrub(tree) == 2 && tree["title"]!.GetValue<string>() == "NPU" && tree["data"]!["items"]![0]!["label"]!.GetValue<string>() == "" && Texts(tree).All(t => Script.Find(t) == ""),
            "a spec is cleaned in place, every string in it, and the count says how many changed", tree.ToJsonString());

        var (brief, arc) = Setup("contoso_sales");
        var clean = Go(Clean(arc), brief);
        var llm = Clean(arc, special: (r, n) =>
            DeckScript.SlotOf(r) is { Index: 2 } && !DeckScript.IsRepair(r) ? ScriptedLlm.Say(Leak(DeckScript.Reply(r, arc)).ToJsonString()) : null);
        var result = Go(llm, brief);
        c.That(result.DraftIssues.Count == 1 && result.DraftIssues[0] is { Slide: 2, Code: "script" } && Lint.SeverityOf("script") == LintSeverity.Hard, "the slip is a hard finding on its slide", Issues(result.DraftIssues));
        c.That(result.Repairs.Count == 1 && result.Repairs[0].Accepted && result.Repairs[0].FixedSlides.SequenceEqual([2]), "and a repair that writes it in English is accepted", string.Join(",", result.Repairs.Select(r => $"{r.IssuesBefore}->{r.IssuesAfter}")));
        c.That(llm.Requests.Last().User.Contains("write every word in English"), "the repair tells the model what to do");
        c.That(Texts(result.Spec).All(t => Script.Find(t) == "") && !result.Warnings.Any(w => w.Contains("non-English")), "nothing is left to remove", string.Join("; ", result.Warnings));

        var stubborn = Clean(arc, special: (r, n) => DeckScript.SlotOf(r) is { Index: 2 } ? ScriptedLlm.Say(Leak(DeckScript.Reply(r, arc)).ToJsonString()) : null);
        var kept = Go(stubborn, brief);
        c.That(kept.Repairs.Count == 1 && !kept.Repairs[0].Accepted && stubborn.Requests.Count == arc.Count + 1, "a model that keeps slipping is asked once", $"{stubborn.Requests.Count} calls");
        c.That(Texts(kept.Spec).All(t => Script.Find(t) == "") && kept.LintRemaining.All(i => i.Code != "script"), "the foreign text is removed before the deck is drawn");
        string takeaway = Slides(kept)[2]["takeaway"]!.GetValue<string>();
        c.That(takeaway == Slides(clean)[2]["takeaway"]!.GetValue<string>(), "and the line reads as it would have without it", takeaway);
        c.That(kept.Warnings.Any(w => w.Contains("Removed non-English text") && w.Contains("1 line")), "the removal is reported", string.Join("; ", kept.Warnings));

        var japanese = Brief.FromJson(brief.ToJson());
        japanese.Client = "株式会社コントソ";
        var line = DeckScript.Slide("kpis", "value", [], 1);
        line["layout"] = "kpis";
        line["takeaway"] = "Built for 株式会社コントソ";
        JsonObject DeckOf() => new() { ["slides"] = new JsonArray(line.DeepClone()) };
        var findings = Lint.Run(DeckOf(), japanese, []);
        c.That(findings.All(i => i.Code != "script"), "a client's own name in its own script is not a finding", Issues(findings));
        findings = Lint.Run(DeckOf(), brief, []);
        c.That(findings.Any(i => i.Slide == 0 && i.Code == "script" && i.Msg.Contains("株式会社コントソ")), "the same line is a finding for a client written in English", Issues(findings));
    }

    private static void BadJsonThenGood(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        int tries = 0;
        var llm = Clean(arc, special: (r, n) => DeckScript.SlotOf(r) is { Index: 1 } && tries++ == 0 ? ScriptedLlm.Say("Sorry, I can't help with that.") : null);
        var result = Go(llm, brief);
        c.That(result.Stats.Retries == 1 && result.Stats.LlmCalls == arc.Count + 1, "a reply that is not JSON costs one retry", $"{result.Stats.Retries} retries, {result.Stats.LlmCalls} calls");
        var second = llm.Requests[2];
        c.That(second.User.Contains("Sorry, I can't help with that.") && second.User.Contains("not a JSON object"), "the retry shows the model what it said and what was wrong");
        c.That(result.LintRemaining.Count == 0 && !result.Warnings.Any(w => w.Contains("placeholder")), "the retried slide is as good as any other");
    }

    private static void PlaceholderOnFailure(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var llm = Clean(arc, special: (r, n) => DeckScript.SlotOf(r) is { Index: 1 } ? ScriptedLlm.Say("nope") : null);
        var result = Go(llm, brief);
        var slides = Slides(result);
        c.That(slides.Count == arc.Count + 1 && TitleOf(slides[1]) == "Pain", "a slot the model cannot write is drawn as a placeholder, the deck still builds");
        c.That(result.Warnings.Any(w => w.Contains("placeholder")), "the placeholder is reported");
        c.That(result.LintRemaining.Any(i => i.Slide == 1), "lint still flags the placeholder", Issues(result.LintRemaining));
        c.That(result.Repairs.Count >= 1 && result.Repairs.All(r => !r.FixedSlides.Contains(1)), "no repair pass managed to fix the placeholder", string.Join(",", result.Repairs.Select(r => string.Join("/", r.FixedSlides))));
        // The missing topics are deck-level findings, so another slide may legitimately be repaired to cover them.
        var again = llm.Requests.Skip(arc.Count + 1).Where(r => DeckScript.SlotOf(r) is { Index: 1 }).ToList();
        c.That(again.Count == 2 * result.Repairs.Count && again.Where((_, k) => k % 2 == 0).All(r => !r.User.Contains("YOUR PREVIOUS ATTEMPT")), "a repair of a placeholder does not show the placeholder to the model as its attempt", $"{again.Count} repair calls");
        c.That(again.Where((_, k) => k % 2 == 1).All(r => r.User.Contains("nope")), "the second try shows what the first one said");
    }

    private static void TopicRepair(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var words = BriefAdapter.Adapt(brief, Story).RequiredTopics["connectivity"];
        string Censor(string text) => words.Aggregate(text, (t, w) => Regex.Replace(t, Regex.Escape(w), "x", RegexOptions.IgnoreCase));
        // The model never works a "connectivity" word into the draft; it does once the repair names the topic.
        var llm = Clean(arc, special: (r, n) =>
        {
            if (DeckScript.SlotOf(r) is not { } slot) return null;
            var topics = DeckScript.TopicWords(r);
            bool told = DeckScript.IsRepair(r) && r.User.Contains("topic 'connectivity'");
            string slide = DeckScript.Slide(arc[slot.Index].Layout, slot.Slot, told ? [.. topics, words[0]] : topics, slot.Index + 1).ToJsonString();
            return ScriptedLlm.Say(told ? slide : Censor(slide));
        });
        var result = Go(llm, brief);
        c.That(result.DraftIssues.Count == 1 && result.DraftIssues[0].Code == "topic" && result.DraftIssues[0].Msg.Contains("'connectivity'"), "the lint reports the one topic nobody covered", Issues(result.DraftIssues));
        int fits = arc.ToList().FindIndex(s => s.Slot == "connectivity");
        c.That(result.Repairs.Count == 1 && result.Repairs[0].Accepted && result.Repairs[0].FixedSlides.SequenceEqual([fits]), "the topic is repaired on the one slide that fits it best", string.Join(",", result.Repairs.Select(r => $"{r.IssuesBefore}->{r.IssuesAfter} {string.Join("/", r.FixedSlides)}")));
        c.That(result.LintRemaining.Count == 0, "the deck is clean after the repair", Issues(result.LintRemaining));
        var asked = llm.Requests.Last();
        c.That(DeckScript.SlotOf(asked) is { Slot: "connectivity" } && asked.User.Contains("FIX THESE ISSUES") && asked.User.Contains(words[0]), "the repair names the topic and the words that would cover it");
    }

    private static void GuidanceRejected(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var llm = Clean(arc, special: (r, n) => ScriptedLlm.Say(DeckScript.Reply(r, arc), guided: false));
        var result = Go(llm, brief);
        c.That(llm.Requests[0].Schema is not null, "the first call asks for constrained decoding");
        c.That(llm.Requests.Skip(1).All(r => r.Schema is null), "once the runtime refuses, no later call sends a schema");
        c.That(llm.Requests.Skip(1).All(r => r.System.Contains("ICONS:")) && !llm.Requests[0].System.Contains("ICONS:"), "without a schema the prompt lists the valid icons");
        c.That(llm.Requests.Skip(1).All(r => r.MaxTokens > 700), "without a schema each call gets a larger budget");
        c.That(!result.GuidedDecoding && result.Warnings.Any(w => w.Contains("constrained decoding")), "the run says it fell back to plain prompts");
        c.That(Slides(result).Count == arc.Count + 1, "the deck is still complete");
    }

    private static void OptionsRespected(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var off = Clean(arc);
        var result = Go(off, brief, new HarnessOptions { UseGuidance = false });
        c.That(off.Requests.All(r => r.Schema is null && r.System.Contains("ICONS:")), "guidance can be switched off up front");
        c.That(!result.GuidedDecoding, "an unguided run does not claim constrained decoding");

        var none = Clean(arc, special: (r, n) => DeckScript.SlotOf(r) is { Index: 2 } ? ScriptedLlm.Say(WithBreakage(DeckScript.Reply(r, arc), true, false).ToJsonString()) : null);
        var noRepair = Go(none, brief, new HarnessOptions { MaxRepairPasses = 0 });
        c.That(noRepair.Repairs.Count == 0 && none.Requests.Count == arc.Count && noRepair.LintRemaining.Count == 1, "with no repair passes the draft is kept as written");

        var generic = Clean(arc, guidance: false);
        Go(generic, brief);
        c.That(generic.Requests.All(r => r.Schema is null), "a model that cannot do constrained decoding is never sent a schema");

        var slow = new ScriptedLlm(ScriptedLlm.Model(), (r, n) => n == 1 ? ScriptedLlm.Say("") : ScriptedLlm.Say(DeckScript.Reply(r, arc)));
        c.That(Go(slow, brief).Stats.Retries == 1, "an empty reply is retried once");

        var themed = Go(Clean(arc), brief, new HarnessOptions { Theme = Theme.Microsoft });
        c.That(themed.RenderSummary.Count == arc.Count + 1, "a chosen theme is used for the dry render");
    }

    private static void Windows(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        // A tokenizer that counts 3.5 characters a token, which is also the harness's first guess, so its estimates are exact here.
        ScriptedLlm Sized(IReadOnlyList<ArcSlot> slots, int context) =>
            new(ScriptedLlm.Model(context: context), (r, n) => ScriptedLlm.Say(DeckScript.Reply(r, slots), promptTokens: (int)((r.System.Length + r.User.Length) / 3.5)));
        int Worst(ScriptedLlm llm) => llm.Requests.Max(r => (int)((r.System.Length + r.User.Length) / 3.5) + r.MaxTokens);

        var large = Sized(arc, 32768);
        c.That(Go(large, brief).ContextLevel == 0, "a large window keeps full prompts");
        c.That(large.Requests.Any(r => r.User.Contains("EXEMPLAR (fictional")) && large.Requests.Any(r => r.User.Contains("ANALOGY BANK\n")), "full prompts carry the exemplars and the analogy bank");

        var small = Sized(arc, 2048);
        var result = Go(small, brief);
        c.That(result.ContextLevel >= 2 && Slides(result).Count == arc.Count + 1, "a 2K window gets compact prompts and still produces the deck", $"level {result.ContextLevel}");
        c.That(Worst(small) <= 2048, "no prompt and reply is planned that could not fit a 2K window", $"{Worst(small)} tokens");
        c.That(Worst(large) <= 3000, "even a full guided prompt and its reply stay near 2K tokens", $"{Worst(large)} tokens");

        var (tbrief, tarc) = Setup("zava_technical");
        var technical = Sized(tarc, 2048);
        c.That(Go(technical, tbrief).ContextLevel >= 2 && Worst(technical) <= 2048, "the technical brief also fits a 2K window", $"{Worst(technical)} tokens");

        var unknown = Sized(arc, 0);
        c.That(Go(unknown, brief).ContextLevel == 2, "an unknown window starts compact");
        c.That(unknown.Requests.All(r => !r.User.Contains("EXEMPLAR (fictional") && !r.User.Contains("ANALOGY BANK\n")), "compact prompts leave out the exemplars and the bank");
        c.That(unknown.Requests.Where(r => DeckScript.SlotOf(r) is { Slot: "big_idea" }).All(r => !r.User.Contains("like the ANALOGY BANK")), "and no longer point the model at a bank that is not there");

        int thrown = 0;
        var tooLong = Clean(arc, context: 0, special: (r, n) => thrown++ == 0 ? throw new PromptTooLongException(9000, 8192, 900) : null);
        var bumped = Go(tooLong, brief);
        c.That(bumped.ContextLevel == 3 && Slides(bumped).Count == arc.Count + 1, "a prompt the runtime refuses is compacted one level and retried", $"level {bumped.ContextLevel}");

        var never = new ScriptedLlm(ScriptedLlm.Model(), (_, _) => throw new PromptTooLongException(9000, 2048, 7000));
        c.That(Throws<HarnessException>(() => Go(never, brief)), "a window that cannot hold even the smallest prompt is reported");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Failures(Check c)
    {
        var (brief, _) = Setup("contoso_sales");
        var dead = new ScriptedLlm(ScriptedLlm.Model(), (_, _) => throw new InvalidOperationException("runtime crashed"));
        var failure = Record.Exception(() => Go(dead, brief));
        c.That(failure is HarnessException h && h.Message.Contains("runtime crashed") && dead.Requests.Count == 3, "a model that keeps failing stops the run after three tries", $"{dead.Requests.Count} calls: {failure?.GetType().Name}");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = new ScriptedLlm(ScriptedLlm.Model(), (_, _) => ScriptedLlm.Say("{}"));
        c.That(Record.Exception(() => DeckHarness.RunAsync(cancelled, Story, brief, ct: cts.Token).GetAwaiter().GetResult()) is OperationCanceledException && cancelled.Requests.Count == 0, "a cancelled run stops before calling the model");

        var (_, arc) = Setup("contoso_sales");
        var half = new CancellationTokenSource();
        var cancelsMidway = new ScriptedLlm(ScriptedLlm.Model(), (r, n) =>
        {
            if (n == 2) half.Cancel();
            return ScriptedLlm.Say(DeckScript.Reply(r, arc));
        });
        c.That(Record.Exception(() => DeckHarness.RunAsync(cancelsMidway, Story, brief, ct: half.Token).GetAwaiter().GetResult()) is OperationCanceledException && cancelsMidway.Requests.Count == 3, "cancelling between slides stops the run", $"{cancelsMidway.Requests.Count}");
    }

    // ---- whole-deck modes ----

    private static void ArcWhole(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var adapted = BriefAdapter.Adapt(brief, Story);
        var llm = new ScriptedLlm(ScriptedLlm.Model(context: 65536), (r, n) => ScriptedLlm.Say(DeckScript.Deck(adapted, arc).ToJsonString(), completionTokens: 2600));
        var result = Go(llm, brief, new HarnessOptions { Mode = HarnessMode.Arc });
        c.That(result.Mode == HarnessMode.Arc && llm.Requests.Count == 1 && llm.Requests[0].Purpose == "deck (arc)", "the arc mode writes the whole story in one call", $"{llm.Requests.Count} calls");
        c.That(Slides(result).Count == arc.Count + 1 && result.LintRemaining.Count == 0, "the one-call deck is complete and clean", Issues(result.LintRemaining));
        c.That(llm.Requests[0].MaxTokens >= 8192 && llm.Requests[0].Schema is not null && llm.Requests[0].Schema!.ToJsonString().Contains("slides"), "the whole-deck call has a large budget and asks for a deck-shaped reply");

        var oneShot = new ScriptedLlm(ScriptedLlm.Model(context: 65536), (r, n) => ScriptedLlm.Say(DeckScript.Deck(adapted, arc).ToJsonString(), completionTokens: 2600));
        var v10 = Go(oneShot, brief, new HarnessOptions { Mode = HarnessMode.OneShot });
        c.That(v10.Mode == HarnessMode.OneShot && oneShot.Requests[0].Purpose == "deck (v1.0)" && Slides(v10).Count == arc.Count + 1, "the one-shot mode uses the v1.0 prompt");

        var small = new ScriptedLlm(ScriptedLlm.Model(context: 8192), (r, n) => ScriptedLlm.Say(DeckScript.Reply(r, arc)));
        var downgraded = Go(small, brief, new HarnessOptions { Mode = HarnessMode.Arc });
        c.That(downgraded.Mode == HarnessMode.Guided && downgraded.Warnings.Any(w => w.Contains("too small")) && small.Requests.All(r => DeckScript.SlotOf(r) is not null), "a window too small for the whole story is written slide by slide");
    }

    private static void ArcFallsBack(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var llm = new ScriptedLlm(ScriptedLlm.Model(context: 65536), (r, n) =>
            DeckScript.SlotOf(r) is null ? ScriptedLlm.Say("Here is your deck, enjoy!") : ScriptedLlm.Say(DeckScript.Reply(r, arc)));
        var result = Go(llm, brief, new HarnessOptions { Mode = HarnessMode.Arc });
        c.That(result.Mode == HarnessMode.Guided && result.Warnings.Any(w => w.Contains("slide by slide")), "two unusable whole-deck replies fall back to guided mode");
        c.That(llm.Requests.Count == 2 + arc.Count && result.Stats.Retries == 2 && Slides(result).Count == arc.Count + 1, "the fallback costs the failed tries plus one call per slot", $"{llm.Requests.Count} calls, {result.Stats.Retries} retries");
        c.That(llm.Requests[1].User.Contains("not valid JSON"), "the second whole-deck try says what was wrong with the first");
    }

    private static void ArcFixes(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var adapted = BriefAdapter.Adapt(brief, Story);
        var deck = DeckScript.Deck(adapted, arc);
        var slides = deck["slides"]!.AsArray();
        var good = (JsonObject)slides[2]!.DeepClone();
        good.Remove("layout");
        slides[2]!["notes"] = "";
        slides.Insert(8, slides[7]!.DeepClone());
        JsonObject Fixes(JsonObject replacement) => new()
        {
            ["fixes"] = new JsonArray(
                new JsonObject { ["index"] = 8, ["slide"] = new JsonObject { ["delete"] = true } },
                new JsonObject { ["index"] = 2, ["slide"] = replacement }),
        };

        var llm = new ScriptedLlm(ScriptedLlm.Model(context: 65536), (r, n) => ScriptedLlm.Say(r.Purpose == "repair (arc)" ? Fixes(good).ToJsonString() : deck.ToJsonString()));
        var result = Go(llm, brief, new HarnessOptions { Mode = HarnessMode.Arc });
        c.That(result.DraftIssues.Select(i => i.Code).Order().SequenceEqual(["count", "notes"]), "the draft has a size finding and a notes finding", Issues(result.DraftIssues));
        c.That(result.Repairs.Count == 1 && result.Repairs[0].Accepted && result.Repairs[0].FixedSlides.Order().SequenceEqual([2, 8]), "the repair applies a replacement and a deletion", string.Join(",", result.Repairs.Select(r => $"{r.IssuesBefore}->{r.IssuesAfter} {string.Join("/", r.FixedSlides)}")));
        var fixedSlides = Slides(result);
        c.That(fixedSlides.Count == arc.Count + 1 && Layout(fixedSlides[2]) == "analogy" && fixedSlides[2]["notes"]!.GetValue<string>().Length > 0, "a replacement without a layout keeps the old slide's layout");
        c.That(result.LintRemaining.Count == 0 && llm.Requests.Count == 2, "the repaired deck is clean after one repair call", $"{llm.Requests.Count} calls; {Issues(result.LintRemaining)}");
        c.That(llm.Requests[1].System.Contains("\"delete\":true"), "the repair prompt explains how to delete a slide");

        var worse = new JsonObject { ["fixes"] = new JsonArray(new JsonObject { ["index"] = 2, ["slide"] = new JsonObject { ["title"] = "x" } }) };
        var stubborn = new ScriptedLlm(ScriptedLlm.Model(context: 65536), (r, n) => ScriptedLlm.Say(r.Purpose == "repair (arc)" ? worse.ToJsonString() : deck.ToJsonString()));
        var kept = Go(stubborn, brief, new HarnessOptions { Mode = HarnessMode.Arc });
        c.That(kept.Repairs.Count == 1 && !kept.Repairs[0].Accepted && Slides(kept)[2]["notes"]!.GetValue<string>() == "", "a repair that adds findings is discarded and the draft stays", Issues(kept.LintRemaining));
        c.That(kept.LintRemaining.Count == kept.DraftIssues.Count, "the findings of a discarded repair are the draft's");

        var noFixes = new ScriptedLlm(ScriptedLlm.Model(context: 65536), (r, n) => ScriptedLlm.Say(r.Purpose == "repair (arc)" ? "I think it looks fine." : deck.ToJsonString()));
        var ignored = Go(noFixes, brief, new HarnessOptions { Mode = HarnessMode.Arc });
        c.That(ignored.Repairs.Count == 1 && !ignored.Repairs[0].Accepted && ignored.Stats.Retries >= 1, "a repair reply with no fixes ends the repair");
    }

    // ---- energy and metrics ----

    private static void Energy(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var meter = new FixedMeter(new EnergyReading(true, 2.5, "GPU board power plus 10 W platform overhead"));
        var result = Go(Clean(arc), brief, meter: meter);
        c.That(meter.Started && result.Energy.Measured && result.Energy.Wh == 2.5, "a power meter is started and read");
        var items = Slides(result)[^2]["data"]!["items"]!.AsArray().Select(i => i!["value"]!.GetValue<string>() + " " + i["label"]!.GetValue<string>()).ToList();
        c.That(items.Any(i => i.Contains("2.5 Wh")) && result.MeasuredFact!.Contains("2.5 Wh"), "the measured energy is on the measured slide", string.Join(" | ", items));
        c.That(!Slides(result)[^2]["notes"]!.GetValue<string>().Contains("not measured"), "the notes do not say energy was unmeasured");

        var failing = Go(Clean(arc), brief, meter: new FixedMeter(EnergyReading.NotMeasured("x"), failOnStop: true));
        c.That(!failing.Energy.Measured && failing.Energy.Basis.Contains("sensor gone"), "a meter that fails is reported as not measured");

        var plain = Go(Clean(arc), brief);
        var plainItems = Slides(plain)[^2]["data"]!["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>()).ToList();
        c.That(!plain.Energy.Measured && !plainItems.Any(l => l.Contains("Wh")) && plainItems.Any(l => l.Contains("tokens/s") || l.Contains("per second")), "without a meter the slide shows speed, never an energy figure", string.Join(" | ", plainItems));
        c.That(Economics.Local(plain.Energy, 1000, 1000)["energy_wh"] is null, "unmeasured energy has no cost estimate");
        c.That(Economics.Cloud("claude-opus-5.5", 1000, 1000)["cost_usd"] is not null, "a cloud comparison row prices tokens");
    }

    private static void MetricsShape(Check c)
    {
        var (brief, arc) = Setup("contoso_sales");
        var llm = Clean(arc, special: (r, n) => DeckScript.SlotOf(r) is { Index: 2 } && !DeckScript.IsRepair(r) ? ScriptedLlm.Say(WithBreakage(DeckScript.Reply(r, arc), true, false).ToJsonString()) : null);
        var result = Go(llm, brief);
        var metrics = Metrics.Build(result, Story, "surface", 1.5, "C:\\out\\deck.pptx");
        string[] keys = ["harness", "brief", "template", "device", "lane", "model", "runtime", "context_tokens", "guided_decoding", "llm_calls", "retries", "prompt_tokens", "completion_tokens",
            "decode_tps", "ttft_s", "generate_s", "render_s", "total_s", "power", "economics", "grade", "pptx", "render_summary", "lint_history", "lint_remaining", "measured_facts", "warnings"];
        c.That(keys.All(k => metrics.ContainsKey(k)), "the metrics file carries the documented fields", string.Join(",", keys.Where(k => !metrics.ContainsKey(k))));
        c.That(metrics["harness"]!.GetValue<string>() == "guided" && metrics["lane"]!.GetValue<string>() == "npu" && metrics["device"]!.GetValue<string>() == "Surface Laptop 7 with Snapdragon X Plus", "the run is described by its mode, lane and device");
        c.That(metrics["power"]!["measured"]!.GetValue<bool>() == false && metrics["power"]!["reason"] is not null, "unmeasured power says why");
        var comparison = metrics["economics"]!["cloud_comparison"]!.AsArray();
        c.That(comparison.Count >= 2 && comparison.All(row => row!["estimate"]!.GetValue<bool>()), "cloud figures are flagged as estimates");
        c.That(metrics["economics"]!["assumptions"] is not null, "the economics carry their assumptions");
        var history = metrics["lint_history"]!.AsArray();
        c.That(history[0]!["pass"]!.GetValue<string>() == "draft" && history[0]!["issues"]!.GetValue<int>() == 1 && history.Count == 2 && history[1]!["accepted"]!.GetValue<bool>(), "lint history shows the draft and each repair pass", history.ToJsonString());
        c.That(metrics["render_summary"]!.AsArray().Count == arc.Count + 1 && metrics["measured_facts"]!.AsArray().Count == 1, "one render summary per slide and the measured fact");
        c.That(Math.Abs(metrics["total_s"]!.GetValue<double>() - Math.Round(result.DraftSeconds + 1.5, 2)) < 0.011, "the total is drafting plus rendering");
        c.That(metrics["llm_calls"]!.GetValue<int>() == arc.Count + 1, "calls include the repair");
    }
}

/// <summary>Runs an action and returns the exception it threw, if any.</summary>
internal static class Record
{
    public static Exception? Exception(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }
}
