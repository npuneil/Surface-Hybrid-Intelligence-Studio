using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Harness;
using Forge.Core.Spec;

namespace Forge.Tests;

/// <summary>A language model that answers from a script, so every harness path runs headless and deterministically. It records each request
/// (system, prompt, schema, purpose) so a test can check what the harness asked, not only what came back.</summary>
public sealed class ScriptedLlm(LlmInfo info, Func<LlmRequest, int, LlmReply> script) : ILlm
{
    public LlmInfo Info { get; } = info;
    public List<LlmRequest> Requests { get; } = [];

    public Task<LlmReply> ChatAsync(LlmRequest request, Action<string>? onText = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Add(request);
        var reply = script(request, Requests.Count - 1);
        onText?.Invoke(reply.Text);
        return Task.FromResult(reply);
    }

    /// <summary>A reply with plausible timings: about 40 tokens per second of decode.</summary>
    public static LlmReply Say(string text, bool guided = true, int promptTokens = 900, int completionTokens = 120) =>
        new(text, 0.4, 3.4, promptTokens, completionTokens, 3.0, guided);

    public static LlmInfo Model(string lane = "npu", int context = 32768, bool guidance = true) =>
        new("Scripted 7B", lane, lane.ToUpperInvariant(), "scripted runtime", context, "M", 7, guidance);
}

/// <summary>Writes the reply a well-behaved model would give for the slide a request asks about. Slides are short, use only allowed numbers
/// and weave in the first word of every topic the prompt lists, so a deck made from them is clean unless a test breaks one on purpose.</summary>
public static class DeckScript
{
    private static readonly Regex SlotPurpose = new(@"^slide (\d+): (\w+)$", RegexOptions.CultureInvariant);

    /// <summary>The 0-based slide index and slot a guided slot request is for; null for any other request.</summary>
    public static (int Index, string Slot)? SlotOf(LlmRequest request)
    {
        var m = SlotPurpose.Match(request.Purpose);
        return m.Success ? (int.Parse(m.Groups[1].Value) - 1, m.Groups[2].Value) : null;
    }

    public static bool IsRepair(LlmRequest request) => request.User.Contains("YOUR PREVIOUS ATTEMPT", StringComparison.Ordinal);

