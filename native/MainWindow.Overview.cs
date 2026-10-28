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
            Eyebrow("Firm overview  |  " + Event.Session),
            H1("Hybrid AI for global banking, with client data kept inside the firm."),
            Set(Body("For line-of-business leaders, CIOs, CDAOs, CISOs and model risk teams: capture a client meeting, transcribe it and draft call notes and checklists on this laptop's RTX GPU, escalate only de-identified complex questions to the cloud, and keep every customer decision with bankers. Synthetic clients, evidence first, no hype."),
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
            StoryCard("1", "Prove it live", "Run the line-of-business and CIO briefing or a technology team code draft on the RTX GPU. Real timing, real model, private reasoning withheld.", "live"),
            StoryCard("2", "Keep customer data local", "Route customer emails and complex questions: what stays in the firm, what is de-identified first and what would go to Azure OpenAI, with every reason shown.", "hybrid"),
            StoryCard("3", "Make it real for each business", "Run six workflows across consumer, commercial, markets, wealth and model risk teams, then size the device and frame a pilot with your own numbers.", "industries"));

        var hybridWays = new AdaptiveGrid(260, 3).With(
            WayCard("\uE8AB", "See the split", "Route a customer email or complex question and watch what stays on this PC, what is de-identified here first and what would go to the cloud, with every reason shown.", "Open the Hybrid router", "hybrid"),
            WayCard("\uEC4A", "Show it off", "Measure the loaded model live, rank models over the session and follow a five-minute run of show that ends with a deck.", "Open Show it off", "showoff"),
            WayCard("\uE943", "Build on it", "Give technology, data science and architecture teams a loopback, OpenAI-compatible endpoint, working code and starter projects that run against this GPU.", "Open Build on Surface", "build"));

        var guardrails = CardBorder(Stack(8,
            H3("What this showcase will not claim"),
            Body("- No cash savings, ROI forecast or subscription replacement; capacity value is illustrative."),
            Body("- No battery or speed comparisons with other devices; metrics describe one local run."),
            Body("- No claim that Snapdragon or Intel workflows ran on this laptop; they are recommended pilots verified per SKU."),
            Body("- Drafts only: no credit, lending, account-opening, dispute, fraud, AML, trade-booking, model-approval or investment decisions without approval by authorized people."),
            Body("- Not financial, legal or investment advice, and no suitability, risk-rating or suspicious-activity determination."),
            Body("- Synthetic clients only: no real customers, account data or NPI, and no regulatory or compliance determinations."),
            Body("- De-identification removes structured identifiers only; it is not certified. No core banking, CRM or payments connection is made."),
            Body("- Generated code is shown as text and never executed. Sources are synthetic.")), 22);

        return Page(Stack(26, hero, Stack(10, stats, specNote), BuildArchitecture(), Stack(14, H2("The five-minute executive story"), story),
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
            Beat("Open (30 s)", "\"Client conversations, account data and NPI never need to leave the firm. This laptop transcribes and drafts on its RTX GPU; only de-identified complex questions would go to the cloud, and bankers approve every decision.\""),
            Beat("Prove (2 min)", "Walk the architecture: run client meeting capture (Tier 1), draft a KYC refresh checklist (Tier 2), then route the complex question and show the NOT SENT de-identified payload (Tier 3). Rehearsal is labelled and has no metrics."),
            Beat("Land (2 min)", "Size the workload with T-shirt sizes, map banker, operations and branch devices with Platform fit, then agree a four-week pilot on synthetic client records with measured baselines."));

        var questions = CardBorder(Stack(8,
            H3("Discovery questions"),
            Body("1. Where are client conversations captured today, and which recordings, notes and identifiers must stay inside the firm?"),
            Body("2. Which banker workflows (meeting prep, call notes, KYC refresh, settlement breaks, model documentation reviews) would you measure first?"),
            Body("3. Which approvals (model risk management, privacy, information security, third-party risk, licensing) must come before a pilot? These are questions for the firm's risk and legal teams; this app makes no determination."),
            Body("4. What result would justify scaling beyond a pilot, and who decides: the line of business, the CIO, the CDAO or a model risk committee?")), 22);

        var objections = Stack(8, H3("Objection handling"),
            Objection("How is AI accelerated on this device?", "On the RTX GPU through the CUDA execution provider in Foundry Local. The run evidence names the model variant and provider; there is no silent CPU fallback."),
            Objection("Is it really 128 GB?", "128 GB is installed physical memory. Windows reports about 64 GB visible, and the GPU driver reports a 64,000 MiB budget. These are separate views and are never added."),
            Objection("Will it save us money?", "This app shows illustrative capacity value only, not cash savings or ROI. Time savings and adoption must be measured in a pilot; cloud subscriptions are unchanged."),
            Objection("Can we use real customer data?", "Not in this showcase. Every client here is synthetic. A pilot with real customer data needs the firm's privacy, information security and model risk reviews first; this app makes no regulatory or compliance determination."),
            Objection("Will it make credit or account decisions?", "No. It drafts notes and checklists for bankers to review and approve. Credit, account, dispute, fraud and investment decisions stay with authorized people; nothing here is financial advice."),
            Objection("What goes to Azure OpenAI?", "In the architecture, only de-identified, minimized queries for complex questions. In this showcase nothing is sent: the Hybrid router shows the payload marked NOT SENT. Structured identifiers are removed; free-text customer detail still needs human review."),
            Objection("Does this run on our Snapdragon or Intel fleet?", "Those are recommended pilots. Verify the exact model, runtime and accelerator support per SKU; nothing here ran on those devices."),
            Objection("What about model reasoning text?", "Private reasoning is counted but never displayed, exported or saved. Only the final answer is shown."),
            Objection("Does local AI replace the cloud?", "No. Hybrid Intelligence keeps private, fast and offline work on the PC and uses the cloud for current information, organisation-wide data and frontier scale. The Hybrid router shows the split and its reasons; the studio itself never calls a cloud service."),
            Objection("Can our developers build on this?", "Yes. Build on Surface starts an OpenAI-compatible endpoint on loopback (off by default, this PC only) and creates small starter projects in Python and C#. Your code reviews and runs them; the studio never executes generated code."));

        var doDont = new AdaptiveGrid(300, 2).With(
            CardBorder(Stack(6, Set(H3("Do"), t => t.Foreground = GreenInk),
                Body("Use live runs whenever the model is ready."),
                Body("Name the exact model and provider shown."),
                Body("Position every estimate as illustrative."),
                Body("Keep every example synthetic and every decision human.")), 22),
            CardBorder(Stack(6, Set(H3("Don't"), t => t.Foreground = AccentDeep),
                Body("Present rehearsal as live output."),
                Body("Compare speed or battery with other devices."),
                Body("Add memory views together or call the GPU budget VRAM."),
                Body("Promise savings, customer outcomes, compliance or decision accuracy.")), 22));

        var rampUp = Id(CardBorder(Lead(14, Glyph("\uE7BE", 22, Accent), Stack(8,
            H3("Optional: ramp up technically"),
            Body("'Coding as a second language' is eight short AI-literacy lessons for bankers, operations and technology teams that teach you to read code like a new language, with a quick check per lesson and a local coding tutor on the RTX GPU. Progress is saved on this laptop only."),
            _learnToggle)), 22), "PlaybookLearnCard");

        return Page(Stack(24,
            Stack(10, Eyebrow("Banking playbook  |  " + Event.Name), H1("Five minutes. Three beats. One pilot."),
                Body("A concise talk track for line-of-business leaders, technology and risk teams, with the guardrails that keep it credible.")),
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
