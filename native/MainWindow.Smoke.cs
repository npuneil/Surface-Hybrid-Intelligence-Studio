using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private async Task RunSmokeAsync()
    {
        var checks = new JsonArray();
        var failures = new List<string>();
        var screenshots = new JsonArray();
        var liveRuns = new JsonArray();
        string screens = Path.Combine(_options.RuntimeDirectory, "screens");
        Directory.CreateDirectory(screens);
        void Check(string name, bool passed, string detail = "")
        {
            checks.Add(new JsonObject { ["name"] = name, ["passed"] = passed, ["detail"] = detail });
            if (!passed) failures.Add($"{name}: {detail}");
        }
        async Task Shot(string name)
        {
            await Settle();
            string? path = await CaptureAsync(Path.Combine(screens, name + ".png"));
            if (path is not null) screenshots.Add(path);
        }

        try
        {
            Check("service-connected", _status is not null, _host.Lifecycle);
            Check("brand-assets-present", File.Exists(AppIdentity.Asset(AppIdentity.IconFile)) && File.Exists(AppIdentity.Asset(AppIdentity.LogoFile)), AppIdentity.Asset(""));
            Check("titlebar-logo", FindById(_root, "TitleBarLogo") is Image { Source: not null });
            Check("brand-mark", FindById(_root, "NavMark") is TextBlock { Text: Brand.Mark } && Title == Brand.WindowTitle, Title);

            foreach (string tag in new[] { "overview", "live", "sizing", "platforms", "business", "playbook", "learn" })
            {
                Navigate(tag);
                await Settle();
                Check($"section-{tag}", ReferenceEquals(_nav.Content, _pages[tag]) && FindById(_root, "Page_" + tag) is not null, _currentSection);
                await Shot($"section-{tag}");
            }

            Navigate("overview");
            await Settle();
            Check("overview-memory-views-distinct",
                FindText(_root, "OverviewInstalledMemory") == "128 GB" && FindText(_root, "OverviewVisibleMemory") == "64 GB"
                && FindText(_root, "OverviewGpuBudget") == "62.5 GiB",
                $"{FindText(_root, "OverviewInstalledMemory")} / {FindText(_root, "OverviewVisibleMemory")} / {FindText(_root, "OverviewGpuBudget")}");

            Navigate("live");
            await Settle();
            foreach (string id in new[] { "LiveModelPicker", "LiveLoadModel", "LiveScenarioPicker", "LivePrompt", "LiveSourceExpander", "LiveRunButton", "LiveRehearsalButton", "LiveStopButton", "LiveOutput", "LiveModeBanner", "LiveExportButton" })
                Check("automation-id-" + id, FindById(_root, id) is not null);
            Check("scenarios-loaded", _scenarioKeys.Count >= 2 && _scenarioKeys.Contains("brief") && _scenarioKeys.Contains("code"), string.Join(",", _scenarioKeys));

            // Sales sizing view (default tab) through the real check boxes.
            Navigate("sizing");
            await Settle();
            Check("sizing-default-tab-sales", !SizingTechnicalVisible && FindById(_root, "SizingSalesView") is FrameworkElement { Visibility: Visibility.Visible });
            SetSalesSelection([]);
            await Settle();
            Check("sales-empty-state", _lastSales is null && _salesRecommendation.Text.StartsWith("Pick at least one") && !_salesTechnical.IsEnabled, _salesRecommendation.Text);
            foreach (var (id, expected) in new[] { ("transcription", TShirtSize.S), ("translation", TShirtSize.M), ("coding", TShirtSize.L), ("prototyping", TShirtSize.XL) })
            {
                SetSalesSelection([]);
                _salesWorkloads[id].Box.IsChecked = true;
                await Settle();
                Check($"sales-{id}-{expected}", _lastSales?.Profile.Size == expected && _salesRecommendation.Text.Contains($"({expected})")
                    && _lastSales.ThisLaptop.Fit == SalesFit.Comfortable && _salesVerdictText.Text.StartsWith("COMFORTABLE"), _salesRecommendation.Text + " / " + _salesVerdictText.Text);
            }
            SetSalesSelection(["transcription", "translation", "coding"]);
            await Settle();
            Check("sales-multi-select-largest-wins", _lastSales?.Profile.Size == TShirtSize.L && _salesRows.Children.Count == 3 && _salesSelectionCount.Text == "3 selected", $"{_lastSales?.Profile.Size} rows={_salesRows.Children.Count}");
            Check("sales-device-compare", FindText(_root, "SalesDeviceFit_this") == "Comfortable" && FindText(_root, "SalesDeviceFit_laptop16") == "Won't fit"
                && FindText(_root, "SalesDeviceFit_laptop32") == "Comfortable", $"{FindText(_root, "SalesDeviceFit_this")} / {FindText(_root, "SalesDeviceFit_laptop32")} / {FindText(_root, "SalesDeviceFit_laptop16")}");
            Check("sales-talk-track-shown", _salesTalkTrack.Text == _lastSales?.TalkTrack && _salesTalkTrack.Text.Contains("Surface Laptop Ultra stands out"), _salesTalkTrack.Text);
            Check("sales-live-buttons", FindById(_root, "SalesLive_coding") is Button { IsEnabled: true } && FindById(_root, "SalesLive_translation") is null);
            await Shot("sizing-sales-view");
            if (FindById(_root, "SalesResults") is FrameworkElement salesResults)
            {
                salesResults.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
                await Shot("sizing-sales-results");
            }
            double salesRequired = _lastSales?.ThisLaptop.Result?.RequiredGiB ?? double.NaN;
            OpenTechnicalFromSales();
            await Settle();
            Check("sales-hands-off-to-technical", SizingTechnicalVisible && _sizePicker.SelectedIndex == 2 && Math.Abs((_lastSizing?.RequiredGiB ?? 0) - salesRequired) < 1e-9,
                $"visible={SizingTechnicalVisible} size={_sizePicker.SelectedIndex} {_lastSizing?.RequiredGiB} vs {salesRequired}");
            SetSalesSelection(SalesSizing.DefaultSelection);

            // T-shirt sizing (technical view) through the real controls.
            Navigate("sizing");
            ShowSizingTab(technical: true);
            await Settle();
            ResetSizing();
            _budgetSource.SelectedIndex = 0;
            _sizePicker.SelectedIndex = 2;
            _bits.SelectedIndex = 0;
            _params.Value = 14;
            await Settle();
            Check("sizing-14b-4bit-weights", _breakdown["weights"].Text == "6.52 GiB", _breakdown["weights"].Text);
            Check("sizing-14b-fits-gpu-budget", _lastSizing?.FitsEstimate == true && _verdictText.Text.StartsWith("FITS"), _verdictText.Text);
            Check("sizing-gpu-budget-not-summed", Math.Abs((_lastSizingInput?.BudgetGiB ?? 0) - 62.5) < 0.01, $"{_lastSizingInput?.BudgetGiB}");
            _budgetSource.SelectedIndex = 1;
            await Settle();
            Check("sizing-windows-view-not-summed", Math.Abs((_lastSizingInput?.BudgetGiB ?? 0) - (_status?.Device.OsVisibleGiB ?? -1)) < 0.01, $"{_lastSizingInput?.BudgetGiB}");
            _sizePicker.SelectedIndex = 3;
            _bits.SelectedIndex = 2;
            _budgetSource.SelectedIndex = 2;
            _budget.Value = 32;
            await Settle();
            Check("sizing-xl-16bit-exceeds-32gib", _lastSizing?.Verdict == SizingVerdict.DoesNotFit && _verdictText.Text.StartsWith("DOES NOT FIT"), _verdictText.Text);
            await Shot("sizing-xl-does-not-fit");
            _params.Value = double.NaN;
            await Settle();
            Check("sizing-empty-parameters-invalid", _verdictText.Text == "INVALID INPUT" && _sizingSummary.Text.Contains("Parameter"), _sizingSummary.Text);
            _params.Value = 32;
            _budget.Value = 0;
            await Settle();
            Check("sizing-zero-budget-invalid", _lastSizing?.Verdict == SizingVerdict.Invalid, _sizingSummary.Text);
            _budget.Value = 32;
            _reserve.Value = 32;
            await Settle();
            Check("sizing-reserve-consumes-budget-invalid", _lastSizing?.Verdict == SizingVerdict.Invalid, _sizingSummary.Text);
            ResetSizing();
            _headroom.Value = 0;
            await Settle();
            Check("sizing-zero-headroom-valid", _lastSizing?.IsValid == true, _sizingSummary.Text);
            ResetSizing();
            await Settle();
            Check("sizing-observed-metadata-shown", _observed.Children.Count >= 2);
            ShowSizingTab(technical: false);

            Navigate("platforms");
            await Settle();
            foreach (var p in PlatformGuide.Platforms)
            {
                Check($"platform-card-{p.Id}", FindById(_root, $"PlatformCard_{p.Id}") is not null);
                for (int i = 0; i < p.Sources.Length; i++)
                    Check($"platform-source-{p.Id}-{i}", FindById(_root, $"PlatformSource_{p.Id}_{i}") is not null && ApprovedLinks.IsApproved(p.Sources[i].Url), p.Sources[i].Url);
            }
            foreach (string key in new[] { "field", "meeting" })
                if (_scenarios.ContainsKey(key)) Check($"platform-try-{key}-enabled", _platformTry.TryGetValue(key, out var b) && b.IsEnabled);

            Navigate("business");
            await Settle();
            foreach (var (key, box) in _businessInputs) box.Value = BusinessCase.Defaults[key];
            bool calculated = await CalculateBusinessAsync();
            await Settle();
            Check("business-calculated-by-service", calculated && _bHours.Text == "2,640" && _bCapacity.Text == "$158,400" && _bInvestment.Text == "$60,000",
                $"{_bHours.Text} / {_bCapacity.Text} / {_bInvestment.Text}");
            Check("business-disclaimer-shown", _bDisclaimer.Text.Contains("not cash savings"), _bDisclaimer.Text);
            string? businessPath = ExportBusiness();
            Check("business-export", businessPath is not null && File.Exists(businessPath) && !LeaksToken(businessPath), businessPath ?? "");
            await Shot("business-calculated");
            _businessInputs["users"].Value = double.NaN;
            bool rejected = !await CalculateBusinessAsync();
            Check("business-invalid-rejected", rejected && _businessError.IsOpen, _businessError.Message);
            _businessInputs["users"].Value = BusinessCase.Defaults["users"];
            _businessError.IsOpen = false;

            // Coding as a second language: optional onboarding tab, quiz, persistence, tutor hand-off, hide/show.
            Navigate("learn");
            await Settle();
            Check("learn-tab-visible-by-default", LearnTabVisible && _learnToggle.IsOn);
            Check("learn-lessons-listed", CodingTrack.Lessons.All(l => FindById(_root, "LessonNav_" + l.Id) is Button), $"{_lessonButtons.Count}");
            Check("learn-progress-starts-empty", _learning.CompletedCount == 0 && _learnProgress.Value == 0 && _learnProgressText.Text.StartsWith("0 of 8"), _learnProgressText.Text);
            var functions = CodingTrack.Find("functions")!;
            ShowLesson(functions);
            await Settle();
            Check("learn-lesson-rendered", _lessonTitle.Text == functions.Title && _lessonCode.Text == functions.Code && _lessonNotes.Children.Count == functions.Notes.Length
                && _quizOptions.Items.Count == 3 && _lessonDone.Visibility == Visibility.Collapsed, _lessonTitle.Text);
            Check("learn-quiz-needs-selection", !CheckAnswer() && _quizFeedback.Severity == InfoBarSeverity.Informational);
            int wrong = Enumerable.Range(0, 3).First(d => (int)((RadioButton)_quizOptions.Items[d]).Tag != functions.Quiz.Correct);
            int right = Enumerable.Range(0, 3).First(d => (int)((RadioButton)_quizOptions.Items[d]).Tag == functions.Quiz.Correct);
            _quizOptions.SelectedIndex = wrong;
            Check("learn-wrong-answer-not-completed", !CheckAnswer() && _quizFeedback.Severity == InfoBarSeverity.Warning && !_learning.Completed.Contains("functions"));
            _quizOptions.SelectedIndex = right;
            Check("learn-right-answer-completes", CheckAnswer() && _quizFeedback.Severity == InfoBarSeverity.Success && _learning.CompletedCount == 1
                && _learnProgress.Value == 1 && _lessonDone.Visibility == Visibility.Visible, _learnProgressText.Text);
            var stored = LearningState.Load(LearningStatePath);
            Check("learn-progress-persisted", stored.Completed.SetEquals(["functions"]) && stored.LastLesson == "functions" && stored.ShowTab, LearningStatePath);
            await Shot("learn-lesson-complete");
            var aiWords = CodingTrack.Find("ai-words")!;
            ShowLesson(aiWords);
            await Settle();
            Check("learn-sizing-link-only-where-relevant", _lessonSizing.Visibility == Visibility.Visible);
            OpenSizingFromLesson();
            await Settle();
            Check("learn-sizing-link-matches-engine", _currentSection == "sizing" && SizingTechnicalVisible && _breakdown["weights"].Text == "6.52 GiB", _breakdown["weights"].Text);
            ShowSizingTab(technical: false);
            Navigate("learn");
            ShowLesson(functions);
            await Settle();
            if (_scenarios.TryGetValue(CodingTrack.TutorScenario, out var tutor))
            {
                Check("learn-tutor-prompt-parity", tutor.Prompt == CodingTrack.TutorPrompt(functions), "service tutor prompt differs from the lesson hand-off");
                Check("learn-tutor-enabled", _lessonTutor.IsEnabled);
                bool handedOff = AskTutor();
                await Settle();
                Check("learn-tutor-hand-off", handedOff && _currentSection == "live" && SelectedScenario?.Key == CodingTrack.TutorScenario && PromptText == CodingTrack.TutorPrompt(functions),
                    $"{_currentSection} {SelectedScenario?.Key}");
                var nextLesson = CodingTrack.Find("ai-code")!;
                Navigate("learn");
                ShowLesson(nextLesson);
                AskTutor();
                await Settle();
                Check("learn-tutor-hand-off-other-lesson", PromptText == CodingTrack.TutorPrompt(nextLesson) && PromptText.Contains("prioritize_tickets"));
                SelectScenario(_scenarioKeys[0]);
            }
            else Check("learn-tutor-scenario-available", false, string.Join(",", _scenarioKeys));
            Navigate("learn");
            await Settle();
            SetLearnTabVisible(false);
            await Settle();
            Check("learn-hide-tab", !LearnTabVisible && _currentSection == "playbook" && !_learnToggle.IsOn && !LearningState.Load(LearningStatePath).ShowTab, _currentSection);
            if (FindById(_root, "PlaybookLearnCard") is FrameworkElement learnCard)
            {
                learnCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
                await Shot("playbook-learn-toggle");
            }
            _learnToggle.IsOn = true;
            await Settle();
            Check("learn-show-tab-from-playbook", LearnTabVisible && LearningState.Load(LearningStatePath).ShowTab && LearningState.Load(LearningStatePath).Completed.Contains("functions"));

            Navigate("live");
            await Settle();
            foreach (string key in _scenarioKeys.ToArray())
            {
                SelectScenario(key);
                await Settle();
                await RunScenarioAsync("rehearsal");
                Check($"rehearsal-{key}", _lastRun is { Mode: "rehearsal", Error: null } && _output.Text.Length > 20
                    && _modeText.Text.StartsWith("REHEARSAL") && _mTtft.Text == "-", _lastRun?.Error ?? _modeText.Text);
            }
            SelectScenario("brief");
            await RunScenarioAsync("rehearsal");
            string? rehearsalExport = ExportRun();
            if (rehearsalExport is not null)
            {
                var json = JsonNode.Parse(File.ReadAllText(rehearsalExport))!;
                Check("rehearsal-export-no-metrics", json["mode"]?.GetValue<string>() == "rehearsal" && json["metrics"] is null && !LeaksToken(rehearsalExport), rehearsalExport);
            }
            else Check("rehearsal-export-no-metrics", false, "export failed");
            await Shot("live-rehearsal");

            if (_options.SmokeLive)
            {
                var deadline = DateTime.UtcNow.AddMinutes(10);
                while (_status?.Engine.IsReady != true && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(2000);
                    await RefreshStatusAsync();
                }
                Check("live-engine-ready", _status?.Engine.IsReady == true, _status?.Engine.Detail ?? "");
                foreach (string key in _scenarioKeys.ToArray())
                {
                    if (_status?.Engine.IsReady != true) break;
                    SelectScenario(key);
                    await Settle();
                    await RunScenarioAsync("live");
                    var m = _lastRun?.Metrics;
                    bool ok = _lastRun is { Mode: "live", Error: null } && m is not null && m.Mode == "live"
                        && m.Provider == "CUDAExecutionProvider" && _lastTokenEvents >= 2 && _output.Text.Length > 40
                        && m.TtftMs > 0 && _modeText.Text.StartsWith("LIVE") && _mTtft.Text != "-";
                    Check($"live-{key}", ok, _lastRun?.Error ?? $"events={_lastTokenEvents} chars={_output.Text.Length} provider={m?.Provider}");
                    liveRuns.Add(new JsonObject
                    {
                        ["scenario"] = key, ["model"] = m?.ModelId, ["provider"] = m?.Provider, ["ttft_ms"] = m?.TtftMs,
                        ["total_ms"] = m?.TotalMs, ["output_tokens"] = m?.OutputTokens, ["tokens_per_second"] = m?.TokensPerSecond,
                        ["token_events"] = _lastTokenEvents, ["visible_characters"] = _output.Text.Length,
                        ["private_reasoning_characters_withheld"] = _lastRun?.ReasoningCharactersWithheld, ["error"] = _lastRun?.Error,
                    });
                    await Shot($"live-{key}");
                    if (key == _scenarioKeys[0])
                    {
                        string? liveExport = ExportRun();
                        bool exported = liveExport is not null && JsonNode.Parse(File.ReadAllText(liveExport))?["metrics"]?["execution_provider"]?.GetValue<string>() == "CUDAExecutionProvider";
                        Check("live-export-provenance", exported && !LeaksToken(liveExport!), liveExport ?? "");
                    }
                }
            }

            Navigate("overview");
            await Shot("overview-final");
        }
        catch (Exception exception)
        {
            failures.Add("exception: " + exception);
        }

        var report = new JsonObject
        {
            ["passed"] = failures.Count == 0,
            ["completed_at"] = DateTimeOffset.Now.ToString("O"),
            ["live_inference_checked"] = _options.SmokeLive,
            ["service_lifecycle"] = _host.Lifecycle,
            ["engine"] = _status?.Engine.Detail,
            ["checks"] = checks,
            ["failures"] = new JsonArray(failures.Select(f => (JsonNode?)f).ToArray()),
            ["live_runs"] = liveRuns,
            ["screenshots"] = screenshots,
        };
        Directory.CreateDirectory(_options.RuntimeDirectory);
        await File.WriteAllTextAsync(Path.Combine(_options.RuntimeDirectory, "native-smoke.json"), report.ToJsonString(Evidence.Options));
        Program.ExitCode = failures.Count == 0 ? 0 : 1;
        if (_options.ExitAfterSmoke) Close();
    }

    private bool LeaksToken(string path)
    {
        string text = File.ReadAllText(path);
        return (_client.CurrentToken is string token && text.Contains(token, StringComparison.Ordinal)) || text.Contains("X-Showcase-Token", StringComparison.OrdinalIgnoreCase);
    }

    private async Task Settle()
    {
        _root.UpdateLayout();
        await Task.Delay(350);
    }

    private async Task<string?> CaptureAsync(string path)
    {
        try
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(_root);
            var pixels = await bitmap.GetPixelsAsync();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            stream.Seek(0);
            await using var file = File.Create(path);
            await stream.AsStreamForRead().CopyToAsync(file);
            return path;
        }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            return null;
        }
    }

    private static DependencyObject? FindById(DependencyObject root, string id)
    {
        if (AutomationProperties.GetAutomationId(root) == id) return root;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (FindById(VisualTreeHelper.GetChild(root, i), id) is { } found) return found;
        return null;
    }

    private static string? FindText(DependencyObject root, string id) => (FindById(root, id) as TextBlock)?.Text;
}
