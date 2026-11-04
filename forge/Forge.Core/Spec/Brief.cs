using System.Text.Json.Nodes;

namespace Forge.Core.Spec;

/// <summary>What a deck is about. The same JSON the Python Forge used (briefs\*.json) and the Studio writes, so old briefs open as they are.
/// Facts are plain sentences; the prompts refer to them as F1, F2 and so on. Required topics map a name to the keywords that show
/// the topic is covered on a slide.</summary>
public sealed class Brief
{
    public string Id { get; set; } = "deck";
    public string Client { get; set; } = "";
    /// <summary>"sales" or "technical".</summary>
    public string PitchType { get; set; } = "sales";
    public string Audience { get; set; } = "";
    public string Objective { get; set; } = "";
    public string TemplateHint { get; set; } = "surface";
    public string Context { get; set; } = "";
    public List<string> Facts { get; set; } = [];
    public Dictionary<string, List<string>> RequiredTopics { get; set; } = new(StringComparer.Ordinal);
    public List<string> Guardrails { get; set; } = [];
    public int SlideMin { get; set; } = 8;
    public int SlideMax { get; set; } = 12;
    /// <summary>"prompt" when the brief was drafted from a free-form request; empty for hand-written briefs.</summary>
    public string Source { get; set; } = "";
    public string Prompt { get; set; } = "";
    /// <summary>Numbers or claims from a drafted brief that were dropped because the user's prompt did not support them.</summary>
    public List<string> Dropped { get; set; } = [];

    public bool IsTechnical => PitchType.Equals("technical", StringComparison.OrdinalIgnoreCase);

    public static Brief FromJson(JsonNode node)
    {
        var o = node as JsonObject ?? throw new FormatException("A brief must be a JSON object.");
        var brief = new Brief
        {
            Id = Str(o, "id", "deck"),
            Client = Str(o, "client"),
            PitchType = Str(o, "pitch_type", "sales").ToLowerInvariant(),
            Audience = Str(o, "audience"),
            Objective = Str(o, "objective"),
            TemplateHint = Str(o, "template_hint", "surface"),
            Context = Str(o, "context"),
            Source = Str(o, "source"),
            Prompt = Str(o, "prompt"),
            Facts = Strings(o["facts"]),
            Guardrails = Strings(o["guardrails"]),
            Dropped = Strings(o["dropped"]),
        };
        if (o["required_topics"] is JsonObject topics)
            foreach (var (name, keywords) in topics)
                brief.RequiredTopics[name] = Strings(keywords);
        if (o["slide_range"] is JsonArray { Count: >= 2 } range
            && range[0] is JsonValue lo && range[1] is JsonValue hi
            && lo.TryGetValue<int>(out int min) && hi.TryGetValue<int>(out int max))
        {
            brief.SlideMin = Math.Max(3, min);
            brief.SlideMax = Math.Max(brief.SlideMin, max);
        }
        return brief;
    }

    public static Brief Load(string path) => FromJson(JsonNode.Parse(File.ReadAllText(path))!);

    public JsonObject ToJson()
    {
        var topics = new JsonObject();
        foreach (var (name, keywords) in RequiredTopics) topics[name] = new JsonArray([.. keywords.Select(k => (JsonNode?)JsonValue.Create(k))]);
        var o = new JsonObject
        {
            ["id"] = Id,
            ["client"] = Client,
            ["pitch_type"] = PitchType,
            ["audience"] = Audience,
            ["objective"] = Objective,
            ["template_hint"] = TemplateHint,
            ["slide_range"] = new JsonArray(SlideMin, SlideMax),
            ["context"] = Context,
            ["facts"] = new JsonArray([.. Facts.Select(f => (JsonNode?)JsonValue.Create(f))]),
            ["required_topics"] = topics,
            ["guardrails"] = new JsonArray([.. Guardrails.Select(f => (JsonNode?)JsonValue.Create(f))]),
        };
        if (Source.Length > 0) o["source"] = Source;
        if (Prompt.Length > 0) o["prompt"] = Prompt;
        if (Dropped.Count > 0) o["dropped"] = new JsonArray([.. Dropped.Select(f => (JsonNode?)JsonValue.Create(f))]);
        return o;
    }

    private static string Str(JsonObject o, string key, string fallback = "") =>
        o[key] is JsonValue v && v.TryGetValue<string>(out string? s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : fallback;

    private static List<string> Strings(JsonNode? node) => node switch
    {
        JsonArray a => [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out string? s) ? s : x?.ToString() ?? "").Where(s => s.Length > 0)],
        JsonValue v when v.TryGetValue<string>(out string? s) && s.Length > 0 => [s],
        _ => [],
    };
}
