using System.Diagnostics;
using System.Text;
using Microsoft.AI.Foundry.Local;

namespace SurfaceDeviceKit.FoundryLocal;

/// <summary>Whether a prompt fits the model's context window, measured by the runtime's own tokenizer.</summary>
public sealed record PromptFit(bool Fits, long PromptTokens, long RequiredTokens, long ContextLimitTokens, long DeficitTokens);

public sealed partial class FoundryEngine
{
    // One request at a time on the accelerator; a second one would only queue inside the runtime and skew the timings.
    private readonly SemaphoreSlim _generationGate = new(1, 1);

    /// <summary>Streams a reply from a loaded model and measures it. <paramref name="onText"/> receives each visible piece as it arrives;
    /// reasoning output is not shown. Cancelling returns what has arrived so far with <see cref="GenerationResult.Cancelled"/> set.</summary>
    public async Task<GenerationResult> GenerateAsync(
        LoadedModel model, string system, string prompt, GenOptions? options = null, Action<string>? onText = null, CancellationToken ct = default)
    {
        if (!model.Choice.IsChat) throw new InvalidOperationException($"{model.Label} is a {model.Choice.Task} model, so it does not take chat prompts.");
        await _generationGate.WaitAsync(ct);
        try { return await GenerateCoreAsync(model, system, prompt, options ?? new GenOptions(), onText, ct); }
        finally { _generationGate.Release(); }
    }

    /// <summary>Asks the runtime how many tokens the prompt takes and whether it, plus the reply, fits the context window.</summary>
    public async Task<PromptFit?> PreflightAsync(LoadedModel model, string system, string prompt, int maxTokens, CancellationToken ct = default)
    {
        try
        {
            using var session = new ChatSession(model.Model);
            using var request = new Request();
            if (!string.IsNullOrWhiteSpace(system)) request.AddItem(MessageItem.System(system));
            request.AddItem(MessageItem.User(prompt));
            session.SetOptions(new RequestOptions { Search = new SearchOptions { MaxOutputTokens = maxTokens } });
            var result = await session.PreflightRequestAsync(request, ct);
            return new PromptFit(result.Fits, result.PromptTokens, result.RequiredTokens, result.ContextLimitTokens, result.DeficitTokens);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Log($"Preflight on {model.Id} failed: {exception.Message}");
            return null;
        }
    }

    private static async Task<GenerationResult> GenerateCoreAsync(
        LoadedModel model, string system, string prompt, GenOptions options, Action<string>? onText, CancellationToken ct)
    {
        using var session = new ChatSession(model.Model);
        using var request = new Request();
        if (!string.IsNullOrWhiteSpace(system)) request.AddItem(MessageItem.System(system));
        request.AddItem(MessageItem.User(prompt));
        session.SetStreaming(true);
        var search = new SearchOptions { MaxOutputTokens = options.MaxTokens, Temperature = options.Temperature ?? 0.2f, TopP = 1.0f };
        // AdditionalOptions must not be assigned at all (not even null) when there is no guidance: the runtime rejects a null dictionary.
        session.SetOptions(options.GuidanceType is { Length: > 0 } guidanceType && options.GuidanceData is { Length: > 0 } guidanceData
            ? new RequestOptions { Search = search, AdditionalOptions = new Dictionary<string, string> { ["guidance_type"] = guidanceType, ["guidance_data"] = guidanceData } }
            : new RequestOptions { Search = search });

        var clock = Stopwatch.StartNew();
        double firstAny = -1;
        int chunks = 0;
        var text = new StringBuilder();
        bool cancelled = false;
        TokenUsage? usage = null;
        try
        {
            var stream = session.ProcessStreamingRequestAsync(request, ct);
            await foreach (var item in stream.WithCancellation(ct))
            {
                string piece = item switch
                {
                    TextItem t when t.Type == TextItemType.Reasoning => "",
                    TextItem t => t.Text ?? "",
                    MessageItem m when m.IsSimpleText() => m.GetSimpleText(),
                    _ => "",
                };
                if (piece.Length == 0) continue;
                if (firstAny < 0) firstAny = clock.Elapsed.TotalSeconds;
                chunks++;
                text.Append(piece);
                onText?.Invoke(piece);
            }
            try
            {
                using var final = await stream.FinalResponse;
                usage = final.GetUsage();
            }
            catch (Exception) when (ct.IsCancellationRequested) { cancelled = true; }
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (FoundryLocalException) when (ct.IsCancellationRequested) { cancelled = true; }

        double total = clock.Elapsed.TotalSeconds;
        bool measured = usage is { CompletionTokens: > 0 };
        int tokens = measured ? usage!.CompletionTokens : chunks;
        string answer = text.ToString().Trim();
        if (!cancelled && answer.Length == 0 && !options.AllowEmpty)
            throw new ModelOutputException($"{model.Label} returned no answer. Run it again.");
        return new GenerationResult(answer, firstAny < 0 ? total : firstAny, total, tokens, measured, cancelled,
            model.Label, model.Ep, model.Device, firstAny >= 0 ? total - firstAny : 0);
    }
}
