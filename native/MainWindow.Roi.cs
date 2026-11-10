using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    internal const string RoiCalculatorName = "Hybrid Intelligence ROI Calculator";

    /// <summary>ROI lives in a separate Store app. This page only points to it; the studio itself calculates and claims no savings or ROI.</summary>
    private FrameworkElement BuildRoi()
    {
        string url = ApprovedLinks.RoiCalculatorPreview;
        var preview = Id(new HyperlinkButton
        {
            Content = "PREVIEW", Padding = new Thickness(0, 2, 0, 2), Foreground = Accent, FontSize = 16, FontWeight = FontWeights.SemiBold
        }, "RoiPreviewLink");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(preview, url);
        ToolTipService.SetToolTip(preview, url);
        preview.Click += async (_, _) =>
        {
            if (ApprovedLinks.IsApproved(url)) await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
        };

        var app = CardBorder(Stack(14,
            Lead(14, Glyph("\uE8EF", 26, Accent), Stack(8,
                Id(Pill("COMING SOON", AmberTint, AmberInk), "RoiComingSoon"),
                Id(H2(RoiCalculatorName), "RoiCalculatorName"),
                Body("A standalone Windows app to help customers understand the ROI of using local AI. Coming soon to the Microsoft Store.", Ink))),
            Stack(2, preview, Small("Opens the preview listing in the Microsoft Store."))), 24);

        var steps = new AdaptiveGrid(260, 3).With(
            RoiStep("1", "Discover", "Find one repeated workflow and agree today's baseline: who does it, how often and how long it takes."),
            RoiStep("2", "Demonstrate and size", "Run the workflow live in this studio, then size the device with T-shirt sizing and Platform fit."),
            RoiStep("3", "Build the ROI case", "Open the " + RoiCalculatorName + " with the customer's own numbers, then validate them in a pilot."));

        return Page(Stack(22,
            Stack(10, Eyebrow("ROI Calculator"), H1("Build the ROI case in the " + RoiCalculatorName + "."),
                Body("This studio proves the workflow and sizes the device. The ROI conversation moves to a standalone app, coming soon to the Microsoft Store.")),
            app,
            Stack(14, H2("Where it fits in the conversation"), steps),
            CardBorder(Stack(6, H3("What this studio will not claim"),
                Body("No cash savings, ROI forecast or subscription replacement. ROI figures come from the customer's own assumptions and are confirmed only by a measured pilot.", Ink)), 22, Sand)));
    }

    private static FrameworkElement RoiStep(string number, string title, string body) => CardBorder(Stack(8,
        Set(Text(number + "  " + title.ToUpperInvariant(), 12, Accent, FontWeights.SemiBold), t => t.CharacterSpacing = 80),
        Body(body, Ink)), 22);
}
