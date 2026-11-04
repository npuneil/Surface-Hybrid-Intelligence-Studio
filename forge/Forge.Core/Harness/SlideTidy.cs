using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Ir;

namespace Forge.Core.Harness;

/// <summary>Turns a model's reply for one slot into a slide the renderer can draw. The slot's layout is forced, the fixed parts (hero icons,
/// the canvas diagram) are rebuilt, and the structural slips a small model makes are repaired without changing what it said.
/// A reply that cannot become a slide (no title, no items) is reported, so the harness can ask again.</summary>
public static class SlideTidy
{
    private static readonly string[] DefaultHeroIcons = ["laptop", "ai", "shield", "cloud"];

    private static readonly Dictionary<string, string?> CollectionKeys = new(StringComparer.Ordinal)
    {
        ["stack"] = "layers", ["flow"] = "steps", ["hub"] = "spokes", ["compare"] = null, ["kpis"] = "items", ["analogy"] = "pairs",
        ["bars"] = "bars", ["timeline"] = "milestones", ["matrix"] = "quadrants", ["cta"] = "steps",
    };

    private static readonly Regex LeadingNumber = new(@"-?\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ai"] = "AI", ["api"] = "API", ["cpu"] = "CPU", ["cta"] = "CTA", ["gpu"] = "GPU", ["kpi"] = "KPI", ["kpis"] = "KPIs", ["llm"] = "LLM",
        ["moe"] = "MoE", ["npu"] = "NPU", ["os"] = "OS", ["pc"] = "PC", ["ram"] = "RAM", ["rag"] = "RAG", ["roi"] = "ROI", ["tco"] = "TCO",
        ["ui"] = "UI", ["ux"] = "UX", ["vram"] = "VRAM",
    };

    /// <summary>A slot id as a person reads it: "moe_analogy" becomes "MoE analogy" and "cta" becomes "CTA".</summary>
    public static string PrettySlot(string slot)
    {
        var words = slot.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Acronyms.TryGetValue(w, out string? acronym) ? acronym : w.ToLowerInvariant()).ToArray();
        if (words.Length == 0) return "";
        if (!Acronyms.ContainsKey(words[0]) && char.IsLower(words[0][0])) words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        return string.Join(' ', words);
    }

    private static JsonObject Unwrap(JsonObject reply)
    {
        if (reply["slide"] is JsonObject inner) return inner;
        if (reply["slides"] is JsonArray { Count: > 0 } list && list[0] is JsonObject first) return first;
        return reply;
    }

    /// <summary>The first non-empty text among the keys; numbers are written as text.</summary>
    public static string TextOf(JsonNode? node, params string[] keys)
    {
        if (node is not JsonObject o) return "";
        foreach (string key in keys)
        {
            if (!o.TryGetPropertyValue(key, out var value) || value is not JsonValue v) continue;
            string text = v.TryGetValue<string>(out string? s) ? s : v.ToString();
            text = text.Trim();
            if (text.Length > 0) return text;
        }
        return "";
    }

    private static JsonArray HeroIcons(JsonNode? node)
    {
        var names = new List<string>();
        if (node is JsonArray array)
            foreach (var item in array)
            {
                string name = item is JsonObject o ? TextOf(o, "icon", "name") : (item as JsonValue)?.ToString() ?? "";
                name = name.Trim().ToLowerInvariant().Replace(' ', '_');
                if (Icons.Known(name) && !names.Contains(name)) names.Add(name);
            }
        foreach (string fallback in DefaultHeroIcons)
            if (names.Count < 4 && !names.Contains(fallback)) names.Add(fallback);
        return new JsonArray([.. names.Take(4).Select(n => (JsonNode?)JsonValue.Create(n))]);
    }

    /// <summary>Exactly <paramref name="count"/> labelled items at most, whatever shape the model used (strings or objects).</summary>
    private static JsonArray LabelledItems(JsonNode? node, int count)
    {
        var items = new JsonArray();
        if (node is JsonArray array)
            foreach (var item in array)
            {
                if (items.Count == count) break;
                string label = item is JsonObject o ? TextOf(o, "label", "name", "title", "text") : (item as JsonValue)?.ToString() ?? "";
                if (label.Length == 0) continue;
                var entry = new JsonObject { ["label"] = label };
                string icon = item is JsonObject io ? TextOf(io, "icon") : "";
                if (icon.Length > 0) entry["icon"] = icon;
                items.Add(entry);
            }
        return items;
    }

    public static bool TryBuild(JsonObject reply, ArcSlot slot, DeviceStory story, out JsonObject slide, out JsonObject? description, out string error)
    {
        slide = new JsonObject();
        description = null;
        error = "";
        var o = Unwrap(reply);
        string layout = slot.Layout;
        string title = TextOf(o, "title");
        if (title.Length == 0)
        {
            error = "the slide has no title";
            return false;
        }

        if (layout == "title")
        {
            slide = new JsonObject
            {
                ["layout"] = "title",
                ["kicker"] = TextOf(o, "kicker"),
                ["title"] = title,
                ["subtitle"] = TextOf(o, "subtitle"),
                ["hero_icons"] = HeroIcons(o["hero_icons"]),
                ["notes"] = TextOf(o, "notes", "speaker_notes"),
            };
            return true;
        }

        if (layout == "canvas")
        {
            var inside = LabelledItems(o["inside"], 3);
            if (inside.Count == 0)
            {
                error = "the diagram has no inside items";
                return false;
            }
            description = new JsonObject
            {
                ["title"] = title,
                ["takeaway"] = TextOf(o, "takeaway"),
                ["inside"] = inside,
                ["inside_caption"] = TextOf(o, "inside_caption", "caption"),
                ["outside_title"] = TextOf(o, "outside_title"),
                ["outside"] = LabelledItems(o["outside"], 2),
                ["notes"] = TextOf(o, "notes", "speaker_notes"),
            };
            slide = CanvasComposer.ToSlide(description, story);
            return true;
        }

        var data = o["data"] as JsonObject;
        CollectionKeys.TryGetValue(layout, out string? key);
        if (data is null && key is not null && o[key] is JsonArray) data = o;
        if (data is null && layout == "compare" && o["left"] is JsonObject && o["right"] is JsonObject) data = o;
        if (data is null)
        {
            error = "the slide has no data";
            return false;
        }
        data = (JsonObject)data.DeepClone();
        if (key is not null && data[key] is not JsonArray { Count: > 0 })
        {
            error = $"data.{key} is missing or empty";
            return false;
        }
        if (layout == "compare" && (data["left"] is not JsonObject || data["right"] is not JsonObject))
        {
            error = "data.left or data.right is missing";
            return false;
        }

        if (layout == "bars") FixBars(data);
        if (layout == "matrix") FixMatrix(data);
        if (layout == "kpis") FixKpis(data);
        slide = new JsonObject
        {
            ["layout"] = layout,
            ["title"] = title,
            ["takeaway"] = TextOf(o, "takeaway"),
            ["data"] = data,
            ["notes"] = TextOf(o, "notes", "speaker_notes"),
        };
        return true;
    }

    private static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<double>(out value)) return true;
        if (v.TryGetValue<string>(out string? s))
        {
            var m = LeadingNumber.Match(s ?? "");
            return m.Success && double.TryParse(m.Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        return false;
    }

    /// <summary>Bar values become numbers and the highlight becomes exactly one bar label (the tallest when the model named none).</summary>
    private static void FixBars(JsonObject data)
    {
        if (data["bars"] is not JsonArray bars) return;
        var labels = new List<string>();
        string tallest = "";
        double max = double.NegativeInfinity;
        foreach (var bar in bars.OfType<JsonObject>())
        {
            string label = TextOf(bar, "label");
            if (label.Length == 0) continue;
            bar["label"] = label;
            double value = 0;
            if (TryNumber(bar["value"], out double parsed)) value = parsed;
            bar["value"] = value;
            labels.Add(label);
            if (value > max) { max = value; tallest = label; }
        }
        string highlight = TextOf(data, "highlight");
        string? exact = labels.FirstOrDefault(l => l == highlight) ?? labels.FirstOrDefault(l => l.Equals(highlight, StringComparison.OrdinalIgnoreCase));
        data["highlight"] = exact ?? tallest;
    }

    private static void FixMatrix(JsonObject data)
    {
        int highlight = TryNumber(data["highlight"], out double h) ? (int)h : 1;
        data["highlight"] = Math.Clamp(highlight, 0, 3);
    }

    /// <summary>KPI values are drawn as text, so a number the model wrote as a number is written out. A long rate is shortened ("tokens per
    /// second" becomes "tok/s"), because a tile's big number is wrapped onto two lines when it is wider than the tile.</summary>
    private static void FixKpis(JsonObject data)
    {
        if (data["items"] is not JsonArray items) return;
        foreach (var item in items.OfType<JsonObject>())
        {
            if (item["value"] is JsonValue v && !v.TryGetValue<string>(out _))
                item["value"] = Convert.ToString(v.ToString(), CultureInfo.InvariantCulture);
            if (item["value"] is JsonValue text && text.TryGetValue<string>(out string? shown) && shown.Length > 8)
                item["value"] = TokensPerSecond.Replace(shown, "tok/s");
        }
    }

    private static readonly Regex TokensPerSecond = new(@"\btokens?\s*(?:/|per)\s*(?:s|secs?|seconds?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A bare slide for a slot the model could not write, so the deck still builds; lint flags it and a repair pass tries again.</summary>
    public static JsonObject Skeleton(ArcSlot slot, DeviceStory story)
    {
        string title = PrettySlot(slot.Slot);
        if (slot.Layout == "title")
            return new JsonObject
            {
                ["layout"] = "title", ["kicker"] = "", ["title"] = title, ["subtitle"] = "",
                ["hero_icons"] = HeroIcons(null), ["notes"] = "",
            };
        if (slot.Layout == "canvas") return CanvasComposer.ToSlide(new JsonObject { ["title"] = title }, story);
        return new JsonObject { ["layout"] = slot.Layout, ["title"] = title, ["takeaway"] = "", ["data"] = new JsonObject(), ["notes"] = "" };
    }
}
