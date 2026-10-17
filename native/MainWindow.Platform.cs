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
    private readonly Dictionary<string, NumberBox> _businessInputs = [];
    private readonly Button _calculate = Id(Primary("Calculate"), "BusinessCalculate");
    private readonly Button _businessExport = Id(Secondary("Export business case (JSON)"), "BusinessExport");
    private readonly InfoBar _businessError = Id(new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = true, Title = "Check the assumptions" }, "BusinessError");
    private readonly InfoBar _businessInfo = Id(new InfoBar { Severity = InfoBarSeverity.Success, IsClosable = true }, "BusinessExportInfo");
    private TextBlock _bHours = null!, _bCapacity = null!, _bInvestment = null!;
    private readonly TextBlock _bDisclaimer = Id(Small(""), "BusinessDisclaimer");
    private readonly TextBlock _bState = Id(Small("Enter the customer's assumptions, then calculate."), "BusinessState");
    private JsonDocument? _lastBusiness;
    private bool _businessDefaultsApplied;
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    private FrameworkElement BuildPlatforms()
    {
        var banner = CardBorder(Lead(12, Glyph("\uE946", 18), Body(PlatformGuide.NoInferenceHere, Ink)), 16, SlateTint);
        Id(banner, "PlatformNoInferenceNotice");
        var cards = new AdaptiveGrid(340, 3, 18);
        foreach (var p in PlatformGuide.Platforms) cards.With(PlatformCard(p));

        var distinctions = CardBorder(Stack(10,
            H3("Runtime distinctions sellers should keep straight"),
            Body(PlatformGuide.FoundryDistinction, Ink),
            Body(PlatformGuide.GenieDistinction, Ink),
            Body(PlatformGuide.RuntimeNotInferred, Ink),
            Body("Vendor memory guidance for a specific model is not the same as this app's generic planner estimate; use both, and verify on the target SKU.", Ink)), 22);
        Id(distinctions, "PlatformDistinctions");

        return Page(Stack(22,
            Stack(10, Eyebrow("Platform fit"), H1("The right device for each workflow."),
                Body("RTX for heavier local models and prototyping; Snapdragon and Intel for recommended pilots where small models, mobility and per-SKU validation matter. Use cases are illustrative until verified.")),
            banner, cards, distinctions));
    }

    private FrameworkElement PlatformCard(PlatformProfile p)
    {
        var band = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = p.CurrentDevice ? Accent : Slate };
        var status = p.CurrentDevice ? Pill("LIVE ON THIS LAPTOP", GreenTint, GreenInk) : Pill("RECOMMENDED PILOT - ILLUSTRATIVE", SlateTint, Slate);
        Id(status, $"PlatformStatus_{p.Id}");
        var body = Stack(12, band, status, Id(H2(p.Name), $"PlatformName_{p.Id}"), Body(p.Tagline, Ink), Small(p.Status),
            CardBorder(Stack(4, Text("ACCELERATOR AND RUNTIME", 11, Muted, FontWeights.SemiBold), Small(p.AcceleratorAndRuntime, Ink)), 12, Paper));

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
                var tryLive = Id(Secondary(p.CurrentDevice ? "Run this live" : "Try the text workflow live on RTX"), $"PlatformTry_{key}");
                tryLive.Padding = new Thickness(10, 4, 10, 5);
                tryLive.FontSize = 12;
                tryLive.IsEnabled = false;
                tryLive.Click += (_, _) => { if (SelectScenario(key)) Navigate("live"); };
                _platformTry[key] = tryLive;
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
        body.Children.Add(Id(new Expander
        {
            Header = Text(p.CurrentDevice ? "What is verified here" : "Verify before a pilot", 13, Ink, FontWeights.SemiBold),
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
        return Id(CardBorder(body, 22), $"PlatformCard_{p.Id}");
    }

    private void UpdatePlatformTryButtons()
    {
        foreach (var (key, button) in _platformTry)
        {
            bool available = _scenarios.ContainsKey(key);
            button.IsEnabled = available;
            ToolTipService.SetToolTip(button, available
                ? "Runs a synthetic text fixture live on this laptop's RTX GPU. Not evidence of execution on other platforms."
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
            Stack(10, Eyebrow("Business case"), H1("Frame the pilot with the customer's own numbers."),
                Body("Deterministic arithmetic from the local service. Every input is the customer's hypothesis to validate in a pilot; nothing here is a forecast.")),
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