    /// <summary>The first keyword of each topic the prompt asks this slide to cover.</summary>
    public static List<string> TopicWords(LlmRequest request)
    {
        var words = new List<string>();
        var lines = request.User.Split('\n');
        int at = Array.FindIndex(lines, l => l.StartsWith("TOPICS THIS SLIDE MUST COVER", StringComparison.Ordinal));
        if (at < 0) return words;
        for (int i = at + 1; i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal); i++)
        {
            string list = lines[i][(lines[i].IndexOf(':') + 1)..];
            string first = list.Split(',')[0].Trim();
            if (first.Length > 0) words.Add(first);
        }
        return words;
    }

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["pain"] = "Reports Eat Too Many Hours", ["big_idea"] = "A Second Lane Beside The Cloud",
        ["position"] = "Keep Cloud, Add A Lane", ["architecture"] = "Sensitive Work Stays Inside",
        ["routing"] = "Each Task Takes The Right Lane", ["connectivity"] = "Weak Wi-Fi Stops Being A Blocker",
        ["device"] = "Four Layers, One Local Lane", ["value"] = "Measured Steps Show The Value",
        ["pilot"] = "A Short Pilot Proves It", ["cta"] = "Approve A Short Pilot",
        ["why_local"] = "Keep Sensitive Work Local",
        ["stack"] = "Four Layers, One Local Lane", ["memory"] = "Three Views Of One Memory",
        ["throughput"] = "Speed Is Measured On This PC", ["moe_analogy"] = "Three Engines Share The Work",
        ["codegen_rag"] = "Code Help Stays On Device", ["sizing"] = "Model Size Follows The Task",
    };

    private static JsonObject Item(string label, string detail, string icon) => new() { ["label"] = label, ["detail"] = detail, ["icon"] = icon };

    private static string Weave(string lead, IReadOnlyList<string> words) => words.Count == 0 ? lead : $"{lead}: {string.Join(" ", words)}";

    /// <summary>One slide as the model would write it for the layout (without the layout key, as the guided prompt asks).</summary>
    public static JsonObject Slide(string layout, string slot, IReadOnlyList<string> words, int number = 0)
    {
        string title = Titles.TryGetValue(slot, out string? t) ? t : "This Slide Makes One Clear Point";
        string takeaway = Weave("Like one road, two lanes", words);
        var slide = new JsonObject { ["title"] = title, ["takeaway"] = takeaway, ["notes"] = "Say the point in one sentence. Then show the picture and ask for questions." };
        switch (layout)
        {
            case "title":
                return new JsonObject
                {
                    ["kicker"] = "LOCAL AI PROPOSAL", ["title"] = "Drafting Moves Onto Your Own Device", ["subtitle"] = Weave("One local lane beside the cloud", words),
                    ["hero_icons"] = new JsonArray("laptop", "ai", "shield", "cloud"), ["notes"] = "Open with the outcome. Say what the pilot will measure.",
                };
            case "kpis":
                slide["data"] = new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject { ["value"] = "3", ["label"] = "steps per task", ["icon"] = "document", ["source"] = "fact sheet" },
                        new JsonObject { ["value"] = "2", ["label"] = "lanes in play", ["icon"] = "swap", ["source"] = "fact sheet" },
                        new JsonObject { ["value"] = "1", ["label"] = "device in hand", ["icon"] = "laptop", ["source"] = "fact sheet" }),
                };
                break;
            case "stack":
                slide["data"] = new JsonObject
                {
                    ["side_label"] = "Closest to you",
                    ["layers"] = new JsonArray(Item("Workflow", "Drafts, reviews", "document"), Item("Local models", "Run locally", "ai"),
                        Item("Runtime", "Picks engines", "gears"), Item("Silicon", "NPU, GPU, CPU", "chip")),
                };
                break;
            case "flow":
                slide["data"] = new JsonObject
                {
                    ["steps"] = new JsonArray(Item("Capture", "Notes arrive", "mic"), Item("Retrieve", "Find sources", "search"), Item("Draft", "Model drafts", "ai"),
                        Item("Review", "Expert checks", "check"), Item("Send", "Share result", "mail")),
                };
                break;
            case "compare":
                slide["data"] = new JsonObject
                {
                    ["left"] = new JsonObject { ["title"] = "Cloud", ["icon"] = "cloud", ["points"] = new JsonArray("Needs links", "Data leaves", "One lane") },
                    ["right"] = new JsonObject { ["title"] = "Hybrid", ["icon"] = "laptop", ["points"] = new JsonArray("Local first", "Data stays", "Two lanes") },
                    ["verdict"] = "Add one lane",
                };
                break;
            case "analogy":
                slide["data"] = new JsonObject
                {
                    ["concept"] = new JsonObject { ["label"] = "Hybrid AI", ["icon"] = "swap" },
                    ["analog"] = new JsonObject { ["label"] = "Two lanes", ["icon"] = "car" },
                    ["pairs"] = new JsonArray(
                        new JsonObject { ["concept"] = "Local lane", ["analog"] = "Fast lane" }, new JsonObject { ["concept"] = "Cloud lane", ["analog"] = "Freight lane" },
                        new JsonObject { ["concept"] = "Router", ["analog"] = "Traffic sign" }, new JsonObject { ["concept"] = "Policy", ["analog"] = "Road rules" }),
                };
                break;
            case "timeline":
                slide["data"] = new JsonObject
                {
                    ["milestones"] = new JsonArray(
                        new JsonObject { ["when"] = "Week 1", ["label"] = "Set up", ["icon"] = "gear" }, new JsonObject { ["when"] = "Week 2", ["label"] = "Run tasks", ["icon"] = "bolt" },
                        new JsonObject { ["when"] = "Week 3", ["label"] = "Measure", ["icon"] = "chart" }, new JsonObject { ["when"] = "Week 4", ["label"] = "Decide", ["icon"] = "flag" }),
                };
                break;
            case "matrix":
                slide["data"] = new JsonObject
                {
                    ["x_axis"] = "Repetition", ["y_axis"] = "Sensitivity", ["highlight"] = 1,
                    ["quadrants"] = new JsonArray(Item("Cloud", "Rare, open", "cloud"), Item("Local", "Frequent, private", "lock"), Item("Cloud", "Rare, private", "cloud"), Item("Either", "Frequent, open", "swap")),
                };
                break;
            case "bars":
                slide["data"] = new JsonObject
                {
                    ["unit"] = "tokens/s", ["highlight"] = "Model B", ["caption"] = "Measured on this PC, single stream", ["icon"] = "speed",
                    ["bars"] = new JsonArray(new JsonObject { ["label"] = "Model A", ["value"] = 4 }, new JsonObject { ["label"] = "Model B", ["value"] = 8 }),
                };
                break;
            case "canvas":
                return new JsonObject
                {
                    ["title"] = title, ["takeaway"] = takeaway, ["notes"] = slide["notes"]!.GetValue<string>(),
                    ["inside"] = new JsonArray(Item("Notes", "", "document"), Item("Local model", "", "ai"), Item("Reviewed draft", "", "check")),
                    ["inside_caption"] = "Stays inside", ["outside_title"] = "Outside",
                    ["outside"] = new JsonArray(Item("Cloud tool", "", "cloud"), Item("Shared mail", "", "mail")),
                };
            case "cta":
                slide["data"] = new JsonObject
                {
                    ["steps"] = new JsonArray(Item("Sponsor", "Approves the pilot", "approve"), Item("IT lead", "Sets up devices", "gear"), Item("Users", "Run real tasks", "people")),
                    ["ask"] = "Approve the pilot and name the owners.",
                };
                break;
            default:
                throw new ArgumentException("No script for layout " + layout);
        }
        return slide;
    }

    /// <summary>The reply for one guided request: a clean slide for the slot, with this slide's topic words woven in.</summary>
    public static string Reply(LlmRequest request, IReadOnlyList<ArcSlot> arc)
    {
        var (index, _) = SlotOf(request) ?? throw new InvalidOperationException("not a slot request: " + request.Purpose);
        var slot = arc[index];
        return Slide(slot.Layout, slot.Slot, TopicWords(request), index + 1).ToJsonString();
    }

    /// <summary>A whole deck as one reply (arc and one-shot modes): every content slide carries its share of the topics.</summary>
    public static JsonObject Deck(Brief brief, IReadOnlyList<ArcSlot> arc, bool withLayouts = true)
    {
        var topics = brief.RequiredTopics.Where(t => t.Key != BriefAdapter.ProvenanceTopic).Select(t => t.Value[0]).ToList();
        var slides = new JsonArray();
        for (int i = 0; i < arc.Count; i++)
        {
            var share = topics.Where((_, k) => 1 + k % Math.Max(1, arc.Count - 2) == i).ToList();
            var slide = arc[i].Layout == "canvas" ? Canvas(arc[i], share, i + 1) : Slide(arc[i].Layout, arc[i].Slot, share, i + 1);
            if (withLayouts) slide["layout"] = arc[i].Layout;
            slides.Add(slide);
        }
        return new JsonObject { ["title"] = "Local AI Proposal", ["client"] = brief.Client, ["slides"] = slides };
    }

    /// <summary>The canvas slot in a whole-deck reply is the described diagram, drawn by the composer like in guided mode.</summary>
    private static JsonObject Canvas(ArcSlot slot, IReadOnlyList<string> words, int number)
    {
        var description = Slide("canvas", slot.Slot, words, number);
        return CanvasComposer.ToSlide(description, DeviceStory.Generic);
    }
}
