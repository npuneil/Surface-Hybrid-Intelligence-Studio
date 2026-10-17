using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

/// <summary>Live demos: a launch gallery, a pre-flight runner and a per-demo view streaming /api/demo/run.</summary>
internal sealed partial class MainWindow
{
    private DemoCatalog _demoCatalog = DemoCatalog.Empty;
    private string? _demoCatalogError;
    private bool _demoCatalogLoading;
    private readonly Dictionary<string, DemoRunRecord> _demoResults = [];
    private readonly Dictionary<string, DemoCardView> _demoCards = [];
    private readonly Dictionary<string, byte[]> _demoMediaCache = [];
    private readonly StackPanel _demoGalleryHost = Id(new StackPanel(), "DemoGallery");
    private readonly PillView _demoPillChat = new("DemoReadyChat");
    private readonly PillView _demoPillModels = new("DemoReadyModels");
    private readonly PillView _demoPillRuntime = new("DemoReadyRuntime");
    private readonly PillView _demoPillMedia = new("DemoReadyMedia");
    private readonly TextBlock _demoReadinessDetail = Id(Small(""), "DemoReadinessDetail");
    private readonly Button _demoRunAll = Id(Primary("Pre-flight: run every demo live"), "DemoRunAll");
    private readonly Button _demoRunAllStop = Id(Secondary("Stop pre-flight"), "DemoRunAllStop");
    private readonly TextBlock _demoPreflightSummary = Id(Text("", 14, Ink, FontWeights.SemiBold), "DemoPreflightSummary");
    private readonly StackPanel _demoPreflightList = Id(new StackPanel { Spacing = 6 }, "DemoPreflightList");
    private StackPanel _demoGalleryView = new();
    private StackPanel _demoRunnerView = new();
    private ScrollViewer? _demoScroll;

