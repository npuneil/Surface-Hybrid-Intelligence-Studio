using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Ir;
using Forge.Core.Layouts;
using Forge.Core.Spec;

namespace Forge.Core.Harness;

/// <summary>Writes a deck with a local model and checks it. The harness is the same on every device; what changes is the model, the
/// lane it runs on and how much of the prompt fits its window. It never talks to a runtime directly, only to an <see cref="ILlm"/>.</summary>
public sealed partial class DeckHarness
{
    private const double ContextShare = 0.92;

    private readonly ILlm _llm;
    private readonly DeviceStory _story;
    private readonly HarnessOptions _options;
    private readonly IEnergyMeter? _meter;
    private readonly IProgress<HarnessProgress>? _progress;
    private readonly CancellationToken _ct;
    private readonly HarnessStats _stats = new();
    private readonly List<string> _warnings = [];
    private readonly List<RepairRecord> _repairs = [];

    private Theme _theme = Theme.Surface;
    private Brief _brief = new();
    private IReadOnlyList<ArcSlot> _arc = [];
    private int _reserved;
    private int _level = -1;
    private bool _guidanceRejected;
    private bool _anyGuided;
    private double _charsPerToken = 3.5;
    private string _phase = "plan";
    private string _message = "";
    private int _step, _total, _pieces;

    private DeckHarness(ILlm llm, DeviceStory story, HarnessOptions options, IEnergyMeter? meter, IProgress<HarnessProgress>? progress, CancellationToken ct)
    {
        _llm = llm;
        _story = story;
        _options = options;
        _meter = meter;
        _progress = progress;
        _ct = ct;
    }

