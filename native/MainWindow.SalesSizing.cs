using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, (CheckBox Box, Border Card)> _salesWorkloads = [];
    private readonly HashSet<string> _salesSelected = new(SalesSizing.DefaultSelection, StringComparer.Ordinal);
    private readonly Border _salesVerdictPill = Id(new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 4, 12, 5), HorizontalAlignment = HorizontalAlignment.Left }, "SalesVerdict");
    private readonly TextBlock _salesVerdictText = Id(Text("", 13, Ink, FontWeights.SemiBold), "SalesVerdictText");
    private readonly TextBlock _salesRecommendation = Id(H2(""), "SalesRecommendation");
    private readonly TextBlock _salesModelClass = Id(Small(""), "SalesModelClass");
    private readonly TextBlock _salesTalkTrack = Id(Set(Text("", 16, Ink), t => { t.LineHeight = 24; t.TextWrapping = TextWrapping.Wrap; }), "SalesTalkTrack");
    private readonly ProgressBar _salesUsage = Id(new ProgressBar { Minimum = 0, Maximum = 100, Height = 10, Foreground = Accent }, "SalesUsageBar");
    private readonly TextBlock _salesUsageText = Id(Small(""), "SalesUsageText");
    private readonly Dictionary<string, (TextBlock Verdict, TextBlock Detail, Border Card)> _salesDevices = [];
    private readonly StackPanel _salesRows = Id(Stack(10), "SalesWorkloadRows");
    private readonly StackPanel _salesDetails = Stack(18);
    private readonly Button _salesTechnical = Id(Primary("See the technical math"), "SalesSeeTechnical");
    private readonly Button _salesCopy = Id(Secondary("Copy talk track"), "SalesCopyTalkTrack");
    private readonly TextBlock _salesSelectionCount = Id(Small(""), "SalesSelectionCount");
    private SalesRecommendation? _lastSales;

    private FrameworkElement BuildSalesSizing()
    {
        _salesVerdictPill.Child = _salesVerdictText;
        var tiles = new AdaptiveGrid(260, 3, 12);
        foreach (var w in SalesSizing.Workloads)
        {
            var box = Id(new CheckBox
            {
                IsChecked = _salesSelected.Contains(w.Id),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0,
                Content = Stack(4,
                    Row(8, Glyph(w.Glyph, 16, Accent), Set(Text(w.Name, 14, Ink, FontWeights.SemiBold), t => t.TextWrapping = TextWrapping.Wrap)),
                    Set(Small(w.Plain), t => t.TextWrapping = TextWrapping.Wrap),
                    Row(6, Pill(w.Size.ToString(), AccentTint, AccentDeep), Pill(w.LiveOnThisLaptop ? "LIVE DEMO HERE" : "GUIDANCE ONLY", w.LiveOnThisLaptop ? GreenTint : Sand, w.LiveOnThisLaptop ? GreenInk : Muted)))
            }, "SalesWorkload_" + w.Id);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, w.Name);
            var card = CardBorder(box, 14);
            string id = w.Id;
            box.Checked += (_, _) => { _salesSelected.Add(id); RecomputeSales(); };
            box.Unchecked += (_, _) => { _salesSelected.Remove(id); RecomputeSales(); };
            _salesWorkloads[w.Id] = (box, card);
            tiles.With(card);
        }
        var clear = Id(Secondary("Clear selection"), "SalesClear");
        clear.Click += (_, _) => SetSalesSelection([]);

        var deviceGrid = new AdaptiveGrid(220, 3, 12);
        foreach (var device in new[] { SalesSizing.ThisLaptop }.Concat(SalesSizing.Comparisons.Select(c => c.Device)))
        {
            var verdict = Id(Set(Text("-", 20, Ink, FontWeights.SemiBold), t => t.FontFamily = Display), "SalesDeviceFit_" + device.Id);
            var detail = Id(Small(""), "SalesDeviceDetail_" + device.Id);
            var card = CardBorder(Stack(4, Text(device.Name, 14, Ink, FontWeights.SemiBold), verdict, detail), 16, device.Id == "this" ? AccentTint : Paper);
            _salesDevices[device.Id] = (verdict, detail, card);
            deviceGrid.With(card);
        }

        _salesTechnical.Click += (_, _) => OpenTechnicalFromSales();
        _salesCopy.Click += (_, _) =>
        {
            if (_lastSales is null) return;
            var package = new DataPackage();
            package.SetText(_lastSales.TalkTrack);
            Clipboard.SetContent(package);
        };

        _salesDetails.Children.Add(CardBorder(Stack(8, Text("SAY IT LIKE THIS", 11, AccentDeep, FontWeights.SemiBold), _salesTalkTrack), 18, Sand));
        _salesDetails.Children.Add(Stack(6, Text("Memory used on this Surface Laptop Ultra", 13, Ink, FontWeights.SemiBold), _salesUsage, _salesUsageText));
        _salesDetails.Children.Add(Stack(10, H3("Where this size fits (memory-only estimate)"), deviceGrid));
        _salesDetails.Children.Add(Stack(10, H3("Selected workloads"), _salesRows));
        _salesDetails.Children.Add(Row(10, _salesTechnical, _salesCopy));

        var results = CardBorder(Stack(14,
            _salesVerdictPill, _salesRecommendation, _salesModelClass, _salesDetails,
            Small(SalesSizing.Footnote)), 24);
        Id(results, "SalesResults");

        RecomputeSales();
        return Stack(22,
            Stack(10, Eyebrow("T-shirt sizing - sales view"), H1("What does the customer want AI to do?"),
                Body("Pick the everyday workloads the customer cares about. The studio picks the model size they need and shows where it fits, with no technical inputs.")),
            CardBorder(Stack(12,
                Lead(12, H3("Pick the customer's workloads"), Set(Row(12, _salesSelectionCount, clear), r => r.HorizontalAlignment = HorizontalAlignment.Right)),
                tiles), 22),
            results);
    }

    internal void SetSalesSelection(IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var (id, (box, _)) in _salesWorkloads)
        {
            bool on = wanted.Contains(id);
            if (box.IsChecked != on) box.IsChecked = on;
        }
        _salesSelected.Clear();
        foreach (var id in wanted.Where(_salesWorkloads.ContainsKey)) _salesSelected.Add(id);
        RecomputeSales();
    }

    private void OpenTechnicalFromSales()
    {
        if (_lastSales is null) return;
        ResetSizing();
        _sizePicker.SelectedIndex = Array.FindIndex(Sizing.Profiles, p => p.Size == _lastSales.Profile.Size);
        ShowSizingTab(technical: true);
    }

    internal void RecomputeSales()
    {
        foreach (var (id, (_, card)) in _salesWorkloads)
        {
            bool on = _salesSelected.Contains(id);
            card.BorderBrush = on ? Accent : Line;
            card.BorderThickness = new Thickness(on ? 2 : 1);
            card.Background = on ? Paper : Card;
        }
        _salesSelectionCount.Text = _salesSelected.Count == 1 ? "1 selected" : $"{_salesSelected.Count} selected";
        var rec = SalesSizing.Recommend(_salesSelected, _status?.Device);
        _lastSales = rec;
        if (rec is null)
        {
            _salesVerdictText.Text = "PICK A WORKLOAD";
            (_salesVerdictPill.Background, _salesVerdictText.Foreground) = (Sand, Muted);
            _salesRecommendation.Text = "Pick at least one workload to size it.";
            _salesModelClass.Text = "";
            _salesDetails.Visibility = Visibility.Collapsed;
            _salesTechnical.IsEnabled = _salesCopy.IsEnabled = false;
            return;
        }

        _salesDetails.Visibility = Visibility.Visible;
        _salesTechnical.IsEnabled = _salesCopy.IsEnabled = true;
        var here = rec.ThisLaptop;
        _salesVerdictText.Text = here.Fit switch
        {
            SalesFit.Comfortable => "COMFORTABLE ON THIS LAPTOP - ESTIMATE",
            SalesFit.Tight => "TIGHT FIT ON THIS LAPTOP - ESTIMATE",
            SalesFit.WontFit => "WON'T FIT ON THIS LAPTOP - ESTIMATE",
            _ => "WAITING FOR THIS LAPTOP'S MEMORY REPORT"
        };
        (_salesVerdictPill.Background, _salesVerdictText.Foreground) = FitColors(here.Fit);
        _salesRecommendation.Text = $"Recommended: {rec.Profile.Name} ({rec.Profile.Size}) model";
        _salesModelClass.Text = $"Typical model class: {rec.Profile.FamilyExamples}. {rec.Profile.TypicalWork}.";
        _salesTalkTrack.Text = rec.TalkTrack;
        _salesUsage.Value = double.IsFinite(here.PercentUsed) ? Math.Clamp(here.PercentUsed, 0, 100) : 0;
        _salesUsage.Foreground = here.Fit == SalesFit.Comfortable ? GreenInk : here.Fit == SalesFit.Tight ? AmberInk : Accent;
        _salesUsageText.Text = double.IsFinite(here.PercentUsed)
            ? $"About {Math.Round(here.PercentUsed):0}% of the memory available for AI after Windows and apps (estimate)."
            : "Waiting for the local service to report this laptop's GPU memory budget.";

        foreach (var fit in new[] { here }.Concat(rec.Comparisons))
        {
            var (verdict, detail, _) = _salesDevices[fit.Device.Id];
            verdict.Text = SalesSizing.FitLabel(fit.Fit);
            verdict.Foreground = FitColors(fit.Fit).Item2;
            detail.Text = double.IsFinite(fit.PercentUsed)
                ? (fit.PercentUsed <= 100 ? $"Uses about {Math.Round(fit.PercentUsed):0}% of available memory." : "Needs more memory than is available.") + " " + fit.Device.Detail + "."
                : fit.Device.Detail + ".";
        }

        _salesRows.Children.Clear();
        foreach (var w in rec.Workloads)
        {
            var actions = Row(8, Pill(w.LiveOnThisLaptop ? "LIVE DEMO HERE" : "GUIDANCE ONLY", w.LiveOnThisLaptop ? GreenTint : Sand, w.LiveOnThisLaptop ? GreenInk : Muted));
            if (w.LiveScenarioKey is string key)
            {
                var show = Id(Secondary("Show it live"), "SalesLive_" + w.Id);
                show.Padding = new Thickness(10, 4, 10, 5);
                show.FontSize = 12;
                show.IsEnabled = _scenarios.ContainsKey(key);
                show.Click += (_, _) => { if (SelectScenario(key)) Navigate("live"); };
                actions.Children.Add(show);
            }
            else if (w.LiveOnThisLaptop)
            {
                var open = Id(Secondary("Open Live GPU workspace"), "SalesLive_" + w.Id);
                open.Padding = new Thickness(10, 4, 10, 5);
                open.FontSize = 12;
                open.Click += (_, _) => Navigate("live");
                actions.Children.Add(open);
            }
            _salesRows.Children.Add(Id(new Border
            {
                BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0),
                Child = Lead(12, Pill(w.Size.ToString(), AccentTint, AccentDeep), Stack(4,
                    Text(w.Name, 14, Ink, FontWeights.SemiBold),
                    Set(Small(SalesSizing.PlatformFit(w), Ink), t => t.TextWrapping = TextWrapping.Wrap),
                    Set(Small(w.LiveNote), t => t.TextWrapping = TextWrapping.Wrap),
                    actions))
            }, "SalesRow_" + w.Id));
        }
    }

    private static (Brush, Brush) FitColors(SalesFit fit) => fit switch
    {
        SalesFit.Comfortable => (GreenTint, GreenInk),
        SalesFit.Tight => (AmberTint, AmberInk),
        SalesFit.WontFit => (AccentTint, AccentDeep),
        _ => (Sand, Muted)
    };
}
