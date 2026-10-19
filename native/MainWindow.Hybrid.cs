using System.Text;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

/// <summary>
/// Hybrid router: a deterministic, explainable policy decides what stays on this PC, what is redacted here first,
/// and what would go to a cloud service. The studio never calls a cloud service; payloads are shown as NOT SENT.
/// </summary>
internal sealed partial class MainWindow
{
    private const int MaxHybridText = 4000;
    private JsonElement? _hybridCatalog;
    private bool _hybridLoading;
    private readonly StackPanel _hybridPresets = Id(Stack(8), "HybridPresets");
    private readonly TextBox _hybridText = Id(new TextBox
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 170, MaxHeight = 320, MaxLength = MaxHybridText,
        PlaceholderText = "Pick a preset, or paste a request. Use fictional data.",
        HorizontalAlignment = HorizontalAlignment.Stretch,
    }, "HybridInput");
    private readonly ToggleSwitch _hybridOffline = Id(new ToggleSwitch
    {
        Header = "What-if: network", OffContent = "Online", OnContent = "This PC is offline",
    }, "HybridOffline");
    private readonly ToggleSwitch _hybridBeyond = Id(new ToggleSwitch
    {
        Header = "What-if: workload size", OffContent = "Fits on this PC", OnContent = "Bigger than one PC should take on",
    }, "HybridBeyond");
    private readonly Button _hybridRoute = Id(Secondary("Explain the route"), "HybridRoute");
    private readonly Button _hybridRun = Id(Primary("Run it on this PC"), "HybridRun");
    private readonly Button _hybridStop = Id(Secondary("Stop"), "HybridStop");
    private readonly TextBlock _hybridBlocker = Id(Small(""), "HybridBlocker");
    private readonly InfoBar _hybridInfo = Id(new InfoBar { IsClosable = true }, "HybridInfo");
    private readonly TextBlock _hybridLaneText = Id(Text("NOT ROUTED YET", 12, Muted, FontWeights.SemiBold), "HybridLane");
    private readonly Border _hybridLane = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 4, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Background = Sand };
    private readonly TextBlock _hybridHeadline = Id(H2(""), "HybridHeadline");
    private readonly StackPanel _hybridSteps = Id(Stack(8), "HybridSteps");
    private readonly StackPanel _hybridReasons = Id(Stack(4), "HybridReasons");
    private readonly TextBlock _hybridMasked = MonoText("", "HybridMasked");
    private readonly Expander _hybridMaskedExpander = Id(new Expander
    {
        Header = "Pattern-masked view of the text",
        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
    }, "HybridMaskedExpander");
    private readonly StackPanel _hybridRunPanel = Id(Stack(8), "HybridRunPanel");
    private readonly TextBlock _hybridStage = Id(Text("", 14, Ink, FontWeights.SemiBold), "HybridStage");
    private readonly TextBox _hybridOutput = ReadOnlyBox("HybridOutput", 110);
    private readonly TextBlock _hybridMetrics = Id(Small(""), "HybridMetrics");
    private readonly TextBlock _hybridCheckText = Id(Text("", 13, Ink, FontWeights.SemiBold), "HybridCheck");
    private readonly Border _hybridCheck = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10) };
    private readonly TextBlock _hybridPayloadTitle = Id(Text("", 13, Slate, FontWeights.Bold), "HybridPayloadTitle");
    private readonly TextBlock _hybridPayloadText = MonoText("", "HybridPayloadText");
    private readonly TextBlock _hybridPayloadNote = Id(Small(""), "HybridPayloadNote");
    private readonly Border _hybridPayload = Id(new Border
    {
        Background = SlateTint, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12, 14, 12),
    }, "HybridPayload");
    private readonly TextBlock _hybridNote = Id(Small(""), "HybridNote");
    private readonly StackPanel _hybridPatterns = Id(Stack(16), "HybridPatterns");

    private FrameworkElement BuildHybrid()
    {
        _hybridLane.Child = _hybridLaneText;
        _hybridMaskedExpander.Content = _hybridMasked;
        _hybridCheck.Child = _hybridCheckText;
        _hybridPayload.Child = Stack(6, _hybridPayloadTitle, _hybridPayloadText, _hybridPayloadNote);
        _hybridRunPanel.Children.Add(_hybridStage);
        _hybridRunPanel.Children.Add(_hybridOutput);
        _hybridRunPanel.Children.Add(_hybridMetrics);
        _hybridPresets.Children.Add(Small("Connect to the local service to load the presets."));
        _hybridPatterns.Children.Add(Body("Connect to the local service to load the patterns."));
        _hybridText.TextChanged += (_, _) => UpdateHybridButtons();
        _hybridRoute.Click += async (_, _) => await RouteHybridAsync();
        _hybridRun.Click += async (_, _) => await RunHybridAsync();
        _hybridStop.Click += (_, _) => { _hybridMetrics.Text = "Stopping..."; StopLab(); };
        ResetHybridResult();

        var input = CardBorder(Stack(14,
            H3("1. What is being asked?"),
            _hybridPresets,
            _hybridText,
            Small("Fictional data only, up to 4,000 characters. Held in memory for this session; nothing is saved."),
            new AdaptiveGrid(220, 2, 12).With(_hybridOffline, _hybridBeyond),
            Set(Row(10, _hybridRoute, _hybridRun, _hybridStop), r => r.Margin = new Thickness(0, 4, 0, 0)),
            _hybridBlocker,
            Small("Explain the route applies the policy only; no model runs. Run it on this PC also runs the local or redaction step on the RTX GPU.")), 22);

        var result = CardBorder(Stack(14,
            H3("2. Where should it run, and why?"),
            _hybridLane, _hybridHeadline, _hybridSteps, _hybridReasons, _hybridMaskedExpander,
            _hybridRunPanel, _hybridCheck, _hybridPayload, _hybridNote), 22);

        return Page(Stack(22,
            Stack(10, Eyebrow("Hybrid router"), H1("Right work, right place, explained."),
                Body("Hybrid Intelligence means the PC does what it does best - private, fast, always available - and the cloud does what only it can: "
                    + "current information, organisation-wide data and frontier scale. This router shows the decision, the reasons and, when cloud is "
                    + "needed, exactly what would leave the device.")),
            CardBorder(Lead(12, Glyph("\uE72E", 20, Slate),
                Small("Boundary: this studio never calls a cloud service. Cloud payloads are displayed as NOT SENT so you can inspect them. "
                    + "The router is a set of deterministic, published rules - not a data-loss-prevention product - so the on-device model and a person still review.", Ink)), 14, SlateTint),
            _hybridInfo,
            TwoPane(input, result, 460, 1040),
            Stack(12, Eyebrow("Hybrid patterns"), H2("Seven ways to split the work between PC and cloud"),
                Body("Each pattern links to something you can run right now on this laptop. Use them to start a design conversation, not as an architecture decision."),
                _hybridPatterns)));
    }

    private async Task LoadHybridCatalogAsync()
    {
        if (_hybridLoading || _hybridCatalog is not null || _closed) return;
        _hybridLoading = true;
        try
        {
            var catalog = await _client.GetJsonAsync("/api/hybrid", _lifetime.Token);
            _hybridCatalog = catalog;
            PopulateHybridPresets(catalog);
            PopulateHybridPatterns(catalog);
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            ShowInfo(_hybridInfo, "Hybrid router unavailable", exception.Message + " Restart the local service with the updated studio files.", InfoBarSeverity.Error);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing.
        }
        finally
        {
            _hybridLoading = false;
        }
    }

    private void PopulateHybridPresets(JsonElement catalog)
    {
        _hybridPresets.Children.Clear();
        _hybridPresets.Children.Add(Small("Presets (fictional)"));
        var grid = new AdaptiveGrid(170, 6, 8);
        foreach (var preset in Items(catalog, "presets"))
        {
            string id = Str(preset, "id");
            var (bg, fg) = LaneBrushes(Str(preset, "expect"));
            var button = Id(new Button
            {
                Content = Stack(2, Text(Str(preset, "title"), 13, Ink, FontWeights.SemiBold), Text(LaneWord(Str(preset, "expect")), 11, fg, FontWeights.SemiBold)),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 9), CornerRadius = new CornerRadius(8), Background = bg,
            }, "HybridPreset_" + id);
            var captured = preset;
            button.Click += async (_, _) => await ApplyHybridPresetAsync(captured);
            grid.With(button);
        }
        _hybridPresets.Children.Add(grid);
    }

    private static string LaneWord(string lane) => lane switch
    {
        "local" => "Expect: on this PC",
        "hybrid" => "Expect: hybrid",
        "cloud" => "Expect: cloud",
        _ => "",
    };

    private async Task ApplyHybridPresetAsync(JsonElement preset)
    {
        _hybridText.Text = Str(preset, "text");
        _hybridOffline.IsOn = Str(preset, "network") == "offline";
        _hybridBeyond.IsOn = Str(preset, "scale") == "beyond_device";
        await RouteHybridAsync();
    }

    private void PopulateHybridPatterns(JsonElement catalog)
    {
        _hybridPatterns.Children.Clear();
        var grid = new AdaptiveGrid(330, 3, 16);
        foreach (var pattern in Items(catalog, "patterns"))
        {
            string id = Str(pattern, "id");
            var flow = Stack(6);
            foreach (var step in Items(pattern, "flow"))
                flow.Children.Add(Lead(8, WherePill(Str(step, "where")), Small(Str(step, "text"), Ink)));
            var card = Stack(10, H3(Str(pattern, "title")), Body(Str(pattern, "summary")), flow,
                Small("When: " + Str(pattern, "when")), Small(Str(pattern, "caveat")));
            if (pattern.TryGetProperty("proof", out var proof) && proof.ValueKind == JsonValueKind.Object)
            {
                string kind = Str(proof, "kind"), target = Str(proof, "target");
                var button = Id(Secondary(Str(proof, "label") + "  \u2192"), "PatternProof_" + id);
                button.MinHeight = 38;
                button.Click += (_, _) => OpenPatternProof(kind, target);
                card.Children.Add(button);
            }
            grid.With(Id(CardBorder(card, 20), "Pattern_" + id));
        }
        _hybridPatterns.Children.Add(grid);
    }

    private void OpenPatternProof(string kind, string target)
    {
        if (kind == "demo")
        {
            if (!OpenDemo(target)) ShowServiceError("Live demo unavailable", _demoCatalogError ?? "The local service has not provided this demo.");
        }
        else if (_pages.ContainsKey(target))
        {
            Navigate(target);
            if (target == "hybrid") _hybridText.Focus(FocusState.Programmatic);
        }
    }

    private Dictionary<string, object>? HybridBody()
    {
        string text = _hybridText.Text.ReplaceLineEndings("\n");
        if (text.Trim().Length == 0 || text.Length > MaxHybridText)
        {
            ShowInfo(_hybridInfo, "Request needs attention", $"Enter 1 to {MaxHybridText:N0} characters, or pick a preset.", InfoBarSeverity.Warning);
            return null;
        }
        return new Dictionary<string, object>
        {
            ["text"] = text,
            ["network"] = _hybridOffline.IsOn ? "offline" : "online",
            ["scale"] = _hybridBeyond.IsOn ? "beyond_device" : "fits",
        };
    }

    private async Task RouteHybridAsync()
    {
        if (_labRunning && _labActivity == HybridActivity) return;
        if (HybridBody() is not { } body) return;
        _hybridInfo.IsOpen = false;
        try
        {
            var decision = await _client.PostJsonAsync("/api/hybrid/route", body, _lifetime.Token, TimeSpan.FromSeconds(10));
            ResetHybridResult();
            RenderHybridDecision(decision, preview: true);
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            ShowInfo(_hybridInfo, "Could not route the request", exception.Message, InfoBarSeverity.Error);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing.
        }
    }

    private const string HybridActivity = "A hybrid run";

    private async Task RunHybridAsync()
    {
        if (LabBlocker(needsModel: false) is string blocker)
        {
            ShowInfo(_hybridInfo, "The hybrid run cannot start", blocker, InfoBarSeverity.Warning);
            return;
        }
        if (HybridBody() is not { } body) return;
        _hybridInfo.IsOpen = false;
        var token = BeginLab(HybridActivity);
        ResetHybridResult();
        _hybridHeadline.Text = "Routing on this PC...";
        var output = new StringBuilder();
        string? error = null;
        bool complete = false, stopped = false;
        try
        {
            await foreach (var ev in _client.StreamJsonAsync("/api/hybrid/run", body, token))
            {
                switch (Str(ev, "type"))
                {
                    case "route":
                        RenderHybridDecision(ev.GetProperty("decision"), preview: false);
                        break;
                    case "stage":
                        output.Clear();
                        _hybridOutput.Text = "";
                        _hybridStage.Text = Str(ev, "title") + ". " + Str(ev, "detail");
                        _hybridMetrics.Text = "Waiting for the first visible text from the GPU...";
                        Show(_hybridRunPanel, true);
                        break;
                    case "token":
                        output.Append(Str(ev, "text"));
                        _hybridOutput.Text = output.ToString();
                        _hybridMetrics.Text = $"Streaming from the GPU: {output.Length:N0} characters";
                        break;
                    case "activity":
                        if (output.Length == 0 && StatusParser.Num(ev, "reasoning_characters") is double chars)
                            _hybridMetrics.Text = $"Model is reasoning privately ({chars:N0} characters withheld)...";
                        break;
                    case "model_done":
                        _hybridMetrics.Text = HybridMetricsLine(ev.TryGetProperty("metrics", out var m) ? m : default);
                        break;
                    case "check":
                        RenderHybridCheck(ev);
                        break;
                    case "payload":
                        RenderHybridPayload(ev, queued: false);
                        break;
                    case "queued":
                        RenderHybridPayload(ev, queued: true);
                        break;
                    case "complete":
                        complete = true;
                        string note = Str(ev, "note");
                        double? ms = StatusParser.Num(ev, "total_ms");
                        _hybridNote.Text = (StatusParser.Bool(ev, "model_ran") ? "Completed on this PC" : "Routed without running a model")
                            + (ms is double total ? $" in {total / 1000:F1} s" : "") + ". Nothing was sent to any cloud service."
                            + (note.Length > 0 ? " " + note : "");
                        Show(_hybridNote, true);
                        break;
                    case "error":
                        error = Str(ev, "error");
                        break;
                }
            }
            if (error is null && !complete) error = "The stream ended before the run completed.";
        }
        catch (Exception) when (token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            stopped = true;
            error = "Stopped by the presenter. No completed result is claimed.";
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
            EndLab();
        }
        if (error is not null)
        {
            ShowInfo(_hybridInfo, stopped ? "Hybrid run stopped" : "The hybrid run did not complete", error,
                stopped ? InfoBarSeverity.Informational : InfoBarSeverity.Error);
            if (_hybridOutput.Text.Length > 0) _hybridMetrics.Text = "Incomplete: partial text is not a finished result.";
        }
        if (!_closed) await RefreshStatusAsync();
    }

    private static string HybridMetricsLine(JsonElement metrics)
    {
        if (metrics.ValueKind != JsonValueKind.Object) return "Generated on this PC.";
        string model = metrics.TryGetProperty("model", out var info) && info.ValueKind == JsonValueKind.Object ? Str(info, "name") : "";
        var parts = new List<string> { "Generated on this PC" + (model.Length > 0 ? " by " + model : "") };
        if (StatusParser.Num(metrics, "ttft_ms") is double ttft) parts.Add($"first text {ttft / 1000:F2} s");
        if (StatusParser.Num(metrics, "total_ms") is double total) parts.Add($"total {total / 1000:F1} s");
        if (StatusParser.Num(metrics, "tokens_per_second") is double rate) parts.Add($"{rate:F1} tokens/s");
        return string.Join(" \u00B7 ", parts) + ". One run, not a benchmark.";
    }

    private void ResetHybridResult()
    {
        _hybridLaneText.Text = "NOT ROUTED YET";
        _hybridLaneText.Foreground = Muted;
        _hybridLane.Background = Sand;
        _hybridHeadline.Text = "Pick a preset or type a request, then explain the route.";
        _hybridSteps.Children.Clear();
        _hybridReasons.Children.Clear();
        _hybridMasked.Text = "";
        _hybridOutput.Text = "";
        _hybridStage.Text = "";
        _hybridMetrics.Text = "";
        _hybridNote.Text = "";
        _hybridPayloadText.Text = "";
        Show(_hybridMaskedExpander, false);
        Show(_hybridRunPanel, false);
        Show(_hybridCheck, false);
        Show(_hybridPayload, false);
        Show(_hybridNote, false);
    }

    private void RenderHybridDecision(JsonElement decision, bool preview)
    {
        string lane = Str(decision, "lane");
        var (bg, fg) = LaneBrushes(lane);
        _hybridLane.Background = bg;
        _hybridLaneText.Foreground = fg;
        _hybridLaneText.Text = (Str(decision, "lane_label") + (StatusParser.Bool(decision, "deferred") ? " (cloud part held)" : "")).ToUpperInvariant();
        _hybridHeadline.Text = Str(decision, "headline");
        _hybridSteps.Children.Clear();
        int n = 1;
        foreach (var step in Items(decision, "steps"))
            _hybridSteps.Children.Add(Lead(10, WherePill(Str(step, "where")), Body($"{n++}. {Str(step, "text")}", Ink)));
        _hybridReasons.Children.Clear();
        _hybridReasons.Children.Add(Text("Why", 13, Ink, FontWeights.SemiBold));
        foreach (var reason in decision.TryGetProperty("reasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array ? reasons.EnumerateArray() : Enumerable.Empty<JsonElement>())
            _hybridReasons.Children.Add(Small("\u2022 " + (reason.GetString() ?? ""), Ink));
        _hybridReasons.Children.Add(Small(Str(decision, "note")));
        string masked = Str(decision, "masked_text");
        _hybridMasked.Text = masked;
        Show(_hybridMaskedExpander, masked.Length > 0 && Items(decision, "signals").Any(s => Str(s, "group") is "identifier" or "secret"));
        if (preview)
        {
            if (decision.TryGetProperty("cloud_payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                RenderHybridPayload(payload, queued: StatusParser.Bool(decision, "deferred"), preview: lane == "hybrid");
            _hybridNote.Text = lane == "cloud"
                ? "Policy only; no model ran. Nothing was sent."
                : "Policy only; no model ran. Choose Run it on this PC to do the " + (lane == "hybrid" ? "redaction" : "local answer") + " on the RTX GPU.";
            Show(_hybridNote, true);
        }
    }

    private void RenderHybridCheck(JsonElement check)
    {
        bool passed = StatusParser.Bool(check, "passed");
        _hybridCheck.Background = passed ? GreenTint : AccentTint;
        _hybridCheckText.Foreground = passed ? GreenInk : AccentDeep;
        _hybridCheckText.Text = (passed ? "\u2713 " : "\u2715 ") + Str(check, "detail");
        Show(_hybridCheck, true);
    }

    private void RenderHybridPayload(JsonElement payload, bool queued, bool preview = false)
    {
        bool withheld = StatusParser.Bool(payload, "withheld");
        string text = Str(payload, "text");
        _hybridPayloadTitle.Text = withheld ? "WITHHELD - NOTHING WOULD BE SENT"
            : queued ? "HELD ON THIS PC - NOT SENT"
            : preview ? "PREVIEW (PATTERN-MASKED) - NOT SENT"
            : "WOULD SEND TO YOUR CLOUD SERVICE - NOT SENT";
        _hybridPayloadTitle.Foreground = withheld ? AccentDeep : Slate;
        _hybridPayload.Background = withheld ? AccentTint : SlateTint;
        _hybridPayloadText.Text = withheld ? "(no payload)" : text;
        string note = Str(payload, "note");
        _hybridPayloadNote.Text = preview
            ? "Preview uses the deterministic pattern mask. Run it on this PC to have the on-device model redact, then re-scan. " + note
            : (Str(payload, "detail") is { Length: > 0 } detail ? detail + " " : "") + note;
        Show(_hybridPayload, true);
    }

    private void UpdateHybridButtons()
    {
        bool hybridRun = _labRunning && _labActivity == HybridActivity;
        bool hasText = _hybridText.Text.Trim().Length > 0;
        string? blocker = LabBlocker(needsModel: false);
        _hybridRoute.IsEnabled = ServiceOnline && hasText && !hybridRun;
        _hybridRun.IsEnabled = blocker is null && hasText;
        _hybridStop.IsEnabled = hybridRun;
        _hybridText.IsReadOnly = hybridRun;
        _hybridOffline.IsEnabled = _hybridBeyond.IsEnabled = !hybridRun;
        _hybridBlocker.Text = hybridRun ? "" : blocker ?? (_status?.Engine.IsReady == true ? ""
            : "The model is not ready yet, so only routes that need no model will run.");
        Show(_hybridBlocker, _hybridBlocker.Text.Length > 0);
    }
}
