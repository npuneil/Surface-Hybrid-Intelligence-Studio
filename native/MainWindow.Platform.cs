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
}
