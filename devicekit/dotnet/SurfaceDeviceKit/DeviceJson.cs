using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SurfaceDeviceKit;

/// <summary>JSON used everywhere: the CLI, the Studio's /api/device and the golden vectors.</summary>
public static class DeviceJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string Serialize<T>(T value, bool indented = true)
    {
        var options = indented ? Options : new JsonSerializerOptions(Options) { WriteIndented = false };
        return JsonSerializer.Serialize(value, options);
    }

    public static RawHardware ParseRaw(string json) =>
        JsonSerializer.Deserialize<RawHardware>(json, Options) ?? throw new InvalidDataException("Hardware JSON is empty.");
}

/// <summary>Representative devices for demos and tests: they go through the same classifier as a real probe, but nothing here was measured on that hardware
/// (except the one profile whose note says so).</summary>
public static class SimulatedProfiles
{
    private static IReadOnlyList<SimulatedDevice>? _all;

    public static IReadOnlyList<SimulatedDevice> All => _all ??= Load(Catalog.ReadEmbedded("SurfaceDeviceKit.simulated-profiles.json"));

    /// <summary>The file has a "defaults" object whose keys fill in anything a profile's "raw" leaves out.</summary>
    public static IReadOnlyList<SimulatedDevice> Load(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })?.AsObject()
            ?? throw new InvalidDataException("The simulated profiles file is empty.");
        var defaults = root["defaults"]?.AsObject() ?? [];
        var list = new List<SimulatedDevice>();
        foreach (var node in root["profiles"]?.AsArray() ?? [])
        {
            var p = node!.AsObject();
            var merged = new JsonObject();
            foreach (var (key, value) in defaults) merged[key] = value?.DeepClone();
            foreach (var (key, value) in p["raw"]!.AsObject()) merged[key] = value?.DeepClone();
            var raw = merged.Deserialize<RawHardware>(DeviceJson.Options)!;
            list.Add(new SimulatedDevice((string)p["id"]!, (string?)p["group"] ?? "", (string?)p["label"] ?? "", (string?)p["note"] ?? "", raw));
        }
        return list;
    }

    public static SimulatedDevice? Find(string id) =>
        All.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
