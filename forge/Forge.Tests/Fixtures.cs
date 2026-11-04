using System.Text.Json.Nodes;

namespace Forge.Tests;

/// <summary>Paths to the fixtures copied next to the test exe (forge\Forge.Tests\fixtures): real decks the Python Forge produced for
/// fictional clients, with the metrics it measured for each, and the briefs they were made from.</summary>
public static class Fixtures
{
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>Each deck directory name (for example "zava_technical-v1.1") with its spec, metrics and brief.</summary>
    public static IEnumerable<DeckFixture> Decks()
    {
        foreach (string dir in Directory.GetDirectories(Path.Combine(Root, "decks")).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            string id = System.Text.RegularExpressions.Regex.Replace(name, @"-v1\.[01]$", "");
            string brief = Path.Combine(Root, "briefs", id + ".json");
            yield return new DeckFixture(name, id,
                JsonNode.Parse(File.ReadAllText(Path.Combine(dir, id + ".spec.json")))!.AsObject(),
                JsonNode.Parse(File.ReadAllText(Path.Combine(dir, id + ".metrics.json")))!.AsObject(),
                File.Exists(brief) ? JsonNode.Parse(File.ReadAllText(brief))!.AsObject() : null);
        }
    }

    public static JsonObject SampleSpec() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "sample_spec.json")))!.AsObject();
}

public sealed record DeckFixture(string Name, string Id, JsonObject Spec, JsonObject Metrics, JsonObject? Brief)
{
    /// <summary>The theme key the deck was rendered with.</summary>
    public string Template => Metrics["template"]?.GetValue<string>() ?? "surface";
}

/// <summary>Tiny assertion helper that counts failures instead of throwing, so one run reports everything.</summary>
public sealed class Check
{
    private readonly string _area;
    public int Failures { get; private set; }
    public int Passed { get; private set; }

    public Check(string area) => _area = area;

    public void That(bool ok, string what, string detail = "")
    {
        if (ok) { Passed++; return; }
        Failures++;
        Console.WriteLine($"  FAIL [{_area}] {what}{(detail.Length > 0 ? " - " + detail : "")}");
    }

    public int Finish()
    {
        Console.WriteLine($"{_area}: {Passed} passed, {Failures} failed");
        return Failures;
    }
}
