using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

/// <summary>One slot of a deck's narrative: a name, the layout that carries it and what the slide should say.</summary>
public sealed record ArcSlot(string Slot, string Layout, string Intent);

/// <summary>The story a deck tells (a slot per slide), the analogy bank, the exemplar slides and the numbers a deck may use.
/// Ported from harness v1.1; the intents are client- and device-neutral, because the facts carry the specifics.</summary>
public static class Arcs
{
    private static readonly (string Slot, string Layout, string Intent)[] Sales =
    [
        ("title", "title", "Kicker '<CLIENT> PROPOSAL'. Title = the business outcome in <= 6 words. Subtitle positions the hybrid lane."),
        ("pain", "kpis", "3-4 numbers from the FACT SHEET that show the audience's problem."),
        ("big_idea", "analogy", "The core idea from the OBJECTIVE in everyday terms, using the best-fitting ANALOGY BANK entry (A8 for hybrid). Title states the idea as a claim."),
        ("position", "compare", "Today vs the recommended approach: keep any cloud assistant named in the CONTEXT, add a local lane. Right side wins; verdict = the decision."),
        ("architecture", "canvas", "Device-boundary diagram: the audience's sensitive inputs from the CONTEXT -> local model -> output reviewed by the audience's own role inside the device; existing cloud services outside. Every box connected. Takeaway weaves in one short analogy with 'like'."),
        ("routing", "matrix", "Which work goes where: x_axis 'Repetition', y_axis 'Sensitivity'; highlight the high/high quadrant = local lane; other quadrants keep the existing cloud assistant. Takeaway weaves in one short analogy with 'think of'."),
        ("connectivity", "analogy", "A constraint from the CONTEXT (connectivity, privacy, latency or cost) explained with the best-fitting ANALOGY BANK entry; disconnected use is validated in the pilot, never guaranteed."),
        ("device", "stack", "The device in plain business terms: drafting workflow -> local models -> on-device runtime -> device silicon, one fact each from the FACT SHEET. No speed charts for executives."),
        ("value", "flow", "Value chain as 5 steps using only FACT SHEET numbers; if the facts give no calculation, show the before/after workflow instead; label any estimate 'estimate to validate'."),
        ("pilot", "timeline", "Next-step plan from the OBJECTIVE as 4 milestones with what is measured; durations only from the FACT SHEET."),
        ("cta", "cta", "3 steps, each naming who acts (roles from the AUDIENCE) + ask that restates the OBJECTIVE with exact numbers from the FACT SHEET."),
    ];

    private static readonly (string Slot, string Layout, string Intent)[] Technical =
    [
        ("title", "title", "Kicker 'TECHNICAL BRIEFING FOR <CLIENT>'. Title = what is proven, <= 6 words."),
        ("why_local", "compare", "Open on the problem from the CONTEXT: why this workload needs a local lane. Left = cloud-only today, right = local lane + cloud for heaviest work."),
        ("stack", "stack", "Silicon -> runtimes -> models -> the audience's workflows from the CONTEXT, each layer with one fact from the FACT SHEET."),
        ("memory", "kpis", "3 KPIs of memory taken from the FACT SHEET (installed, visible to Windows, what the AI model may use). Title says they are never summed; takeaway uses bank A10 (A10p when GPU memory is a separate pool)."),
        ("throughput", "bars", "Single-stream decode ONLY (tokens/s) for the models in the FACT SHEET; highlight the fastest; caption = source. Title: what the speed means for the audience's work."),
        ("moe_analogy", "analogy", "The key technical idea for this audience explained with the best-fitting ANALOGY BANK entry (A11 for NPU, GPU and CPU lanes, A4 for mixture of experts)."),
        ("codegen_rag", "flow", "The audience's core workflow from the CONTEXT as 5 steps from input to a decision by the audience's own role (e.g. developer, reviewer; never a role from the EXEMPLAR), showing where the local model sits. Takeaway uses bank A6 (open-book exam) when retrieval is involved."),
        ("sizing", "analogy", "Model sizing bands with bank A5 (shop machines); bands are planning estimates, not accuracy tiers."),
        ("routing", "canvas", "Hybrid routing: user -> routing policy; local lane (sensitive work from the CONTEXT) inside a dashed device boundary; cloud lane for heaviest reasoning outside. Every box connected. Takeaway uses bank A3 (make-or-buy)."),
        ("cta", "cta", "3 steps for the pilot or next step in the OBJECTIVE (who joins, what runs, what is measured) + ask with its exact size and duration from the FACT SHEET; never invent a size or duration."),
    ];

