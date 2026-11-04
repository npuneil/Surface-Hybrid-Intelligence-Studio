using System.Text.Json.Nodes;

namespace Forge.Core.Harness;

/// <summary>One request to the local language model. When <see cref="Schema"/> is set the runtime may constrain decoding to it,
/// so a small model cannot return broken JSON. An "x-guidance" block inside the schema tunes the decoder (compact separators).</summary>
public sealed record LlmRequest(string System, string User)
{
    public int MaxTokens { get; init; } = 1024;
    public float Temperature { get; init; } = 0.3f;
    public JsonObject? Schema { get; init; }
    /// <summary>A short label for logs and metrics, for example "slide 4 (compare)".</summary>
    public string Purpose { get; init; } = "";
}

/// <summary>What one model call returned and how long it took. Token counts are the runtime's own when <see cref="TokensMeasured"/>.</summary>
public sealed record LlmReply(
    string Text, double FirstTokenSeconds, double TotalSeconds, int PromptTokens, int CompletionTokens, double DecodeSeconds,
    bool Guided, bool Cancelled = false, bool TokensMeasured = true)
{
    public double DecodeTokensPerSecond => CompletionTokens > 1 && DecodeSeconds > 0.05 ? (CompletionTokens - 1) / DecodeSeconds : 0;
}

/// <summary>The model the harness is talking to. <see cref="Lane"/> is "npu", "gpu" or "cpu"; <see cref="ContextTokens"/> is 0 when unknown.</summary>
public sealed record LlmInfo(
    string Model, string Lane, string Device, string Runtime, int ContextTokens, string ModelClass, double ParamsB, bool SupportsGuidance)
{
    public static LlmInfo Unknown { get; } = new("model", "cpu", "CPU", "local runtime", 0, "M", 7, true);
}

/// <summary>The prompt plus the reply would not fit the model's context window, by <see cref="DeficitTokens"/> tokens.</summary>
public sealed class PromptTooLongException(long promptTokens, long contextTokens, long deficitTokens)
    : Exception($"The prompt takes {promptTokens} tokens and the model's window is {contextTokens}; it is {deficitTokens} tokens too long.")
{
    public long PromptTokens { get; } = promptTokens;
    public long ContextTokens { get; } = contextTokens;
    public long DeficitTokens { get; } = deficitTokens;
}

/// <summary>The harness talks to the model only through this. <c>Forge.Engine</c> implements it over Foundry Local and the device kit;
/// tests implement it with a script, so every harness path runs headless.</summary>
public interface ILlm
{
    LlmInfo Info { get; }

    /// <summary>Runs one chat turn. <paramref name="onText"/> receives pieces as they stream. Throws <see cref="PromptTooLongException"/>
    /// when the prompt cannot fit, and <see cref="OperationCanceledException"/> when cancelled.</summary>
    Task<LlmReply> ChatAsync(LlmRequest request, Action<string>? onText = null, CancellationToken ct = default);
}
