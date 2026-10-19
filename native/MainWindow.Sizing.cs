using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private readonly RadioButtons _sizePicker = Id(new RadioButtons { Header = "T-shirt size (illustrative band)", MaxColumns = 4 }, "SizingSizePicker");
    private readonly TextBlock _sizeFamily = Id(Small(""), "SizingFamily");
    private readonly NumberBox _params = SizingBox("Parameters (billions)", "SizingParameters", 0.1, 1);
    private readonly ComboBox _bits = Id(new ComboBox { Header = "Weight precision", HorizontalAlignment = HorizontalAlignment.Stretch }, "SizingBits");
    private readonly NumberBox _context = SizingBox("Context window (tokens)", "SizingContext", 1024, 4096);
    private readonly NumberBox _kv = SizingBox("KV-cache allowance (GiB per 1,000 tokens)", "SizingKv", 0.01, 0.05);
    private readonly NumberBox _runtime = SizingBox("Runtime allowance (GiB)", "SizingRuntime", 0.5, 1);
    private readonly RadioButtons _budgetSource = Id(new RadioButtons { Header = "Memory budget view (one at a time)" }, "SizingBudgetSource");
    private readonly NumberBox _budget = SizingBox("Memory budget (GiB)", "SizingBudget", 1, 8);
    private readonly NumberBox _reserve = SizingBox("OS, apps and other GPU work reserve (GiB)", "SizingReserve", 1, 4);
    private readonly NumberBox _headroom = SizingBox("Safety headroom (GiB)", "SizingHeadroom", 0.5, 2);
    private readonly TextBlock _budgetNote = Id(Small(""), "SizingBudgetNote");
    private readonly Border _verdictPill = Id(new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 4, 12, 5), HorizontalAlignment = HorizontalAlignment.Left }, "SizingVerdict");
    private readonly TextBlock _verdictText = Id(Text("", 13, Ink, FontWeights.SemiBold), "SizingVerdictText");
    private readonly TextBlock _sizingHeadline = Id(H2(""), "SizingHeadline");
    private readonly TextBlock _sizingSummary = Id(Body(""), "SizingSummary");
    private readonly Grid _neededBar = new() { Height = 22, CornerRadius = new CornerRadius(6) };
    private readonly Grid _usableBar = new() { Height = 22, CornerRadius = new CornerRadius(6) };
    private readonly Dictionary<string, TextBlock> _breakdown = [];
    private readonly Dictionary<TShirtSize, (TextBlock Weight, TextBlock Verdict)> _compare = [];
    private readonly StackPanel _observed = Id(Stack(8), "SizingObserved");
    private readonly Button _sizingExport = Id(Secondary("Export estimate (JSON)"), "SizingExport");
    private readonly InfoBar _sizingInfo = Id(new InfoBar { IsClosable = true }, "SizingExportInfo");
    private bool _suppressSizing;
    private SizingResult? _lastSizing;
    private SizingInput? _lastSizingInput;
    private readonly SelectorBar _sizingTabs = Id(new SelectorBar(), "SizingTabs");
    private StackPanel _technicalView = null!;
    private FrameworkElement _salesView = null!;
    private ScrollViewer? _sizingScroller;

    private static NumberBox SizingBox(string header, string id, double small, double large) => Id(new NumberBox
    {
        Header = header,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        SmallChange = small,
        LargeChange = large,
        ValidationMode = NumberBoxValidationMode.Disabled,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    }, id);

    private FrameworkElement BuildSizing()
    {
        foreach (var p in Sizing.Profiles)
            _sizePicker.Items.Add(Id(new RadioButton { Content = $"{p.Size}  {p.MinBillions:0}-{p.MaxBillions:0}B", Tag = p.Size, MinWidth = 0, Margin = new Thickness(0, 0, 12, 0) }, "SizingSize_" + p.Size));
        foreach (int b in Sizing.SupportedBits) _bits.Items.Add(new ComboBoxItem { Content = $"{b}-bit", Tag = b });
        _budgetSource.Items.Add(Id(new RadioButton { Content = "GPU memory budget (driver-reported)", Tag = BudgetSource.GpuDriverBudget }, "SizingBudget_Gpu"));
        _budgetSource.Items.Add(Id(new RadioButton { Content = "Windows-visible memory", Tag = BudgetSource.WindowsVisibleMemory }, "SizingBudget_Windows"));
        _budgetSource.Items.Add(Id(new RadioButton { Content = "Custom budget", Tag = BudgetSource.Custom }, "SizingBudget_Custom"));
        _verdictPill.Child = _verdictText;

        _sizePicker.SelectionChanged += (_, _) => { if (_sizePicker.SelectedIndex >= 0) ApplySizePreset(Sizing.Profiles[_sizePicker.SelectedIndex]); };
        _budgetSource.SelectionChanged += (_, _) => { SyncBudgetBox(); RecomputeSizing(); };
        foreach (var box in new[] { _params, _context, _kv, _runtime, _budget, _reserve, _headroom })
            box.ValueChanged += (_, _) => RecomputeSizing();
        _bits.SelectionChanged += (_, _) => RecomputeSizing();
        _sizingExport.Click += (_, _) => ExportSizing();
        var reset = Id(Secondary("Reset to defaults"), "SizingReset");
        reset.Click += (_, _) => ResetSizing();

        var inputs = CardBorder(Stack(14,
            H3("Model and workload"),
            _sizePicker, _sizeFamily,
            Pair(_params, _bits),
            Pair(_context, _kv),
            _runtime,
            new Border { Height = 1, Background = Line },
            H3("Memory budget"),
            _budgetSource, _budgetNote,
            _budget,
            Pair(_reserve, _headroom),
            reset), 22);

        foreach (var (key, label) in new[]
        {
            ("weights", "Model weights"), ("kv", "KV cache (context)"), ("runtime", "Runtime allowance"),
            ("working", "Working-set estimate"), ("headroom", "+ Safety headroom"), ("required", "= Required"),
            ("usable", "Usable budget after reserve"), ("margin", "Spare (or shortfall)")
        })
            _breakdown[key] = Id(Text("-", 14, Ink, FontWeights.SemiBold), "SizingResult_" + key);

        var table = Stack(6);
        foreach (var (key, label) in new[]
        {
            ("weights", "Model weights"), ("kv", "KV cache (context)"), ("runtime", "Runtime allowance"),
            ("working", "Working-set estimate"), ("headroom", "+ Safety headroom"), ("required", "= Required"),
            ("usable", "Usable budget after reserve"), ("margin", "Spare (or shortfall)")
        })
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bool strong = key is "working" or "required" or "margin";
            row.Children.Add(Text(label, 14, strong ? Ink : Muted, strong ? FontWeights.SemiBold : FontWeights.Normal));
            _breakdown[key].HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(_breakdown[key], 1);
            row.Children.Add(_breakdown[key]);
            table.Children.Add(row);
            if (key is "runtime" or "required") table.Children.Add(new Border { Height = 1, Background = Line });
        }

        var legend = Row(14, LegendDot(Accent, "Weights"), LegendDot(AccentDeep, "KV cache"), LegendDot(AmberInk, "Runtime"), LegendDot(Slate, "Headroom"));
        var results = CardBorder(Stack(14,
            _verdictPill, _sizingHeadline, _sizingSummary,
            Stack(6, Small("Needed"), _neededBar, Small("Usable after reserve"), _usableBar, legend),
            table,
            Set(Small(Sizing.Formula + ".  Working set = weights + KV cache (context tokens / 1,000 x allowance) + runtime allowance. Fit uses the full working set plus headroom against one budget view after the reserve."), t => t.FontFamily = Mono),
            CardBorder(Small(Sizing.Caveat, Ink), 12, Sand),
            Row(10, _sizingExport), _sizingInfo), 22);
        Id(_neededBar, "SizingNeededBar");
        Id(_usableBar, "SizingUsableBar");

        var compare = new AdaptiveGrid(200, 4, 12);
        foreach (var p in Sizing.Profiles)
        {
            var weight = Id(Text("-", 22, Ink, FontWeights.SemiBold), "SizingCompare_" + p.Size);
            var verdict = Id(Small(""), "SizingCompareVerdict_" + p.Size);
            _compare[p.Size] = (weight, verdict);
            compare.With(CardBorder(Stack(4,
                Row(8, Pill(p.Size.ToString(), AccentTint, AccentDeep), Text($"{p.Name}, {p.DefaultBillions:0.#}B", 13, Ink, FontWeights.SemiBold)),
                weight, Small("weights at the selected precision"), verdict, Small(p.FamilyExamples)), 16));
        }

        var observedCard = CardBorder(Stack(10,
            H3("Observed on this laptop (actual metadata, not an estimate)"),
            _observed), 22);
        _observed.Children.Add(Small("Waiting for the local service to report model metadata."));
        ResetSizing();

        _technicalView = Id(Stack(22,
            Stack(10, Eyebrow("T-shirt sizing planner - technical view"), H1("How much model fits? Size it with real arithmetic."),
                Body("Pick an illustrative size band, adjust precision, context and allowances, and check the working set against one memory budget view. Every number is visible and editable.")),
            TwoPane(inputs, results, 440, 1000),
            Stack(12, H2("All four sizes at these settings"), Small("Each size uses its default parameter count and KV allowance with your precision, context, runtime, budget, reserve and headroom."), compare),
            observedCard), "SizingTechnicalView");
        _salesView = Id(BuildSalesSizing(), "SizingSalesView");

        var salesTab = Id(new SelectorBarItem { Text = "Sales view", Icon = Glyph("\uE716", 16, Accent), Tag = "sales" }, "SizingTab_Sales");
        var technicalTab = Id(new SelectorBarItem { Text = "Technical view", Icon = Glyph("\uE9D9", 16, Accent), Tag = "technical" }, "SizingTab_Technical");
        _sizingTabs.Items.Add(salesTab);
        _sizingTabs.Items.Add(technicalTab);
        _sizingTabs.SelectionChanged += (_, _) => ApplySizingTab();
        _sizingTabs.SelectedItem = salesTab;
        ApplySizingTab();

        _sizingScroller = Page(Stack(18,
            Lead(16, _sizingTabs, Set(Small("Sales view for customer conversations. Technical view shows and edits every number."), t => { t.VerticalAlignment = VerticalAlignment.Center; t.TextWrapping = TextWrapping.Wrap; })),
            _salesView, _technicalView));
        return _sizingScroller;
    }

    internal bool SizingTechnicalVisible => _technicalView.Visibility == Visibility.Visible;

    internal void ShowSizingTab(bool technical)
    {
        var target = _sizingTabs.Items[technical ? 1 : 0];
        if (!ReferenceEquals(_sizingTabs.SelectedItem, target)) _sizingTabs.SelectedItem = target;
        ApplySizingTab();
    }

    private void ApplySizingTab()
    {
        bool technical = _sizingTabs.SelectedItem is SelectorBarItem { Tag: "technical" };
        if (_technicalView.Visibility == (technical ? Visibility.Visible : Visibility.Collapsed) && _salesView.Visibility == (technical ? Visibility.Collapsed : Visibility.Visible)) return;
        _technicalView.Visibility = technical ? Visibility.Visible : Visibility.Collapsed;
        _salesView.Visibility = technical ? Visibility.Collapsed : Visibility.Visible;
        _sizingScroller?.ChangeView(null, 0, null, true);
    }

    private static FrameworkElement Pair(FrameworkElement a, FrameworkElement b)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(a);
        Grid.SetColumn(b, 1);
        grid.Children.Add(b);
        a.VerticalAlignment = b.VerticalAlignment = VerticalAlignment.Bottom;
        return grid;
    }

    private static FrameworkElement LegendDot(Brush color, string label) =>
        Row(6, new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = color, VerticalAlignment = VerticalAlignment.Center }, Small(label));

    private void ApplySizePreset(SizeProfile profile)
    {
        _suppressSizing = true;
        _params.Value = profile.DefaultBillions;
        _kv.Value = profile.KvGiBPer1kTokens;
        _runtime.Value = profile.RuntimeOverheadGiB;
        _suppressSizing = false;
        _sizeFamily.Text = $"{profile.FamilyExamples}. Typical work: {profile.TypicalWork}. Band and examples are illustrative.";
        RecomputeSizing();
    }

    internal void ResetSizing()
    {
        _suppressSizing = true;
        _bits.SelectedIndex = Array.IndexOf(Sizing.SupportedBits, Sizing.DefaultBits);
        _context.Value = Sizing.DefaultContextTokens;
        _reserve.Value = Sizing.DefaultReserveGiB;
        _headroom.Value = Sizing.DefaultHeadroomGiB;
        _budget.Value = 32;
        _budgetSource.SelectedIndex = 0;
        _sizePicker.SelectedIndex = -1;
        _suppressSizing = false;
        _sizePicker.SelectedIndex = 2;
        SyncBudgetBox();
        RecomputeSizing();
    }

    private BudgetSource SelectedBudgetSource =>
        _budgetSource.SelectedItem is RadioButton { Tag: BudgetSource s } ? s : BudgetSource.GpuDriverBudget;

    private void SyncBudgetBox()
    {
        var source = SelectedBudgetSource;
        bool custom = source == BudgetSource.Custom;
        _budget.IsEnabled = custom;
        var device = _status?.Device;
        if (!custom)
        {
            double? resolved = Sizing.ResolveBudget(source, device, double.NaN);
            _suppressSizing = true;
            _budget.Value = resolved is double v ? Math.Round(v, 2) : double.NaN;
            _suppressSizing = false;
        }
        string installed = device?.InstalledGiB is double i ? $"{Math.Round(i):N0} GB installed physical memory" : "Installed memory";
        _budgetNote.Text = source switch
        {
            BudgetSource.GpuDriverBudget => device?.PrimaryGpuBudgetMiB is long mib
                ? $"Driver-reported GPU budget: {mib:N0} MiB = {mib / 1024d:F2} GiB. Best view for GPU inference here. Not dedicated VRAM."
                : "GPU budget not reported yet. Choose another view or enter a custom budget.",
            BudgetSource.WindowsVisibleMemory => device?.OsVisibleGiB is double w
                ? $"Windows-visible memory: {w:F2} GiB. A different view of the same machine; never added to the GPU budget."
                : "Windows-visible memory not reported yet.",
            _ => "Enter any budget, for example a customer device's memory. Useful for Snapdragon and Intel sizing conversations."
        } + $" {installed} is shown for context and is not used as a budget.";
    }

    private void ApplySizingStatus(StatusSnapshot status)
    {
        if (_sizePicker.SelectedIndex < 0) ResetSizing();
        if (SelectedBudgetSource != BudgetSource.Custom) SyncBudgetBox();
        RecomputeSizing();
        RecomputeSales();
        _observed.Children.Clear();
        var d = status.Device;
        _observed.Children.Add(Body($"Memory views: {Gb(d.InstalledGiB)} installed physical  |  {d.OsVisibleGiB:F1} GiB Windows-visible  |  " +
            (d.PrimaryGpuBudgetMiB is long mib ? $"{mib:N0} MiB GPU budget" : "GPU budget unavailable") + ". Distinct views; never summed.", Ink));
        foreach (var m in status.Engine.Models)
        {
            bool loaded = status.Engine.Model?.Alias == m.Alias;
            string size = m.SizeMb is double mb ? (m.IsLlamaCpp ? $"{mb / 1000:N1} GB weights file" : $"{mb:N0} MB catalog file size") : "no local file";
            string detail = $"{m.Label}  |  {size}  |  {m.Provider}" + (m.Available ? "" : $"  |  {m.Blocker}") +
                (string.IsNullOrWhiteSpace(m.Published) ? "" : $"  {m.Published}");
            _observed.Children.Add(Lead(10,
                Pill(loaded ? "Loaded" : !m.Available ? "Unavailable" : m.Cached ? "Cached" : "Not cached",
                    loaded ? GreenTint : Sand, loaded ? GreenInk : Muted),
                Set(Text(detail, 13, Ink), t => t.TextWrapping = TextWrapping.Wrap)));
        }
        _observed.Children.Add(Small("File size is the model on disk, not measured runtime memory. This app does not measure runtime memory use; the planner above remains an estimate. One chat model is selected at a time."));
    }

    private SizingInput ReadSizingInput()
    {
        int bits = _bits.SelectedItem is ComboBoxItem { Tag: int b } ? b : 0;
        double budget = SelectedBudgetSource == BudgetSource.Custom
            ? _budget.Value
            : Sizing.ResolveBudget(SelectedBudgetSource, _status?.Device, double.NaN) ?? double.NaN;
        return new SizingInput(_params.Value, bits, _context.Value, _kv.Value, _runtime.Value, budget, _reserve.Value, _headroom.Value);
    }

    internal void RecomputeSizing()
    {
        if (_suppressSizing) return;
        var input = ReadSizingInput();
        var result = Sizing.Estimate(input);
        _lastSizing = result;
        _lastSizingInput = input;
        _verdictText.Text = result.Verdict switch
        {
            SizingVerdict.Fits => "FITS - ESTIMATE",
            SizingVerdict.Tight => "TIGHT FIT - ESTIMATE",
            SizingVerdict.DoesNotFit => "DOES NOT FIT - ESTIMATE",
            _ => "INVALID INPUT"
        };
        (_verdictPill.Background, _verdictText.Foreground) = result.Verdict switch
        {
            SizingVerdict.Fits => ((Brush)GreenTint, (Brush)GreenInk),
            SizingVerdict.Tight => (AmberTint, AmberInk),
            SizingVerdict.DoesNotFit => (AccentTint, AccentDeep),
            _ => (Sand, Muted)
        };
        _sizingHeadline.Text = result.Headline;
        _sizingSummary.Text = result.Summary;
        string F(double v) => double.IsFinite(v) ? $"{v:F2} GiB" : "-";
        _breakdown["weights"].Text = F(result.WeightGiB);
        _breakdown["kv"].Text = F(result.KvGiB);
        _breakdown["runtime"].Text = result.IsValid ? F(input.RuntimeOverheadGiB) : "-";
        _breakdown["working"].Text = F(result.WorkingSetGiB);
        _breakdown["headroom"].Text = result.IsValid ? F(input.HeadroomGiB) : "-";
        _breakdown["required"].Text = F(result.RequiredGiB);
        _breakdown["usable"].Text = F(result.UsableGiB);
        _breakdown["margin"].Text = double.IsFinite(result.MarginGiB) ? (result.MarginGiB >= 0 ? "+" : "") + F(result.MarginGiB) : "-";
        _breakdown["margin"].Foreground = result.Verdict == SizingVerdict.DoesNotFit ? AccentDeep : Ink;
        _sizingExport.IsEnabled = true;
        DrawBars(result, input);

        foreach (var p in Sizing.Profiles)
        {
            var r = Sizing.Estimate(input with { ParameterBillions = p.DefaultBillions, KvGiBPer1kTokens = p.KvGiBPer1kTokens });
            var (weight, verdict) = _compare[p.Size];
            weight.Text = double.IsFinite(r.WeightGiB) ? $"{r.WeightGiB:F1} GiB" : "-";
            verdict.Text = r.IsValid ? $"{r.Headline}: needs {r.RequiredGiB:F1} of {r.UsableGiB:F1} GiB" : "Check the inputs";
            verdict.Foreground = r.Verdict == SizingVerdict.DoesNotFit ? AccentDeep : r.FitsEstimate ? GreenInk : Muted;
        }
    }

    private void DrawBars(SizingResult result, SizingInput input)
    {
        _neededBar.Children.Clear(); _neededBar.ColumnDefinitions.Clear();
        _usableBar.Children.Clear(); _usableBar.ColumnDefinitions.Clear();
        _neededBar.Background = _usableBar.Background = Sand;
        if (!result.IsValid) return;
        double scale = Math.Max(result.UsableGiB, result.RequiredGiB);
        if (scale <= 0) return;
        void Segment(Grid bar, double value, Brush brush)
        {
            if (value <= 0) return;
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(value / scale, GridUnitType.Star) });
            var b = new Border { Background = brush };
            Grid.SetColumn(b, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(b);
        }
        Segment(_neededBar, result.WeightGiB, Accent);
        Segment(_neededBar, result.KvGiB, AccentDeep);
        Segment(_neededBar, input.RuntimeOverheadGiB, AmberInk);
        Segment(_neededBar, input.HeadroomGiB, Slate);
        Segment(_neededBar, scale - result.RequiredGiB, Sand);
        Segment(_usableBar, result.UsableGiB, result.FitsEstimate ? GreenInk : Muted);
        Segment(_usableBar, scale - result.UsableGiB, Sand);
    }

    private void ExportSizing()
    {
        if (_lastSizing is null || _lastSizingInput is null) return;
        try
        {
            var size = _sizePicker.SelectedIndex >= 0 ? Sizing.Profiles[_sizePicker.SelectedIndex].Size : (TShirtSize?)null;
            string path = Evidence.Save(Evidence.SizingExport(_lastSizingInput, _lastSizing, size, SelectedBudgetSource), ExportDirectory, "sizing-estimate");
            _sizingInfo.Severity = InfoBarSeverity.Success; _sizingInfo.Title = "Estimate exported"; _sizingInfo.Message = path; _sizingInfo.IsOpen = true;
        }
        catch (Exception exception)
        {
            _sizingInfo.Severity = InfoBarSeverity.Error; _sizingInfo.Title = "Export failed"; _sizingInfo.Message = exception.Message; _sizingInfo.IsOpen = true;
        }
    }
}
