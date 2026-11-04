using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

/// <summary>Turns model text into a deck spec. Small models fence their JSON, leave trailing commas, or stop mid-object when they run
/// out of tokens, so parsing is lenient and reports whether the text was clean JSON (the grade rewards that).</summary>
public static class SpecJson
{
    public sealed record Parsed(JsonObject? Spec, bool Strict, string Error);

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly Regex Fence = new(@"^```(?:json)?\s*|\s*```$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static Parsed Parse(string? text)
    {
        string raw = (text ?? "").Trim();
        string error = "no JSON object found";
        try
        {
            if (JsonNode.Parse(raw, null, new JsonDocumentOptions()) is JsonObject strict) return new Parsed(strict, true, "");
            error = "top-level value is not an object";
        }
        catch (JsonException exception) { error = exception.Message; }

        string body = Fence.Replace(raw, "");
        int a = body.IndexOf('{');
        if (a < 0) return new Parsed(null, false, error);
        int b = body.LastIndexOf('}');
        string chunk = b > a ? body[a..(b + 1)] : body[a..];
        foreach (string candidate in Candidates(chunk))
        {
            try
            {
                if (JsonNode.Parse(candidate, null, Lenient) is JsonObject spec) return new Parsed(spec, false, error);
            }
            catch (JsonException exception) { if (ReferenceEquals(candidate, chunk)) error = exception.Message; }
        }
        return new Parsed(null, false, error);
    }

    /// <summary>The text as is, then repairs for an object the model cut off: close it where it stopped, or cut back to an earlier
    /// comma and close from there.</summary>
    private static IEnumerable<string> Candidates(string text)
    {
        yield return text;
        var stack = new List<char>();
        var commas = new List<(int Index, string Closers)>();
        bool inString = false, escaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Add('}'); break;
                case '[': stack.Add(']'); break;
                case '}' or ']' when stack.Count > 0: stack.RemoveAt(stack.Count - 1); break;
                case ',': commas.Add((i, string.Concat(Enumerable.Reverse(stack)))); break;
            }
        }
        if (stack.Count > 0 || inString)
        {
            string closers = string.Concat(Enumerable.Reverse(stack));
            string tail = inString ? "\"" : "";
            yield return text.TrimEnd() + tail + closers;
            yield return text.TrimEnd().TrimEnd(',', ':') + tail + closers;
        }
        foreach (var (index, closers) in Enumerable.Reverse(commas).Take(60))
            yield return text[..index] + closers;
    }

    /// <summary>The slide objects of a spec; anything that is not an object becomes a statement slide, as the Python Forge did.</summary>
    public static JsonObject Normalize(JsonObject? spec, Brief brief)
    {
        var copy = spec is null ? new JsonObject() : (JsonObject)spec.DeepClone();
        var slides = new JsonArray();
        if (copy["slides"] is JsonArray existing)
            foreach (var s in existing)
                slides.Add(s is JsonObject o ? o.DeepClone() : new JsonObject { ["layout"] = "statement", ["title"] = s?.ToString() ?? "" });
        copy["slides"] = slides;
        if (copy["client"] is null) copy["client"] = brief.Client;
        if (copy["title"] is null) copy["title"] = brief.Objective.Length > 60 ? brief.Objective[..60] : brief.Objective;
        return copy;
    }

    public static List<JsonObject> SlideObjects(JsonObject spec) =>
        spec["slides"] is JsonArray a ? [.. a.OfType<JsonObject>()] : [];

    public static JsonObject WithSlides(JsonObject spec, IEnumerable<JsonObject> slides)
    {
        var copy = (JsonObject)spec.DeepClone();
        copy["slides"] = new JsonArray([.. slides.Select(s => (JsonNode?)s.DeepClone())]);
        return copy;
    }

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Pretty(JsonNode node) => node.ToJsonString(PrettyOptions);

    public static string Compact(JsonNode node) => node.ToJsonString(CompactOptions);
}
