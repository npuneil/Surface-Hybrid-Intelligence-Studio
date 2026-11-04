using Forge.Core.Harness;

namespace Forge.Engine;

/// <summary>What one real slide, written the way the harness writes them, said about a model on this PC.</summary>
public sealed record CalibrationResult(
    bool Completed, bool SlideParsed, GuidanceState Guidance,
    double DecodeTokensPerSecond, double PrefillTokensPerSecond, double FirstTokenSeconds, int PromptTokens, int SlideTokens, double Seconds,
    string Summary)
{
    public static CalibrationResult Failed(string why) => new(false, false, GuidanceState.Unknown, 0, 0, 0, 0, 0, 0, why);

    public SpeedEntry ToEntry(ModelFacts model, string variant) => new()
    {
        Variant = variant,
        Model = model.Title,
        Lane = model.Lane,
        Ep = model.EpLabel,
        DecodeTokensPerSecond = DecodeTokensPerSecond,
        PrefillTokensPerSecond = PrefillTokensPerSecond,
        FirstTokenSeconds = FirstTokenSeconds,
        SlideTokens = SlideTokens,
        Guidance = Guidance,
        MeasuredAt = DateTimeOffset.UtcNow,
        Samples = 1,
    };
}

/// <summary>Writes one slide of a bundled brief with the real model, exactly as a deck run would: same prompt, same schema, same compaction
/// level for the model's window, same parser. It costs a few seconds and gives the app what it needs to plan the rest: the model's real
/// speed on this PC (for the ETA and the repair budget) and whether constrained decoding works on this model and lane.</summary>
public static class Calibrator
{
    private const double ContextShare = 0.92;
    private static readonly string[] TypicalLayouts = ["stack", "flow", "hub", "compare", "kpis"];

    public static async Task<CalibrationResult> RunAsync(ILlm llm, DeviceStory story, CancellationToken ct = default)
    {
        var source = BundledBriefs.Find("zava_technical") ?? BundledBriefs.All.FirstOrDefault();
        if (source is null) return CalibrationResult.Failed("no bundled brief to calibrate with");
        var brief = BriefAdapter.Adapt(source, story);
        var arc = Arcs.For(brief, Arcs.Reserved(brief));
        if (arc.Count == 0) return CalibrationResult.Failed("the bundled brief has no slides");

        int index = Enumerable.Range(1, Math.Max(0, arc.Count - 1)).FirstOrDefault(i => TypicalLayouts.Contains(arc[i].Layout));
        if (index == 0) index = Math.Min(1, arc.Count - 1);
        var slot = arc[index];
        var deckSoFar = new[] { arc[0].Slot.Replace('_', ' ') };

        int window = llm.Info.ContextTokens;
        bool guided = llm.Info.SupportsGuidance;
        int level = window == 0 ? 2 : 0;
        // Like the harness, a model that cannot do constrained decoding is never sent the schema.
        LlmRequest Build(int l)
        {
            var built = Prompts.Slot(new SlotPrompt(brief, story, arc, index, deckSoFar, [], l, guided));
            return guided ? built : built with { Schema = null };
        }

        LlmReply reply;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var request = Build(level);
            while (window > 0 && level < 3 && EstimateTokens(request) + request.MaxTokens > ContextShare * window) request = Build(++level);
            try
            {
                reply = await llm.ChatAsync(request, null, ct).ConfigureAwait(false);
                if (reply.Cancelled) ct.ThrowIfCancellationRequested();
                break;
            }
            catch (PromptTooLongException) when (level < 3) { level++; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return CalibrationResult.Failed($"the model could not write the calibration slide: {exception.Message.Split('\n')[0].Trim()}");
            }
        }
        double seconds = clock.Elapsed.TotalSeconds;

        var parsed = SpecJson.Parse(reply.Text);
        bool slideOk = parsed.Spec is not null && SlideTidy.TryBuild(parsed.Spec, slot, story, out _, out _, out _);

        var guidance = !guided ? GuidanceState.Unknown : !reply.Guided ? GuidanceState.Rejected : slideOk ? GuidanceState.Verified : GuidanceState.Unknown;

        // Speeds only count when the runtime reported its token counts; an estimate would put a made-up number in the ETA.
        double decode = 0, prefill = 0;
        int slideTokens = 0;
        if (reply.TokensMeasured && reply.CompletionTokens > 0)
        {
            slideTokens = reply.CompletionTokens;
            double decodeSeconds = reply.DecodeSeconds > 0 ? reply.DecodeSeconds : Math.Max(reply.TotalSeconds - reply.FirstTokenSeconds, 0);
            if (decodeSeconds > 0) decode = reply.CompletionTokens / decodeSeconds;
            if (reply.PromptTokens > 0 && reply.FirstTokenSeconds > 0) prefill = reply.PromptTokens / reply.FirstTokenSeconds;
        }

        string note = guidance switch
        {
            GuidanceState.Verified => "constrained decoding verified",
            GuidanceState.Rejected => "constrained decoding was refused, so slides are checked and repaired instead",
            _ when guided => "the slide did not parse, so constrained decoding is not confirmed yet",
            _ => "constrained decoding is off for this model",
        };
        string summary = decode > 0
            ? $"Measured one slide on {llm.Info.Model} ({llm.Info.Device}): {decode:0} tokens per second, first token in {reply.FirstTokenSeconds:0.0} s; {note}."
            : $"Wrote one slide on {llm.Info.Model} ({llm.Info.Device}) in {seconds:0.0} s; the runtime did not report token counts; {note}.";
        return new CalibrationResult(true, slideOk, guidance, decode, prefill, reply.FirstTokenSeconds, reply.PromptTokens, slideTokens, seconds, summary);
    }

    private static double EstimateTokens(LlmRequest request) => (request.System.Length + request.User.Length) / 3.5;
}
