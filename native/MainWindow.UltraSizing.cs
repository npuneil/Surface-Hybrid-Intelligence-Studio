using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private readonly ToggleSwitch _ultraTogether = Id(new ToggleSwitch
    {
        Header = "AI models in use", OffContent = "One at a time", OnContent = "All open together", VerticalAlignment = VerticalAlignment.Top
    }, "UltraTogether");
    private readonly ToggleSwitch _ultraLong = Id(new ToggleSwitch
    {
        Header = "Document length", OffContent = "Standard", OnContent = "Long documents (32K)", VerticalAlignment = VerticalAlignment.Top
    }, "UltraLongDocuments");
    private readonly RadioButtons _ultraBasis = Id(new RadioButtons { Header = "GPU share of each configuration's memory" }, "UltraBasis");
    private readonly NumberBox _ultraCustom = Id(new NumberBox
    {
        Header = "Custom GPU share (%)",
        Minimum = UltraSizing.MinCustomPercent,
        Maximum = UltraSizing.MaxCustomPercent,
        Value = UltraSizing.DefaultCustomPercent,
        SmallChange = 1,
        LargeChange = 5,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        ValidationMode = NumberBoxValidationMode.Disabled,
        Width = 220,
        HorizontalAlignment = HorizontalAlignment.Left,
    }, "UltraCustomShare");
    private readonly Border _ultraPill = Id(new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 4, 12, 5), HorizontalAlignment = HorizontalAlignment.Left }, "UltraVerdict");
    private readonly TextBlock _ultraPillText = Id(Text("", 13, Ink, FontWeights.SemiBold), "UltraVerdictText");
    private readonly TextBlock _ultraRecommendation = Id(H2(""), "UltraRecommendation");
    private readonly TextBlock _ultraTalkTrack = Id(Set(Text("", 16, Ink), t => { t.LineHeight = 24; t.TextWrapping = TextWrapping.Wrap; }), "UltraTalkTrack");
    private readonly TextBlock _ultraBasisNote = Id(Small(""), "UltraBasisNote");
    private readonly Button _ultraCopy = Id(Secondary("Copy talk track"), "UltraCopyTalkTrack");
    private readonly StackPanel _ultraBody = Id(Stack(16), "UltraBody");
    private readonly Dictionary<int, (Border Card, TextBlock Fit, ProgressBar Bar, TextBlock Detail, TextBlock Here)> _ultraCards = [];
    private UltraPlan? _lastUltra;
    private bool _ultraReady;

    private SplitBasis SelectedUltraBasis =>
        _ultraBasis.SelectedItem is RadioButton { Tag: SplitBasis basis } ? basis : SplitBasis.MicrosoftLaunch;

    private FrameworkElement BuildUltraPlanner()
    {
        _ultraPill.Child = _ultraPillText;
        _ultraBasis.Items.Add(Id(new RadioButton { Content = $"Microsoft launch unit ({UltraSizing.LaunchShare * 100:0}%)", Tag = SplitBasis.MicrosoftLaunch }, "UltraBasis_Microsoft"));
        _ultraBasis.Items.Add(Id(new RadioButton { Content = "This PC's measured split", Tag = SplitBasis.MeasuredThisPc }, "UltraBasis_Measured"));
        _ultraBasis.Items.Add(Id(new RadioButton { Content = "Custom share", Tag = SplitBasis.Custom }, "UltraBasis_Custom"));
        _ultraBasis.SelectedIndex = 0;

        var grid = new AdaptiveGrid(170, 5, 12);
        foreach (var config in UltraSizing.Configurations)
        {
            int gb = config.MemoryGB;
            var fit = Id(Text("-", 15, Ink, FontWeights.SemiBold), $"UltraFit_{gb}");
            var bar = Id(new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Foreground = Accent }, $"UltraBar_{gb}");
            var detail = Id(Small(""), $"UltraDetail_{gb}");
            var here = Id(Small("", Ink), $"UltraHere_{gb}");
            var card = Id(CardBorder(Stack(6,
                Set(Text($"{gb} GB", 24, Ink, FontWeights.SemiBold), t => t.FontFamily = Display),
                fit, bar, detail, Small(config.Processor), here), 16), $"UltraConfig_{gb}");
            _ultraCards[gb] = (card, fit, bar, detail, here);
            grid.With(card);
        }

        _ultraTogether.Toggled += (_, _) => RecomputeUltra();
        _ultraLong.Toggled += (_, _) => RecomputeUltra();
        _ultraBasis.SelectionChanged += (_, _) => RecomputeUltra();
        _ultraCustom.ValueChanged += (_, _) => RecomputeUltra();
        _ultraCopy.Click += (_, _) =>
        {
            if (_lastUltra is not { TalkTrack.Length: > 0 }) return;
            var package = new DataPackage();
            package.SetText(_lastUltra.TalkTrack);
            Clipboard.SetContent(package);
        };

        var specs = Id(new HyperlinkButton { Content = "Surface Laptop Ultra tech specs (Microsoft Learn)  \u2197", Padding = new Thickness(0, 2, 0, 2), Foreground = Accent }, "UltraSpecLink");
        ToolTipService.SetToolTip(specs, ApprovedLinks.SurfaceLaptopUltraSpecs);
        specs.Click += async (_, _) =>
        {
            if (ApprovedLinks.IsApproved(ApprovedLinks.SurfaceLaptopUltraSpecs))
                await Windows.System.Launcher.LaunchUriAsync(new Uri(ApprovedLinks.SurfaceLaptopUltraSpecs));
        };

        _ultraBody.Children.Add(grid);
        _ultraBody.Children.Add(CardBorder(Stack(8, Text("SAY IT LIKE THIS", 11, AccentDeep, FontWeights.SemiBold), _ultraTalkTrack, _ultraCopy), 18, Sand));

        var controls = new AdaptiveGrid(240, 3, 16).With(_ultraTogether, _ultraLong, Stack(8, _ultraBasis, _ultraCustom));
        var planner = Id(CardBorder(Stack(16,
            Stack(8, Eyebrow("Surface Laptop Ultra configurations"), H2("Which memory configuration fits these workloads?"),
                Body("Uses the workloads picked above. Windows and the RTX GPU share the laptop's memory: part of it is set aside as dedicated GPU memory, " +
                     "and that share decides how large an AI model fits. Each card runs the same estimate against that configuration's GPU share.")),
            controls,
            _ultraPill, _ultraRecommendation,
            _ultraBody,
            _ultraBasisNote,
            Small(UltraSizing.Footnote),
            specs), 24), "UltraPlanner");
        _ultraReady = true;
        return planner;
    }

    internal void RecomputeUltra()
    {
        if (!_ultraReady) return;
        var device = _status?.Device;
        if (_ultraBasis.Items[1] is RadioButton measured) measured.IsEnabled = UltraSizing.MeasuredShare(device) is not null;
        var basis = SelectedUltraBasis;
        _ultraCustom.Visibility = basis == SplitBasis.Custom ? Visibility.Visible : Visibility.Collapsed;
        _ultraBasisNote.Text = UltraSizing.BasisNote(basis, device, _ultraCustom.Value);
        var plan = UltraSizing.Plan(_salesSelected, _ultraTogether.IsOn, _ultraLong.IsOn, basis, _ultraCustom.Value, device);
        _lastUltra = plan;
        if (plan is null)
        {
            _ultraPillText.Text = "PICK A WORKLOAD";
            (_ultraPill.Background, _ultraPillText.Foreground) = (Sand, Muted);
            _ultraRecommendation.Text = "Pick at least one workload above to size a configuration.";
            _ultraBody.Visibility = Visibility.Collapsed;
            _ultraCopy.IsEnabled = false;
            return;
        }

        bool waiting = double.IsNaN(plan.GpuShare);
        _ultraBody.Visibility = Visibility.Visible;
        _ultraCopy.IsEnabled = plan.TalkTrack.Length > 0;
        _ultraRecommendation.Text = plan.Headline;
        _ultraTalkTrack.Text = waiting ? "Choose a GPU share to size the configurations." : plan.TalkTrack;
        var summary = waiting ? SalesFit.Unknown
            : plan.Recommended is not null ? SalesFit.Comfortable
            : plan.SmallestThatHolds is not null ? SalesFit.Tight
            : SalesFit.WontFit;
        _ultraPillText.Text = summary switch
        {
            SalesFit.Comfortable => "RECOMMENDED CONFIGURATION - ESTIMATE",
            SalesFit.Tight => "LITTLE ROOM TO SPARE - ESTIMATE",
            SalesFit.WontFit => "NO CONFIGURATION HOLDS THIS - ESTIMATE",
            _ => "WAITING FOR A GPU SHARE"
        };
        (_ultraPill.Background, _ultraPillText.Foreground) = FitColors(summary);

        int? thisPc = UltraSizing.ThisPcConfig(device);
        foreach (var fit in plan.Configs)
        {
            var (card, fitText, bar, detail, here) = _ultraCards[fit.Config.MemoryGB];
            bool recommended = fit.Config.MemoryGB == plan.Recommended?.Config.MemoryGB;
            var tone = fit.Fit switch
            {
                UltraFit.Roomy => SalesFit.Comfortable,
                UltraFit.LittleRoom => SalesFit.Tight,
                UltraFit.WontFit => SalesFit.WontFit,
                _ => SalesFit.Unknown
            };
            fitText.Text = UltraSizing.FitLabel(fit, plan.Recommended);
            fitText.Foreground = FitColors(tone).Item2;
            card.Background = recommended ? GreenTint : Paper;
            card.BorderBrush = recommended ? GreenInk : Line;
            card.BorderThickness = new Thickness(recommended ? 2 : 1);
            bar.Value = double.IsFinite(fit.PercentUsed) ? Math.Clamp(fit.PercentUsed, 0, 100) : fit.Fit == UltraFit.WontFit ? 100 : 0;
            bar.Foreground = tone switch { SalesFit.Comfortable => GreenInk, SalesFit.Tight => AmberInk, _ => Accent };
            detail.Text = !double.IsFinite(fit.GpuGiB) ? "Waiting for a GPU share."
                : $"GPU about {fit.GpuGiB:0.#} GB, Windows about {fit.WindowsGiB:0.#} GB. " + (double.IsFinite(fit.PercentUsed)
                    ? fit.PercentUsed <= 100 ? $"Uses about {Math.Round(fit.PercentUsed):0}% of the memory left for AI." : "Needs more than the memory left for AI."
                    : "Nothing is left for AI after the reserve.");
            bool isThisPc = thisPc == fit.Config.MemoryGB;
            here.Visibility = isThisPc ? Visibility.Visible : Visibility.Collapsed;
            here.Text = !isThisPc ? ""
                : fit.MeasuredHere ? "This PC: GPU share as measured by its driver."
                : device?.PrimaryGpuBudgetMiB is long mib ? $"This PC has {fit.Config.MemoryGB} GB; its driver reports {mib / 1024d:0.#} GB for the GPU."
                : $"This PC has {fit.Config.MemoryGB} GB.";
        }
    }
}
