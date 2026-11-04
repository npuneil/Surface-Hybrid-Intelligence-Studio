using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

/// <summary>Makes any brief fit the PC the deck is drafted on: resolves device tokens, adds what was detected (facts and topic keywords),
/// and warns when a hand-written brief names a different device. The brief the user wrote is never changed on disk.</summary>
public static class BriefAdapter
{
    public static IReadOnlyList<string> DefaultGuardrails { get; } =
    [
        "Use only numbers from the fact sheet; label any estimate as an estimate to validate.",
        "Do not claim local AI replaces Microsoft 365 Copilot or any cloud subscription.",
        "Do not claim air-gapped, certified offline or security certification; say disconnected use is validated in the pilot.",
        "Do not cite TOPS or Copilot+ PC certification.",
    ];

    /// <summary>Words that show the device is covered on a slide, whatever the device is.</summary>
    private static readonly string[] GenericDeviceWords = ["surface", "device", "laptop", "this pc", "cpu", "gpu", "npu", "chip", "silicon"];

    private static readonly HashSet<string> DeviceTopics = new(StringComparer.OrdinalIgnoreCase) { "device", "hardware", "silicon", "platform" };

    public const string ProvenanceTopic = "made_on_device";

    public static readonly string[] ProvenanceKeywords = ["this deck", "drafted on", "built this", "this device", "made on"];

    /// <summary>Fills {device}, {device_full}, {silicon}, {engine}, {lane}, {runtime} and {model} from what was detected.</summary>
    public static string Resolve(string text, DeviceStory story) => text
        .Replace("{device_full}", story.NameWithSilicon, StringComparison.OrdinalIgnoreCase)
        .Replace("{device}", story.Name, StringComparison.OrdinalIgnoreCase)
        .Replace("{silicon}", story.Silicon, StringComparison.OrdinalIgnoreCase)
        .Replace("{engine}", story.Engine, StringComparison.OrdinalIgnoreCase)
        .Replace("{lane}", story.Lane, StringComparison.OrdinalIgnoreCase)
        .Replace("{runtime}", story.Runtime, StringComparison.OrdinalIgnoreCase)
        .Replace("{model}", story.Model, StringComparison.OrdinalIgnoreCase);

    public static Brief Adapt(Brief source, DeviceStory story)
    {
        var brief = Brief.FromJson(source.ToJson());
        brief.Audience = Resolve(brief.Audience, story);
        brief.Objective = Resolve(brief.Objective, story);
        brief.Context = Resolve(brief.Context, story);
        brief.Facts = [.. brief.Facts.Select(f => Resolve(f, story))];
        brief.Guardrails = [.. brief.Guardrails.Select(g => Resolve(g, story))];

        var seen = new HashSet<string>(brief.Facts, StringComparer.OrdinalIgnoreCase);
        foreach (string fact in story.Facts.Concat(brief.IsTechnical ? story.TechnicalFacts : []))
            if (fact.Length > 0 && seen.Add(fact)) brief.Facts.Add(fact);

        foreach (string name in brief.RequiredTopics.Keys.ToList())
        {
            var words = brief.RequiredTopics[name].Select(w => Resolve(w, story).ToLowerInvariant()).ToList();
            if (DeviceTopics.Contains(name))
                foreach (string extra in story.Keywords.Concat(GenericDeviceWords).Select(w => w.ToLowerInvariant()))
                    if (extra.Length > 0 && !words.Contains(extra)) words.Add(extra);
            brief.RequiredTopics[name] = words;
        }
        if (brief.Source == "prompt" && !brief.RequiredTopics.Keys.Any(DeviceTopics.Contains))
            brief.RequiredTopics["device"] = [.. story.Keywords.Concat(GenericDeviceWords).Select(w => w.ToLowerInvariant()).Where(w => w.Length > 0).Distinct()];
        return brief;
    }

    private static readonly string[] DeviceMarkers =
    [
        "Surface Laptop Ultra", "Surface Laptop Studio", "Surface Laptop 7", "Surface Laptop 8", "Surface Pro 11", "Surface Pro 12",
        "Surface Laptop Go", "RTX Spark", "Snapdragon X2", "Snapdragon X Elite", "Snapdragon X Plus", "Core Ultra 7", "Core Ultra 9", "Core Ultra 5",
    ];

