using System.Diagnostics;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private static readonly string[] ScenarioOrder = ["brief", "code", "field", "meeting"];

    private readonly ComboBox _modelPicker = Id(new ComboBox { Header = "Model on the RTX GPU", HorizontalAlignment = HorizontalAlignment.Stretch }, "LiveModelPicker");
    private readonly Button _loadModel = Id(Secondary("Load selected model"), "LiveLoadModel");
    private readonly TextBlock _engineText = Id(Small("Connecting to the local service"), "LiveEngineState");
    private readonly ProgressRing _engineRing = new() { Width = 16, Height = 16, IsActive = false };
    private readonly RadioButtons _scenarioPicker = Id(new RadioButtons { Header = "Workflow" }, "LiveScenarioPicker");
    private readonly TextBlock _scenarioNote = Id(Small(""), "LiveScenarioNote");
    private readonly Border _scenarioNoteCard = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Background = SlateTint, Visibility = Visibility.Collapsed };
    private readonly TextBox _prompt = Id(new TextBox
    {
        Header = "Prompt (editable for live runs)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150,
        HorizontalAlignment = HorizontalAlignment.Stretch
    }, "LivePrompt");
    private readonly HyperlinkButton _restorePrompt = Id(new HyperlinkButton { Content = "Restore authored prompt", Padding = new Thickness(0), Foreground = Accent }, "LiveRestorePrompt");
    private readonly TextBox _source = Id(new TextBox
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 12, MaxHeight = 260,
        HorizontalAlignment = HorizontalAlignment.Stretch
    }, "LiveSourceText");
    private readonly Button _runLive = Id(Primary("Run live on GPU"), "LiveRunButton");
    private readonly Button _runRehearsal = Id(Secondary("Play authored rehearsal"), "LiveRehearsalButton");
    private readonly Button _stop = Id(Secondary("Stop"), "LiveStopButton");
    private readonly Border _modeBanner = Id(new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Background = Sand }, "LiveModeBanner");
    private readonly TextBlock _modeText = Id(Text("Ready. Choose a workflow, then run it live on the GPU.", 13, Ink, FontWeights.SemiBold), "LiveModeText");
    private readonly InfoBar _runError = Id(new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, Title = "The run did not complete" }, "LiveErrorInfoBar");
    private readonly TextBlock _outputTitle = Id(H2("Output"), "LiveOutputTitle");
    private readonly TextBox _output = Id(new TextBox
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = Mono, FontSize = 13.5,
        MinHeight = 340, MaxHeight = 560, PlaceholderText = "Model output appears here as it streams.",
        HorizontalAlignment = HorizontalAlignment.Stretch
    }, "LiveOutput");
    private readonly TextBlock _runStatus = Id(Small(""), "LiveRunStatus");
    private readonly TextBlock _mTtft = Id(Text("-", 20, Ink, FontWeights.SemiBold), "LiveMetricTtft");
    private readonly TextBlock _mTotal = Id(Text("-", 20, Ink, FontWeights.SemiBold), "LiveMetricTotal");
    private readonly TextBlock _mTokens = Id(Text("-", 20, Ink, FontWeights.SemiBold), "LiveMetricTokens");
    private readonly TextBlock _mRate = Id(Text("-", 20, Ink, FontWeights.SemiBold), "LiveMetricRate");
    private readonly TextBlock _mModel = Id(Text("-", 14, Ink, FontWeights.SemiBold), "LiveMetricModel");
    private readonly TextBlock _mReasoning = Id(Text("-", 20, Ink, FontWeights.SemiBold), "LiveMetricReasoning");
    private readonly Button _copy = Id(Secondary("Copy output"), "LiveCopyButton");
    private readonly Button _export = Id(Secondary("Export evidence (JSON)"), "LiveExportButton");
    private readonly InfoBar _exportInfo = Id(new InfoBar { Severity = InfoBarSeverity.Success, IsClosable = true }, "LiveExportInfo");
    private CancellationTokenSource? _runCts;
    private bool _running;
    private bool _switching;
    private bool _modelsPopulated;
    private RunRecord? _lastRun;
    private int _lastTokenEvents;
    private readonly List<string> _scenarioKeys = [];

    private FrameworkElement BuildLive()
    {
        _engineRing.Visibility = Visibility.Collapsed;
        _scenarioNoteCard.Child = _scenarioNote;
        _modeBanner.Child = _modeText;
        _stop.IsEnabled = false;
        _copy.IsEnabled = _export.IsEnabled = false;
        _runLive.IsEnabled = _runRehearsal.IsEnabled = _loadModel.IsEnabled = false;

        _loadModel.Click += async (_, _) => await SwitchModelAsync();
        _scenarioPicker.SelectionChanged += (_, _) => OnScenarioChanged();
        _restorePrompt.Click += (_, _) => { if (SelectedScenario is { } s) _prompt.Text = s.Prompt; };
        _runLive.Click += async (_, _) => await RunScenarioAsync("live");
        _runRehearsal.Click += async (_, _) => await RunScenarioAsync("rehearsal");
        _stop.Click += (_, _) => { _runStatus.Text = "Stopping..."; _runCts?.Cancel(); };
        _copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(_output.Text);
            Clipboard.SetContent(package);
            ShowExportInfo("Copied", "Output text copied to the clipboard.", InfoBarSeverity.Informational);
        };
        _export.Click += (_, _) => ExportRun();

        var modelRow = new Grid { ColumnSpacing = 10 };
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        modelRow.Children.Add(_modelPicker);
        _loadModel.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_loadModel, 1);
        modelRow.Children.Add(_loadModel);

        var sourceExpander = Id(new Expander
        {
            Header = Text("Synthetic source notes sent with the prompt", 13, Ink, FontWeights.SemiBold),
            Content = _source, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        }, "LiveSourceExpander");

        var controls = CardBorder(Stack(14,
            modelRow,
            Lead(8, _engineRing, _engineText),
            Small("One model is resident at a time; loading another unloads the current one. Both are CUDA variants run through Foundry Local."),
            new Border { Height = 1, Background = Line },
            _scenarioPicker,
            _scenarioNoteCard,
            _prompt,
            _restorePrompt,
            sourceExpander,
            Set(Row(10, _runLive, _runRehearsal, _stop), r => r.Margin = new Thickness(0, 4, 0, 0)),
            Small("Rehearsal plays an authored sample with the fixed prompt. It is labelled, has no metrics, and is never presented as model output.")), 22);

        var metrics = new AdaptiveGrid(130, 6, 10).With(
            Metric(_mTtft, "First visible text"), Metric(_mTotal, "Total time"), Metric(_mTokens, "Output tokens"),
            Metric(_mRate, "Tokens / second"), Metric(_mReasoning, "Private reasoning withheld"), Metric(_mModel, "Model / provider"));

        var output = CardBorder(Stack(14,
            _modeBanner, _runError, _outputTitle, _output, _runStatus, metrics,
            Row(10, _copy, _export), _exportInfo,
            Small(Evidence.MetricCaveat + " " + Evidence.PrivacyNote)), 22);

        var layout = TwoPane(controls, output, 420, 1000);
        return Page(Stack(22,
            Stack(10, Eyebrow("Live GPU workspace"), H1("Real models. Real time. On this laptop."),
                Body("Every live run streams from a model loaded on the RTX GPU through the CUDA execution provider. Sources are synthetic, outputs are drafts, and generated code is shown as text only.")),
            layout));
    }

    private static FrameworkElement Metric(TextBlock value, string label) =>
        new Border { Background = Paper, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Child = Stack(2, value, Small(label)) };

    private static Grid TwoPane(FrameworkElement left, FrameworkElement right, double leftWidth, double breakpoint)
    {
        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(left);
        grid.Children.Add(right);
        left.VerticalAlignment = VerticalAlignment.Top;
        right.VerticalAlignment = VerticalAlignment.Top;
        bool? wide = null;
        void Apply(double width)
        {
            bool isWide = width >= breakpoint;
            if (wide == isWide) return;
            wide = isWide;
            grid.ColumnDefinitions[0].Width = isWide ? new GridLength(leftWidth) : new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions[1].Width = isWide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(right, isWide ? 1 : 0);
            Grid.SetRow(right, isWide ? 0 : 1);
            grid.ColumnSpacing = isWide ? 20 : 0;
        }
        grid.SizeChanged += (_, e) => Apply(e.NewSize.Width);
        Apply(breakpoint);
        return grid;
    }

    private Scenario? SelectedScenario =>
        _scenarioPicker.SelectedIndex >= 0 && _scenarioPicker.SelectedIndex < _scenarioKeys.Count
        && _scenarios.TryGetValue(_scenarioKeys[_scenarioPicker.SelectedIndex], out var s) ? s : null;

    private void PopulateScenarios()
    {
        string? previous = SelectedScenario?.Key;
        _scenarioKeys.Clear();
        _scenarioKeys.AddRange(ScenarioOrder.Where(_scenarios.ContainsKey).Concat(_scenarios.Keys.Where(k => !ScenarioOrder.Contains(k)).Order()));
        _scenarioPicker.Items.Clear();
        foreach (string key in _scenarioKeys)
            _scenarioPicker.Items.Add(Id(new RadioButton { Content = _scenarios[key].Label, Tag = key }, "LiveScenario_" + key));
        _scenarioPicker.SelectedIndex = Math.Max(0, previous is null ? 0 : _scenarioKeys.IndexOf(previous));
        UpdatePlatformTryButtons();
        RecomputeSales();
        UpdateTutorButton();
    }

    internal bool SelectScenario(string key)
    {
        int index = _scenarioKeys.IndexOf(key);
        if (index < 0 || _running) return false;
        _scenarioPicker.SelectedIndex = index;
        return true;
    }

    /// <summary>The editable prompt with '\n' line endings; the WinUI TextBox stores line breaks as '\r'.</summary>
    private string PromptText => _prompt.Text.ReplaceLineEndings("\n");

    private static bool IsPlatformFit(Scenario s) =>
        s.Key is "field" or "meeting" || s.Label.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase) || s.Label.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    private void OnScenarioChanged()
    {
        if (SelectedScenario is not { } s) return;
        _prompt.Text = s.Prompt;
        _source.Text = s.Source;
        _outputTitle.Text = s.Title;
        if (IsPlatformFit(s))
        {
            _scenarioNote.Text = "Platform-fit workflow (recommended pilot pattern). Here it runs live on this laptop's RTX GPU as a text fixture: no microphone, transcription or vision, and not evidence of QNN or OpenVINO execution.";
            _scenarioNoteCard.Visibility = Visibility.Visible;
        }
        else _scenarioNoteCard.Visibility = Visibility.Collapsed;
    }

    private void ApplyLiveStatus(StatusSnapshot status)
    {
        var engine = status.Engine;
        _switching = engine.IsBusy;
        _engineRing.IsActive = engine.IsBusy;
        _engineRing.Visibility = engine.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        _engineText.Text = engine.State == "error" ? $"{engine.Detail}: {engine.Error}" : engine.Detail;
        _engineText.Foreground = engine.State == "error" ? AccentDeep : engine.IsReady ? GreenInk : Muted;
        if (!_modelsPopulated && engine.Models.Count > 0 && !_modelPicker.IsDropDownOpen)
        {
            _modelPicker.Items.Clear();
            foreach (var m in engine.Models)
                _modelPicker.Items.Add(Id(new ComboBoxItem
                {
                    Content = Stack(0, Text(m.Label, 14, Ink, FontWeights.SemiBold),
                        Small($"{m.SizeMb:N0} MB catalog file{(m.Cached ? " | cached" : "")}{(m.Reasoning ? " | reasoning model" : "")}")),
                    Tag = m.Alias
                }, "LiveModel_" + m.Alias));
            _modelsPopulated = true;
            SelectModelInPicker(engine.SelectedModel);
        }
        else if (engine.IsBusy) SelectModelInPicker(engine.SelectedModel);
        UpdateLiveButtons();
    }

    private void SelectModelInPicker(string? alias)
    {
        for (int i = 0; i < _modelPicker.Items.Count; i++)
            if (_modelPicker.Items[i] is ComboBoxItem { Tag: string tag } && tag == alias) _modelPicker.SelectedIndex = i;
    }

    private void UpdateLiveButtons()
    {
        bool online = _status is not null && !_serviceInfo.IsOpen;
        bool ready = online && _status!.Engine.IsReady && !_switching;
        _runLive.IsEnabled = ready && !_running && SelectedScenario is not null;
        _runRehearsal.IsEnabled = online && !_running && !_switching && SelectedScenario is not null;
        _loadModel.IsEnabled = online && !_running && !_switching && _status!.Engine.State is "ready" or "error" && _modelPicker.SelectedItem is not null;
        _stop.IsEnabled = _running;
        _scenarioPicker.IsEnabled = !_running;
        _modelPicker.IsEnabled = !_running && !_switching;
        _prompt.IsReadOnly = _running;
        _copy.IsEnabled = !_running && _output.Text.Length > 0;
        _export.IsEnabled = !_running && _lastRun is not null;
    }

    private async Task SwitchModelAsync()
    {
        if (_modelPicker.SelectedItem is not ComboBoxItem { Tag: string alias } || _running) return;
        if (_status?.Engine.IsReady == true && _status.Engine.Model?.Alias == alias)
        {
            _engineText.Text = "That model is already loaded and ready.";
            return;
        }
        _runError.IsOpen = false;
        try
        {
            _switching = true;
            UpdateLiveButtons();
            _engineText.Text = "Requesting model switch...";
            await _client.SwitchModelAsync(alias, _lifetime.Token);
            _statusTimer.Interval = TimeSpan.FromSeconds(1.5);
            await RefreshStatusAsync();
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _switching = false;
            ShowRunError("Model switch was not accepted", exception.Message);
            UpdateLiveButtons();
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing during a switch request.
        }
    }

    private async Task RunScenarioAsync(string mode)
    {
        if (_running || SelectedScenario is not { } scenario) return;
        bool live = mode == "live";
        if (!live) _prompt.Text = scenario.Prompt;
        string prompt = live ? PromptText : scenario.Prompt;
        _runError.IsOpen = false;
        _exportInfo.IsOpen = false;
        if (live && (prompt.Trim().Length == 0 || prompt.Length > 4000))
        {
            ShowRunError("Prompt needs attention", "The prompt must contain 1 to 4,000 characters.");
            return;
        }
        _running = true;
        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _output.Text = "";
        _lastRun = null;
        _lastTokenEvents = 0;
        ResetMetrics();
        SetModeBanner(live, running: true);
        _runStatus.Text = live ? "Waiting for the first visible text from the GPU..." : "Playing the authored rehearsal...";
        UpdateLiveButtons();
        _statusTimer.Interval = TimeSpan.FromSeconds(3);

        var text = new StringBuilder();
        long reasoning = 0;
        RunMetrics? metrics = null;
        string? error = null;
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        try
        {
            await foreach (var ev in _client.RunAsync(scenario.Key, mode, prompt, _runCts.Token))
            {
                switch (ev.Kind)
                {
                    case RunEventKind.Token:
                        text.Append(ev.Text);
                        _lastTokenEvents++;
                        _output.Text = text.ToString();
                        _runStatus.Text = $"Streaming: {text.Length:N0} characters in {clock.Elapsed.TotalSeconds:F1} s";
                        break;
                    case RunEventKind.Activity:
                        reasoning = ev.ReasoningCharacters;
                        _mReasoning.Text = $"{reasoning:N0} chars";
                        if (text.Length == 0) _runStatus.Text = $"Model is reasoning privately ({reasoning:N0} characters withheld)...";
                        break;
                    case RunEventKind.Done:
                        metrics = ev.Metrics;
                        break;
                    case RunEventKind.Error:
                        error = ev.Error;
                        break;
                }
            }
            if (error is null && metrics is null) error = "The stream ended before the run completed.";
        }
        catch (Exception) when (_runCts.IsCancellationRequested)
        {
            error = "Stopped by the presenter. No completed result is claimed.";
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        finally
        {
            _running = false;
        }

        if (error is not null) metrics = null;
        _lastRun = new RunRecord(scenario.Key, scenario.Title, scenario.Label, prompt, scenario.Source, mode,
            text.ToString(), metrics, live ? reasoning : 0, started, error);
        if (error is not null)
        {
            ShowRunError("The run did not complete", error);
            _runStatus.Text = "Run did not complete; partial text (if any) is not a finished result.";
            SetModeBanner(live, running: false, failed: true);
        }
        else
        {
            SetModeBanner(live, running: false);
            ShowMetrics(live, metrics!, reasoning);
            _runStatus.Text = live
                ? $"Completed live on {metrics!.Provider ?? "the GPU"}: {text.Length:N0} visible characters."
                : "Authored rehearsal shown. Not model output; no performance metrics.";
        }
        UpdateLiveButtons();
        if (!_closed) await RefreshStatusAsync();
    }

    private void ResetMetrics()
    {
        foreach (var t in new[] { _mTtft, _mTotal, _mTokens, _mRate, _mReasoning, _mModel }) t.Text = "-";
    }

    private void ShowMetrics(bool live, RunMetrics m, long reasoning)
    {
        if (!live)
        {
            ResetMetrics();
            _mModel.Text = "Authored sample";
            return;
        }
        _mTtft.Text = m.TtftMs is double t ? $"{t / 1000:F2} s" : "-";
        _mTotal.Text = m.TotalMs is double total ? $"{total / 1000:F1} s" : "-";
        _mTokens.Text = m.OutputTokens is double tokens ? $"{tokens:N0}" : "-";
        _mRate.Text = m.TokensPerSecond is double rate ? $"{rate:F1}" : "-";
        _mReasoning.Text = $"{reasoning:N0} chars";
        _mModel.Text = $"{m.ModelName ?? "Unknown"}\n{(m.Provider == "CUDAExecutionProvider" ? "CUDA EP" : m.Provider ?? "provider not reported")}";
    }

    private void SetModeBanner(bool live, bool running, bool failed = false)
    {
        if (failed)
        {
            _modeBanner.Background = AccentTint;
            _modeText.Foreground = AccentDeep;
            _modeText.Text = live ? "LIVE RUN INCOMPLETE - no result is claimed" : "REHEARSAL INCOMPLETE";
            return;
        }
        _modeBanner.Background = live ? GreenTint : AmberTint;
        _modeText.Foreground = live ? GreenInk : AmberInk;
        _modeText.Text = live
            ? (running ? "LIVE - generating now on this laptop's RTX GPU" : "LIVE - generated on this laptop's RTX GPU")
            : "REHEARSAL - authored sample, not model output. No metrics.";
    }

    private void ShowRunError(string title, string message)
    {
        _runError.Title = title;
        _runError.Message = message;
        _runError.IsOpen = true;
    }

    private void ShowExportInfo(string title, string message, InfoBarSeverity severity)
    {
        _exportInfo.Title = title;
        _exportInfo.Message = message;
        _exportInfo.Severity = severity;
        _exportInfo.IsOpen = true;
    }

    private string? ExportRun()
    {
        if (_lastRun is null) return null;
        try
        {
            string path = Evidence.Save(Evidence.Run(_lastRun, _status?.Device, _host.Lifecycle), ExportDirectory, $"run-{_lastRun.ScenarioKey}-{_lastRun.Mode}");
            ShowExportInfo("Evidence exported", path, InfoBarSeverity.Success);
            return path;
        }
        catch (Exception exception)
        {
            ShowExportInfo("Export failed", exception.Message, InfoBarSeverity.Error);
            return null;
        }
    }
}
