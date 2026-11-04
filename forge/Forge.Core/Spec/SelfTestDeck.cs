using System.Text.Json.Nodes;

namespace Forge.Core.Spec;

/// <summary>A neutral deck with one slide per layout and placeholder words only. It checks that the layouts, the PPTX writer and the preview
/// work on the PC it runs on. It names no device and no model, so nothing in it can be mistaken for a claim, and no run ever offers it as a result.</summary>
public static class SelfTestDeck
{
    private const string Resource = "Forge.Core.self-test-deck.json";

    public static JsonObject Spec()
    {
        using var stream = typeof(SelfTestDeck).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"The {Resource} resource is missing from Forge.Core.");
        return JsonNode.Parse(stream)!.AsObject();
    }
}
