using System.Diagnostics;
using System.Text.RegularExpressions;
using Forge.Core.Harness;
using Forge.Core.Spec;
using Forge.Engine;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurfaceDeviceKit.FoundryLocal;

namespace OfflinePptxForge;

// Making a deck: load the model, turn typed words into a brief when asked, then write, repair, grade and save, with progress and a Stop button.
internal sealed partial class MainWindow
{
    private static readonly Regex TrailingPercent = new(@"(\d{1,3})\s*%\s*$", RegexOptions.Compiled);
    private const double DraftShare = 0.85;

    private bool _running;
    private bool _runNoticeShown;
    private CancellationTokenSource? _runCts;
    private DispatcherQueueTimer? _clock;
    private readonly Stopwatch _runWatch = new();
    private int _lastLoggedBucket = -1;
    private (string Phase, int Step) _lastLoggedStep = ("", -1);
    private string _runPhase = "";

    private void SetBusy(bool busy)
    {
        _running = busy;
        _choices.IsEnabled = !busy;
        _simulateBox.IsEnabled = !busy;
        _stopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _stopButton.IsEnabled = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = busy;
        _progress.Value = 0;
        _buildButton.Content = busy ? "Making the deck..." : "Make the deck";
        if (busy) StartClock();
        else StopClock();
        UpdateBuildEnabled();
    }

    private void StartClock()
    {
        _runWatch.Restart();
        _elapsed.Text = "";
        if (_clock is null)
        {
            _clock = DispatcherQueue.CreateTimer();
            _clock.Interval = TimeSpan.FromSeconds(1);
            _clock.Tick += (_, _) => _elapsed.Text = _running ? Provenance.Short(_runWatch.Elapsed.TotalSeconds) + " elapsed" : "";
        }
        _clock.Start();
    }

    private void StopClock()
    {
        _clock?.Stop();
        _elapsed.Text = "";
    }

    private void StopRun()
    {
        if (_runCts is not { IsCancellationRequested: false } cts) return;
        _stopButton.IsEnabled = false;
        SetStatus("Stopping...");
        cts.Cancel();
    }

