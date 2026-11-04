using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Forge.Core.Spec;

/// <summary>What is on a slide and which numbers it quotes: the text helpers lint and grade both read (the Python schema.py ones).</summary>
public static class SpecText
{
    /// <summary>Keys whose values are not text a viewer reads: layout and style names, icons, geometry, flags.</summary>
    private static readonly HashSet<string> VisibleSkip = new(StringComparer.Ordinal)
    {
        "layout", "icon", "hero_icons", "tone", "type", "notes", "speaker_notes", "align", "size",
        "highlight", "x", "y", "w", "h", "x2", "y2", "dashed", "verdict_icon", "ask_icon", "unit_icon",
    };

    /// <summary>Keys whose numeric values are claims drawn on the slide (chart values, capacities) and so count as text.</summary>
    private static readonly HashSet<string> NumericClaimKeys = new(StringComparer.Ordinal) { "value", "size", "capacity" };

    private static readonly Regex Number = new(@"(?<![A-Za-z\d.])\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant);

    /// <summary>Every on-canvas string of every slide, with its slide index and the key it sat under (null for a bare list item at the top).</summary>
    public static List<(int Slide, string? Key, string Text)> VisibleStrings(JsonObject spec)
    {
        var output = new List<(int, string?, string)>();
        if (spec["slides"] is JsonArray slides)
            for (int i = 0; i < slides.Count; i++)
                Walk(slides[i], i, null, output);
        return output;
    }

    /// <summary>The on-canvas strings of one slide.</summary>
    public static List<(string? Key, string Text)> SlideStrings(JsonNode? slide)
    {
        var output = new List<(int, string?, string)>();
        Walk(slide, 0, null, output);
        return [.. output.Select(o => (o.Item2, o.Item3))];
    }

    /// <summary>Speaker notes per slide (notes, else speaker_notes, else ""), one entry for every slide that is an object.</summary>
    public static List<string> NotesText(JsonObject spec)
    {
        var output = new List<string>();
        if (spec["slides"] is JsonArray slides)
            foreach (var node in slides)
                if (node is JsonObject slide)
                    output.Add(Py.Truthy(slide["notes"]) ? Py.Str(slide["notes"]) : Py.StrOrEmpty(slide["speaker_notes"]));
        return output;
    }

    /// <summary>Every number in the text as a double: "1,200" is 1200, "3.5" is 3.5, a digit run glued to letters ("A8", "F2") is not a number.</summary>
    public static List<double> Numbers(string? text)
    {
        var output = new List<double>();
        foreach (Match m in Number.Matches(text ?? ""))
            if (double.TryParse(Py.AsciiDigits(m.Value.Replace(",", "")), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                output.Add(v);
        return output;
    }

    /// <summary>The numbers a deck may quote because the brief states them: those in the audience, objective, context, facts,
    /// guardrails and slide range. Read from the text of the brief's JSON, so a digit that follows an escape such as \n does not count.</summary>
    public static HashSet<double> BriefNumbers(Brief brief)
    {
        var sb = new StringBuilder("{\"audience\": ");
        AppendJson(sb, brief.Audience);
        sb.Append(", \"objective\": ");
        AppendJson(sb, brief.Objective);
        sb.Append(", \"context\": ");
        AppendJson(sb, brief.Context);
        sb.Append(", \"facts\": ");
        AppendJsonList(sb, brief.Facts);
        sb.Append(", \"guardrails\": ");
        AppendJsonList(sb, brief.Guardrails);
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $", \"slide_range\": [{brief.SlideMin}, {brief.SlideMax}]}}");
        return [.. Numbers(sb.ToString())];
    }

    private static void Walk(JsonNode? node, int index, string? key, List<(int, string?, string)> output)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    if (VisibleSkip.Contains(k)) continue;
                    if (NumericClaimKeys.Contains(k) && Py.IsNumber(v)) { output.Add((index, k, Py.Str(v))); continue; }
                    Walk(v, index, k, output);
                }
                break;
            case JsonArray a:
                foreach (var v in a) Walk(v, index, key, output);
                break;
            case JsonValue v when v.TryGetValue<string>(out string? s):
                output.Add((index, key, s));
                break;
        }
    }

    private static void AppendJsonList(StringBuilder sb, List<string> items)
    {
        sb.Append('[');
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            AppendJson(sb, items[i]);
        }
        sb.Append(']');
    }

    /// <summary>json.dumps(text, ensure_ascii=False).</summary>
    private static void AppendJson(StringBuilder sb, string text)
    {
        sb.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case < ' ': sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}
