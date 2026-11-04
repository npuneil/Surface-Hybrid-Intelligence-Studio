using System.Text.Json.Nodes;
using Forge.Core.Ir;

namespace Forge.Core.Harness;

/// <summary>Builds the device-boundary diagram from a short content description. The layout is the validated exemplar structure
/// (a dashed device boundary of four lines, three connected boxes inside, an engine box, a cloud lane outside), so a small model
/// never has to place coordinates, and every box touches an arrow or line by construction. The device pill is taller and the engine
/// box wider than in the exemplar, so a real product name and engine label fit at full size and the lint's fit rule stays quiet.</summary>
public static class CanvasComposer
{
    private static JsonObject Box(string type, double x, double y, double w, double h, string label, string icon, string tone) =>
        new() { ["type"] = type, ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["label"] = label, ["icon"] = icon, ["tone"] = tone };

    private static JsonObject Line(string type, double x, double y, double x2, double y2, string tone, bool dashed = false)
    {
        var o = new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["x2"] = x2, ["y2"] = y2, ["tone"] = tone };
        if (dashed) o["dashed"] = true;
        return o;
    }

    private static JsonObject Text(double x, double y, double w, double h, string label, string size, string? tone) =>
        new JsonObject { ["type"] = "text", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["label"] = label, ["size"] = size }
            .Also(o => { if (tone is not null) o["tone"] = tone; });

    private static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }

    private static string Label(JsonNode? node, string fallback)
    {
        string text = node?["label"]?.GetValue<string>()?.Trim() ?? "";
        return text.Length > 0 ? text : fallback;
    }

    private static string IconOf(JsonNode? node, string fallback)
    {
        string name = node?["icon"]?.GetValue<string>()?.Trim() ?? "";
        return Icons.Known(name) ? name : fallback;
    }

    public static string EngineIcon(string lane) => lane == "gpu" ? "gpu" : "chip";

    /// <summary>The 17 diagram elements for a description with <c>inside</c> (3 items), <c>inside_caption</c>, <c>outside_title</c> and <c>outside</c> (2 items).</summary>
    public static JsonArray Compose(JsonObject description, DeviceStory story)
    {
        var inside = description["inside"] as JsonArray ?? [];
        var outside = description["outside"] as JsonArray ?? [];
        string caption = description["inside_caption"]?.GetValue<string>()?.Trim() ?? "";
        string outsideTitle = description["outside_title"]?.GetValue<string>()?.Trim() ?? "";

        var elements = new JsonArray
        {
            Box("pill", 0.2, 0.0, 7.8, 0.7, story.Name, "laptop", "primary"),
            Line("line", 0.2, 0.8, 8.0, 0.8, "accent2", dashed: true),
            Line("line", 0.2, 5.6, 8.0, 5.6, "accent2", dashed: true),
            Line("line", 0.2, 0.8, 0.2, 5.6, "accent2", dashed: true),
            Line("line", 8.0, 0.8, 8.0, 5.6, "accent2", dashed: true),
            Box("box", 0.5, 1.3, 2.2, 1.0, Label(inside.ElementAtOrDefault(0), "Local files"), IconOf(inside.ElementAtOrDefault(0), "document"), "accent2"),
            Line("arrow", 2.7, 1.8, 3.0, 1.8, "primary"),
            Box("box", 3.0, 1.3, 2.2, 1.0, Label(inside.ElementAtOrDefault(1), "Local model"), IconOf(inside.ElementAtOrDefault(1), "ai"), "primary"),
            Line("arrow", 5.2, 1.8, 5.5, 1.8, "primary"),
            Box("box", 5.5, 1.3, 2.2, 1.0, Label(inside.ElementAtOrDefault(2), "Reviewed draft"), IconOf(inside.ElementAtOrDefault(2), "check"), "accent1"),
            Line("line", 4.1, 2.3, 4.1, 3.3, "muted"),
            Box("box", 2.5, 3.3, 3.2, 0.9, story.Engine, EngineIcon(story.Lane), "accent4"),
            Text(0.5, 4.7, 7.2, 0.6, caption.Length > 0 ? caption : "Inside the boundary: files, prompts, drafts", "s", "muted"),
            Text(8.6, 0.0, 3.3, 0.55, outsideTitle.Length > 0 ? outsideTitle : "Cloud lane", "m", null),
            Box("box", 8.6, 1.3, 3.2, 1.0, Label(outside.ElementAtOrDefault(0), "Cloud assistant"), IconOf(outside.ElementAtOrDefault(0), "cloud"), "accent3"),
            Box("box", 8.6, 3.3, 3.2, 0.9, Label(outside.ElementAtOrDefault(1), "Everyday work"), IconOf(outside.ElementAtOrDefault(1), "chat"), "card"),
            Line("arrow", 10.2, 2.3, 10.2, 3.3, "accent3"),
        };
        return elements;
    }

    /// <summary>Turns the model's diagram description into a finished canvas slide.</summary>
    public static JsonObject ToSlide(JsonObject description, DeviceStory story) => new()
    {
        ["layout"] = "canvas",
        ["title"] = description["title"]?.DeepClone() ?? "",
        ["takeaway"] = description["takeaway"]?.DeepClone() ?? "",
        ["data"] = new JsonObject { ["elements"] = Compose(description, story) },
        ["notes"] = description["notes"]?.DeepClone() ?? "",
    };
}
