using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Harness;
using Forge.Core.Ir;
using Forge.Core.Spec;
using Forge.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SurfaceDeviceKit;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OfflinePptxForge;

// --smoke and --smoke-live: the app drives itself the way a screen reader would (UI Automation, never key presses), writes a JSON report,
// and with --capture saves pictures of what it drew. --smoke-live also makes real decks with the local model.
internal sealed partial class MainWindow
{
    private sealed class SmokeLog(string? captureDir)
    {
        public JsonArray Checks { get; } = [];
        public JsonArray Screens { get; } = [];
        public List<string> Failures { get; } = [];
        public string? CaptureDir { get; } = captureDir;

        public void Check(string name, bool ok, string detail = "")
        {
            Checks.Add(new JsonObject { ["name"] = name, ["passed"] = ok, ["detail"] = detail });
            if (!ok) Failures.Add(detail.Length > 0 ? $"{name}: {detail}" : name);
        }
    }

    private async Task RunSmokeAsync()
    {
        var log = new SmokeLog(_options.Capture);
        long errorsBefore = ErrorLogLength();
        try
        {
            await SmokeStaticAsync(log);
            if (_options.SmokeLive) await SmokeLiveAsync(log);
        }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            log.Check("the smoke run finished without an exception", false, exception.Message);
        }
        log.Check("no unhandled error was logged", ErrorLogLength() == errorsBefore, "see app-errors.log in the data folder");
        await WriteSmokeReportAsync(log);
        Program.ExitCode = log.Failures.Count == 0 ? 0 : 1;
        Close();
    }

    private long ErrorLogLength()
    {
        try
        {
            var file = new FileInfo(Path.Combine(_options.DataDir, "app-errors.log"));
            return file.Exists ? file.Length : 0;
        }
        catch (IOException) { return 0; }
    }

    private async Task WriteSmokeReportAsync(SmokeLog log)
    {
        var real = _devices.Detected.Profile;
        var report = new JsonObject
        {
            ["passed"] = log.Failures.Count == 0,
            ["completed_at"] = DateTimeOffset.Now.ToString("O"),
            ["live"] = _options.SmokeLive,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["device"] = real.Headline,
            ["pc_class"] = $"{real.PcClassLabel} · {real.DeviceClassLabel}",
            ["engine"] = _engineText.Text,
            ["checks"] = log.Checks,
            ["failures"] = new JsonArray(log.Failures.Select(f => (JsonNode?)f).ToArray()),
            ["screenshots"] = log.Screens,
        };
        string path = Path.GetFullPath(_options.Report ?? Path.Combine(_options.DataDir, "smoke-report.json"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Program.LogError(_options, exception);
        }
    }

    // ---- Everything that does not need a model to run ----

    private async Task SmokeStaticAsync(SmokeLog log)
    {
        void Check(string name, bool ok, string detail = "") => log.Check(name, ok, detail);
        var real = _devices.Detected.Profile;
        bool engine = _plan is not null;
        var options = _plan?.Models.Options ?? [];

        Check("the title bar names this PC", _chipText.Text == real.Headline, _chipText.Text);
        Check("local AI started", engine, engine ? _engineText.Text : _engineProblem);
        DependencyObject[] interactive =
        [
            _buildButton, _stopButton, _draftButton, _openButton, _folderButton, _modelCombo, _emulateCheck, _briefTabs, _briefCombo, _clientBox,
            _pitchCombo, _requestBox, _provenanceCheck, _modeCombo, _repairCombo, _guidanceCombo, _previewsCheck, _simulateBox, _sections,
        ];
        var unnamed = interactive.Where(c => string.IsNullOrEmpty(AutomationProperties.GetAutomationId(c))).Select(c => c.GetType().Name).ToList();
        Check("every control has an automation id", unnamed.Count == 0, string.Join(", ", unnamed));

        // ---- Make a deck ----
        ShowSection("deck");
        await Settle();
        Check("the model list matches what Foundry Local has for this PC", _modelCombo.Items.Count == options.Count, $"{_modelCombo.Items.Count} shown, {options.Count} available");
        if (engine)
        {
            Check("the recommended model is selected", SelectedModel()?.VariantId == _plan!.Default?.VariantId, SelectedModel()?.Title ?? "none");
            Check("the model note says what is known about its speed", _modelNote.Text.Length > 20, _modelNote.Text);
            foreach (string tile in new[] { "model", "speed", "eta", "memory" })
                Check($"the '{tile}' expectation tile is shown", FindById(_root, "Kpi_" + tile) is not null);
        }
        Check("the pre-staged briefs are listed", _briefByTag.Count >= BundledBriefs.All.Count, $"{_briefByTag.Count} briefs");
        Check("the brief card lists the facts the deck can use", FindById(_root, "BriefFacts") is not null);
        Check("Make the deck is ready when a model and a brief are chosen", _buildButton.IsEnabled == engine, $"enabled={_buildButton.IsEnabled}");
        Check("the status line says what Make the deck will do", !engine || _status.Text.StartsWith("Ready.", StringComparison.Ordinal), _status.Text);
        bool thumbnails = await WaitFor(() => _templateImages.Count == Theme.All.Count && _templateImages.Values.All(i => i.Source is not null), 60);
        Check("every look has a thumbnail", thumbnails, $"{_templateImages.Values.Count(i => i.Source is not null)} of {_templateImages.Count}");

        string originalLook = _template;
        foreach (var theme in Theme.All)
        {
            if (FindById(_root, "Template_" + theme.Key) is Button button)
            {
                InvokeButton(button);
                await Settle();
                Check($"choosing the {theme.Label} look selects it", _template == theme.Key, _template);
            }
            else Check($"the {theme.Label} look has a button", false);
        }
        if (FindById(_root, "Template_" + originalLook) is Button restore) InvokeButton(restore);

        if (FindById(_root, "SettingsExpander") is Expander settings) settings.IsExpanded = true;
        await Settle();
        if (engine)
        {
            Check("the settings say what Automatic will do", _policyText.Text.StartsWith("With these settings:", StringComparison.Ordinal), _policyText.Text);
            string automatic = _policyText.Text;
            SelectTag(_modeCombo, "Arc");
            Check("choosing a writing style is reflected in the plan", _policyText.Text != automatic && _policyText.Text.Contains("whole story in one reply", StringComparison.OrdinalIgnoreCase), _policyText.Text);
            SelectTag(_modeCombo, "");
        }
        await ShotScrolledAsync(log, "deck", _leftScroll);
        if (FindById(_root, "SettingsExpander") is Expander closing) closing.IsExpanded = false;

        // Writing your own brief
        _briefTabs.SelectedItem = _briefTabs.Items[1];
        await Settle();
        _requestBox.Text = "Too short";
        await Settle();
        Check("a request under the minimum cannot be drafted or made", !_draftButton.IsEnabled && !_buildButton.IsEnabled, _requestHint.Text);
        _requestBox.Text = "A short sales pitch for a bakery that wants faster invoicing.";
        await Settle();
        Check("a long enough request can be drafted and made", _draftButton.IsEnabled == engine && _buildButton.IsEnabled == engine, _requestHint.Text);
        Check("the hint says the request is ready", _requestHint.Text.StartsWith("Ready", StringComparison.Ordinal), _requestHint.Text);
        await ShotScrolledAsync(log, "deck-write", _leftScroll);
        _requestBox.Text = "";
        _briefTabs.SelectedItem = _briefTabs.Items[0];
        await Settle();
        Check("the pre-staged briefs work again after writing", PickedBrief() is not null && _buildButton.IsEnabled == engine, _pickedTag);

        // A brief another app leaves in the inbox
        string inbox = Path.Combine(_options.DataDir, "inbox");
        string handoff = Path.Combine(inbox, "smoke_handoff.json");
        try
        {
            Directory.CreateDirectory(inbox);
            var brief = Brief.FromJson(BundledBriefs.All[0].ToJson());
            brief.Id = "smoke_handoff";
            brief.Client = "Hand-off Test Co";
            await File.WriteAllTextAsync(handoff, brief.ToJson().ToJsonString());
            ShowSection("pc");
            RescanInbox();
            await Settle();
            Check("a brief left in the inbox is picked up", _pickedTag.StartsWith("inbox:", StringComparison.Ordinal) && _pickedTag.EndsWith("smoke_handoff.json", StringComparison.OrdinalIgnoreCase) && PickedBrief()?.Client == "Hand-off Test Co", _pickedTag);
            Check("the page switches to the deck and says a brief arrived", _section == "deck" && _notice.IsOpen && _notice.Title == "Brief received", $"{_section} / {_notice.Title}");
        }
        finally
        {
            try { File.Delete(handoff); }
            catch (IOException) { }
        }
        ClearNotice();
        RefillBriefs("bundled:" + BundledBriefs.All[0].Id);
        RefreshBriefSummary();

        // Narrow windows stack the page. The window is really resized, so the page's own size handling is what gets tested.
        var wideSize = AppWindow.Size;
        await ResizeAsync(640, 820);
        bool narrow = await WaitFor(() => _wide == false, 10);
        Check("a narrow window stacks the page in one scroller", narrow && _deckPage.Children.Contains(_narrowScroll) && !_deckPage.Children.Contains(_leftScroll), $"page is {_deckPage.ActualWidth:0} wide");
        Check("the Make button stays reachable in a narrow window", _buildButton.ActualWidth > 0 && FindById(_root, "BuildButton") is not null);
        await ShotScrolledAsync(log, "deck-narrow", _narrowScroll);
        ShowSection("pc");
        await ShotScrolledAsync(log, "pc-narrow", (ScrollViewer)_pages["pc"], 2);
        ShowSection("deck");
        AppWindow.Resize(wideSize);
        bool wideAgain = await WaitFor(() => _wide == true, 10);
        await Settle();
        Check("a wide window puts the choices beside the result", wideAgain && _deckPage.Children.Contains(_leftScroll) && _deckPage.Children.Contains(_rightScroll), $"page is {_deckPage.ActualWidth:0} wide");

        // ---- This PC ----
        ShowSection("pc");
        await Settle();
        Check("the page names the detected PC", _deviceTitle.Text == real.Headline, _deviceTitle.Text);
        Check("the page shows its class", _deviceSub.Text.Contains(real.PcClassLabel, StringComparison.Ordinal), _deviceSub.Text);
        Check("every lane is shown", CountIds(_root, "Lane_") == real.Lanes.Count, $"{CountIds(_root, "Lane_")} of {real.Lanes.Count}");
        Check("every capability is listed with its status", CountIds(_root, "Capability_") == real.Capabilities.Count && CountIds(_root, "Status_") == real.Capabilities.Count,
            $"{CountIds(_root, "Capability_")} of {real.Capabilities.Count}");
        Check("a detected PC is not marked as simulated", !_simulationBar.IsOpen && !_devices.IsSimulated);
        string wantPackage = SiliconPackage.For(real);
        string packageLine = (FindById(_root, "PackageLine") as TextBlock)?.Text ?? "";
        Check("the package line names this copy and the package this PC takes", packageLine.Contains(ForgePackage.Label, StringComparison.Ordinal)
            && (packageLine.Contains("the package for this PC", StringComparison.Ordinal) == ForgePackage.Fits(real))
            && (packageLine.Contains("the package for this PC", StringComparison.Ordinal) || packageLine.Contains(SiliconPackage.Describe(wantPackage), StringComparison.Ordinal)), packageLine);
        Check("the package banner shows only when another package fits this PC", _packageBar.IsOpen == (ForgePackage.Mismatch(real) is not null),
            _packageBar.IsOpen ? _packageBar.Title : "closed");
        await ShotScrolledAsync(log, "pc", (ScrollViewer)_pages["pc"]);

        var ids = new List<string>();
        foreach (string wanted in new[] { "snapdragon-x2-plus-16", "snapdragon-x-plus-16" })
            if (SimulatedProfiles.Find(wanted) is not null) ids.Add(wanted);
        foreach (string group in new[] { "intel", "nvidia" })
            if (_devices.SimulatedDevices.FirstOrDefault(d => d.Group == group) is { } first) ids.Add(first.Id);
        if (_devices.SimulatedDevices.FirstOrDefault(d => d.Group is not ("snapdragon" or "intel" or "nvidia")) is { } other) ids.Add(other.Id);

        bool shot = false;
        foreach (string id in ids)
        {
            await SimulateAsync(id);
            var shown = _devices.Effective.Profile;
            Check($"{id}: the page shows the simulated device", _devices.IsSimulated && shown.SimulatedId == id && _deviceTitle.Text == shown.Headline && _simulationBar.IsOpen, _deviceTitle.Text);
            Check($"{id}: the title bar still names this PC", _chipText.Text == real.Headline, _chipText.Text);
            Check($"{id}: lanes and capabilities are redrawn", CountIds(_root, "Lane_") == shown.Lanes.Count && CountIds(_root, "Capability_") == shown.Capabilities.Count,
                $"{CountIds(_root, "Lane_")} lanes, {CountIds(_root, "Capability_")} capabilities");
            string shownPackage = (FindById(_root, "PackageLine") as TextBlock)?.Text ?? "";
            Check($"{id}: the package line names {SiliconPackage.For(shown)}", shownPackage.Contains(SiliconPackage.Describe(SiliconPackage.For(shown)), StringComparison.Ordinal), shownPackage);
            if (engine) Check($"{id}: the models this PC can run are not changed", _modelCombo.Items.Count == options.Count, $"{_modelCombo.Items.Count} of {options.Count}");
            if (id.StartsWith("snapdragon", StringComparison.Ordinal) && _devices.Effective.Plans.TryGetValue(ForgeSession.Workload, out var plan))
                Check($"{id}: the plan puts the NPU first", plan.PrimaryLane == "npu", plan.Summary);
            if (!shot && id.StartsWith("snapdragon", StringComparison.Ordinal))
            {
                shot = true;
                await ShotScrolledAsync(log, "pc-simulated-" + id, (ScrollViewer)_pages["pc"]);
            }
        }

        if (engine && ids.Contains("snapdragon-x2-plus-16"))
        {
            await SimulateAsync("snapdragon-x2-plus-16");
            ShowSection("deck");
            await Settle();
            string? before = SelectedModel()?.VariantId;
            Check("a simulated device offers the model it would use", _emulateCheck.Visibility == Visibility.Visible, _emulateCheck.Content?.ToString() ?? "");
            _emulateCheck.IsChecked = true;
            bool answered = await WaitFor(() => _emulateNote.Text.Length > 0, 90);
            Check("the model it would use is resolved, or the reason it cannot be is given", answered, _emulateNote.Text);
            bool emulated = _emulatedModel is not null;
            if (emulated)
                Check("the emulated model is chosen and the deck still says this PC", SelectedModel()?.VariantId == _emulatedModel!.VariantId && _emulateNote.Text.Contains("this PC", StringComparison.Ordinal), _emulateNote.Text);
            await ShotAsync(log, "deck-emulating");
            _emulateCheck.IsChecked = false;
            await WaitFor(() => _emulatedModel is null && _modelCombo.Items.Count == options.Count, 20);
            Check("unticking goes back to this PC's own models", _emulatedModel is null && _modelCombo.Items.Count == options.Count, $"{_modelCombo.Items.Count} of {options.Count}");
            // The emulated model can be one of this PC's own models that needs a download, so staying on it would cost the next deck a download.
            Check("unticking goes back to the model chosen before", SelectedModel()?.VariantId == before, SelectedModel()?.Summary ?? "none");
            if (emulated)
            {
                _emulateCheck.IsChecked = true;
                bool again = await WaitFor(() => _emulatedModel is not null, 90);
                await SimulateAsync("");
                Check("choosing This PC while emulating goes back to the model chosen before",
                    again && _emulatedModel is null && SelectedModel()?.VariantId == before, SelectedModel()?.Summary ?? "none");
            }
        }

        await SimulateAsync("");
        Check("choosing This PC clears the simulation", !_devices.IsSimulated && !_simulationBar.IsOpen && _deviceTitle.Text == real.Headline, _deviceTitle.Text);
        ShowSection("deck");
        await Settle();
    }

    // ---- Real decks ----

    private async Task SmokeLiveAsync(SmokeLog log)
    {
        void Check(string name, bool ok, string detail = "") => log.Check(name, ok, detail);
        if (_plan is null)
        {
            Check("live: local AI is ready", false, _engineProblem);
            return;
        }
        ShowSection("deck");
        await Settle();

        // A model that is not on this PC would open a download dialog that waits for a person.
        var usable = _plan.Models.Options.Where(o => o.Cached || _options.AllowDownload).ToList();
        if (usable.Count == 0)
        {
            Check("live: a model is already on this PC", false, "none is cached; run once with --allow-download");
            return;
        }
        var first = SelectedModel() is { } current && usable.Any(o => o.VariantId == current.VariantId) ? current : usable[0];
        SelectModel(first.VariantId);
        // With downloads allowed nothing stops the recommended model from being used; without, a model already on this PC stands in.
        var recommended = _plan.Default;
        bool usesRecommended = recommended is not null && first.VariantId == recommended.VariantId;
        Check("live: the first deck uses the recommended model", usesRecommended || !_options.AllowDownload,
            usesRecommended ? first.Summary : $"recommended: {recommended?.Summary ?? "none"}; using {first.Summary}");

        // 1. Stop a deck part-way.
        InvokeButton(_buildButton);
        Check("live: Make the deck starts a run", await WaitFor(() => _running, 10));
        // The first run loads the model, and with --allow-download fetches it first, which can take most of an hour on slow Wi-Fi.
        await WaitFor(() => _runPhase == "draft" || !_running, _options.AllowDownload ? 3600 : 900);
        Check("live: the run reaches the drafting step", _runPhase == "draft", $"{_runPhase} / {_status.Text}");
        if (_running)
        {
            await Task.Delay(2000);
            InvokeButton(_stopButton);
            bool ended = await WaitFor(() => !_running, 180);
            Check("live: Stop ends the run and the page is usable again",
                ended && _lastRun is null && _buildButton.IsEnabled && _status.Text.StartsWith("Stopped", StringComparison.Ordinal), _status.Text);
        }

        // 2. A whole deck from a pre-staged brief.
        await BuildThroughUiAsync(log, "pre-staged brief", "deck-made");

        // 3. A brief written in plain words, on another model when this PC already has one (downloading a second model would only add time).
        var other = _plan.Models.Options.FirstOrDefault(o => o.Cached && o.VariantId != first.VariantId);
        if (other is not null) SelectModel(other.VariantId);
        _briefTabs.SelectedItem = _briefTabs.Items[1];
        _clientBox.Text = "Studio Bakery";
        const string request = "A sales pitch for a bakery with 12 staff that loses time on paper invoices. The goal is a pilot of automatic invoicing.";
        _requestBox.Text = request;
        await Settle();
        InvokeButton(_draftButton);
        bool drafting = await WaitFor(() => _running, 10);
        if (drafting) await WaitFor(() => !_running, 900);
        Check("live: Draft the brief turns typed words into a brief", _drafted is { Source: "prompt" } && _drafted.Client.Length > 0, _status.Text);
        if (_drafted is not null)
        {
            var typed = Regex.Matches(request, @"\d+").Select(m => m.Value).ToHashSet();
            var invented = _drafted.Facts.SelectMany(f => Regex.Matches(f, @"\d+").Select(m => m.Value)).Where(n => !typed.Contains(n)).Distinct().ToList();
            Check("live: no number in the drafted facts is one the model made up", invented.Count == 0, string.Join(", ", invented));
            Check("live: the drafted brief is shown before the deck", FindById(_root, "BriefFacts") is not null);
            await ShotAsync(log, "deck-drafted");
        }
        var run = await BuildThroughUiAsync(log, "drafted brief", "deck-made-drafted");
        if (run is not null && _drafted is not null)
            Check("live: the deck was made from the drafted brief", run.Result.Brief.Client == _drafted.Client, run.Result.Brief.Client);
        if (other is not null)
            Check("live: the other model was loaded in place of the first", _prepared?.Variant == other.VariantId, _prepared?.Variant ?? "none");
    }

    private async Task<ForgeRun?> BuildThroughUiAsync(SmokeLog log, string label, string shot)
    {
        void Check(string name, bool ok, string detail = "") => log.Check($"live ({label}): {name}", ok, detail);
        var before = _lastRun;
        var watch = Stopwatch.StartNew();
        InvokeButton(_buildButton);
        bool started = await WaitFor(() => _running, 10);
        if (started) await WaitFor(() => !_running, 1800);
        var run = _lastRun;
        bool made = started && run is not null && !ReferenceEquals(run, before);
        Check("a deck is made", made, _status.Text);
        if (!made || run is null) return null;

        string pptx = run.Files.Pptx;
        Check("the .pptx exists", File.Exists(pptx), pptx);
        int slideParts = -1;
        try
        {
            using var zip = ZipFile.OpenRead(pptx);
            slideParts = zip.Entries.Count(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { }
        Check("the file is a package with every slide", slideParts == run.Deck.Slides.Count, $"{slideParts} slide parts, {run.Deck.Slides.Count} slides");
        Check("the Forge graded it", run.Result.Grade is not null, run.Result.Grade?.AutoGrade ?? "no grade");
        bool pictures = await WaitFor(() => _strip.Children.Count > 0 && _strip.Children.OfType<Border>().All(b => (b.Child as Button)?.Content is Microsoft.UI.Xaml.Controls.Image { Source: not null }), 30);
        Check("the preview and the strip show every slide", run.Files.Previews.Count == run.Deck.Slides.Count && _strip.Children.Count == run.Deck.Slides.Count && pictures && _previewImage.Source is not null,
            $"{run.Files.Previews.Count} previews, {_strip.Children.Count} thumbnails");
        Check("the deck card shows where the file is", _resultCard.Visibility == Visibility.Visible && _deckPath.Text == pptx, _deckPath.Text);
        foreach (string tile in new[] { "grade", "slides", "time", "speed", "energy", "calls", "lint" })
            Check($"the '{tile}' number is shown", FindById(_root, "Kpi_" + tile) is not null);
        string energy = FindText(FindById(_root, "Kpi_energy"), 1) ?? "";
        Check("energy is shown only when it was measured", run.Result.Energy.Measured ? energy.EndsWith("Wh", StringComparison.Ordinal) : energy == "Not measured", energy);
        Check("the activity log recorded the run", _logLines.Any(l => l.Contains("Saved ", StringComparison.Ordinal)));
        Check("the controls are usable again", _buildButton.IsEnabled && _choices.IsEnabled && _stopButton.Visibility == Visibility.Collapsed);
        await ShotScrolledAsync(log, shot, _rightScroll);
        log.Check($"live ({label}) timing", true, $"{watch.Elapsed.TotalSeconds:0} s end to end, {Provenance.Duration(run.Result.DraftSeconds)} drafting, {run.Result.Grade?.AutoGrade} {run.Result.Grade?.AutoScore:0.#}");
        if (_prepared?.Model is { } model)
            log.Check($"live ({label}) model", true,
                $"{model.Label} on the {model.Choice.LaneLabel} via {model.Ep}, {(model.WasCached ? "already on this PC" : "downloaded for this run")}, loaded in {model.LoadSeconds:0.#} s");
        return run;
    }

    // ---- Helpers ----

    private async Task SimulateAsync(string id)
    {
        foreach (var item in _simulateBox.Items.OfType<ComboBoxItem>())
            if (item.Tag as string == id)
            {
                _simulateBox.SelectedItem = item;
                break;
            }
        await WaitFor(() => id.Length == 0 ? !_devices.IsSimulated : _devices.IsSimulated && _devices.Effective.Profile.SimulatedId == id, 20);
        await Settle();
    }

    private async Task ResizeAsync(int widthDips, int heightDips)
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        if (scale <= 0) scale = 1;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(widthDips * scale), (int)(heightDips * scale)));
        await Settle();
    }

    private static void SelectTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if (item.Tag as string == tag)
            {
                combo.SelectedItem = item;
                return;
            }
    }

    private void SelectModel(string variant) => SelectTag(_modelCombo, variant);

    private static int CountIds(DependencyObject root, string prefix)
    {
        int count = AutomationProperties.GetAutomationId(root) is { } id && id.StartsWith(prefix, StringComparison.Ordinal) ? 1 : 0;
        int children = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < children; i++) count += CountIds(VisualTreeHelper.GetChild(root, i), prefix);
        return count;
    }

    /// <summary>The nth text block under an element, in tree order; the tiles are label, value, note.</summary>
    private static string? FindText(DependencyObject? root, int index)
    {
        var found = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock text) found.Add(text.Text);
            int children = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < children; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        if (root is not null) Walk(root);
        return index < found.Count ? found[index] : null;
    }

    private async Task ShotAsync(SmokeLog log, string name)
    {
        if (log.CaptureDir is null) return;
        Directory.CreateDirectory(log.CaptureDir);
        await Settle();
        await Task.Delay(400);
        if (await CaptureAsync(Path.Combine(log.CaptureDir, name + ".png")) is { } path) log.Screens.Add(path);
    }

    /// <summary>One picture per screenful of a scrolling page, so a long page is seen in full.</summary>
    private async Task ShotScrolledAsync(SmokeLog log, string name, ScrollViewer scroller, int maxFrames = 4)
    {
        if (log.CaptureDir is null) return;
        await Settle();
        scroller.ChangeView(null, 0, null, true);
        await Settle();
        double page = Math.Max(200, scroller.ViewportHeight - 40);
        int frames = (int)Math.Clamp(Math.Ceiling(scroller.ScrollableHeight / page) + 1, 1, maxFrames);
        for (int i = 0; i < frames; i++)
        {
            if (i > 0)
            {
                scroller.ChangeView(null, Math.Min(scroller.ScrollableHeight, i * page), null, true);
                await Settle();
            }
            await ShotAsync(log, frames == 1 ? name : $"{name}-{i + 1}");
        }
        scroller.ChangeView(null, 0, null, true);
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
}