    /// <summary>When the facts carry no two decode speeds, a chart of speeds has nothing honest to compare, so the slot becomes KPIs of what was measured or planned for this PC.</summary>
    private const string MeasuredKpisIntent =
        "3-4 KPIs about this PC taken only from the FACT SHEET (model, decode speed when measured, model size, memory budget). Caption each value's source in the 'source' field; never add a number the FACT SHEET lacks.";

    private static readonly Regex Speed = new(@"[0-9][0-9.,]*\s*(?:tokens/s|tokens per second|tok/s)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The slots for this brief, in order. Technical pitches show a speed chart only when the facts carry two or more speeds.
    /// When the brief's slide range is smaller than the story, the least essential slots are dropped (never the title, the call to
    /// action or an analogy slot), keeping <paramref name="reserved"/> places free for slides the harness adds itself.</summary>
    public static IReadOnlyList<ArcSlot> For(Brief brief, int reserved = 0)
    {
        var template = brief.IsTechnical ? Technical : Sales;
        string client = (brief.Client.Length > 0 ? brief.Client : "CLIENT").ToUpperInvariant();
        int speeds = brief.Facts.SelectMany(f => Speed.Matches(f).Select(m => m.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var slots = template.Select(s =>
        {
            string layout = s.Layout, intent = s.Intent;
            if (s.Slot == "throughput" && speeds < 2) { layout = "kpis"; intent = MeasuredKpisIntent; }
            return new ArcSlot(s.Slot, layout, intent.Replace("<CLIENT>", client));
        }).ToList();
        string[] dropOrder = brief.IsTechnical
            ? ["codegen_rag", "why_local", "stack", "memory", "throughput"]
            : ["value", "pilot", "position", "device", "routing"];
        foreach (string slot in dropOrder)
        {
            if (slots.Count + reserved <= brief.SlideMax) break;
            slots.RemoveAll(s => s.Slot == slot);
        }
        return slots;
    }

    /// <summary>How many slides the harness adds after the model is done (the measured "built on this device" slide).</summary>
    public static int Reserved(Brief brief) => brief.RequiredTopics.ContainsKey("made_on_device") ? 1 : 0;

    /// <summary>Slots whose takeaway is asked to weave in an analogy (the lint wants two such slides outside the analogy slides).</summary>
    public static bool WeavesAnalogy(Brief brief, string slot) =>
        brief.IsTechnical ? slot is "memory" or "codegen_rag" or "routing" : slot is "architecture" or "routing";

    private static readonly HashSet<string> WovenLayouts = new(StringComparer.Ordinal) { "kpis", "flow", "canvas", "bars", "stack", "matrix", "compare" };

    /// <summary>The slots of this arc whose takeaway weaves an analogy in: the usual ones that survived trimming, topped up from other
    /// layouts the lint accepts so there are always two.</summary>
    public static HashSet<string> WeaveSlots(Brief brief, IReadOnlyList<ArcSlot> arc)
    {
        var slots = arc.Where(s => WeavesAnalogy(brief, s.Slot)).Select(s => s.Slot).ToHashSet(StringComparer.Ordinal);
        foreach (var s in arc)
        {
            if (slots.Count >= 2) break;
            if (WovenLayouts.Contains(s.Layout)) slots.Add(s.Slot);
        }
        return slots;
    }

    // ---- Analogy bank and exemplars ----

    private static readonly Lazy<JsonArray> BankData = new(() => (JsonArray)ReadJson("analogy-bank.json")!);
    private static readonly Lazy<JsonArray> ExemplarData = new(() => (JsonArray)ReadJson("exemplars.json")!);

    private static readonly (string Old, string New)[] NeutralBankText =
    [
        ("Consultant decides", "Your expert decides"), ("Consultant review", "Expert review"),
        ("Sensitive client files", "Sensitive files"), ("Client documents", "Source documents"),
        ("Restricted client Wi-Fi", "Restricted Wi-Fi"), ("client Wi-Fi", "Wi-Fi"), ("2-week pilot", "The pilot"),
        ("S: 1-4B", "Small models"), ("M: 7-8B", "Mid-size models"), ("L: 14-20B", "Large models"), ("XL: 24-32B", "Largest local models"),
    ];

    /// <summary>The analogy bank as it should be shown to the model: memory entries match how this PC shares memory, and entries that
    /// name roles or sizes the brief never mentions are reworded so they cannot leak numbers the audience never saw.</summary>
    public static IReadOnlyList<JsonObject> Bank(Brief brief, DeviceStory story)
    {
        bool bandsInFacts = brief.Facts.Any(f => f.Contains("1-4B", StringComparison.OrdinalIgnoreCase));
        bool neutral = brief.Source == "prompt" || !bandsInFacts;
        var entries = new List<JsonObject>();
        foreach (var node in BankData.Value.OfType<JsonObject>())
        {
            string id = node["id"]!.GetValue<string>();
            bool separatePools = !story.UnifiedMemory && story.Lane == "gpu";
            if (id == "A10" && separatePools) continue;
            if (id == "A10p" && !separatePools) continue;
            var copy = (JsonObject)node.DeepClone();
            if (neutral)
            {
                string text = SpecJson.Compact(copy);
                foreach (var (old, replacement) in NeutralBankText) text = text.Replace(old, replacement, StringComparison.Ordinal);
                copy = (JsonObject)JsonNode.Parse(text)!;
            }
            entries.Add(copy);
        }
        return entries;
    }

    /// <summary>The bank entries worth showing for one slot: a small model copes better with the one or two analogies that fit than with all of them.</summary>
    public static IReadOnlyList<JsonObject> BankFor(Brief brief, string slot, DeviceStory story)
    {
        var all = Bank(brief, story);
        string[] wanted = (brief.IsTechnical, slot) switch
        {
            (false, "big_idea" or "architecture" or "routing") => ["A8", "A2"],
            (false, "connectivity") => ["A9"],
            (true, "memory") => ["A10", "A10p"],
            (true, "moe_analogy") => MentionsExperts(brief) ? ["A4"] : ["A11"],
            (true, "codegen_rag") => ["A6"],
            (true, "sizing") => ["A5"],
            (true, "routing") => ["A3"],
            _ => [],
        };
        return [.. all.Where(e => wanted.Contains(e["id"]!.GetValue<string>()))];
    }

    private static bool MentionsExperts(Brief brief) =>
        brief.Facts.Any(f => f.Contains("mixture of experts", StringComparison.OrdinalIgnoreCase) || f.Contains("MoE", StringComparison.Ordinal));

    /// <summary>The exemplar slides with this PC's name and engine filled in, so a model that copies the structure copies the right device.</summary>
    public static IReadOnlyList<JsonObject> Exemplars(DeviceStory story)
    {
        string text = ExemplarData.Value.ToJsonString().Replace("{DEVICE}", JsonEscape(story.Name), StringComparison.Ordinal)
            .Replace("{ENGINE}", JsonEscape(story.Engine), StringComparison.Ordinal);
        return [.. ((JsonArray)JsonNode.Parse(text)!).OfType<JsonObject>()];
    }

    public static JsonObject? Exemplar(string layout, DeviceStory story) =>
        Exemplars(story).FirstOrDefault(e => e["layout"]?.GetValue<string>() == layout);

    private static string JsonEscape(string s)
    {
        string quoted = System.Text.Json.JsonSerializer.Serialize(s);
        return quoted[1..^1];
    }

    private static JsonNode? ReadJson(string name)
    {
        using var stream = typeof(Arcs).Assembly.GetManifestResourceStream("Forge.Core." + name)
            ?? throw new FileNotFoundException($"The embedded data file {name} is missing from Forge.Core.");
        return JsonNode.Parse(stream);
    }

    // ---- Numbers the deck may use ----

    private static readonly Regex NumToken = new(
        @"(?<![A-Za-z0-9.])~?[0-9]+(?:,[0-9]{3})*(?:\.[0-9]+)?(?:\s?(?:%|GB|GiB|MiB|B\b|s\b|minutes|hours|weeks|days|tokens/s|tokens per second))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Every number token in the fact sheet, with its unit, in order and without repeats.</summary>
    public static List<string> AllowedNumbers(Brief brief)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (string fact in brief.Facts)
            foreach (Match m in NumToken.Matches(fact))
            {
                string token = m.Value.Trim();
                if (seen.Add(token)) output.Add(token);
            }
        return output;
    }
}