    /// <summary>Runs one job with the page locked, one Stop button and one place where every failure becomes a readable message.</summary>
    private async Task RunWorkAsync(Func<CancellationToken, Task> work)
    {
        if (_running || _closed) return;
        if (_runNoticeShown)
        {
            ClearNotice();
            _runNoticeShown = false;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _runCts = cts;
        _runPhase = "";
        _lastLoggedStep = ("", -1);
        SetBusy(true);
        try { await work(cts.Token); }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("Stopped. No deck was saved.");
                AddLog("Stopped.");
            }
        }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            if (!_closed)
            {
                if (exception is ModelLoadException load)
                    foreach (string problem in load.Problems) AddLog(problem);
                SetStatus("The deck could not be made. " + exception.Message);
                AddLog("Failed: " + exception.Message);
                Notify("The deck could not be made", exception.Message, InfoBarSeverity.Error);
                _runNoticeShown = true;
            }
        }
        finally
        {
            _runCts = null;
            if (!_closed) SetBusy(false);
        }
    }

    // ---- The model ----

    /// <summary>The model for this run: the one already loaded, or the selected one after freeing the last. Null when a needed download was declined.</summary>
    private async Task<PreparedModel?> EnsureModelAsync(ResolvedModel option, CancellationToken ct)
    {
        if (_prepared is { } current && current.Variant == option.VariantId) return current;
        if (_prepared is { } old)
        {
            _prepared = null;
            SetStatus($"Freeing {old.Facts.Title}...");
            AddLog($"Unloading {old.Facts.Title}.");
            try { await Task.Run(() => _session.UnloadAsync(old, CancellationToken.None)); }
            catch (Exception exception) { AddLog("The last model did not unload cleanly: " + exception.Message); }
        }

        bool allowDownload = _options.AllowDownload;
        while (true)
        {
            _lastLoggedBucket = -1;
            SetStatus($"Loading {option.Title} on the {option.LaneLabel}...");
            AddLog($"Loading {option.Title} on the {option.LaneLabel} ({option.EpLabel}).");
            try
            {
                var prepared = await Task.Run(() => _session.PrepareAsync(option, new PrepareOptions(allowDownload), line => Post(() => OnEngineProgress(line)), ct), ct);
                _prepared = prepared;
                var loaded = prepared.Model;
                AddLog($"{prepared.Facts.Title} is ready on the {loaded.Device} ({loaded.Ep}): load {loaded.LoadSeconds:0.0} s, warm-up {loaded.WarmSeconds:0.0} s.");
                if (prepared.FellBack)
                    foreach (string problem in prepared.Problems) AddLog("Fell back: " + problem);
                if (prepared.Calibration is { } calibration) AddLog(calibration.Summary);
                if (!option.Cached) await RefreshPlanAsync(option.VariantId, ct);
                else OnModelChanged();
                return prepared;
            }
            catch (DownloadRequiredException needed) when (!allowDownload)
            {
                if (!await ConfirmDownloadAsync(needed.Model)) return null;
                allowDownload = true;
            }
        }
    }

    /// <summary>A model that was just downloaded is cached now, so the list should stop saying it needs a download.</summary>
    private async Task RefreshPlanAsync(string select, CancellationToken ct)
    {
        try
        {
            _plan = await Task.Run(() => _session.PlanAsync(ct), ct);
            RefreshModels(select);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { AddLog("The model list could not be refreshed: " + exception.Message); }
    }

    private async Task<bool> ConfirmDownloadAsync(ResolvedModel model)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot,
            RequestedTheme = ElementTheme.Light,
            Title = $"Download {model.Title}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Text = $"{model.Title} is not on this PC yet. It is {ForgeText.Gb(model.FileGb)} and needs the internet once. After that the Forge works offline.",
            },
            PrimaryButtonText = "Download",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Loading and downloading report as text; a trailing percentage becomes a real progress bar, anything else a moving one.</summary>
    private void OnEngineProgress(string line)
    {
        if (!_running || string.IsNullOrWhiteSpace(line)) return;
        SetStatus(line);
        if (TrailingPercent.Match(line) is { Success: true } match && int.TryParse(match.Groups[1].Value, out int percent))
        {
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Clamp(percent / 100d, 0, 1);
            int bucket = percent / 10;
            if (bucket == _lastLoggedBucket) return;
            _lastLoggedBucket = bucket;
        }
        else
        {
            _progress.IsIndeterminate = true;
            _lastLoggedBucket = -1;
        }
        AddLog(line);
    }

    // ---- The brief ----

    private async Task<Brief> DraftAsync(PreparedModel model, CancellationToken ct)
    {
        string prompt = _requestBox.Text.Trim();
        string key = DraftKey();
        var options = DraftOptionsNow();
        SetStatus("Turning your words into a brief...");
        _progress.IsIndeterminate = true;
        var result = await Task.Run(() => _session.DraftBriefAsync(model, prompt, options, ct), ct);
        var brief = result.Brief;
        _drafted = brief;
        _draftedFrom = key;
        RefreshBriefSummary();
        AddLog($"Drafted a {PitchLabel(brief)} brief for {brief.Client}: {brief.Facts.Count} fact{(brief.Facts.Count == 1 ? "" : "s")} kept, {brief.Dropped.Count} left out because your words did not contain them.");
        return brief;
    }

    private async Task<Brief> BriefForRunAsync(PreparedModel model, CancellationToken ct)
    {
        if (!_writing) return PickedBrief() ?? throw new InvalidOperationException("Pick a brief first.");
        if (_drafted is not null && DraftKey() == _draftedFrom) return _drafted;
        return await DraftAsync(model, ct);
    }

    private async Task DraftOnlyAsync()
    {
        if (_running || _closed || !_writing || !PromptReady || _plan is null || SelectedModel() is not { } option) return;
        await RunWorkAsync(async ct =>
        {
            var model = await EnsureModelAsync(option, ct);
            if (model is null)
            {
                SetStatus("The model was not downloaded, so no brief was drafted.");
                return;
            }
            var brief = await DraftAsync(model, ct);
            SetStatus($"The brief for {brief.Client} is ready. Read it above, then make the deck.");
            _briefSummary.StartBringIntoView();
        });
    }

    // ---- The deck ----

    private async Task BuildAsync()
    {
        if (_running || _closed || _plan is null || SelectedModel() is not { } option || !BriefReady()) return;
        var request = SelectedRequest();
        string template = _template;
        bool previews = _previewsCheck.IsChecked == true;
        await RunWorkAsync(async ct =>
        {
            var model = await EnsureModelAsync(option, ct);
            if (model is null)
            {
                SetStatus("The model was not downloaded, so no deck was made.");
                return;
            }
            var brief = await BriefForRunAsync(model, ct);
            AddLog($"Making a deck for {brief.Client} with {model.Facts.Title} on the {model.Model.Device}.");
            SetStatus("Writing the deck...");
            _progress.IsIndeterminate = true;

            var progress = new Progress<HarnessProgress>(OnHarnessProgress);
            var run = await Task.Run(() => _session.RunAsync(model, brief, new ForgeRunOptions(request, template, previews), progress, ct), ct);

            _progress.IsIndeterminate = false;
            _progress.Value = 1;
            AddLog($"How it was written: {ForgeText.Settings(run.Settings)}.");
            foreach (string why in run.Settings.Why) AddLog(why);
            foreach (string warning in run.Result.Warnings) AddLog("Note: " + warning);
            await ShowResultAsync(run);
            _resultCard.StartBringIntoView();
            string grade = run.Result.Grade is { } graded ? $", {graded.AutoGrade} on the Forge's own checks" : "";
            SetStatus($"Done: {run.Deck.Slides.Count} slides{grade}, made in {Provenance.Duration(_runWatch.Elapsed.TotalSeconds)}. Saved to {run.Files.Pptx}");
            AddLog($"Saved {run.Files.Pptx}");
        });
    }

    private void OnHarnessProgress(HarnessProgress p)
    {
        if (!_running || p.Phase.Length == 0) return;
        _runPhase = p.Phase;
        SetStatus(p.Tokens > 0 ? $"{p.Message} · {Provenance.Count(p.Tokens)} tokens written" : p.Message);

        // Draft steps fill most of the bar and repair passes the rest; a one-reply draft has no steps to count, so it just moves.
        bool counted = p.Total > 1 || p.Phase == "repair";
        _progress.IsIndeterminate = !counted;
        if (counted)
        {
            double done = Math.Clamp((p.Step - 1d) / Math.Max(1, p.Total), 0, 1);
            double fraction = p.Phase == "repair" ? DraftShare + (1 - DraftShare) * done : DraftShare * done;
            _progress.Value = Math.Max(_progress.Value, fraction);
        }
        if ((p.Phase, p.Step) == _lastLoggedStep) return;
        _lastLoggedStep = (p.Phase, p.Step);
        AddLog($"{p.Phase} {p.Step}/{p.Total}: {p.Message}");
    }
}