    private readonly HyperlinkButton _demoBack = Id(new HyperlinkButton { Content = "\u2190 All demos", Padding = new Thickness(0), Foreground = Accent }, "DemoBack");
    private readonly FontIcon _demoGlyph = Glyph("\uE768", 22, Accent);
    private readonly TextBlock _demoCategory = Eyebrow("");
    private readonly TextBlock _demoTitle = Id(H1(""), "DemoRunnerTitle");
    private readonly TextBlock _demoPitch = Id(Body(""), "DemoRunnerPitch");
    private readonly TextBlock _demoProof = Id(Small("", Ink), "DemoRunnerProof");
    private readonly TextBlock _demoInputLabel = Id(Small(""), "DemoInputLabel");
    private readonly StackPanel _demoInputHost = Id(new StackPanel { Spacing = 10 }, "DemoInputHost");
    private readonly ComboBox _demoOption = Id(new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }, "DemoOption");
    private readonly TextBox _demoPrompt = Id(new TextBox
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150, HorizontalAlignment = HorizontalAlignment.Stretch
    }, "DemoPrompt");
    private readonly HyperlinkButton _demoRestore = Id(new HyperlinkButton { Content = "Restore authored instruction", Padding = new Thickness(0), Foreground = Accent }, "DemoRestorePrompt");
    private readonly TextBlock _demoFixedNote = Id(Small(""), "DemoFixedNote");
    private readonly Button _demoRunLive = Id(Primary("Run live on GPU"), "DemoRunLive");
    private readonly Button _demoRunRehearsal = Id(Secondary("Play rehearsal"), "DemoRunRehearsal");
    private readonly Button _demoStop = Id(Secondary("Stop"), "DemoStop");
    private readonly TextBlock _demoBlocker = Id(Small("", AmberInk), "DemoBlocker");
    private readonly TextBlock _demoRehearsalNote = Id(Small(""), "DemoRehearsalNote");
    private readonly Border _demoBanner = Id(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Background = Sand }, "DemoModeBanner");
    private readonly TextBlock _demoBannerText = Id(Text("", 13, Ink, FontWeights.SemiBold), "DemoModeText");
    private readonly InfoBar _demoError = Id(new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, Title = "The demo did not complete" }, "DemoErrorInfoBar");
    private readonly TextBlock _demoClock = Id(Small(""), "DemoClock");
    private readonly StackPanel _demoStepsHost = Id(new StackPanel { Spacing = 12 }, "DemoSteps");
    private readonly StackPanel _demoChecksHost = Id(new StackPanel { Spacing = 8 }, "DemoChecks");
    private readonly StackPanel _demoChecksSection = new() { Spacing = 8 };
    private readonly TextBlock _demoSummary = Id(Text("", 14, Ink, FontWeights.SemiBold), "DemoSummary");
    private readonly TextBlock _demoNote = Id(Small(""), "DemoNote");
    private readonly Button _demoCopy = Id(Secondary("Copy output"), "DemoCopy");
    private readonly Button _demoExport = Id(Secondary("Export evidence (JSON)"), "DemoExport");
    private readonly InfoBar _demoExportInfo = Id(new InfoBar { IsClosable = true }, "DemoExportInfo");
    private readonly DispatcherTimer _demoClockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<DemoStepView> _demoStepViews = [];
    private IReadOnlyList<DemoStepRecord>? _demoStepsSource;
    private IReadOnlyList<DemoCheck>? _demoChecksSource;
    private int _demoChecksShown;
    private DemoInfo? _openDemo;
    private DemoRunRecord? _shownDemoRun;
    private ActiveDemo? _activeDemo;
    private CancellationTokenSource? _demoCts;
    private Stopwatch? _demoStopwatch;
    private bool _demoRunning;
    private bool _preflightRunning;
    private bool _preflightStop;
    private bool _demoOptionSync;
    private bool _demoPreviewLoading;
    private string? _demoPreviewKey;
    private int _demoPreviewVersion;

    private const int MaxDemoPrompt = 4000;

    private FrameworkElement BuildDemos()
    {
        _demoRunAll.Click += async (_, _) => await RunAllDemosAsync();
        _demoRunAllStop.Click += (_, _) =>
        {
            _preflightStop = true;
            _demoPreflightSummary.Text = "Stopping after the current step...";
            try { _demoCts?.Cancel(); } catch (ObjectDisposedException) { }
        };
        _demoRunAll.IsEnabled = _demoRunAllStop.IsEnabled = false;
        _demoPreflightSummary.Visibility = _demoPreflightList.Visibility = Visibility.Collapsed;
        _demoGalleryHost.Children.Add(Body("Loading the demo list from the local GPU service..."));

        var readiness = Id(CardBorder(Stack(12,
            Text("READY TO PRESENT?", 11, Muted, FontWeights.SemiBold),
            new AdaptiveGrid(230, 4, 8).With(_demoPillChat.Root, _demoPillModels.Root, _demoPillRuntime.Root, _demoPillMedia.Root),
            _demoReadinessDetail,
            Set(Row(10, _demoRunAll, _demoRunAllStop), r => r.Margin = new Thickness(0, 4, 0, 0)),
            Small("Pre-flight runs every demo live once with its default input, so you know what works before the customer arrives. " +
                  "The first pre-flight loads every model and takes a few minutes. The face-off grades each model on its own, so one wrong answer there is a real result."),
            _demoPreflightSummary,
            _demoPreflightList), 20), "DemoReadinessCard");

        _demoGalleryView = Stack(22,
            Stack(10, Eyebrow("Live demos"), H1("Show it working, on this laptop."),
                Body("Every demo runs real models on the RTX GPU: speech recognition, vision, document search, chat and reasoning. " +
                     "Inputs are synthetic and fictional, and live output is checked against ground truth written into them. The service listens only on this laptop.")),
            readiness,
            _demoGalleryHost);
        _demoRunnerView = BuildDemoRunner();
        _demoRunnerView.Visibility = Visibility.Collapsed;
        _demoScroll = Page(Stack(0, _demoGalleryView, _demoRunnerView));
        return _demoScroll;
    }

    private StackPanel BuildDemoRunner()
    {
        _demoBanner.Child = _demoBannerText;
        _demoStop.IsEnabled = _demoCopy.IsEnabled = _demoExport.IsEnabled = false;
        _demoRunLive.IsEnabled = _demoRunRehearsal.IsEnabled = false;
        _demoBack.Click += (_, _) => CloseDemo();
        _demoOption.SelectionChanged += (_, _) =>
        {
            if (_demoOptionSync || _openDemo is not { } demo) return;
            _demoPrompt.Text = demo.PromptFor(SelectedDemoOption);
            LoadDemoPreview(demo, SelectedDemoOption);
            UpdateDemoRunnerNotes();
            UpdateDemoButtons();
        };
        _demoRestore.Click += (_, _) => { if (_openDemo is { } d) _demoPrompt.Text = d.PromptFor(SelectedDemoOption); };
        _demoRunLive.Click += async (_, _) => await RunOpenDemoAsync("live");
        _demoRunRehearsal.Click += async (_, _) => await RunOpenDemoAsync("rehearsal");
        _demoStop.Click += (_, _) =>
        {
            _demoClock.Text = "Stopping...";
            try { _demoCts?.Cancel(); } catch (ObjectDisposedException) { }
        };
        _demoCopy.Click += (_, _) =>
        {
            if (_shownDemoRun is not { } run) return;
            var package = new DataPackage();
            package.SetText(run.AllText());
            Clipboard.SetContent(package);
            ShowDemoExportInfo("Copied", "Step output copied to the clipboard.", InfoBarSeverity.Informational);
        };
        _demoExport.Click += (_, _) => ExportDemoRun();
        _demoClockTimer.Tick += (_, _) =>
        {
            if (_activeDemo is { } active && _demoStopwatch is { } clock && _openDemo?.Id == active.Demo.Id)
                _demoClock.Text = $"{(active.Mode == "live" ? "Live run" : "Rehearsal")}: {clock.Elapsed.TotalSeconds:F0} s elapsed. {active.Progress}";
        };

        _demoChecksSection.Children.Add(H3("Checks on this run"));
        _demoChecksSection.Children.Add(_demoChecksHost);
        _demoChecksSection.Children.Add(Small(DemoEvidence.CheckNote));
        _demoChecksSection.Visibility = Visibility.Collapsed;

        var input = CardBorder(Stack(14,
            Text("INPUT", 11, Muted, FontWeights.SemiBold),
            _demoInputLabel,
            _demoInputHost,
            new Border { Height = 1, Background = Line },
            _demoOption,
            _demoPrompt,
            _demoRestore,
            _demoFixedNote,
            Set(Row(10, _demoRunLive, _demoRunRehearsal, _demoStop), r => r.Margin = new Thickness(0, 4, 0, 0)),
            _demoBlocker,
            _demoRehearsalNote), 22);

        var output = CardBorder(Stack(14,
            _demoBanner, _demoError, _demoClock,
            H3("Steps"), _demoStepsHost,
            _demoChecksSection,
            _demoSummary, _demoNote,
            Row(10, _demoCopy, _demoExport), _demoExportInfo,
            Small(Evidence.MetricCaveat + " " + Evidence.PrivacyNote)), 22);

        var header = Stack(8, _demoBack, Row(10, _demoGlyph, _demoCategory), _demoTitle, _demoPitch, _demoProof);
        return Stack(22, header, TwoPane(input, output, 440, 1000));
    }

    private string? SelectedDemoOption => (_demoOption.SelectedItem as ComboBoxItem)?.Tag as string ?? _openDemo?.DefaultOption;

    private bool DemoRunnerVisible => _currentSection == "demos" && _demoRunnerView.Visibility == Visibility.Visible;

    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private string RoleLabel(string role)
    {
        if (_demoCatalog.MediaModels.FirstOrDefault(m => m.Role == role) is { } media) return media.Label;
        if (role == "chat") return _status?.Engine.Model?.Name ?? "Selected chat model";
        if (role == "measure") return "Windows GPU counters";
        return _status?.Engine.Models.FirstOrDefault(m => m.Alias == role)?.Label ?? role;
    }

    private async Task RefreshDemoCatalogAsync()
    {
        if (_demoCatalogLoading || _closed) return;
        _demoCatalogLoading = true;
        try
        {
            var catalog = await _client.GetDemosAsync(_lifetime.Token);
            bool rebuild = !catalog.Demos.Select(d => d.Id).SequenceEqual(_demoCatalog.Demos.Select(d => d.Id));
            _demoCatalog = catalog;
            _demoCatalogError = null;
            if (rebuild) BuildDemoGallery();
            if (_openDemo is { } open && catalog.Find(open.Id) is { } fresh) _openDemo = fresh;
            if (_openDemo is { } demo && _demoPreviewKey is null && !_demoPreviewLoading && catalog.MediaState == "ready")
                LoadDemoPreview(demo, SelectedDemoOption);
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _demoCatalogError = exception.Message;
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing while the demo list was loading.
        }
        finally
        {
            _demoCatalogLoading = false;
        }
        if (_closed) return;
        UpdateDemoReadiness();
        UpdateDemoCards();
        UpdateDemoButtons();
    }

    private void ApplyDemosStatus(StatusSnapshot status)
    {
        UpdateDemoReadiness();
        UpdateDemoCards();
        bool stale = _demoCatalog.Demos.Count == 0 || _currentSection == "demos" || _demoCatalog.MediaState != "ready"
            || !_demoCatalog.RuntimeReady || _demoCatalog.MediaModels.Count == 0;
        if (stale && status.Engine.State != "disabled") _ = RefreshDemoCatalogAsync();
    }

    private void BuildDemoGallery()
    {
        _demoGalleryHost.Children.Clear();
        _demoCards.Clear();
        if (_demoCatalog.Demos.Count == 0)
        {
            _demoGalleryHost.Children.Add(Body("The local service returned no demos."));
            return;
        }
        var cards = new List<FrameworkElement>();
        foreach (var demo in _demoCatalog.Demos)
        {
            var status = Id(Small(""), "DemoCardStatus_" + demo.Id);
            var models = Id(Small("", Ink), "DemoCardModels_" + demo.Id);
            var launch = Id(Primary("Open demo"), "DemoLaunch_" + demo.Id);
            string id = demo.Id;
            launch.Click += (_, _) => OpenDemo(id);
            var top = Stack(8, Row(8, Glyph(demo.Glyph, 18, Accent), Eyebrow(demo.Category)), H3(demo.Title), Body(demo.Pitch), models);
            var bottom = Stack(10, status, launch);
            var layout = new Grid { RowSpacing = 12 };
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(top);
            Grid.SetRow(bottom, 1);
            layout.Children.Add(bottom);
            var card = Id(CardBorder(layout, 20), "DemoCard_" + demo.Id);
            _demoCards[demo.Id] = new DemoCardView(card, status, models, launch);
            cards.Add(card);
        }
        _demoGalleryHost.Children.Add(new AdaptiveGrid(300, 3, 16).With([.. cards]));
    }

    private void UpdateDemoReadiness()
    {
        var engine = _status?.Engine;
        if (engine is null) _demoPillChat.Set("Chat model: waiting for service", Sand, Muted);
        else if (engine.IsReady) _demoPillChat.Set($"Chat: {engine.Model!.Name} ready", GreenTint, GreenInk);
        else if (engine.State is "error" or "disabled") _demoPillChat.Set("Chat model unavailable", AccentTint, AccentDeep);
        else _demoPillChat.Set("Chat model loading", AmberTint, AmberInk);

        var media = _demoCatalog.MediaModels.Where(m => DemoCatalog.MediaRoles.Contains(m.Role)).ToList();
        int cached = media.Count(m => m.Cached), loaded = media.Count(m => m.Loaded);
        if (media.Count == 0) _demoPillModels.Set("Speech, vision, search: checking", Sand, Muted);
        else if (cached == media.Count) _demoPillModels.Set(loaded == 0 ? "Speech, vision, search: downloaded" : $"Speech, vision, search: {loaded} of {media.Count} loaded", GreenTint, GreenInk);
        else _demoPillModels.Set($"Speech, vision, search: {cached} of {media.Count} downloaded", AmberTint, AmberInk);

        if (_demoCatalog.RuntimeReady) _demoPillRuntime.Set("GPU media libraries: ready", GreenTint, GreenInk);
        else if (_demoCatalog.RuntimeState == "missing") _demoPillRuntime.Set("GPU media libraries: one-time prepare", AmberTint, AmberInk);
        else if (_demoCatalog.RuntimeState == "unknown") _demoPillRuntime.Set("GPU media libraries: checking", Sand, Muted);
        else _demoPillRuntime.Set($"GPU media libraries: {_demoCatalog.RuntimeState}", AccentTint, AccentDeep);

        if (_demoCatalog.MediaState == "ready") _demoPillMedia.Set("Synthetic media: ready", GreenTint, GreenInk);
        else if (_demoCatalog.MediaState == "error") _demoPillMedia.Set("Synthetic media: error", AccentTint, AccentDeep);
        else _demoPillMedia.Set("Synthetic media: rendering", AmberTint, AmberInk);

        if (_demoCatalogError is string error)
        {
            _demoReadinessDetail.Text = "Demo list unavailable: " + error;
            _demoReadinessDetail.Foreground = AccentDeep;
            return;
        }
        var blockers = _demoCatalog.Demos.Select(d => _demoCatalog.LiveBlocker(d, engine)).ToList();
        int ready = blockers.Count(b => b is null);
        var reasons = blockers.OfType<string>().Distinct().ToList();
        _demoReadinessDetail.Text = _demoCatalog.Demos.Count == 0 ? "Waiting for the demo list."
            : $"{ready} of {_demoCatalog.Demos.Count} demos can run live now." + (reasons.Count > 0 ? " " + string.Join(" ", reasons) : "");
        _demoReadinessDetail.Foreground = reasons.Count > 0 ? AmberInk : GreenInk;
    }

    private void UpdateDemoCards()
    {
        foreach (var demo in _demoCatalog.Demos)
        {
            if (!_demoCards.TryGetValue(demo.Id, out var card)) continue;
            card.Models.Text = "Runs: " + string.Join(" \u2192 ", demo.Steps.Select(s => RoleLabel(s.Role)).Distinct());
            string? blocker = _demoCatalog.LiveBlocker(demo, _status?.Engine);
            if (_activeDemo?.Demo.Id == demo.Id)
                (card.Status.Text, card.Status.Foreground) = ("Running now...", AmberInk);
            else if (_demoResults.TryGetValue(demo.Id, out var run))
                (card.Status.Text, card.Status.Foreground) = ($"Last run: {(run.Live ? "LIVE" : "REHEARSAL")} \u00B7 {run.Outcome}{Seconds(run)}",
                    !run.Live ? AmberInk : run.Passed ? GreenInk : run.RanEndToEnd ? AmberInk : AccentDeep);
            else if (blocker is not null)
                (card.Status.Text, card.Status.Foreground) = (blocker, AmberInk);
            else
                (card.Status.Text, card.Status.Foreground) = ("Ready to run live.", GreenInk);
        }
    }

    private static string Seconds(DemoRunRecord run) => run.Live && run.Summary?.TotalMs is double ms ? $" \u00B7 {ms / 1000:F1} s" : "";

    internal bool OpenDemo(string id)
    {
        Navigate("demos");
        if (_demoCatalog.Find(id) is not { } demo)
        {
            CloseDemo();
            return false;
        }
        if (_openDemo?.Id != demo.Id)
        {
            _openDemo = demo;
            _demoGlyph.Glyph = demo.Glyph;
            _demoCategory.Text = demo.Category.ToUpperInvariant();
            _demoTitle.Text = demo.Title;
            _demoPitch.Text = demo.Pitch;
            _demoProof.Text = "How it is proven: " + demo.Proof;
            _demoOptionSync = true;
            _demoOption.Items.Clear();
            foreach (var option in demo.Options)
                _demoOption.Items.Add(Id(new ComboBoxItem { Content = option.Label, Tag = option.Id }, "DemoOption_" + option.Id));
            _demoOption.Header = demo.OptionLabel ?? "Option";
            _demoOption.SelectedIndex = demo.Options.Count == 0 ? -1 : Math.Max(0, demo.Options.ToList().FindIndex(o => o.Id == demo.DefaultOption));
            _demoOptionSync = false;
            Show(_demoOption, demo.Options.Count > 0);
            _demoPrompt.Header = demo.PromptLabel + (demo.Editable ? " (editable for live runs)" : "");
            _demoPrompt.Text = demo.PromptFor(SelectedDemoOption);
            Show(_demoPrompt, demo.Editable || demo.Prompt.Length > 0);
            Show(_demoRestore, demo.Editable);
            _demoFixedNote.Text = demo.Editable ? "" : "Fixed measurement: nothing to type. It gives each model a small real task, then reads residency from Foundry Local and GPU memory from Windows.";
            Show(_demoFixedNote, !demo.Editable);
            _demoError.IsOpen = false;
            _demoExportInfo.IsOpen = false;
            UpdateDemoRunnerNotes();
            LoadDemoPreview(demo, SelectedDemoOption);
        }
        if (_activeDemo is { } active && active.Demo.Id == demo.Id) RenderDemo(active.Snapshot(), running: true);
        else RenderDemo(_demoResults.GetValueOrDefault(demo.Id), running: false);
        _demoGalleryView.Visibility = Visibility.Collapsed;
        _demoRunnerView.Visibility = Visibility.Visible;
        _demoScroll?.ChangeView(null, 0, null, true);
        UpdateDemoButtons();
        return true;
    }

    private void CloseDemo()
    {
        StopDemoAudio();
        _demoRunnerView.Visibility = Visibility.Collapsed;
        _demoGalleryView.Visibility = Visibility.Visible;
        _demoScroll?.ChangeView(null, 0, null, true);
        UpdateDemoCards();
    }

    private void UpdateDemoRunnerNotes()
    {
        if (_openDemo is not { } demo) return;
        string? option = SelectedDemoOption;
        _demoRehearsalNote.Text = demo.RehearsalFor(option)
            ? "Rehearsal plays an authored sample with the authored instruction. It is labelled, scores no checks and is never presented as model output."
            : demo.Rehearsal
                ? "No authored rehearsal for this option; it runs live only."
                : "Live only: this demo compares or measures real models, so there is no authored rehearsal.";
    }

    private void UpdateDemoButtons()
    {
        bool online = _status is not null && !_serviceInfo.IsOpen;
        bool otherBusy = _running || _switching;
        _demoRunAll.IsEnabled = online && !otherBusy && !_demoRunning && _status!.Engine.IsReady && _demoCatalog.Demos.Count > 0;
        _demoRunAllStop.IsEnabled = _preflightRunning && !_preflightStop;
        if (_openDemo is not { } demo) return;
        string? blocker = _demoCatalog.LiveBlocker(demo, _status?.Engine);
        bool ownRun = _activeDemo?.Demo.Id == demo.Id;
        _demoRunLive.IsEnabled = online && !otherBusy && !_demoRunning && blocker is null;
        _demoRunRehearsal.IsEnabled = online && !otherBusy && !_demoRunning && demo.RehearsalFor(SelectedDemoOption);
        _demoStop.IsEnabled = _demoRunning && ownRun && !_preflightRunning;
        _demoOption.IsEnabled = !(_demoRunning && ownRun);
        _demoPrompt.IsReadOnly = _demoRunning || !demo.Editable;
        _demoBlocker.Text = _preflightRunning ? "Pre-flight is running every demo. Wait for it, or stop it from the demo list."
            : _demoRunning ? ""
            : _running ? "The Live GPU workspace is running. Wait for it to finish."
            : _switching ? "A chat model switch is in progress."
            : blocker ?? "";
        Show(_demoBlocker, _demoBlocker.Text.Length > 0);
        bool showingFinished = _shownDemoRun is not null && !(ownRun && _demoRunning);
        _demoCopy.IsEnabled = showingFinished && _shownDemoRun!.AllText().Length > 0;
        _demoExport.IsEnabled = showingFinished;
    }

    private async Task RunOpenDemoAsync(string mode)
    {
        if (_openDemo is not { } demo || _demoRunning || _running || _switching) return;
        string? option = SelectedDemoOption;
        string authored = demo.PromptFor(option);
        bool live = mode == "live";
        if (!live || !demo.Editable) _demoPrompt.Text = authored;
        string prompt = live && demo.Editable ? _demoPrompt.Text.ReplaceLineEndings("\n") : authored;
        _demoError.IsOpen = false;
        _demoExportInfo.IsOpen = false;
        if (live && demo.Editable && (prompt.Trim().Length == 0 || prompt.Trim().Length > MaxDemoPrompt))
        {
            ShowDemoError("Instruction needs attention", $"The instruction must contain 1 to {MaxDemoPrompt:N0} characters.");
            return;
        }
        var record = await RunDemoCoreAsync(demo, mode, option, prompt, prompt.Trim() != authored.Trim(), null);
        if (record.Error is string error && !_closed && _openDemo?.Id == demo.Id) ShowDemoError("The demo did not complete", error);
    }

    /// <summary>Streams one demo run. Results are kept per demo so the gallery and runner can show them later.</summary>
    private async Task<DemoRunRecord> RunDemoCoreAsync(DemoInfo demo, string mode, string? option, string prompt, bool edited, Action<string>? progress)
    {
        var active = new ActiveDemo(demo, mode, option, prompt, edited, demo.Steps.Select(s => new DemoStepRecord(s.Title, RoleLabel(s.Role))));
        _activeDemo = active;
        _demoRunning = true;
        _demoCts?.Dispose();
        var cts = _demoCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _demoStopwatch = Stopwatch.StartNew();
        _demoClockTimer.Start();
        if (_openDemo?.Id == demo.Id) RenderDemo(active.Snapshot(), running: true);
        UpdateLiveButtons();
        UpdateDemoCards();
        _statusTimer.Interval = TimeSpan.FromSeconds(3);
        try
        {
            await foreach (var ev in _client.RunDemoAsync(demo.Id, mode, option, prompt, cts.Token))
            {
                active.Apply(ev);
                if (_closed) break;
                progress?.Invoke(active.Progress);
                if (_openDemo?.Id == demo.Id) RenderDemo(active.Snapshot(), running: true);
            }
            if (active.Error is null && active.Summary is null) active.Error = "The stream ended before the demo completed.";
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            active.Error = "Stopped by the presenter. No completed result is claimed.";
        }
        catch (Exception exception)
        {
            active.Error = BackendClient.Describe(exception);
        }
        finally
        {
            _demoRunning = false;
            _activeDemo = null;
            _demoClockTimer.Stop();
            _demoStopwatch?.Stop();
        }
        active.Finish();
        var record = active.Snapshot();
        if (_closed) return record;
        _demoResults[demo.Id] = record;
        if (_openDemo?.Id == demo.Id) RenderDemo(record, running: false);
        UpdateLiveButtons();
        UpdateDemoCards();
        await RefreshStatusAsync();
        await RefreshDemoCatalogAsync();
        return record;
    }

    private async Task RunAllDemosAsync()
    {
        if (_demoRunning || _running || _switching || _demoCatalog.Demos.Count == 0) return;
        _preflightRunning = true;
        _preflightStop = false;
        _demoPreflightList.Children.Clear();
        var demos = _demoCatalog.Demos.ToArray();
        var rows = new Dictionary<string, TextBlock>();
        foreach (var demo in demos)
        {
            var text = Id(Small("Waiting"), "DemoPreflight_" + demo.Id);
            rows[demo.Id] = text;
            var title = Set(Text(demo.Title, 13, Ink, FontWeights.SemiBold), t => t.Width = 260);
            _demoPreflightList.Children.Add(Lead(12, title, text));
        }
        var clock = Stopwatch.StartNew();
        int passed = 0, ran = 0, skipped = 0, endToEnd = 0;
        _demoPreflightSummary.Text = "Pre-flight running...";
        _demoPreflightSummary.Foreground = AmberInk;
        _demoPreflightSummary.Visibility = _demoPreflightList.Visibility = Visibility.Visible;
        UpdateLiveButtons();
        try
        {
            foreach (var demo in demos)
            {
                var row = rows[demo.Id];
                if (_preflightStop || _closed)
                {
                    (row.Text, row.Foreground) = ("Not run (pre-flight stopped).", Muted);
                    continue;
                }
                if (_demoCatalog.LiveBlocker(_demoCatalog.Find(demo.Id) ?? demo, _status?.Engine) is string blocker)
                {
                    (row.Text, row.Foreground) = ("Skipped: " + blocker, AmberInk);
                    skipped++;
                    continue;
                }
                (row.Text, row.Foreground) = ("Running live...", AmberInk);
                string? option = demo.DefaultOption;
                var record = await RunDemoCoreAsync(demo, "live", option, demo.PromptFor(option), false, p => row.Text = p);
                if (_closed) return;
                ran++;
                if (record.Passed) passed++;
                // A graded wrong answer (the face-off) still ran end to end; amber, not red, keeps that distinction visible.
                if (record.RanEndToEnd) endToEnd++;
                row.Text = record.Outcome + Seconds(record) + (record.Error is string e ? ": " + e : "");
                row.Foreground = record.Passed ? GreenInk : record.RanEndToEnd ? AmberInk : AccentDeep;
            }
        }
        finally
        {
            _preflightRunning = false;
        }
        if (_closed) return;
        _demoPreflightSummary.Text = $"{endToEnd} of {ran} live demos ran end to end on the GPU; {passed} passed every check"
            + (skipped > 0 ? $"; {skipped} skipped" : "")
            + (_preflightStop ? "; stopped early" : "") + $". Pre-flight took {clock.Elapsed.TotalMinutes:F1} min.";
        _demoPreflightSummary.Foreground = passed == ran && skipped == 0 && !_preflightStop ? GreenInk : AmberInk;
        UpdateLiveButtons();
        UpdateDemoCards();
    }

    private void RenderDemo(DemoRunRecord? run, bool running)
    {
        _shownDemoRun = running ? null : run;
        IReadOnlyList<DemoStepRecord> steps = run?.Steps
            ?? (_openDemo?.Steps.Select(s => new DemoStepRecord(s.Title, RoleLabel(s.Role))).ToList() ?? []);
        if (!ReferenceEquals(_demoStepsSource, steps) || _demoStepViews.Count != steps.Count)
        {
            _demoStepsSource = steps;
            _demoStepViews.Clear();
            _demoStepsHost.Children.Clear();
            for (int i = 0; i < steps.Count; i++)
            {
                var view = new DemoStepView(i, steps[i].Title);
                _demoStepViews.Add(view);
                _demoStepsHost.Children.Add(view.Root);
            }
        }
        for (int i = 0; i < steps.Count; i++) _demoStepViews[i].Update(steps[i]);

        IReadOnlyList<DemoCheck> checks = run?.Checks ?? [];
        if (!ReferenceEquals(_demoChecksSource, checks))
        {
            _demoChecksSource = checks;
            _demoChecksHost.Children.Clear();
            _demoChecksShown = 0;
        }
        for (int i = _demoChecksShown; i < checks.Count; i++) _demoChecksHost.Children.Add(DemoCheckRow(i, checks[i]));
        _demoChecksShown = checks.Count;
        Show(_demoChecksSection, checks.Count > 0);

        SetDemoBanner(run, running);
        if (!running) _demoClock.Text = "";
        Show(_demoClock, running);
        if (run is null || running)
        {
            _demoSummary.Text = "";
            _demoNote.Text = "";
        }
        else if (run.Error is not null)
        {
            _demoSummary.Text = "Did not complete. Partial output, if any, is not a finished result.";
            _demoNote.Text = "";
        }
        else if (!run.Live)
        {
            _demoSummary.Text = "Authored rehearsal shown. Not model output; no checks or metrics.";
            _demoNote.Text = run.Summary?.Note ?? "";
        }
        else
        {
            _demoSummary.Text = run.Outcome + (run.Summary?.TotalMs is double ms ? $" \u00B7 {ms / 1000:F1} s in total" : "") + (run.Edited ? " \u00B7 edited instruction" : "");
            _demoNote.Text = run.Summary?.Note ?? "";
        }
        _demoSummary.Foreground = run is null || running ? Ink : run.Error is not null ? AccentDeep : !run.Live ? AmberInk
            : run.Passed ? GreenInk : run.RanEndToEnd ? AmberInk : AccentDeep;
        Show(_demoSummary, _demoSummary.Text.Length > 0);
        Show(_demoNote, _demoNote.Text.Length > 0);
        UpdateDemoButtons();
    }

    private void SetDemoBanner(DemoRunRecord? run, bool running)
    {
        (string text, Brush background, Brush foreground) = run switch
        {
            null => (_openDemo?.Rehearsal == true ? "Ready. Run it live on the GPU, or play the labelled rehearsal." : "Ready. This demo runs live on the GPU.", Sand, Ink),
            { Error: not null } r => (r.Live ? "LIVE RUN INCOMPLETE - no result is claimed" : "REHEARSAL INCOMPLETE", AccentTint, AccentDeep),
            { Live: true } => (running ? "LIVE - running now on this laptop's RTX GPU" : "LIVE - generated on this laptop's RTX GPU", GreenTint, GreenInk),
            _ => ("REHEARSAL - authored sample, not model output. No checks, no metrics.", AmberTint, AmberInk),
        };
        _demoBannerText.Text = text;
        _demoBannerText.Foreground = foreground;
        _demoBanner.Background = background;
    }

    private static FrameworkElement DemoCheckRow(int index, DemoCheck check)
    {
        var detail = Small(check.Detail);
        Show(detail, check.Detail.Length > 0);
        return Id(Lead(10, Glyph(check.Pass ? "\uE73E" : "\uE711", 16, check.Pass ? GreenInk : AccentDeep),
            Stack(2, Text($"{check.Label}: {check.Value}", 13, Ink, FontWeights.SemiBold), detail)), $"DemoCheck_{index}");
    }

    private void ShowDemoError(string title, string message)
    {
        _demoError.Title = title;
        _demoError.Message = message;
        _demoError.IsOpen = true;
    }

    private void ShowDemoExportInfo(string title, string message, InfoBarSeverity severity)
    {
        _demoExportInfo.Title = title;
        _demoExportInfo.Message = message;
        _demoExportInfo.Severity = severity;
        _demoExportInfo.IsOpen = true;
    }

    private string? ExportDemoRun()
    {
        if (_shownDemoRun is not { } run) return null;
        try
        {
            string path = Evidence.Save(DemoEvidence.Run(run, _status?.Device, _host.Lifecycle), ExportDirectory, $"demo-{run.Demo.Id}-{run.Mode}");
            ShowDemoExportInfo("Evidence exported", path, InfoBarSeverity.Success);
            return path;
        }
        catch (Exception exception)
        {
            ShowDemoExportInfo("Export failed", exception.Message, InfoBarSeverity.Error);
            return null;
        }
    }

    // ---------------------------------------------------------------- input preview

    private async void LoadDemoPreview(DemoInfo demo, string? option)
    {
        int version = ++_demoPreviewVersion;
        StopDemoAudio();
        _demoPreviewKey = null;
        _demoPreviewLoading = true;
        _demoInputHost.Children.Clear();
        _demoInputLabel.Text = demo.InputLabel;
        string? media = demo.MediaFor(option);
        try
        {
            if (demo.InputKind == "image" && media is not null)
            {
                var image = Id(new Image { Stretch = Stretch.Uniform, MaxHeight = 560, HorizontalAlignment = HorizontalAlignment.Left }, "DemoInputImage");
                var status = Id(Small("Loading the synthetic document..."), "DemoInputStatus");
                _demoInputHost.Children.Add(new Border
                {
                    Background = Paper, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8), Child = image,
                });
                _demoInputHost.Children.Add(status);
                if (await FetchDemoMediaAsync(media, status) is not { } bytes || version != _demoPreviewVersion) return;
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                if (version != _demoPreviewVersion) return;
                image.Source = bitmap;
                status.Text = $"Rendered on this laptop ({bitmap.PixelWidth} x {bitmap.PixelHeight} pixels). Every name and number is fictional.";
                _demoPreviewKey = media;
            }
            else if (demo.InputKind == "audio" && media is not null)
            {
                var play = Id(Secondary("\u25B6  Play recording"), "DemoPlayAudio");
                var stop = Id(Secondary("Stop audio"), "DemoStopAudio");
                var info = Id(Small("Loading the synthetic recording..."), "DemoAudioInfo");
                play.IsEnabled = false;
                stop.Click += (_, _) => StopDemoAudio();
                _demoInputHost.Children.Add(Row(8, play, stop));
                _demoInputHost.Children.Add(info);
                if (!string.IsNullOrWhiteSpace(demo.Source))
                    _demoInputHost.Children.Add(Id(new Expander
                    {
                        Header = Text("Script (ground truth for the accuracy check)", 13, Ink, FontWeights.SemiBold),
                        Content = SourceBox(demo.Source, "DemoScriptText"),
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    }, "DemoScript"));
                if (await FetchDemoMediaAsync(media, info) is not { } bytes || version != _demoPreviewVersion) return;
                string path = Path.Combine(_options.RuntimeDirectory, "demo-media", Path.GetFileName(media));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) await File.WriteAllBytesAsync(path, bytes);
                if (version != _demoPreviewVersion) return;
                double? seconds = DemoParser.WavSeconds(bytes);
                info.Text = (seconds is double s ? $"{s:F0}-second recording" : "Recording")
                    + " made with Windows text-to-speech voices on this laptop. It plays through the speakers; nothing is recorded from the microphone.";
                play.IsEnabled = true;
                play.Click += (_, _) =>
                {
                    if (!PlaySound(path, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault))
                        info.Text = "Audio playback is unavailable on this device. The demo still runs on the recording.";
                };
                _demoPreviewKey = media;
            }
            else if (!string.IsNullOrWhiteSpace(demo.Source))
            {
                _demoInputHost.Children.Add(SourceBox(demo.Source, "DemoSourceText"));
                _demoPreviewKey = "text";
            }
            else
            {
                if (demo.Editable) _demoInputHost.Children.Add(Small("The instruction below is the complete input."));
                _demoPreviewKey = "none";
            }
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            Program.LogError(_options, exception);
            if (version == _demoPreviewVersion) _demoInputHost.Children.Add(Small("Preview unavailable: " + exception.Message, AccentDeep));
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing while a preview was loading.
        }
        finally
        {
            if (version == _demoPreviewVersion) _demoPreviewLoading = false;
        }
    }

    private static TextBox SourceBox(string text, string id) => Id(new TextBox
    {
        Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 12,
        MaxHeight = 280, HorizontalAlignment = HorizontalAlignment.Stretch,
    }, id);

    private async Task<byte[]?> FetchDemoMediaAsync(string name, TextBlock status)
    {
        if (_demoMediaCache.TryGetValue(name, out var cached)) return cached;
        if (_demoCatalog.MediaState != "ready")
        {
            status.Text = _demoCatalog.MediaState == "error"
                ? "Synthetic media could not be rendered: " + _demoCatalog.MediaDetail
                : "Synthetic media is still rendering on this laptop; the preview appears when it is ready.";
            return null;
        }
        var bytes = await _client.GetDemoMediaAsync(name, _lifetime.Token);
        _demoMediaCache[name] = bytes;
        return bytes;
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndFilename = 0x00020000;

    private static void StopDemoAudio()
    {
        try { PlaySound(null, IntPtr.Zero, 0); } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
    }

    // ---------------------------------------------------------------- view helpers

    private sealed record DemoCardView(Border Root, TextBlock Status, TextBlock Models, Button Launch);

    /// <summary>A pill whose label and colours change with state.</summary>
    private sealed class PillView
    {
        internal readonly TextBlock Label = Text("Checking", 12, Muted, FontWeights.SemiBold);
        internal readonly Border Root;

        internal PillView(string id)
        {
            Root = Id(new Border
            {
                Background = Sand, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 4), Child = Label,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            }, id);
            Id(Label, id + "Text");
        }

        internal void Set(string text, Brush background, Brush foreground)
        {
            Label.Text = text;
            Label.Foreground = foreground;
            Root.Background = background;
        }
    }

    /// <summary>One step card. Updates are incremental so streaming text does not rebuild the tree.</summary>
    private sealed class DemoStepView
    {
        internal readonly Border Root;
        private readonly PillView _state;
        private readonly TextBlock _model;
        private readonly TextBlock _detail;
        private readonly TextBlock _error;
        private readonly TextBlock _reasoning;
        private readonly TextBox _output;
        private readonly Border _metricsHost = new();
        private readonly int _index;
        private int _textLength = -1;
        private List<DemoMetric>? _metricsSource;
        private int _metricCount = -1;

        internal DemoStepView(int index, string title)
        {
            _index = index;
            _state = new PillView($"DemoStepState_{index}");
            _model = Id(Small(""), $"DemoStepModel_{index}");
            _detail = Id(Small(""), $"DemoStepDetail_{index}");
            _error = Id(Small("", AccentDeep), $"DemoStepError_{index}");
            _reasoning = Id(Small(""), $"DemoStepReasoning_{index}");
            _output = Id(new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 13,
                MaxHeight = 340, HorizontalAlignment = HorizontalAlignment.Stretch,
            }, $"DemoStepText_{index}");
            ScrollViewer.SetVerticalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
            Root = Id(CardBorder(Stack(8,
                Lead(10, _state.Root, Stack(2, Text($"{index + 1}. {title}", 15, Ink, FontWeights.SemiBold), _model)),
                _detail, _output, _reasoning, _metricsHost, _error), 16, Paper), $"DemoStep_{index}");
        }

        internal string OutputText => _output.Text;

        internal void Update(DemoStepRecord step)
        {
            var (label, background, foreground) = step.State switch
            {
                "running" => ("RUNNING", AmberTint, AmberInk),
                "done" => ("DONE", GreenTint, GreenInk),
                "failed" => ("FAILED", AccentTint, AccentDeep),
                "stopped" => ("STOPPED", AccentTint, AccentDeep),
                "skipped" => ("SKIPPED", Sand, Muted),
                _ => ("WAITING", Sand, Muted),
            };
            _state.Set(label, background, foreground);
            _model.Text = step.Model;
            Show(_model, step.Model.Length > 0);
            _detail.Text = step.State is "running" or "skipped" ? step.Detail ?? "" : "";
            Show(_detail, _detail.Text.Length > 0);
            _error.Text = step.Error ?? "";
            Show(_error, _error.Text.Length > 0);
            if (step.Text.Length != _textLength)
            {
                _textLength = step.Text.Length;
                _output.Text = step.Text.ToString();
            }
            Show(_output, step.Text.Length > 0);
            _reasoning.Text = step.ReasoningCharacters > 0
                ? $"Private reasoning withheld: {step.ReasoningCharacters:N0} characters (never shown, exported or saved)." : "";
            Show(_reasoning, _reasoning.Text.Length > 0);
            if (!ReferenceEquals(_metricsSource, step.Metrics) || step.Metrics.Count != _metricCount)
            {
                _metricsSource = step.Metrics;
                _metricCount = step.Metrics.Count;
                _metricsHost.Child = step.Metrics.Count == 0 ? null : new AdaptiveGrid(160, 4, 8).With([.. step.Metrics.Select((m, i) =>
                    (FrameworkElement)Id(new Border
                    {
                        Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
                        Child = Stack(2, Text(m.Value, 15, Ink, FontWeights.SemiBold), Small(m.Label)),
                    }, $"DemoStepMetric_{_index}_{i}"))]);
            }
            Show(_metricsHost, step.Metrics.Count > 0);
        }
    }

    /// <summary>Mutable state of the run in progress; snapshots share its lists so views update incrementally.</summary>
    private sealed class ActiveDemo(DemoInfo demo, string mode, string? option, string prompt, bool edited, IEnumerable<DemoStepRecord> steps)
    {
        internal DemoInfo Demo { get; } = demo;
        internal string Mode { get; } = mode;
        private readonly DateTimeOffset _started = DateTimeOffset.Now;
        private readonly List<DemoStepRecord> _steps = [.. steps];
        private readonly List<DemoCheck> _checks = [];
        private DemoSummary? _summary;
        internal string? Error { get; set; }
        internal DemoSummary? Summary => _summary;

        internal DemoRunRecord Snapshot() => new(Demo, Mode, option, prompt, edited, _started, _steps, _checks, _summary, Error);

        internal string Progress
        {
            get
            {
                int i = _steps.FindIndex(s => s.State == "running");
                return i < 0 ? "Working..." : $"Step {i + 1} of {_steps.Count}: {_steps[i].Detail ?? _steps[i].Title}";
            }
        }

        internal void Apply(DemoEvent ev)
        {
            var step = ev.Index >= 0 && ev.Index < _steps.Count ? _steps[ev.Index] : null;
            switch (ev.Kind)
            {
                case DemoEventKind.Plan when ev.Plan is { Count: > 0 } plan:
                    _steps.Clear();
                    _steps.AddRange(plan.Select(p => new DemoStepRecord(p.Title, p.Model)));
                    break;
                case DemoEventKind.Step when step is not null:
                    step.State = ev.State ?? step.State;
                    if (!string.IsNullOrEmpty(ev.Model)) step.Model = ev.Model;
                    if (ev.Detail is not null) step.Detail = ev.Detail;
                    if (ev.Error is not null) step.Error = ev.Error;
                    if (ev.Metrics is { Count: > 0 } metrics)
                    {
                        step.Metrics.Clear();
                        step.Metrics.AddRange(metrics);
                    }
                    break;
                case DemoEventKind.Text when step is not null:
                    step.Text.Append(ev.Text);
                    break;
                case DemoEventKind.Activity when step is not null:
                    step.ReasoningCharacters = ev.ReasoningCharacters;
                    break;
                case DemoEventKind.Check when ev.Check is not null:
                    _checks.Add(ev.Check);
                    break;
                case DemoEventKind.Done:
                    _summary = ev.Summary;
                    break;
                case DemoEventKind.Error:
                    Error = ev.Error ?? "The demo failed.";
                    break;
            }
        }

        /// <summary>After a failed or stopped run, nothing is left looking as if it were still running.</summary>
        internal void Finish()
        {
            if (Error is null) return;
            foreach (var s in _steps.Where(s => s.State == "running")) s.State = "stopped";
        }
    }
}
