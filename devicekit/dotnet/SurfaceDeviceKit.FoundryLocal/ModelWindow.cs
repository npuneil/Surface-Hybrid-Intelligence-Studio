using System.Text.Json;
using Microsoft.AI.Foundry.Local;

namespace SurfaceDeviceKit.FoundryLocal;

/// <summary>
/// The context window of a model that is on this PC. Foundry Local's catalog often leaves it blank, but every build ships a
/// <c>genai_config.json</c> that states it, so a cached build can be asked directly. A caller that knows the window can size its
/// prompts to it instead of assuming the smallest.
/// </summary>
public static class ModelWindow
{
    private const string ConfigFile = "genai_config.json";

    /// <summary>The window of a cached build, or null when the build is not on this PC or states none.</summary>
    internal static async Task<long?> ReadAsync(IModel variant, CancellationToken ct)
    {
        try
        {
            string folder = await variant.GetPathAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(folder) ? null : FromFolder(folder);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Reads the window from the folder a build was unpacked to (or one folder below it).</summary>
    public static long? FromFolder(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        string direct = Path.Combine(folder, ConfigFile);
        if (File.Exists(direct)) return FromConfig(direct);
        foreach (string child in Directory.EnumerateDirectories(folder))
        {
            string nested = Path.Combine(child, ConfigFile);
            if (File.Exists(nested) && FromConfig(nested) is { } window) return window;
        }
        return null;
    }

    /// <summary>Reads <c>model.context_length</c>, else <c>search.max_length</c>, from one genai_config.json. A value that is not a positive number is ignored.</summary>
    public static long? FromConfig(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            return Positive(root, "model", "context_length") ?? Positive(root, "search", "max_length");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static long? Positive(JsonElement root, string section, string key) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(section, out var block) && block.ValueKind == JsonValueKind.Object
        && block.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number) && number > 0
            ? number
            : null;
}
