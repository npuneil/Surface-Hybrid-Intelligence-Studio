using System.Diagnostics;
using Forge.Core.Harness;
using SurfaceDeviceKit;
using SurfaceDeviceKit.FoundryLocal;

namespace Forge.Engine;

/// <param name="Alias">The model to test, by catalog alias; null tests the plan's first choice for this PC.</param>
/// <param name="Lane">The lane (npu, gpu or cpu) to run <paramref name="Alias"/> on; null uses this PC's first usable lane.</param>
/// <param name="OutputRoot">Where the test deck goes; null uses a temporary folder that is removed afterwards.</param>
public sealed record LiveTestOptions(string BriefId = "zava_technical", string? Alias = null, string? Lane = null, bool AllowDownload = false, bool Cancel = true, RunRequest? Request = null, string? OutputRoot = null);

/// <summary>The end-to-end check on a real PC: Foundry Local starts, a model loads, one slide measures it, a whole deck is written and saved
/// as a well-formed .pptx, the speed log learns from it, and a run that is cancelled mid-deck stops promptly and leaves the model usable.
/// What it proves is about this PC and this model only; nothing in it is a claim about another device.</summary>
public static class ForgeLiveTest
{
    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public static async Task<List<SelfTestResult>> RunAsync(ForgeSession session, LiveTestOptions? options, Action<string> say, CancellationToken ct = default)
    {
        options ??= new LiveTestOptions();
        var results = new List<SelfTestResult>();
        bool Add(string name, bool ok, string detail)
        {
            results.Add(new SelfTestResult(name, ok, detail));
            say($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? " - " + detail : "")}");
            return ok;
        }

        var brief = BundledBriefs.Find(options.BriefId);
        if (brief is null)
        {
            Add("the brief for the test deck exists", false, $"no bundled brief '{options.BriefId}'");
            return results;
        }

