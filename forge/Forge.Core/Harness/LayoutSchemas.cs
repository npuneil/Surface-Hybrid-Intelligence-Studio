using System.Text.Json.Nodes;
using Forge.Core.Ir;

namespace Forge.Core.Harness;

/// <summary>JSON schemas for constrained decoding. With a schema the grammar guarantees the shape (right keys, icon names from the
/// renderer's table, item counts), so a small model spends its tokens on content and never on repairing structure. Strings carry a
/// generous length cap as a guard against run-on text; the lint still enforces the real word limits.
/// The root carries an "x-guidance" block (compact output: no whitespace between tokens, which cut a slide by about 38 percent).</summary>
public static class LayoutSchemas
{
    private static JsonObject Str(int max) => new() { ["type"] = "string", ["maxLength"] = max };

    private static JsonObject Num() => new() { ["type"] = "number" };

    private static JsonObject Icon() =>
        new() { ["type"] = "string", ["enum"] = new JsonArray([.. Icons.Names.Select(n => (JsonNode?)JsonValue.Create(n))]) };

    private static JsonObject IntEnum(params int[] values) =>
        new() { ["type"] = "integer", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode?)JsonValue.Create(v))]) };

    private static JsonObject Arr(JsonObject item, int min, int max) =>
        new() { ["type"] = "array", ["minItems"] = min, ["maxItems"] = max, ["items"] = item };

    private static JsonObject Obj(params (string Key, JsonObject Schema)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (key, schema) in props)
        {
            properties[key] = schema;
            required.Add(key);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }

    private static JsonObject WithGuidance(JsonObject root)
    {
        root["x-guidance"] = new JsonObject { ["whitespace_flexible"] = false, ["item_separator"] = ",", ["key_separator"] = ":" };
        return root;
    }

    private static JsonObject Labelled() => Obj(("label", Str(32)), ("icon", Icon()));

    private static JsonObject Step() => Obj(("label", Str(32)), ("detail", Str(56)), ("icon", Icon()));

    private static JsonObject Side() => Obj(("title", Str(40)), ("icon", Icon()), ("points", Arr(Str(56), 3, 3)));

    private static JsonObject Data(string layout, string slot) => layout switch
    {
        "stack" => Obj(("side_label", Str(32)), ("layers", Arr(Step(), 4, 4))),
        "flow" => Obj(("steps", Arr(Step(), 5, 5))),
        "hub" => Obj(("center", Labelled()), ("spokes", Arr(Labelled(), 4, 6))),
        "compare" => Obj(("left", Side()), ("right", Side()), ("verdict", Str(110))),
        "kpis" => Obj(("items", slot == "memory"
            ? Arr(Obj(("value", Str(18)), ("label", Str(36)), ("icon", Icon()), ("source", Str(40))), 3, 3)
            : Arr(Obj(("value", Str(18)), ("label", Str(36)), ("icon", Icon()), ("source", Str(40))), 3, 4))),
        "analogy" => Obj(("concept", Labelled()), ("analog", Labelled()),
            ("pairs", Arr(Obj(("concept", Str(48)), ("analog", Str(48))), 4, 4))),
        "bars" => Obj(("unit", Str(20)), ("highlight", Str(40)), ("icon", Icon()), ("caption", Str(90)),
            ("bars", Arr(Obj(("label", Str(40)), ("value", Num())), 2, 6))),
        "timeline" => Obj(("milestones", Arr(Obj(("when", Str(24)), ("label", Str(36)), ("icon", Icon())), 4, 4))),
        "matrix" => Obj(("x_axis", Str(28)), ("y_axis", Str(28)), ("highlight", IntEnum(0, 1, 2, 3)),
            ("quadrants", Arr(Step(), 4, 4))),
        "cta" => Obj(("steps", Arr(Step(), 3, 3)), ("ask", Str(120))),
        "statement" => Obj(("icon", Icon()), ("text", Str(80)), ("sub", Str(90))),
        _ => new JsonObject { ["type"] = "object" },
    };

    /// <summary>The schema of one slide for a slot. With <paramref name="boundary"/> the canvas slot asks for the diagram's content
    /// (see <see cref="CanvasComposer"/>) instead of raw coordinates.</summary>
    public static JsonObject ForSlot(string layout, string slot, bool boundary)
    {
        if (layout == "title")
            return WithGuidance(Obj(("kicker", Str(48)), ("title", Str(64)), ("subtitle", Str(110)), ("hero_icons", Arr(Icon(), 4, 4)), ("notes", Str(360))));
        if (layout == "canvas" && boundary)
            return WithGuidance(Obj(
                ("title", Str(64)), ("takeaway", Str(110)),
                ("inside", Arr(Labelled(), 3, 3)), ("inside_caption", Str(80)),
                ("outside_title", Str(24)), ("outside", Arr(Labelled(), 2, 2)),
                ("notes", Str(360))));
        return WithGuidance(Obj(("title", Str(64)), ("takeaway", Str(110)), ("data", Data(layout, slot)), ("notes", Str(360))));
    }

    /// <summary>Arc mode lets the model design the whole deck: the schema only guarantees valid JSON of the right outer shape.</summary>
    public static JsonObject DeckEnvelope()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject { ["type"] = "string" },
                ["client"] = new JsonObject { ["type"] = "string" },
                ["slides"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "object" } },
            },
            ["required"] = new JsonArray("title", "client", "slides"),
        };
        return WithGuidance(root);
    }

    public static JsonObject FixesEnvelope()
    {
        var item = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["index"] = new JsonObject { ["type"] = "integer" },
                ["slide"] = new JsonObject { ["type"] = "object" },
            },
            ["required"] = new JsonArray("index", "slide"),
        };
        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["fixes"] = new JsonObject { ["type"] = "array", ["items"] = item } },
            ["required"] = new JsonArray("fixes"),
        };
        return WithGuidance(root);
    }

    /// <summary>The brief a model drafts from a free-form request.</summary>
    public static JsonObject BriefDraft()
    {
        var list = () => new JsonObject { ["type"] = "array", ["items"] = Str(240) };
        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["client"] = Str(60),
                ["pitch_type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("sales", "technical") },
                ["audience"] = Str(240),
                ["objective"] = Str(300),
                ["context"] = Str(700),
                ["facts"] = list(),
                ["required_topics"] = new JsonObject
                {
                    ["type"] = "array",
                    ["minItems"] = 3,
                    ["maxItems"] = 7,
                    ["items"] = Obj(("topic", Str(40)), ("keywords", Arr(Str(28), 3, 6))),
                },
                ["guardrails"] = list(),
            },
            ["required"] = new JsonArray("client", "pitch_type", "audience", "objective", "context", "facts", "required_topics", "guardrails"),
            ["additionalProperties"] = false,
        };
        return WithGuidance(root);
    }
}
