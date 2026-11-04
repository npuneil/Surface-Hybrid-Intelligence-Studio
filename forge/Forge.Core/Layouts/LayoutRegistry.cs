using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Forge.Core.Draw;

namespace Forge.Core.Layouts;

/// <summary>Draws one infographic layout into the area under the slide title. <paramref name="data"/> is the slide's "data" object,
/// or the slide itself when it has none. Layouts never read anything outside their data and the canvas theme.</summary>
public delegate void LayoutFn(Canvas c, JsonNode data);

/// <summary>The 13 infographic layouts, by the name a spec uses. "title" and "section" are not here: <see cref="DeckBuilder"/>
/// draws those itself, the way the Python Forge's render() does.</summary>
public static class LayoutRegistry
{
    public static IReadOnlyList<string> Names { get; } =
        ["stack", "flow", "hub", "compare", "kpis", "analogy", "bars", "timeline", "matrix", "memory", "cta", "statement", "canvas"];

    public static IReadOnlyDictionary<string, LayoutFn> All { get; } = new Dictionary<string, LayoutFn>
    {
        ["stack"] = StructureLayouts.Stack,
        ["flow"] = StructureLayouts.Flow,
        ["hub"] = StructureLayouts.Hub,
        ["compare"] = StructureLayouts.Compare,
        ["kpis"] = DataLayouts.Kpis,
        ["analogy"] = DataLayouts.Analogy,
        ["bars"] = DataLayouts.Bars,
        ["timeline"] = DataLayouts.Timeline,
        ["matrix"] = DataLayouts.Matrix,
        ["memory"] = DataLayouts.Memory,
        ["cta"] = StoryLayouts.Cta,
        ["statement"] = StoryLayouts.Statement,
        ["canvas"] = StoryLayouts.CanvasDsl,
    }.ToFrozenDictionary();
}
