using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private TextBlock _ovInstalled = null!, _ovGpuCores = null!, _ovCpuCores = null!, _ovDevice = null!;

    private static Border StatCard(out TextBlock value, string initial, string label, string detail, string id, out TextBlock detailText)
    {
        value = Id(Set(Text(initial, 30, Ink, FontWeights.SemiBold), t => t.FontFamily = Display), id);
        detailText = Small(detail);
        return CardBorder(Stack(4, value, Text(label, 14, Ink, FontWeights.SemiBold), detailText), 20);
    }

    private FrameworkElement BuildOverview()
    {
        _ovDevice = Id(Body("Reading device inventory..."), "OverviewDeviceName");
        var startLive = Id(Primary("Start the live GPU demo"), "OverviewStartLive");
        startLive.Click += (_, _) => Navigate("live");
        var size = Id(Secondary("Size a workload"), "OverviewOpenSizing");
        size.Click += (_, _) => Navigate("sizing");
        var evidence = Id(Secondary("Device evidence"), "OverviewDeviceEvidence");
        evidence.Click += async (_, _) => await ShowDeviceEvidenceAsync();

        var hero = Stack(14,
            Eyebrow("Executive overview"),
            H1("Serious local AI, running on the RTX GPU of a Surface Laptop Ultra."),
            Set(Body("Show a 14B coding model and a 20B reasoning model working on this laptop, size what a customer's workload needs, then frame a measured pilot. Evidence first, no hype."),
                t => { t.FontSize = 16; t.LineHeight = 24; t.MaxWidth = 820; t.HorizontalAlignment = HorizontalAlignment.Left; }),
            _ovDevice,
            Set(Row(10, startLive, size, evidence), r => r.Margin = new Thickness(0, 6, 0, 0)));

        var stats = new AdaptiveGrid(230, 4).With(
            StatCard(out _ovInstalled, "...", "LPDDR5x unified memory", "Installed in this laptop, shared by the CPU and RTX GPU", "OverviewInstalledMemory", out _),
            StatCard(out _ovGpuCores, "...", "NVIDIA Blackwell RTX GPU", "NVIDIA RTX Spark N1X", "OverviewGpuCores", out _),
            StatCard(out _ovCpuCores, "...", "CPU", "NVIDIA RTX Spark N1X", "OverviewCpuCores", out _),
            StatCard(out _, "15-inch", "PixelSense Ultra display", "3270 × 2180, up to 120Hz, 2,000-nit HDR peak", "OverviewDisplay", out _));
        var specNote = Id(Small("Memory and core counts as reported by this laptop. Display and processor family from Microsoft's Surface Laptop Ultra tech specs."), "OverviewSpecSource");

        var story = new AdaptiveGrid(260, 3).With(
            StoryCard("1", "Prove it live", "Run the executive briefing or code draft on the RTX GPU. Real timing, real model, private reasoning withheld.", "live"),
            StoryCard("2", "Size it honestly", "Use T-shirt sizes to show what S to XL models need, against one memory budget at a time.", "sizing"),
            StoryCard("3", "Fit the fleet, frame the pilot", "Match Snapdragon, Intel and RTX devices to workflows, then model capacity with the customer's numbers.", "platforms"));

        var hybridWays = new AdaptiveGrid(260, 3).With(
            WayCard("\uE8AB", "See the split", "Route a request and watch what stays on this PC, what is redacted here first and what would go to the cloud, with every reason shown.", "Open the Hybrid router", "hybrid"),
            WayCard("\uEC4A", "Show it off", "Measure the loaded model live, rank models over the session and follow a five-minute run of show that ends with a deck.", "Open Show it off", "showoff"),
            WayCard("\uE943", "Build on it", "Start a loopback, OpenAI-compatible endpoint, copy working code and create starter projects that run against this GPU.", "Open Build on Surface", "build"));

        var guardrails = CardBorder(Stack(8,
            H3("What this showcase will not claim"),
            Body("- No cash savings, ROI forecast or subscription replacement; capacity value is illustrative."),
            Body("- No battery or speed comparisons with other devices; metrics describe one local run."),
            Body("- No claim that Snapdragon or Intel workflows ran on this laptop; they are recommended pilots verified per SKU."),
            Body("- Generated code is shown as text and never executed. Sources are synthetic.")), 22);

        return Page(Stack(26, hero, Stack(10, stats, specNote), Stack(14, H2("The five-minute executive story"), story),
            Stack(14, H2("Hybrid Intelligence: see it, show it, build on it"),
                Body("The PC does what it does best - private, fast, always available - and the cloud does what only it can. These pages make that split visible, measurable and buildable."),
                hybridWays),
            guardrails));
    }

    private FrameworkElement WayCard(string glyph, string title, string body, string action, string target)
    {
        var go = Id(Secondary(action + "  \u2192"), "OverviewWay_" + target);
        go.HorizontalAlignment = HorizontalAlignment.Left;
        go.Click += (_, _) => Navigate(target);
        return CardBorder(Stack(10, Glyph(glyph, 24, Accent), H3(title), Body(body), go), 22);
    }

    private FrameworkElement StoryCard(string number, string title, string body, string target)
    {
        var badge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Set(Text(number, 15, White, FontWeights.Bold), t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; })
        };
        var go = Id(new HyperlinkButton { Content = "Open", Padding = new Thickness(0), Foreground = Accent }, "OverviewStory_" + target);
        go.Click += (_, _) => Navigate(target);
        return CardBorder(Stack(10, badge, H3(title), Body(body), go), 22);
    }

    private void ApplyOverviewStatus(StatusSnapshot status)
    {
        var d = status.Device;
        _ovDevice.Text = d.Error is null
            ? $"{d.DisplayName}  |  {d.Cpu}"
            : "Device inventory unavailable: " + d.Error;
        _ovInstalled.Text = Gb(d.InstalledGiB);
        _ovGpuCores.Text = CoreCount(d.Cpu, "GPU") ?? d.Accelerators.Select(a => CoreCount(a, "")).FirstOrDefault(c => c is not null) ?? "Unavailable";
        _ovCpuCores.Text = CoreCount(d.Cpu, "CPU") ?? (d.Cores is int cores ? $"{cores}-core" : "Unavailable");
    }

    private static string? CoreCount(string? text, string unit)
    {
        var match = text is null ? null : System.Text.RegularExpressions.Regex.Match(text, $@"(\d+)-core\s*(?:\w+\s+)*?{unit}");
        return match is { Success: true } && int.TryParse(match.Groups[1].Value, out int n)
            ? n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "-core"
            : null;
    }

    private FrameworkElement BuildPlaybook()
    {
        var beats = new AdaptiveGrid(280, 3).With(
            Beat("Open (30 s)", "\"This laptop runs a 14B coding model and a 20B reasoning model locally on its RTX GPU. Let me show you, then we'll size what your teams need.\""),
            Beat("Prove (2 min)", "Run the executive briefing live. Point to the model, CUDA provider and timing. Switch to code drafting. Note: rehearsal is labelled and has no metrics."),
            Beat("Land (2 min)", "Size the workload with T-shirt sizes, map the fleet with Platform fit, then agree a 10-user, two-week pilot with measured baselines."));

        var questions = CardBorder(Stack(8,
            H3("Discovery questions"),
            Body("1. Which teams work with sensitive or connectivity-constrained content today?"),
            Body("2. Which repeatable drafting, summarising or coding tasks would you measure first?"),
            Body("3. Which approvals (security, licensing, app compatibility) must come before a pilot?"),
            Body("4. What result would justify scaling beyond a pilot, and who decides?")), 22);

        var objections = Stack(8, H3("Objection handling"),
            Objection("How is AI accelerated on this device?", "On the RTX GPU through the CUDA execution provider in Foundry Local. The run evidence names the model variant and provider; there is no silent CPU fallback."),
            Objection("Is it really 128 GB?", "128 GB is installed physical memory. Windows reports about 64 GB visible, and the GPU driver reports a 64,000 MiB budget. These are separate views and are never added."),
            Objection("Will it save us money?", "This app shows illustrative capacity value only, not cash savings or ROI. Time savings and adoption must be measured in a pilot; cloud subscriptions are unchanged."),
            Objection("Does this run on our Snapdragon or Intel fleet?", "Those are recommended pilots. Verify the exact model, runtime and accelerator support per SKU; nothing here ran on those devices."),
            Objection("What about model reasoning text?", "Private reasoning is counted but never displayed, exported or saved. Only the final answer is shown."),
            Objection("Does local AI replace the cloud?", "No. Hybrid Intelligence keeps private, fast and offline work on the PC and uses the cloud for current information, organisation-wide data and frontier scale. The Hybrid router shows the split and its reasons; the studio itself never calls a cloud service."),
            Objection("Can our developers build on this?", "Yes. Build on Surface starts an OpenAI-compatible endpoint on loopback (off by default, this PC only) and creates small starter projects in Python and C#. Your code reviews and runs them; the studio never executes generated code."));

        var doDont = new AdaptiveGrid(300, 2).With(
            CardBorder(Stack(6, Set(H3("Do"), t => t.Foreground = GreenInk),
                Body("Use live runs whenever the model is ready."),
                Body("Name the exact model and provider shown."),
                Body("Position every estimate as illustrative."),
                Body("Export evidence only when the customer asks.")), 22),
            CardBorder(Stack(6, Set(H3("Don't"), t => t.Foreground = AccentDeep),
                Body("Present rehearsal as live output."),
                Body("Compare speed or battery with other devices."),
                Body("Add memory views together or call the GPU budget VRAM."),
                Body("Promise savings, procurement fit or provider support.")), 22));

        var rampUp = Id(CardBorder(Lead(14, Glyph("\uE7BE", 22, Accent), Stack(8,
            H3("Optional: ramp up technically"),
            Body("'Coding as a second language' is eight short lessons that teach you to read code like a new language, with a quick check per lesson and a local coding tutor on the RTX GPU. Progress is saved on this laptop only."),
            _learnToggle)), 22), "PlaybookLearnCard");

        return Page(Stack(24,
            Stack(10, Eyebrow("Seller playbook"), H1("Five minutes. Three beats. One pilot."),
                Body("A concise talk track for an executive conversation, with the guardrails that keep it credible.")),
            beats, questions, objections, doDont, rampUp));
    }

    private static FrameworkElement Beat(string title, string body) =>
        CardBorder(Stack(8, Set(Text(title.ToUpperInvariant(), 12, Accent, FontWeights.SemiBold), t => t.CharacterSpacing = 80), Set(Body(body, Ink), t => t.FontSize = 15)), 22);

    private static FrameworkElement Objection(string question, string answer) => new Expander
    {
        Header = Text(question, 14, Ink, FontWeights.SemiBold),
        Content = Body(answer, Ink),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
    };
}
