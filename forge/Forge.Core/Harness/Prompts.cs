using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Ir;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

/// <summary>Everything one slot call needs to write its prompt.</summary>
public sealed record SlotPrompt(
    Brief Brief, DeviceStory Story, IReadOnlyList<ArcSlot> Arc, int Index, IReadOnlyList<string> DeckSoFar,
    IReadOnlyList<string> Topics, int Level, bool Guided, string? PreviousAttempt = null, IReadOnlyList<string>? Issues = null);

/// <summary>The prompts of the three harness modes. Text is ported from the Python Forge (v1.0 and v1.1, verbatim where it carries no
/// device claim) and made device-neutral: the facts carry the device, so the same rules read right on an NPU, GPU or CPU lane.
/// A compaction level (0 full, 3 minimal) lets the harness fit a small context window without changing what is asked.</summary>
public static class Prompts
{
    public const string LayoutCatalog = """
LAYOUTS (each slide = {"layout": ..., "title": ..., "takeaway": ..., "data": {...}, "notes": ...}):
- title: {"layout":"title","kicker":str,"title":str,"subtitle":str,"hero_icons":[4 icon names]}  (no data)
- stack: layered architecture, top = closest to user. data {"side_label":str,"layers":[{"label","detail","icon"} x3-5]}
- flow: left-to-right process. data {"steps":[{"label","detail","icon"} x3-6]}
- hub: one center serving many. data {"center":{"label","icon"},"spokes":[{"label","icon"} x4-8]}
- compare: two options side by side, right side is the recommended one. data {"left":{"title","icon","points":[str x2-4]},"right":{...},"verdict":str}
- kpis: 2-4 big numbers. data {"items":[{"value":str,"label":str,"icon","source":str}]}
- analogy: map a technical concept onto an everyday thing. data {"concept":{"label","icon"},"analog":{"label","icon"},"pairs":[{"concept":str,"analog":str} x3-5]}
- bars: bar chart. data {"unit":str,"highlight":label,"icon","caption":str,"bars":[{"label","value":number} x2-8]}
- timeline: data {"milestones":[{"when","label","icon"} x3-6]}
- matrix: 2x2. data {"x_axis":str,"y_axis":str,"highlight":0-3,"quadrants":[{"label","detail","icon"} x4]}  order: top-left, top-right, bottom-left, bottom-right
- memory: capacity bar. data {"capacity":number,"unit":"GB","capacity_label":str,"caption":str,"blocks":[{"label","size":number} x1-5]}
- statement: one big idea. data {"icon","text":str,"sub":str}
- canvas: free-form diagram on a 12 x 6 grid (x 0-12, y 0-6). data {"elements":[...up to 30]}
    element types: box|pill|circle|hexagon|chevron {x,y,w,h,label,icon,tone}; arrow|line {x,y,x2,y2,label,tone,dashed};
    text {x,y,w,h,label,size:s|m|l|xl,tone}; icon {x,y,w,h,icon,tone}
    tone: primary|accent1|accent2|accent3|accent4|muted|card|text
- cta: closing ask. data {"steps":[{"label","detail","icon"} x2-4],"ask":str}
""";

