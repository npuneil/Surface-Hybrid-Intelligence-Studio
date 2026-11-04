using System.Globalization;
using Forge.Core.Harness;
using Forge.Engine;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SurfaceDeviceKit;
using static OfflinePptxForge.Ui;

namespace OfflinePptxForge;

// The "This PC" page: what the device detection service found, as lanes and capabilities, and a switch to preview another Surface.
internal sealed partial class MainWindow
{
    private readonly StackPanel _deviceBody = new() { Spacing = 22 };
    private readonly TextBlock _deviceTitle = Id(H1("Detecting this PC..."), "DeviceTitle");
    private readonly TextBlock _deviceSub = Id(Body(""), "DeviceClass");
    private readonly WrapPanel _devicePills = new() { HorizontalSpacing = 8, VerticalSpacing = 6 };
    private readonly ComboBox _simulateBox = Id(new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 440, MaxWidth = 640 }, "SimulateCombo");
    private readonly TextBlock _simulateNote = Id(Set(Small(""), t => t.Visibility = Visibility.Collapsed), "SimulateNote");
    private bool _fillingSimulate;

    private FrameworkElement BuildDevicePage()
    {
        FillSimulateBox();
        _simulateBox.SelectionChanged += async (_, _) => await OnSimulateChosenAsync();
        var chooser = CardBorder(Stack(8,
            H3("Show another Surface"),
            Small("Preview what a different Surface can do: the lanes, the capabilities and the model the Forge would plan. Display and planning only. Decks are always made on this PC."),
            _simulateBox,
            _simulateNote));
        var page = new StackPanel { Spacing = 18, MaxWidth = 1180, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(20, 8, 20, 32) };
        page.Children.Add(Stack(8, Eyebrow("This PC"), _deviceTitle, _deviceSub, _devicePills));
        page.Children.Add(chooser);
        page.Children.Add(_deviceBody);
        return Id(new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled }, "DevicePage");
    }

    private static string GroupName(string group) => group switch
    {
        "snapdragon" => "Snapdragon",
        "intel" => "Intel",
        "nvidia" => "NVIDIA",
        _ => "Other",
    };

    private void FillSimulateBox()
    {
        _fillingSimulate = true;
        try
        {
            _simulateBox.Items.Clear();
            _simulateBox.Items.Add(new ComboBoxItem { Content = "This PC (detected)", Tag = "" });
            string? group = null;
            foreach (var device in _devices.SimulatedDevices)
            {
                if (device.Group != group)
                {
                    group = device.Group;
                    _simulateBox.Items.Add(new ComboBoxItem { Content = GroupName(group).ToUpperInvariant(), IsEnabled = false, FontWeight = FontWeights.SemiBold, FontSize = 11 });
                }
                _simulateBox.Items.Add(new ComboBoxItem { Content = device.Label, Tag = device.Id });
            }
            _simulateBox.SelectedIndex = 0;
        }
        finally { _fillingSimulate = false; }
    }

    private string? SimulatedId => _devices.IsSimulated ? _devices.Effective.Profile.SimulatedId : null;

    private void SyncSimulateBox()
    {
        string wanted = SimulatedId ?? "";
        _fillingSimulate = true;
        try
        {
            foreach (var item in _simulateBox.Items.OfType<ComboBoxItem>())
                if (item.Tag as string == wanted && !ReferenceEquals(_simulateBox.SelectedItem, item)) _simulateBox.SelectedItem = item;
            _simulateNote.Text = wanted.Length > 0 && SimulatedProfiles.Find(wanted) is { } device ? device.Note : "";
            _simulateNote.Visibility = _simulateNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _fillingSimulate = false; }
    }

    private async Task OnSimulateChosenAsync()
    {
        if (_fillingSimulate || _simulateBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        if (id == (SimulatedId ?? "")) return;
        try
        {
            if (id.Length == 0) await Task.Run(_devices.ClearSimulation);
            else await Task.Run(() => _devices.Simulate(id));
        }
        catch (Exception exception)
        {
            Program.LogError(_options, exception);
            Notify("Could not switch the device", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private static string Num(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private static UIElement SectionTitle(string title, string? sub = null) =>
        sub is null ? H2(title) : Stack(2, H2(title), Small(sub));

    private void RebuildDevice()
    {
        var report = _devices.Effective;
        var profile = report.Profile;
        bool simulated = _devices.IsSimulated;

        _deviceTitle.Text = profile.Headline;
        _deviceSub.Text = $"{profile.PcClassLabel} · {profile.DeviceClassLabel}";
        _devicePills.Children.Clear();
        _devicePills.Children.Add(simulated ? Pill("Simulated · not this PC", AmberTint, AmberInk) : Pill("Detected on this PC", GreenTint, GreenInk));
        foreach (var (status, label) in new[] { (CapabilityMatrix.Available, "available"), (CapabilityMatrix.Limited, "limited"), (CapabilityMatrix.Unavailable, "not available") })
        {
            int count = profile.Capabilities.Count(c => c.Status == status);
            if (count == 0) continue;
            var (back, fore) = StatusColors(status);
            _devicePills.Children.Add(Pill($"{count} {label}", back, fore));
        }
        SyncSimulateBox();

        _deviceBody.Children.Clear();
        _deviceBody.Children.Add(FactsCard(profile, simulated));
        _deviceBody.Children.Add(SectionTitle("Where models can run", "Processors that can run a language model, preferred first. Each has its own memory budget."));
        _deviceBody.Children.Add(LaneCards(profile));
        _deviceBody.Children.Add(SectionTitle("What the Forge runs here", simulated
            ? $"The plan for {profile.Label}, then what this PC can really run today."
            : "The plan for this PC, then the models Foundry Local has for it today."));
        _deviceBody.Children.Add(PlanCard(report, simulated));
        _deviceBody.Children.Add(SectionTitle("What this device can do", "Every line says where it comes from. Estimates are worked out from the detected memory against real model sizes."));
        _deviceBody.Children.Add(CapabilitiesCard(profile));
    }

    private static UIElement FactsCard(DeviceProfile p, bool simulated)
    {
        var grid = new Grid { RowSpacing = 9, ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TextBlock Add(string key, string value)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Small(key.ToUpperInvariant());
            label.FontWeight = FontWeights.SemiBold;
            var text = Text(value, 13, Ink);
            text.IsTextSelectionEnabled = true;
            Grid.SetRow(label, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(label);
            grid.Children.Add(text);
            return text;
        }
        Add("Processor", $"{p.Cpu.Name} · {p.Cpu.LogicalCores} logical cores");
        Add("Graphics", p.Gpus.Count == 0 ? "None reported"
            : string.Join("\n", p.Gpus.Select(g => $"{g.Name} · {g.Kind}{(g.VramGb >= 0.5 ? $" · {Num(g.VramGb)} GB" : "")}")));
        Add("NPU", p.Npus.Count == 0 ? "None reported"
            : string.Join("\n", p.Npus.Select(n => $"{n.Name} · {(n.RatedTops is { } t ? $"rated {Num(t)} TOPS (vendor figure)" : "no rating")}{(n.LlmCapable ? "" : " · not used for language models")}")));
        Add("Memory", $"{Num(p.Memory.InstalledGb)} GB installed · {Num(p.Memory.VisibleGb)} GB visible to Windows{(p.Cpu.UnifiedMemory ? " · shared by the CPU and GPU" : "")}");
        Add("Storage", $"{p.Storage.FreeGb:0} GB free of {p.Storage.TotalGb:0} GB on {p.Storage.Drive}{(simulated ? " (this PC)" : "")}");
        Add("Windows", $"{p.Os.Version} · {p.Os.Arch} · {(p.Os.Emulated ? $"this {p.Os.ProcessArch} build is emulated" : $"native {p.Os.ProcessArch} build")}");
        Add("Software", $"{(p.Software.PowerPoint ? $"PowerPoint {p.Software.PowerPointVersion}".TrimEnd() : "PowerPoint not found")} · {(p.Software.NvidiaSmi ? "nvidia-smi found, so GPU energy can be measured" : "no nvidia-smi, so energy is not measured")}{(simulated ? " (this PC)" : "")}");
        Id(Add("Package", ForgePackage.Line(p, simulated)), "PackageLine");
        return Id(CardBorder(grid), "DeviceFacts");
    }

    private UIElement LaneCards(DeviceProfile p)
    {
        var cards = new List<FrameworkElement>();
        bool first = true;
        foreach (var lane in p.Lanes)
        {
            string label = _devices.Catalog.Lanes.TryGetValue(lane.Id, out var spec) ? spec.Label : lane.Id.ToUpperInvariant();
            var header = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
            header.Children.Add(H2(label));
            header.Children.Add(lane.Available ? Pill("Available", GreenTint, GreenInk) : Pill("Not available", SlateTint, Slate));
            if (lane.Available && first) header.Children.Add(Pill("Preferred", AccentTint, AccentDeep));
            if (lane.Available) first = false;
            var body = Stack(6, header, Text(lane.Device, 13, Ink, FontWeights.SemiBold));
            if (lane.Available)
            {
                string cap = _devices.Catalog.ModelClasses.Describe.TryGetValue(lane.ClassCap, out var describe) ? $"{lane.ClassCap}-class, {describe}" : $"{lane.ClassCap}-class";
                body.Children.Add(Body($"Memory budget {Num(lane.BudgetGb)} GB for model weights. Up to {cap}."));
                body.Children.Add(Small("Runs through: " + string.Join(", ", lane.Eps)));
            }
            if (lane.RatedTops is { } tops) body.Children.Add(Small($"Rated {Num(tops)} TOPS (vendor figure; nothing here measures it)."));
            body.Children.Add(Small(lane.Note));
            cards.Add(Id(CardBorder(body, 16, lane.Available ? Card : Paper), "Lane_" + lane.Id));
        }
        return new AdaptiveGrid(300, 3).With([.. cards]);
    }

    private UIElement PlanCard(DeviceReport report, bool simulated)
    {
        var body = Stack(10);
        string label = report.Profile.Label;
        if (report.Plans.TryGetValue(ForgeSession.Workload, out var plan))
        {
            body.Children.Add(H3(simulated ? $"On {label}" : "On this PC"));
            body.Children.Add(Body(plan.Summary));
            body.Children.Add(Small(plan.Why));
        }

        body.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 4, 0, 4) });
        body.Children.Add(H3(simulated ? "On this PC today" : "Models Foundry Local has for this PC"));
        if (_plan is null)
        {
            body.Children.Add(Body(_engineProblem.Length > 0 ? "The local model engine is not available: " + _engineProblem : "The local model engine is starting. This fills in when it is ready."));
        }
        else
        {
            if (_plan.Models.Options.Count == 0) body.Children.Add(Body("Foundry Local has no model for the Forge that fits this PC."));
            foreach (var option in _plan.Models.Options)
            {
                bool starts = option == _plan.Default;
                var row = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
                row.Children.Add(Pill(option.LaneLabel, AccentTint, AccentDeep));
                row.Children.Add(Text(option.Summary, 13, Ink, starts ? FontWeights.SemiBold : FontWeights.Normal));
                if (starts) row.Children.Add(Pill("Starts with this", GreenTint, GreenInk));
                body.Children.Add(row);
                if (!starts) continue;
                var speed = _session.Speed.Entry(option.VariantId);
                body.Children.Add(Small(ForgeText.Settings(RunPolicy.Decide(ModelFacts.From(option), speed))));
                body.Children.Add(Small(ForgeText.Speed(_session.Speed, option.VariantId, speed, ForgeText.TypicalSlides)));
            }
            foreach (string note in _plan.Models.Notes) body.Children.Add(Small(note));
        }
        return Id(CardBorder(body), "DevicePlan");
    }

    private static string GroupTitle(string group) => group switch
    {
        "lane" => "Processors that can run models",
        "workload" => "What this device can do with local AI",
        _ => "Platform",
    };

    private static string BasisHelp(string basis) => basis switch
    {
        "detected" => "Read from this PC.",
        "estimated" => "Worked out from the detected memory against the catalog's model sizes.",
        "vendor-rated" => "A vendor figure. Nothing in this app measures it.",
        "catalog" => "Looked up in the device catalog.",
        _ => basis,
    };

    private static UIElement CapabilitiesCard(DeviceProfile p)
    {
        var all = Stack(14);
        foreach (var group in p.Capabilities.GroupBy(c => c.Group))
        {
            var rows = Stack(10, H3(GroupTitle(group.Key)));
            foreach (var capability in group)
            {
                var (back, fore) = StatusColors(capability.Status);
                string status = capability.Status switch { "available" => "Available", "limited" => "Limited", _ => "Not available" };
                var pill = Id(Pill(status, back, fore), "Status_" + capability.Id);
                pill.MinWidth = 78;
                var text = Stack(2, Text(capability.Title, 13, Ink, FontWeights.SemiBold), Small(capability.Detail));
                var basis = Small(capability.Basis);
                basis.VerticalAlignment = VerticalAlignment.Top;
                basis.HorizontalAlignment = HorizontalAlignment.Right;
                ToolTipService.SetToolTip(basis, BasisHelp(capability.Basis));
                var line = new Grid { ColumnSpacing = 12 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                pill.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(text, 1);
                Grid.SetColumn(basis, 2);
                line.Children.Add(pill);
                line.Children.Add(text);
                line.Children.Add(basis);
                rows.Children.Add(Named(line, "Capability_" + capability.Id, $"{capability.Title}: {status}"));
            }
            all.Children.Add(rows);
        }
        return Id(CardBorder(all), "DeviceCapabilities");
    }
}
