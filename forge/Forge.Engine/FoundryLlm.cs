using Forge.Core.Harness;
using SurfaceDeviceKit.FoundryLocal;

namespace Forge.Engine;

/// <summary>What <see cref="FoundryLlm"/> needs from a loaded model. <see cref="FoundryChatBackend"/> is the real one; tests use a fake.</summary>
public interface IChatBackend
{
    /// <summary>The runtime's own token count for a prompt and whether it fits the window; null when the runtime could not say.</summary>
    Task<PromptFit?> PreflightAsync(string system, string prompt, int maxTokens, CancellationToken ct);

    Task<GenerationResult> GenerateAsync(string system, string prompt, GenOptions options, Action<string>? onText, CancellationToken ct);
}

/// <summary>A model that Foundry Local has loaded, as the chat backend.</summary>
public sealed class FoundryChatBackend(FoundryEngine engine, LoadedModel model) : IChatBackend
{
    public Task<PromptFit?> PreflightAsync(string system, string prompt, int maxTokens, CancellationToken ct) =>
        engine.PreflightAsync(model, system, prompt, maxTokens, ct);

    public Task<GenerationResult> GenerateAsync(string system, string prompt, GenOptions options, Action<string>? onText, CancellationToken ct) =>
        engine.GenerateAsync(model, system, prompt, options, onText, ct);
}

/// <summary>The harness's language model over Foundry Local. It adds what the harness relies on and the runtime does not give directly:
/// the runtime's own prompt token count and a "does not fit" signal before anything is generated, constrained decoding to the slide's
/// schema, and a fallback to plain prompts when a runtime or model rejects the constraint (remembered, so it is only discovered once).</summary>
public sealed class FoundryLlm : ILlm
{
    private readonly IChatBackend _backend;
    private readonly LlmInfo _info;
    private readonly Action<string>? _log;
    private volatile bool _guidanceOff;
    private int _guidedReplies;

    public FoundryLlm(LlmInfo info, IChatBackend backend, Action<string>? log = null, bool guidanceKnownRejected = false)
    {
        _info = info;
        _backend = backend;
        _log = log;
        _guidanceOff = guidanceKnownRejected;
    }

    /// <summary>The model the harness is told about. <see cref="LlmInfo.SupportsGuidance"/> turns false once the runtime has refused constrained decoding.</summary>
    public LlmInfo Info => _guidanceOff && _info.SupportsGuidance ? _info with { SupportsGuidance = false } : _info;

    /// <summary>True once a request with a schema was refused and then answered without one.</summary>
    public bool GuidanceRejected => _guidanceOff;

    /// <summary>Replies that really were produced under the schema. Greater than zero means constrained decoding works on this model and lane.</summary>
    public int GuidedReplies => Volatile.Read(ref _guidedReplies);

    public static LlmInfo InfoFor(ResolvedModel model, long? window = null) => new(
        model.Title, model.Lane, model.Device, $"Foundry Local ({model.EpLabel})",
        (int)Math.Clamp(window is > 0 ? window.Value : model.ContextTokens ?? 0, 0, int.MaxValue), model.Class, model.ParamsB, SupportsGuidance: true);

    public static FoundryLlm For(FoundryEngine engine, LoadedModel model, Action<string>? log = null, bool guidanceKnownRejected = false) =>
        new(InfoFor(model.Choice, model.ContextTokens), new FoundryChatBackend(engine, model), log, guidanceKnownRejected);

    public async Task<LlmReply> ChatAsync(LlmRequest request, Action<string>? onText = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var fit = await _backend.PreflightAsync(request.System, request.User, request.MaxTokens, ct).ConfigureAwait(false);
        if (fit is { Fits: false, ContextLimitTokens: > 0 })
            throw new PromptTooLongException(fit.PromptTokens, fit.ContextLimitTokens, fit.DeficitTokens);

        bool promptMeasured = fit is { PromptTokens: > 0 };
        int promptTokens = promptMeasured ? (int)Math.Min(fit!.PromptTokens, int.MaxValue) : EstimateTokens(request);

        GenerationResult result;
        bool guided = request.Schema is not null && !_guidanceOff;
        if (!guided) result = await GenerateAsync(request, guided: false, onText, ct).ConfigureAwait(false);
        else
        {
            try { result = await GenerateAsync(request, guided: true, onText, ct).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Plain prompts may work where the constraint did not. Guidance is only blamed when the plain request succeeds,
                // so a model that is simply failing is not mistaken for one that cannot be constrained.
                _log?.Invoke($"Constrained decoding failed on {_info.Model} ({_info.Device}): {FirstLine(exception.Message)} Trying without it.");
                result = await GenerateAsync(request, guided: false, onText, ct).ConfigureAwait(false);
                _guidanceOff = true;
                guided = false;
            }
        }
        if (guided && !result.Cancelled) Interlocked.Increment(ref _guidedReplies);

        return new LlmReply(
            result.Text, result.FirstTokenSeconds, result.TotalSeconds, promptTokens, result.OutputTokens, result.DecodeSeconds,
            Guided: guided, Cancelled: result.Cancelled, TokensMeasured: result.TokensMeasured && promptMeasured);
    }

    private Task<GenerationResult> GenerateAsync(LlmRequest request, bool guided, Action<string>? onText, CancellationToken ct)
    {
        var options = new GenOptions
        {
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
            GuidanceType = guided ? "json_schema" : null,
            GuidanceData = guided ? request.Schema!.ToJsonString() : null,
        };
        return _backend.GenerateAsync(request.System, request.User, options, onText, ct);
    }

    /// <summary>Only used when the runtime could not count the prompt: about 3.5 characters per token, the same figure the harness starts from.</summary>
    private static int EstimateTokens(LlmRequest request) => (int)Math.Ceiling((request.System.Length + request.User.Length) / 3.5);

    private static string FirstLine(string text)
    {
        string line = text.Split('\n', '\r')[0].Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