    /// <summary>Drafts, repairs and grades one deck. Throws <see cref="HarnessException"/> when the model cannot produce one and
    /// <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static Task<HarnessResult> RunAsync(ILlm llm, DeviceStory story, Brief brief, HarnessOptions? options = null, IEnergyMeter? meter = null,
        IProgress<HarnessProgress>? progress = null, CancellationToken ct = default) =>
        new DeckHarness(llm, story, options ?? new HarnessOptions(), meter, progress, ct).ExecuteAsync(brief);

    private sealed record Draft(JsonObject Spec, string Raw, bool Strict, bool UsesArc, HarnessMode Mode, List<LintIssue> DraftIssues);

    private bool GuidedDecoding => _options.UseGuidance && _llm.Info.SupportsGuidance && !_guidanceRejected;

    private async Task<HarnessResult> ExecuteAsync(Brief source)
    {
        var clock = Stopwatch.StartNew();
        _theme = _options.Theme ?? Theme.All.FirstOrDefault(t => t.Key.Equals(source.TemplateHint, StringComparison.OrdinalIgnoreCase)) ?? Theme.Surface;
        _brief = BriefAdapter.Adapt(source, _story);
        _warnings.AddRange(BriefAdapter.Warnings(source, _story));
        _reserved = Arcs.Reserved(_brief);
        _arc = Arcs.For(_brief, _reserved);
        if (_arc.Count + _reserved < _brief.SlideMin)
            _warnings.Add($"The brief asks for at least {_brief.SlideMin} slides; the story for this pitch has {_arc.Count + _reserved}.");
        var mode = ChooseMode();

        bool metering = false;
        try
        {
            _meter?.Start();
            metering = _meter is not null;
        }
        catch (Exception exception) { _warnings.Add("The power meter could not start: " + exception.Message); }
        var energy = EnergyReading.NotMeasured("no power meter on this device");

        Draft draft;
        try
        {
            draft = mode switch
            {
                HarnessMode.Arc => await ArcAsync(oneShot: false).ConfigureAwait(false),
                HarnessMode.OneShot => await ArcAsync(oneShot: true).ConfigureAwait(false),
                _ => await GuidedAsync().ConfigureAwait(false),
            };
        }
        finally
        {
            if (metering)
            {
                try { energy = _meter!.Stop(); }
                catch (Exception exception) { energy = EnergyReading.NotMeasured("the power meter failed: " + exception.Message); }
            }
        }
        double draftSeconds = clock.Elapsed.TotalSeconds;

        int scrubbed = Script.Scrub(draft.Spec, Script.AllowedText(_brief));
        if (scrubbed > 0) _warnings.Add($"Removed non-English text the model left in {scrubbed} line(s) of the deck.");
        var (remaining, _) = Evaluate(draft.Spec, draft.UsesArc);
        var spec = draft.Spec;
        var graded = _brief;
        string? measured = null;
        if (_reserved > 0)
        {
            var slides = SpecJson.SlideObjects(spec);
            slides.Insert(Math.Max(1, slides.Count - 1), Provenance.Slide(_story, _stats, draftSeconds, energy));
            spec = SpecJson.WithSlides(spec, slides);
            measured = Provenance.MeasuredFact(_story, _stats, draftSeconds, energy);
            graded = Brief.FromJson(_brief.ToJson());
            graded.Facts.Add(measured);
        }
        var summary = DeckBuilder.Build(spec, _theme).Summary;
        var grade = Grade.Run(spec, summary, graded, draft.Strict);

        return new HarnessResult
        {
            Spec = spec,
            Raw = draft.Raw,
            StrictJson = draft.Strict,
            Brief = graded,
            Mode = draft.Mode,
            GuidedDecoding = _anyGuided,
            ContextLevel = Math.Max(0, _level),
            Stats = _stats,
            Repairs = _repairs,
            DraftIssues = draft.DraftIssues,
            LintRemaining = remaining,
            Grade = grade,
            RenderSummary = summary,
            Energy = energy,
            MeasuredFact = measured,
            DraftSeconds = draftSeconds,
            Model = _llm.Info,
            Warnings = _warnings,
        };
    }

    private HarnessMode ChooseMode()
    {
        var mode = _options.Mode;
        int window = _llm.Info.ContextTokens;
        int needed = mode switch { HarnessMode.Arc => 16000, HarnessMode.OneShot => 12000, _ => 0 };
        if (window > 0 && window < needed)
        {
            _warnings.Add($"The model's window is {window} tokens, too small to write the whole deck at once; writing it slide by slide instead.");
            return HarnessMode.Guided;
        }
        return mode;
    }

    // ---- Model calls ----

    private void SetStep(string phase, int step, int total, string message)
    {
        _phase = phase;
        _step = step;
        _total = total;
        _message = message;
        _pieces = 0;
        Report();
    }

    private void Report() => _progress?.Report(new HarnessProgress(_phase, _step, _total, _message, _stats.CompletionTokens + _pieces));

    private void OnText(string piece)
    {
        if (piece.Length == 0) return;
        if (++_pieces % 6 == 0) Report();
    }

    private int Estimate(LlmRequest request) => (int)Math.Ceiling((request.System.Length + request.User.Length) / _charsPerToken);

    /// <summary>Sends one request, first compacting the prompt (levels 0 to 3) until it and its reply fit the model's window. The level never
    /// goes down within a run, so later calls do not rediscover that the window is small.</summary>
    private async Task<LlmReply> ChatAsync(Func<int, LlmRequest> build)
    {
        int window = _llm.Info.ContextTokens;
        while (true)
        {
            _ct.ThrowIfCancellationRequested();
            int level = Math.Max(_level, window == 0 ? 2 : 0);
            var request = build(level);
            while (window > 0 && level < 3 && Estimate(request) + request.MaxTokens > ContextShare * window)
                request = build(++level);
            _level = level;
            if (!GuidedDecoding && request.Schema is not null) request = request with { Schema = null };
            try
            {
                var reply = await _llm.ChatAsync(request, OnText, _ct).ConfigureAwait(false);
                if (reply.Cancelled) _ct.ThrowIfCancellationRequested();
                Record(request, reply);
                return reply;
            }
            catch (PromptTooLongException tooLong)
            {
                if (level >= 3)
                    throw new HarnessException($"The model's window ({tooLong.ContextTokens} tokens) cannot hold even the smallest prompt for this brief.", tooLong);
                _level = level + 1;
            }
        }
    }

    private void Record(LlmRequest request, LlmReply reply)
    {
        _pieces = 0;
        _stats.LlmCalls++;
        _stats.PromptTokens += reply.PromptTokens;
        _stats.CompletionTokens += reply.CompletionTokens;
        _stats.GenerateSeconds += reply.TotalSeconds;
        _stats.FirstTokenSeconds += reply.FirstTokenSeconds;
        if (reply.CompletionTokens > 1 && reply.DecodeSeconds > 0.05)
        {
            _stats.DecodeTokens += reply.CompletionTokens - 1;
            _stats.DecodeSeconds += reply.DecodeSeconds;
        }
        if (!reply.TokensMeasured) _stats.TokensMeasured = false;
        if (reply.PromptTokens > 0)
        {
            double measured = (request.System.Length + request.User.Length) / (double)reply.PromptTokens;
            _charsPerToken = Math.Clamp(0.6 * _charsPerToken + 0.4 * measured, 2.0, 6.0);
        }
        if (reply.Guided) _anyGuided = true;
        else if (request.Schema is not null && !_guidanceRejected)
        {
            _guidanceRejected = true;
            _warnings.Add("The runtime did not accept constrained decoding for this model, so the rest of the deck used plain prompts.");
        }
        Report();
    }

    // ---- Lint ----

    private (List<LintIssue> Issues, IReadOnlyList<SlideSummary> Summary) Evaluate(JsonObject spec, bool useArc)
    {
        var summary = DeckBuilder.Build(spec, _theme).Summary;
        var arc = useArc ? _arc.Select(s => (s.Slot, s.Layout)).ToList() : null;
        return (Lint.Run(spec, _brief, summary, arc, _reserved, _options.WordLimit), summary);
    }

    private static List<string> Codes(IEnumerable<LintIssue> issues) =>
        [.. issues.Select(i => i.Code).Distinct().Order(StringComparer.Ordinal)];

    private static readonly Regex Word = new("[a-z]{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "that", "this", "into", "are", "not", "its", "use", "one", "each", "your", "per", "any", "all", "can",
    };

    private static HashSet<string> Tokens(string text) =>
        [.. Word.Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => !Filler.Contains(w))];

    private static JsonObject ReplaceSlide(JsonObject spec, int index, JsonObject slide)
    {
        var slides = SpecJson.SlideObjects(spec);
        slides[index] = slide;
        return SpecJson.WithSlides(spec, slides);
    }

    /// <summary>The on-canvas words of one slide, lower case: what the lint's topic check reads.</summary>
    private static string SlideText(JsonNode? slide) =>
        string.Join(" ", SpecText.SlideStrings(slide).Select(t => t.Text)).ToLowerInvariant();

    private static bool Mentions(string text, IEnumerable<string> keywords) =>
        keywords.Any(k => k.Length > 0 && text.Contains(k.ToLowerInvariant(), StringComparison.Ordinal));
}
