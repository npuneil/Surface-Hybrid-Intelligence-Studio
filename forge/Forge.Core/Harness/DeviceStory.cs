namespace Forge.Core.Harness;

/// <summary>What the deck may say about the PC it was made on. The Engine builds this from the device kit's report and the model that is
/// loaded, so every statement traces to something that was detected, planned or measured. Nothing here is a hard-coded device name.</summary>
public sealed record DeviceStory
{
    /// <summary>The product as a person says it, for example "Surface Laptop 7".</summary>
    public string Name { get; init; } = "this PC";
    /// <summary>The processor family, for example "Snapdragon X Plus" or "Intel Core Ultra 7".</summary>
    public string Silicon { get; init; } = "";
    /// <summary>A short label for the engine that runs the model, used in diagrams: "Snapdragon NPU", "RTX GPU", "CPU".</summary>
    public string Engine { get; init; } = "local engine";
    /// <summary>"npu", "gpu" or "cpu": where the model runs.</summary>
    public string Lane { get; init; } = "cpu";
    /// <summary>For example "Foundry Local with the CUDA execution provider".</summary>
    public string Runtime { get; init; } = "Foundry Local";
    /// <summary>The model that wrote the deck, for example "Qwen2.5 7B".</summary>
    public string Model { get; init; } = "a local model";
    /// <summary>True when the processor shares one memory pool with the GPU (so memory views are views of one pool).</summary>
    public bool UnifiedMemory { get; init; }
    /// <summary>Facts about this PC for every deck. Each is a plain sentence the model may rely on.</summary>
    public IReadOnlyList<string> Facts { get; init; } = [];
    /// <summary>Extra facts that only a technical audience wants (memory views, runtime, measured speed).</summary>
    public IReadOnlyList<string> TechnicalFacts { get; init; } = [];
    /// <summary>Words that show the device is covered on a slide (added to the brief's "device" topics).</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];
    /// <summary>Decode speed measured on this PC for this model and lane, in tokens per second. Zero when never measured.</summary>
    public double MeasuredTokensPerSecond { get; init; }
    public string Noun { get; init; } = "PC";

    public string NameWithSilicon => Silicon.Length > 0 ? $"{Name} with {Silicon}" : Name;

    public static DeviceStory Generic { get; } = new();
}