    /// <summary>Notes for a brief that names a device other than this one. Not blocking: the deck still builds from this PC's facts.</summary>
    public static List<string> Warnings(Brief source, DeviceStory story)
    {
        var warnings = new List<string>();
        string here = story.Name + " " + story.Silicon;
        string text = string.Join("\n", new[] { source.Objective, source.Context, source.Audience }.Concat(source.Facts));
        foreach (string marker in DeviceMarkers)
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase) && !here.Contains(marker, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"This brief mentions \"{marker}\", but this PC is {story.NameWithSilicon}. Its device facts may not apply here; the deck is drafted from this PC's own facts.");
        return warnings;
    }
}

public sealed record DraftOptions(string? Client = null, string? Pitch = null, string? Template = null, bool Provenance = false);

public sealed record DraftResult(Brief Brief, LlmReply Reply);

/// <summary>Turns a free-form request into a brief. The model only structures what the seller typed; a deterministic guard then drops every
/// fact, guardrail or sentence whose numbers are not in the request, so every number a deck may use traces to the seller's own words
/// or to a measurement.</summary>
public static class BriefDrafter
{
    private static readonly HashSet<double> NameNumbers = [365.0];

    private static readonly Dictionary<string, double> NumberWords = BuildNumberWords();

    private static Dictionary<string, double> BuildNumberWords()
    {
        var words = new Dictionary<string, double>(StringComparer.Ordinal);
        string[] units = "zero one two three four five six seven eight nine ten eleven twelve".Split(' ');
        for (int i = 0; i < units.Length; i++) words[units[i]] = i;
        words["dozen"] = 12; words["twenty"] = 20; words["thirty"] = 30; words["forty"] = 40; words["fifty"] = 50;
        words["sixty"] = 60; words["hundred"] = 100; words["thousand"] = 1000;
        return words;
    }

