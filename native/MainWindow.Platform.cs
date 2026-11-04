using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private readonly Dictionary<string, Button> _platformTry = [];
    private readonly Dictionary<string, PlatformCardView> _platformCards = [];
    private TextBlock _platformBanner = null!;
    private readonly Dictionary<string, NumberBox> _businessInputs = [];
    private readonly Button _calculate = Id(Primary("Calculate"), "BusinessCalculate");
    private readonly Button _businessExport = Id(Secondary("Export business case (JSON)"), "BusinessExport");
    private readonly InfoBar _businessError = Id(new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, Title = "Check the assumptions" }, "BusinessError");
    private readonly InfoBar _businessInfo = Id(new InfoBar { Severity = InfoBarSeverity.Success, IsClosable = true }, "BusinessExportInfo");
    private TextBlock _bHours = null!, _bCapacity = null!, _bInvestment = null!;
    private readonly TextBlock _bDisclaimer = Id(Small(""), "BusinessDisclaimer");
    private readonly TextBlock _bState = Id(Small("Enter the firm's assumptions, then calculate."), "BusinessState");
    private JsonDocument? _lastBusiness;
    private bool _businessDefaultsApplied;
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The parts of a platform card that follow the PC shown: which card is live, its status line and its live-run buttons.</summary>
    private sealed record PlatformCardView(Border Band, PillView Status, TextBlock StatusLine, TextBlock VerifyHeader, IReadOnlyList<Button> LiveButtons);

    private FrameworkElement BuildPlatforms()
    {
        _platformBanner = Id(Body(PlatformGuide.NoInferenceHere(_words, false), Ink), "PlatformNoInferenceText");
        var banner = CardBorder(Lead(12, Glyph("\uE946", 18), _platformBanner), 16, SlateTint);
        Id(banner, "PlatformNoInferenceNotice");
        var cards = new AdaptiveGrid(340, 3, 18);
        foreach (var p in PlatformGuide.Platforms) cards.With(PlatformCard(p));

        var distinctions = CardBorder(Stack(10,
            H3("Runtime distinctions to keep straight"),
            Body(PlatformGuide.FoundryDistinction, Ink),
            Body(PlatformGuide.GenieDistinction, Ink),
            Body(PlatformGuide.RuntimeNotInferred, Ink),
            Body("Vendor memory guidance for a specific model is not the same as this app's generic planner estimate; use both, and verify on the target SKU.", Ink)), 22);
        Id(distinctions, "PlatformDistinctions");

        return Page(Stack(22,
            Stack(10, Eyebrow("Platform fit"), H1("The right device for each workflow."),
                Body("NVIDIA RTX for heavier local models and prototyping; Snapdragon and Intel Core Ultra for small models, mobility and per-SKU validation. " +
                     "If this PC's processor has a card, it is marked live and its text workflows can run here; the others are recommended pilots. Use cases are illustrative until verified.")),
            banner, cards, distinctions));
    }

    private FrameworkElement PlatformCard(PlatformProfile p)
    {
        var band = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Slate };
        var status = new PillView($"PlatformStatus_{p.Id}");
        status.Set("RECOMMENDED PILOT - ILLUSTRATIVE", SlateTint, Slate);
        var statusLine = Id(Small(p.Status), $"PlatformStatusLine_{p.Id}");
        var body = Stack(12, band, status.Root, Id(H2(p.Name), $"PlatformName_{p.Id}"), Body(p.Tagline, Ink), statusLine,
            CardBorder(Stack(4, Text("ACCELERATOR AND RUNTIME", 11, Muted, FontWeights.SemiBold), Small(p.AcceleratorAndRuntime, Ink)), 12, Paper));

        var liveButtons = new List<Button>();
        for (int i = 0; i < p.UseCases.Length; i++)
        {
            var u = p.UseCases[i];
            var actions = Row(8);
            var size = Id(new HyperlinkButton { Content = $"Size it ({u.Size})", Padding = new Thickness(0), Foreground = Accent }, $"PlatformSize_{p.Id}_{i}");
            var sizeIndex = Array.FindIndex(Sizing.Profiles, x => x.Size == u.Size);
            size.Click += (_, _) => { Navigate("sizing"); ShowSizingTab(technical: true); _sizePicker.SelectedIndex = sizeIndex; };
            actions.Children.Add(size);
            if (u.LiveScenarioKey is string key)
            {
                var tryLive = Id(Secondary(TryLiveLabel), $"PlatformTry_{key}");
                tryLive.Padding = new Thickness(10, 4, 10, 5);
                tryLive.FontSize = 12;
                tryLive.IsEnabled = false;
                tryLive.Click += (_, _) => { if (SelectScenario(key)) Navigate("live"); };
                _platformTry[key] = tryLive;
                liveButtons.Add(tryLive);
                actions.Children.Add(tryLive);
            }
            body.Children.Add(new Border
            {
                BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0),
                Child = Stack(6,
                    Lead(8, Pill(u.Size.ToString(), AccentTint, AccentDeep), Set(Text(u.Title, 14, Ink, FontWeights.SemiBold), t => t.TextWrapping = TextWrapping.Wrap)),
                    Body(u.Workflow), Small(u.Caveat, AmberInk), actions)
            });
        }

        var verify = Stack(4);
        foreach (string v in p.VerifyBeforePilot) verify.Children.Add(Small("- " + v, Ink));
        var verifyHeader = Text("Verify before a pilot", 13, Ink, FontWeights.SemiBold);
        body.Children.Add(Id(new Expander
        {
            Header = verifyHeader,
            Content = verify, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
        }, $"PlatformVerify_{p.Id}"));

        var sources = Stack(2, Text("OFFICIAL SOURCES", 11, Muted, FontWeights.SemiBold));
        for (int i = 0; i < p.Sources.Length; i++)
        {
            var s = p.Sources[i];
            var link = Id(new HyperlinkButton { Content = s.Label + "  \u2197", Padding = new Thickness(0, 2, 0, 2), Foreground = Accent }, $"PlatformSource_{p.Id}_{i}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(link, s.Url);
            ToolTipService.SetToolTip(link, s.Url);
            link.Click += async (_, _) =>
            {
                if (ApprovedLinks.IsApproved(s.Url)) await Windows.System.Launcher.LaunchUriAsync(new Uri(s.Url));
            };
            sources.Children.Add(link);
        }
        body.Children.Add(sources);
        _platformCards[p.Id] = new PlatformCardView(band, status, statusLine, verifyHeader, liveButtons);
        return Id(CardBorder(body, 22), $"PlatformCard_{p.Id}");
    }

    private const string TryLiveLabel = "Try the text workflow live on this PC";

    /// <summary>The card for the processor that runs this PC's models is the live one. While a Surface is previewed it is the previewed
    /// Surface's card, marked as an estimate, and nothing can be run.</summary>
    private void ApplyPlatformStatus(StatusSnapshot status)
    {
        var target = status.Target is { Error: null } t ? t : null;
        bool preview = target?.Simulated == true;
        string? current = target?.Platform;
        var words = status.Words;
        _platformBanner.Text = PlatformGuide.NoInferenceHere(words, preview);
        foreach (var p in PlatformGuide.Platforms)
        {
            if (!_platformCards.TryGetValue(p.Id, out var view)) continue;
            bool isCurrent = p.Id == current;
            view.Band.Background = isCurrent ? (preview ? Slate : Accent) : Slate;
            if (!isCurrent) view.Status.Set("RECOMMENDED PILOT - ILLUSTRATIVE", SlateTint, Slate);
            else if (preview) view.Status.Set("PREVIEWED SURFACE - ESTIMATE", AmberTint, AmberInk);
            else view.Status.Set("LIVE ON THIS PC", GreenTint, GreenInk);
            view.StatusLine.Text = !isCurrent ? p.Status
                : preview ? $"An estimate for {target!.Label}: its models would run {On(words)}. Nothing runs on the previewed Surface."
                : status.Engine.Model is { } model
                    ? $"Running live on this PC: {model.Name} {On(words)}" + (string.IsNullOrWhiteSpace(model.Ep) ? "" : $" through the {model.Ep} execution provider") + ", via Foundry Local."
                    : $"This PC runs its models {On(words)} through {words.Provider}. The first live run loads the model.";
            view.VerifyHeader.Text = isCurrent && !preview && p.Id == "rtx" ? "What is verified here" : "Verify before a pilot";
            foreach (var button in view.LiveButtons) button.Content = isCurrent && !preview ? "Run this live" : TryLiveLabel;
        }
        UpdatePlatformTryButtons();
    }

    private void UpdatePlatformTryButtons()
    {
        bool preview = _status?.Target?.Simulated == true;
        foreach (var (key, button) in _platformTry)
        {
            bool available = _scenarios.ContainsKey(key);
            button.IsEnabled = available && !preview;
            ToolTipService.SetToolTip(button, preview ? "Live runs are paused while another Surface is previewed."
                : available ? $"Runs a synthetic text fixture live {On(_words)}. Not evidence of execution on other platforms."
                : "This workflow is not offered by the running local service.");
        }
    }

    private FrameworkElement BuildBusiness()
    {
        var grid = new AdaptiveGrid(230, 4, 14);
        foreach (var (key, label, min, max, help) in BusinessCase.Fields)
        {
            var box = Id(new NumberBox
            {
                Header = label, Value = BusinessCase.Defaults[key], SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                SmallChange = key is "users" ? 10 : 1, LargeChange = key is "users" ? 100 : 10,
                ValidationMode = NumberBoxValidationMode.Disabled, PlaceholderText = $"{min:N0} to {max:N0}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            }, "Business_" + key);
            box.ValueChanged += (_, _) => _bState.Text = "Assumptions changed. Calculate to refresh the results.";
            _businessInputs[key] = box;
            grid.With(Stack(4, box, Small(string.IsNullOrEmpty(help) ? $"{min:N0} to {max:N0}" : $"{help}. {min:N0} to {max:N0}")));
        }
        _calculate.Click += async (_, _) => await CalculateBusinessAsync();
        var reset = Id(Secondary("Reset to defaults"), "BusinessReset");
        reset.Click += (_, _) =>
        {
            foreach (var (key, box) in _businessInputs) box.Value = (_status?.Assumptions ?? BusinessCase.Defaults).GetValueOrDefault(key, BusinessCase.Defaults[key]);
        };
        _businessExport.IsEnabled = false;
        _businessExport.Click += (_, _) => ExportBusiness();

        var results = new AdaptiveGrid(240, 3).With(
            StatCard(out _bHours, "-", "Capacity hours per year", "Users x tasks x minutes x days x adoption", "BusinessHours", out _),
            StatCard(out _bCapacity, "-", "Illustrative capacity value", "Hours x loaded hourly cost. Not cash savings", "BusinessCapacity", out _),
            StatCard(out _bInvestment, "-", "Device premium investment", "Users x premium per user", "BusinessInvestment", out _));
        _bCapacity.Foreground = Accent;

        return Page(Stack(22,
            Stack(10, Eyebrow("Business case"), H1("Frame the pilot with the firm's own numbers."),
                Body("Deterministic arithmetic from the local service. Every input is the firm's hypothesis to validate in a pilot; nothing here is a forecast.")),
            CardBorder(Stack(16, grid, Row(10, _calculate, reset), _bState, _businessError), 22),
            results,
            CardBorder(Stack(8, H3("How to read this"), _bDisclaimer,
                Small("Formula: hours = users x tasks x minutes / 60 x days x adoption / 100; capacity value = hours x hourly cost; investment = users x device premium."),
                Row(10, _businessExport), _businessInfo), 22, Sand)));
    }

    private void ApplyBusinessStatus(StatusSnapshot status)
    {
        if (_businessDefaultsApplied || status.Assumptions.Count == 0) return;
        _businessDefaultsApplied = true;
        foreach (var (key, box) in _businessInputs)
            if (status.Assumptions.TryGetValue(key, out double v)) box.Value = v;
        _bState.Text = "Defaults loaded from the local service. Adjust, then calculate.";
    }

    internal async Task<bool> CalculateBusinessAsync()
    {
        _businessError.IsOpen = false;
        var values = _businessInputs.ToDictionary(k => k.Key, k => k.Value.Value);
        if (BusinessCase.Validate(values) is string invalid)
        {
            _businessError.Message = invalid;
            _businessError.IsOpen = true;
            _bState.Text = "Not calculated: fix the highlighted assumption.";
            return false;
        }
        _calculate.IsEnabled = false;
        try
        {
            var document = await _client.CalculateAsync(values, _lifetime.Token);
            _lastBusiness?.Dispose();
            _lastBusiness = document;
            var r = document.RootElement;
            _bHours.Text = (StatusParser.Num(r, "hours") ?? double.NaN).ToString("N0", Usd);
            _bCapacity.Text = (StatusParser.Num(r, "capacity") ?? double.NaN).ToString("C0", Usd);
            _bInvestment.Text = (StatusParser.Num(r, "investment") ?? double.NaN).ToString("C0", Usd);
            _bDisclaimer.Text = StatusParser.Str(r, "disclaimer") ?? "";
            _bState.Text = $"Calculated by the local service at {DateTime.Now:t}.";
            _businessExport.IsEnabled = true;
            return true;
        }
        catch (Exception exception) when (!_lifetime.IsCancellationRequested)
        {
            _businessError.Message = exception.Message;
            _businessError.IsOpen = true;
            _bState.Text = "Not calculated.";
            return false;
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        finally { _calculate.IsEnabled = true; }
    }

    private string? ExportBusiness()
    {
        if (_lastBusiness is null) return null;
        try
        {
            string path = Evidence.Save(Evidence.BusinessCaseExport(_lastBusiness.RootElement), ExportDirectory, "business-case");
            _businessInfo.Title = "Business case exported"; _businessInfo.Message = path; _businessInfo.Severity = InfoBarSeverity.Success; _businessInfo.IsOpen = true;
            return path;
        }
        catch (Exception exception)
        {
            _businessInfo.Title = "Export failed"; _businessInfo.Message = exception.Message; _businessInfo.Severity = InfoBarSeverity.Error; _businessInfo.IsOpen = true;
            return null;
        }
    }
}
