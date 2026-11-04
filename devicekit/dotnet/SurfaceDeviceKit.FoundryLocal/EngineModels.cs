using System.Text.Json.Nodes;
using Microsoft.AI.Foundry.Local;

namespace SurfaceDeviceKit.FoundryLocal;

public enum EngineState { NotStarted, Starting, Ready, Unavailable }

public sealed class EngineOptions
{
    /// <summary>Folder name under %LOCALAPPDATA% for this app's own logs, models and execution providers.</summary>
    public string AppName { get; init; } = "SurfaceDeviceKit";

    /// <summary>Other Surface demo apps on the same PC. A model one of them already downloaded is reused instead of downloaded again.</summary>
    public IReadOnlyList<string> SharedAppData { get; init; } = ["SurfaceSalesKit", "ASUAgenticShowcase", "SurfaceCES2027"];

    /// <summary>Forces the app data folder (SURFACE_DEVICEKIT_APPDATA does the same).</summary>
    public string? AppDataDirectory { get; init; }

    public string? LogsDirectory { get; init; }

    /// <summary>Register every execution provider a lane lists instead of only the first one that works. Costs downloads, so off by default.</summary>
    public bool RegisterAllEps { get; init; }

    public Action<string>? Log { get; init; }
}

/// <summary>A model variant that exists in this PC's live Foundry Local catalog, runs on one lane, and fits that lane's memory budget.</summary>
public sealed record ResolvedModel
{
    public required string Workload { get; init; }
    public required string Alias { get; init; }
    public required string Title { get; init; }
    public required string Class { get; init; }
    public double ParamsB { get; init; }
    /// <summary>npu, gpu or cpu.</summary>
    public required string Lane { get; init; }
    /// <summary>NPU, GPU or CPU, as Foundry Local names the device.</summary>
    public required string Device { get; init; }
    public required string Ep { get; init; }
    public required string EpLabel { get; init; }
    public required string VariantId { get; init; }
    /// <summary>Real file size from the live catalog.</summary>
    public double FileGb { get; init; }
    public double RequiredGb { get; init; }
    public bool Cached { get; init; }
    /// <summary>What the model does, as Foundry Local names it: chat-completion, vision-language-chat, automatic-speech-recognition, embeddings.</summary>
    public string Task { get; init; } = "";
    /// <summary>True for models a chat session can talk to. Speech and embedding models are driven through their own clients.</summary>
    public bool IsChat => Task.Length == 0 || Task.Contains("chat", StringComparison.OrdinalIgnoreCase);
    /// <summary>Context window the runtime reports, when it reports one.</summary>
    public long? ContextTokens { get; init; }
    internal IModel Variant { get; init; } = null!;

    public string LaneLabel => Device.ToUpperInvariant();

    public string Summary =>
        $"{Title} on the {LaneLabel} ({EpLabel}), {EngineFormat.Gb(FileGb)}{(Cached ? ", on this PC" : ", needs a download")}";

    public JsonObject Describe() => new()
    {
        ["workload"] = Workload, ["alias"] = Alias, ["title"] = Title, ["class"] = Class, ["lane"] = Lane, ["device"] = Device, ["ep"] = EpLabel,
        ["variant"] = VariantId, ["fileGb"] = Math.Round(FileGb, 2), ["requiredGb"] = Math.Round(RequiredGb, 2), ["cached"] = Cached, ["task"] = Task,
        ["contextTokens"] = ContextTokens,
    };
}

/// <summary>Every model that can run on this PC for a workload, strongest first, plus why anything was left out.</summary>
public sealed record ResolvedPlan(string Workload, string Title, IReadOnlyList<ResolvedModel> Options, IReadOnlyList<string> Notes)
{
    public ResolvedModel? Primary => Options.Count > 0 ? Options[0] : null;

    /// <summary>The strongest option that is already on this PC, so it can start without a download.</summary>
    public ResolvedModel? PrimaryOnThisPc => Options.FirstOrDefault(o => o.Cached);

    public JsonObject Describe() => new()
    {
        ["workload"] = Workload, ["title"] = Title,
        ["options"] = new JsonArray(Options.Select(o => (JsonNode)o.Describe()).ToArray()),
        ["notes"] = new JsonArray(Notes.Select(n => (JsonNode)n).ToArray()),
    };
}

/// <summary>A model that is loaded, warmed up and ready to answer.</summary>
public sealed class LoadedModel
{
    internal IModel Model { get; init; } = null!;
    public required ResolvedModel Choice { get; init; }
    public string Id => Choice.VariantId;
    public string Label => Choice.Title;
    public string Ep => Choice.EpLabel;
    public string Device => Choice.Device;
    private long? _context;
    /// <summary>The context window: what the catalog reported, else what the build's own config states once it is on this PC.</summary>
    public long? ContextTokens
    {
        get => _context is > 0 ? _context : Choice.ContextTokens;
        internal set => _context = value;
    }
    public bool WasCached { get; init; }
    public double LoadSeconds { get; internal set; }
    public double WarmSeconds { get; internal set; }

