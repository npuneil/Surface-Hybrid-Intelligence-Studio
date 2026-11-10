using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static HybridIntelligenceStudio.Ui;

namespace HybridIntelligenceStudio;

internal sealed partial class MainWindow
{
    private TextBlock _ovInstalled = null!, _ovInstalledNote = null!, _ovCpuCores = null!, _ovCpuNote = null!, _ovLanes = null!, _ovLanesNote = null!,
        _ovRunsOn = null!, _ovRunsNote = null!, _ovDevice = null!, _ovMemoryAnswer = null!;

    private static Border StatCard(out TextBlock value, string initial, string label, string detail, string id, out TextBlock detailText, double size = 30)
    {
        value = Id(Set(Text(initial, size, Ink, FontWeights.SemiBold), t => { t.FontFamily = Display; t.TextWrapping = TextWrapping.Wrap; }), id);
        detailText = Small(detail);
        return CardBorder(Stack(4, value, Text(label, 14, Ink, FontWeights.SemiBold), detailText), 20);
    }

    private FrameworkElement BuildOverview()
    {
        _ovDevice = Id(Body("Reading device inventory..."), "OverviewDeviceName");
        var startLive = Id(Primary("Start the live demo"), "OverviewStartLive");
        startLive.Click += (_, _) => Navigate("live");
        var size = Id(Secondary("Size a workload"), "OverviewOpenSizing");
        size.Click += (_, _) => Navigate("sizing");
        var evidence = Id(Secondary("Device evidence"), "OverviewDeviceEvidence");
        evidence.Click += async (_, _) => await ShowDeviceEvidenceAsync();

        var hero = Stack(14,
            Eyebrow("Independent prototype  |  " + Event.Session),
            H1("Explore Hybrid Intelligence on your Surface."),
            Set(Worded(Body(""), w => $"Explore local drafting {On(w)}, the PC-and-cloud split, and workload-to-device fit. Use synthetic Industry Examples, measured run evidence and human review to evaluate the approach. This is an independent demonstration prototype, not a product or supported production solution."),
                t => { t.FontSize = 16; t.LineHeight = 24; t.MaxWidth = 820; t.HorizontalAlignment = HorizontalAlignment.Left; }),
            _ovDevice,
            Set(Row(10, startLive, size, evidence), r => r.Margin = new Thickness(0, 6, 0, 0)));

        var stats = new AdaptiveGrid(230, 4).With(
            StatCard(out _ovInstalled, "...", "Installed memory", "Reading memory...", "OverviewInstalledMemory", out _ovInstalledNote),
            StatCard(out _ovCpuCores, "...", "CPU", "Reading processor...", "OverviewCpuCores", out _ovCpuNote),
            StatCard(out _ovLanes, "...", "Processing lanes", "Detecting NPU, GPU and CPU...", "OverviewLanes", out _ovLanesNote, 24),
            StatCard(out _ovRunsOn, "...", "Models run on", "Waiting for the local service...", "OverviewRunsOn", out _ovRunsNote, 24));
        var specNote = Id(Small("Counts and names as detected on the PC shown. This PC lists every lane, the model each slot runs and how each demo fits."), "OverviewSpecSource");

        var story = new AdaptiveGrid(260, 3).With(
            StoryCard("1", "Discover and demonstrate", w => $"Ask where repeated drafting, sensitive data or unreliable connectivity matter. Then run one relevant workflow {On(w)} and show its actual timing and model.", "live"),
            StoryCard("2", "Keep customer data local", "Route customer emails and complex questions: what stays in the firm, what is de-identified first and what would go to Azure OpenAI, with every reason shown.", "hybrid"),
            StoryCard("3", "Size and evaluate", "Use the synthetic Industry Examples to match a workflow to a role. Size the device, then scope an evaluation with a baseline, owner, quality gate and success criteria.", "industries"));

        var hybridWays = new AdaptiveGrid(260, 3).With(
            WayCard("\uE8AB", "See the split", "Route a customer email or complex question and watch what stays on this PC, what is de-identified here first and what would go to the cloud, with every reason shown.", "Open the Hybrid router", "hybrid"),
            WayCard("\uEC4A", "Show it off", "Measure the loaded model live, rank models over the session and follow a five-minute run of show that ends with a deck.", "Open Show it off", "showoff"),
            WayCard("\uE943", "Build on it", "Give technology, data science and architecture teams a loopback, OpenAI-compatible endpoint, working code and starter projects that run against the local models on this PC.", "Open Build on Surface", "build"));

        var guardrails = CardBorder(Stack(8,
            H3("What this showcase will not claim"),
            Body("- No cash savings, ROI forecast or subscription replacement claims. For ROI, use the standalone " + RoiCalculatorName + " (coming soon)."),
            Body("- No battery or speed comparisons with other devices; metrics describe one local run."),
            Body("- No claim that a workflow ran on a device it did not run on: a previewed Surface runs nothing, and every result names the PC and lane it ran on. Verify the exact model, runtime and accelerator per SKU before a pilot."),
            Body("- Drafts only: no credit, lending, account-opening, dispute, fraud, AML, trade-booking, model-approval or investment decisions without approval by authorized people."),
            Body("- Not financial, legal or investment advice, and no suitability, risk-rating or suspicious-activity determination."),
            Body("- Synthetic clients only: no real customers, account data or NPI, and no regulatory or compliance determinations."),
            Body("- De-identification removes structured identifiers only; it is not certified. No core banking, CRM or payments connection is made."),
            Body("- Generated code is shown as text and never executed. Sources are synthetic.")), 22);

        return Page(Stack(26, hero, Stack(10, stats, specNote), Stack(14, H2("Explore the workflow in five minutes"), story), BuildArchitecture(),
            Stack(14, H2("Hybrid Intelligence: see it, show it, build on it"),
                Body("Explore which work can run locally and which work might need the cloud. These pages make that split visible, measurable and buildable; availability and performance depend on the device, models and runtime."),
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

    /// <summary>"on the NPU", "on the RTX GPU" or, before the service says, "on this PC": it reads the same while another Surface is previewed.</summary>
    private static string On(TargetWords words) => "on " + words.Where;

    /// <summary>"the RTX GPU" as the bare name "RTX GPU", for a stat card.</summary>
    private static string Bare(string where) => where.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? where[4..] : where;

    private FrameworkElement StoryCard(string number, string title, string body, string target) => StoryCard(number, title, Body(body), target);

    private FrameworkElement StoryCard(string number, string title, Func<TargetWords, string> body, string target) =>
        StoryCard(number, title, Worded(Body(""), body), target);

    private FrameworkElement StoryCard(string number, string title, TextBlock body, string target)
    {
        var badge = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Accent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Set(Text(number, 15, White, FontWeights.Bold), t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; })
        };
        var go = Id(new HyperlinkButton { Content = "Open", Padding = new Thickness(0), Foreground = Accent }, "OverviewStory_" + target);
        go.Click += (_, _) => Navigate(target);
        return CardBorder(Stack(10, badge, H3(title), body, go), 22);
    }

    /// <summary>The four stat cards and the memory answer describe the PC being shown: this PC from its own inventory, or a previewed
    /// Surface from the device kit's report for it. Nothing here is a fixed figure for one device.</summary>
    private void ApplyOverviewStatus(StatusSnapshot status)
    {
        var d = status.Device;
        var target = status.Target is { Error: null } t ? t : null;
        bool preview = target?.Simulated == true;
        var report = _deviceReport is { } r && r.Simulated == preview ? r : null;
        bool loadingPreview = preview && report is null;

        _ovDevice.Text = preview
            ? $"Previewing {target!.Label}. This PC is {d.DisplayName}; its models and live runs are unchanged."
            : d.Error is null ? $"{d.DisplayName}  |  {d.Cpu}" : "Device inventory unavailable: " + d.Error;

        double? installed = preview ? report?.InstalledGb : d.InstalledGiB;
        double? visible = preview ? report?.VisibleGb : d.OsVisibleGiB;
        long? budgetMiB = preview ? null : d.PrimaryGpuBudgetMiB;
        _ovInstalled.Text = loadingPreview ? "..." : Gb(installed);
        _ovInstalledNote.Text = loadingPreview ? "Reading the preview..." : MemoryViews(installed, visible, budgetMiB, brief: true);
        _ovMemoryAnswer.Text = MemoryViews(installed, visible, budgetMiB, brief: false);

        int? logical = report?.CpuCores;
        string? cpuName = preview ? report?.Cpu : report?.Cpu ?? d.Cpu;
        _ovCpuCores.Text = loadingPreview ? "..."
            : logical is int l ? $"{l} logical cores"
            : !preview && d.Cores is int c ? $"{c} cores" : "Unavailable";
        _ovCpuNote.Text = loadingPreview ? "Reading the preview..." : string.IsNullOrWhiteSpace(cpuName) ? "Processor not reported" : cpuName!;

        var lanes = target?.Lanes ?? [];
        var active = lanes.Where(x => x.Available).ToList();
        _ovLanes.Text = target is null ? "..." : active.Count == 0 ? "None detected" : string.Join(" + ", active.Select(x => LaneName(x.Id)));
        _ovLanesNote.Text = target is null ? "Detecting the NPU, GPU and CPU..." : string.Join("; ",
            new[] { "npu", "gpu", "cpu" }.Select(id => lanes.FirstOrDefault(x => x.Id == id))
                .Where(x => x is not null)
                .Select(x => x!.Available ? $"{LaneName(x.Id)}: {(string.IsNullOrWhiteSpace(x.Device) ? "detected" : x.Device)}" : $"{LaneName(x.Id)}: not used for models"));

        var words = status.Words;
        var model = status.Engine.Model;
        _ovRunsOn.Text = words.Known ? Bare(words.Where) : "Not chosen yet";
        _ovRunsNote.Text = preview ? $"An estimate for {target!.Label}. Nothing runs on the previewed Surface."
            : model is not null ? $"{model.Name}" + (string.IsNullOrWhiteSpace(model.Ep) ? "" : $" through {model.Ep}")
            : "Foundry Local picks the processor when the first model loads.";
    }

    /// <summary>The memory views of one PC, kept apart: installed physical memory, what Windows can see and the GPU driver's budget.
    /// They are different measurements and are never added together.</summary>
    private static string MemoryViews(double? installedGb, double? visibleGb, long? gpuBudgetMiB, bool brief)
    {
        if (installedGb is not double installed) return brief ? "Not reported by Windows." : "This PC did not report its installed memory.";
        var parts = new List<string>();
        bool differs = visibleGb is double v && Math.Abs(v - installed) > 1;
        if (brief)
        {
            parts.Add("Installed physical memory");
            if (differs) parts.Add($"Windows shows about {Math.Round(visibleGb!.Value):N0} GB");
            if (gpuBudgetMiB is long b && b > 0) parts.Add($"GPU memory budget {b / 1024.0:0.#} GiB");
            return string.Join(". ", parts) + ".";
        }
        string text = $"{Math.Round(installed):N0} GB is installed physical memory.";
        if (visibleGb is double seen) text += differs ? $" Windows reports about {Math.Round(seen):N0} GB visible." : $" Windows reports about the same, {Math.Round(seen):N0} GB, as visible.";
        if (gpuBudgetMiB is long budget && budget > 0) text += $" The GPU driver reports a {budget:N0} MiB budget for the GPU.";
        return text + " These are separate views of the same hardware and are never added together.";
    }

    private FrameworkElement BuildPlaybook()
    {
        var beats = new AdaptiveGrid(280, 3).With(
            Beat("Discover (30 s)", "\"Which repeated workflow takes time today? Which data must stay local? Where does connectivity get in the way?\" Choose one Industry Example that matches the role; do not promise an outcome."),
            Beat("Demonstrate (2 min)", w => $"Run a relevant workflow {On(w)}. Show the source, draft, model, provider and measured timing. Use the Hybrid router to explain what stays local and what would go to the cloud; the preview is NOT SENT. Rehearsal is labelled and has no metrics."),
            Beat("Scope a pilot (2 min)", "Use T-shirt sizing and Platform fit to discuss the device. Agree a workflow, baseline, owner, human review, quality gate and success criteria. For ROI, point to the standalone " + RoiCalculatorName + " (coming soon)."));

        var questions = CardBorder(Stack(8,
            H3("Discovery questions"),
            Body("1. Which repeated drafting or review task would you measure first, and what is today's baseline?"),
            Body("2. Which inputs must stay on the device, and when is connectivity unavailable or unreliable?"),
            Body("3. Which privacy, security, licensing and deployment approvals are required before a pilot?"),
            Body("4. Who owns the pilot, reviews the output and decides whether its quality and measured results justify scaling?")), 22);

        _ovMemoryAnswer = Id(Body("", Ink), "PlaybookMemoryAnswer");
        var objections = Stack(8, H3("Objection handling"),
            Objection("How is AI accelerated on this device?", w => $"Foundry Local chooses the processor for each model from what this PC offers: the NPU, the GPU or the CPU. The chat model runs {On(w)} through {w.Provider}. The run evidence names the model variant and provider, and the app never switches providers automatically."),
            Objection("How much memory does this PC really have?", _ovMemoryAnswer),
            Objection("Will it save us money?", "This studio makes no savings or ROI claim. Explore ROI with the customer's own numbers in the standalone " + RoiCalculatorName + " (coming soon; see the ROI Calculator page), then measure time savings and adoption in a pilot. Cloud subscriptions are unchanged."),
            Objection("Can we use real customer data?", "Not in this showcase. Every client here is synthetic. A pilot with real customer data needs the firm's privacy, information security and model risk reviews first; this app makes no regulatory or compliance determination."),
            Objection("Will it make credit or account decisions?", "No. It drafts notes and checklists for bankers to review and approve. Credit, account, dispute, fraud and investment decisions stay with authorized people; nothing here is financial advice."),
            Objection("What goes to Azure OpenAI?", "In the architecture, only de-identified, minimized queries for complex questions. In this showcase nothing is sent: the Hybrid router shows the payload marked NOT SENT. Structured identifiers are removed; free-text customer detail still needs human review."),
            Objection("Does this run on our Snapdragon, Intel, AMD or NVIDIA fleet?", "The Studio detects the PC it runs on and plans its model lineup for it: the NPU where Foundry Local offers NPU models for that PC, otherwise the GPU, otherwise the CPU. This PC lists the lineup and how each demo fits. Preview another Surface to see what it would run; a preview is an estimate and nothing runs on the previewed device. Verify the exact model, runtime and accelerator support per SKU in a pilot."),
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
                Body("Add memory views together or call a GPU memory budget VRAM."),
                Body("Promise savings, customer outcomes, compliance or decision accuracy.")), 22));

        var rampUp = Id(CardBorder(Lead(14, Glyph("\uE7BE", 22, Accent), Stack(8,
            H3("Optional: ramp up technically"),
            Worded(Body(""), w => $"'Coding as a second language' is eight short AI-literacy lessons for bankers, operations and technology teams that teach you to read code like a new language, with a quick check per lesson and a local coding tutor running {On(w)}. Progress is saved on this PC only. The tab is hidden until you turn it on here."),
            _learnToggle)), 22), "PlaybookLearnCard");

        return Page(Stack(24,
            Stack(10, Eyebrow("Seller playbook  |  " + Event.Name), H1("Discover. Demonstrate. Scope a pilot."),
                Body("Practice the sales conversation with Industry Examples: connect a workflow to a device, show evidence, handle objections and agree a measured next step.")),
            beats, questions, objections, doDont, rampUp));
    }

    private FrameworkElement Beat(string title, Func<TargetWords, string> body) => BeatCard(title, Worded(Body("", Ink), body));

    private static FrameworkElement Beat(string title, string body) => BeatCard(title, Body(body, Ink));

    private static FrameworkElement BeatCard(string title, TextBlock body) =>
        CardBorder(Stack(8, Set(Text(title.ToUpperInvariant(), 12, Accent, FontWeights.SemiBold), t => t.CharacterSpacing = 80), Set(body, t => t.FontSize = 15)), 22);

    private FrameworkElement Objection(string question, Func<TargetWords, string> answer) => Objection(question, Worded(Body("", Ink), answer));

    private static FrameworkElement Objection(string question, string answer) => Objection(question, Body(answer, Ink));

    private static FrameworkElement Objection(string question, TextBlock answer) => new Expander
    {
        Header = Text(question, 14, Ink, FontWeights.SemiBold),
        Content = answer,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Left,
    };
}
