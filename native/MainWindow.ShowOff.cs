using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed record SpeedRun(string Model, string Alias, string Provider, double? DecodeTps, string DecodeSource, double? TtftMs,
    double? OutputTokens, double? PrefillTps, string PrefillSource, double? PrefillPromptTokens, int PrefillCharacters,
    DateTimeOffset MeasuredAt, string Note);

/// <summary>
/// Show it off: a live speed test on the loaded model, a session leaderboard across models, a presenter run of show,
/// and launchers for companion local-AI tools. Every number is measured on this PC now and labelled with its source.
/// </summary>
internal sealed partial class MainWindow
{
    private const string SpeedActivity = "The speed test";
    private const int MaxSpeedRuns = 20;
    private readonly List<SpeedRun> _speedRuns = [];
    private SpeedRun? _lastSpeed;
    private bool _toolsLoading;
    private readonly TextBlock _speedModel = Id(Text("Load a model, then run the speed test.", 14, Ink, FontWeights.SemiBold), "SpeedModel");
    private readonly TextBlock _speedNumber = Id(Set(Text("-", 72, Accent, FontWeights.SemiBold), t => { t.FontFamily = Display; t.LineHeight = 80; }), "SpeedLive");
    private readonly TextBlock _speedUnit = Id(Text("tokens per second", 15, Muted, FontWeights.SemiBold), "SpeedUnit");
    private readonly TextBlock _speedPhase = Id(Small("Two phases: generation speed, then reading speed. About 30-90 seconds."), "SpeedPhase");
    private readonly ProgressBar _speedProgress = Id(new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed }, "SpeedProgress");
    private readonly TextBlock _speedDecode = Id(Set(Text("-", 24, Ink, FontWeights.SemiBold), t => t.FontFamily = Display), "SpeedDecode");
    private readonly TextBlock _speedDecodeSource = Id(Small(""), "SpeedDecodeSource");
    private readonly TextBlock _speedTtft = Id(Set(Text("-", 24, Ink, FontWeights.SemiBold), t => t.FontFamily = Display), "SpeedTtft");
    private readonly TextBlock _speedPrefill = Id(Set(Text("-", 24, Ink, FontWeights.SemiBold), t => t.FontFamily = Display), "SpeedPrefill");
    private readonly TextBlock _speedPrefillSource = Id(Small(""), "SpeedPrefillSource");
    private readonly TextBox _speedText = ReadOnlyBox("SpeedText", 90);
    private readonly Button _speedRun = Id(Primary("Run the speed test"), "SpeedRun");
    private readonly Button _speedStop = Id(Secondary("Stop"), "SpeedStop");
    private readonly Button _speedExport = Id(Secondary("Export results (JSON)"), "SpeedExport");
    private readonly TextBlock _speedBlocker = Id(Small(""), "SpeedBlocker");
    private readonly TextBlock _speedNote = Id(Small(""), "SpeedNote");
    private readonly InfoBar _speedInfo = Id(new InfoBar { IsClosable = true }, "SpeedInfo");
    private readonly StackPanel _speedBoard = Id(Stack(6), "SpeedBoard");
    private readonly StackPanel _toolGallery = Id(Stack(16), "ToolGallery");
    private readonly InfoBar _toolInfo = Id(new InfoBar { IsClosable = true }, "ToolInfo");

    private FrameworkElement BuildShowOff()
    {
        _speedRun.Click += async (_, _) => await RunSpeedTestAsync();
        _speedStop.Click += (_, _) => { _speedPhase.Text = "Stopping..."; StopLab(); };
        _speedExport.Click += (_, _) => ExportSpeed();
        _speedExport.IsEnabled = false;
        Show(_speedText, false);
        var switchModel = Id(new HyperlinkButton { Content = "Load a different model in the Live GPU workspace", Padding = new Thickness(0), Foreground = Accent }, "SpeedSwitchModel");
        switchModel.Click += (_, _) => Navigate("live");
        var refreshTools = Id(Secondary("Refresh"), "ToolRefresh");
        refreshTools.Click += async (_, _) => await RefreshToolsAsync();
        RenderSpeedBoard();
        _toolGallery.Children.Add(Body("Looking for companion tools beside the studio..."));

        var gauge = CardBorder(Stack(6, _speedModel,
            Row(12, _speedNumber, Set(Stack(0, _speedUnit), s => s.VerticalAlignment = VerticalAlignment.Bottom)),
            _speedPhase, _speedProgress), 22, AccentTint);
        var results = new AdaptiveGrid(190, 3, 10).With(
            SpeedMetric(_speedDecode, "Generation speed (tokens/s)", _speedDecodeSource),
            SpeedMetric(_speedTtft, "First visible text", null),
            SpeedMetric(_speedPrefill, "Reading speed (prompt tokens/s)", _speedPrefillSource));
        var speed = Id(CardBorder(Stack(14,
            Row(10, Glyph("\uEC4A", 20, Accent), H2("GPU speedometer")),
            Body("Measures the model loaded right now, on this laptop's RTX GPU: how fast it writes, and how fast it reads a long prompt."),
            gauge, results,
            _speedText,
            Set(Row(10, _speedRun, _speedStop, _speedExport), r => r.Margin = new Thickness(0, 4, 0, 0)),
            _speedBlocker, _speedInfo, _speedNote,
            Small(Evidence.MetricCaveat + " Generated text is not exported; only timings and counts.")), 22), "SpeedCard");
        var board = Id(CardBorder(Stack(12,
            Row(10, Glyph("\uE9F9", 20, Accent), H2("Session leaderboard")),
            Body("Run the test, switch models, run it again. Each row is one run, ranked by generation speed."),
            _speedBoard, switchModel,
            Small("Session only: cleared when the studio closes. Rows from different models are comparable only as single runs on this PC.")), 22), "SpeedBoardCard");

        var steps = new AdaptiveGrid(200, 5, 12).With(
            ShowStep(1, "Show the decision", "Route a request with personal data and a cloud need.", "Open the router", () => OpenRouterMoment()),
            ShowStep(2, "Put a number on it", "Run the speedometer on the loaded model.", "Run speed test", () => { _ = RunSpeedTestAsync(); }),
            ShowStep(3, "Prove it is an endpoint", "Start the loopback API and send a real call.", "Open Build on Surface", () => Navigate("build")),
            ShowStep(4, "Make it theirs", "Run a workflow from their industry.", "Industry scenarios", () => Navigate("industries")),
            ShowStep(5, "Leave with a deck", "Prepare a brief for the offline PPTX generator.", "Open PPTX Forge", () => OpenTool("forge", "open")));

        return Page(Stack(22,
            Stack(10, Eyebrow("Show it off"), H1("Put a number on it, live."),
                Body("Measure what the loaded model does on this PC right now, rank models over a session, follow a five-minute run of show, "
                    + "and launch the companion tools that tell the rest of the Hybrid Intelligence story.")),
            TwoPane(speed, board, 540, 1020),
            Stack(12, Eyebrow("Five-minute run of show"), H2("A demo that builds to a close"), steps),
            Stack(12,
                new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } },
                    Children = { Stack(6, Eyebrow("Companion tools"), H2("More ways to show local AI on Surface")), Set(refreshTools, b => { Grid.SetColumn(b, 1); b.VerticalAlignment = VerticalAlignment.Bottom; }) },
                },
                Body("Separate projects that run beside the studio. The studio only finds and opens them; each has its own runtime, data and caveats."),
                _toolInfo, _toolGallery)));
    }

    private static FrameworkElement SpeedMetric(TextBlock value, string label, TextBlock? source) =>
        new Border
        {
            Background = Paper, CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12),
            Child = source is null ? Stack(2, value, Small(label)) : Stack(2, value, Small(label), source),
        };

    private FrameworkElement ShowStep(int number, string title, string detail, string action, Action click)
    {
        var button = Id(Secondary(action + "  \u2192"), $"ShowOffStep_{number}");
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Click += (_, _) => click();
        return CardBorder(Stack(8, Text(number.ToString(), 28, Accent, FontWeights.SemiBold), H3(title), Small(detail), button), 18);
    }

    private void OpenRouterMoment()
    {
        Navigate("hybrid");
        if (_hybridCatalog is { } catalog && Items(catalog, "presets").FirstOrDefault(p => Str(p, "id") == "rates") is { ValueKind: JsonValueKind.Object } preset)
            _ = ApplyHybridPresetAsync(preset);
    }

    private async Task RunSpeedTestAsync()
    {
        if (LabBlocker() is string blocker)
        {
            ShowInfo(_speedInfo, "The speed test cannot start", blocker, InfoBarSeverity.Warning);
            return;
        }
        _speedInfo.IsOpen = false;
        if (_currentSection != "showoff") Navigate("showoff");
        var token = BeginLab(SpeedActivity);
        foreach (var t in new[] { _speedDecode, _speedTtft, _speedPrefill }) t.Text = "-";
        _speedDecodeSource.Text = _speedPrefillSource.Text = _speedNote.Text = "";
        _speedNumber.Text = "-";
        _speedUnit.Text = "waiting for the first token";
        _speedText.Text = "";
        Show(_speedText, true);
        Show(_speedProgress, true);
        _speedPhase.Text = "Starting...";
        var text = new StringBuilder();
        JsonElement model = default;
        JsonElement? complete = null;
        string? error = null;
        bool stopped = false;
        int prefillChars = 0;
        try
        {
            await foreach (var ev in _client.StreamJsonAsync("/api/speedtest", new Dictionary<string, object>(), token))
            {
                switch (Str(ev, "type"))
                {
                    case "start":
                        model = ev.TryGetProperty("model", out var m) ? m : default;
                        _speedModel.Text = SpeedModelLine(model) + (StatusParser.Bool(ev, "reasoning") ? " \u00B7 reasoning model: private reasoning is timed, never shown" : "");
                        break;
                    case "phase":
                        string phase = Str(ev, "phase"), state = Str(ev, "state");
                        if (state == "running" && phase == "decode")
                            _speedPhase.Text = "1 of 2 \u00B7 Generation speed: writing a ~200-word explainer.";
                        else if (state == "running")
                        {
                            prefillChars = (int)(StatusParser.Num(ev, "characters") ?? 0);
                            _speedPhase.Text = $"2 of 2 \u00B7 Reading speed: reading {prefillChars:N0} characters of synthetic policy text.";
                            _speedUnit.Text = "reading the long prompt...";
                        }
                        else if (ev.TryGetProperty("result", out var result)) ApplySpeedPhase(phase, result);
                        break;
                    case "token":
                        text.Append(Str(ev, "text"));
                        _speedText.Text = text.ToString();
                        break;
                    case "progress":
                        if (Str(ev, "phase") == "decode" && StatusParser.Num(ev, "live_rate") is double rate)
                        {
                            _speedNumber.Text = $"{rate:F1}";
                            _speedUnit.Text = "streamed chunks / second (live)";
                        }
                        else if (Str(ev, "phase") == "prefill" && StatusParser.Num(ev, "elapsed_ms") is double elapsed)
                            _speedUnit.Text = $"reading... {elapsed / 1000:F1} s";
                        break;
                    case "complete":
                        complete = ev;
                        break;
                    case "error":
                        error = Str(ev, "error");
                        break;
                }
            }
            if (error is null && complete is null) error = "The stream ended before the speed test completed.";
        }
        catch (Exception) when (token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            stopped = true;
            error = "Stopped by the presenter. No result is recorded.";
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            error = exception.Message;
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            Show(_speedProgress, false);
            EndLab();
        }

        if (error is not null || complete is not { } done)
        {
            ShowInfo(_speedInfo, stopped ? "Speed test stopped" : "The speed test did not complete", error ?? "",
                stopped ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
            _speedPhase.Text = "Incomplete: no result is recorded.";
            _speedNumber.Text = "-";
            _speedUnit.Text = "tokens per second";
        }
        else
        {
            var info = done.TryGetProperty("model", out var dm) && dm.ValueKind == JsonValueKind.Object ? dm : model;
            var run = new SpeedRun(
                Model: info.ValueKind == JsonValueKind.Object ? Str(info, "name") : "Unknown model",
                Alias: info.ValueKind == JsonValueKind.Object ? Str(info, "alias") : "",
                Provider: info.ValueKind == JsonValueKind.Object ? Str(info, "provider") : "",
                DecodeTps: StatusParser.Num(done, "decode_tps"), DecodeSource: Str(done, "decode_source"),
                TtftMs: StatusParser.Num(done, "ttft_ms"), OutputTokens: StatusParser.Num(done, "output_tokens"),
                PrefillTps: StatusParser.Num(done, "prefill_tps"), PrefillSource: Str(done, "prefill_source"),
                PrefillPromptTokens: StatusParser.Num(done, "prefill_prompt_tokens"),
                PrefillCharacters: (int)(StatusParser.Num(done, "prefill_characters") ?? prefillChars),
                MeasuredAt: DateTimeOffset.Now, Note: Str(done, "note"));
            _lastSpeed = run;
            _speedRuns.Add(run);
            if (_speedRuns.Count > MaxSpeedRuns) _speedRuns.RemoveAt(0);
            RenderSpeedBoard();
            _speedNumber.Text = run.DecodeTps is double tps ? $"{tps:F1}" : "-";
            _speedUnit.Text = "tokens / second generated";
            _speedPhase.Text = $"Measured on this PC at {run.MeasuredAt:t}.";
            _speedNote.Text = run.Note;
        }
        UpdateShowOffButtons();
        if (!_closed) await RefreshStatusAsync();
    }

    private static string SpeedModelLine(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object) return "Model: unknown";
        string provider = Str(model, "provider");
        return "Model: " + Str(model, "name") + (provider.Length > 0 ? " \u00B7 " + (provider == "CUDAExecutionProvider" ? "CUDA EP" : provider) : "");
    }

    private void ApplySpeedPhase(string phase, JsonElement result)
    {
        if (phase == "decode")
        {
            _speedDecode.Text = StatusParser.Num(result, "decode_tps") is double tps ? $"{tps:F1}" : "-";
            _speedDecodeSource.Text = Str(result, "decode_source");
            _speedTtft.Text = StatusParser.Num(result, "ttft_ms") is double ttft ? $"{ttft / 1000:F2} s" : "-";
            if (StatusParser.Num(result, "decode_tps") is double rate)
            {
                _speedNumber.Text = $"{rate:F1}";
                _speedUnit.Text = "tokens / second generated";
            }
        }
        else
        {
            _speedPrefill.Text = StatusParser.Num(result, "prefill_tps") is double tps ? $"{tps:N0}" : "-";
            _speedPrefillSource.Text = Str(result, "prefill_source");
        }
    }

    private void RenderSpeedBoard()
    {
        _speedBoard.Children.Clear();
        if (_speedRuns.Count == 0)
        {
            _speedBoard.Children.Add(Small("No runs yet this session."));
            return;
        }
        _speedBoard.Children.Add(BoardRow("#", "Model", "Gen tok/s", "Read tok/s", "First text", header: true));
        int rank = 1;
        foreach (var run in _speedRuns.OrderByDescending(r => r.DecodeTps ?? -1).ThenBy(r => r.MeasuredAt))
        {
            var row = BoardRow(rank.ToString(), $"{run.Model}\n{run.MeasuredAt:t}",
                run.DecodeTps is double d ? $"{d:F1}" : "-",
                run.PrefillTps is double p ? $"{p:N0}" + (run.PrefillSource.StartsWith("Rough", StringComparison.Ordinal) ? "*" : "") : "-",
                run.TtftMs is double t ? $"{t / 1000:F2} s" : "-");
            _speedBoard.Children.Add(Id(row, $"SpeedBoardRow_{rank}"));
            rank++;
        }
        if (_speedRuns.Any(r => r.PrefillSource.StartsWith("Rough", StringComparison.Ordinal)))
            _speedBoard.Children.Add(Small("* Rough estimate: the runtime did not report prompt tokens, so about 4 characters per token is assumed."));
    }

    private static FrameworkElement BoardRow(string rank, string model, string gen, string read, string ttft, bool header = false)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 6, 10, 6) };
        foreach (var width in new[] { 28.0, -1, 80, 90, 80 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        string[] cells = [rank, model, gen, read, ttft];
        for (int i = 0; i < cells.Length; i++)
        {
            var cell = header ? Text(cells[i], 12, Muted, FontWeights.SemiBold)
                : Text(cells[i], i == 2 ? 15 : 13, i == 2 ? Accent : Ink, i is 0 or 2 ? FontWeights.SemiBold : FontWeights.Normal);
            if (i >= 2) cell.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return header ? grid : new Border { Background = Paper, CornerRadius = new CornerRadius(8), Child = grid };
    }

    private void ExportSpeed()
    {
        if (_lastSpeed is not { } latest) return;
        try
        {
            static JsonObject Row(SpeedRun r) => new()
            {
                ["model"] = r.Model, ["alias"] = r.Alias, ["provider"] = r.Provider,
                ["generation_tokens_per_second"] = r.DecodeTps, ["generation_source"] = r.DecodeSource,
                ["time_to_first_visible_text_ms"] = r.TtftMs, ["output_tokens"] = r.OutputTokens,
                ["reading_prompt_tokens_per_second"] = r.PrefillTps, ["reading_source"] = r.PrefillSource,
                ["reading_prompt_tokens"] = r.PrefillPromptTokens, ["reading_prompt_characters"] = r.PrefillCharacters,
                ["measured_at"] = r.MeasuredAt.ToString("o"),
            };
            var json = new JsonObject
            {
                ["schema"] = Brand.SchemaPrefix + ".speedtest/1",
                ["exported_at"] = DateTimeOffset.Now.ToString("o"),
                ["device"] = Evidence.Device(_status?.Device),
                ["latest"] = Row(latest),
                ["session_runs"] = new JsonArray(_speedRuns.Select(r => (JsonNode)Row(r)).ToArray()),
                ["note"] = latest.Note,
                ["caveat"] = Evidence.MetricCaveat,
                ["privacy"] = "Generated text and private reasoning are not exported; only timings and counts.",
            };
            string path = Evidence.Save(json, ExportDirectory, "speedtest");
            ShowInfo(_speedInfo, "Speed results exported", path, InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowInfo(_speedInfo, "Export failed", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshToolsAsync()
    {
        if (_toolsLoading || _closed) return;
        _toolsLoading = true;
        try
        {
            string root = _options.Root;
            var states = await Task.Run(() => Toolkit.Tools.Select(t => Toolkit.Inspect(t, root)).ToList());
            if (_closed) return;
            RenderTools(states);
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            ShowInfo(_toolInfo, "Companion tools could not be checked", exception.Message, InfoBarSeverity.Error);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing.
        }
        finally
        {
            _toolsLoading = false;
        }
    }

    private List<ToolState> _toolStates = [];

    private void RenderTools(List<ToolState> states)
    {
        _toolStates = states;
        _toolGallery.Children.Clear();
        var grid = new AdaptiveGrid(330, 3, 16);
        foreach (var state in states)
        {
            var tool = state.Tool;
            var (bg, fg) = state.Status switch
            {
                "Found" or "Running" => (GreenTint, GreenInk),
                "Not found" => (Sand, Muted),
                _ => (AmberTint, AmberInk),
            };
            var actions = new AdaptiveGrid(170, 3, 8);
            foreach (var action in state.Actions)
            {
                var button = Id(action.Primary ? Primary(action.Label) : Secondary(action.Label), $"Tool_{tool.Id}_{action.Id}");
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.MinHeight = 38;
                string actionId = action.Id;
                button.Click += (_, _) => OpenTool(tool.Id, actionId);
                actions.With(button);
            }
            var card = Stack(10,
                Lead(10, Glyph(tool.Glyph, 22, Accent), Stack(4, H3(tool.Title), Id(Pill(state.Status.ToUpperInvariant(), bg, fg), $"ToolStatus_{tool.Id}"))),
                Body(tool.Summary),
                Small("Why it lands: " + tool.Story, Ink),
                Small(tool.Boundary));
            if (state.Detail is { Length: > 0 } detail) card.Children.Add(Small(detail));
            card.Children.Add(Small(tool.Repository));
            card.Children.Add(actions);
            grid.With(Id(CardBorder(card, 20), "Tool_" + tool.Id));
        }
        _toolGallery.Children.Add(grid);
    }

    private void OpenTool(string toolId, string actionId)
    {
        var state = _toolStates.FirstOrDefault(s => s.Tool.Id == toolId)
            ?? Toolkit.Inspect(Toolkit.Tools.First(t => t.Id == toolId), _options.Root);
        var action = state.Actions.FirstOrDefault(a => a.Id == actionId);
        if (action is null)
        {
            Navigate("showoff");
            ShowInfo(_toolInfo, state.Tool.Title + " is not ready", state.Detail ?? "It was not found beside the studio.", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            if (action.CopyText is { } copy)
            {
                CopyToClipboard(copy);
                ShowInfo(_toolInfo, "Command copied", copy, InfoBarSeverity.Informational);
                return;
            }
            if (toolId == "forge" && actionId == "open" && PptxForge.IsRunning())
            {
                ShowInfo(_toolInfo, "Offline PPTX Forge is already open", "Switch to its window. Use Industry scenarios to prepare a brief for it.", InfoBarSeverity.Informational);
                return;
            }
            if (toolId == "hydrafusion" && actionId == "start" && (_running || _demoRunning || _labRunning))
            {
                ShowInfo(_toolInfo, "Wait for the current run", "HydraFusion loads its own model on the GPU. Start it when the studio is idle.", InfoBarSeverity.Warning);
                return;
            }
            if (!_options.Smoke) Toolkit.Open(action.Target!);
            ShowInfo(_toolInfo, state.Tool.Title, actionId switch
            {
                "start" => "Starting in its own window. It opens your browser when ready; the first start can take a minute while its model loads.",
                "folder" => "Opened the project folder.",
                "repo" => "Opened the repository page in your browser.",
                _ => "Opened in its own window. It is a separate app with its own runtime and caveats.",
            }, InfoBarSeverity.Informational);
            if (actionId == "start") _ = RefreshToolsLaterAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowInfo(_toolInfo, state.Tool.Title + " could not open", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task RefreshToolsLaterAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _lifetime.Token);
            await RefreshToolsAsync();
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
    }

    private void ApplyShowOffStatus(StatusSnapshot status)
    {
        if (!(_labRunning && _labActivity == SpeedActivity) && status.Engine.Model is { } model && _speedRuns.Count == 0 && _lastSpeed is null)
            _speedModel.Text = $"Loaded now: {model.Name}";
        UpdateShowOffButtons();
    }

    private void UpdateShowOffButtons()
    {
        bool speedRunning = _labRunning && _labActivity == SpeedActivity;
        string? blocker = LabBlocker();
        _speedRun.IsEnabled = blocker is null;
        _speedStop.IsEnabled = speedRunning;
        _speedExport.IsEnabled = !speedRunning && _lastSpeed is not null;
        _speedBlocker.Text = speedRunning ? "" : blocker ?? "";
        Show(_speedBlocker, _speedBlocker.Text.Length > 0);
    }
}
