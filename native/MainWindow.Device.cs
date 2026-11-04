using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

/// <summary>
/// "This PC": what the device kit detected, which processing lanes the Studio can use, what it runs for each model slot and how each demo fits.
/// Also the preview of another Surface for sellers, and the words every page uses for where this PC runs its models.
/// </summary>
internal sealed partial class MainWindow
{
    private TargetWords _words = TargetWords.Neutral;
    private bool _wordsApplied;
    private readonly List<Action<TargetWords>> _wordBindings = [];

    private readonly TextBlock _brandDevice = Id(Text(Brand.Device, 13, Muted), "TitleBarDevice");
    private readonly TextBlock _devicePillText = Id(Text("Detecting device", 12, Slate, FontWeights.SemiBold), "TitleDeviceClass");
    private readonly Border _devicePill = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3, 10, 4), Background = SlateTint, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previewBack = Id(Secondary("Back to this PC"), "PreviewBackButton");
    private readonly InfoBar _previewInfo = Id(new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, Margin = new Thickness(16, 0, 16, 8) }, "PreviewInfoBar");

    private readonly TextBlock _deviceHeadline = Id(H1("Detecting this PC"), "DeviceHeadline");
    private readonly StackPanel _devicePills = Id(Row(8), "DevicePills");
    private readonly TextBlock _deviceIntro = Id(Body(""), "DeviceIntro");
    private readonly StackPanel _deviceFacts = Id(Stack(8), "DeviceFacts");
    private readonly ContentControl _deviceLanes = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _deviceLineup = Id(Stack(10), "DeviceLineup");
    private readonly TextBlock _devicePlacement = Id(Body("", Ink), "DevicePlacement");
    private readonly StackPanel _deviceDemoFits = Id(Stack(10), "DeviceDemoFits");
    private readonly StackPanel _deviceCapabilities = Id(Stack(8), "DeviceCapabilities");
    private readonly ComboBox _simulatePicker = Id(new ComboBox { Header = "Surface to preview", PlaceholderText = "Choose a Surface", HorizontalAlignment = HorizontalAlignment.Stretch }, "DeviceSimulatePicker");
    private readonly Button _simulateButton = Id(Primary("Preview this Surface"), "DevicePreviewButton");
    private readonly Button _simulateBack = Id(Secondary("Back to this PC"), "DeviceBackButton");
    private readonly TextBlock _simulateNote = Id(Small(""), "DeviceSimulateNote");
    private readonly InfoBar _deviceError = Id(new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true }, "DeviceError");
    private DeviceReport? _deviceReport;
    private string? _devicePickerKey;
    private string? _deviceKey;
    private bool _simulating;
    private bool _deviceLoading;

    /// <summary>Text whose sentence names where this PC runs its models; it is rewritten when the service reports a different place.
    /// Only call it from the Build* methods: field initializers of the partial class run in no defined order.</summary>
    private T Worded<T>(T element, Func<TargetWords, string> text, Action<T, string> apply) where T : DependencyObject
    {
        apply(element, text(_words));
        _wordBindings.Add(words => apply(element, text(words)));
        return element;
    }

    private TextBlock Worded(TextBlock block, Func<TargetWords, string> text) => Worded(block, text, static (b, s) => b.Text = s);

    private Button Worded(Button button, Func<TargetWords, string> text) => Worded(button, text, static (b, s) => b.Content = s);

    private ComboBox WordedHeader(ComboBox box, Func<TargetWords, string> text) => Worded(box, text, static (b, s) => b.Header = s);

    private static string LaneName(string id) => id switch { "npu" => "NPU", "gpu" => "GPU", "cpu" => "CPU", _ => id.ToUpperInvariant() };

    private static string GroupName(string group) => group switch
    {
        "snapdragon" => "Snapdragon", "intel" => "Intel", "nvidia" => "NVIDIA", "other" => "AMD and other", _ => group,
    };

    private static string RuntimeName(string token) => token switch
    {
        "qnn" => "QNN", "cuda" => "CUDA", "nvtensorrtrtx" => "TensorRT for RTX", "webgpu" => "WebGPU", "dml" => "DirectML",
        "openvino" => "OpenVINO", "vitisai" => "Vitis AI", "cpu" => "CPU", _ => token.ToUpperInvariant(),
    };

    private static string BasisText(string basis) => basis switch
    {
        "detected" => "Detected on this PC", "estimated" => "Estimated", "vendor-rated" => "Vendor-rated",
        "catalog" => "From Microsoft's device pages", _ => basis,
    };

    private FrameworkElement BuildDevicePage()
    {
        _simulatePicker.SelectionChanged += (_, _) => UpdateSimulateButtons();
        _simulateButton.Click += async (_, _) =>
        {
            if (_simulatePicker.SelectedItem is ComboBoxItem { Tag: string id }) await SimulateDeviceAsync(id);
        };
        _simulateBack.Click += async (_, _) => await SimulateDeviceAsync(null);
        _previewBack.Click += async (_, _) => await SimulateDeviceAsync(null);
        _deviceError.IsOpen = false;

        var factsCard = CardBorder(Stack(12, H3("What was detected"), _deviceFacts), 22);
        Id(factsCard, "DeviceFactsCard");
        var lineupCard = CardBorder(Stack(12, H3("What the Studio runs here"),
            Small("One model per slot, chosen from Foundry Local's catalog for this PC's lanes and memory. Models that are not downloaded yet are marked."),
            _deviceLineup, _devicePlacement), 22);
        var demosCard = CardBorder(Stack(12, H3("Demos on this PC"),
            Small("The demo list is ordered by how well each demo fits this PC. A demo whose models do not fit is listed last, with the reason."),
            _deviceDemoFits), 22);
        var capabilitiesCard = CardBorder(Stack(12, H3("Capabilities"),
            Small("Each line says how it is known: detected on this PC, estimated from the model catalog, or rated by the vendor."),
            _deviceCapabilities), 22);
        var previewCard = CardBorder(Stack(12, H3("Preview another Surface"),
            Body("See what the Studio would run on a different Surface: which processor does the work, which models fit and which demos lead. " +
                 "A preview is an estimate from the device kit's catalog and published specifications, not a measurement. Live runs pause while a preview is shown, " +
                 "because they would measure this PC, not the one on screen."),
            _simulatePicker, Row(10, _simulateButton, _simulateBack), _simulateNote, _deviceError), 22, Sand);
        Id(previewCard, "DevicePreviewCard");

        return Page(Stack(22,
            Stack(10, Eyebrow("This PC"), _deviceHeadline, _devicePills, _deviceIntro),
            factsCard, _deviceLanes, previewCard, lineupCard, demosCard, capabilitiesCard));
    }

    /// <summary>Reads /api/device for the page. Failures show on the page; the rest of the app keeps working.</summary>
    private async Task RefreshDeviceAsync()
    {
        if (_deviceLoading || _simulating || _closed) return;
        _deviceLoading = true;
        try
        {
            RenderDevice(await _client.GetDeviceAsync(_lifetime.Token));
            _deviceError.IsOpen = false;
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _deviceError.Title = "Device detection unavailable";
            _deviceError.Message = exception.Message;
            _deviceError.IsOpen = true;
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            // Closing: the request was cancelled.
        }
        finally { _deviceLoading = false; }
    }

    private async Task<bool> SimulateDeviceAsync(string? deviceId)
    {
        if (_simulating) return false;
        _simulating = true;
        _deviceError.IsOpen = false;
        UpdateSimulateButtons();
        try
        {
            var report = await _client.SimulateDeviceAsync(deviceId, _lifetime.Token);
            RenderDevice(report);
            await RefreshAfterDeviceChangeAsync();
            return true;
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _deviceError.Title = deviceId is null ? "Could not go back to this PC" : "Could not preview that Surface";
            _deviceError.Message = exception.Message;
            _deviceError.IsOpen = true;
            return false;
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _simulating = false;
            UpdateSimulateButtons();
        }
    }

    /// <summary>The status, the demo list and the router wording all follow the PC being shown.</summary>
    private async Task RefreshAfterDeviceChangeAsync()
    {
        for (int waited = 0; _refreshing && waited < 100; waited++) await Task.Delay(50, _lifetime.Token);
        await RefreshStatusAsync();
        await RefreshDemoCatalogAsync();
        await LoadHybridCatalogAsync();
    }

    /// <summary>Called on every status poll: the title bar, the preview notice and the words the pages use follow the PC being shown.</summary>
    private void ApplyTarget(StatusSnapshot status)
    {
        var target = status.Target is { Error: null } t ? t : null;
        _brandDevice.Text = target?.Label ?? Brand.Device;
        _devicePillText.Text = target?.ClassPillText ?? "Device not detected";
        _devicePillText.Foreground = target?.Simulated == true ? AmberInk : Slate;
        _devicePill.Background = target?.Simulated == true ? AmberTint : SlateTint;
        _previewInfo.IsOpen = target?.Simulated == true;
        if (target is { Simulated: true })
        {
            _previewInfo.Title = $"Previewing {target.Label}";
            _previewInfo.Message = (target.DeviceClassLabel is { Length: > 0 } dc ? dc + ". " : "") +
                "Models, lanes and demo order are estimates from the device kit for this Surface. Live runs are paused because they would measure this PC, not the one on screen.";
        }
        var words = status.Words;
        if (!_wordsApplied || words != _words)
        {
            _words = words;
            _wordsApplied = true;
            foreach (var binding in _wordBindings) binding(words);
        }
        // The key is set before the request so a failing request is not repeated on every poll.
        string? key = target is null ? null : $"{target.Simulated}|{target.Label}";
        if (key is not null && key != _deviceKey && !_deviceLoading && !_simulating)
        {
            _deviceKey = key;
            _ = RefreshDeviceAsync();
        }
        UpdateSimulateButtons();
    }

    private void UpdateSimulateButtons()
    {
        bool runActive = _running || _demoRunning || _labRunning;
        bool free = !_simulating && !runActive;
        bool preview = _deviceReport?.Simulated ?? _status?.Target?.Simulated ?? false;
        _simulatePicker.IsEnabled = free;
        _simulateButton.IsEnabled = free && _simulatePicker.SelectedItem is ComboBoxItem { Tag: string };
        _simulateBack.IsEnabled = free && preview;
        _previewBack.IsEnabled = free;
        _simulateNote.Text = runActive ? "A live run is active. Wait for it to finish before changing the preview."
            : _simulating ? "Changing the preview..."
            : preview ? "A preview is shown. Choose another Surface, or go back to this PC to run live."
            : "Choose a Surface to preview. This PC is not changed.";
    }

    private void RenderDevice(DeviceReport r)
    {
        _deviceReport = r;
        _deviceHeadline.Text = r.Headline;
        _devicePills.Children.Clear();
        if (r.PcClassLabel is { Length: > 0 } pc) _devicePills.Children.Add(Id(Pill(pc, AccentTint, AccentDeep), "DevicePcClassPill"));
        if (r.DeviceClassLabel is { Length: > 0 } dc) _devicePills.Children.Add(Id(Pill(dc, SlateTint, Slate), "DeviceClassPill"));
        if (r.Simulated) _devicePills.Children.Add(Id(Pill("PREVIEW - LIVE RUNS PAUSED", AmberTint, AmberInk), "DeviceSimulatedPill"));
        _deviceIntro.Text = r.Simulated
            ? $"This is a preview of {r.Label}. This PC is {r.DetectedLabel}; its models and live runs are unchanged."
            : "Read from this PC when the local service started. Lanes, memory budgets and model sizes are marked as detected, estimated or vendor-rated.";

        _deviceFacts.Children.Clear();
        _deviceFacts.Children.Add(FactRow("Device", string.IsNullOrWhiteSpace(r.Product) ? r.Label : r.Product, "DeviceFactModel"));
        _deviceFacts.Children.Add(FactRow("Processor", r.Cpu + (r.CpuCores is int cores ? $" ({cores} logical cores)" : ""), "DeviceFactCpu"));
        _deviceFacts.Children.Add(FactRow("Graphics", r.Gpus.Count > 0 ? string.Join("; ", r.Gpus) : "None detected", "DeviceFactGpu"));
        _deviceFacts.Children.Add(FactRow("NPU", r.Npus.Count > 0 ? string.Join("; ", r.Npus) : "None detected", "DeviceFactNpu"));
        _deviceFacts.Children.Add(FactRow("Memory", MemoryText(r), "DeviceFactMemory"));
        _deviceFacts.Children.Add(FactRow("Operating system", r.Os, "DeviceFactOs"));

        var lanes = new AdaptiveGrid(330, 3, 16);
        foreach (string id in new[] { "npu", "gpu", "cpu" })
            if (r.Lane(id) is { } lane) lanes.With(LaneCard(lane, r));
        _deviceLanes.Content = lanes;

        _deviceLineup.Children.Clear();
        foreach (var row in r.Lineup)
        {
            var text = Stack(2, Text(row.Summary, 14, row.Available ? Ink : AmberInk, FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(row.Better)) text.Children.Add(Small(row.Better!));
            _deviceLineup.Children.Add(Id(Labeled(row.Title, text), $"DeviceLineup_{row.Slot}"));
        }
        _devicePlacement.Text = r.Placement;

        RenderDeviceDemos(r);

        _deviceCapabilities.Children.Clear();
        foreach (var (group, heading) in new[] { ("lane", "Processing lanes"), ("workload", "Workloads"), ("platform", "Platform") })
        {
            var items = r.Capabilities.Where(c => c.Group == group).ToList();
            if (items.Count == 0) continue;
            _deviceCapabilities.Children.Add(Set(Text(heading.ToUpperInvariant(), 11, Muted, FontWeights.SemiBold), t => t.Margin = new Thickness(0, 8, 0, 0)));
            foreach (var capability in items) _deviceCapabilities.Children.Add(CapabilityRow(capability));
        }

        PopulateSimulation(r);
        UpdateSimulateButtons();
        if (_status is { } shown) ApplyOverviewStatus(shown);
        RefreshSizingForDevice();
    }


    private void RenderDeviceDemos(DeviceReport r)
    {
        _deviceDemoFits.Children.Clear();
        foreach (var row in r.Demos.OrderBy(d => d.Fit.Rank))
        {
            string title = _demoCatalog.Find(row.Id)?.Title ?? row.Id;
            var (label, background, foreground) = row.Fit.State switch
            {
                "ready" => ("READY", GreenTint, GreenInk),
                "limited" => ("LIMITED", AmberTint, AmberInk),
                _ => ("NOT AVAILABLE", SlateTint, Slate),
            };
            var text = Stack(2, Text(title, 14, Ink, FontWeights.SemiBold), Small(row.Fit.Summary, Ink));
            if (!string.IsNullOrWhiteSpace(row.Fit.Note)) text.Children.Add(Small(row.Fit.Note!));
            var open = Id(new HyperlinkButton { Content = "Open", Padding = new Thickness(0), Foreground = Accent, VerticalAlignment = VerticalAlignment.Center }, $"DeviceDemoOpen_{row.Id}");
            string demoId = row.Id;
            open.Click += (_, _) => { Navigate("demos"); OpenDemo(demoId); };
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pill = Pill(label, background, foreground);
            pill.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(text, 1);
            Grid.SetColumn(open, 2);
            grid.Children.Add(pill);
            grid.Children.Add(text);
            grid.Children.Add(open);
            _deviceDemoFits.Children.Add(Id(grid, $"DeviceDemoFit_{row.Id}"));
        }
    }

    private static string MemoryText(DeviceReport r)
    {
        if (r.InstalledGb is not double installed) return "Not reported";
        string text = $"{installed:0.#} GB installed";
        if (r.VisibleGb is double visible && Math.Abs(visible - installed) > 1) text += $", {visible:0.#} GB visible to Windows";
        return text;
    }

    private static Grid FactRow(string label, string value, string id)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Text(label, 13, Muted));
        var v = Id(Set(Text(string.IsNullOrWhiteSpace(value) ? "Not reported" : value, 13, Ink, FontWeights.SemiBold), t => t.IsTextSelectionEnabled = true), id);
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        return grid;
    }

    private static Grid Labeled(string label, FrameworkElement content)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Text(label, 13, Muted));
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private static FrameworkElement LaneCard(DeviceLane lane, DeviceReport r)
    {
        var pill = lane.Available ? Pill("AVAILABLE", GreenTint, GreenInk) : Pill("NOT USED FOR MODELS", SlateTint, Slate);
        var body = Stack(8, Row(10, Id(H2(LaneName(lane.Id)), $"DeviceLaneName_{lane.Id}"), pill),
            Text(string.IsNullOrWhiteSpace(lane.Device) ? "Not detected" : lane.Device!, 13, Ink, FontWeights.SemiBold));
        if (lane.Available && lane.BudgetGb is double gb)
            body.Children.Add(Small($"Memory for model weights: {gb:0.#} GB" + (lane.ClassCap is { Length: > 0 } cap ? $", up to {cap}-class models" : "") + ".", Ink));
        if (lane.RatedTops is double tops) body.Children.Add(Small($"Rated {tops:0} TOPS (vendor figure).", Ink));
        if (lane.Available && lane.Eps.Count > 0) body.Children.Add(Small("Runtimes: " + string.Join(", ", lane.Eps.Select(RuntimeName)) + ".", Ink));
        var runs = r.Lineup.Where(l => l.Available && l.Lane == lane.Id).Select(l => l.Title.ToLowerInvariant()).ToList();
        body.Children.Add(Small(runs.Count > 0 ? "Runs here: " + string.Join(", ", runs) + "." : "Runs none of the Studio's models.", lane.Available && runs.Count > 0 ? GreenInk : Muted));
        if (!string.IsNullOrWhiteSpace(lane.Note)) body.Children.Add(Small(lane.Note!));
        return Id(CardBorder(body, 20), $"DeviceLane_{lane.Id}");
    }

    private static FrameworkElement CapabilityRow(DeviceCapability c)
    {
        var (label, background, foreground) = c.Status switch
        {
            "available" => ("AVAILABLE", GreenTint, GreenInk),
            "limited" => ("LIMITED", AmberTint, AmberInk),
            _ => ("NOT AVAILABLE", SlateTint, Slate),
        };
        var pill = Pill(label, background, foreground);
        pill.VerticalAlignment = VerticalAlignment.Top;
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Stack(2, Text(c.Title, 14, Ink, FontWeights.SemiBold), Small(c.Detail, Ink), Small(BasisText(c.Basis)));
        Grid.SetColumn(text, 1);
        grid.Children.Add(pill);
        grid.Children.Add(text);
        AutomationProperties.SetName(grid, $"{c.Title}: {label.ToLowerInvariant()}");
        return Id(grid, $"DeviceCapability_{c.Id}");
    }

    /// <summary>Fills the preview picker once per catalog, grouped by vendor, and selects the Surface being previewed.</summary>
    private void PopulateSimulation(DeviceReport r)
    {
        string key = string.Join("|", r.Devices.Select(d => d.Id));
        if (key != _devicePickerKey)
        {
            _devicePickerKey = key;
            _simulatePicker.Items.Clear();
            string? group = null;
            foreach (var device in r.Devices)
            {
                if (device.Group != group)
                {
                    group = device.Group;
                    _simulatePicker.Items.Add(new ComboBoxItem
                    {
                        Content = GroupName(group), IsEnabled = false, FontWeight = FontWeights.SemiBold, Tag = null,
                    });
                }
                var item = new ComboBoxItem { Content = device.Label, Tag = device.Id };
                if (!string.IsNullOrWhiteSpace(device.Note)) ToolTipService.SetToolTip(item, device.Note);
                AutomationProperties.SetName(item, device.Label);
                _simulatePicker.Items.Add(item);
            }
        }
        _simulatePicker.SelectedItem = r.SimulatedId is string id
            ? _simulatePicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == id)
            : null;
    }
}
