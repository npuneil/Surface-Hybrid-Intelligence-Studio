using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        var liveDemos = new JsonArray();
        var liveLab = new JsonObject();
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

            foreach (string tag in new[] { "overview", "live", "demos", "hybrid", "industries", "showoff", "build", "sizing", "platforms", "business", "playbook", "learn" })
            {
                Navigate(tag);
                await Settle();
                Check($"section-{tag}", ReferenceEquals(_nav.Content, _pages[tag]) && FindById(_root, "Page_" + tag) is not null, _currentSection);
                await Shot($"section-{tag}");
            }

            Navigate("overview");
            await Settle();
            Check("overview-hero-stats",
                FindText(_root, "OverviewInstalledMemory") == "128 GB" && FindText(_root, "OverviewGpuCores") == "6,144-core"
                && FindText(_root, "OverviewCpuCores") == "20-core" && FindText(_root, "OverviewDisplay") == "15-inch"
                && FindById(_root, "OverviewVisibleMemory") is null && FindById(_root, "OverviewGpuBudget") is null && FindById(_root, "OverviewLiveModel") is null,
                $"{FindText(_root, "OverviewInstalledMemory")} / {FindText(_root, "OverviewGpuCores")} / {FindText(_root, "OverviewCpuCores")} / {FindText(_root, "OverviewDisplay")}");

            Navigate("live");
            await Settle();
            foreach (string id in new[] { "LiveModelPicker", "LiveLoadModel", "LiveScenarioPicker", "LivePrompt", "LiveSourceExpander", "LiveRunButton", "LiveRehearsalButton", "LiveStopButton", "LiveOutput", "LiveModeBanner", "LiveExportButton" })
                Check("automation-id-" + id, FindById(_root, id) is not null);
            Check("scenarios-loaded", _scenarioKeys.Count >= 2 && _scenarioKeys.Contains("brief") && _scenarioKeys.Contains("code"), string.Join(",", _scenarioKeys));

            // Larger models: every catalog row is in the picker, unavailable ones are disabled and explained.
            var choices = _status?.Engine.Models ?? [];
            Check("models-foundry-and-llama", choices.Any(m => m.Runtime == "Foundry Local") && choices.Any(m => m.IsLlamaCpp),
                string.Join(",", choices.Select(m => $"{m.Alias}:{m.Runtime}")));
            foreach (var m in choices)
                Check("model-picker-" + m.Alias, _modelPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == m.Alias) is { } item
                    && item.IsEnabled == m.Available && (m.Available || !string.IsNullOrWhiteSpace(m.Blocker)),
                    $"available={m.Available} blocker={m.Blocker}");
            if (choices.FirstOrDefault(m => m.Alias == "mai-code-1.1-flash") is { } mai)
                Check("model-mai-honest", mai.Available
                    || (_modelNote.Visibility == Visibility.Visible && _modelNote.Text.Contains("MAI Code 1.1 Flash") && _modelNote.Text.Contains("Not measured by this app")),
                    _modelNote.Text);
            else Check("model-mai-honest", false, "MAI row missing from the catalog");

            // Industry scenarios and the PPTX Offline Generator hand-off.
            Navigate("industries");
            await Settle();
            var industries = _scenarios.Values.Where(s => s.Industry is not null).ToList();
            Check("industry-scenarios-loaded", industries.Count >= 4, string.Join(",", industries.Select(s => s.Key)));
            foreach (var s in industries)
                Check("industry-card-" + s.Key, FindById(_root, "IndustryCard_" + s.Key) is not null
                    && FindById(_root, "IndustryRun_" + s.Key) is Button { IsEnabled: true }
                    && FindById(_root, "IndustryForge_" + s.Key) is Button && FindById(_root, "IndustryExport_" + s.Key) is HyperlinkButton
                    && _scenarioKeys.Contains(s.Key));
            if (industries.FirstOrDefault() is { } first)
            {
                ExportIndustryBrief(first);
                string briefPath = Path.Combine(ExportDirectory, "forge-briefs", "studio_" + first.Key.Replace('-', '_') + ".json");
                var brief = File.Exists(briefPath) ? JsonNode.Parse(File.ReadAllText(briefPath)) : null;
                Check("industry-brief-export", _industryInfo.IsOpen && _industryInfo.Severity == InfoBarSeverity.Success && brief is not null
                    && brief["client"]?.GetValue<string>() == first.Client && brief["pitch_type"]?.GetValue<string>() == "sales"
                    && brief["facts"]?.AsArray().Count > 0 && brief["guardrails"]?.AsArray().Count > 0 && !LeaksToken(briefPath), briefPath);
            }
            await Shot("industries");

            // Hybrid router: every preset routes to its expected lane through the service; nothing is ever sent.
            Navigate("hybrid");
            await LoadHybridCatalogAsync();
            await WaitFor(() => _hybridCatalog is not null && !_hybridLoading, 15);
            await Settle();
            var hybridPresets = _hybridCatalog is { } presetCatalog ? Items(presetCatalog, "presets").ToList() : [];
            var hybridPatterns = _hybridCatalog is { } patternCatalog ? Items(patternCatalog, "patterns").ToList() : [];
            Check("hybrid-catalog-loaded", hybridPresets.Count >= 6 && hybridPatterns.Count >= 7, $"{hybridPresets.Count} presets / {hybridPatterns.Count} patterns");
            foreach (string id in new[] { "HybridInput", "HybridOffline", "HybridBeyond", "HybridRoute", "HybridRun", "HybridStop", "HybridLane", "HybridHeadline", "HybridSteps", "HybridPatterns" })
                Check("automation-id-" + id, FindById(_root, id) is not null);
            foreach (var preset in hybridPresets)
            {
                string id = Str(preset, "id"), expect = Str(preset, "expect");
                bool hasButton = FindById(_root, "HybridPreset_" + id) is Button;
                await ApplyHybridPresetAsync(preset);
                await Settle();
                bool payloadHonest = _hybridPayload.Visibility == Visibility.Collapsed || _hybridPayloadTitle.Text.Contains("NOT");
                Check("hybrid-preset-" + id, hasButton && ReferenceEquals(_hybridLane.Background, LaneBrushes(expect).Background)
                    && _hybridSteps.Children.Count > 0 && _hybridNote.Text.Contains("no model ran") && payloadHonest,
                    $"expected {expect}: {_hybridLaneText.Text} / {_hybridPayloadTitle.Text}");
                if (id == "rates")
                {
                    Check("hybrid-rates-preview-masked", _hybridPayload.Visibility == Visibility.Visible && _hybridPayloadTitle.Text.Contains("NOT SENT")
                        && !_hybridPayloadText.Text.Contains("jonas.becker@example.com"), _hybridPayloadText.Text);
                    await Shot("hybrid-route-rates");
                }
            }
            if (hybridPresets.FirstOrDefault(p => Str(p, "id") == "complaint") is { ValueKind: JsonValueKind.Object } complaint
                && FindById(_root, "HybridPreset_complaint") is Button complaintButton)
            {
                InvokeButton(complaintButton);
                bool routed = await WaitFor(() => _hybridText.Text.ReplaceLineEndings("\n") == Str(complaint, "text") && ReferenceEquals(_hybridLane.Background, LaneBrushes("local").Background), 10);
                Check("hybrid-preset-button-click", routed, _hybridLaneText.Text);
            }
            foreach (var pattern in hybridPatterns)
            {
                string id = Str(pattern, "id");
                bool hasProof = pattern.TryGetProperty("proof", out var proof) && proof.ValueKind == JsonValueKind.Object;
                Check("hybrid-pattern-" + id, FindById(_root, "Pattern_" + id) is not null && (!hasProof || FindById(_root, "PatternProof_" + id) is Button));
            }
            if (FindById(_root, "HybridPatterns") is FrameworkElement patternGallery)
                patternGallery.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.2, AnimationDesired = false });
            await Shot("hybrid-patterns");
            if (hybridPatterns.FirstOrDefault(p => p.TryGetProperty("proof", out var pr) && Str(pr, "kind") == "demo") is { ValueKind: JsonValueKind.Object } demoPattern
                && FindById(_root, "PatternProof_" + Str(demoPattern, "id")) is Button proofButton)
            {
                string target = Str(demoPattern.GetProperty("proof"), "target");
                InvokeButton(proofButton);
                await Settle();
                Check("hybrid-pattern-proof-opens-demo", _currentSection == "demos" && DemoRunnerVisible && _openDemo?.Id == target, $"{_currentSection} {_openDemo?.Id} vs {target}");
                CloseDemo();
            }
            else Check("hybrid-pattern-proof-opens-demo", false, "no pattern links to a live demo");

            // Build on Surface: endpoint off by default, kits and snippets from the service, starter written under exports only.
            Navigate("build");
            await WaitFor(() => _builderState is not null && !_builderLoading, 15);
            await Settle();
            foreach (string id in new[] { "BuildEndpointState", "BuildBaseUrl", "BuildModel", "BuildStartEndpoint", "BuildStopEndpoint", "BuildTestCall", "BuildCopyUrl", "BuildSnippetPicker", "BuildSnippetCode", "BuildKits" })
                Check("automation-id-" + id, FindById(_root, id) is not null);
            var (endpointState, endpointActive) = BuilderEndpoint();
            Check("build-endpoint-off-by-default", endpointActive == "large" || endpointState != "running", $"{endpointActive}:{endpointState}");
            Check("build-loopback-base-url", _buildBaseUrl.Text.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), _buildBaseUrl.Text);
            foreach (string kit in new[] { "chat", "private-docs", "redact-then-cloud", "tool-agent", "csharp-chat" })
                Check("build-kit-" + kit, FindById(_root, "Kit_" + kit) is not null && FindById(_root, "KitCreate_" + kit) is Button && FindText(_root, "KitRun_" + kit) is { Length: > 0 });
            bool modelKnown = _buildModel.Text != "No model loaded yet";
            Check("build-snippets", modelKnown ? _snippets.Count >= 7 && _buildSnippetCode.Text.Contains(_buildBaseUrl.Text.Replace("/v1", ""))
                : _snippets.Count == 0 && _buildSnippetNote.Text.Length > 0, $"{_snippets.Count} snippets; {_buildSnippetNote.Text}");
            _lastStarterPath = null;
            await CreateStarterAsync("redact-then-cloud");
            if (modelKnown)
            {
                string starter = _lastStarterPath ?? "";
                Check("build-starter-written", starter.StartsWith(StarterDirectory, StringComparison.OrdinalIgnoreCase) && _buildInfo.Severity == InfoBarSeverity.Success
                    && File.Exists(Path.Combine(starter, "redact_then_cloud.py")) && File.Exists(Path.Combine(starter, "hybrid.py"))
                    && File.ReadAllText(Path.Combine(starter, "local_ai.py")).Contains(_buildBaseUrl.Text) && !LeaksToken(Path.Combine(starter, "local_ai.py")),
                    starter + " " + _buildInfo.Message);
            }
            else Check("build-starter-needs-model", _lastStarterPath is null && _buildInfo.Severity == InfoBarSeverity.Error, _buildInfo.Message ?? "");
            await Shot("build");
            if (FindById(_root, "BuildKits") is FrameworkElement buildKits)
            {
                buildKits.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.4, AnimationDesired = false });
                await Shot("build-kits");
            }

            // Show it off: tool gallery, run of show, speedometer gating.
            Navigate("showoff");
            await WaitFor(() => _toolStates.Count == Toolkit.Tools.Length && !_toolsLoading, 15);
            await Settle();
            foreach (var tool in Toolkit.Tools)
                Check("tool-card-" + tool.Id, FindById(_root, "Tool_" + tool.Id) is not null && FindById(_root, "ToolStatus_" + tool.Id) is not null
                    && _toolStates.FirstOrDefault(s => s.Tool.Id == tool.Id) is { Actions.Count: > 0 } state
                    && state.Actions.All(a => FindById(_root, $"Tool_{tool.Id}_{a.Id}") is Button), _toolStates.FirstOrDefault(s => s.Tool.Id == tool.Id)?.Status ?? "missing");
            for (int n = 1; n <= 5; n++) Check($"showoff-step-{n}", FindById(_root, $"ShowOffStep_{n}") is Button);
            Check("speed-gated-on-model", _speedRun.IsEnabled == (LabBlocker() is null) && !_speedExport.IsEnabled && _speedRuns.Count == 0, LabBlocker() ?? "ready");
            await Shot("showoff");
            if (FindById(_root, "ToolGallery") is FrameworkElement toolGallery)
            {
                toolGallery.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = false });
                await Shot("showoff-tools");
            }
            if (FindById(_root, "ShowOffStep_1") is Button stepOne) InvokeButton(stepOne);
            bool rates = await WaitFor(() => _currentSection == "hybrid" && _hybridText.Text.Contains("exchange rates") && ReferenceEquals(_hybridLane.Background, LaneBrushes("hybrid").Background), 10);
            Check("showoff-step-1-routes-rates", rates, $"{_currentSection} {_hybridLaneText.Text}");

            foreach (var way in new[] { "hybrid", "showoff", "build" })
            {
                Navigate("overview");
                await Task.Delay(150);
                var wayButton = FindById(_root, "OverviewWay_" + way) as Button;
                if (wayButton is not null) InvokeButton(wayButton);
                bool landed = wayButton is not null && await WaitFor(() => _currentSection == way, 5);
                Check($"overview-way-{way}", landed, $"found={wayButton is not null} section={_currentSection}");
            }

            // One GPU job at a time: a lab job locks the other workspaces, and they lock the lab.
            _labRunning = true;
            _labActivity = SpeedActivity;
            UpdateLiveButtons();
            Check("lab-interlock-locks-others", !_runLive.IsEnabled && !_loadModel.IsEnabled && !_demoRunAll.IsEnabled && !_hybridRun.IsEnabled
                && !_buildStart.IsEnabled && !_buildTest.IsEnabled && !_speedRun.IsEnabled && _speedStop.IsEnabled && _speedBlocker.Text.Length == 0, _hybridBlocker.Text);
            OpenDemo("brief");
            Check("lab-interlock-demo-blocker", !_demoRunLive.IsEnabled && _demoBlocker.Text.Contains(SpeedActivity), _demoBlocker.Text);
            CloseDemo();
            _labRunning = false;
            _labActivity = "";
            _demoRunning = true;
            UpdateLiveButtons();
            Check("lab-locked-by-demo", !_hybridRun.IsEnabled && !_speedRun.IsEnabled && !_buildStart.IsEnabled && _speedBlocker.Text.Contains("live demo"), _speedBlocker.Text);
            _demoRunning = false;
            UpdateLiveButtons();

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
            Check("sales-live-buttons", FindById(_root, "SalesLive_coding") is Button { IsEnabled: true } && FindById(_root, "SalesLive_translation") is Button { IsEnabled: true });
            foreach (var workload in SalesSizing.Workloads.Where(w => w.LiveOnThisLaptop))
            {
                var launch = FindById(_root, "SalesDemo_" + workload.Id) as Button;
                Check("sales-obvious-launch-button-" + workload.Id, launch is
                    {
                        MinHeight: >= 40, HorizontalAlignment: HorizontalAlignment.Stretch,
                        Content: StackPanel { Children.Count: 2 } content
                    } && content.Children[0] is FontIcon
                    && content.Children[1] is TextBlock { Text: "Run live demo" }
                    && launch.Style == (Style)Application.Current.Resources["AccentButtonStyle"]);
            }
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

            // Surface Laptop Ultra configuration planner through the real controls.
            ShowSizingTab(technical: false);
            _ultraTogether.IsOn = false;
            _ultraLong.IsOn = false;
            _ultraBasis.SelectedIndex = 0;
            SetSalesSelection(["coding"]);
            await Settle();
            Check("ultra-coding-48", _lastUltra?.Recommended?.Config.MemoryGB == 48 && _ultraRecommendation.Text == "Recommended: 48 GB Surface Laptop Ultra"
                && FindText(_root, "UltraFit_24") == "Won't fit" && FindText(_root, "UltraFit_32") == "Little room" && FindText(_root, "UltraFit_48") == "Recommended"
                && FindText(_root, "UltraFit_64") == "Plenty of room" && _ultraPillText.Text.StartsWith("RECOMMENDED") && _ultraTalkTrack.Text == _lastUltra.TalkTrack
                && _ultraCopy.IsEnabled && _ultraCustom.Visibility == Visibility.Collapsed,
                $"{_ultraRecommendation.Text} / {FindText(_root, "UltraFit_32")} / {FindText(_root, "UltraFit_48")}");
            Check("ultra-cards-show-split", FindText(_root, "UltraDetail_128")?.StartsWith("GPU about 79.9 GB, Windows about 48.1 GB.") == true
                && _ultraBasisNote.Text.Contains("79.9 GB") && FindById(_root, "UltraSpecLink") is HyperlinkButton, FindText(_root, "UltraDetail_128") ?? "");
            int? thisPc = UltraSizing.ThisPcConfig(_status?.Device);
            Check("ultra-this-pc-marker", thisPc is null || (FindById(_root, $"UltraHere_{thisPc}") is TextBlock { Visibility: Visibility.Visible }
                && UltraSizing.Configurations.Where(c => c.MemoryGB != thisPc).All(c => FindById(_root, $"UltraHere_{c.MemoryGB}") is TextBlock { Visibility: Visibility.Collapsed })),
                $"{thisPc}");
            _ultraBasis.SelectedIndex = 1;
            await Settle();
            bool measurable = UltraSizing.MeasuredShare(_status?.Device) is not null;
            Check("ultra-measured-basis", !measurable || (_lastUltra?.Basis == SplitBasis.MeasuredThisPc && _ultraBasisNote.Text.StartsWith("GPU share: this PC's driver reports")
                && _lastUltra.Configs.Any(c => c.MeasuredHere) == (thisPc is not null)), _ultraBasisNote.Text);
            _ultraBasis.SelectedIndex = 2;
            await Settle();
            Check("ultra-custom-basis", _ultraCustom.Visibility == Visibility.Visible && _lastUltra?.Basis == SplitBasis.Custom
                && Math.Abs(_lastUltra.GpuShare - UltraSizing.DefaultCustomPercent / 100) < 1e-9, $"{_lastUltra?.GpuShare}");
            _ultraBasis.SelectedIndex = 0;
            SetSalesSelection(["frontier"]);
            await Settle();
            Check("ultra-frontier-128", _lastUltra?.Recommended?.Config.MemoryGB == 128 && FindText(_root, "UltraFit_64") == "Won't fit"
                && FindText(_root, "UltraFit_128") == "Recommended" && FindById(_root, "SalesDemo_frontier") is null, _ultraRecommendation.Text);
            SetSalesSelection(["coding", "reasoning"]);
            _ultraTogether.IsOn = true;
            await Settle();
            Check("ultra-together-64", _lastUltra is { Together: true, Recommended.Config.MemoryGB: 64 } && FindText(_root, "UltraFit_48") == "Little room"
                && _ultraTalkTrack.Text.StartsWith("Keeping Local coding assistant and Analysis & reasoning ready at the same time"), _ultraTalkTrack.Text);
            _ultraLong.IsOn = true;
            await Settle();
            Check("ultra-long-together-128", _lastUltra is { LongDocuments: true, Recommended.Config.MemoryGB: 128 } && FindText(_root, "UltraFit_64") == "Little room", _ultraRecommendation.Text);
            if (FindById(_root, "UltraPlanner") is FrameworkElement ultraPlanner)
            {
                ultraPlanner.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false });
                await Shot("sizing-ultra-planner");
            }
            SetSalesSelection(SalesSizing.Workloads.Select(w => w.Id));
            await Settle();
            Check("ultra-none-holds-everything-together", _lastUltra is { Recommended: null, SmallestThatHolds: null } && _ultraPillText.Text.StartsWith("NO CONFIGURATION"), _ultraPillText.Text);
            SetSalesSelection([]);
            await Settle();
            Check("ultra-empty-state", _lastUltra is null && _ultraBody.Visibility == Visibility.Collapsed && !_ultraCopy.IsEnabled
                && _ultraRecommendation.Text.StartsWith("Pick at least one"), _ultraRecommendation.Text);
            _ultraTogether.IsOn = false;
            _ultraLong.IsOn = false;
            SetSalesSelection(SalesSizing.DefaultSelection);
            await Settle();
            Check("ultra-default-coding-48", _lastUltra?.Recommended?.Config.MemoryGB == 48, _ultraRecommendation.Text);

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

            // Live demos tab: registry parity, gallery, every runner, rehearsals, previews, option rules, interlock, hand-offs, export.
            Navigate("demos");
            await Settle();
            for (int i = 0; i < 90 && (_demoCatalog.Demos.Count == 0 || _demoCatalog.MediaState is not ("ready" or "error")); i++)
            {
                await Task.Delay(1000);
                await RefreshDemoCatalogAsync();
            }
            await Settle();
            Check("demos-catalog-matches-app", _demoCatalog.Demos.Select(d => d.Id).SequenceEqual(DemoCatalog.KnownIds), string.Join(",", _demoCatalog.Demos.Select(d => d.Id)));
            Check("demos-media-ready", _demoCatalog.MediaState == "ready", $"{_demoCatalog.MediaState} {_demoCatalog.MediaDetail}");
            foreach (string id in new[] { "DemoReadinessCard", "DemoReadyChat", "DemoReadyModels", "DemoReadyRuntime", "DemoReadyMedia", "DemoReadinessDetail", "DemoRunAll", "DemoGallery" })
                Check("automation-id-" + id, FindById(_root, id) is not null);
            foreach (var demo in _demoCatalog.Demos)
                Check($"demo-card-{demo.Id}", FindById(_root, "DemoCard_" + demo.Id) is not null && FindById(_root, "DemoLaunch_" + demo.Id) is Button { IsEnabled: true }
                    && FindText(_root, "DemoCardStatus_" + demo.Id) is { Length: > 0 } && FindText(_root, "DemoCardModels_" + demo.Id)?.StartsWith("Runs: ") == true);
            await Shot("demos-gallery");

            if (FindById(_root, "DemoLaunch_meeting") is Button launchMeeting) InvokeButton(launchMeeting);
            await Settle();
            Check("demo-launch-opens-runner", DemoRunnerVisible && _openDemo?.Id == "meeting" && _demoTitle.Text == _demoCatalog.Find("meeting")?.Title, _demoTitle.Text);
            foreach (string id in new[] { "DemoBack", "DemoRunnerTitle", "DemoInputHost", "DemoPrompt", "DemoRunLive", "DemoRunRehearsal", "DemoStop", "DemoModeBanner", "DemoSteps", "DemoCopy", "DemoExport" })
                Check("automation-id-" + id, FindById(_root, id) is not null);
            await WaitFor(() => FindById(_root, "DemoPlayAudio") is Button { IsEnabled: true }, 20);
            Check("demo-audio-preview", FindById(_root, "DemoPlayAudio") is Button { IsEnabled: true } && FindText(_root, "DemoAudioInfo")?.Contains("-second recording") == true
                && FindById(_root, "DemoScript") is Expander && File.Exists(Path.Combine(_options.RuntimeDirectory, "demo-media", "meeting.wav")), FindText(_root, "DemoAudioInfo") ?? "");
            await Shot("demo-meeting-runner");
            if (FindById(_root, "DemoBack") is HyperlinkButton back) InvokeButton(back);
            await Settle();
            Check("demo-back-to-gallery", !DemoRunnerVisible && _demoGalleryView.Visibility == Visibility.Visible);

            foreach (var demo in _demoCatalog.Demos.ToArray())
            {
                OpenDemo(demo.Id);
                await Settle();
                bool opened = DemoRunnerVisible && _openDemo?.Id == demo.Id && _demoStepViews.Count == demo.Steps.Count && _demoPrompt.IsReadOnly == !demo.Editable;
                if (!demo.RehearsalFor(SelectedDemoOption))
                {
                    Check($"demo-{demo.Id}-live-only", opened && !_demoRunRehearsal.IsEnabled && _demoRehearsalNote.Text.Contains("live only", StringComparison.OrdinalIgnoreCase), _demoRehearsalNote.Text);
                    continue;
                }
                await RunOpenDemoAsync("rehearsal");
                await Settle();
                var r = _demoResults.GetValueOrDefault(demo.Id);
                Check($"demo-{demo.Id}-rehearsal", opened && r is { Mode: "rehearsal", Error: null, Checks.Count: 0, Completed: true }
                    && r.Steps.All(s => s.Text.Length > 0 && s.State == "done" && s.Metrics.All(m => m.Value.StartsWith("Rehearsal")))
                    && _demoBannerText.Text.StartsWith("REHEARSAL") && _demoSummary.Text.StartsWith("Authored rehearsal") && _demoChecksSection.Visibility == Visibility.Collapsed
                    && _demoStepViews.All(v => v.OutputText.Length > 0) && _demoExport.IsEnabled,
                    r?.Error ?? $"{_demoBannerText.Text} steps={r?.Steps.Count} checks={r?.Checks.Count}");
            }
            await Shot("demo-rehearsal");

            OpenDemo("translate");
            if (_demoOption.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == "ja") is { } japanese) _demoOption.SelectedItem = japanese;
            await Settle();
            Check("demo-translate-option-live-only", SelectedDemoOption == "ja" && !_demoRunRehearsal.IsEnabled && _demoPrompt.Text.Contains("Japanese")
                && _demoRehearsalNote.Text.Contains("runs live only"), _demoPrompt.Text);
            _demoPrompt.Text = "";
            await RunOpenDemoAsync("live");
            Check("demo-empty-instruction-rejected", _demoError.IsOpen && _demoError.Title == "Instruction needs attention" && _demoResults["translate"].Mode == "rehearsal", _demoError.Message ?? "");
            _demoError.IsOpen = false;
            if (FindById(_root, "DemoRestorePrompt") is HyperlinkButton restore) InvokeButton(restore);
            Check("demo-restore-instruction", _demoPrompt.Text == _demoCatalog.Find("translate")?.PromptFor("ja"));

            OpenDemo("vision");
            if (_demoOption.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == "receipt") is { } receipt) _demoOption.SelectedItem = receipt;
            await WaitFor(() => _demoPreviewKey == "receipt.png" && FindById(_root, "DemoInputImage") is Image { Source: not null }, 20);
            Check("demo-vision-receipt-preview", _demoPreviewKey == "receipt.png" && FindById(_root, "DemoInputImage") is Image { Source: not null }
                && FindText(_root, "DemoInputStatus")?.StartsWith("Rendered on this laptop") == true, FindText(_root, "DemoInputStatus") ?? "");
            await RunOpenDemoAsync("rehearsal");
            await Settle();
            Check("demo-vision-receipt-rehearsal", _demoResults.GetValueOrDefault("vision") is { Mode: "rehearsal", Option: "receipt", Error: null }
                && _demoStepViews[0].OutputText.Contains("subtotal"), _demoStepViews.FirstOrDefault()?.OutputText ?? "");
            await Shot("demo-vision-receipt");

            OpenDemo("brief");
            await Settle();
            string? demoExport = ExportDemoRun();
            if (demoExport is not null)
            {
                var json = JsonNode.Parse(File.ReadAllText(demoExport))!;
                Check("demo-rehearsal-export", json["schema"]?.GetValue<string>() == Brand.SchemaPrefix + ".demo-evidence/1" && json["mode"]?.GetValue<string>() == "rehearsal"
                    && json["checks"]?.AsArray().Count == 0 && json["mode_label"]!.GetValue<string>().Contains("not model output") && !LeaksToken(demoExport), demoExport);
            }
            else Check("demo-rehearsal-export", false, "export failed");

            // One inference at a time: each workspace locks the other while it runs.
            _running = true;
            UpdateLiveButtons();
            Check("demo-interlock-live-workspace-busy", !_demoRunLive.IsEnabled && !_demoRunRehearsal.IsEnabled && !_demoRunAll.IsEnabled
                && _demoBlocker.Text.Contains("Live GPU workspace"), _demoBlocker.Text);
            _running = false;
            _demoRunning = true;
            UpdateLiveButtons();
            Check("demo-interlock-demo-busy", !_runLive.IsEnabled && !_runRehearsal.IsEnabled && !_loadModel.IsEnabled && !_modelPicker.IsEnabled);
            _demoRunning = false;
            UpdateLiveButtons();

            CloseDemo();
            SetSalesSelection(["translation"]);
            Navigate("sizing");
            await Settle();
            _running = true;
            if (FindById(_root, "SalesLive_translation") is Button salesTranslate) InvokeButton(salesTranslate);
            await Settle();
            Check("sales-hands-off-to-demo", _currentSection == "demos" && DemoRunnerVisible && _openDemo?.Id == "translate"
                && _demoError.IsOpen && _demoError.Message?.Contains("Live GPU workspace") == true, $"{_currentSection} {_openDemo?.Id}");
            foreach (var workload in SalesSizing.Workloads.Where(w => w.LiveOnThisLaptop))
            {
                Navigate("sizing");
                ShowSizingTab(technical: false);
                await Settle();
                var selection = _salesSelected.ToHashSet(StringComparer.Ordinal);
                var launch = FindById(_root, "SalesDemo_" + workload.Id) as Button;
                if (launch is not null) InvokeButton(launch);
                await Settle();
                Check("sales-badge-launch-" + workload.Id, launch is { IsEnabled: true }
                    && DemoRunnerVisible && _openDemo?.Id == workload.DemoId
                    && _salesSelected.SetEquals(selection) && _demoError.IsOpen
                    && _demoError.Message?.Contains("Live GPU workspace") == true, $"{_currentSection} {_openDemo?.Id}");
            }
            Navigate("sizing");
            Check("sales-guidance-not-launchable", FindById(_root, "SalesDemo_prototyping") is null);
            _running = false;
            UpdateLiveButtons();
            SetSalesSelection(SalesSizing.DefaultSelection);
            CloseDemo();

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

                // Every demo live, through the presenter's own pre-flight button path.
                if (_status?.Engine.IsReady == true)
                {
                    Navigate("demos");
                    CloseDemo();
                    await RefreshDemoCatalogAsync();
                    var blocked = _demoCatalog.Demos.Select(d => _demoCatalog.LiveBlocker(d, _status?.Engine)).OfType<string>().Distinct().ToList();
                    Check("demos-live-ready", blocked.Count == 0 && _demoRunAll.IsEnabled, string.Join(" ", blocked));
                    await RunAllDemosAsync();
                    await Settle();
                    foreach (var demo in _demoCatalog.Demos)
                    {
                        var r = _demoResults.GetValueOrDefault(demo.Id);
                        bool ok = r is { Live: true, Completed: true } && r.Summary!.StepsFailed == 0 && r.Steps.All(s => s.State == "done")
                            && r.Steps.Any(s => s.Text.Length > 0 || s.Metrics.Count > 0);
                        Check($"demo-live-{demo.Id}", ok, r?.Error ?? r?.Outcome ?? "not run");
                        liveDemos.Add(new JsonObject
                        {
                            ["demo"] = demo.Id, ["outcome"] = r?.Outcome, ["error"] = r?.Error, ["total_ms"] = r?.Summary?.TotalMs,
                            ["checks_passed"] = r?.Summary?.ChecksPassed, ["checks_total"] = r?.Summary?.ChecksTotal,
                            ["failed_checks"] = new JsonArray((r?.Checks ?? []).Where(c => !c.Pass).Select(c => (JsonNode?)$"{c.Label}: {c.Value} ({c.Detail})").ToArray()),
                            ["steps"] = new JsonArray((r?.Steps ?? []).Select(s => (JsonNode?)new JsonObject
                            {
                                ["title"] = s.Title, ["model"] = s.Model, ["state"] = s.State, ["error"] = s.Error, ["characters"] = s.Text.Length,
                                ["private_reasoning_characters_withheld"] = s.ReasoningCharacters,
                                ["metrics"] = new JsonArray(s.Metrics.Select(m => (JsonNode?)$"{m.Label}: {m.Value}").ToArray()),
                            }).ToArray()),
                        });
                    }
                    Check("demos-preflight-summary", _demoPreflightSummary.Text.Contains("live demos ran end to end on the GPU") && _demoPreflightSummary.Text.Contains("passed every check")
                        && _demoPreflightList.Children.Count == _demoCatalog.Demos.Count,
                        _demoPreflightSummary.Text);
                    await Shot("demos-preflight");
                    foreach (string id in new[] { "meeting", "voice-memo", "vision", "ask-docs", "redact", "reasoning", "faceoff", "team" })
                    {
                        if (!OpenDemo(id)) continue;
                        await Settle();
                        await Shot($"demo-live-{id}");
                    }
                    OpenDemo("meeting");
                    string? liveDemoExport = ExportDemoRun();
                    var liveDemoJson = liveDemoExport is null ? null : JsonNode.Parse(File.ReadAllText(liveDemoExport));
                    Check("demo-live-export", liveDemoJson?["mode"]?.GetValue<string>() == "live" && liveDemoJson["mode_label"]!.GetValue<string>().StartsWith("LIVE")
                        && liveDemoJson["steps"]?.AsArray().Count > 0 && !LeaksToken(liveDemoExport!), liveDemoExport ?? "export failed");
                    CloseDemo();
                }

                // Show it off, Build on Surface and the Hybrid router on the GPU.
                if (_status?.Engine.IsReady == true)
                {
                    Navigate("showoff");
                    _speedInfo.IsOpen = false;
                    await RunSpeedTestAsync();
                    await Settle();
                    Check("speed-test-live", _lastSpeed is { DecodeTps: > 0, TtftMs: > 0 } && _speedRuns.Count == 1 && FindById(_root, "SpeedBoardRow_1") is not null
                        && !(_speedInfo.IsOpen && _speedInfo.Severity == InfoBarSeverity.Error) && _speedDecodeSource.Text.Length > 0 && _speedExport.IsEnabled,
                        _speedInfo.Message ?? _speedPhase.Text);
                    await Shot("showoff-speed-live");
                    ExportSpeed();
                    string speedPath = _speedInfo.Severity == InfoBarSeverity.Success ? _speedInfo.Message ?? "" : "";
                    var speedJson = File.Exists(speedPath) ? JsonNode.Parse(File.ReadAllText(speedPath)) : null;
                    Check("speed-export", speedJson?["schema"]?.GetValue<string>() == Brand.SchemaPrefix + ".speedtest/1"
                        && speedJson["latest"]?["generation_tokens_per_second"] is not null && !File.ReadAllText(speedPath).Contains(_speedText.Text.Trim() is { Length: > 40 } t ? t[..40] : "\u0000")
                        && !LeaksToken(speedPath), speedPath);
                    liveLab["speed"] = new JsonObject
                    {
                        ["model"] = _lastSpeed?.Model, ["provider"] = _lastSpeed?.Provider, ["decode_tps"] = _lastSpeed?.DecodeTps, ["decode_source"] = _lastSpeed?.DecodeSource,
                        ["ttft_ms"] = _lastSpeed?.TtftMs, ["prefill_tps"] = _lastSpeed?.PrefillTps, ["prefill_source"] = _lastSpeed?.PrefillSource,
                    };

                    Navigate("build");
                    await WaitFor(() => _builderState is not null && !_builderLoading, 15);
                    await RefreshBuilderAsync();
                    if (BuilderEndpoint().Active == "foundry")
                    {
                        await ChangeEndpointAsync("start");
                        Check("build-endpoint-start", BuilderEndpoint().State == "running" && _buildStateText.Text == "RUNNING ON LOOPBACK" && _buildTest.IsEnabled,
                            $"{BuilderEndpoint()} {_buildInfo.Message}");
                    }
                    await TestEndpointAsync();
                    Check("build-endpoint-test-call", _buildTestMeta.Text.StartsWith("HTTP 200") && _buildTestReply.Text.Length > 0 && !_buildTestReply.Text.StartsWith("(")
                        && !_buildTestReply.Text.Contains("<|") && _buildTestRequest.Text.Contains("/chat/completions"), $"{_buildTestMeta.Text} {_buildInfo.Message}");
                    liveLab["endpoint"] = new JsonObject { ["base_url"] = _buildBaseUrl.Text, ["model"] = _buildModel.Text, ["meta"] = _buildTestMeta.Text, ["reply"] = _buildTestReply.Text };
                    await Shot("build-endpoint-live");
                    if (BuilderEndpoint().Active == "foundry")
                    {
                        await ChangeEndpointAsync("stop");
                        Check("build-endpoint-stop", BuilderEndpoint().State == "stopped" && _buildStateText.Text == "OFF" && !_buildTest.IsEnabled, $"{BuilderEndpoint()} {_buildInfo.Message}");
                    }

                    Navigate("hybrid");
                    if (_hybridCatalog is { } liveCatalog && Items(liveCatalog, "presets").FirstOrDefault(p => Str(p, "id") == "rates") is { ValueKind: JsonValueKind.Object } ratesPreset)
                    {
                        await ApplyHybridPresetAsync(ratesPreset);
                        await RunHybridAsync();
                        await Settle();
                        bool clean = _hybridCheckText.Text.StartsWith("\u2713");
                        Check("hybrid-live-rates", _hybridNote.Text.StartsWith("Completed on this PC") && _hybridOutput.Text.Length > 20
                            && _hybridCheck.Visibility == Visibility.Visible && _hybridPayload.Visibility == Visibility.Visible
                            && (clean ? _hybridPayloadTitle.Text.Contains("NOT SENT") && !_hybridPayloadText.Text.Contains("jonas.becker@example.com")
                                      : _hybridPayloadTitle.Text.StartsWith("WITHHELD")),
                            $"{_hybridInfo.Message} | {_hybridCheckText.Text} | {_hybridPayloadTitle.Text}");
                        liveLab["hybrid_rates"] = new JsonObject { ["check"] = _hybridCheckText.Text, ["payload_title"] = _hybridPayloadTitle.Text, ["metrics"] = _hybridMetrics.Text };
                        await Shot("hybrid-live-rates");
                    }
                    else Check("hybrid-live-rates", false, "rates preset missing");
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
            ["live_demos"] = liveDemos,
            ["live_lab"] = liveLab,
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

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(250);
        return condition();
    }

    /// <summary>Clicks through UI Automation, the same path a screen reader or test driver uses.</summary>
    private static void InvokeButton(ButtonBase button)
    {
        if (FrameworkElementAutomationPeer.CreatePeerForElement(button)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
        else throw new InvalidOperationException("Button has no invoke pattern: " + AutomationProperties.GetAutomationId(button));
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
