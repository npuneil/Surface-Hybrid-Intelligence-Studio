using System.Text.Json.Nodes;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

/// <summary>The pre-staged briefs that ship inside the app, for fictional clients. They are device-neutral: {device} and its siblings are
/// filled in from the PC the deck is drafted on (<see cref="BriefAdapter"/>), and every fact about that PC comes from the device kit and
/// the loaded model, so the same brief reads right on a Snapdragon NPU, an Intel Core Ultra or an NVIDIA GPU.</summary>
public static class BundledBriefs
{
    private const string Prefix = "Forge.Core.briefs.";

    private static readonly Lazy<IReadOnlyList<Brief>> Loaded = new(Load);

    /// <summary>Sales briefs first, then technical, each group by client.</summary>
    public static IReadOnlyList<Brief> All => Loaded.Value;

    public static Brief? Find(string id) => All.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<Brief> Load()
    {
        var assembly = typeof(BundledBriefs).Assembly;
        var briefs = new List<Brief>();
        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            briefs.Add(Brief.FromJson(JsonNode.Parse(stream)!));
        }
        return [.. briefs.OrderBy(b => b.IsTechnical ? 1 : 0).ThenBy(b => b.Client, StringComparer.Ordinal)];
    }
}