    public JsonObject Describe() => new()
    {
        ["id"] = Id, ["label"] = Label, ["class"] = Choice.Class, ["device"] = Device, ["ep"] = Ep, ["lane"] = Choice.Lane,
        ["sizeGb"] = Math.Round(Choice.FileGb, 2), ["contextTokens"] = ContextTokens, ["cachedBeforeStart"] = WasCached,
        ["loadSeconds"] = Math.Round(LoadSeconds, 2), ["warmSeconds"] = Math.Round(WarmSeconds, 2),
    };
}

/// <summary>An execution provider the runtime offers on this PC.</summary>
public sealed record EpState(string Token, string Name, bool Registered, bool Usable, bool Cached);

public sealed record GenOptions
{
    public int MaxTokens { get; init; } = 400;
    public float? Temperature { get; init; }
    public bool AllowEmpty { get; init; }
    /// <summary>Constrained decoding: "json_schema", "regex" or "lark_grammar". Set it together with <see cref="GuidanceData"/>.
    /// The runtime then only lets the model emit text that matches, so a small model cannot return broken JSON.</summary>
    public string? GuidanceType { get; init; }
    public string? GuidanceData { get; init; }
}

/// <summary>Everything measured about one generation. Tokens come from the runtime's usage report when it gives one;
/// otherwise the streamed chunk count stands in and <see cref="TokensMeasured"/> is false.</summary>
public sealed record GenerationResult(
    string Text, double FirstTokenSeconds, double TotalSeconds, int OutputTokens, bool TokensMeasured, bool Cancelled,
    string ModelLabel, string Ep, string Device, double DecodeSeconds)
{
    /// <summary>Decode rate: tokens after the first one, over the time after the first token.</summary>
    public double TokensPerSecond => OutputTokens > 1 && DecodeSeconds > 0.05 ? (OutputTokens - 1) / DecodeSeconds : 0;

    public string Summary =>
        $"{ModelLabel} on the {Device.ToUpperInvariant()} ({Ep}) · first words in {EngineFormat.Seconds(FirstTokenSeconds)} · {(TokensMeasured ? "" : "≈")}{OutputTokens} tokens · {TokensPerSecond:0.0} tok/s";

    public JsonObject Describe() => new()
    {
        ["model"] = ModelLabel, ["device"] = Device, ["ep"] = Ep, ["firstTokenSeconds"] = Math.Round(FirstTokenSeconds, 2),
        ["totalSeconds"] = Math.Round(TotalSeconds, 2), ["tokens"] = OutputTokens, ["tokensMeasured"] = TokensMeasured,
        ["tokensPerSecond"] = Math.Round(TokensPerSecond, 1), ["cancelled"] = Cancelled, ["chars"] = Text.Length,
    };
}

public sealed class ModelOutputException(string message) : Exception(message);

/// <summary>The model is not on this PC and downloads are off.</summary>
public sealed class DownloadRequiredException(ResolvedModel model)
    : Exception($"{model.Title} is not on this PC yet ({EngineFormat.Gb(model.FileGb)}). Allow downloads to fetch it.")
{
    public ResolvedModel Model { get; } = model;
}

/// <summary>Every option for the workload was tried or ruled out, and none started.</summary>
public sealed class ModelLoadException(string message, IReadOnlyList<string> problems) : Exception(message)
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

internal static class EngineFormat
{
    internal static string Gb(double gb) => gb >= 10 ? $"{gb:0} GB" : $"{gb:0.0} GB";

    internal static string Seconds(double s) => s < 1 ? $"{s * 1000:0} ms" : s < 100 ? $"{s:0.0} s" : $"{s:0} s";

    internal static string FirstLine(string? text)
    {
        string line = (text ?? "").Split('\n', '\r')[0].Trim();
        return line.Length > 220 ? line[..220] + "…" : line;
    }

    /// <summary>"CUDAExecutionProvider" and "cuda" both become "cuda".</summary>
    internal static string NormEp(string? ep) =>
        (ep ?? "").ToLowerInvariant().Replace("executionprovider", "").Replace("-", "").Replace("_", "").Trim();

    internal static string EpLabel(string? ep) => NormEp(ep) switch
    {
        "cuda" => "CUDA",
        "nvtensorrtrtx" => "TensorRT for RTX",
        "qnn" => "QNN",
        "openvino" => "OpenVINO",
        "vitisai" => "Vitis AI",
        "webgpu" => "WebGPU",
        "dml" => "DirectML",
        "cpu" => "CPU",
        "" => "unknown",
        var other => other,
    };
}