        var clock = Stopwatch.StartNew();
        try
        {
            await session.StartAsync(options.AllowDownload, say, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Add("Foundry Local starts on this PC", false, exception.Message.Split('\n')[0].Trim());
            return results;
        }
        Add("Foundry Local starts on this PC", true, $"{session.Engine.Status} ({clock.Elapsed.TotalSeconds:0.0} s)");

        ResolvedModel? choice;
        if (options.Alias is not null)
        {
            string lane = options.Lane ?? session.Hardware.UsableLanes.FirstOrDefault()?.Id ?? "cpu";
            string why;
            try { (choice, why) = await session.Engine.ResolveModelAsync(options.Alias, lane, ct).ConfigureAwait(false); }
            catch (KeyNotFoundException exception) { (choice, why) = (null, exception.Message); }
            if (!Add($"{options.Alias} can run on the {lane.ToUpperInvariant()} of this PC", choice is not null, choice?.Summary ?? why)) return results;
        }
        else
        {
            var plan = await session.PlanAsync(ct).ConfigureAwait(false);
            choice = plan.Default;
            if (!Add("the live catalog has a model for the Forge on this PC", choice is not null,
                    choice is null ? string.Join(" ", plan.Models.Notes) : $"{plan.Models.Options.Count} option(s); testing {choice.Summary}"))
                return results;
        }

        PreparedModel model;
        clock.Restart();
        try
        {
            model = await session.PrepareAsync(choice!, new PrepareOptions(options.AllowDownload, Calibrate: true), say, ct).ConfigureAwait(false);
        }
        catch (DownloadRequiredException exception)
        {
            Add($"{choice!.Title} is on this PC", false, exception.Message.Split('\n')[0].Trim() + " Allow downloads to fetch it.");
            return results;
        }
        catch (Exception exception) when (exception is ModelLoadException or InvalidOperationException)
        {
            Add($"{choice!.Title} loads", false, exception.Message.Split('\n')[0].Trim());
            return results;
        }
        Add($"{model.Facts.Title} loads", true, $"{model.Model.Device} ({model.Model.Ep}): load {model.Model.LoadSeconds:0.0} s, warm-up {model.Model.WarmSeconds:0.0} s, window {(model.Model.ContextTokens is > 0 ? $"{model.Model.ContextTokens:N0} tokens" : "unknown")}");
        var calibration = model.Calibration;
        Add("one real slide measures the model", calibration is { Completed: true }, calibration?.Summary ?? "no calibration ran");

        string root = options.OutputRoot ?? Path.Combine(Path.GetTempPath(), "forge-livetest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            ForgeRun? run = null;
            clock.Restart();
            try
            {
                var last = ("", -1);
                var progress = new Inline<HarnessProgress>(p =>
                {
                    if (p.Phase.Length == 0 || (p.Phase, p.Step) == last) return;
                    last = (p.Phase, p.Step);
                    say($"  {p.Phase} {p.Step}/{p.Total}: {p.Message}");
                });
                run = await session.RunAsync(model, brief, new ForgeRunOptions(options.Request, null, true, root), progress, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HarnessException or ModelOutputException or PromptTooLongException)
            {
                Add("a whole deck is written", false, exception.Message.Split('\n')[0].Trim());
            }

            if (run is not null)
            {
                int slides = run.Deck.Slides.Count;
                var result = run.Result;
                Add("a whole deck is written", slides >= brief.SlideMin,
                    $"{slides} slides in {clock.Elapsed.TotalSeconds:0.0} s ({result.Mode}, {(result.GuidedDecoding ? "constrained decoding" : "plain prompts")}, {result.Stats.LlmCalls} model calls, {result.Repairs.Count} repair pass(es))");
                var package = ForgeSelfTest.CheckPackage(File.ReadAllBytes(run.Files.Pptx), slides);
                Add("the deck is a well-formed .pptx", package.Ok, package.Detail);
                Add("every slide has a preview", run.Files.Previews.Count == slides && run.Files.Previews.All(File.Exists), $"{run.Files.Previews.Count} of {slides}");
                Add("the deck's own numbers are saved with it", run.Files.Metrics is not null && File.Exists(run.Files.MetricsJson),
                    run.Files.Metrics is null ? "no metrics" : $"{Path.GetFileName(run.Files.MetricsJson)}; device \"{run.Files.Metrics["device"]}\"");
                int lint = result.LintRemaining.Count;
                say($"  quality: grade {result.Grade?.AutoGrade ?? "?"} ({result.Grade?.AutoScore:0.0}), {lint} lint issue(s) left, {result.DraftIssues.Count} before repair; slow-down warnings: {result.Warnings.Count}");
                foreach (string warning in result.Warnings) say($"  warning: {warning}");
                var energy = result.Energy;
                say(energy.Measured ? $"  energy: {energy.Wh:0.00} Wh ({energy.Basis})" : $"  energy: not measured ({energy.Basis})");

                var entry = session.Speed.Entry(model.Variant);
                var eta = session.Speed.Estimate(model.Variant, slides);
                Add("the run teaches the speed log", entry is { DecodeTokensPerSecond: > 0 } && eta is not null,
                    entry is null ? "no entry" : $"{entry.DecodeTokensPerSecond:0.0} tok/s decode, constrained decoding {entry.Guidance}; next deck of {slides} slides: about {eta?.Seconds:0} s ({eta?.Basis})");
            }

            if (options.Cancel && run is not null)
            {
                using var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var cancelledAt = new Stopwatch();
                var progress = new Inline<HarnessProgress>(p =>
                {
                    if (p.Step >= 2 && !cancelledAt.IsRunning)
                    {
                        cancelledAt.Start();
                        source.Cancel();
                    }
                });
                bool stopped = false;
                try
                {
                    await session.RunAsync(model, brief, new ForgeRunOptions(options.Request, null, false, root), progress, source.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { stopped = true; }
                Add("cancelling a deck stops it promptly", stopped && cancelledAt.Elapsed.TotalSeconds < 30,
                    stopped ? $"stopped {cancelledAt.Elapsed.TotalSeconds:0.0} s after the cancel" : "the run finished instead of stopping");

                string answer = "";
                try
                {
                    var reply = await model.Llm.ChatAsync(new LlmRequest("Answer with one word.", "Say OK.") { MaxTokens = 16, Temperature = 0 }, null, ct).ConfigureAwait(false);
                    answer = reply.Text.Trim();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    answer = "(failed: " + exception.Message.Split('\n')[0].Trim() + ")";
                }
                Add("the model still answers after a cancel", answer.Length > 0 && !answer.StartsWith("(failed", StringComparison.Ordinal), answer.Length == 0 ? "no reply" : answer);
            }
        }
        finally
        {
            if (options.OutputRoot is null)
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
                catch (IOException) { /* the test decks are in the temp folder */ }
            }
            try { await session.UnloadAsync(model, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { say($"  could not unload the model: {exception.Message.Split('\n')[0].Trim()}"); }
        }
        return results;
    }
}