    private static readonly Lazy<Dictionary<string, string>> Lines = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var text = new StringBuilder();
        void Flush()
        {
            if (current is not null) map[current] = text.ToString().TrimEnd();
            text.Clear();
        }
        foreach (string raw in LayoutCatalog.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                current = line[2..line.IndexOf(':')];
                text.AppendLine(line);
            }
            else if (current is not null && line.Length > 0) text.AppendLine(line);
        }
        Flush();
        return map;
    });

    /// <summary>The catalog entry of one layout (the canvas entry includes its element rules).</summary>
    public static string LayoutLine(string layout) => Lines.Value.TryGetValue(layout, out string? line) ? line : "";

    public static string IconList => string.Join(", ", Icons.Names);

    // ---------------- v1.0: one shot ----------------

    private const string SystemV10Head = """
You are Offline PPTX Forge, a presentation designer that turns a brief into a JSON slide spec.
A deterministic renderer draws every slide as native infographics (shapes, icons, charts), so you
choose layouts and write very short text. Decks are text-light and visual-first, and they explain
Local AI architecture with analogies that make concepts resonate for the audience.

RULES
1. Return ONLY one JSON object: {"title":str,"client":str,"slides":[...]}. No markdown, no commentary.
2. 8-12 slides. Slide 1 layout "title". Last slide layout "cta". Every other slide uses an infographic
   layout; use "statement" at most once.
3. Word limits: title <= 7 words, takeaway <= 14 words, labels <= 4 words, details <= 9 words.
   Speaker notes <= 60 words per slide carry the talk track.
4. Include at least 2 "analogy" slides (or canvas diagrams built around an analogy).
5. Use at least 6 different layouts. Use at least 1 "canvas" diagram for a custom architecture.
6. Every number must come from FACTS in the brief. Never invent numbers, prices, savings or
   benchmarks. Respect every guardrail.
7. Icons: use only names from ICONS.

""";

    public static string SystemV10 => SystemV10Head + LayoutCatalog + "\nICONS: " + IconList + "\n";

    /// <summary>The v1.0 request: the brief as JSON, asking for the whole deck at once.</summary>
    public static LlmRequest OneShot(Brief brief, int maxTokens = 8192, string? retryNote = null)
    {
        var fields = new JsonObject();
        var json = brief.ToJson();
        foreach (string key in new[] { "client", "pitch_type", "audience", "objective", "context", "facts", "required_topics", "guardrails", "slide_range" })
        {
            if (json[key] is null) continue;
            fields[key] = key == "required_topics" && json[key] is JsonObject topics
                ? new JsonArray([.. topics.Select(kv => (JsonNode?)JsonValue.Create(kv.Key))])
                : json[key]!.DeepClone();
        }
        string user = "BRIEF\n" + SpecJson.Pretty(fields) + "\n\nReturn the JSON deck spec now."
            + (retryNote is null ? "" : "\n\nYour previous reply could not be used: " + retryNote + "\nReturn ONLY the JSON object.");
        return new LlmRequest(SystemV10, user) { MaxTokens = maxTokens, Temperature = retryNote is null ? 0.4f : 0.2f, Purpose = "deck (v1.0)" };
    }

    // ---------------- v1.1: arc in one shot ----------------

    private const string SystemV11Head = """
You are Offline PPTX Forge v1.1, a presentation designer that turns a brief into a JSON slide spec.
A deterministic renderer draws every slide as native infographics, so you choose layouts and write
very short, sharp text. Decks are text-light, visual-first, and explain Local AI with accurate analogies.

RULES
1. Return ONLY one JSON object: {"title":str,"client":str,"slides":[...]}. No markdown, no commentary.
2. Follow the ARC exactly: one slide per slot, in order, using the slot's layout.
3. Titles are insights, not labels: a short claim with a verb, <= 7 words. Turn caveats into claims
   (good: "Device Speed Is Not Task Success", "Authorize a Pilot, Not a Rollout",
   "Three Memory Views, Never Summed"; bad: "Throughput", "Memory Views", "Pilot Ask").
4. Text budget: <= 30 visible words per slide in total. takeaway <= 12 words, labels <= 4 words,
   details <= 7 words, KPI sources <= 4 words. Speaker notes: 2-3 sentences, <= 45 words.
5. Numbers: use ONLY numbers from ALLOWED NUMBERS, copied exactly with their unit. Never invent sizes,
   percentages, prices or counts. Prefer words over numbers when unsure.
6. Charts: a bars chart compares like with like: ONE unit and ONE measurement type for every bar
   (never mix counts, hours, GiB and speed; never mix single-stream and aggregate); every bar value is
   an allowed number; "highlight" is exactly one bar label and is the number the slide is about;
   caption gives source + caveat. Never split memory into parts that add up; show memory views as kpis.
   KPI items carry a short "source".
7. Analogies: the best analogies map the caveat, not just the nouns. Use 4 pairs; each pair maps one
   concrete part of the concept to one concrete part of the analog. Use the ANALOGY BANK (adapt the
   wording to the audience). Exactly two "analogy" slides, never back to back; weave more analogies
   into takeaways with "like" or "think of" (e.g. "Like zoom levels on one map: never add them").
   {MEMORY}
8. Canvas: 12-20 elements inside the 12 x 6 grid, no overlapping boxes, every box has a label and icon,
   and every box touches an arrow or line (no floating nodes). Mark the device boundary with four dashed
   lines, as in the EXEMPLAR.
9. Guardrails: state the positive version and never mention a banned term, not even to deny it
   (say "disconnected use validated in the pilot", not "works offline").
10. Icons: use only names from ICONS. Every content slide shows at least 3 icons.
11. Do not add a slide about how this deck was generated; the harness adds a measured one when relevant.

""";

    /// <summary>The memory rule depends on how this PC shares memory, so the deck never states a pool layout the device does not have.</summary>
    public static string MemoryRule(DeviceStory story) => story.UnifiedMemory
        ? "Never say a token is a word; never call system RAM and GPU memory separate spaces."
        : "Never say a token is a word; system RAM and GPU memory are separate pools, so never add them or call one the other.";

    public static string SystemV11(DeviceStory story) =>
        SystemV11Head.Replace("{MEMORY}", MemoryRule(story), StringComparison.Ordinal) + LayoutCatalog + "\nICONS: " + IconList + "\n";

    public const string RepairSystem = """
You are the repair pass of Offline PPTX Forge v1.1. You receive a JSON deck spec and a list of lint
issues by slide index (0-based). Fix ONLY the listed slides, keeping their intent and layout unless an
issue says to change it. Apply every RULE from the original instructions (word budget, allowed numbers,
one unit per chart, insight titles, 4-pair analogies, guardrails stated positively).
When the deck has too many slides, remove the weakest middle slide with {"index":N,"slide":{"delete":true}}; never remove the first or the last slide.
Return ONLY JSON: {"fixes":[{"index":int,"slide":{...complete replacement slide...}}]}
""";

    private static string Facts(IEnumerable<string> facts) => string.Join("\n", facts.Select((f, i) => $"F{i + 1}: {f}"));

    private static string Bullets(IEnumerable<string> lines) => "- " + string.Join("\n- ", lines);

    /// <summary>The v1.1 request: the full arc, the fact sheet, the bank and the exemplars in one prompt (for models with room to spare).</summary>
    public static LlmRequest Arc(Brief brief, DeviceStory story, int level, int maxTokens = 8192, string? retryNote = null)
    {
        var arc = Arcs.For(brief, Arcs.Reserved(brief));
        var facts = level >= 3 ? brief.Facts.Take(8).ToList() : brief.Facts;
        string arcText = string.Join("\n", arc.Select((s, i) => $"{i + 1}. [{s.Layout}] {s.Slot}: {s.Intent}"));
        string topics = string.Join(", ", brief.RequiredTopics.Keys.Where(t => t != "made_on_device"));
        string context = level >= 2 ? Clip(brief.Context, 320) : brief.Context;
        var user = new StringBuilder()
            .Append($"CLIENT: {brief.Client}\nPITCH: {brief.PitchType}\nAUDIENCE: {brief.Audience}\n")
            .Append($"OBJECTIVE: {brief.Objective}\nCONTEXT: {context}\n\n")
            .Append($"FACT SHEET\n{Facts(facts)}\n\nALLOWED NUMBERS: {string.Join(", ", Arcs.AllowedNumbers(brief))} (plus small ordinals 1-10)\n\n")
            .Append($"GUARDRAILS\n{Bullets(brief.Guardrails)}\n\nTOPICS TO COVER ON-SLIDE: {topics}\n\n")
            .Append($"ARC ({arc.Count} slides)\n{arcText}\n\n");
        if (level < 2) user.Append($"ANALOGY BANK\n{SpecJson.Compact(new JsonArray([.. Arcs.Bank(brief, story).Select(b => (JsonNode?)b.DeepClone())]))}\n\n");
        if (level < 1)
            user.Append($"EXEMPLAR SLIDES (fictional Fabrikam; copy the structure, not the content)\n{SpecJson.Compact(new JsonArray([.. Arcs.Exemplars(story).Select(b => (JsonNode?)b.DeepClone())]))}\n\n");
        user.Append($"Return the JSON deck spec for {brief.Client} now ({arc.Count} slides).");
        if (retryNote is not null) user.Append("\n\nYour previous reply could not be used: ").Append(retryNote).Append("\nReturn ONLY the JSON object.");
        return new LlmRequest(SystemV11(story), user.ToString())
        {
            MaxTokens = maxTokens,
            Temperature = retryNote is null ? 0.4f : 0.2f,
            Schema = LayoutSchemas.DeckEnvelope(),
            Purpose = "deck (arc)",
        };
    }

    /// <summary>The v1.1 repair request: lint issues by slide index and a spec to fix, answered with replacement slides.</summary>
    public static LlmRequest ArcRepair(Brief brief, DeviceStory story, JsonObject spec, IReadOnlyList<LintIssue> issues, int level)
    {
        var lines = new List<string>();
        foreach (var group in issues.GroupBy(i => i.Slide).OrderBy(g => g.Key))
            lines.AddRange(group.Select(i => $"- {(group.Key < 0 ? "deck" : "slide " + group.Key)}: {i.Msg}"));
        var body = new StringBuilder()
            .Append($"ALLOWED NUMBERS: {string.Join(", ", Arcs.AllowedNumbers(brief))} (plus small ordinals 1-10)\n")
            .Append($"GUARDRAILS\n{Bullets(brief.Guardrails)}\n\n");
        if (level < 2) body.Append($"ANALOGY BANK\n{SpecJson.Compact(new JsonArray([.. Arcs.Bank(brief, story).Select(b => (JsonNode?)b.DeepClone())]))}\n\n");
        body.Append("ISSUES\n").Append(string.Join("\n", lines)).Append("\n\nSPEC\n").Append(SpecJson.Compact(spec));
        return new LlmRequest(SystemV11(story) + "\n\n" + RepairSystem, body.ToString())
        {
            MaxTokens = 6144,
            Temperature = 0.3f,
            Schema = LayoutSchemas.FixesEnvelope(),
            Purpose = "repair (arc)",
        };
    }

    // ---------------- guided slots: one small call per slide ----------------

    private const string SlotSystemHead = """
You write ONE slide of a {KIND} as a JSON object. Output only that JSON object, with no markdown and no commentary.
A deterministic renderer draws the slide as native infographics, so write very short, sharp text.

RULES
1. The title is an insight: a short claim with a verb, 7 words or fewer (good: "Device Speed Is Not Task Success"; bad: "Throughput").
2. Text budget: 30 visible words or fewer on the slide. takeaway 12 words or fewer, labels 4 or fewer, details 7 or fewer, KPI sources 4 or fewer. notes: 2-3 sentences, 45 words or fewer.
3. Numbers: use ONLY numbers from ALLOWED NUMBERS, copied exactly with their unit. Never invent sizes, percentages, prices or counts. When unsure, use words.
4. Guardrails: state the positive version and never mention a banned term, not even to deny it.
5. Choose icon names that fit each item and use different icons for different items.
6. Follow the slide's INTENT and fill its LAYOUT exactly. {MEMORY}
""";

    private static string SlotSystem(Brief brief, DeviceStory story, bool guided)
    {
        string text = SlotSystemHead
            .Replace("{KIND}", brief.IsTechnical ? "technical briefing" : "sales deck", StringComparison.Ordinal)
            .Replace("{MEMORY}", MemoryRule(story), StringComparison.Ordinal);
        return guided ? text : text + "7. Icons: use only names from ICONS.\n\nICONS: " + IconList + "\n";
    }

    private static readonly Dictionary<string, string> LayoutRules = new(StringComparer.Ordinal)
    {
        ["title"] = "kicker is a short ALL CAPS label; title is the outcome in 6 words or fewer; subtitle is one short line; hero_icons are 4 different icon names. There is no data or takeaway.",
        ["stack"] = "Exactly 4 layers, top = closest to the user. Each layer has a short label, a short detail and an icon.",
        ["flow"] = "Exactly 5 steps from input to outcome, each with a short label, a short detail and an icon.",
        ["kpis"] = "3 or 4 items. Each item is one number with a short label and a short source (4 words or fewer) saying where the number comes from. The value is the number and a short unit, 8 characters or fewer in all; write tokens per second as tok/s.",
        ["analogy"] = "Exactly 4 pairs. Each pair maps one concrete part of the concept to one concrete part of the analog, like the ANALOGY BANK entry (adapt its wording to this audience). Never say a token is a word.",
        ["compare"] = "Exactly 3 points per side. The right side is the recommended option. verdict is the decision in one short sentence.",
        ["bars"] = "ONE unit and ONE measurement type for every bar; every bar value is an allowed number; highlight is exactly one bar label, the number this slide is about; caption gives the source and caveat.",
        ["timeline"] = "Exactly 4 milestones, each with a short \"when\", a short label and an icon.",
        ["matrix"] = "highlight is the index (0-3) of the quadrant that is the answer: 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right.",
        ["cta"] = "Exactly 3 steps, each naming who acts. ask is one sentence that restates the OBJECTIVE with exact numbers from the FACT SHEET.",
        ["canvas"] = "Describe the diagram's content only; the renderer draws the device boundary, arrows and the engine box. inside has 3 items that flow left to right (input, local model, reviewed output); outside holds what stays with the existing cloud lane.",
    };

    /// <summary>The canvas slot is written as a short description that <see cref="CanvasComposer"/> draws, never as raw coordinates.</summary>
    private const string CanvasDescriptionLine =
        "- canvas: {\"title\":str,\"takeaway\":str,\"inside\":[{\"label\",\"icon\"} x3],\"inside_caption\":str,\"outside_title\":str,\"outside\":[{\"label\",\"icon\"} x2],\"notes\":str}";

    /// <summary>Shortens text at a word boundary.</summary>
    public static string Clip(string text, int max)
    {
        if (text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd(',', ';', ':') + "…";
    }

    private static readonly Regex Words = new(@"[A-Za-z]{4,}", RegexOptions.Compiled);

    /// <summary>Facts ranked by how much they share with what the slide is about, for prompts that cannot carry the whole sheet.</summary>
    private static List<string> FactsFor(Brief brief, ArcSlot slot, IReadOnlyList<string> topics, int cap)
    {
        if (brief.Facts.Count <= cap) return brief.Facts;
        var wanted = new HashSet<string>(
            Words.Matches(slot.Slot + " " + slot.Intent + " " + string.Join(" ", topics)).Select(m => m.Value.ToLowerInvariant()));
        return [.. brief.Facts
            .Select((fact, index) => (fact, index, score: Words.Matches(fact).Select(m => m.Value.ToLowerInvariant()).Distinct().Count(wanted.Contains)))
            .OrderByDescending(x => x.score).ThenBy(x => x.index).Take(cap).OrderBy(x => x.index).Select(x => x.fact)];
    }

    /// <summary>The request for one slide of the arc.</summary>
    public static LlmRequest Slot(SlotPrompt p)
    {
        var slot = p.Arc[p.Index];
        var brief = p.Brief;
        int level = p.Level;
        int cap = level switch { <= 1 => int.MaxValue, 2 => 10, _ => 6 };
        var facts = FactsFor(brief, slot, p.Topics, cap);
        var allowed = Arcs.AllowedNumbers(new Brief { Facts = facts });
        var user = new StringBuilder();
        user.Append($"CLIENT: {brief.Client}\nPITCH: {brief.PitchType}\nAUDIENCE: {Clip(brief.Audience, level >= 3 ? 120 : 400)}\nOBJECTIVE: {Clip(brief.Objective, level >= 3 ? 160 : 500)}\n");
        if (level < 3) user.Append($"CONTEXT: {(level >= 2 ? Clip(brief.Context, 280) : brief.Context)}\n");
        user.Append($"\nFACT SHEET\n{Facts(facts)}\n\nALLOWED NUMBERS: {string.Join(", ", allowed)} (plus small ordinals 1-10)\n");
        if (brief.Guardrails.Count > 0) user.Append($"\nGUARDRAILS\n{Bullets(brief.Guardrails)}\n");
        if (p.Topics.Count > 0) user.Append($"\nTOPICS THIS SLIDE MUST COVER (work one listed word into a label or the takeaway):\n{Bullets(p.Topics)}\n");
        if (p.DeckSoFar.Count > 0 && level < 3) user.Append($"\nSLIDES SO FAR (do not repeat their titles): {string.Join(" | ", p.DeckSoFar)}\n");

        bool weave = Arcs.WeaveSlots(brief, p.Arc).Contains(slot.Slot);
        user.Append($"\nSLIDE {p.Index + 1} of {p.Arc.Count}: [{slot.Layout}] {slot.Slot}\nINTENT: {slot.Intent}\n");
        if (weave && slot.Layout != "analogy")
            user.Append("The takeaway also weaves in one short analogy with \"like\" or \"think of\" (for example \"Like zoom levels on one map: never add them\").\n");
        user.Append($"LAYOUT: {(slot.Layout == "canvas" ? CanvasDescriptionLine : LayoutLine(slot.Layout))}\n");
        if (LayoutRules.TryGetValue(slot.Layout, out string? rule))
        {
            // The bank is left out of small prompts, so the rule must not send the model looking for it.
            if (level >= 2) rule = rule.Replace(", like the ANALOGY BANK entry (adapt its wording to this audience)", "", StringComparison.Ordinal);
            user.Append("LAYOUT RULES: ").Append(rule).Append('\n');
        }
        if (slot.Layout is "analogy" || weave)
        {
            var bank = Arcs.BankFor(brief, slot.Slot, p.Story);
            if (bank.Count > 0 && level < 2)
                user.Append($"ANALOGY BANK\n{SpecJson.Compact(new JsonArray([.. bank.Select(b => (JsonNode?)b.DeepClone())]))}\n");
        }
        if (level < 1 && slot.Layout is "kpis" or "bars" or "cta" && Arcs.Exemplar(slot.Layout, p.Story) is { } exemplar)
            user.Append($"EXEMPLAR (fictional; copy the structure, not the content)\n{SpecJson.Compact(exemplar)}\n");
        if (p.PreviousAttempt is not null)
        {
            user.Append($"\nYOUR PREVIOUS ATTEMPT\n{p.PreviousAttempt}\n");
            if (p.Issues is { Count: > 0 }) user.Append($"\nFIX THESE ISSUES\n{Bullets(p.Issues)}\n");
            user.Append("\nReturn the corrected slide as one JSON object.");
        }
        else user.Append($"\nReturn the JSON for this slide only.");

        bool boundary = slot.Layout == "canvas";
        return new LlmRequest(SlotSystem(brief, p.Story, p.Guided), user.ToString())
        {
            MaxTokens = p.Guided ? 700 : 1100,
            Temperature = p.PreviousAttempt is null ? 0.35f : 0.25f,
            Schema = LayoutSchemas.ForSlot(slot.Layout, slot.Slot, boundary),
            Purpose = $"slide {p.Index + 1}: {slot.Slot}",
        };
    }

    /// <summary>The prompt for drafting a brief from a free-form request.</summary>
    public const string BriefSystem = """
You structure a seller's request into a brief for a deck generator that builds Local AI pitches for Surface PCs.
Return ONLY a JSON object with these keys:
  client: the customer name from the request ("Customer" if none is given)
  pitch_type: "sales" (business buyers) or "technical" (engineers, IT, developers)
  audience: who sees the deck and what they care about (1-2 sentences)
  objective: the one decision or next step the deck asks for (1 sentence)
  context: 3-5 sentences of background, using only the request
  facts: 2-10 short standalone sentences, each restating one concrete detail from the request (numbers, workloads, constraints, tools already in use, pilot scope)
  required_topics: 4-7 objects {"topic": snake_case name, "keywords": 3-6 lowercase words or short phrases that would appear on a slide about that topic}
  guardrails: 0-4 things the deck must not claim, based on the request
RULES
- Use only information in the REQUEST. Never invent numbers, prices, percentages, dates, headcounts or savings.
- Do not add regulations, certifications, products or customer details the REQUEST does not mention.
- Copy every number exactly as written in the REQUEST. Do not calculate new numbers.
- A short request gets a short brief; two or three facts is fine.
""";
}