    private static readonly Regex Sentence = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);
    private static readonly Regex WordRx = new("[a-z]+", RegexOptions.Compiled);

    private static HashSet<double> Allowed(string text)
    {
        var numbers = new HashSet<double>(SpecText.Numbers(text));
        foreach (Match m in WordRx.Matches(text.ToLowerInvariant()))
            if (NumberWords.TryGetValue(m.Value, out double n)) numbers.Add(n);
        numbers.UnionWith(NameNumbers);
        return numbers;
    }

    private static List<string> Unsupported(string text, HashSet<double> allowed) =>
        [.. SpecText.Numbers(text).Where(n => !allowed.Contains(n)).Select(n => n.ToString("G6", CultureInfo.InvariantCulture))];

    private static string Scrub(string? text, HashSet<double> allowed, List<string> dropped, string field)
    {
        var keep = new List<string>();
        foreach (string sentence in Sentence.Split((text ?? "").Trim()))
        {
            var bad = Unsupported(sentence, allowed);
            if (bad.Count > 0) dropped.Add($"{field}: {sentence}  [not in prompt: {string.Join(", ", bad)}]");
            else if (sentence.Length > 0) keep.Add(sentence);
        }
        return string.Join(" ", keep);
    }

    public static string Slug(string text)
    {
        string s = Regex.Replace(Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "_"), "_+", "_").Trim('_');
        if (s.Length > 32) s = s[..32];
        return s.Length > 0 ? s : "custom";
    }

    private static Dictionary<string, List<string>> Topics(JsonNode? raw)
    {
        var topics = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string name, IEnumerable<string> keywords)
        {
            var words = keywords.Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).Distinct().Take(6).ToList();
            string key = Slug(name);
            if (words.Count > 0 && key is not ("device" or "made_on_device") && topics.Count < 7 && !topics.ContainsKey(key)) topics[key] = words;
        }
        static IEnumerable<string> Strings(JsonNode? node) => node switch
        {
            JsonArray a => a.Select(x => x?.ToString() ?? ""),
            null => [],
            _ => [node.ToString()],
        };
        switch (raw)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonObject o) Add(o["topic"]?.ToString() ?? o["name"]?.ToString() ?? "", Strings(o["keywords"]));
                    else if (item is not null) Add(item.ToString(), [item.ToString().Replace('_', ' ')]);
                }
                break;
            case JsonObject map:
                foreach (var (name, keywords) in map) Add(name, Strings(keywords));
                break;
        }
        return topics;
    }

    public static async Task<DraftResult> DraftAsync(ILlm llm, string prompt, DraftOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DraftOptions();
        prompt = prompt.Trim();
        if (prompt.Length == 0) throw new ArgumentException("Type what the deck is for first.", nameof(prompt));
        string user = $"REQUEST\n{prompt}\n";
        if (!string.IsNullOrWhiteSpace(options.Client)) user += $"\nCLIENT (use exactly): {options.Client}";
        if (!string.IsNullOrWhiteSpace(options.Pitch)) user += $"\nPITCH TYPE (use exactly): {options.Pitch}";

        var request = new LlmRequest(Prompts.BriefSystem, user)
        {
            MaxTokens = 1800, Temperature = 0.2f, Schema = LayoutSchemas.BriefDraft(), Purpose = "brief",
        };
        var reply = await llm.ChatAsync(request, null, ct);
        var parsed = SpecJson.Parse(reply.Text);
        if (parsed.Spec is null) throw new HarnessException("The model did not return a JSON brief: " + parsed.Error);
        var raw = parsed.Spec;

        var allowed = Allowed(prompt + " " + (options.Client ?? ""));
        var dropped = new List<string>();
        string client = (options.Client ?? raw["client"]?.ToString() ?? "").Trim();
        if (client.Length == 0) client = "Customer";
        string pitch = options.Pitch ?? ((raw["pitch_type"]?.ToString() ?? "").StartsWith("tech", StringComparison.OrdinalIgnoreCase) ? "technical" : "sales");

        var facts = new List<string>();
        if (raw["facts"] is JsonArray factArray)
            foreach (var node in factArray)
            {
                string fact = (node?.ToString() ?? "").Trim();
                var bad = Unsupported(fact, allowed);
                if (bad.Count > 0) dropped.Add($"fact: {fact}  [not in prompt: {string.Join(", ", bad)}]");
                else if (fact.Length > 0) facts.Add(fact);
            }
        var guardrails = new List<string>();
        if (raw["guardrails"] is JsonArray guardArray)
            foreach (var node in guardArray)
            {
                string guard = (node?.ToString() ?? "").Trim();
                if (guard.Length > 0 && Unsupported(guard, allowed).Count == 0) guardrails.Add(guard);
                else if (guard.Length > 0) dropped.Add($"guardrail: {guard}");
            }
        var topics = Topics(raw["required_topics"]);
        if (options.Provenance) topics[BriefAdapter.ProvenanceTopic] = [.. BriefAdapter.ProvenanceKeywords];
        var have = new HashSet<string>(guardrails.Select(g => g.ToLowerInvariant()));
        guardrails.AddRange(BriefAdapter.DefaultGuardrails.Where(g => !have.Contains(g.ToLowerInvariant())));

        string objective = Scrub(raw["objective"]?.ToString(), allowed, dropped, "objective");
        if (objective.Length == 0)
        {
            string firstLine = prompt.Split('\n')[0];
            objective = Scrub(firstLine.Length > 240 ? firstLine[..240] : firstLine, allowed, dropped, "objective");
            if (objective.Length == 0) objective = $"Agree next steps with {client}.";
        }
        string audience = Scrub(raw["audience"]?.ToString(), allowed, dropped, "audience");
        var brief = new Brief
        {
            Id = $"{Slug(client)}_{pitch}",
            Client = client,
            PitchType = pitch,
            Audience = audience.Length > 0 ? audience : $"{client} decision makers.",
            Objective = objective,
            TemplateHint = options.Template ?? "surface",
            SlideMin = 8,
            SlideMax = 12,
            Context = Scrub(raw["context"]?.ToString(), allowed, dropped, "context"),
            Facts = facts,
            RequiredTopics = topics,
            Guardrails = [.. guardrails.Take(8)],
            Source = "prompt",
            Prompt = prompt,
            Dropped = dropped,
        };
        return new DraftResult(brief, reply);
    }
}
